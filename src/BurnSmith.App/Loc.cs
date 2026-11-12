using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace BurnSmith.App;

/// <summary>
/// The language of BurnSmith's own windows: French, as the windows and the code are written, or English. English changes no window and no code: each text
/// is translated when it is first laid out (a text block, a button's caption, a tool tip, a window title, a message box), from en.tsv: one
/// "French TAB English" line per text, as the running program produces it. A French side with holes ({0}, {1}...) is a pattern for a text built in code
/// ("≈ {0} Go sur {1} Go utilisables"); what a hole catches is translated in turn. A text without a line stays in French.
/// The choice is kept in the registry: the user's own (HKCU, set in Paramètres avancés), else the installer's (HKLM), else Windows' language.
/// What is written on a disc's menu is another setting, the menu's own (see MenuDesign.Language).
/// </summary>
static class Loc
{
    const string Key = @"Software\BurnSmith", Value = "Language";

    /// <summary>"fr" or "en": what this run shows.</summary>
    public static string Language { get; private set; } = "fr";

    /// <summary>The language the next start will use.</summary>
    public static string Saved() => Read(Registry.CurrentUser) ?? Read(Registry.LocalMachine) ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en");

    static string? Read(RegistryKey root)
    {
        try { using var key = root.OpenSubKey(Key); return key?.GetValue(Value) is "fr" or "en" ? (string)key.GetValue(Value)! : null; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static void Save(string language)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(Value, language);
    }

    /// <summary>Once, before the first window: <paramref name="asked"/> (--language fr|en) wins for this run and is not kept.</summary>
    public static void Start(string? asked = null)
    {
        Language = asked is "fr" or "en" ? asked : Saved();
        if (Language != "en") return;
        using (var file = typeof(Loc).Assembly.GetManifestResourceStream("en.tsv"))
            if (file != null) Load(new StreamReader(file, Encoding.UTF8).ReadToEnd());
        // everything a window shows as text ends in one of these two, whatever control made it: a button's caption, a list's item, a tool tip
        // on its first size, not on Loaded: WPF raises Loaded only where a handler was set on the element itself
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Follow((DependencyObject)s, TextBlock.TextProperty)));
        EventManager.RegisterClassHandler(typeof(AccessText), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Follow((DependencyObject)s, AccessText.TextProperty)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Follow((DependencyObject)s, Window.TitleProperty)));
        // the name a screen reader says for a control without a caption
        EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Show((DependencyObject)s, AutomationProperties.NameProperty)));
    }

    static readonly DependencyProperty FollowerProperty = DependencyProperty.RegisterAttached("Follower", typeof(Follower), typeof(Loc));

    /// <summary>Translates the text now and each time the code changes it (a status line, a title that counts).</summary>
    static void Follow(DependencyObject element, DependencyProperty property)
    {
        if (element.GetValue(FollowerProperty) == null) element.SetValue(FollowerProperty, new Follower(element, property)); // its binding shows the text at once
    }

    // SetCurrentValue: a binding that feeds the text stays, and its next value comes back here
    static string? Show(DependencyObject element, DependencyProperty property)
    {
        if (element.GetValue(property) is not string text) return null;
        var shown = T(text);
        if (shown != text) element.SetCurrentValue(property, shown);
        return shown;
    }

    /// <summary>Held by the element it watches, and by nothing else: it goes away with it.</summary>
    sealed class Follower : DependencyObject
    {
        static readonly DependencyProperty SeenProperty = DependencyProperty.Register("Seen", typeof(object), typeof(Follower), new PropertyMetadata(null, (d, _) => ((Follower)d).Changed()));
        readonly WeakReference<DependencyObject> element;
        readonly DependencyProperty property;
        string? wrote; // never translated twice: an English text that reads like another French line would go round for ever

        public Follower(DependencyObject element, DependencyProperty property)
        {
            (this.element, this.property) = (new(element), property);
            BindingOperations.SetBinding(this, SeenProperty, new Binding { Source = element, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
        }

        void Changed() { if (element.TryGetTarget(out var target) && target.GetValue(property) as string != wrote) wrote = Show(target, property); }
    }

    static readonly Dictionary<string, string> exact = [];
    static readonly List<(Regex Pattern, string English, int Holes, HashSet<int> Names)> patterns = [];
    static readonly Dictionary<string, string> answered = []; // texts already worked out, those without a line too: a list of 300 names tries the patterns once

    /// <summary>The lines of en.tsv: "French TAB English", \n \t \\ for a line break, a tab and a backslash, # for a comment.</summary>
    internal static void Load(string tsv)
    {
        static string Plain(string s) => Regex.Replace(s, @"\\([nt\\])", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", _ => "\\" });
        foreach (var line in tsv.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#' || line.TrimEnd('\r').Split('\t') is not [var french, var english]) continue;
            (french, english) = (Plain(french), Plain(english));
            if (!Regex.IsMatch(french, @"\{\d+\}")) { exact.TryAdd(french, english); continue; }
            // a pattern: its plain parts as they are, each hole anything (the same hole twice = the same text twice)
            var seen = new HashSet<string>();
            var names = new HashSet<int>(); // a hole between « » is a name the user gave (a title, a file): never translated
            var quoted = false;
            var regex = new StringBuilder(@"\A");
            foreach (Match part in Regex.Matches(french, @"\{(\d+)\}|\{\{|\}\}|[^{}]+|[{}]"))
                if (part.Groups[1].Success)
                {
                    if (quoted) names.Add(int.Parse(part.Groups[1].Value));
                    regex.Append(seen.Add(part.Groups[1].Value) ? $"(?<h{part.Groups[1].Value}>.*?)" : $@"\k<h{part.Groups[1].Value}>");
                }
                else { quoted = part.Value.EndsWith("« "); var text = part.Value is "{{" ? "{" : part.Value is "}}" ? "}" : part.Value; regex.Append(Regex.Escape(text)); }
            patterns.Add((new Regex(regex.Append(@"\z").ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), english, seen.Max(int.Parse) + 1, names));
        }
        patterns.Sort((a, b) => b.Pattern.ToString().Length.CompareTo(a.Pattern.ToString().Length)); // the one that says the most first
    }

    /// <summary><paramref name="text"/> in the app's language: itself in French, or without a line for it.</summary>
    public static string T(string? text)
    {
        if (text == null) return "";
        if (Language != "en" || !text.Any(char.IsLetter)) return text;
        if (exact.TryGetValue(text, out var known)) return known;
        lock (answered)
        {
            if (answered.TryGetValue(text, out var done)) return done;
            var result = text;
            foreach (var (pattern, english, holes, names) in patterns)
            {
                if (pattern.Match(text) is not { Success: true } m) continue;
                try { result = string.Format(CultureInfo.CurrentCulture, english, [.. Enumerable.Range(0, holes).Select(h => (object)(names.Contains(h) ? m.Groups["h" + h].Value : T(m.Groups["h" + h].Value)))]); }
                catch (FormatException) { continue; } // a line of the file with a hole its French side does not have: as if it were not there
                break;
            }
            if (answered.Count > 5000) answered.Clear();
            return answered[text] = result;
        }
    }

    /// <summary>A file dialog's filter ("Images|*.png|Tous les fichiers|*.*"): its names translated, its patterns kept.</summary>
    public static string Filter(string filter) => string.Join('|', filter.Split('|').Select((part, i) => i % 2 == 0 ? T(part) : part));
}

/// <summary>The app's message boxes, in its language: within this namespace the name hides System.Windows.MessageBox, so every call goes through here.</summary>
static class MessageBox
{
    public static MessageBoxResult Show(string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult byDefault = MessageBoxResult.None) =>
        System.Windows.MessageBox.Show(Loc.T(text), Loc.T(caption), button, icon, byDefault);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult byDefault = MessageBoxResult.None) =>
        owner == null ? Show(text, caption, button, icon, byDefault) : System.Windows.MessageBox.Show(owner, Loc.T(text), Loc.T(caption), button, icon, byDefault);
}
