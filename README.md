# BurnSmith

Créateur de DVD, Blu-ray et Blu-ray 4K pour Windows : on choisit une vidéo, on dessine le menu, on obtient un dossier de disque, une image ISO, ou on grave directement. L'application est un **seul `BurnSmith.exe` portable** (rien à installer).

> Projet jeune. Les dossiers BDMV / VIDEO_TS générés se lisent dans VLC ; la gravure sur disque réel et la lecture sur lecteurs de salon **n'ont pas encore été testées**.

## Ce que ça fait

- DVD (PAL/NTSC, MPEG-2 + AC-3), Blu-ray (H.264, copie si la source est déjà conforme), Blu-ray 4K (HEVC 2160p).
- Plusieurs vidéos (séries), choix des pistes audio et sous-titres, page de réglages dans le menu.
- Éditeur de menu avec aperçu en direct, enregistrement de ses propres modèles, et **galerie des modèles de menu de DVDFab et de Leawo Prof. Media installés sur le PC** (voir ci-dessous).
- Menus Blu-ray en BD-J (un xlet générique + images), menus DVD par dvdauthor/spumux.
- Barre de capacité (DVD-5/9, BD-25/50/66/100), chapitres automatiques, création d'ISO (UDF), gravure.

## Utiliser

1. Compiler `BurnSmith.exe` (voir *Compiler*) et le lancer ; un exe prêt à l'emploi sera publié dans *Releases*.
2. Placer à côté les programmes tiers dans un dossier `tools\` (ou les mettre dans le `PATH`, ou définir `BURNSMITH_TOOLS`) :

| Programme | Rôle | Obligatoire pour |
|---|---|---|
| `ffmpeg.exe`, `ffprobe.exe` | analyse et encodage | tout |
| `tsMuxeR.exe` ([justdan96/tsMuxer](https://github.com/justdan96/tsMuxer)) | assemblage Blu-ray | Blu-ray, 4K |
| `dvdauthor.exe`, `spumux.exe` | menus et structure DVD | DVD |
| `ImgBurn.exe` | création d'ISO et gravure | ISO, gravure |

Ces programmes ne sont pas fournis dans ce dépôt (leurs licences sont les leurs). Le mode DVD est écrit pour une build Windows de dvdauthor/spumux dont la ligne de commande est `spumux script entrée sortie` et `dvdauthor -o dest -x script.xml -h 0 -q` ; les builds standard ne sont pas encore gérées.

## Compiler

Prérequis : SDK .NET 10.

```powershell
.\publish.ps1      # produit dist\BurnSmith.exe (autonome, un seul fichier)
```

Le menu Blu-ray est un xlet Java 1.3 (`bdj/`). `assets/bdj/menu.jar` est déjà compilé et embarqué dans l'exe ; `bdj\build.ps1` le reconstruit avec un JDK 8 (`tools\jdk8`).

Ligne de commande : `burnsmith bd|dvd --video f.mkv --out dossier [--design NOM|DOSSIER_DU_MODELE] [--title TEXTE] [--no-menu] [--chapters MIN] [--uhd]` ; `burnsmith preview NOM|DOSSIER sortie [--picture image.jpg] [--episodes N]` dessine toutes les pages d'un modèle.

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

BurnSmith est indépendant de DVDFab et de Leawo (aucun code de ces logiciels n'est repris) et n'est pas affilié à eux.
