using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>One stream from yt-dlp's format list, as far as estimating sizes needs. <paramref name="Height"/> is the
/// shorter side; <paramref name="Bytes"/> is a size the site states, null when it only gives a bitrate.</summary>
public sealed record MediaFormat(string? Id, int Height, double Fps, string? VCodec, string? ACodec, string? DynamicRange,
    double? Tbr, long? Bytes, string? Language, double LanguagePreference)
{
    public bool HasVideo => VCodec is not (null or "none");
    public bool HasAudio => ACodec is not (null or "none");
}

/// <summary>
/// How big a download will be: the streams yt-dlp would choose for these options (mirroring the filters and sort order
/// in <see cref="YtDlpArgs"/>), rather than the largest ones on offer.
/// </summary>
public static class SizeEstimator
{
    /// <summary>The size for a quality under the given options. Sites whose format list isn't known (Douyin) keep the
    /// size that came with the quality.</summary>
    public static long? Estimate(VideoInfo video, QualityOption quality, DownloadTemplate options) =>
        video.Formats.Count == 0
            ? quality.EstimatedBytes
            : Estimate(video.Formats, video.Duration, quality.Height, options, AppSettings.Current.PreferH264);

    public static long? Estimate(IReadOnlyList<MediaFormat> formats, double? duration, int height, DownloadTemplate options, bool preferH264)
    {
        if (PickVideo(formats, height, options, preferH264) is not { } video) return null;
        if (SizeOf(video, formats, duration) is not { } videoBytes) return null;
        // A stream with sound built in has nothing merged into it.
        if (video.HasAudio) return videoBytes;
        return videoBytes + (PickAudio(formats, options) is { } audio ? SizeOf(audio, formats, duration) ?? 0 : 0);
    }

    /// <summary>The video stream the download would take: the codec asked for (relaxed if the video has none), then resolution, picture,
    /// then frame rate and bitrate, as in the format filter and sort order given to yt-dlp.</summary>
    public static MediaFormat? PickVideo(IReadOnlyList<MediaFormat> formats, int height, DownloadTemplate options, bool preferH264)
    {
        var candidates = formats.Where(f => f.HasVideo && CodecFits(f, options)).ToList();
        if (candidates.Count == 0) candidates = formats.Where(f => f.HasVideo).ToList();
        // yt-dlp keeps the codec and gives up resolution: the height asked for, else the next one down, else up.
        var below = candidates.Where(f => f.Height <= height).Select(f => f.Height).DefaultIfEmpty(0).Max();
        var chosen = below > 0 ? below : candidates.Select(f => f.Height).DefaultIfEmpty(0).Min();
        var compatibility = preferH264 && options.Codec == VideoCodecs.Auto;
        return candidates.Where(f => f.Height == chosen)
            .OrderByDescending(f => PictureRank(f, options.DynamicRange, compatibility))
            .ThenByDescending(f => compatibility && VideoCodecs.Family(f.VCodec) == "h264")
            .ThenByDescending(f => f.Fps)
            .ThenByDescending(f => f.Tbr ?? 0)
            .FirstOrDefault();
    }

    /// <summary>The audio stream merged in: the track language, then the kind the container takes (both relaxed if
    /// none fits), then the closest to the bitrate asked for.</summary>
    public static MediaFormat? PickAudio(IReadOnlyList<MediaFormat> formats, DownloadTemplate options)
    {
        var audio = formats.Where(f => !f.HasVideo && f.HasAudio).ToList();
        var language = options.AudioLanguage.Trim();
        var inLanguage = language.Length > 0
            ? audio.Where(f => f.Language?.StartsWith(language, StringComparison.OrdinalIgnoreCase) == true).ToList()
            : audio;
        if (inLanguage.Count > 0) audio = inLanguage;
        var fitting = audio.Where(f => AudioFits(f, options)).ToList();
        if (fitting.Count > 0) audio = fitting;
        var limit = options.AudioQuality;
        return audio
            .OrderByDescending(f => language.Length > 0 ? 0 : f.LanguagePreference)
            .ThenByDescending(f => limit > 0 && f.Tbr > limit ? 0 : 1)
            .ThenByDescending(f => limit > 0 && f.Tbr > limit ? -(f.Tbr ?? 0) : f.Tbr ?? 0)
            .FirstOrDefault();
    }

    /// <summary>The bitrate of the audio the download would take with no limit asked for, rounded; null if unknown.</summary>
    public static int? TopAudioKbps(IReadOnlyList<MediaFormat> formats, DownloadTemplate options)
    {
        var unlimited = options.Clone();
        unlimited.AudioQuality = 0;
        return PickAudio(formats, unlimited)?.Tbr is > 0 and var kbps ? (int)Math.Round(kbps) : null;
    }

    /// <summary>The stated size; else the stated size of the same encode in another delivery; else bitrate × duration.
    /// YouTube's HLS streams state no size, and the bitrate they do state is the peak, up to twice the average, but
    /// they are the same encode as a DASH stream that does (measured: the files match to the kilobyte).</summary>
    static long? SizeOf(MediaFormat f, IReadOnlyList<MediaFormat> formats, double? duration)
    {
        if (f.Bytes is > 0) return f.Bytes;
        if (f.HasVideo && !f.HasAudio && VideoCodecs.Family(f.VCodec) is { } family
            && formats.FirstOrDefault(o => o.Bytes is > 0 && o.HasVideo && !o.HasAudio && o.Height == f.Height
                                           && Math.Abs(o.Fps - f.Fps) < 1 && VideoCodecs.Family(o.VCodec) == family
                                           && MediaFeatures.HdrKind(o.DynamicRange) == MediaFeatures.HdrKind(f.DynamicRange)) is { } sibling)
            return sibling.Bytes;
        // tbr is in kbit/s.
        return f.Tbr is > 0 && duration is > 0 ? (long)(f.Tbr.Value * 1000 / 8 * duration.Value) : null;
    }

    static bool CodecFits(MediaFormat f, DownloadTemplate o)
    {
        var family = VideoCodecs.Family(f.VCodec);
        if (o.Codec != VideoCodecs.Auto) return family == o.Codec;
        return o.Container != "webm" || family is "vp9" or "av1";
    }

    static bool AudioFits(MediaFormat f, DownloadTemplate o)
    {
        var a = f.ACodec?.ToLowerInvariant() ?? "";
        // Audio-only downloads take the best track, in the format asked for if it is offered directly.
        if (o.Mode == "audio")
            return o.AudioFormat switch { "m4a" => a.StartsWith("mp4a"), "opus" => a == "opus", _ => true };
        return o.AudioFormat switch
        {
            "aac" => a.StartsWith("mp4a"),
            "opus" => a == "opus",
            MediaFeatures.HiRes => MediaFeatures.IsHiResCodec(a),
            MediaFeatures.Atmos => MediaFeatures.IsAtmosCodec(a),
            _ => o.Container switch
            {
                "webm" => a is "opus" or "vorbis",
                "mkv" => true,
                _ => a.StartsWith("mp4a"),
            },
        };
    }

    static readonly string[] HdrOrder = ["HLG", "HDR10", "HDR10+", "HDR12", "DV"];

    /// <summary>yt-dlp's "hdr" sort with the cap from <see cref="MediaFeatures.HdrSort"/>: the best at or below the cap,
    /// anything above it last.</summary>
    static int PictureRank(MediaFormat f, string picture, bool compatibility)
    {
        var rank = MediaFeatures.HdrKind(f.DynamicRange) is { } kind ? Array.IndexOf(HdrOrder, kind) + 1 : 0;
        var cap = picture == MediaFeatures.DolbyVision ? HdrOrder.Length
            : picture == MediaFeatures.Sdr || compatibility ? 0
            : Array.IndexOf(HdrOrder, "HDR10") + 1;
        return rank <= cap ? rank : -1;
    }
}
