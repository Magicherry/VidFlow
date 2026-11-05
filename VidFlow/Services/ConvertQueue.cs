using System.Collections.ObjectModel;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>
/// The files on the convert page, converted one at a time (ffmpeg already uses every core). It lives with the app,
/// so conversions carry on when the page is left. Not saved: a list of local files to convert is set up again in
/// moments, unlike a download list. All members are used from the UI thread.
/// </summary>
public sealed class ConvertQueue
{
    public ObservableCollection<ConvertItem> Items { get; } = [];

    bool _running;

    public ConvertQueue()
    {
        Items.CollectionChanged += (_, e) =>
        {
            foreach (ConvertItem item in e.NewItems ?? Array.Empty<ConvertItem>())
                item.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(ConvertItem.State)) ActiveCountChanged?.Invoke();
                };
            ActiveCountChanged?.Invoke();
        };
    }

    /// <summary>Files still to go in the run under way (converting or waiting), for the badge in the pane; 0 while
    /// nothing runs, as files merely added are not under way yet.</summary>
    public int ActiveCount => _running ? Items.Count(i => i.IsWaiting || i.IsConverting) : 0;

    public event Action? ActiveCountChanged;

    /// <summary>Adds the files that can be converted (ones already waiting are skipped); returns how many could not.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var skipped = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || MediaConverter.KindOf(path) is null || MediaConverter.TargetsFor(path).Count == 0)
            {
                skipped++;
                continue;
            }
            if (Items.Any(i => (i.IsWaiting || i.IsConverting) && string.Equals(i.Source, path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new ConvertItem(path));
        }
        return skipped;
    }

    public bool AnyWaiting => Items.Any(i => i.IsWaiting);

    /// <summary>Converts everything waiting, in order, into its own folder or <see cref="AppSettings.ConvertFolder"/>.</summary>
    public async Task StartAsync()
    {
        if (_running) return;
        _running = true;
        ActiveCountChanged?.Invoke();
        try
        {
            while (Items.FirstOrDefault(i => i.IsWaiting) is { } item)
                await RunAsync(item);
        }
        finally
        {
            _running = false;
            ActiveCountChanged?.Invoke();
        }
    }

    public bool Running => _running;

    async Task RunAsync(ConvertItem item)
    {
        var folder = AppSettings.Current.ConvertFolder is { Length: > 0 } f ? f : null;
        var dest = MediaConverter.OutputPath(item.Source, item.Target, folder);
        using var cts = new CancellationTokenSource();
        item.Cts = cts;
        _current = dest;
        item.State = ConvertState.Converting;
        item.Progress = 0;
        item.StatusText = L.Format("Convert_Converting", "0");
        var dispatcher = App.UiQueue;
        try
        {
            await Task.Run(() => MediaConverter.ConvertAsync(item.Source, dest, item.Target, p => dispatcher.TryEnqueue(() =>
            {
                if (!item.IsConverting) return;
                item.Progress = p;
                item.StatusText = L.Format("Convert_Converting", p.ToString("0"));
            }), cts.Token));
            item.OutputPath = dest;
            item.Progress = 100;
            item.State = ConvertState.Done;
            item.StatusText = L.Format("Convert_Done", Path.GetFileName(dest));
            Log.Info($"Converted: {dest}");
        }
        catch (OperationCanceledException)
        {
            item.State = ConvertState.Canceled;
            item.StatusText = L.Get("Convert_Canceled");
        }
        catch (Exception ex)
        {
            item.State = ConvertState.Failed;
            item.StatusText = ex.Message.Replace('\n', ' ');
            item.ErrorDetail = ex is YtDlpException { Detail: { } d } ? d : ex.ToString();
            Log.Warn($"Conversion failed: {item.Source}: {ex.Message}");
        }
        finally
        {
            item.Cts = null;
            _current = null;
        }
    }

    /// <summary>The file being written, while a conversion runs.</summary>
    string? _current;

    /// <summary>On exit: ffmpeg is stopped and its unfinished file removed (the app won't be around to do it later).</summary>
    public void Shutdown()
    {
        if (_current is not { } dest) return;
        foreach (var item in Items.Where(i => i.IsConverting)) item.Cts?.Cancel();
        // ffmpeg was killed just now; Windows lets go of its file a moment later.
        var part = MediaConverter.PartPath(dest);
        for (var tries = 0; tries < 20 && File.Exists(part); tries++)
        {
            try { File.Delete(part); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    public void Cancel(ConvertItem item)
    {
        if (item.IsWaiting)
        {
            item.State = ConvertState.Canceled;
            item.StatusText = L.Get("Convert_Canceled");
        }
        else item.Cts?.Cancel();
    }

    public void Retry(ConvertItem item)
    {
        if (!item.CanRetry) return;
        item.Progress = 0;
        item.ErrorDetail = null;
        item.State = ConvertState.Waiting;
        item.StatusText = L.Get("Convert_Waiting");
    }

    public void Remove(ConvertItem item)
    {
        if (item.CanRemove) Items.Remove(item);
    }

    /// <summary>Done, failed and canceled ones go; waiting and converting ones stay.</summary>
    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.State is ConvertState.Done or ConvertState.Failed or ConvertState.Canceled).ToList())
            Items.Remove(item);
    }
}
