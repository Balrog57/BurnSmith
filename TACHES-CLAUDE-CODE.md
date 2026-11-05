# File d'implémentation (orchestration)

Répartition : **REA / décompilation et orchestration** de mon côté, **implémentation** par Claude Code (sous-agent), une tâche à la fois pour éviter deux écritures sur les mêmes fichiers. Une tâche n'est déclarée finie que si la build est propre, que `check-templates` reste à 144 sans problème, et que la preuve demandée existe.

## En cours

### T1 — Surbrillances DVD exactes d'après les modèles

- **But** : les menus DVD utilisent les images de surbrillance du modèle au lieu du cadre plat à quatre couleurs.
- **Périmètre** : `Menu/InstalledTemplate.cs`, `Menu/MenuRenderer.cs`, `Menu/IndexedPng.cs`, `Dvd.cs`, nouvelle commande `check-highlights` dans `src/BurnSmith.Cli/Program.cs`.
- **Preuves REA fournies** : `ANALYSE-REA.md` §6 (script spumux décompilé : `highlight` / `select` par page, `<button>` par bouton avec rectangle et voisins, codes `0 = haut, 1 = bas, 2 = gauche, 3 = droite`) et §6 bis (mesures PNG : `frame/0.dvd.sub.png` 390×260 en palette 8 bits, masque 1 bit, `selection/0.png` 180×180 en 2 bits).
- **Fini quand** : `check-highlights` passe, un DVD construit avec un modèle DVDFab montre la surbrillance du modèle, et le cadre générique reste le repli.

## À suivre

### T2 — Mises en page Leawo exactes (les 6 modèles qui ont un `template.xml`)

- **But** : remplacer l'interprétation de BurnSmith par les positions du fichier `MenuTemplate\Build-in\16-9\<thème>\template.xml` (`<Menu>` : `Frame`, `videoscr`, `TitleText`, `ButtonPlayText`, `ButtonScenesText` ; `<Scene><Page>` : `FrameN`, `videoscrN`, `ButtonNext`, `ButtonPre`, `Return`, `ButtonTextN`), pour 16-9 **et** 16:9-2, en 4:3 aussi.
- **Périmètre** : `Menu/LeawoSkin.cs` (aujourd'hui : mesure des ouvertures transparentes et placement deviné) et son appelant.
- **Fini quand** : pour les trois thèmes qui ont un XML (childhood01, education01, holiday01), les rectangles lus dans le XML sont utilisés tels quels ; `check-templates` inchangé ; `preview` d'un de ces modèles comparé à `background01.jpg` du modèle.
- **Conflit** : ne pas lancer avant que T1 ait fini (mêmes fichiers, `InstalledTemplate.cs`).

### T3 — Pages de réglages des modèles Blu-ray DVDFab (`audiolist` / `subtitlelist`)

- **But** : les modèles DVDFab Blu-ray décrivent leurs pages audio / sous-titres (`<setup>` : `audiolist`, `subtitlelist`, six `title` par colonne, `line_height`, `rect` et `frame_rect`). Les dessiner à ces positions au lieu des lignes génériques de BurnSmith ; garder les lignes génériques pour les modèles qui n'en ont pas.
- **Périmètre** : `Menu/InstalledTemplate.cs` (`HasSetupLists`, `SetupBackdrop`), `Menu/MenuLayout.cs` (`Settings`).
- **Fini quand** : un modèle DVDFab Blu-ray avec `<setup>` dessine ses lignes là où le XML le dit, et un modèle sans `<setup>` garde la pastille « Settings » actuelle.

### T4 — Boutons ajoutés par l'utilisateur (et non plus seulement des éléments décoratifs)

- **But** : dans l'éditeur, ajouter un **bouton** avec une action choisie (« aller à la page X », « lire tel titre », « audio n », « sous-titre n », « retour au menu »), placé librement, en plus des éléments texte / image / cadre déjà livrés (T0).
- **Périmètre** : `MenuElement` (nouveau genre `button` + `MenuAction`), `MenuBuilder.WithElements`, rendu de l'état de focus, écrivains DVD (spumux) et Blu-ray (BD-J / `MenuFiles`) qui doivent voir ces boutons.
- **Fini quand** : un bouton ajouté à la main apparaît sur le disque, répond à la télécommande, et `check-elements` couvre son voisinage (haut / bas / gauche / droite).

### T5 — HDMV natif (phase 3 de la feuille de route)

- Non planifié : dépend de la phase 0 (essai sur lecteur de salon), qui demande un disque inscriptible et un lecteur.

## Règles de délégation

- Une seule tâche à la fois ; périmètre de fichiers explicite ; pas de retouche de `README.md`, `FEUILLE-DE-ROUTE-BLURAY.md` ni `ANALYSE-REA.md` par le sous-agent.
- Aucun binaire commercial copié dans le dépôt ; les outils libres (`dvdauthor`, `spumux`) ne servent qu'aux essais locaux, via le `PATH` du processus de test.
- Preuve obligatoire à chaque tâche : build sans avertissement, `check-templates` à 144, et un essai concret (disque construit, image de page, ou vérification autonome).
