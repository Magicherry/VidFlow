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
        Assert.StartsWith("bv*[vcodec~='^(vp0?9|av01)']+ba[acodec~='^(opus|vorbis)']", F(args));
        Assert.EndsWith("/bv*+ba/b", F(args));
        Assert.Equal("webm/mkv", Arg(args, "--merge-output-format"));
    }

    [Fact]
    public void MkvTakesAnyAudioAndRecodeIsOptIn()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 0, false, "o", Container: "mkv");
        var args = YtDlpArgs.Download(req, new AppSettings(), Env);
        Assert.Equal("bv*+ba/b", F(args));
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
        Assert.Equal("bv*+ba[ext=m4a][language^=ja]", chain[0]);
        Assert.Contains("bv*+ba[language^=ja]", chain);
        Assert.Equal("b", chain[^1]);
    }

    [Fact]
    public void AudioQualityLimitsAudioBitrateInVideoMode()
    {
        var req = new DownloadRequest("https://youtu.be/x", "video", 720, false, "o", AudioQuality: 128);
        Assert.Equal("res:720,fps,vbr,tbr,abr:128", Arg(YtDlpArgs.Download(req, new AppSettings(), Env), "-S"));
    }

    [Theory]
    [InlineData("mp3", 192, "192K", "ba/b")]
    [InlineData("m4a", 0, "0", "ba[ext=m4a]/ba/b")]
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
