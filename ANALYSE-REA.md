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