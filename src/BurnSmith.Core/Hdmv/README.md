# Menus Blu-ray natifs HDMV (Interactive Graphics)

Port en C# de **bdmvkit** (kajisho5, LGPL-3.0-or-later, v0.7.0, commit 5789076 ; voir `THIRD-PARTY-NOTICES.md`) : un menu Blu-ray qui n'est **pas** du BD-J (non signé, risqué sur les lecteurs de salon) mais un flux *Interactive Graphics* lu par tout lecteur Blu-ray, avec ses commandes de navigation (HDMV). Phase 3 de `FEUILLE-DE-ROUTE-BLURAY.md`.

**Rien n'est branché** dans `BluRay.cs`, `MenuFiles.cs`, `Program.cs` ni l'application WPF : ce dossier est une bibliothèque, `src/BurnSmith.HdmvCheck` son programme de vérification.

## Fichiers

| Fichier | Rôle (bdmvkit) |
|---|---|
| `Pgs.cs` | RVB → YCbCr (BT.709, plage studio), RLE, découpe en segments `PG` (`pgs.py`) |
| `IgStream.cs` | quantificateur ≤ 255 couleurs, ICS 0x18 / PDS 0x14 / ODS 0x15 / END 0x80, voisins haut-bas-gauche-droite (`igs.py`) |
| `TransportStream.cs` | formats vidéo, lecture d'un `.m2ts` (PES, EP_map), `SetStreamTypes` : PMT 0x90 → 0x91 + CRC-32 MPEG-2 (`formats.py`, `m2ts.py`) |
| `ClipFiles.cs` | `.clpi` et `.mpls` (`clip.py`, `playlist.py`) |
| `HdmvNav.cs` | commandes de 12 octets, `MovieObject.bdmv`, `index.bdmv` (`hdmv.py`) |
| `MenuClip.cs` | image fixe + flux IG muxés par le ffmpeg de BurnSmith (`encode.py`) |
| `HdmvAuthor.cs` | `HdmvAuthor.AddMenus` : l'API de haut niveau (`program.py`, `disc.py`, réduits à ce dont BurnSmith a besoin) |

## API

```csharp
var set = new HdmvMenuSet
{
    Movies = [new HdmvMovie(Playlist: 1, Chapters: 5)],        // 00001.mpls écrit par tsMuxeR --no-menu
    Pages  = [new HdmvPage { Id = "root", Background = "bg_root.jpg", Buttons = [
        new HdmvButtonSpec { Id = "play", X = 100, Y = 800, Selected = "hl_play.png",
                             Action = HdmvAction.PlayTitle(1),
                             Nav = new Dictionary<string,string>{ ["down"] = "scenes" } },
        new HdmvButtonSpec { Id = "scenes", X = 100, Y = 900, Selected = "hl_scenes.png",
                             Action = HdmvAction.OpenPage("scenes") } ] },
              /* ... */ ],
};
HdmvResult r = HdmvAuthor.AddMenus(@"D:\disque\BDMV", set);
```

`AddMenus` prend le dossier `BDMV` déjà produit par tsMuxeR (films seuls), choisit les prochains numéros de clip et de playlist libres, écrit pour chaque page `NNNNN.m2ts` / `.clpi` / `.mpls`, puis **réécrit** `MovieObject.bdmv` et `index.bdmv` (les titres : films d'abord, pages ensuite ; premier lancement = menu principal si `StartWithMenu`). Tout est écrit aussi dans `BDMV\BACKUP`. Si `index.bdmv` existe, son en-tête, son AppInfo et ses extensions (drapeaux UHD) sont conservés (`HdmvNav.PatchIndex`) ; sinon un index neuf est écrit.

Actions de bouton : `PlayTitle(n)`, `PlayChapter(n, c)` (le film joue depuis le chapitre c puis retour au menu), `OpenPage(id)`, `Run(commandes)` (commandes brutes de `HdmvCommand`). Voisins omis → calculés par `IgStream.AutoNav` (le plus proche dans chaque direction).

`Bdmv.PatchIndex` (un seul titre BD-J) n'est **pas modifié** : la voie BD-J reste telle quelle ; `HdmvNav.PatchIndex` est une fonction à part.

## Options (au-delà de bdmvkit) et valeurs par défaut

Toutes dans `IgOptions` (`HdmvMenuSet.Ig`) et `HdmvMenuSet.PlaylistUoMask`. Pour retrouver **exactement** la sortie de bdmvkit (c'est ce que fait le mode `disc` de `HdmvCheck`) il faut les trois réglages : `Ig = IgOptions.BdmvkitCompatible` (segments non datés, masque de page 0, son quantificateur), `PlaylistUoMask = UoMask.None` et `Fast = true` (x264 `ultrafast`, comme le clip joué de bdmvkit ; sans cela le PTS de départ du clip diffère : 1,4835 s au lieu de 1,4 s).

« Disque de référence » ci-dessous = `BR_LBAMVFVOST.iso` (« La boîte à musique (Fans Edition) »), un disque Blu-ray **d'amateur**, HDMV pur, décodé ici (section 4 de `FEUILLE-DE-ROUTE-BLURAY.md`) ; ce n'est ni un disque commercial ni un disque DVDFab, et sa lecture sur lecteur de salon n'est pas établie.

| Option | Défaut | bdmvkit | Pourquoi |
|---|---|---|---|
| `Timeline` | `Reference` : ICS daté PTS ≠ DTS, PDS / ODS / END datés, `-itsoffset` pour que l'image vienne avant l'ICS | `Zero` : tous les temps à 0 | Le disque de référence décodé date ses segments : ICS PTS − DTS = temps de décodage du plan (23 328 ticks en 1080), PDS PTS = DTS de l'ICS, DTS d'un ODS = PTS du précédent, END = PTS du dernier ODS (21 objets sur 21 retrouvés). Un lecteur qui planifie le décodage sur les DTS n'a ainsi rien à deviner. |
| `PageUoMask` et `PlaylistUoMask` | `UoMask.MenuReference` = `3cb805ff40000000` | 0 (tout permis) | C'est le masque du disque de référence (page IG et playlist de menu) : interdit recherche de chapitre / temps, suivant / précédent, pause, avance / retour rapide, changement d'audio / angle / pop-up pendant le menu. Il vient de ce disque d'amateur, **pas d'une valeur certifiée** ; `UoMask.None` ou `UoMask.Mask(...)` pour autre chose. |
| `Palette` | `MedianCut` (coupe médiane sur (r, g, b, a)) | `Bdmvkit` : on retire des bits de couleur jusqu'à ≤ 255 | Même format de sortie (≤ 255 entrées), erreur moindre : 5,94 contre 10,74 sur le dégradé de test ; sur les images de focus réelles de BurnSmith, 255 couleurs à 1,3 d'erreur moyenne par canal contre 110 couleurs à 11,9 (`HdmvCheck palette`). |
| `PresentationTicks` | 90 000 (1 s) | — | Instant (90 kHz, dans le `.sup`) où l'ICS est présenté avec `Timeline = Reference` ; son DTS est antérieur du temps de décodage du plan. |

Non porté : menus animés / avec musique (`motion`, `music`), fenêtre pop-up (`add_ig_stream`), transitions entre pages, lecture de tout, aléatoire, menus de choix audio / sous-titres (les constructeurs de commandes `SetStreams` / `SetSubtitles` existent). `ButtonOffset` est géré mais n'a pas été joué.

## Vérifier

`src/BurnSmith.HdmvCheck` (assertions, aucun paquet NuGet ; **hors** `BurnSmith.slnx` et hors `publish.ps1`) :

```powershell
dotnet build src\BurnSmith.HdmvCheck -c Release
$dll = 'src\BurnSmith.HdmvCheck\bin\Release\net10.0-windows\BurnSmith.HdmvCheck.dll'
dotnet $dll unit                          # aller-retour quantif / RLE, CRC, YCbCr, ICS décodé, constantes de la timeline
dotnet $dll bdmvkit <run> <travail>       # + comparaison octet à octet avec la sortie jouée de bdmvkit
dotnet $dll disc <run> <sortie>           # dossier de disque construit à partir des entrées de bdmvkit
dotnet $dll addmenu <BDMV> <run> [default|zero|reference] [none|menu]   # menu ajouté à un film tsMuxeR
dotnet $dll realmenu <menu-dir> <BDMV> [palette]                        # le menu BD-J de BurnSmith (menu.txt) converti en pages HDMV
dotnet $dll synthcmp <dossier> ; palette <menu-dir> ; pes <m2ts> [pid]
```

`<run>` = un dossier d'essai de bdmvkit (menu.png, boutons PNG, `disc.json`, `feat.m2ts`, `out\BDMV`). Lecture dans libbluray :

```powershell
tools\vlctest.ps1 -Disc <dossier-disque> -OutDir <sortie> -Keys 'key-nav-down','key-nav-activate','sleep:14' -Wait 30
# les preuves sont dans <sortie>\libbluray.log (les captures VLC ne montrent pas la surcouche IG)
```

### Résultats

- **[testé]** `unit` : une trentaine de vérifications passent.
- **[testé]** Sortie de bdmvkit rejouée en C# (`BdmvkitCompatible`) : 12 segments IG identiques octet pour octet à ceux du `00002.m2ts` joué ; `index.bdmv`, `MovieObject.bdmv`, `.clpi`, `.mpls` (et `BACKUP`) identiques ; les charges PES du flux IG du menu et le type 0x91 de la PMT aussi (`disc`), et un `cmp` fait à la main du `.m2ts` entier était identique ; le dégradé synthétique (`synthcmp`) identique (26 425 octets).
- **[testé]** libbluray (VLC portable, `vlctest.ps1`), disque construit par le C# : menu → bas → entrée → film depuis le chapitre 2 → retour au menu, avec les réglages par défaut (segments datés + masque UO), avec le film tsMuxeR + menu C#, et avec le menu réel de BurnSmith à deux pages (accueil → scènes → chapitre).
- **[déduit]** Rien de tout cela ne prouve un lecteur de salon : libbluray n'est pas un lecteur certifié, et on n'a pas vérifié qu'il applique le masque UO et les DTS comme un lecteur matériel.

## Non prouvé

- **Aucun lecteur de salon testé.** Le test de `bdmvkit-test.iso` sur le lecteur de Marc est en attente (c'est à lui de le faire). Les options ci-dessus (DTS, masque UO) sont des hypothèses alignées sur ce disque d'amateur, pas des correctifs établis.
- Règle de l'ICS PTS quand les objets dépassent le plan (max(plan, Σ objets)) : extrapolée, non mesurée.
- Le PTS de l'ICS tombe 0,26 s après la première image vidéo (la référence les a égaux) ; muxage dans l'ordre de ffmpeg et non entrelacé par date d'arrivée comme la référence.
- Chemin 720p : formats définis, encodage non essayé.

## Points d'intégration (étape suivante)

1. **`BluRay.cs`** : après le montage tsMuxeR du film en `--no-menu`, construire un `HdmvMenuSet` depuis le modèle de pages / boutons de `MenuFiles` et appeler `HdmvAuthor.AddMenus(bdmvDir, set)` au lieu de `Bdmv.PatchIndex` + jar BD-J. `HdmvMovie.Chapters` doit refléter les marques de la playlist tsMuxeR.
2. **`MenuFiles.cs`** : `bg_*.jpg` → `HdmvPage.Background`, `hl_*.png` + position → `HdmvButtonSpec.Selected` / `X` / `Y`, la table de voisins → `Nav`, `play` / `chapter` / `page` → `HdmvAction.PlayTitle` / `PlayChapter` / `OpenPage`. La conversion de `menu.txt` faite par `HdmvCheck realmenu` en est la maquette.
3. **Solution** : ajouter `HdmvCheck` à `BurnSmith.slnx` (et `publish.ps1` si on veut le publier) ; l'option « compatibilité maximale (HDMV) » (phase 3e) vit dans l'application WPF.
