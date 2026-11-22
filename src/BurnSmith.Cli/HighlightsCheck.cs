using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

static class HighlightsCheck
{
    public static int Run(string? outDir)
    {
        var hlFailed = 0;
        void Check(string what, bool ok) { if (!ok) hlFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        static bool Has(byte[] hay, string ascii)
        {
            var needle = System.Text.Encoding.ASCII.GetBytes(ascii);
            for (var i = 0; i + needle.Length <= hay.Length; i++) if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
            return false;
        }
    
        var hlContent = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 4).ToList(), null, ["Français AC-3 mono", "English AC-3 mono"], ["Français"]);
    
        // (0) sans modèle : l'ordre des couleurs de la teinte, une image réécrite sous le même nom, un paquet téléchargé mal formé
        Check("la teinte d'une image rouge est rouge (octets B, G, R, A)", IndexedPng.Average([0, 0, 255, 255], 1, 1) is { R: 255, G: 0, B: 0 });
        var hlTmp = Path.Combine(Path.GetTempPath(), $"burnsmith-hl-{Environment.ProcessId}");
        Directory.CreateDirectory(hlTmp);
        try
        {
            void Dot(string file, byte b, byte g, byte r, int day)
            {
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { b, g, r, 255 }, 4)));
                using (var stream = File.Create(file)) png.Save(stream);
                File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, day, 0, 0, 0, DateTimeKind.Utc));
            }
            var dot = Path.Combine(hlTmp, "c1.png");
            Dot(dot, 0, 0, 255, 1);
            var dotRed = MenuRenderer.Sta(() => MenuRenderer.Raster(dot, null, 1, 1)).Px;
            Dot(dot, 255, 0, 0, 2);
            var dotBlue = MenuRenderer.Sta(() => MenuRenderer.Raster(dot, null, 1, 1)).Px;
            Check("une image réécrite sous le même nom est relue (les vignettes du disque suivant, dans l'application)", dotRed[2] == 255 && dotRed[0] == 0 && dotBlue[0] == 255 && dotBlue[2] == 0);
            File.WriteAllText(Path.Combine(hlTmp, TemplatePack.FileName), """{"schema":"burnsmith/template/1","name":"x","design":{"elements":[null]}}""");
            Check("un paquet dont une liste contient null est refusé, sans arrêter le catalogue", TemplatePack.TryRead(hlTmp) == null);
            File.WriteAllText(Path.Combine(hlTmp, TemplatePack.FileName), """{"schema":"burnsmith/template/1","name":"x","assets":[{"role":"image","path":"C:\\Windows\\win.ini"},{"role":"image","path":"c1.png"}]}""");
            Check("un paquet ne fait rien ouvrir hors de son dossier (un chemin complet, un partage réseau) : l'image est dite absente", TemplatePack.TryRead(hlTmp)?.Problems() is [var outside] && outside.Contains("win.ini"));
        }
        finally { Directory.Delete(hlTmp, true); }
    
        // (a) an installed DVD template names its highlighted artwork for each button that has one
        var hlModel = TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.TemplateDir != null && d.Kind == TemplateKind.Dvd);
        if (hlModel == null) Console.WriteLine("ok   (aucun modèle DVD installé : la vérification du modèle est sautée)");
        else
        {
            var hlPages = MenuBuilder.Build(hlModel, hlContent);
            var hlButtons = hlPages.SelectMany(p => p.Buttons).Where(b => b.DvdHighlightImage != null).ToList();
            Check("le modèle nomme l'image de survol de ses boutons, et le fichier existe", hlButtons.Count > 0 && hlButtons.All(b => File.Exists(b.DvdHighlightImage!)));
            Check("le masque de forme vient du jumeau « .mask. » du modèle", hlButtons.Any(b => b.DvdHighlightMask is { } m && File.Exists(m)));
            var hlOne = hlButtons.First();
            var (hlPx, hlW, hlH) = MenuRenderer.Sta(() => MenuRenderer.Raster(hlOne.DvdHighlightImage!, hlOne.DvdHighlightMask, 120, 80));
            var hlOpaque = hlPx.Where((_, i) => i % 4 == 3).Count(alpha => alpha >= 128);
            Check("le raster a la taille du bouton et des pixels visibles (c'est ce que le lecteur affichera)",
                  hlW == 120 && hlH == 80 && hlOpaque > 0);
            // l'état choisi vient du modèle : son propre marqueur (selection/0.png), pas la silhouette du survol
            var hlSel = hlButtons.Select(b => b.DvdSelectImage).FirstOrDefault(i => i != null);
            Check("le modèle nomme l'image de l'état choisi, et son marqueur donne une forme visible", hlSel != null && File.Exists(hlSel));
    
            // --out DOSSIER : écrit les deux images de page (survol et choisi) telles que le surlignage DVD les reçoit, pour les regarder
            if (outDir is { } hlDir)
            {
                Directory.CreateDirectory(hlDir);
                var (hw, hh) = (720, 576);
                double psx = hw / (double)MenuBuilder.W, psy = hh / (double)MenuBuilder.H;
                var hovered = new IndexedPng.Canvas(hw, hh);
                var chosen = new IndexedPng.Canvas(hw, hh);
                var page = hlPages.First(q => q.Buttons.Any(b => b.DvdHighlightImage != null));
                var shots = MenuRenderer.Sta(() => page.Buttons.Select(b =>
                {
                    int bw = (int)Math.Round(b.Rect.W * psx), bh = (int)Math.Round(b.Rect.H * psy);
                    (byte[] Px, int W, int H)? At(string? image, string? mask) => image == null ? null : MenuRenderer.Raster(image, mask, bw, bh);
                    (byte[] Px, int W, int H)? Marker(string? image)
                    {
                        if (image == null) return null;
                        var natural = MenuRenderer.Size(image) is var (iw, ih) ? ((int)Math.Round(iw * psx), (int)Math.Round(ih * psy)) : (bw, bh);
                        return MenuRenderer.Raster(image, null, Math.Min(bw, Math.Max(1, natural.Item1)), Math.Min(bh, Math.Max(1, natural.Item2)));
                    }
                    return (Hl: At(b.DvdHighlightImage, b.DvdHighlightMask), Sel: Marker(b.DvdSelectImage), b.Rect);
                }).ToList());
                foreach (var s in shots)
                {
                    if (s.Hl is not { } hover) continue;
                    int x0 = (int)(s.Rect.X * psx) & ~1, y0 = (int)(s.Rect.Y * psy) & ~1;
                    hovered.Stamp(hover.Px, hover.W, hover.H, x0, y0, 1);
                    var c = s.Sel ?? hover;
                    var at = s.Sel != null ? (x0 + (hover.W - c.W) / 2, y0 + (hover.H - c.H) / 2) : (x0, y0);
                    chosen.Stamp(c.Px, c.W, c.H, at.Item1, at.Item2, 2);
                }
                var tint = shots.Select(s => s.Hl).FirstOrDefault(x => x != null) is { } f ? IndexedPng.Average(f.Px, f.W, f.H) : new IndexedPng.Rgba(255, 255, 255, 255);
                IndexedPng.Rgba[] pal = [new(0, 0, 0, 0), tint, new(255, 255, 255, 255), new(0, 0, 0, 0)];
                File.WriteAllBytes(Path.Combine(hlDir, $"hl_{page.Id}.png"), IndexedPng.Encode(hovered, pal));
                File.WriteAllBytes(Path.Combine(hlDir, $"sel_{page.Id}.png"), IndexedPng.Encode(chosen, pal));
                Console.WriteLine($"écrit {hlDir}\\hl_{page.Id}.png et sel_{page.Id}.png (page {page.Id}, {page.Buttons.Count} boutons)");
    
            }
            var hlPage = new IndexedPng.Canvas(720, 576);
            hlPage.Stamp(hlPx, hlW, hlH, 200, 200, 1);
            Check($"collée sur la page, la forme du modèle est reprise telle quelle ({hlOpaque} pixels) et rien n'est ajouté ailleurs",
                  hlPage.Pixels.Count(v => v == 1) == hlOpaque && hlPage.Pixels.Count(v => v != 0) == hlOpaque);
        }
    
        // (b) the encoder: a 2-bit indexed PNG, four colours, index 0 transparent
        var hlCanvas = new IndexedPng.Canvas(16, 8);
        var hlArt = Enumerable.Range(0, 4 * 2 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 0)).ToArray();
        hlCanvas.Stamp(hlArt, 4, 2, 4, 3, 1);
        var hlPng = IndexedPng.Encode(hlCanvas, [new(0, 0, 0, 0), new(200, 40, 40, 255), new(255, 255, 255, 255), new(0, 0, 0, 0)]);
        Check("la surbrillance est un PNG indexé 2 bits à quatre couleurs, avec transparence",
              hlPng.Length > 26 && hlPng[24] == 2 && hlPng[25] == 3 && Has(hlPng, "tRNS") && Has(hlPng, "PLTE"));
        Check("l'artwork est tamponné à sa place, le reste de la page reste transparent",
              hlCanvas.Pixels[3 * 16 + 4] == 1 && hlCanvas.Pixels[3 * 16 + 7] == 1 && hlCanvas.Pixels[3 * 16 + 8] == 0 && hlCanvas.Pixels[0] == 0);
    
        // (c) a design without a model has no artwork at all: Dvd.cs keeps its four-colour frame there
        var hlPlain = TemplateCatalog.Find("Nuit d'encre") ?? new MenuDesign();
        Check("un modèle sans artwork garde le cadre à quatre couleurs (aucune image de surbrillance nommée)",
              hlPlain.TemplateDir == null && MenuBuilder.Build(hlPlain, hlContent).All(p => p.Buttons.All(b => b.DvdHighlightImage == null)));
    
        // scene and Bonus page options, on a design of BurnSmith and on a template: scenes only, first video left out, text Bonus page
        var season = new MenuContent(1, null, [], [.. Enumerable.Range(1, 4).Select(i => new TitleEntry($"Épisode {i}", i, null))], null, null,
                                     [new TitleEntry("Making-of", 5, null), new TitleEntry("Bande-annonce", 6, null)]);
        foreach (var model in new[] { hlPlain, TemplateCatalog.Default() })
        {
            MenuDesign With(Action<MenuDesign> set) { var m = model.Clone(); set(m); return m; }
            var only = MenuBuilder.Build(With(m => m.ScenesOnly = true), season);
            var skip = MenuBuilder.Build(With(m => m.SkipFirstScene = true), season);
            Check($"{model.Name} : menu limité aux scènes (la page de scènes est le menu, avec Lecture), sans la 1re vidéo (pas de case e1, « Tout lire » reste)",
                  only[0].Id == "root" && only[0].Buttons.Any(b => b.Id == "e1") && only[0].Buttons.Any(b => b.Action.Kind == "playall") && only.All(p => p.Id != "scenes1")
                  && skip.SelectMany(p => p.Buttons).All(b => b.Id != "e1") && skip.SelectMany(p => p.Buttons).Any(b => b.Id == "e2")
                  && skip.First(p => p.Id == "root").Buttons.Any(b => b.Action.Kind == "playall"));
        }
        var textBonus = MenuBuilder.Build(With2(hlPlain, m => m.BonusText = true), season).First(p => p.Id == "bonus1");
        Check("page Bonus en texte : pas d'image sur la page, ses deux boutons y sont", textBonus.Ops.All(o => o is not Picture) && textBonus.Buttons.Count(b => b.Id.StartsWith('b')) == 2);
        static MenuDesign With2(MenuDesign model, Action<MenuDesign> set) { var m = model.Clone(); set(m); return m; }
    
        // the three highlight styles: a box, an underline, or the label in colour
        var hlPageRoot = MenuBuilder.Build(hlPlain, hlContent).First(q => q.Id == "root");
        var hlBtn = hlPageRoot.Buttons.First(b => b.Id == "play");
        var hlName = hlPageRoot.Ops.OfType<Label>().First(l => l.Rect == hlBtn.Rect).Value;
        var hlShots = new[] { "box", "underline", "textcolor" }.ToDictionary(st => st, st =>
        {
            var styled = hlPlain.Clone();
            styled.HighlightStyle = st;
            return MenuRenderer.Sta(() => MenuRenderer.Focus(styled, hlBtn, 1, hlName));
        });
        Check("trois styles de surbrillance distincts, tous dessinés (cadre, soulignement, texte en couleur)",
              hlShots.Values.All(v => v.Length > 0) && hlShots.Values.Select(v => v.Length).Distinct().Count() == 3);
        Check("le style « texte en couleur » ne dessine pas de cadre : son image est plus légère que celle du cadre",
              hlShots["textcolor"].Length < hlShots["box"].Length);
    
        // a selection colour of its own, and the fill transparency
        var hlInk = hlPlain.Clone();
        hlInk.SelectionColor = "#00FF00";
        hlInk.HighlightStyle = "underline";
        var hlInkShot = MenuRenderer.Sta(() => MenuRenderer.Focus(hlInk, hlBtn, 1, hlName));
        Check("une couleur de sélection distincte change l'image (le soulignement n'a plus la teinte d'accent)",
              !hlInkShot.SequenceEqual(hlShots["underline"]) && hlInk.SelectionInk == "#00FF00");
        var hlFaint = hlPlain.Clone();
        hlFaint.SelectionOpacity = 0;
        var hlFaintShot = MenuRenderer.Sta(() => MenuRenderer.Focus(hlFaint, hlBtn, 1, hlName));
        Check("une transparence de sélection différente change l'image (0 % ne remplit plus le cadre)",
              !hlFaintShot.SequenceEqual(hlShots["box"]));
        if (outDir is { } styleDir)
        {
            Directory.CreateDirectory(styleDir);
            foreach (var (st, png) in hlShots) File.WriteAllBytes(Path.Combine(styleDir, $"style_{st}.png"), png);
            File.WriteAllBytes(Path.Combine(styleDir, "style_custom.png"), hlInkShot);
            File.WriteAllBytes(Path.Combine(styleDir, "style_faint.png"), hlFaintShot);
            Console.WriteLine("écrit style_box.png, style_underline.png et style_textcolor.png dans " + styleDir);
        }
    
        // le menu Blu-ray reçoit bien l'image du style choisi (et le libellé du bouton pour « texte en couleur »)
        var hlBdj = Path.Combine(Path.GetTempPath(), "bs_hl_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var styled = hlPlain.Clone();
            styled.HighlightStyle = "textcolor";
            MenuRenderer.Sta(() => { MenuFiles.WriteBdj(styled, [hlPageRoot], hlBdj); return 0; });
            var focus = Path.Combine(hlBdj, $"hl_root_play.png");
            Check("le menu Blu-ray reçoit l'image du style choisi, libellé compris", File.Exists(focus) && new FileInfo(focus).Length > 0
                  && !File.ReadAllBytes(focus).SequenceEqual(hlShots["box"]));
    
            var firstFrame = Path.Combine(hlBdj, "source1.png");
            var secondFrame = Path.Combine(hlBdj, "source2.png");
            File.WriteAllBytes(firstFrame, hlShots["box"]);
            File.WriteAllBytes(secondFrame, hlShots["underline"]);
            styled.FocusFrames = [firstFrame, secondFrame];
            var animatedPage = hlPageRoot with { Buttons = [hlBtn with { FocusSlot = new Box(90, 120, 30, 60) }] };
            var animatedDir = Path.Combine(hlBdj, "animated");
            MenuFiles.WriteBdj(styled, [animatedPage], animatedDir, scale: 2.0 / 3);
            var animation = File.ReadAllText(Path.Combine(animatedDir, "menu.txt"));
            Check("le curseur BD-J embarque ses images et sa position à l'échelle de la page",
                  animation.Contains("focus-frames focus_frame_0.png focus_frame_1.png")
                  && animation.Contains("focus-slot 60 80 20 40")
                  && File.ReadAllBytes(Path.Combine(animatedDir, "focus_frame_0.png")).SequenceEqual(hlShots["box"])
                  && File.ReadAllBytes(Path.Combine(animatedDir, "focus_frame_1.png")).SequenceEqual(hlShots["underline"]));
            File.WriteAllText(secondFrame, "PNG illisible");
            var fallbackDir = Path.Combine(hlBdj, "fallback");
            var warnings = new List<string>();
            MenuFiles.WriteBdj(styled, [animatedPage], fallbackDir, log: warnings.Add);
            var fallback = File.ReadAllText(Path.Combine(fallbackDir, "menu.txt"));
            Check("une image animée illisible garde la surbrillance fixe et les commandes du menu",
                  !fallback.Contains("focus-frames") && !fallback.Contains("focus-slot") && fallback.Contains("button root play")
                  && File.Exists(Path.Combine(fallbackDir, "hl_root_play.png")) && warnings.Any(w => w.Contains("surbrillance fixe")));
        }
        finally { try { Directory.Delete(hlBdj, true); } catch (IOException) { } }
    
        Console.WriteLine(hlFailed == 0 ? "all ok" : $"{hlFailed} failed");
        return hlFailed == 0 ? 0 : 1;
    }
}
