using System.Windows;
using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>DVDFab's "Paramètres avancés": only what BurnSmith really applies (volume name, chapter spacing).</summary>
public partial class AdvancedWindow : Window
{
    public string Volume { get; private set; }
    public int ChapterMinutes { get; private set; }
    public EncodeOptions Encode { get; private set; }

    static readonly (string Name, int Crf, string Preset)[] Qualities = [("Rapide (fichier plus gros)", 20, "veryfast"), ("Standard", 17, "medium"), ("Haute (plus lent)", 14, "slow")];

    public AdvancedWindow(string volume, int chapterMinutes, EncodeOptions encode)
    {
        InitializeComponent();
        Volume = volume;
        ChapterMinutes = chapterMinutes;
        Encode = encode;
        VolumeBox.Text = volume;
        ChapterBox.ItemsSource = new[] { 1, 2, 3, 5, 10, 15 };
        ChapterBox.SelectedItem = chapterMinutes;
        ModeBox.ItemsSource = new[] { "Automatique (copie si la vidéo convient)", "Toujours convertir" };
        ModeBox.SelectedIndex = encode.ForceReencode ? 1 : 0;
        QualityBox.ItemsSource = Qualities.Select(q => q.Name).ToList();
        QualityBox.SelectedIndex = Math.Max(0, Array.FindIndex(Qualities, q => q.Crf == encode.Crf && q.Preset == encode.Preset));
        DvdBitrateBox.Text = encode.DvdKbps > 0 ? encode.DvdKbps.ToString() : "0";
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        var name = VolumeBox.Text.Trim();
        if (name.Length > 0) Volume = name;
        if (ChapterBox.SelectedItem is int minutes) ChapterMinutes = minutes;
        var quality = Qualities[Math.Max(0, QualityBox.SelectedIndex)];
        Encode = new EncodeOptions(ModeBox.SelectedIndex == 1, quality.Crf, quality.Preset, int.TryParse(DvdBitrateBox.Text, out var kbps) && kbps > 0 ? Math.Clamp(kbps, 1500, 9000) : 0);
        DialogResult = true;
    }
}
