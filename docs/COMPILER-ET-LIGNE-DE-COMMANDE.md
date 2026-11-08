# Installer, compiler et utiliser en ligne de commande

## Utiliser

### Installer

Télécharger `BurnSmith-Setup-<version>.exe` dans [Releases](https://github.com/Balrog57/BurnSmith/releases) et le lancer. Il installe BurnSmith dans `Program Files` (Windows demande les droits administrateur), crée un raccourci dans le menu Démarrer (et sur le bureau, si on le coche) et un désinstalleur. Réinstaller par-dessus une version plus ancienne la remplace.

**Mise à jour automatique.** Une copie installée (l'exe est sous `Program Files`) interroge à chaque démarrage la dernière release GitHub de `Balrog57/BurnSmith`. Si elle est plus récente, un message propose de la télécharger et de l'installer : elle attend la fin du travail en cours, le fichier est contrôlé (SHA-256 donné par GitHub ; sans ce hash, la mise à jour est refusée), Windows demande les droits administrateur, l'application se ferme puis redémarre. Si la demande est refusée ou reste sans réponse, l'application reste ouverte et le dit. Hors ligne, ou déjà à jour : aucun message. Une compilation de développement ne se met jamais à jour toute seule. Vérification sans l'application : `BurnSmith.Cli check-update [--live]`.

### Où sont les fichiers

- **tsMuxeR** est décompressé au premier lancement, et de nouveau quand ils changent : dans `%LOCALAPPDATA%\BurnSmith\files` (`Program Files` n'est pas inscriptible). On peut supprimer ce dossier, il sera refait. Un dossier `tools\` à côté de l'exe ou la variable `BURNSMITH_TOOLS` remplacent les outils si besoin.
- **ffmpeg et ffprobe** sont dans l'installateur, pas dans l'exe ([`installer\ffmpeg.iss`](../installer/ffmpeg.iss)) : le build « full » 8.1.1 de gyan.dev, celui qu'épingle la CI ([`ci.yml`](../.github/workflows/ci.yml)), sous GPL v3 (`LICENSE` et `README.txt`, avec le lien des sources, à côté). Ils vont dans `Program Files\BurnSmith\tools\ffmpeg`, ajouté au `PATH` machine ; une mise à jour les remplace, la désinstallation retire ce dossier et cette entrée du `PATH`, rien d'autre. Rien n'est téléchargé : une nouvelle version de ffmpeg n'arrive qu'avec une release de BurnSmith (la 9 a retiré `-top` et ne lisait plus une liste de FLAC au-delà du premier, sans erreur).
- **ImgBurn** : l'installateur porte le setup officiel d'ImgBurn 2.5.8.0, tel quel ([`installer\imgburn.iss`](../installer/imgburn.iss)), et le lance en silence s'il n'y a pas d'ImgBurn dans `Program Files`, en lui imposant `Program Files (x86)\ImgBurn` (sans cela, il retourne dans le dossier dont il se souvient, où BurnSmith ne le cherche pas) et sans icône sur le bureau. ImgBurn reste un programme à part, avec son désinstalleur ; s'il échoue, BurnSmith grave avec IMAPI2.
- **La bibliothèque de modèles** est dans **`Documents\BurnSmith\Templates`**, un dossier visible qu'on peut sauvegarder et enrichir : voir [MODELES.md](MODELES.md#où-sont-les-modèles-sur-le-disque). Elle n'est pas effacée à la désinstallation.
- **BurnSmith doit être fermé pour être désinstallé** : ouvert, le désinstalleur demande de le fermer (Réessayer ou Annuler) et, tant qu'on n'a pas continué, ne retire rien ; en silence (`/SILENT /SUPPRESSMSGBOXES`), il s'arrête avec le code 1 ([`installer\running.iss`](../installer/running.iss)). La mise à jour, elle, ferme l'application toute seule.
- **La désinstallation laisse aussi** `%LOCALAPPDATA%\BurnSmith\files` (tsMuxeR et le journal d'erreurs `burnsmith.log`) et `%LOCALAPPDATA%\BurnSmith\thumbs`. On peut les supprimer à la main.

Le mode **DVD** ne demande aucun outil en plus de ffmpeg : la structure VIDEO_TS (IFO, paquets de navigation, surlignage des menus, sous-titres VobSub) est écrite par le code de BurnSmith (`src/BurnSmith.Core/DvdVideo`). Seule une compilation de développement cherche un dossier `tools\` dans les dossiers parents.

## Compiler

Prérequis : SDK .NET 10 et `tools\tsmuxer\bin\tsMuxeR.exe` (ou `-TsMuxer`). Ce binaire n'est pas dans le dépôt : `publish.ps1` le zippe depuis votre PC. Il faut aussi, sur ce PC, ffmpeg et ffprobe 8.1.1 de gyan.dev (`tools\ffmpeg`, ou le `PATH` : `winget install Gyan.FFmpeg --version 8.1.1`, ou `-Ffmpeg DOSSIER`) et `tools\imgburn-setup\SetupImgBurn_2.5.8.0.exe` (téléchargé depuis download.imgburn.com, ou `-ImgBurnSetup`) : `publish.ps1` vérifie leur SHA-256 avant de les mettre dans l'installateur. [Inno Setup 6](https://jrsoftware.org/isinfo.php) est obligatoire (`winget install JRSoftware.InnoSetup`). Les licences de .NET viennent des paquets NuGet que la publication vient d'utiliser.

```powershell
.\publish.ps1      # produit dist\BurnSmith-Setup-<version>.exe, seul fichier de la release
                   # (environ 310 Mo, ffmpeg et ImgBurn compris ; dist\BurnSmith.exe, autonome, en est une étape : l'installateur l'emballe)
```

Sans Inno Setup, ou sans un fichier de licence de .NET, `publish.ps1` échoue. L'installateur (`installer\BurnSmith.iss`) installe l'exe, `LICENSE`, les notices et les licences de .NET dans `Program Files`, avec raccourcis et désinstalleur, puis ffmpeg et ImgBurn (ci-dessus) ; sa version est celle de l'exe (`<Version>` dans `src\BurnSmith.App\BurnSmith.App.csproj`).

**Publier une version** : la mise à jour automatique lit la dernière release GitHub. Le **tag** de la release doit être `vX.Y.Z` (la version de l'exe) et le nom de l'**asset** de l'installateur doit commencer par `BurnSmith-Setup` et finir par `.exe` (`publish.ps1` le nomme `BurnSmith-Setup-X.Y.Z.exe`). C'est le seul fichier à joindre. La release doit porter le SHA-256 que GitHub calcule pour ses assets.

`publish.ps1` zippe aussi les **images et fichiers de mise en page des modèles trouvés sur ce PC** (`BurnSmith.Cli pack-templates` : ni polices ni programmes) et les embarque dans l'exe. Ils ne sont pas dans le dépôt ; sans ces modèles sur la machine de publication, `publish.ps1` s'arrête au lieu de produire un exe sans eux.

Le menu Blu-ray est un xlet Java 1.3 (`bdj/`). `assets/bdj/menu.jar` est déjà compilé et embarqué dans l'exe ; `bdj\build.ps1` le reconstruit avec un JDK 8 (`tools\jdk8`) et le jar libbluray de VLC (`tools\vlc`, pour compiler seulement).

## Ligne de commande

Le programme est `BurnSmith.Cli.exe`, écrit `burnsmith` ci-dessous. **Il n'est pas dans la release** : il se compile depuis `src\BurnSmith.Cli`. Il n'embarque rien : ffmpeg et tsMuxeR viennent d'un dossier `tools\` ou du `PATH`, et les modèles portés seulement s'ils sont présents sur le PC.

`burnsmith bd|dvd --video f.mkv --out dossier [--design NOM|DOSSIER_DU_MODELE] [--title TEXTE] [--label ETIQUETTE] [--highlight box|underline|textcolor] [--list] [--animated-thumbs (DVD ; sans effet sur un Blu-ray)] [--page-bg PAGE=IMAGE, répétable, PAGE = root|scenes|settings|credits|bonus] [--no-menu] [--chapters MIN] [--uhd] [--credits 'A|B'] [--titles 'fra=Titre|eng=Title'] [--element 'kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54' […]] [--loop [--loop-file V] [--loop-start S] [--loop-seconds S]] [--music FICHIER [--music-start S] [--music-volume POURCENT] [--music-fade S] [--loop-seconds S]] [--reencode] [--crf N] [--preset NOM] [--dvd-kbps N] [par vidéo, après son `--video` : --trim DÉBUT-FIN (secondes ou h:mm:ss, un côté peut rester vide) --volume POURCENT --chapter-at T,T,... | --no-chapters | --chapter-every MIN (un chapitre toutes les MIN minutes pour cette vidéo) | --sub-file FICHIER ou LANG=FICHIER (répétable : un sous-titre externe, .idx compris, gardé après les pistes de la vidéo) | --audio N,N --sub N,N (les pistes gardées) --default-audio N --default-sub N (la piste audio / le sous-titre du départ, parmi les gardés) --track-lang a0=fre,s1=eng (la langue d'une piste du fichier) | --bonus (cette vidéo est un bonus : après le film, page Bonus du menu) | --intro (cette vidéo est lue au démarrage du disque, avant le menu ; Blu-ray et DVD)] [bd : --profile "BD25 720p" --quality source|fit|15|20|30|KBPS (fit par défaut, comme « Adapter au disque ») --capacity GO --play-mode 0|1|2 [--no-rewind]]` ; `burnsmith preview NOM|DOSSIER sortie [--picture image.jpg] [--episodes N] [--bonus N] [--list] [--credits 'A|B'] [--element '…']` dessine toutes les pages d'un modèle ; `burnsmith dump NOM` affiche sa mise en page ; `burnsmith pack-templates sortie.zip` fait le zip des modèles que `publish.ps1` embarque ; `burnsmith slideshow sortie.mkv [--seconds 5] [--music morceau]… photo…` fabrique un diaporama, `burnsmith song sortie.mkv audio [--cover image]` un morceau avec sa pochette, `burnsmith join sortie.mkv vidéo vidéo…` colle des vidéos de mêmes flux (à donner ensuite à `--video`) ; `burnsmith check-disc dossier|image.iso [dossier]` contrôle la structure d'un disque fini ; `burnsmith drives` liste les graveurs vus par Windows ; `burnsmith iso dossier sortie.iso [--label X] [--dvd]` crée l'image UDF ; `burnsmith burn image.iso F:` grave (disque vierge) puis relit le disque ; `burnsmith verify image.iso F:` compare un disque à son image.

## Structure

- `src/BurnSmith.Core` : analyse, DVD, Blu-ray, ISO, menus (modèle, mise en page, rendu WPF).
- `src/BurnSmith.App` : application WPF (thème Fluent sombre).
- `src/BurnSmith.Cli` : même moteur en ligne de commande.
- `src/BurnSmith.Payload` : porte dans l'exe publié les zips des outils et des modèles.
- `installer/` : script Inno Setup.
- `bdj/` : source du xlet de menu Blu-ray.
- `installer\test-ffmpeg.ps1` : fait tourner `ffmpeg.iss`, `imgburn.iss` et `running.iss` dans un vrai installateur (`installer\test-ffmpeg.iss`), sans droits d'administrateur, sur une clé de registre d'essai : ffmpeg copié et ajouté au `PATH`, ImgBurn installé dans le dossier imposé même quand son setup se souvient d'un autre, réinstallation, désinstallation d'une application ouverte, désinstallation ; après `publish.ps1`, aussi une mise à jour lancée depuis l'application ouverte comme le fait `Updater.cs` (fermeture par Restart Manager, relance). Ne couvre pas le vrai `PATH` machine, `Program Files` ni l'élévation.
- `tools/vlctest.ps1` : lit un disque dans un VLC portable et affiche les traces du xlet.
- `BurnSmith.Cli check-edit` : vérifie l'arithmétique d'une coupe (durée gardée, chapitres décalés, temps saisis), puis ce qui demande les outils : vidéos faites par ffmpeg, DVD avec vidéo de début (page Réglages après elle, vidéo NTSC sur un film PAL). `BurnSmith.Cli check-elements` : vérifie les éléments libres du menu (lecture d'un élément de ligne de commande, page visée, ce qui est dessiné, ordre devant / derrière les boutons, copie avec le modèle). `BurnSmith.Cli check-highlights` : vérifie les surbrillances DVD (image de survol nommée par le modèle, masque, raster à la taille du bouton, PNG 2 bits à quatre couleurs, repli sur le cadre plat). `BurnSmith.Cli check-bd` : de courts clips passent par les vraies constructions, puis ffprobe lit ce qu'un lecteur attend (son, profondeur, niveau, synchronisation) ; aussi les refus du DVD, le dossier de travail, les PGS abîmés. `BurnSmith.Cli check-reliability` : *Annuler* sur de vrais encodages Blu-ray et DVD, une image à moitié écrite, une source disparue, le journal d'erreurs.
- `BurnSmith.Cli check-templates` : construit chaque modèle (film puis série) et vérifie pages, boutons, déplacements à la télécommande et images (144 sur un PC qui a tous les modèles, 54 en intégration continue, où seuls les modèles libres existent) ; `--only NOM` se limite aux modèles dont le nom contient NOM, `--picture image.jpg` donne l'image d'aperçu ; avec `--matrix`, toutes les formes de disque (1 à 40 chapitres, 2 à 60 épisodes, 1 à 9 pistes audio, 0 à 9 sous-titres) et les 200 menus composables dans l'éditeur. `BurnSmith.Cli templates` liste les modèles trouvés. `BurnSmith.exe [vidéo] --snapshot out.png [design=modèle] [editor[=modèle]] [scroll[=pixels]] [lib=N] [tab=N] [button] | [advanced] | [edit] [focus]` dessine une fenêtre hors écran pour la vérifier.
- `BurnSmith.Cli convert-templates [--out DOSSIER] [--force]` : réunit tous les modèles trouvés dans le format unique (un dossier par modèle, `template.json` lisible + images PNG, fonds et boutons séparés) ; sans `--force`, un pack déjà converti est gardé, donc les retouches survivent. `BurnSmith.Cli check-conversion` vérifie chaque pack (JSON lisible, images présentes, au moins un fond et un bouton) ; `BurnSmith.Cli check-library` vérifie que chaque fond et chaque bouton des packs est bien proposé par la bibliothèque de l'éditeur.
- `--no-dvd` (`check-edit`, `check-bd`, `check-reliability`) : passe les constructions de DVD réelles, lentes (utilisé par l'intégration continue, `.github/workflows/ci.yml`). Chaque partie passée est nommée par une ligne `skip`.

## Régénérer les captures du README

Les images de `docs/images/` sont dessinées hors écran par l'application elle-même (`--snapshot`), avec **des modèles libres uniquement** : les images des modèles portés ne sont jamais versionnées. Il faut quelques vidéos d'exemple (ffmpeg sait en fabriquer : `-f lavfi -i gradients=s=1920x1080:r=24:d=60`).

```powershell
$exe = "src\BurnSmith.App\bin\Debug\net10.0-windows\BurnSmith.exe"
& $exe ep1.mkv ep2.mkv ep3.mkv --snapshot fenetre.png "design=Tube cathodique"            # fenêtre principale, sur ce modèle
& $exe ep1.mkv ep2.mkv ep3.mkv --snapshot editeur.png "design=Tube cathodique" editor tab=1  # éditeur de menu, sur ce modèle, propriétés sur l'onglet Menu
src\BurnSmith.Cli\bin\Debug\net10.0-windows\BurnSmith.Cli.exe preview "Nuit d'encre" pages --picture image.jpg --episodes 3   # pages d'un modèle (NOM_page.jpg)
```

Le rendu garde une marge transparente à droite et en bas (la taille de la fenêtre, pas celle de son contenu) : à rogner avant de versionner, selon l'image (`ffmpeg -vf crop=1264:761:0:0` pour la fenêtre principale, `crop=1423:761:0:0` pour l'éditeur). Gardez l'onglet Modèles de la bibliothèque, dont le haut ne montre que les modèles libres : les onglets Fonds et Boutons afficheraient des images des modèles portés.
