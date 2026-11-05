using System.ComponentModel;
using System.Runtime.CompilerServices;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Models;

public enum ConvertState { Waiting, Converting, Done, Failed, Canceled }

/// <summary>A format one file can become, as offered in its row's list.</summary>
public sealed record ConvertTarget(string Format, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One file on the convert page: what it is, what it becomes, and how that is going.</summary>
public sealed class ConvertItem : INotifyPropertyChanged
{
    public ConvertItem(string source)
    {
        Source = source;
        Kind = MediaConverter.KindOf(source) ?? MediaKind.Video;
        Targets = MediaConverter.TargetsFor(source).Select(t => new ConvertTarget(t, Label(t))).ToList();
        var preferred = MediaConverter.DefaultTarget(source);
        _targetIndex = Math.Max(0, Targets.ToList().FindIndex(t => t.Format == preferred));
        var size = new FileInfo(source).Length;
        Details = $"{Path.GetExtension(source).TrimStart('.').ToUpperInvariant()}  ·  {Format.Bytes(size)}";
        _status = L.Get("Convert_Waiting");
    }

    /// <summary>"MKV", "MP3 (audio only)" for a video's audio formats, "Markdown".</summary>
    string Label(string format) => Kind == MediaKind.Subtitle ? SubtitleFiles.ShortName(format)
        : Kind == MediaKind.Video && MediaConverter.IsAudioTarget(format) ? L.Format("Convert_AudioOnly", format.ToUpperInvariant())
        : format.ToUpperInvariant();

    public string Source { get; }
    public string Name => Path.GetFileName(Source);
    public MediaKind Kind { get; }
    public string Details { get; }
    public IReadOnlyList<ConvertTarget> Targets { get; }

    public string Glyph => Kind switch
    {
        MediaKind.Audio => "",
        MediaKind.Image => "",
        MediaKind.Subtitle => "",
        _ => "",
    };

    int _targetIndex;
    /// <summary>Bound to the row's list. A recycled row's list says -1 for a moment while it gets its new choices;
    /// that is not the user's choice and is ignored.</summary>
    public int TargetIndex
    {
        get => _targetIndex;
        set
        {
            if (value >= 0 && value < Targets.Count) Set(ref _targetIndex, value);
        }
    }
    public string Target => Targets[_targetIndex].Format;

    ConvertState _state;
    public ConvertState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            foreach (var name in new[] { nameof(IsWaiting), nameof(IsConverting), nameof(IsDone), nameof(IsFailed), nameof(CanCancel),
                         nameof(CanRemove), nameof(CanRetry) })
                OnPropertyChanged(name);
        }
    }

    public bool IsWaiting => State == ConvertState.Waiting;
    public bool IsConverting => State == ConvertState.Converting;
    public bool IsDone => State == ConvertState.Done;
    public bool IsFailed => State == ConvertState.Failed;
    public bool CanCancel => State is ConvertState.Waiting or ConvertState.Converting;
    public bool CanRemove => State != ConvertState.Converting;
    public bool CanRetry => State is ConvertState.Failed or ConvertState.Canceled;

    double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    string _status;
    public string StatusText { get => _status; set => Set(ref _status, value); }

    public string? OutputPath { get; set; }
    public string? ErrorDetail { get; set; }
    internal CancellationTokenSource? Cts { get; set; }

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
