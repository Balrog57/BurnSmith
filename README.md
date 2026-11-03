# BurnSmith

Créateur de DVD, Blu-ray et Blu-ray 4K pour Windows : on choisit une vidéo, on dessine le menu, on obtient un dossier de disque, une image ISO, ou on grave directement. L'application est un **seul `BurnSmith.exe` portable** (rien à installer).

> Projet jeune. Les dossiers BDMV / VIDEO_TS générés se lisent dans VLC ; la gravure sur disque réel et la lecture sur lecteurs de salon **n'ont pas encore été testées**.

## Ce que ça fait

- DVD (PAL/NTSC, MPEG-2 + AC-3), Blu-ray (H.264, copie si la source est déjà conforme), Blu-ray 4K (HEVC 2160p).
- Éditeur de menu avec aperçu en direct, modèles intégrés, enregistrement de ses propres modèles.
- Menus Blu-ray en BD-J (un xlet générique + images), menus DVD par dvdauthor/spumux.
- Barre de capacité (DVD-5/9, BD-25/50/66/100), chapitres automatiques, création d'ISO (UDF), gravure.

## Utiliser

1. Télécharger `BurnSmith.exe` (page *Releases*) et le lancer.
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

Ligne de commande : `burnsmith bd|dvd --video f.mkv --out dossier [--design NOM] [--title TEXTE] [--no-menu] [--chapters MIN] [--uhd]`.

## Structure

- `src/BurnSmith.Core` : analyse, DVD, Blu-ray, ISO, menus (modèle, mise en page, rendu WPF).
- `src/BurnSmith.App` : application WPF (thème Fluent sombre).
- `src/BurnSmith.Cli` : même moteur en ligne de commande.
- `bdj/` : source du xlet de menu Blu-ray.
- `tools/vlctest.ps1` : lit un disque dans un VLC portable et affiche les traces du xlet.

## Reste à faire

Voir les tâches ouvertes : sélection audio/sous-titres et page de réglages, plusieurs vidéos (séries), davantage de modèles de menu, musique de fond, ré-encodage HDR, signature BD-J.

Les menus sont conçus d'après le fonctionnement observé de logiciels existants ; aucun code, modèle ni visuel de ces logiciels n'est repris.
