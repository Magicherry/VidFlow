using System.Globalization;

namespace VidFlow.Services;

/// <summary>Progress reported while yt-dlp runs. <see cref="Processing"/> means all streams are downloaded
/// and ffmpeg is merging/converting; <see cref="StreamFinished"/> marks one stream done with more to come.</summary>
public readonly record struct DownloadProgress(
    bool Processing, bool StreamFinished, int Part, int Parts,
    double Percent, long Downloaded, long Total, double Speed, double Eta);

public abstract record DownloadEvent;
public sealed record StartedEvent(int Parts, string? Title, string? ThumbnailUrl) : DownloadEvent;
public sealed record FileEvent(string Path) : DownloadEvent;
public sealed record ProgressEvent(DownloadProgress Progress) : DownloadEvent;

/// <summary>
/// Parses the machine-readable lines VidFlow asks yt-dlp to print (see <see cref="YtDlpArgs"/>):
/// <c>VFS|format|thumbnail|title</c> before downloading, <c>VFP|…</c> for progress, <c>VFF|path</c> at the end.
/// Stateful: counts finished streams so "video then audio" can be told apart.
/// </summary>
public sealed class YtDlpOutputParser
{
    int _parts = 1;
    int _finished;

    public DownloadEvent? Feed(string line)
    {
        if (line.StartsWith("VFS|", StringComparison.Ordinal))
        {
            var f = line.Split('|', 4);
            _parts = f[1].Count(c => c == '+') + 1;
            _finished = 0;
            return new StartedEvent(_parts, NullIfNa(f.ElementAtOrDefault(3)), NullIfNa(f.ElementAtOrDefault(2)));
        }
        if (line.StartsWith("VFF|", StringComparison.Ordinal))
            return new FileEvent(line[4..].Trim());
        if (!line.StartsWith("VFP|", StringComparison.Ordinal))
            return null;

        var p = line.Split('|');
        if (p.Length < 7) return null;
        if (p[1] == "finished")
        {
            _finished++;
            var last = _finished >= _parts;
            return new ProgressEvent(new DownloadProgress(last, !last, Math.Min(_finished, _parts), _parts, 100, 0, 0, 0, 0));
        }
        var downloaded = Long(p[2]);
        var total = Long(p[3]);
        if (total <= 0) total = Long(p[4]);
        var percent = total > 0 ? Math.Clamp(downloaded * 100.0 / total, 0, 100) : 0;
        return new ProgressEvent(new DownloadProgress(false, false, Math.Min(_finished + 1, _parts), _parts,
            percent, downloaded, total, Dbl(p[5]), Dbl(p[6])));
    }

    static string? NullIfNa(string? s) => string.IsNullOrWhiteSpace(s) || s == "NA" ? null : s;

    static long Long(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : 0;

    static double Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
