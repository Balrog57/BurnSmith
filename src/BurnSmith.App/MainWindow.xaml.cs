using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BurnSmith.Core;
using BurnSmith.Core.Menu;
using Microsoft.Win32;

namespace BurnSmith.App;

public partial class MainWindow : Window
{
    sealed record Cap(string Name, double Gb, double Usable)
    {
        public override string ToString() => $"{Name} ({Gb:0.#} Go)";
    }

    // nominal size, and the part a build may fill (rest = file system and menu overhead)
    static readonly Cap[] Dvds = [new("DVD-5", 4.7, 4.2), new("DVD-9", 8.5, 7.6)];
    static readonly Cap[] Bds = [new("BD-25", 25, 23.3), new("BD-50", 50, 46.5)];
    static readonly Cap[] Uhds = [new("BD-66", 66, 61), new("BD-100", 100, 93)];

    sealed record Disc(string Folder, string Label, bool Dvd, string Support);

    Disc? lastDisc; // the last successful build: what "Graver" writes
    string kind = "bd"; // dvd | bd | uhd
    SourceInfo? src;
    string? srcPath, previewFrame;
    bool labelEdited, outEdited;
    MenuDesign design = MenuDesign.BuiltIn[0].Clone();

    public MainWindow()
    {
        InitializeComponent();
        design.PlayLabel = "Lecture"; design.ScenesLabel = "Scènes"; design.HomeLabel = "Accueil"; design.ChapterLabel = "Chapitre";
        MenuName.Text = design.Name;
        ChapterBox.ItemsSource = new[] { 1, 2, 3, 5, 10, 15 };
        ChapterBox.SelectedItem = 5;
        KindBd.IsChecked = true;
        RefreshDrives();
        Activated += (_, _) => RefreshDrives(); // a burner plugged in meanwhile shows up when the user comes back
        SyncOutput();
    }

    void RefreshDrives()
    {
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.Name.TrimEnd('\\')).ToList();
        if (!drives.SequenceEqual(DriveBox.Items.Cast<string>()))
        {
            DriveBox.ItemsSource = drives;
            DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
        }
        BurnButton.IsEnabled = lastDisc != null && drives.Count > 0 && Busy.Visibility != Visibility.Visible;
    }

    public Window CreateEditor() => new MenuEditorWindow(design, previewFrame, 12);

    void Append(string line)
    {
        Log.AppendText(line + Environment.NewLine);
        Log.ScrollToEnd();
    }

    void SyncOutput()
    {
        if (!outEdited)
            OutBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BurnSmith", LabelBox.Text.Trim());
    }

    void OnLabelTyped(object s, TextCompositionEventArgs e) => labelEdited = true;
    void OnOutTyped(object s, TextCompositionEventArgs e) => outEdited = true;

    void OnKind(object s, RoutedEventArgs e)
    {
        kind = (string)((RadioButton)s).Tag;
        CapacityBox.ItemsSource = kind == "dvd" ? Dvds : kind == "uhd" ? Uhds : Bds;
        CapacityBox.SelectedIndex = 0;
    }

    void OnChanged(object s, SelectionChangedEventArgs e) => Update();

    void OnMenuToggle(object s, RoutedEventArgs e) { if (MenuButton != null) MenuButton.IsEnabled = MenuCheck.IsChecked == true; }

    void Update()
    {
        if (CapacityBox?.SelectedItem is not Cap cap || CapBar == null) return;
        var est = 0.0;
        var note = "";
        if (src != null)
        {
            // shortcut: Blu-ray size = source file size (exact when the video is copied, an under-estimate when it is re-encoded)
            est = kind == "dvd" ? Dvd.EstimateGB(src, cap.Usable) : new FileInfo(srcPath!).Length / 1e9;
            if (est > cap.Usable) note = "  - trop grand pour ce support";
            if (kind == "uhd" && (src.VideoCodec != "hevc" || src.Height != 2160)) note = "  - la source doit être en HEVC 2160p";
        }
        CapBar.Value = Math.Min(100, est / cap.Gb * 100);
        CapText.Text = $"{est:0.00} Go / {cap.Gb:0.#} Go ({cap.Name}){note}";
    }

    void OnOpen(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) == true) _ = LoadSource(dlg.FileName);
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = LoadSource(files[0]);
    }

    public async Task LoadSource(string path)
    {
        try
        {
            var info = await Task.Run(() => BluRay.Probe(path));
            src = info;
            srcPath = path;
            NoSource.Visibility = Visibility.Collapsed;
            SourceInfo.Visibility = Visibility.Visible;
            SrcName.Text = Path.GetFileName(path);
            var t = TimeSpan.FromSeconds(info.Duration);
            SrcVideo.Text = $"{info.Width}×{info.Height} · {info.VideoCodec} · {info.Fps:0.###} ips · {(int)t.TotalHours} h {t.Minutes:00} min";
            SrcAudio.Text = info.AudioCodec == "" ? "Pas de son" : $"Audio : {info.AudioCodec}, {info.AudioChannels} canaux";

            var name = Path.GetFileNameWithoutExtension(path);
            if (!labelEdited) LabelBox.Text = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9_]+", "_").Trim('_') is { Length: > 0 } l ? l[..Math.Min(32, l.Length)] : "MON_DISQUE";
            if (design.Title == new MenuDesign().Title) design.Title = name;
            SyncOutput();
            Update();
            previewFrame = await Task.Run(() => ExtractPreview(path, info));
        }
        catch (Exception ex)
        {
            Append("Impossible de lire cette vidéo : " + ex.Message);
        }
    }

    /// <summary>One frame at 10% of the movie, used as the picture on the menu preview.</summary>
    static string? ExtractPreview(string path, SourceInfo info)
    {
        var file = Path.Combine(Path.GetTempPath(), "BurnSmith", "preview.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", (info.Duration * 0.1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", path, "-frames:v", "1", "-q:v", "2", "-vf", "scale=800:450:force_original_aspect_ratio=increase,crop=800:450", file]);
            return File.Exists(file) ? file : null;
        }
        catch (Exception) { return null; } // the preview just shows an empty frame
    }

    void OnEditMenu(object s, RoutedEventArgs e)
    {
        var chapters = src == null ? 12 : BluRay.Chapters(src.Duration, (int)ChapterBox.SelectedItem).Count;
        var editor = new MenuEditorWindow(design, previewFrame, chapters) { Owner = this };
        if (editor.ShowDialog() != true) return;
        design = editor.Result;
        MenuName.Text = design.Name;
    }

    void OnBrowseOut(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier du disque" };
        if (dlg.ShowDialog(this) != true) return;
        OutBox.Text = dlg.FolderName;
        outEdited = true;
    }

    /// <summary>Runs one long job off the UI thread, with the busy state and a log line per step. Returns false when it failed.</summary>
    async Task<bool> Work(Action<Action<string>> job)
    {
        StartButton.IsEnabled = BurnButton.IsEnabled = false;
        Busy.IsIndeterminate = true;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => job(line => Dispatcher.BeginInvoke(() => Append(line))));
            return true;
        }
        catch (Exception ex)
        {
            Append("ERREUR : " + ex.Message);
            return false;
        }
        finally
        {
            StartButton.IsEnabled = true;
            Busy.IsIndeterminate = false;
            Busy.Visibility = Visibility.Hidden;
            RefreshDrives();
        }
    }

    async void OnStart(object s, RoutedEventArgs e)
    {
        if (src == null || srcPath == null) { MessageBox.Show(this, "Choisissez d'abord une vidéo.", "BurnSmith"); return; }
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0) { MessageBox.Show(this, "Choisissez un dossier de sortie.", "BurnSmith"); return; }
        var video = srcPath;
        var menu = MenuCheck.IsChecked == true ? design.Clone() : null;
        var minutes = (int)ChapterBox.SelectedItem;
        var cap = (Cap)CapacityBox.SelectedItem;
        var label = LabelBox.Text.Trim();
        var uhd = kind == "uhd";
        var dvd = kind == "dvd";
        var withIso = IsoCheck.IsChecked == true;
        lastDisc = null;
        Log.Clear();

        var ok = await Work(log =>
        {
            if (dvd) Dvd.Build(new DvdJob(video, outDir, menu, minutes, cap.Usable), log);
            else BluRay.Build(new BluRayJob(video, outDir, menu, uhd, label.Length > 0 ? label : "BURNSMITH", minutes), log);
            File.Delete(IsoFile(outDir)); // an image of an earlier build must never be burnt by mistake
            if (withIso) Iso.Build(outDir, IsoFile(outDir), label, dvd, log);
        });
        if (!ok) return;
        lastDisc = new Disc(outDir, label, dvd, cap.Name);
        RefreshDrives();
        Append("Terminé.");
    }

    // not "<folder>.iso": that name could belong to the user's own file
    static string IsoFile(string folder) => Path.TrimEndingDirectorySeparator(folder) + "_BurnSmith.iso";

    async void OnBurn(object s, RoutedEventArgs e)
    {
        if (lastDisc is not { } disc || DriveBox.SelectedItem is not string drive) return;
        var answer = MessageBox.Show(this,
            $"Graver « {disc.Label} » sur {drive} ?\n\nInsérez un disque vierge adapté ({disc.Support}). Le disque sera écrit définitivement.",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (await Work(log =>
        {
            var iso = IsoFile(disc.Folder);
            if (!File.Exists(iso)) Iso.Build(disc.Folder, iso, disc.Label, disc.Dvd, log);
            Iso.Burn(iso, drive, log);
        })) Append("Terminé.");
    }
}
