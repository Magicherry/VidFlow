using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    static readonly int[] AudioQualities = [320, 256, 192, 128, 96];

    DownloadTemplate _options = new();
    VideoInfo? _video;
    bool _loading;

    public DownloadOptionsEditor()
    {
        InitializeComponent();
        AddItems(ModeCombo, ("video", L.Get("Mode_Video")), ("audio", L.Get("Mode_Audio")), ("cover", L.Get("Mode_Cover")),
            (DownloadRequest.SubtitlesMode, L.Get("Mode_Subtitles")));
        AddItems(SubFormatCombo, SubtitleFiles.Formats.Select(f => (f, SubtitleFiles.DisplayName(f))).ToArray());
        AddItems(ContainerCombo, ("mp4", L.Get("Container_Mp4")), ("mkv", L.Get("Container_Mkv")), ("webm", L.Get("Container_Webm")));
        AddItems(FolderCombo, ("", L.Get("Folder_None")), ("{platform}", L.Get("Folder_Platform")), ("{uploader}", L.Get("Folder_Uploader")),
            ("{year}-{month}", L.Get("Folder_Month")), ("{platform}/{uploader}", L.Get("Folder_PlatformUploader")));

        // File name presets read as their variables' names ("Uploader - Title"), plus "Custom".
        foreach (var preset in NamingTemplate.FilePresets)
        {
            var text = Describe(preset);
            if (preset == NamingTemplate.DefaultFileName) text = L.Format("Naming_Recommended", text);
            FileNamePresetCombo.Items.Add(new ComboBoxItem { Content = text, Tag = preset });
        }
        FileNamePresetCombo.Items.Add(new ComboBoxItem { Content = L.Get("Naming_Custom"), Tag = CustomTag });

        foreach (var name in NamingTemplate.Variables)
        {
            var button = new Button
            {
                Content = VariableName(name),
                Padding = new Thickness(8, 3, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 6, 0),
            };
            ToolTipService.SetToolTip(button, $"{{{name}}}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.Format("Naming_InsertVariable", VariableName(name)));
            button.Click += (_, _) => InsertVariable(name);
            VariablePanel.Children.Add(button);
        }
    }

    const string CustomTag = "\0custom";

    static string VariableName(string variable) => L.Get($"Var_{variable}");

    /// <summary>"{uploader} - {title}" → "Uploader - Title".</summary>
    static string Describe(string pattern)
    {
        foreach (var v in NamingTemplate.Variables) pattern = pattern.Replace($"{{{v}}}", VariableName(v));
        return pattern;
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

    /// <summary>For looking only: every choice is shown (audio opened up) but can't be changed. Not the whole control
    /// disabled, because an expander that is disabled can't be opened to read what is inside.</summary>
    public void MakeReadOnly()
    {
        foreach (var input in new Control[] { ModeCombo, QualityCombo, CodecCombo, PictureCombo, ContainerCombo, AudioFormatCombo,
                     AudioQualityCombo, AudioTrackCombo, SubFormatCombo, SubLanguageCombo, SubsToggle, RecodeToggle, FileNamePresetCombo, FileNameBox, FolderCombo, CompatFixButton })
            input.IsEnabled = false;
        VariablePanel.Visibility = Visibility.Collapsed;
        AudioExpander.IsExpanded = true;
    }

    public void Load(DownloadTemplate options, VideoInfo? video = null)
    {
        _options = options;
        _video = video;
        _loading = true;

        Select(ModeCombo, options.Mode is "audio" or "cover" or DownloadRequest.SubtitlesMode ? options.Mode : "video");
        Select(SubFormatCombo, SubtitleFiles.Normalize(options.SubtitleFormat));
        FillSubtitleLanguages();
        FillQualities();
        FillCodecs();
        FillPictures();
        Select(ContainerCombo, options.Container);
        FillAudioFormats();
        FillAudioQualities();
        FillAudioTracks();
        SubsToggle.IsOn = options.Subtitles;
        RecodeToggle.IsOn = options.Recode;
        FileNameBox.Text = string.IsNullOrWhiteSpace(options.FileNameTemplate) ? NamingTemplate.DefaultFileName : options.FileNameTemplate;
        SyncFileNamePreset();
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
            // The top resolution is "best" (0) itself: listing both would show it twice.
            string Label(QualityOption q) => Format.Quality(q, SizeEstimator.Estimate(v, q, _options));
            QualityCombo.Items.Add(new ComboBoxItem { Content = Label(v.Qualities[0]), Tag = 0 });
            foreach (var q in v.Qualities.Skip(1))
                QualityCombo.Items.Add(new ComboBoxItem { Content = Label(q), Tag = q.Height });
        }
        else
        {
            QualityCombo.Items.Add(new ComboBoxItem { Content = L.Get("Quality_Best"), Tag = 0 });
            foreach (var h in GenericHeights)
                QualityCombo.Items.Add(new ComboBoxItem { Content = Format.Height(h), Tag = h });
        }
        // Closest choice not above the wanted height (a 1080p template on a 720p video shows 720p).
        var items = QualityCombo.Items.Cast<ComboBoxItem>().ToList();
        QualityCombo.SelectedIndex = _options.MaxHeight == 0 || _options.MaxHeight >= TopHeight ? 0
            : Math.Max(0, items.FindIndex(i => (int)i.Tag > 0 && (int)i.Tag <= _options.MaxHeight));
    }

    string SizeInputs() => $"{_options.Mode}|{_options.Codec}|{_options.DynamicRange}|{_options.Container}|{_options.AudioFormat}|{_options.AudioQuality}|{_options.AudioLanguage}";

    /// <summary>The parsed video's highest resolution (the first, "best" entry); unknown before parsing.</summary>
    int TopHeight => _video is { Qualities.Count: > 0 } v ? v.Qualities[0].Height : int.MaxValue;

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

    /// <summary>Picture: Auto / Dolby Vision / SDR. For a parsed video only the kinds it offers (none: hidden).</summary>
    void FillPictures()
    {
        PictureCombo.Items.Clear();
        var hdr = _video is null || _video.HasHdr;
        var dv = _video is null || _video.HasDolbyVision;
        AddItems(PictureCombo, (MediaFeatures.Auto, L.Get(hdr ? "Picture_Auto" : "Picture_AutoNoHdr")));
        if (dv) AddItems(PictureCombo, (MediaFeatures.DolbyVision, L.Get("Picture_DolbyVision")));
        AddItems(PictureCombo, (MediaFeatures.Sdr, L.Get("Picture_Sdr")));
        if (_options.DynamicRange == MediaFeatures.DolbyVision && !dv)
            PictureCombo.Items.Add(new ComboBoxItem { Content = L.Format("Codec_Unavailable", L.Get("Picture_DolbyVision")), Tag = MediaFeatures.DolbyVision });
        Select(PictureCombo, _options.DynamicRange);
    }

    /// <summary>What this video's audio actually is, when known (not in the template editor).</summary>
    int? TopAudioKbps => _video is { Formats.Count: > 0 } v ? SizeEstimator.TopAudioKbps(v.Formats, _options) : null;

    /// <summary>The first entry is "as high as it goes": the video's real bitrate when known, and only lower limits
    /// after it, since nothing above what the source has can be had.</summary>
    void FillAudioQualities()
    {
        AudioQualityCombo.Items.Clear();
        var top = TopAudioKbps;
        AudioQualityCombo.Items.Add(new ComboBoxItem { Content = top is { } kbps ? $"{kbps} kbps" : L.Get("AudioQuality_Best"), Tag = 0 });
        foreach (var q in AudioQualities.Where(q => top is null || q < top))
            AudioQualityCombo.Items.Add(new ComboBoxItem { Content = $"{q} kbps", Tag = q });
        // Closest choice not above the wanted bitrate.
        var items = AudioQualityCombo.Items.Cast<ComboBoxItem>().ToList();
        var wanted = _options.AudioQuality;
        AudioQualityCombo.SelectedIndex = wanted <= 0 ? 0 : Math.Max(0, items.FindIndex(i => (int)i.Tag > 0 && (int)i.Tag <= wanted));
    }

    void FillAudioFormats()
    {
        AudioFormatCombo.Items.Clear();
        if (_options.Mode == "audio")
        {
            AddItems(AudioFormatCombo, (MediaFeatures.Original, L.Get("AudioFormat_Original")),
                ("mp3", "MP3"), ("m4a", "M4A (AAC)"), ("opus", "Opus"), ("flac", L.Get("AudioFormat_Flac")), ("wav", "WAV"));
            if (_options.AudioFormat is not ("mp3" or "m4a" or "opus" or "flac" or "wav" or MediaFeatures.Original)) _options.AudioFormat = "mp3";
        }
        else
        {
            AddItems(AudioFormatCombo, ("auto", L.Get("AudioFormat_Auto")), ("aac", "AAC"), ("opus", "Opus"));
            // Hi-Res / Dolby Atmos: when the video offers them (always listed in the template editor).
            if (_video is null || _video.HasHiRes) AddItems(AudioFormatCombo, (MediaFeatures.HiRes, L.Get("AudioFormat_HiRes")));
            if (_video is null || _video.HasAtmos) AddItems(AudioFormatCombo, (MediaFeatures.Atmos, L.Get("AudioFormat_Atmos")));
            if (_options.AudioFormat is not ("auto" or "aac" or "opus" or MediaFeatures.HiRes or MediaFeatures.Atmos)) _options.AudioFormat = "auto";
            // Chosen in a template but not offered by this video: keep it visible, the download falls back.
            if (!AudioFormatCombo.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == _options.AudioFormat))
                AudioFormatCombo.Items.Add(new ComboBoxItem
                {
                    Content = L.Format("Codec_Unavailable", L.Get(_options.AudioFormat == MediaFeatures.HiRes ? "AudioFormat_HiRes" : "AudioFormat_Atmos")),
                    Tag = _options.AudioFormat,
                });
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

    /// <summary>The languages in Settings, then each track the video has: the uploader's own, then automatic ones.</summary>
    void FillSubtitleLanguages()
    {
        SubLanguageCombo.Items.Clear();
        AddItems(SubLanguageCombo, ("", L.Get("SubLang_Settings")));
        foreach (var code in _video?.SubtitleLanguages ?? [])
            AddItems(SubLanguageCombo, (code, $"{SubtitleFiles.LanguageName(code)} ({code})"));
        foreach (var code in _video?.AutoSubtitleLanguages ?? [])
            AddItems(SubLanguageCombo, (SubtitleFiles.AutoPrefix + code, L.Format("SubLang_Auto", SubtitleFiles.LanguageName(code))));
        // Made from the sound, for any video; offered greyed out until it is downloaded in Settings.
        var speech = SpeechRecognizer.CanRecognize;
        foreach (var language in SpeechRecognizer.Languages)
        {
            var text = L.Format("SubLang_Speech", L.Get($"Asr_Lang_{language}"));
            SubLanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = speech ? text : L.Format("SubLang_SpeechOff", text),
                Tag = SubtitleFiles.SpeechPrefix + language,
                IsEnabled = speech,
            });
        }
        // A track another video had (from a template) means nothing here.
        if (!SubLanguageCombo.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == _options.SubtitleLanguage && i.IsEnabled)) _options.SubtitleLanguage = "";
        Select(SubLanguageCombo, _options.SubtitleLanguage);
        SubLanguageCard.Description = _video is { HasSubtitles: false }
            ? L.Get("Options_SubtitleNone") : L.Get("Options_SubtitleLanguageDescription");
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
        var sizesBefore = SizeInputs();
        _options.Mode = TagOf(ModeCombo) ?? "video";
        var height = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
        // The first entry stands for both "best" and the top resolution: a 1080p template on a 1080p video keeps its 1080.
        if (height != 0 || _options.MaxHeight < TopHeight) _options.MaxHeight = height;
        _options.Codec = TagOf(CodecCombo) ?? VideoCodecs.Auto;
        _options.DynamicRange = TagOf(PictureCombo) ?? MediaFeatures.Auto;
        _options.Container = TagOf(ContainerCombo) ?? "mp4";
        _options.AudioFormat = TagOf(AudioFormatCombo) ?? _options.AudioFormat;
        var audioQuality = (AudioQualityCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
        // As with resolution: the first entry is the top, so a higher limit from a template stays as it is.
        if (audioQuality != 0 || TopAudioKbps is not { } top || _options.AudioQuality < top) _options.AudioQuality = audioQuality;
        _options.AudioLanguage = TagOf(AudioTrackCombo) ?? "";
        _options.Subtitles = SubsToggle.IsOn;
        _options.SubtitleFormat = TagOf(SubFormatCombo) ?? SubtitleFiles.DefaultFormat;
        _options.SubtitleLanguage = TagOf(SubLanguageCombo) ?? "";
        _options.Recode = RecodeToggle.IsOn;
        _options.FolderTemplate = TagOf(FolderCombo) ?? "";

        if (_options.Mode != modeBefore)
        {
            // Video and audio-only have different audio format choices.
            _loading = true;
            FillAudioFormats();
            _loading = false;
        }

        // The sizes in the quality list depend on these: a different codec or audio track is a different file.
        if (_video is not null && SizeInputs() != sizesBefore)
        {
            _loading = true;
            FillQualities();
            FillAudioQualities();
            _loading = false;
        }
        Refresh();
        Changed?.Invoke();
    }

    void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _options.FileNameTemplate = FileNameBox.Text;
        SyncFileNamePreset();
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>The preset list follows the pattern: a preset when it matches one, otherwise "Custom".</summary>
    void SyncFileNamePreset()
    {
        var text = FileNameBox.Text.Trim();
        var match = FileNamePresetCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == text)
                    ?? FileNamePresetCombo.Items.Cast<ComboBoxItem>().Last();
        if (!ReferenceEquals(FileNamePresetCombo.SelectedItem, match))
        {
            var loading = _loading;
            _loading = true;
            FileNamePresetCombo.SelectedItem = match;
            _loading = loading;
        }
    }

    void FileNamePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FileNamePresetCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == CustomTag)
        {
            // Keep the current pattern and put the caret at its end, ready to edit.
            FileNameBox.Focus(FocusState.Programmatic);
            FileNameBox.SelectionStart = FileNameBox.Text.Length;
            return;
        }
        FileNameBox.Text = tag;
    }

    /// <summary>Inserts "{variable}" at the caret (replacing any selection), with a space before it if needed.</summary>
    void InsertVariable(string name)
    {
        var text = FileNameBox.Text;
        var start = Math.Clamp(FileNameBox.SelectionStart, 0, text.Length);
        var end = Math.Clamp(start + FileNameBox.SelectionLength, start, text.Length);
        // A button click takes focus from the box; without a caret it appends.
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), FileNameBox) && FileNameBox.SelectionLength == 0 && start == 0) start = end = text.Length;
        var token = $"{{{name}}}";
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '[' or '-' or '_')) token = " " + token;
        FileNameBox.Text = text[..start] + token + text[end..];
        FileNameBox.Focus(FocusState.Programmatic);
        FileNameBox.SelectionStart = start + token.Length;
        FileNameBox.SelectionLength = 0;
    }

    void Refresh()
    {
        var video = _options.Mode == "video";
        var cover = _options.Mode == "cover";
        var subtitles = _options.Mode == DownloadRequest.SubtitlesMode;
        QualityCard.Visibility = CodecCard.Visibility = ContainerCard.Visibility = RecodeCard.Visibility =
            video ? Visibility.Visible : Visibility.Collapsed;
        // A cover is just the image, subtitles just the text: no sound to choose.
        AudioExpander.Visibility = SubtitlesCard.Visibility = cover || subtitles ? Visibility.Collapsed : Visibility.Visible;
        SubFormatCard.Visibility = subtitles ? Visibility.Visible : Visibility.Collapsed;
        // Which tracks there are is only known for a parsed video (not in the template editor).
        SubLanguageCard.Visibility = subtitles && _video is not null ? Visibility.Visible : Visibility.Collapsed;
        // Nothing to choose for a video that is SDR only.
        PictureCard.Visibility = video && (_video is null || _video.HdrKinds.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        var request = _options.ToRequest("", "");
        var ext = subtitles ? SubtitleFiles.Normalize(_options.SubtitleFormat)
            : cover ? "jpg"
            : video ? (request.NeedsMkv ? "mkv" : _options.Container)
            : _options.AudioFormat == MediaFeatures.Original ? (_video?.HasHiRes == true ? "flac" : "m4a")
            : _options.AudioFormat;
        PreviewText.Text = $"{L.Get("Options_PreviewRoot")}\\{NamingTemplate.Preview(_options.FileNameTemplate, _options.FolderTemplate, ext, request.WriteSubtitles)}";
        PreviewIcon.Glyph = subtitles ? "\uED1E" : cover ? "" : video ? "" : "";
        var unknown = NamingTemplate.UnknownVariables(_options.FileNameTemplate);
        UnknownVariableText.Visibility = unknown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (unknown.Count > 0)
            UnknownVariableText.Text = L.Format("Naming_Unknown", string.Join(" ", unknown.Select(u => $"{{{u}}}")));

        var issue = FormatCompatibility.Check(_options, _video);
        CompatBar.IsOpen = issue is not null;
        if (issue is null) return;
        CompatBar.Message = L.Get(issue.MessageKey);
        // "Will be saved as MKV" is what happens, not a problem.
        CompatBar.Severity = issue.Info ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        CompatFixButton.Content = L.Get(issue.Info ? "Compat_UseMkv" : "Compat_Apply");
        CompatFixButton.Visibility = issue.SuggestedContainer is not null || issue.SuggestedAudioFormat is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void CompatFix_Click(object sender, RoutedEventArgs e)
    {
        if (FormatCompatibility.Check(_options, _video) is not { } issue) return;
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
