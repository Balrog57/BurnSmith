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
    readonly List<Clip> clips;
    readonly string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BurnSmith", "edit");
    readonly System.Windows.Threading.DispatcherTimer seekTimer, fxTimer;
    readonly bool ready;
    string? frame; // the source frame shown, full size
    double frameAt;
    int frameVersion, outputVersion;
    string? wmPicture;
    bool looksMade, syncing;

    public VideoEditWindow(string video, SourceInfo info, string name, double start, double? end, int volume, IReadOnlyList<double>? chapterAt, int? chapterMinutes,
                           int discMinutes, VideoEffects? effects, string? part = null, IReadOnlyList<Clip>? clips = null)
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
        if (!PosBox.IsKeyboardFocusWithin) PosBox.Text = Timecode.Format(SeekSlider.Value);
        if (advancing) return; // the player moved it
        Stop(); // the user moved it: the player stops there
        seekTimer.Stop();
        seekTimer.Start();
    }

    /// <summary>The buttons under the slider: Tag is the step in seconds.</summary>
    void OnStep(object s, RoutedEventArgs e) => SeekSlider.Value = Math.Clamp(SeekSlider.Value + double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture), 0, duration);

    void OnPosKey(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnPosEdited(s, e);
        e.Handled = true; // not the window's OK
    }

    void OnPosEdited(object s, RoutedEventArgs e)
    {
        if (Timecode.Parse(PosBox.Text) is { } t) SeekSlider.Value = Math.Clamp(t, 0, duration);
        PosBox.Text = Timecode.Format(SeekSlider.Value);
    }

    // ---- playing: ffmpeg decodes from the slider's time at the video's own pace into the source picture (no sound, no effects) ----

    CancellationTokenSource? playing;
    bool advancing;

    void OnPlay(object s, RoutedEventArgs e)
    {
        if (playing != null) Stop();
        else _ = Play(duration);
    }

    void Stop()
    {
        if (playing == null) return;
        playing.Cancel();
        playing = null;
        PlayButton.Content = "";
        ShowCrop();
        seekTimer.Stop();
        seekTimer.Start(); // the still frames and the output picture of where it stopped
    }

    /// <summary>Plays from the slider's time up to <paramref name="until"/> (a clip's end, or the end of the file).</summary>
    async Task Play(double until)
    {
        if (!File.Exists(video)) return;
        const int Fps = 25;
        var scale = Math.Min(456.0 / Math.Max(1, info.Width), 232.0 / Math.Max(1, info.Height));
        int w = Math.Max(2, (int)(info.Width * scale) / 2 * 2), h = Math.Max(2, (int)(info.Height * scale) / 2 * 2);
        var cts = playing = new CancellationTokenSource();
        PlayButton.Content = "";
        ShowCrop();
        var from = SeekSlider.Value >= until - 0.5 ? 0 : SeekSlider.Value;
        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-re", "-ss", N(from), "-i", video, "-an", "-sn", "-vf", $"{Sdr}fps={Fps},scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" })
            psi.ArgumentList.Add(a);
        System.Diagnostics.Process? ffmpeg = null;
        try
        {
            ffmpeg = System.Diagnostics.Process.Start(psi)!;
            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
            SourceImage.Source = bmp;
            var picture = new byte[w * h * 3];
            for (var n = 1; ; n++)
            {
                await ffmpeg.StandardOutput.BaseStream.ReadExactlyAsync(picture, cts.Token);
                bmp.WritePixels(new Int32Rect(0, 0, w, h), picture, w * 3, 0);
                var at = Math.Min(from + (double)n / Fps, duration);
                advancing = true;
                SeekSlider.Value = at;
                advancing = false;
                if (at >= until) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { } // the end of the file
        catch (Exception ex) { MessageBox.Show(this, "Lecture impossible : " + ex.Message, "Lecture", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            try { ffmpeg?.Kill(); } catch (Exception) { } // already gone
            ffmpeg?.Dispose();
            if (playing == cts) Stop();
        }
    }

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
            await Task.Run(() => Tools.Run(Ffmpeg, ["-y", "-v", "error", "-ss", N(at), "-i", video, "-vf", Sdr + "null", "-frames:v", "1", "-update", "1", file]));
        }
        catch (Exception) { return; } // a frame that cannot be read leaves the last one
        if (mine != frameVersion || !File.Exists(file) || playing != null) return;
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
        ShowCrop();
    }

    // the crop frame on the source picture: the picture is shown turned and flipped like the output, so the margins are drawn where they cut

    /// <summary>Where the turned frame sits in the source box: its scale (screen pixels per video pixel) and top left corner.</summary>
    (double Scale, double X, double Y) Placement()
    {
        var (fw, fh) = Turned;
        var (cw, ch) = (SourceBox.ActualWidth, SourceBox.ActualHeight);
        var scale = Math.Min(cw / Math.Max(1, fw), ch / Math.Max(1, fh));
        return (scale, (cw - fw * scale) / 2, (ch - fh * scale) / 2);
    }

    (int Top, int Bottom, int Left, int Right) Margins() => (Math.Max(0, Int(TopBox.Text, 0)), Math.Max(0, Int(BottomBox.Text, 0)), Math.Max(0, Int(LeftBox.Text, 0)), Math.Max(0, Int(RightBox.Text, 0)));

    void ShowCrop()
    {
        SourceTurn.Angle = rotate;
        SourceFlip.ScaleX = FlipHButton.IsChecked == true ? -1 : 1;
        SourceFlip.ScaleY = FlipVButton.IsChecked == true ? -1 : 1;
        CropCanvas.Visibility = Tabs.SelectedIndex == 0 && playing == null ? Visibility.Visible : Visibility.Collapsed;
        if (SourceBox.ActualWidth < 1) return;
        var (scale, x, y) = Placement();
        var (fw, fh) = Turned;
        var (top, bottom, left, right) = Margins();
        var inner = new Rect(x + left * scale, y + top * scale, Math.Max(0, (fw - left - right) * scale), Math.Max(0, (fh - top - bottom) * scale));
        Canvas.SetLeft(CropRect, inner.X);
        Canvas.SetTop(CropRect, inner.Y);
        (CropRect.Width, CropRect.Height) = (inner.Width, inner.Height);
        CropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(x, y, fw * scale, fh * scale)), new RectangleGeometry(inner));
        // the handles: the four corners and the middle of each side
        if (CropCanvas.Children.Count == 2)
            for (var k = 0; k < 8; k++) CropCanvas.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, IsHitTestVisible = false });
        var points = new[] { inner.TopLeft, inner.TopRight, inner.BottomLeft, inner.BottomRight,
                             new Point(inner.X + inner.Width / 2, inner.Y), new Point(inner.X + inner.Width / 2, inner.Bottom), new Point(inner.X, inner.Y + inner.Height / 2), new Point(inner.Right, inner.Y + inner.Height / 2) };
        for (var k = 0; k < 8; k++)
        {
            Canvas.SetLeft(CropCanvas.Children[2 + k], points[k].X - 4);
            Canvas.SetTop(CropCanvas.Children[2 + k], points[k].Y - 4);
        }
    }

    void OnSourceBoxSize(object s, SizeChangedEventArgs e) => ShowCrop();

    void OnTab(object s, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && ready) ShowCrop(); // the lists inside the tabs raise this event too
    }

    // what a drag holds: the sides it moves (left, top, right, bottom; all four = the whole frame), where it began and the margins then
    (bool L, bool T, bool R, bool B) dragSides;
    Point dragFrom;
    (int Top, int Bottom, int Left, int Right) dragMargins;
    bool dragging;

    /// <summary>The sides a press at <paramref name="p"/> takes: near a corner two, near a side one, inside the frame all four, outside none.</summary>
    (bool L, bool T, bool R, bool B) SidesAt(Point p)
    {
        var r = new Rect(Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect), CropRect.Width, CropRect.Height);
        const double near = 8;
        bool l = Math.Abs(p.X - r.Left) < near, rt = Math.Abs(p.X - r.Right) < near, t = Math.Abs(p.Y - r.Top) < near, b = Math.Abs(p.Y - r.Bottom) < near;
        bool inX = p.X > r.Left - near && p.X < r.Right + near, inY = p.Y > r.Top - near && p.Y < r.Bottom + near;
        if (!inX || !inY) return default;
        if (l || rt || t || b) return (l, t, rt && !l, b && !t);
        return (true, true, true, true);
    }

    void OnCropDown(object s, MouseButtonEventArgs e)
    {
        var sides = SidesAt(e.GetPosition(CropCanvas));
        if (sides == default) return;
        (dragSides, dragFrom, dragMargins, dragging) = (sides, e.GetPosition(CropCanvas), Margins(), true);
        CropCanvas.CaptureMouse();
        e.Handled = true;
    }

    void OnCropMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(CropCanvas);
        if (!dragging)
        {
            var (l, t, r, b) = SidesAt(p);
            CropCanvas.Cursor = (l && t) || (r && b) ? (l && r ? Cursors.SizeAll : Cursors.SizeNWSE) : (l && b) || (r && t) ? Cursors.SizeNESW
                : l || r ? Cursors.SizeWE : t || b ? Cursors.SizeNS : Cursors.Arrow;
            return;
        }
        var (scale, _, _) = Placement();
        var (fw, fh) = Turned;
        int dx = (int)Math.Round((p.X - dragFrom.X) / scale), dy = (int)Math.Round((p.Y - dragFrom.Y) / scale);
        var (top, bottom, left, right) = dragMargins;
        const int min = 16;
        if (dragSides is (true, true, true, true)) // the whole frame moves, its size kept
        {
            dx = Math.Clamp(dx, -left, right);
            dy = Math.Clamp(dy, -top, bottom);
            SetMargins(top + dy, bottom - dy, left + dx, right - dx);
            return;
        }
        if (dragSides.L) left = Math.Clamp(left + dx, 0, fw - right - min);
        if (dragSides.R) right = Math.Clamp(right - dx, 0, fw - left - min);
        if (dragSides.T) top = Math.Clamp(top + dy, 0, fh - bottom - min);
        if (dragSides.B) bottom = Math.Clamp(bottom - dy, 0, fh - top - min);
        var corner = (dragSides.L || dragSides.R) && (dragSides.T || dragSides.B);
        if (corner && RatioValue() is { } ratio)
        {
            // a fixed shape: the width leads, the height follows, the opposite corner stays
            var w = fw - left - right;
            var h = (int)Math.Round(w / ratio);
            if (h > fh - (dragSides.T ? bottom : top)) { h = fh - (dragSides.T ? bottom : top); w = (int)Math.Round(h * ratio); }
            if (dragSides.L) left = fw - right - w; else right = fw - left - w;
            if (dragSides.T) top = fh - bottom - h; else bottom = fh - top - h;
        }
        else if (!corner) RatioBox.SelectedIndex = 0; // one side alone changes the shape
        var index = RatioBox.SelectedIndex;
        SetMargins(top, bottom, left, right);
        RatioBox.SelectedIndex = index;
    }

    void OnCropUp(object s, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        CropCanvas.ReleaseMouseCapture();
    }

    /// <summary>The shape asked for in the Format list (width / height), null for Libre.</summary>
    double? RatioValue()
    {
        var tag = TagOf(RatioBox);
        if (tag == "orig") return Turned.W / (double)Math.Max(1, Turned.H);
        return double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
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
        if (!ready || syncing || RatioValue() is not { } ratio) return;
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

    /// <summary>Why the clips do not make a disc (null when they do): each lasts a second or more, inside the file, after the one before.</summary>
    string? ClipError()
    {
        for (var k = 0; k < clips.Count; k++)
        {
            var c = clips[k];
            if (c.From < 0 || c.To > duration + 0.05) return $"Clip {k + 1} : ses temps doivent être dans la vidéo (0:00:00 à {Timecode.Format(duration)}).";
            if (c.To - c.From < 1) return $"Clip {k + 1} : il doit durer au moins une seconde.";
            if (k > 0 && c.From < clips[k - 1].To - 0.01) return $"Clip {k + 1} : il doit commencer après la fin du clip {k}.";
        }
        return null;
    }

    /// <summary>One block per clip as DVDFab shows them: its start and end (typed, or taken from the slider), its length, play and delete.</summary>
    void ShowClips()
    {
        ClipPanel.Children.Clear();
        for (var k = 0; k < clips.Count; k++)
        {
            var i = k;
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new DockPanel();
            var delete = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = "Supprimer ce clip (sa partie est enlevée du disque)", IsEnabled = clips.Count > 1 };
            delete.Click += (_, _) => { clips.RemoveAt(i); ShowClips(); };
            var play = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = "Lire ce clip" };
            play.Click += (_, _) => { Stop(); SeekSlider.Value = clips[i].From; _ = Play(clips[i].To); };
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(play, Dock.Right);
            head.Children.Add(delete);
            head.Children.Add(play);
            head.Children.Add(new TextBlock { Text = $"Clip {k + 1}  ·  durée {Timecode.Format(Math.Max(0, clips[k].To - clips[k].From))}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            block.Children.Add(head);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var isStart in new[] { true, false })
            {
                line.Children.Add(new TextBlock { Text = isStart ? "Heure de début :" : "Heure de fin :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(isStart ? 0 : 16, 0, 6, 0) });
                var box = new TextBox { Style = (Style)FindResource("Time"), Text = Timecode.Format(isStart ? clips[k].From : clips[k].To) };
                var first = isStart;
                void Typed()
                {
                    if (Timecode.Parse(box.Text) is { } t) clips[i] = first ? clips[i] with { From = Math.Round(t, 2) } : clips[i] with { To = Math.Round(t, 2) };
                    ShowClips();
                }
                box.LostFocus += (_, _) => { if (box.Text != Timecode.Format(first ? clips[i].From : clips[i].To)) Typed(); };
                box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Typed(); e.Handled = true; } };
                var here = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = isStart ? "Début ici (à la position de l'aperçu)" : "Fin ici (à la position de l'aperçu)" };
                here.Click += (_, _) =>
                {
                    var t = Math.Round(SeekSlider.Value, 2);
                    clips[i] = first ? clips[i] with { From = t } : clips[i] with { To = t };
                    ShowClips();
                };
                line.Children.Add(box);
                line.Children.Add(here);
            }
            block.Children.Add(line);
            ClipPanel.Children.Add(block);
        }
        var error = ClipError();
        var kept = clips.Sum(c => Math.Max(0, c.To - c.From));
        KeptText.Text = error ?? $"Durée sur le disque : {Timecode.Format(kept)}" + (clips.Count > 1 ? $" ({clips.Count} clips, {Timecode.Format(Math.Max(0, clips[^1].To - clips[0].From - kept))} enlevés entre eux)" : "");
        if (error == null) KeptText.ClearValue(TextBlock.ForegroundProperty);
        else KeptText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        ShowMarks();
    }

    /// <summary>"Couper ici": the clip under the slider becomes two that meet there.</summary>
    void OnSplitClip(object s, RoutedEventArgs e)
    {
        var t = Math.Round(SeekSlider.Value, 2);
        var k = clips.FindIndex(c => t > c.From + 1 && t < c.To - 1);
        if (k < 0)
        {
            MessageBox.Show(this, "Placez l'aperçu dans un clip, à plus d'une seconde de ses bords.", "Couper ici", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        clips.Insert(k + 1, clips[k] with { From = t });
        clips[k] = clips[k] with { To = t };
        ShowClips();
    }

    /// <summary>A clip of a minute (less at the end of the file) where the slider is, outside the clips already there.</summary>
    void OnAddClip(object s, RoutedEventArgs e)
    {
        var from = Math.Round(SeekSlider.Value, 2);
        var to = Math.Min(duration, from + 60);
        if (clips.FirstOrDefault(c => c.From > from) is { } next) to = Math.Min(to, next.From);
        if (to - from < 1 || clips.Any(c => from >= c.From && from < c.To))
        {
            MessageBox.Show(this, "Placez l'aperçu en dehors des clips déjà là, devant au moins une seconde libre.", "Ajouter un clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        clips.Add(new Clip(from, to));
        clips.Sort((a, b) => a.From.CompareTo(b.From));
        ShowClips();
    }

    void OnClipsDefault(object s, RoutedEventArgs e)
    {
        clips.Clear();
        clips.Add(new Clip(0, duration));
        ShowClips();
    }

    /// <summary>The clips (coloured) and the chapters of the list (white) under the slider.</summary>
    void ShowMarks()
    {
        Marks.Children.Clear();
        var w = Marks.ActualWidth;
        if (w < 1 || duration <= 0) return;
        Marks.Children.Add(new Rectangle { Width = w, Height = 4, Fill = (Brush)FindResource("LineBrush") });
        foreach (var c in clips)
        {
            var bar = new Rectangle { Width = Math.Max(1, (c.To - c.From) / duration * w), Height = 4, Fill = (Brush)FindResource("AccentBrush") };
            Canvas.SetLeft(bar, Math.Max(0, c.From) / duration * w);
            Marks.Children.Add(bar);
        }
        if (ChapCustom.IsChecked != true) return;
        foreach (var t in times.Where(t => t > 0 && t < duration))
        {
            var tick = new Rectangle { Width = 2, Height = 8, Fill = Brushes.White };
            Canvas.SetLeft(tick, t / duration * w - 1);
            Marks.Children.Add(tick);
        }
    }

    void OnMarksSize(object s, SizeChangedEventArgs e) => ShowMarks();

    /// <summary>"Insertion automatique": the custom list filled with one chapter every N minutes of the disc, set at their times in the original file.</summary>
    void OnAutoInsert(object s, RoutedEventArgs e)
    {
        if (!(int.TryParse(EveryBox.Text.Trim(), out var minutes) && minutes is >= 1 and <= 600))
        {
            MessageBox.Show(this, "Entrez un nombre de minutes entre 1 et 600.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        times.Clear();
        var passed = 0.0; // disc time at the start of the clip
        foreach (var c in clips)
        {
            for (var at = Math.Ceiling(passed / (minutes * 60.0)) * minutes * 60; at < passed + c.To - c.From; at += minutes * 60)
                if (at > 0) times.Add(Math.Round(c.From + at - passed, 1));
            passed += c.To - c.From;
        }
        ChapCustom.IsChecked = true;
        ShowTimes();
    }

    void OnChapterMode(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        ListPanel.IsEnabled = ChapCustom.IsChecked == true;
        ShowMarks();
    }

    void ShowTimes()
    {
        ChapterList.ItemsSource = times.Select(Timecode.Format).ToList();
        if (ready) ShowMarks();
    }

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
        Effects = fx.IsEmpty ? null : fx;
        Clips = kept.Count > 1 ? kept : null;
        DialogResult = true;
    }
}
