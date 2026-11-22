using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

/// <summary>Manual assignment of episodes/videos across multiple discs, with live per-disc size estimates.</summary>
public partial class SeasonSplitWindow : Window
{
    readonly Project project;
    readonly List<(DiscTitle Title, SourceInfo Src)> intro;
    readonly List<(DiscTitle Title, SourceInfo Src)> episodes;
    readonly MenuDesign? menu;
    readonly double usableGb;
    readonly List<EpisodeRow> rows = [];
    int discCount;
    bool updating;

    public IReadOnlyList<IReadOnlyList<int>>? DiscGroups { get; private set; }

    public SeasonSplitWindow(Project project, IReadOnlyList<(DiscTitle Title, SourceInfo Src)> probed, double usableGb)
    {
        InitializeComponent();
        this.project = project;
        this.usableGb = usableGb;
        intro = probed.Where(p => p.Title.Intro).ToList();
        episodes = probed.Where(p => !p.Title.Intro).ToList();
        menu = project.Menu || episodes.Count > 1 ? project.Design : null;

        var totalGb = project.EstimateUnfittedGB(probed, menu);
        SummaryText.Text = usableGb > 0
            ? $"Saison complète : {totalGb:0.00} Go estimés avant compression · {usableGb:0.#} Go utilisables par disque."
            : $"Saison complète : {totalGb:0.00} Go estimés avant compression ({episodes.Count} vidéos).";
        if (intro.Count > 0) IntroNote.Visibility = Visibility.Visible;

        discCount = Math.Clamp(usableGb > 0 ? (int)Math.Ceiling(totalGb / usableGb) : 2, 2, episodes.Count);
        for (var i = 0; i < episodes.Count; i++)
        {
            var ep = episodes[i];
            var name = ep.Title.Bonus ? $"{ep.Title.DisplayName} (bonus)" : ep.Title.DisplayName;
            var ts = TimeSpan.FromSeconds(Math.Max(0, ep.Src.Duration));
            var dur = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
            rows.Add(new EpisodeRow(i, $"{i + 1}.", name, dur));
        }
        EpisodeList.ItemsSource = rows;
        ApplyEvenSplit();
    }

    void ApplyEvenSplit()
    {
        updating = true;
        var choices = DiscLabels();
        var baseCount = episodes.Count / discCount;
        var remainder = episodes.Count % discCount;
        var idx = 0;
        for (var d = 0; d < discCount; d++)
        {
            var count = baseCount + (d < remainder ? 1 : 0);
            for (var k = 0; k < count && idx < rows.Count; k++, idx++)
            {
                rows[idx].DiscChoices = choices;
                rows[idx].DiscIndex = d;
            }
        }
        updating = false;
        RefreshSummaries();
    }

    List<string> DiscLabels() => Enumerable.Range(1, discCount).Select(n => $"Disque {n}").ToList();

    void SetDiscCount(int next)
    {
        next = Math.Clamp(next, 2, episodes.Count);
        if (next == discCount) return;
        discCount = next;
        updating = true;
        var choices = DiscLabels();
        foreach (var row in rows)
        {
            row.DiscChoices = choices;
            if (row.DiscIndex >= discCount) row.DiscIndex = discCount - 1;
        }
        updating = false;
        RefreshSummaries();
    }

    void RefreshSummaries()
    {
        DiscCountText.Text = discCount.ToString();
        LessButton.IsEnabled = discCount > 2;
        MoreButton.IsEnabled = discCount < episodes.Count;

        var summaries = new List<DiscSummary>(discCount);
        var hasEmpty = false;
        for (var d = 0; d < discCount; d++)
        {
            var group = rows.Where(r => r.DiscIndex == d).Select(r => episodes[r.Index]).ToList();
            if (group.Count == 0)
            {
                hasEmpty = true;
                summaries.Add(new($"Disque {d + 1}", "0 vidéo", 0, "Aucune vidéo"));
                continue;
            }
            var gb = project.EstimateUnfittedGB(intro.Concat(group).ToList(), menu);
            var fill = usableGb > 0 ? Math.Min(100, gb / usableGb * 100) : 0;
            var note = "";
            if (usableGb > 0 && gb >= usableGb * Project.SplitSuggestionRatio)
                note = $" (+{(int)Math.Round((gb / usableGb - 1) * 100)} %)";
            else if (usableGb > 0 && gb > usableGb)
                note = " (compressé)";
            var sizeText = usableGb > 0 ? $"{gb:0.00} / {usableGb:0.#} Go{note}" : $"{gb:0.00} Go";
            summaries.Add(new($"Disque {d + 1}", $"{group.Count} vidéo{(group.Count > 1 ? "s" : "")}", fill, sizeText));
        }
        DiscSummaryList.ItemsSource = summaries;
        ValidationText.Text = hasEmpty ? "Placez au moins une vidéo sur chaque disque." : "";
        ConfirmButton.IsEnabled = !hasEmpty;
    }

    void OnLessDiscs(object s, RoutedEventArgs e) => SetDiscCount(discCount - 1);
    void OnMoreDiscs(object s, RoutedEventArgs e) => SetDiscCount(discCount + 1);
    void OnEvenSplit(object s, RoutedEventArgs e) => ApplyEvenSplit();
    void OnEpisodeDiscChanged(object s, SelectionChangedEventArgs e) { if (!updating) RefreshSummaries(); }

    void OnConfirm(object s, RoutedEventArgs e)
    {
        var groups = Enumerable.Range(0, discCount)
            .Select(d => (IReadOnlyList<int>)rows.Where(r => r.DiscIndex == d).Select(r => r.Index).ToList())
            .ToList();
        if (groups.Any(g => g.Count == 0)) return;
        DiscGroups = groups;
        DialogResult = true;
    }

    sealed record DiscSummary(string Label, string CountText, double FillPercent, string SizeText);

    sealed class EpisodeRow(int index, string number, string name, string durationText) : INotifyPropertyChanged
    {
        List<string> discChoices = [];
        int discIndex;
        public int Index { get; } = index;
        public string Number { get; } = number;
        public string Name { get; } = name;
        public string DurationText { get; } = durationText;
        public List<string> DiscChoices { get => discChoices; set { discChoices = value; PropertyChanged?.Invoke(this, new(nameof(DiscChoices))); } }
        public int DiscIndex { get => discIndex; set { if (discIndex == value) return; discIndex = value; PropertyChanged?.Invoke(this, new(nameof(DiscIndex))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
