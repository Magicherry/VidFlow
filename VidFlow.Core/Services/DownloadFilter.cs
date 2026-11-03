using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>The status groups the download list can be narrowed to.</summary>
public enum DownloadStatusFilter { All, Active, Completed, Failed }

/// <summary>What a download saves.</summary>
public enum DownloadKind { Video, Audio, Cover, Photos, Subtitles }

/// <summary>What the download list shows: a status group, one platform, one kind, and text found in the title or link.</summary>
public sealed record DownloadFilter(DownloadStatusFilter Status = DownloadStatusFilter.All, Platform? Platform = null, string Query = "",
    DownloadKind? Kind = null)
{
    public static DownloadKind KindOf(DownloadRequest r) =>
        r.IsPhotos ? DownloadKind.Photos : r.IsSubtitles ? DownloadKind.Subtitles : r.IsCover ? DownloadKind.Cover : r.IsAudio ? DownloadKind.Audio : DownloadKind.Video;

    /// <summary>Paused counts as in progress (it continues later); canceled sits with failed (neither finished).</summary>
    public static DownloadStatusFilter GroupOf(DownloadState state) => state switch
    {
        DownloadState.Completed => DownloadStatusFilter.Completed,
        DownloadState.Failed or DownloadState.Canceled => DownloadStatusFilter.Failed,
        _ => DownloadStatusFilter.Active,
    };

    /// <summary>Everything but the status: what the counts on the status choices are taken over.</summary>
    public bool MatchesExceptStatus(DownloadRecord r)
    {
        if (Kind is { } kind && KindOf(r.Request) != kind) return false;
        if (Platform is { } p && UrlTools.DetectPlatform(r.Request.Url) != p) return false;
        var q = Query.Trim();
        return q.Length == 0
            || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Request.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(DownloadRecord r) =>
        (Status == DownloadStatusFilter.All || GroupOf(r.State) == Status) && MatchesExceptStatus(r);

    /// <summary>Makes <paramref name="shown"/> the same as <paramref name="wanted"/> (same items, same order) with as few
    /// changes as possible, so rows that stay aren't rebuilt and the list doesn't jump.</summary>
    public static void Sync<T>(IList<T> shown, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = shown.Count - 1; i >= 0; i--)
            if (!keep.Contains(shown[i])) shown.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < shown.Count && ReferenceEquals(shown[i], wanted[i])) continue;
            var at = shown.IndexOf(wanted[i]);
            if (at >= 0) shown.RemoveAt(at);
            shown.Insert(i, wanted[i]);
        }
    }
}
