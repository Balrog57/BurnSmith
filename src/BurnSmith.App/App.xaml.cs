using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

public partial class App : Application
{
    /// <summary>
    /// burnsmith.exe [video]                      opens the window, optionally with a video already loaded
    /// burnsmith.exe [video] --snapshot out.png [design=template name] [editor[=template name]] [scroll[=pixels]] [lib=N] [tab=N] [button] [zones] | [advanced] | [edit] [focus]   renders the window (or the menu editor, on a template; scroll = bottom of its properties; lib / tab = the library / properties tab on show; button = the first library button dropped on the page; or the advanced settings; or the video edit)
    ///                                            off screen to a PNG and exits; lets the UI be checked without touching the desktop
    /// burnsmith.exe --try-install file.exe       runs that file as the update's installer: how a stand-in proves the window's side of an update
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        snapshotRun = e.Args.Contains("--snapshot");
        DispatcherUnhandledException += OnWindowError;
        // only these two see what runs off the window's thread; the app is dying in the first, so what it started must die with it
        AppDomain.CurrentDomain.UnhandledException += (_, x) => { BurnSmith.Core.AppFiles.LogError("Erreur fatale", x.ExceptionObject as Exception ?? new InvalidOperationException($"{x.ExceptionObject}")); BurnSmith.Core.Tools.KillAll(); };
        TaskScheduler.UnobservedTaskException += (_, x) => BurnSmith.Core.AppFiles.LogError("Tâche en arrière-plan", x.Exception);
        if (BurnSmith.Core.Updater.IsInstalled()) try { running = new System.Threading.Mutex(false, "BurnSmithRunning"); } catch (UnauthorizedAccessException) { } // installer\running.iss waits for it; another account's copy already holds that name
        BurnSmith.Core.AppFiles.Root(); // now, before the unpack below takes its lock: the log needs this answer without waiting
        BurnSmith.Core.Tools.Payload = () => BurnSmith.Payload.Payload.Open(); // a lambda: the large assembly loads only when this runs
        BurnSmith.Core.Tools.TemplatePayload = () => BurnSmith.Payload.Payload.OpenTemplates();
        // first run of a build: unpack tsMuxeR, the open menu library and the template pictures (see AppFiles) while the window comes up
        _ = Task.Run(() => { BurnSmith.Core.Tools.BundledDir(); BurnSmith.Core.Tools.TemplatesDir(); BurnSmith.Core.Menu.OpenLibrary.Dir(); });
        var snap = Array.IndexOf(e.Args, "--snapshot");
        var snapshot = snap >= 0 && snap + 1 < e.Args.Length;
        var videos = e.Args.Where((a, i) => !a.StartsWith("--") && (snap < 0 || i != snap + 1) && File.Exists(a)).ToList(); // not the snapshot's own output
        var main = new MainWindow();
        static void OffScreen(Window w) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -20000; w.ShowInTaskbar = false; }
        if (snapshot) OffScreen(main); // never flashes on the desktop
        main.Show();
        if (!snapshot && BurnSmith.Core.Updater.IsInstalled()) _ = CheckForUpdate(main); // while the videos load; never in a development build
        if (Array.IndexOf(e.Args, "--try-install") is var tryAt and >= 0 && tryAt + 1 < e.Args.Length) _ = InstallUpdate(main, () => Task.FromResult(e.Args[tryAt + 1]));
        foreach (var video in videos) await main.LoadSource(video);
        if (!snapshot) return;
        await main.DefaultDesignLoaded;
        if (e.Args.FirstOrDefault(a => a.StartsWith("design=")) is { } design && TemplateCatalog.Find(design[7..]) is { } start) main.UseDesign(start);

        Window window = main;
        if (e.Args.FirstOrDefault(a => a == "editor" || a.StartsWith("editor=")) is { } editor)
        {
            var win = main.CreateEditor(editor.Length > 7 ? TemplateCatalog.Find(editor[7..]) ?? Templates.List().FirstOrDefault(t => t.Name == editor[7..]) : null);
            window = win;
            OffScreen(window);
            window.Show();
            int? Number(string name) => e.Args.FirstOrDefault(a => a.StartsWith(name + "=")) is { } a && int.TryParse(a[(name.Length + 1)..], out var n) ? n : null;
            win.ShowTabs(Number("lib"), Number("tab"), e.Args.Contains("button"));
            if (e.Args.FirstOrDefault(a => a == "scroll" || a.StartsWith("scroll=")) is { } scroll) win.ScrollProps(scroll.Length > 7 ? double.Parse(scroll[7..], System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        else if (e.Args.FirstOrDefault(a => a == "edit" || a.StartsWith("edit=")) is { } edit) // edit=crop|effect|watermark|subtitle|chapters: that tab; with a video, its real frames
        {
            var info = videos.Count > 0 ? BurnSmith.Core.BluRay.Probe(videos[0]) : new BurnSmith.Core.SourceInfo("h264", 1920, 1080, 25, "ac3", 2, 5400, [], []);
            window = new VideoEditWindow(videos.FirstOrDefault() ?? "Épisode 1.mkv", info, "Épisode 1", 90, 3600, 120, [600, 1800, 3000], null, 5,
                                         new BurnSmith.Core.VideoEffects(Brightness: 10, WatermarkText: "BurnSmith", WatermarkSize: 64, BurnSubtitle: videos.Select(v => Path.ChangeExtension(v, ".srt")).FirstOrDefault(File.Exists)), edit.Length > 5 ? edit[5..] : null,
                                         [new(90, 600), new(900, 1500)]);
            OffScreen(window);
            window.Show();
        }
        else if (e.Args.FirstOrDefault(a => a.StartsWith("cut=")) is { } cut) { window = new CutWindow(cut[4..], BurnSmith.Core.BluRay.Probe(cut[4..]).Duration, 60, null); OffScreen(window); window.Show(); }
        else if (e.Args.Contains("advanced")) { window = new AdvancedWindow("MY_DISC", 5, new BurnSmith.Core.EncodeOptions(FitDisc: true), new BurnSmith.Core.PlaybackOptions(), "bd", Output.Bds, Output.DefaultBd); OffScreen(window); window.Show(); }
        if (e.Args.Contains("focus")) // selection and keyboard focus states: the second template of the list, the first text box (the output folder on the main window)
        {
            (window.FindName("BuiltInList") as System.Windows.Controls.ListBox)?.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1);
            (window.FindName("OutBox") as System.Windows.Controls.TextBox)?.Focus();
            if (window.FindName("CapacityBox") is System.Windows.Controls.ComboBox box) box.IsDropDownOpen = true; // builds the drop-down items with the theme's item style
        }
        await Task.Delay(2500); // lets async previews finish
        if (e.Args.Contains("zones") && window is MenuEditorWindow zoned) { zoned.TryZones(); await Task.Delay(2500); }
        if (e.Args.Contains("undo") && window is MenuEditorWindow undone) { await undone.TryUndo(); await Task.Delay(1000); }
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(e.Args[snap + 1])) png.Save(f);
        Shutdown();
    }

    bool snapshotRun, showingError;
    static System.Threading.Mutex? running; // static: held until the process ends

    /// <summary>
    /// An error nobody caught on the window's thread: written to the log and shown, and the app goes on, since a build running on another thread is not hurt.
    /// Out of memory, or a snapshot run with nobody to answer, still ends the app with a failing exit code; with no window left (it failed while opening) the app closes instead of staying hidden.
    /// </summary>
    void OnWindowError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        BurnSmith.Core.AppFiles.LogError("Erreur dans la fenêtre", e.Exception);
        if (snapshotRun || e.Exception is OutOfMemoryException) return;
        e.Handled = true;
        if (showingError) return; // an error at every redraw must not stack a dialog per redraw; each is logged
        showingError = true;
        var windowLeft = Windows.OfType<Window>().Any(w => w.IsVisible);
        MessageBox.Show($"Une erreur inattendue s'est produite :\n\n{e.Exception.Message}\n\nLe détail est enregistré dans :\n{BurnSmith.Core.AppFiles.ErrorLog()}\n\n" + (windowLeft ? "BurnSmith reste ouvert ; si quelque chose semble anormal, fermez-le et relancez-le." : "BurnSmith va se fermer."),
            "BurnSmith", MessageBoxButton.OK, MessageBoxImage.Error);
        showingError = false;
        if (!windowLeft) Shutdown(1);
    }

    /// <summary>Once per start of an installed copy: offers the newest GitHub release. Offline or up to date, it says nothing.</summary>
    static async Task CheckForUpdate(MainWindow owner)
    {
        var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
        BurnSmith.Core.Updater.Release? release;
        try { release = await BurnSmith.Core.Updater.LatestAsync(current); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return; } // the last two: an answer that is JSON but not a release (a proxy's page)
        if (release == null || MessageBox.Show(owner,
                $"BurnSmith {release.Version.ToString(3)} est disponible (vous avez la {current.ToString(3)}).\n\nLa télécharger et l'installer maintenant ? BurnSmith se ferme pendant l'installation puis redémarre.",
                "Mise à jour", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        await InstallUpdate(owner, () => BurnSmith.Core.Updater.DownloadAsync(release, new Progress<double>(p => owner.Title = $"Téléchargement de la mise à jour… {p:P0}")));
    }

    /// <summary>Gets the installer, waits for the work in progress, runs it. The installer closes the app when it can install; if it cannot, the app says so and stays.</summary>
    static async Task InstallUpdate(MainWindow owner, Func<Task<string>> getSetup)
    {
        var title = owner.Title;
        try
        {
            var setup = await getSetup();
            while (owner.Working) // the install closes the app: it waits for the disc; closed meanwhile, the next start offers it again
            {
                owner.Title = "Mise à jour téléchargée : elle s'installera à la fin du travail en cours";
                await Task.Delay(1000);
            }
            if (!owner.IsVisible) return;
            owner.IsEnabled = false; // Restart Manager closes the app whatever OnClosing says: no disc may start while the installer runs
            owner.Title = "Mise à jour : répondez à la demande d'administrateur de Windows…";
            await BurnSmith.Core.Updater.Install(setup);
            Current.Shutdown();
        }
        catch (Exception e)
        {
            owner.IsEnabled = true;
            owner.Title = title;
            MessageBox.Show(owner, $"La mise à jour n'a pas pu être installée : {e.Message}.\n\nBurnSmith reste ouvert. Elle sera proposée de nouveau au prochain démarrage ; vous pouvez aussi la télécharger sur https://github.com/Balrog57/BurnSmith/releases/latest", "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
