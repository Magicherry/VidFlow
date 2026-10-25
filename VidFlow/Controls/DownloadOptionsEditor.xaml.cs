using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Controls;

/// <summary>
/// Edits download options in place on a <see cref="DownloadTemplate"/>. Given a parsed video, the quality, codec
/// and audio-track lists show only what that video offers; otherwise generic choices.
/// </summary>
public sealed partial class DownloadOptionsEditor : UserControl
{
    static readonly int[] GenericHeights = [2160, 1440, 1080, 720, 480, 360];
    static readonly int[] AudioQualities = [0, 320, 256, 192, 128, 96];

    DownloadTemplate _options = new();
    VideoInfo? _video;
    bool _loading;

    public DownloadOptionsEditor()
    {
        InitializeComponent();
        AddItems(ModeCombo, ("video", L.Get("Mode_Video")), ("audio", L.Get("Mode_Audio")));
        AddItems(ContainerCombo, ("mp4", L.Get("Container_Mp4")), ("mkv", L.Get("Container_Mkv")), ("webm", L.Get("Container_Webm")));
        foreach (var q in AudioQualities)
            AudioQualityCombo.Items.Add(new ComboBoxItem { Content = q == 0 ? L.Get("AudioQuality_Best") : $"{q} kbps", Tag = q });
        AddItems(FolderCombo, ("", L.Get("Folder_None")), ("{platform}", L.Get("Folder_Platform")), ("{uploader}", L.Get("Folder_Uploader")),
            ("{year}-{month}", L.Get("Folder_Month")), ("{platform}/{uploader}", L.Get("Folder_PlatformUploader")));
    }

    /// <summary>Raised whenever the user changes an option.</summary>
    public event Action? Changed;

    /// <summary>The options being edited (the same instance passed to <see cref="Load"/>).</summary>
    public DownloadTemplate Options => _options;

    /// <summary>Show the file naming / folder rules (template editor) or not (home page).</summary>
    public bool ShowNaming
    {
        get => NamingExpander.Visibility == Visibility.Visible;
        set => NamingExpander.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Load(DownloadTemplate options, VideoInfo? video = null)
    {
        _options = options;
        _video = video;
        _loading = true;

        Select(ModeCombo, options.Mode == "audio" ? "audio" : "video");
        FillQualities();
        FillCodecs();
        Select(ContainerCombo, options.Container);
        FillAudioFormats();
        Select(AudioQualityCombo, options.AudioQuality);
        FillAudioTracks();
        SubsToggle.IsOn = options.Subtitles;
        RecodeToggle.IsOn = options.Recode;
        FileNameBox.Text = options.FileNameTemplate;
        if (!NamingTemplate.FolderPresets.Contains(options.FolderTemplate))
            FolderCombo.Items.Add(new ComboBoxItem { Content = options.FolderTemplate, Tag = options.FolderTemplate });
        Select(FolderCombo, options.FolderTemplate);

        _loading = false;
        Refresh();
    }

    void FillQualities()
    {
        QualityCombo.Items.Clear();
        if (_video is { Qualities.Count: > 0 } v)
        {
            var best = v.Qualities[0];
            QualityCombo.Items.Add(new ComboBoxItem { Content = $"{L.Get("Quality_Best")}  ·  {Format.Quality(best)}", Tag = 0 });
            foreach (var q in v.Qualities)
                QualityCombo.Items.Add(new ComboBoxItem { Content = Format.Quality(q), Tag = q.Height });
        }
        else
        {
            QualityCombo.Items.Add(new ComboBoxItem { Content = L.Get("Quality_Best"), Tag = 0 });
            foreach (var h in GenericHeights)
                QualityCombo.Items.Add(new ComboBoxItem { Content = Format.Height(h), Tag = h });
        }
        // Closest choice not above the wanted height (a 1080p template on a 720p video shows 720p).
        var items = QualityCombo.Items.Cast<ComboBoxItem>().ToList();
        QualityCombo.SelectedIndex = _options.MaxHeight == 0 ? 0
            : Math.Max(0, items.FindIndex(i => (int)i.Tag > 0 && (int)i.Tag <= _options.MaxHeight));
    }

    void FillCodecs()
    {
        CodecCombo.Items.Clear();
        CodecCombo.Items.Add(new ComboBoxItem { Content = VideoCodecs.DisplayName(VideoCodecs.Auto), Tag = VideoCodecs.Auto });
        foreach (var codec in VideoCodecs.All)
        {
            if (_video is null)
                CodecCombo.Items.Add(new ComboBoxItem { Content = VideoCodecs.DisplayName(codec), Tag = codec });
            else if (_video.Codecs.TryGetValue(codec, out var max))
                CodecCombo.Items.Add(new ComboBoxItem { Content = L.Format("Codec_WithMax", VideoCodecs.DisplayName(codec), max), Tag = codec });
        }
        // A codec this video doesn't offer: keep it visible so it's clear the download will fall back.
        if (_options.Codec != VideoCodecs.Auto && !CodecCombo.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == _options.Codec))
            CodecCombo.Items.Add(new ComboBoxItem { Content = L.Format("Codec_Unavailable", VideoCodecs.DisplayName(_options.Codec)), Tag = _options.Codec });
        Select(CodecCombo, _options.Codec);
    }

    void FillAudioFormats()
    {
        AudioFormatCombo.Items.Clear();
        if (_options.Mode == "audio")
        {
            AddItems(AudioFormatCombo, ("mp3", "MP3"), ("m4a", "M4A (AAC)"), ("opus", "Opus"), ("flac", L.Get("AudioFormat_Flac")), ("wav", "WAV"));
            if (_options.AudioFormat is not ("mp3" or "m4a" or "opus" or "flac" or "wav")) _options.AudioFormat = "mp3";
        }
        else
        {
            AddItems(AudioFormatCombo, ("auto", L.Get("AudioFormat_Auto")), ("aac", "AAC"), ("opus", "Opus"));
            if (_options.AudioFormat is not ("auto" or "aac" or "opus")) _options.AudioFormat = "auto";
        }
        Select(AudioFormatCombo, _options.AudioFormat);
    }

    void FillAudioTracks()
    {
        AudioTrackCombo.Items.Clear();
        AudioTrackCombo.Items.Add(new ComboBoxItem { Content = L.Get("AudioTrack_Default"), Tag = "" });
        var languages = (_video?.AudioLanguages ?? []).ToList();
        if (_options.AudioLanguage.Length > 0 && !languages.Contains(_options.AudioLanguage, StringComparer.OrdinalIgnoreCase))
            languages.Add(_options.AudioLanguage);
        else if (_video is null)
            foreach (var common in new[] { "zh", "en", "ja", "ko" })
                if (!languages.Contains(common)) languages.Add(common);
        foreach (var code in languages)
            AudioTrackCombo.Items.Add(new ComboBoxItem { Content = LanguageName(code), Tag = code });
        Select(AudioTrackCombo, _options.AudioLanguage);
    }

    static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return $"{culture.NativeName} ({code})";
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var modeBefore = _options.Mode;
        _options.Mode = TagOf(ModeCombo) ?? "video";
        _options.MaxHeight = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
        _options.Codec = TagOf(CodecCombo) ?? VideoCodecs.Auto;
        _options.Container = TagOf(ContainerCombo) ?? "mp4";
        _options.AudioFormat = TagOf(AudioFormatCombo) ?? _options.AudioFormat;
        _options.AudioQuality = (AudioQualityCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
        _options.AudioLanguage = TagOf(AudioTrackCombo) ?? "";
        _options.Subtitles = SubsToggle.IsOn;
        _options.Recode = RecodeToggle.IsOn;
        _options.FolderTemplate = TagOf(FolderCombo) ?? "";

        if (_options.Mode != modeBefore)
        {
            // Video and audio-only have different audio format choices.
            _loading = true;
            FillAudioFormats();
            _loading = false;
        }
        Refresh();
        Changed?.Invoke();
    }

    void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _options.FileNameTemplate = FileNameBox.Text;
        Refresh();
        Changed?.Invoke();
    }

    void Refresh()
    {
        var video = _options.Mode != "audio";
        QualityCard.Visibility = CodecCard.Visibility = ContainerCard.Visibility = RecodeCard.Visibility =
            video ? Visibility.Visible : Visibility.Collapsed;

        var ext = video ? _options.Container : _options.AudioFormat;
        PreviewText.Text = L.Format("Options_Preview", NamingTemplate.Preview(_options.FileNameTemplate, _options.FolderTemplate, ext));

        var issue = FormatCompatibility.Check(_options);
        CompatBar.IsOpen = issue is not null;
        if (issue is null) return;
        CompatBar.Message = L.Get(issue.MessageKey);
        CompatFixButton.Visibility = issue.SuggestedContainer is not null || issue.SuggestedAudioFormat is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void CompatFix_Click(object sender, RoutedEventArgs e)
    {
        if (FormatCompatibility.Check(_options) is not { } issue) return;
        if (issue.SuggestedContainer is { } c) _options.Container = c;
        if (issue.SuggestedAudioFormat is { } a) _options.AudioFormat = a;
        Load(_options, _video);
        Changed?.Invoke();
    }

    static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    static void AddItems(ComboBox combo, params (string Tag, string Text)[] items)
    {
        foreach (var (tag, text) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
    }

    static void Select(ComboBox combo, object tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? combo.Items.FirstOrDefault();
}
