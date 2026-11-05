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
        public string Name { get; set; } = System.IO.Path.GetFileNameWithoutExtension(path);
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
        /// <summary>The name as the table shows it.</summary>
        public string Shown => Intro ? $"Début · {Name}" : Bonus ? $"Bonus · {Name}" : Name;

        /// <summary>The source as the disc sees it: its duration is the part kept.</summary>
        public SourceInfo OnDisc => ToDisc().Cut(Info);

        /// <summary>The video as the track lists see it: its own audio and subtitles, then the subtitle files.</summary>
        public SourceInfo Tracks => ToDisc().WithSubtitleFiles(Info);

        /// <summary>The length on the disc, which is the cut one once the video was edited.</summary>
        public string DurationText => Timecode.Format(Math.Floor(OnDisc.Duration));
        public string? DurationTip => ToDisc() is { Trimmed: true } ? $"Durée d'origine : {Timecode.Format(Math.Floor(Info.Duration))}" : null;
        public string Hdr => Info.Hdr;
        public string AudioText => Summary(ToDisc().KeptAudio(Tracks).Select(a => a.Name).ToList()); // in disc order: the default one first
        public string SubText => Summary(Subs.OrderBy(i => i == DefaultSub ? 0 : 1).Select(i => (Tracks.Subtitles.FirstOrDefault(t => t.Index == i)?.Name ?? "?") + (i == DefaultSub ? " (défaut)" : "")).ToList());

        static string Summary(List<string> names) => names.Count == 0 ? "Aucun" : names.Count == 1 ? names[0] : $"{names[0]} +{names.Count - 1}";
        public DiscTitle ToDisc() => new(Path, Name, Audio, Subs, Start, End, Volume, ChapterAt, Extra, Bonus, DefaultAudio, DefaultSub, ChapterMinutes, Intro, TrackLanguage);

        /// <summary>Another row for the same video with the same settings (a title cut in two).</summary>
        public Entry Clone() => new(Path, Info)
        {
            Name = Name, Audio = [.. Audio], Subs = [.. Subs], Extra = [.. Extra], Start = Start, End = End, Volume = Volume, ChapterAt = ChapterAt,
            ChapterMinutes = ChapterMinutes, Bonus = Bonus, Intro = Intro, DefaultAudio = DefaultAudio, DefaultSub = DefaultSub, TrackLanguage = new(TrackLanguage),
        };
    }

    readonly ObservableCollection<Entry> titles = [];
    string kind = "bd"; // dvd | bd | uhd
    string volume = "MON_DISQUE";
    int chapterMinutes = 5;
    EncodeOptions encode = new(FitDisc: true); // "Adapter au disque": the video stays as it is while it fits
    PlaybackOptions playback = new(); // back to the menu after the title that was picked, as before the option existed
    string? previewFrame, previewFor;
    bool labelEdited, outEdited, busy, refreshingDrives;
    int thumbVersion;
    CancellationTokenSource? cancel;
    MenuDesign design = TemplateCatalog.Default().Clone();

    public MainWindow()
    {
        InitializeComponent();
        FrenchLabels();
        MenuName.Text = design.Name;
        TitleList.ItemsSource = titles;
        SetKind("bd");
        RefreshDrives();
        Activated += (_, _) => RefreshDrives(); // a burner plugged in meanwhile shows up when the user comes back
        AfterTitlesChanged();
        Loaded += async (_, _) => await OfferDvdIfPossible();
    }

    /// <summary>The released exe carries neither dvdauthor nor spumux: DVD is on offer only when they were put in place. Off the UI thread: looking waits for the exe's tools to unpack.</summary>
    async Task OfferDvdIfPossible()
    {
        var missing = await Task.Run(() =>
        {
            var absent = new List<string>();
            foreach (var tool in new[] { "dvdauthor.exe", "spumux.exe" })
                try { Tools.Find(tool); }
                catch (FileNotFoundException) { absent.Add(tool); }
                catch (Exception) { } // a tools folder that cannot be read must not stop the window at launch: the build itself will say what is wrong
            return absent;
        });
        if (missing.Count == 0) return;
        DvdItem.IsEnabled = false;
        DvdItem.ToolTip = $"Indisponible : {string.Join(" et ", missing)} introuvable(s). Placez ces programmes dans un dossier tools à côté de BurnSmith.";
        if (kind == "dvd") SetKind("bd"); // chosen in the moment before the answer came
    }

    void FrenchLabels()
    {
        design.PlayLabel = "Lecture"; design.ScenesLabel = "Scènes"; design.HomeLabel = "Accueil"; design.ChapterLabel = "Chapitre";
        design.PlayAllLabel = "Tout lire"; design.EpisodesLabel = "Épisodes"; design.SettingsLabel = "Réglages"; design.AudioLabel = "Audio";
        design.SubtitlesLabel = "Sous-titres"; design.OffLabel = "Désactivés"; design.BackLabel = "Retour";
    }

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
            var drives = await Task.Run(() =>
            {
                try { return Imapi.Drives().Where(d => d.Letter.Length > 0).ToList(); }
                catch (Exception) { return new List<Imapi.Drive>(); } // IMAPI2 unavailable: no burning, creating still works
            });
            if (busy) return;
            if (!drives.Select(d => d.Id).SequenceEqual(DriveBox.Items.Cast<Imapi.Drive>().Select(d => d.Id)))
            {
                DriveBox.ItemsSource = drives;
                DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
            }
            BurnButton.IsEnabled = EraseButton.IsEnabled = BurnCheck.IsEnabled = drives.Count > 0;
            if (drives.Count == 0) BurnCheck.IsChecked = false;
        }
        finally { refreshingDrives = false; }
    }

    public MenuEditorWindow CreateEditor(MenuDesign? start = null) => new(start ?? design, previewFrame, EditorContent());

    /// <summary>The video the menu's picture, the disc's name and the audio / subtitle pages come from: the first that is not a bonus.</summary>
    Entry? MainEntry => titles.FirstOrDefault(t => !t.Bonus && !t.Intro) ?? titles.FirstOrDefault();

    /// <summary>What the editor previews: the real chapters / episodes / bonus / tracks of the disc, or a sample while no video is loaded.</summary>
    MenuContent EditorContent()
    {
        if (MainEntry is not { } first)
            return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, 12).ToList(), null, ["Français 5.1", "English 5.1"], ["Français", "English"]);
        var disc = first.ToDisc();
        var main = titles.Where(t => !t.Intro && (!t.Bonus || t == first)).ToList(); // as on the disc: the feature first, then the extras (the start video is on neither)
        var extras = titles.Where(t => t.Bonus && !t.Intro && t != first).ToList();
        var series = main.Count > 1;
        var chapters = series ? 0 : disc.ChapterList(first.OnDisc.Duration, chapterMinutes).Count;
        return new MenuContent(1, previewFrame, Enumerable.Repeat<string?>(null, chapters).ToList(),
            series ? main.Select((t, i) => new TitleEntry(t.Name, i + 1, null)).ToList() : null,
            disc.KeptAudio(first.Tracks).Select(a => a.Name).ToList(), disc.KeptSubtitles(first.Tracks).Where(s => s.Supported).Select(s => s.Name).ToList(),
            extras.Count > 0 ? extras.Select((t, i) => new TitleEntry(t.Name, main.Count + i + 1, null)).ToList() : null);
    }

    void Append(string line)
    {
        Log.AppendText(line + Environment.NewLine);
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
            est = kind == "dvd" ? Dvd.EstimateGB(titles.Select(t => (t.OnDisc, t.ToDisc())).ToList(), cap.Usable) : Estimate(cap, encode) ?? titles.Sum(t => new FileInfo(t.Path).Length * t.OnDisc.Duration / t.Info.Duration) / 1e9;
            if (est > cap.Usable) note = "\ntrop grand pour ce support";
            if (kind == "uhd" && titles.Any(t => t.Info.VideoCodec != "hevc" || t.Info.Height != 2160)) note = "\nles sources doivent être en HEVC 2160p";
        }
        CapBar.Value = Math.Min(100, est / cap.Gb * 100);
        CapText.Text = $"{est:0.00} Go / {cap.Usable:0.#} Go utilisables{note}";
        ProjectInfo.Text = titles.Count == 0 ? "" : $"{titles.Count} vidéo{(titles.Count > 1 ? "s" : "")}{(titles.Any(t => t.Bonus) ? $" (dont {titles.Count(t => t.Bonus)} bonus)" : "")}{(titles.Any(t => t.Intro) ? " (dont une vidéo de début)" : "")}  ·  {titles.Sum(t => new FileInfo(t.Path).Length) / 1e9:0.0} Go";
    }

    /// <summary>Size in GB of the Blu-ray for an output and an encoding (the video at the fitted rate, plus audio and menu); null for a DVD, a 4K disc or no video.</summary>
    double? Estimate(Output output, EncodeOptions enc) =>
        kind != "bd" || titles.Count == 0 ? null
        : BluRay.EstimateGB(new BluRayJob(titles.Select(t => t.ToDisc()).ToList(), "", MenuCheck.IsChecked == true || NeedsMenu(titles.Select(t => t.ToDisc())) ? design : null, false, "X", chapterMinutes, enc, output.Usable),
            titles.Select(t => t.OnDisc).ToList());

    // ---- videos ----

    void OnOpen(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Vidéos, photos et musique|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob;*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff;*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.ac3;*.mka"
                     + "|Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv;*.webm;*.mpg;*.mpeg;*.vob|Photos (diaporama)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|Musique|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.ac3;*.mka|Tous les fichiers|*.*",
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
        try
        {
            if (photos.Count > 0)
            {
                var answer = Prompt.Ask(this, photos.Count == 1 ? "Diaporama d'une photo : secondes" : $"Diaporama de {photos.Count} photos : secondes par photo", "5");
                if (answer != null && double.TryParse(answer.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var each) && each > 0)
                {
                    var file = NextMade("Diaporama" + (madeNames.Values.Count(n => n.StartsWith("Diaporama")) is var k and > 0 ? $" {k + 1}" : ""));
                    var music = songs.ToList();
                    songs.Clear(); // the songs of a slideshow are its music, not titles
                    StatusText.Text = "Préparation du diaporama…";
                    await Task.Run(() => MediaMaker.Slideshow(photos, each, music, file, Say));
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
        return videos;
    }

    /// <summary>Probes and adds videos; several at once are put in natural order (episode 2 before episode 10).</summary>
    async Task AddFiles(IEnumerable<string> files)
    {
        var list = files.Where(File.Exists).ToList();
        NaturalOrder(list);
        list = await MakeVideos(list);
        foreach (var path in list)
        {
            try
            {
                var entry = new Entry(path, await Task.Run(() => BluRay.Probe(path))) { Extra = ExternalSubtitle.Near(path) };
                if (madeNames.TryGetValue(path, out var shown)) entry.Name = shown;
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

    static BitmapImage? LoadImage(string? file)
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
            var name = MainEntry!.Name;
            if (!labelEdited) volume = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9_]+", "_").Trim('_') is { Length: > 0 } l ? l[..Math.Min(32, l.Length)] : "MON_DISQUE";
            if (design.Title == new MenuDesign().Title) design.Title = name;
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
    static string? ExtractPreview(string path, double seconds)
    {
        var file = Path.Combine(Path.GetTempPath(), "BurnSmith", "preview.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-frames:v", "1", "-q:v", "2", "-vf", "scale=800:450:force_original_aspect_ratio=increase,crop=800:450", file]);
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

    /// <summary>The "..." button: move the title up or down (the order is the order of the episodes in the menu).</summary>
    void OnRowMore(object s, RoutedEventArgs e)
    {
        var entry = Row(s);
        // FontFamily: a menu inherits the font of its target, which here is the icon button (the text would show as empty boxes)
        var menu = new ContextMenu { PlacementTarget = (UIElement)s, Placement = PlacementMode.Bottom, FontFamily = FontFamily };
        var up = new MenuItem { Header = "Monter", IsEnabled = titles.IndexOf(entry!) > 0 };
        var down = new MenuItem { Header = "Descendre", IsEnabled = titles.IndexOf(entry!) < titles.Count - 1 };
        up.Click += (_, _) => Move(entry, -1);
        down.Click += (_, _) => Move(entry, 1);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
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
        menu.Items.Add(new Separator());
        var split = new MenuItem { Header = "Couper en deux titres…" };
        var join = new MenuItem { Header = "Fusionner avec la vidéo suivante", IsEnabled = titles.IndexOf(entry) < titles.Count - 1 };
        split.IsEnabled = !entry.Intro;
        split.Click += (_, _) => SplitEntry(entry);
        join.Click += (_, _) => _ = JoinWithNext(entry);
        menu.Items.Add(split);
        menu.Items.Add(join);
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
        try
        {
            var file = NextMade(entry.Name);
            await Task.Run(() => MediaMaker.Join([entry.Path, next.Path], file, Say));
            var info = await Task.Run(() => BluRay.Probe(file));
            var joined = new Entry(file, info)
            {
                Name = entry.Name, Audio = [.. entry.Audio.Where(a => a < info.Audio.Count)], Subs = [.. entry.Subs.Where(s => s < info.Subtitles.Count)],
                Volume = entry.Volume, Bonus = entry.Bonus, Intro = entry.Intro, DefaultAudio = entry.DefaultAudio, DefaultSub = entry.DefaultSub, TrackLanguage = new(entry.TrackLanguage), // the join keeps both videos' (equal) streams
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

    void Move(Entry? entry, int step)
    {
        if (entry == null) return;
        var i = titles.IndexOf(entry);
        if (i + step < 0 || i + step >= titles.Count) return;
        titles.Move(i, i + step);
        RefreshThumb(); // episode order changes the menu
    }

    /// <summary>The pencil: the name shown in the menu, the part kept, the volume and the chapters of this video.</summary>
    async void OnRowEdit(object s, RoutedEventArgs e)
    {
        if (Row(s) is not { } entry) return;
        var dlg = new VideoEditWindow(entry.Name, entry.Info.Duration, entry.Start, entry.End, entry.Volume, entry.ChapterAt, entry.ChapterMinutes, chapterMinutes) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        (entry.Name, entry.Start, entry.End, entry.Volume, entry.ChapterAt, entry.ChapterMinutes) = (dlg.NameText, dlg.Start, dlg.End, dlg.Volume, dlg.ChapterAt, dlg.ChapterMinutes);
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
            Title = $"Sous-titres de {entry.Name}", Multiselect = true, InitialDirectory = Path.GetDirectoryName(entry.Path),
            Filter = "Sous-titres (*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sup;*.idx;*.sub|Tous les fichiers|*.*",
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
            var max = audio ? MenuBuilder.MaxAudio : MenuBuilder.MaxSubtitles;
            if (kept.Count >= max)
            {
                item.IsChecked = false;
                Warn($"Au plus {MenuBuilder.MaxAudio} pistes audio et {MenuBuilder.MaxSubtitles} sous-titres (limite de la page Réglages du menu).");
                return;
            }
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
        var editor = new MenuEditorWindow(design, previewFrame, EditorContent()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        design = editor.Result;
        MenuName.Text = design.Name;
        ProjectTitle.Text = titles.Count == 0 ? "Aucune source" : design.Title;
        RefreshThumb();
    }

    void OnAdvanced(object s, RoutedEventArgs e)
    {
        var dlg = new AdvancedWindow(volume, chapterMinutes, encode, playback, kind, Output.For(kind), (Output)CapacityBox.SelectedItem, Estimate) { Owner = this };
        if (dlg.ShowDialog() != true) return;
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
        var dlg = new OpenFolderDialog { Title = "Dossier du disque" };
        if (dlg.ShowDialog(this) != true) return;
        OutBox.Text = dlg.FolderName;
        outEdited = true;
    }

    void OnCancel(object s, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Annulation…";
        cancel?.Cancel();
    }

    /// <summary>
    /// Runs one long job off the UI thread with its progress bar (indeterminate until the job reports a value) and a log line per step.
    /// Returns false when it failed or was cancelled.
    /// </summary>
    async Task<bool> Work(string what, Action<Action<string>, IProgress<double>, CancellationToken> job)
    {
        busy = true;
        StartButton.IsEnabled = BurnButton.IsEnabled = EraseButton.IsEnabled = false;
        Busy.IsIndeterminate = true;
        StatusText.Text = what;
        ProgressPanel.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
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
            busy = false;
            StartButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            cancel.Dispose();
            cancel = null;
            RefreshDrives();
        }
    }

    async void OnStart(object s, RoutedEventArgs e)
    {
        if (titles.Count == 0) { MessageBox.Show(this, "Ajoutez d'abord une vidéo.", "BurnSmith"); return; }
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0) { MessageBox.Show(this, "Choisissez un dossier de sortie.", "BurnSmith"); return; }
        var cap = (Output)CapacityBox.SelectedItem;
        var enc = encode with { Hd720 = cap.Hd720 && kind == "bd" };
        var play = playback;
        var label = volume;
        var drive = DriveBox.SelectedItem as Imapi.Drive;
        var burn = BurnCheck.IsChecked == true && drive != null;
        if (burn && MessageBox.Show(this,
                $"Après la création, graver « {label} » sur {drive!.Letter} ?\n\nInsérez un disque vierge adapté ({cap.Name}). Le disque sera écrit définitivement.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var discTitles = titles.Select(t => t.ToDisc()).ToList();
        var menu = MenuCheck.IsChecked == true || NeedsMenu(discTitles) ? design.Clone() : null;
        var minutes = chapterMinutes;
        var uhd = kind == "uhd";
        var dvd = kind == "dvd";
        var withIso = IsoCheck.IsChecked == true;
        Log.Clear();

        var ok = await Work("Création du disque…", (log, progress, token) =>
        {
            if (dvd) Dvd.Build(new DvdJob(discTitles, outDir, menu, minutes, cap.Usable, enc), log);
            else BluRay.Build(new BluRayJob(discTitles, outDir, menu, uhd, label.Length > 0 ? label : "BURNSMITH", minutes, enc, cap.Usable, play), log);
            var iso = IsoFile(outDir);
            File.Delete(iso); // an image of an earlier build must never be burnt by mistake
            var problems = DiscCheck.Folder(outDir);
            log(problems.Count == 0 ? "Vérification de la structure du disque : aucune anomalie." : "Vérification de la structure du disque : " + string.Join(" ; ", problems));
            if (burn && problems.Count > 0) throw new InvalidOperationException("Le disque n'est pas gravé, sa structure a des anomalies : " + string.Join(" ; ", problems));
            if (!withIso && !burn) return;
            token.ThrowIfCancellationRequested();
            log("Création de l'image ISO…");
            Iso.Build(outDir, iso, label, dvd, log, progress);
            var imageProblems = DiscCheck.Image(iso, outDir);
            log(imageProblems.Count == 0 ? "Vérification de l'image ISO : aucune anomalie." : "Vérification de l'image ISO : " + string.Join(" ; ", imageProblems));
            if (burn && imageProblems.Count > 0) throw new InvalidOperationException("Le disque n'est pas gravé, l'image ISO a des anomalies : " + string.Join(" ; ", imageProblems));
            if (burn)
            {
                token.ThrowIfCancellationRequested();
                Iso.Burn(iso, drive!.Letter, log, progress, token);
            }
            if (!withIso) File.Delete(iso); // only made to burn
        });
        if (ok) Append("Terminé.");
    }

    // not "<folder>.iso": that name could belong to the user's own file
    static string IsoFile(string folder) => Path.TrimEndingDirectorySeparator(folder) + "_BurnSmith.iso";

    /// <summary>Burns an image that already exists (made earlier, or downloaded) on a DVD / Blu-ray / Blu-ray 4K.</summary>
    async void OnBurnIso(object s, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not Imapi.Drive drive) { MessageBox.Show(this, "Aucun graveur détecté.", "BurnSmith"); return; }
        var dlg = new OpenFileDialog { Title = "Image à graver", Filter = "Images disque|*.iso|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var iso = dlg.FileName;
        var answer = MessageBox.Show(this,
            $"Graver « {Path.GetFileName(iso)} » sur {drive.Letter} ?\n\nInsérez un disque vierge adapté à la taille de l'image. Le disque sera écrit définitivement.",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (await Work("Gravure…", (log, progress, token) => Iso.Burn(iso, drive.Letter, log, progress, token))) Append("Terminé.");
    }

    async void OnErase(object s, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not Imapi.Drive drive) { MessageBox.Show(this, "Aucun graveur détecté.", "BurnSmith"); return; }
        if (MessageBox.Show(this, $"Effacer le disque réinscriptible de {drive.Letter} ?\n\nToutes les données du disque seront perdues.",
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (await Work("Effacement…", (log, _, _) => Imapi.Erase(drive.Letter, log))) Append("Terminé.");
    }
}
