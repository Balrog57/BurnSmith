# Plan de production de BurnSmith

Mis à jour le 16 novembre 2026 pour BurnSmith 1.0.36. **La création et la navigation des disques passent avant le travail sur l'interface d'authoring.** Le rapport « BurnSmith — ce qui manque, ce que les utilisateurs demandent, pistes d'amélioration » décrit la 1.0.27. Les pages 5 à 6 aident à faire le point sur les fonctions déjà livrées et les pages 9 à 10 sur le travail restant. Les propositions d'interface des pages 11 à 12 seront reprises quand les parcours de création auront passé leurs essais. Les essais actuels portent exclusivement sur des ISO dans VLC et PowerDVD. Le BD-RE est suspendu.

## Acquis

`main` comprend l'enregistrement et la restauration automatique du projet, les chapitres importés et nommés, l'audio externe avec décalage et volume, le style et le décalage des sous-titres, le son sans perte sur Blu-ray, la file de fabrication, le disque d'essai, l'ouverture dans VLC, le récapitulatif, le temps restant et la notification de fin. L'interface est disponible en français et en anglais. La galerie propose une recherche, des favoris et les modèles récents. Le menu HDMV simple existe. La compatibilité maximale l'utilise avec des réglages audio et de gravure adaptés.

**Résultats sur ISO 1.0.36.** VLC a lu les ISO UHD HDR10 et Dolby Vision. Une touche de remplacement dans des copies de test a permis d'ouvrir le pop-up. Les sous-titres du film et du bonus ont été activés puis désactivés pendant la lecture HDR10. Sur l'ISO Dolby Vision, le changement de piste TrueHD Atmos vers DTS-HD MA a produit du PCM stéréo non silencieux avant et après le changement. Les métadonnées Dolby Vision profil 7 MEL sont présentes dans l'ISO. VLC a utilisé une sortie SDR Rec.709 sur le Samsung de test. Le rendu Dolby Vision et la sortie bitstream n'ont pas été vérifiés sur ce PC.

PowerDVD 24 a refusé les deux ISO UHD comme format non pris en charge. L'éditeur de PowerDVD documente l'abandon de la lecture des disques UHD sur Windows. Les deux témoins Blu-ray 1080p, BD-J et HDMV, ont laissé un écran noir après le chargement. Les fonctions de navigation et de choix des pistes restent à vérifier dans PowerDVD. [Assistance CyberLink](https://support.cyberlink.com/faq/uhd-blu-ray-playback-removed-windows-powerdvd)

**Références sur platine.** Le DVD a été essayé sur platine et le Blu-ray 1080p BD-J de *Bright* a été lu sur BD-RE, chapitres compris. Les menus du lot de référence ont été validés sur Panasonic 820 et 9000. Ce lot comprend dix ISO, soit *Bright*, *Le Flic de Beverly Hills – Axel F*, les six saisons de *Cobra Kai* et les deux disques de *The Originals*, saison 5. Les essais UHD avec la chaîne Denon et LG sont documentés dans l'historique ci-dessous.

## Ce que les projets comparables apportent au moteur

**Blu-Disc Studio UHD** documente le profil BD-J 6, les paramètres de débit et de capacité du média, les rôles distincts du titre de menu et du titre de film et la trace des actions dans le simulateur. Cela donne des cas à vérifier sur les ISO de BurnSmith dès maintenant, dont démarrage, Top Menu, passage au titre suivant et retour automatique. Les réglages HDR des graphismes et sous-titres serviront à comparer la lisibilité du pop-up sur une chaîne HDR lorsque ces essais reprendront. La reprise de lecture persistante et les actions programmables par titre restent des fonctions distinctes de la commande Reprendre du pop-up actuel. [Réglages de projet](https://blu-disc.net/help/project_properties.general.htm) · [Simulation](https://blu-disc.net/help/options.htm) · [Graphismes HDR](https://blu-disc.net/help/project_properties.additional.htm) · [Actions de navigation](https://blu-disc.net/help/button_actions_common.htm)

**bdmvkit et oncemore** montrent deux contrôles utiles pour le Blu-ray HD avec menu natif. bdmvkit écrit les listes de lecture, les points d'accès et les commandes HDMV. La commande `validate` d'oncemore signale notamment les titres ou menus inaccessibles et les menus sans issue. BurnSmith peut appliquer ces contrôles à ses propres références et cibles. Ces projets servent de comparaison pour le HDMV classique. Les essais BD-J UHD gardent leur propre lot. [bdmvkit](https://github.com/kajisho5/bdmvkit) · [oncemore](https://github.com/kajisho5/oncemore)

**DVD Forge** vérifie les liens entre boutons, teste les changements de page et relit l'ISO avec un validateur indépendant du générateur. Les contrôles UDF, BDMV et DVD-Video décrits par le projet donnent des cas de corruption à injecter dans les tests BurnSmith. Ses tests et son simulateur partagent le même routage. C'est un principe à reprendre quand la navigation de BurnSmith sera éprouvée. [DVD Forge](https://github.com/Nicolasalucin/dvd-forge)

**DVDStyler et dvdauthor** servent de références pour le DVD-Video à plusieurs pistes, les chapitres et ce qui se passe après chaque titre. VLC/libdvdnav permet d'exercer les commandes de navigation à partir d'une ISO. Le démultiplexeur DVD-Video de FFmpeg permet de lire les pistes annoncées et de compter les paquets effectivement présents titre par titre. [DVDStyler](https://www.dvdstyler.org/en/) · [dvdauthor](https://dvdauthor.sourceforge.net/doc/) · [libdvdnav](https://images.videolan.org/developers/libdvdnav.html) · [FFmpeg DVD-Video](https://ffmpeg.org/ffmpeg-formats.html#dvdvideo)

**BDMV Author et tsMuxeR.** Le dépôt historique de tsMuxeR est archivé. BurnSmith embarque déjà la version patchée livrée par BDMV Author 0.1.133, dont les fichiers sont vérifiés par SHA-256. Garder cette version comme référence et comparer chaque candidat sur les mêmes sources et ISO. Le cache persistant des encodages de BDMV Author fournit aussi un exemple de réutilisation lors d'un changement de menu. [tsMuxeR](https://github.com/justdan96/tsMuxer) · [Cache de BDMV Author](https://github.com/blenheimears/bdmvauthor#project-files-and-cache) · [Moteur embarqué](COMPILER-ET-LIGNE-DE-COMMANDE.md)

## Défauts à reproduire avant chaque correction

**Contrôle BDJO corrigé localement.** Sur une copie d'un disque produit par BurnSmith, le contrôle de dossier 1.0.36 a accepté un `00000.bdjo` réduit aux huit octets `BDJO0300`. Le descripteur n'avait plus de table d'applications. Avec la correction locale, `check-disc` rejette ce dossier et identifie le fichier en cause. Le dossier Dolby Vision d'origine passe le contrôle. La compilation et `check-bd` complet, essais DVD compris, passent aussi avec les nouveaux cas de descripteur tronqué et de table invalide.

**Pistes DVD vérifiées en PAL et NTSC.** Sur l'ISO PAL 1.0.36, le bonus japonais annonçait deux pistes, dont une vide, et les libellés du bonus aux pistes inversées ne correspondaient pas aux flux. Sur les ISO reconstruites, FFprobe a confirmé les langues, les identifiants des flux et la présence de paquets pour chaque piste annoncée. Dans VLC, le choix français donne bien le signal à 880 Hz sur le film et sur le bonus aux pistes inversées. Sur le bonus sans piste française, la lecture revient au japonais, confirmé par le signal à 660 Hz. [FFmpeg DVD-Video](https://ffmpeg.org/ffmpeg-formats.html#dvdvideo)

**Sous-titres des bonus BD-J corrigés localement.** L'ISO UHD HDR10 1.0.36 transportait une piste PGS française dans le bonus, mais son pop-up ne proposait aucun choix si le film ne contenait aucun sous-titre. Sur l'ISO reconstruit, le pop-up propose la piste du bonus. Les captures et le journal de VLC confirment l'apparition du texte, sa disparition sur demande et le retour au film sans sous-titre. Le pop-up a été testé avec une touche de remplacement sur une copie de l'ISO.

**Sous-titres forcés vérifiés par titre en UHD BD-J.** Sur une ISO HDR10 créée à partir de sous-titres SRT convertis en PGS, choisir le français ouvre le film avec l'audio 2 et le sous-titre forcé français. Le bonus qui ne contient que de l'audio japonais bascule sur l'audio 1 et le sous-titre forcé japonais. Au retour au film, le français est restauré. Le journal BD-J de VLC et les trois captures de lecture confirment la séquence.

**Listes de lecture Blu-ray.** `check-disc` acceptait une liste `00001.mpls` réduite à son en-tête de huit octets. Il vérifie maintenant les limites des tables de lecture et de repères, ainsi que l'existence de l'élément visé par chaque repère. La suite `check-bd` couvre la liste tronquée et le repère qui vise un élément inexistant.

**UHD HDR10+ sans menu.** L'ISO de la mire démarre directement dans VLC et produit des images décodées. FFprobe retrouve les métadonnées dynamiques SMPTE ST 2094-40 dans le flux gravable. Le rendu HDR10+ sur écran reste à vérifier. En mode navigation Blu-ray, VLC atteint la fin du titre et reste ouvert malgré `--play-and-exit`. Un Blu-ray SDR sans menu reproduit ce comportement. En lecture directe du titre (`--no-bluray-menu`), VLC quitte à la fin des deux ISO.

## Ordre des corrections et critères de passage

1. **Durcir la validation du dossier et de l'ISO.** Commencer par le contrôle BDJO et couvrir ensuite les références entre titres, listes de lecture, clips et cibles des boutons, les chapitres hors limites et les flux déclarés absents. Pour chaque contrôle, une sortie conforme doit passer et le cas problématique doit produire un diagnostic qui identifie le fichier ou le titre en cause. Contrôler les flux contenus dans l'ISO et sa capacité après multiplexage.
2. **Corriger et vérifier les choix de lecture par titre.** Partir des cas d'échec ci-dessus et écrire les corrections dans le moteur BurnSmith. Fabriquer des ISO avec film, épisode et bonus aux pistes différentes. Vérifier la correspondance entre le libellé, l'index de piste et la langue réellement jouée, ainsi que le choix de repli si la piste n'existe pas. Exercer les sous-titres forcés, complets et désactivés à partir de sources SRT et PGS. Vérifier dans le même scénario les chapitres, Tout lire sans le bonus, la fin de titre et Top Menu.
3. **Passer un lot représentatif sur ISO.** Créer des DVD PAL et NTSC, des Blu-ray 1080p BD-J et HDMV et des UHD HDR10, HDR10+ et Dolby Vision. Inclure au moins un cas sans menu et une introduction. Vérifier les profils Dolby Vision converti et copié ainsi que les pistes TrueHD/Atmos et DTS-HD MA avec les outils d'inspection. Exercer la lecture et les menus dans VLC, puis essayer PowerDVD lorsqu'il ouvre le format. Consigner par ISO la version du lecteur, les étapes réussies et les blocages observés. `check-disc` valide la structure. Le décodage, les menus et la sortie audio/vidéo ont chacun leur propre preuve.
4. **Reprendre une fabrication interrompue.** Réutiliser les conversions terminées avec une clé qui tient compte des sources, des réglages d'encodage et des versions des outils. Publier chaque résultat complet de façon atomique. Après un échec au titre 3, les deux titres déjà vérifiés doivent être réutilisés. Une modification du menu seul doit réutiliser les conversions et une modification de piste doit invalider le titre qui la porte.
5. **Comparer le multiplexeur et terminer le flux séries.** Pour un candidat tsMuxeR, comparer pistes et métadonnées, synchronisation, débit, points d'accès, navigation et taille des images avec le moteur embarqué. Ensuite, éprouver la répartition d'une saison sur plusieurs ISO en gardant ordre, chapitres, choix de pistes et capacité de chaque volume.

L'interface d'authoring reprend après la validation des parcours couverts par le lot. Les capacités qui exigent une platine ou une chaîne HDR et bitstream restent consignées séparément. Construire l'installateur depuis le commit de version, relancer les contrôles concernés et vérifier la mise à jour avec conservation des réglages et des projets.

## Historique des essais matériels

### Essais sur BD-RE effectués précédemment

| Image | Relecture ImgBurn sur le BU40N | Résultat sur Panasonic |
|---|---|---|
| Blu-ray 1080p BD-J animé | Les 55 040 secteurs correspondent à l'image. | Le pop-up et la reprise pendant la lecture fonctionnent. |
| UHD HDR10 avec menu BD-J | Les 8 800 secteurs correspondent à l'image. | La platine reconnaît un Ultra HD Blu-ray et le téléviseur signale HDR. Le menu et le film ne démarrent pas. |
| Blu-ray 1080p HDMV (SDR) | Les 57 056 secteurs correspondent à l'image. | Le disque et son menu fonctionnent. L'absence d'indication HDR est attendue en SDR. |
| UHD HDR10 sans menu | Les 8 608 secteurs correspondent à l'image. | La mire de 9 secondes démarre et se lit en HDR. |
| UHD HDR10 avec menu HDMV | Les 11 968 secteurs correspondent à l'image. | Le menu s'affiche en SDR et Lecture lance la mire en HDR. Les clips vidéo du menu sont signalés en BT.709 et la mire en PQ / BT.2020. |
| UHD HDR10 BD-J avec en-têtes `BDJO0300` | Les 8 800 secteurs correspondent à l'image. | Le Panasonic affiche 4K HDR puis reste bloqué. Le menu ne démarre pas. |
| UHD HDR10 BD-J avec en-têtes `BDJO0300` et `BDID0300` | Les 8 800 secteurs correspondent à l'image. | Le menu ne démarre toujours pas sur le Panasonic. |
| UHD HDR10 BD-J en First Playback | Les 8 800 secteurs correspondent à l'image. | Le Panasonic reste bloqué au démarrage et le menu ne s'affiche pas. |
| UHD HDR10 BD-J en First Playback, profil 6 | Les 8 800 secteurs correspondent à l'image. | Le menu s'affiche et Lecture lance la mire en HDR. Le retour manuel retrouve l'apparence initiale du menu. Au retour automatique, les couleurs diffèrent. Le mode HDR de l'écran à cet instant reste à confirmer. |
| UHD HDR10 BD-J profil 6, navigation BurnSmith | Les 8 800 secteurs correspondent à l'image. | Le menu reste en SDR au démarrage et au retour automatique, avec les mêmes couleurs. La mire se lit en HDR. |

Les ISO UHD sans menu et UHD HDMV ont le même flux multiplexé que le disque UHD BD-J. Les deux passent `check-disc` et décodent le film en 3840 × 2160 dans VLC.

Le format UHD inclut le BD-J. Les premières variantes BurnSmith écrivaient le profil 1, version d'application 1.0.0. BurnSmith écrit désormais le profil 6 pour l'UHD, comme le documente Blu-Disc Studio. Le dernier essai du tableau confirme le démarrage de cette configuration sur Panasonic. [Blu-ray Disc Association](https://blu-raydisc.info/format-spec/rom4-spec.php) · [Documentation Blu-Disc Studio](https://blu-disc.net/help/project_properties.general.htm)

La variante `BDID0300` change seulement les deux en-têtes de `CERTIFICATE/id.bdmv` par rapport à l'ISO `BDJO0300`. Elle passe `check-disc` et VLC, puis reproduit le blocage sur platine. [Prise en charge de `BDID0300` dans libbluray](https://www.mail-archive.com/libbluray-devel@videolan.org/msg02774.html).

La variante de démarrage direct BD-J reprend `BDID0300` et change uniquement l'entrée First Playback d'`index.bdmv` et de sa sauvegarde. Elle lance le BDJO du menu directement et reproduit le blocage sur Panasonic. [Documentation de First Playback](https://bdedit.pel.hu/).

Le passage du profil d'application de 1 à 6 débloque le démarrage direct BD-J sur Panasonic. La version d'application reste 1.0.0. La dernière variante rétablit l'entrée First Playback de BurnSmith, avec des titres distincts pour le film et le menu. VLC confirme la transition de titre au retour automatique. Sur la platine, le menu retrouve ses couleurs d'origine et l'écran repasse en SDR. [Profil UHD documenté par Blu-Disc Studio](https://blu-disc.net/help/project_properties.general.htm).

### Validation du menu UHD BD-J complet

- **Navigation** · Sur l'ISO, vérifier que les flèches atteignent chaque bouton, que Lecture ouvre le bon titre, et que le retour automatique et la touche Top Menu réouvrent le menu. Blu-Disc Studio distingue le titre du menu du titre du film et propose un simulateur avec journal des actions. [Actions de navigation](https://blu-disc.net/help/button_actions_common.htm) · [Options de simulation](https://blu-disc.net/help/options.htm)
- **Pop-up en lecture** · Sur platine, ouvrir le pop-up en HDR, sauter à un chapitre, sélectionner la deuxième piste audio, activer puis désactiver un sous-titre et reprendre la lecture. Vérifier que le film reste en HDR et que le menu principal retrouve ses couleurs après la fin du film. Blu-Disc Studio expose ces actions et prévoit une conversion des graphismes superposés et des sous-titres pour le HDR. [Actions de lecture](https://blu-disc.net/help/button_actions_common.htm) · [Réglages HDR](https://blu-disc.net/help/project_properties.additional.htm)
- **Lot BD25** · L'ISO de test contient un fond animé, deux pistes audio, un sous-titre et un pop-up. Dans VLC, le fond animé, la lecture HDR10 et le retour au menu passent. ImgBurn a vérifié ses 13 952 secteurs après gravure à 2×. Le lot a été validé sur Panasonic, notamment l'ouverture du pop-up en lecture HDR, la reprise et le retour au menu.
- **Dolby Vision et Atmos** · La mire Dolby Vision 8.1, convertie en profil 7 MEL par BurnSmith, a été associée à une piste de démonstration TrueHD Atmos. ImgBurn a vérifié les 44 192 secteurs du BD-RE gravé à 2×. L'affichage Dolby Vision et la présence d'effets audio ont été vérifiés sur Panasonic. FFprobe identifie la piste du disque comme Dolby TrueHD + Dolby Atmos.
- **Deux pistes sans perte** · Un extrait de *Blade Runner 2049* a été préparé en Dolby Vision profil 7 MEL avec un menu BD-J et deux choix audio dans le pop-up. Les deux pistes ont été validées sur le Panasonic 9000 : VO Dolby TrueHD Atmos 7.1 et VF DTS-HD Master Audio 5.1. Matériel indiqué pour l'essai : ampli Denon 3800 et téléviseurs LG OLED C2 et C5. ImgBurn a vérifié les 76 384 secteurs du BD-RE gravé à 2×. La copie sans réencodage d'une source Dolby Vision profil 7 d'origine reste à vérifier sur platine.

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
| Installed on test PC | 0.5.0 in `C:\Program Files\BurnSmith` |
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
- **Requires explicit approval:** push, tag, GitHub release, installing on the test PC, committing another session's uncommitted work, deleting a branch or worktree.
- **Not in this plan:** the vendor artwork decision (recorded and final) and the features listed above.

### Phases

| Phase | Work | Findings | Main files | Depends on |
|---|---|---|---|---|
| 1. Merge | Merge `langue-des-pistes`, then `fond-par-page`, into `main`. Rebuild and rerun all checks. The project owner decides on the menu editor branch. | none | `Program.cs`, `MainWindow.xaml.cs`, `Tracks.cs`, menu files | Merge approval |
| 2. Discs and data | Safe `PrepareOutput`, 48 kHz, copy only compatible video, one frame-rate helper, DVD preflight, intro in fit-to-disc, work folder cleanup, PGS decoding. New `check-bd`. | 1, 2, 3, 4, 7, 10, 11, 12, 13 (part) | `Tools.cs` (`PrepareOutput`), `BluRay.cs`, `Dvd.cs`, `Tracks.cs`, `Disc/PgsStream.cs`, `Cli/` | Phase 1 |
| 3. App reliability | Real Cancel with process kill, `Closing` guard, no update during work, global handler with log file, UI start not blocked. | 5, 8, 14 | `Tools.Run`, `BluRay.Build`, `Dvd.Build`, `MainWindow`, `App.xaml.cs` | Phase 2 (same files) |
| 4. Release safety | Bundled tools first in a release, refuse a missing digest, clean old installers, pin ffmpeg in `publish.ps1`, fail on empty templates. | 6, 9 (digest), 15, 16 (part) | `Tools.Find`, `Updater.cs`, `publish.ps1` | Phase 1; can run beside phase 3 if `Tools.cs` edits are merged in turn |
| 5. CI | GitHub Action on Windows: build and `check-*`. Add the hand checks to `check-edit`. | 13 | `.github/`, `Cli/` | Phase 2 |
| 6. Release and hardware | Version 0.5.2, build from `git archive`, install, real update 0.5.0 → 0.5.2, uninstall. Burn a DVD-RW and play it on a player. | 9 (signing requires a project decision) | none | Phases 2 to 5 and approval for release and hardware tests |

The native HDMV menu stays on hold until the DVD-RW result is available from phase 6.

### Decisions requiring approval

1. Phase 1: is the menu editor branch committed and merged now, or left out of 0.5.2? It has not been tried with the mouse and is expected to conflict with `fond-par-page`.
2. Phase 6: go-ahead to push and publish, and whether to buy a code-signing certificate.
3. Phase 6: the test operator handles UAC prompts and verifies player playback.
