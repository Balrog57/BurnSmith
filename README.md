# BurnSmith

Créateur de Blu-ray (et DVD, Blu-ray 4K) libre pour Windows : on choisit une ou plusieurs vidéos, on dessine le menu, on obtient un dossier de disque, une image ISO, ou on grave directement ; on peut aussi graver un ISO existant. L'application est un **seul `BurnSmith.exe` portable** qui embarque ffmpeg et tsMuxeR ; la gravure et la création d'ISO passent par Windows (IMAPI2), rien à installer. Licence : GNU GPL v3 ou ultérieure.

> Projet jeune. Les dossiers BDMV générés se lisent dans VLC et les ISO se montent sous Windows ; la **gravure sur disque réel et la lecture sur lecteurs de salon n'ont pas encore été testées**.

## Ce que ça fait

- DVD (PAL/NTSC, MPEG-2 + AC-3), Blu-ray (H.264, copie si la source est déjà conforme), Blu-ray 4K (HEVC 2160p).
- Plusieurs vidéos (séries), choix des pistes audio et sous-titres, page de réglages dans le menu.
- Éditeur de menu avec aperçu en direct, enregistrement de ses propres modèles, et **galerie des modèles de menu de DVDFab et de Leawo Prof. Media installés sur le PC** (voir ci-dessous).
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

## Modèles de menu

BurnSmith **ne contient aucun modèle** : il lit à l'exécution ceux que vous avez déjà installés (dossiers `MenuTemplate` de DVDFab, y compris ceux téléchargés depuis DVDFab, de Leawo Prof. Media, ou un dossier ajouté à la main dans `%APPDATA%\BurnSmith\template-folders.txt`). Rien n'est copié dans le dépôt ni dans l'exe ; sans DVDFab ni Leawo, l'éditeur propose seulement un dégradé uni personnalisable.

- DVDFab Blu-ray / 4K (`template.bd.xml`) : 21 modèles lisibles, avec les images et positions du modèle. Quand un modèle n'a pas de page de réglages (`none_setup`, ex. Birthday1), BurnSmith ajoute lui-même une pastille « Settings » et des panneaux sombres. Le modèle « Streaming » (tout-en-un) n'est pas géré.
- DVDFab DVD (`template.xml`) : 5 modèles. Les surbrillances DVD restent un cadre générique à 4 couleurs, pas celui du modèle.
- Leawo : seuls 6 modèles lisibles (childhood01, education01, holiday01, en 16:9 et 16:9-2, les seuls à avoir un fichier de mise en page) ; **les positions sont une interprétation de BurnSmith**, approximative (en 16:9-2, le titre d'education01 chevauche le cadre). Les 27 autres (versions 4:3 et modèles sans fichier de mise en page) ne sont pas gérés : leurs positions ne sont pas dans les dossiers de modèles.
- Les libellés des boutons sont dans les images du modèle (souvent en anglais) ; certains PNG du modèle peuvent être corrompus et sont alors ignorés.

## Reste à faire

Fonctions de DVDFab Créateur pas encore reprises : paramètres avancés (nom du volume, sortie BD25/BD50, conversion ou passthrough, qualité/débit), aperçu / recherche de métadonnées / coupe / suppression par titre, options HDR. Aussi : musique de fond, ré-encodage HDR pour la 4K, signature BD-J, surbrillances DVD exactes d'après les modèles, essais sur disque réel et sur lecteurs de salon.

## Licence

GNU GPL v3 ou ultérieure (`LICENSE`). Ce projet s'inspire de projets libres lus sur GitHub, notamment futureburn (MIT) pour la gravure IMAPI2 ; détails dans `THIRD-PARTY-NOTICES.md`. BurnSmith est indépendant de DVDFab et de Leawo (aucun code ni modèle de ces logiciels n'est repris) et n'est pas affilié à eux.
