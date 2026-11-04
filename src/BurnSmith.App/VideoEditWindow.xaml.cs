using System.Windows;
using System.Windows.Controls;
using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>
/// Yuhan's "Video Settings" / DVDFab's video edit, for one video: its name in the menu, the part kept (start and end), the volume and the chapters
/// (the disc's "one every N minutes", none, or a list). Times are those of the original file; the result is read from the properties after OK.
/// </summary>
public partial class VideoEditWindow : Window
{
    public string NameText { get; private set; }
    public double Start { get; private set; }
    /// <summary>Null = to the end of the file.</summary>
    public double? End { get; private set; }
    public int Volume { get; private set; }
    /// <summary>Null = the disc's setting, empty = no chapters, else the chapter times.</summary>
    public IReadOnlyList<double>? ChapterAt { get; private set; }

    readonly double duration;
    readonly List<double> times;
    readonly bool ready;

    public VideoEditWindow(string name, double duration, double start, double? end, int volume, IReadOnlyList<double>? chapterAt, int discMinutes)
    {
        InitializeComponent();
        this.duration = duration;
        NameText = name;
        Start = start;
        End = end;
        Volume = volume;
        ChapterAt = chapterAt;
        NameBox.Text = name;
        SourceText.Text = $"Durée du fichier d'origine : {Timecode.Format(duration)}";
        StartCheck.IsChecked = start > 0;
        StartBox.Text = Timecode.Format(start);
        EndCheck.IsChecked = end != null;
        EndBox.Text = Timecode.Format(end ?? duration);
        VolumeSlider.Value = volume;
        ChapDisc.Content = $"Comme le disque (un chapitre toutes les {discMinutes} min)";
        times = chapterAt?.Order().ToList() ?? [];
        (chapterAt == null ? ChapDisc : chapterAt.Count == 0 ? ChapNone : ChapCustom).IsChecked = true;
        ready = true;
        OnCut(null!, null!);
        OnVolume(null!, null!);
        OnChapterMode(null!, null!);
        ShowTimes();
    }

    /// <summary>The part kept, or why the times typed do not make one. An end at the end of the file is no cut at all.</summary>
    bool TryCut(out double start, out double? end, out string error)
    {
        start = 0;
        end = null;
        error = "";
        if (StartCheck.IsChecked == true)
        {
            if (Timecode.Parse(StartBox.Text) is not { } s || s >= duration) { error = "Début : un temps dans la vidéo est attendu (h:mm:ss)."; return false; }
            start = s;
        }
        if (EndCheck.IsChecked == true)
        {
            if (Timecode.Parse(EndBox.Text) is not { } e) { error = "Fin : un temps dans la vidéo est attendu (h:mm:ss)."; return false; }
            if (e < duration - 0.05) end = e;
        }
        if ((end ?? duration) - start < 1) { error = "La partie gardée doit durer au moins une seconde."; return false; }
        return true;
    }

    void OnCut(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        StartBox.IsEnabled = StartCheck.IsChecked == true;
        EndBox.IsEnabled = EndCheck.IsChecked == true;
        var ok = TryCut(out var start, out var end, out var error);
        KeptText.Text = ok ? $"Durée sur le disque : {Timecode.Format((end ?? duration) - start)}" : error;
        if (ok) KeptText.ClearValue(TextBlock.ForegroundProperty);
        else KeptText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
    }

    void OnVolume(object s, RoutedEventArgs e)
    {
        if (ready) VolumeText.Text = $"{(int)VolumeSlider.Value} %";
    }

    void OnChapterMode(object s, RoutedEventArgs e)
    {
        if (ready) ListPanel.IsEnabled = ChapCustom.IsChecked == true;
    }

    void ShowTimes() => ChapterList.ItemsSource = times.Select(Timecode.Format).ToList();

    void OnInsert(object s, RoutedEventArgs e)
    {
        if (Timecode.Parse(AtBox.Text) is not { } t || t <= 0 || t >= duration)
        {
            MessageBox.Show(this, "Entrez un temps dans la vidéo d'origine (h:mm:ss), après le début.", "Chapitre", MessageBoxButton.OK, MessageBoxImage.Information);
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

    void OnDeleteAll(object s, RoutedEventArgs e)
    {
        times.Clear();
        ShowTimes();
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        if (!TryCut(out var start, out var end, out _)) return; // the reason is already shown under the times
        if (NameBox.Text.Trim() is { Length: > 0 } name) NameText = name;
        Start = start;
        End = end;
        Volume = (int)VolumeSlider.Value;
        ChapterAt = ChapDisc.IsChecked == true ? null : ChapNone.IsChecked == true ? [] : times.ToList();
        DialogResult = true;
    }
}
