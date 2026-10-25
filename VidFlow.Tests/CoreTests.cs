using System.Globalization;
using System.Xml.Linq;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Tests;

public class UrlToolsTests
{
    [Fact]
    public void ExtractsUrlFromBilibiliShareText()
    {
        var text = "【【官方 MV】Never Gonna Give You Up-哔哩哔哩】 https://b23.tv/AbCd123";
        Assert.Equal("https://b23.tv/AbCd123", UrlTools.ExtractUrl(text));
    }

    [Fact]
    public void ExtractsAllDistinctUrlsForBatchPaste()
    {
        var text = "https://youtu.be/a\nhttps://www.bilibili.com/video/BV1x, https://youtu.be/a";
        Assert.Equal(["https://youtu.be/a", "https://www.bilibili.com/video/BV1x"], UrlTools.ExtractUrls(text));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=x", Platform.YouTube)]
    [InlineData("https://youtu.be/x", Platform.YouTube)]
    [InlineData("https://b23.tv/x", Platform.Bilibili)]
    [InlineData("https://www.bilibili.com/video/BV1x", Platform.Bilibili)]
    [InlineData("https://example.com/v", Platform.Other)]
    public void DetectsPlatform(string url, Platform expected) => Assert.Equal(expected, UrlTools.DetectPlatform(url));

    [Fact]
    public void WatchLinkWithPlaylistIsTreatedAsVideo()
    {
        Assert.True(UrlTools.IsYouTubeVideoInPlaylist("https://www.youtube.com/watch?v=abc&list=PL1"));
        Assert.False(UrlTools.IsYouTubeVideoInPlaylist("https://www.youtube.com/playlist?list=PL1"));
    }
}

public class OutputParserTests
{
    [Fact]
    public void TracksVideoThenAudioStreams()
    {
        var p = new YtDlpOutputParser();
        var started = Assert.IsType<StartedEvent>(p.Feed("VFS|137+140|https://i.ytimg.com/a.jpg|A | B title"));
        Assert.Equal(2, started.Parts);
        Assert.Equal("A | B title", started.Title);

        var prog = Assert.IsType<ProgressEvent>(p.Feed("VFP|downloading|500|1000|NA|2048|3")).Progress;
        Assert.Equal(50, prog.Percent);
        Assert.Equal(1, prog.Part);

        var videoDone = Assert.IsType<ProgressEvent>(p.Feed("VFP|finished|1000|1000|NA|NA|NA")).Progress;
        Assert.True(videoDone.StreamFinished);
        Assert.False(videoDone.Processing);

        Assert.Equal(2, Assert.IsType<ProgressEvent>(p.Feed("VFP|downloading|10|100|NA|NA|NA")).Progress.Part);

        var audioDone = Assert.IsType<ProgressEvent>(p.Feed("VFP|finished|100|100|NA|NA|NA")).Progress;
        Assert.True(audioDone.Processing);
    }

    [Fact]
    public void UsesEstimateWhenTotalUnknownAndIgnoresOtherLines()
    {
        var p = new YtDlpOutputParser();
        var prog = Assert.IsType<ProgressEvent>(p.Feed("VFP|downloading|250|NA|1000.0|NA|NA")).Progress;
        Assert.Equal(25, prog.Percent);
        Assert.Null(p.Feed("[download] Destination: x.mp4"));
        Assert.Equal(@"C:\a\b.mp4", Assert.IsType<FileEvent>(p.Feed(@"VFF|C:\a\b.mp4")).Path);
    }

    [Fact]
    public void NaThumbnailBecomesNull()
    {
        var s = Assert.IsType<StartedEvent>(new YtDlpOutputParser().Feed("VFS|18|NA|Title"));
        Assert.Null(s.ThumbnailUrl);
        Assert.Equal(1, s.Parts);
    }
}

public class ErrorMapperTests
{
    [Fact]
    public void PicksLastErrorLineAndAddsHint()
    {
        var raw = "WARNING: something\nERROR: first\nERROR: [youtube] x: Sign in to confirm you're not a bot\n";
        var msg = ErrorMapper.Friendly(raw);
        Assert.EndsWith("Sign in to confirm you're not a bot", msg);
        Assert.Contains('\n', msg); // hint + raw line
    }

    [Theory]
    [InlineData("ERROR: Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271")]
    [InlineData("ERROR: Failed to decrypt with DPAPI")]
    public void DetectsCookieReadErrors(string line) => Assert.True(ErrorMapper.IsCookieReadError(line));

    [Fact]
    public void NoFormatsIsNotReportedAsUnavailable()
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            L.ApplyLanguage("en");
            var raw = "ERROR: [youtube] qY6lCyN0CJA: Requested format is not available. Use --list-formats for a list of available formats";
            Assert.True(ErrorMapper.IsNoFormats(raw));
            Assert.StartsWith(L.Get("Error_NoFormats"), ErrorMapper.Friendly(raw));
            Assert.DoesNotContain(L.Get("Error_Unavailable"), ErrorMapper.Friendly(raw));
            Assert.StartsWith(L.Get("Error_Unavailable"), ErrorMapper.Friendly("ERROR: [youtube] x: Video unavailable"));
            Assert.StartsWith(L.Get("Error_Unavailable"), ErrorMapper.Friendly("ERROR: [youtube] zzzzzzzzzzz: This video is unavailable"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Theory]
    [InlineData("ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", true)]
    [InlineData("ERROR: [youtube] x: Join this channel to get access to members-only content", true)]
    [InlineData("ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", true)]
    [InlineData("ERROR: [youtube] x: Requested format is not available", false)]
    [InlineData("ERROR: [youtube] x: Video unavailable", false)]
    public void DetectsSignInRequired(string error, bool expected) => Assert.Equal(expected, ErrorMapper.NeedsSignIn(error));

    [Fact]
    public void UnknownErrorIsPassedThrough() =>
        Assert.Equal("ERROR: weird", ErrorMapper.Friendly("ERROR: weird"));
}

public class InfoParserTests
{
    const string VideoJson = """
    {
      "title": "Clip", "uploader": "Me", "duration": 100, "webpage_url": "https://www.youtube.com/watch?v=abc",
      "upload_date": "20240131",
      "thumbnails": [{"url": "https://i.ytimg.com/a.webp"}, {"url": "https://i.ytimg.com/hq.jpg"}],
      "formats": [
        {"format_id": "140", "vcodec": "none", "acodec": "mp4a", "filesize": 1000000},
        {"format_id": "137", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 30, "filesize": 5000000},
        {"format_id": "299", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 60, "filesize_approx": 8000000},
        {"format_id": "22", "vcodec": "avc1", "acodec": "mp4a", "height": 720, "fps": 30, "tbr": 800},
        {"format_id": "sb0", "vcodec": "none", "acodec": "none"}
      ]
    }
    """;

    [Fact]
    public void ParsesVideoWithQualitiesAndSizes()
    {
        var v = Assert.IsType<VideoInfo>(InfoParser.Parse(VideoJson, "https://youtu.be/abc"));
        Assert.Equal("Clip", v.Title);
        Assert.Equal(Platform.YouTube, v.Platform);
        Assert.Equal("https://i.ytimg.com/hq.jpg", v.ThumbnailUrl);
        Assert.Equal([1080, 720], v.Qualities.Select(q => q.Height));
        Assert.Equal(60, v.Qualities[0].Fps);
        Assert.Equal(8_000_000 + 1_000_000, v.Qualities[0].EstimatedBytes); // largest 1080p stream + best audio
        Assert.Equal(800 * 1000 / 8 * 100 + 1_000_000, v.Qualities[1].EstimatedBytes); // from tbr × duration
        Assert.Equal(1_000_000, v.AudioBytes);
        Assert.Equal(1080, v.Codecs["h264"]); // all three avc1 streams; the highest is 1080p
        Assert.Single(v.Codecs);
    }

    [Fact]
    public void ParsesPlaylistSkippingUnavailableEntries()
    {
        const string json = """
        {"_type": "playlist", "title": "List", "webpage_url": "https://www.youtube.com/playlist?list=PL1",
         "entries": [
           {"id": "a", "url": "a", "title": "First", "duration": 10},
           null,
           {"url": "https://www.youtube.com/watch?v=b", "title": "Second",
            "thumbnails": [{"url": "https://i.ytimg.com/b.jpg"}]}
         ]}
        """;
        var p = Assert.IsType<PlaylistInfo>(InfoParser.Parse(json, "https://www.youtube.com/playlist?list=PL1"));
        Assert.Equal(2, p.Entries.Count);
        Assert.Equal("https://www.youtube.com/watch?v=a", p.Entries[0].Url);
        Assert.Equal(2, p.Entries[1].Index);
        Assert.Equal("https://i.ytimg.com/b.jpg", p.ThumbnailUrl);
    }

    [Fact]
    public void UpgradesHttpThumbnails()
    {
        var v = Assert.IsType<VideoInfo>(InfoParser.Parse(
            """{"title":"t","thumbnail":"http://i0.hdslb.com/x.jpg","webpage_url":"https://www.bilibili.com/video/BV1"}""",
            "https://www.bilibili.com/video/BV1"));
        Assert.Equal("https://i0.hdslb.com/x.jpg", v.ThumbnailUrl);
        Assert.Equal(Platform.Bilibili, v.Platform);
    }
}

public class ToolVersionTests
{
    [Theory]
    [InlineData("2026.10.21", "2026.10.21", true)]
    [InlineData("2026.10.21", "2026.11.02", false)]
    [InlineData("2026.11.02.233154", "2026.11.02", true)] // nightly is newer than the release it follows
    [InlineData(" 2026.10.21\n", "2026.10.21", true)]
    [InlineData(null, "2026.10.21", false)]
    [InlineData("2026.10.21", null, false)]
    public void ComparesYtDlpVersions(string? installed, string? latest, bool expected) =>
        Assert.Equal(expected, ToolManager.IsUpToDate(installed, latest));

    [Fact]
    public void ComparesFfmpegBuildDates()
    {
        const string installed = "N-128018-ga881f0a9ad-20261024";
        Assert.Equal(new DateOnly(2026, 10, 24), ToolManager.FfmpegBuildDate(installed));
        Assert.True(ToolManager.IsFfmpegUpToDate(installed, new DateOnly(2026, 10, 24)));
        Assert.False(ToolManager.IsFfmpegUpToDate(installed, new DateOnly(2026, 10, 25)));
        Assert.False(ToolManager.IsFfmpegUpToDate("7.1-full_build", new DateOnly(2026, 10, 24))); // no date: can't tell
        Assert.False(ToolManager.IsFfmpegUpToDate(installed, null));
    }

    [Fact]
    public void ReadsTagFromReleaseRedirect()
    {
        Assert.Equal("2026.10.21", ToolManager.TagFromReleaseUrl(new Uri("https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.21")));
        Assert.Null(ToolManager.TagFromReleaseUrl(new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest")));
        Assert.Null(ToolManager.TagFromReleaseUrl(null));
    }
}

public class BilibiliApiTests
{
    [Fact]
    public void FillsPartNamesCoverAndUploader()
    {
        var playlist = new PlaylistInfo
        {
            Url = "https://www.bilibili.com/video/BV1Rx411876f",
            Title = "Course",
            Platform = Platform.Bilibili,
            Entries =
            [
                new PlaylistEntry { Index = 1, Url = "https://www.bilibili.com/video/BV1Rx411876f?p=1", Title = "Video 1" },
                new PlaylistEntry { Index = 2, Url = "https://www.bilibili.com/video/BV1Rx411876f?p=2", Title = "Video 2" },
            ],
        };
        const string view = """
        {"code":0,"data":{"pic":"http://i0.hdslb.com/c.jpg","owner":{"name":"Up"},
         "pages":[{"page":1,"part":"Intro","duration":61},{"page":2,"part":"","duration":5}]}}
        """;
        var result = BilibiliApi.ApplyView(playlist, view);
        Assert.Equal("P1 Intro", result.Entries[0].Title);
        Assert.Equal(61, result.Entries[0].Duration);
        Assert.Equal("Video 2", result.Entries[1].Title); // empty part name keeps the fallback
        Assert.Equal("https://i0.hdslb.com/c.jpg", result.ThumbnailUrl);
        Assert.Equal("Up", result.Uploader);
    }

    [Fact]
    public void ErrorResponseLeavesPlaylistUnchanged()
    {
        var playlist = new PlaylistInfo { Url = "u", Title = "t", Entries = [] };
        Assert.Same(playlist, BilibiliApi.ApplyView(playlist, """{"code":-404,"data":null}"""));
    }
}

public class YtDlpArgsTests
{
    static readonly YtDlpArgs.Environment WithFfmpeg = new(@"C:\ff", null, false);

    static DownloadRequest Req(string url = "https://www.youtube.com/watch?v=x", string mode = "video", int height = 0, bool subs = false) =>
        new(url, mode, height, subs, @"C:\out");

    [Fact]
    public void VideoPrefersAacAndAppliesHeightAndH264Sort()
    {
        var args = YtDlpArgs.Download(Req(height: 1080), new AppSettings { PreferH264 = true }, WithFfmpeg);
        Assert.Contains("bv*+ba[ext=m4a]/bv*+ba/b", args);
        Assert.Equal("res:1080,vcodec:h264,acodec:m4a,fps,vbr,tbr", args[args.IndexOf("-S") + 1]);
        Assert.Equal("https://www.youtube.com/watch?v=x", args[^1]);
    }

    [Fact]
    public void AutoPrefersBitrateOverCodecAtTheSameResolution()
    {
        // yt-dlp's default would rank AV1 first and pick the lowest-bitrate stream; we sort by bitrate instead.
        var args = YtDlpArgs.Download(Req(height: 1080), new AppSettings(), WithFfmpeg);
        Assert.Equal("res:1080,fps,vbr,tbr", args[args.IndexOf("-S") + 1]);
        var best = YtDlpArgs.Download(Req(), new AppSettings(), WithFfmpeg);
        Assert.Equal("res,fps,vbr,tbr", best[best.IndexOf("-S") + 1]);
    }

    [Fact]
    public void WithoutFfmpegTakesSingleFileAndSkipsEmbedding()
    {
        var args = YtDlpArgs.Download(Req(), new AppSettings(), WithFfmpeg with { FfmpegDir = null });
        Assert.Equal("b", args[args.IndexOf("-f") + 1]);
        Assert.DoesNotContain("--embed-thumbnail", args);
        Assert.DoesNotContain("--ffmpeg-location", args);
    }

    [Fact]
    public void AudioModeExtracts()
    {
        var args = YtDlpArgs.Download(Req(mode: "mp3"), new AppSettings(), WithFfmpeg);
        Assert.Contains("-x", args);
        Assert.Equal("mp3", args[args.IndexOf("--audio-format") + 1]);
    }

    [Fact]
    public void ChosenCodecIsTriedFirstWithFallback()
    {
        var req = Req(height: 1080) with { Codec = "h264" };
        var args = YtDlpArgs.Download(req, new AppSettings { PreferH264 = false }, WithFfmpeg);
        Assert.Equal("bv*[vcodec~='^(avc|h264)']+ba[ext=m4a]/bv*[vcodec~='^(avc|h264)']+ba/bv*+ba[ext=m4a]/bv*+ba/b",
            args[args.IndexOf("-f") + 1]);

        // An explicit codec overrides the global compatibility mode's codec preference.
        var hevc = YtDlpArgs.Download(Req() with { Codec = "h265" }, new AppSettings { PreferH264 = true }, WithFfmpeg);
        Assert.Equal("res,fps,vbr,tbr", hevc[hevc.IndexOf("-S") + 1]);

        Assert.Equal("b[vcodec^=av01]/b", YtDlpArgs.Download(Req() with { Codec = "av1" }, new AppSettings(), WithFfmpeg with { FfmpegDir = null })
            .SkipWhile(a => a != "-f").Skip(1).First());
    }

    [Theory]
    [InlineData("avc1.640032", "h264")]
    [InlineData("hev1.1.6.L120.90", "h265")]
    [InlineData("hvc1.2.4.L153", "h265")]
    [InlineData("av01.0.08M.08", "av1")]
    [InlineData("vp09.00.40.08", "vp9")]
    [InlineData("vp9", "vp9")]
    [InlineData("none", null)]
    public void MapsCodecFamilies(string vcodec, string? expected) => Assert.Equal(expected, VideoCodecs.Family(vcodec));

    [Fact]
    public void SpeedLimitIsPassed()
    {
        var args = YtDlpArgs.Download(Req(), new AppSettings { SpeedLimitKBps = 2048 }, WithFfmpeg);
        Assert.Equal("2048K", args[args.IndexOf("-r") + 1]);
    }

    [Fact]
    public void BilibiliSubtitlesIncludeDanmakuAndAreNotConverted()
    {
        var s = new AppSettings { SaveDanmaku = true, SubtitleLanguages = "zh-Hans" };
        var args = YtDlpArgs.Download(Req("https://www.bilibili.com/video/BV1", subs: true), s, WithFfmpeg);
        Assert.Equal("zh-Hans,danmaku", args[args.IndexOf("--sub-langs") + 1]);
        Assert.DoesNotContain("--convert-subs", args);

        var yt = YtDlpArgs.Download(Req(subs: true), s, WithFfmpeg);
        Assert.Equal("zh-Hans", yt[yt.IndexOf("--sub-langs") + 1]);
        Assert.Contains("--convert-subs", yt);
    }

    [Fact]
    public void CookiePriorityBrowserThenLogin()
    {
        var s = new AppSettings { CookiesBrowser = "edge" };
        var env = new YtDlpArgs.Environment(null, @"C:\login.txt", false);
        var args = YtDlpArgs.Common(s, env);
        Assert.Equal("utf-8", args[args.IndexOf("--encoding") + 1]);
        Assert.Equal("edge", args[args.IndexOf("--cookies-from-browser") + 1]);

        var fallback = YtDlpArgs.Common(s, env with { SkipBrowserCookies = true });
        Assert.DoesNotContain("--cookies-from-browser", fallback);
        Assert.Equal(@"C:\login.txt", fallback[fallback.IndexOf("--cookies") + 1]);

        // YouTube tries anonymously first: no cookies at all.
        var anonymous = YtDlpArgs.Common(s, env with { SkipBrowserCookies = true, SkipLoginCookies = true });
        Assert.DoesNotContain("--cookies", anonymous);
    }

    [Fact]
    public void InfoUsesFlatPlaylistAndNoPlaylistForWatchLinks()
    {
        var args = YtDlpArgs.Info("https://www.youtube.com/watch?v=a&list=PL", new AppSettings(), WithFfmpeg);
        Assert.Contains("--flat-playlist", args);
        Assert.Contains("--no-playlist", args);
        Assert.DoesNotContain("--no-playlist", YtDlpArgs.Info("https://www.bilibili.com/video/BV1", new AppSettings(), WithFfmpeg));
    }
}

[Collection("DataDir")]
public class FormatAndStoreTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    public void FormatsBytes(double bytes, string expected) =>
        Assert.Equal(expected, Format.Bytes(bytes).Replace(',', '.'));

    [Fact]
    public void FormatsDurationAndDate()
    {
        Assert.Equal("3:32", Format.Duration(212));
        Assert.Equal("1:00:05", Format.Duration(3605));
        Assert.Equal("2024-01-31", Format.Date("20240131"));
        Assert.Null(Format.Date("2024"));
    }

    [Fact]
    public void NetscapeCookiesMarkHttpOnlyAndDedupe()
    {
        var text = AccountStore.ToNetscape([
            new CookieRecord(".bilibili.com", "/", "SESSDATA", "v", true, true, 1900000000),
            new CookieRecord(".bilibili.com", "/", "SESSDATA", "dup", true, true, 1900000000),
        ]);
        var lines = text.Split('\n').Where(l => l.Contains("SESSDATA")).ToList();
        Assert.Single(lines);
        Assert.StartsWith("#HttpOnly_.bilibili.com\tTRUE\t/\tTRUE\t1900000000\tSESSDATA\tv", lines[0]);
    }

    [Fact]
    public void DownloadListRoundTripsAndResumesRunningItems()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidflow-test-" + Guid.NewGuid());
        var old = AppSettings.DataDir;
        AppSettings.DataDir = dir;
        try
        {
            DownloadStore.Save([
                new DownloadRecord { Request = new("u", "video", 720, false, "o"), Title = "a", State = DownloadState.Downloading },
                new DownloadRecord { Request = new("u2", "mp3", 0, false, "o"), Title = "b", State = DownloadState.Completed },
            ]);
            var loaded = DownloadStore.Load();
            Assert.Equal(DownloadState.Queued, loaded[0].State);
            Assert.Equal(720, loaded[0].Request.MaxHeight);
            Assert.Equal(DownloadState.Completed, loaded[1].State);
        }
        finally
        {
            AppSettings.DataDir = old;
            Directory.Delete(dir, true);
        }
    }
}

[Collection("DataDir")]
public class SettingsMigrationTests
{
    [Fact]
    public void SettingsFromVersion1KeepChinese()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidflow-test-" + Guid.NewGuid());
        var old = AppSettings.DataDir;
        AppSettings.DataDir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "Theme": "Dark", "MaxConcurrent": 3 }""");
            AppSettings.Load();
            Assert.Equal("zh", AppSettings.Current.Language);
            Assert.Equal(3, AppSettings.Current.MaxConcurrent);

            AppSettings.Current.Language = "";
            AppSettings.Save();
            AppSettings.Load();
            Assert.Equal("", AppSettings.Current.Language); // an explicit "follow Windows" is kept
        }
        finally
        {
            AppSettings.DataDir = old;
            Directory.Delete(dir, true);
        }
    }
}

public class LocalizationTests
{
    static string ResxPath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "VidFlow.Core"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "VidFlow.Core", "Localization", name);
    }

    static HashSet<string> Keys(string file) =>
        XDocument.Load(ResxPath(file)).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();

    [Fact]
    public void ChineseAndEnglishHaveTheSameKeys()
    {
        var en = Keys("Strings.resx");
        var zh = Keys("Strings.zh.resx");
        Assert.Empty(en.Except(zh));
        Assert.Empty(zh.Except(en));
    }

    [Fact]
    public void EveryKeyUsedInSourceExists()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(ResxPath("x")))!;
        root = Path.GetDirectoryName(root)!;
        var keys = Keys("Strings.resx");
        var used = new HashSet<string>();
        var pattern = new System.Text.RegularExpressions.Regex(@"L\.(?:Get|Format)\(\s*[""']([A-Za-z0-9_]+)[""']");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("VidFlow.Tests")))
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(keys));
    }

    [Fact]
    public void DownloadLabelsFollowCurrentLanguage()
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            var req = new DownloadRequest("https://www.bilibili.com/video/BV1", "mp3", 0, false, "o");
            L.ApplyLanguage("en");
            Assert.Equal("Bilibili  ·  MP3 audio  ·  P3", DownloadLabels.Label(req, 3));
            L.ApplyLanguage("zh");
            Assert.Equal("哔哩哔哩  ·  MP3 音频", DownloadLabels.Label(req, null));
            var done = new DownloadRecord { Request = req, Title = "t", State = DownloadState.Completed, FilePath = @"C:\a\x.mp3" };
            Assert.Equal("已完成 · x.mp3", DownloadLabels.RestingStatus(done));
            Assert.Null(DownloadLabels.RestingStatus(new DownloadRecord { Request = req, Title = "t", State = DownloadState.Failed }));
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Fact]
    public void SwitchesLanguage()
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            L.ApplyLanguage("en");
            Assert.Equal("Bilibili", L.Get("Platform_Bilibili"));
            L.ApplyLanguage("zh");
            Assert.Equal("哔哩哔哩", L.Get("Platform_Bilibili"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }
}
