# File d'implémentation (orchestration)

Répartition : **REA / décompilation et orchestration** de mon côté, **implémentation** par Claude Code (sous-agent), une tâche à la fois pour éviter deux écritures sur les mêmes fichiers. Une tâche n'est déclarée finie que si la build est propre, que `check-templates` reste à 144 sans problème, et que la preuve demandée existe.

## Fait

### T0 — Éléments libres du menu (fait le 2026-11-05, commit `55ada06`)

`MenuElement` (texte / image / cadre), placement à la souris dans l'éditeur, `--element` en ligne de commande, `check-elements` (11 vérifications).

### T1 — Surbrillances DVD prises aux modèles (fait le 2026-11-05, commit `55ada06`)

`DvdHighlightImage` / `DvdHighlightMask` sur `MenuButton`, `MenuRenderer.Raster`, `IndexedPng.Stamp` / `Average`, composition page par page dans `Dvd.cs`, `check-highlights` (7 vérifications). Épreuve : DVD construit avec `DVDFab DVD | 01movie`, VOB de menu portant une piste `dvd_subtitle` (ffprobe), `check-templates` à 144.

**Ces deux tâches ont été écrites par l'orchestrateur, pas par le sous-agent** : deux délégations à Claude Code n'ont produit aucun code (la première a refusé de livrer sans pouvoir compiler, la seconde n'a rien rendu). Tant que `dotnet` reste hors de son bac à sable, la délégation utile est « écris, l'orchestrateur compile » — mais elle n'a pas encore abouti.

## À suivre

### T2 — Mises en page Leawo exactes (les 6 modèles qui ont un `template.xml`)

- **But** : remplacer l'interprétation de BurnSmith par les positions du fichier `MenuTemplate\Build-in\16-9\<thème>\template.xml` (`<Menu>` : `Frame`, `videoscr`, `TitleText`, `ButtonPlayText`, `ButtonScenesText` ; `<Scene><Page>` : `FrameN`, `videoscrN`, `ButtonNext`, `ButtonPre`, `Return`, `ButtonTextN`), pour 16-9 **et** 16:9-2, en 4:3 aussi.
- **Périmètre** : `Menu/LeawoSkin.cs` (aujourd'hui : mesure des ouvertures transparentes et placement deviné).
- **Fini quand** : pour les trois thèmes qui ont un XML (childhood01, education01, holiday01), les rectangles lus dans le XML sont utilisés tels quels ; `check-templates` inchangé ; `preview` d'un de ces modèles comparé à `background01.jpg` du modèle.
- **Note** : les 27 autres thèmes n'ont que des images (vérifié : aucun binaire Leawo ne contient leurs noms), donc la mesure d'ouverture reste la bonne méthode pour eux.

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
