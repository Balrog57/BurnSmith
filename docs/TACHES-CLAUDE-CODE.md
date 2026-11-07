# File d'implémentation (orchestration)

Répartition : **REA / décompilation et orchestration** d'un côté, **implémentation** de l'autre (Claude Code quand son bac à sable l'autorise, orchestrateur sinon). Une tâche n'est déclarée finie que si la build est propre, que `check-templates` reste à 144 sans problème, et que la preuve demandée existe.

## Fait

| Tâche | État | Commit |
|---|---|---|
| T0 — Éléments libres du menu (texte, image, cadre ; devant ou derrière les boutons ; éditeur + `--element` ; `check-elements`) | fait | `55ada06` |
| T1 — Surbrillances DVD prises aux modèles (`DvdHighlightImage` / `Mask`, `Raster`, `Stamp`, `Average`, `check-highlights`) | fait | `55ada06` |
| T4 — Boutons posés à la main (`Kind = button`, `Target`, `Relink` géométrique, éditeur, `check-elements` 13/13) | fait | `3766f0d` |
| Brief, tâche 1 — Réglages Yuhan en deux pages | fait | `2847db0`, `13de658`, `df38f81` |
| Brief, tâche 2 — Reprise au dernier bouton prouvée | fait | `f89af21` |
| T2 — Positions Leawo exactes | **annulée : déjà fait** | — |
| T3 — Pages de réglages DVDFab (`audiolist` / `subtitlelist`) | **annulée : déjà fait** | — |

Deux constats de cette file, vérifiés par dump plutôt que supposés :

- **Leawo** : `dump "childhood01 (16-9)"` donne le cadre à 699,307 en 752x498, le titre à 392,420 et Lecture à 220,532 — exactement les rectangles de `MenuTemplate\Build-in\16-9\childhood01\template.xml`. BurnSmith lisait donc déjà les positions de Leawo ; le repli par mesure ne sert qu'aux 27 dossiers sans XML et au cas où le XML de Leawo fait chevaucher ses propres miniatures (education01 16:9).
- **DVDFab Blu-ray** : `InstalledTemplate.Settings` lit déjà `audiolist` / `subtitlelist` (`frame_rect`, `line_height`) dès que le modèle les porte ; les lignes génériques ne servent qu'aux modèles `none_setup`.

## Reste

### T5 — Menu HDMV natif (phase 3 de la feuille de route)

- **But** : remplacer le menu BD-J par un menu HDMV (IG) écrit par BurnSmith, pour la compatibilité lecteurs de salon.
- **Pourquoi ce n'est pas commencé** : la phase 0 de la feuille de route (graver et lire sur un lecteur réel) n'est pas faite et demande un disque inscriptible et un lecteur. Écrire un second moteur de menu avant de savoir si le BD-J échoue serait du travail à l'aveugle.
- **Ce qui est déjà prêt** : la feuille de route §4, §4c et §4d donne la structure exacte (IG hors multiplexage, clip impair, sous-chemin de type 3, `MovieObject` avec R100-R108) ; le projet contient déjà le module `src/BurnSmith.Core/Hdmv/` (écrivain IG, navigation, clips), pas encore branché, et son vérificateur (`BurnSmith.HdmvCheck`).

### Petites fonctions encore absentes

- Sons de boutons (`sound.bdmv`), page Bonus paramétrable (nombre d'éléments, mode d'affichage), scènes par fichier, menu contextuel (pop-up).

## Règles de délégation

- Une seule tâche à la fois ; périmètre de fichiers explicite ; aucun sous-agent ne touche `README.md`, `FEUILLE-DE-ROUTE-BLURAY.md`, `ANALYSE-REA.md` ni ce fichier.
- Aucun binaire commercial copié dans le dépôt ; `dvdauthor` / `spumux` ne sont pas fournis : le mode DVD les demande à l'utilisateur ; une compilation de développement les trouve dans `tools\dvdauthor`.
- Preuve obligatoire : build sans avertissement, `check-templates` à 144, et un essai concret (disque construit, image de page, ou vérification autonome).
- Constat du 2026-11-05, dépassé : le bac à sable de Claude Code refusait alors `dotnet`. Depuis, les sessions dans leur propre worktree compilent et vérifient elles-mêmes.
