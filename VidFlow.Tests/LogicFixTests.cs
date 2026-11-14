using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Tests;

public class SubtitleTimelineTests
{
    [Theory]
    [InlineData("00:00:01,000", "00:00:02,000")]
    [InlineData("00:01:00,000", "00:01:01,000")]
    public void OrdinaryRepeatedPhrasesRemainInTranscripts(string start, string end)
    {
        var cues = SubtitleFiles.Parse($"1\n00:00:00,000 --> 00:00:01,000\nThank you.\n\n2\n{start} --> {end}\nThank you.\n");
        Assert.Equal(2, cues.Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(SubtitleFiles.ToText(cues), "Thank you").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(SubtitleFiles.ToMarkdown(cues, "T", null, "en"), "Thank you").Count);
    }

    [Fact]
    public void RepeatedWordsInsideOneCueAreNotRollingCaptions()
    {
        var cues = SubtitleFiles.Parse("1\n00:00:00,000 --> 00:00:01,000\nHello\nHello\n");
        Assert.Equal("Hello\nHello", Assert.Single(cues).Text);
    }

    [Fact]
    public void ConvertedRollingSrtStillDropsBridgeRepetitions()
    {
        var cues = SubtitleFiles.Parse("1\n00:00:00,000 --> 00:00:01,000\nhello\n\n2\n00:00:01,000 --> 00:00:01,010\nhello\n\n3\n00:00:01,010 --> 00:00:02,000\nhello\nworld\n");
        Assert.Equal(["hello", "world"], cues.Select(c => c.Text));
    }
}

public class DeferredSubtitleTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=a", "en")]
    [InlineData("https://www.youtube.com/watch?v=a", "auto:en-orig")]
    [InlineData("https://www.bilibili.com/video/BV1x", "asr:zh")]
    public void MediaIsSavedBeforeAnySubtitleOrDanmakuRequest(string url, string language)
    {
        var req = new DownloadTemplate { Subtitles = true, SubtitleLanguage = language }.ToRequest(url, "o");
        var args = YtDlpArgs.Download(req, new AppSettings { SaveDanmaku = true }, new(@"C:\ff", null, false), includeSubtitles: false);
        Assert.DoesNotContain("--write-subs", args);
        Assert.DoesNotContain("--write-auto-subs", args);
        Assert.DoesNotContain("--sub-langs", args);
        Assert.Contains("--no-write-subs", args);
        Assert.Contains("--no-write-auto-subs", args);
        Assert.Contains('/', args[args.IndexOf("-o") + 1]);
    }

    [Fact]
    public void DanmakuAloneIsNeverConvertedToSrt()
    {
        var req = new DownloadTemplate { Mode = "subtitles", SubtitleLanguage = "danmaku" }.ToRequest("https://www.bilibili.com/video/BV1x", "o");
        var args = YtDlpArgs.Download(req, new AppSettings(), new(@"C:\ff", null, false));
        Assert.DoesNotContain("--convert-subs", args);
    }
}

public class ToolUsageTests
{
    [Fact]
    public async Task ConcurrentMediaJobsPreventComponentReplacement()
    {
        var usage = new ToolUsage();
        using var first = await usage.UseAsync(TestContext.Current.CancellationToken);
        using var second = await usage.UseAsync(TestContext.Current.CancellationToken);
        Assert.Null(usage.TryBeginUpdate());
        first.Dispose();
        Assert.Null(usage.TryBeginUpdate());
        second.Dispose();
        using var update = usage.TryBeginUpdate();
        Assert.NotNull(update);
        Assert.True(usage.IsUpdating);
    }

    [Fact]
    public async Task JobsWaitForReplacementAndResumeWhenItFinishes()
    {
        var usage = new ToolUsage();
        using var update = usage.TryBeginUpdate();
        var waiting = usage.UseAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.Null(usage.TryBeginUpdate());
        update!.Dispose();
        using var job = await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(usage.IsUpdating);
        Assert.Null(usage.TryBeginUpdate());
    }

    [Fact]
    public async Task AJobWaitingForUpdatesCanBeCanceledWithoutLeakingALease()
    {
        var usage = new ToolUsage();
        using var update = usage.TryBeginUpdate();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = usage.UseAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        update!.Dispose();
        using var next = usage.TryBeginUpdate();
        Assert.NotNull(next);
    }
}

[Collection("DataDir")]
public class CookieAndRepairTests
{
    [Theory]
    [InlineData(".bilibili.com", "1", false)]
    [InlineData(".bilibili.com", "0", true)]
    [InlineData("#HttpOnly_.bilibili.com", "4102444800", true)]
    [InlineData("www.bilibili.com", "4102444800", true)]
    [InlineData("notbilibili.com", "4102444800", false)]
    [InlineData(".bilibili.com", "bad timestamp", false)]
    public void OnlyLiveCookiesForTheActualDomainCountAsALogin(string domain, string expiration, bool expected)
    {
        var old = AppSettings.DataDir;
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            AppSettings.DataDir = dir;
            File.WriteAllText(AccountStore.CookieFile, $"{domain}\tTRUE\t/\tTRUE\t{expiration}\tSESSDATA\tfake\n");
            Assert.Equal(expected, AccountStore.IsLoggedIn(Platform.Bilibili));
            Assert.Equal(expected ? "SESSDATA=fake" : null, AccountStore.CookieHeader(Platform.Bilibili));
        }
        finally { AppSettings.DataDir = old; Directory.Delete(dir, true); }
    }

    [Fact]
    public void PendingSubtitleRepairAndItsWarningSurviveRestart()
    {
        var old = AppSettings.DataDir;
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            AppSettings.DataDir = dir;
            var request = new DownloadTemplate { Subtitles = true, EmbedSubtitles = true, SubtitleLanguage = "en" }.ToRequest("https://youtu.be/a", "o");
            var record = new DownloadRecord { Request = request, Title = "video", FilePath = "video.mp4",
                State = DownloadState.Processing, IsSubtitleRetry = true, SubtitleProblem = "captions unavailable" };
            DownloadStore.Save([record]);
            var restored = Assert.Single(DownloadStore.Load());
            Assert.True(restored.IsSubtitleRetry);
            Assert.True(restored.Request.EmbedSubtitles);
            Assert.Equal("en", restored.Request.SubtitleLanguage);
            Assert.Equal(DownloadState.Queued, restored.State);
            restored.State = DownloadState.Completed;
            Assert.Contains("captions unavailable", DownloadLabels.RestingStatus(restored));
        }
        finally { AppSettings.DataDir = old; Directory.Delete(dir, true); }
    }
}
