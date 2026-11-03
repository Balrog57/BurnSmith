using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

public partial class App : Application
{
    /// <summary>
    /// burnsmith.exe [video]                      opens the window, optionally with a video already loaded
    /// burnsmith.exe [video] --snapshot out.png [editor]   renders the window (or the menu editor) off screen to a PNG and exits;
    ///                                            lets the UI be checked without touching the desktop
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var snap = Array.IndexOf(e.Args, "--snapshot");
        var video = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        var main = new MainWindow();
        Window window = e.Args.Contains("editor") ? main.CreateEditor() : main;

        if (snap < 0 || snap + 1 >= e.Args.Length) { main.Show(); if (video != null) await main.LoadSource(video); return; }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000; window.ShowInTaskbar = false;
        window.Show();
        if (video != null) await main.LoadSource(video);
        await Task.Delay(2500); // lets async previews finish
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(e.Args[snap + 1])) png.Save(f);
        Shutdown();
    }
}
