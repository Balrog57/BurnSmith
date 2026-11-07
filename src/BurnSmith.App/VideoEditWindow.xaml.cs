using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    public HashSet<string> ApplyAll { get; } = [];

    static readonly (string Name, string Hex)[] Colors =
        [("Blanc", "FFFFFF"), ("Noir", "000000"), ("Jaune", "FFE600"), ("Rouge", "E23A4E"), ("Vert", "3CC85A"), ("Bleu", "3C8CFF"), ("Gris", "9A9AA0")];
    static readonly int[] TextSizes = [12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 48, 60, 72, 96, 120, 160, 200];

    readonly string video;
    readonly SourceInfo info;
    readonly double duration;
    readonly List<double> times;
    readonly string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BurnSmith", "edit");
    readonly System.Windows.Threading.DispatcherTimer seekTimer, fxTimer;
    readonly bool ready;
    string? frame; // the source frame shown, full size
    double frameAt;
    int frameVersion, outputVersion;
    string? wmPicture;
    bool looksMade, syncing;

    public VideoEditWindow(string video, SourceInfo info, string name, double start, double? end, int volume, IReadOnlyList<double>? chapterAt, int? chapterMinutes,
                           int discMinutes, VideoEffects? effects, string? part = null)
    {
        InitializeComponent();
        this.video = video;
        this.info = info;
        duration = info.Duration;
        Title = $"Éditer - {System.IO.Path.GetFileName(video)}";
        // the row button used opens the window on its tab: "cut" and "chapters" on Chapitres, else the tab of that name
        var tab = Tabs.Items.Cast<TabItem>().FirstOrDefault(t => (string)t.Tag == (part == "cut" ? "chapters" : part));
        if (tab != null) Tabs.SelectedItem = tab;
        Loaded += (_, _) => (part is "cut" ? StartCheck : part is "chapters" ? ChapDisc : (Control)NameBox).Focus();

        NameText = name;
        Start = start;
        End = end;
        Volume = volume;
        ChapterAt = chapterAt;
        ChapterMinutes = chapterMinutes;
        NameBox.Text = name;
        SourceText.Text = $"{info.Width}x{info.Height} · {Timecode.Format(duration)}";
        StartCheck.IsChecked = start > 0;
        StartBox.Text = Timecode.Format(start);
        EndCheck.IsChecked = end != null;
        EndBox.Text = Timecode.Format(end ?? duration);
        VolumeSlider.Value = volume;
        ChapDisc.Content = $"Comme le disque (un chapitre toutes les {discMinutes} min)";
        times = chapterAt?.Order().ToList() ?? [];
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
        OnCut(null!, null!);
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

    int rotate;
    string? burnFile;

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
            BurnPosition: (int)BurnPosSlider.Value, BurnDelay: Math.Round(BurnDelaySlider.Value, 1));
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

    // ---- the preview ----

    static string Ffmpeg => Tools.Find("ffmpeg.exe");
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void OnSeek(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        TimeText.Text = $"{Timecode.Format(SeekSlider.Value)} / {Timecode.Format(duration)}";
        seekTimer.Stop();
        seekTimer.Start();
    }

    void OnBack(object s, RoutedEventArgs e) => SeekSlider.Value = Math.Max(0, SeekSlider.Value - 10);
    void OnForward(object s, RoutedEventArgs e) => SeekSlider.Value = Math.Min(duration, SeekSlider.Value + 10);

    /// <summary>The source frame at the slider, full size (the crop and the watermark are in its pixels), then the output made from it.</summary>
    async Task ReadFrame()
    {
        if (!File.Exists(video)) return;
        var mine = ++frameVersion;
        var at = SeekSlider.Value;
        var file = System.IO.Path.Combine(temp, $"src_{mine % 2}.png");
        try
        {
            Directory.CreateDirectory(temp);
            await Task.Run(() => Tools.Run(Ffmpeg, ["-y", "-v", "error", "-ss", N(at), "-i", video, "-frames:v", "1", "-update", "1", file]));
        }
        catch (Exception) { return; } // a frame that cannot be read leaves the last one
        if (mine != frameVersion || !File.Exists(file)) return;
        frame = file;
        frameAt = at;
        SourceImage.Source = Load(file);
        if (!looksMade) { looksMade = true; _ = MakeLooks(file); }
        await RenderOutput();
    }

    /// <summary>The frame as the disc will show it: these effects, then fitted into 16:9 with bars as the disc does.</summary>
    async Task RenderOutput()
    {
        if (frame == null) return;
        var mine = ++outputVersion;
        VideoEffects fx;
        try { fx = Read(); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return; } // a font WPF cannot draw: the last picture stays
        UpdateCropText();
        var (src, at) = (frame, frameAt);
        var file = System.IO.Path.Combine(temp, $"out_{mine % 2}.png");
        string? error = null;
        await Task.Run(() =>
        {
            try { Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", src, "-vf", fx.Vf(info.Width, info.Height, at, BluRay.Fit(960, 540)), "-frames:v", "1", "-update", "1", file]); }
            catch (Exception ex) { error = ex.Message.Split('\n')[0]; }
        });
        if (mine != outputVersion) return;
        PreviewError.Text = error != null ? "Aperçu impossible : " + error : "";
        PreviewError.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        if (error == null && File.Exists(file)) OutputImage.Source = Load(file);
    }

    /// <summary>The Effet tab's looks, each drawn on the first frame read.</summary>
    async Task MakeLooks(string src)
    {
        var copy = System.IO.Path.Combine(temp, "looks_src.png");
        File.Copy(src, copy, true);
        for (var k = 0; k < VideoEffects.Looks.Count; k++)
        {
            var file = System.IO.Path.Combine(temp, $"look_{k}.png");
            var filter = VideoEffects.Looks[k].Filter;
            try { await Task.Run(() => Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", copy, "-vf", "scale=208:-2" + (filter.Length > 0 ? "," + filter : ""), "-frames:v", "1", "-update", "1", file])); }
            catch (Exception) { continue; }
            if (((LookList.Items[k] as ListBoxItem)?.Content as StackPanel)?.Children[0] is Image image) image.Source = Load(file);
        }
    }

    /// <summary>A picture read whole, so the file can be written again for the next frame.</summary>
    static BitmapImage Load(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Any setting of the tabs changed: the output preview follows.</summary>
    void OnFx(object s, RoutedEventArgs e)
    {
        if (!ready || syncing) return;
        fxTimer.Stop();
        fxTimer.Start();
    }

    // ---- Rogner ----

    (int W, int H) Turned => rotate % 180 == 90 ? (info.Height, info.Width) : (info.Width, info.Height);

    void UpdateCropText()
    {
        var fx = new VideoEffects(Rotate: rotate, CropTop: Int(TopBox.Text, 0), CropBottom: Int(BottomBox.Text, 0), CropLeft: Int(LeftBox.Text, 0), CropRight: Int(RightBox.Text, 0));
        var (w, h) = fx.Size(info.Width, info.Height);
        OutSizeText.Text = $"Taille de sortie : {w}x{h}";
        if (!CropWBox.IsKeyboardFocusWithin) CropWBox.Text = w.ToString();
        if (!CropHBox.IsKeyboardFocusWithin) CropHBox.Text = h.ToString();
    }

    void OnRotate(object s, RoutedEventArgs e)
    {
        rotate = ((rotate + int.Parse((string)((Button)s).Tag)) % 360 + 360) % 360;
        SetMargins(0, 0, 0, 0); // the margins were for the other side up
    }

    void SetMargins(int top, int bottom, int left, int right)
    {
        syncing = true;
        (TopBox.Text, BottomBox.Text, LeftBox.Text, RightBox.Text) = (top.ToString(), bottom.ToString(), left.ToString(), right.ToString());
        syncing = false;
        UpdateCropText();
        OnFx(null!, null!);
    }

    void OnMargin(object s, TextChangedEventArgs e)
    {
        if (!ready || syncing) return;
        RatioBox.SelectedIndex = 0; // margins typed by hand: no fixed shape any more
        UpdateCropText();
        OnFx(s, e);
    }

    /// <summary>A crop size typed: the box keeps its top left corner, the right and bottom margins follow.</summary>
    void OnCropSize(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        var (fw, fh) = Turned;
        var (left, top) = (Int(LeftBox.Text, 0), Int(TopBox.Text, 0));
        var w = Math.Clamp(Int(CropWBox.Text, fw), 16, fw - left);
        var h = Math.Clamp(Int(CropHBox.Text, fh), 16, fh - top);
        SetMargins(top, fh - top - h, left, fw - left - w);
    }

    void OnCropSizeKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) OnCropSize(s, e); }

    /// <summary>A shape picked: the largest box of that shape in the middle of the picture.</summary>
    void OnRatio(object s, SelectionChangedEventArgs e)
    {
        if (!ready || syncing || !double.TryParse(TagOf(RatioBox), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)) return;
        var (fw, fh) = Turned;
        var (w, h) = fw / (double)fh > ratio ? ((int)(fh * ratio), fh) : (fw, (int)(fw / ratio));
        var (left, top) = ((fw - w) / 2, (fh - h) / 2);
        var index = RatioBox.SelectedIndex;
        SetMargins(top, fh - h - top, left, fw - w - left);
        RatioBox.SelectedIndex = index;
    }

    // ---- Effet ----

    void OnVolume(object s, RoutedEventArgs e)
    {
        if (ready) VolumeText.Text = $"{(int)VolumeSlider.Value} %";
    }

    // ---- Filigrane ----

    void OnWmKind(object s, RoutedEventArgs e)
    {
        WmTextPanel.Visibility = WmTextRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WmPicPanel.Visibility = WmPicRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OnFx(s, e);
    }

    void OnWmPicture(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Image du filigrane", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        wmPicture = dlg.FileName;
        WmPicText.Text = System.IO.Path.GetFileName(wmPicture);
        OnFx(s, e);
    }

    // ---- Sous-titres ----

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
        var dlg = new OpenFileDialog { Title = "Sous-titres à incruster", Filter = "Sous-titres texte (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt" };
        if (dlg.ShowDialog(this) != true) return;
        var item = new ComboBoxItem { Content = System.IO.Path.GetFileName(dlg.FileName), Tag = dlg.FileName };
        BurnBox.Items.Add(item);
        BurnBox.SelectedItem = item;
    }

    // ---- the buttons under each tab ----

    void OnDefault(object s, RoutedEventArgs e)
    {
        var part = (string)((Button)s).Tag;
        var current = Read();
        Show(new VideoEffects().Take(current, "crop").Take(current, "effect").Take(current, "watermark").Take(current, "subtitle") is var keep
            ? part switch
            {
                "crop" => keep with { Rotate = 0, FlipH = false, FlipV = false, CropTop = 0, CropBottom = 0, CropLeft = 0, CropRight = 0 },
                "effect" => keep with { Brightness = 0, Contrast = 0, Saturation = 0, WhiteBalance = 0, Look = "" },
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

    // ---- Chapitres ----

    /// <summary>The part kept, or why the times typed do not make one. An end at the end of the file is no cut at all.</summary>
    bool TryCut(out double start, out double? end, out string error)
    {
        start = 0;
        end = null;
        error = "";
        if (StartCheck.IsChecked == true)
        {
            if (Timecode.Parse(StartBox.Text) is not { } s || s >= duration) { error = "Début : un temps dans la vidéo est attendu (h:mm:ss)."; return false; }
            start = s;
        }
        if (EndCheck.IsChecked == true)
        {
            if (Timecode.Parse(EndBox.Text) is not { } e) { error = "Fin : un temps dans la vidéo est attendu (h:mm:ss)."; return false; }
            if (e < duration - 0.05) end = e;
        }
        if ((end ?? duration) - start < 1) { error = "La partie gardée doit durer au moins une seconde."; return false; }
        return true;
    }

    void OnCut(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        StartBox.IsEnabled = StartCheck.IsChecked == true;
        EndBox.IsEnabled = EndCheck.IsChecked == true;
        var ok = TryCut(out var start, out var end, out var error);
        KeptText.Text = ok ? $"Durée sur le disque : {Timecode.Format((end ?? duration) - start)}" : error;
        if (ok) KeptText.ClearValue(TextBlock.ForegroundProperty);
        else KeptText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
    }

    void OnStartHere(object s, RoutedEventArgs e) { StartCheck.IsChecked = true; StartBox.Text = Timecode.Format(SeekSlider.Value); }
    void OnEndHere(object s, RoutedEventArgs e) { EndCheck.IsChecked = true; EndBox.Text = Timecode.Format(SeekSlider.Value); }

    void OnChapterMode(object s, RoutedEventArgs e)
    {
        if (ready) ListPanel.IsEnabled = ChapCustom.IsChecked == true;
    }

    void ShowTimes() => ChapterList.ItemsSource = times.Select(Timecode.Format).ToList();

    void OnInsert(object s, RoutedEventArgs e)
    {
        if (Timecode.Parse(AtBox.Text) is not { } t) t = -1;
        AddChapter(t);
    }

    void OnInsertHere(object s, RoutedEventArgs e) => AddChapter(SeekSlider.Value);

    void AddChapter(double t)
    {
        if (t <= 0 || t >= duration)
        {
            MessageBox.Show(this, "Entrez un temps dans la vidéo d'origine (h:mm:ss), après le début.", "Chapitre", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        t = Math.Round(t, 1);
        if (!times.Contains(t)) times.Add(t);
        times.Sort();
        ShowTimes();
    }

    void OnDelete(object s, RoutedEventArgs e)
    {
        foreach (var i in ChapterList.SelectedItems.Cast<string>().Select(ChapterList.Items.IndexOf).OrderDescending()) times.RemoveAt(i);
        ShowTimes();
    }

    void OnDeleteAll(object s, RoutedEventArgs e)
    {
        times.Clear();
        ShowTimes();
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        if (!TryCut(out var start, out var end, out _)) { Tabs.SelectedIndex = 4; return; } // the reason is already shown under the times
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
        Effects = fx.IsEmpty ? null : fx;
        DialogResult = true;
    }
}
