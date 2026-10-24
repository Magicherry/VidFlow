using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Builds yt-dlp command lines. Pure: everything it depends on is passed in, so it is unit tested.</summary>
public static class YtDlpArgs
{
    public const string OutputTemplate = "%(title).180B [%(id)s].%(ext)s";

    public const string ProgressTemplate =
        "download:VFP|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

    /// <summary>Where cookies and tools come from.</summary>
    public sealed record Environment(string? FfmpegDir, string? LoginCookieFile, bool SkipBrowserCookies);

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
            "-o", OutputTemplate,
            "--print", "before_dl:VFS|%(format_id)s|%(thumbnail)s|%(title)s",
            "--print", "after_move:VFF|%(filepath)s",
            "--progress-template", ProgressTemplate,
        };

        if (req.Mode == "video")
        {
            if (hasFfmpeg)
            {
                // Prefer AAC audio: Opus inside MP4 is rejected by many players and editors.
                args.AddRange(["-f", "bv*+ba[ext=m4a]/bv*+ba/b", "--merge-output-format", "mp4"]);
            }
            else
            {
                // Without ffmpeg separate streams cannot be merged; take the best single file.
                args.AddRange(["-f", "b"]);
            }
            var sort = new List<string>();
            if (req.MaxHeight > 0) sort.Add($"res:{req.MaxHeight}");
            if (s.PreferH264) sort.AddRange(["vcodec:h264", "acodec:m4a"]);
            if (sort.Count > 0) args.AddRange(["-S", string.Join(',', sort)]);
        }
        else
        {
            args.AddRange(["-f", req.Mode == "m4a" ? "ba[ext=m4a]/ba/b" : "ba/b",
                "-x", "--audio-format", req.Mode, "--audio-quality", "0"]);
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

    public static List<string> Common(AppSettings s, Environment env)
    {
        // --encoding: the standalone yt-dlp.exe otherwise prints in the legacy ANSI code page (e.g. GBK),
        // which garbles titles and file paths containing characters like "｜".
        var args = new List<string> { "--no-colors", "--encoding", "utf-8" };
        if (env.FfmpegDir is not null) args.AddRange(["--ffmpeg-location", env.FfmpegDir]);
        if (s.Proxy.Trim().Length > 0) args.AddRange(["--proxy", s.Proxy.Trim()]);

        // Cookie source priority: explicit cookies.txt > browser > in-app login.
        if (s.CookiesFile.Length > 0 && File.Exists(s.CookiesFile))
            args.AddRange(["--cookies", s.CookiesFile]);
        else if (s.CookiesBrowser.Length > 0 && !env.SkipBrowserCookies)
            args.AddRange(["--cookies-from-browser", s.CookiesBrowser]);
        else if (env.LoginCookieFile is not null)
            args.AddRange(["--cookies", env.LoginCookieFile]);
        return args;
    }

    public static bool UsesBrowserCookies(AppSettings s) =>
        s.CookiesBrowser.Length > 0 && !(s.CookiesFile.Length > 0 && File.Exists(s.CookiesFile));
}
