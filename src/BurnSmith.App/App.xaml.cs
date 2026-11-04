using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

public partial class App : Application
{
    /// <summary>
    /// burnsmith.exe [video]                      opens the window, optionally with a video already loaded
    /// burnsmith.exe [video] --snapshot out.png [editor[=template name]] [scroll] | [advanced] [focus]   renders the window (or the menu editor, on a template; scroll = bottom of its properties; or the advanced settings)
    ///                                            off screen to a PNG and exits; lets the UI be checked without touching the desktop
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BurnSmith.Core.Tools.Payload = () => BurnSmith.Payload.Payload.Open(); // a lambda: the 170 MB assembly loads only when this runs
        _ = Task.Run(() => BurnSmith.Core.Tools.BundledDir()); // first run: unpack ffmpeg and tsMuxeR while the window comes up
        var snap = Array.IndexOf(e.Args, "--snapshot");
        var snapshot = snap >= 0 && snap + 1 < e.Args.Length;
        var videos = e.Args.Where((a, i) => !a.StartsWith("--") && (snap < 0 || i != snap + 1) && File.Exists(a)).ToList(); // not the snapshot's own output
        var main = new MainWindow();
        static void OffScreen(Window w) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -20000; w.ShowInTaskbar = false; }
        if (snapshot) OffScreen(main); // never flashes on the desktop
        main.Show();
        foreach (var video in videos) await main.LoadSource(video);
        if (!snapshot) return;

        Window window = main;
        if (e.Args.FirstOrDefault(a => a == "editor" || a.StartsWith("editor=")) is { } editor)
        {
            var win = main.CreateEditor(editor.Length > 7 ? TemplateCatalog.Find(editor[7..]) ?? Templates.List().FirstOrDefault(t => t.Name == editor[7..]) : null);
            window = win;
            OffScreen(window);
            window.Show();
            if (e.Args.Contains("scroll")) win.ScrollPropsToEnd();
        }
        else if (e.Args.Contains("advanced")) { window = new AdvancedWindow("MY_DISC", 5); OffScreen(window); window.Show(); }
        if (e.Args.Contains("focus")) // selection and keyboard focus states: the second template of the list, the first text box (the output folder on the main window)
        {
            (window.FindName("BuiltInList") as System.Windows.Controls.ListBox)?.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1);
            (window.FindName("OutBox") as System.Windows.Controls.TextBox)?.Focus();
            if (window.FindName("CapacityBox") is System.Windows.Controls.ComboBox box) box.IsDropDownOpen = true; // builds the drop-down items with the theme's item style
        }
        await Task.Delay(2500); // lets async previews finish
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(e.Args[snap + 1])) png.Save(f);
        Shutdown();
    }
}
