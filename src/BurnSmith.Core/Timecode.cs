using System.Globalization;

namespace BurnSmith.Core;

/// <summary>Times as a person types them: "90", "1:30", "1:02:03", "1:02:03.5" (a comma works too: that is what a French keyboard has).</summary>
public static class Timecode
{
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return null;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            // only the seconds may carry a fraction
            if (!double.TryParse(parts[i], i < parts.Length - 1 ? NumberStyles.None : NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>"1:02:03", with a tenth of a second only when there is one.</summary>
    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 1));
        var whole = $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return t.Milliseconds == 0 ? whole : whole + "." + (t.Milliseconds / 100);
    }
}
