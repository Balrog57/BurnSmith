using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BurnSmith.Core;

namespace BurnSmith.App;

public partial class VideoEditWindow
{
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
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        // an HDR source is made SDR once small: in time for the playback
        foreach (var a in new[] { "-v", "error", "-re", "-ss", N(from), "-i", video, "-an", "-sn", "-vf", $"fps={Fps},scale={w}:{h}" + (BluRay.ToSdr(info.Hdr) is { Length: > 0 } sdr ? "," + sdr : ""), "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" })
            psi.ArgumentList.Add(a);
        Process? ffmpeg = null;
        try
        {
            ffmpeg = Process.Start(psi)!;
            Tools.TieToApp(ffmpeg);
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
        if (dragSides.L) left = Math.Clamp(left + dx, 0, Math.Max(0, fw - right - min)); // Max: margins typed larger than the picture would make Clamp throw
        if (dragSides.R) right = Math.Clamp(right - dx, 0, Math.Max(0, fw - left - min));
        if (dragSides.T) top = Math.Clamp(top + dy, 0, Math.Max(0, fh - bottom - min));
        if (dragSides.B) bottom = Math.Clamp(bottom - dy, 0, Math.Max(0, fh - top - min));
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
        var (left, top) = (Math.Clamp(Int(LeftBox.Text, 0), 0, Math.Max(0, fw - 16)), Math.Clamp(Int(TopBox.Text, 0), 0, Math.Max(0, fh - 16)));
        var w = Math.Clamp(Int(CropWBox.Text, fw), 16, Math.Max(16, fw - left));
        var h = Math.Clamp(Int(CropHBox.Text, fh), 16, Math.Max(16, fh - top));
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
}
