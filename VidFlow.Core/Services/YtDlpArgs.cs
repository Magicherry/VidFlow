using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Builds yt-dlp command lines. Pure: everything it depends on is passed in, so it is unit tested.</summary>
public static class YtDlpArgs
{
    public const string OutputTemplate = "%(title).180B [%(id)s].%(ext)s";

    public const string ProgressTemplate =
        "download:VFP|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

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

    public static List<string> Download(DownloadRequest req, AppSettings s, Environment env)
    {
        var hasFfmpeg = env.FfmpegDir is not null;
        var platform = UrlTools.DetectPlatform(req.Url);
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--progress", "--no-warnings", "--continue",
            "-P", req.OutputFolder,
            "-o", NamingTemplate.ToOutputTemplate(req.FileNameTemplate, req.FolderTemplate, platform.DisplayName()),
            "--print", "before_dl:VFS|%(format_id)s|%(thumbnail)s|%(title)s",
            "--print", "after_move:VFF|%(filepath)s",
            "--progress-template", ProgressTemplate,
        };

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
                args.AddRange(["-f", video.Length > 0 ? $"b{video}/b" : "b"]);
            }
            // Resolution, then frame rate, then bitrate. yt-dlp's default ranks codecs (AV1 > VP9 > HEVC > H.264)
            // ahead of bitrate, which picks the smallest stream: e.g. Bilibili 1080p AV1 at ~600 kbps over H.264 at
            // ~2600 kbps — visibly worse than the site.
            var sort = new List<string> { req.MaxHeight > 0 ? $"res:{req.MaxHeight}" : "res" };
            // The global compatibility mode only steers "auto"; an explicit codec choice wins.
            if (s.PreferH264 && req.Codec == VideoCodecs.Auto) sort.AddRange(["vcodec:h264", "acodec:m4a"]);
            sort.AddRange(["fps", "vbr", "tbr"]);
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
            var langs = string.IsNullOrWhiteSpace(s.SubtitleLanguages) ? "zh.*,en.*" : s.SubtitleLanguages.Trim();
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
            $"bv*{video}+ba{audio}{lang}",
            $"bv*{video}+ba{lang}",
            $"bv*{video}+ba{audio}",
            $"bv*{video}+ba",
            $"bv*+ba{audio}",
            "bv*+ba",
            "b",
        };
        return string.Join('/', chain.Distinct());
    }

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
