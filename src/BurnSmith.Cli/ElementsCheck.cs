using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

static class ElementsCheck
{
    public static int Run()
    {
        var elFailed = 0;
        void Check(string what, bool ok) { if (!ok) elFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        static bool Throws(Action work) { try { work(); return false; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return true; } }
    
        var elText = MenuElement.Parse("kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;color=#FFE6AE;align=center;bold=1");
        Check("text element: kind, rectangle, text, size, colour, alignment, bold",
              elText.Kind == "text" && elText.Box() == new Box(100, 120, 900, 90) && elText.Text == "Bienvenue" && elText.Size == 54 && elText.Color == "#FFE6AE" && elText.Align == "center" && elText.Bold);
        Check("geometry that leaves the page is pulled back inside it", new MenuElement { X = -50, Y = 2000, W = 5000, H = 10 }.Box() == new Box(0, 1070, 1920, 10));
        Check("page targeting: all, root, scenes, bonus; a misspelt page hits nothing",
              new MenuElement { Page = "all" }.AppliesTo("settings") && new MenuElement { Page = "root" }.AppliesTo("root") && !new MenuElement { Page = "root" }.AppliesTo("scenes1")
              && new MenuElement { Page = "scenes" }.AppliesTo("scenes2") && !new MenuElement { Page = "scenes" }.AppliesTo("bonus1") && !new MenuElement { Page = "roots" }.AppliesTo("root"));
        var strayPages = MenuBuilder.Build(new MenuDesign { Elements = [MenuElement.Parse("kind=button;text=Bonus;target=bonus1;x=100;y=100;w=300;h=80"), MenuElement.Parse("kind=button;text=X;target=my page;x=100;y=300;w=300;h=80")] },
            new MenuContent(1, null, Enumerable.Repeat<string?>(null, 4).ToList()));
        Check("a hand-placed button to a page the disc does not have (no bonus, a mistyped name) leads to the root page",
              strayPages.SelectMany(p => p.Buttons).Where(b => b.Id.StartsWith('u')).ToList() is { Count: > 0 } stray && stray.All(b => b.Action is { Kind: "page", Page: "root" }));
        Check("an unknown kind is refused, so a misspelt element is not silently something else", Throws(() => MenuElement.Parse("kind=vidéo;text=x")));
    
        // the zones the editor offers: what each template draws, nothing more; only what the user wrote reaches the page
        var zoneSeries = new MenuContent(1, null, [], [new TitleEntry("Un", 1, null), new TitleEntry("Deux", 2, null)]);
        var zonePlain = MenuBuilder.Build(new MenuDesign(), zoneSeries);
        Check("BurnSmith's own layout: the title on the menu, the videos' names on their page, no text zone",
              zonePlain[0].Zones.Any(z => z.Key == "title") && zonePlain.First(p => p.Id == "scenes1").Zones.Select(z => z.Key).SequenceEqual(["name:1", "name:2"]) && !zonePlain.SelectMany(p => p.Zones).Any(z => z.Key.StartsWith("slot:")));
        if (TemplateCatalog.Find("Streaming") is { } streaming && TemplateCatalog.Find("Birthday1") is { } noZones)
        {
            var zd = streaming.Clone();
            var blank = MenuBuilder.Build(zd, zoneSeries)[0];
            Check("Streaming declares its information line and four text zones", blank.Zones.Count(z => z.Key.StartsWith("slot:")) == 5 && blank.Zones.Any(z => z.Key == "poster"));
            Check("an empty text zone draws nothing; the information line says what the disc holds", blank.Ops.OfType<Text>().Count(t => t.Rect.X == 536 && t.Rect.Y >= 298) == 0 && blank.Ops.OfType<Text>().Any(t => t.Value.StartsWith("2 ")));
            zd.TextZones["overview1"] = "2018 | Action";
            zd.TextZones["overview4"] = "Trente ans après";
            var filled = MenuBuilder.Build(zd, zoneSeries)[0].Ops.OfType<Text>().ToList();
            Check("what the user writes is drawn in the template's zone, on several lines where it allows them",
                  filled.Any(t => t.Value == "2018 | Action" && !t.Wrap && t.Rect.Y == 298) && filled.Any(t => t.Value == "Trente ans après" && t.Wrap && t.Rect.Y == 488));
            var told = streaming.Clone();
            told.Title = "Ma série";
            var season5 = new MenuContent(1, null, [], [.. Enumerable.Range(1, 5).Select(i => new TitleEntry(i == 2 ? "E02 - Déjà" : $"Épisode {i} · Nom {i}", i, null, i == 1 ? "Il était une fois" : null))]);
            // a Blu-ray writes the summary of the episode under the remote, under its word, and leaves the first two zones to the user; a DVD cannot, and gives every episode its row
            var lit5 = MenuBuilder.Build(told, season5)[0];
            Check("Streaming on a Blu-ray: the summary comes with its episode's selected state, under \"Overview:\", in the template's last two zones; the first two stay the user's",
                  lit5.Buttons.First(b => b.Id == "e1").FocusTexts is [{ Value: "Il était une fois", Wrap: true, Rect: { X: 536, Y: 488 } }] && lit5.Buttons.First(b => b.Id == "e2").FocusTexts == null
                  && lit5.Ops.OfType<Text>().Any(t => t.Value == "Overview:" && t.Bold && t.Rect.Y == 432) && !lit5.Ops.OfType<Text>().Any(t => t.Value == "Il était une fois")
                  && lit5.Zones.Count(z => z.Key.StartsWith("slot:overview")) == 2
                  && MenuRenderer.FocusBox(lit5.Buttons.First(b => b.Id == "e1")) is { Y: 488 } whole && whole.X + whole.W == 1866 && MenuRenderer.FocusBox(lit5.Buttons.First(b => b.Id == "e2")).Y > 700);
            var told5 = MenuBuilder.Build(told, season5 with { PlainFocus = true });
            var first = told5[0].Ops.OfType<Text>().ToList();
            Check("Streaming on a DVD: episodes numbered E01 - Name, a summary under its episode's name in place of the text zones, the disc's title on every episode page",
                  first.Any(t => t.Value == "E01 - Nom 1" && t.Bold && t.Rect.X == 536 && t.Rect.Y == 298) && first.Any(t => t.Value == "Il était une fois" && t.Wrap && t.Rect.Y == 332)
                  && first.Count(t => t.Value == "E02 - Déjà") == 2 && !told5[0].Zones.Any(z => z.Key.StartsWith("slot:overview"))
                  && told5.First(p => p.Id == "scenes2").Ops.OfType<Text>().Any(t => t.Value == "Ma série") && told5.First(p => p.Id == "scenes2").Zones.Any(z => z.Key == "slot:overview1"));
            Check("a template that declares no text zone offers none", !MenuBuilder.Build(noZones.Clone(), zoneSeries).SelectMany(p => p.Zones).Any(z => z.Key.StartsWith("slot:")));
        }
    
        // DVDFab's DVD menus are words: written from the left of their zone, and the buttons BurnSmith adds are words beside them, not capsules
        // the alignment marks of the editor (SmartGuides): a short pull onto an edge, a middle or an even gap, and the lines that say so
        var (landed, guideMarks) = SmartGuides.Move(new Box(304, 616, 420, 90), [new Box(300, 300, 420, 90), new Box(300, 460, 420, 90)]);
        Check("alignment marks: a third button let go 4 px off lands on the column's edge and at the gap of the two above, with a line on that edge and a mark in each gap",
              landed == new Box(300, 620, 420, 90) && guideMarks.Any(g => g is { Vertical: true, At: 300, Spacing: false }) && guideMarks.Count(g => g.Spacing) == 2);
        Check("alignment marks: with nothing within reach the element stays where it is and no line is drawn",
              SmartGuides.Move(new Box(1000, 900, 100, 50), [new Box(300, 300, 420, 90)]) is var (free, noMarks) && free == new Box(1000, 900, 100, 50) && noMarks.Count == 0);
        Check("alignment marks: a corner dragged near the page's middle meets it; the edges that do not move are left alone",
              SmartGuides.Resize(new Box(100, 100, 857, 200), false, false, []) is var (grown, sized) && grown == new Box(100, 100, 860, 200) && sized.Any(g => g is { Vertical: true, At: 960 }));
    
        // traced buttons stack the same colour at several opacities (tools\vectorize-buttons.py): the opaque shape must not take the faint one's opacity
        var twoReds = MenuRenderer.Sta(() => MenuRenderer.Raster("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="10" height="10" fill="#ff0000" fill-opacity="0.2"/><rect x="10" width="10" height="10" fill="#ff0000"/></svg>""", null, 20, 10)).Px;
        Check("SVG: a colour drawn see-through, then opaque, is opaque the second time (alpha of each half)", twoReds[4 * 5 + 3] is > 40 and < 62 && twoReds[4 * 15 + 3] == 255);
    
        // a button picture and its traced twin: the picture at its own size (the exact copy), the drawing once it is enlarged
        var twinDir = Directory.CreateTempSubdirectory("burnsmith-twin").FullName;
        try
        {
            var twinPng = Path.Combine(twinDir, "b.png");
            MenuRenderer.Sta(() =>
            {
                var px = new byte[40 * 20 * 4];
                Array.Fill(px, (byte)255);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(40, 20, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 160)));
                using var f = File.Create(twinPng);
                enc.Save(f);
                return 0;
            });
            File.WriteAllText(Path.ChangeExtension(twinPng, ".svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="#ff0000"/></svg>""");
            Check("traced twin: a picture with its drawing beside it has a twin, the drawing itself none", MenuRenderer.Traced(twinPng)?.EndsWith("b.svg") == true && MenuRenderer.Traced(Path.ChangeExtension(twinPng, ".svg")) == null);
            var (same, large) = MenuRenderer.Sta(() => (MenuRenderer.Raster(twinPng, null, 40, 20).Px, MenuRenderer.Raster(twinPng, null, 80, 40).Px));
            Check("traced twin: the page shows the red drawing, not the white picture, at its size and enlarged", same[0] == 0 && same[2] == 255 && large[0] == 0 && large[1] == 0 && large[2] == 255);
        }
        finally { Directory.Delete(twinDir, true); }
    
        // BurnSmith's two designs made for a disc, as anyone gets them: Métal (Bright) and Saison (Cobra Kai)
        var metal = BuiltInTemplates.All().First(t => t.Name == "Métal");
        var taken = new MenuDesign { Elements = { new MenuElement { Kind = "text", Text = "à moi" } } };
        taken.TakeLook(metal);
        Check("Métal chosen in the gallery comes whole: no preview window, its two fades on the main page and its veils, under the user's own text",
              !taken.ShowPreview && taken.Elements.Count == 5 && taken.Elements[^1].Text == "à moi" && taken.Elements.Count(e => e.OfLook && e.Page == "root" && MenuRenderer.IsSvgText(e.Image ?? "")) == 2);
        taken.TakeLook(new MenuDesign());
        Check("another look takes Métal's fades and veils away and leaves the user's text", taken.Elements is [{ Text: "à moi" }] && taken.ShowPreview);
        var animatedTemplate = new MenuDesign { FocusFrames = ["a.png", "b.png"] };
        taken.TakeLook(animatedTemplate);
        var edited = taken.Clone();
        if (edited.FocusFrames is { } frames) frames[0] = "changed.png";
        Check("applying an animated template carries independent frames into the editor",
              taken.FocusFrames is ["a.png", "b.png"] && !ReferenceEquals(taken.FocusFrames, animatedTemplate.FocusFrames)
              && edited.FocusFrames is ["changed.png", "b.png"]);
        taken.TakeLook(new MenuDesign());
        Check("applying a static template clears the previous animation", taken.FocusFrames == null);
    
        var fadePage = MenuBuilder.Build(metal.Clone(), new MenuContent(1, null, [null, null], null, ["Français"], [])).First(p => p.Id == "root");
        var fadePx = MenuRenderer.Sta(() => new System.Windows.Media.Imaging.JpegBitmapDecoder(new MemoryStream(MenuRenderer.Background(new MenuDesign { Top = "#FFFFFF", Bottom = "#FFFFFF", Elements = metal.Elements, Name = "" }, fadePage with { Ops = fadePage.Ops.Where(o => o is Image).ToList() })),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0]);
        int Grey(int y) { var one = new byte[4]; new System.Windows.Media.Imaging.FormatConvertedBitmap(fadePx, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(960, y, 1, 1), one, 4, 0); return one[1]; }
        Check($"Métal's fades: the main page is dark at its top and bottom edges and untouched in the middle ({Grey(4)}, {Grey(540)}, {Grey(1075)})", Grey(4) < 60 && Grey(540) > 200 && Grey(1075) < 60);
        if (TemplateCatalog.Find("Saison") is { } saison)
            Check("Saison: listed with BurnSmith's own designs, laid out by the Streaming template, its background blurred",
                  saison.Category == TemplateLabel.Own && saison.TemplateDir != null && saison.BackgroundBlur > 0 && TemplateCatalog.Scan().Usable.TakeWhile(f => f.Design.Category == TemplateLabel.Own).Any(f => f.Design.Name == "Saison"));
        else Console.WriteLine("skip Saison: the Streaming template is not on this PC");
    
        // each template against the program it comes from (2026-11-11): the three faults that were plain to see
        if (TemplateCatalog.Find("standard (16-9)") is { } leawo && TemplateCatalog.Find("Natural 18") is { } yuhan18 && TemplateCatalog.Find("blue 16-9") is { } styler)
        {
            var disc = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 8).ToList(), null, ["Français", "English"], ["Français"]);
            var lw = MenuBuilder.Build(leawo.Clone(), disc);
            var lwTitle = lw[0].Ops.OfType<Text>().First();
            Check("Leawo standard: the program's own layout — the title ends at 783 (Left 460 + 327 - 4), the frame at 819,110 is drawn over the preview, three thumbnails a page",
                  lwTitle.Align == "right" && lwTitle.Rect.X + lwTitle.Rect.W == 783 && lw[0].Ops.OfType<Image>().Last().Rect == new Box(819, 110, 751, 496)
                  && lw[0].Ops.FindIndex(o => o is Framed) < lw[0].Ops.FindLastIndex(o => o is Image) && lw.First(p => p.Id == "scenes1").Ops.OfType<Framed>().Count() == 3);
            Check("Yuhan theme 18: four thumbnails a page, although its file names two of its four frames twice",
                  MenuBuilder.Build(yuhan18.Clone(), disc).First(p => p.Id == "scenes1").Ops.OfType<Framed>().Count() == 4);
            Check("DVDStyler blue 16-9: the background covers the whole page, as the program stretches it",
                  MenuBuilder.Build(styler.Clone(), disc)[0].Ops.OfType<Image>().First().Rect == new Box(0, 0, 1920, 1080));
            if (TemplateCatalog.Find("Autumn 4-3") is { } framed && TemplateCatalog.Find("ocean 4-3") is { } oval)
            {
                var fp = MenuBuilder.Build(framed.Clone(), disc).First(p => p.Id == "scenes1");
                Check("DVDStyler Autumn 4-3: each of the four thumbnails under the frame the template draws for it, home and arrow as its own shapes (no capsule), no name where there is no room for one",
                      fp.Ops.OfType<Framed>().Count() == 4 && fp.Ops.OfType<Image>().Count(i => MenuRenderer.IsSvgText(i.Path)) == 6 && !fp.Ops.OfType<Fill>().Any() && !fp.Ops.OfType<Text>().Any());
                Check("DVDStyler ocean 4-3: each thumbnail is cut to the ellipse of its button",
                      MenuBuilder.Build(oval.Clone(), disc).First(p => p.Id == "scenes1").Ops.OfType<Framed>().ToList() is { Count: 4 } cut && cut.All(f => f.Mask != null && MenuRenderer.IsSvgText(f.Mask)));
            }
        }
    
        // by kind: DVDFab's Blu-ray template of the same name (picture buttons) comes first in the catalogue when the PC has it
        if (TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.Name == "The Fleeting Meteor" && d.Kind == TemplateKind.Dvd) is { } meteor
            && TemplateCatalog.Find("04baby") is { } baby)
        {
            var wordDisc = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 8).ToList(), null, ["Français", "English"], ["Français"]);
            var row = MenuBuilder.Build(meteor.Clone(), wordDisc)[0];
            var rb = row.Buttons;
            Check("The Fleeting Meteor: Play, Scenes and Settings are three words side by side on the banner's solid part, none over another, no capsule",
                  rb.Select(b => b.Id).SequenceEqual(["play", "scenes", "settings"]) && rb.All(b => b.Rect.Y == rb[0].Rect.Y) && !row.Ops.OfType<Fill>().Any()
                  && rb.Zip(rb.Skip(1), (l, r) => l.Rect.X + l.Rect.W < r.Rect.X).All(apart => apart) && rb[0].Rect.X > 540 && rb[^1].Rect.X + rb[^1].Rect.W < 1350);
            var column = MenuBuilder.Build(baby.Clone(), wordDisc)[0];
            var (cb, cw) = (column.Buttons, column.Ops.OfType<Text>().TakeLast(3).ToList());
            Check("04baby: the words start at the left of their zone, and Settings follows Scenes down the column at the template's step",
                  cw.All(t => t.Align == "left" && t.Rect.X == cw[0].Rect.X) && cw[2].Value == "Settings" && !column.Ops.OfType<Fill>().Any()
                  && cb.Count == 3 && cb[1].Rect.Y > cb[0].Rect.Y && cb[2].Rect.Y - cb[1].Rect.Y == cb[1].Rect.Y - cb[0].Rect.Y);
        }
    
        var elDesign = TemplateCatalog.Default().Clone();
        var elContent = new MenuContent(1, null, ["a", "b", "c"], null, ["Français", "English"], ["Français"]);
        var elBefore = MenuBuilder.Build(elDesign, elContent);
        elDesign.Elements.Add(MenuElement.Parse("kind=text;page=root;rect=10,10,400,60;text=Salut;size=40"));
        elDesign.Elements.Add(MenuElement.Parse("kind=box;page=scenes;rect=0,700,1920,380;fill=#000000;opacity=50;border=#FFC83D;borderw=4"));
        elDesign.Elements.Add(MenuElement.Parse("kind=text;page=credits;rect=10,10,400,60;text=Fin"));
        elDesign.Elements.Add(MenuElement.Parse("kind=image;page=all;rect=10,10,100,100;file=Z:\\absent\\logo.png"));
        var elPages = MenuBuilder.Build(elDesign, elContent);
    
        Check("elements change the drawing, not the pages", elPages.Select(p => p.Id).SequenceEqual(elBefore.Select(p => p.Id)));
        Check("a text on the root page is drawn there and nowhere else",
              elPages.First(p => p.Id == "root").Ops.OfType<Text>().Any(t => t.Value == "Salut") && !elPages.First(p => p.Id == "scenes1").Ops.OfType<Text>().Any(t => t.Value == "Salut"));
        var elGrids = elPages.Where(p => p.Id.StartsWith("scenes")).ToList();
        Check("a box on the scenes pages: one fill and one border on each, in its own colours",
              elGrids.Count > 0 && elGrids.All(p => p.Ops.OfType<Fill>().Count(f => f.Color == "#000000" && Math.Abs(f.Opacity - 0.5) < 1e-9) == 1
                                             && p.Ops.OfType<Border>().Count(b => b.Color == "#FFC83D" && b.Width == 4) == 1));
        Check("an element for a page the disc has not got is simply not drawn", !elPages.Any(p => p.Ops.OfType<Text>().Any(t => t.Value == "Fin")));
        Check("an image whose file is missing draws nothing (the page still builds)", !elPages.Any(p => p.Ops.OfType<Image>().Any(i => i.Path.Contains("logo.png"))));
        Check("the elements are copied with the design, not shared",
              elDesign.Clone().Elements.Count == elDesign.Elements.Count && !ReferenceEquals(elDesign.Clone().Elements[0], elDesign.Elements[0]));
    
        var elUnder = elDesign.Clone();
        elUnder.Elements.Add(MenuElement.Parse("kind=box;page=root;rect=0,700,1920,380;fill=#123456;opacity=100;behind=1"));
        var elOver = elDesign.Clone();
        elOver.Elements.Add(MenuElement.Parse("kind=box;page=root;rect=0,700,1920,380;fill=#123456;opacity=100"));
        static int Panel(MenuDesign dd, MenuContent cc) => MenuBuilder.Build(dd, cc).First(p => p.Id == "root").Ops.FindIndex(o => o is Fill { Color: "#123456" });
        Check("a box told to stay behind the buttons is drawn before them, otherwise over them", Panel(elUnder, elContent) < Panel(elOver, elContent));
    
        // a hand-placed button is a real menu button, not a drawing
        var elButton = elDesign.Clone();
        elButton.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=120,600,300,100;text=Lire;target=movie"));
        var elButtonPage = MenuBuilder.Build(elButton, elContent).First(p => p.Id == "root");
        var elButtonOne = elButtonPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
        Check("un élément bouton devient un vrai bouton du menu, avec son libellé et l'action choisie",
              elButtonOne.Count == 1 && elButtonOne[0].Action.Kind == "play" && elButtonPage.Ops.OfType<Label>().Any(l => l.Value == "Lire"));
    
        elButton.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=520,600,300,100;text=Chapitres;target=scenes1"));
        var elTwoPage = MenuBuilder.Build(elButton, elContent).First(p => p.Id == "root");
        var elTwo = elTwoPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
        Check("deux boutons posés côte à côte se pointent l'un l'autre à la télécommande, et la page visée est celle demandée",
              elTwo.Count == 2 && elTwo[0].Right == elTwo[1].Id && elTwo[1].Left == elTwo[0].Id && elTwo[1].Action is { Kind: "page", Page: "scenes1" });
    
        // what "Enregistrer le modèle" writes must come back whole: the elements and the hand-placed buttons are part of the template
        var elJson = System.Text.Json.JsonSerializer.Serialize(elButton);
        var elBack = System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(elJson)!;
        Check("un modèle enregistré revient entier (éléments, bouton ajouté et sa cible)",
              elBack.Elements.Count == elButton.Elements.Count && elBack.Elements[^1] is { Kind: "button", Target: "scenes1", Text: "Chapitres" }
              && elBack.Elements[0].X == elButton.Elements[0].X && elBack.Title == elButton.Title);
    
        // la page de scènes en liste : les lignes se posent aux rectangles que le modèle donne
        var theme = TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(x => x.Kind == TemplateKind.Theme);
        if (theme == null) Console.WriteLine("ok   (aucun thème installé : vérification du mode liste sautée)");
        else
        {
            var ten = new MenuContent(1, null, [], Enumerable.Range(1, 10).Select(i => new TitleEntry($"Episode {i}", i, null)).ToList(), ["Français AC-3"], []);
            var grid = MenuBuilder.Build(theme.Clone(), ten).First(q => q.Id == "scenes1");
            var listed = theme.Clone();
            listed.SceneList = true;
            var listPage = MenuBuilder.Build(listed, ten).First(q => q.Id == "scenes1");
            Check("le mode liste tient dix lignes là où la grille n'en tient que quelques-unes, aux positions du thème",
                  listPage.Buttons.Count(b => b.Id.StartsWith("e")) == 10 && grid.Buttons.Count(b => b.Id.StartsWith("e")) < 10
                  && listPage.Buttons.Where(b => b.Id.StartsWith("e")).Select(b => b.Rect.X).Distinct().Count() == 1);
        }
    
        // a picture for one kind of page (the scenes pages): those are drawn from it, the root page keeps the design's own background; copies and saved models keep it
        var pbPng = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "burnsmith-pagebg.png"));
        var pbPx = new byte[16 * 9 * 4];
        for (var i = 0; i < pbPx.Length; i += 4) { pbPx[i + 2] = 255; pbPx[i + 3] = 255; } // opaque red (BGRA)
        var pbEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        pbEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(16, 9, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pbPx, 64)));
        using (var pbFile = File.Create(pbPng)) pbEnc.Save(pbFile);
        var pbDesign = new MenuDesign();
        pbDesign.PageBackgrounds["scenes"] = pbPng;
        (int R, int B) Corner(MenuPage page) => MenuRenderer.Sta(() =>
        {
            var jpeg = new System.Windows.Media.Imaging.JpegBitmapDecoder(new MemoryStream(MenuRenderer.Background(pbDesign, page)), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var px = new byte[4];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(jpeg.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(3, 3, 1, 1), px, 4, 0);
            return (px[2], px[0]);
        });
        var pbPages = MenuBuilder.Build(pbDesign, new MenuContent(1, null, Enumerable.Repeat<string?>(null, 3).ToList(), null, ["Français", "English"], ["Français"])); // no thumbnails: nothing to load but the backgrounds
        var (pbRoot, pbScenes) = (Corner(pbPages.First(p => p.Id == "root")), Corner(pbPages.First(p => p.Id == "scenes1")));
        Check("a page with its own background is drawn from it (red), the root page keeps the gradient (blue)", pbScenes.R > 120 && pbScenes.B < 60 && pbRoot.B > pbRoot.R);
        Check("the kind of a page is its id without the number or the choice", MenuDesign.PageKind("scenes12") == "scenes" && MenuDesign.PageKind("settings_1_2") == "settings" && MenuDesign.PageKind("root") == "root");
        Check("a missing picture is no background (the page still builds)", new MenuDesign { PageBackgrounds = { ["root"] = "Z:\absent.png" } }.PageBackground("root") == null);
        var pbCopy = pbDesign.Clone();
        pbCopy.PageBackgrounds.Remove("scenes");
        Check("the page backgrounds are copied with the design, not shared", pbDesign.PageBackgrounds.Count == 1 && pbCopy.PageBackgrounds.Count == 0);
        Check("a saved model keeps its page backgrounds", System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(System.Text.Json.JsonSerializer.Serialize(pbDesign))!.PageBackgrounds["scenes"] == pbPng);
    
        // a button drawn from a library picture, selected with its own picture (any two library pictures stand in for a button and its "sub" twin)
        var elShown = ElementLibrary.Backgrounds().Where(x => x.Source == ElementLibrary.Open).Take(2).ToList();
        Check("la bibliothèque livre des images pour les essais qui suivent", elShown.Count == 2);
        if (elShown.Count == 2)
        {
            string normalPic = elShown[0].Path, focusPic = elShown[1].Path;
            var elPic = elDesign.Clone();
            elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=120,600,300,100;text=Lire;target=movie;file={normalPic};focus={focusPic}"));
            elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=520,600,300,100;target=scenes1;file={normalPic}"));
            elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=920,600,300,100;text=Absent;file=Z:\\absent\\bouton.png;focus={focusPic}"));
            var elPicPage = MenuBuilder.Build(elPic, elContent).First(p => p.Id == "root");
            var elPics = elPicPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
            Check("un bouton image : son image occupe son rectangle et son libellé est écrit dessus",
                  elPicPage.Ops.OfType<Image>().Any(i => i.Path == normalPic && i.Rect == new Box(120, 600, 300, 100)) && elPicPage.Ops.OfType<Label>().Any(l => l.Value == "Lire"));
            Check("son image « sub » devient l'image de sélection Blu-ray et la surbrillance DVD",
                  elPics.Count == 3 && elPics[0].FocusImage == focusPic && elPics[0].DvdHighlightImage == focusPic);
            Check("le libellé d'un bouton image est réécrit sur son image de sélection, qui sinon le cacherait",
                  MenuRenderer.Sta(() => !MenuRenderer.Focus(elPic, elPicPage, elPics[0]).SequenceEqual(MenuRenderer.Focus(elPic, elPics[0]))));
            Check("sans image de sélection : le cadre du thème (rien n'est inventé)", elPics[1].FocusImage == null && elPics[1].DvdHighlightImage == null);
            Check("une image de bouton absente : le bouton garde le look du thème et ne vise pas une image de sélection absente",
                  elPics[2].FocusImage == null && elPicPage.Ops.OfType<Label>().Any(l => l.Value == "Absent") && !elPicPage.Ops.OfType<Image>().Any(i => i.Path.Contains("bouton.png")));
    
            // its own look, without touching the design's
            var elStyled = elDesign.Clone();
            elStyled.ButtonStyle = "Text";
            elStyled.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=120,600,300,100;text=Carte;look=Card"));
            elStyled.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=520,600,300,100;text=Nu;look=Inconnu"));
            var elStyledOps = MenuBuilder.Build(elStyled, elContent).First(p => p.Id == "root").Ops;
            Check("un bouton peut avoir son look (Cartes) alors que le thème est en « Texte seul » ; un look inconnu est ignoré",
                  elStyledOps.OfType<Fill>().Any(f => f.Rect == new Box(120, 600, 300, 100) && f.Color == elStyled.CardFill) && !elStyledOps.OfType<Fill>().Any(f => f.Rect == new Box(520, 600, 300, 100)) && elStyled.ButtonStyle == "Text");
    
            var elPicBack = System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(System.Text.Json.JsonSerializer.Serialize(elPic))!;
            Check("un modèle enregistré garde l'image du bouton et celle de sa sélection", elPicBack.Elements[^3] is { Image: { } i0, Focus: { } f0 } && i0 == normalPic && f0 == focusPic);
    
            // the button library: BurnSmith's looks first, the user's imports, the template pictures; imports are copied and can be removed
            var elButtons = ElementLibrary.Buttons();
            Check("la bibliothèque de boutons commence par les looks de BurnSmith", elButtons.Take(ElementLibrary.ButtonStyles.Length).All(b => b.Source == ElementLibrary.Styles && b.Style != null && b.Normal == null));
            Check("chaque bouton image existe, et sa sélection aussi", elButtons.Where(b => b.Style == null).All(b => b.Normal != null && File.Exists(b.Normal) && (b.Focus == null || File.Exists(b.Focus))));
            var houseButtons = elButtons.Where(b => b.Source.StartsWith(TemplateLabel.Own + " · ")).ToList();
            Check($"les boutons de BurnSmith ({houseButtons.Count}) sont listés avec leur sélection, et se lisent", houseButtons.Count >= 80
                  && houseButtons.All(b => b.Focus != null && MenuRenderer.Size(b.Normal!) != null && MenuRenderer.Size(b.Focus) != null));
            var paperTag = houseButtons.FirstOrDefault(b => b.Source.EndsWith("Papier") && b.Name == "Étiquette")?.Normal;
            var inkTag = houseButtons.FirstOrDefault(b => b.Source.EndsWith("Encre") && b.Name == "Rectangle")?.Normal;
            Check("un libellé sur un bouton image se lit : foncé sur l'étiquette claire, la couleur du modèle gardée sur le bouton sombre",
                  paperTag != null && inkTag != null && MenuRenderer.InkOn(paperTag, "#FFF4DC") == "#141414" && MenuRenderer.InkOn(inkTag, "#FFF4DC") == null
                  && MenuElement.Parse($"kind=button;rect=0,0,400,120;text=Chapitres;file={paperTag}").Ops(new MenuDesign { TextColor = "#FFF4DC" }).OfType<Label>().Single().Ink == "#141414");
            Check("les modèles se rangent par usage, Original quand le nom ne dit rien",
                  TemplateUsage.Of("UHD Birthday1") == "Anniversaire" && TemplateUsage.Of("01movie") == "Film" && TemplateUsage.Of("Saison") == "Série"
                  && TemplateUsage.Of("Natural 18") == "Voyage et nature" && TemplateUsage.Of("blue 16-9") == TemplateUsage.Original && TemplateUsage.Of("Wedding2") == "Mariage");
            {
                var userPicks = TemplatePicks.Dir;
                TemplatePicks.Dir = Path.Combine(Path.GetTempPath(), "bs_picks_" + Guid.NewGuid().ToString("N")[..8]); // never the user's own stars
                try
                {
                    TemplateCatalog.Found wedding = new(new MenuDesign { Name = "Wedding2", Category = "Libres" }, null), autumn = new(new MenuDesign { Name = "Autumn 4-3", Category = "Libres" }, null),
                        season = new(new MenuDesign { Name = "Saison", Category = TemplateLabel.Own }, null);
                    Check("la recherche de la galerie trouve le nom ou l'usage, sans casse ni accents",
                          TemplatePicks.Matches(wedding, "MARIAGE") && TemplatePicks.Matches(season, "serie") && TemplatePicks.Matches(autumn, "autu") && TemplatePicks.Matches(autumn, " ")
                          && !TemplatePicks.Matches(wedding, "série"));
                    TemplatePicks.Star("Autumn 16-9", true);
                    TemplatePicks.Dir = TemplatePicks.Dir; // read back from the file, as the next start does
                    var starredFirst = TemplatePicks.Sorted([wedding, season, autumn]).First() == autumn && autumn.Favorite && !wedding.Favorite;
                    TemplatePicks.Star("Autumn 4-3", false);
                    Check("une étoile couvre les deux formes d'un modèle, le met en tête, se garde et s'enlève", starredFirst && !autumn.Favorite && TemplatePicks.Favorites.Count == 0);
                    foreach (var n in new[] { "A", "B", "C", "D", "E", "F", "G", "C 4-3" }) TemplatePicks.Used(n);
                    Check($"« Récemment utilisés » : les {TemplatePicks.RecentCount} derniers, le plus récent d'abord, sans doublon",
                          TemplatePicks.Recent() is ["C", "G", "F", "E", "D", "B"]);
                }
                finally { Tools.TryDelete(TemplatePicks.Dir); TemplatePicks.Dir = userPicks; }
            }
            MenuDesign wide169 = new() { Name = "Autumn 16-9", Category = "Libres" }, narrow43 = new() { Name = "Autumn 4-3", Category = "Libres" }, alone = new() { Name = "Sun 4-3", Category = "Libres" };
            Check("un modèle en 16:9 et en 4:3 n'en fait qu'un, le choix de l'écran prend sa version ; un modèle d'une seule forme reste",
                  TemplateShape.Base("Autumn 4-3") == "Autumn" && TemplateShape.Base("business (16-9-2)") == "business 2" && TemplateShape.Base("business (4-3)") == "business"
                  && TemplateShape.IsNarrow("ocean 4-3") && !TemplateShape.IsNarrow("business (16-9-2)") && TemplateShape.Base("Birthday1") == "Birthday1"
                  && TemplateShape.Pick(wide169, true, [wide169, narrow43, alone]) == narrow43 && TemplateShape.Pick(narrow43, false, [wide169, narrow43]) == wide169
                  && TemplateShape.Pick(alone, false, [wide169, narrow43, alone]) == alone);
            // the template a disc starts from: the user's last choice, else one for its videos (never Birthday1 any more)
            Check("sans modèle choisi : un film, une saison SxxEyy, plusieurs vidéos ont chacun le leur",
                  TemplateCatalog.Suggested([]) == "Salle obscure" && TemplateCatalog.Suggested([@"D:\Films\Bright.2017.mkv"]) == "Salle obscure"
                  && TemplateCatalog.Suggested([@"D:\Cobra.Kai.S01E01.mkv", @"D:\Cobra Kai - s01e10 - Fin.mkv"]) == "Saison"
                  && TemplateCatalog.Suggested([@"D:\Cobra.Kai.S01E01.mkv", @"D:\Vacances 2024.mkv"]) == "Nuit d'encre" && TemplateCatalog.Suggested([@"D:\BS01E01X.mkv", @"D:\a.mkv"]) == "Nuit d'encre");
            var choiceRoot = Directory.CreateTempSubdirectory("burnsmith-last-template-").FullName;
            var choiceFile = Path.Combine(choiceRoot, "profile", "last-template.txt");
            try
            {
                var (film, series, many) = (TemplateCatalog.DefaultFor(["film.mkv"], choiceFile), TemplateCatalog.DefaultFor(["x.S01E01.mkv", "x.S01E02.mkv"], choiceFile), TemplateCatalog.DefaultFor(["a.mkv", "b.mkv"], choiceFile));
                Check($"rien de choisi encore : {film.Name}, {series.Name}, {many.Name}", film.Name == "Salle obscure" && series.Name is "Saison" or "Séries" && many.Name == "Nuit d'encre");
                TemplateCatalog.RememberChoice("Métal", choiceFile);
                Check("le dernier modèle choisi passe avant les vidéos", TemplateCatalog.DefaultFor(["x.S01E01.mkv", "x.S01E02.mkv"], choiceFile).Name == "Métal");
                TemplateCatalog.RememberChoice("Modèle disparu", choiceFile);
                Check("un dernier modèle qui n'existe plus est ignoré", TemplateCatalog.DefaultFor(["a.mkv", "b.mkv"], choiceFile).Name == "Nuit d'encre");
            }
            finally { Directory.Delete(choiceRoot, recursive: true); }
            Check("seuls les boutons de l'utilisateur peuvent être retirés", elButtons.Where(b => b.Removable).All(b => b.Source == ElementLibrary.MyButtons));
            var imported = ElementLibrary.ImportButton(normalPic, focusPic);
            try
            {
                var listed = ElementLibrary.Buttons().FirstOrDefault(b => b.Normal == imported.Normal);
                Check("un bouton importé est copié avec sa sélection, et listé parmi les boutons de l'utilisateur",
                      listed is { Removable: true, Source: ElementLibrary.MyButtons } && listed.Focus == imported.Focus && imported.Normal != normalPic && File.Exists(imported.Normal!) && File.Exists(imported.Focus!));
            }
            finally { ElementLibrary.RemoveButton(imported); }
            Check("retirer un bouton importé supprime l'image et sa sélection", !File.Exists(imported.Normal!) && !File.Exists(imported.Focus!));
            Check("un bouton de modèle n'est pas à la bibliothèque de le supprimer", Throws(() => ElementLibrary.RemoveButton(new ElementLibrary.ButtonItem("x", "Thème", normalPic))));
            Check("un fichier qui n'est pas une image est refusé à l'import", Throws(() => ElementLibrary.ImportButton(Path.GetFullPath("check-elements.txt"))));
    
            // the user's own drawings: an SVG is a button like a PNG, and a PNG of the same name never becomes that drawing's picture
            var ownSvg = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(normalPic) + ".svg"); // same name as the picture, away from the library
            File.WriteAllText(ownSvg, """<svg xmlns="http://www.w3.org/2000/svg" width="300" height="100"><rect width="300" height="100" rx="20" fill="#2060c0"/></svg>""");
            var ownDrawing = ElementLibrary.ImportButton(ownSvg);
            var ownPicture = ElementLibrary.ImportButton(normalPic);
            var ownBackground = ElementLibrary.Import(ownSvg);
            try
            {
                Check("un bouton SVG importé est listé et se dessine ; un PNG du même nom est rangé sous un autre nom, et reste une image",
                      ElementLibrary.Buttons().Any(b => b.Normal == ownDrawing.Normal) && MenuRenderer.Size(ownDrawing.Normal!) != null
                      && Path.GetFileNameWithoutExtension(ownPicture.Normal) != Path.GetFileNameWithoutExtension(ownDrawing.Normal) && MenuRenderer.Traced(ownPicture.Normal!) == null);
                Check("un fond SVG importé est listé parmi les images de l'utilisateur", ElementLibrary.Backgrounds().Any(b => b.Path == ownBackground.Path && b.Removable));
            }
            finally
            {
                ElementLibrary.RemoveButton(ownDrawing);
                ElementLibrary.RemoveButton(ownPicture);
                ElementLibrary.Remove(ownBackground);
                File.Delete(ownSvg);
            }
    
            // « Enregistrer le modèle » : the saved design is found by its name wherever a template is named, with its own pictures
            var keptDesign = elPic.Clone();
            keptDesign.Name = "check-" + Guid.NewGuid().ToString("N")[..8];
            Templates.Save(keptDesign);
            try
            {
                // a template whose buttons carry their word: French by default, the template's own picture on request, each with its chosen state
                Func<MenuDesign, string, (string? Picture, string? Focus)> wordsOf = (t, language) =>
                {
                    t.ButtonLanguage = language;
                    var page = InstalledTemplate.Load(t.TemplateDir!)?.Pages(t, elContent).FirstOrDefault();
                    var play = page?.Buttons.FirstOrDefault(b => b.Id == "play");
                    return (page?.Ops.OfType<BurnSmith.Core.Menu.Image>().FirstOrDefault(i => i.Rect == play?.Rect)?.Path, play?.FocusImage);
                };
                var inFrench = (string? f) => f != null && f.Contains(Path.DirectorySeparatorChar + InstalledTemplate.FrenchDir + Path.DirectorySeparatorChar);
                var worded = TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(t => t.TemplateDir != null && Directory.Exists(Path.Combine(t.TemplateDir, InstalledTemplate.FrenchDir)) && inFrench(wordsOf(t, "fr").Picture));
                var (fr, en) = worded == null ? default : (wordsOf(worded, "fr"), wordsOf(worded, "en"));
                var inWords = new MenuDesign { PlayLabel = "Lancer" }.Language("fr");
                Check("langue du menu : tous les mots en français, celui écrit par l'utilisateur gardé, puis de retour en anglais",
                      inWords is { ScenesLabel: "Chapitres", ResumeLabel: "Reprendre", MainMenuLabel: "Menu principal", CreditsLabel: "Crédits", Title: "Mon disque", PlayLabel: "Lancer", ButtonLanguage: "fr" }
                      && inWords.Untitled() && inWords.Language("en") is { ScenesLabel: "Scenes", ResumeLabel: "Resume", Title: "My Disc", PlayLabel: "Lancer", ButtonLanguage: "en" });
                if (worded == null) Console.WriteLine("skip boutons à mot dessiné : aucun modèle avec ses boutons français sur ce PC"); // the vendor's pictures are not in the repository
                else Check($"boutons à mot dessiné : français par défaut, anglais sur demande ({worded.Name})",
                      new MenuDesign().ButtonLanguage == "fr" && en.Picture != null && !inFrench(en.Picture) && !inFrench(en.Focus)
                      && MenuRenderer.Size(fr.Picture!) == MenuRenderer.Size(en.Picture) && (en.Focus == null) == (fr.Focus == null));
                Check("un modèle enregistré se retrouve par son nom (Mes modèles), avec ses boutons image",
                      TemplateCatalog.Find(keptDesign.Name) is { } again && again.Elements.Count == keptDesign.Elements.Count && again.Elements[^3].Image == normalPic && Templates.List().Any(t => t.Name == keptDesign.Name));
            }
            finally { Templates.Delete(keptDesign.Name); }
    
            // a pack written by hand (Vamp): the bonus has its picture button, a row has its own picture ({n} = its number), lit in place when chosen
            var handDir = Path.Combine(Path.GetTempPath(), "burnsmith-hand-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
                foreach (var f in new[] { "bg.png", "play/0.normal.png", "bonus/0.normal.png", "bonus/0.sub.png", "tv/2.normal.png", "tv/2.sub.png" })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(handDir, f))!);
                    File.WriteAllBytes(Path.Combine(handDir, f), pixel);
                }
                static string Node(string name, string attrs = "", string text = "", string kids = "") => $$"""{"name":"{{name}}","attrs":{{{attrs}}},"text":"{{text}}","kids":[{{kids}}]}""";
                var rows = string.Join(",", Enumerable.Range(0, 3).Select(i => Node("item", $"\"rect\":\"300,{100 + i * 100},800,60\",\"icon\":\"tv/{{n}}.normal.png\",\"bonus_icon\":\"bonus/0.normal.png\",\"icon_rect\":\"200,{100 + i * 100},60,60\"")));
                var tree = Node("template", kids: string.Join(",",
                    Node("common", kids: Node("playbuttontimage", text: "play/0.normal.png") + "," + Node("bonusbuttontimage", text: "bonus/0.normal.png")),
                    Node("rootmenu", "\"row\":\"center\"", kids: string.Join(",", Node("bg", kids: Node("image", text: "bg.png")), Node("play", "\"rect\":\"100,900,100,100\""), Node("bonus", "\"rect\":\"400,900,100,100\""))),
                    Node("scenes", kids: Node("tscenes", kids: Node("scene", kids: string.Join(",", Node("bg", kids: Node("image", text: "bg.png")), Node("videoes"), Node("list", kids: rows)))))));
                File.WriteAllText(Path.Combine(handDir, TemplatePack.FileName), $$"""{"schema":"burnsmith/template/1","name":"hand","category":"BurnSmith","kind":"BluRay","design":{"sceneList":true},"assets":[],"tree":{{tree}}}""");
                var handDesign = TemplatePack.TryRead(handDir)?.Design ?? new MenuDesign();
                handDesign.TemplateDir = handDir;
                var handContent = new MenuContent(1, null, [], [.. Enumerable.Range(1, 3).Select(i => new TitleEntry($"Épisode {i}", i, null))], null, null, [new TitleEntry("Making-of", 4, null)]);
                var handPages = InstalledTemplate.Load(handDir)?.Pages(handDesign, handContent) ?? [];
                var second = handPages.FirstOrDefault(p => p.Id == "scenes1")?.Buttons.ElementAtOrDefault(1);
                Check("modèle fait main : bouton image des bonus, image par ligne (la deuxième a la sienne, allumée à sa place ; les autres, sans image, restent des lignes)",
                      handPages.FirstOrDefault(p => p.Id == "root")?.Buttons.FirstOrDefault(b => b.Id == "bonus") is { FocusImage: { } lit } && lit.EndsWith("0.sub.png")
                      && second is { FocusSlot: { X: 200, Y: 200, W: 60, H: 60 }, Rect.X: 200, FocusImage: { } chosen } && chosen.EndsWith("2.sub.png")
                      && handPages.First(p => p.Id == "scenes1").Buttons[0].FocusSlot == null);
                int PlayAt(MenuContent content) => InstalledTemplate.Load(handDir)!.Pages(handDesign, content).First(p => p.Id == "root").Buttons.First(b => b.Id == "play").Rect.X;
                Check("modèle fait main : la rangée (row=center) reste à sa place quand tout s'affiche, et se recentre quand le disque n'a pas de bonus",
                      PlayAt(handContent) is var full && PlayAt(handContent with { Bonus = null }) == full + 150);
                handDesign.BonusText = true;
                Check("modèle fait main : une ligne de la page Bonus prend l'image bonus_icon du modèle, pas la pastille numérotée d'un épisode",
                      InstalledTemplate.Load(handDir)!.Pages(handDesign, handContent).FirstOrDefault(p => p.Id == "bonus1")?.Buttons.FirstOrDefault() is { FocusImage: { } star }
                      && Path.GetFileName(Path.GetDirectoryName(star)) == "bonus" && star.EndsWith("0.sub.png"));
                handDesign.BonusText = false;
                var handZip = Path.Combine(handDir, "..", Path.GetFileName(handDir) + ".zip");
                try
                {
                    var packed = TemplateCatalog.Pack(handZip, null, [handDir]);
                    using var z = System.IO.Compression.ZipFile.OpenRead(handZip);
                    Check("un modèle fait main est embarqué tel quel avec les modèles publiés", packed == 1 && z.Entries.Any(e => e.FullName.Replace('\\', '/').EndsWith("hand/tv/2.sub.png")));
                }
                finally { File.Delete(handZip); }
            }
            finally { Tools.TryDelete(handDir); }
        }
    
        // what a design stores for a picture that travels in the exe works on any PC; a plain path is left alone
        Check("une référence « lib: » revient au même fichier", ElementLibrary.Backgrounds().FirstOrDefault(x => x.Source == ElementLibrary.Open) is { } libPic && ElementLibrary.Resolve(ElementLibrary.Reference(libPic.Path)) == libPic.Path && ElementLibrary.Reference(libPic.Path).StartsWith("lib:"));
        Check("une référence « carried: » se range sous le dossier des images des modèles, ou ne mène à rien quand l'exe n'en porte pas",
              Tools.TemplatesDir() is { } carriedRoot ? ElementLibrary.Resolve("carried:x/y.png") == Path.Combine(carriedRoot, "x", "y.png") : ElementLibrary.Resolve("carried:x/y.png") == null);
        Check("un chemin ordinaire reste tel quel", ElementLibrary.Resolve(@"C:\a\b.png") == @"C:\a\b.png" && ElementLibrary.Resolve(null) == null);
    
        // DVDStyler's SVG buttons: each is drawn at two sizes (wide and square), shows something, and its selected state differs
        var svgButtons = ElementLibrary.Buttons().Where(b => b.Source == ElementLibrary.DvdStyler).ToList();
        var svgBad = MenuRenderer.Sta(() => svgButtons.Where(b =>
        {
            static bool Inked((byte[] Px, int W, int H) r) => Enumerable.Range(0, r.W * r.H).Any(i => r.Px[i * 4 + 3] > 0);
            try
            {
                var wide = MenuRenderer.Raster(b.Normal!, null, 300, 90);
                var square = MenuRenderer.Raster(b.Normal!, null, 120, 120);
                var focus = b.Focus == null ? null : MenuRenderer.Raster(b.Focus, null, 300, 90).Px;
                // a text button (text-circle…) is only its label until chosen: then its mark must show
                var shows = Inked(wide) && Inked(square) || focus != null && Inked((focus, 300, 90));
                return !shows || (focus != null && focus.SequenceEqual(wide.Px));
            }
            catch (InvalidDataException) { return true; }
        }).Select(b => b.Name).ToList());
        Check($"les {svgButtons.Count} boutons SVG de DVDStyler se dessinent à toute taille, avec leur état choisi{(svgBad.Count > 0 ? " — en défaut : " + string.Join(", ", svgBad) : "")}",
              svgButtons.Count >= 50 && svgBad.Count == 0);
        {
            // an SVG's links out of the document are not followed, and what SVGImage misses on its own is drawn as a browser does
            var svgDir = Path.Combine(Path.GetTempPath(), "burnsmith-svg-check");
            Directory.CreateDirectory(svgDir);
            var red = Path.Combine(svgDir, "red.png");
            MenuRenderer.Sta(() =>
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat<byte[]>([0, 0, 255, 255], 64).SelectMany(p => p).ToArray(), 8 * 4)));
                using var png = File.Create(red);
                encoder.Save(png);
                return 0;
            });
            (byte B, byte G, byte R, byte A) Pixel(string body, int x, int y, int width = 100, int height = 100)
            {
                var file = Path.Combine(svgDir, $"{Guid.NewGuid():N}.svg");
                File.WriteAllText(file, $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{width}\" height=\"{height}\">{body}</svg>");
                var (px, w, _) = MenuRenderer.Sta(() => MenuRenderer.Raster(file, null, width, height));
                var i = (y * w + x) * 4;
                return (px[i], px[i + 1], px[i + 2], px[i + 3]);
            }
            Check("a picture an SVG links to is not opened (a share would get the login hash), the SVG's own shapes still draw",
                  Pixel($"<image href=\"{new Uri(red).AbsoluteUri}\" width=\"100\" height=\"100\"/><image xlink:href=\"{red}\" width=\"100\" height=\"100\"/>", 50, 50).A == 0
                  && Pixel("<defs><rect id=\"r\" width=\"100\" height=\"100\" fill=\"#0000ff\"/></defs><use xlink:href=\"#r\"/>", 50, 50) is { B: > 200, A: > 200 });
            Check("a nested <svg> is placed by its viewBox",
                  Pixel("<svg width=\"100%\" height=\"100%\" viewBox=\"0 0 200 200\" preserveAspectRatio=\"none\"><rect x=\"100\" y=\"100\" width=\"100\" height=\"100\" fill=\"#ff0000\"/></svg>", 75, 75) is { R: > 200, A: > 200 }
                  && Pixel("<svg width=\"100%\" height=\"100%\" viewBox=\"0 0 200 200\" preserveAspectRatio=\"none\"><rect x=\"100\" y=\"100\" width=\"100\" height=\"100\" fill=\"#ff0000\"/></svg>", 25, 25).A == 0);
            Check("a length in % is of the width across and of the height down (a wide button's ellipse)",
                  Pixel("<ellipse cx=\"50%\" cy=\"50%\" rx=\"45%\" ry=\"45%\" fill=\"#ff0000\"/>", 20, 25, 200, 50) is { R: > 200, A: > 200 });
            Check("#RRGGBBAA is a colour and its opacity", Pixel("<rect width=\"100\" height=\"100\" fill=\"#ff000080\"/>", 50, 50) is { R: > 100 and < 160, A: > 100 and < 160, B: < 20 });
            Check("a shape that sets only its stroke's width keeps the colour its group gives",
                  Pixel("<g stroke=\"#00ff00\" fill=\"none\"><rect x=\"10\" y=\"10\" width=\"80\" height=\"80\" stroke-width=\"10\"/></g>", 10, 50) is { G: > 200, R: < 50 });
            Check("hidden parts and unused symbols draw nothing, a group's opacity fades it",
                  Pixel("<defs><rect id=\"r\" width=\"100\" height=\"100\"/></defs><use xlink:href=\"#r\" style=\"fill:#00ff00;visibility:hidden\"/>", 50, 50).A == 0
                  && Pixel("<symbol id=\"s\"><rect width=\"100\" height=\"100\" fill=\"#00ff00\"/></symbol>", 50, 50).A == 0
                  && Pixel("<g opacity=\"0.5\"><rect width=\"100\" height=\"100\" fill=\"#0000ff\"/></g>", 50, 50) is { A: > 100 and < 160 });
            var bomb = "<rect id=\"a0\" width=\"100\" height=\"100\" fill=\"#0000ff\"/>" + string.Concat(Enumerable.Range(1, 9).Select(i => $"<g id=\"a{i}\">{string.Concat(Enumerable.Repeat($"<use xlink:href=\"#a{i - 1}\"/>", 10))}</g>"));
            var bombClock = System.Diagnostics.Stopwatch.StartNew();
            Pixel($"<defs>{bomb}</defs><use xlink:href=\"#a9\"/>", 50, 50);
            Check("an SVG whose <use> copy each other a billion times is done with in a moment, not with all the memory", bombClock.Elapsed < TimeSpan.FromSeconds(10));
            Directory.Delete(svgDir, true);
        }
        SettingsCheck.Model(Check);
        {
            // a downloaded pack names files outside its folder (a network share sends the login hash, a private picture would be copied): none is followed
            var hostile = Path.Combine(Path.GetTempPath(), "burnsmith-hostile-pack");
            Directory.CreateDirectory(hostile);
            File.WriteAllBytes(Path.Combine(hostile, "thumb.png"), [0x89, 0x50, 0x4E, 0x47]);
            var bad = new TemplatePack { Name = "x", Assets = [new() { Role = "thumbnail", Path = @"..\outside.png" }] };
            bad.Design.BackgroundImage = @"\\attacker.invalid\s\bg.png";
            bad.Design.Poster = @"C:\Users\Public\x.jpg";
            bad.Design.PageBackgrounds["root"] = "lib:open/backgrounds/a.jpg";
            bad.Design.PageBackgrounds["scenes"] = @"\\attacker.invalid\s\p.png";
            bad.Save(hostile);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "outside.png"), [0x89, 0x50, 0x4E, 0x47]);
            var read = TemplatePack.TryRead(hostile);
            Check("a pack's paths outside its folder are dropped (share, drive, ..), library ones kept",
                  read is { Design.BackgroundImage: null, Design.Poster: null } && read.ThumbnailFile(hostile) == null
                  && read.Design.PageBackgrounds.Count == 1 && read.Design.PageBackgrounds["root"] == "lib:open/backgrounds/a.jpg");
            File.WriteAllText(Path.Combine(hostile, TemplatePack.FileName), File.ReadAllText(Path.Combine(hostile, TemplatePack.FileName)).Replace("\"design\": {", "\"design\": null, \"x\": {"));
            Check("a pack with \"design\": null is unreadable, not a crash", TemplatePack.TryRead(hostile) == null);
            Directory.Delete(hostile, true);
            File.Delete(Path.Combine(Path.GetTempPath(), "outside.png"));
        }
        // « Zone sûre »: the frames a TV may crop (93 % / 90 %, SMPTE ST 2046-1), and what leaves the title-safe one
        Check("safe area: action-safe 93 % and title-safe 90 % of 1920x1080, centred",
              SafeArea.Action == new Box(67, 38, 1786, 1004) && SafeArea.Title == new Box(96, 54, 1728, 972));
        var safePage = new MenuPage("root", [new Label("Lecture", new Box(40, 500, 300, 80), 40, true), new Label("Dedans", new Box(200, 300, 300, 80), 40, true), new Label("", new Box(0, 0, 10, 10), 40, true),
                                           new Label("Réglages", new Box(0, 60, 1920, 120), 60, true), new Label("Une phrase écrite tout à gauche", new Box(20, 200, 900, 60), 40, false, "left")],
                                    [new MenuButton("b1", new Box(40, 500, 300, 80), new MenuAction("play")), new MenuButton("b2", new Box(96, 54, 1728, 972), new MenuAction("play")), new MenuButton("b3", new Box(1700, 1000, 200, 60), new MenuAction("play"))]);
        Check("safe area: a button or text partly outside the title-safe frame is reported once (a button's own text by its button); one wholly inside (edges included), an empty text and a title centred across the page are not",
              SafeArea.Outside(safePage, new MenuDesign()).Select(x => x.What).SequenceEqual(["button b1", "button b3", "text \"Une phrase écrite tout à gauche\""]) && SafeArea.Warnings([safePage], new MenuDesign()).Count() == 3);
        Console.WriteLine(elFailed == 0 ? "all ok" : $"{elFailed} failed");
        return elFailed == 0 ? 0 : 1;
    }
}
