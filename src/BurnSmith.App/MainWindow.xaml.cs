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
        /// <summary>Sound files added to the video (a dub found apart), numbered after its own audio tracks (see <see cref="DiscTitle.AudioFiles"/>).</summary>
        public List<AudioTrack> AudioFiles { get; set; } = [];
        /// <summary>dB each kept audio track is made louder (quieter when negative), by its number in <see cref="Audio"/>.</summary>
        public Dictionary<int, double> AudioVolume { get; set; } = [];
        /// <summary>The names of the chapters of <see cref="ChapterAt"/>, one for one (null = "Chapitre N" on the menu).</summary>
        public IReadOnlyList<string?>? ChapterTitles { get; set; }
        public string? EndAction { get; set; }
        public GridLength HdrColWidth { get; set; } = new(84);
        public DiscTitle ToDisc() => new(Path, Name, Audio, Subs, Start, End, Volume, ChapterAt, Extra, Bonus, DefaultAudio, DefaultSub, ChapterMinutes, Intro, TrackLanguage, Effects, Clips, ThumbAt, ThumbImage, Overview, SubDelay, AudioDelay, ChapterTitles, AudioFiles, AudioVolume, EndAction);

        /// <summary>The row of a title read from a project or an undo step.</summary>
        public static Entry From(DiscTitle d, SourceInfo info) => new(d.Video, info)
        {
            Name = d.DisplayName, Audio = [.. d.Audio ?? info.Audio.Take(1).Select(a => a.Index)], Subs = [.. d.Subtitles ?? []], Extra = [.. d.External ?? []], Start = d.Start, End = d.End, Volume = d.Volume, ChapterAt = d.ChapterAt,
            ChapterMinutes = d.ChapterMinutes, Bonus = d.Bonus, Intro = d.Intro, DefaultAudio = d.DefaultAudio, DefaultSub = d.DefaultSubtitle, TrackLanguage = new(d.TrackLanguage ?? new Dictionary<string, string>()),
            Effects = d.Effects, Clips = d.Clips, ThumbAt = d.ThumbAt, ThumbImage = d.ThumbImage, Overview = d.Overview, SubDelay = new(d.SubtitleDelay ?? new Dictionary<int, double>()),
            AudioDelay = new(d.AudioDelay ?? new Dictionary<int, double>()), ChapterTitles = d.ChapterTitles, AudioFiles = [.. d.AudioFiles ?? []], AudioVolume = new(d.AudioVolume ?? new Dictionary<int, double>()),
            EndAction = d.EndAction,
        };

        /// <summary>Another row for the same video with the same settings (a title cut in two).</summary>
        public Entry Clone() => new(Path, Info)
        {
            Name = Name, Audio = [.. Audio], Subs = [.. Subs], Extra = [.. Extra], Start = Start, End = End, Volume = Volume, ChapterAt = ChapterAt,
            ChapterMinutes = ChapterMinutes, Bonus = Bonus, Intro = Intro, DefaultAudio = DefaultAudio, DefaultSub = DefaultSub, TrackLanguage = new(TrackLanguage), Effects = Effects, Clips = Clips,
            ThumbAt = ThumbAt, ThumbImage = ThumbImage, Overview = Overview, SubDelay = new(SubDelay), AudioDelay = new(AudioDelay), ChapterTitles = ChapterTitles, AudioFiles = [.. AudioFiles], AudioVolume = new(AudioVolume),
            EndAction = EndAction,
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
    Task defaultDesign; // done again when videos added call for another template than the one BurnSmith chose (autoDesign)
    MenuDesign? autoDesign; // the template BurnSmith chose itself, for autoFor: any other (the editor's, a project's, an undo step's) is the user's and stays
    string? autoFor;

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
        QuickChapterBox.ItemsSource = new[] { 5, 10, 15, 20, 30 };
        SyncQuickSettings();
        SetKind("bd");
        (VerifyCheck.IsChecked, EjectCheck.IsChecked) = (BurnSetting("BurnVerify", true), BurnSetting("BurnEject", false));
        RefreshDrives();
        Activated += (_, _) => RefreshDrives(); // a burner plugged in meanwhile shows up when the user comes back
        AfterTitlesChanged();
        LoadQueue();
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
            List<string> videos;
            MenuDesign found;
            do // again when videos were added during the scan
            {
                videos = MainVideos();
                found = await Task.Run(() => MenuRenderer.Sta(() => TemplateCatalog.DefaultFor(videos).Clone()));
            } while (TemplateCatalog.Suggested(videos) != TemplateCatalog.Suggested(MainVideos()));
            if (!ReferenceEquals(design, blank)) return;
            if (autoDesign == null || found.Name != design.Name) UseDesign(found);
            (autoDesign, autoFor) = (design, TemplateCatalog.Suggested(videos));
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
            if (!drives.SequenceEqual(DriveBox.Items.Cast<Imapi.Drive>())) // a disc put in or taken out changes the list too
            {
                var chosen = (DriveBox.SelectedItem as Imapi.Drive)?.Letter;
                DriveBox.ItemsSource = drives;
                DriveBox.SelectedIndex = drives.Count > 0 ? Math.Max(0, drives.FindIndex(d => d.Letter == chosen)) : -1;
            }
            BurnButton.IsEnabled = EraseButton.IsEnabled = BurnCheck.IsEnabled = drives.Count > 0;
            if (drives.Count == 0) BurnCheck.IsChecked = false;
        }
        // logged, not shown: this runs at every activation of the window, and a box would bring it back as soon as it is closed
        catch (Exception e) when (e is not OutOfMemoryException) { AppFiles.LogError("Lecteurs de disque", e); }
        finally { refreshingDrives = false; }
    }

    void OnDrivesOpened(object? s, EventArgs e) => RefreshDrives(); // the disc in each burner, read again when the user looks, never polled

    /// <summary>Asks before burning when the disc in <paramref name="drive"/> holds less than <paramref name="usableGb"/>; true = go on.</summary>
    async Task<bool> DiscBigEnough(Imapi.Drive drive, double usableGb, string what)
    {
        if (await Task.Run(() => Iso.Disc(drive.Letter)) is not { } disc || !Iso.TooSmall(disc, usableGb)) return true;
        return MessageBox.Show(this, $"Le disque de {drive.Letter} ({disc.Kind}, {disc.TotalBytes / 1e9:0.#} Go) est trop petit pour {what} ({usableGb:0.#} Go).\n\nMettez un disque plus grand avant la gravure. Continuer quand même ?",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public MenuEditorWindow CreateEditor(MenuDesign? start = null) => openEditor = new(start ?? design, previewFrame, EditorContent())
        { Playback = playback, DvdPreview = kind == "dvd", Hdmv = kind != "dvd" && (hdmvMenu || CompatCheck.IsChecked == true),
          CanPreviewPopup = kind != "dvd" && !hdmvMenu && CompatCheck.IsChecked != true };

    /// <summary>The video the menu's picture, the disc's name and the audio / subtitle pages come from: the first that is not a bonus.</summary>
    Entry? MainEntry => titles.FirstOrDefault(t => !t.Bonus && !t.Intro) ?? titles.FirstOrDefault();
    List<string> MainVideos() => [.. titles.Where(t => !t.Bonus && !t.Intro).Select(t => t.Path)];

    /// <summary>What the editor previews: the real chapters / episodes / bonus / tracks of the disc, or a sample while no video is loaded.</summary>
    MenuContent EditorContent()
    {
        if (MainEntry is not { } first)
            return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, 12).ToList(), null, ["Français 5.1", "English 5.1"], ["Français", "English"])
                { PopupTitles = [(1, 12, ["Français 5.1", "English 5.1"], [("Français", 1), ("English", 2)])] };
        var disc = first.ToDisc();
        var (main, extras) = DiscOrder(first);
        var order = main.Concat(extras).ToList();
        var discTitles = order.Select(t => t.ToDisc()).ToList();
        var sources = order.Select(t => t.Tracks).ToList();
        var lined = order.Count > 1 ? DiscTitle.LineUpSubtitles(discTitles, sources) : null;
        var popup = order.Select((entry, index) =>
        {
            var title = discTitles[index];
            var audio = title.KeptAudio(sources[index]);
            var subs = lined?[index] ?? title.KeptSubtitles(sources[index]).Where(track => track.Supported).ToList();
            var (listed, _) = BluRay.ForcedTracks(audio.Select(track => track.Lang).ToList(), subs.Select(track => (track.Lang, track.Forced)).ToList());
            return (Playlist: index + 1, Chapters: title.ChapterList(entry.OnDisc.Duration, chapterMinutes).Count,
                Audio: (IReadOnlyList<string>)audio.Select(track => track.Name).ToList(),
                Subtitles: (IReadOnlyList<(string Name, int Number)>)subs.Take(listed).Select((track, n) => (track, n + 1))
                    .Where(pair => pair.track.Index >= 0).Select(pair => (pair.track.Name, pair.Item2)).ToList());
        }).ToList();
        var series = main.Count > 1;
        // the same moments as the disc's pictures: BluRay.Build and Dvd.Build take them there
        string? TenPercent(Entry t) => Frame(t, t.Start + t.OnDisc.Duration * 0.1);
        return new MenuContent(1, previewFrame, series ? [] : disc.ChapterList(first.OnDisc.Duration, chapterMinutes).Select(at => Frame(first, first.Start + at + 3)).ToList(),
            series ? main.Select((t, i) => new TitleEntry(t.Name, i + 1, TenPercent(t), t.Overview)).ToList() : null,
            disc.KeptAudio(first.Tracks).Select(a => a.Name).ToList(), disc.KeptSubtitles(first.Tracks).Where(s => s.Supported).Select(s => s.Name).ToList(),
            extras.Count > 0 ? extras.Select((t, i) => new TitleEntry(t.Name, main.Count + i + 1, TenPercent(t))).ToList() : null)
            { ChapterNames = series ? null : disc.ChapterNames(first.OnDisc.Duration, chapterMinutes), PopupTitles = popup, HasIntro = titles.Any(t => t.Intro) };
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

    /// <summary>« Copier le rapport »: the log, headed by the « À savoir avant de démarrer » of the last start, to paste where help is asked.</summary>
    void OnCopyReport(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Log.Text); }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show(this, "Le presse-papiers est occupé par un autre programme : réessayez.", "BurnSmith"); }
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

    void OnBurnOptionsClick(object s, RoutedEventArgs e)
    {
        BurnOptionsButton.ContextMenu.PlacementTarget = BurnOptionsButton;
        BurnOptionsButton.ContextMenu.IsOpen = true;
    }

    // the burn options belong to this PC, not to a project: kept beside the app's language (HKCU\Software\BurnSmith)
    const string SettingsKey = @"Software\BurnSmith";

    static bool BurnSetting(string name, bool byDefault)
    {
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey); return key?.GetValue(name) is int v ? v != 0 : byDefault; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return byDefault; }
    }

    void OnBurnOption(object s, RoutedEventArgs e)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("BurnVerify", VerifyCheck.IsChecked ? 1 : 0);
            key.SetValue("BurnEject", EjectCheck.IsChecked ? 1 : 0);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { AppFiles.LogError("Options de gravure", ex); }
    }

    /// <summary>Just before burning, off the UI thread: true when the rewritable disc in <paramref name="letter"/> holds data and the user agreed to erase it, false when it is blank; their « non » cancels.</summary>
    bool EraseFirst(string letter)
    {
        if (Iso.UsedRewritable(letter) == null) return false;
        return Dispatcher.Invoke(() => MessageBox.Show(this, $"Le disque dans {letter} contient déjà des données. L'effacer avant de graver ?", "BurnSmith",
                   MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes ? true : throw new OperationCanceledException();
    }

    void SetKind(string k)
    {
        kind = k;
        ModeText.Text = k == "dvd" ? "DVD" : k == "uhd" ? "Blu-ray 4K" : "Blu-ray";
        CapacityBox.ItemsSource = Output.For(k);
        CapacityBox.SelectedItem = k == "bd" ? Output.DefaultBd : Output.For(k)[0];
        var hdrWidth = k == "dvd" ? new GridLength(0) : new GridLength(84);
        if (HdrHeaderCol != null) HdrHeaderCol.Width = hdrWidth;
        if (HdrHeaderBorder != null) HdrHeaderBorder.Visibility = k == "dvd" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var t in titles)
        {
            t.HdrColWidth = hdrWidth;
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

    bool syncingQuick;

    void SyncQuickSettings()
    {
        if (QuickPlayModeBox == null || QuickChapterBox == null || OutputModeBox == null) return;
        syncingQuick = true;
        QuickPlayModeBox.SelectedIndex = Math.Clamp(playback.Mode, 0, 2);
        QuickChapterBox.SelectedItem = QuickChapterBox.Items.Cast<int>().Contains(chapterMinutes) ? chapterMinutes : 5;
        var iso = IsoCheck.IsChecked == true;
        var burn = BurnCheck.IsChecked == true;
        OutputModeBox.SelectedIndex = burn ? (iso ? 3 : 2) : (iso ? 1 : 0);
        syncingQuick = false;
    }

    void OnQuickPlayMode(object s, SelectionChangedEventArgs e)
    {
        if (syncingQuick || QuickPlayModeBox.SelectedIndex < 0) return;
        playback = PlaybackOptions.FromMode(QuickPlayModeBox.SelectedIndex, playback.Rewind)
            with { AutoStartSeconds = playback.AutoStartSeconds, RememberPosition = playback.RememberPosition };
        Remember();
    }

    void OnQuickChapter(object s, SelectionChangedEventArgs e)
    {
        if (syncingQuick || QuickChapterBox.SelectedItem is not int minutes) return;
        chapterMinutes = minutes;
        Remember();
    }

    void OnOutputModeChanged(object s, SelectionChangedEventArgs e)
    {
        if (syncingQuick || OutputModeBox.SelectedIndex < 0) return;
        var idx = OutputModeBox.SelectedIndex;
        if (idx >= 2 && !BurnCheck.IsEnabled)
        {
            Warn("Aucun graveur détecté : choisissez Dossier ou Image ISO.");
            SyncQuickSettings();
            return;
        }
        IsoCheck.IsChecked = idx is 1 or 3;
        BurnCheck.IsChecked = idx is 2 or 3;
        Remember();
    }

    void OnStep3Click(object s, RoutedEventArgs e)
    {
        OutBox.Focus();
        OutBox.SelectAll();
    }

    void Update()
    {
        if (CapacityBox?.SelectedItem is not Output cap || CapBar == null) return;
        var hdrWidth = kind == "dvd" ? new GridLength(0) : new GridLength(84);
        foreach (var t in titles) if (t.HdrColWidth != hdrWidth) { t.HdrColWidth = hdrWidth; t.Refresh(); }
        if (Step1Btn != null)
        {
            var hasVideos = titles.Count > 0;
            var hasOut = !string.IsNullOrWhiteSpace(OutBox?.Text);
            Step1Btn.Style = (Style)FindResource(!hasVideos ? "Primary" : typeof(Button));
            Step2Btn.Style = (Style)FindResource(hasVideos && !hasOut ? "Primary" : typeof(Button));
            Step3Btn.Style = (Style)FindResource(hasVideos && hasOut ? "Primary" : typeof(Button));
        }
        SyncQuickSettings();
        var est = 0.0;
        var note = "";
        if (titles.Count > 0)
        {
            est = Estimate(cap, encode) ?? titles.Sum(t =>
            {
                var raw = Tools.SizeOf(t.Path) * t.OnDisc.Duration / Math.Max(1, t.Info.Duration);
                var droppedAudio = t.Info.Audio.Where((_, idx) => !t.Audio.Contains(idx))
                    .Sum(a => (a.Codec is "truehd" or "dts" ? 3000.0 : a.Channels >= 6 ? 640.0 : 256.0) * 125.0 * t.OnDisc.Duration);
                return Math.Max(0, raw - droppedAudio);
            }) / 1e9;
            if (!cap.Unlimited && est > cap.Usable) note = "\ntrop grand pour ce support";
            if (titles.Any(t => !File.Exists(t.Path))) note = "\nune vidéo source est introuvable";
            if (kind == "uhd" && !encode.ForceReencode && titles.Any(t => t.Info.VideoCodec != "hevc" || t.Info.Height != 2160)) note = "\nPassthrough : sources HEVC 2160p seulement";
        }
        CapBar.Value = cap.Unlimited ? 0 : Math.Min(100, est / cap.Gb * 100);
        CapText.Text = cap.Unlimited ? $"{est:0.00} Go, sans limite de taille{note}" : $"{est:0.00} Go / {cap.Usable:0.#} Go utilisables{note}";
        if (SplitSuggestPanel != null)
        {
            var probedItems = titles.Select(t => (t.ToDisc(), t.OnDisc)).ToList();
            if (!cap.Unlimited && titles.Count(t => !t.Intro) > 1
                && Snapshot().ShouldSuggestSplit(out var rawEst, out var usableGb, probedItems))
            {
                var overPct = (int)Math.Round((rawEst / usableGb - 1) * 100);
                SplitSuggestText.Text = $"La saison dépasse ce disque de {overPct} % ({rawEst:0.0} Go) : répartir sur plusieurs disques limite la perte de qualité.";
                SplitSuggestPanel.Visibility = Visibility.Visible;
            }
            else SplitSuggestPanel.Visibility = Visibility.Collapsed;
        }
        ProjectInfo.Text = titles.Count == 0 ? "" : $"{titles.Count} vidéo{(titles.Count > 1 ? "s" : "")}{(titles.Any(t => t.Bonus) ? $" (dont {titles.Count(t => t.Bonus)} bonus)" : "")}{(titles.Any(t => t.Intro) ? " (dont une vidéo de début)" : "")}  ·  {titles.Sum(t => Tools.SizeOf(t.Path)) / 1e9:0.0} Go";
    }

    /// <summary>Size in GB of the Blu-ray for an output and an encoding (the video at the fitted rate, plus audio and menu); null for a DVD, a 4K disc or no video.</summary>
    double? Estimate(Output output, EncodeOptions enc) =>
        titles.Count == 0 || kind == "uhd" ? null
        : kind == "dvd" ? Dvd.EstimateGB(titles.Select(t => (t.OnDisc, t.ToDisc())).ToList(), output.Usable, enc.DvdKbps)
        : BluRay.EstimateGB(new BluRayJob(titles.Select(t => t.ToDisc()).ToList(), "", MenuCheck.IsChecked == true || NeedsMenu(titles.Select(t => t.ToDisc())) ? design : null, false, "X", chapterMinutes, enc, output.Usable),
            titles.Select(t => t.OnDisc).ToList());

}
