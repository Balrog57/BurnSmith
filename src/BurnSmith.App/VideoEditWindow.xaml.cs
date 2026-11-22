using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BurnSmith.Core;
using Microsoft.Win32;

namespace BurnSmith.App;

/// <summary>
/// The video edit for one video: Rogner (rotation, mirror, crop), Effet (colour, a look, the volume), Filigrane (a text or a picture), Sous-titres (one
/// burnt in), Chapitres (the part kept and the chapters), with a frame of the source and the same frame as it will be on the disc. Times are those of the
/// original file; the result is read from the properties after OK. <see cref="ApplyAll"/>: the tabs whose "Appliquer à tous" was pressed.
/// </summary>
public partial class VideoEditWindow : Window
{
    public string NameText { get; private set; }
    public double Start { get; private set; }
    /// <summary>Null = to the end of the file.</summary>
    public double? End { get; private set; }
    public int Volume { get; private set; }
    /// <summary>Null = the disc's setting, empty = no chapters, else the chapter times.</summary>
    public IReadOnlyList<double>? ChapterAt { get; private set; }
    /// <summary>With no chapter list: null = the disc's "one every N minutes", else this video's own N.</summary>
    public int? ChapterMinutes { get; private set; }
    /// <summary>Null = none.</summary>
    public VideoEffects? Effects { get; private set; }
    /// <summary>The parts kept when there are several (DVDFab's output clips); null = only <see cref="Start"/> to <see cref="End"/>.</summary>
    public IReadOnlyList<Clip>? Clips { get; private set; }
    public HashSet<string> ApplyAll { get; } = [];

    static readonly (string Name, string Hex)[] Colors =
        [("Blanc", "FFFFFF"), ("Noir", "000000"), ("Jaune", "FFE600"), ("Rouge", "E23A4E"), ("Vert", "3CC85A"), ("Bleu", "3C8CFF"), ("Gris", "9A9AA0")];
    static readonly int[] TextSizes = [12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 48, 60, 72, 96, 120, 160, 200];

    readonly string video;
    readonly SourceInfo info;
    /// <summary>An HDR source is shown as the 1080p disc or the DVD will show it (SDR), not grey: as a filter prefix, empty for SDR.</summary>
    string Sdr => BluRay.ToSdr(info.Hdr) is { Length: > 0 } f ? f + "," : "";
    readonly double duration;
    readonly List<double> times;
    /// <summary>The chapters' names, by their time in <see cref="times"/>: the menu writes them instead of "Chapitre N".</summary>
    readonly Dictionary<double, string> names = [];
    /// <summary>The names of <see cref="ChapterAt"/>, one for one (null = no chapter is named).</summary>
    public IReadOnlyList<string?>? ChapterTitles { get; private set; }
    readonly List<Clip> clips;
    readonly string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BurnSmith", "edit");
    readonly System.Windows.Threading.DispatcherTimer seekTimer, fxTimer;
    readonly bool ready;
    string? frame; // the source frame shown, full size
    double frameAt;
    int frameVersion, outputVersion;
    string? wmPicture;
    bool looksMade, syncing;
    int rotate;
    string? burnFile;

    public VideoEditWindow(string video, SourceInfo info, string name, double start, double? end, int volume, IReadOnlyList<double>? chapterAt, int? chapterMinutes,
                           int discMinutes, VideoEffects? effects, string? part = null, IReadOnlyList<Clip>? clips = null, IReadOnlyList<string?>? chapterTitles = null)
    {
        InitializeComponent();
        this.video = video;
        this.info = info;
        duration = info.Duration;
        Title = $"Éditer - {System.IO.Path.GetFileName(video)}";
        // the row button used opens the window on its tab: "cut" and "chapters" on Chapitres, else the tab of that name
        var tab = Tabs.Items.Cast<TabItem>().FirstOrDefault(t => (string)t.Tag == (part == "cut" ? "chapters" : part));
        if (tab != null) Tabs.SelectedItem = tab;
        Loaded += (_, _) => (part is "cut" ? PosBox : part is "chapters" ? ChapDisc : (Control)NameBox).Focus();
        Closed += (_, _) => playing?.Cancel();

        NameText = name;
        Start = start;
        End = end;
        Volume = volume;
        ChapterAt = chapterAt;
        ChapterMinutes = chapterMinutes;
        NameBox.Text = name;
        SourceText.Text = $"{info.Width}x{info.Height} · {Timecode.Format(duration)}";
        this.clips = clips is { Count: > 1 } ? [.. clips] : [new Clip(start, Math.Min(end ?? duration, duration))];
        TotalText.Text = "/ " + Timecode.Format(duration);
        VolumeSlider.Value = volume;
        ChapDisc.Content = $"Comme le disque (un chapitre toutes les {discMinutes} min)";
        times = chapterAt?.Order().ToList() ?? [];
        foreach (var (t, title) in (chapterAt ?? []).Zip(chapterTitles ?? [])) if (!string.IsNullOrWhiteSpace(title)) names[t] = title;
        FileChaptersButton.Visibility = info.Chapters == null ? Visibility.Collapsed : Visibility.Visible;
        EveryBox.Text = (chapterMinutes ?? discMinutes).ToString();
        (chapterAt == null ? chapterMinutes == null ? ChapDisc : ChapEvery : chapterAt.Count == 0 ? ChapNone : ChapCustom).IsChecked = true;

        // the lists of the tabs
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        WmFontBox.ItemsSource = fonts;
        BurnFontBox.ItemsSource = fonts;
        WmSizeBox.ItemsSource = TextSizes;
        BurnSizeBox.ItemsSource = TextSizes;
        foreach (var box in new[] { WmColorBox, BurnColorBox })
            foreach (var (cname, hex) in Colors)
            {
                var swatch = new Rectangle { Width = 40, Height = 12, Margin = new Thickness(0, 0, 10, 0), Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex)), Stroke = Brushes.Gray };
                box.Items.Add(new ComboBoxItem { Tag = hex, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = cname } } } });
            }
        foreach (var (lname, _) in VideoEffects.Looks)
            LookList.Items.Add(new ListBoxItem { Tag = lname, Width = 118, Content = new StackPanel { Children = { new Image { Width = 104, Height = 58, Stretch = Stretch.UniformToFill }, new TextBlock { Text = lname, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } } });
        BurnBox.Items.Add(new ComboBoxItem { Content = "Aucun sous-titre", Tag = null });
        foreach (var t in info.Subtitles.Where(t => t.IsText))
            BurnBox.Items.Add(new ComboBoxItem { Content = t.File != null ? $"{t.Name} ({System.IO.Path.GetFileName(t.File)})" : t.Name, Tag = t });
        Show(effects ?? new VideoEffects());

        SeekSlider.Maximum = Math.Max(1, duration);
        seekTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        seekTimer.Tick += async (_, _) => { seekTimer.Stop(); await ReadFrame(); };
        fxTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        fxTimer.Tick += async (_, _) => { fxTimer.Stop(); await RenderOutput(); };
        ready = true;
        ShowClips();
        OnVolume(null!, null!);
        OnChapterMode(null!, null!);
        ShowTimes();
        UpdateCropText();
        SeekSlider.Value = Math.Min(duration * 0.1, Math.Max(start, 0) + 30); // a frame of the film, not its black first second
        if (SeekSlider.Value == 0) OnSeek(null!, null!);
    }

    // ---- the settings shown, and read back ----

    /// <summary>Puts <paramref name="fx"/> into every tab.</summary>
    void Show(VideoEffects fx)
    {
        syncing = true;
        rotate = fx.Rotate;
        FlipHButton.IsChecked = fx.FlipH;
        FlipVButton.IsChecked = fx.FlipV;
        TopBox.Text = fx.CropTop.ToString();
        BottomBox.Text = fx.CropBottom.ToString();
        LeftBox.Text = fx.CropLeft.ToString();
        RightBox.Text = fx.CropRight.ToString();
        RatioBox.SelectedIndex = 0;
        BrightSlider.Value = fx.Brightness;
        ContrastSlider.Value = fx.Contrast;
        SaturationSlider.Value = fx.Saturation;
        WhiteSlider.Value = fx.WhiteBalance;
        LookList.SelectedItem = LookList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == fx.Look) ?? LookList.Items[0];
        Pick(DeintBox, string.IsNullOrEmpty(fx.Deinterlace) ? "auto" : fx.Deinterlace);
        DenoiseSlider.Value = Math.Clamp(fx.Denoise, 0, 100);
        (fx.WatermarkIsText ? WmTextRadio : WmPicRadio).IsChecked = true;
        WmTextBox.Text = fx.WatermarkText;
        WmFontBox.SelectedItem = fx.WatermarkFont;
        WmSizeBox.Text = fx.WatermarkSize.ToString();
        WmBold.IsChecked = fx.WatermarkBold;
        WmItalic.IsChecked = fx.WatermarkItalic;
        WmUnderline.IsChecked = fx.WatermarkUnderline;
        Pick(WmColorBox, fx.WatermarkColor);
        wmPicture = fx.WatermarkIsText ? null : fx.Watermark;
        WmPicText.Text = wmPicture != null ? System.IO.Path.GetFileName(wmPicture) : "Aucune image";
        WmWidthSlider.Value = fx.WatermarkWidth > 0 ? Math.Round(fx.WatermarkWidth * 100) : 20;
        WmTransSlider.Value = 100 - fx.WatermarkOpacity;
        Pick(WmPlaceBox, fx.WatermarkPlace);
        var burn = BurnBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is string f && f == fx.BurnSubtitle || i.Tag is SubtitleTrack t && Extracted(t) == fx.BurnSubtitle);
        if (burn == null && fx.BurnSubtitle != null) BurnBox.Items.Add(burn = new ComboBoxItem { Content = System.IO.Path.GetFileName(fx.BurnSubtitle), Tag = fx.BurnSubtitle });
        BurnBox.SelectedItem = burn ?? BurnBox.Items[0];
        BurnFontBox.SelectedItem = fx.BurnFont;
        BurnSizeBox.Text = fx.BurnSize.ToString();
        BurnBold.IsChecked = fx.BurnBold;
        BurnItalic.IsChecked = fx.BurnItalic;
        Pick(BurnColorBox, fx.BurnColor);
        BurnTransSlider.Value = 100 - fx.BurnOpacity;
        BurnPosSlider.Value = fx.BurnPosition;
        BurnDelaySlider.Value = fx.BurnDelay;
        syncing = false;
        burnFile = fx.BurnSubtitle;
        OnWmKind(null!, null!);
        BurnPanel.IsEnabled = burnFile != null;
    }

    static void Pick(ComboBox box, string tag) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
    static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    static int Int(string text, int fallback) => int.TryParse(text.Trim(), out var v) ? v : fallback;

    /// <summary>The effects as the tabs set them now (the text watermark drawn into its picture).</summary>
    VideoEffects Read()
    {
        var text = WmTextRadio.IsChecked == true;
        var fx = new VideoEffects(
            Rotate: rotate, FlipH: FlipHButton.IsChecked == true, FlipV: FlipVButton.IsChecked == true,
            CropTop: Math.Max(0, Int(TopBox.Text, 0)), CropBottom: Math.Max(0, Int(BottomBox.Text, 0)), CropLeft: Math.Max(0, Int(LeftBox.Text, 0)), CropRight: Math.Max(0, Int(RightBox.Text, 0)),
            Brightness: (int)BrightSlider.Value, Contrast: (int)ContrastSlider.Value, Saturation: (int)SaturationSlider.Value, WhiteBalance: (int)WhiteSlider.Value,
            Look: (LookList.SelectedItem as ListBoxItem)?.Tag as string ?? "",
            WatermarkIsText: text, WatermarkText: WmTextBox.Text, WatermarkFont: WmFontBox.SelectedItem as string ?? "Arial", WatermarkSize: Math.Clamp(Int(WmSizeBox.Text, 72), 6, 600),
            WatermarkBold: WmBold.IsChecked == true, WatermarkItalic: WmItalic.IsChecked == true, WatermarkUnderline: WmUnderline.IsChecked == true, WatermarkColor: TagOf(WmColorBox),
            WatermarkWidth: text ? 0 : WmWidthSlider.Value / 100, WatermarkOpacity: 100 - (int)WmTransSlider.Value, WatermarkPlace: TagOf(WmPlaceBox),
            BurnSubtitle: burnFile, BurnFont: BurnFontBox.SelectedItem as string ?? "Arial", BurnSize: Math.Clamp(Int(BurnSizeBox.Text, 22), 6, 200),
            BurnBold: BurnBold.IsChecked == true, BurnItalic: BurnItalic.IsChecked == true, BurnColor: TagOf(BurnColorBox), BurnOpacity: 100 - (int)BurnTransSlider.Value,
            BurnPosition: (int)BurnPosSlider.Value, BurnDelay: Math.Round(BurnDelaySlider.Value, 1),
            Deinterlace: TagOf(DeintBox) is { Length: > 0 } deint ? deint : "auto", Denoise: (int)DenoiseSlider.Value);
        return fx with { Watermark = text ? TextPicture(fx) : wmPicture };
    }

    /// <summary>The text watermark drawn by WPF into a PNG (its font size in pixels of the video), named after what it shows so an equal one is reused.</summary>
    string? TextPicture(VideoEffects fx)
    {
        if (string.IsNullOrWhiteSpace(fx.WatermarkText)) return null;
        var typeface = new Typeface(new FontFamily(fx.WatermarkFont), fx.WatermarkItalic ? FontStyles.Italic : FontStyles.Normal, fx.WatermarkBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + fx.WatermarkColor));
        var text = new FormattedText(fx.WatermarkText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fx.WatermarkSize, brush, 1.0);
        if (fx.WatermarkUnderline) text.SetTextDecorations(TextDecorations.Underline);
        var key = $"{fx.WatermarkText}|{fx.WatermarkFont}|{fx.WatermarkSize}|{fx.WatermarkBold}|{fx.WatermarkItalic}|{fx.WatermarkUnderline}|{fx.WatermarkColor}";
        var file = System.IO.Path.Combine(temp, "wm_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16] + ".png");
        if (File.Exists(file)) return file;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(text, new Point(2, 2));
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 4, (int)Math.Ceiling(text.Height) + 4, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        Directory.CreateDirectory(temp);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(file)) png.Save(f);
        return file;
    }

    // ---- Effet, Filigrane, Sous-titres ----

    void OnVolume(object s, RoutedEventArgs e)
    {
        if (ready) VolumeText.Text = $"{(int)VolumeSlider.Value} %";
    }

    void OnWmKind(object s, RoutedEventArgs e)
    {
        WmTextPanel.Visibility = WmTextRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WmPicPanel.Visibility = WmPicRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OnFx(s, e);
    }

    void OnWmPicture(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Image du filigrane"), Filter = Loc.Filter("Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp") };
        if (dlg.ShowDialog(this) != true) return;
        wmPicture = dlg.FileName;
        WmPicText.Text = System.IO.Path.GetFileName(wmPicture);
        OnFx(s, e);
    }

    /// <summary>Where a text track of the video itself is written as a file, for the subtitles filter (reading it out of a large video at each preview would be slow).</summary>
    string Extracted(SubtitleTrack t) => System.IO.Path.Combine(temp, $"burn_{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(video)))[..12]}_{t.Index}.ass");

    async void OnBurnPick(object s, SelectionChangedEventArgs e)
    {
        if (!ready || syncing) return;
        var tag = (BurnBox.SelectedItem as ComboBoxItem)?.Tag;
        if (tag is SubtitleTrack { File: null } track)
        {
            var file = Extracted(track);
            if (!File.Exists(file))
            {
                BurnPanel.IsEnabled = false;
                try
                {
                    Directory.CreateDirectory(temp);
                    await Task.Run(() => Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", video, "-map", $"0:s:{track.Index}", file]));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Impossible de lire ce sous-titre : " + ex.Message.Split('\n')[0], "Sous-titres", MessageBoxButton.OK, MessageBoxImage.Warning);
                    BurnBox.SelectedIndex = 0;
                    return;
                }
            }
            burnFile = file;
        }
        else burnFile = tag switch { SubtitleTrack t => t.File, string f => f, _ => null };
        BurnPanel.IsEnabled = burnFile != null;
        OnFx(s, e);
    }

    void OnBurnAdd(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Sous-titres à incruster"), Filter = Loc.Filter("Sous-titres texte (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt") };
        if (dlg.ShowDialog(this) != true) return;
        var item = new ComboBoxItem { Content = System.IO.Path.GetFileName(dlg.FileName), Tag = dlg.FileName };
        BurnBox.Items.Add(item);
        BurnBox.SelectedItem = item;
    }

    // ---- buttons under each tab & OK ----

    void OnDefault(object s, RoutedEventArgs e)
    {
        var part = (string)((Button)s).Tag;
        var current = Read();
        Show(new VideoEffects().Take(current, "crop").Take(current, "effect").Take(current, "watermark").Take(current, "subtitle") is var keep
            ? part switch
            {
                "crop" => keep with { Rotate = 0, FlipH = false, FlipV = false, CropTop = 0, CropBottom = 0, CropLeft = 0, CropRight = 0 },
                "effect" => keep with { Brightness = 0, Contrast = 0, Saturation = 0, WhiteBalance = 0, Look = "", Deinterlace = "auto", Denoise = 0 },
                "watermark" => keep.Take(new VideoEffects(), "watermark"),
                _ => keep.Take(new VideoEffects(), "subtitle") with { BurnSubtitle = null },
            } with { BurnSubtitle = part == "subtitle" ? null : current.BurnSubtitle }
            : current);
        if (part == "effect") VolumeSlider.Value = 100;
        OnFx(s, e);
    }

    void OnApplyAll(object s, RoutedEventArgs e)
    {
        var part = (string)((Button)s).Tag;
        ApplyAll.Add(part);
        ((Button)s).Content = "Appliqué à tous ✓";
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        if (ClipError() != null) { Tabs.SelectedIndex = 4; return; } // the reason is already shown under the clips
        Stop();
        // clips that meet (after "Couper ici") are one part again
        var kept = new List<Clip>();
        foreach (var c in clips)
            if (kept.Count > 0 && c.From <= kept[^1].To + 0.01) kept[^1] = kept[^1] with { To = c.To };
            else kept.Add(c);
        var start = kept[0].From < 0.05 ? 0 : kept[0].From;
        double? end = kept[^1].To < duration - 0.05 ? kept[^1].To : null; // an end at the end of the file is no cut at all
        var minutes = 0;
        if (ChapEvery.IsChecked == true && !(int.TryParse(EveryBox.Text.Trim(), out minutes) && minutes is >= 1 and <= 600))
        {
            Tabs.SelectedIndex = 4;
            MessageBox.Show(this, "Entrez un nombre de minutes entre 1 et 600.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var fx = Read();
        var (w, h) = fx.Size(info.Width, info.Height);
        if (fx.CropLeft + fx.CropRight >= Turned.W - 16 || fx.CropTop + fx.CropBottom >= Turned.H - 16)
        {
            Tabs.SelectedIndex = 0;
            MessageBox.Show(this, $"Le recadrage ne laisse presque rien de l'image ({w}x{h}).", "Rogner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (NameBox.Text.Trim() is { Length: > 0 } name) NameText = name;
        Start = start;
        End = end;
        Volume = (int)VolumeSlider.Value;
        ChapterMinutes = ChapEvery.IsChecked == true ? minutes : null;
        ChapterAt = ChapDisc.IsChecked == true || ChapEvery.IsChecked == true ? null : ChapNone.IsChecked == true ? [] : times.ToList();
        ChapterTitles = ChapterAt is { Count: > 0 } && times.Any(names.ContainsKey) ? [.. times.Select(t => names.GetValueOrDefault(t))] : null;
        Effects = fx.IsEmpty ? null : fx;
        Clips = kept.Count > 1 ? kept : null;
        DialogResult = true;
    }
}
