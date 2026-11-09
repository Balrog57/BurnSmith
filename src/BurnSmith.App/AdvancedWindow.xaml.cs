using System.Windows;
using System.Windows.Controls;
using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>
/// The advanced settings: the disc (<see cref="Output"/>, shared with the main window's list), the picture quality, how the menu plays the titles,
/// chapter spacing and the encoder settings. What only a Blu-ray has is hidden for a DVD; a 4K disc has its own processing mode (Passthrough copies the HEVC, Conversion makes it again).
/// </summary>
public partial class AdvancedWindow : Window
{
    public string Volume { get; private set; }
    public int ChapterMinutes { get; private set; }
    public EncodeOptions Encode { get; private set; }
    public PlaybackOptions Playback { get; private set; }
    public Output Output { get; private set; }

    static readonly double[] Rates = [0, 23.976, 24, 25, 29.97, 50, 59.94]; // 0 = as the source
    static readonly (string Name, int Crf, string Preset)[] Speeds = [("Rapide (fichier plus gros)", 20, "veryfast"), ("Standard", 17, "medium"), ("Lente (meilleure qualité)", 14, "slow")];
    // index 0 = fit the disc, the last = typed in; Blu-ray: 1 = the source's own quality. A DVD is always made again in MPEG-2: it has no "source" choice
    static readonly (string Name, int Kbps)[] BdRates = [("Adapter au disque", 0), ("Identique à la source", 0), ("15 Mbps", 15000), ("20 Mbps", 20000), ("30 Mbps", 30000), ("Personnaliser", -1)];
    static readonly (string Name, int Kbps)[] UhdRates = [("Adapter au disque", 0), ("Identique à la source", 0), ("30 Mbps", 30000), ("50 Mbps", 50000), ("60 Mbps", 60000), ("Personnaliser", -1)];
    static readonly (string Name, int Kbps)[] DvdRates = [("Adapter au disque", 0), ("5 Mbps", 5000), ("7 Mbps", 7000), ("9 Mbps", 9000), ("Personnaliser", -1)];
    readonly (string Name, int Kbps)[] rates = BdRates;
    readonly bool dvd, uhd;
    int Custom => rates.Length - 1;
    (int Min, int Max) Range => dvd ? (2000, 9000) : uhd ? (2000, 60000) : (2000, 35000);
    bool Passthrough => uhd && TreatBox.SelectedIndex == 0;

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

        dvd = kind == "dvd";
        uhd = kind == "uhd";
        if (dvd) rates = DvdRates; else if (uhd) rates = UhdRates;
        TreatLabel.Visibility = TreatBox.Visibility = TreatNote.Visibility = uhd ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel.Visibility = ModeBox.Visibility = uhd ? Visibility.Collapsed : Visibility.Visible; // the 4K disc says it in "Mode de traitement"
        TreatBox.ItemsSource = new[] { "Passthrough", "Conversion" };
        TreatBox.SelectedIndex = encode.ForceReencode || encode.BdKbps > 0 ? 1 : 0;
        DvdPanel.Visibility = dvd ? Visibility.Visible : Visibility.Collapsed;
        EncodePanel.Visibility = dvd ? Visibility.Collapsed : Visibility.Visible;
        RateBox.ToolTip = $"Débit vidéo, de {Range.Min} à {Range.Max} kb/s";
        (encode.DvdPal switch { true => TvPal, false => TvNtsc, null => TvAuto }).IsChecked = true;
        (encode.DvdWide ? Wide : Narrow).IsChecked = true;

        var kbps = dvd ? encode.DvdKbps : encode.BdKbps;
        QualityBox.ItemsSource = rates.Select(r => r.Name).ToList();
        QualityBox.SelectedIndex = kbps > 0 ? (Array.FindIndex(rates, r => r.Kbps == kbps) is var rate and >= 0 ? rate : Custom) : dvd || encode.FitDisc ? 0 : 1;
        RateBox.Text = kbps > 0 ? kbps.ToString() : dvd ? "6000" : "15000";
        ModeBox.ItemsSource = new[] { "Automatique (copie si la vidéo convient)", "Toujours convertir" };
        ModeBox.SelectedIndex = encode.ForceReencode ? 1 : 0;
        GpuBox.IsChecked = encode.Gpu;
        FpsBox.ItemsSource = Rates.Select(r => r == 0 ? "Comme la source" : r.ToString("0.###")).ToList();
        FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(Rates, encode.Fps ?? 0));
        SpeedBox.ItemsSource = Speeds.Select(q => q.Name).ToList();
        SpeedBox.SelectedIndex = Array.FindIndex(Speeds, q => q.Crf == encode.Crf && q.Preset == encode.Preset) is var speed and >= 0 ? speed : 1;

        (Playback.Mode == 0 ? Mode0 : Playback.Mode == 1 ? Mode1 : Mode2).IsChecked = true;
        Rewind0.IsChecked = Rewind1.IsChecked = playback.Rewind;
        ready = true;
        OnMode(null!, null!);
        OnQuality(null!, null!);
    }

    int ChosenRate => QualityBox.SelectedIndex is var i && i > 0 && rates[i].Kbps > 0 ? rates[i].Kbps
        : QualityBox.SelectedIndex == Custom && int.TryParse(RateBox.Text, out var typed) ? Math.Clamp(typed, Range.Min, Range.Max) : 0;

    EncodeOptions Current()
    {
        var speed = Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
        var output = OutputBox.SelectedItem as Output ?? Output;
        // each kind keeps the other's rate as it was
        // a 4K passthrough never re-encodes: no rate, no fitting
        return new EncodeOptions(uhd ? !Passthrough : ModeBox.SelectedIndex == 1, speed.Crf, speed.Preset, dvd ? ChosenRate : Encode.DvdKbps,
            dvd ? Encode.BdKbps : Passthrough ? 0 : ChosenRate, dvd ? Encode.FitDisc : !Passthrough && QualityBox.SelectedIndex == 0, output.Hd720,
            TvPal.IsChecked == true ? true : TvNtsc.IsChecked == true ? false : null, Narrow.IsChecked != true,
            GpuBox.IsChecked == true, dvd ? Encode.Fps : FpsBox.SelectedIndex > 0 ? Rates[FpsBox.SelectedIndex] : null);
    }

    void OnQuality(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        QualityBox.IsEnabled = !Passthrough;
        RateBox.IsEnabled = !Passthrough && QualityBox.SelectedIndex == Custom;
        OnRefresh(s, e);
    }

    void OnRefresh(object s, RoutedEventArgs e)
    {
        if (!ready || OutputBox.SelectedItem is not Output output) return;
        var gb = estimate?.Invoke(output, Current());
        SizeText.Text = gb == null ? "—" : $"{gb:0.00} Go";
        SizeText.ToolTip = gb == null ? "Ajoutez une vidéo pour voir la taille du disque."
            : $"≈ {gb:0.0} Go sur {output.Usable:0.#} Go utilisables{(gb > output.Usable ? " : trop grand pour ce support" : "")}";
        if (gb > output.Usable) SizeText.Foreground = System.Windows.Media.Brushes.IndianRed; else SizeText.ClearValue(ForegroundProperty);
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
