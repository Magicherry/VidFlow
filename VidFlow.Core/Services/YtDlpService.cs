using System.Diagnostics;
using System.Text;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>A yt-dlp failure: <see cref="Exception.Message"/> is user-facing, <see cref="Detail"/> is the raw output.</summary>
public sealed class YtDlpException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>Runs yt-dlp. Argument building and output parsing live in <see cref="YtDlpArgs"/> / <see cref="YtDlpOutputParser"/>.</summary>
public static class YtDlpService
{
    /// <summary>Raised (on a background thread) when browser cookies could not be read and were skipped.</summary>
    public static event Action? BrowserCookiesFailed;

    /// <summary>The browser whose cookies failed to read this session (reset when the setting changes).</summary>
    static volatile string? _unreadableBrowser;

    /// <summary>
    /// Douyin: given douyin.com/video/ID, returns the JSON of its "aweme/detail" API. Douyin signs those requests
    /// in page script (which yt-dlp can't do), so the app loads the page in a hidden WebView2 and reads the answer.
    /// </summary>
    public static Func<string, CancellationToken, Task<string>>? FetchDouyinDetail { get; set; }

    const string DouyinReferer = "https://www.douyin.com/";

    static async Task<string> GetDouyinAsync(string url, CancellationToken ct)
    {
        if (FetchDouyinDetail is not { } fetch) throw new YtDlpException(L.Get("Error_DouyinUnavailable"));
        if (UrlTools.CanonicalDouyinUrl(url) is null) throw new YtDlpException(L.Get("Error_DouyinNotVideo"));
        return await fetch(url, ct);
    }

    public static async Task<MediaInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        url = await ShortLinks.NormalizeAsync(url, ct);
        if (UrlTools.DetectPlatform(url) == Platform.Douyin)
        {
            var detail = await GetDouyinAsync(url, ct);
            return DouyinParser.ParsePhotos(detail) is { } note ? DouyinParser.ToVideoInfo(note) : DouyinParser.ToVideoInfo(DouyinParser.Parse(detail));
        }
        if (TikTokPhotos.IsPhotoUrl(url))
            return TikTokPhotos.ToVideoInfo(await TikTokPhotos.FetchAsync(url, ct) ?? throw new YtDlpException(L.Get("Error_TikTokNoPhotos")), url);
        var stdout = new StringBuilder();
        var (code, error) = await RunWithCookieFallbackAsync(url,
            env => { stdout.Clear(); return YtDlpArgs.Info(url, AppSettings.Current, env); },
            line => stdout.AppendLine(line), ct);
        if (code != 0)
        {
            // Or fails on it, depending on the post.
            if (UrlTools.DetectPlatform(url) == Platform.TikTok && await TryPhotoPostAsync(url, ct) is { } post)
                return TikTokPhotos.ToVideoInfo(post, url);
            throw new YtDlpException(ErrorMapper.Friendly(error), error);
        }

        var info = InfoParser.Parse(stdout.ToString(), url);
        // yt-dlp "succeeds" on a photo post's /video/ address, with its sound and no picture.
        if (info is VideoInfo { Platform: Platform.TikTok, Qualities.Count: 0 } && await TryPhotoPostAsync(url, ct) is { } photos)
            return TikTokPhotos.ToVideoInfo(photos, url);
        if (info is PlaylistInfo { Entries.Count: 0 })
            throw new YtDlpException(L.Get("Error_EmptyPlaylist"));
        if (info is PlaylistInfo { Platform: Platform.Bilibili } parts)
            info = await BilibiliApi.EnrichAsync(parts, ct);
        return info;
    }

    /// <summary>Whether the failure was a photo post; failing to tell leaves yt-dlp's error to be reported.</summary>
    static async Task<PhotoPost?> TryPhotoPostAsync(string url, CancellationToken ct)
    {
        try
        {
            return await TikTokPhotos.FetchAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            Log.Info($"TikTok page not read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs the download, reporting events as they happen. Returns the final file path when yt-dlp reports one.</summary>
    public static async Task<string?> DownloadAsync(DownloadRequest req, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        if (req.IsAudio && ToolManager.FfmpegPath is null)
            throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        req = req with { Url = await ShortLinks.NormalizeAsync(req.Url, ct) };
        Directory.CreateDirectory(req.OutputFolder);
        if (UrlTools.DetectPlatform(req.Url) != Platform.Douyin && (req.IsPhotos || TikTokPhotos.IsPhotoUrl(req.Url)))
            return await TikTokPhotos.DownloadAsync(req, onEvent, ct);

        if (req.IsSubtitles) return await DownloadSubtitlesAsync(req, onEvent, ct);

        string? filePath = null;
        // Set when VidFlow found the title itself: yt-dlp, given only a media file's address, would report that file's name.
        string? knownTitle = null;
        var parser = new YtDlpOutputParser();
        void OnLine(string line)
        {
            if (parser.Feed(line) is not { } e) return;
            if (e is FileEvent f) filePath = f.Path;
            if (e is StartedEvent s && knownTitle is not null) e = s with { Title = knownTitle };
            onEvent(e);
        }

        int code;
        string error;
        if (UrlTools.DetectPlatform(req.Url) == Platform.Douyin)
        {
            // Stream addresses expire, so look them up again right before downloading (also after a restart).
            var detail = await GetDouyinAsync(req.Url, ct);
            if (DouyinParser.ParsePhotos(detail) is { } photos)
                return await PhotoPosts.SaveAsync(photos, req.OutputFolder, DouyinReferer, onEvent, ct) ?? throw new YtDlpException(L.Get("Error_DouyinUnavailable"));
            var video = DouyinParser.Parse(detail);
            var stream = DouyinParser.Pick(video, req.MaxHeight, req.Codec);
            var date = video.UploadDate is { Length: 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : null;
            var known = new Dictionary<string, string?>
            {
                ["title"] = video.Title,
                ["uploader"] = video.Author ?? "Unknown",
                ["date"] = date ?? "unknown",
                ["year"] = date?[..4] ?? "unknown",
                ["month"] = date?[5..7] ?? "00",
                ["resolution"] = req.IsCover ? "cover" : req.IsAudio ? "audio" : $"{stream.Resolution}p",
                ["id"] = video.Id,
            };
            var ffmpegDir = ToolManager.FfmpegPath is { } ff ? Path.GetDirectoryName(ff) : null;
            // The media host needs no cookies; only the Referer.
            var env = new YtDlpArgs.Environment(ffmpegDir, null, SkipBrowserCookies: true);
            // The cover is a plain image address too, downloaded the same way.
            var media = req.IsCover ? video.Cover ?? throw new YtDlpException(L.Get("Error_NoCover")) : stream.Url;
            var direct = new YtDlpArgs.DirectMedia(media, DouyinReferer, known);
            knownTitle = video.Title;
            (code, error) = await RunAsync(YtDlpArgs.Download(req with { Url = $"https://www.douyin.com/video/{video.Id}" }, AppSettings.Current, env, direct), OnLine, ct);
        }
        else
        {
            if (req.IsAudio && req.AudioOutputFormat == MediaFeatures.Original)
                req = req with { AudioFormat = await OriginalAudioFormatAsync(req, ct) };
            (code, error) = await RunWithCookieFallbackAsync(req.Url,
                env => YtDlpArgs.Download(req, AppSettings.Current, env), OnLine, ct, accelerate: true);
        }

        ct.ThrowIfCancellationRequested();
        if (code != 0) throw new YtDlpException(ErrorMapper.Friendly(error), error);
        if (!req.WriteSubtitles || filePath is null || !File.Exists(filePath)) return filePath;
        // The video is saved: whatever goes wrong with its subtitles from here is said, not a failed download.
        try
        {
            onEvent(new SubtitleStepEvent());
            if (await CompleteSubtitlesAsync(req, filePath, onEvent, ct) is { } problem) onEvent(new SubtitleProblemEvent(problem));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Subtitles for {Path.GetFileName(filePath)}: {ex.Message}");
            onEvent(new SubtitleProblemEvent(ex.Message.Replace('\n', ' ')));
        }
        return LeaveLoneFolder(filePath);
    }

    /// <summary>What subtitles fall back to when a video has none: speech recognition telling the language itself.</summary>
    const string SpeechFallback = SubtitleFiles.SpeechPrefix + "auto";

    /// <summary>Any track the uploader provided, in whatever language (not danmaku, not YouTube's live chat).</summary>
    const string AnyTrack = "all,-danmaku,-live_chat";

    /// <summary>With no particular track chosen, a video without subtitles gets them from its sound, once speech
    /// recognition has been set up (installing it is the user saying they want that).</summary>
    static bool CanFallBackToSpeech(DownloadRequest req) => req.SubtitleLanguage.Length == 0 && SpeechRecognizer.CanRecognize;

    /// <summary>
    /// After a video (or audio) download with subtitles. yt-dlp brought the ones in the languages chosen; when none
    /// came and none in particular was asked for, the automatic ones in the video's own language are tried, then any
    /// track at all, then speech recognition. Then they become the format chosen, or go inside the video.
    /// Returns why there are none, when there are none.
    /// </summary>
    static async Task<string?> CompleteSubtitlesAsync(DownloadRequest req, string file, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        var douyin = UrlTools.DetectPlatform(req.Url) == Platform.Douyin;
        if (SubtitleFiles.IsSpeech(req.SubtitleLanguage))
        {
            if (!await RecognizeBesideAsync(file, req.SubtitleLanguage, onEvent, ct)) return L.Get("Sub_NoSpeech");
        }
        else if (!HasSubtitlesBeside(file))
        {
            if (req.SubtitleLanguage.Length > 0) return L.Get("Sub_NotFound");
            // Douyin's videos never have subtitles (and its downloads are not yt-dlp's to repeat).
            if (!douyin)
                foreach (var langs in new[] { SubtitleFiles.AutoOriginal, AnyTrack })
                {
                    Log.Info($"No subtitles in the languages chosen, trying {langs}");
                    await FetchBesideAsync(req, file, langs, ct);
                    if (!HasSubtitlesBeside(file)) continue;
                    if (langs == AnyTrack) KeepOne(SubtitlesBeside(file));
                    break;
                }
            if (!HasSubtitlesBeside(file))
            {
                if (!SpeechRecognizer.CanRecognize) return L.Get("Sub_NotFound");
                Log.Info("No subtitles at all, making them by speech recognition");
                if (!await RecognizeBesideAsync(file, SpeechFallback, onEvent, ct)) return L.Get("Sub_NoSpeech");
            }
        }

        var subtitles = SubtitlesBeside(file);
        if (req.EmbedSubtitles && ToolManager.FfmpegPath is not null)
            await EmbedAsync(file, subtitles, ct);
        else
            await ConvertBesideAsync(subtitles, SubtitleFiles.Normalize(req.SubtitleFormat), req.Url, ct);
        return null;
    }

    /// <summary>One more yt-dlp run for subtitles alone, saved next to the video under its name.</summary>
    static async Task FetchBesideAsync(DownloadRequest req, string file, string languages, CancellationToken ct)
    {
        var sub = req with
        {
            Mode = DownloadRequest.SubtitlesMode, WriteSubtitles = false, OutputFolder = Path.GetDirectoryName(file)!,
            FolderTemplate = "", SubtitleLanguage = languages, SubtitleFormat = SubtitleFiles.DefaultFormat, EmbedSubtitles = false,
        };
        var (code, error) = await RunWithCookieFallbackAsync(sub.Url, env => YtDlpArgs.Download(sub, AppSettings.Current, env), _ => { }, ct);
        ct.ThrowIfCancellationRequested();
        if (code != 0) Log.Info($"Subtitles ({languages}) not fetched: {error.Split('\n').LastOrDefault()}");
    }

    static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa", ".lrc"];

    /// <summary>The subtitle files yt-dlp (or recognition) saved next to a downloaded file: "Title [id].zh.srt" for
    /// "Title [id].mp4". Danmaku is not subtitles.</summary>
    internal static List<string> SubtitlesBeside(string file)
    {
        var folder = Path.GetDirectoryName(file);
        if (folder is null || !Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(file) + ".";
        return Directory.EnumerateFiles(folder)
            .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && SubtitleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool HasSubtitlesBeside(string file) => SubtitlesBeside(file).Count > 0;

    /// <summary>"Any track" can bring dozens of translations: one is kept (English when there is one, the most widely
    /// read), the others removed. Returns the one kept.</summary>
    internal static string KeepOne(List<string> subtitles)
    {
        var kept = subtitles.FirstOrDefault(s => LanguageOf(s).StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? subtitles[0];
        foreach (var s in subtitles.Where(s => s != kept)) MediaConverter.TryDelete(s);
        return kept;
    }

    /// <summary>"Title [id].zh-CN.srt" → "zh-CN".</summary>
    internal static string LanguageOf(string subtitle) =>
        Path.GetExtension(Path.GetFileNameWithoutExtension(subtitle)).TrimStart('.') is { Length: > 0 } l ? l : "und";

    /// <summary>Each subtitle file into the format chosen (TXT and Markdown written by VidFlow, the timed ones by ffmpeg).</summary>
    static async Task ConvertBesideAsync(List<string> subtitles, string format, string url, CancellationToken ct)
    {
        foreach (var path in subtitles)
        {
            if (Path.GetExtension(path).Equals("." + format, StringComparison.OrdinalIgnoreCase)) continue;
            if (SubtitleFiles.IsText(format))
            {
                // TXT and Markdown are read from SRT or WebVTT; others are put into SRT first.
                var source = Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" ? path : await SpeechRecognizer.ConvertAsync(path, "srt", ct);
                SubtitleFiles.WriteText(new SavedSubtitle(LanguageOf(source), source), format, null, url);
            }
            else await SpeechRecognizer.ConvertAsync(path, format, ct);
        }
    }

    /// <summary>
    /// The subtitles into the video as tracks players can turn on (MP4's own text format, SRT in MKV, WebVTT in
    /// WebM), each named by its language; the files beside it then go. The picture and sound are copied as they are.
    /// </summary>
    internal static async Task EmbedAsync(string file, List<string> subtitles, CancellationToken ct)
    {
        if (subtitles.Count == 0) return;
        var ffmpeg = ToolManager.FfmpegPath ?? throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        var part = MediaConverter.PartPath(file);
        try
        {
            var (code, error) = await ProcessRunner.RunAsync(ffmpeg, EmbedArguments(file, subtitles, part), null, ct);
            if (code != 0 || !File.Exists(part)) throw new YtDlpException(L.Get("Error_EmbedSubtitles"), error);
            File.Move(part, file, overwrite: true);
            foreach (var s in subtitles) MediaConverter.TryDelete(s);
        }
        finally
        {
            MediaConverter.TryDelete(part);
        }
    }

    /// <summary>ffmpeg's arguments for <see cref="EmbedAsync"/> (pure, for testing).</summary>
    internal static List<string> EmbedArguments(string file, List<string> subtitles, string dest)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", file };
        foreach (var s in subtitles) args.AddRange(["-i", s]);
        // The video's own tracks, minus subtitles it already had (those would clash in MP4's format).
        args.AddRange(["-map", "0", "-map", "-0:s?"]);
        for (var i = 0; i < subtitles.Count; i++) args.AddRange(["-map", $"{i + 1}:0"]);
        var codec = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".mkv" => "srt",
            ".webm" => "webvtt",
            _ => "mov_text",
        };
        args.AddRange(["-c", "copy", "-c:s", codec]);
        for (var i = 0; i < subtitles.Count; i++)
        {
            var language = LanguageOf(subtitles[i]);
            args.AddRange([$"-metadata:s:s:{i}", $"title={language}"]);
            if (Iso639_2(language) is { } code) args.AddRange([$"-metadata:s:s:{i}", $"language={code}"]);
        }
        args.Add(dest);
        return args;
    }

    /// <summary>The three-letter code players show for a subtitle track ("zh-CN" and Bilibili's "ai-zh" → "chi").</summary>
    internal static string? Iso639_2(string language)
    {
        var code = language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase) ? language[3..] : language;
        return code.Split('-', '_')[0].ToLowerInvariant() switch
        {
            "zh" => "chi",
            "en" => "eng",
            "ja" => "jpn",
            "ko" => "kor",
            "fr" => "fre",
            "de" => "ger",
            "es" => "spa",
            "ru" => "rus",
            "pt" => "por",
            "it" => "ita",
            _ => null,
        };
    }

    /// <summary>Speech recognition on a video or audio file just downloaded: "Title [id].zh.srt" goes next to it.
    /// False when nothing was said (music, silence): no empty file is left.</summary>
    static async Task<bool> RecognizeBesideAsync(string file, string choice, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        if (!SpeechRecognizer.CanRecognize) throw new YtDlpException(L.Get("Error_AsrNotReady"));
        var work = Path.Combine(SpeechRecognizer.WorkRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var (srt, language) = await SpeechRecognizer.RecognizeAsync(file, choice[SubtitleFiles.SpeechPrefix.Length..],
                Path.Combine(work, "speech"), p => onEvent(new RecognizingEvent(p)), ct);
            if (!(await File.ReadAllTextAsync(srt, ct)).Contains("-->", StringComparison.Ordinal))
            {
                Log.Info($"No speech found in {Path.GetFileName(file)}: no subtitles made");
                return false;
            }
            var dest = Path.ChangeExtension(file, null) + $".{language}.srt";
            File.Move(srt, dest, overwrite: true);
            Log.Info($"Subtitles made by speech recognition: {dest}");
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                Log.Info($"Speech work folder not removed: {ex.Message}");
            }
        }
    }

    /// <summary>A download with subtitles goes into a folder of its own (see <see cref="NamingTemplate"/>); when no
    /// subtitles came, the file alone needs none, so it moves up and the folder goes. Returns where the file is.</summary>
    internal static string LeaveLoneFolder(string file)
    {
        try
        {
            var folder = Path.GetDirectoryName(file);
            var parent = Path.GetDirectoryName(folder);
            // Only the folder named after the file, holding nothing else; never over a file already there.
            if (folder is null || parent is null || Path.GetFileName(folder) != Path.GetFileNameWithoutExtension(file)
                || Directory.EnumerateFileSystemEntries(folder).Skip(1).Any())
                return file;
            var dest = Path.Combine(parent, Path.GetFileName(file));
            if (File.Exists(dest)) return file;
            File.Move(file, dest);
            Directory.Delete(folder);
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Left in its own folder: {ex.Message}");
            return file;
        }
    }

    /// <summary>The subtitles alone. With no language chosen it is the uploader's own subtitles in the languages from
    /// Settings, failing those the automatic ones in the video's own language, then any track, then speech
    /// recognition (when set up). Returns the first file.</summary>
    static async Task<string> DownloadSubtitlesAsync(DownloadRequest req, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        if (SubtitleFiles.IsSpeech(req.SubtitleLanguage)) return await RecognizeSubtitlesAsync(req, onEvent, ct);
        // Douyin's own downloads don't go through yt-dlp, and its videos come without subtitles.
        if (UrlTools.DetectPlatform(req.Url) == Platform.Douyin)
            return CanFallBackToSpeech(req) ? await RecognizeSubtitlesAsync(req with { SubtitleLanguage = SpeechFallback }, onEvent, ct)
                : throw new YtDlpException(L.Get("Error_NoSubtitles"));
        var target = SubtitleFiles.IsText(req.SubtitleFormat) ? "srt" : req.SubtitleFormat;

        async Task<(List<SavedSubtitle> Files, string? Title, string? Url)> RunOnce(DownloadRequest r)
        {
            string? result = null;
            var (code, error) = await RunWithCookieFallbackAsync(r.Url, env => YtDlpArgs.Download(r, AppSettings.Current, env),
                line => { if (line.StartsWith("VFSUB|", StringComparison.Ordinal)) result = line[6..]; }, ct);
            ct.ThrowIfCancellationRequested();
            if (code != 0) throw new YtDlpException(ErrorMapper.Friendly(error), error);
            return SubtitleFiles.Saved(result, target, r.OutputFolder);
        }

        var saved = await RunOnce(req);
        if (saved.Files.Count == 0 && req.SubtitleLanguage.Length == 0)
        {
            Log.Info("No subtitles in the chosen languages, trying the automatic ones");
            saved = await RunOnce(req with { SubtitleLanguage = SubtitleFiles.AutoOriginal });
            if (saved.Files.Count == 0)
            {
                Log.Info("No automatic ones either, trying any track");
                saved = await RunOnce(req with { SubtitleLanguage = AnyTrack });
                if (saved.Files.Count > 1)
                {
                    var kept = KeepOne(saved.Files.Select(f => f.Path).ToList());
                    saved = (saved.Files.Where(f => f.Path == kept).ToList(), saved.Title, saved.Url);
                }
            }
        }
        if (saved.Files.Count == 0)
        {
            if (!CanFallBackToSpeech(req)) throw new YtDlpException(L.Get("Error_NoSubtitles"));
            Log.Info("No subtitles at all, making them by speech recognition");
            return await RecognizeSubtitlesAsync(req with { SubtitleLanguage = SpeechFallback }, onEvent, ct);
        }
        if (!SubtitleFiles.IsText(req.SubtitleFormat)) return saved.Files[0].Path;
        return saved.Files.Select(f => SubtitleFiles.WriteText(f, req.SubtitleFormat, saved.Title, saved.Url ?? req.Url)).ToList()[0];
    }

    /// <summary>Subtitles made from the sound: the audio is downloaded like any audio download (so Douyin and file
    /// naming work as usual) but into a folder of its own under Temp, so nothing already in the download folder (an
    /// audio download of the same video, say) is reused or removed. The subtitles get the name the audio had:
    /// "Title [id].zh.srt", or "Title [id].zh.asr.srt" when that is taken.</summary>
    static async Task<string> RecognizeSubtitlesAsync(DownloadRequest req, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        if (!SpeechRecognizer.CanRecognize) throw new YtDlpException(L.Get("Error_AsrNotReady"));
        var work = Path.Combine(SpeechRecognizer.WorkRoot, Guid.NewGuid().ToString("N"));
        try
        {
            string? title = null;
            // MaxHeight 1: where sound only comes with the picture (Douyin), the smallest picture will do.
            var audio = await DownloadAsync(req with
                {
                    Mode = "audio", AudioFormat = "wav", MaxHeight = 1, WriteSubtitles = false, SubtitleLanguage = "", OutputFolder = work,
                },
                e => { if (e is StartedEvent { Title: { } t }) title ??= t; onEvent(e); }, ct);
            if (audio is null || !File.Exists(audio)) throw new YtDlpException(L.Get("Error_AsrFailed"));

            var (srt, language) = await SpeechRecognizer.RecognizeAsync(audio, req.SubtitleLanguage[SubtitleFiles.SpeechPrefix.Length..],
                Path.Combine(work, "speech"), p => onEvent(new RecognizingEvent(p)), ct);
            // Where the audio would have gone (the same folders and name), with the language before the extension.
            var name = Path.Combine(req.OutputFolder, Path.ChangeExtension(Path.GetRelativePath(work, audio), null));
            var tag = File.Exists($"{name}.{language}.{req.SubtitleFormat}") ? $"{language}.asr" : language;
            var path = $"{name}.{tag}.srt";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(srt, path, overwrite: true);
            return req.SubtitleFormat switch
            {
                "srt" => path,
                "txt" or "md" => SubtitleFiles.WriteText(new SavedSubtitle(language, path), req.SubtitleFormat, title, req.Url),
                _ => await SpeechRecognizer.ConvertAsync(path, req.SubtitleFormat, ct),
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                Log.Info($"Speech work folder not removed: {ex.Message}");
            }
        }
    }

    /// <summary>"Original" audio: which codec the track has decides the file type (Hi-Res FLAC → .flac). Falls back to
    /// yt-dlp's "best" if the question can't be answered.</summary>
    static async Task<string> OriginalAudioFormatAsync(DownloadRequest req, CancellationToken ct)
    {
        string? codec = null;
        var (code, _) = await RunWithCookieFallbackAsync(req.Url,
            env => YtDlpArgs.ProbeAudio(req, AppSettings.Current, env),
            line => codec ??= line.Trim().Length > 0 ? line.Trim() : null, ct);
        var format = code == 0 ? MediaFeatures.OriginalOutput(codec) : MediaFeatures.Original;
        Log.Info($"Original audio: {codec ?? "?"} -> {format}");
        return format;
    }

    /// <summary>
    /// Runs yt-dlp with cookie fallbacks:
    /// <list type="bullet">
    /// <item>If the configured browser's cookies can't be read (Chrome/Edge lock them while running), retry
    /// without them and skip them for the rest of the session.</item>
    /// <item>YouTube: signed-in requests often come back with no downloadable formats (YouTube treats them
    /// differently, and exported cookies get rotated), so try without the in-app login first and only use it
    /// when YouTube says sign-in is needed (age / members-only / bot check). Bilibili always uses the login.</item>
    /// </list>
    /// </summary>
    static async Task<(int Code, string Error)> RunWithCookieFallbackAsync(
        string url, Func<YtDlpArgs.Environment, List<string>> buildArgs, Action<string> onLine, CancellationToken ct,
        bool accelerate = false)
    {
        var ffmpegDir = ToolManager.FfmpegPath is { } ff ? Path.GetDirectoryName(ff) : null;
        var browser = AppSettings.Current.CookiesBrowser;
        var loginFile = AccountStore.LoginCookieFileOrNull;
        var youTubeLogin = loginFile is not null && UrlTools.DetectPlatform(url) == Platform.YouTube
                           && AccountStore.IsLoggedIn(Platform.YouTube);
        var env = new YtDlpArgs.Environment(ffmpegDir, loginFile,
            SkipBrowserCookies: _unreadableBrowser == browser,
            SkipLoginCookies: youTubeLogin,
            DenoPath: ToolManager.DenoPath);
        if (accelerate) env = Accelerated(env, url);

        var result = await RunAsync(buildArgs(env), onLine, ct);

        if (result.Code != 0 && !env.SkipBrowserCookies && YtDlpArgs.UsesBrowserCookies(AppSettings.Current)
            && ErrorMapper.IsCookieReadError(result.Error))
        {
            Log.Warn($"Browser cookies unreadable, skipping them this session: {ErrorMapper.LastErrorLine(result.Error)}");
            _unreadableBrowser = browser;
            BrowserCookiesFailed?.Invoke();
            env = env with { SkipBrowserCookies = true };
            result = await RunAsync(buildArgs(env), onLine, ct);
        }

        if (result.Code != 0 && youTubeLogin && env.SkipLoginCookies && ErrorMapper.NeedsSignIn(result.Error))
        {
            Log.Info($"YouTube needs sign-in, retrying with the in-app login: {ErrorMapper.LastErrorLine(result.Error)}");
            env = env with { SkipLoginCookies = false };
            result = await RunAsync(buildArgs(env), onLine, ct);
        }

        // aria2c can trip over what yt-dlp's own downloader copes with (a host refusing parallel connections, an
        // unusual proxy): fall back to one connection rather than fail.
        if (result.Code != 0 && env.Aria2Path is not null && ErrorMapper.IsAria2Failure(result.Error))
        {
            Log.Warn($"aria2c failed, retrying with yt-dlp's own downloader: {ErrorMapper.LastErrorLine(result.Error)}");
            result = await RunAsync(buildArgs(env with { Aria2Path = null }), onLine, ct);
        }
        return result;
    }

    /// <summary>Adds aria2 (when installed and switched on) and the proxy Windows would use for <paramref name="url"/>.
    /// Not for the Chinese platforms: their CDNs are fast on one connection, and some of Bilibili's nodes answer a
    /// burst of connections with 503.</summary>
    static YtDlpArgs.Environment Accelerated(YtDlpArgs.Environment env, string url)
    {
        if (!AppSettings.Current.MultiConnection || ToolManager.Aria2Path is not { } aria2) return env;
        if (UrlTools.DetectPlatform(url) is Platform.Bilibili or Platform.Douyin or Platform.Weibo or Platform.Xiaohongshu) return env;
        string? proxy = null;
        try
        {
            var target = new Uri(url);
            // Environment variables (HTTPS_PROXY) first, then Windows' own proxy setting; null when bypassed.
            if (HttpClient.DefaultProxy.GetProxy(target) is { } p && p != target) proxy = p.GetLeftPart(UriPartial.Authority);
        }
        catch (Exception ex)
        {
            Log.Info($"System proxy lookup failed: {ex.Message}");
        }
        return env with { Aria2Path = aria2, SystemProxy = proxy };
    }

    /// <summary>Runs yt-dlp, streaming stdout lines to <paramref name="onLine"/>. Returns exit code and stderr.</summary>
    static async Task<(int Code, string Error)> RunAsync(List<string> args, Action<string> onLine, CancellationToken ct)
    {
        var exe = ToolManager.YtDlpPath ?? throw new YtDlpException(L.Get("Error_NoYtDlp"));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Info("yt-dlp " + string.Join(' ', args.Select(a => a.Contains(' ') || a.Contains('|') ? $"\"{a}\"" : a)));

        using var p = new Process { StartInfo = psi };
        var errors = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors)
                if (errors.Length < 16000) errors.AppendLine(e.Data);
        };

        p.Start();
        ProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        string error;
        lock (errors) error = errors.ToString().Trim();
        if (p.ExitCode != 0) Log.Warn($"yt-dlp exited {p.ExitCode}: {ErrorMapper.LastErrorLine(error)}");
        return (p.ExitCode, error);
    }
}
