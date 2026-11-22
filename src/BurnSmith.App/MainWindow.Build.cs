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
    // ---- tracks ----

    void OnAudioMenu(object s, RoutedEventArgs e) => TrackMenu((Button)s, true);
    void OnSubMenu(object s, RoutedEventArgs e) => TrackMenu((Button)s, false);

    /// <summary>The dropdown of a row: one checkable line per track of the source, ticking one updates just that video.</summary>
    void TrackMenu(Button button, bool audio)
    {
        if (Row(button) is not { } entry) return;
        var kept = audio ? entry.Audio : entry.Subs;
        IEnumerable<(int Index, string Text, bool Enabled)> tracks = audio
            ? entry.Tracks.Audio.Select(a => (a.Index, $"{a.Name}  ({(a.File != null ? Path.GetFileName(a.File) : a.Codec)})", true))
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
        if (audio)
        {
            menu.Items.Add(new Separator());
            var addAudio = new MenuItem { Header = "Ajouter une piste audio…" };
            addAudio.Click += (_, _) => AddAudioFiles(entry);
            menu.Items.Add(addAudio);
        }
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
            if (audio) menu.Items.Add(VolumeItem(entry, index, name));
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

    /// <summary>« Ajouter une piste audio… »: sound files (a dub found apart) kept as more audio tracks of the video, set like its own (language, delay, volume).</summary>
    void AddAudioFiles(Entry entry)
    {
        var patterns = string.Join(';', AudioTrack.Extensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = Loc.T($"Pistes audio de {entry.Name}"), Multiselect = true, InitialDirectory = Path.GetDirectoryName(entry.Path),
            Filter = Loc.Filter($"Pistes audio ({patterns})|{patterns}|Tous les fichiers|*.*"),
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (entry.AudioFiles.Any(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase))) continue;
            try { entry.AudioFiles.Add(AudioTrack.FromFile(entry.Path, file)); }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { Warn($"{Path.GetFileName(file)} : {e.Message}"); continue; }
            entry.Audio.Add(entry.Info.Audio.Count + entry.AudioFiles.Count - 1);
        }
        entry.Refresh();
        Update();
    }

    /// <summary>« Volume de « … » »: dB louder or quieter for one kept audio track; a track copied as it is is then made again.</summary>
    MenuItem VolumeItem(Entry entry, int index, string name)
    {
        var db = entry.AudioVolume.GetValueOrDefault(index);
        var item = new MenuItem { Header = $"Volume de « {name} »" + (db != 0 ? $" : {db.ToString("+0.#;-0.#", CultureInfo.CurrentCulture)} dB…" : "…") };
        item.Click += (_, _) =>
        {
            const string what = "Volume de la piste";
            if (Prompt.Ask(this, "Volume de la piste, en dB (de -30 à 30) : 6 = plus fort, -6 = moins fort, 0 = inchangé. Une piste copiée telle quelle (AC-3, DTS) est alors réencodée.", db.ToString("0.#", CultureInfo.CurrentCulture)) is not { } text) return;
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > 30)
            {
                MessageBox.Show(this, "Entrez un nombre de dB entre -30 et 30, par exemple 3 ou -4,5.", what, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (value == 0) entry.AudioVolume.Remove(index); else entry.AudioVolume[index] = value;
        };
        return item;
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
            other.Audio = cur.Audio.Where(i => other.Tracks.Audio.Any(a => a.Index == i)).ToList(); // Tracks: an added sound file of that video counts too
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
        if (editor.Result.Name != design.Name) // a template picked: the next disc starts from it (TemplateCatalog.DefaultFor)
            try { TemplateCatalog.RememberChoice(editor.Result.Name); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppFiles.LogError("Dernier modèle choisi", ex); }
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
        RemainingText.Text = "";
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        // the time left from the pace so far; a step that starts again from 0 (the ISO image after the videos, the burn) starts its own clock
        var clock = new Stopwatch();
        var last = 0.0;
        var progress = new Progress<double>(v =>
        {
            Busy.IsIndeterminate = false;
            Busy.Value = v * 100;
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            TaskbarItemInfo.ProgressValue = v;
            if (!clock.IsRunning || v < last - 0.05) clock.Restart();
            last = v;
            RemainingText.Text = Remaining(clock.Elapsed, v);
        });
        void Log(string line) => Dispatcher.BeginInvoke(() => { Append(line); StatusText.Text = line; });
        var ended = "failed";
        try
        {
            await Task.Run(() => job(Log, progress, token));
            ended = "done";
            return true;
        }
        catch (OperationCanceledException) { Warn("Annulé."); ended = "cancelled"; return false; }
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
            TaskbarItemInfo.ProgressState = ended == "failed" ? System.Windows.Shell.TaskbarItemProgressState.Error : System.Windows.Shell.TaskbarItemProgressState.None;
            cancel.Dispose();
            cancel = null;
            RefreshDrives();
            var step = what.TrimEnd('…');
            if (ended != "cancelled") Notify.Ended(this, "BurnSmith", Loc.T(ended == "done" ? $"Terminé : {step}" : $"Arrêté sur une erreur : {step}"), ended == "failed");
            if (ended != "cancelled" && ShutdownCheck.IsChecked == true) ShutDown();
            if (closeAfterWork) Close();
        }
    }

    /// <summary>"encore 42 min" once the pace is known (a few seconds and 1 % in), nothing when the step is at its end.</summary>
    static string Remaining(TimeSpan elapsed, double done)
    {
        if (done < 0.01 || done >= 0.999 || elapsed.TotalSeconds < 10) return "";
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - done) / done);
        return Loc.T(left.TotalMinutes < 1 ? "encore moins d'une minute" : left.TotalHours < 1 ? $"encore {(int)Math.Ceiling(left.TotalMinutes)} min" : $"encore {(int)left.TotalHours} h {left.Minutes:00}");
    }

    /// <summary>« Éteindre le PC à la fin »: a minute to change one's mind, then Windows shuts down.</summary>
    void ShutDown()
    {
        try { Tools.ShutdownSoon(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Warn("Le PC n'a pas pu être éteint : " + ex.Message); return; }
        Append("Le PC s'éteint dans une minute.");
        if (MessageBox.Show(this, "Le travail est fini : le PC s'éteint dans une minute.\n\nAnnuler l'extinction ?", "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            Tools.CancelShutdown();
            Append("Extinction annulée.");
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
        var (verify, eject) = (VerifyCheck.IsChecked, EjectCheck.IsChecked);
        if (burn && cap.Unlimited) { MessageBox.Show(this, "« Sans limite » ne se grave pas : choisissez la taille du disque à graver, ou décochez la gravure.", "BurnSmith"); return; }
        if (burn && !await DiscBigEnough(drive!, cap.Usable, "le projet")) return;
        // what is known to fail on players, said before the job starts (the PS5 note: with the burn of a Blu-ray, and in the log)
        if (Project.Warning(kind, cap.Usable) is { } warning && MessageBox.Show(this, warning, "BurnSmith", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (burn && MessageBox.Show(this,
                Loc.T($"Après la création, graver « {label} » sur {drive!.Letter} ?\n\nInsérez un disque vierge adapté ({cap.Name}). Le disque sera écrit définitivement.")
                + "\n\n" + Loc.T(Recap.MediaAdvice(kind == "dvd", cap.Gb))
                + (kind == "dvd" ? "" : "\n\n" + Loc.T(Project.Ps5Note)),
                "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var minutes = chapterMinutes;
        // a trial disc: a few minutes of each video, in a folder of its own, so the real disc's folder is never replaced by it
        var trial = TrialCheck.IsChecked == true ? TrialSeconds : 0;
        if (trial > 0) outDir = Path.TrimEndingDirectorySeparator(outDir) + "_essai";
        var infos = titles.GroupBy(t => t.Path).ToDictionary(g => g.Key, g => g.First().Info);
        var discTitles = Project.OnDisc(titles.Select(t => t.ToDisc()).ToList(), minutes, trial, path => infos[path]);
        if (CompatCheck.IsChecked == true) { (discTitles, enc, hdmv) = Project.Compatible(discTitles, enc, path => infos[path]); speed = Iso.Speeds[^1]; } // « Compatibilité maximale »
        var menu = MenuCheck.IsChecked == true || NeedsMenu(discTitles) ? design.Clone() : null;
        var look = subtitleStyle;
        var uhd = kind == "uhd";
        var dvd = kind == "dvd";
        var withIso = IsoCheck.IsChecked == true;
        // « À savoir avant de démarrer »: what the build is about to do, from the build's own decisions (Core.Recap); it heads the log, so « Copier le rapport » carries it
        List<string> recap;
        try
        {
            recap = dvd ? Recap.For(new DvdJob(discTitles, outDir, menu, minutes, cap.Usable, enc, play, look), path => infos[path])
                        : Recap.For(new BluRayJob(discTitles, outDir, menu, uhd, "X", minutes, enc, cap.Usable, play, look, hdmv, cap.Avchd), path => infos[path]);
        }
        catch (Exception) { recap = []; } // only advice: the build itself says what is wrong
        if (recap.Count > 0 && MessageBox.Show(this, Loc.T("À savoir avant de démarrer :") + "\n\n" + string.Join("\n", recap.Select(Loc.T)), "BurnSmith",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Log.Clear();
        PlayDiscButton.Visibility = Visibility.Collapsed; // the disc it opened may be the one this build replaces
        NavigationButton.Visibility = Visibility.Collapsed;
        if (recap.Count > 0) foreach (var line in (string[])["À savoir avant de démarrer :", .. recap, ""]) Append(line);
        if (!dvd) Append(Project.Ps5Note);

        var verified = true;
        var ok = await Work("Création du disque…", (log, progress, token) =>
        {
            if (trial > 0) log($"Disque d'essai : {trial / 60} minutes de chaque vidéo, un passage à chaque chapitre, dans {outDir}");
            if (dvd) Dvd.Build(new DvdJob(discTitles, outDir, menu, minutes, cap.Usable, enc, play, look), log, token, progress);
            else BluRay.Build(new BluRayJob(discTitles, outDir, menu, uhd, label.Length > 0 ? label : "BURNSMITH", minutes, enc, cap.Usable, play, look, hdmv, cap.Avchd), log, token, progress);
            var iso = IsoFile(outDir);
            File.Delete(iso); // an image of an earlier build must never be burnt by mistake
            var checkDir = !dvd && cap.Avchd ? Path.Combine(outDir, "AVCHD") : outDir;
            var problems = DiscCheck.Folder(checkDir, cap.Gb);
            if (problems.Count > 0) throw new InvalidOperationException("Vérification de la structure du disque : " + string.Join(" ; ", problems));
            log("Vérification de la structure du disque : aucune anomalie.");
            if (!withIso && !burn) return;
            token.ThrowIfCancellationRequested();
            log("Création de l'image ISO…");
            Iso.Build(outDir, iso, label, dvd, log, progress, token);
            var imageProblems = DiscCheck.Image(iso, outDir, cap.Gb);
            if (imageProblems.Count > 0) throw new InvalidOperationException("Vérification de l'image ISO : " + string.Join(" ; ", imageProblems));
            log("Vérification de l'image ISO : aucune anomalie.");
            if (burn)
            {
                writing = true;
                token.ThrowIfCancellationRequested();
                verified = Iso.Burn(iso, drive!.Letter, log, progress, token, speed, verify, eject, EraseFirst(drive.Letter));
            }
            if (!withIso && verified) File.Delete(iso); // keep the image if readback did not finish
        });
        if (ok && !verified) Warn(NotVerified);
        else if (ok) Append(trial == 0 ? "Terminé." : $"Disque d'essai terminé. Ouvrez {(withIso ? IsoFile(outDir) : outDir)} dans VLC (Média > Ouvrir un disque) pour vérifier le menu, puis décochez « Essai » pour le vrai disque.");
        if (ok)
        {
            ShowPlayDisc(withIso || !verified ? IsoFile(outDir) : outDir);
            NavigationButton.Visibility = Directory.Exists(Path.Combine(outDir, "BDMV", "JAR")) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- « Lire sur l'ordinateur » ----

    string? madeDisc; // the last disc made: its ISO when there is one, else its folder

    void ShowPlayDisc(string disc)
    {
        madeDisc = disc;
        PlayDiscButton.Visibility = Visibility.Visible;
    }

    /// <summary>For the snapshot run (<c>playdisc</c>): the button as it shows after a disc was made.</summary>
    internal void TryPlayDisc() => ShowPlayDisc(OutBox.Text);

    async void OnDiscNavigation(object s, RoutedEventArgs e)
    {
        if (madeDisc is not { } disc) return;
        NavigationButton.IsEnabled = false;
        try
        {
            var (trace, problems) = await Task.Run(() => DiscCheck.Navigation(disc));
            foreach (var line in trace) Append(line);
            foreach (var problem in problems) Append(problem);
        }
        catch (Exception ex) { Warn("Impossible de lire la navigation : " + ex.Message); }
        finally { NavigationButton.IsEnabled = true; }
    }

    void OnPlayDisc(object s, RoutedEventArgs e)
    {
        if (madeDisc == null) return;
        try
        {
            if (!Vlc.Play(madeDisc))
            {
                if (MessageBox.Show(this, "VLC n'est pas installé sur cet ordinateur. C'est un lecteur gratuit qui lit les dossiers et les images ISO de Blu-ray et de DVD.\n\nOuvrir videolan.org pour le télécharger ?",
                        "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(Vlc.Site) { UseShellExecute = true });
                return;
            }
            Append($"Ouvert dans VLC : {madeDisc}");
            if (!Vlc.Arguments(madeDisc).StartsWith("\"dvd:")) Append(Vlc.JavaNote); // a Blu-ray, or an ISO that may be one
        }
        catch (Exception ex) { Warn("Impossible d'ouvrir le disque dans VLC : " + ex.Message); }
    }

    static string IsoFile(string folder) => Project.IsoFile(folder);

    const string NotVerified = "Attention : disque écrit mais pas contrôlé. ImgBurn n'a pas relu le disque (tiroir resté ouvert ?) : gardez l'image ISO pour le contrôler ou le regraver.";

    // ---- queue ----

    /// <summary>« File d'attente »: discs made one after the other, persisted across restarts in queue.json.</summary>
    readonly List<(string Name, Project Disc)> queue = [];
    sealed record QueueEntry(string Name, Project Disc);
    static string QueueFile() => Path.Combine(AppFiles.Root(), "queue.json");

    void SaveQueue()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(queue.Select(q => new QueueEntry(q.Name, q.Disc)).ToList());
            Project.WriteWhole(QueueFile(), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void LoadQueue()
    {
        try
        {
            if (!File.Exists(QueueFile())) return;
            var items = System.Text.Json.JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(QueueFile()));
            if (items == null) return;
            queue.Clear();
            foreach (var item in items)
                if (!string.IsNullOrWhiteSpace(item.Name) && item.Disc != null) queue.Add((item.Name, item.Disc));
            QueueCount.Text = queue.Count > 0 ? queue.Count.ToString() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    /// <summary>For the snapshot run: the disc of the window put in the queue.</summary>
    public void TryQueue() => QueueCurrent();

    void ShowQueue()
    {
        QueueCount.Text = queue.Count > 0 ? queue.Count.ToString() : "";
        SaveQueue();
    }

    void OnQueue(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)s, Placement = PlacementMode.Bottom };
        var current = new MenuItem { Header = "Ajouter ce disque à la file", IsEnabled = titles.Count > 0 };
        current.Click += (_, _) => QueueCurrent();
        menu.Items.Add(current);
        var season = new MenuItem { Header = "Répartir la saison sur plusieurs disques…", IsEnabled = !busy && titles.Count(t => !t.Intro) > 1 };
        season.Click += (_, _) => QueueVolumes();
        menu.Items.Add(season);
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

    void OnSplitSeason(object s, RoutedEventArgs e)
    {
        if (!busy && titles.Count(t => !t.Intro) > 1) QueueVolumes();
    }

    void QueueVolumes()
    {
        if (StillLoadingDesign() || OutBox.Text.Trim().Length == 0)
        {
            if (OutBox.Text.Trim().Length == 0) Warn("Choisissez un dossier de sortie.");
            return;
        }
        if (titles.Count(t => !t.Intro) < 2) return;
        var cap = CapacityBox.SelectedItem as Output;
        var project = Project.Parse(Snapshot().ToJson()) with
        {
            Out = OutBox.Text.Trim(),
            Label = volume,
            Encode = encode with { Hd720 = (cap?.Hd720 ?? false) && kind == "bd" }
        };
        var probedItems = titles.Select(t => (t.ToDisc(), t.OnDisc)).ToList();
        var usableGb = cap is { Unlimited: false } ? cap.Usable : 0;
        var dlg = new SeasonSplitWindow(project, probedItems, usableGb) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.DiscGroups is not { } groups) return;
        try
        {
            foreach (var (disc, _) in project.Volumes(null, groups, probedItems))
                Queue(Path.GetFileName(disc.Out!), disc);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { Warn(ex.Message); }
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
        if (!await DiscBigEnough(drive, new FileInfo(iso).Length / 1e9, "l'image")) return;
        var answer = MessageBox.Show(this,
            $"Graver « {Path.GetFileName(iso)} » sur {drive.Letter} ?\n\nInsérez un disque vierge adapté à la taille de l'image. Le disque sera écrit définitivement.",
            "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        writing = true;
        var speed = Iso.Speeds[CompatCheck.IsChecked == true ? ^1 : Math.Max(0, SpeedBox.SelectedIndex)]; // « Compatibilité maximale »: the slowest
        var (verify, eject) = (VerifyCheck.IsChecked, EjectCheck.IsChecked);
        var verified = true;
        if (await Work("Gravure…", (log, progress, token) => verified = Iso.Burn(iso, drive.Letter, log, progress, token, speed, verify, eject, EraseFirst(drive.Letter))))
        {
            if (verified) Append("Terminé.");
            else Warn(NotVerified);
        }
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
