using System.Globalization;

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
        if (line.StartsWith("VFF|", StringComparison.Ordinal))
            return new FileEvent(line[4..].Trim());
        if (!line.StartsWith("VFP|", StringComparison.Ordinal))
            return null;

        var p = line.Split('|');
        if (p.Length < 7) return null;
        var current = Math.Min(_finished, _parts - 1);
        var downloaded = Long(p[2]);
        var total = Long(p[3]);
        // An exact size beats the one printed up front; a running estimate only stands in for an unknown one.
        if (total > 0)
        {
            _sizes[current] = total;
            _estimated[current] = false;
        }
        else if ((total = Long(p[4])) > 0 && (_sizes[current] <= 0 || _estimated[current]))
        {
            _sizes[current] = total;
            _estimated[current] = true;
        }

        if (p[1] == "finished")
        {
            _finished++;
            var last = _finished >= _parts;
            _percent = last ? 100 : Math.Max(_percent, Overall(_finished, 0));
            return new ProgressEvent(new DownloadProgress(last, !last, Math.Min(_finished, _parts), _parts, _percent, 0, 0, 0, 0));
        }

        // Fragmented streams (HLS / DASH segments) re-estimate their total after every fragment, which makes
        // bytes ÷ estimate jump around; the fragment count only goes up.
        var fragment = p.Length >= 9 ? Long(p[7]) : 0;
        var fragments = p.Length >= 9 ? Long(p[8]) : 0;
        var streamFraction = fragments > 0 ? (double)fragment / fragments
            : total > 0 ? (double)downloaded / total
            : 0;
        // Never move backwards within one download.
        _percent = Math.Max(_percent, Overall(current, Math.Clamp(streamFraction, 0, 1)));
        return new ProgressEvent(new DownloadProgress(false, false, current + 1, _parts,
            _percent, downloaded, total, Dbl(p[5]), Dbl(p[6])));
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

    static double Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
