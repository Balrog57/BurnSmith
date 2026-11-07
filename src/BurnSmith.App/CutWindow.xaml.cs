using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>
/// The cut: the video with a playhead, a strip of frames, and the kept part between two handles.
/// Times are those of the original file; the result is read from <see cref="Start"/> and <see cref="End"/> after OK.
/// </summary>
public partial class CutWindow : Window
{
    public double Start { get; private set; }
    /// <summary>Null = to the end of the file.</summary>
    public double? End { get; private set; }

    const int Thumbs = 8, PlayW = 640, PlayH = 360, Fps = 25;
    readonly string path;
    readonly double duration;
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "BurnSmith", "cut");
    double start, end, pos;
    string? dragging;
    int seekVersion;
    CancellationTokenSource? playing;

    public CutWindow(string path, double duration, double start, double? end)
    {
        InitializeComponent();
        this.path = path;
        this.duration = duration;
        this.start = start;
        this.end = end ?? duration;
        pos = start;
        Title = "Couper - " + Path.GetFileName(path);
        TotalText.Text = "/ " + Timecode.Format(duration);
        Loaded += async (_, _) => { ShowTimes(); await Seek(); await FillStrip(); };
        Closed += (_, _) => playing?.Cancel();
    }

    void ShowTimes()
    {
        PosBox.Text = Timecode.Format(pos);
        StartBox.Text = Timecode.Format(start);
        EndBox.Text = Timecode.Format(end);
        PosText.Text = $"{Timecode.Format(pos)} / {Timecode.Format(duration)}";
        KeptText.Text = $"Durée gardée : {Timecode.Format(end - start)}";
        Layout();
    }

    double X(double t) => t / duration * Track.ActualWidth;

    void Layout()
    {
        var w = Track.ActualWidth;
        if (w <= 0) return;
        Rail.Width = w;
        Canvas.SetLeft(Kept, X(start));
        Kept.Width = Math.Max(0, X(end) - X(start));
        Canvas.SetLeft(StartHandle, X(start) - 3);
        Canvas.SetLeft(EndHandle, X(end) - 3);
        Canvas.SetLeft(Playhead, X(pos) - 1);
    }

    void OnTrackSize(object s, SizeChangedEventArgs e)
    {
        Layout();
        // the ruler: eleven times
        Ruler.Children.Clear();
        for (var i = 0; i <= 10; i++)
        {
            var label = new TextBlock { Text = Timecode.Format(Math.Floor(duration * i / 10)), FontSize = 11, Opacity = 0.6 };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(4 + X(duration * i / 10) - label.DesiredSize.Width / 2, 0, Math.Max(0, Ruler.ActualWidth - label.DesiredSize.Width)));
            Canvas.SetTop(label, 2);
            Ruler.Children.Add(label);
        }
    }

    /// <summary>The frames of the strip, evenly spread; slow sources fill it a little later.</summary>
    async Task FillStrip()
    {
        var made = new string?[Thumbs];
        for (var i = 0; i < Thumbs; i++) Strip.Children.Add(new Image { Stretch = Stretch.UniformToFill });
        await Task.Run(() => Parallel.For(0, Thumbs, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => made[i] = MainWindow.ExtractFrame(path, duration * (i + 0.5) / Thumbs, Path.Combine(tempDir, $"strip{i}.jpg"), 160, 90)));
        for (var i = 0; i < Thumbs; i++) ((Image)Strip.Children[i]).Source = MainWindow.LoadImage(made[i]);
    }

    /// <summary>The picture at the playhead; a newer move wins over a frame still being read.</summary>
    async Task Seek()
    {
        if (playing != null) return;
        var mine = ++seekVersion;
        await Task.Delay(120); // a drag asks for many frames: only the last one is read
        if (mine != seekVersion) return;
        var file = Path.Combine(tempDir, $"pos{mine % 2}.jpg");
        var at = pos;
        var made = await Task.Run(() => MainWindow.ExtractFrame(path, at, file, 1280, 720, fill: false));
        if (mine == seekVersion && playing == null) Screen.Source = MainWindow.LoadImage(made);
    }

    void MoveTo(string what, double t)
    {
        t = Math.Clamp(t, 0, duration);
        switch (what)
        {
            case "start": start = Math.Min(t, end - 1); pos = start; break;
            case "end": end = Math.Max(t, start + 1); pos = end; break;
            default: pos = t; break;
        }
        Stop();
        ShowTimes();
        _ = Seek();
    }

    // ---- the track: the nearest of the two handles (within a few pixels) is dragged, else the playhead ----

    void OnTrackDown(object s, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(Track).X;
        dragging = Math.Abs(x - X(start)) <= 6 ? "start" : Math.Abs(x - X(end)) <= 6 ? "end" : "pos";
        Track.CaptureMouse();
        OnTrackMove(s, e);
    }

    void OnTrackMove(object s, MouseEventArgs e)
    {
        if (dragging != null && Track.ActualWidth > 0) MoveTo(dragging, e.GetPosition(Track).X / Track.ActualWidth * duration);
    }

    void OnTrackUp(object s, MouseButtonEventArgs e)
    {
        dragging = null;
        Track.ReleaseMouseCapture();
    }

    void OnStripClick(object s, MouseButtonEventArgs e) => MoveTo("pos", e.GetPosition(Strip).X / Strip.ActualWidth * duration);

    // ---- the buttons and the time boxes ----

    void OnToStart(object s, RoutedEventArgs e) => MoveTo("pos", start);
    void OnToEnd(object s, RoutedEventArgs e) => MoveTo("pos", end);
    void OnMark(object s, RoutedEventArgs e) => MoveTo((string)((Button)s).Tag, pos);

    void OnStep(object s, RoutedEventArgs e)
    {
        var parts = ((string)((Button)s).Tag).Split(':');
        var now = parts[0] switch { "start" => start, "end" => end, _ => pos };
        MoveTo(parts[0], now + int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    void OnTimeKey(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnTimeEdited(s, e);
        e.Handled = true; // Enter in a time box takes the time, it does not close the window
    }

    void OnTimeEdited(object s, RoutedEventArgs e)
    {
        var box = (TextBox)s;
        var what = box == StartBox ? "start" : box == EndBox ? "end" : "pos";
        if (Timecode.Parse(box.Text) is { } t) MoveTo(what, t);
        else ShowTimes(); // not a time: the box shows the current one again
    }

    // ---- playing: ffmpeg decodes from the playhead at the video's own pace, its frames are drawn as they come ----

    void OnPlay(object s, RoutedEventArgs e)
    {
        if (playing != null) Stop();
        else _ = Play();
    }

    void Stop()
    {
        playing?.Cancel();
        playing = null;
        PlayButton.Content = "";
    }

    async Task Play()
    {
        if (pos >= end - 0.5) pos = start;
        var cts = playing = new CancellationTokenSource();
        PlayButton.Content = "";
        var from = pos;
        var psi = new ProcessStartInfo(Tools.Find("ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-re", "-ss", from.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-an", "-sn",
                     "-vf", $"fps={Fps},scale={PlayW}:{PlayH}:force_original_aspect_ratio=decrease,pad={PlayW}:{PlayH}:-1:-1", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" })
            psi.ArgumentList.Add(a);
        Process? ffmpeg = null;
        try
        {
            ffmpeg = Process.Start(psi)!;
            var bmp = new WriteableBitmap(PlayW, PlayH, 96, 96, PixelFormats.Bgr24, null);
            Screen.Source = bmp;
            var frame = new byte[PlayW * PlayH * 3];
            for (var n = 1; ; n++)
            {
                await ffmpeg.StandardOutput.BaseStream.ReadExactlyAsync(frame, cts.Token);
                bmp.WritePixels(new Int32Rect(0, 0, PlayW, PlayH), frame, PlayW * 3, 0);
                pos = Math.Min(from + (double)n / Fps, duration);
                ShowTimes();
                if (pos >= end) break; // the end of the kept part
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { } // the end of the file
        catch (Exception ex) { MessageBox.Show(this, "Lecture impossible : " + ex.Message, "Couper", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            try { ffmpeg?.Kill(); } catch (Exception) { } // already gone
            ffmpeg?.Dispose();
            if (playing == cts) Stop();
        }
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        Stop();
        Start = start < 0.05 ? 0 : start;
        End = end < duration - 0.05 ? end : null; // an end at the end of the file is no cut at all
        DialogResult = true;
    }
}
