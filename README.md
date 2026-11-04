# BurnSmith

Créateur de Blu-ray (et DVD, Blu-ray 4K) libre pour Windows : on choisit une ou plusieurs vidéos, on dessine le menu, on obtient un dossier de disque, une image ISO, ou on grave directement ; on peut aussi graver un ISO existant. L'application est un **seul `BurnSmith.exe` portable** qui embarque ffmpeg, tsMuxeR et les modèles de menu avec leurs images ; la gravure et la création d'ISO passent par Windows (IMAPI2), rien à installer. Licence : GNU GPL v3 ou ultérieure.

> Projet jeune. Les dossiers BDMV générés se lisent dans VLC et les ISO se montent sous Windows ; la **gravure sur disque réel et la lecture sur lecteurs de salon n'ont pas encore été testées**.

## Ce que ça fait

- DVD (PAL/NTSC, MPEG-2 + AC-3), Blu-ray (H.264, copie si la source est déjà conforme), Blu-ray 4K (HEVC 2160p).
- Plusieurs vidéos (séries), choix des pistes audio et sous-titres, page de réglages dans le menu.
- Fenêtre construite comme celle de DVDFab Créateur, thème sombre à accent rouge (liste des vidéos avec durée / HDR / audio / sous-titres, carte du projet, capacité du disque, sortie dossier / ISO / graveur, progression et annulation de la gravure, effacement d'un disque réinscriptible).
- Éditeur de menu avec aperçu en direct et **bibliothèque d'éléments** : 54 modèles libres (DVD Forge, DVDStyler) et 60 modèles DVDFab / Leawo et 30 thèmes Yuhan (images embarquées) ; on compose son menu en choisissant fond, police, style de boutons, grille et couleurs, et on enregistre ses propres modèles (voir ci-dessous).
- Menus Blu-ray en BD-J (un xlet générique + images), menus DVD par dvdauthor/spumux.
- **Menu animé** : une vidéo (extrait du film ou fichier au choix, avec sa musique) tourne en boucle derrière les boutons, sur Blu-ray (playlist 99 relancée par le xlet ; pas en 4K) et sur DVD (le bouton choisi est conservé à chaque tour).
- **Musique du menu** (comme celle de Yuhan) : un fichier audio, à partir d'un début au choix, **volume** en %, **fondu** entrée/sortie, boucle de 5 à 120 s. Avec un menu animé elle remplace le son de la vidéo ; sans, le menu reste fixe et la musique joue sous une image noire (Blu-ray : même playlist 99, les pages restent opaques et la musique continue d'une page à l'autre ; DVD : chaque page boucle sur sa propre musique, qui repart quand on change de page). Le son est mis en 48 kHz stéréo. Pas en 4K.
- **Page Crédits** (texte libre, bouton sur le menu principal) et **titre du disque par langue** pour les lecteurs Blu-ray (`BDMV\META\DL\bdmt_xxx.xml`).
- Réglages avancés (comme DVDFab) : volume ; **Sortie** (BD50 / BD25 / BD9 / BD5 en 1080p/i ou 720p, partagée avec la liste de la fenêtre principale) ; qualité vidéo (**Adapter au disque** : la vidéo reste telle quelle tant qu'elle tient, sinon elle est refaite au débit qui remplit le disque ; Source ; 15 / 20 / 30 Mb/s ; débit à la main) avec la taille estimée ; **Mode de lecture** (menu puis tous les titres, premier titre au démarrage puis les autres, ou retour au menu après le titre choisi, avec « Rembobiner ») ; durée des chapitres ; mode vidéo ; vitesse x264 ; débit vidéo du DVD à la main. Un disque 720p porte une vidéo 1280x720 et un menu dessiné en 1280x720.
- **Coupe / montage par vidéo** (crayon de la ligne, comme le « Video Settings » de Yuhan) : nom dans le menu, **début et fin** (on ne garde que la partie entre les deux ; la durée, la taille estimée, le débit « Adapter au disque » et les vignettes suivent), **volume** en % (le son est alors refait en AC-3) et **chapitres** (comme le disque, aucun, ou une liste de temps avec Insérer / Supprimer / Tout supprimer). Un début après 0 refait toujours l'image pour commencer à la bonne image ; en 4K, qui n'est jamais réencodé, la coupe commence à l'image clé précédente.
- Éditeur de menu : **aperçu complet du thème** : une bande avec toutes les pages (menu, scènes, réglages, crédits), flèches / Entrée / souris pour naviguer comme sur le disque.
- Barre de capacité (DVD-5/9, BD-25/50/66/100), chapitres automatiques, création d'ISO (UDF), gravure.

## Utiliser

Télécharger `BurnSmith.exe` dans [Releases](https://github.com/Balrog57/BurnSmith/releases) (ou le compiler, voir *Compiler*) et le lancer. Au premier lancement de chaque version, l'exe **se décompresse dans un sous-dossier `BurnSmith-data` à côté de lui** : ffmpeg, ffprobe, tsMuxeR, les modèles de menu et leurs images (quelques secondes ; `%LOCALAPPDATA%\BurnSmith\files` si le dossier de l'exe n'est pas inscriptible). On peut supprimer ce dossier, il sera refait. Un dossier `tools\` à côté de l'exe ou la variable `BURNSMITH_TOOLS` remplacent les outils si besoin.

Le mode **DVD** reste hors de l'exe : il demande `dvdauthor.exe` et `spumux.exe` (builds Windows dont la ligne de commande est `spumux script entrée sortie` et `dvdauthor -o dest -x script.xml -h 0 -q`), à placer dans `tools\` ou le `PATH`. Les licences des programmes embarqués sont dans `THIRD-PARTY-NOTICES.md`.

## Compiler

Prérequis : SDK .NET 10, un dossier contenant `ffmpeg.exe` et `ffprobe.exe` (dans le `PATH` ou `-FfmpegDir`) et `tools\tsmuxer\bin\tsMuxeR.exe` (ou `-TsMuxer`). Ces binaires ne sont pas dans le dépôt : `publish.ps1` les zippe depuis votre PC.

```powershell
.\publish.ps1      # produit dist\BurnSmith.exe (autonome, un seul fichier, plus de 350 Mo)
```

`publish.ps1` zippe aussi les **images et fichiers de mise en page des modèles DVDFab et Leawo trouvés sur ce PC** (`BurnSmith.Cli pack-templates` : ni polices ni programmes) et les embarque dans l'exe. Ils ne sont pas dans le dépôt ; sans ces logiciels sur la machine de publication, l'exe n'embarque que les modèles libres.

Le menu Blu-ray est un xlet Java 1.3 (`bdj/`). `assets/bdj/menu.jar` est déjà compilé et embarqué dans l'exe ; `bdj\build.ps1` le reconstruit avec un JDK 8 (`tools\jdk8`).

Ligne de commande : `burnsmith bd|dvd --video f.mkv --out dossier [--design NOM|DOSSIER_DU_MODELE] [--title TEXTE] [--no-menu] [--chapters MIN] [--uhd] [--credits 'A|B'] [--titles 'fra=Titre|eng=Title'] [--loop [--loop-file V] [--loop-start S] [--loop-seconds S]] [--music FICHIER [--music-start S] [--music-volume POURCENT] [--music-fade S]] [--reencode] [--crf N] [--preset NOM] [--dvd-kbps N] [par vidéo, après son `--video` : --trim DÉBUT-FIN (secondes ou h:mm:ss, un côté peut rester vide) --volume POURCENT --chapter-at T,T,... | --no-chapters] [bd : --profile "BD25 720p" --quality source|fit|15|20|30|KBPS --capacity GO --play-mode 0|1|2 [--no-rewind]]` ; `burnsmith preview NOM|DOSSIER sortie [--picture image.jpg] [--episodes N]` dessine toutes les pages d'un modèle ; `burnsmith drives` liste les graveurs vus par Windows ; `burnsmith iso dossier sortie.iso [--label X] [--dvd]` crée l'image UDF ; `burnsmith burn image.iso F:` grave (disque vierge) puis relit le disque ; `burnsmith verify image.iso F:` compare un disque à son image.

## Structure

- `src/BurnSmith.Core` : analyse, DVD, Blu-ray, ISO, menus (modèle, mise en page, rendu WPF).
- `src/BurnSmith.App` : application WPF (thème Fluent sombre).
- `src/BurnSmith.Cli` : même moteur en ligne de commande.
- `bdj/` : source du xlet de menu Blu-ray.
- `tools/vlctest.ps1` : lit un disque dans un VLC portable et affiche les traces du xlet.
- `BurnSmith.Cli check-edit` : vérifie sans vidéo l'arithmétique d'une coupe (durée gardée, chapitres décalés, temps saisis).
- `BurnSmith.Cli check-templates` : construit chaque modèle (film puis série) et vérifie pages, boutons, déplacements à la télécommande et images (114 sur 114 aujourd'hui) ; avec `--matrix`, toutes les formes de disque (1 à 40 chapitres, 2 à 60 épisodes, 1 à 9 pistes audio, 0 à 9 sous-titres) et les 200 menus composables dans l'éditeur. `BurnSmith.Cli templates` liste les modèles trouvés. `BurnSmith.exe [vidéo] --snapshot out.png [editor[=modèle]] [scroll] | [advanced]` dessine une fenêtre hors écran pour la vérifier.

## Modèles de menu et bibliothèque

Trois provenances, gardées séparées :

- **Livrés dans l'exe (libres)** : les 5 modèles de [DVD Forge](https://github.com/Nicolasalucin/dvd-forge) (GPL-3.0 ; seuls leurs paramètres sont repris : grille, couleurs, police, marges, le dessin est celui de BurnSmith) et les 49 modèles [DVDStyler](https://github.com/ntxalex/DVDStyler) (GPL ; fichiers `.dvdt` et images tels quels, convertis à l'exécution : positions mises à l'échelle, 4:3 centré, texte réduit à sa boîte). Ils sont dépaquetés une fois dans `BurnSmith-data\open`. Cinq d'entre eux (Wall, travel x2, textAuto1 x2) n'ont pas de page de scènes ou pas de miniatures : BurnSmith leur ajoute sa propre page de scènes (grille 3 x 2) sur leur fond, avec leur police et leur couleur. Ce sont des menus **DVD** : en Blu-ray, BurnSmith garde leurs fonds, couleurs et boutons mais redessine la page selon son propre moteur.
- **Images et mises en page DVDFab / Leawo / Yuhan, embarquées dans l'exe publié** (90 modèles ; pas dans le dépôt, pas sous GPL, elles appartiennent à leurs auteurs, voir `THIRD-PARTY-NOTICES.md` ; aucun code, aucune interface, aucune police). Un modèle installé sur le PC (dossiers `MenuTemplate` de DVDFab, y compris ceux téléchargés, de Leawo Prof. Media et `PresetMenu` de Yuhan Blu-ray DVD Creator, ou un dossier ajouté à la main dans `%APPDATA%\BurnSmith\template-folders.txt`) est lu sur place et remplace la copie embarquée du même nom. Les polices commerciales des modèles ne sont pas embarquées : BurnSmith utilise alors une police de Windows.
- **Les vôtres** : « Mes modèles » (JSON dans `%APPDATA%\BurnSmith\templates`) et « Mes images » (copies dans `%APPDATA%\BurnSmith\library\backgrounds`).

La **bibliothèque** de l'éditeur catalogue les fonds (DVDStyler, fonds des modèles DVDFab / Leawo trouvés sur le PC, vos images, avec *Importer…*), les 5 styles de boutons (capsules, texte seul, cartes, cadre classique, verre néon), la grille des scènes (1-5 colonnes x 1-4 lignes, avec ou sans images, coins carrés), la police (celles de Windows) et les couleurs. « Composer à partir de ce fond » sort d'un modèle DVDFab / Leawo en gardant son fond (par son chemin, sans copie). Pas encore : placer librement des éléments au glisser-déposer ni ajouter des boutons interactifs supplémentaires.

État des modèles (144 utilisables, aucun refusé, y compris sans DVDFab, Leawo ni Yuhan installés) :

- DVDFab Blu-ray / 4K (`template.bd.xml`) : 22 modèles, avec les images et positions du modèle. Quand un modèle n'a pas de page de réglages (`none_setup`, ex. Birthday1), BurnSmith ajoute lui-même une pastille « Settings » et des panneaux sombres. « Streaming » (tout-en-un, sans menu principal) : sa première page de scènes sert de menu principal (affiche, nom du volume, boutons Lecture / Scènes dans sa police) ; le bouton Accueil de sa page de scènes est celui de BurnSmith.
- DVDFab DVD (`template.xml`) : 5 modèles. Les surbrillances DVD restent un cadre générique à 4 couleurs, pas celui du modèle.
- Leawo : 6 modèles définis (childhood01, education01, holiday01, en 16:9 et 16:9-2) ; **les positions sont une interprétation de BurnSmith**, approximative (en 16:9-2, le titre d'education01 chevauche le cadre). Les 27 autres dossiers (4:3, et les anciens modèles business, childhood, education, holiday, nature, sports, standard, wedding) n'existent sur le disque que sous forme d'images : Leawo garde leur mise en page ailleurs. BurnSmith les met en page lui-même d'après leurs images : il mesure l'ouverture transparente de chaque cadre (toujours 640x360 pour l'aperçu et 320x180 pour les miniatures, ramenés au 1920x1080), y place l'aperçu et les miniatures, centre le cadre (accroché en haut quand il a des fils), met Lecture / Scènes dessous et une grille 3 x 2 sur la page de scènes. **Les images sont celles de Leawo, les positions sont celles de BurnSmith.** Les 4:3 sont centrés avec des bandes noires.
- Yuhan (30 thèmes « Birthday » et « Natural », `PresetMenu\THEMExx`) : mise en page lue dans `config.dat` (chiffré avec une clé que le programme garde dans son exe ; BurnSmith la lit chez vous à l'exécution, l'exe publié embarque la mise en page en clair avec les images 1920x1080 seulement). Menu principal : aperçu dans son cadre, titre, Lecture / Scènes / Réglages en texte ; pages de scènes : 3 cadres, noms dessous, précédent / retour / suivant. Le bouton « Bonus » et la page réglages propre à Yuhan ne sont pas repris (BurnSmith dessine ses propres lignes audio / sous-titres sur le fond du thème).
- Les libellés des boutons sont dans les images du modèle (souvent en anglais) ; certains PNG du modèle peuvent être corrompus et sont alors ignorés.

## Reste à faire

Fonctions de DVDFab Créateur et de Yuhan pas encore reprises : recherche de métadonnées, montage et coupe par titre (découpe début / fin, volume, chapitres personnalisés), sous-titres externes (SRT, ASS, SUP…), amélioration IA / HDR, vidéo de début (first-play), menu Bonus, mode de scènes par fichier / texte, élément de menu par glisser-déposer, menu contextuel (pop-up), musique de menu avec fondu, diaporama de photos et disque de musique. Aussi : menus HDMV natifs (par exemple avec [bdmvauthor](https://github.com/blenheimears/bdmvauthor), Apache-2.0) : le BD-J non signé est un risque de compatibilité sur lecteurs de salon, menu animé en Blu-ray 4K, placement libre d'éléments dans le menu, surbrillances DVD exactes d'après les modèles, **essais de gravure sur disque réel (non faits : un DVD-RW d'abord, le Blu-ray en dernier)** et sur lecteurs de salon. Le menu animé Blu-ray n'a été vérifié que dans libbluray (journal du xlet), pas à l'image ni sur lecteur.

## Licence

GNU GPL v3 ou ultérieure (`LICENSE`). Ce projet s'inspire de projets libres lus sur GitHub, notamment futureburn (MIT) pour la gravure IMAPI2 ; les modèles et fonds DVDStyler (GPL) et les paramètres de DVD Forge (GPL-3.0) sont livrés avec leurs avis ; détails dans `THIRD-PARTY-NOTICES.md`. Le code et l'interface de BurnSmith sont libres. L'exe publié embarque aussi des images de modèles DVDFab, Leawo et Yuhan (propriété de leurs auteurs, hors GPL, absentes du dépôt) ; aucun code, interface, police ni programme de ces logiciels n'est repris, et BurnSmith n'est pas affilié à eux.
