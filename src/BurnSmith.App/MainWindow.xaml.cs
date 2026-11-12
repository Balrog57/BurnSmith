using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BurnSmith.Core;
using BurnSmith.Core.Menu;
using Microsoft.Win32;

namespace BurnSmith.App;

public partial class MainWindow : Window
{
    /// <summary>One video of the disc, with the tracks the user keeps (indexes into the source's own audio / subtitle lists).</summary>
    sealed class Entry(string path, SourceInfo info) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Redraws the row after Name, Audio or Subs changed.</summary>
        public void Refresh() => PropertyChanged?.Invoke(this, new(string.Empty));

        public string Path { get; } = path;
        public SourceInfo Info { get; } = info;
        public string Name { get; set; } = DiscTitle.Readable(path).Name;
        public List<int> Audio { get; set; } = info.Audio.Take(1).Select(a => a.Index).ToList();
        public List<int> Subs { get; set; } = [];
        /// <summary>Subtitle files beside the video (found by name or added by hand): more tracks, numbered after the video's own.</summary>
        public List<ExternalSubtitle> Extra { get; set; } = [];
        // the cut (see DiscTitle): part kept, volume in %, chapter times in the original file (null = the disc's setting, empty = none)
        public double Start { get; set; }
        public double? End { get; set; }
        public int Volume { get; set; } = 100;
        public IReadOnlyList<double>? ChapterAt { get; set; }
        /// <summary>One chapter every N minutes for this video (null = the disc's setting); used when there is no <see cref="ChapterAt"/> list.</summary>
        public int? ChapterMinutes { get; set; }
        /// <summary>An extra (trailer, making-of): after the feature on the disc, off "Lecture" / "Tout lire", on the menu's Bonus page.</summary>
        public bool Bonus { get; set; }
        /// <summary>The video that plays when the disc starts, before the menu (one at most; not a title of the menu; Blu-ray only).</summary>
        public bool Intro { get; set; }
        /// <summary>The audio track that plays and the subtitle that shows when the disc starts (source track numbers among those kept; null = the first audio, no subtitle).</summary>
        public int? DefaultAudio { get; set; }
        public int? DefaultSub { get; set; }
        /// <summary>Languages set by hand on the video's own tracks (see <see cref="DiscTitle.TrackLanguage"/>).</summary>
        public Dictionary<string, string> TrackLanguage { get; set; } = [];
        /// <summary>Rotation, crop, colour, watermark and burnt subtitle of the video editor (null = none).</summary>
        public VideoEffects? Effects { get; set; }
        /// <summary>The parts kept when there are several (see <see cref="DiscTitle.Clips"/>; null = only <see cref="Start"/> to <see cref="End"/>).</summary>
        public IReadOnlyList<Clip>? Clips { get; set; }
        /// <summary>The name as the table shows it.</summary>
        public string Shown => Intro ? $"Début · {Name}" : Bonus ? $"Bonus · {Name}" : Name;

        /// <summary>The source as the disc sees it: its duration is the part kept.</summary>
        public SourceInfo OnDisc => ToDisc().Cut(Info);

        /// <summary>The video as the track lists see it: its own audio and subtitles, then the subtitle files.</summary>
        public SourceInfo Tracks => ToDisc().WithSubtitleFiles(Info);

        /// <summary>The length on the disc, which is the cut one once the video was edited.</summary>
        public string DurationText => Timecode.Format(Math.Floor(OnDisc.Duration));
        public string? DurationTip => ToDisc() is { Trimmed: true } ? $"Durée d'origine : {Timecode.Format(Math.Floor(Info.Duration))}" : null;
        public string Hdr => Info.Stereo.Length > 0 ? Info.Hdr + " 3D" : Info.Hdr; // 3D side by side or top and bottom: made half-size for a 3D TV
        public string AudioText => Summary(ToDisc().KeptAudio(Tracks).Select(a => a.Name).ToList()); // in disc order: the default one first
        public string SubText => Summary(Subs.OrderBy(i => i == DefaultSub ? 0 : 1).Select(i => (Tracks.Subtitles.FirstOrDefault(t => t.Index == i)?.Name ?? "?") + (i == DefaultSub ? " (défaut)" : "")).ToList());

        static string Summary(List<string> names) => names.Count == 0 ? "Aucun" : names.Count == 1 ? names[0] : $"{names[0]} +{names.Count - 1}";
        /// <summary>The menu thumbnail: a moment of the video on the disc, or a picture (see <see cref="DiscTitle.ThumbAt"/>); both null = automatic.</summary>
        public double? ThumbAt { get; set; }
        public string? ThumbImage { get; set; }
        /// <summary>The summary written under the video's name on the menu pages that show one (Streaming).</summary>
        public string? Overview { get; set; }
        /// <summary>Seconds each kept subtitle shows later (earlier when negative), by its number in <see cref="Subs"/>.</summary>
        public Dictionary<int, double> SubDelay { get; set; } = [];
        /// <summary>Seconds each kept audio track is heard later (earlier when negative), by its number in <see cref="Audio"/>.</summary>
        public Dictionary<int, double> AudioDelay { get; set; } = [];
        /// <summary>The names of the chapters of <see cref="ChapterAt"/>, one for one (null = "Chapitre N" on the menu).</summary>
        public IReadOnlyList<string?>? ChapterTitles { get; set; }
        public DiscTitle ToDisc() => new(Path, Name, Audio, Subs, Start, End, Volume, ChapterAt, Extra, Bonus, DefaultAudio, DefaultSub, ChapterMinutes, Intro, TrackLanguage, Effects, Clips, ThumbAt, ThumbImage, Overview, SubDelay, AudioDelay, ChapterTitles);

        /// <summary>The row of a title read from a project or an undo step.</summary>
        public static Entry From(DiscTitle d, SourceInfo info) => new(d.Video, info)
        {
            Name = d.DisplayName, Audio = [.. d.Audio ?? info.Audio.Take(1).Select(a => a.Index)], Subs = [.. d.Subtitles ?? []], Extra = [.. d.External ?? []], Start = d.Start, End = d.End, Volume = d.Volume, ChapterAt = d.ChapterAt,
            ChapterMinutes = d.ChapterMinutes, Bonus = d.Bonus, Intro = d.Intro, DefaultAudio = d.DefaultAudio, DefaultSub = d.DefaultSubtitle, TrackLanguage = new(d.TrackLanguage ?? new Dictionary<string, string>()),
            Effects = d.Effects, Clips = d.Clips, ThumbAt = d.ThumbAt, ThumbImage = d.ThumbImage, Overview = d.Overview, SubDelay = new(d.SubtitleDelay ?? new Dictionary<int, double>()),
            AudioDelay = new(d.AudioDelay ?? new Dictionary<int, double>()), ChapterTitles = d.ChapterTitles,
        };

        /// <summary>Another row for the same video with the same settings (a title cut in two).</summary>
        public Entry Clone() => new(Path, Info)
        {
            Name = Name, Audio = [.. Audio], Subs = [.. Subs], Extra = [.. Extra], Start = Start, End = End, Volume = Volume, ChapterAt = ChapterAt,
            ChapterMinutes = ChapterMinutes, Bonus = Bonus, Intro = Intro, DefaultAudio = DefaultAudio, DefaultSub = DefaultSub, TrackLanguage = new(TrackLanguage), Effects = Effects, Clips = Clips,
            ThumbAt = ThumbAt, ThumbImage = ThumbImage, Overview = Overview, SubDelay = new(SubDelay), AudioDelay = new(AudioDelay), ChapterTitles = ChapterTitles,
        };
    }

    readonly ObservableCollection<Entry> titles = [];
    string kind = "bd"; // dvd | bd | uhd
    string volume = "MON_DISQUE";
    int chapterMinutes = 5;
    EncodeOptions encode = new(FitDisc: true); // "Adapter au disque": the video stays as it is while it fits
    PlaybackOptions playback = new(); // menu, a picked video followed by the next ones, back to the menu after the last (as the studios' series discs)
    bool hdmvMenu; // Blu-ray: the menu as native HDMV navigation instead of the BD-J program (Paramètres avancés)
    string? previewFrame, previewFor;
    bool labelEdited, outEdited, busy, refreshingDrives;
    int thumbVersion;
    CancellationTokenSource? cancel;
    volatile bool writing; // a burn or an erase is under way: stopping it can ruin the disc
    bool closeAfterWork;
    int making; // slideshows, songs and joins being made: long ffmpeg runs without a Cancel button
    MenuDesign design = new();
    readonly Task defaultDesign;

    /// <summary>For the off-screen snapshot: what it shows must start from the default template.</summary>
    public Task DefaultDesignLoaded => defaultDesign;

    /// <summary>A disc or a video is being made: nothing may shut the app down now.</summary>
    public bool Working => busy || making > 0;

    public MainWindow()
    {
        InitializeComponent();
        VersionRun.Text = " " + typeof(App).Assembly.GetName().Version?.ToString(3);
        FrenchLabels();
        MenuName.Text = "Chargement…";
        TitleList.ItemsSource = titles;
        SetKind("bd");
        RefreshDrives();
        Activated += (_, _) => RefreshDrives(); // a burner plugged in meanwhile shows up when the user comes back
        AfterTitlesChanged();
        Closed += (_, _) => Tools.TryDelete(frameDir);
        defaultDesign = LoadDefaultDesign();
        watch.Tick += (_, _) => Remember();
        watch.Start();
        Closed += (_, _) => watch.Stop();
    }

    /// <summary>The default template, off the UI thread: the scan waits for the exe's files to unpack. A template chosen meanwhile stays; if the scan fails the blank design does.</summary>
    async Task LoadDefaultDesign()
    {
        var blank = design;
        try
        {
            var found = await Task.Run(() => MenuRenderer.Sta(() => TemplateCatalog.Default().Clone()));
            if (ReferenceEquals(design, blank)) UseDesign(found);
        }
        catch (Exception e)
        {
            AppFiles.LogError("Modèle de menu par défaut", e);
            if (ReferenceEquals(design, blank)) MenuName.Text = design.Name;
        }
    }

    /// <summary>True when the default template is still loading: what the user would edit or build would not be it. Says so.</summary>
    bool StillLoadingDesign()
    {
        if (defaultDesign.IsCompleted) return false;
        MessageBox.Show(this, "Le modèle de menu se charge encore (au premier lancement, BurnSmith installe ses fichiers). Réessayez dans un instant.", "BurnSmith");
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (busy)
        {
            e.Cancel = true; // the window closes when the work has stopped (see Work): quitting now would leave the work folder behind
            if (closeAfterWork) return;
            if (writing) { RefuseClosing(); return; }
            if (MessageBox.Show(this, "Un disque est en cours de création.\n\nFermer BurnSmith annule la création : le travail est arrêté, ses fichiers temporaires sont supprimés et le disque ne sera pas terminé. La fenêtre se ferme dès l'arrêt.\n\nFermer quand même ?",
                    "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (!busy) { e.Cancel = false; return; } // it ended while the question was open
            if (writing) { RefuseClosing(); return; } // it went on to the burn meanwhile
            closeAfterWork = true;
            CancelButton.IsEnabled = false;
            StatusText.Text = "Annulation…";
            cancel?.Cancel();
        }
        else if (making > 0 && !closeAfterWork
                 && MessageBox.Show(this, "Une vidéo est en préparation (diaporama, morceau ou fusion).\n\nFermer BurnSmith l'arrête : elle ne sera pas ajoutée.\n\nFermer quand même ?",
                     "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
    }

    void RefuseClosing() =>
        MessageBox.Show(this, "Une gravure ou un effacement est en cours : BurnSmith reste ouvert, car l'interrompre peut rendre le disque inutilisable.\n\nAttendez la fin. Une gravure peut être arrêtée avec le bouton Annuler.", "BurnSmith", MessageBoxButton.OK, MessageBoxImage.Warning);

    protected override void OnClosed(EventArgs e)
    {
        Tools.KillAll(); // what runs without a Cancel button (a slideshow, a preview): before Closed removes its folder
        base.OnClosed(e);
    }

    void FrenchLabels() => design.French();

    /// <summary>For the off-screen snapshot (<c>design=NAME</c>): the project starts from this template instead of the default one.</summary>
    public void UseDesign(MenuDesign d)
    {
        var title = design.Title; // the disc's name already taken from the videos
        design = d.Clone();
        design.Title = title;
        FrenchLabels();
        MenuName.Text = design.Name;
        ProjectTitle.Text = titles.Count == 0 ? "Aucune source" : design.Title;
        RefreshThumb();
    }

    /// <summary>Asks Windows (IMAPI2) for the burners off the UI thread: the first call can wait for a drive to spin up.</summary>
    async void RefreshDrives()
    {
        if (busy || refreshingDrives) return;
        refreshingDrives = true;
        try
        {
            var drives = await Task.Run(Iso.Drives); // none: no burning, creating still works
            if (busy) return;
            if (!drives.Select(d => d.Id).SequenceEqual(DriveBox.Items.Cast<Imapi.Drive>().Select(d => d.Id)))
            {
                DriveBox.ItemsSource = drives;
                DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
            }
            BurnButton.IsEnabled = EraseButton.IsEnabled = BurnCheck.IsEnabled = drives.Count > 0;
            if (drives.Count == 0) BurnCheck.IsChecked = false;
        }
        // logged, not shown: this runs at every activation of the window, and a box would bring it back as soon as it is closed
        catch (Exception e) when (e is not OutOfMemoryException) { AppFiles.LogError("Lecteurs de disque", e); }
        finally { refreshingDrives = false; }
    }

    public MenuEditorWindow CreateEditor(MenuDesign? start = null) => openEditor = new(start ?? design, previewFrame, EditorContent()) { Playback = playback, Hdmv = hdmvMenu && kind != "dvd" };

    /// <summary>The video the menu's picture, the disc's name and the audio / subtitle pages come from: the first that is not a bonus.</summary>
    Entry? MainEntry => titles.FirstOrDefault(t => !t.Bonus && !t.Intro) ?? titles.FirstOrDefault();

    /// <summary>What the editor previews: the real chapters / episodes / bonus / tracks of the disc, or a sample while no video is loaded.</summary>
    MenuContent EditorContent()
    {
        if (MainEntry is not { } first)
            return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, 12).ToList(), null, ["Français 5.1", "English 5.1"], ["Français", "English"]);
        var disc = first.ToDisc();
        var (main, extras) = DiscOrder(first);
        var series = main.Count > 1;
        // the same moments as the disc's pictures: BluRay.Build and Dvd.Build take them there
        string? TenPercent(Entry t) => Frame(t, t.Start + t.OnDisc.Duration * 0.1);
        return new MenuContent(1, previewFrame, series ? [] : disc.ChapterList(first.OnDisc.Duration, chapterMinutes).Select(at => Frame(first, first.Start + at + 3)).ToList(),
            series ? main.Select((t, i) => new TitleEntry(t.Name, i + 1, TenPercent(t), t.Overview)).ToList() : null,
            disc.KeptAudio(first.Tracks).Select(a => a.Name).ToList(), disc.KeptSubtitles(first.Tracks).Where(s => s.Supported).Select(s => s.Name).ToList(),
            extras.Count > 0 ? extras.Select((t, i) => new TitleEntry(t.Name, main.Count + i + 1, TenPercent(t))).ToList() : null)
            { ChapterNames = series ? null : disc.ChapterNames(first.OnDisc.Duration, chapterMinutes) };
    }

    /// <summary>As on the disc: the feature first, then the extras (the start video is on neither). Numbered from 1 in this order, they are the playlists of <see cref="EditorContent"/>.</summary>
    (List<Entry> Main, List<Entry> Extras) DiscOrder(Entry first) =>
        (titles.Where(t => !t.Intro && (!t.Bonus || t == first)).ToList(), titles.Where(t => t.Bonus && !t.Intro && t != first).ToList());

    // the pictures of the menu preview: one file per video and moment, null while it is being made (or when ffmpeg could not read a frame: the empty frame stays)
    readonly string frameDir = Path.Combine(Path.GetTempPath(), "BurnSmith-frames-" + Environment.ProcessId);
    readonly Dictionary<(string Path, double At), string?> frames = [];
    readonly HashSet<(string Path, double At)> wantedFrames = [];
    MenuEditorWindow? openEditor;

    string? Frame(Entry t, double at)
    {
        var key = (t.Path, Math.Round(at));
        if (frames.TryGetValue(key, out var file)) return file;
        wantedFrames.Add(key);
        return null;
    }

    /// <summary>Makes the pictures the preview is missing, then redraws it (the editor too, if it is open).</summary>
    async Task RefreshFrames()
    {
        _ = EditorContent(); // asks for what the disc's current state needs
        if (wantedFrames.Count == 0) return;
        var wanted = wantedFrames.Select((k, i) => (Key: k, File: Path.Combine(frameDir, $"f{frames.Count + i}.jpg"))).ToList();
        wantedFrames.Clear();
        foreach (var (key, _) in wanted) frames[key] = null;
        var made = new string?[wanted.Count];
        await Task.Run(() => Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => made[i] = ExtractFrame(wanted[i].Key.Path, wanted[i].Key.At, wanted[i].File, 480, 270)));
        for (var i = 0; i < wanted.Count; i++) frames[wanted[i].Key] = made[i];
        RefreshThumb();
        openEditor?.UseContent(EditorContent());
    }

    void Append(string line)
    {
        Log.AppendText(Loc.T(line) + Environment.NewLine);
        Log.ScrollToEnd();
    }

    /// <summary>A line the user must see: it opens the log.</summary>
    void Warn(string line)
    {
        Append(line);
        LogExpander.IsExpanded = true;
    }

    void SyncOutput()
    {
        if (!outEdited)
            OutBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BurnSmith", volume);
    }

    void OnOutTyped(object s, TextCompositionEventArgs e) => outEdited = true;

    void OnOutChanged(object s, TextChangedEventArgs e)
    {
        if (FreeText == null) return; // fires while the window is being built
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(OutBox.Text));
            FreeText.Text = root == null ? "" : $"Espace libre : {new DriveInfo(root).AvailableFreeSpace / 1e9:0} Go";
        }
        catch (Exception) { FreeText.Text = ""; } // a path being typed
    }

    void OnModeClick(object s, RoutedEventArgs e)
    {
        ModeButton.ContextMenu.PlacementTarget = ModeButton;
        ModeButton.ContextMenu.IsOpen = true;
    }

    void OnMode(object s, RoutedEventArgs e) => SetKind((string)((MenuItem)s).Tag);

    void SetKind(string k)
    {
        kind = k;
        ModeText.Text = k == "dvd" ? "DVD" : k == "uhd" ? "Blu-ray 4K" : "Blu-ray";
        CapacityBox.ItemsSource = Output.For(k);
        CapacityBox.SelectedItem = k == "bd" ? Output.DefaultBd : Output.For(k)[0];
        foreach (var t in titles)
        {
            t.Subs = t.Subs.Where(i => t.Tracks.Subtitles.First(x => x.Index == i).Supported).ToList();
            t.Refresh();
        }
        Update();
    }

    void OnChanged(object s, SelectionChangedEventArgs e) => Update();

    void OnMenuToggle(object s, RoutedEventArgs e) => UpdateMenuState();

    /// <summary>Several videos can only be chosen from a menu, and a series menu lists episodes instead of chapters.</summary>
    /// <summary>More than one video to choose from; the start video plays by itself and is not one of them.</summary>
    static bool NeedsMenu(IEnumerable<DiscTitle> list) => list.Count(t => !t.Intro) > 1;

    void UpdateMenuState()
    {
        if (MenuButton == null) return;
        var series = NeedsMenu(titles.Select(t => t.ToDisc()));
        if (series) MenuCheck.IsChecked = true;
        MenuCheck.IsEnabled = !series;
        MenuButton.IsEnabled = MenuCheck.IsChecked == true;
        MenuThumb.Opacity = MenuCheck.IsChecked == true ? 1 : 0.25;
    }

    void Update()
    {
        if (CapacityBox?.SelectedItem is not Output cap || CapBar == null) return;
        var est = 0.0;
        var note = "";
        if (titles.Count > 0)
        {
            // shortcut: a 4K disc is sized by its source files (HEVC is kept), the audio dropped from them is not subtracted
            est = Estimate(cap, encode) ?? titles.Sum(t => Tools.SizeOf(t.Path) * t.OnDisc.Duration / t.Info.Duration) / 1e9;
            if (!cap.Unlimited && est > cap.Usable) note = "\ntrop grand pour ce support";
            if (titles.Any(t => !File.Exists(t.Path))) note = "\nune vidéo source est introuvable";
            if (kind == "uhd" && !encode.ForceReencode && titles.Any(t => t.Info.VideoCodec != "hevc" || t.Info.Height != 2160)) note = "\nPassthrough : sources HEVC 2160p seulement";
        }
        CapBar.Value = cap.Unlimited ? 0 : Math.Min(100, est / cap.Gb * 100);
        CapText.Text = cap.Unlimited ? $"{est:0.00} Go, sans limite de taille{note}" : $"{est:0.00} Go / {cap.Usable:0.#} Go utilisables{note}";
        ProjectInfo.Text = titles.Count == 0 ? "" : $"{titles.Count} vidéo{(titles.Count > 1 ? "s" : "")}{(titles.Any(t => t.Bonus) ? $" (dont {titles.Count(t => t.Bonus)} bonus)" : "")}{(titles.Any(t => t.Intro) ? " (dont une vidéo de début)" : "")}  ·  {titles.Sum(t => Tools.SizeOf(t.Path)) / 1e9:0.0} Go";
    }

    /// <summary>Size in GB of the Blu-ray for an output and an encoding (the video at the fitted rate, plus audio and menu); null for a DVD, a 4K disc or no video.</summary>
    double? Estimate(Output output, EncodeOptions enc) =>
        titles.Count == 0 || kind == "uhd" ? null
        : kind == "dvd" ? Dvd.EstimateGB(titles.Select(t => (t.OnDisc, t.ToDisc())).ToList(), output.Usable, enc.DvdKbps)
        : BluRay.EstimateGB(new BluRayJob(titles.Select(t => t.ToDisc()).ToList(), "", MenuCheck.IsChecked == true || NeedsMenu(titles.Select(t => t.ToDisc())) ? design : null, false, "X", chapterMinutes, enc, output.Usable),
            titles.Select(t => t.OnDisc).ToList());

    // ---- project: save, open, undo ----

    SubtitleStyle? subtitleStyle; // how the disc draws its text subtitles (null = the usual look)
    string? projectFile;
    int loading; // videos or a project being read: the list is half filled
    /// <summary>What ffprobe said of each file added: a project reopened or a removal undone needs no second probe.</summary>
    readonly Dictionary<string, SourceInfo> probed = new(StringComparer.OrdinalIgnoreCase);
    // one undo step per change of the project, seen by a timer: every setting is in Snapshot, so none can be forgotten.
    // shortcut: a change is seen up to half a second late, and two made within it are one step; call Remember where a change is made if that is missed
    readonly Stack<string> undo = new(), redo = new();
    string? kept; // the state last seen
    readonly System.Windows.Threading.DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>The disc as it stands: what a project file holds, and one undo step.</summary>
    Project Snapshot() => new(kind, titles.Select(t => t.ToDisc()).ToList(), design, MenuCheck.IsChecked == true, labelEdited ? volume : null, chapterMinutes, encode, playback, subtitleStyle,
                              (CapacityBox.SelectedItem as Output)?.Name, (CapacityBox.SelectedItem as Output)?.Usable ?? 0, outEdited ? OutBox.Text : null, IsoCheck.IsChecked == true, TrialCheck.IsChecked == true ? TrialSeconds : 0, hdmvMenu);

    /// <summary>Five minutes of each video on a trial disc.</summary>
    const int TrialSeconds = 300;

    /// <summary>Puts a project (a file opened, an undo step) in the window. Its videos must have been probed; one that was not (a file gone) is left out.</summary>
    void Apply(Project p)
    {
        titles.Clear();
        SetKind(p.Kind);
        // a project written by the command line names no size: the one of its capacity (below 0 = « Sans limite »)
        if ((Output.For(kind).FirstOrDefault(o => o.Name == p.Output)
             ?? Output.For(kind).FirstOrDefault(o => o.Hd720 == (p.Encode?.Hd720 ?? false) && (p.CapacityGB < 0 ? o.Unlimited : Math.Abs(o.Usable - p.CapacityGB) < 0.01))) is { } cap) CapacityBox.SelectedItem = cap;
        (volume, labelEdited) = (p.Label ?? "MON_DISQUE", p.Label != null);
        (chapterMinutes, encode, playback, subtitleStyle, hdmvMenu) = (p.ChapterMinutes, p.Encode ?? new(FitDisc: true), p.Playback ?? new(), p.Subtitles, p.HdmvMenu);
        if (p.Design != null) { design = p.Design; MenuName.Text = design.Name; }
        foreach (var t in p.Titles ?? [])
            if (probed.TryGetValue(t.Video, out var info)) titles.Add(Entry.From(t, info));
        (MenuCheck.IsChecked, IsoCheck.IsChecked, TrialCheck.IsChecked, outEdited) = (p.Menu, p.Iso, p.Trial > 0, p.Out != null);
        if (p.Out != null) OutBox.Text = p.Out;
        AfterTitlesChanged(); // the disc's name, the output folder, the size and the menu picture follow
        _ = RefreshPreview();
    }

    void Remember()
    {
        if (busy || loading > 0 || !defaultDesign.IsCompleted) return; // the default template arriving is not a change of the user's
        var now = Snapshot().ToJson();
        if (now == kept) return;
        if (kept != null) { undo.Push(kept); redo.Clear(); }
        kept = now;
        (UndoButton.IsEnabled, RedoButton.IsEnabled) = (undo.Count > 0, redo.Count > 0);
    }

    void Step(Stack<string> from, Stack<string> to)
    {
        if (busy || loading > 0) return;
        Remember();
        if (from.Count == 0 || kept == null) return;
        to.Push(kept);
        Apply(Project.Parse(from.Pop()));
        kept = Snapshot().ToJson(); // as the window took it
        (UndoButton.IsEnabled, RedoButton.IsEnabled) = (undo.Count > 0, redo.Count > 0);
    }

    public void Undo() => Step(undo, redo);
    public void Redo() => Step(redo, undo);
    void OnUndo(object s, RoutedEventArgs e) => Undo();
    void OnRedo(object s, RoutedEventArgs e) => Redo();

    /// <summary>For the off-screen snapshot (<c>undo</c>): the last video removed, then brought back by Undo; the picture shows the list whole and Rétablir lit.</summary>
    public void TryUndo()
    {
        Remember();
        if (titles.Count > 0) titles.RemoveAt(titles.Count - 1);
        AfterTitlesChanged();
        Undo();
    }

    // on the way up: a text box has taken its own Ctrl+Z by then
    void OnWindowKey(object s, KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not (ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Z && !shift) Undo();
        else if (e.Key == Key.Y || e.Key == Key.Z) Redo();
        else if (e.Key == Key.S) SaveProject(ask: false);
        else if (e.Key == Key.O) OnOpenProject(s, e);
        else return;
        e.Handled = true;
    }

    void OnOpenProject(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Ouvrir un projet"), Filter = Loc.Filter($"Projet BurnSmith|*{Project.Extension}|Tous les fichiers|*.*") };
        if (dlg.ShowDialog(this) == true) _ = OpenProject(dlg.FileName);
    }

    /// <summary>Replaces what the window holds by a saved project (Annuler brings the former back). A video that is gone or unreadable is told and left out.</summary>
    public async Task OpenProject(string file)
    {
        if (busy) return;
        Project p;
        try { p = Project.Load(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Impossible d'ouvrir {Path.GetFileName(file)} : {e.Message}", "Projet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await defaultDesign; // arriving later, it would not replace the project's menu, but Annuler would have no state to go back to
        Remember();
        loading++;
        try
        {
            foreach (var video in (p.Titles ?? []).Select(t => t.Video).Where(v => !probed.ContainsKey(v)).Distinct().ToList())
            {
                StatusText.Text = "Ouverture de " + Path.GetFileName(video) + "…";
                try { probed[video] = await Task.Run(() => BluRay.Probe(video)); }
                catch (Exception ex) { Warn($"Projet : « {Path.GetFileName(video)} » est introuvable ou illisible, cette vidéo n'est pas reprise ({ex.Message.Split('\n')[0]})."); }
            }
            Apply(p);
            projectFile = file;
            Title = Path.GetFileNameWithoutExtension(file) + " — BurnSmith";
        }
        finally { loading--; }
    }

    void OnSaveProject(object s, RoutedEventArgs e) => SaveProject(ask: true);

    /// <summary>Writes the project; <paramref name="ask"/> false = in the file it came from, when there is one.</summary>
    void SaveProject(bool ask)
    {
        if (ask || projectFile == null)
        {
            var dlg = new SaveFileDialog { Title = Loc.T("Enregistrer le projet"), Filter = Loc.Filter($"Projet BurnSmith|*{Project.Extension}"), DefaultExt = Project.Extension, FileName = projectFile ?? volume };
            if (dlg.ShowDialog(this) != true) return;
            projectFile = dlg.FileName;
        }
        try
        {
            Snapshot().Save(projectFile);
            Title = Path.GetFileNameWithoutExtension(projectFile) + " — BurnSmith";
            Append("Projet enregistré : " + projectFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Le projet n'a pas pu être enregistré : {ex.Message}", "Projet", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- videos ----

    void OnOpen(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = Loc.Filter("Vidéos, photos et musique|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob;*.iso;*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff;*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.ac3;*.mka")
                     + "|Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob|Blu-ray (image ISO non chiffrée, 3D compris)|*.iso|Photos (diaporama)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|Musique|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.ac3;*.mka|Tous les fichiers|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = AddFiles(dlg.FileNames);
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = AddFiles(files);
    }

    public Task LoadSource(string path) => AddFiles([path]);

    // what MediaMaker built from photos and songs (a folder of ours, removed when the window closes) and the name each one shows
    readonly string mediaDir = Path.Combine(Path.GetTempPath(), "BurnSmith-media-" + Environment.ProcessId);
    readonly Dictionary<string, string> madeNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> discNames = new(StringComparer.OrdinalIgnoreCase); // the film of a Blu-ray added whole, named after its .iso or folder

    static void NaturalOrder(List<string> list) =>
        list.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), CultureInfo.CurrentCulture, CompareOptions.NumericOrdering));

    void Say(string line) => Dispatcher.BeginInvoke(() => { Append(line); StatusText.Text = line; });

    /// <summary>The path of a video to make in our folder, with the name its row will show.</summary>
    string NextMade(string name)
    {
        if (madeNames.Count == 0) Closed += (_, _) => Tools.TryDelete(mediaDir);
        Directory.CreateDirectory(mediaDir);
        var file = Path.Combine(mediaDir, $"m{madeNames.Count + 1:00}.mkv");
        madeNames[file] = name;
        return file;
    }

    /// <summary>
    /// Photos dropped together become one slideshow (the songs dropped with them play under it); songs alone become one title each, with their cover.
    /// Everything else is a video and stays as it is.
    /// </summary>
    async Task<List<string>> MakeVideos(List<string> files)
    {
        var photos = files.Where(MediaMaker.IsPhoto).ToList();
        var songs = files.Where(MediaMaker.IsSong).ToList();
        var videos = files.Where(f => !MediaMaker.IsPhoto(f) && !MediaMaker.IsSong(f)).ToList();
        if (photos.Count == 0 && songs.Count == 0) return videos;
        NaturalOrder(photos);
        NaturalOrder(songs);
        making++;
        try
        {
            if (photos.Count > 0)
            {
                var answer = Prompt.Ask(this, photos.Count == 1 ? "Diaporama d'une photo : secondes" : $"Diaporama de {photos.Count} photos : secondes par photo", "5");
                if (answer != null && double.TryParse(answer.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var each) && each > 0)
                {
                    var transition = photos.Count < 2 ? null
                        : MediaMaker.TransitionOf(Prompt.Ask(this, "Transition entre les photos : fondu, glissement, volet, cercle, mélange ou aucune", "fondu") ?? "aucune");
                    var file = NextMade("Diaporama" + (madeNames.Values.Count(n => n.StartsWith("Diaporama")) is var k and > 0 ? $" {k + 1}" : ""));
                    var music = songs.ToList();
                    songs.Clear(); // the songs of a slideshow are its music, not titles
                    StatusText.Text = "Préparation du diaporama…";
                    await Task.Run(() => MediaMaker.Slideshow(photos, each, music, file, Say, transition));
                    videos.Add(file);
                }
                else
                {
                    Warn("Diaporama annulé (durée par photo absente ou invalide) : les photos" + (songs.Count > 0 ? " et les morceaux déposés avec elles" : "") + " ne sont pas ajoutés.");
                    songs.Clear(); // they were dropped as the music of that slideshow
                }
            }
            foreach (var song in songs)
            {
                var file = NextMade(Path.GetFileNameWithoutExtension(song));
                StatusText.Text = "Préparation de " + Path.GetFileName(song) + "…";
                var title = await Task.Run(() =>
                {
                    MediaMaker.Song(song, MediaMaker.CoverOf(song, mediaDir), file, Say);
                    return MediaMaker.Describe(song).Title;
                });
                if (title != null) madeNames[file] = title;
                videos.Add(file);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Warn("Photos / musique : " + ex.Message.Split('\n')[0]);
        }
        finally { making--; }
        return videos;
    }

    /// <summary>Probes and adds videos; several at once are put in natural order (episode 2 before episode 10).</summary>
    async Task AddFiles(IEnumerable<string> files)
    {
        if (files.FirstOrDefault(f => f.EndsWith(Project.Extension, StringComparison.OrdinalIgnoreCase)) is { } project) { await OpenProject(project); return; } // dropped, or given to the exe
        loading++;
        try { await AddVideos(files); }
        finally { loading--; }
    }

    async Task AddVideos(IEnumerable<string> files)
    {
        var list = new List<string>();
        foreach (var f in files)
        {
            // a Blu-ray given whole (.iso, or a folder with BDMV) adds its film, named after the disc
            if (!Directory.Exists(f) && !Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase)) { if (File.Exists(f)) list.Add(f); continue; }
            try
            {
                StatusText.Text = "Ouverture de " + Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) + "…";
                var clip = await Task.Run(() => BluRay.MainClip(f));
                discNames[clip] = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(f));
                list.Add(clip);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Warn($"Impossible de lire {Path.GetFileName(Path.TrimEndingDirectorySeparator(f))} : {ex.Message.Split('\n')[0]}");
            }
        }
        NaturalOrder(list);
        list = await MakeVideos(list);
        foreach (var path in list)
        {
            try
            {
                var info = probed[path] = await Task.Run(() => BluRay.Probe(path));
                var entry = new Entry(path, info) { Extra = ExternalSubtitle.Near(path), ChapterAt = info.Chapters, ChapterTitles = info.ChapterNames }; // the file's own chapters and their names, when it has some, instead of one every N minutes
                if (madeNames.TryGetValue(path, out var shown) || discNames.TryGetValue(path, out shown)) entry.Name = shown;
                // episodes usually share one layout: start from the first video's choice
                if (titles.FirstOrDefault() is { } first && first.Info.Audio.Count == entry.Info.Audio.Count)
                {
                    entry.Audio = first.Audio.ToList();
                    entry.Subs = first.Subs.Where(i => entry.Tracks.Subtitles.Any(t => t.Index == i && t.Supported)).ToList();
                }
                titles.Add(entry);
            }
            catch (Exception ex)
            {
                Warn($"Impossible de lire {Path.GetFileName(path)} : {ex.Message.Split('\n')[0]}");
            }
        }
        AfterTitlesChanged();
        await RefreshPreview();
    }

    /// <summary>The menu picture is a frame of the first video, at 10% of the part kept; it changes with the first video and its cut.</summary>
    async Task RefreshPreview()
    {
        if (MainEntry is not { } first) return;
        var key = $"{first.Path}|{first.Start}|{first.End}";
        if (previewFor == key) return;
        previewFor = key;
        var at = first.Start + first.OnDisc.Duration * 0.1;
        previewFrame = await Task.Run(() => ExtractPreview(first.Path, at));
        Poster.Source = LoadImage(previewFrame);
        RefreshThumb();
    }

    internal static BitmapImage? LoadImage(string? file)
    {
        if (file == null || !File.Exists(file)) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // the preview file is overwritten for each new first video
        img.UriSource = new Uri(file);
        img.EndInit();
        img.Freeze();
        return img;
    }

    void AfterTitlesChanged()
    {
        NoSource.Visibility = titles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (titles.Count == 0) { previewFrame = previewFor = null; Poster.Source = null; }
        else
        {
            if (titles.All(t => t.Bonus || t.Intro)) // a disc needs a feature: the first video becomes it again
            {
                titles[0].Bonus = titles[0].Intro = false;
                titles[0].Refresh();
                Warn($"« {titles[0].Name} » redevient le film principal : un disque a besoin d'au moins une vidéo qui ne soit ni un bonus ni la vidéo de début.");
            }
            var name = DiscTitle.Readable(MainEntry!.Path).Series ?? MainEntry.Name; // a season is named after its series, not after its first episode
            if (!labelEdited) volume = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9_]+", "_").Trim('_') is { Length: > 0 } l ? l[..Math.Min(32, l.Length)] : "MON_DISQUE";
            if (design.Untitled()) design.Title = name;
        }
        ProjectTitle.Text = titles.Count == 0 ? "Aucune source" : design.Title;
        UpdateMenuState();
        SyncOutput();
        Update();
        RefreshThumb();
    }

    /// <summary>The menu's first page, small, from the real menu builder (so it shows the chosen template).</summary>
    async void RefreshThumb()
    {
        _ = RefreshFrames();
        var mine = ++thumbVersion;
        var d = design.Clone();
        var content = EditorContent();
        try
        {
            var jpeg = await Task.Run(() => MenuRenderer.Sta(() => MenuRenderer.Background(d, MenuBuilder.Build(d, content)[0])));
            if (mine != thumbVersion) return; // a newer change is already rendering
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(jpeg);
            img.DecodePixelWidth = 320;
            img.EndInit();
            img.Freeze();
            MenuThumb.Source = img;
        }
        catch (Exception) { if (mine == thumbVersion) MenuThumb.Source = null; } // the thumbnail is cosmetic; the editor shows the error
    }

    /// <summary>One frame of the movie at <paramref name="seconds"/>, used as the picture on the menu preview.</summary>
    static string? ExtractPreview(string path, double seconds) => ExtractFrame(path, seconds, Path.Combine(Path.GetTempPath(), "BurnSmith", "preview.jpg"), 800, 450);

    /// <summary><paramref name="fill"/>: cropped to fill w×h, else the whole picture within w×h.</summary>
    internal static string? ExtractFrame(string path, double seconds, string file, int w, int h, bool fill = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-frames:v", "1", "-q:v", "2", "-vf", fill ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}" : $"scale={w}:{h}:force_original_aspect_ratio=decrease", file]);
            return File.Exists(file) ? file : null;
        }
        catch (Exception) { return null; } // the preview just shows an empty frame
    }

    // ---- one row of the video table ----

    static Entry? Row(object s) => (s as FrameworkElement)?.DataContext as Entry;

    void OnRowRemove(object s, RoutedEventArgs e)
    {
        if (Row(s) is not { } entry) return;
        titles.Remove(entry);
        AfterTitlesChanged();
    }

    /// <summary>The "..." button: bonus, start video.</summary>
    void OnRowMore(object s, RoutedEventArgs e)
    {
        var entry = Row(s);
        // FontFamily: a menu inherits the font of its target, which here is the icon button (the text would show as empty boxes)
        var menu = new ContextMenu { PlacementTarget = (UIElement)s, Placement = PlacementMode.Bottom, FontFamily = FontFamily };
        // a disc keeps at least one video that is not a bonus
        var bonus = new MenuItem
        {
            Header = "Bonus (après le film, page Bonus du menu)", IsCheckable = true, IsChecked = entry!.Bonus,
            IsEnabled = !entry.Intro && (entry.Bonus || titles.Count(t => !t.Bonus && !t.Intro) > 1),
        };
        bonus.Click += (_, _) => SetBonus(entry, bonus.IsChecked);
        menu.Items.Add(bonus);
        var intro = new MenuItem
        {
            Header = "Vidéo de début (lue avant le menu)", IsCheckable = true, IsChecked = entry.Intro,
            IsEnabled = entry.Intro || titles.Count(t => !t.Bonus && !t.Intro && t != entry) > 0,
        };
        intro.Click += (_, _) => SetIntro(entry, intro.IsChecked);
        menu.Items.Add(intro);
        // the picture of this video in the menu: a moment of it, or an image file
        var thumbAt = new MenuItem { Header = entry.ThumbAt is { } at ? $"Vignette du menu : à {Timecode.Format(at)}…" : "Vignette du menu : choisir le moment…" };
        thumbAt.Click += (_, _) =>
        {
            var length = (entry.End ?? entry.Info.Duration) - entry.Start;
            if (Prompt.Ask(this, "Vignette du menu : moment de la vidéo (h:mm:ss, depuis son début sur le disque)", Timecode.Format(entry.ThumbAt ?? Math.Floor(length * 0.1))) is not { } text) return;
            if (Timecode.Parse(text.Trim()) is not { } t || t < 0 || t > length)
            {
                MessageBox.Show(this, $"Entrez un moment entre 0:00:00 et {Timecode.Format(length)}.", "Vignette du menu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            (entry.ThumbAt, entry.ThumbImage) = (t, null);
        };
        menu.Items.Add(thumbAt);
        var thumbImage = new MenuItem { Header = entry.ThumbImage is { } img ? $"Vignette du menu : {System.IO.Path.GetFileName(img)}…" : "Vignette du menu : choisir une image…" };
        thumbImage.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = Loc.T("Image de la vignette"), Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Tous les fichiers|*.*") };
            if (dlg.ShowDialog(this) == true) (entry.ThumbImage, entry.ThumbAt) = (dlg.FileName, null);
        };
        menu.Items.Add(thumbImage);
        if (entry.ThumbAt != null || entry.ThumbImage != null)
        {
            var auto = new MenuItem { Header = "Vignette du menu : automatique" };
            auto.Click += (_, _) => (entry.ThumbAt, entry.ThumbImage) = (null, null);
            menu.Items.Add(auto);
        }
        var overview = new MenuItem { Header = entry.Overview is { Length: > 0 } ? "Résumé pour le menu : modifier…" : "Résumé pour le menu…" };
        overview.Click += (_, _) =>
        {
            if (Prompt.Ask(this, "Résumé de la vidéo, sous son nom sur les pages d'épisodes (modèle Streaming) ; vide = aucun", entry.Overview ?? "") is not { } text) return;
            entry.Overview = text.Trim().Length > 0 ? text.Trim() : null;
        };
        menu.Items.Add(overview);
        // SplitEntry / JoinWithNext are kept but out of the menu for now (Marc, 2026-11-07)
        menu.IsOpen = true;
    }

    /// <summary>Two titles from one video: the first runs up to the moment asked, the second starts there (same tracks and settings; the second is re-encoded on a Blu-ray, a copy cannot start on a given frame).</summary>
    async void SplitEntry(Entry entry)
    {
        var (from, to) = (entry.Start, entry.End ?? entry.Info.Duration);
        if (Prompt.Ask(this, "Couper en deux : à quel moment ? (h:mm:ss)", Timecode.Format(Math.Floor(from + (to - from) / 2))) is not { } text) return;
        if (Timecode.Parse(text.Trim()) is not { } at || at <= from + 1 || at >= to - 1)
        {
            MessageBox.Show(this, $"Entrez un moment entre {Timecode.Format(from + 1)} et {Timecode.Format(to - 1)}.", "Couper en deux titres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var second = entry.Clone();
        second.Name = entry.Name + " (2/2)";
        entry.Name += " (1/2)";
        (entry.End, second.Start) = (at, at);
        entry.Clips = second.Clips = null; // the clips were for the whole
        titles.Insert(titles.IndexOf(entry) + 1, second);
        entry.Refresh();
        AfterTitlesChanged();
        await RefreshPreview();
    }

    /// <summary>The video and the next one end to end in one new file (stream copy): neither may be cut and both need the same streams. Subtitle files added by hand are not carried over.</summary>
    async Task JoinWithNext(Entry entry)
    {
        var i = titles.IndexOf(entry);
        if (i < 0 || i + 1 >= titles.Count) return;
        var next = titles[i + 1];
        var why = entry.ToDisc().Trimmed || next.ToDisc().Trimmed ? "Retirez d'abord la coupe des deux vidéos."
            : entry.Bonus != next.Bonus || entry.Intro != next.Intro ? "Une vidéo du film, un bonus et la vidéo de début ne se fusionnent pas entre eux."
            : !MediaMaker.SameLayout(entry.Info, next.Info) ? "Les deux vidéos n'ont pas les mêmes flux (codec et profil, taille, cadence, pistes) : on ne peut pas les coller sans les réencoder."
            : null;
        if (why != null) { MessageBox.Show(this, why, "Fusionner", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        StatusText.Text = "Fusion des deux vidéos…";
        making++;
        try
        {
            var file = NextMade(entry.Name);
            await Task.Run(() => MediaMaker.Join([entry.Path, next.Path], file, Say));
            var info = probed[file] = await Task.Run(() => BluRay.Probe(file));
            var joined = new Entry(file, info)
            {
                Name = entry.Name, Audio = [.. entry.Audio.Where(a => a < info.Audio.Count)], Subs = [.. entry.Subs.Where(s => s < info.Subtitles.Count)],
                Volume = entry.Volume, Bonus = entry.Bonus, Intro = entry.Intro, DefaultAudio = entry.DefaultAudio, DefaultSub = entry.DefaultSub, TrackLanguage = new(entry.TrackLanguage), Effects = entry.Effects, // the join keeps both videos' (equal) streams
            };
            if (entry.Extra.Count + next.Extra.Count > 0) Warn("Les fichiers de sous-titres ajoutés aux deux vidéos ne suivent pas la fusion.");
            titles[i] = joined;
            titles.Remove(next);
            AfterTitlesChanged();
            await RefreshPreview();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Warn("Fusion : " + ex.Message.Split('\n')[0]);
        }
        finally { making--; }
    }

    /// <summary>Marks a video as an extra, or back as part of the feature: the menu gets (or loses) its Bonus button and page.</summary>
    /// <summary>Makes a video the one that plays when the disc starts (the others lose it: there is one), or a title like the others again.</summary>
    void SetIntro(Entry entry, bool intro)
    {
        foreach (var other in titles.Where(t => t.Intro && t != entry).ToList()) { other.Intro = false; other.Refresh(); }
        entry.Intro = intro;
        if (intro) entry.Bonus = false;
        entry.Refresh();
        UpdateMenuState(); // a film and its start video need no menu; two films do
        Update();
        RefreshThumb();
        _ = RefreshPreview();
    }

    void SetBonus(Entry entry, bool bonus)
    {
        entry.Bonus = bonus;
        entry.Refresh();
        Update();
        RefreshThumb();
        _ = RefreshPreview(); // the menu's picture is a frame of the first video that is not a bonus
    }

    /// <summary>Dragging a row by its name reorders the titles (the order is the order of the episodes in the menu).</summary>
    void OnRowNameMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Row(s) is { } entry)
            DragDrop.DoDragDrop((DependencyObject)s, entry, DragDropEffects.Move);
    }

    void OnRowDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Entry)) is not Entry moved || Row(s) is not { } target) return; // files dropped here go on to the window
        e.Handled = true;
        var (from, to) = (titles.IndexOf(moved), titles.IndexOf(target));
        if (from < 0 || to < 0 || from == to) return;
        titles.Move(from, to);
        RefreshThumb(); // episode order changes the menu
    }

    /// <summary>
    /// The pencil: the video edit (name in the menu, crop, effects and volume, watermark, burnt subtitle, part kept, chapters).
    /// A tab's "Appliquer à tous" copies that tab to every other video.
    /// </summary>
    async void OnRowEdit(object s, RoutedEventArgs e)
    {
        if (Row(s) is not { } entry) return;
        var dlg = new VideoEditWindow(entry.Path, entry.Tracks, entry.Name, entry.Start, entry.End, entry.Volume, entry.ChapterAt, entry.ChapterMinutes, chapterMinutes, entry.Effects, clips: entry.Clips, chapterTitles: entry.ChapterTitles) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        (entry.Name, entry.Start, entry.End, entry.Volume, entry.ChapterAt, entry.ChapterMinutes, entry.Effects, entry.Clips, entry.ChapterTitles) = (dlg.NameText, dlg.Start, dlg.End, dlg.Volume, dlg.ChapterAt, dlg.ChapterMinutes, dlg.Effects, dlg.Clips, dlg.ChapterTitles);
        foreach (var other in titles.Where(t => t != entry && dlg.ApplyAll.Count > 0))
        {
            var fx = dlg.ApplyAll.Aggregate(other.Effects ?? new VideoEffects(), (o, part) => o.Take(dlg.Effects ?? new VideoEffects(), part));
            other.Effects = fx.IsEmpty ? null : fx;
            if (dlg.ApplyAll.Contains("effect")) other.Volume = dlg.Volume;
            other.Refresh();
        }
        await Edited(entry);
    }

    /// <summary>The scissors: cutting, the part kept chosen on the video itself.</summary>
    async void OnRowCut(object s, RoutedEventArgs e)
    {
        if (Row(s) is not { } entry) return;
        var dlg = new CutWindow(entry.Path, entry.Info.Duration, entry.Start, entry.End) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        (entry.Start, entry.End, entry.Clips) = (dlg.Start, dlg.End, null); // one part kept: the editor's clips give way
        await Edited(entry);
    }

    async Task Edited(Entry entry)
    {
        entry.Refresh();
        Update(); // the size follows the length kept
        RefreshThumb(); // the name and the chapters are on the menu
        await RefreshPreview();
    }

    void OnRowPlay(object s, RoutedEventArgs e)
    {
        if (Row(s) is not { } entry) return;
        try { Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Warn("Impossible d'ouvrir la vidéo : " + ex.Message); }
    }

    // ---- tracks ----

    void OnAudioMenu(object s, RoutedEventArgs e) => TrackMenu((Button)s, true);
    void OnSubMenu(object s, RoutedEventArgs e) => TrackMenu((Button)s, false);

    /// <summary>The dropdown of a row: one checkable line per track of the source, ticking one updates just that video.</summary>
    void TrackMenu(Button button, bool audio)
    {
        if (Row(button) is not { } entry) return;
        var kept = audio ? entry.Audio : entry.Subs;
        IEnumerable<(int Index, string Text, bool Enabled)> tracks = audio
            ? entry.Tracks.Audio.Select(a => (a.Index, $"{a.Name}  ({a.Codec})", true))
            : entry.Tracks.Subtitles.Select(t => (t.Index, t.Supported ? $"{t.Name}  ({(t.File != null ? Path.GetFileName(t.File) : t.Codec)})" : $"{t.Name}  ({t.Codec}, non pris en charge)", t.Supported));
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var keepAll = new MenuItem { Header = titles.Count > 1 ? "Tout garder sur toutes les vidéos" : "Tout garder" };
        keepAll.Click += (_, _) => KeepAll(audio);
        menu.Items.Add(keepAll);
        menu.Items.Add(new Separator());
        foreach (var (index, text, enabled) in tracks)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = kept.Contains(index), IsEnabled = enabled, StaysOpenOnClick = true };
            item.Click += (_, _) => ToggleTrack(entry, audio, index, item);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = audio ? "Aucune piste audio" : "Aucun sous-titre", IsEnabled = false });
        AddDefaultMenu(menu, entry, audio);
        // a track the file leaves "und" or labels wrongly: one submenu per track of the video itself (a subtitle file's language is set further down)
        var own = audio ? entry.Tracks.Audio.Select(a => (a.Index, a.Name, a.Lang)) : entry.Tracks.Subtitles.Where(t => t.File == null).Select(t => (t.Index, t.Name, t.Lang));
        foreach (var (index, name, _) in own) menu.Items.Add(LanguageMenu(entry, audio, index, name));
        if (!audio)
        {
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "Ajouter un fichier de sous-titres…" };
            add.Click += (_, _) => AddSubtitleFiles(entry);
            menu.Items.Add(add);
            // the language of a subtitle file is not always in its name: one submenu per file
            for (var k = 0; k < entry.Extra.Count; k++)
            {
                var (at, file) = (k, entry.Extra[k]);
                var languages = new MenuItem { Header = $"Langue de « {Path.GetFileName(file.File)} »" + (file.Stream > 0 ? $" (piste {file.Stream + 1})" : "") };
                foreach (var (code, name) in Languages.Choices.Prepend(("und", "Inconnue")))
                {
                    var choice = new MenuItem { Header = name, IsCheckable = true, IsChecked = Languages.Same(code, file.Lang) };
                    choice.Click += (_, _) => SetSubtitleLanguage(entry, at, code);
                    languages.Items.Add(choice);
                }
                menu.Items.Add(languages);
            }
        }
        // a track that comes too early or too late (a subtitle file made for another cut of the film, a sound out of step with the lips): moved as a whole
        var delays = audio ? entry.AudioDelay : entry.SubDelay;
        if (kept.Count > 0) menu.Items.Add(new Separator());
        foreach (var index in kept)
        {
            var late = delays.GetValueOrDefault(index);
            var name = (audio ? entry.Tracks.Audio.FirstOrDefault(a => a.Index == index)?.Name : entry.Tracks.Subtitles.FirstOrDefault(t => t.Index == index)?.Name) ?? "?";
            var delay = new MenuItem { Header = $"Décalage de « {name} »" + (late != 0 ? $" : {late.ToString("+0.###;-0.###", CultureInfo.CurrentCulture)} s…" : "…") };
            delay.Click += (_, _) =>
            {
                var what = audio ? "Décalage du son" : "Décalage du sous-titre";
                if (Prompt.Ask(this, $"{what}, en secondes : {(audio ? "0,2" : "1,5")} = plus tard, -{(audio ? "0,2" : "1,5")} = plus tôt, 0 = aucun", late.ToString("0.###", CultureInfo.CurrentCulture)) is not { } text) return;
                if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || Math.Abs(seconds) > 3600)
                {
                    MessageBox.Show(this, "Entrez un nombre de secondes, par exemple 1,5 ou -0,8.", what, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (seconds == 0) delays.Remove(index); else delays[index] = seconds;
            };
            menu.Items.Add(delay);
        }
        if (titles.Count > 1)
        {
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "Appliquer les pistes de cette vidéo aux autres" };
            all.Click += (_, _) => ApplyToOthers(entry);
            menu.Items.Add(all);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// "Piste audio par défaut" / "Sous-titre par défaut": which kept track plays (shows) when the disc starts. Nothing to choose from with one audio track;
    /// for subtitles "Aucun" is the other choice.
    /// </summary>
    void AddDefaultMenu(ContextMenu menu, Entry entry, bool audio)
    {
        var kept = audio ? entry.Audio : entry.Subs;
        if (audio ? kept.Count < 2 : kept.Count == 0) return;
        var current = audio ? (entry.DefaultAudio is { } d && kept.Contains(d) ? d : kept[0]) : entry.DefaultSub is { } s && kept.Contains(s) ? s : -1;
        var parent = new MenuItem { Header = audio ? "Piste audio par défaut" : "Sous-titre par défaut" };
        void Add(string name, int index)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = current == index };
            item.Click += (_, _) =>
            {
                if (audio) entry.DefaultAudio = index; else entry.DefaultSub = index < 0 ? null : index;
                entry.Refresh();
                Update();
            };
            parent.Items.Add(item);
        }
        if (!audio) Add("Aucun", -1);
        foreach (var index in kept) Add(audio ? entry.Tracks.Audio.First(a => a.Index == index).Name : entry.Tracks.Subtitles.First(t => t.Index == index).Name, index);
        menu.Items.Add(new Separator());
        menu.Items.Add(parent);
    }

    /// <summary>"Langue de « … »" for one track of the video itself: the language it is read as on the disc; "Langue d'origine" gives back the file's own.</summary>
    MenuItem LanguageMenu(Entry entry, bool audio, int index, string track)
    {
        var key = DiscTitle.LanguageKey(audio, index);
        var menu = new MenuItem { Header = $"Langue de « {track} »" };
        void Add(string name, string? code)
        {
            var set = entry.TrackLanguage.GetValueOrDefault(key);
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = code == null ? set == null : set != null && Languages.Same(set, code) };
            item.Click += (_, _) =>
            {
                if (code == null) entry.TrackLanguage.Remove(key); else entry.TrackLanguage[key] = code;
                entry.Refresh();
            };
            menu.Items.Add(item);
        }
        Add("Langue d'origine", null);
        foreach (var (code, name) in Languages.Choices.Prepend(("und", "Inconnue"))) Add(name, code);
        return menu;
    }

    /// <summary>Sets the language of the <paramref name="at"/>th subtitle file of a video: its name in the lists, on the settings page and the disc's language code follow.</summary>
    void SetSubtitleLanguage(Entry entry, int at, string code)
    {
        entry.Extra = [.. entry.Extra.Select((x, i) => i == at ? x with { Lang = code } : x)];
        entry.Refresh();
    }

    /// <summary>Adds subtitle files (srt, ass, ssa, vtt, sup, idx + sub) to one video, ticked: they are tracks like the video's own, after them (an .idx holds one per language).</summary>
    void AddSubtitleFiles(Entry entry)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T($"Sous-titres de {entry.Name}"), Multiselect = true, InitialDirectory = Path.GetDirectoryName(entry.Path),
            Filter = Loc.Filter("Sous-titres (*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx;*.sub|Tous les fichiers|*.*"),
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var chosen in dialog.FileNames)
        {
            var file = Path.GetExtension(chosen).Equals(".sub", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(chosen, ".idx")) ? Path.ChangeExtension(chosen, ".idx") : chosen; // a VobSub is its .idx
            if (entry.Extra.Any(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            if (!ExternalSubtitle.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) { Warn($"{Path.GetFileName(file)} : seuls les sous-titres srt, ass, ssa, vtt, sup (PGS) et idx + sub (VobSub) sont pris en charge."); continue; }
            List<ExternalSubtitle> added;
            try { added = ExternalSubtitle.ForAll(entry.Path, file); }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException) { Warn($"{Path.GetFileName(file)} : {e.Message}"); continue; }
            foreach (var one in added)
            {
                entry.Extra = [.. entry.Extra, one];
                var index = entry.Info.Subtitles.Count + entry.Extra.Count - 1;
                if (entry.Subs.Count < MenuBuilder.MaxSubtitles) { entry.Subs.Add(index); entry.Subs.Sort(); }
            }
        }
        entry.Refresh();
        Update();
    }

    /// <summary>Every track each video has, whatever its layout (episodes do not all have the same tracks).</summary>
    void KeepAll(bool audio)
    {
        foreach (var t in titles)
        {
            if (audio) t.Audio = t.Tracks.Audio.Select(a => a.Index).ToList();
            else t.Subs = t.Tracks.Subtitles.Where(s => s.Supported).Select(s => s.Index).ToList();
            t.Refresh();
        }
        if (titles.Any(t => (audio ? t.Audio.Count : t.Subs.Count) > (audio ? MenuBuilder.MaxAudio : MenuBuilder.MaxSubtitles))) WarnNoRow();
        Update();
    }

    void WarnNoRow() => Warn($"La page Réglages du menu propose au plus {MenuBuilder.MaxAudio} pistes audio et {MenuBuilder.MaxSubtitles} sous-titres : les autres restent sur le disque, sans ligne dans le menu.");

    void ToggleTrack(Entry entry, bool audio, int index, MenuItem item)
    {
        var kept = audio ? entry.Audio : entry.Subs;
        if (item.IsChecked != true)
        {
            kept.Remove(index);
            if (audio && entry.DefaultAudio == index) entry.DefaultAudio = null;
            if (!audio && entry.DefaultSub == index) entry.DefaultSub = null;
        }
        else if (!kept.Contains(index))
        {
            if (kept.Count >= (audio ? MenuBuilder.MaxAudio : MenuBuilder.MaxSubtitles)) WarnNoRow();
            kept.Add(index);
            kept.Sort(); // disc order = source order; the first one is the default
        }
        entry.Refresh();
        Update();
    }

    /// <summary>Copies one video's choice to the others, by track number.</summary>
    void ApplyToOthers(Entry cur)
    {
        foreach (var other in titles.Where(t => t != cur))
        {
            other.Audio = cur.Audio.Where(i => other.Info.Audio.Any(a => a.Index == i)).ToList();
            other.Subs = cur.Subs.Where(i => other.Tracks.Subtitles.Any(t => t.Index == i && t.Supported)).ToList();
            other.TrackLanguage = cur.TrackLanguage.Where(kv => int.Parse(kv.Key[1..]) is var n && (kv.Key[0] == 'a' ? other.Info.Audio.Any(a => a.Index == n) : other.Info.Subtitles.Any(t => t.Index == n))).ToDictionary(kv => kv.Key, kv => kv.Value);
            other.DefaultAudio = other.Audio.Contains(cur.DefaultAudio ?? -1) ? cur.DefaultAudio : null;
            other.DefaultSub = other.Subs.Contains(cur.DefaultSub ?? -1) ? cur.DefaultSub : null;
            other.Refresh();
        }
        Append("Pistes appliquées à toutes les vidéos (par numéro de piste).");
        Update();
    }

    // ---- menu, output, build ----

    void OnEditMenu(object s, RoutedEventArgs e)
    {
        if (StillLoadingDesign()) return;
        var editor = CreateEditor();
        editor.Owner = this;
        var ok = editor.ShowDialog() == true;
        openEditor = null;
        if (!ok) return;
        design = editor.Result;
        playback = editor.Playback;
        MenuName.Text = design.Name;
        if (MainEntry is { } first && editor.RenamedTitles.Count > 0) // the names written on the menu's page are the videos' names
        {
            var (main, extras) = DiscOrder(first);
            var order = main.Concat(extras).ToList();
            foreach (var (playlist, name) in editor.RenamedTitles)
            {
                order[playlist - 1].Name = name;
                order[playlist - 1].Refresh();
            }
        }
        AfterTitlesChanged(); // the title, the volume name and the picture follow
    }

    void OnAdvanced(object s, RoutedEventArgs e)
    {
        var dlg = new AdvancedWindow(volume, chapterMinutes, encode, playback, kind, Output.For(kind), (Output)CapacityBox.SelectedItem, Estimate, subtitleStyle, hdmvMenu) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        subtitleStyle = dlg.Subtitles;
        hdmvMenu = dlg.HdmvMenu;
        if (dlg.Volume != volume) { volume = dlg.Volume; labelEdited = true; SyncOutput(); }
        chapterMinutes = dlg.ChapterMinutes;
        encode = dlg.Encode;
        playback = dlg.Playback;
        CapacityBox.SelectedItem = dlg.Output; // the same list as the main window's "Sortie"
        Update();
        RefreshThumb();
    }

    void OnBrowseOut(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Dossier du disque") };
        if (dlg.ShowDialog(this) != true) return;
        OutBox.Text = dlg.FolderName;
        outEdited = true;
    }

    void OnCancel(object s, RoutedEventArgs e)
    {
        if (writing && MessageBox.Show(this, "Arrêter la gravure ?\n\nLe disque en cours d'écriture sera probablement inutilisable (un disque réinscriptible devra être effacé). L'arrêt peut prendre quelques minutes.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Annulation…";
        cancel?.Cancel();
    }

    /// <summary>
    /// Runs one long job off the UI thread with its progress bar (indeterminate until the job reports a value) and a log line per step.
    /// Returns false when it failed or was cancelled.
    /// </summary>
    async Task<bool> Work(string what, Action<Action<string>, IProgress<double>, CancellationToken> job, bool canCancel = true)
    {
        busy = true;
        StartButton.IsEnabled = BurnButton.IsEnabled = EraseButton.IsEnabled = false;
        Busy.IsIndeterminate = true;
        StatusText.Text = what;
        ProgressPanel.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = canCancel;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var progress = new Progress<double>(v => { Busy.IsIndeterminate = false; Busy.Value = v * 100; });
        void Log(string line) => Dispatcher.BeginInvoke(() => { Append(line); StatusText.Text = line; });
        try
        {
            await Task.Run(() => job(Log, progress, token));
            return true;
        }
        catch (OperationCanceledException) { Warn("Annulé."); return false; }
        catch (Exception ex)
        {
            Warn("ERREUR : " + ex.Message);
            return false;
        }
        finally
        {
            busy = writing = false;
            StartButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            cancel.Dispose();
            cancel = null;
            RefreshDrives();
            if (closeAfterWork) Close();
        }
    }

    async void OnStart(object s, RoutedEventArgs e)
    {
        if (titles.Count == 0) { MessageBox.Show(this, "Ajoutez d'abord une vidéo.", "BurnSmith"); return; }
        if (StillLoadingDesign()) return;
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0) { MessageBox.Show(this, "Choisissez un dossier de sortie.", "BurnSmith"); return; }
        var cap = (Output)CapacityBox.SelectedItem;
        var enc = encode with { Hd720 = cap.Hd720 && kind == "bd" };
        var play = playback;
        var hdmv = hdmvMenu;
        var label = volume;
        var drive = DriveBox.SelectedItem as Imapi.Drive;
        var speed = Iso.Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
        var burn = BurnCheck.IsChecked == true && drive != null;
        if (burn && cap.Unlimited) { MessageBox.Show(this, "« Sans limite » ne se grave pas : choisissez la taille du disque à graver, ou décochez la gravure.", "BurnSmith"); return; }
        if (burn && MessageBox.Show(this,
                $"Après la création, graver « {label} » sur {drive!.Letter} ?\n\nInsérez un disque vierge adapté ({cap.Name}). Le disque sera écrit définitivement.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var minutes = chapterMinutes;
        // a trial disc: a few minutes of each video, in a folder of its own, so the real disc's folder is never replaced by it
        var trial = TrialCheck.IsChecked == true ? TrialSeconds : 0;
        if (trial > 0) outDir = Path.TrimEndingDirectorySeparator(outDir) + "_essai";
        var infos = titles.GroupBy(t => t.Path).ToDictionary(g => g.Key, g => g.First().Info);
        var discTitles = Project.OnDisc(titles.Select(t => t.ToDisc()).ToList(), minutes, trial, path => infos[path]);
        var menu = MenuCheck.IsChecked == true || NeedsMenu(discTitles) ? design.Clone() : null;
        var look = subtitleStyle;
        var uhd = kind == "uhd";
        var dvd = kind == "dvd";
        var withIso = IsoCheck.IsChecked == true;
        Log.Clear();

        var ok = await Work("Création du disque…", (log, progress, token) =>
        {
            if (trial > 0) log($"Disque d'essai : {trial / 60} minutes de chaque vidéo, un passage à chaque chapitre, dans {outDir}");
            if (dvd) Dvd.Build(new DvdJob(discTitles, outDir, menu, minutes, cap.Usable, enc, play, look), log, token);
            else BluRay.Build(new BluRayJob(discTitles, outDir, menu, uhd, label.Length > 0 ? label : "BURNSMITH", minutes, enc, cap.Usable, play, look, hdmv), log, token);
            var iso = IsoFile(outDir);
            File.Delete(iso); // an image of an earlier build must never be burnt by mistake
            var problems = DiscCheck.Folder(outDir, cap.Gb);
            log(problems.Count == 0 ? "Vérification de la structure du disque : aucune anomalie." : "Vérification de la structure du disque : " + string.Join(" ; ", problems));
            if (burn && problems.Count > 0) throw new InvalidOperationException("Le disque n'est pas gravé, sa structure a des anomalies : " + string.Join(" ; ", problems));
            if (!withIso && !burn) return;
            token.ThrowIfCancellationRequested();
            log("Création de l'image ISO…");
            Iso.Build(outDir, iso, label, dvd, log, progress, token);
            var imageProblems = DiscCheck.Image(iso, outDir, cap.Gb);
            log(imageProblems.Count == 0 ? "Vérification de l'image ISO : aucune anomalie." : "Vérification de l'image ISO : " + string.Join(" ; ", imageProblems));
            if (burn && imageProblems.Count > 0) throw new InvalidOperationException("Le disque n'est pas gravé, l'image ISO a des anomalies : " + string.Join(" ; ", imageProblems));
            if (burn)
            {
                writing = true;
                token.ThrowIfCancellationRequested();
                Iso.Burn(iso, drive!.Letter, log, progress, token, speed);
            }
            if (!withIso) File.Delete(iso); // only made to burn
        });
        if (ok) Append(trial == 0 ? "Terminé." : $"Disque d'essai terminé. Ouvrez {(withIso ? IsoFile(outDir) : outDir)} dans VLC (Média > Ouvrir un disque) pour vérifier le menu, puis décochez « Essai » pour le vrai disque.");
    }

    static string IsoFile(string folder) => Project.IsoFile(folder);

    // ---- queue ----

    /// <summary>
    /// « File d'attente »: discs made one after the other, each a whole project with its own folder (and its ISO when it asks for one); nothing is burnt.
    /// shortcut: the queue lives as long as the window; a disc to keep for another day is a saved project, added again with « Ajouter des projets enregistrés ».
    /// </summary>
    readonly List<(string Name, Project Disc)> queue = [];

    /// <summary>For the snapshot run: the disc of the window put in the queue.</summary>
    public void TryQueue() => QueueCurrent();

    void ShowQueue() => QueueCount.Text = queue.Count > 0 ? queue.Count.ToString() : "";

    void OnQueue(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)s, Placement = PlacementMode.Bottom };
        var current = new MenuItem { Header = "Ajouter ce disque à la file", IsEnabled = titles.Count > 0 };
        current.Click += (_, _) => QueueCurrent();
        menu.Items.Add(current);
        var saved = new MenuItem { Header = "Ajouter des projets enregistrés…" };
        saved.Click += (_, _) => QueueFiles();
        menu.Items.Add(saved);
        if (queue.Count > 0) menu.Items.Add(new Separator());
        foreach (var item in queue.ToList())
        {
            var row = new MenuItem { Header = $"Retirer « {item.Name} »  ({item.Disc.Kind switch { "dvd" => "DVD", "uhd" => "Blu-ray 4K", _ => "Blu-ray" }}, {item.Disc.Out})" };
            row.Click += (_, _) => { queue.Remove(item); ShowQueue(); };
            menu.Items.Add(row);
        }
        if (queue.Count > 0)
        {
            menu.Items.Add(new Separator());
            var run = new MenuItem { Header = queue.Count > 1 ? $"Lancer la file ({queue.Count} disques)" : "Lancer la file (1 disque)", IsEnabled = !busy };
            run.Click += (_, _) => RunQueue();
            menu.Items.Add(run);
        }
        menu.IsOpen = true;
    }

    /// <summary>The disc as the window holds it now, frozen: later changes in the window do not reach the queue.</summary>
    void QueueCurrent()
    {
        if (StillLoadingDesign()) return;
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0) { MessageBox.Show(this, "Choisissez un dossier de sortie.", "BurnSmith"); return; }
        var cap = (Output)CapacityBox.SelectedItem;
        Queue(volume.Length > 0 ? volume : titles[0].Name, Project.Parse(Snapshot().ToJson()) with { Out = outDir, Label = volume, Encode = encode with { Hd720 = cap.Hd720 && kind == "bd" } });
    }

    void QueueFiles()
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Projets à ajouter à la file"), Filter = Loc.Filter($"Projet BurnSmith|*{Project.Extension}"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames)
            try { Queue(Path.GetFileNameWithoutExtension(file), Project.Load(file)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { Warn($"{Path.GetFileName(file)} : {ex.Message}"); }
    }

    void Queue(string name, Project disc)
    {
        // two discs in one folder: the second would replace the first
        if (disc.Out is not { Length: > 0 }) Warn($"« {name} » n'est pas ajouté à la file : ce projet n'indique pas de dossier de sortie. Ouvrez-le, choisissez le dossier, enregistrez-le.");
        else if (queue.Any(q => string.Equals(Path.GetFullPath(q.Disc.Out!), Path.GetFullPath(disc.Out), StringComparison.OrdinalIgnoreCase) && (q.Disc.Trial > 0) == (disc.Trial > 0)))
            Warn($"« {name} » n'est pas ajouté à la file : un disque de la file va déjà dans {disc.Out}. Choisissez un autre dossier de sortie.");
        else
        {
            queue.Add((name, disc));
            Append($"File d'attente : « {name} » ajouté ({queue.Count} disque{(queue.Count > 1 ? "s" : "")}). Le bouton de la file permet de la lancer.");
        }
        ShowQueue();
    }

    async void RunQueue()
    {
        if (busy || queue.Count == 0) return;
        var items = queue.ToList();
        var done = new List<(string Name, Project Disc)>();
        var failed = new List<string>();
        Log.Clear();
        var ended = await Work("File d'attente…", (log, progress, token) =>
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                log($"File d'attente : disque {done.Count + failed.Count + 1} sur {items.Count}, « {item.Name} »");
                try
                {
                    var problems = item.Disc.Build(null, item.Disc.Trial, log, token, progress);
                    log(problems.Count == 0 ? $"« {item.Name} » : vérification du disque, aucune anomalie." : $"« {item.Name} » : " + string.Join(" ; ", problems));
                    if (problems.Count == 0) done.Add(item); else failed.Add(item.Name);
                }
                // one disc that fails must not lose the night for the others
                catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add(item.Name); log($"ERREUR, « {item.Name} » : {ex.Message}"); }
            }
        });
        queue.RemoveAll(done.Contains); // what failed or was not reached stays, to be run again
        ShowQueue();
        if (failed.Count > 0) Warn($"File d'attente : {done.Count} disque(s) fait(s), {failed.Count} en échec, resté(s) dans la file : {string.Join(", ", failed)}.");
        else if (ended) Append($"File d'attente terminée : {done.Count} disque(s) fait(s).");
    }

    /// <summary>Burns an image that already exists (made earlier, or downloaded) on a DVD / Blu-ray / Blu-ray 4K.</summary>
    async void OnBurnIso(object s, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not Imapi.Drive drive) { MessageBox.Show(this, "Aucun graveur détecté.", "BurnSmith"); return; }
        var dlg = new OpenFileDialog { Title = Loc.T("Image à graver"), Filter = Loc.Filter("Images disque|*.iso|Tous les fichiers|*.*") };
        if (dlg.ShowDialog(this) != true) return;
        var iso = dlg.FileName;
        var answer = MessageBox.Show(this,
            $"Graver « {Path.GetFileName(iso)} » sur {drive.Letter} ?\n\nInsérez un disque vierge adapté à la taille de l'image. Le disque sera écrit définitivement.",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        writing = true;
        var speed = Iso.Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
        if (await Work("Gravure…", (log, progress, token) => Iso.Burn(iso, drive.Letter, log, progress, token, speed))) Append("Terminé.");
    }

    async void OnErase(object s, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not Imapi.Drive drive) { MessageBox.Show(this, "Aucun graveur détecté.", "BurnSmith"); return; }
        if (MessageBox.Show(this, $"Effacer le disque réinscriptible de {drive.Letter} ?\n\nToutes les données du disque seront perdues.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        writing = true;
        if (await Work("Effacement…", (log, _, _) => Imapi.Erase(drive.Letter, log), canCancel: false)) Append("Terminé."); // IMAPI's erase takes no cancel
    }
}
