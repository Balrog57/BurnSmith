using System.Windows;
using System.Windows.Controls;
using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>
/// DVDFab's "Paramètres avancés": the disc (<see cref="Output"/>, shared with the main window's list), the picture quality, how the menu plays the titles,
/// chapter spacing and the x264 settings. What only a Blu-ray has is hidden for a DVD, and the picture quality for a 4K disc (its HEVC is kept as it is).
/// </summary>
public partial class AdvancedWindow : Window
{
    public string Volume { get; private set; }
    public int ChapterMinutes { get; private set; }
    public EncodeOptions Encode { get; private set; }
    public PlaybackOptions Playback { get; private set; }
    public Output Output { get; private set; }

    static readonly (string Name, int Crf, string Preset)[] Speeds = [("Rapide (fichier plus gros)", 20, "veryfast"), ("Standard", 17, "medium"), ("Lente (meilleure qualité)", 14, "slow")];
    // index 0 = fit the disc, 1 = the source's own quality, 2..4 = a fixed rate, 5 = typed in
    static readonly (string Name, int Kbps)[] Rates = [("Adapter au disque (recommandé)", 0), ("Source (copie si possible)", 0), ("15 Mb/s", 15000), ("20 Mb/s", 20000), ("30 Mb/s", 30000), ("Personnaliser…", -1)];

    readonly Func<Output, EncodeOptions, double?>? estimate;
    bool ready;

    /// <param name="estimate">Size in GB of the disc for a given output and encoding (null = no video yet).</param>
    public AdvancedWindow(string volume, int chapterMinutes, EncodeOptions encode, PlaybackOptions playback, string kind, IReadOnlyList<Output> outputs, Output output,
                          Func<Output, EncodeOptions, double?>? estimate = null)
    {
        InitializeComponent();
        this.estimate = estimate;
        Volume = volume;
        ChapterMinutes = chapterMinutes;
        Encode = encode;
        Playback = playback;
        Output = output;
        VolumeBox.Text = volume;
        OutputBox.ItemsSource = outputs;
        OutputBox.SelectedItem = output;
        ChapterBox.ItemsSource = new[] { 1, 2, 3, 5, 10, 15 };
        ChapterBox.SelectedItem = chapterMinutes;

        var dvd = kind == "dvd";
        QualityPanel.Visibility = kind == "bd" ? Visibility.Visible : Visibility.Collapsed;
        DvdPanel.Visibility = dvd ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPanel.Visibility = dvd ? Visibility.Collapsed : Visibility.Visible;
        EncodePanel.Visibility = dvd ? Visibility.Collapsed : Visibility.Visible;
        DvdBitrateBox.Text = encode.DvdKbps > 0 ? encode.DvdKbps.ToString() : "0";

        QualityBox.ItemsSource = Rates.Select(r => r.Name).ToList();
        QualityBox.SelectedIndex = encode.BdKbps > 0 ? Math.Max(2, Array.FindIndex(Rates, r => r.Kbps == encode.BdKbps) is var at && at >= 0 ? at : 5) : encode.FitDisc ? 0 : 1;
        RateBox.Text = encode.BdKbps > 0 ? encode.BdKbps.ToString() : "15000";
        ModeBox.ItemsSource = new[] { "Automatique (copie si la vidéo convient)", "Toujours convertir" };
        ModeBox.SelectedIndex = encode.ForceReencode ? 1 : 0;
        SpeedBox.ItemsSource = Speeds.Select(q => q.Name).ToList();
        SpeedBox.SelectedIndex = Math.Max(1, Array.FindIndex(Speeds, q => q.Crf == encode.Crf && q.Preset == encode.Preset));

        (Playback.Mode == 0 ? Mode0 : Playback.Mode == 1 ? Mode1 : Mode2).IsChecked = true;
        Rewind0.IsChecked = Rewind1.IsChecked = playback.Rewind;
        ready = true;
        OnMode(null!, null!);
        OnQuality(null!, null!);
    }

    int ChosenRate => QualityBox.SelectedIndex is var i && i >= 2 && Rates[i].Kbps > 0 ? Rates[i].Kbps
        : QualityBox.SelectedIndex == 5 && int.TryParse(RateBox.Text, out var typed) ? Math.Clamp(typed, 2000, 35000) : 0;

    EncodeOptions Current()
    {
        var speed = Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
        var output = OutputBox.SelectedItem as Output ?? Output;
        return new EncodeOptions(ModeBox.SelectedIndex == 1, speed.Crf, speed.Preset, int.TryParse(DvdBitrateBox.Text, out var kbps) && kbps > 0 ? Math.Clamp(kbps, 1500, 9000) : 0,
            ChosenRate, QualityBox.SelectedIndex == 0, output.Hd720);
    }

    void OnQuality(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        RateBox.IsEnabled = QualityBox.SelectedIndex == 5;
        OnRefresh(s, e);
    }

    void OnRefresh(object s, RoutedEventArgs e)
    {
        if (!ready || OutputBox.SelectedItem is not Output output) return;
        var gb = estimate?.Invoke(output, Current());
        SizeText.Text = gb == null ? "Ajoutez une vidéo pour voir la taille du disque."
            : $"≈ {gb:0.0} Go sur {output.Usable:0.#} Go utilisables{(gb > output.Usable ? "  (trop grand pour ce support)" : "")}";
    }

    /// <summary>"Rembobiner" belongs to its own radio button only.</summary>
    void OnMode(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        Rewind0.IsEnabled = Mode0.IsChecked == true;
        Rewind1.IsEnabled = Mode1.IsChecked == true;
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        var name = VolumeBox.Text.Trim();
        if (name.Length > 0) Volume = name;
        if (ChapterBox.SelectedItem is int minutes) ChapterMinutes = minutes;
        if (OutputBox.SelectedItem is Output output) Output = output;
        Encode = Current();
        var mode = Mode0.IsChecked == true ? 0 : Mode1.IsChecked == true ? 1 : 2;
        Playback = PlaybackOptions.FromMode(mode, mode == 0 ? Rewind0.IsChecked == true : mode == 1 ? Rewind1.IsChecked == true : true);
        DialogResult = true;
    }
}
