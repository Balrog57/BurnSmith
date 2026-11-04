using System.Windows;

namespace BurnSmith.App;

/// <summary>DVDFab's "Paramètres avancés": only what BurnSmith really applies (volume name, chapter spacing).</summary>
public partial class AdvancedWindow : Window
{
    public string Volume { get; private set; }
    public int ChapterMinutes { get; private set; }

    public AdvancedWindow(string volume, int chapterMinutes)
    {
        InitializeComponent();
        Volume = volume;
        ChapterMinutes = chapterMinutes;
        VolumeBox.Text = volume;
        ChapterBox.ItemsSource = new[] { 1, 2, 3, 5, 10, 15 };
        ChapterBox.SelectedItem = chapterMinutes;
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        var name = VolumeBox.Text.Trim();
        if (name.Length > 0) Volume = name;
        if (ChapterBox.SelectedItem is int minutes) ChapterMinutes = minutes;
        DialogResult = true;
    }
}
