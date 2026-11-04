using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>One video of the disc, with the tracks the user keeps (indexes into the source's own audio / subtitle lists).</summary>
    sealed class Entry(string path, SourceInfo info)
    {
        public string Path { get; } = path;
        public SourceInfo Info { get; } = info;
        public string Name { get; set; } = System.IO.Path.GetFileNameWithoutExtension(path);
        public List<int> Audio { get; set; } = info.Audio.Take(1).Select(a => a.Index).ToList();
        public List<int> Subs { get; set; } = [];
        public string Label
        {
            get
            {
                var t = TimeSpan.FromSeconds(Info.Duration);
                return $"{Name}   ·   {(int)t.TotalHours} h {t.Minutes:00} min   ·   {Info.Width}×{Info.Height}   ·   {Info.Audio.Count} audio, {Info.Subtitles.Count} sous-titres";
            }
        }
        public DiscTitle ToDisc() => new(Path, Name, Audio, Subs);
    }

    /// <summary>A checkbox line of the audio / subtitle lists.</summary>
    sealed class TrackItem
    {
        public int Index { get; init; }
        public string Text { get; init; } = "";
        public bool Checked { get; set; }
        public bool Enabled { get; init; }
    }

    readonly ObservableCollection<Entry> titles = [];
    string kind = "bd"; // dvd | bd | uhd
    string? previewFrame, previewFor;
    bool labelEdited, outEdited;
    MenuDesign design = TemplateCatalog.Default().Clone();

    public MainWindow()
    {
        InitializeComponent();
        design.PlayLabel = "Lecture"; design.ScenesLabel = "Scènes"; design.HomeLabel = "Accueil"; design.ChapterLabel = "Chapitre";
        design.PlayAllLabel = "Tout lire"; design.EpisodesLabel = "Épisodes"; design.SettingsLabel = "Réglages"; design.AudioLabel = "Audio";
        design.SubtitlesLabel = "Sous-titres"; design.OffLabel = "Désactivés"; design.BackLabel = "Retour";
        MenuName.Text = design.Name;
        TitleList.ItemsSource = titles;
        ChapterBox.ItemsSource = new[] { 1, 2, 3, 5, 10, 15 };
        ChapterBox.SelectedItem = 5;
        KindBd.IsChecked = true;
        RefreshDrives();
        Activated += (_, _) => RefreshDrives(); // a burner plugged in meanwhile shows up when the user comes back
        AfterTitlesChanged();
    }

    void RefreshDrives()
    {
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.Name.TrimEnd('\\')).ToList();
        if (!drives.SequenceEqual(DriveBox.Items.Cast<string>()))
        {
            DriveBox.ItemsSource = drives;
            DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
        }
        var idle = Busy.Visibility != Visibility.Visible;
        BurnButton.IsEnabled = drives.Count > 0 && idle;
        BurnCheck.IsEnabled = drives.Count > 0;
        if (drives.Count == 0) BurnCheck.IsChecked = false;
    }

    public Window CreateEditor() => new MenuEditorWindow(design, previewFrame, EditorContent());

    /// <summary>What the editor previews: the real chapters / episodes / tracks of the disc, or a sample while no video is loaded.</summary>
    MenuContent EditorContent()
    {
        if (titles.FirstOrDefault() is not { } first)
            return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, 12).ToList(), null, ["Français 5.1", "English 5.1"], ["Français", "English"]);
        var disc = first.ToDisc();
        var series = titles.Count > 1;
        var chapters = series ? 0 : BluRay.Chapters(first.Info.Duration, (int)ChapterBox.SelectedItem).Count;
        return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, chapters).ToList(),
            series ? titles.Select((t, i) => new TitleEntry(t.Name, i + 1, null)).ToList() : null,
            disc.KeptAudio(first.Info).Select(a => a.Name).ToList(), disc.KeptSubtitles(first.Info).Where(SupportsSub).Select(s => s.Name).ToList());
    }

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

    // a DVD can only carry text subtitles (redrawn as bitmaps); a Blu-ray also takes PGS
    bool SupportsSub(SubtitleTrack s) => kind == "dvd" ? s.IsText : s.Supported;

    void OnKind(object s, RoutedEventArgs e)
    {
        kind = (string)((RadioButton)s).Tag;
        CapacityBox.ItemsSource = kind == "dvd" ? Dvds : kind == "uhd" ? Uhds : Bds;
        CapacityBox.SelectedIndex = 0;
        foreach (var t in titles) t.Subs = t.Subs.Where(i => SupportsSub(t.Info.Subtitles.First(x => x.Index == i))).ToList();
        ShowTracks();
        Update();
    }

    void OnChanged(object s, SelectionChangedEventArgs e) => Update();

    void OnMenuToggle(object s, RoutedEventArgs e) => UpdateMenuState();

    /// <summary>Several videos can only be chosen from a menu, and a series menu lists episodes instead of chapters.</summary>
    void UpdateMenuState()
    {
        if (MenuButton == null || ChapterBox == null) return;
        var series = titles.Count > 1;
        if (series) MenuCheck.IsChecked = true;
        MenuCheck.IsEnabled = !series;
        MenuButton.IsEnabled = MenuCheck.IsChecked == true;
        ChapterBox.IsEnabled = !series;
    }

    void Update()
    {
        if (CapacityBox?.SelectedItem is not Cap cap || CapBar == null) return;
        var est = 0.0;
        var note = "";
        if (titles.Count > 0)
        {
            // shortcut: Blu-ray size = source file sizes (exact when the video is copied, off when re-encoded or tracks are dropped)
            est = kind == "dvd" ? Dvd.EstimateGB(titles.Select(t => (t.Info, t.ToDisc())).ToList(), cap.Usable) : titles.Sum(t => new FileInfo(t.Path).Length) / 1e9;
            if (est > cap.Usable) note = "  - trop grand pour ce support";
            if (kind == "uhd" && titles.Any(t => t.Info.VideoCodec != "hevc" || t.Info.Height != 2160)) note = "  - les sources doivent être en HEVC 2160p";
        }
        CapBar.Value = Math.Min(100, est / cap.Gb * 100);
        CapText.Text = $"{est:0.00} Go / {cap.Gb:0.#} Go ({cap.Name}){note}";
    }

    // ---- videos ----

    void OnOpen(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob|Tous les fichiers|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = AddFiles(dlg.FileNames);
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = AddFiles(files);
    }

    public Task LoadSource(string path) => AddFiles([path]);

    /// <summary>Probes and adds videos; several at once are put in natural order (episode 2 before episode 10).</summary>
    async Task AddFiles(IEnumerable<string> files)
    {
        var list = files.Where(File.Exists).ToList();
        list.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), CultureInfo.CurrentCulture, CompareOptions.NumericOrdering));
        foreach (var path in list)
        {
            try
            {
                var entry = new Entry(path, await Task.Run(() => BluRay.Probe(path)));
                // episodes usually share one layout: start from the first video's choice
                if (titles.FirstOrDefault() is { } first && first.Info.Audio.Count == entry.Info.Audio.Count)
                {
                    entry.Audio = first.Audio.ToList();
                    entry.Subs = first.Subs.Where(i => entry.Info.Subtitles.Any(t => t.Index == i && SupportsSub(t))).ToList();
                }
                titles.Add(entry);
            }
            catch (Exception ex)
            {
                Append($"Impossible de lire {Path.GetFileName(path)} : {ex.Message.Split('\n')[0]}");
            }
        }
        AfterTitlesChanged();
        if (titles.Count > 0 && previewFor != titles[0].Path)
        {
            var first = titles[0];
            previewFor = first.Path;
            previewFrame = await Task.Run(() => ExtractPreview(first.Path, first.Info));
        }
    }

    void AfterTitlesChanged()
    {
        NoSource.Visibility = titles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (titles.Count == 0) { previewFrame = previewFor = null; }
        else
        {
            var name = titles[0].Name;
            if (!labelEdited) LabelBox.Text = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9_]+", "_").Trim('_') is { Length: > 0 } l ? l[..Math.Min(32, l.Length)] : "MON_DISQUE";
            if (design.Title == new MenuDesign().Title) design.Title = name;
        }
        if (TitleList.SelectedItem == null && titles.Count > 0) TitleList.SelectedIndex = 0; // shows its tracks through OnTitleSelected
        else ShowTracks();
        UpdateMenuState();
        SyncOutput();
        Update();
    }

    /// <summary>One frame at 10% of the movie, used as the picture on the menu preview.</summary>
    static string? ExtractPreview(string path, SourceInfo info)
    {
        var file = Path.Combine(Path.GetTempPath(), "BurnSmith", "preview.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", (info.Duration * 0.1).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-frames:v", "1", "-q:v", "2", "-vf", "scale=800:450:force_original_aspect_ratio=increase,crop=800:450", file]);
            return File.Exists(file) ? file : null;
        }
        catch (Exception) { return null; } // the preview just shows an empty frame
    }

    void OnRemove(object s, RoutedEventArgs e)
    {
        if (TitleList.SelectedItem is not Entry entry) return;
        var i = titles.IndexOf(entry);
        titles.Remove(entry);
        TitleList.SelectedIndex = Math.Min(i, titles.Count - 1);
        AfterTitlesChanged();
    }

    void OnUp(object s, RoutedEventArgs e) => Move(-1);
    void OnDown(object s, RoutedEventArgs e) => Move(1);

    void Move(int step)
    {
        if (TitleList.SelectedItem is not Entry entry) return;
        var i = titles.IndexOf(entry);
        if (i + step < 0 || i + step >= titles.Count) return;
        titles.Move(i, i + step);
        TitleList.SelectedIndex = i + step;
    }

    void OnRename(object s, MouseButtonEventArgs e)
    {
        if (TitleList.SelectedItem is not Entry entry) return;
        var name = Prompt.Ask(this, "Nom affiché dans le menu", entry.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        entry.Name = name;
        var i = titles.IndexOf(entry);
        titles[i] = entry; // re-assigning makes the list redraw its label
        TitleList.SelectedIndex = i;
    }

    // ---- tracks ----

    void OnTitleSelected(object s, SelectionChangedEventArgs e) => ShowTracks();

    void ShowTracks()
    {
        var entry = TitleList.SelectedItem as Entry;
        TracksTitle.Text = entry == null ? "Pistes" : $"Pistes de « {entry.Name} »";
        ApplyAllButton.IsEnabled = entry != null && titles.Count > 1;
        RemoveButton.IsEnabled = UpButton.IsEnabled = DownButton.IsEnabled = entry != null;
        if (entry == null) { AudioList.ItemsSource = SubList.ItemsSource = null; return; }
        AudioList.ItemsSource = entry.Info.Audio.Select(a => new TrackItem
        {
            Index = a.Index, Text = $"{a.Name}  ({a.Codec})", Checked = entry.Audio.Contains(a.Index), Enabled = true,
        }).ToList();
        SubList.ItemsSource = entry.Info.Subtitles.Select(t => new TrackItem
        {
            Index = t.Index, Text = SupportsSub(t) ? $"{t.Name}  ({t.Codec})" : $"{t.Name}  ({t.Codec}, non pris en charge)",
            Checked = entry.Subs.Contains(t.Index), Enabled = SupportsSub(t),
        }).ToList();
    }

    /// <summary>
    /// Ticking or unticking one line updates just that track of the selected video, so it can run at any time
    /// (also while the lists are being filled, or when the box is toggled by an accessibility tool).
    /// </summary>
    void OnTrack(object s, RoutedEventArgs e)
    {
        var box = (CheckBox)s;
        if (box.DataContext is not TrackItem item || TitleList.SelectedItem is not Entry entry) return;
        var audio = AudioList.Items.Contains(item);
        if (!audio && !SubList.Items.Contains(item)) return; // a line of a list that has been replaced meanwhile
        var kept = audio ? entry.Audio : entry.Subs;
        if (box.IsChecked != true) kept.Remove(item.Index);
        else if (!kept.Contains(item.Index))
        {
            var max = audio ? MenuBuilder.MaxAudio : MenuBuilder.MaxSubtitles;
            if (kept.Count >= max)
            {
                box.IsChecked = false;
                Append($"Au plus {MenuBuilder.MaxAudio} pistes audio et {MenuBuilder.MaxSubtitles} sous-titres (limite de la page Réglages du menu).");
                return;
            }
            kept.Add(item.Index);
            kept.Sort(); // disc order = source order; the first one is the default
        }
        Update();
    }

    /// <summary>Copies the selected video's choice to the others, by track number.</summary>
    void OnApplyAll(object s, RoutedEventArgs e)
    {
        if (TitleList.SelectedItem is not Entry cur) return;
        foreach (var other in titles.Where(t => t != cur))
        {
            other.Audio = cur.Audio.Where(i => other.Info.Audio.Any(a => a.Index == i)).ToList();
            other.Subs = cur.Subs.Where(i => other.Info.Subtitles.Any(t => t.Index == i && SupportsSub(t))).ToList();
        }
        Append("Pistes appliquées à toutes les vidéos (par numéro de piste).");
        Update();
    }

    // ---- menu, output, build ----

    void OnEditMenu(object s, RoutedEventArgs e)
    {
        var editor = new MenuEditorWindow(design, previewFrame, EditorContent()) { Owner = this };
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
        if (titles.Count == 0) { MessageBox.Show(this, "Ajoutez d'abord une vidéo.", "BurnSmith"); return; }
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0) { MessageBox.Show(this, "Choisissez un dossier de sortie.", "BurnSmith"); return; }
        var cap = (Cap)CapacityBox.SelectedItem;
        var label = LabelBox.Text.Trim();
        var drive = DriveBox.SelectedItem as string;
        var burn = BurnCheck.IsChecked == true && drive != null;
        if (burn && MessageBox.Show(this,
                $"Après la création, graver « {label} » sur {drive} ?\n\nInsérez un disque vierge adapté ({cap.Name}). Le disque sera écrit définitivement.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var discTitles = titles.Select(t => t.ToDisc()).ToList();
        var menu = MenuCheck.IsChecked == true || discTitles.Count > 1 ? design.Clone() : null;
        var minutes = (int)ChapterBox.SelectedItem;
        var uhd = kind == "uhd";
        var dvd = kind == "dvd";
        var withIso = IsoCheck.IsChecked == true;
        Log.Clear();

        var ok = await Work(log =>
        {
            if (dvd) Dvd.Build(new DvdJob(discTitles, outDir, menu, minutes, cap.Usable), log);
            else BluRay.Build(new BluRayJob(discTitles, outDir, menu, uhd, label.Length > 0 ? label : "BURNSMITH", minutes), log);
            var iso = IsoFile(outDir);
            File.Delete(iso); // an image of an earlier build must never be burnt by mistake
            if (!withIso && !burn) return;
            Iso.Build(outDir, iso, label, dvd, log);
            if (burn) Iso.Burn(iso, drive!, log);
            if (!withIso) File.Delete(iso); // only made to burn
        });
        if (ok) Append("Terminé.");
    }

    // not "<folder>.iso": that name could belong to the user's own file
    static string IsoFile(string folder) => Path.TrimEndingDirectorySeparator(folder) + "_BurnSmith.iso";

    /// <summary>Burns an image that already exists (made earlier, or downloaded) on a DVD / Blu-ray / Blu-ray 4K.</summary>
    async void OnBurnIso(object s, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not string drive) { MessageBox.Show(this, "Aucun graveur détecté.", "BurnSmith"); return; }
        var dlg = new OpenFileDialog { Title = "Image à graver", Filter = "Images disque|*.iso|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var iso = dlg.FileName;
        var answer = MessageBox.Show(this,
            $"Graver « {Path.GetFileName(iso)} » sur {drive} ?\n\nInsérez un disque vierge adapté à la taille de l'image. Le disque sera écrit définitivement.",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (await Work(log => Iso.Burn(iso, drive, log))) Append("Terminé.");
    }
}
