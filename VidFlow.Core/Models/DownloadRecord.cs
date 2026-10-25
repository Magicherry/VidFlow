namespace VidFlow.Models;

public enum DownloadState { Queued, Downloading, Processing, Completed, Failed, Canceled, Paused }

/// <summary>Persisted form of a download, so the list and unfinished work survive restarts.</summary>
public sealed class DownloadRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required DownloadRequest Request { get; set; }
    public required string Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    /// <summary>Position in a playlist / multi-part video, shown as "P3".</summary>
    public int? PartIndex { get; set; }
    public DownloadState State { get; set; }
    public string? FilePath { get; set; }
    public string? StatusText { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Set by "download next": queued items with a later value start first.</summary>
    public DateTime? PriorityAt { get; set; }
}
