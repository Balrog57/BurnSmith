using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BurnSmith.Core;
using Microsoft.Win32;

namespace BurnSmith.App;

public partial class VideoEditWindow
{
    // ---- Chapitres & clips ----

    /// <summary>Why the clips do not make a disc (null when they do): each lasts a second or more, inside the file, after the one before.</summary>
    string? ClipError()
    {
        for (var k = 0; k < clips.Count; k++)
        {
            var c = clips[k];
            if (c.From < 0 || c.To > duration + 0.05) return $"Clip {k + 1} : ses temps doivent être dans la vidéo (0:00:00 à {Timecode.Format(duration)}).";
            if (c.To - c.From < 1) return $"Clip {k + 1} : il doit durer au moins une seconde.";
            if (k > 0 && c.From < clips[k - 1].To - 0.01) return $"Clip {k + 1} : il doit commencer après la fin du clip {k}.";
        }
        return null;
    }

    /// <summary>One block per clip as DVDFab shows them: its start and end (typed, or taken from the slider), its length, play and delete.</summary>
    void ShowClips()
    {
        ClipPanel.Children.Clear();
        for (var k = 0; k < clips.Count; k++)
        {
            var i = k;
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new DockPanel();
            var delete = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = "Supprimer ce clip (sa partie est enlevée du disque)", IsEnabled = clips.Count > 1 };
            delete.Click += (_, _) => { clips.RemoveAt(i); ShowClips(); };
            var play = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = "Lire ce clip" };
            play.Click += (_, _) => { Stop(); SeekSlider.Value = clips[i].From; _ = Play(clips[i].To); };
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(play, Dock.Right);
            head.Children.Add(delete);
            head.Children.Add(play);
            head.Children.Add(new TextBlock { Text = $"Clip {k + 1}  ·  durée {Timecode.Format(Math.Max(0, clips[k].To - clips[k].From))}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            block.Children.Add(head);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var isStart in new[] { true, false })
            {
                line.Children.Add(new TextBlock { Text = isStart ? "Heure de début :" : "Heure de fin :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(isStart ? 0 : 16, 0, 6, 0) });
                var box = new TextBox { Style = (Style)FindResource("Time"), Text = Timecode.Format(isStart ? clips[k].From : clips[k].To) };
                var first = isStart;
                void Typed()
                {
                    if (Timecode.Parse(box.Text) is { } t) clips[i] = first ? clips[i] with { From = Math.Round(t, 2) } : clips[i] with { To = Math.Round(t, 2) };
                    ShowClips();
                }
                box.LostFocus += (_, _) => { if (box.Text != Timecode.Format(first ? clips[i].From : clips[i].To)) Typed(); };
                box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Typed(); e.Handled = true; } };
                var here = new Button { Style = (Style)FindResource("IconBtn"), Content = "", ToolTip = isStart ? "Début ici (à la position de l'aperçu)" : "Fin ici (à la position de l'aperçu)" };
                here.Click += (_, _) =>
                {
                    var t = Math.Round(SeekSlider.Value, 2);
                    clips[i] = first ? clips[i] with { From = t } : clips[i] with { To = t };
                    ShowClips();
                };
                line.Children.Add(box);
                line.Children.Add(here);
            }
            block.Children.Add(line);
            ClipPanel.Children.Add(block);
        }
        var error = ClipError();
        var kept = clips.Sum(c => Math.Max(0, c.To - c.From));
        KeptText.Text = error ?? $"Durée sur le disque : {Timecode.Format(kept)}" + (clips.Count > 1 ? $" ({clips.Count} clips, {Timecode.Format(Math.Max(0, clips[^1].To - clips[0].From - kept))} enlevés entre eux)" : "");
        if (error == null) KeptText.ClearValue(TextBlock.ForegroundProperty);
        else KeptText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        ShowMarks();
    }

    /// <summary>"Couper ici": the clip under the slider becomes two that meet there.</summary>
    void OnSplitClip(object s, RoutedEventArgs e)
    {
        var t = Math.Round(SeekSlider.Value, 2);
        var k = clips.FindIndex(c => t > c.From + 1 && t < c.To - 1);
        if (k < 0)
        {
            MessageBox.Show(this, "Placez l'aperçu dans un clip, à plus d'une seconde de ses bords.", "Couper ici", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        clips.Insert(k + 1, clips[k] with { From = t });
        clips[k] = clips[k] with { To = t };
        ShowClips();
    }

    /// <summary>A clip of a minute (less at the end of the file) where the slider is, outside the clips already there.</summary>
    void OnAddClip(object s, RoutedEventArgs e)
    {
        var from = Math.Round(SeekSlider.Value, 2);
        var to = Math.Min(duration, from + 60);
        if (clips.FirstOrDefault(c => c.From > from) is { } next) to = Math.Min(to, next.From);
        if (to - from < 1 || clips.Any(c => from >= c.From && from < c.To))
        {
            MessageBox.Show(this, "Placez l'aperçu en dehors des clips déjà là, devant au moins une seconde libre.", "Ajouter un clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        clips.Add(new Clip(from, to));
        clips.Sort((a, b) => a.From.CompareTo(b.From));
        ShowClips();
    }

    void OnClipsDefault(object s, RoutedEventArgs e)
    {
        clips.Clear();
        clips.Add(new Clip(0, duration));
        ShowClips();
    }

    /// <summary>The clips (coloured) and the chapters of the list (white) under the slider.</summary>
    void ShowMarks()
    {
        Marks.Children.Clear();
        var w = Marks.ActualWidth;
        if (w < 1 || duration <= 0) return;
        Marks.Children.Add(new Rectangle { Width = w, Height = 4, Fill = (Brush)FindResource("LineBrush") });
        foreach (var c in clips)
        {
            var bar = new Rectangle { Width = Math.Max(1, (c.To - c.From) / duration * w), Height = 4, Fill = (Brush)FindResource("AccentBrush") };
            Canvas.SetLeft(bar, Math.Max(0, c.From) / duration * w);
            Marks.Children.Add(bar);
        }
        if (ChapCustom.IsChecked != true) return;
        foreach (var t in times.Where(t => t > 0 && t < duration))
        {
            var tick = new Rectangle { Width = 2, Height = 8, Fill = Brushes.White };
            Canvas.SetLeft(tick, t / duration * w - 1);
            Marks.Children.Add(tick);
        }
    }

    void OnMarksSize(object s, SizeChangedEventArgs e) => ShowMarks();

    /// <summary>"Insertion automatique": the custom list filled with one chapter every N minutes of the disc, set at their times in the original file.</summary>
    void OnAutoInsert(object s, RoutedEventArgs e)
    {
        if (!(int.TryParse(EveryBox.Text.Trim(), out var minutes) && minutes is >= 1 and <= 600))
        {
            MessageBox.Show(this, "Entrez un nombre de minutes entre 1 et 600.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        times.Clear();
        var passed = 0.0; // disc time at the start of the clip
        foreach (var c in clips)
        {
            for (var at = Math.Ceiling(passed / (minutes * 60.0)) * minutes * 60; at < passed + c.To - c.From; at += minutes * 60)
                if (at > 0) times.Add(Math.Round(c.From + at - passed, 1));
            passed += c.To - c.From;
        }
        ChapCustom.IsChecked = true;
        ShowTimes();
    }

    void OnChapterMode(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        ListPanel.IsEnabled = ChapCustom.IsChecked == true;
        ShowMarks();
    }

    void ShowTimes()
    {
        ChapterList.ItemsSource = times.Select(t => Timecode.Format(t) + (names.TryGetValue(t, out var name) ? "   " + name : "")).ToList();
        if (ready) ShowMarks();
    }

    async void OnDetectScenes(object s, RoutedEventArgs e)
    {
        DetectScenesButton.IsEnabled = false;
        DetectScenesButton.Content = "Analyse…";
        try
        {
            var minGap = duration < 180 ? 10 : 60;
            var detected = await Task.Run(() => DiscTitle.DetectSceneChapters(video, 0.35, minGap));
            if (detected.Count == 0)
            {
                MessageBox.Show(this, "Aucun changement de scène marqué n'a été détecté dans cette vidéo.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            times.Clear();
            times.AddRange(detected);
            ChapCustom.IsChecked = true;
            ShowTimes();
        }
        catch (Exception ex) { MessageBox.Show(this, "Détection des scènes impossible : " + ex.Message, "Chapitres", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            DetectScenesButton.Content = "Détecter les scènes";
            DetectScenesButton.IsEnabled = true;
        }
    }

    void OnInsert(object s, RoutedEventArgs e)
    {
        if (Timecode.Parse(AtBox.Text) is not { } t) t = -1;
        AddChapter(t);
    }

    void OnInsertHere(object s, RoutedEventArgs e) => AddChapter(SeekSlider.Value);

    void AddChapter(double t)
    {
        if (t < 0 || t >= duration) // 0 is taken too: the disc starts a chapter there anyway, and listed it can be named
        {
            MessageBox.Show(this, "Entrez un temps dans la vidéo d'origine (h:mm:ss).", "Chapitre", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        t = Math.Round(t, 1);
        if (!times.Contains(t)) times.Add(t);
        times.Sort();
        ShowTimes();
    }

    void OnDelete(object s, RoutedEventArgs e)
    {
        foreach (var i in ChapterList.SelectedItems.Cast<string>().Select(ChapterList.Items.IndexOf).OrderDescending()) times.RemoveAt(i);
        ShowTimes();
    }

    /// <summary>"Ceux du fichier": the list becomes the chapters the file itself holds (an mkv's), to keep or retouch.</summary>
    void OnFileChapters(object s, RoutedEventArgs e)
    {
        times.Clear();
        names.Clear();
        // the one at the very start is kept for its name: the disc always begins a chapter there
        foreach (var (t, title) in (info.Chapters ?? []).Zip(info.ChapterNames ?? Enumerable.Repeat<string?>(null, int.MaxValue)).Where(c => c.First >= 0 && c.First < duration))
        {
            if (!times.Contains(Math.Round(t, 1))) times.Add(Math.Round(t, 1));
            if (title != null) names.TryAdd(Math.Round(t, 1), title);
        }
        ShowTimes();
    }

    /// <summary>"Nommer…": the name the menu writes for the chosen chapter, instead of "Chapitre N"; the first chapter of the disc is the one at 0:00:00.</summary>
    void OnNameChapter(object s, RoutedEventArgs e)
    {
        if (ChapterList.SelectedIndex is not (>= 0 and var i))
        {
            MessageBox.Show(this, "Choisissez d'abord un chapitre dans la liste.", "Nom du chapitre", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Prompt.Ask(this, $"Nom du chapitre de {Timecode.Format(times[i])} sur le menu ; vide = « Chapitre N »", names.GetValueOrDefault(times[i], "")) is not { } text) return;
        if (text.Trim().Length == 0) names.Remove(times[i]); else names[times[i]] = text.Trim();
        ShowTimes();
    }

    void OnDeleteAll(object s, RoutedEventArgs e)
    {
        times.Clear();
        names.Clear();
        ShowTimes();
    }

    void OnImportChapters(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Fichiers de chapitres|*.txt;*.xml;*.csv|Tous les fichiers|*.*"), Title = Loc.T("Importer des chapitres") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var imported = ChapterFiles.Load(dlg.FileName).Where(c => c.Time >= 0 && c.Time < duration).ToList();
            if (imported.Count == 0)
            {
                MessageBox.Show(this, "Aucun chapitre valide trouvé dans ce fichier pour la durée de cette vidéo.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ChapCustom.IsChecked = true;
            times.Clear();
            names.Clear();
            foreach (var c in imported)
            {
                var t = Math.Round(c.Time, 1);
                if (!times.Contains(t)) times.Add(t);
                if (!string.IsNullOrWhiteSpace(c.Name)) names[t] = c.Name!;
            }
            times.Sort();
            ShowTimes();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Impossible de lire ce fichier de chapitres : " + ex.Message, "Chapitres", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnExportChapters(object s, RoutedEventArgs e)
    {
        if (times.Count == 0)
        {
            MessageBox.Show(this, "La liste des chapitres est vide.", "Chapitres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog { Filter = Loc.Filter("Chapitres OGM (*.txt)|*.txt"), FileName = System.IO.Path.GetFileNameWithoutExtension(video) + "_chapitres.txt", Title = Loc.T("Exporter les chapitres") };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, ChapterFiles.FormatOgm(times, times.Select(t => names.GetValueOrDefault(t)).ToList()), new UTF8Encoding(false));
    }
}
