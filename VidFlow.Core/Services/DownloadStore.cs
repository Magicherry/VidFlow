using System.Text.Json;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Reads and writes the download list (downloads.json).</summary>
public static class DownloadStore
{
    static string FilePath => Path.Combine(AppSettings.DataDir, "downloads.json");

    public static List<DownloadRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var records = JsonSerializer.Deserialize(File.ReadAllText(FilePath), VidFlowJson.Default.ListDownloadRecord) ?? [];
            // Anything that was running when the app closed is resumed from where it stopped.
            foreach (var r in records.Where(r => r.State is DownloadState.Downloading or DownloadState.Processing))
                r.State = DownloadState.Queued;
            return records;
        }
        catch (Exception ex)
        {
            Log.Warn($"Download list could not be read: {ex.Message}");
            return [];
        }
    }

    public static void Save(IEnumerable<DownloadRecord> records)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records.ToList(), VidFlowJson.Default.ListDownloadRecord));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Download list could not be saved: {ex.Message}");
        }
    }
}
