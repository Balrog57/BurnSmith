# Création DVD / Blu-ray avec menu : ce que la décompilation établit (note REA, 2026-11-05)

Note de travail, séparée de la feuille de route. Elle consigne **les preuves** apportées par REA sur les trois suites
(Yuhan Blu-ray DVD Creator, Leawo Prof. Media, DVDFab) et **ce que BurnSmith en fait ou peut en faire**. Rien n'a été
exécuté : ni Yuhan, ni Leawo, ni DVDFab n'ont été lancés, aucun disque n'a été produit par eux. Ce sont des constats sur
les fichiers livrés (octets, chaînes, code décompilé).

## Cibles et méthode

| Cible | Taille | SHA-256 (début) | Outil | Ce qui en a été tiré |
|---|---|---|---|---|
| `Yuhan\DVDCreator.dll` | 190 976 | `538c43d9…dcbf` | REA / Ghidra 12.1.4 | 374 procédures, 221 chaînes, inventaire des chaînes de la voie DVD |
| `Yuhan\BlurayDVDCreator.exe` | 1 761 280 | `6c5e2f40…3f7f` | balayage de chaînes local | noms de clips et de dossiers de la voie Blu-ray |
| `Yuhan\DISCBURN.dll` | 68 608 | `4828353014a9…` | REA / Ghidra | témoin de mise en route (374 procédures, 8 segments) |
| `Yuhan\PresetMenu\THEME01..30` | 30 thèmes | — | lecture des fichiers | format `config.dat` (chiffré) et arborescence des images |
| `Leawo\MenuTemplate\Build-in` | 33 dossiers | — | lecture des fichiers | `template.xml` en clair, 16:9 et 4:3 |
| `DVDFab\author\templates`, `bdtemplates` | 28 dossiers | — | lecture des fichiers | `template.xml` / `template.bd.xml` en clair, états `.normal` / `.sub` / `.mask` |

Les binaires analysés ont été **copiés** dans `analysis\bin\` (dossier de travail, non livré) : les originaux de
`C:\Program Files\` ne sont ni modifiés ni déplacés. Les preuves REA portent sur la copie ; l'identité est la même
(SHA-256 ci-dessus, recalculé à l'ouverture).

**Limite de méthode, à connaître** : l'import Ghidra de `BlurayDVDCreator.exe` (1,7 Mo) dépasse la fenêtre d'appel du
pont REA (`request timed out`, deux essais), comme lors de la session du 2026-11-05 qui avait dû passer par
`analyzeHeadless` pour `BlurayCreator.dll`. Les DLL de 70 à 265 ko s'analysent normalement (c'est le cas ici de
`DVDCreator.dll`). Le code de l'exécutable n'est donc **pas** décompilé : seules ses chaînes sont lues, et elles sont
marquées comme telles.

## 1. Yuhan, voie DVD : `dvdauthor` + `spumux`, comme BurnSmith

Chaînes de `DVDCreator.dll` (REA, `search_strings`, adresses `0x180022ee0` à `0x1800230a0`) :

| Chaîne | Lecture |
|---|---|
| `%1/dvdauthor.exe` | le programme est livré et appelé depuis le dossier de travail |
| `%1/dvdauthor.xml` | script de navigation engendré à l'exécution |
| `%1/spumux.exe` | surcouche de surbrillance (sous-titres de menu) |
| `%1/spumux_%2.xml` | un script spumux par page de menu (`%2` = la page) |
| `%1/menu_spumux_%2.vob` | la sortie de spumux, un VOB de menu par page |
| `subpictures`, `subpicture`, `dvdauthor` | vocabulaire du format de script |

Ce que cela établit : **la voie DVD de Yuhan est exactement celle de BurnSmith** (`dvdauthor -x` + `spumux script
entrée sortie`), avec un script par page de menu. Ce n'est pas une surprise, mais c'est la confirmation que le choix de
BurnSmith n'est pas un contournement : c'est la chaîne de référence du produit.

Ce que cela n'établit pas : la **forme** du script (boutons, voisins, commandes) et surtout la **construction de la
surbrillance** à partir des images du modèle (`frame/0.dvd.sub.png`, `frame/0.dvd.mask.png`, `selection/0.png`). Ces
fonctions n'ont pas été lues. C'est précisément le point ouvert de la feuille de route (« surbrillances DVD exactes
d'après les modèles ») : pour le fermer il faudrait décompiler les fonctions de `DVDCreator.dll` qui écrivent
`spumux_%2.xml` (à localiser par les références aux chaînes ci-dessus, `xrefs` sur `0x180022ee0`).

## 2. Yuhan, voie Blu-ray : des clips de menu par page **et** par rôle

Chaînes de `BlurayDVDCreator.exe` (balayage local des sections ASCII et UTF-16) :

```
%1/menu_0.m2ts            %1/menu_%2.m2ts             %1/bf_menu_%2.m2ts
%1/setup_0.m2ts           %1/setup_audio_%2.m2ts      %1/setup_subtitle_%2.m2ts
%1/PresetMenu/%2          %1/PresetMenu/%2/%3         %1/config.dat
%1/res/bdmt_eng.xml       %1/%2/BDMV/META/DL/bdmt_eng.xml
%1/%2/%3/%4.m2ts          %1/%2/%3/%4.vob
slotMenuTemplateChanged(QListWidgetItem*)
```

Lecture [déduit du nom, non prouvé par le code] : un clip par page de menu (`menu_N`), un clip de **menu contextuel**
(`bf_menu_N`, « bf » = *background/foreground* ou *bonus feature*), et des **pages de réglages** séparées pour l'accueil
(`setup_0`), l'audio (`setup_audio_N`) et les sous-titres (`setup_subtitle_N`). Le nom `slotMenuTemplateChanged` est un
slot Qt : le choix d'un modèle dans une liste déclenche la reconstruction de l'aperçu.

Ce que cela confirme et complète : la feuille de route (§4d, `BlurayCreator.dll`) avait établi qu'un menu = un clip
vidéo de fond + un clip IG impair, avec `ui_model` 1 pour le menu contextuel. Les noms de fichiers de l'exécutable
montrent **la même structure au niveau des fichiers**, plus les pages de réglages que la DLL laissait deviner (elle écrit
les commandes `SET_STREAM`). Pour BurnSmith, cela ne change pas le plan (écrire notre propre clip IG), mais cela fixe la
nomenclature à reproduire si l'on veut comparer page à page avec un disque Yuhan.

Reste non lu : tout le code de l'exécutable (limite de méthode ci-dessus), donc la façon dont les pages de réglages
sont reliées aux commandes (registres R102 / R103 déjà documentés côté DLL).

## 3. Les bibliothèques de modèles sont lisibles en clair (sauf Yuhan)

C'est le point qui compte le plus pour « toute la bibliothèque de modèles » : **deux des trois suites livrent leur
mise en page en XML lisible**, avec une sémantique directement réutilisable.

**DVDFab, DVD** (`author\templates\<nom>\template.xml`) : un XML plat, chemins d'images relatifs, géométrie en pixels
d'une grille 640×480 pour le DVD :

```xml
<Template>
  <bg>background/bg0.png</bg>  <home>home/0.normal.png</home>  <frame>frame/0.dvd.normal.png</frame>
  <mask>mask/0.normal.png</mask>  <selection>selection/0.png</selection>
  <video_per_scene>3</video_per_scene>
  <font>Brush Script MT</font> <font_size>16</font_size> <font_color>#FFE6AE</font_color>
  <Topmenu> <label>My DVD</label> <label_left>54</label_left> <label_top>25</label_top> … </Topmenu>
  <Title> <home_left>260</home_left> <video1_left>36</video1_left> … </Title>
</Template>
```

**DVDFab, Blu-ray** (`template.bd.xml`, 22 modèles, 4K compris) : même idée, en 1920×1080, avec des rects
`x,y,w,h` par élément et des **états d'image par bouton** : `selection/0.png` (survol), `mask/0.normal.png`,
`frame/0.dvd.normal.png`, `play/0.normal.png` + `play/0.sub.png`, `LANG_JPN/…` pour les variantes de langue. Les nœuds
`<setup>` portent `audiolist` / `subtitlelist` (six `title` par colonne, `line_height` 80) : la page de réglages est
**décrite dans le modèle**, pas dessinée par le programme.

**Leawo** (`MenuTemplate\Build-in\16-9\<thème>\template.xml`) : `<TemplateDef DefName=… Aspect="16-9">` puis
`<Color>`, `<Menu>` (fond, cadre, zone vidéo, textes Lecture / Scènes), `<Scene><Page>` (jusqu'à 4 `FrameN` avec leur
`videoscrN`, `ButtonNext`, `ButtonPre`, `Return`, `ButtonTextN`). Un même fichier porte la variante 4-3 en second
`<TemplateDef>`.

**Yuhan** (`PresetMenu\THEMExx\config.dat`) : **chiffré**. Décodé par BurnSmith à l'exécution : AES-128-CBC, clé de
16 octets et IV de 16 octets lus à l'adresse `0x194020` de `BlurayDVDCreator.exe`, contenu = longueur sur 2 octets
(petit-boutiste) puis XML UTF-8 `<MenuTemplate>` avec une `TemplateBranch` par résolution (768×576, 1280×720,
1920×1080, 3840×2160). L'exe publié embarque le XML déjà déchiffré ; les images restent celles de l'utilisateur.

Conséquence pour BurnSmith : les modèles DVDFab et Leawo sont **déjà repris tels quels** (144 modèles utilisables, voir
README). Ce que la lecture en clair permettrait d'ajouter, sans décompiler davantage :

1. **Les surbrillances DVD exactes** : `frame/*.dvd.sub.png` + `frame/*.dvd.mask.png` + `selection/0.png` d'un modèle
   DVDFab DVD donnent l'image et le masque de la surbrillance ; c'est la brique qui manque pour remplacer le cadre
   générique à quatre couleurs de `Dvd.cs`.
2. **Les pages de réglages des modèles DVDFab Blu-ray** (`audiolist` / `subtitlelist`, positions et `line_height`) :
   BurnSmith les dessine aujourd'hui lui-même ; le modèle dit où.
3. **Les variantes de langue** (`LANG_JPN/…`) : les mêmes boutons, dans une autre langue, déjà dessinés.

## 4. Ce qui a été fait dans BurnSmith à partir de là

Cette session a porté sur le point « possibilité de modifier comme DVDStyler » de la feuille de route (phase 2), en
s'appuyant sur la structure commune des trois modèles (un élément = un rect + une image + un état) :

- **Éléments libres** (`MenuElement`) : texte, image ou cadre, placés par l'utilisateur en pixels du canevas
  1920×1080, sur une page précise (`root`, `scenes`, `bonus`, `settings`, `credits`) ou sur toutes.
- Rendu par le même `MenuRenderer` que le reste : les éléments sont **fondus dans l'image de page**, donc visibles sur
  un DVD (dvdauthor/spumux) **et** sur un Blu-ray (menu BD-J), sans une ligne de code côté lecteur.
- Ordre : un élément peut passer **derrière les boutons** (panneau, cadre) ou devant (légende, pastille).
- Éditeur : ajout, sélection, déplacement à la souris sur l'aperçu, position / taille / couleur / bordure / opacité /
  police / alignement, cible de page, suppression. Le modèle s'enregistre dans les modèles de l'utilisateur (JSON).
- Ligne de commande : `--element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"` (clés :
  `kind, page, rect, text, file, font, size, color, align, bold, italic, outline, outlinew, fill, opacity, radius,
  border, borderw, fit, behind`).
- Épreuve : `check-elements` (10 vérifications), rendu d'un modèle DVDFab avec éléments (racine et scènes), **et un DVD
  réellement construit** avec dvdauthor + spumux dont le VOB de menu montre le texte et le panneau.

## 5. Reste ouvert, par ordre de valeur

| Question | Preuve qui manque | Où la chercher |
|---|---|---|
| Surbrillance DVD exacte d'après un modèle | fonctions qui écrivent `spumux_%2.xml` | `DVDCreator.dll`, `xrefs` sur `0x180022ee0` |
| Pages de réglages des modèles DVDFab Blu-ray | comment `setup` est lu et rendu | `BlurayDVDCreator.exe` (bloqué) ou directement le XML des modèles + rendu BurnSmith |
| Menu contextuel (pop-up) Yuhan | liaison `bf_menu_*` aux playlists de film | `BlurayDVDCreator.exe` (bloqué) |
| Menu HDMV natif (phase 3) | rien de neuf ici ; la voie reste celle de la feuille de route | `BDFix.dll` / `BlurayCreator.dll` déjà lus |
| Lecture sur lecteur de salon | une gravure et un lecteur | phase 0 de la feuille de route |

La limite qui domine reste la même : **aucun des trois produits n'a été exécuté et aucun de leurs disques n'a été
décodé**. Tout ce qui précède décrit du code et des fichiers, pas un comportement observé sur matériel.

## 6. Le script spumux de Yuhan, décompilé (2026-11-05)

C'est la réponse à la question restée ouverte au §1 : `FUN_18001b650` (orchestrateur DVD de `DVDCreator.dll`, 2 447 octets) engendre et lance les outils, et `FUN_180015840` écrit le script spumux d'une page.

**Orchestrateur `FUN_18001b650`** [REA, pseudocode lu en entier] :

- il calcule le nombre de pages de menu : 1 (accueil) + les pages de chapitres (`+0x48`) + un jeu de pages selon le mode `+0xe4` (0 = chapitres **et** réglages, 1 = réglages seulement, 2 = chapitres seulement, avec les compteurs `+0x50` et `+0x4c`), + les pages de bonus quand l'indicateur `+0x30` est levé ; si `+0xec` vaut 0, ce total remplace le compte de chapitres ;
- pour chaque page *i* : écrit `%1/spumux_%2.xml` (par `FUN_180015840`), puis lance `spumux.exe` avec trois arguments — le script, la page de menu MPEG d'entrée (liste `**param_2`), et `%1/menu_spumux_%2.vob` en sortie — dans un `QThread` dont il attend la fin (200 ms entre deux attentes) ; la sortie est ajoutée à la liste des VOB de menu (`param_1 + 0x10`) ;
- il écrit ensuite `%1/dvdauthor.xml` par `FUN_180009200` ou `FUN_180001640` selon `+0xcc`, puis lance **`dvdauthor.exe -w -o <destination> -x <dvdauthor.xml> -h <n>`** (ordre des arguments lu sur les `push`) ;
- pendant l'attente de dvdauthor il calcule une progression : taille du dossier de sortie `FUN_18001c660` sur la somme des tailles des entrées et des VOB de menu, en pour-mille (plafonnée à 10 000).

**Écrivain du script `FUN_180015840`** [REA, pseudocode lu en entier] — structure exacte, sans surprise mais avec deux détails qui comptent :

```xml
<subpictures>
  <stream>
    <spu force="yes" start="00:00:00"
         image="<liste +0x68>[page]"
         highlight="<liste +0x70>[page]"
         select="<liste +0x78>[page]">
      <button name="<liste +0x80>[i]" x0=".." y0=".." x1=".." y1=".."
              left=".." right=".." up=".." down=".."/>
    </spu>
  </stream>
</subpictures>
```

- **`highlight` et `select` sont des images de page entière**, une paire par page (listes `+0x70` et `+0x78`) : spumux prend, pour chaque bouton, la partie de l'image couverte par son rectangle. C'est exactement ce qu'il faut pour un modèle qui fournit `frame/0.dvd.sub.png` (survol) et `selection/0.png` (sélection) : on les colle à la place du bouton sur un canevas de la taille de la page, on quantifie en 4 couleurs, et on n'écrit qu'un `<button>` par bouton.
- **`image=` n'apparaît que dans un mode** : `si *(config+0xe8) == 2` (un troisième mode de menu, image unique pour la page). Les deux modes ordinaires ne l'écrivent pas.
- `force="yes"` et `start="00:00:00"` : la surbrillance est forcée dès le début, pas de plage d'affichage.
- **Rectangles** : `x0`/`y0`/`x1`/`y1` viennent d'un `QRect` par bouton (liste `+0x88`), en coordonnées de la page DVD, et `name` est le **numéro** du bouton (liste `+0x80`).
- **Voisins** : `left`, `right`, `up`, `down` sont résolus par `FUN_1800164a0(liste, bouton, direction)` avec des codes de direction constants que l'ordre des attributs fixe : **2 = gauche, 3 = droite, 0 = haut, 1 = bas** ; l'attribut reçoit le *numéro* du bouton voisin, pas son nom.

**Ce que cela change pour BurnSmith** : `Dvd.cs` écrit déjà la même structure (`<subpictures><stream><spu start highlight select force>` + `<button name x0 y0 x1 y1 up down left right>`, lignes 263 à 281) — la voie est donc la bonne. Il ne manque que **la source des deux images de page** : aujourd'hui `Dvd.cs` dessine un cadre plat à quatre couleurs (`hl.Frame` / `sel.Frame`), alors que le modèle fournit les images. Le reste (quantification, rectangles, voisins) est déjà en place.
### 6 bis. Ce que contiennent vraiment les images d'un modèle DVD DVDFab (mesuré)

Relevé des en-têtes PNG du modèle `templates\01movie` :

| Fichier | Taille | Format | Rôle que cela fixe |
|---|---|---|---|
| `frame/0.dvd.sub.png` | 390 × 260 | 8 bits, palette | état **survol** du bouton-vidéo, déjà en couleurs indexées : c'est la matière de l'image `highlight` |
| `frame/0.dvd.mask.png` | 390 × 260 | **1 bit**, palette | masque de forme du même bouton |
| `frame/0.dvd.normal.png` | 390 × 260 | RGBA | état repos, **dessiné dans la vidéo**, pas dans la surbrillance |
| `home/0.sub.png`, `left/0.sub.png`, `right/0.sub.png` | 180 × 180 | 8 bits, palette | états survol des boutons de navigation |
| `selection/0.png` | 180 × 180 | **2 bits**, palette | marqueur de sélection |
| `mask/0.normal.png` | 720 × 480 | RGBA | masque de page entier ; `mask/0.mask.png` en est la version 1 bit |

Conséquence, et c'est le point qui compte : les fichiers du modèle sont **par bouton**, alors que le `highlight` / `select` de spumux est **par page**. La composition est donc : un canevas de la taille de la page DVD (720 × 480 ou 720 × 576), chaque `.sub.png` collé à l'emplacement de son bouton (mis à l'échelle du rectangle), puis quantification en quatre couleurs ; le `select` se compose de la même façon avec le marqueur `selection/0.png` (ou le même cadre dans la couleur d'accent). C'est bien la structure que `FUN_180015840` écrit : une paire d'images par page, puis un `<button>` par bouton avec son rectangle et ses voisins.
## 11. Décompilation du menu : DVDFab (BD-J), Leawo (DVD et Blu-ray), Yuhan (2026-11-05, second passage)

Objectif de ce passage : aller au bout de la **création de menu et des templates** des trois suites, et non plus seulement de la navigation.

### 11.1 DVDFab : le menu est une application BD-J signée, lue en entier

`App\DVDFab13\BDMV\` porte `00000.jar` (75 025 octets, signé `SIG-BD00`) et `BDJO\00000.bdjo` qui désigne `org.homebrew.MenuTo`. Le jar contient **57 classes**, décompilées en Java lisible avec **CFR 0.152** (téléchargé depuis Maven Central : `org/benf/cfr/0.152`, exécuté hors ligne, `java -jar cfr.jar 00000.jar`). Les noms sont obfusqués (`g_0`, `i_0`, `n_0`, `p`, `w`, `o_0`…), les chaînes et la structure non.

**`AuthorData.xml`** (chemin `r.a + "AuthorData.xml"`, classe `m`) est le **format de projet du menu**. Racine :

| Clé | Sens |
|---|---|
| `playlist_count` | nombre de listes de lecture (films) |
| `loop_play` | lecture en boucle |
| `no_menu` | disque sans menu |
| `scence_capacity` | nombre de vignettes de scènes par page (orthographe du fichier) |
| `preview` | aperçu du film sur le menu principal |
| `play_mode` | mode de lecture |
| `resolution_x`, `resolution_y` | 1920 × 1080, ou 1280 × 720 |
| `menu_mode` | **0** = menus `root` et `scene` séparés ; **1** = un seul menu `root` « tout en un » ; **2** = un seul élément `scene` qui sert des deux (il doit y en avoir exactement un) |

Enfants : `root`, `scene`, `setup`. Le lecteur de XML (`ac`/`ad`) est un simple arbre balise → attributs/enfants.

**Menu `root`** (classe `G`, décompilée `g_0`) : `bg_image` (image pleine page), puis des enfants `button` et `video` :

- `button` : `id`, `type`, `highlight` (chemin de l'image de survol du bouton), rectangle par `left`, `top`, `right`, `bottom`, voisins `up_id`, `down_id`, `left_id`, `right_id` ;
- `video` : `id`, `highlight`, rectangle, voisins, et **`playlist`** (le bouton lance directement une liste de lecture) ; son `type` vaut 6 ;
- dessin : `g.a(bg_image, 0, 0, resolution_x, resolution_y)` pour le fond, puis chaque bouton dessine **sa propre image `highlight`** dans son rectangle ; la sélection passe par l'identifiant, la navigation par les quatre `*_id`.

**Menu `setup`** (classe `n_0`) : `item_height`, `lang_code`, `name`, `normal`, `highlight`, `selection_margin_left`, rectangles et `id` — donc une liste de langues (audio / sous-titres) avec une hauteur de ligne et une marge de marqueur, exactement ce que BurnSmith dessine aujourd'hui à la main.

**Menu « tout en un »** (classe `i_0`) : `bg_image`, `arrow_mode`, `description`, `item_height`, `mode`, `playlist`, `normal`, `highlight`.

**Entrée de l'application** (`MenuTo`) : `initXlet` lit **GPR100**, le remet à 0, et en tire une opération (`64176` → 0, sinon 1) ; `startXlet` lance d'abord la **liste de lecture 99** (le clip de fond du menu) sauf si `no_menu`, puis applique l'opération : **1** = afficher le menu (et, si un second registre n'est pas nul, reprendre au bouton qu'il désigne), **0** = lire les listes 0 à `playlist_count-1` à la suite, **2** = lire le titre courant d'après le numéro de titre BD-J. C'est le mécanisme de **reprise au dernier bouton** et de **lecture enchaînée** de DVDFab, et il tient dans les registres, pas dans le disque.

### 11.2 Leawo, DVD : `DVDMenuMgr`, 161 symboles décorés

`DVDMaker.dll` expose les noms C++ non obfusqués de sa classe de menu (relevés par expression régulière sur les symboles `?méthode@DVDMenuMgr@@…`, 460 symboles décorés au total). Ce que la classe **reçoit** décrit le menu :

`setBackgroundList`, `setHighLightImageList`, **`setSelectedImageList`**, **`setRectList`**, **`setIDList`**, **`setItemsPerPage`**, `setXmlList`, `setMenuDisabled`, `setLoopPlay`, `setHasMusic`, `setHasVideo`, `setFadeIn`, `setFadeOut`, `setMusicPath`, `setMusicLength`, `setImageVolume`, `setDvdAspect`, `setDvdStandard`, `setFrameRaite`, `setDvdAudioCodec`, `setDiscVolume`, `setMpegFileCaption`.

Ce qu'elle **fait** ensuite : `generateXml` (script dvdauthor) → `Img2m2v` / `Video2Mpg` / `mplex` → **`spumux`** → **`dvdAuthor`**, puis gravure par `ImgBurn`, `StarBurn`, `growisofs` ou `VSO` (`CreateIso`, `CheckDiscType`, `ComputeCappedBurnSpeed`…).

Deux conséquences, et elles répondent à une question restée ouverte :

1. Leawo a **deux listes d'images** : `highLight` (survol) et `selected` (choisi). C'est la paire que `spumux` attend (`highlight` / `select`) — le même schéma que Yuhan (§6) et que BurnSmith.
2. **La mise en page n'est pas dans la DLL** : elle arrive sous forme de `setRectList` + `setIDList` + `setItemsPerPage`. Les 27 thèmes sans `template.xml` n'ont donc pas de table de positions cachée dans un binaire : c'est le programme principal de Leawo qui la calcule à partir des images — exactement ce que fait BurnSmith en mesurant l'ouverture transparente des cadres. La recherche d'une table perdue est close.

Les fonctions `setDVDCinava*` (filigrane Cinavia) existent bien ; elles restent hors périmètre, comme convenu.

### 11.3 Leawo, Blu-ray : `BDFix.dll`, l'API de menu en clair

`CBDMenu` : `width`, `height`, `frameRate`, `videoFile` (clip de fond), `audioFiles`, `addButton`, `createButton`, `button`, `buttonCount`. `CBDButton` : **`selectedImage`**, **`activatedImage`**, `buttonHeader`, `addCommand`, `command`, `commandCount`. `CBDMixInfo` : `addMenu`, `addVideo`, `createMenu`, `createBDMovie`, `menuCount`, `videoCount`, `videoMarks`, `setDVD2BD`, `setUHD`, `dispositionIndex`, `metaObject`, `errorMessage`, `onProgress`.

C'est le modèle IG complet : un menu = un clip de fond + de l'audio + des boutons, chaque bouton portant **deux états d'image** et sa liste de commandes.

### 11.4 Ce que cela change pour la reproduction

| Élément des trois suites | BurnSmith aujourd'hui | Écart réel |
|---|---|---|
| Fond de menu + image de survol **par bouton** | oui (DVD et BD-J) | — |
| Deux états d'image (survol / choisi) | DVD : survol du modèle, « choisi » = silhouette blanche ; BD-J : une seule image | **écart** : l'état « choisi » n'utilise pas `selection/0.png` |
| Rectangles + voisins par bouton | oui | — |
| Menu de réglages par listes (langues, hauteur de ligne, marge) | oui (DVDFab `audiolist`) | — |
| Reprise au dernier bouton | non | **écart** (DVDFab : registre ; Yuhan/HDMV : `SET_BUTTON_PAGE`) |
| Liste de lecture 99 comme fond de menu | non (BurnSmith brûle un clip par page) | écart de conception, sans gain visible |
| Modes de menu 0/1/2 (`root` + `scene`, tout-en-un, scène en racine) | partiellement (« Streaming ») | écart d'ergonomie |
| Pipeline DVD `spumux` + `dvdauthor` | oui, à l'identique | — |
### 11.5 Reproduit : l'état « choisi » d'un bouton DVD vient du modèle (2026-11-05)

Écart comblé, avec ce que la décompilation a montré (Leawo : `setHighLightImageList` **et** `setSelectedImageList` ; DVDFab : `selection/0.png` ; Yuhan : attributs `highlight` et `select`).

- `MenuButton.DvdSelectImage` porte le marqueur du modèle (`<selection>` du `template.xml`, lu par `InstalledTemplate.Dvd`, devenu méthode d'instance pour atteindre `common`).
- `Dvd.cs` rastérise deux images par bouton : le survol (index 1, teinte moyenne de l'artwork) et l'état choisi (index 2). Le marqueur **garde sa taille naturelle**, centré sur le bouton : `selection/0.png` est un glyphe de 180 × 180 dont 4 375 pixels sur 32 400 sont opaques (une seule couleur, 158,114,217) ; l'étirer à la boîte d'un bouton de 390 × 260 en faisait un bloc plein. Sans marqueur, on retombe sur la silhouette du survol, comme avant.
- Nouveau `MenuRenderer.Size` (taille d'une image, null si illisible) ; `check-highlights --out DOSSIER` écrit les deux images de page telles que spumux les recevra.

**Épreuves** : `check-highlights` 8/8 ; `sel_scenes1.png` montre désormais un petit marqueur à chaque bouton (531 octets) au lieu des blocs précédents (628 octets, même page) ; un DVD construit avec `01movie` passe spumux et dvdauthor (« disc structure checked: no problem ») ; `check-elements` 14/14.
## 12. DVDFab : le modèle de menu du créateur Blu-ray, en clair dans l'exe (2026-11-05)

`DVDFab64.exe` (93 Mo) n'est pas décompilable par le pont REA (délai), mais il contient son **modèle de données QML en clair**, et cela suffit à documenter la création de menu. Trois relevés, faits par recherche d'octets et extraction des chaînes voisines :

### 12.1 Les énumérations du créateur (`BDCQmlNS`, à 0x3B70296)

```
SceneType.RootMenu, ThumbnailScene, PlaylistScene, SetupMenu, AudioSetupMenu, SubtitleSetupMenu
CreatorItemType.Item_Video, Item_Button, Item_Picture, Item_Text, Item_Playlist,
                Item_TSceneTitle, Item_SetupAudio, Item_SetupSubtitles, Item_TextButton
ButtonType2.Button2_None, Button2_Play, Button2_Scene, Button2_Setup, Button2_AudioSetup,
            Button2_SubtitleSetup, Button2_Left, Button2_Right, Button2_Home, Button2_Back
ECreatorTempType.StaticBacKground_StaticThumbnail, StaticBacKground_DynamicThumbnail, DynamicBacKground_StaticThumbnail
ECreatorButtonType.TextButton, ImageButton
EButtonSelectedHighlightType.DrawBox, DrawUnderline, ChangeTextColor
EBacKgroundType.Default_StaticBacKground, Default_DynamicBacKground, Custom_StaticBacKground, Custom_DynamicBacKground, Crop_DynamicBacKground
```

C'est le modèle complet : **six sortes de pages** (racine, scènes en vignettes, scènes en liste de lecture, réglages, réglages audio, réglages sous-titres), **neuf sortes d'éléments**, **dix rôles de bouton** (les mêmes que `buttontype` dans `template.bd.xml`), **trois genres de modèle** (fond fixe + vignettes fixes, fond fixe + vignettes animées, fond animé + vignettes fixes), **deux genres de bouton**, **trois styles de surbrillance** (cadre, soulignement, changement de couleur du texte) et **cinq genres de fond**.

Deux enseignements qui manquaient : DVDFab propose **des vignettes animées** (un extrait qui tourne dans la case de la scène) et **trois styles de surbrillance** réglables. BurnSmith n'a ni l'un ni l'autre (voir §15).

### 12.2 Le schéma `template.bd.xml`, tel que le lit le programme (125 attributs QML)

Le créateur charge ses modèles par du QML (`author/BDAuthor.qml` dans l'exe) et les décrit avec des liaisons `<xmlattr>`. Les noms relevés dans tout l'exe donnent le vocabulaire exact du fichier :

- en-tête : `template_version`, `templatetype` (cf. `ECreatorTempType`), `template_category`, `templatename`, `templateid`, `parentid`, `template_is_for_bd`, `template_is_for_uhd`, `buttontype`, `font`, `font_size`, `font_color`, `font_bold`, `font_italic`, `font_underline`, `list_hover_bg_color`, `list_hover_fg_color`, `pscene_list_hover_bg_image`, `selected_default_style_color`, `selected_custom_style_color`, `bdmv` ;
- images de bouton : `playbuttontimage`, `scenebuttontimage`, `setupbuttontimage`, `leftbuttontimage`, `homebuttontimage`, `rightbuttontimage`, `frame`, `mask`, `selection` ;
- pages : `rootmenu`, `scenes`, `setup`, `pscenes`, `tscenes`, `videoes`, `video_frame`, `description_frame`, `pics`, `texts`, `buttons`, `extra_pics`, `extra_texts`, `specialvideo`, `nomenu` ;
- géométrie et style : `rect`, `frame_rect`, `move`, `remove`, `count`, `top`, `left`, `bottom`, `right`, `up_id`, `down_id`, `left_id`, `right_id`, `item_height`, `line_height`, `arrow_mode`, `alignment`, `horizontal_alignment`, `vertical_alignment`, `wrap`, `stroke_color`, `stroke_width`, `radius`, `pscenes_picture_radius`, `hover`, `normal`, `select`, `highlight`, `mode`, `type`, `image`, `text`, `tr_lang`, `mul_scene_op`, `videoCount`, `video_count`, `pic_num`, `bg_playlist`, `playing_time`…
- conditions : `enable_when_output_res` (un élément ne s'affiche qu'à une résolution donnée), `cite`, `as` (liaison QML), `state`, `valid`, `normal_valid`.

### 12.3 Où le programme range ses propres fichiers

`AuthorData.xml` (menu engendré), `author/templates` (DVD), `author/bdtemplates` (Blu-ray, plus `/nomenu/background.png`), `author/BDAuthor.qml`, et `MenuTemplate` (modèles téléchargés). La chaîne `No menu.` et `bd-fulldisk:` accompagne les disques sans menu.

### 12.4 Le menu BD-J : registres et types de bouton (jar décompilé, suite du §11)

Lecture complète des classes de menu ; ce qui manquait au §11 :

| Élément | Valeur observée |
|---|---|
| **Types de bouton** (attribut `type`) | **0** = lire tous les titres · **1** = aller aux scènes · **2** = aller aux réglages · **3** = menu audio · **4** = menu sous-titres · **5** = menu principal · **6** = lire la playlist `playlist` |
| **GPR100** | numéro de reprise ; lu au démarrage, `64176` = pas de reprise ; écrit par un bouton « lire » avec `playlist + 1` |
| **GPR108** | dernier bouton choisi (« last video stream » dans le code), relu au retour au menu |
| **GPR16 / GPR17** | dernière piste audio / dernier sous-titre choisis |
| **GPR15** | drapeau « choix audio initialisé » (0 = pas encore) |
| **PSR4** | numéro du titre courant (menu « tout en un » qui liste les titres) |
| **Mode de lecture** | `play_mode` 1 = jouer le titre choisi seul, sinon jouer depuis le titre choisi jusqu'à la fin (`playlist_count - n` éléments) |
| **Aperçu** | `preview` ≠ 0 : les boutons « lire » sont désactivés (`m.g`) |
| **Pages de scènes** | deux modes : `mode=0` = une page `g_0` classique, `mode=1` = page en **liste** (classe `o`, qui lit un nœud `infos` avec, par `info`, `video_id` et `image`), c'est le `PlaylistScene` du §12.1 |
| **Réglages** | nœuds `audios` et `subtitles` avec `top/left/bottom/right` par colonne, `selection_margin_left`, panneaux `au_panel_n` / `su_panel_n` |

Le comportement « reprise » est donc : un bouton de lecture écrit GPR100 = playlist+1 et GPR108 = son rang ; au retour, `MenuTo` relit GPR100, choisit l'opération 1 (menu) et `p.a(n)` réaffiche le menu avec le bouton mémorisé.

## 13. Yuhan : le schéma complet des thèmes, déchiffré sur les 30 (2026-11-05)

La clé AES de `BlurayDVDCreator.exe` (32 octets à l'octet de fichier **0x192420** : clé 16 octets puis IV 16 octets) ouvre les 30 `PresetMenu\THEMExx\config.dat`. Le clair commence par une longueur 16 bits petit-boutiste, puis le XML. Chaque fichier porte **quatre branches** `TemplateBranch` (768x576, 1280x720, 1920x1080, 3840x2160).

**Vocabulaire relevé sur les 30 thèmes** — nœuds : `MenuTemplate`, `TemplateBranch`, `Color`, `Menu`, `Scene`, `Setup`, `Background`, `Frame`, `ThumbFrame`, `Thumb`, `Thumb0..9`, `Frame0..9`, `TitleText`, `PlayText`, `ScenesText`, `SetupText`, `BonusText`, `SetupAudioText`, `SetupSubtitleText`, `ListTypeText`, `ItemText0..9`, `LangItemText`, `ASLangTitleText`, `ThumbText0..9`, `ButtonNext`, `ButtonPre`, `Return` — attributs : `Name`, `Type`, `Resolution`, `FileName`, `Left`, `Top`, `Width`, `Height`, `Alignment`, `Color`, `Size`, `Bold`, `Italic`, `Underline`, `Hover`, `Select`, `encoding`, `version`.

**Ce que chaque thème porte réellement** (compté sur les 30) :

| Élément | Constat |
|---|---|
| `BonusText` | **les 30 thèmes** ont une page Bonus (bouton et libellé) |
| `ListTypeText` + `ItemText0..9` | **les 30** ont le mode **liste** (10 lignes), en plus des vignettes |
| `ThumbFrame` / `Thumb` / `ThumbText` | 3 à 10 vignettes par page selon le thème (12 à 39 `FrameN` comptés sur les quatre branches) |
| `SetupAudioText`, `SetupSubtitleText`, `ASLangTitleText` | page de réglages décrite (deux colonnes + titre de langue) |
| `Color` (`Text`, `Frame`, `Button` avec `Hover` et `Select`) | trois jeux de couleurs par thème, deux états chacun |
| `ButtonNext`, `ButtonPre`, `Return` | images des boutons de navigation, avec leurs rectangles |

C'est un schéma **plus riche que ce que BurnSmith dessine** pour ces thèmes : voir §15.

## 14. Leawo : structure des modèles et où vit la mise en page

Chaînes de `Leawo Prof. Media.exe` :

```
%1MenuTemplate/%2/%3/%4/%5/%6        %1MenuTemplate/%2/%3/%4/Menu        %1MenuTemplate/%2/%3/%4/Scene
/Template.xml                        /DownloadTemplate.xml               "Template.xml" not found.
%1/spumux_%2.xml                     ?setHighLightImageList@DVDMenuMgr@@  ?setSelectedImageList@DVDMenuMgr@@
```

Donc : un modèle = `MenuTemplate\<Type>\<Aspect>\<Thème>\{Menu,Scene}` plus un `Template.xml` (celui des 4 dossiers qui en ont un) ; le téléchargement écrit `DownloadTemplate.xml`. Le nommage `spumux_%2.xml` est **le même que chez Yuhan** (§6) : la lignée est la même.

`panda.dll` porte `[BDJNeutral] firstplay_type=%d topmenu_type=%d jars=%d unsigned_jars=%d` et `[BDJNeutral] neutralization complete: FirstPlay/TopMenu -> HDMV obj0 -> PlayPL(%05d)` : Leawo **neutralise** les disques Blu-ray à menu BD-J en réécrivant le premier lancement et le menu principal vers un objet HDMV et une playlist. C'est une fonction de conversion de disque (BDFix), pas de création ; elle reste hors périmètre.

## 15. Écarts établis entre les trois suites et BurnSmith (à reproduire)

| Fonction, prouvée par la décompilation | BurnSmith | Où c'est prouvé |
|---|---|---|
| Vignettes **animées** dans la page de scènes | non | `ECreatorTempType…DynamicThumbnail`, `videoCount` |
| Page de scènes en **liste** (10 lignes + description) | **fait** (§15.2) | `ListTypeText`/`ItemText0..9` (30 thèmes Yuhan), `PlaylistScene`, classe `o` du jar |
| **Page Bonus** décrite par le modèle | partielle (BurnSmith dessine la sienne) | `BonusText` sur les 30 thèmes |
| **3 styles de surbrillance** (cadre, soulignement, changement de couleur) | **fait** (§17) | `EButtonSelectedHighlightType` |
| Bouton **« choisir la langue »** de la page de réglages | partiel | `LangItemText`, `ASLangTitleText` |
| **Reprise au dernier bouton** | **fait** (§19, effet observé sur l'indice 0) | GPR100/GPR108, `MenuTo` |
| **Modes de menu** 0 / 1 / 2 | partiel | `menu_mode`, classes `g_0`, `i_0`, `y_0` |
| Vignettes du modèle **au-delà de 3** (4 à 10) | **oui, vérifié** : `YuhanSkin` lit `Frame0..N` et place chaque vignette à ses coordonnées (Natural 02 = 4 cases en 2 x 2, Birthday 01 = 3) | `Frame3..Frame9`, `Thumb3..Thumb9` ; `dump` des deux thèmes |
| Images de survol **et** de sélection par bouton | fait (commit `01ceddf`) | `selection/0.png`, `setSelectedImageList` |

Rien n'a été reproduit dans ce passage : cette note est le cahier des charges. Les artefacts de travail (sources Java décompilées, XML de thème, CFR) sont dans `analysis\`, hors dépôt.
### 15.1 Reproduit : le bouton Bonus des thèmes Yuhan (2026-11-05)

`YuhanSkin` lit maintenant `Menu/BonusText` (présent dans les **30** thèmes, §13) et l'émet comme bouton-texte ; `InstalledTemplate.Root` l'utilise au lieu d'ajouter une pastille. Vérifié par rendu : sur `Birthday 01`, « Bonus » se place à **1494, 720**, sous « Settings » (1494, 630), dans la police et la couleur du thème — la position que le modèle donne, et non plus une capsule de BurnSmith.

Reste de ce côté : le **mode liste** (`ListTypeText` + `ItemText0..9`) et la page de réglages décrite par `SetupAudioText` / `SetupSubtitleText` / `ASLangTitleText`, que `YuhanSkin` n'exploite pas encore.
### 15.2 Reproduit : la page de scènes en liste (2026-11-05)

`MenuDesign.SceneList` bascule la page de scènes du modèle en **liste de lignes** : `YuhanSkin` émet un nœud `list` avec les rectangles des `ItemText0..9` (police, taille, couleur, gras, alignement du thème), et `InstalledTemplate.Scenes` y place un bouton par chapitre ou par épisode, sans cadre ni vignette. La pagination suit : dix lignes par page, comme le modèle.

**Épreuves** : rendu de `Birthday 01` avec `--list` et dix épisodes — les dix lignes tombent à x = 540, y de 270 à 810, dans la police du thème, avec le bouton Retour du thème en dessous ; `check-elements` 15/15 (nouvelle vérification : dix boutons en liste là où la grille n'en tient que quelques-uns, tous à la même abscisse) ; `check-templates` inchangé, le mode grille restant celui par défaut.

Reste de ce côté : la page de réglages décrite par `SetupAudioText` / `SetupSubtitleText` / `ASLangTitleText`, et les **vignettes animées** de DVDFab (`DynamicThumbnail`).
## 16. Yuhan : l'exécutable décompilé (Ghidra en ligne de commande), et une reproduction bloquée (2026-11-05)

Le pont REA refuse `BlurayDVDCreator.exe` (1,7 Mo, délai) : j'ai donc utilisé **Ghidra en ligne de commande**, comme le prévoit le guide. Script `analysis\ghidra\DumpMenuFuncs.java` (décompile toute fonction référençant une chaîne de menu), lancé par `analyzeHeadless` ; analyse réussie, **10 fonctions** rendues dans `analysis\yuhan-exe-menu.txt`.

### 16.1 Ce que l'exécutable fait vraiment

| Chaîne | Fonction | Ce qu'elle fait |
|---|---|---|
| `%1/config.dat` | `FUN_1400931f0` | lit le fichier, **saute les 2 premiers octets** (`setRawData(buf + 2, n)`), puis **parse le XML avec `QDomDocument`** et parcourt les enfants en comparant l'attribut **`Resolution`** |
| `%1/PresetMenu/%2` | `FUN_140078660` | construit le dossier du thème, avec `%1x%2` pour la résolution (« 1920x1080 ») |
| `%1/PresetMenu/%2/%3` | `FUN_140059730` | construit le chemin de la vignette : `thumb_169.jpg` / `thumb_43.jpg`, avec repli sur les images **`no_menu_small`**, `no_menu_small_169`, `no_menu_small_43` |
| `no_menu_large` | `FUN_14006e380` | dessine l'aperçu « pas de menu » (fond `background-color:#808080;`, texte) |
| `menu_add_text`, `menu_add_pic`, `prev_arrow`, `next_arrow`, `play`, `stop`, `browse` | `FUN_140056b20` | construit la **barre d'outils de l'éditeur de menu** |
| `%1/menu_%2.m2ts`, `%1/bf_menu_%2.m2ts`, `%1/menu_0.vob`, `%1/menu_%2.vob` | `FUN_14001ca10`, `FUN_140017fe0`, `FUN_140040320`, `FUN_140042480` | écriture des clips de menu (Blu-ray et DVD) |

**Deux constats qui comptent.**

1. Le déchiffrement de `config.dat` **n'est pas** dans la fonction qui lit le fichier : elle reçoit un tampon déjà en clair et saute l'en-tête de 2 octets. La clé AES reste dans l'exécutable (octet `0x192420`, §13) et le déchiffrement se fait en amont. Cela confirme l'implémentation de `YuhanSkin` (même en-tête de longueur, même XML, même sélection par `Resolution`).
2. **L'éditeur de menu de Yuhan se réduit à « ajouter du texte », « ajouter une image », page précédente / suivante, lecture et arrêt.** Aucun bouton libre, aucune mise en page par glisser-déposer. C'est bien moins que DVDStyler, et cela recadre l'ambition : ce que BurnSmith offre déjà (éléments libres + boutons à action) dépasse Yuhan.

### 16.2 La page de réglages : une reproduction volontairement non faite

Le `<Setup>` de chaque branche porte **17 enfants** :

```xml
<Setup>
  <Background FileName="bg_menu.jpg" …/>
  <SetupAudioText    Left="850" Top="400" Size="54"/>      <!-- libellé AUDIO -->
  <SetupSubtitleText Left="850" Top="540" Size="54"/>      <!-- libellé SOUS-TITRES -->
  <ASLangTitleText   Left="750" Top="150" Size="45"/>      <!-- titre de la page -->
  <LangItemText>                                            <!-- 8 lignes, x = 750 -->
    <ItemText0 Left="750" Top="270" Size="36"/>
    <ItemText1 Left="750" Top="345" Size="36"/>  … ItemText7 Top="795"
  </LangItemText>
  <ButtonPre  Left="627" Top="900" Width="64" Height="64"/>
  <Return     Left="927" Top="900" Width="64" Height="64"/>
  <ButtonNext Left="1227" Top="900" Width="64" Height="64"/>
</Setup>
```

C'est **une liste unique de huit langues** à x = 750, pas deux colonnes : les deux libellés (y = 400 et 540) tombent **sur** les lignes 2 et 4 de cette liste. Le modèle décrit donc une page où la liste et les libellés ne sont pas affichés ensemble (ou dont les libellés servent dans un autre mode), et il n'y a **aucune place** pour deux colonnes de six et six lignes.

Une reproduction « deux colonnes sous les deux en-têtes » — ce que proposait la délégation — aurait donné des lignes traversant l'en-tête des sous-titres et une dernière ligne hors écran (y = 1168 pour 1080). **Je ne l'ai pas appliquée.** Reproduire cette page fidèlement demande un modèle que BurnSmith n'a pas : soit des sous-pages audio / sous-titres (Yuhan écrit d'ailleurs des clips séparés, `setup_audio_%2.m2ts` et `setup_subtitle_%2.m2ts`, §2), soit une pagination de la liste. C'est une tâche de conception, pas un correctif.

### 16.3 État de la délégation

Trois tentatives de faire écrire Claude Code dans ce dépôt ont échoué de la même façon : `Edit` et `Write` sont refusés par son bac à sable (« don't ask mode »), et `dotnet` aussi. Les deux dernières ont rendu des **patchs utilisables** (le second avec une analyse juste de la chaîne d'appel `HasSetupLists` → `MenuBuilder.Settings`), mais rien n'a pu être appliqué ni compilé par lui. Tant que ces droits ne sont pas ouverts, la reproduction reste faite par l'orchestrateur.
## 17. Reproduit : les trois styles de surbrillance de DVDFab (2026-11-05)

`EButtonSelectedHighlightType` (§12.1) énumère `DrawBox`, `DrawUnderline`, `ChangeTextColor`. BurnSmith n'en avait qu'un (le cadre d'accent).

- `MenuDesign.HighlightStyle` : `box` (défaut, le cadre d'accent), `underline` (une barre sous le bouton), `textcolor` (le libellé redessiné en couleur d'accent, sans cadre). Repris par `TakeLook`, donc un modèle qui l'a réglé le transmet.
- `MenuRenderer.Focus` dessine les trois ; il reçoit le libellé du bouton, que `MenuFiles.WriteBdj` retrouve dans les opérations de la page (`LabelOf` : l'étiquette dont le rectangle est celui du bouton) — c'est ce qui permet à `textcolor` d'exister, une image de focus ne connaissant pas le texte par ailleurs.
- Éditeur : liste « Surbrillance du bouton choisi » ; ligne de commande : `--highlight box|underline|textcolor`.
- Un modèle qui apporte sa propre image de survol garde la priorité : les styles ne concernent que les boutons sans artwork.

**Épreuves** : `check-highlights` 11/11, dont « trois styles distincts, tous dessinés », « le style texte en couleur est plus léger que le cadre » (2 218 octets contre 2 349) et « le menu Blu-ray reçoit l'image du style choisi, libellé compris » (fichier `hl_root_play.png` réellement écrit par `MenuFiles.WriteBdj`). Images côte à côte dans `analysis\out\styles` : le soulignement fait 387 octets (une barre), le texte en couleur 2 218 (le mot « Play » en accent).
## 18. `AuthorData.xml` : le schéma complet, tiré du jar (2026-11-05)

Relevé attribut par attribut dans les classes décompilées. C'est le format de projet que DVDFab écrit pour son menu BD-J, et il correspond trait pour trait à ce qu'un menu HDMV devrait porter.

```xml
<AuthorData>                     <!-- classe m : a = playlist_count, c = loop_play, d = no_menu, b = scence_capacity,
                                      g = preview, h = play_mode, e/f = resolution_x/y, i = menu_mode -->

  <!-- menu_mode 0 : un <root> et un <scene> ; 1 : un seul <root> « tout en un » ; 2 : une seule <scene> servant de racine -->
  <root id="0" bg_image="…">                     <!-- classe g_0 (G) -->
    <button id="0" type="6" highlight="play.png" left="…" top="…" right="…" bottom="…"
            up_id="…" down_id="…" left_id="…" right_id="…"/>
    <video  id="1" type="6" highlight="v1.png"   left="…" top="…" right="…" bottom="…"
            up_id="…" down_id="…" left_id="…" right_id="…" playlist="3"/>
  </root>

  <scene>                                        <!-- classe h_0 : une page par enfant, mode 0 = grille, 1 = liste -->
    <page mode="0" id="0" bg_image="…">          <!-- mode 0 : classe g_0, mêmes enfants que <root> -->
      <button …/> <video …/>
    </page>
    <page mode="1" id="1" bg_image="…">          <!-- mode 1 : classe o, une liste de films -->
      <infos top="…" left="…" bottom="…" right="…">
        <info video_id="3" image="thumb3.png"/>
      </infos>
    </page>
  </scene>

  <setup selection_margin_left="100">            <!-- classe n_0 -->
    <audios top="…" left="…" bottom="…" right="…" item_height="80">
      <audio id="0" type="…" name="English" lang_code="1" normal="a.png" highlight="ah.png"/>
    </audios>
    <subtitles top="…" left="…" bottom="…" right="…" item_height="80">
      <subtitle id="0" type="…" name="Français" lang_code="2" normal="s.png" highlight="sh.png"/>
    </subtitles>
  </setup>
</AuthorData>
```

Détails qui ne se devinaient pas :

- **Le marqueur de piste active n'est pas dans le XML** : `n_0` charge `selection.png`, un nom **fixe**, dans le dossier du menu (à côté des images du modèle).
- **Chaque entrée audio / sous-titre porte ses deux images** (`normal`, `highlight`) et son `lang_code` numérique ; `type` est le genre de piste.
- **Le menu « tout en un »** (classe `i_0`) a ses propres enfants : `video_frame` (`top`, `bottom`, `left`, `right`, `item_height`, `arrow_mode`), `description_frame`, `videos` → `video` (`id`, `highlight`, `playlist`), et un `button` ; c'est le `PlaylistScene` de §12.1.
- **La page de réglages défile dans chaque colonne** (classe `c`) au lieu de paginer : deux panneaux (`n_0.e` audio, `n_0.f` sous-titres), un compteur par colonne, et le choix part par `SET_STREAM`. C'est le modèle que BurnSmith devrait imiter s'il veut dépasser ses six pistes audio.

## 18.1 Preuve de bout en bout du style de surbrillance (même date)

`burnsmith bd --design "Cinema Dark" --highlight textcolor` sur un film d'essai produit un disque Blu-ray complet (`disc structure checked: no problem`) dont le **`BDMV\JAR\00000.jar` contient `hl_root_play.png` de 2 218 octets** — exactement la taille mesurée pour le style « texte en couleur » (§17), et non les 2 349 du cadre. Le réglage traverse donc tout la chaîne : ligne de commande → `MenuDesign` → `MenuRenderer.Focus` → `MenuFiles.WriteBdj` → jar du disque.
## 19. Reproduit (partiellement prouvé) : la reprise au dernier bouton (2026-11-05)

DVDFab garde le bouton de départ dans **GPR108** et le relit au retour au menu (§12.4). Même mécanisme dans le xlet de BurnSmith (`bdj\src\org\burnsmith\menu\Menu.java`) :

- `remember(b)` écrit `GPR108 = index + 1` (0 = rien de mémorisé, sinon le premier bouton passerait toujours pour « mémorisé ») ;
- `show(firstPage)` relit le registre et remet le focus sur ce bouton, en journalisant `resume on button N` ;
- `readGpr` / `writeGpr` avalent l'échec : un lecteur sans accès aux registres garde un menu fonctionnel, il oublie seulement la position ;
- la mémoire ne vaut que pour la **page d'accueil** : un indice de la page de scènes appliqué à la racine désignerait un autre bouton (défaut trouvé en essai, corrigé).

**Preuves** : `bdj\build.ps1` reconstruit `assets\bdj\menu.jar` (10 907 octets, classes majeures **47** = Java 1.3) ; `javap` montre `bipush 108`, `RegisterAccess.getGPR/setGPR` et les trois appels à `remember` ; le disque Blu-ray construit embarque ce `Menu.class` (14 690 octets dans le jar du disque). En lecture dans le VLC portable (libbluray, JDK 8) : le journal donne `page root` → `play playlist 1` → `playback ended` → `page root` → **`resume on button 0`**.

**Ce qui n'est pas prouvé** : la différence de bouton. Les touches de navigation envoyées par l'interface RC de VLC (`key-nav-right`, `key-nav-down`) n'ont pas déplacé le focus dans cet essai (le journal joue toujours le bouton 0), donc la reprise a été observée sur l'indice 0, celui qui était mémorisé. La mécanique (écriture, lecture, réapplication) est établie ; le cas « on revient sur un autre bouton » ne l'est pas.
## 20. DVDFab : le modèle de l'éditeur de menu, et où il n'est pas (2026-11-05)

### 20.1 Question close : ce n'est dans aucune DLL

Recherche des littéraux de création de menu (`spumux`, `dvdauthor`, `AuthorData`, `template.bd.xml`, `bdtemplates`, `selection.png`, `menu_%`) dans **toutes** les DLL de 1 à 15 Mo du dossier `App\DVDFab13` : seule `xdisc.dll` (11,8 Mo) en contient un (`menu_%`), et le contexte montre que c'est le **lecteur** de disques — `pathplayer`, `VTS_%02d_?.VOB`, `button: vts_%d menu_%d pgc_%d cell_%d`, `pathplayer: button stack begin`. Aucune DLL ne porte `spumux` ni `dvdauthor` : la création de menu de DVDFab est **dans l'exécutable** (93 Mo), avec son interface QML `author/BDAuthor.qml` et le jar BD-J.

### 20.2 Analyse complète de l'exécutable : tentée, abandonnée

Ghidra en ligne de commande sur `DVDFab64.exe` (93 Mo) : **34 minutes de CPU, 1,6 Go de mémoire, aucune fin** — arrêté. C'est une limite de moyens, pas une conclusion sur le fichier : le programme n'est ni packé ni protégé à ce qu'on en voit (ses chaînes QML sont en clair, §12). Pour l'explorer il faudrait une machine plus large ou une analyse ciblée par adresse, hors de ce qui est faisable ici.

### 20.3 Ce que l'exécutable dit de l'éditeur de menu (extraction ciblée)

Balayage des 4 Mo autour de la zone QML (`0x2D80000`–`0x3180000`), jetons filtrés. Ce sont les **propriétés du modèle de l'éditeur**, donc la liste de ce que DVDFab sait régler :

| Famille | Propriétés relevées |
|---|---|
| Pages du disque | `has_root_page`, `has_setup_page`, `has_separate_audio_subtitle_page`, `no_menu`, `none_setup`, `no_setup_btn`, `is_uhd_menu`, `menu_mode`, `scenes_all_in_one` |
| Fond | `background_type`, `menu_background_type`, `menu_background_video_type`, `use_menu_background_video`, `still_image`, `image_video`, `frame_video`, `bg_playlist`, `bg_music`, `covering_color`, `background_cover` |
| Surbrillance | `selection_color_index`, `selection_margin_left`, `selection_pos`, `selected_default_style_color`, `selected_custom_style_color`, `selected_default_transparency`, `selected_custom_transparency`, `frame_hover`, `frame_selected`, `box_hover`, `btn_style`, `btn_style_type_1`, `btn_style_type_ok`, `ui_style`, `skin_color_name` |
| Pages de scènes | `video_per_scene`, `mul_scene_op`, `scenes_width`, `scenes_height`, `scenes_left`, `scenes_top`, `scenes_font_size`, `play_font_size`, `pic_num`, `pscenes`, `pscene_list_hover_bg_image`, `pscenes_picture_radius`, `tscenes` |
| Textes et images libres | `extra_texts`, `extra_pics`, `texts`, `pics`, `specialvideo`, `title_widget`, `photo_list`, `title_list`, `audio_list`, `subtitle_list` |
| Actions de l'éditeur | `updateTemplate()`, `slot_saveTemplate`, `slot_saveTemplateFinish`, `signal_templateCountChange`, `msgbox_delete_template`, `CustomizeSetDialog`, `open_menu_background_dlg`, `editImageBg`, `editVideoBg`, `editBG` |
| États « actif » des boutons du menu | `rootmenu_play_avtive`, `rootmenu_scenes_avtive`, `rootmenu_setup_avtive`, `setup_home_avtive`, `setup_play_avtive` (fautes de frappe de l'éditeur, recopiées telles quelles) |
| Sauvegarde d'un modèle | UUID de modèle, `slot_saveTemplate(QString)`, `slot_saveTemplateFinish(QString, QString, QString)`, `thumb4x3.bd.png`, `backgroud1.png` (faute là encore), message « Oops! This name already exists. » |

Trois choses que cela établit et qui manquaient :

1. **DVDFab distingue trois natures de fond** — image fixe, image animée, vidéo plein cadre (`still_image` / `image_video` / `frame_video`) — et un drapeau `use_menu_background_video`. BurnSmith n'a que « image » ou « boucle vidéo ».
2. **La surbrillance est réglée par style et par transparence** (`selected_default_style_color` + `selected_default_transparency`, et une variante « custom »), ce qui complète les trois styles du §12.1 : le modèle porte aussi l'opacité.
3. **Un modèle sauvegardé garde sa vignette 4:3** (`thumb4x3.bd.png`) et son fond (`backgroud1.png`) à côté de son XML — la structure exacte des dossiers `author/templates` et `author/bdtemplates` observée sur le disque.