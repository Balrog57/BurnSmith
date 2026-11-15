---
name: burnsmith
description: Pilote BurnSmith en ligne de commande, sans sa fenêtre - fabriquer un Blu-ray, un Blu-ray 4K ou un DVD avec menu à partir de vidéos, choisir et prévisualiser un modèle de menu, régler pistes audio / sous-titres / chapitres / coupes, créer l'image ISO, graver, effacer, contrôler un disque fini. À utiliser dès qu'on demande de créer, graver, vérifier un disque ou de manipuler les modèles de menu de BurnSmith.
---

# BurnSmith en ligne de commande

Tout ce que fait la fenêtre de BurnSmith se fait aussi ici. Le programme est `BurnSmith.Cli.exe` (écrit `burnsmith` plus bas). 

- **BurnSmith installé** (depuis la version 1.0.19) : l'exe est à côté de l'application, `C:\Program Files\BurnSmith\BurnSmith.Cli.exe` sauf autre dossier choisi à l'installation. Rien à compiler ni à télécharger. Il se sert des outils et des modèles que l'application déballe à son premier lancement : si une commande répond « introuvable » ou si `templates` ne liste que quelques modèles, demander à l'utilisateur d'ouvrir BurnSmith une fois. Ce fichier est installé dans `C:\Program Files\BurnSmith\skill\burnsmith\SKILL.md` ; on le donne à son IA en copiant le dossier `burnsmith` dans son dossier de skills (`%USERPROFILE%\.claude\skills\` pour Claude).
- **Depuis le dépôt** : `dotnet build src/BurnSmith.Cli -c Release` donne `src/BurnSmith.Cli/bin/Release/net10.0-windows/BurnSmith.Cli.exe`. Il lui faut `ffmpeg`, `ffprobe` et `tsMuxeR` dans `tools\` ou sur le `PATH`. `docs/COMPILER-ET-LIGNE-DE-COMMANDE.md` et les commentaires en tête de `src/BurnSmith.Cli/Program.cs` détaillent chaque option.

Sans argument, l'exe affiche son usage et sort avec 2.

## Règles

- Un build dure de quelques minutes à plusieurs heures : le lancer en arrière-plan, jamais deux à la fois vers le même dossier `--out`.
- `--out DOSSIER` est remplacé, et `DOSSIER_work` à côté est effacé : ne jamais viser un dossier qui contient autre chose.
- Graver et effacer sont irréversibles : demander confirmation à l'utilisateur avant `burn` et `erase`, en nommant le lecteur.
- Code de sortie : 0 = réussi, 1 = le disque fini a un problème (lisible dans la sortie), 2 = commande mal écrite. Une exception = échec.
- Les options écrites après un `--video` (jusqu'au suivant) sont celles de cette vidéo. Avec une seule vidéo, l'ordre est libre.
- Les nombres décimaux s'écrivent avec un point (`--fps 23.976`, `--capacity 7.6`).

## Déroulé habituel

1. **Lire la vidéo** : `burnsmith probe film.mkv` donne en JSON la vidéo et ses pistes. `Index` est le numéro que prennent `--audio` et `--sub`.
2. **Choisir un modèle** : `burnsmith templates` les liste (`OK Catégorie | Nom`). `--design` prend le nom, un dossier de modèle, ou un `.json` de modèle enregistré. Sans `--design`, comme l'application : le dernier modèle choisi dans l'éditeur de menu de l'application, sinon d'après les vidéos du film (bonus et vidéo de début à part) : une seule = « Salle obscure », des épisodes tous nommés SxxEyy = « Saison » (« Séries » si le modèle Streaming manque), plusieurs autres = « Nuit d'encre ». La sortie le dit (`menu template: NOM`).
3. **Regarder le menu avant d'encoder** : ajouter `--preview` à la commande `bd` / `dvd` écrit seulement les pages en JPEG dans `--out` (`menu_root.jpg`…). Les lire avec l'outil de lecture d'images pour vérifier. `--safe-area` y dessine les cadres de la zone sûre (pointillés : zone d'action, 93 % ; cadre plein : zone des titres, 90 %) et entoure en orange un bouton ou un texte qui dépasse de la zone des titres. Avec ou sans l'option, chaque élément qui en dépasse est signalé par une ligne `warning: page … is outside the title-safe area` (aussi pendant la fabrication du disque).
   **Savoir ce qui va se passer** : ajouter `--recap` à la commande `bd` / `dvd` (ou `burnsmith project disque.burnsmith --recap`) affiche seulement « À savoir avant de démarrer », ce que la fenêtre montre avant Démarrer : vidéos copiées telles quelles ou converties (et en combien de temps, à peu près), son refait (« Le son TrueHD 7.1 de la vidéo 2 sera refait en Dolby Digital 5.1 »), remplissage du disque, son et sous-titres au démarrage. Rien n'est encodé. Le dire à l'utilisateur avant un long build.
4. **Fabriquer le disque** : `burnsmith bd …` ou `burnsmith dvd …`. La commande contrôle le dossier à la fin.
5. **Image ISO** : `burnsmith iso DOSSIER sortie.iso --label NOM` (ajouter `--dvd` pour un DVD).
6. **Contrôler** : `burnsmith check-disc sortie.iso DOSSIER`.
7. **Graver** (après accord) : `burnsmith drives` puis `burnsmith burn sortie.iso F: [--speed 4] [--no-verify] [--eject] [--erase]`. La gravure relit le disque (sauf `--no-verify`) ; un BD-RE/DVD±RW déjà écrit demande `--erase`.

## Fabriquer un disque

```bash
burnsmith bd --video film.mkv --audio 0,1 --sub 0 --default-audio 0 --out D:\Disques\Film --design "Birthday1" --title "Mon film" --label MON_FILM
```

```bash
burnsmith dvd --video ep1.mkv --name "Épisode 1" --video ep2.mkv --name "Épisode 2" --out D:\Disques\Serie --pal
```

| But | Options |
|---|---|
| Type de disque | `bd` (Blu-ray), `bd --uhd` (4K), `dvd` |
| Taille | bd : `--profile BD25\|BD50\|BD9\|BD5\|MiniBD` (ajouter ` 720p`), `--capacity GO` (taille utilisable : 46.5 = BD-50, 7.6 = DVD-9, 1.3 = Mini DVD 8 cm ; `0` = sans limite, rien n'est adapté ni contrôlé en taille) |
| Qualité | bd : `--quality fit` (défaut, « Adapter au disque ») `\|source\|15\|20\|30\|KBPS` ; `--reencode --crf N --preset NOM` ; dvd : `--dvd-kbps N` ; `--cpu` (sans carte NVIDIA) ; `--fps R` |
| DVD | `--pal` \| `--ntsc`, `--4x3` |
| Pistes (par vidéo) | `--audio N,N`, `--sub N,N` | `--sub none`, `--default-audio N`, `--default-sub N`, `--track-lang a0=fre,s1=eng`, `--sub-file fra=film.srt` (srt, ass, vtt, sup, idx), `--sub-delay 0=1.5,2=-0.4` (le sous-titre N plus tard de tant de secondes, plus tôt si négatif), `--audio-delay 0=0.2` (pareil pour la piste audio N) |
| Sous-titres en texte (disque) | `--sub-font Arial`, `--sub-size 125` (% de la taille habituelle), `--sub-color FFE600`, `--sub-position 10` (0 = en bas, 100 = en haut) |\| `--sub none`, `--default-audio N`, `--default-sub N`, `--track-lang a0=fre,s1=eng`, `--sub-file fra=film.srt` (srt, ass, vtt, sup, idx) |
| Coupe et son (par vidéo) | `--trim 90-1:30:00` (ou `A-B,C-D`), `--volume 120` |
| Chapitres | les chapitres que le fichier contient (`Chapters` de `probe`) sont gardés par défaut ; sinon un toutes les 5 minutes. `--chapters MIN` (disque), par vidéo : `--chapter-every MIN`, `--chapter-at 0,300,725`, `--no-chapters` |
| Rôle d'une vidéo | `--bonus` (page Bonus), `--intro` (lue au démarrage, avant le menu) |
| Au menu (par vidéo) | `--name TEXTE`, `--overview TEXTE`, `--thumb-at 12:30` \| `--thumb image.jpg` |
| Effets (par vidéo) | `--effects reglages.json` (les réglages de l'éditeur vidéo : recadrage, filigrane, sous-titre incrusté…) |
| Disque d'essai | `--trial [SECONDES]` : chaque vidéo réduite à SECONDES en tout (300 par défaut), un passage à chaque chapitre ; le menu est complet, le disque prêt en quelques minutes. À fabriquer dans un autre `--out` que le vrai disque |
| Projet | `--save-project disque.burnsmith` : n'encode rien, écrit le disque décrit par les options dans un projet que la fenêtre ouvre ; `burnsmith project disque.burnsmith [--out DOSSIER] [--trial]` fabrique le disque d'un projet (enregistré par la fenêtre ou par `--save-project`). Le projet ouvert dans la fenêtre est gardé tout seul dans `autosave.burnsmith` (`%LOCALAPPDATA%\BurnSmith\files` pour la copie installée) et rouvert au lancement ; « Nouveau projet » le vide |
| File d'attente | `burnsmith queue un.burnsmith deux.burnsmith [--trial]` : les projets fabriqués l'un après l'autre, chacun dans son dossier (`Out` du projet, `_essai` au bout pour un essai), avec son ISO s'il la demande ; un échec n'arrête pas les suivants, code de sortie 1 si l'un a échoué |
| Éteindre à la fin | `--shutdown` (bd, dvd, project, queue) : la commande finie (disque bon ou non), Windows s'éteint une minute plus tard ; `shutdown /a` l'annule |
| Noms de chapitres (par vidéo) | `--chapter-names "Prologue\|Le duel\|\|Final"` : les noms écrits sur le menu, dans l'ordre de `--chapter-at` ou des chapitres du fichier (un nom vide = « Chapitre N ») ; sans l'option, les noms que le fichier donne sont gardés |
| Son sans perte (bd) | `--lossless` : TrueHD gardé tel quel avec un cœur AC-3, FLAC / PCM en LPCM, au lieu d'AC-3 ; DTS et DTS-HD toujours copiés. `probe` dit `"Lossless": true` pour une piste concernée |
| Lecture | `--play-mode 0\|1\|2` (menu puis tout, le défaut : un épisode choisi enchaîne les suivants, menu après le dernier, bonus seuls \| premier titre puis tout \| retour au menu), `--no-rewind` |

## Le menu

| But | Options |
|---|---|
| Sans menu | `--no-menu` (une seule vidéo) |
| Textes | `--title TEXTE`, `--titles "fra=Mon disque\|eng=My disc"`, `--credits "Ligne 1\|Ligne 2"` |
| Fonds par page | `--page-bg root=fond.jpg` (répétable ; `root`, `scenes`, `settings`, `credits`, `bonus`) |
| Langue du menu | `--menu-language fr` (défaut) ou `en` : tous les mots du menu (Lecture / Play, Chapitres / Scenes, Réglages / Settings, Reprendre / Resume, Menu principal / Main menu, Crédits / Credits, titre d'exemple Mon disque / My Disc) et les mots dessinés dans les boutons d'un modèle. Sans l'option, c'est la langue du design (`"ButtonLanguage"` de son JSON, `fr` par défaut). Un mot écrit autrement dans le JSON du design est gardé ; aussi dans `preview` |
| Mots des boutons d'un modèle | `--button-language fr` ou `en`, après la langue du menu, pour les seuls mots dessinés : les boutons dont le mot est dessiné dans l'image (Play, Scenes, Set Up, Main Menu chez DVDFab) en français, refaits par BurnSmith, ou tels que le modèle les a ; aussi dans `preview` et dans le JSON d'un design (`ButtonLanguage`) |
| Flou du fond | `--background-blur 12` (0 à 40 : l'image de fond floutée, pour que le texte posé dessus se lise ; le modèle `Saison` en a 12) |
| Menu animé | `--loop [--loop-file video] [--loop-start S] [--loop-seconds S\|music]` |
| Musique | `--music morceau [--music-start S] [--music-volume 80] [--music-fade 2]` |
| Présentation | `--list` (scènes en liste), `--scenes-only`, `--skip-first-scene`, `--bonus-text`, `--animated-thumbs` (DVD), `--highlight box\|underline\|textcolor` |
| Pop-up Blu-ray | `--popup [--popup-main TEXTE] [--popup-resume TEXTE]` |
| Type de menu Blu-ray | `--menu-mode bdj` (défaut : programme Java, toutes les fonctions) `\|hdmv` (menu natif que toute platine lit ; pages fixes : pas de `--loop`, pas de `--popup`) |
| Élément libre | `--element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"` (répétable ; `kind` = `text`, `image`, `box`, `button` ; un bouton prend `target=movie\|chapter:3\|scenes1\|settings\|bonus1\|credits`) |

Pour modifier un modèle en profondeur : `burnsmith design-export NOM modele.json`, éditer le JSON, puis `--design modele.json`. `burnsmith preview NOM|modele.json dossier [--picture image.jpg] [--episodes N] [--overview "résumé"] [--title TEXTE] [--bonus N] [--focus] [--safe-area]` dessine toutes les pages d'un modèle avec un contenu d'exemple (`--focus` : avec l'état sélectionné des boutons, tel que la télécommande le montre ; `--safe-area` : les cadres de la zone sûre, comme le bouton « Zone sûre » de l'éditeur) ; `burnsmith dump NOM` liste ce qui est dessiné, rectangle par rectangle.

## Préparer des vidéos

- `burnsmith slideshow sortie.mkv [--seconds 5] [--transition fondu] [--music morceau]… photo…` : un diaporama.
- `burnsmith song sortie.mkv audio [--cover image]` : un morceau avec sa pochette.
- `burnsmith join sortie.mkv a.mkv b.mkv…` : des vidéos de mêmes flux bout à bout.

Le résultat se donne ensuite à `--video`.

## Image, gravure, disque

- `burnsmith drives [--capacity GO]` : les graveurs, comme la liste de la fenêtre : seulement ceux qui écrivent (pas le lecteur virtuel d'une image montée), chacun avec son disque (`F:  BD-R vierge, 25 Go`, `BD-RE déjà écrit`, `pas de disque`, `disque non inscriptible`). Avec `--capacity` (la taille utilisable du projet, comme pour `bd`), une ligne `warning:` pour chaque disque trop petit ; `burn` avertit de même quand l'image dépasse le disque inséré.
- `burnsmith iso DOSSIER sortie.iso [--label X] [--dvd]`
- `burnsmith repair-top-menu DOSSIER` : remet un Blu-ray à menu BD-J fait avant 1.0.33 (menu et épisodes dans un seul titre, Top Menu refusé pendant un épisode) sur la disposition d'aujourd'hui : le menu en Top Menu, un titre par vidéo ; son menu est gardé, le jar signé de nouveau. Un menu sans mode de lecture reçoit celui d'aujourd'hui (un épisode choisi enchaîne les suivants de son bouton « tout lire », menu après le dernier) ; un disque déjà rangé ne reçoit que ce mode. Refaire l'image ensuite avec `burnsmith iso`.
- `burnsmith burn image.iso F: [--speed 4] [--no-verify] [--eject] [--erase]` : grave un disque vierge puis le relit ; `--no-verify` saute la relecture, `--eject` éjecte à la fin, `--erase` efface d'abord un réinscriptible qui contient des données (refusé sans).
- `burnsmith verify image.iso F:` : compare un disque à son image.
- `burnsmith erase F:` : efface un disque réinscriptible.
- `burnsmith check-disc DOSSIER|image.iso [DOSSIER]` : structure d'un disque fini, sans lecteur.
- `burnsmith scan-damage image.iso|DOSSIER [--decode|--no-decode]` : où une copie de disque (Blu-ray ou DVD) est abîmée, sans monter l'image : secteurs remplis de FF ou de zéros par le logiciel de copie, blocs de 6 144 octets remplacés par des paquets nuls, synchronisation perdue, paquets encore chiffrés, chacun placé dans son fichier et dans le temps (« 00005.m2ts (film, 1 h 32) : illisible de 76:18 à la fin ») ; les ruptures de continuité sont signalées à part (souvent d'origine). Les petits clips (images fixes, menus, moins de 64 Mo) sont aussi décodés par ffmpeg : une image que le décodeur a dû masquer (« image abîmée ») est un dommage, même sans paquet FF ni nul ; les flux de menu (IG) que ffmpeg prend pour du mp3 sont ignorés. `--decode` fait décoder chaque flux (plusieurs minutes pour un film), `--no-decode` aucun. Code de sortie 1 quand un dommage est trouvé.
- `burnsmith play DOSSIER|image.iso` : ouvre le disque dans le VLC installé sur le PC (bouton « Lire sur l'ordinateur » de la fenêtre), en `bluray:///` ou `dvd:///` pour un dossier ; code 1 si VLC n'est pas installé (https://www.videolan.org/vlc/). VLC n'affiche un menu BD-J qu'avec Java installé ; sans Java, il lit le disque sans menu. Ouvre une fenêtre sur le bureau de l'utilisateur : ne pas l'utiliser pour un contrôle automatique (`check-play` vérifie la ligne de commande sans lancer VLC).

## Modèles de menu

- `burnsmith templates` : tous les modèles trouvés (ceux de l'utilisateur sous « Mes modèles »), et ceux refusés avec la raison.
- `burnsmith templates [TEXTE] [--use USAGE] [--star NOM] [--unstar NOM]` : comme la recherche et les groupes de la galerie. `TEXTE` garde les modèles qui l'ont dans leur nom, leur usage ou leur groupe, `--use` un usage de la galerie (`Film`, `Série`, `Mariage`, `Anniversaire`, `Voyage`…), sans casse ni accents. Les favoris viennent en tête, marqués `| favori` ; `--star` / `--unstar` posent ou ôtent l'étoile (pour les deux formes 16:9 et 4:3 du modèle). Les lignes `RECENT nom` sont les derniers modèles validés dans l'éditeur (« Récemment utilisés »). Rangés dans `%AppData%\BurnSmith\template-favorites.txt` et `template-recent.txt`.
- `burnsmith template-save NOM|modele.json ["NOUVEAU NOM"]` : range un modèle dans « Mes modèles » (`Documents\BurnSmith\Templates\mine`), comme « Enregistrer le modèle… » de l'éditeur. Il se retrouve ensuite par son nom : `--design "NOUVEAU NOM"`, `preview`, `design-export`, et dans l'éditeur de l'application.
- `burnsmith import-background image…` et `burnsmith import-button image [image-sélectionnée]` : copient des images (jpg, png, bmp, svg) dans la bibliothèque de l'utilisateur (bibliothèques de fonds et de boutons de l'éditeur) et affichent le chemin de chaque copie. Ce chemin sert dans un modèle : `"BackgroundImage"` du JSON, `--page-bg root=CHEMIN`, ou un bouton `--element "kind=button;page=root;rect=120,600,300,100;target=movie;file=CHEMIN;focus=CHEMIN-SÉLECTION"`.
- `burnsmith convert-templates [--out DOSSIER] [--force]` : convertit les modèles du PC au format unique (`template.json` + images) dans `Documents\BurnSmith\Templates\converted`. `--force` remplace les paquets existants, modifications de l'utilisateur comprises : demander avant.
- Contrôles, tous à 0 quand tout va bien : `check-conversion` (paquets lisibles et complets), `check-fidelity` (chaque modèle d'origine et son paquet dessinent le même menu), `check-library`, `check-templates [--matrix] [--only NOM]` (pages, boutons, télécommande ; `--matrix` dure plus d'une heure).

## Vérifier le logiciel après une modification du code

Depuis le dépôt seulement. `check-update`, `check-edit`, `check-elements`, `check-highlights`, `check-templates`, `check-fidelity`, `check-bd`, `check-reliability`, `check-drives` (liste des graveurs : lit les lecteurs, n'écrit rien), `check-play`, `check-recap` (ajouter `--no-dvd` aux trois qui l'acceptent pour passer les vrais builds DVD, lents). Les lancer un par un : l'exe est verrouillé pendant qu'un contrôle tourne, donc pas de `dotnet build` en même temps.
