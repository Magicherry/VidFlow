using System.Text.Json;
using System.Text.Json.Serialization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>User preferences, persisted as JSON under %LocalAppData%\VidFlow.</summary>
public sealed class AppSettings
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VidFlow");

    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VidFlow");

    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>"" follows Windows; otherwise "zh" or "en". Applied on next start.</summary>
    public string Language { get; set; } = "";

    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Per-download speed limit in KB/s; 0 means unlimited.</summary>
    public int SpeedLimitKBps { get; set; }

    /// <summary>video, mp3 or m4a.</summary>
    public string DefaultMode { get; set; } = "video";

    /// <summary>Maximum video height; 0 means best available.</summary>
    public int DefaultHeight { get; set; }

    public bool PreferH264 { get; set; }
    public bool EmbedMetadata { get; set; } = true;
    public bool WriteSubtitles { get; set; }

    /// <summary>yt-dlp --sub-langs value.</summary>
    public string SubtitleLanguages { get; set; } = "zh-Hans,zh-Hant,zh-CN,zh-TW,zh,ai-zh,en";

    /// <summary>Also save Bilibili danmaku (bullet comments) as XML when subtitles are on.</summary>
    public bool SaveDanmaku { get; set; }

    /// <summary>Browser to read cookies from (edge, chrome, firefox…); empty to disable.</summary>
    public string CookiesBrowser { get; set; } = "";
    public string CookiesFile { get; set; } = "";
    public string Proxy { get; set; } = "";

    public bool WatchClipboard { get; set; }
    public bool NotifyOnFinish { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoUpdateYtDlp { get; set; } = true;
    public DateTime LastYtDlpUpdateCheck { get; set; }
    public bool FirstRunCompleted { get; set; }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize(json, VidFlowJson.Default.AppSettings) ?? new();
                // Settings from 1.0 (Chinese-only, no Language field): keep those users on Chinese
                // rather than switching them to their Windows display language on upgrade.
                if (!json.Contains("\"Language\"", StringComparison.Ordinal)) Current.Language = "zh";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read, using defaults: {ex.Message}");
            Current = new();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, VidFlowJson.Default.AppSettings));
        }
        catch (Exception ex)
        {
            // Settings are a convenience; failing to persist them must not crash the app.
            Log.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}

/// <summary>Source-generated serializers (no reflection, so the app can be trimmed).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<DownloadRecord>))]
internal sealed partial class VidFlowJson : JsonSerializerContext;
