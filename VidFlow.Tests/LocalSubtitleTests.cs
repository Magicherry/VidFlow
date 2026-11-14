using VidFlow.Services;

namespace VidFlow.Tests;

public class LocalSubtitleTests
{
    [Fact]
    public void DetectsRealVideoAudioAndSubtitleStreamIndexes()
    {
        var info = LocalSubtitles.ParseInfo("""
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264","disposition":{"attached_pic":0}},
              {"index":1,"codec_type":"audio","codec_name":"aac"},
              {"index":3,"codec_type":"subtitle","codec_name":"mov_text","tags":{"language":"eng","title":"English"}},
              {"index":5,"codec_type":"subtitle","codec_name":"hdmv_pgs_subtitle"}],
              "format":{"duration":"42.5"}}
            """ );
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(42.5, info.Duration);
        Assert.Equal([3, 5], info.Tracks.Select(t => t.Index));
        Assert.True(info.Tracks[0].CanExport);
        Assert.Equal(("eng", "English"), (info.Tracks[0].Language, info.Tracks[0].Title));
        Assert.False(info.Tracks[1].CanExport);
        Assert.Equal("und", info.Tracks[1].Language);
    }

    [Fact]
    public void CoverArtIsNotAProcessableVideo()
    {
        var info = LocalSubtitles.ParseInfo("""
            {"streams":[{"index":0,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},
            {"index":1,"codec_type":"audio","codec_name":"mp3"}]}
            """);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Empty(info.Tracks);
    }

    [Theory]
    [InlineData(LocalSubtitleOperation.Generate, true)]
    [InlineData(LocalSubtitleOperation.GenerateAndEmbed, true)]
    [InlineData(LocalSubtitleOperation.Extract, false)]
    [InlineData(LocalSubtitleOperation.Remove, false)]
    [InlineData(LocalSubtitleOperation.Embed, false)]
    public void ExistingSubtitlesNeedNoRecognitionModel(LocalSubtitleOperation operation, bool speech) =>
        Assert.Equal(speech, LocalSubtitles.NeedsSpeech(operation));

    [Theory]
    [InlineData(LocalSubtitleOperation.Generate, false, LocalSubtitleOperation.Generate)]
    [InlineData(LocalSubtitleOperation.Generate, true, LocalSubtitleOperation.GenerateAndEmbed)]
    [InlineData(LocalSubtitleOperation.Embed, true, LocalSubtitleOperation.Embed)]
    [InlineData(LocalSubtitleOperation.Extract, true, LocalSubtitleOperation.Extract)]
    [InlineData(LocalSubtitleOperation.Remove, true, LocalSubtitleOperation.Remove)]
    public void EmbeddingSwitchOnlyChangesSubtitleGeneration(LocalSubtitleOperation action, bool embed, LocalSubtitleOperation expected) =>
        Assert.Equal(expected, LocalSubtitles.ResolveOperation(action, embed));

    [Fact]
    public void ImportedSubtitleTrackUsesItsOwnTitle()
    {
        var args = LocalSubtitles.EmbedArguments("video.mp4", "normalized.srt", "output.mkv", new(true, false, 10, []), "en", "English captions");
        Assert.Contains("title=English captions", args);
        Assert.Contains("language=eng", args);
    }

    [Fact]
    public async Task EmbeddingRequiresASubtitleFileBeforeProcessing()
    {
        await Assert.ThrowsAsync<YtDlpException>(() => LocalSubtitles.ProcessAsync(new("video.mp4", LocalSubtitleOperation.Embed),
            _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SubtitleRemovalCopiesOriginalStreamsWithoutSubtitles()
    {
        var args = LocalSubtitles.RemoveArguments("source.mkv", "dest.mkv");
        Assert.Contains("-0:s?", args);
        Assert.Contains("0", args);
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.DoesNotContain("-vn", args);
        Assert.Equal("dest.mkv", args[^1]);
    }

    [Fact]
    public void EmbeddingKeepsExistingTracksAndConvertsMovTextForMkv()
    {
        var info = new LocalSubtitleInfo(true, true, 10, [new(2, "mov_text", "eng", null), new(4, "ass", "chi", null)]);
        var args = LocalSubtitles.EmbedArguments("source.mp4", "generated.srt", "dest.mkv", info, "en");
        Assert.Contains("0", args);
        Assert.DoesNotContain("-0:s?", args);
        Assert.Contains("-0:d?", args);
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.Equal("srt", args[args.IndexOf("-c:s:0") + 1]);
        Assert.DoesNotContain("-c:s:1", args);
        Assert.Equal("srt", args[args.IndexOf("-c:s:2") + 1]);
        Assert.Contains("language=eng", args);
    }

    [Fact]
    public void OutputNamingNeverOverwritesTheSourceOrAnEarlierResult()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Path.Combine(dir, "video.mkv");
            File.WriteAllText(source, "original");
            var first = LocalSubtitles.OutputPath(source, "subtitled", "mkv");
            Assert.Equal(Path.Combine(dir, "video.subtitled.mkv"), first);
            File.WriteAllText(first, "previous");
            var second = LocalSubtitles.OutputPath(source, "subtitled", "mkv");
            Assert.Equal(Path.Combine(dir, "video.subtitled (2).mkv"), second);
            File.WriteAllText(MediaConverter.PartPath(second), "in progress");
            Assert.Equal(Path.Combine(dir, "video.subtitled (3).mkv"), LocalSubtitles.OutputPath(source, "subtitled", "mkv"));
            Assert.Equal("original", File.ReadAllText(source));
            Assert.Equal("previous", File.ReadAllText(first));
            var other = Path.Combine(dir, "output");
            Assert.Equal(Path.Combine(other, "video.en.srt"), LocalSubtitles.OutputPath(source, "en", "srt", other));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MissingSourceIsReportedBeforeAnyProcessing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        await Assert.ThrowsAsync<FileNotFoundException>(() => LocalSubtitles.ProcessAsync(new(missing, LocalSubtitleOperation.Remove),
            _ => { }, TestContext.Current.CancellationToken));
    }
}
