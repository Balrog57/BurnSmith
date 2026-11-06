using System.Buffers.Binary;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

/// <summary>
/// La page de réglages à une liste (thèmes Yuhan, <c>LangItemText</c>) : deux pages, audio et sous-titres. Un thème d'essai écrit ici, aux coordonnées de Yuhan, fait tourner
/// ces vérifications partout, même sans modèle commercial ; <c>Birthday 01</c> et <c>01movie</c> n'existent que sur un PC qui les a, et leur absence est dite, pas comptée comme un succès.
/// </summary>
static class SettingsCheck
{
    static MenuContent Content(int audio, int subs, int chapters = 1) => new(1, null, Enumerable.Repeat<string?>(null, chapters).ToList(), null,
        Enumerable.Range(1, audio).Select(i => $"Piste {i}").ToList(), Enumerable.Range(1, subs).Select(i => $"Sous-titre {i}").ToList());

    /// <summary>Un thème au format de Yuhan : le fichier en clair que le programme publié porte aussi. <paramref name="list"/> false = sans liste de langues, comme ceux qui n'en ont pas.</summary>
    static MenuDesign Theme(string root, string name, bool list)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "1920x1080"));
        File.WriteAllBytes(Path.Combine(dir, "1920x1080", "bg.jpg"), MenuRenderer.Sta(() => MenuRenderer.Background(new MenuDesign(), new MenuPage("root", [], []))));
        static string T(string tag, int left, int top, int size) => $"""<{tag} Type="Tahoma" Color="#ffffff" Size="{size}" Bold="true" Left="{left}" Top="{top}"/>""";
        var items = string.Concat(Enumerable.Range(0, 8).Select(i => T($"ItemText{i}", 750, 270 + 75 * i, 36)));
        var setup = !list ? "" : T("SetupAudioText", 850, 400, 54) + T("SetupSubtitleText", 850, 540, 54) + T("ASLangTitleText", 750, 150, 45) + $"<LangItemText>{items}</LangItemText>"
            + """<ButtonNext Left="1227" Top="900" Width="64" Height="64"/><ButtonPre Left="627" Top="900" Width="64" Height="64"/><Return Left="927" Top="900" Width="64" Height="64"/>""";
        File.WriteAllText(Path.Combine(dir, "config.xml"), $"""
            <MenuTemplate Name="Essai"><TemplateBranch Resolution="1920x1080">
            <Menu><Background FileName="bg.jpg"/><ThumbFrame Left="100" Top="100" Width="600" Height="340"/><Thumb Left="10" Top="10" Width="580" Height="320"/>
            {T("TitleText", 1000, 200, 60)}{T("PlayText", 1000, 400, 54)}{T("ScenesText", 1000, 500, 54)}{T("SetupText", 1000, 600, 54)}</Menu>
            <Scene><Background FileName="bg.jpg"/><ThumbFrame><Frame0 Left="300" Top="300" Width="350" Height="219"/></ThumbFrame><Thumb><Thumb0 Left="15" Top="15" Width="320" Height="189"/></Thumb>
            <Return Left="927" Top="900" Width="64" Height="64"/>{T("TitleText", 750, 150, 45)}</Scene>
            <Setup><Background FileName="bg.jpg"/>{setup}</Setup>
            </TemplateBranch></MenuTemplate>
            """);
        return TemplateCatalog.Find(dir) ?? throw new InvalidOperationException($"thème d'essai refusé : {name}");
    }

    /// <summary>Ce qui ne demande ni dvdauthor ni modèle commercial, plus Birthday 01 et 01movie quand ils sont là.</summary>
    public static void Model(Action<string, bool> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_settings_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            TwoPages(check, "thème d'essai", Theme(dir, "THEME01", true));
            if (TemplateCatalog.Find("Birthday 01") is { } birthday) TwoPages(check, "Birthday 01", birthday);
            else Console.WriteLine("skip Birthday 01 absent de ce PC : la mise en page du vrai thème n'est pas vérifiée ici");

            var worst = Content(6, 5);
            var themes = TemplateCatalog.Scan().Usable.Select(f => f.Design).Where(t => MenuBuilder.SettingsIds(t, worst).Contains("settings_audio")).ToList();
            var covered = themes.Select(t => (Theme: t, Pages: MenuBuilder.Build(t, worst).Where(p => p.Id is "settings_audio" or "settings_sub").ToList())).ToList();
            if (themes.Count == 0) Console.WriteLine("skip aucun thème à deux pages sur ce PC : seul le thème d'essai est vérifié");
            else check($"réglages, les {themes.Count} thèmes à deux pages de ce PC, au pire cas : chaque page a la catégorie pour titre, et aucun texte ne recouvre une ligne",
                       covered.All(t => t.Pages.Count == 2 && t.Pages.All(p => Overlaps(p).Count == 0 && p.Ops.OfType<Text>().First().Value == (p.Id == "settings_sub" ? t.Theme.SubtitlesLabel : t.Theme.AudioLabel))));

            OnePage(check, "thème sans liste de langues", Theme(dir, "THEME02", false));
            OnePage(check, "modèle DVD Forge (dans git)", TemplateCatalog.Scan().Usable.Select(f => f.Design).First(d => d.Category == DvdForgeTemplates.Category));
            if (TemplateCatalog.Find("01movie") is { } dvdfab) OnePage(check, "01movie", dvdfab);
            else Console.WriteLine("skip 01movie absent de ce PC : le modèle DVDFab n'est pas vérifié ici (DVD Forge l'est, même chemin)");

            var text = new MenuContent(1, null, [null], null, ["Piste 1", "Piste 2"], ["Sous-titre 1"]);
            var plain = new MenuDesign();
            check("réglages : aucune piste n'est perdue en silence : 3 audio et 2 sous-titres tiennent, 9 et 9 le disent, avec les nombres",
                  MenuBuilder.TracksLeftOut(MenuBuilder.Build(plain, Content(3, 2)), Content(3, 2)) == null
                  && MenuBuilder.TracksLeftOut(MenuBuilder.Build(plain, Content(9, 9)), Content(9, 9)) is { } said && said.Contains("6 pistes audio sur 9") && said.Contains("5 sous-titres sur 9")
                  && MenuBuilder.TracksLeftOut(MenuBuilder.Build(plain, text), text) == null);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>Les textes d'une page, hors lignes de la liste, dont le rectangle touche celui d'une ligne.</summary>
    static List<string> Overlaps(MenuPage p)
    {
        var rows = p.Buttons.Where(b => b.Id.Length == 2 && b.Id[0] is 'a' or 's' && char.IsDigit(b.Id[1])).Select(b => b.Rect).ToList();
        return [.. p.Ops.OfType<Text>().Where(t => !rows.Contains(t.Rect) && rows.Any(r => t.Rect.X < r.X + r.W && r.X < t.Rect.X + t.Rect.W && t.Rect.Y < r.Y + r.H && r.Y < t.Rect.Y + t.Rect.H)).Select(t => t.Value)];
    }

    static void TwoPages(Action<string, bool> check, string name, MenuDesign d)
    {
        void Check(string what, bool ok) => check($"réglages, {name} : {what}", ok);
        var c = Content(3, 2);
        var pages = MenuBuilder.Build(d, c);
        var (audio, sub) = (pages.FirstOrDefault(p => p.Id == "settings_audio"), pages.FirstOrDefault(p => p.Id == "settings_sub"));
        Check("deux pages, settings_audio et settings_sub, plus de page settings", audio != null && sub != null && pages.All(p => p.Id != "settings"));
        if (audio == null || sub == null) return;
        Check("le bouton Réglages de la racine mène à la page audio", pages[0].Buttons.Any(b => b.Id == "settings" && b.Action is { Kind: "page", Page: "settings_audio" }));

        static bool At(MenuPage p, string value, int x, int y) => p.Ops.OfType<Text>().Any(t => t.Value == value && t.Rect.X == x && t.Rect.Y == y);
        static string Rows(MenuPage p, string prefix) => string.Join(' ', p.Buttons.Where(b => b.Id[0] == prefix[0] && char.IsDigit(b.Id[1])).Select(b => $"{b.Id}@{b.Rect.X},{b.Rect.Y}"));
        Box? Rect(MenuPage p, string id) => p.Buttons.FirstOrDefault(b => b.Id == id)?.Rect;
        var six = MenuBuilder.Settings(d, Content(6, 5), null, null, "settings_audio");
        var offPlusFive = MenuBuilder.Settings(d, Content(6, 5), null, null, "settings_sub");
        Check("page audio : la catégorie dans le titre, à 750,150, six lignes à x 750, y 270 à 645 au pas de 75 (le plafond de pistes s'arrête à 6, les lignes 7 et 8 du thème ne servent pas)",
              At(audio, d.AudioLabel, 750, 150) && !audio.Ops.OfType<Text>().Any(t => t.Value == d.SettingsLabel)
              && Rows(six, "a") == "a1@750,270 a2@750,345 a3@750,420 a4@750,495 a5@750,570 a6@750,645");
        Check("rien aux places des libellés du thème (850,400 et 850,540), sur aucune des deux pages",
              !new[] { six, offPlusFive }.Any(p => p.Ops.OfType<Text>().Any(t => t.Rect.X == 850 && t.Rect.Y is 400 or 540)));
        Check("au pire cas (6 audio ; Désactivé + 5 sous-titres) aucun texte ne recouvre une ligne", Overlaps(six).Count == 0 && Overlaps(offPlusFive).Count == 0);
        Check("page audio : Retour à 927,900, « Sous-titres » à 1227,900 et rien à 627,900",
              Rect(audio, "back") == new Box(927, 900, 64, 64) && audio.Buttons.First(b => b.Id == "back").Action.Page == "root"
              && Rect(audio, "next") == new Box(1227, 900, 64, 64) && audio.Buttons.First(b => b.Id == "next").Action.Page == "settings_sub" && Rect(audio, "prev") == null);
        Check("page sous-titres : la catégorie dans le titre, à 750,150, « Désactivé » puis les sous-titres sur les mêmes lignes",
              At(sub, d.SubtitlesLabel, 750, 150) && !sub.Ops.OfType<Text>().Any(t => t.Value == d.SettingsLabel) && Rows(sub, "s") == "s0@750,270 s1@750,345 s2@750,420" && At(sub, d.OffLabel, 750, 270));
        Check("page sous-titres : Retour à 927,900, « Audio » à 627,900 et rien à 1227,900",
              Rect(sub, "back") == new Box(927, 900, 64, 64) && Rect(sub, "prev") == new Box(627, 900, 64, 64) && sub.Buttons.First(b => b.Id == "prev").Action.Page == "settings_audio" && Rect(sub, "next") == null);

        // une catégorie sans choix n'a pas de page : la racine ne doit pas mener à une page absente, et aucun bouton ne doit y conduire
        string Shape(int a, int s)
        {
            var built = MenuBuilder.Build(d, Content(a, s));
            var ids = built.Where(p => MenuDesign.PageKind(p.Id) == "settings").ToList();
            var entry = built[0].Buttons.FirstOrDefault(b => b.Id == "settings")?.Action.Page;
            return string.Join('+', ids.Select(p => p.Id)) + $">{entry}:{ids.SelectMany(p => p.Buttons).Count(b => b.Id is "next" or "prev")}";
        }
        Check("une seule catégorie à choisir : une seule page, celle où mène la racine, sans bouton vers l'autre",
              Shape(2, 0) == "settings_audio>settings_audio:0" && Shape(1, 2) == "settings_sub>settings_sub:0" && Shape(1, 0) == ">:0");

        // le marqueur : le xlet le dessine à (bouton inflaté + MarkerOffset), un DVD dans la page ; les deux doivent tomber au même endroit, à l'écran et hors de la ligne
        var active = MenuBuilder.Settings(d, c, 2, null, "settings_audio_2");
        var row = active.Buttons.First(b => b.Id == "a2");
        var dot = active.Ops.OfType<Fill>().Where(f => f.Rect.W == MenuBuilder.MarkerSize && f.Rect.H == MenuBuilder.MarkerSize).ToList();
        var xlet = row.Rect.X - MenuBuilder.FocusMargin + MenuBuilder.MarkerOffset(d);
        Check("le point de la piste active : le DVD le dessine une fois, là où le xlet le dessine, à gauche de la ligne",
              dot.Count == 1 && dot[0].Rect.X == xlet && dot[0].Rect.Y == row.Rect.Y + (row.Rect.H - MenuBuilder.MarkerSize) / 2 && dot[0].Rect.X + dot[0].Rect.W <= row.Rect.X && dot[0].Rect.X >= 0);
        Check("le Blu-ray ne fige aucun point dans ses pages : c'est le xlet qui le dessine", !audio.Ops.OfType<Fill>().Any(f => f.Rect.W == MenuBuilder.MarkerSize) && !sub.Ops.OfType<Fill>().Any(f => f.Rect.W == MenuBuilder.MarkerSize));

        // un DVD n'a qu'une page par choix d'une catégorie : l'autre choix ne doit rien changer à ses octets, et son propre choix doit les changer
        byte[] Drawn(int? a, int? s, string id) => MenuRenderer.Sta(() => MenuRenderer.Background(d, MenuBuilder.Settings(d, c, a, s, id)));
        Check("la page audio ne dépend que de l'audio, la page sous-titres que du sous-titre (octets du rendu)",
              Drawn(2, 0, "settings_audio_2").SequenceEqual(Drawn(2, 1, "settings_audio_2")) && !Drawn(2, 0, "settings_audio_2").SequenceEqual(Drawn(3, 0, "settings_audio_2"))
              && Drawn(1, 1, "settings_sub_1").SequenceEqual(Drawn(3, 1, "settings_sub_1")) && !Drawn(1, 1, "settings_sub_1").SequenceEqual(Drawn(1, 2, "settings_sub_1")));

        var menu = Path.Combine(Path.GetTempPath(), "bs_settings_menu_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            MenuFiles.WriteBdj(d, pages, menu);
            var lines = File.ReadAllLines(Path.Combine(menu, "menu.txt"));
            bool Button(string page, string id, string action) => lines.Any(l => l.StartsWith($"button {page} {id} ", StringComparison.Ordinal) && l.EndsWith(" " + action, StringComparison.Ordinal));
            // Menu.java compare l'action du bouton au choix en cours : ce sont ces lignes, et le décalage du marqueur, qui font suivre le point
            Check("menu.txt du xlet : les deux pages, la racine vers l'audio, l'action de chaque ligne (celle que suit le point), le décalage du marqueur, les images",
                  lines.Contains("page settings_audio bg_settings_audio.jpg") && lines.Contains("page settings_sub bg_settings_sub.jpg")
                  && Button("root", "settings", "page settings_audio") && Button("settings_audio", "a2", "audio 2") && Button("settings_sub", "s0", "sub 0") && Button("settings_sub", "s2", "sub 2")
                  && Button("settings_audio", "next", "page settings_sub") && Button("settings_sub", "prev", "page settings_audio") && Button("settings_audio", "back", "page root")
                  && lines.Contains($"marker mark.png {MenuBuilder.MarkerSize} {MenuBuilder.MarkerSize} {MenuBuilder.MarkerOffset(d)}") && File.Exists(Path.Combine(menu, "mark.png")));
        }
        finally { if (Directory.Exists(menu)) Directory.Delete(menu, true); }

        // un élément posé sur « Réglages » et un bouton qui y mène suivent les deux pages
        var placed = d.Clone();
        placed.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=100,100,300,90;text=Langues;target=settings"));
        placed.Elements.Add(MenuElement.Parse("kind=text;page=settings;rect=100,800,400,60;text=Note"));
        var withElements = MenuBuilder.Build(placed, c);
        Check("un bouton posé qui vise « settings » mène à la page audio, un élément des Réglages est sur les deux pages et sur celles d'un DVD",
              withElements[0].Buttons.Any(b => b.Id == "u0" && b.Action.Page == "settings_audio")
              && withElements.Where(p => MenuDesign.PageKind(p.Id) == "settings").All(p => p.Ops.OfType<Label>().Any(l => l.Value == "Note") || p.Ops.OfType<Text>().Any(t => t.Value == "Note"))
              && new MenuElement { Page = "settings" }.AppliesTo("settings_sub_1"));
    }

    static void OnePage(Action<string, bool> check, string name, MenuDesign d)
    {
        var pages = MenuBuilder.Build(d, Content(3, 2));
        var mine = pages.Where(p => MenuDesign.PageKind(p.Id) == "settings").ToList();
        check($"réglages, {name} : une seule page settings, deux colonnes, vers laquelle mène un bouton",
              mine.Count == 1 && mine[0].Id == "settings" && mine[0].Buttons.Any(b => b.Id == "a3") && mine[0].Buttons.Any(b => b.Id == "s2")
              && mine[0].Buttons.All(b => b.Id is not ("next" or "prev")) && pages.Any(p => p.Buttons.Any(b => b.Action is { Kind: "page", Page: "settings" })));
    }

    /// <summary>De vrais DVD : 2 audio et 1 sous-titre, les commandes qui mènent aux pages et les relient, relues dans l'IFO ; 6 audio et 5 sous-titres, le nombre de pages.</summary>
    public static void Disc(Action<string, bool> check, bool noDvd)
    {
        if (noDvd) { Console.WriteLine("skip réglages à deux pages sur un vrai DVD (--no-dvd)"); return; }
        var dir = Path.Combine(Path.GetTempPath(), "bs_settings_dvd_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var (film, srt, disc) = (Path.Combine(dir, "film.mkv"), Path.Combine(dir, "film.srt"), Path.Combine(dir, "disc"));
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-f", "lavfi", "-i", "sine=f=440:d=2", "-f", "lavfi", "-i", "sine=f=880:d=2", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", film]);
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nBonjour\n");
            Dvd.Build(new DvdJob([new DiscTitle(film, Audio: [0, 1], Subtitles: [0], External: [new ExternalSubtitle(srt, "fre")])], disc, Theme(dir, "THEME03", true)));

            var vts = File.ReadAllBytes(Path.Combine(disc, "VIDEO_TS", "VTS_01_0.IFO"));
            var vob = File.ReadAllBytes(Path.Combine(disc, "VIDEO_TS", "VTS_01_0.VOB"));
            int U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(o));
            int U32(int o) => (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(o));
            var table = U32(0xD0) * 2048 + U32(U32(0xD0) * 2048 + 12); // les chaînes de menu de la première langue
            int Chain(int n) => table + U32(table + 8 * n + 4);
            // sans la chaîne d'entrée que dvdauthor ajoute, qui n'a pas de cellule : l'indice + 1 est le numéro que donnent les sauts
            var pages = Enumerable.Range(1, U16(table)).Select(Chain).Where(p => vts[p + 3] > 0).ToList();
            string Post(int pgc)
            {
                var commands = pgc + U16(pgc + 0xE4);
                return string.Join(' ', Enumerable.Range(U16(commands), U16(commands + 2)).Select(i => Convert.ToHexString(vts.AsSpan(commands + 8 + 8 * i, 8))));
            }
            List<string> Buttons(int pgc)
            {
                var pci = U32(pgc + U16(pgc + 0xE8) + 8) * 2048 + 0x2D; // le premier paquet de navigation de la page porte ses boutons
                return Enumerable.Range(0, vob[pci + 0x71]).Select(i => Convert.ToHexString(vob.AsSpan(pci + 0x8E + 18 * i + 10, 8))).ToList();
            }
            static string Jump(int page) => $"200400000000{page:X4}";

            // numéros : 1 = racine, 2 et 3 = audio 1 et 2, 4 et 5 = sous-titres désactivés et 1
            check("réglages, vrai DVD : la racine et quatre pages (2 audio + 2 choix de sous-titres), pas leur produit", pages.Count == 5);
            if (pages.Count != 5) return; // la suite lit les pages par leur numéro
            const string toAudio = "20A4000200010002 20A4000200020003", toSub = "20A4000300000004 20A4000300010005"; // si g2 = 1, page 2 ; si g2 = 2, page 3 ; si g3 = 0, page 4 ; si g3 = 1, page 5
            static bool Goes(List<string> buttons) => buttons.Any(b => b.StartsWith("7101000F", StringComparison.Ordinal)); // le bouton part vers ses commandes dans la fin de chaîne
            check("réglages, vrai DVD : le bouton de la racine compare g2 et saute à la page audio du choix en cours, sans regarder le sous-titre",
                  Goes(Buttons(pages[0])) && Post(pages[0]).Contains(toAudio) && !Post(pages[0]).Contains(toSub));
            check("réglages, vrai DVD : « Sous-titres » compare g3 sur chaque page audio, « Audio » compare g2 sur chaque page sous-titres, et Retour mène à la racine",
                  new[] { 2, 3 }.All(n => Post(pages[n - 1]).Contains(toSub)) && new[] { 4, 5 }.All(n => Post(pages[n - 1]).Contains(toAudio)) && Enumerable.Range(2, 4).All(n => Buttons(pages[n - 1]).Contains(Jump(1))));
            check("réglages, vrai DVD : choisir l'audio 2 écrit g2 et le flux 1, et mène à sa page ; choisir un sous-titre écrit g3 et le flux, et mène à sa page",
                  Post(pages[1]).Contains("7100000200020000 5104008100000003") && Post(pages[4]).Contains("7100000300000000 51040000BE000004") && Post(pages[3]).Contains("7100000300010000 51040000C0000005"));
            check("réglages, vrai DVD : un choix garde l'autre registre : la page audio n'écrit jamais g3, celle des sous-titres jamais g2",
                  new[] { 2, 3 }.All(n => !Post(pages[n - 1]).Contains("71000003")) && new[] { 4, 5 }.All(n => !Post(pages[n - 1]).Contains("71000002")));
            check("réglages, vrai DVD : la page audio porte ses 2 lignes, Retour et « Sous-titres » ; celle des sous-titres ses 2 lignes, « Audio » et Retour", Buttons(pages[1]).Count == 4 && Buttons(pages[3]).Count == 4);
            check("réglages, vrai DVD : le disque passe le contrôle de structure", DiscCheck.Folder(disc).Count == 0);

            var (film6, worst) = (Path.Combine(dir, "film6.mkv"), Path.Combine(dir, "worst"));
            var sources = Enumerable.Range(0, 6).SelectMany(i => new[] { "-f", "lavfi", "-i", $"sine=f={300 + 100 * i}:d=2" });
            var streams = Enumerable.Range(1, 6).SelectMany(i => new[] { "-map", $"{i}:a" });
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", .. sources, "-map", "0:v", .. streams, "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", film6]);
            Dvd.Build(new DvdJob([new DiscTitle(film6, Audio: [0, 1, 2, 3, 4, 5], Subtitles: [0, 1, 2, 3, 4], External: [.. new[] { "fre", "eng", "spa", "ger", "ita" }.Select(l => new ExternalSubtitle(srt, l))])], worst, Theme(dir, "THEME04", true)));
            var ifo = File.ReadAllBytes(Path.Combine(worst, "VIDEO_TS", "VTS_01_0.IFO"));
            int Word(int o) => (int)BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(o));
            var chains = Word(0xD0) * 2048 + Word(Word(0xD0) * 2048 + 12);
            var count = Enumerable.Range(1, BinaryPrimitives.ReadUInt16BigEndian(ifo.AsSpan(chains))).Count(n => ifo[chains + Word(chains + 8 * n + 4) + 3] > 0);
            check($"réglages, vrai DVD au pire cas (6 audio, 5 sous-titres) : la racine et 12 pages, pas 72 ({count} chaînes de menu)", count == 13);
        }
        catch (FileNotFoundException e) { check($"réglages, vrai DVD : {e.Message}", false); }
        catch (InvalidOperationException e) { check($"réglages, vrai DVD : {e.Message.Split('\n')[0]}", false); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
