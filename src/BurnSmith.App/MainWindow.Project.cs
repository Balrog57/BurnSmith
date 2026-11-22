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

public partial class MainWindow
{
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
                              (CapacityBox.SelectedItem as Output)?.Name, (CapacityBox.SelectedItem as Output)?.Usable ?? 0, outEdited ? OutBox.Text : null, IsoCheck.IsChecked == true, TrialCheck.IsChecked == true ? TrialSeconds : 0, hdmvMenu,
                              MaxCompat: CompatCheck.IsChecked == true, Avchd: (CapacityBox.SelectedItem as Output)?.Avchd == true);

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
        CompatCheck.IsChecked = p.MaxCompat;
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
        SaveSoon();
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
        SaveSoon();
    }

    // ---- the project kept by itself (Project.AutosaveFile): reopened at start, written two seconds after the last change of a burst ----

    readonly System.Windows.Threading.DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    bool autosaving; // off until the former project is back (and in a snapshot run): the empty window must not replace it

    void SaveSoon()
    {
        if (!autosaving) return;
        autosave.Stop();
        autosave.Start();
    }

    void WriteAutosave()
    {
        autosave.Stop();
        if (busy || loading > 0 || kept == null) { autosave.Start(); return; } // a disc being made or a project being read: once it is over
        try { Project.WriteWhole(Project.AutosaveFile(), kept); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppFiles.LogError("Enregistrement automatique du projet", e); }
    }

    /// <summary>At start: the project as the app left it, without a word unless a video is gone (it is left out, the log says so). Then each change is kept.</summary>
    public async Task RestoreAutosave()
    {
        await defaultDesign;
        if (File.Exists(Project.AutosaveFile())) await OpenProject(Project.AutosaveFile(), autosaved: true);
        Remember();
        undo.Clear(); // Annuler starts from the project reopened, not from the empty window
        (UndoButton.IsEnabled, RedoButton.IsEnabled) = (false, false);
        autosave.Tick += (_, _) => WriteAutosave();
        Closed += (_, _) => { Remember(); if (autosave.IsEnabled) WriteAutosave(); }; // the last change, made less than two seconds before closing
        autosaving = true;
    }

    /// <summary>« Nouveau projet » : no video, the usual disc settings, the menu template kept with its sample title. Annuler brings the former project back.</summary>
    void OnNewProject(object s, RoutedEventArgs e)
    {
        if (busy || loading > 0) return;
        Remember();
        var fresh = design.Clone();
        fresh.Title = fresh.ButtonLanguage == "en" ? "My Disc" : "Mon disque";
        Apply(new Project(Design: fresh));
        projectFile = null; // Ctrl+S asks where: the file opened before is not this project
        Title = "BurnSmith";
        Remember();
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
    public async Task OpenProject(string file, bool autosaved = false)
    {
        if (busy) return;
        Project p;
        try { p = Project.Load(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (autosaved) { AppFiles.LogError("Projet repris au démarrage", e); return; } // nothing to ask at start: the window starts empty
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
            if (autosaved) return; // the kept project is no file of the user's: Ctrl+S asks where
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
                var discName = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(f));
                StatusText.Text = "Ouverture de " + Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) + "…";
                var found = await Task.Run(() => BluRay.ListDiscTitles(f));
                if (found.Count > 1)
                {
                    var picker = new DiscImportWindow(discName, found) { Owner = this };
                    if (picker.ShowDialog() != true) continue;
                    foreach (var chosen in picker.Chosen)
                    {
                        StatusText.Text = "Extraction de " + chosen.Name + "…";
                        var clip = await Task.Run(() => BluRay.ExtractDiscTitle(chosen));
                        discNames[clip] = chosen.Name;
                        list.Add(clip);
                    }
                }
                else if (found.Count == 1)
                {
                    var clip = await Task.Run(() => BluRay.ExtractDiscTitle(found[0]));
                    discNames[clip] = found[0].Name;
                    list.Add(clip);
                }
                else
                {
                    var clip = await Task.Run(() => BluRay.MainClip(f));
                    discNames[clip] = discName;
                    list.Add(clip);
                }
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
        if (ReferenceEquals(design, autoDesign) && defaultDesign.IsCompleted && TemplateCatalog.Suggested(MainVideos()) != autoFor) defaultDesign = LoadDefaultDesign();
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
        if (kind != "dvd" && !hdmvMenu && CompatCheck.IsChecked != true && !entry.Intro)
        {
            var ending = new MenuItem { Header = "À la fin du titre (BD-J)" };
            foreach (var (action, label) in new (string? Action, string Label)[]
            {
                (null, "Réglage du disque"), ("menu", "Retour au menu"), ("next", "Titre suivant"),
                ("repeat", "Relire ce titre"), ("stop", "Arrêter la lecture")
            })
            {
                var choice = new MenuItem { Header = label, IsCheckable = true, IsChecked = entry.EndAction == action, IsEnabled = action != "next" || !entry.Bonus };
                choice.Click += (_, _) => { entry.EndAction = action; Remember(); };
                ending.Items.Add(choice);
            }
            menu.Items.Add(ending);
        }
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
        // SplitEntry / JoinWithNext are kept but out of the menu for now.
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

}
