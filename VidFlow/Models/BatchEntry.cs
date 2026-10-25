using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Models;

/// <summary>One link of a multi-link paste: parsed in the background, then shown with a checkbox.</summary>
public sealed class BatchEntry(string url) : INotifyPropertyChanged
{
    public string Url { get; } = url;

    string _title = url;
    public string Title { get => _title; private set => Set(ref _title, value); }

    string _meta = L.Get("Batch_Parsing");
    public string Meta { get => _meta; private set => Set(ref _meta, value); }

    ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

    bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    bool _failed;
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }

    /// <summary>Set once parsed: a single video or a playlist / multi-part video.</summary>
    public MediaInfo? Info { get; private set; }

    /// <summary>How many downloads this row adds (a playlist adds all of its entries).</summary>
    public int TaskCount => Info is PlaylistInfo p ? p.Entries.Count : 1;

    public void SetResult(MediaInfo info)
    {
        Info = info;
        Title = info.Title;
        var parts = new List<string> { info.Platform.DisplayName() };
        if (info is PlaylistInfo p) parts.Add(L.Format("Batch_PlaylistMeta", p.Entries.Count));
        else if (info is VideoInfo { Duration: > 0 } v) parts.Add(Format.Duration(v.Duration!.Value));
        if (info.Uploader is { Length: > 0 } uploader) parts.Add(uploader);
        Meta = string.Join("  ·  ", parts);
        IsLoading = false;
    }

    public void SetError(string message)
    {
        Failed = true;
        Meta = message.Split('\n')[0];
        IsLoading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
