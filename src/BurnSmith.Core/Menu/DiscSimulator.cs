namespace BurnSmith.Core.Menu;

/// <summary>
/// Unified interactive disc simulator for BD-J, HDMV and DVD: executes the exact pages, buttons and playback rules
/// built by <see cref="MenuBuilder"/> and records a step-by-step action trace (start, remote keys, audio/subtitle
/// selection, pop-up overlay, end-of-title chaining, Top Menu and session resume).
/// </summary>
public sealed class DiscSimulator
{
    readonly Dictionary<string, MenuPage> pages;
    readonly string startPage;
    readonly PlaybackOptions playback;
    readonly string mode;
    readonly int mainCount;
    readonly bool hasIntro;
    readonly IReadOnlyDictionary<int, string> endActions;

    public string? CurrentPageId { get; private set; }
    public string? FocusedButtonId { get; private set; }
    public int? PlayingTitle { get; private set; }
    public int PlayingChapter { get; private set; }
    public int AudioTrack { get; private set; } = 1;
    public int SubtitleTrack { get; private set; }
    public bool PopupOpen { get; private set; }
    public bool ChainPlaying { get; private set; }
    public (int Title, int Chapter)? ResumePoint { get; private set; }
    public List<string> Log { get; } = [];

    public DiscSimulator(IReadOnlyList<MenuPage> pages, PlaybackOptions? playback = null, string mode = "BD-J",
        int mainCount = 1, bool hasIntro = false, IReadOnlyDictionary<int, string>? endActions = null)
    {
        this.pages = pages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        startPage = pages.Count > 0 ? pages[0].Id : "root";
        this.playback = playback ?? new PlaybackOptions();
        this.mode = mode;
        this.mainCount = Math.Max(1, mainCount);
        this.hasIntro = hasIntro;
        this.endActions = endActions ?? new Dictionary<int, string>();
        OpenPage(startPage, log: false);
    }

    public string StartDisc()
    {
        Log.Clear();
        PopupOpen = false;
        PlayingTitle = null;
        if (hasIntro) Add("Lecture de la vidéo d'introduction.");
        if (playback.RememberPosition && ResumePoint is { } saved)
        {
            PlayingTitle = saved.Title;
            PlayingChapter = saved.Chapter;
            CurrentPageId = null;
            return Add($"[{mode}] Reprise de session → Titre {saved.Title}, chapitre {saved.Chapter} (audio {AudioTrack}, sous-titre {SubLabel()}).");
        }
        if (playback.StartAtFirstTitle)
        {
            ChainPlaying = playback.PlayAll;
            return StartTitle(1, 1);
        }
        OpenPage(startPage, log: false);
        var auto = playback.AutoStartSeconds > 0 ? $" (démarrage auto après {playback.AutoStartSeconds} s)" : "";
        return Add($"[{mode}] Démarrage sur « {startPage} »{auto}.");
    }

    public string Press(string key)
    {
        switch (key)
        {
            case "Start": return StartDisc();
            case "Menu":
                if (PlayingTitle is { } t && playback.RememberPosition) ResumePoint = (t, Math.Max(1, PlayingChapter));
                PopupOpen = false;
                PlayingTitle = null;
                ChainPlaying = false;
                OpenPage(startPage, log: false);
                return Add($"[{mode}] Touche Menu principal → page « {startPage} ».");
            case "Popup":
                if (PlayingTitle == null) return Add($"[{mode}] Pop-up : disponible uniquement pendant la lecture d'un titre.");
                if (mode == "DVD") return Add("[DVD] Pas de menu pop-up en DVD-Video.");
                PopupOpen = !PopupOpen;
                return Add(PopupOpen
                    ? $"[{mode}] Pop-up ouvert sur le titre {PlayingTitle} (audio {AudioTrack}, sous-titre {SubLabel()})."
                    : $"[{mode}] Pop-up fermé, reprise du titre {PlayingTitle}.");
            case "End":
                return OnTitleEnd();
            case "Up" or "Down" or "Left" or "Right":
                return MoveFocus(key);
            case "Enter":
                return ActivateFocused();
            default:
                return Add($"[{mode}] Touche « {key} » ignorée.");
        }
    }

    string MoveFocus(string dir)
    {
        if (CurrentPageId == null || !pages.TryGetValue(CurrentPageId, out var page) || page.Buttons.Count == 0)
            return Add($"[{mode}] Aucune page active pour {dir}.");
        var current = page.Buttons.FirstOrDefault(b => b.Id == FocusedButtonId) ?? page.Buttons[0];
        var nextId = dir switch { "Up" => current.Up, "Down" => current.Down, "Left" => current.Left, _ => current.Right };
        if (!string.IsNullOrEmpty(nextId) && page.Buttons.Any(b => b.Id == nextId)) FocusedButtonId = nextId;
        return Add($"[{mode}] {CurrentPageId} · {dir} → bouton « {FocusedButtonId} ».");
    }

    string ActivateFocused()
    {
        if (CurrentPageId == null || !pages.TryGetValue(CurrentPageId, out var page))
            return Add($"[{mode}] OK pendant la lecture du titre {PlayingTitle}.");
        var btn = page.Buttons.FirstOrDefault(b => b.Id == FocusedButtonId) ?? page.Buttons.FirstOrDefault();
        if (btn == null) return Add($"[{mode}] Page « {CurrentPageId} » sans bouton.");
        var a = btn.Action;
        return a.Kind switch
        {
            "page" => OpenPage(a.Page ?? startPage, log: true),
            "play" => StartTitle(Math.Max(1, a.Playlist - BluRay.MoviePlaylist + 1), 1, chain: playback.PlayAll),
            "chapter" => StartTitle(Math.Max(1, a.Playlist - BluRay.MoviePlaylist + 1), Math.Max(1, a.Mark + 1), chain: playback.PlayAll),
            "playall" => StartTitle(1, 1, chain: true),
            "audio" => SetAudio(Math.Max(1, a.Mark)),
            "sub" => SetSub(Math.Max(0, a.Mark)),
            "resume" => ResumePlayback(),
            "menu" => Press("Menu"),
            "popup" => Press("Popup"),
            _ => Add($"[{mode}] Action « {a.Kind} ».")
        };
    }

    string OpenPage(string id, bool log)
    {
        if (!pages.TryGetValue(id, out var page))
        {
            if (pages.Count == 0) return Add($"[{mode}] Page « {id} » introuvable.");
            page = pages[startPage];
        }
        CurrentPageId = page.Id;
        FocusedButtonId = page.Buttons.FirstOrDefault()?.Id;
        return log ? Add($"[{mode}] Page → « {page.Id} » (bouton « {FocusedButtonId ?? "aucun"} »).") : "";
    }

    string StartTitle(int title, int chapter, bool chain = false)
    {
        PlayingTitle = title;
        PlayingChapter = chapter;
        ChainPlaying = chain && title <= mainCount;
        CurrentPageId = null;
        PopupOpen = false;
        return Add($"[{mode}] Lecture titre {title}, chapitre {chapter} (audio {AudioTrack}, sous-titre {SubLabel()}{(ChainPlaying ? ", Tout lire" : "")}).");
    }

    string SetAudio(int track)
    {
        AudioTrack = track;
        return Add($"[{mode}] Piste audio → {track}.");
    }

    string SetSub(int track)
    {
        SubtitleTrack = track;
        return Add($"[{mode}] Sous-titres → {SubLabel()}.");
    }

    string ResumePlayback()
    {
        PopupOpen = false;
        return Add(PlayingTitle != null ? $"[{mode}] Reprise du titre {PlayingTitle}." : $"[{mode}] Aucune lecture en cours.");
    }

    string OnTitleEnd()
    {
        if (PlayingTitle is not { } title) return Add($"[{mode}] Fin de titre : aucune lecture en cours.");
        PopupOpen = false;
        ResumePoint = null;
        if (endActions.TryGetValue(title, out var action))
        {
            if (action == "repeat") return StartTitle(title, 1, ChainPlaying);
            if (action == "stop") { PlayingTitle = null; return Add($"[{mode}] Fin du titre {title} → arrêt."); }
            if (action == "next" && title < mainCount) return StartTitle(title + 1, 1, ChainPlaying);
            if (action == "menu") { PlayingTitle = null; OpenPage(startPage, log: false); return Add($"[{mode}] Fin du titre {title} → retour au menu « {startPage} »."); }
        }
        if (ChainPlaying && title < mainCount) return StartTitle(title + 1, 1, chain: true);
        if (playback.PlayAll && playback.StartAtFirstTitle && playback.Rewind) return StartTitle(1, 1, chain: true);
        if (!playback.PlayAll || playback.Rewind)
        {
            PlayingTitle = null;
            OpenPage(startPage, log: false);
            return Add($"[{mode}] Fin du titre {title} → retour au menu « {startPage} ».");
        }
        PlayingTitle = null;
        return Add($"[{mode}] Fin du titre {title} → arrêt.");
    }

    string SubLabel() => SubtitleTrack <= 0 ? "désactivés" : SubtitleTrack.ToString();

    string Add(string line)
    {
        if (line.Length > 0) Log.Add(line);
        return line;
    }
}
