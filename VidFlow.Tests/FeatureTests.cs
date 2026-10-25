using System.Security.Cryptography;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Tests;

public class FormatSelectionTests
{
    static readonly YtDlpArgs.Environment Env = new(@"C:\ff", null, false);
    static string F(List<string> args) => args[args.IndexOf("-f") + 1];
    static string Arg(List<string> args, string name) => args[args.IndexOf(name) + 1];

    [Fact]
    public void WebmRestrictsToVp9Av1AndOpusAndFallsBackToMkv()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 0, false, "o", Container: "webm");
        var args = YtDlpArgs.Download(req, new AppSettings(), Env);
        Assert.StartsWith("bv*[ext!=jpg][ext!=png][ext!=webp][vcodec~='^(vp0?9|av01)']+ba[acodec~='^(opus|vorbis)']", F(args));

        Assert.EndsWith("/bv*[ext!=jpg][ext!=png][ext!=webp]+ba/b[ext!=jpg][ext!=png][ext!=webp]/b", F(args));
        Assert.Equal("webm/mkv", Arg(args, "--merge-output-format"));
    }

    [Fact]
    public void MkvTakesAnyAudioAndRecodeIsOptIn()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 0, false, "o", Container: "mkv");
        var args = YtDlpArgs.Download(req, new AppSettings(), Env);
        Assert.Equal("bv*[ext!=jpg][ext!=png][ext!=webp]+ba/b[ext!=jpg][ext!=png][ext!=webp]/b", F(args));
        Assert.Equal("mkv", Arg(args, "--merge-output-format"));
        Assert.DoesNotContain("--recode-video", args);

        var recode = YtDlpArgs.Download(req with { Container = "webm", Codec = "h264", Recode = true }, new AppSettings(), Env);
        Assert.Equal("webm", Arg(recode, "--recode-video"));
    }

    [Fact]
    public void AudioTrackLanguageIsPreferredThenRelaxed()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 0, false, "o", AudioLanguage: "ja");
        var chain = F(YtDlpArgs.Download(req, new AppSettings(), Env)).Split('/');
        Assert.Equal("bv*[ext!=jpg][ext!=png][ext!=webp]+ba[acodec^=mp4a][language^=ja]", chain[0]);
        Assert.Contains("bv*[ext!=jpg][ext!=png][ext!=webp]+ba[language^=ja]", chain);
        Assert.Equal("b", chain[^1]);
    }

    [Fact]
    public void AudioQualityLimitsAudioBitrateInVideoMode()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 720, false, "o", AudioQuality: 128);
        Assert.Equal("res:720,hdr:HDR10,fps,tbr,abr:128", Arg(YtDlpArgs.Download(req, new AppSettings(), Env), "-S"));
    }

    [Theory]
    [InlineData("mp3", 192, "192K", "ba/b")]
    [InlineData("m4a", 0, "0", "ba[acodec^=mp4a]/ba/b")]  // by codec: Bilibili's FLAC is ".m4a" too
    [InlineData("opus", 160, "160K", "ba[acodec=opus]/ba/b")]
    [InlineData("flac", 320, "0", "ba/b")] // lossless ignores a bitrate
    [InlineData("wav", 0, "0", "ba/b")]
    public void AudioOnlyFormats(string format, int quality, string expectedQuality, string expectedFormat)
    {
        var req = new DownloadRequest("https://youtu.be/x", "audio", 0, false, "o", AudioFormat: format, AudioQuality: quality);
        var args = YtDlpArgs.Download(req, new AppSettings(), Env);
        Assert.Equal(format, Arg(args, "--audio-format"));
        Assert.Equal(expectedQuality, Arg(args, "--audio-quality"));
        Assert.Equal(expectedFormat, F(args));
    }

    [Fact]
    public void LegacyAudioModesStillWork()
    {
        var legacy = new DownloadRequest("https://youtu.be/x", "m4a", 0, false, "o");
        Assert.True(legacy.IsAudio);
        Assert.Equal("m4a", Arg(YtDlpArgs.Download(legacy, new AppSettings(), Env), "--audio-format"));
    }

    [Fact]
    public void JsRuntimeIsPassedWhenDenoIsInstalled()
    {
        var args = YtDlpArgs.Common(new AppSettings(), Env with { DenoPath = @"C:\bin\deno.exe" });
        Assert.Equal(@"deno:C:\bin\deno.exe", Arg(args, "--js-runtimes"));
        Assert.DoesNotContain("--js-runtimes", YtDlpArgs.Common(new AppSettings(), Env));
        Assert.Equal("2.5.4", ToolManager.DenoVersion("deno 2.5.4 (stable, release, x86_64-pc-windows-msvc)"));
    }
}

public class NamingTemplateTests
{
    [Fact]
    public void TranslatesVariablesAndFolders()
    {
        var o = NamingTemplate.ToOutputTemplate("{date} {title}", "{platform}/{uploader}", "YouTube");
        Assert.Equal("YouTube/%(uploader,channel,creator|Unknown)s/%(upload_date>%Y-%m-%d|unknown)s %(title).150B.%(ext)s", o);
    }

    [Fact]
    public void DefaultKeepsTheOldFileName()
    {
        Assert.Equal("%(title).150B [%(id)s].%(ext)s", NamingTemplate.ToOutputTemplate(NamingTemplate.DefaultFileName, "", "YouTube"));
        Assert.Equal("%(title).150B [%(id)s].%(ext)s", NamingTemplate.ToOutputTemplate("   ", "", "YouTube"));
    }

    [Fact]
    public void CleansLiteralTextAndEscapesPercent()
    {
        // Slashes in a file name would create folders; forbidden characters and % are made safe.
        Assert.Equal("a_b_c 100%% %(id)s.%(ext)s", NamingTemplate.ToOutputTemplate("a/b:c 100% {id}", "", "YouTube"));
        Assert.Equal("{unknown}.%(ext)s", NamingTemplate.ToOutputTemplate("{unknown}", "", "YouTube"));
    }

    [Fact]
    public void PreviewShowsAnExample()
    {
        Assert.Equal(@"YouTube\Blender\2014-11-10 Big Buck Bunny.mp4", NamingTemplate.Preview("{date} {title}", "{platform}/{uploader}"));
        Assert.Equal("Big Buck Bunny [aqz-KE-bpKQ].mp3", NamingTemplate.Preview(NamingTemplate.DefaultFileName, "", "mp3"));
    }
}

public class CompatibilityTests
{
    [Fact]
    public void FlagsIncompatibleCombinationsWithASuggestion()
    {
        var webmH264 = FormatCompatibility.Check(new DownloadTemplate { Container = "webm", Codec = "h264" });
        Assert.Equal("mkv", webmH264?.SuggestedContainer);
        Assert.Equal("opus", FormatCompatibility.Check(new DownloadTemplate { Container = "webm", AudioFormat = "aac" })?.SuggestedAudioFormat);
        Assert.Equal("aac", FormatCompatibility.Check(new DownloadTemplate { Container = "mp4", AudioFormat = "opus" })?.SuggestedAudioFormat);
        Assert.NotNull(FormatCompatibility.Check(new DownloadTemplate { Mode = "audio", AudioFormat = "flac" }));
        Assert.Null(FormatCompatibility.Check(new DownloadTemplate { Container = "mkv", Codec = "h265", AudioFormat = "opus" }));
        Assert.Null(FormatCompatibility.Check(new DownloadTemplate()));
    }
}

[Collection("DataDir")]
public class TemplateStoreTests
{
    [Fact]
    public void CreateEditDuplicateRenameDeleteAndDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidflow-test-" + Guid.NewGuid());
        var old = AppSettings.DataDir;
        AppSettings.DataDir = dir;
        TemplateStore.ResetCache();
        try
        {
            Assert.Equal(TemplateStore.BuiltIns[0].Id, TemplateStore.Default.Id); // nothing chosen yet

            var mine = TemplateStore.Duplicate(TemplateStore.BuiltIns[2], "Mine");
            Assert.False(mine.IsBuiltIn);
            Assert.Equal("h264", mine.Codec);

            mine.Container = "mkv";
            TemplateStore.Save(mine);
            TemplateStore.Rename(mine, "Renamed");
            TemplateStore.SetDefault(mine);

            TemplateStore.ResetCache(); // reload from disk
            var loaded = TemplateStore.Find(mine.Id)!;
            Assert.Equal("Renamed", loaded.DisplayName);
            Assert.Equal("mkv", loaded.Container);
            Assert.Equal(mine.Id, TemplateStore.Default.Id);

            Assert.Equal("Renamed (2)", TemplateStore.UniqueName("Renamed"));
            Assert.Throws<InvalidOperationException>(() => TemplateStore.Save(TemplateStore.BuiltIns[0]));

            TemplateStore.Delete(loaded);
            Assert.Null(TemplateStore.Find(mine.Id));
            Assert.Equal(TemplateStore.BuiltIns[0].Id, TemplateStore.Default.Id); // default falls back
        }
        finally
        {
            AppSettings.DataDir = old;
            TemplateStore.ResetCache();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TemplateCopyIsIndependentForPerTaskOverrides()
    {
        var t = TemplateStore.BuiltIns[1];
        var copy = t.Clone();
        copy.MaxHeight = 720;
        Assert.Equal(1080, t.MaxHeight); // the preset is untouched
        Assert.False(copy.SameOptionsAs(t));
        var req = copy.ToRequest("https://youtu.be/x", @"C:\out");
        Assert.Equal(720, req.MaxHeight);
        Assert.Equal("video", req.Mode);
    }
}

public class AudioLanguageTests
{
    [Fact]
    public void ListsTracksOriginalFirstOnlyWhenThereIsAChoice()
    {
        const string multi = """
        {"title":"t","webpage_url":"https://www.youtube.com/watch?v=x","formats":[
          {"format_id":"a1","vcodec":"none","acodec":"opus","language":"es","language_preference":-1},
          {"format_id":"a2","vcodec":"none","acodec":"opus","language":"en","language_preference":10},
          {"format_id":"a3","vcodec":"none","acodec":"mp4a","language":"ja","language_preference":-1},
          {"format_id":"v","vcodec":"avc1","acodec":"none","height":720,"language":"en"}]}
        """;
        var v = Assert.IsType<VideoInfo>(InfoParser.Parse(multi, "https://youtu.be/x"));
        Assert.Equal(["en", "es", "ja"], v.AudioLanguages);

        const string single = """{"title":"t","formats":[{"vcodec":"none","acodec":"opus","language":"en"}]}""";
        Assert.Empty(Assert.IsType<VideoInfo>(InfoParser.Parse(single, "https://youtu.be/x")).AudioLanguages);
    }
}

[Collection("DataDir")]
public class UpdateServiceTests
{
    [Fact]
    public async Task VerifiesSizeAndChecksum()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
            var good = new UpdateManifest("9.9.9", "u", hash, 4, "", DateTime.Now);
            Assert.True(await UpdateService.VerifyAsync(file, good));
            Assert.False(await UpdateService.VerifyAsync(file, good with { Size = 5 }));
            Assert.False(await UpdateService.VerifyAsync(file, good with { Sha256 = new string('0', 64) }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ComparesVersions()
    {
        var m = new UpdateManifest("1.2.0", "u", "", 0, "", DateTime.Now);
        Assert.True(UpdateService.IsNewer(m, new Version(1, 1, 1)));
        Assert.False(UpdateService.IsNewer(m, new Version(1, 2, 0)));
        Assert.False(UpdateService.IsNewer(m with { Version = "garbage" }, new Version(1, 0)));
    }

    [Fact]
    public async Task DownloadsFromALocalManifestAndDetectsFailedInstalls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidflow-test-" + Guid.NewGuid());
        var old = AppSettings.DataDir;
        AppSettings.DataDir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "setup.exe");
            await File.WriteAllBytesAsync(source, [9, 8, 7]);
            var m = new UpdateManifest("2.0.0", source, Convert.ToHexString(SHA256.HashData(new byte[] { 9, 8, 7 })), 3, "notes", DateTime.Now);

            var installer = await UpdateService.DownloadAsync(m, null, CancellationToken.None);
            Assert.True(await UpdateService.VerifyAsync(installer, m));

            // A corrupt package is rejected after the retries.
            await Assert.ThrowsAnyAsync<Exception>(() => UpdateService.DownloadAsync(m with { Version = "2.0.1", Sha256 = new string('0', 64) }, null, CancellationToken.None));

            // Pending marker: still on the old version at next start → the install failed (rolled back).
            Directory.CreateDirectory(Path.Combine(dir, "updates"));
            File.WriteAllText(Path.Combine(dir, "updates", "pending.json"), """{"Version":"2.0.0","StartedAt":"2026-10-25T00:00:00"}""");
            Assert.Equal((UpdateOutcome.Failed, "2.0.0"), UpdateService.CheckPendingResult(new Version(1, 9, 0)));
            Assert.Equal((UpdateOutcome.None, (string?)null), UpdateService.CheckPendingResult(new Version(1, 9, 0))); // reported once

            File.WriteAllText(Path.Combine(dir, "updates", "pending.json"), """{"Version":"2.0.0","StartedAt":"2026-10-25T00:00:00"}""");
            Assert.Equal((UpdateOutcome.Succeeded, "2.0.0"), UpdateService.CheckPendingResult(new Version(2, 0, 0)));
        }
        finally
        {
            AppSettings.DataDir = old;
            Directory.Delete(dir, true);
        }
    }
}

public class SubtitleLanguageTests
{
    [Fact]
    public void OldDefaultSettingReadsAsTicks()
    {
        var (chosen, other) = SubtitleLanguageSet.Split("zh-Hans,zh-Hant,zh-CN,zh-TW,zh,ai-zh,en");
        Assert.Equal(["en", "zh-Hans", "zh-Hant"], chosen.Order());
        Assert.Equal("", other);
    }

    [Fact]
    public void RegionalCodesBelongToTheirLanguageAndUnknownCodesAreKept()
    {
        var (chosen, other) = SubtitleLanguageSet.Split("en-US, ja , fr，de;zh-HK");
        Assert.Equal(["en", "ja", "zh-Hant"], chosen.Order());
        Assert.Equal("fr, de", other);
    }

    [Fact]
    public void JoinWritesEveryCodeOfTickedLanguagesWithoutDuplicates()
    {
        Assert.Equal("zh-Hans,zh-CN,zh,ai-zh,en.*,fr", SubtitleLanguageSet.Join(["en", "zh-Hans"], "fr, en.*"));
        Assert.Equal("", SubtitleLanguageSet.Join([], " "));
        Assert.Equal("简体中文, English, fr", SubtitleLanguageSet.Describe("zh,en,fr"));
    }

    [Fact]
    public void EmptySettingFallsBackToChineseAndEnglish()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 0, true, "o");
        var args = YtDlpArgs.Download(req, new AppSettings { SubtitleLanguages = "" }, new YtDlpArgs.Environment(@"C:\ff", null, false));
        Assert.Equal(SubtitleLanguageSet.Fallback, args[args.IndexOf("--sub-langs") + 1]);
    }
}

public class NamingPresetTests
{
    [Fact]
    public void PresetsUseOnlyKnownVariables()
    {
        Assert.Contains(NamingTemplate.DefaultFileName, NamingTemplate.FilePresets);
        Assert.All(NamingTemplate.FilePresets, p => Assert.Empty(NamingTemplate.UnknownVariables(p)));
    }

    [Fact]
    public void UnknownVariablesAreReported()
    {
        Assert.Equal(["author", "views"], NamingTemplate.UnknownVariables("{author} {title} {views} {author}"));
    }
}

public class BilibiliQrLoginTests
{
    [Theory]
    [InlineData(86101, QrLoginState.Waiting)]
    [InlineData(86090, QrLoginState.Scanned)]
    [InlineData(86038, QrLoginState.Expired)]
    [InlineData(0, QrLoginState.Success)]
    public void PollCodesMapToStates(int code, QrLoginState expected)
    {
        var json = """{"code":0,"message":"OK","data":{"url":"","refresh_token":"","timestamp":0,"code":""" + code + ""","message":""}}""";
        Assert.Equal(expected, BilibiliQrLogin.ParsePoll(json).State);
    }

    [Fact]
    public void SuccessUrlBecomesLoginCookies()
    {
        var url = "https://passport.biligame.com/x/passport-login/web/crossDomain?DedeUserID=123&DedeUserID__ckMd5=abc"
                  + "&Expires=1893456000&SESSDATA=s%2C1893456000%2Cxyz&bili_jct=jct&gourl=https%3A%2F%2Fwww.bilibili.com";
        var cookies = BilibiliQrLogin.CookiesFromUrl(url);
        Assert.Equal(["DedeUserID", "DedeUserID__ckMd5", "SESSDATA", "bili_jct"], cookies.Select(c => c.Name));
        var sess = cookies.Single(c => c.Name == "SESSDATA");
        Assert.Equal("s,1893456000,xyz", sess.Value);
        Assert.Equal(".bilibili.com", sess.Domain);
        Assert.True(sess.HttpOnly);
        Assert.Equal(1893456000, sess.Expires);

        // Written out, it counts as signed in to Bilibili.
        var line = AccountStore.ToNetscape(cookies).Split('\n').Single(l => l.Contains("SESSDATA"));
        Assert.StartsWith("#HttpOnly_.bilibili.com\tTRUE\t/\t", line);
    }

    [Fact]
    public void NonLoginUrlGivesNoCookies() => Assert.Empty(BilibiliQrLogin.CookiesFromUrl(""));
}

[Collection("DataDir")]
public class AccountStoreChangeTests
{
    [Fact]
    public async Task SavingCookiesNotifiesListeners()
    {
        var old = AppSettings.DataDir;
        var dir = Path.Combine(Path.GetTempPath(), "vidflow-acct-" + Guid.NewGuid().ToString("N"));
        AppSettings.DataDir = dir;
        var raised = 0;
        void OnChanged() => raised++;
        AccountStore.Changed += OnChanged;
        try
        {
            await AccountStore.WriteAsync(BilibiliQrLogin.CookiesFromUrl(
                "https://passport.biligame.com/x/passport-login/web/crossDomain?DedeUserID=1&SESSDATA=s&bili_jct=j&Expires=1893456000"));
            Assert.Equal(1, raised);
            Assert.True(AccountStore.IsLoggedIn(Platform.Bilibili));
        }
        finally
        {
            AccountStore.Changed -= OnChanged;
            AppSettings.DataDir = old;
            Directory.Delete(dir, true);
        }
    }
}

public class DouyinLinkTests
{
    [Theory]
    [InlineData("https://www.douyin.com/video/7412345678901234567", "https://www.douyin.com/video/7412345678901234567")]
    [InlineData("https://www.douyin.com/jingxuan?modal_id=7412345678901234567", "https://www.douyin.com/video/7412345678901234567")]
    [InlineData("https://www.douyin.com/user/MS4wLjAB?from_tab_name=main&modal_id=7412345678901234567", "https://www.douyin.com/video/7412345678901234567")]
    [InlineData("https://www.iesdouyin.com/share/video/7412345678901234567/?region=CN", "https://www.douyin.com/video/7412345678901234567")]
    public void LinksBecomeVideoLinks(string url, string expected) => Assert.Equal(expected, UrlTools.CanonicalDouyinUrl(url));

    [Fact]
    public void ShareTextYieldsTheShortLinkAndIsRecognised()
    {
        var share = "7.94 复制打开抖音，看看【某某的作品】今天的晚霞 # 风景 https://v.douyin.com/iABCdEf/ 12/31 Abc:/";
        var url = UrlTools.ExtractUrl(share);
        Assert.Equal("https://v.douyin.com/iABCdEf/", url);
        Assert.Equal(Platform.Douyin, UrlTools.DetectPlatform(url!));
        Assert.True(UrlTools.IsShortLink(url!));
        Assert.Null(UrlTools.CanonicalDouyinUrl(url!)); // resolved over the network by ShortLinks
    }

    [Fact]
    public void ProfilePagesAreNotVideos() => Assert.Null(UrlTools.CanonicalDouyinUrl("https://www.douyin.com/user/MS4wLjAB"));

    /// <summary>Shape of Douyin's web aweme/detail answer (trimmed to the fields VidFlow reads).</summary>
    const string Detail = """
        {"status_code":0,"aweme_detail":{
          "aweme_id":"7412345678901234567","desc":"晚霞 #风景\n第二行","create_time":1730000000,
          "author":{"nickname":"某某"},
          "images":null,
          "video":{"duration":15200,
            "cover":{"url_list":["https://p3.douyinpic.com/cover.jpeg"]},
            "play_addr":{"url_list":["https://www.douyin.com/aweme/v1/play/?video_id=v0"],"width":720,"height":1280},
            "bit_rate":[
              {"bit_rate":1200000,"is_h265":0,"play_addr":{"url_list":["https://www.douyin.com/aweme/v1/play/?x=1","https://v26-web.douyinvod.com/720.mp4"],"width":720,"height":1280,"data_size":2000000}},
              {"bit_rate":2600000,"is_h265":1,"play_addr":{"url_list":["https://v26-web.douyinvod.com/1080-h265.mp4"],"width":1080,"height":1920,"data_size":5000000}},
              {"bit_rate":3100000,"is_h265":0,"play_addr":{"url_list":["https://v26-web.douyinvod.com/1080-h264.mp4"],"width":1080,"height":1920,"data_size":6000000}}
            ]}}}
        """;

    [Fact]
    public void DetailBecomesVideoInfo()
    {
        var video = DouyinParser.Parse(Detail);
        Assert.Equal("7412345678901234567", video.Id);
        Assert.Equal("某某", video.Author);
        Assert.Equal(15.2, video.Duration);
        Assert.Equal("https://v26-web.douyinvod.com/720.mp4", video.Streams.Single(s => s.Resolution == 720).Url); // CDN over redirect

        var info = DouyinParser.ToVideoInfo(video);
        Assert.Equal("https://www.douyin.com/video/7412345678901234567", info.Url);
        Assert.Equal(Platform.Douyin, info.Platform);
        Assert.Equal([1080, 720], info.Qualities.Select(q => q.Height)); // portrait: the short side
        Assert.Equal(6000000, info.Qualities[0].EstimatedBytes);
        Assert.Equal(1080, info.Codecs["h265"]);
    }

    [Theory]
    [InlineData(0, "auto", "1080-h264.mp4")]     // best: highest resolution, then bitrate
    [InlineData(0, "h265", "1080-h265.mp4")]
    [InlineData(720, "auto", "720.mp4")]
    [InlineData(480, "auto", "720.mp4")]          // nothing that small: the smallest there is
    [InlineData(720, "h265", "720.mp4")]          // codec not offered at that size: fall back
    public void PicksTheStreamThatFits(int maxHeight, string codec, string expected) =>
        Assert.EndsWith(expected, DouyinParser.Pick(DouyinParser.Parse(Detail), maxHeight, codec).Url);

    [Fact]
    public void PhotoPostsAndMissingVideosAreRefused()
    {
        Assert.Throws<YtDlpException>(() => DouyinParser.Parse("""{"status_code":0,"aweme_detail":null}"""));
        Assert.Throws<YtDlpException>(() => DouyinParser.Parse(
            """{"aweme_detail":{"aweme_id":"1","desc":"","images":[{"url_list":["a"]}],"video":{}}}"""));
    }

    [Fact]
    public void KnownValuesNameTheFile()
    {
        var known = new Dictionary<string, string?> { ["title"] = "晚霞 #风景\n第二行", ["uploader"] = "某某", ["id"] = "7412" };
        var output = NamingTemplate.ToOutputTemplate("{uploader} - {title} [{id}]", "{platform}", "抖音", known);
        Assert.Equal("抖音/某某 - 晚霞 #风景 第二行 [7412].%(ext)s", output);
    }
}

public class PlatformLinkTests
{
    [Theory]
    [InlineData("https://weibo.com/tv/show/1034:5356122991951924", Platform.Weibo)]
    [InlineData("https://m.weibo.cn/status/5123456789012345", Platform.Weibo)]
    [InlineData("http://t.cn/AXlFHACG", Platform.Weibo)]
    [InlineData("https://www.xiaohongshu.com/explore/6411cf99000000001300b6d9?xsec_token=AB", Platform.Xiaohongshu)]
    [InlineData("http://xhslink.com/a/AbCdEf", Platform.Xiaohongshu)]
    [InlineData("https://x.com/NASA/status/2113776971603689874", Platform.X)]
    [InlineData("https://twitter.com/NASA/status/2113776971603689874", Platform.X)]
    [InlineData("https://mobile.twitter.com/NASA/status/2113776971603689874", Platform.X)]
    [InlineData("https://t.co/abc123", Platform.X)]
    [InlineData("https://www.iesdouyin.com/share/video/7412345678901234567/", Platform.Douyin)]
    [InlineData("https://www.netflix.com/title/1", Platform.Other)]   // not "x.com"
    [InlineData("https://notweibo.com.example.org/", Platform.Other)]
    [InlineData("not a link", Platform.Other)]
    public void PlatformComesFromTheHost(string url, Platform expected) => Assert.Equal(expected, UrlTools.DetectPlatform(url));

    [Theory]
    [InlineData("https://video.weibo.com/show?fid=1034:5356122991951924", "https://weibo.com/tv/show/1034:5356122991951924")]
    [InlineData("https://weibo.com/tv/show/1034:5356122991951924?from=old_pc_videoshow", "https://weibo.com/tv/show/1034:5356122991951924")]
    [InlineData("https://video.weibo.com/show?fid=1034%3A5356122991951924", "https://weibo.com/tv/show/1034:5356122991951924")]
    public void WeiboVideoLinksBecomeTvLinks(string url, string expected) => Assert.Equal(expected, UrlTools.CanonicalWeiboUrl(url));

    [Fact]
    public void WeiboPostLinksAreLeftForYtDlp() => Assert.Null(UrlTools.Canonical("https://m.weibo.cn/status/5123456789012345"));

    [Theory]
    [InlineData("http://t.cn/AXlFHACG", true)]
    [InlineData("http://xhslink.com/a/AbCdEf", true)]
    [InlineData("https://v.douyin.com/iABCdEf/", true)]
    [InlineData("https://t.co/abc123", false)]          // yt-dlp follows these itself
    [InlineData("https://weibo.com/tv/show/1034:1", false)]
    public void ShortLinksAreRecognised(string url, bool expected) => Assert.Equal(expected, UrlTools.IsShortLink(url));

    [Fact]
    public void OnlySomePlatformsHaveSignIn()
    {
        Assert.True(AccountStore.HasSignIn(Platform.Bilibili));
        Assert.False(AccountStore.HasSignIn(Platform.Weibo));
        Assert.False(AccountStore.IsLoggedIn(Platform.X));
    }
}

public class ImageFormatTests
{
    [Fact]
    public void PreviewImagesAreNotQualitiesAndPortraitUsesTheShortSide()
    {
        // Weibo lists a 180x320 JPEG "scrubber" among its formats; its videos are portrait.
        const string json = """
            {"_type":"video","id":"5356127206245063","title":"t","webpage_url":"https://weibo.com/tv/show/1034:1","duration":132,
             "formats":[
               {"format_id":"scrubber_hd","ext":"jpg","width":180,"height":320,"vcodec":"unknown","acodec":"unknown"},
               {"format_id":"mp4_hd","ext":"mp4","width":540,"height":820,"vcodec":"avc1.64001f","acodec":"mp4a.40.5","filesize":16230000},
               {"format_id":"mp4_1080p","ext":"mp4","width":1080,"height":1644,"vcodec":"avc1.640032","acodec":"mp4a.40.5","filesize":43350000}]}
            """;
        var info = Assert.IsType<VideoInfo>(InfoParser.Parse(json, "https://weibo.com/tv/show/1034:1"));
        Assert.Equal([1080, 540], info.Qualities.Select(q => q.Height));
        Assert.Equal(1080, info.Codecs["h264"]);
    }

    [Fact]
    public void FormatSelectionNeverPicksAnImage()
    {
        var req = new DownloadRequest("https://weibo.com/tv/show/1034:1", "video", 320, false, "o");
        var args = YtDlpArgs.Download(req, new AppSettings(), new YtDlpArgs.Environment(@"C:\ff", null, false));
        var chain = args[args.IndexOf("-f") + 1].Split('/');
        Assert.All(chain[..^1], s => Assert.Contains("[ext!=jpg]", s));
        Assert.Equal("res:320,hdr:HDR10,fps,tbr", args[args.IndexOf("-S") + 1]);
    }
}

public class MediaFeatureTests
{
    static readonly YtDlpArgs.Environment Env = new(@"C:\ff", null, false);
    static string Arg(List<string> args, string name) => args[args.IndexOf(name) + 1];

    /// <summary>Trimmed from yt-dlp's answer for a Bilibili video with Dolby Vision, Dolby Atmos and Hi-Res.</summary>
    const string Bilibili = """
        {"_type":"video","id":"BV18bnN6nEC6","title":"t","webpage_url":"https://www.bilibili.com/video/BV18bnN6nEC6","duration":200,
         "formats":[
           {"format_id":"30250","ext":"m4a","vcodec":"none","acodec":"ec-3","tbr":1665},
           {"format_id":"30280","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","tbr":320},
           {"format_id":"30251","ext":"m4a","vcodec":"none","acodec":"flac","tbr":1690},
           {"format_id":"30120","ext":"mp4","width":3840,"height":2160,"vcodec":"avc1.640034","acodec":"none","dynamic_range":"SDR","tbr":21240},
           {"format_id":"30126","ext":"mp4","width":3840,"height":2160,"vcodec":"hvc1.2.4.L156.90","acodec":"none","dynamic_range":"DV","tbr":21003}]}
        """;

    [Fact]
    public void FeaturesAreDetected()
    {
        var info = Assert.IsType<VideoInfo>(InfoParser.Parse(Bilibili, "https://www.bilibili.com/video/BV18bnN6nEC6"));
        Assert.Equal(["DV"], info.HdrKinds);
        Assert.True(info.HasDolbyVision);
        Assert.False(info.HasHdr);
        Assert.True(info.HasHiRes);
        Assert.True(info.HasAtmos);
        Assert.Equal(["DV", MediaFeatures.HiRes, MediaFeatures.Atmos], info.Features);
    }

    [Theory]
    [InlineData(MediaFeatures.Auto, false, "res,hdr:HDR10,fps,tbr")]   // HDR10 / HLG yes, Dolby Vision only on request
    [InlineData(MediaFeatures.DolbyVision, false, "res,hdr,fps,tbr")]
    [InlineData(MediaFeatures.Sdr, false, "res,hdr:SDR,fps,tbr")]
    [InlineData(MediaFeatures.Auto, true, "res,hdr:SDR,vcodec:h264,acodec:m4a,fps,tbr")]  // compatibility mode
    public void PictureSteersTheSort(string picture, bool compatibility, string expected)
    {
        var req = new DownloadRequest("https://www.bilibili.com/video/BV1", "video", 0, false, "o", DynamicRange: picture);
        Assert.Equal(expected, Arg(YtDlpArgs.Download(req, new AppSettings { PreferH264 = compatibility }, Env), "-S"));
    }

    [Fact]
    public void DolbyVisionAndHiResGoToMkvWithoutReencoding()
    {
        var dv = new DownloadRequest("https://www.bilibili.com/video/BV1", "video", 0, false, "o", Recode: true, DynamicRange: MediaFeatures.DolbyVision);
        var args = YtDlpArgs.Download(dv, new AppSettings(), Env);
        Assert.Equal("mkv", Arg(args, "--merge-output-format"));
        Assert.DoesNotContain("--recode-video", args);

        var hiRes = new DownloadRequest("https://www.bilibili.com/video/BV1", "video", 0, false, "o", AudioFormat: MediaFeatures.HiRes);
        var hiResArgs = YtDlpArgs.Download(hiRes, new AppSettings(), Env);
        Assert.Equal("mkv", Arg(hiResArgs, "--merge-output-format"));
        Assert.Contains("+ba[acodec~='^(flac|alac)']", Arg(hiResArgs, "-f"));

        var atmos = new DownloadRequest("https://www.bilibili.com/video/BV1", "video", 0, false, "o", AudioFormat: MediaFeatures.Atmos);
        Assert.Contains("+ba[acodec~='^(ec-?3|eac3|ac-?4)']", Arg(YtDlpArgs.Download(atmos, new AppSettings(), Env), "-f"));
    }

    [Fact]
    public void Mp4AutoAudioIsAacByCodecNotExtension()
    {
        // Bilibili labels its FLAC track ".m4a": an extension filter would let it into MP4.
        var req = new DownloadRequest("https://www.bilibili.com/video/BV1", "video", 0, false, "o");
        Assert.StartsWith("bv*[ext!=jpg][ext!=png][ext!=webp]+ba[acodec^=mp4a]", Arg(YtDlpArgs.Download(req, new AppSettings(), Env), "-f"));
    }

    [Fact]
    public void OriginalAudioKeepsTheSource()
    {
        var req = new DownloadRequest("https://www.bilibili.com/video/BV1", "audio", 0, false, "o", AudioFormat: MediaFeatures.Original);
        var args = YtDlpArgs.Download(req, new AppSettings(), Env);
        Assert.Equal("best", Arg(args, "--audio-format"));
        Assert.Equal("0", Arg(args, "--audio-quality"));
    }

    [Fact]
    public void AdviceFollowsWhatTheVideoOffers()
    {
        var info = Assert.IsType<VideoInfo>(InfoParser.Parse(Bilibili, "https://www.bilibili.com/video/BV18bnN6nEC6"));

        var mp3 = new DownloadTemplate { Mode = "audio", AudioFormat = "mp3" };
        Assert.Equal("flac", FormatCompatibility.Check(mp3, info)!.SuggestedAudioFormat);
        Assert.Null(FormatCompatibility.Check(mp3));                                    // no Hi-Res known: nothing to say
        Assert.Null(FormatCompatibility.Check(new DownloadTemplate { Mode = "audio", AudioFormat = "flac" }, info));

        var dv = FormatCompatibility.Check(new DownloadTemplate { DynamicRange = MediaFeatures.DolbyVision }, info)!;
        Assert.True(dv.Info);
        Assert.Equal("mkv", dv.SuggestedContainer);
        Assert.Null(FormatCompatibility.Check(new DownloadTemplate { DynamicRange = MediaFeatures.DolbyVision, Container = "mkv" }, info));
    }

    [Fact]
    public void LabelsMentionTheChoices()
    {
        var t = new DownloadTemplate { DynamicRange = MediaFeatures.DolbyVision, AudioFormat = MediaFeatures.HiRes };
        var summary = DownloadLabels.Summary(t);
        Assert.Contains(MediaFeatures.Label("DV"), summary);
        Assert.Contains(MediaFeatures.Label(MediaFeatures.HiRes), summary);
        Assert.StartsWith("MKV", DownloadLabels.Label(t.ToRequest("https://www.bilibili.com/video/BV1", "o"), null).Split("·")[1].Trim());
    }
}

public class MultiConnectionTests
{
    static readonly DownloadRequest Req = new("https://www.youtube.com/watch?v=abc", "video", 1080, false, "o");

    [Fact]
    public void Aria2DownloadsWholeFilesAndYtDlpKeepsFragments()
    {
        var env = new YtDlpArgs.Environment(@"C:\ff", null, false, Aria2Path: @"C:\bin\aria2c.exe", SystemProxy: "http://127.0.0.1:7897");
        var args = YtDlpArgs.Download(Req, new AppSettings(), env);
        Assert.Equal("8", args[args.IndexOf("-N") + 1]);
        Assert.Contains(@"C:\bin\aria2c.exe", args);
        Assert.Contains("m3u8:native", args);
        Assert.StartsWith("aria2c:-x16", args[args.IndexOf("--downloader-args") + 1]);
        // aria2c ignores Windows' proxy setting, so it is passed on explicitly.
        Assert.Equal("http://127.0.0.1:7897", args[args.IndexOf("--proxy") + 1]);
    }

    [Fact]
    public void OwnProxyWinsAndNoAria2MeansNoDownloader()
    {
        var env = new YtDlpArgs.Environment(@"C:\ff", null, false, Aria2Path: @"C:\bin\aria2c.exe", SystemProxy: "http://127.0.0.1:7897");
        var args = YtDlpArgs.Download(Req, new AppSettings { Proxy = "socks5://10.0.0.1:1080" }, env);
        Assert.Equal(["socks5://10.0.0.1:1080"], args.Select((a, i) => (a, i)).Where(x => x.a == "--proxy").Select(x => args[x.i + 1]));

        var plain = YtDlpArgs.Download(Req, new AppSettings(), new YtDlpArgs.Environment(@"C:\ff", null, false, SystemProxy: "http://127.0.0.1:7897"));
        Assert.DoesNotContain("--downloader", plain);
        Assert.DoesNotContain("--proxy", plain);
        Assert.Contains("-N", plain);
    }

    [Fact]
    public void Aria2ReadoutIsProgress()
    {
        var parser = new YtDlpOutputParser();
        parser.Feed("VFS|137+140|100000000,10000000|NA|Title");
        var e = Assert.IsType<ProgressEvent>(parser.Feed("[#86c7bd 50MiB/100MiB(50%) CN:16 DL:20MiB ETA:2s]"));
        Assert.Equal(50L << 20, e.Progress.Downloaded);
        Assert.Equal(100L << 20, e.Progress.Total);
        Assert.Equal(20 << 20, e.Progress.Speed);
        Assert.Equal(2, e.Progress.Eta);
        Assert.Equal(1, e.Progress.Part);
        Assert.Null(parser.Feed(@"FILE: C:\x.f137.mp4"));
    }

    [Fact]
    public void FinishedLineGluedAfterReadoutCounts()
    {
        var parser = new YtDlpOutputParser();
        parser.Feed("VFS|137+140|100|NA|Title");
        var e = Assert.IsType<ProgressEvent>(parser.Feed("[#86c7bd 82MiB/83MiB(99%) CN:1 DL:33MiB]VFP|finished|100|100|NA|NA|NA|NA|NA"));
        Assert.True(e.Progress.StreamFinished);
    }

    [Theory]
    [InlineData("0B", 0)]
    [InlineData("512KiB", 512 * 1024)]
    [InlineData("1.5GiB", 1610612736)]
    public void Aria2Sizes(string text, long bytes) => Assert.Equal(bytes, YtDlpOutputParser.Bytes(text));

    [Theory]
    [InlineData("45s", 45)]
    [InlineData("3m12s", 192)]
    [InlineData("1h2m", 3720)]
    public void Aria2Eta(string text, double seconds) => Assert.Equal(seconds, YtDlpOutputParser.Seconds(text));

    [Fact]
    public void Aria2VersionLine() => Assert.Equal("1.37.0", ToolManager.Aria2Version("aria2 version 1.37.0"));
}
