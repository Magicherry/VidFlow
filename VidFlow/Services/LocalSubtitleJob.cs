using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>One local subtitle job, kept alive across page navigation.</summary>
public sealed class LocalSubtitleJob
{
    public string? Source { get; private set; }
    public LocalSubtitleInfo? Info { get; private set; }
    public string? OutputPath { get; private set; }
    public string? ErrorDetail { get; private set; }
    public string Status { get; private set; } = "";
    public double? Progress { get; private set; }
    public bool Busy { get; private set; }
    public bool Failed { get; private set; }
    public event Action? Changed;
    CancellationTokenSource? _cts;

    public async Task InspectAsync(string source)
    {
        if (Busy) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        Source = source;
        Info = null;
        OutputPath = ErrorDetail = null;
        Failed = false;
        Busy = true;
        Progress = null;
        Status = L.Get("LocalSub_Inspecting");
        Changed?.Invoke();
        try
        {
            if (App.ToolUsage.IsUpdating) { Status = L.Get("Status_WaitingTools"); Changed?.Invoke(); }
            using var tools = await App.ToolUsage.UseAsync(cts.Token);
            var info = await LocalSubtitles.InspectAsync(source, cts.Token);
            if (!info.HasVideo) throw new YtDlpException(L.Get("Error_ConvertNoVideo"));
            Info = info;
            Status = L.Format("LocalSub_Detected", info.Tracks.Count);
        }
        catch (OperationCanceledException) { Status = L.Get("Convert_Canceled"); }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            _cts = null;
            Busy = false;
            Changed?.Invoke();
        }
    }

    public async Task StartAsync(LocalSubtitleRequest request)
    {
        if (Busy || Info is null || Source != request.Source) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        Busy = true;
        Failed = false;
        OutputPath = ErrorDetail = null;
        Status = L.Get("LocalSub_Inspecting");
        Progress = null;
        Changed?.Invoke();
        try
        {
            if (App.ToolUsage.IsUpdating) { Status = L.Get("Status_WaitingTools"); Changed?.Invoke(); }
            using var tools = await App.ToolUsage.UseAsync(cts.Token);
            var output = await Task.Run(() => LocalSubtitles.ProcessAsync(request, p => App.UiQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_cts, cts) || !Busy) return;
                Progress = p.Percent;
                Status = L.Get(p.MessageKey) + (p.Percent is { } percent ? $" · {percent:0}%" : "");
                Changed?.Invoke();
            }), cts.Token));
            OutputPath = output;
            Progress = 100;
            Status = L.Format("Convert_Done", Path.GetFileName(output));
            Log.Info($"Local subtitles saved: {output}");
        }
        catch (OperationCanceledException) { Status = L.Get("Convert_Canceled"); }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            _cts = null;
            Busy = false;
            Changed?.Invoke();
        }
    }

    void Fail(Exception ex)
    {
        Failed = true;
        Status = ex.Message.Replace('\n', ' ');
        ErrorDetail = ex is YtDlpException { Detail: { } detail } ? detail : ex.ToString();
        Log.Warn($"Local subtitles failed: {ex.Message}");
    }

    public void Cancel() => _cts?.Cancel();
}
