using System.ComponentModel;
using System.Windows;
using BurnSmith.Core;

namespace BurnSmith.App;

public partial class DiscImportWindow : Window
{
    sealed class Row(BluRay.DiscSourceTitle source, bool selected) : INotifyPropertyChanged
    {
        bool selected = selected;
        public BluRay.DiscSourceTitle Source { get; } = source;
        public string Title => Source.Name;
        public string Detail => Source.Summary;
        public bool Selected
        {
            get => selected;
            set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly List<Row> rows;
    public List<BluRay.DiscSourceTitle> Chosen { get; private set; } = [];

    public DiscImportWindow(string discName, IReadOnlyList<BluRay.DiscSourceTitle> titles)
    {
        InitializeComponent();
        HeaderText.Text = $"Titres détectés dans « {discName} »";
        rows = titles.Select((t, i) => new Row(t, i == 0)).ToList();
        TitleList.ItemsSource = rows;
    }

    void OnAll(object s, RoutedEventArgs e) { foreach (var r in rows) r.Selected = true; }
    void OnNone(object s, RoutedEventArgs e) { foreach (var r in rows) r.Selected = false; }

    void OnOk(object s, RoutedEventArgs e)
    {
        Chosen = rows.Where(r => r.Selected).Select(r => r.Source).ToList();
        if (Chosen.Count == 0)
        {
            MessageBox.Show(this, "Cochez au moins un titre à importer.", "BurnSmith", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
