using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>
/// Owns the download queue: runs up to <see cref="AppSettings.MaxConcurrent"/> jobs, persists the list,
/// supports pause/resume, and reports overall progress (taskbar, tray tooltip, notifications).
/// All public members are used from the UI thread.
/// </summary>
public sealed class DownloadManager
{
    readonly DispatcherQueue _dispatcher;
    readonly DispatcherQueueTimer _saveTimer;
    int _completedSinceIdle;
    string? _lastCompletedFolder;

    public DownloadManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
        Items.CollectionChanged += (_, _) => ScheduleSave();

        _stallTimer = dispatcher.CreateTimer();
        _stallTimer.Interval = TimeSpan.FromSeconds(30);
        _stallTimer.Tick += (_, _) => CheckStalled();
        _stallTimer.Start();
    }

    static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(3);
    readonly DispatcherQueueTimer _stallTimer;

    /// <summary>A running download with no output from yt-dlp for a while is flagged (status + one notification).</summary>
    void CheckStalled()
    {
        foreach (var item in Items.Where(i => i.State == DownloadState.Downloading && !i.StallReported
                                             && DateTime.Now - i.LastActivity > StallAfter))
        {
            item.StallReported = true;
            item.StatusText = L.Format("Status_Stalled", (int)StallAfter.TotalMinutes);
            Log.Warn($"Download stalled: {item.Request.Url}");
            if (AppSettings.Current.NotifyOnFailure && !App.MainWindow.IsForeground)
                NotificationService.DownloadStalled(item.Title);
        }
    }

    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>Raised when the number of active (queued or running) downloads changes.</summary>
    public event Action? ActiveCountChanged;

    public int ActiveCount => Items.Count(i => i.IsActive);

    public void LoadPersisted()
    {
        foreach (var record in DownloadStore.Load().OrderByDescending(r => r.CreatedAt))
        {
            // Re-create status text in the current language (the app language may have changed).
            if (DownloadLabels.RestingStatus(record) is { } status) record.StatusText = status;
            var item = new DownloadItem(record);
            Items.Add(item);
            _ = LoadThumbnailAsync(item);
        }
        ImageLoader.PruneCache();
        Changed();
    }

    /// <summary>Adds a download. Title/thumbnail may be provisional (batch links): yt-dlp fills them in.</summary>
    public void Enqueue(DownloadRequest request, string title, string? thumbnailUrl, int? partIndex = null,
        Microsoft.UI.Xaml.Media.ImageSource? thumbnail = null)
    {
        var item = new DownloadItem(new DownloadRecord
        {
            Request = request,
            Title = title,
            ThumbnailUrl = thumbnailUrl,
            PartIndex = partIndex,
        }, thumbnail);
        Items.Insert(0, item);
        if (thumbnail is null) _ = LoadThumbnailAsync(item);
        Changed();
    }

    public void Pause(DownloadItem item)
    {
        if (!item.CanPause) return;
        if (item.State == DownloadState.Queued)
        {
            SetPaused(item);
        }
        else
        {
            item.PauseRequested = true;
            item.Cts?.Cancel();
        }
        Changed();
    }

    public void Resume(DownloadItem item)
    {
        if (!item.CanResume) return;
        item.State = DownloadState.Queued;
        item.StatusText = L.Get("Status_Waiting");
        Changed();
    }

    public void PauseAll()
    {
        foreach (var item in Items.Where(i => i.CanPause).ToList()) Pause(item);
    }

    public void ResumeAll()
    {
        foreach (var item in Items.Where(i => i.CanResume).ToList()) Resume(item);
    }

    public void Cancel(DownloadItem item)
    {
        if (item.State is DownloadState.Queued or DownloadState.Paused)
        {
            item.State = DownloadState.Canceled;
            item.StatusText = L.Get("Status_Canceled");
        }
        else
        {
            item.Cts?.Cancel();
        }
        Changed();
    }

    public void Retry(DownloadItem item)
    {
        if (!item.CanRetry) return;
        item.Progress = 0;
        item.ErrorDetail = null;
        item.StatusText = L.Get("Status_Waiting");
        item.State = DownloadState.Queued;
        Changed();
    }

    public void Remove(DownloadItem item)
    {
        if (item.CanRemove) Items.Remove(item);
    }

    static bool IsFinished(DownloadItem i) => i.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;

    public int FinishedCount => Items.Count(IsFinished);

    public void ClearFinished()
    {
        foreach (var item in Items.Where(IsFinished).ToList())
            Items.Remove(item);
    }

    /// <summary>On exit: stop processes but keep running items as "queued" so they resume next launch.</summary>
    public void Shutdown()
    {
        foreach (var item in Items.Where(i => i.IsRunning))
        {
            item.PauseRequested = true; // not a user cancel
            item.Cts?.Cancel();
            item.Record.State = DownloadState.Queued;
        }
        SaveNow();
    }

    /// <summary>Call after any state change: saves, refreshes the badge/taskbar, and starts queued work.</summary>
    void Changed()
    {
        ScheduleSave();
        ActiveCountChanged?.Invoke();
        UpdateOverallProgress();
        Pump();
    }

    /// <summary>"Download next": this queued item starts as soon as a slot is free.</summary>
    public void Prioritize(DownloadItem item)
    {
        if (!item.CanPrioritize) return;
        item.Record.PriorityAt = DateTime.Now;
        item.StatusText = L.Get("Status_Next");
        Changed();
    }

    void Pump()
    {
        var running = Items.Count(i => i.IsRunning);
        // Prioritized items first (most recently prioritized first), then oldest first (the list is newest-on-top).
        foreach (var item in Items.Reverse().Where(i => i.State == DownloadState.Queued)
                     .OrderByDescending(i => i.Record.PriorityAt ?? DateTime.MinValue).ToList())
        {
            if (running >= Math.Max(1, AppSettings.Current.MaxConcurrent)) break;
            running++;
            _ = RunAsync(item);
        }

        if (ActiveCount == 0 && _completedSinceIdle > 0)
        {
            if (_completedSinceIdle > 1 && ShouldNotify())
                NotificationService.BatchCompleted(_completedSinceIdle, _lastCompletedFolder ?? AppSettings.Current.DownloadFolder);
            _completedSinceIdle = 0;
        }
    }

    async Task RunAsync(DownloadItem item)
    {
        item.State = DownloadState.Downloading;
        item.StatusText = L.Get("Status_Connecting");
        item.PauseRequested = false;
        item.LastActivity = DateTime.Now;
        item.StallReported = false;
        using var cts = new CancellationTokenSource();
        item.Cts = cts;
        Log.Info($"Download start: {item.Request.Url} ({item.Request.Mode}, {item.Request.MaxHeight})");
        try
        {
            var path = await Task.Run(() => YtDlpService.DownloadAsync(item.Request,
                e => _dispatcher.TryEnqueue(() => Apply(item, e)), cts.Token));
            item.FilePath = path;
            item.Progress = 100;
            item.State = DownloadState.Completed;
            item.StatusText = path is null ? L.Get("Status_Completed") : L.Format("Status_CompletedFile", Path.GetFileName(path));
            Log.Info($"Download done: {path}");
            OnCompleted(item);
        }
        catch (OperationCanceledException)
        {
            if (item.PauseRequested) SetPaused(item);
            else
            {
                item.State = DownloadState.Canceled;
                item.StatusText = L.Get("Status_Canceled");
            }
        }
        catch (Exception ex)
        {
            item.State = DownloadState.Failed;
            item.StatusText = ex.Message.Replace('\n', ' ');
            item.ErrorDetail = ex is YtDlpException { Detail: { } d } ? d : ex.ToString();
            Log.Warn($"Download failed: {item.Request.Url}: {ex.Message}");
            if (AppSettings.Current.NotifyOnFailure && !App.MainWindow.IsForeground)
                NotificationService.DownloadFailed(item.Title, ex.Message);
        }
        finally
        {
            item.Cts = null;
            Changed();
        }
    }

    void OnCompleted(DownloadItem item)
    {
        _completedSinceIdle++;
        _lastCompletedFolder = item.Request.OutputFolder;
        // A single download gets its own notification; batches get one summary when the queue empties.
        var othersActive = Items.Any(i => i != item && i.IsActive);
        if (!othersActive && _completedSinceIdle == 1 && ShouldNotify())
            NotificationService.DownloadCompleted(item.Title, item.FilePath, item.Request.OutputFolder);
    }

    static bool ShouldNotify() => AppSettings.Current.NotifyOnFinish && !App.MainWindow.IsForeground;

    static void SetPaused(DownloadItem item)
    {
        item.State = DownloadState.Paused;
        item.StatusText = item.Progress > 0 ? L.Format("Status_PausedAt", item.Progress.ToString("0.0")) : L.Get("Status_Paused");
    }

    void Apply(DownloadItem item, DownloadEvent e)
    {
        if (!item.IsRunning) return;
        item.LastActivity = DateTime.Now;
        item.StallReported = false;
        switch (e)
        {
            case StartedEvent s:
                // Batch links start with the URL as title; take the real one from yt-dlp.
                if (s.Title is not null && item.Title != s.Title) item.Title = s.Title;
                if (item.Record.ThumbnailUrl is null && s.ThumbnailUrl is not null)
                {
                    item.Record.ThumbnailUrl = s.ThumbnailUrl;
                    _ = LoadThumbnailAsync(item);
                }
                ScheduleSave();
                break;
            case ProgressEvent { Progress: var p }:
                ApplyProgress(item, p);
                break;
        }
    }

    void ApplyProgress(DownloadItem item, DownloadProgress p)
    {
        if (p.Processing)
        {
            item.State = DownloadState.Processing;
            item.StatusText = item.Request.Mode == "video" ? L.Get("Status_Merging") : L.Get("Status_Converting");
            UpdateOverallProgress();
            return;
        }

        item.State = DownloadState.Downloading;
        // One percentage for the whole download: the bar keeps going from video into audio instead of restarting.
        item.Progress = p.Percent;
        if (p.StreamFinished)
        {
            item.StatusText = L.Get("Status_VideoDoneAudioNext");
            UpdateOverallProgress();
            return;
        }

        var parts = new List<string>();
        if (p.Parts > 1) parts.Add(p.Part == 1 ? L.Get("Status_VideoStream") : L.Get("Status_AudioStream"));
        parts.Add($"{p.Percent:0.0}%");
        if (p.Total > 0) parts.Add($"{Format.Bytes(p.Downloaded)} / {Format.Bytes(p.Total)}");
        if (p.Speed > 0) parts.Add($"{Format.Bytes(p.Speed)}/s");
        if (p.Eta > 0) parts.Add(L.Format("Status_Remaining", Format.Duration(p.Eta)));
        item.StatusText = string.Join("  ·  ", parts);
        UpdateOverallProgress();
    }

    /// <summary>Taskbar button progress and tray tooltip reflect all active downloads together.</summary>
    void UpdateOverallProgress()
    {
        var active = Items.Where(i => i.IsActive).ToList();
        var hwnd = App.MainWindow?.Hwnd ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        if (active.Count == 0)
        {
            TaskbarProgress.Set(hwnd, Items.Any(i => i.IsFailed && i.Record.CreatedAt > DateTime.Now.AddMinutes(-1))
                ? TaskbarProgress.State.Error : TaskbarProgress.State.None, 100);
            TrayIcon.SetToolTip("VidFlow");
            return;
        }
        var percent = active.Average(i => i.Progress);
        TaskbarProgress.Set(hwnd, active.All(i => i.State == DownloadState.Queued)
            ? TaskbarProgress.State.Indeterminate : TaskbarProgress.State.Normal, percent);
        TrayIcon.SetToolTip(L.Format("Tray_Tooltip", active.Count, percent.ToString("0")));
    }

    async Task LoadThumbnailAsync(DownloadItem item)
    {
        if (item.Record.ThumbnailUrl is { } url) item.Thumbnail = await ImageLoader.LoadAsync(url);
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow() => DownloadStore.Save(Items.Select(i => i.Record));
}
