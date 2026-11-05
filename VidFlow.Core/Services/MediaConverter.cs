using System.Globalization;
using System.Text;
using System.Text.Json;
using VidFlow.Localization;

namespace VidFlow.Services;

public enum MediaKind { Video, Audio, Image, Subtitle }

/// <summary>What ffprobe says about a file: its streams' codecs (in stream order) and how long it plays.</summary>
public sealed record MediaProbe(IReadOnlyList<string> VideoCodecs, IReadOnlyList<string> AudioCodecs, double Duration,
    IReadOnlyList<string>? SubtitleCodecs = null)
{
    public IReadOnlyList<string> Subtitles => SubtitleCodecs ?? [];
}

/// <summary>
/// Converting files the user already has, between the formats of one kind (and from video to audio). ffmpeg does the
/// work; a stream the target can hold as it is gets copied (instant, nothing lost), only the others are encoded.
/// Subtitles to TXT / Markdown go through <see cref="SubtitleFiles"/>, which writes those itself.
/// </summary>
public static class MediaConverter
{
    static readonly Dictionary<string, MediaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = MediaKind.Video, ["mkv"] = MediaKind.Video, ["mov"] = MediaKind.Video, ["webm"] = MediaKind.Video,
        ["avi"] = MediaKind.Video, ["flv"] = MediaKind.Video, ["m4v"] = MediaKind.Video, ["ts"] = MediaKind.Video,
        ["wmv"] = MediaKind.Video, ["3gp"] = MediaKind.Video,
        ["mp3"] = MediaKind.Audio, ["m4a"] = MediaKind.Audio, ["aac"] = MediaKind.Audio, ["opus"] = MediaKind.Audio,
        ["ogg"] = MediaKind.Audio, ["flac"] = MediaKind.Audio, ["wav"] = MediaKind.Audio, ["wma"] = MediaKind.Audio,
        ["jpg"] = MediaKind.Image, ["jpeg"] = MediaKind.Image, ["png"] = MediaKind.Image, ["webp"] = MediaKind.Image,
        ["bmp"] = MediaKind.Image, ["gif"] = MediaKind.Image, ["tif"] = MediaKind.Image, ["tiff"] = MediaKind.Image,
        ["avif"] = MediaKind.Image,
        ["srt"] = MediaKind.Subtitle, ["vtt"] = MediaKind.Subtitle, ["ass"] = MediaKind.Subtitle, ["ssa"] = MediaKind.Subtitle,
        ["lrc"] = MediaKind.Subtitle,
    };

    public static readonly string[] VideoTargets = ["mp4", "mkv", "mov", "webm"];
    public static readonly string[] AudioTargets = ["mp3", "m4a", "opus", "flac", "wav"];
    public static readonly string[] ImageTargets = ["jpg", "png", "webp", "bmp"];

    static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static MediaKind? KindOf(string path) => Kinds.TryGetValue(Ext(path), out var kind) ? kind : null;

    /// <summary>What a file can become: other formats of its kind (a video also into audio alone), never its own.</summary>
    public static IReadOnlyList<string> TargetsFor(string path)
    {
        var own = Ext(path) is "jpeg" ? "jpg" : Ext(path);
        IEnumerable<string> targets = KindOf(path) switch
        {
            MediaKind.Video => VideoTargets.Concat(AudioTargets),
            MediaKind.Audio => AudioTargets,
            MediaKind.Image => ImageTargets,
            MediaKind.Subtitle => SubtitleFiles.Formats,
            _ => [],
        };
        return targets.Where(t => t != own).ToList();
    }

    /// <summary>The usual choice: MP4 for a video (MKV for an MP4), MP3 for audio, JPG for an image, SRT for subtitles.</summary>
    public static string DefaultTarget(string path)
    {
        var targets = TargetsFor(path);
        var preferred = KindOf(path) switch
        {
            MediaKind.Video => "mp4",
            MediaKind.Audio => "mp3",
            MediaKind.Image => "jpg",
            _ => "srt",
        };
        return targets.Contains(preferred) ? preferred : targets.FirstOrDefault() ?? preferred;
    }

    public static bool IsAudioTarget(string target) => AudioTargets.Contains(target);

    /// <summary>"name.mkv" next to the source (or in <paramref name="folder"/>), "name (2).mkv" when that is taken.</summary>
    public static string OutputPath(string source, string target, string? folder = null)
    {
        var dir = folder ?? Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var path = Path.Combine(dir, $"{name}.{target}");
        for (var n = 2; File.Exists(path) || string.Equals(path, source, StringComparison.OrdinalIgnoreCase); n++)
            path = Path.Combine(dir, $"{name} ({n}).{target}");
        return path;
    }

    // ---- Which streams can be copied ----------------------------------------------------------------------------

    /// <summary>Video codecs (ffprobe names) each target holds as they are.</summary>
    static bool HoldsVideo(string target, string codec) => target switch
    {
        "mkv" => true,
        "mp4" => codec is "h264" or "hevc" or "av1" or "mpeg4" or "vp9",
        "mov" => codec is "h264" or "hevc" or "mpeg4" or "prores",
        "webm" => codec is "vp8" or "vp9" or "av1",
        _ => false,
    };

    static bool HoldsAudio(string target, string codec) => target switch
    {
        "mkv" => true,
        "mp4" or "mov" => codec is "aac" or "mp3" or "ac3" or "eac3" or "alac",
        "webm" => codec is "opus" or "vorbis",
        "m4a" => codec is "aac" or "alac",
        "mp3" => codec == "mp3",
        "opus" => codec == "opus",
        "flac" => codec == "flac",
        "wav" => codec.StartsWith("pcm_", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Subtitle codecs MKV holds as they are. MP4's own text subtitles (mov_text) go in as SRT; ones it can't
    /// hold at all (closed captions, teletext) are left out.</summary>
    static readonly string[] MkvSubtitles = ["subrip", "ass", "ssa", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle"];

    static string[] VideoEncoder(string target) => target == "webm"
        ? ["libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1"]
        // x264 needs even sizes in 4:2:0: an odd one loses its last row or column.
        : ["libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"];

    static string[] AudioEncoder(string target) => target switch
    {
        "mp3" => ["libmp3lame", "-q:a", "2"],
        // libopus takes only the usual layouts (not 5.1 "side", say): the nearest one is used.
        "opus" or "webm" => ["libopus", "-b:a", "160k", "-af", "aformat=channel_layouts=7.1|5.1|5.0|quad|stereo|mono"],
        "flac" => ["flac"],
        "wav" => ["pcm_s16le"],
        _ => ["aac", "-b:a", "192k"],
    };

    /// <summary>ffmpeg's arguments for a video or audio conversion (pure, for testing). Progress goes to stdout.</summary>
    public static List<string> Arguments(string source, string dest, string target, MediaProbe probe)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", source, "-progress", "pipe:1", "-nostats" };
        if (IsAudioTarget(target))
        {
            // Audio alone: the first track, the cover pictures some files carry left out.
            args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn", "-c:a"]);
            args.AddRange(probe.AudioCodecs.FirstOrDefault() is { } codec && HoldsAudio(target, codec) ? ["copy"] : AudioEncoder(target));
        }
        else
        {
            // MKV keeps every track (subtitles and all); the others keep picture and sound.
            // ("V": video that isn't a cover picture, which would otherwise be encoded as a second video.)
            // (MKV can't hold data tracks, like the timecode in camera and QuickTime files.)
            args.AddRange(target == "mkv" ? ["-map", "0", "-map", "-0:d?"] : ["-map", "0:V?", "-map", "0:a?"]);
            var copyVideo = probe.VideoCodecs.All(c => HoldsVideo(target, c));
            args.Add("-c:v");
            args.AddRange(copyVideo ? ["copy"] : VideoEncoder(target));
            // HEVC copied into MP4 / MOV is tagged the way Apple's players need to play it.
            if (copyVideo && target is "mp4" or "mov" && probe.VideoCodecs.Count > 0 && probe.VideoCodecs.All(c => c == "hevc"))
                args.AddRange(["-tag:v", "hvc1"]);
            args.Add("-c:a");
            args.AddRange(probe.AudioCodecs.All(c => HoldsAudio(target, c)) ? ["copy"] : AudioEncoder(target));
            if (target == "mkv")
            {
                // Each subtitle track is copied, made into SRT, or left out (the output numbering skips those).
                var kept = 0;
                for (var i = 0; i < probe.Subtitles.Count; i++)
                {
                    var codec = probe.Subtitles[i];
                    if (MkvSubtitles.Contains(codec)) args.AddRange([$"-c:s:{kept++}", "copy"]);
                    else if (codec is "mov_text" or "text") args.AddRange([$"-c:s:{kept++}", "srt"]);
                    else args.AddRange(["-map", $"-0:s:{i}"]);
                }
            }
            if (target is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        }
        args.Add(dest);
        return args;
    }

    /// <summary>Whether everything is copied (the conversion is a quick rewrap, nothing is re-encoded).</summary>
    public static bool IsRewrap(string target, MediaProbe probe) => IsAudioTarget(target)
        ? probe.AudioCodecs.FirstOrDefault() is { } a && HoldsAudio(target, a)
        : probe.VideoCodecs.All(c => HoldsVideo(target, c)) && probe.AudioCodecs.All(c => HoldsAudio(target, c));

    public static List<string> ImageArguments(string source, string dest, string target)
    {
        // One picture (an animation's first frame), never taken for a numbered sequence like "100%d.png".
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", source, "-frames:v", "1", "-update", "1" };
        // JPG and BMP have no transparency: what was see-through becomes white rather than whatever color it hid.
        if (target is "jpg" or "bmp")
            args.AddRange(["-filter_complex", "[0:v]format=rgba,split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=format=auto"]);
        args.AddRange(target switch
        {
            "jpg" => ["-q:v", "2"],
            "webp" => ["-quality", "90"],
            _ => [],
        });
        args.Add(dest);
        return args;
    }

    /// <summary>"out_time_us=12345678" (ffmpeg's -progress) as a percentage of <paramref name="duration"/>.</summary>
    public static double? ProgressOf(string line, double duration)
    {
        if (duration <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var us))
            return null;
        return Math.Clamp(us / 1e6 / duration * 100, 0, 100);
    }

    /// <summary>ffprobe's JSON (streams and format) read into what the conversion needs.</summary>
    public static MediaProbe ParseProbe(string json)
    {
        var video = new List<string>();
        var audio = new List<string>();
        var subtitles = new List<string>();
        double duration = 0;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
            {
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                // A cover picture inside an audio or video file is a "video" stream marked as attached.
                var attached = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;
                if (type == "video" && !attached) video.Add(codec);
                else if (type == "audio") audio.Add(codec);
                else if (type == "subtitle") subtitles.Add(codec);
            }
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var dur)
            && double.TryParse(dur.GetString(), CultureInfo.InvariantCulture, out var seconds))
            duration = seconds;
        return new(video, audio, duration, subtitles);
    }

    // ---- Running --------------------------------------------------------------------------------------------------

    /// <summary>Converts <paramref name="source"/> into <paramref name="dest"/> (written to a ".part" file first, so a
    /// canceled or failed conversion leaves nothing that looks finished). Progress is 0-100.</summary>
    public static async Task ConvertAsync(string source, string dest, string target, Action<double> onProgress, CancellationToken ct)
    {
        var ffmpeg = ToolManager.FfmpegPath ?? throw new YtDlpException(L.Get("Error_ConvertNeedsFfmpeg"));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = PartPath(dest);
        try
        {
            if (KindOf(source) == MediaKind.Subtitle)
                await ConvertSubtitleAsync(ffmpeg, source, part, target, ct);
            else if (KindOf(source) == MediaKind.Image)
                await RunFfmpegAsync(ffmpeg, ImageArguments(source, part, target), null, ct);
            else
            {
                var probe = await ProbeAsync(ffmpeg, source, ct);
                if (!IsAudioTarget(target) && probe.VideoCodecs.Count == 0) throw new YtDlpException(L.Get("Error_ConvertNoVideo"));
                if (probe.AudioCodecs.Count == 0 && IsAudioTarget(target)) throw new YtDlpException(L.Get("Error_ConvertNoAudio"));
                Log.Info($"Converting {Path.GetFileName(source)} to {target} ({(IsRewrap(target, probe) ? "rewrap" : "encode")})");
                await RunFfmpegAsync(ffmpeg, Arguments(source, part, target, probe), line =>
                {
                    if (ProgressOf(line, probe.Duration) is { } p) onProgress(p);
                }, ct);
            }
            File.Move(part, dest, overwrite: false);
            onProgress(100);
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>Where <see cref="ConvertAsync"/> writes before the file is finished.</summary>
    public static string PartPath(string dest) => dest + ".part" + Path.GetExtension(dest);

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { Log.Info($"Could not delete {path}: {ex.Message}"); }
    }

    static async Task ConvertSubtitleAsync(string ffmpeg, string source, string dest, string target, CancellationToken ct)
    {
        // ffmpeg reads subtitles as UTF-8 only: older ones (Chinese GBK, Japanese Shift-JIS) are put into UTF-8 first.
        var utf8 = await Utf8CopyAsync(source, ct);
        try
        {
            await ConvertUtf8SubtitleAsync(ffmpeg, utf8 ?? source, Path.GetFileNameWithoutExtension(source), dest, target, ct);
        }
        finally
        {
            if (utf8 is not null) TryDelete(utf8);
        }
    }

    static MediaConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The text of a subtitle file that isn't UTF-8 (UTF-16, or the system's legacy code page); null for UTF-8.</summary>
    public static string? DecodeLegacy(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return null;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return null;
        }
        catch (DecoderFallbackException)
        {
            var page = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            // A Western system's code page would read Chinese as accented letters: GB18030 is the likelier there.
            return Encoding.GetEncoding(page is 1252 or 437 ? 54936 : page).GetString(bytes);
        }
    }

    /// <summary>A UTF-8 copy of a subtitle file in another encoding (null when it is UTF-8 already).</summary>
    static async Task<string?> Utf8CopyAsync(string source, CancellationToken ct)
    {
        if (DecodeLegacy(await File.ReadAllBytesAsync(source, ct)) is not { } text) return null;
        var copy = Path.Combine(Path.GetTempPath(), $"vidflow-{Guid.NewGuid():N}{Path.GetExtension(source)}");
        await File.WriteAllTextAsync(copy, text, new UTF8Encoding(false), ct);
        return copy;
    }

    static async Task ConvertUtf8SubtitleAsync(string ffmpeg, string source, string title, string dest, string target, CancellationToken ct)
    {
        if (!SubtitleFiles.IsText(target))
        {
            await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-v", "error", "-y", "-i", source, "-f", target switch
            {
                "vtt" => "webvtt",
                _ => target,
            }, dest], null, ct);
            return;
        }
        // TXT and Markdown are read from SRT or WebVTT; ASS and LRC are put into SRT first.
        var text = Ext(source) is "srt" or "vtt" ? await File.ReadAllTextAsync(source, ct) : null;
        if (text is null)
        {
            var srt = dest + ".srt";
            try
            {
                await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-v", "error", "-y", "-i", source, "-f", "srt", srt], null, ct);
                text = await File.ReadAllTextAsync(srt, ct);
            }
            finally
            {
                TryDelete(srt);
            }
        }
        var cues = SubtitleFiles.Parse(text);
        var output = target == "md" ? SubtitleFiles.ToMarkdown(cues, title, null, "") : SubtitleFiles.ToText(cues);
        await File.WriteAllTextAsync(dest, output, new UTF8Encoding(false), ct);
    }

    static async Task<MediaProbe> ProbeAsync(string ffmpeg, string source, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) throw new YtDlpException(L.Get("Error_ConvertNeedsFfmpeg"));
        var json = new StringBuilder();
        var (code, error) = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name:stream_disposition=attached_pic:format=duration", "-of", "json", source],
            line => json.AppendLine(line), ct, stdoutOnly: true);
        if (code != 0) throw new YtDlpException(L.Get("Error_ConvertUnreadable"), error);
        return ParseProbe(json.ToString());
    }

    static async Task RunFfmpegAsync(string ffmpeg, List<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var (code, error) = await ProcessRunner.RunAsync(ffmpeg, args, onLine, ct);
        if (code != 0) throw new YtDlpException(L.Get("Error_ConvertFailed"), error);
    }
}
