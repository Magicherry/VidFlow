using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Models;

/// <summary>View model for one row of the download list. Backed by a persisted <see cref="DownloadRecord"/>.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public DownloadItem(DownloadRecord record, ImageSource? thumbnail = null)
    {
        Record = record;
        _thumbnail = thumbnail;
        _progress = record.State == DownloadState.Completed ? 100 : 0;
        record.StatusText ??= L.Get("Status_Waiting");
    }

    public DownloadRecord Record { get; }
    public DownloadRequest Request => Record.Request;
    public string Label => DownloadLabels.Label(Record.Request, Record.PartIndex);
    internal CancellationTokenSource? Cts { get; set; }

    /// <summary>Set when the running process is being stopped for a pause rather than a cancel.</summary>
    internal bool PauseRequested { get; set; }

    /// <summary>Last time yt-dlp reported anything; used to detect stalled downloads.</summary>
    internal DateTime LastActivity { get; set; } = DateTime.Now;
    internal bool StallReported { get; set; }

    public string Title
    {
        get => Record.Title;
        set { if (Record.Title != value) { Record.Title = value; OnPropertyChanged(); } }
    }

    ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

    double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string StatusText
    {
        get => Record.StatusText ?? "";
        set { if (Record.StatusText != value) { Record.StatusText = value; OnPropertyChanged(); } }
    }

    public string? FilePath { get => Record.FilePath; set => Record.FilePath = value; }
    public string? ErrorDetail { get => Record.ErrorDetail; set => Record.ErrorDetail = value; }

    public DownloadState State
    {
        get => Record.State;
        set
        {
            if (Record.State == value) return;
            Record.State = value;
            OnPropertyChanged();
            foreach (var name in new[] { nameof(IsActive), nameof(IsRunning), nameof(IsIndeterminate), nameof(IsCompleted),
                         nameof(IsFailed), nameof(IsPausedOrCanceled), nameof(CanPause), nameof(CanResume),
                         nameof(CanRetry), nameof(CanRemove), nameof(CanCancel), nameof(CanPrioritize), nameof(ShowProgress) })
                OnPropertyChanged(name);
        }
    }

    /// <summary>Queued or running: counts toward the badge and blocks removal.</summary>
    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Processing;
    public bool IsRunning => State is DownloadState.Downloading or DownloadState.Processing;
    public bool IsIndeterminate => State is DownloadState.Queued or DownloadState.Processing;
    public bool IsCompleted => State == DownloadState.Completed;
    public bool IsFailed => State == DownloadState.Failed;
    public bool IsPausedOrCanceled => State is DownloadState.Canceled or DownloadState.Paused;
    public bool CanPause => State is DownloadState.Queued or DownloadState.Downloading;
    public bool CanResume => State == DownloadState.Paused;
    public bool CanRetry => State is DownloadState.Failed or DownloadState.Canceled;
    public bool CanRemove => !IsActive;
    public bool CanCancel => IsActive || State == DownloadState.Paused;
    public bool CanPrioritize => State == DownloadState.Queued;
    public bool ShowProgress => State != DownloadState.Completed;

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
