using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Builds yt-dlp command lines. Pure: everything it depends on is passed in, so it is unit tested.</summary>
public static class YtDlpArgs
{
    public const string OutputTemplate = "%(title).180B [%(id)s].%(ext)s";

    public const string ProgressTemplate =
        "download:VFP|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s"
        + "|%(progress.fragment_index)s|%(progress.fragment_count)s";

    /// <summary>Printed before downloading: format, size of each stream (NA when unknown), thumbnail, title.</summary>
    public const string StartTemplate =
        "before_dl:VFS|%(format_id)s"
        + "|%(requested_formats.0.filesize,requested_formats.0.filesize_approx,filesize,filesize_approx|NA)s"
        + ",%(requested_formats.1.filesize,requested_formats.1.filesize_approx|NA)s"
        + "|%(thumbnail)s|%(title)s";

    /// <summary>Where cookies and tools come from.</summary>
    public sealed record Environment(string? FfmpegDir, string? LoginCookieFile, bool SkipBrowserCookies,
        bool SkipLoginCookies = false, string? DenoPath = null);

    public static List<string> Info(string url, AppSettings s, Environment env)
    {
        // --flat-playlist: list playlist / multi-part entries without resolving each one (fast).
        var args = new List<string> { "-J", "--no-warnings", "--flat-playlist" };
        if (UrlTools.IsYouTubeVideoInPlaylist(url)) args.Add("--no-playlist");
        args.AddRange(Common(s, env));
        args.Add(url);
        return args;
    }

    /// <summary>A media file VidFlow located itself (Douyin): yt-dlp only downloads it, so the names it would
    /// normally read from the page are passed in <see cref="Known"/>.</summary>
    public sealed record DirectMedia(string Url, string Referer, IReadOnlyDictionary<string, string?> Known);

    public static List<string> Download(DownloadRequest req, AppSettings s, Environment env, DirectMedia? direct = null)
    {
        var hasFfmpeg = env.FfmpegDir is not null;
        var platform = UrlTools.DetectPlatform(req.Url);
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--progress", "--no-warnings", "--continue",
            "-P", req.OutputFolder,
            "-o", NamingTemplate.ToOutputTemplate(req.FileNameTemplate, req.FolderTemplate, platform.DisplayName(), direct?.Known),
            "--print", StartTemplate,
            "--print", "after_move:VFF|%(filepath)s",
            "--progress-template", ProgressTemplate,
        };

        if (direct is not null)
        {
            // One ready-made MP4 with picture and sound: nothing to choose or merge.
            args.AddRange(["--add-header", $"Referer:{direct.Referer}"]);
            if (req.IsAudio)
                args.AddRange(["-x", "--audio-format", req.AudioOutputFormat,
                    "--audio-quality", req.AudioOutputFormat is "flac" or "wav" || req.AudioQuality <= 0 ? "0" : $"{req.AudioQuality}K"]);
            else if (req.Container != "mp4" && hasFfmpeg)
                args.AddRange([req.Recode ? "--recode-video" : "--remux-video", req.Container]);
            if (s.SpeedLimitKBps > 0) args.AddRange(["-r", $"{s.SpeedLimitKBps}K"]);
            args.AddRange(Common(s, env));
            args.Add(direct.Url);
            return args;
        }

        var lang = LanguageFilter(req.AudioLanguage);
        if (!req.IsAudio)
        {
            var video = VideoFilter(req);
            var audio = AudioFilter(req);
            if (hasFfmpeg)
            {
                args.AddRange(["-f", FormatChain(video, audio, lang), "--merge-output-format", FormatCompatibility.MergeFormats(req.Container)]);
                // Re-encode when the streams don't fit the chosen container (only if needed; slow).
                if (req.Recode) args.AddRange(["--recode-video", req.Container]);
            }
            else
            {
                // Without ffmpeg separate streams cannot be merged; take the best single file.
                args.AddRange(["-f", string.Join('/', new[] { $"b{NotImage}{video}", $"b{NotImage}", "b" }.Distinct())]);
            }
            // Resolution (yt-dlp's "res" is the shorter side, so portrait 1080×1920 counts as 1080p), then frame rate,
            // then bitrate. yt-dlp's default ranks codecs (AV1 > VP9 > HEVC > H.264) ahead of bitrate, which picks
            // the smallest stream: e.g. Bilibili 1080p AV1 at ~600 kbps over H.264 at ~2600 kbps. Total bitrate, not
            // video bitrate: some sites' best file is one MP4 with sound and no separate video bitrate (X's 10 Mbps
            // 1080p would otherwise lose to a 2.8 Mbps HLS stream).
            var sort = new List<string> { req.MaxHeight > 0 ? $"res:{req.MaxHeight}" : "res" };
            // The global compatibility mode only steers "auto"; an explicit codec choice wins.
            if (s.PreferH264 && req.Codec == VideoCodecs.Auto) sort.AddRange(["vcodec:h264", "acodec:m4a"]);
            sort.AddRange(["fps", "tbr"]);
            if (req.AudioQuality > 0) sort.Add($"abr:{req.AudioQuality}");
            args.AddRange(["-S", string.Join(',', sort)]);
        }
        else
        {
            var fmt = req.AudioOutputFormat;
            var preferred = fmt switch { "m4a" => "[ext=m4a]", "opus" => "[acodec=opus]", _ => "" };
            var chain = new List<string>();
            if (lang.Length > 0) chain.Add($"ba{preferred}{lang}");
            if (preferred.Length > 0) chain.Add($"ba{preferred}");
            if (lang.Length > 0) chain.Add($"ba{lang}");
            chain.AddRange(["ba", "b"]);
            args.AddRange(["-f", string.Join('/', chain.Distinct()), "-x", "--audio-format", fmt,
                "--audio-quality", fmt is "flac" or "wav" || req.AudioQuality <= 0 ? "0" : $"{req.AudioQuality}K"]);
        }

        if (req.WriteSubtitles)
        {
            var langs = string.IsNullOrWhiteSpace(s.SubtitleLanguages) ? SubtitleLanguageSet.Fallback : s.SubtitleLanguages.Trim();
            if (platform == Platform.Bilibili && s.SaveDanmaku) langs += ",danmaku";
            args.AddRange(["--write-subs", "--sub-langs", langs]);
            // Bilibili already serves SRT, and its danmaku is XML that must not be converted.
            if (platform != Platform.Bilibili && hasFfmpeg) args.AddRange(["--convert-subs", "srt"]);
        }

        if (s.EmbedMetadata && hasFfmpeg)
            args.AddRange(["--embed-metadata", "--embed-thumbnail"]);

        if (s.SpeedLimitKBps > 0)
            args.AddRange(["-r", $"{s.SpeedLimitKBps}K"]);

        args.AddRange(Common(s, env));
        args.Add(req.Url);
        return args;
    }

    /// <summary>Video stream filter: the chosen codec, or for WebM (which only holds VP9 / AV1) those codecs.</summary>
    static string VideoFilter(DownloadRequest r)
    {
        var codec = VideoCodecs.Filter(r.Codec);
        if (codec.Length > 0) return codec;
        return r.Container == "webm" ? "[vcodec~='^(vp0?9|av01)']" : "";
    }

    /// <summary>Audio stream filter for video downloads. MP4 prefers AAC (Opus in MP4 trips up many players and
    /// editors), WebM needs Opus / Vorbis, MKV takes the best of anything.</summary>
    static string AudioFilter(DownloadRequest r) => r.AudioFormat switch
    {
        "aac" => "[acodec^=mp4a]",
        "opus" => "[acodec=opus]",
        _ => r.Container switch
        {
            "webm" => "[acodec~='^(opus|vorbis)']",
            "mkv" => "",
            _ => "[ext=m4a]",
        },
    };

    static string LanguageFilter(string language) =>
        string.IsNullOrWhiteSpace(language) ? "" : $"[language^={language.Trim()}]";

    /// <summary>Most specific first, then relax: language, then audio codec, then video codec, so a choice the
    /// site can't satisfy degrades to the best available instead of failing.</summary>
    internal static string FormatChain(string video, string audio, string lang)
    {
        var chain = new List<string>
        {
            $"bv*{NotImage}{video}+ba{audio}{lang}",
            $"bv*{NotImage}{video}+ba{lang}",
            $"bv*{NotImage}{video}+ba{audio}",
            $"bv*{NotImage}{video}+ba",
            $"bv*{NotImage}+ba{audio}",
            $"bv*{NotImage}+ba",
            $"b{NotImage}",
            "b",
        };
        return string.Join('/', chain.Distinct());
    }

    /// <summary>Some sites list preview images among the formats (Weibo's 180×320 "scrubber" JPEG). Their codec is
    /// "unknown" rather than "none", so bv* would take them when a low resolution is asked for.</summary>
    internal const string NotImage = "[ext!=jpg][ext!=png][ext!=webp]";

    public static List<string> Common(AppSettings s, Environment env)
    {
        // --encoding: the standalone yt-dlp.exe otherwise prints in the legacy ANSI code page (e.g. GBK),
        // which garbles titles and file paths containing characters like "｜".
        var args = new List<string> { "--no-colors", "--encoding", "utf-8" };
        if (env.FfmpegDir is not null) args.AddRange(["--ffmpeg-location", env.FfmpegDir]);
        // YouTube needs a JavaScript runtime to solve its player challenges; without one yt-dlp warns that
        // "some formats may be missing" (often the higher-quality ones).
        if (env.DenoPath is not null) args.AddRange(["--js-runtimes", $"deno:{env.DenoPath}"]);
        if (s.Proxy.Trim().Length > 0) args.AddRange(["--proxy", s.Proxy.Trim()]);

        // Cookie source priority: explicit cookies.txt > browser > in-app login.
        if (s.CookiesFile.Length > 0 && File.Exists(s.CookiesFile))
            args.AddRange(["--cookies", s.CookiesFile]);
        else if (s.CookiesBrowser.Length > 0 && !env.SkipBrowserCookies)
            args.AddRange(["--cookies-from-browser", s.CookiesBrowser]);
        else if (env.LoginCookieFile is not null && !env.SkipLoginCookies)
            args.AddRange(["--cookies", env.LoginCookieFile]);
        return args;
    }

    public static bool UsesBrowserCookies(AppSettings s) =>
        s.CookiesBrowser.Length > 0 && !(s.CookiesFile.Length > 0 && File.Exists(s.CookiesFile));
}
