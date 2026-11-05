# Installer, compiler et utiliser en ligne de commande

## Utiliser

### Installer (la façon recommandée)

Télécharger `BurnSmith-Setup-<version>.exe` dans [Releases](https://github.com/Balrog57/BurnSmith/releases) et le lancer. Il installe BurnSmith dans `Program Files` (Windows demande les droits administrateur), crée un raccourci dans le menu Démarrer (et sur le bureau, si on le coche) et un désinstalleur. Réinstaller par-dessus une version plus ancienne la remplace.

**Mise à jour automatique.** Une copie installée (l'exe est sous `Program Files`) interroge à chaque démarrage la dernière release GitHub de `Balrog57/BurnSmith`. Si elle est plus récente, un message propose de la télécharger et de l'installer : le fichier est contrôlé (SHA-256, quand GitHub le fournit), Windows demande les droits administrateur, l'application se ferme puis redémarre. Hors ligne, ou déjà à jour : aucun message. Une copie portable ou de développement ne se met jamais à jour toute seule. Vérification sans l'application : `BurnSmith.Cli check-update [--live]`.

### Copie portable

`BurnSmith.exe` seul fonctionne aussi, sans installation (il est dans `dist\` après la compilation). Rien n'est installé ni mis à jour automatiquement.

### Où sont les fichiers

- **Les programmes** (ffmpeg, ffprobe, tsMuxeR) sont décompressés au premier lancement de chaque version : dans `BurnSmith-data`, à côté de l'exe, pour une copie portable ; dans `%LOCALAPPDATA%\BurnSmith\files` pour une copie installée (`Program Files` n'est pas inscriptible). On peut supprimer ce dossier, il sera refait. Un dossier `tools\` à côté de l'exe ou la variable `BURNSMITH_TOOLS` remplacent les outils si besoin.
- **La bibliothèque de modèles** est dans **`Documents\BurnSmith\Templates`**, un dossier visible qu'on peut sauvegarder et enrichir : voir [MODELES.md](MODELES.md#où-sont-les-modèles-sur-le-disque). Elle n'est pas effacée à la désinstallation.

Le mode **DVD** reste hors de l'exe : il demande `dvdauthor.exe` et `spumux.exe` (builds Windows dont la ligne de commande est `spumux script entrée sortie` et `dvdauthor -o dest -x script.xml -h 0 -q`). Sans droits d'administrateur, pour une copie installée : `setx BURNSMITH_TOOLS "C:\dossier\de\dvdauthor"` (ou ce dossier ajouté au `PATH`), puis relancez BurnSmith. Une copie portable accepte aussi un dossier `tools\` à côté de l'exe. Seule une compilation de développement cherche un dossier `tools\` dans les dossiers parents. Les licences des programmes embarqués sont dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Compiler

Prérequis : SDK .NET 10, un dossier contenant `ffmpeg.exe` et `ffprobe.exe` (dans le `PATH` ou `-FfmpegDir`), exactement le build 8.1.1 « full » de gyan.dev que promet [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (`publish.ps1` s'arrête sinon ; pour en changer, modifiez ses hashes et cette notice ensemble), et `tools\tsmuxer\bin\tsMuxeR.exe` (ou `-TsMuxer`). Ces binaires ne sont pas dans le dépôt : `publish.ps1` les zippe depuis votre PC. Pour l'installateur : [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
.\publish.ps1      # produit dist\BurnSmith.exe (portable, autonome, un seul fichier, plus de 350 Mo)
                   # puis, si Inno Setup 6 est installé, dist\BurnSmith-Setup-<version>.exe
```

Sans Inno Setup, `publish.ps1` prévient et s'arrête après l'exe portable. L'installateur (`installer\BurnSmith.iss`) installe l'exe dans `Program Files`, avec raccourcis et désinstalleur ; sa version est celle de l'exe (`<Version>` dans `src\BurnSmith.App\BurnSmith.App.csproj`).

**Publier une version** : la mise à jour automatique lit la dernière release GitHub. Le **tag** de la release doit être `vX.Y.Z` (la version de l'exe) et l'**asset** de l'installateur doit s'appeler `BurnSmith-Setup-X.Y.Z.exe`, comme `publish.ps1` le nomme.

`publish.ps1` zippe aussi les **images et fichiers de mise en page des modèles DVDFab et Leawo trouvés sur ce PC** (`BurnSmith.Cli pack-templates` : ni polices ni programmes) et les embarque dans l'exe. Ils ne sont pas dans le dépôt ; sans ces logiciels sur la machine de publication, `publish.ps1` s'arrête au lieu de produire un exe sans eux.

Le menu Blu-ray est un xlet Java 1.3 (`bdj/`). `assets/bdj/menu.jar` est déjà compilé et embarqué dans l'exe ; `bdj\build.ps1` le reconstruit avec un JDK 8 (`tools\jdk8`).

## Ligne de commande

`burnsmith bd|dvd --video f.mkv --out dossier [--design NOM|DOSSIER_DU_MODELE] [--title TEXTE] [--no-menu] [--chapters MIN] [--uhd] [--credits 'A|B'] [--titles 'fra=Titre|eng=Title'] [--element 'kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54' […]] [--loop [--loop-file V] [--loop-start S] [--loop-seconds S]] [--music FICHIER [--music-start S] [--music-volume POURCENT] [--music-fade S]] [--reencode] [--crf N] [--preset NOM] [--dvd-kbps N] [par vidéo, après son `--video` : --trim DÉBUT-FIN (secondes ou h:mm:ss, un côté peut rester vide) --volume POURCENT --chapter-at T,T,... | --no-chapters | --chapter-every MIN (un chapitre toutes les MIN minutes pour cette vidéo) | --sub-file FICHIER ou LANG=FICHIER (répétable : un sous-titre externe, .idx compris, gardé après les pistes de la vidéo) | --audio N,N --sub N,N (les pistes gardées) --default-audio N --default-sub N (la piste audio / le sous-titre du départ, parmi les gardés) | --bonus (cette vidéo est un bonus : après le film, page Bonus du menu) | --intro (cette vidéo est lue au démarrage du disque, avant le menu ; Blu-ray et DVD)] [bd : --profile "BD25 720p" --quality source|fit|15|20|30|KBPS --capacity GO --play-mode 0|1|2 [--no-rewind]]` ; `burnsmith preview NOM|DOSSIER sortie [--picture image.jpg] [--episodes N]` dessine toutes les pages d'un modèle ; `burnsmith slideshow sortie.mkv [--seconds 5] [--music morceau]… photo…` fabrique un diaporama, `burnsmith song sortie.mkv audio [--cover image]` un morceau avec sa pochette, `burnsmith join sortie.mkv vidéo vidéo…` colle des vidéos de mêmes flux (à donner ensuite à `--video`) ; `burnsmith check-disc dossier|image.iso [dossier]` contrôle la structure d'un disque fini ; `burnsmith drives` liste les graveurs vus par Windows ; `burnsmith iso dossier sortie.iso [--label X] [--dvd]` crée l'image UDF ; `burnsmith burn image.iso F:` grave (disque vierge) puis relit le disque ; `burnsmith verify image.iso F:` compare un disque à son image.

## Structure

- `src/BurnSmith.Core` : analyse, DVD, Blu-ray, ISO, menus (modèle, mise en page, rendu WPF).
- `src/BurnSmith.App` : application WPF (thème Fluent sombre).
- `src/BurnSmith.Cli` : même moteur en ligne de commande.
- `bdj/` : source du xlet de menu Blu-ray.
- `tools/vlctest.ps1` : lit un disque dans un VLC portable et affiche les traces du xlet.
- `BurnSmith.Cli check-edit` : vérifie sans vidéo l'arithmétique d'une coupe (durée gardée, chapitres décalés, temps saisis). `BurnSmith.Cli check-elements` : vérifie les éléments libres du menu (lecture d'un élément de ligne de commande, page visée, ce qui est dessiné, ordre devant / derrière les boutons, copie avec le modèle). `BurnSmith.Cli check-highlights` : vérifie les surbrillances DVD (image de survol nommée par le modèle, masque, raster à la taille du bouton, PNG 2 bits à quatre couleurs, repli sur le cadre plat).
- `BurnSmith.Cli check-templates` : construit chaque modèle (film puis série) et vérifie pages, boutons, déplacements à la télécommande et images (114 sur 114 aujourd'hui) ; avec `--matrix`, toutes les formes de disque (1 à 40 chapitres, 2 à 60 épisodes, 1 à 9 pistes audio, 0 à 9 sous-titres) et les 200 menus composables dans l'éditeur. `BurnSmith.Cli templates` liste les modèles trouvés. `BurnSmith.exe [vidéo] --snapshot out.png [editor[=modèle]] [scroll] | [advanced]` dessine une fenêtre hors écran pour la vérifier.

## Régénérer les captures du README

Les images de `docs/images/` sont dessinées hors écran par l'application elle-même (`--snapshot`), avec **des modèles libres uniquement** (DVD Forge, DVDStyler) : les images DVDFab, Leawo et Yuhan ne sont jamais versionnées. Il faut quelques vidéos d'exemple (ffmpeg sait en fabriquer : `-f lavfi -i gradients=s=1920x1080:r=24:d=60`).

```powershell
$exe = "src\BurnSmith.App\bin\Debug\net10.0-windows\BurnSmith.exe"
& $exe ep1.mkv ep2.mkv ep3.mkv --snapshot fenetre.png "design=Retro CRT"            # fenêtre principale, sur ce modèle
& $exe ep1.mkv ep2.mkv ep3.mkv --snapshot editeur.png "design=Retro CRT" editor      # éditeur de menu, sur ce modèle
src\BurnSmith.Cli\bin\Debug\net10.0-windows\BurnSmith.Cli.exe preview "Cinema Dark" pages --picture image.jpg --episodes 3   # pages d'un modèle (NOM_page.jpg)
```

Le rendu garde une marge transparente à droite et en bas (la taille de la fenêtre, pas celle de son contenu) : à rogner avant de versionner (`ffmpeg -vf crop=1264:761:0:0`).
