using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;

namespace VidFlow.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Drives app updates on the UI thread: checks at startup and every 6 hours (unless the mode is manual), downloads
/// automatically in "auto" mode, and installs on the user's confirmation. See <see cref="UpdateService"/>.
/// </summary>
public sealed class UpdateController
{
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    readonly DispatcherQueue _dispatcher;
    readonly DispatcherQueueTimer _timer;
    string? _notifiedVersion;
    bool _busy;

    public UpdateController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (AppSettings.Current.UpdateMode != "manual") await CheckAsync(userInitiated: false);
        };
    }

    public static Version Current { get; } = typeof(App).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateManifest? Latest { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public (UpdateOutcome Outcome, string? Version) LastResult { get; private set; }

    /// <summary>A newer version than the running one is known (whether or not it has been downloaded yet).</summary>
    public bool HasNewer => Latest is { } m && UpdateService.IsNewer(m, Current);

    /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
    public event Action? Changed;

    public void Start()
    {
        LastResult = UpdateService.CheckPendingResult(Current);
        if (LastResult.Outcome != UpdateOutcome.None)
            NotificationService.UpdateResult(LastResult.Outcome == UpdateOutcome.Succeeded, LastResult.Version!);

        // First check shortly after startup (not during it), then periodically.
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Start();
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (_busy) return;
        _busy = true;
        Set(UpdateState.Checking);
        try
        {
            var manifest = await Task.Run(() => UpdateService.FetchManifestAsync());
            AppSettings.Current.LastUpdateCheck = DateTime.Now;
            AppSettings.Save();
            if (manifest is null || !UpdateService.IsNewer(manifest, Current))
            {
                Latest = manifest;
                Set(UpdateState.UpToDate);
                return;
            }
            Latest = manifest;
            Log.Info($"Update available: {manifest.Version}");
            if (AppSettings.Current.UpdateMode == "auto")
            {
                _busy = false;
                await DownloadAsync();
                return;
            }
            Set(UpdateState.Available);
            if (!userInitiated && _notifiedVersion != manifest.Version)
            {
                _notifiedVersion = manifest.Version;
                NotificationService.UpdateAvailable(manifest.Version);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            Error = Friendly(ex);
            Set(UpdateState.Failed);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task DownloadAsync()
    {
        if (_busy || Latest is null) return;
        _busy = true;
        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            var manifest = Latest;
            await Task.Run(() => UpdateService.DownloadAsync(manifest, progress, CancellationToken.None));
            Set(UpdateState.Ready);
            if (_notifiedVersion != manifest.Version)
            {
                _notifiedVersion = manifest.Version;
                NotificationService.UpdateReady(manifest.Version);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update download failed: {ex.Message}");
            Error = Friendly(ex);
            Set(UpdateState.Failed);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Asks for confirmation (mentioning running downloads), then runs the installer and exits.
    /// Unfinished downloads resume after the update.</summary>
    public async Task InstallAsync(XamlRoot root)
    {
        if (Latest is null) return;
        var installer = await Task.Run(() => UpdateService.DownloadAsync(Latest, null, CancellationToken.None));

        var active = App.Downloads.ActiveCount;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.Format("Update_InstallTitle", Latest.Version),
            Content = active > 0 ? L.Format("Update_InstallBodyActive", active) : L.Get("Update_InstallBody"),
            PrimaryButtonText = L.Get("Update_InstallNow"),
            CloseButtonText = L.Get("Update_Later"),
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        UpdateService.LaunchInstaller(installer, Latest);
        App.QuitForUpdate();
    }

    /// <summary>No raw HTTP messages in the UI.</summary>
    static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => L.Get("Update_NoManifest"),
        HttpRequestException or TaskCanceledException => L.Get("Update_Network"),
        System.Text.Json.JsonException => L.Get("Update_BadManifest"),
        _ => ex.Message,
    };

    void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        if (_dispatcher.HasThreadAccess) Changed?.Invoke();
        else _dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
}
