using System.Globalization;
using System.Text;
using System.Text.Json;
using VidFlow.Localization;

namespace VidFlow.Services;

public enum LocalSubtitleOperation { Generate, GenerateAndEmbed, Extract, Remove, Embed }

public sealed record LocalSubtitleTrack(int Index, string Codec, string Language, string? Title)
{
    public bool CanExport => Codec is "subrip" or "srt" or "webvtt" or "ass" or "ssa" or "mov_text" or "text" or "ttml";
}

public sealed record LocalSubtitleInfo(bool HasVideo, bool HasAudio, double Duration, IReadOnlyList<LocalSubtitleTrack> Tracks);
public sealed record LocalSubtitleRequest(string Source, LocalSubtitleOperation Operation, string Language = "auto",
    string Format = "srt", int TrackIndex = -1, string? OutputFolder = null, string? SubtitleFile = null);
public sealed record LocalSubtitleProgress(string MessageKey, double? Percent = null);

/// <summary>Subtitle operations on local media. Sources are read only; only verified outputs are published.</summary>
public static class LocalSubtitles
{
    public static LocalSubtitleOperation ResolveOperation(LocalSubtitleOperation action, bool embedGenerated) =>
        action == LocalSubtitleOperation.Generate && embedGenerated ? LocalSubtitleOperation.GenerateAndEmbed : action;

    public static bool NeedsSpeech(LocalSubtitleOperation operation) =>
        operation is LocalSubtitleOperation.Generate or LocalSubtitleOperation.GenerateAndEmbed;

    public static LocalSubtitleInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var video = false;
        var audio = false;
        var tracks = new List<LocalSubtitleTrack>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                var attached = stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;
                video |= type == "video" && !attached;
                audio |= type == "audio";
                if (type != "subtitle" || !stream.TryGetProperty("index", out var index)) continue;
                var tags = stream.TryGetProperty("tags", out var t) ? t : default;
                tracks.Add(new(index.GetInt32(), Text(stream, "codec_name") ?? "", Text(tags, "language") ?? "und", Text(tags, "title")));
            }
        var duration = root.TryGetProperty("format", out var format) &&
            double.TryParse(Text(format, "duration"), CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        return new(video, audio, duration, tracks);
    }

    static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static async Task<LocalSubtitleInfo> InspectAsync(string source, CancellationToken ct)
    {
        if (!File.Exists(source)) throw new FileNotFoundException(L.Get("LocalSub_MissingFile"), source);
        var ffmpeg = ToolManager.FfmpegPath ?? throw new YtDlpException(L.Get("Error_ConvertNeedsFfmpeg"));
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(probe)) throw new YtDlpException(L.Get("Error_ConvertNeedsFfmpeg"));
        var json = new StringBuilder();
        var (code, error) = await ProcessRunner.RunAsync(probe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", source],
            line => json.AppendLine(line), ct, stdoutOnly: true);
        if (code != 0) throw new YtDlpException(L.Get("Error_ConvertUnreadable"), error);
        return ParseInfo(json.ToString());
    }

    public static string OutputPath(string source, string suffix, string extension, string? folder = null)
    {
        var dir = string.IsNullOrWhiteSpace(folder) ? Path.GetDirectoryName(Path.GetFullPath(source))! : folder;
        var stem = Path.GetFileNameWithoutExtension(source) + "." + suffix;
        var path = Path.Combine(dir, stem + "." + extension);
        for (var n = 2; File.Exists(path) || File.Exists(MediaConverter.PartPath(path)) ||
            Path.GetFullPath(path).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase); n++)
            path = Path.Combine(dir, $"{stem} ({n}).{extension}");
        return path;
    }

    public static List<string> RemoveArguments(string source, string dest) =>
        ["-hide_banner", "-v", "error", "-y", "-i", source, "-map", "0", "-map", "-0:s?", "-c", "copy",
            "-progress", "pipe:1", "-nostats", dest];

    public static List<string> EmbedArguments(string source, string subtitle, string dest, LocalSubtitleInfo info, string language, string? title = null)
    {
        // MKV holds the original audio/video codecs and existing subtitle tracks without re-encoding the media.
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", source, "-i", subtitle,
            "-map", "0", "-map", "-0:d?", "-map", "1:0", "-c", "copy" };
        for (var i = 0; i < info.Tracks.Count; i++)
            if (info.Tracks[i].Codec is "mov_text" or "text") args.AddRange([$"-c:s:{i}", "srt"]);
        args.AddRange([$"-c:s:{info.Tracks.Count}", "srt", $"-metadata:s:s:{info.Tracks.Count}", $"title={title ?? L.Get("LocalSub_GeneratedTrack")}"]);
        if (YtDlpService.Iso639_2(language) is { } code)
            args.AddRange([$"-metadata:s:s:{info.Tracks.Count}", $"language={code}"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", dest]);
        return args;
    }

    public static async Task<string> ProcessAsync(LocalSubtitleRequest request, Action<LocalSubtitleProgress> onProgress, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Operation)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Operation == LocalSubtitleOperation.Embed)
        {
            if (string.IsNullOrWhiteSpace(request.SubtitleFile)) throw new YtDlpException(L.Get("LocalSub_ChooseSubtitleNote"));
            if (!File.Exists(request.SubtitleFile)) throw new FileNotFoundException(L.Get("LocalSub_MissingSubtitle"), request.SubtitleFile);
            if (MediaConverter.KindOf(request.SubtitleFile) != MediaKind.Subtitle)
                throw new YtDlpException(L.Get("LocalSub_TimedSubtitleRequired"));
        }
        onProgress(new("LocalSub_Inspecting"));
        var info = await InspectAsync(request.Source, ct);
        if (!info.HasVideo) throw new YtDlpException(L.Get("Error_ConvertNoVideo"));
        if (NeedsSpeech(request.Operation) && !info.HasAudio) throw new YtDlpException(L.Get("Error_ConvertNoAudio"));
        if (NeedsSpeech(request.Operation) && !SpeechRecognizer.CanRecognize) throw new YtDlpException(L.Get("Error_AsrNotReady"));
        if (request.Operation is LocalSubtitleOperation.Remove or LocalSubtitleOperation.Extract && info.Tracks.Count == 0)
            throw new YtDlpException(L.Get("LocalSub_NoTracks"));

        var work = Path.Combine(Path.GetTempPath(), "VidFlow", "local-subtitles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string? part = null;
        try
        {
            var ffmpeg = ToolManager.FfmpegPath!;
            string? srt = null;
            var language = "und";
            string? trackTitle = null;
            var trackIndex = request.TrackIndex;
            if (NeedsSpeech(request.Operation))
            {
                (srt, language) = await SpeechRecognizer.RecognizeAsync(request.Source, request.Language, Path.Combine(work, "speech"),
                    p => onProgress(new(p < 0 ? "LocalSub_WaitingSpeech" : "LocalSub_Recognizing", p < 0 ? null : p)), ct);
                if (!SubtitleFiles.HasContent(srt)) throw new YtDlpException(L.Get("LocalSub_NoSpeech"));
            }
            else if (request.Operation == LocalSubtitleOperation.Extract)
            {
                var track = request.TrackIndex < 0 ? info.Tracks.FirstOrDefault(t => t.CanExport)
                    : info.Tracks.FirstOrDefault(t => t.Index == request.TrackIndex);
                if (track is null || !track.CanExport) throw new YtDlpException(L.Get("LocalSub_ImageTrack"));
                language = track.Language;
                trackIndex = track.Index;
                srt = Path.Combine(work, "extracted.srt");
                onProgress(new("LocalSub_Processing"));
                await RunAsync(ffmpeg, ["-v", "error", "-y", "-i", request.Source, "-map", $"0:{track.Index}", "-c:s", "srt", srt], info, onProgress, ct);
                if (!SubtitleFiles.HasContent(srt)) throw new YtDlpException(L.Get("LocalSub_EmptyTrack"));
            }
            else if (request.Operation == LocalSubtitleOperation.Embed)
            {
                srt = Path.Combine(work, "imported.srt");
                onProgress(new("LocalSub_Processing"));
                await MediaConverter.ConvertAsync(request.SubtitleFile!, srt, "srt", _ => { }, ct);
                if (!SubtitleFiles.HasContent(srt)) throw new YtDlpException(L.Get("LocalSub_EmptyTrack"));
                language = YtDlpService.LanguageOf(request.SubtitleFile!);
                trackTitle = Path.GetFileNameWithoutExtension(request.SubtitleFile!);
            }

            if (request.Operation is LocalSubtitleOperation.Generate or LocalSubtitleOperation.Extract)
            {
                var format = SubtitleFiles.Normalize(request.Format);
                var converted = srt!;
                onProgress(new("LocalSub_Processing"));
                if (SubtitleFiles.IsText(format))
                    converted = SubtitleFiles.WriteText(new(language, srt!), format, Path.GetFileNameWithoutExtension(request.Source), null);
                else if (format != "srt")
                {
                    converted = Path.Combine(work, "result." + format);
                    await MediaConverter.ConvertAsync(srt!, converted, format, _ => { }, ct);
                }
                var tag = new string(language.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
                if (tag.Length == 0) tag = "und";
                var suffix = request.Operation == LocalSubtitleOperation.Generate ? tag : $"track{trackIndex}.{tag}";
                var dest = OutputPath(request.Source, suffix, format, request.OutputFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                ct.ThrowIfCancellationRequested();
                File.Move(converted, dest); // No overwrite, even if another job claimed the name meanwhile.
                return dest;
            }

            var removing = request.Operation == LocalSubtitleOperation.Remove;
            var extension = removing ? Path.GetExtension(request.Source).TrimStart('.') : "mkv";
            var output = OutputPath(request.Source, removing ? "no-subs" : "subtitled", extension, request.OutputFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            part = MediaConverter.PartPath(output);
            onProgress(new("LocalSub_Processing", 0));
            await RunAsync(ffmpeg, removing ? RemoveArguments(request.Source, part)
                : EmbedArguments(request.Source, srt!, part, info, language, trackTitle), info, onProgress, ct);
            onProgress(new("LocalSub_Verifying"));
            if (removing)
            {
                if ((await InspectAsync(part, ct)).Tracks.Count != 0) throw new YtDlpException(L.Get("LocalSub_VerifyFailed"));
            }
            else
            {
                var json = new StringBuilder();
                var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
                var (code, error) = await ProcessRunner.RunAsync(probe,
                    ["-v", "error", "-select_streams", $"s:{info.Tracks.Count}", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "json", part],
                    line => json.AppendLine(line), ct, stdoutOnly: true);
                if (code != 0 || !YtDlpService.HasEmbeddedSubtitles(json.ToString(), 1))
                    throw new YtDlpException(L.Get("LocalSub_VerifyFailed"), error);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, output);
            return output;
        }
        finally
        {
            if (part is not null) MediaConverter.TryDelete(part);
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"Subtitle work retained: {ex.Message}"); }
        }
    }

    static async Task RunAsync(string ffmpeg, List<string> args, LocalSubtitleInfo info, Action<LocalSubtitleProgress> onProgress, CancellationToken ct)
    {
        var (code, error) = await ProcessRunner.RunAsync(ffmpeg, args,
            line => { if (MediaConverter.ProgressOf(line, info.Duration) is { } p) onProgress(new("LocalSub_Processing", p)); }, ct);
        if (code != 0) throw new YtDlpException(L.Get("Error_ConvertFailed"), error);
    }
}
