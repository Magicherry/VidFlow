using System.Globalization;
using System.Text.RegularExpressions;

namespace VidFlow.Services;

/// <summary>Progress reported while yt-dlp runs. <see cref="Processing"/> means all streams are downloaded
/// and ffmpeg is merging/converting; <see cref="StreamFinished"/> marks one stream done with more to come.
/// <see cref="Percent"/> covers the whole download (all streams); the byte counts are the current stream's.</summary>
public readonly record struct DownloadProgress(
    bool Processing, bool StreamFinished, int Part, int Parts,
    double Percent, long Downloaded, long Total, double Speed, double Eta);

public abstract record DownloadEvent;
public sealed record StartedEvent(int Parts, string? Title, string? ThumbnailUrl) : DownloadEvent;
public sealed record FileEvent(string Path) : DownloadEvent;
public sealed record ProgressEvent(DownloadProgress Progress) : DownloadEvent;

/// <summary>
/// Parses the machine-readable lines VidFlow asks yt-dlp to print (see <see cref="YtDlpArgs"/>):
/// <c>VFS|format|sizes|thumbnail|title</c> before downloading, <c>VFP|…</c> for progress, <c>VFF|path</c> at the end.
/// Stateful: counts finished streams so "video then audio" can be told apart, and turns them into one smooth
/// percentage for the whole download.
/// </summary>
public sealed class YtDlpOutputParser
{
    /// <summary>When a stream's size is unknown until it starts: audio is typically about a tenth of the video.</summary>
    const double UnknownLastStreamShare = 0.1;

    int _parts = 1;
    int _finished;
    long[] _sizes = [0];
    bool[] _estimated = [false];
    double _percent;

    public DownloadEvent? Feed(string line)
    {
        if (line.StartsWith("VFS|", StringComparison.Ordinal))
        {
            // The title comes last and may itself contain "|".
            var f = line.Split('|', 5);
            _parts = f[1].Count(c => c == '+') + 1;
            _finished = 0;
            _percent = 0;
            _sizes = new long[_parts];
            _estimated = new bool[_parts];
            var sizes = (f.ElementAtOrDefault(2) ?? "").Split(',');
            for (var i = 0; i < _parts; i++) _sizes[i] = Long(sizes.ElementAtOrDefault(i) ?? "");
            return new StartedEvent(_parts, NullIfNa(f.ElementAtOrDefault(4)), NullIfNa(f.ElementAtOrDefault(3)));
        }
        // aria2c writes its own readout without a line break, so yt-dlp's final line can end up glued after it.
        var glued = line.IndexOf("]VF", StringComparison.Ordinal);
        if (glued > 0) line = line[(glued + 1)..];
        if (line.StartsWith("VFF|", StringComparison.Ordinal))
            return new FileEvent(line[4..].Trim());
        if (line.StartsWith("[#", StringComparison.Ordinal))
            return Aria2(line);
        if (!line.StartsWith("VFP|", StringComparison.Ordinal))
            return null;

        var p = line.Split('|');
        if (p.Length < 7) return null;
        if (p[1] == "finished")
        {
            Sizes(Long(p[3]), Long(p[4]));
            _finished++;
            var last = _finished >= _parts;
            _percent = last ? 100 : Math.Max(_percent, Overall(_finished, 0));
            return new ProgressEvent(new DownloadProgress(last, !last, Math.Min(_finished, _parts), _parts, _percent, 0, 0, 0, 0));
        }
        return Downloading(Long(p[2]), Long(p[3]), Long(p[4]), Dbl(p[5]), Dbl(p[6]),
            p.Length >= 9 ? Long(p[7]) : 0, p.Length >= 9 ? Long(p[8]) : 0);
    }

    /// <summary>aria2c's summary line, e.g. <c>[#86c7bd 82MiB/83MiB(99%) CN:16 DL:33MiB ETA:1s]</c>. While aria2c
    /// downloads, yt-dlp prints no progress of its own; it reports the stream finished as usual afterwards.</summary>
    ProgressEvent? Aria2(string line)
    {
        var m = Aria2Readout.Match(line);
        if (!m.Success) return null;
        var downloaded = Bytes(m.Groups["done"].Value);
        var total = Bytes(m.Groups["total"].Value);
        if (total <= 0) return null;
        return Downloading(downloaded, total, 0, m.Groups["speed"].Success ? Bytes(m.Groups["speed"].Value) : 0,
            m.Groups["eta"].Success ? Seconds(m.Groups["eta"].Value) : 0, 0, 0);
    }

    static readonly Regex Aria2Readout = new(
        @"^\[#\w+ (?<done>[\d.]+\w*)/(?<total>[\d.]+\w*)\(\d+%\)(?: CN:\d+)?(?: DL:(?<speed>[\d.]+\w*))?(?: ETA:(?<eta>\w+))?\]");

    /// <summary>Records the current stream's size: an exact size beats the one printed up front; a running estimate
    /// only stands in for an unknown one. Returns the size to measure progress against.</summary>
    long Sizes(long total, long estimate)
    {
        var current = Math.Min(_finished, _parts - 1);
        if (total > 0)
        {
            _sizes[current] = total;
            _estimated[current] = false;
            return total;
        }
        if (estimate > 0 && (_sizes[current] <= 0 || _estimated[current]))
        {
            _sizes[current] = estimate;
            _estimated[current] = true;
            return estimate;
        }
        return 0;
    }

    ProgressEvent Downloading(long downloaded, long total, long estimate, double speed, double eta, long fragment, long fragments)
    {
        var current = Math.Min(_finished, _parts - 1);
        total = Sizes(total, estimate);
        // Fragmented streams (HLS / DASH segments) re-estimate their total after every fragment, which makes
        // bytes ÷ estimate jump around; the fragment count only goes up.
        var streamFraction = fragments > 0 ? (double)fragment / fragments
            : total > 0 ? (double)downloaded / total
            : 0;
        // Never move backwards within one download.
        _percent = Math.Max(_percent, Overall(current, Math.Clamp(streamFraction, 0, 1)));
        return new ProgressEvent(new DownloadProgress(false, false, current + 1, _parts,
            _percent, downloaded, total, speed, eta));
    }

    /// <summary>Percent of the whole download with <paramref name="done"/> streams finished and the next one
    /// <paramref name="fraction"/> through, each stream weighted by its size.</summary>
    double Overall(int done, double fraction)
    {
        var weights = new double[_parts];
        var known = _sizes.Where(s => s > 0).ToList();
        for (var i = 0; i < _parts; i++)
            weights[i] = _sizes[i] > 0 ? _sizes[i]
                : known.Count == 0 ? 1
                : i == _parts - 1 && _parts > 1 ? known.Sum() * UnknownLastStreamShare
                : known.Average();
        var sum = weights.Sum();
        var reached = weights.Take(done).Sum() + (done < _parts ? weights[done] * fraction : 0);
        return Math.Clamp(reached / sum * 100, 0, 100);
    }

    static string? NullIfNa(string? s) => string.IsNullOrWhiteSpace(s) || s == "NA" ? null : s;

    static long Long(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : 0;

    /// <summary>aria2's sizes: "82MiB", "512KiB", "0B", "1.2GiB".</summary>
    internal static long Bytes(string s)
    {
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (!double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        var scale = s[i..] switch { "KiB" => 1L << 10, "MiB" => 1L << 20, "GiB" => 1L << 30, "TiB" => 1L << 40, _ => 1 };
        return (long)(v * scale);
    }

    /// <summary>aria2's ETA: "45s", "3m12s", "1h2m".</summary>
    internal static double Seconds(string s)
    {
        double total = 0, n = 0;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) { n = n * 10 + (c - '0'); continue; }
            total += n * c switch { 'h' => 3600, 'm' => 60, _ => 1 };
            n = 0;
        }
        return total;
    }

    static double Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
