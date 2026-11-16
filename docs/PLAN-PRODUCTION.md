# Plan de production de BurnSmith

Mis à jour le 16 novembre 2026 à partir du rapport « BurnSmith — ce qui manque, ce que les utilisateurs demandent, pistes d'amélioration » et de l'état de `main`. Le rapport décrivait la 1.0.27. Ses pistes d'amélioration servent à prioriser le travail restant.

## Acquis

`main` comprend l'enregistrement et la restauration automatique du projet, les chapitres importés et nommés, l'audio externe avec décalage et volume, le style et le décalage des sous-titres, le son sans perte sur Blu-ray, la file de fabrication, le disque d'essai, l'ouverture dans VLC, le récapitulatif, le temps restant et la notification de fin. L'interface est disponible en français et en anglais. La galerie propose une recherche, des favoris et les modèles récents. Le menu HDMV simple existe. La compatibilité maximale l'utilise avec des réglages audio et de gravure adaptés.

Le DVD a été essayé sur platine. Le Blu-ray 1080p BD-J de Bright a été lu sur BD-RE, chapitres compris.

Marc a confirmé la validation des menus du lot de référence sur les Panasonic 820 et 9000. Le lot comprend dix ISO : *Bright*, *Le Flic de Beverly Hills – Axel F*, les six saisons de *Cobra Kai* et les deux disques de *The Originals*, saison 5. Ce lot sert de référence pour les menus. Les essais à venir ciblent les configurations et les fonctions qui restent à vérifier, notamment le pop-up, l'activation HDR et la vérification ImgBurn.

## Priorités restantes

| Priorité | Travail | Critère de fin |
|---|---|---|
| 1. Valider les ISO, puis la lecture sur platine | Fabriquer des ISO de test et les lire directement dans VLC. Utiliser le lot déjà validé sur Panasonic comme référence. Regrouper les points matériels à vérifier : comportement du menu HDMV, animation et pop-up BD-J, UHD HDR10 et Dolby Vision, ainsi que la gravure, la relecture ImgBurn et l'éjection sur le BD-RE réservé aux essais. | Consigner les résultats VLC, puis le modèle et le firmware du lecteur, le support, la version de BurnSmith, le démarrage, Top Menu, les chapitres, la fin d'un titre et le choix des pistes. Vérifier l'activation HDR ou Dolby Vision sur l'écran. |
| 2. Simplifier le premier disque | Rendre visibles les étapes Vidéos → Menu → Sortie. Donner un choix clair entre dossier, ISO et gravure, et rapprocher les réglages courants du projet. Alléger le panneau Élément quand aucune sélection n'est active. | Un nouvel utilisateur fabrique un disque d'essai, puis retrouve son projet au redémarrage. Vérifier le parcours à la souris et au clavier, en français et en anglais. |
| 3. Reprendre une fabrication interrompue | Réutiliser les titres déjà convertis. Associer chaque résultat à sa source et aux réglages qui influencent l'encodage, et écrire les résultats de façon atomique. | Après un échec au titre 3, la reprise réutilise les deux titres valides. Changer le menu seul réutilise les conversions, et modifier une piste invalide le titre concerné. |
| 4. Terminer le flux séries et copies | Proposer la répartition des titres entre plusieurs disques à partir de la capacité estimée et envoyer les projets dérivés dans la file existante. Permettre plusieurs gravures à partir d'une ISO, avec demande de changement de disque. | Une saison conserve l'ordre, le menu et les pistes sur chaque volume, chaque ISO respecte la capacité choisie et deux copies se font avec une seule fabrication. |
| 5. Entretenir le multiplexeur | Comparer le tsMuxeR embarqué à une version candidate sur un lot de référence comprenant HDMV, TrueHD, Dolby Vision, HDR10+ et sous-titres PGS. | Conserver les pistes, la navigation et la synchronisation. Documenter les régressions éventuelles et vérifier la lecture matérielle des cas modifiés avant de changer la version embarquée. |

La version Windows peut être considérée prête pour l'usage courant après les validations matérielles et le parcours de premier disque. La reprise et les volumes multiples améliorent les projets longs. Le simulateur complet, les sous-menus libres, les animations de boutons, les jaquettes intégrées, Mac/Linux et l'amélioration d'image par IA restent des évolutions ultérieures.

Pour chaque version, construire l'installateur à partir du commit de version et vérifier le parcours de mise à jour depuis une version installée, avec conservation des réglages et des projets. Relancer les contrôles CLI concernés et publier les résultats matériels avec les combinaisons effectivement essayées.

Pour chaque essai de disque, produire une ISO, contrôler sa structure avec `check-disc` et ouvrir l'image dans VLC. Vérifier la lecture, les chapitres, le retour au menu et les pistes présentes. Préparer ensuite un lot d'ISO et lister les points qui nécessitent la platine. Marc a autorisé la réutilisation du BD-RE réservé aux essais. La gravure se fait à 2× (`--speed 2`), avec relecture.

### Essais sur BD-RE

| Image | Relecture ImgBurn sur le BU40N | Résultat sur Panasonic |
|---|---|---|
| Blu-ray 1080p BD-J animé | Les 55 040 secteurs correspondent à l'image. | Marc confirme le pop-up et la reprise pendant la lecture. |
| UHD HDR10 avec menu BD-J | Les 8 800 secteurs correspondent à l'image. | La platine reconnaît un Ultra HD Blu-ray et le téléviseur signale HDR. Le menu et le film ne démarrent pas. |
| Blu-ray 1080p HDMV (SDR) | Les 57 056 secteurs correspondent à l'image. | Marc confirme que le disque et son menu fonctionnent. L'absence d'indication HDR est attendue en SDR. |
| UHD HDR10 sans menu | Les 8 608 secteurs correspondent à l'image. | Marc confirme que la mire de 9 secondes démarre et se lit en HDR. |
| UHD HDR10 avec menu HDMV | Les 11 968 secteurs correspondent à l'image. | Marc confirme que le menu s'affiche en SDR et que Lecture lance la mire en HDR. Les clips vidéo du menu sont signalés en BT.709 et la mire en PQ / BT.2020. |
| UHD HDR10 BD-J avec en-têtes `BDJO0300` | Les 8 800 secteurs correspondent à l'image. | Le Panasonic affiche 4K HDR puis reste bloqué. Le menu ne démarre pas. |
| UHD HDR10 BD-J avec en-têtes `BDJO0300` et `BDID0300` | Les 8 800 secteurs correspondent à l'image. | Le menu ne démarre toujours pas sur le Panasonic. |
| UHD HDR10 BD-J en First Playback | Les 8 800 secteurs correspondent à l'image. | Le Panasonic reste bloqué au démarrage et le menu ne s'affiche pas. |
| UHD HDR10 BD-J en First Playback, profil 6 | Les 8 800 secteurs correspondent à l'image. | Marc confirme que le menu s'affiche et que Lecture lance la mire en HDR. Le retour manuel retrouve l'apparence initiale du menu. Au retour automatique, les couleurs diffèrent. Le mode HDR de l'écran à cet instant reste à confirmer. |
| UHD HDR10 BD-J profil 6, navigation BurnSmith | Les 8 800 secteurs correspondent à l'image. | Marc confirme que le menu reste en SDR au démarrage et au retour automatique, avec les mêmes couleurs. La mire se lit en HDR. |

Les ISO UHD sans menu et UHD HDMV ont le même flux multiplexé que le disque UHD BD-J. Les deux passent `check-disc` et décodent le film en 3840 × 2160 dans VLC.

Le format UHD inclut le BD-J. Blu-Disc Studio documente un profil BD-J 6 pour ses projets UHD, tandis que BurnSmith écrit un descripteur d'application BD-J de profil 1, version 1.0.0, y compris en UHD. Cet écart est une piste pour le blocage au démarrage. Il reste à vérifier les champs du BDJO sur un disque UHD de référence et à reproduire la correction sur platine. [Blu-ray Disc Association](https://blu-raydisc.info/format-spec/rom4-spec.php), [documentation Blu-Disc Studio](https://blu-disc.net/help/project_properties.general.htm).

La variante `BDID0300` change seulement les deux en-têtes de `CERTIFICATE/id.bdmv` par rapport à l'ISO `BDJO0300`. Elle passe `check-disc` et VLC, puis reproduit le blocage sur platine. [Prise en charge de `BDID0300` dans libbluray](https://www.mail-archive.com/libbluray-devel@videolan.org/msg02774.html).

La variante de démarrage direct BD-J reprend `BDID0300` et change uniquement l'entrée First Playback d'`index.bdmv` et de sa sauvegarde. Elle lance le BDJO du menu directement et reproduit le blocage sur Panasonic. [Documentation de First Playback](https://bdedit.pel.hu/).

Le passage du profil d'application de 1 à 6 débloque le démarrage direct BD-J sur Panasonic. La version d'application reste 1.0.0. La dernière variante rétablit l'entrée First Playback de BurnSmith, avec des titres distincts pour le film et le menu. VLC confirme la transition de titre au retour automatique. Sur la platine, le menu retrouve ses couleurs d'origine et l'écran repasse en SDR. [Profil UHD documenté par Blu-Disc Studio](https://blu-disc.net/help/project_properties.general.htm).

### Validation du menu UHD BD-J complet

- **Navigation** · Sur l'ISO, vérifier que les flèches atteignent chaque bouton, que Lecture ouvre le bon titre, et que le retour automatique et la touche Top Menu réouvrent le menu. Blu-Disc Studio distingue le titre du menu du titre du film et propose un simulateur avec journal des actions. [Actions de navigation](https://blu-disc.net/help/button_actions_common.htm) · [Options de simulation](https://blu-disc.net/help/options.htm)
- **Pop-up en lecture** · Sur platine, ouvrir le pop-up en HDR, sauter à un chapitre, sélectionner la deuxième piste audio, activer puis désactiver un sous-titre et reprendre la lecture. Vérifier que le film reste en HDR et que le menu principal retrouve ses couleurs après la fin du film. Blu-Disc Studio expose ces actions et prévoit une conversion des graphismes superposés et des sous-titres pour le HDR. [Actions de lecture](https://blu-disc.net/help/button_actions_common.htm) · [Réglages HDR](https://blu-disc.net/help/project_properties.additional.htm)
- **Lot BD25** · L'ISO de test contient un fond animé, deux pistes audio, un sous-titre et un pop-up. Dans VLC, le fond animé, la lecture HDR10 et le retour au menu passent. ImgBurn a vérifié ses 13 952 secteurs après gravure à 2×. Marc a confirmé le bon fonctionnement du lot sur Panasonic, dont l'ouverture du pop-up en lecture HDR, la reprise et le retour au menu.
- **Dolby Vision et Atmos** · La mire Dolby Vision 8.1, convertie en profil 7 MEL par BurnSmith, a été associée à une piste de démonstration TrueHD Atmos. ImgBurn a vérifié les 44 192 secteurs du BD-RE gravé à 2×. Marc a confirmé l'affichage Dolby Vision et la présence d'effets audio sur Panasonic. FFprobe identifie la piste du disque comme Dolby TrueHD + Dolby Atmos.
- **Deux pistes sans perte** · Un extrait de *Blade Runner 2049* a été préparé en Dolby Vision profil 7 MEL avec un menu BD-J et deux choix audio dans le pop-up. Marc confirme le fonctionnement des deux pistes sur le Panasonic 9000 : VO Dolby TrueHD Atmos 7.1 et VF DTS-HD Master Audio 5.1. Matériel indiqué pour l'essai : ampli Denon 3800 et téléviseurs LG OLED C2 et C5. ImgBurn a vérifié les 76 384 secteurs du BD-RE gravé à 2×. La copie sans réencodage d'une source Dolby Vision profil 7 d'origine reste à vérifier sur platine.

## Clôture du projet

1. Fusionner dans `main` toutes les PR et branches de travail du projet. Avant de retirer une branche ou son worktree, vérifier que ses changements sont intégrés et récupérer tout travail local encore utile.
2. Pousser `main` et vérifier que la CI passe sur le commit final. Publier la version finale depuis ce même commit.
3. Une fois le travail intégré, retirer les branches et worktrees devenus inutiles. Ranger hors du dépôt les fichiers privés à conserver. À la fin, il reste un seul dossier de travail BurnSmith, sur `main`, sans changement en attente et synchronisé avec `origin/main`. Aucune PR du projet ne reste ouverte.

## Archive du 5 novembre 2026

Les sections suivantes décrivent l'état historique de la 0.5.1 et les décisions de cette date.

Sources: five reports written on 2026-11-05, checked against the repository, GitHub and test runs. Only the code review is kept: `docs/RAPPORT-SESSION-2026-11-05.md` (finding numbers below refer to it). This document replaces the other four.

Only two labels are used. **Checked** means seen today in the code, git, GitHub or a test run. **Reported** means a report says so and it could not be re-checked; treat it as a lead, not as proof.

## 1. Facts

### Project state (checked)

| Fact | Value |
|---|---|
| Published release | v0.5.1 is Latest, tag at `d5d619b`, with `BurnSmith-Setup-0.5.1.exe` (356,216,337 bytes) and `BurnSmith.exe`, both with a GitHub SHA-256 digest |
| `main` | `a567dd3`, 5 commits ahead of `origin/main` (`d5d619b`), not pushed |
| Version in the code | 0.5.1, the same number as the release although the code is newer |
| Installed on Marc's PC | 0.5.0 in `C:\Program Files\BurnSmith` |
| Local build | `dist\BurnSmith-Setup-0.5.1.exe` from `627e3cd`, not the same file as the GitHub one, unsigned, not installed |
| Code size | about 10,900 lines of C#, about 7,500 of them in `BurnSmith.Core` |
| Tests | No test project, no CI (`.github/` absent). Checks are CLI commands. |
| Signing | No signing step in `publish.ps1`, `installer/BurnSmith.iss` or `bdj/build.ps1` |
| `shortcut:` comments | 7 |

### Branches not merged in `main` (checked)

| Branch | Content | Files it changes compared to `main` |
|---|---|---|
| `langue-des-pistes` | Track language: 2 commits | `MainWindow.xaml.cs`, `Program.cs`, `Tracks.cs` |
| `fond-par-page` | Background per page: 2 commits, base older than `main` | `MenuEditorWindow.xaml(.cs)`, `Program.cs`, `MenuDesign.cs`, `MenuRenderer.cs` |
| menu editor branch | menu editor: **no commit, all uncommitted** in its worktree, about 800 added lines | `App.xaml.cs`, `MenuEditorWindow.xaml(.cs)`, `Program.cs`, `ElementLibrary.cs`, `MenuElement.cs`, `MenuLayout.cs` |
| `claude/hdmv-menu-csharp-c5f4bb` | Native HDMV menu worktree | On hold (see phase 6); contents not reviewed |

### Check results at `627e3cd` (checked; all exit 0)

`check-update` 5, `check-edit` 51 (50 without `dvdauthor`), `check-elements` 15, `check-highlights` 11, `check-templates` 144: all passed, build with 0 warnings and 0 errors.

These prove that discs and menus build with the right structure. They do not prove that a disc plays on hardware or looks like the original template.

### Defects (finding numbers from the review)

| # | Defect | Evidence |
|---|---|---|
| 1 | `Tools.PrepareOutput` deletes everything in a folder that contains one `VIDEO_TS` or `BDMV` entry | Run: other files were deleted |
| 2 | Blu-ray: 44.1 kHz sources become 44.1 kHz AC-3 | Run: `ffprobe` on the m2ts |
| 3 | Blu-ray: 10-bit H.264 is copied unchanged and `check-disc` passes | Run: `ffprobe` shows `High 10` |
| 4 | Blu-ray: a re-encoded 24.000 fps source drifts, video ended 1.76 s before the audio in 30 min | Run: packet times |
| 5 | Cancel only acts between steps (no token in `Build` or `Tools.Run`), no `Closing` handler, update can shut down mid-burn | Code |
| 6 | Parent `tools` folders are searched before the bundled tools | Run: fake `ffprobe.exe` was used. `C:\tools` exists here and users can create it (`icacls`) |
| 7 | DVD without `dvdauthor`: encodes first, then fails. Without `spumux`: logs "subtitles dropped" and continues | Run |
| 8 | No global exception handler and no log file; `Update()` reads the size of each source on the UI thread | Code |
| 9 | Updater runs the installer unchecked if GitHub gives no digest; digest and file come from the same source; installer unsigned | Code |
| 10 | Blu-ray "fit to disc" does not count the intro; the DVD path does | Code |
| 11 | `<output>_work` is deleted only on success | Code |
| 12 | Truncated PGS data throws `IndexOutOfRangeException`, only `InvalidDataException` is caught | Code |
| 13 | `check-disc` passes findings 2 and 3; no CI | Run |
| 14 | `MainWindow` runs the template scan on the UI thread, which waits for the same locks as the unpacking | Code; duration not measured on a release |
| 15 | `publish.ps1` ships the first ffmpeg on `PATH`; templates can be left out silently | Code |
| 16 | Small list in the review | Code |

### Not done or never tried

- **Checked as absent from the repo:** a test for the Settings page after a DVD intro, or for an NTSC intro on a PAL film.
- **Reported as never tried:** real burning, a living-room player, uninstall, a real update from an installed version, the mouse in the new editor, the WPF window in the `fond-par-page` and `langue-des-pistes` work.
- **Reported, not re-checked:**
  - DVD Settings page after an intro and NTSC intro on PAL film were checked by hand in VLC.
  - Installed 0.5.0 starts and works.
  - `check-elements` reaches 31 on the menu editor branch and 19 on `fond-par-page`; `check-edit` reaches 54 on `langue-des-pistes`.
  - `check-templates` lists overlapping buttons in some carried templates as "to review".
  - Unsigned BD-J jars are refused by some players.
- **Features reported as missing:** video and music per page, cut slider, built-in preview, button sounds, configurable Bonus page, pop-up menu, undo in the editor, SVG buttons, template opened as editable elements, UHD re-encoding, native HDMV menu.

### Claims removed because they are false or out of date

| Claim in a report | Reality |
|---|---|
| A wrong output folder choice destroys nothing | False, finding 1 |
| Cancel gets out of a stuck ffmpeg | False, it acts only after the step ends |
| Tool versions are fixed by the payload | Only inside one build; `publish.ps1` takes what is on the PC (finding 15) |
| Temporary folders are cleaned up | Only on success (finding 11) |
| DVD fails cleanly when tools are missing | It fails after encoding (finding 7) |
| Last release is 0.5.0 | It is 0.5.1 |
| `check-templates` is 114/114 | It is 144 |
| `main` has 4 CA2013 warnings | The build at `627e3cd` has 0 warnings |
| "46 discs without a false positive" | Cannot be checked: removed |

## 2. Plan

### Rules for every phase

- **Writers:** one worktree and branch per phase. Two sessions never change the same files at the same time.
- **Done means:** build with 0 warnings; all checks in section 1 still pass; the defect is reproduced before the fix and gone after it, with a check that fails if it returns.
- **Release builds:** always from `git archive <sha>`, never from a working tree with other sessions' edits.
- **Never without Marc's explicit OK:** push, tag, GitHub release, installing on his PC, committing another session's uncommitted work, deleting a branch or worktree.
- **Not in this plan:** the vendor artwork decision (recorded and final) and the features listed above.

### Phases

| Phase | Work | Findings | Main files | Depends on |
|---|---|---|---|---|
| 1. Merge | Merge `langue-des-pistes`, then `fond-par-page`, into `main`. Rebuild and rerun all checks. Marc decides on the menu editor branch. | none | `Program.cs`, `MainWindow.xaml.cs`, `Tracks.cs`, menu files | Marc's OK to merge |
| 2. Discs and data | Safe `PrepareOutput`, 48 kHz, copy only compatible video, one frame-rate helper, DVD preflight, intro in fit-to-disc, work folder cleanup, PGS decoding. New `check-bd`. | 1, 2, 3, 4, 7, 10, 11, 12, 13 (part) | `Tools.cs` (`PrepareOutput`), `BluRay.cs`, `Dvd.cs`, `Tracks.cs`, `Disc/PgsStream.cs`, `Cli/` | Phase 1 |
| 3. App reliability | Real Cancel with process kill, `Closing` guard, no update during work, global handler with log file, UI start not blocked. | 5, 8, 14 | `Tools.Run`, `BluRay.Build`, `Dvd.Build`, `MainWindow`, `App.xaml.cs` | Phase 2 (same files) |
| 4. Release safety | Bundled tools first in a release, refuse a missing digest, clean old installers, pin ffmpeg in `publish.ps1`, fail on empty templates. | 6, 9 (digest), 15, 16 (part) | `Tools.Find`, `Updater.cs`, `publish.ps1` | Phase 1; can run beside phase 3 if `Tools.cs` edits are merged in turn |
| 5. CI | GitHub Action on Windows: build and `check-*`. Add the hand checks to `check-edit`. | 13 | `.github/`, `Cli/` | Phase 2 |
| 6. Release and hardware | Version 0.5.2, build from `git archive`, install, real update 0.5.0 → 0.5.2, uninstall. Burn a DVD-RW and play it on a player. | 9 (signing is Marc's choice) | none | Phases 2 to 5 and Marc for each step |

The native HDMV menu stays on hold until Marc has the DVD-RW result from phase 6.

### Decisions only Marc can make

1. Phase 1: is the menu editor branch committed and merged now, or left out of 0.5.2? It has not been tried with the mouse and is expected to conflict with `fond-par-page`.
2. Phase 6: go-ahead to push and publish, and whether to buy a code-signing certificate.
3. Phase 6: he runs the UAC prompts, the burn and the player test.
