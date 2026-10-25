using System.Text.Json;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Built-in download presets plus the user's templates (templates.json), and which one is the default.</summary>
public static class TemplateStore
{
    static string FilePath => Path.Combine(AppSettings.DataDir, "templates.json");
    static List<DownloadTemplate>? _custom;

    /// <summary>Fixed ids so "default template" survives restarts and language changes.</summary>
    public static readonly IReadOnlyList<DownloadTemplate> BuiltIns =
    [
        new() { Id = new("6f1d6c2e-0001-4000-8000-000000000001"), NameKey = "Preset_Best" },
        new() { Id = new("6f1d6c2e-0002-4000-8000-000000000002"), NameKey = "Preset_HD", MaxHeight = 1080 },
        new() { Id = new("6f1d6c2e-0003-4000-8000-000000000003"), NameKey = "Preset_Compatible", MaxHeight = 1080, Codec = "h264", AudioFormat = "aac" },
        new() { Id = new("6f1d6c2e-0004-4000-8000-000000000004"), NameKey = "Preset_SaveSpace", MaxHeight = 720, Codec = "h265", AudioQuality = 128 },
        new() { Id = new("6f1d6c2e-0005-4000-8000-000000000005"), NameKey = "Preset_AudioOnly", Mode = "audio", AudioFormat = "mp3" },
    ];

    public static IReadOnlyList<DownloadTemplate> Custom => _custom ??= Load();

    public static IEnumerable<DownloadTemplate> All => BuiltIns.Concat(Custom);

    public static event Action? Changed;

    public static DownloadTemplate? Find(Guid id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>The template new downloads start from (falls back to "Best quality").</summary>
    public static DownloadTemplate Default => Find(AppSettings.Current.DefaultTemplateId) ?? BuiltIns[0];

    public static void SetDefault(DownloadTemplate t)
    {
        AppSettings.Current.DefaultTemplateId = t.Id;
        AppSettings.Save();
        Changed?.Invoke();
    }

    /// <summary>Adds a new user template (or replaces the one with the same id).</summary>
    public static DownloadTemplate Save(DownloadTemplate t)
    {
        if (t.IsBuiltIn) throw new InvalidOperationException("Built-in presets are read-only.");
        var list = (List<DownloadTemplate>)Custom;
        var i = list.FindIndex(x => x.Id == t.Id);
        if (i >= 0) list[i] = t; else list.Add(t);
        Persist();
        return t;
    }

    public static DownloadTemplate Duplicate(DownloadTemplate source, string name)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.NameKey = null;
        copy.Name = name;
        return Save(copy);
    }

    public static void Rename(DownloadTemplate t, string name)
    {
        if (t.IsBuiltIn) return;
        t.Name = name;
        Persist();
    }

    public static void Delete(DownloadTemplate t)
    {
        if (t.IsBuiltIn) return;
        ((List<DownloadTemplate>)Custom).RemoveAll(x => x.Id == t.Id);
        if (AppSettings.Current.DefaultTemplateId == t.Id)
        {
            AppSettings.Current.DefaultTemplateId = BuiltIns[0].Id;
            AppSettings.Save();
        }
        Persist();
    }

    /// <summary>"Name", "Name (2)", ... avoiding names already used.</summary>
    public static string UniqueName(string baseName)
    {
        var names = All.Select(t => t.DisplayName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
            if (!names.Contains($"{baseName} ({n})")) return $"{baseName} ({n})";
    }

    static List<DownloadTemplate> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), VidFlowJson.Default.ListDownloadTemplate) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Templates could not be read: {ex.Message}");
        }
        return [];
    }

    static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize((List<DownloadTemplate>)Custom, VidFlowJson.Default.ListDownloadTemplate));
        }
        catch (Exception ex)
        {
            Log.Warn($"Templates could not be saved: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>For tests: forget the cached list so the next access reloads from disk.</summary>
    internal static void ResetCache() => _custom = null;
}
