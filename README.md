# BurnSmith

Créateur de Blu-ray (et DVD, Blu-ray 4K) libre pour Windows : on choisit une ou plusieurs vidéos, on dessine le menu, on obtient un dossier de disque, une image ISO, ou on grave directement ; on peut aussi graver un ISO existant. L'application est un **seul `BurnSmith.exe` portable** qui embarque ffmpeg et tsMuxeR ; la gravure et la création d'ISO passent par Windows (IMAPI2), rien à installer. Licence : GNU GPL v3 ou ultérieure.

> Projet jeune. Les dossiers BDMV générés se lisent dans VLC et les ISO se montent sous Windows ; la **gravure sur disque réel et la lecture sur lecteurs de salon n'ont pas encore été testées**.

## Ce que ça fait

- DVD (PAL/NTSC, MPEG-2 + AC-3), Blu-ray (H.264, copie si la source est déjà conforme), Blu-ray 4K (HEVC 2160p).
- Plusieurs vidéos (séries), choix des pistes audio et sous-titres, page de réglages dans le menu.
- Fenêtre construite comme celle de DVDFab Créateur (liste des vidéos avec durée / HDR / audio / sous-titres, carte du projet, capacité du disque, sortie dossier / ISO / graveur, progression et annulation de la gravure, effacement d'un disque réinscriptible).
- Éditeur de menu avec aperçu en direct et **bibliothèque d'éléments** : 49 modèles livrés (DVD Forge, DVDStyler), plus ceux de DVDFab et de Leawo Prof. Media installés sur le PC ; on compose son menu en choisissant fond, police, style de boutons, grille et couleurs, et on enregistre ses propres modèles (voir ci-dessous).
- Menus Blu-ray en BD-J (un xlet générique + images), menus DVD par dvdauthor/spumux.
- Barre de capacité (DVD-5/9, BD-25/50/66/100), chapitres automatiques, création d'ISO (UDF), gravure.

## Utiliser

Télécharger `BurnSmith.exe` dans [Releases](https://github.com/Balrog57/BurnSmith/releases) (ou le compiler, voir *Compiler*) et le lancer. Au premier lancement, ffmpeg, ffprobe et tsMuxeR sont dépaquetés dans `%LOCALAPPDATA%\BurnSmith\tools` (quelques secondes, environ 440 Mo) ; un dossier `tools\` à côté de l'exe ou la variable `BURNSMITH_TOOLS` les remplacent si besoin.

Le mode **DVD** reste hors de l'exe : il demande `dvdauthor.exe` et `spumux.exe` (builds Windows dont la ligne de commande est `spumux script entrée sortie` et `dvdauthor -o dest -x script.xml -h 0 -q`), à placer dans `tools\` ou le `PATH`. Les licences des programmes embarqués sont dans `THIRD-PARTY-NOTICES.md`.

## Compiler

Prérequis : SDK .NET 10, un dossier contenant `ffmpeg.exe` et `ffprobe.exe` (dans le `PATH` ou `-FfmpegDir`) et `tools\tsmuxer\bin\tsMuxeR.exe` (ou `-TsMuxer`). Ces binaires ne sont pas dans le dépôt : `publish.ps1` les zippe depuis votre PC.

```powershell
.\publish.ps1      # produit dist\BurnSmith.exe (autonome, un seul fichier, environ 230 Mo)
```

Le menu Blu-ray est un xlet Java 1.3 (`bdj/`). `assets/bdj/menu.jar` est déjà compilé et embarqué dans l'exe ; `bdj\build.ps1` le reconstruit avec un JDK 8 (`tools\jdk8`).

Ligne de commande : `burnsmith bd|dvd --video f.mkv --out dossier [--design NOM|DOSSIER_DU_MODELE] [--title TEXTE] [--no-menu] [--chapters MIN] [--uhd]` ; `burnsmith preview NOM|DOSSIER sortie [--picture image.jpg] [--episodes N]` dessine toutes les pages d'un modèle ; `burnsmith drives` liste les graveurs vus par Windows ; `burnsmith iso dossier sortie.iso [--label X] [--dvd]` crée l'image UDF ; `burnsmith burn image.iso F:` grave (disque vierge) puis relit le disque ; `burnsmith verify image.iso F:` compare un disque à son image.

## Structure

- `src/BurnSmith.Core` : analyse, DVD, Blu-ray, ISO, menus (modèle, mise en page, rendu WPF).
- `src/BurnSmith.App` : application WPF (thème Fluent sombre).
- `src/BurnSmith.Cli` : même moteur en ligne de commande.
- `bdj/` : source du xlet de menu Blu-ray.
- `tools/vlctest.ps1` : lit un disque dans un VLC portable et affiche les traces du xlet.

## Modèles de menu et bibliothèque

Trois provenances, gardées séparées :

- **Livrés dans l'exe (libres)** : les 5 modèles de [DVD Forge](https://github.com/Nicolasalucin/dvd-forge) (GPL-3.0 ; seuls leurs paramètres sont repris : grille, couleurs, police, marges, le dessin est celui de BurnSmith) et les 49 modèles [DVDStyler](https://github.com/ntxalex/DVDStyler) (GPL ; fichiers `.dvdt` et images tels quels, convertis à l'exécution : positions mises à l'échelle, 4:3 centré, texte réduit à sa boîte). Ils sont dépaquetés une fois dans `%LOCALAPPDATA%\BurnSmith\open`. Cinq d'entre eux (Wall, travel x2, textAuto1 x2) n'ont pas de page de scènes ou pas de miniatures : BurnSmith leur ajoute sa propre page de scènes (grille 3 x 2) sur leur fond, avec leur police et leur couleur. Ce sont des menus **DVD** : en Blu-ray, BurnSmith garde leurs fonds, couleurs et boutons mais redessine la page selon son propre moteur.
- **Lus sur votre PC (jamais copiés)** : les modèles de DVDFab (dossiers `MenuTemplate`, y compris ceux téléchargés depuis DVDFab) et de Leawo Prof. Media, ou un dossier ajouté à la main (`%APPDATA%\BurnSmith\template-folders.txt`). BurnSmith ne les contient pas ; sans DVDFab ni Leawo l'éditeur propose les modèles livrés.
- **Les vôtres** : « Mes modèles » (JSON dans `%APPDATA%\BurnSmith\templates`) et « Mes images » (copies dans `%APPDATA%\BurnSmith\library\backgrounds`).

La **bibliothèque** de l'éditeur catalogue les fonds (DVDStyler, fonds des modèles DVDFab / Leawo trouvés sur le PC, vos images, avec *Importer…*), les 5 styles de boutons (capsules, texte seul, cartes, cadre classique, verre néon), la grille des scènes (1-5 colonnes x 1-4 lignes, avec ou sans images, coins carrés), la police (celles de Windows) et les couleurs. « Composer à partir de ce fond » sort d'un modèle DVDFab / Leawo en gardant son fond (par son chemin, sans copie). Pas encore : placer librement des éléments au glisser-déposer ni ajouter des boutons interactifs supplémentaires.

État des modèles lus sur cette machine (114 utilisables, aucun refusé) :

- DVDFab Blu-ray / 4K (`template.bd.xml`) : 22 modèles, avec les images et positions du modèle. Quand un modèle n'a pas de page de réglages (`none_setup`, ex. Birthday1), BurnSmith ajoute lui-même une pastille « Settings » et des panneaux sombres. « Streaming » (tout-en-un, sans menu principal) : sa première page de scènes sert de menu principal (affiche, nom du volume, boutons Lecture / Scènes dans sa police) ; le bouton Accueil de sa page de scènes est celui de BurnSmith.
- DVDFab DVD (`template.xml`) : 5 modèles. Les surbrillances DVD restent un cadre générique à 4 couleurs, pas celui du modèle.
- Leawo : 6 modèles définis (childhood01, education01, holiday01, en 16:9 et 16:9-2) ; **les positions sont une interprétation de BurnSmith**, approximative (en 16:9-2, le titre d'education01 chevauche le cadre). Les 27 autres dossiers (4:3, et les anciens modèles business, childhood, education, holiday, nature, sports, standard, wedding) n'existent sur le disque que sous forme d'images : Leawo garde leur mise en page ailleurs. BurnSmith les met en page lui-même d'après leurs images : il mesure l'ouverture transparente de chaque cadre (toujours 640x360 pour l'aperçu et 320x180 pour les miniatures, ramenés au 1920x1080), y place l'aperçu et les miniatures, centre le cadre (accroché en haut quand il a des fils), met Lecture / Scènes dessous et une grille 3 x 2 sur la page de scènes. **Les images sont celles de Leawo, les positions sont celles de BurnSmith.** Les 4:3 sont centrés avec des bandes noires.
- Les libellés des boutons sont dans les images du modèle (souvent en anglais) ; certains PNG du modèle peuvent être corrompus et sont alors ignorés.

## Reste à faire

Fonctions de DVDFab Créateur pas encore reprises : débit vidéo manuel et conversion / passthrough au choix, recherche de métadonnées, montage et coupe par titre (les icônes ne sont pas là plutôt que sans effet), amélioration IA / HDR. Aussi : menus HDMV natifs (par exemple avec [bdmvauthor](https://github.com/blenheimears/bdmvauthor), Apache-2.0) : le BD-J non signé est un risque de compatibilité sur lecteurs de salon, musique de fond, placement libre d'éléments dans le menu, surbrillances DVD exactes d'après les modèles, **essais de gravure sur disque réel (non faits : un DVD-RW d'abord, le Blu-ray en dernier)** et sur lecteurs de salon.

## Licence

GNU GPL v3 ou ultérieure (`LICENSE`). Ce projet s'inspire de projets libres lus sur GitHub, notamment futureburn (MIT) pour la gravure IMAPI2 ; les modèles et fonds DVDStyler (GPL) et les paramètres de DVD Forge (GPL-3.0) sont livrés avec leurs avis ; détails dans `THIRD-PARTY-NOTICES.md`. BurnSmith est indépendant de DVDFab et de Leawo (aucun code ni modèle de ces logiciels n'est repris) et n'est pas affilié à eux.
