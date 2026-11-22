using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BurnSmith.Core;

/// <summary>Reads and writes standard chapter list files: OGM (.txt), Matroska XML (.xml) and simple timecode-per-line text files.</summary>
public static partial class ChapterFiles
{
    public sealed record Entry(double Time, string? Name);

    [GeneratedRegex(@"^CHAPTER(\d+)=(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OgmTime();

    [GeneratedRegex(@"^CHAPTER(\d+)NAME=(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OgmName();

    public static List<Entry> Load(string file)
    {
        var raw = File.ReadAllBytes(file);
        var text = BluRay.IsUnicode(raw) ? Encoding.UTF8.GetString(raw) : Encoding.GetEncoding(1252).GetString(raw);
        return Parse(text);
    }

    public static List<Entry> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('<')) return ParseMatroskaXml(trimmed);

        var ogmTimes = new Dictionary<int, double>();
        var ogmNames = new Dictionary<int, string>();
        var simple = new List<Entry>();

        foreach (var rawLine in trimmed.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (OgmTime().Match(line) is { Success: true } tm && Timecode.Parse(tm.Groups[2].Value.Trim()) is { } ts)
            {
                ogmTimes[int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture)] = ts;
                continue;
            }
            if (OgmName().Match(line) is { Success: true } nm)
            {
                ogmNames[int.Parse(nm.Groups[1].Value, CultureInfo.InvariantCulture)] = nm.Groups[2].Value.Trim();
                continue;
            }
            // Simple format: "00:05:12.500 Title" or "00:05:12,Title" or "00:05:12\tTitle"
            var split = line.IndexOfAny([' ', '\t', ';', ',']);
            var token = split > 0 ? line[..split].Trim() : line;
            var label = split > 0 ? line[(split + 1)..].Trim() : "";
            if (Timecode.Parse(token) is { } seconds && seconds >= 0)
                simple.Add(new Entry(Math.Round(seconds, 3), label.Length > 0 ? label : null));
        }

        if (ogmTimes.Count > 0)
            return [.. ogmTimes.OrderBy(k => k.Key).Select(k => new Entry(Math.Round(k.Value, 3),
                ogmNames.TryGetValue(k.Key, out var n) && n.Length > 0 ? n : null)).DistinctBy(e => e.Time).OrderBy(e => e.Time)];

        return [.. simple.DistinctBy(e => e.Time).OrderBy(e => e.Time)];
    }

    static List<Entry> ParseMatroskaXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var entries = new List<Entry>();
        foreach (var atom in doc.Descendants().Where(e => e.Name.LocalName == "ChapterAtom"))
        {
            var timeText = atom.Elements().FirstOrDefault(e => e.Name.LocalName == "ChapterTimeStart")?.Value.Trim();
            if (string.IsNullOrEmpty(timeText)) continue;
            // Matroska XML timestamps may have nanoseconds: "00:05:12.500000000"
            if (timeText.IndexOf('.') is var dot and > 0 && timeText.Length - dot > 4) timeText = timeText[..(dot + 4)];
            if (Timecode.Parse(timeText) is not { } sec || sec < 0) continue;
            var name = atom.Descendants().FirstOrDefault(e => e.Name.LocalName == "ChapterString")?.Value.Trim();
            entries.Add(new Entry(Math.Round(sec, 3), string.IsNullOrEmpty(name) ? null : name));
        }
        return [.. entries.DistinctBy(e => e.Time).OrderBy(e => e.Time)];
    }

    /// <summary>Exports chapter timestamps and optional titles in standard OGM .txt format.</summary>
    public static string FormatOgm(IReadOnlyList<double> times, IReadOnlyList<string?>? names = null)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < times.Count; i++)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, times[i]));
            var num = i + 1;
            sb.AppendLine($"CHAPTER{num:00}={(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}");
            var title = names != null && i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i]!.Trim() : $"Chapitre {num}";
            sb.AppendLine($"CHAPTER{num:00}NAME={title}");
        }
        return sb.ToString();
    }
}
