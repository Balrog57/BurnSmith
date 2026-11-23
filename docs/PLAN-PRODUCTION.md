# Plan de production de BurnSmith

Mis à jour le 22 novembre 2026 à partir de BurnSmith 1.0.37 (branche de travail `main`) et des trois rapports comparatifs de novembre 2026 (*BurnSmith — ce qui manque*, *Guide comparatif Authoring Blu-ray*, *Panorama 2026*). **La fiabilité de fabrication, la compatibilité platine et la qualité du code passent avant les fonctions secondaires.** Les essais actuels sur ce poste portent sur des ISO dans VLC et sur les suites automatisées ; la reprise des essais physiques sur BD-RE/platine reste planifiée pour les points non encore prouvés matériellement.

## Acquis (1.0.37 + travail en cours)

Depuis le bilan 1.0.27, la majorité des manques « utiles à presque chaque disque » a été livrée :

- **Flux de travail** : enregistrement et restauration automatique du projet (`autosave.burnsmith`), file de fabrication (`queue`), répartition **manuelle** d'une saison sur plusieurs disques avec suggestion automatique dès que la saison dépasse de 30 % la capacité utilisable du support, disque d'essai 5 min, ouverture directe dans VLC, récapitulatif avant démarrage, temps restant, notification et extinction en fin de travail. La gravure en série (plusieurs copies d'une même ISO) est **abandonnée**.
- **Pistes, chapitres et formats** : chapitres repris du fichier et nommés au menu, audio externe avec décalage et volume par piste, style et décalage des sous-titres texte, sous-titres forcés par langue, son sans perte Blu-ray (TrueHD/Atmos avec noyau AC-3, DTS-HD MA, LPCM, Dolby Digital Plus autonome copié depuis 1.0.37), 4K UHD natif (HEVC 10 bits, HDR10, HDR10+, Dolby Vision profil 7 en passthrough et profil 8 → 7 MEL en conversion).
- **Menus et navigation** : interface FR/EN, 144 modèles avec recherche, filtres, favoris et récents, menu HDMV natif simple et mode « Compatibilité maximale », sous-menus personnalisés, liens directionnels par page, actions de fin BD-J par titre, démarrage auto après délai, fondu de page BD-J (rendu sur platine à qualifier), contraste du pop-up, reprise entre sessions BD-J (implémentée côté code, reste à finaliser/qualifier), curseur animé et boucle BD-J automatique, commande `check-navigation` et bouton **Navigation**.
- **Reprise et cache** : cache persistant par titre (`%LOCALAPPDATA%\BurnSmith\encode-cache`) partagé entre fenêtre, CLI et file de fabrication (reprise après arrêt et changement de menu sans réencoder).

**Reste à faire et à qualifier en priorité (suivi direct)** : la reprise de lecture entre sessions (BD-J), le simulateur interactif et son équivalent DVD/HDMV, la calibration HDR (luminance des menus/pop-up/sous-titres en HDR) ainsi que le rendu du fondu sur platine.

**Résultats sur ISO 1.0.36 / 1.0.37.** VLC a lu les ISO UHD HDR10 et Dolby Vision. Une touche de remplacement dans des copies de test a permis d'ouvrir le pop-up. Les sous-titres du film et du bonus ont été activés puis désactivés pendant la lecture HDR10. Sur l'ISO Dolby Vision, le changement de piste TrueHD Atmos vers DTS-HD MA a produit du PCM stéréo non silencieux avant et après le changement. Les métadonnées Dolby Vision profil 7 MEL sont présentes dans l'ISO. VLC a utilisé une sortie SDR Rec.709 sur le Samsung de test. Le rendu Dolby Vision et la sortie bitstream n'ont pas été vérifiés sur ce PC.

PowerDVD 24 a refusé les deux ISO UHD comme format non pris en charge. L'éditeur de PowerDVD documente l'abandon de la lecture des disques UHD sur Windows. Les deux témoins Blu-ray 1080p, BD-J et HDMV, ont laissé un écran noir après le chargement. Les fonctions de navigation et de choix des pistes restent à vérifier dans PowerDVD. [Assistance CyberLink](https://support.cyberlink.com/faq/uhd-blu-ray-playback-removed-windows-powerdvd)

**Références sur platine.** Le DVD a été essayé sur platine et le Blu-ray 1080p BD-J de *Bright* a été lu sur BD-RE, chapitres compris. Les menus du lot de référence ont été validés sur Panasonic 820 et 9000 (dix ISO : *Bright*, *Le Flic de Beverly Hills – Axel F*, six saisons de *Cobra Kai*, deux disques de *The Originals* saison 5). Les essais UHD avec la chaîne Denon et LG sont documentés dans l'historique ci-dessous et dans la [matrice de compatibilité](COMPATIBILITE.md).

## Comparatif : fonctionnalités essentielles des autres logiciels encore manquantes

Le positionnement de BurnSmith est déjà unique en gratuit/libre sur Windows : il combine 4K UHD natif (HDR10/HDR10+/Dolby Vision), vrais menus BD-J avec pop-up, menu HDMV simple et 144 modèles, là où TMPGEnc Authoring Works 7 n'a pas de 4K UHD, Blu-Disc Studio et EasyBD n'ont pas Dolby Vision, et les alternatives libres récentes (OpenBDAuth, oncemore) restent limitées au Blu-ray HD.

Le tableau ci-dessous recense uniquement les **écarts essentiels encore absents** face aux références du marché pour atteindre un logiciel complètement fonctionnel :

| Priorité | Fonctionnalité manquante | Références concurrentes | Écart actuel dans BurnSmith |
|---|---|---|---|
| **P1 — Compatibilité** | **Menu pop-up et fond animé en HDMV natif** | TMPGEnc 7, EasyBD + IG Editor, oncemore, Yuhan | Le mode HDMV (« Compatibilité maximale ») reste limité à des pages fixes sans pop-up. Un pop-up HDMV couvrirait les platines qui rejettent BD-J. |
| **P1 — Séries** | **Réalignement des pistes audio sur une série** | Disques commerciaux (cohérence multi-épisodes) | Seuls les sous-titres sont réalignés entre épisodes (`LineUpSubtitles`). Des épisodes aux pistes audio dans un ordre différent ou absentes peuvent faire tomber le choix du menu sur la mauvaise langue. |
| **P1 — Navigation** | **Simulateur interactif unifié (BD-J, HDMV et DVD) et carte de navigation** | TMPGEnc 7, Blu-Disc Studio, dvd-forge | **Reste à faire** : `check-navigation` lit le BD-J fabriqué et l'aperçu actuel reste limité à des images fixes ; il manque un vrai simulateur interactif (et son équivalent DVD/HDMV), une carte visuelle des pages/boutons et un routage partagé entre simulateur et génération (principe *dvd-forge*). |
| **P1 — HDR & BD-J** | **Calibration HDR, reprise entre sessions et rendu du fondu sur platine** | Blu-Disc Studio (réglages HDR graphismes/sous-titres, reprise persistante), Scenarist | **Reste à faire / qualifier** : calibration de luminance HDR des menus, pop-up et sous-titres ; finalisation et qualification platine de la reprise de lecture entre sessions ; qualification sur platine du rendu du fondu de page BD-J. |
| **P2 — Ergonomie** | **Parcours guidé et fenêtre principale allégée** | TMPGEnc 7, Yuhan, Wondershare, Leawo | Propositions du bilan 1.0.27 encore en attente : 3 étapes numérotées (1 Vidéos / 2 Menu / 3 Sortie), choix unique « Que voulez-vous obtenir ? » (dossier / ISO / gravure), masquage de la colonne HDR en DVD, filtrage des graveurs virtuels, réglages courants sortis de « Paramètres avancés », mode Simple / Complet. |
| **P2 — Chapitrage** | **Chapitres automatiques aux changements de scène** | TMPGEnc 7 (*Smart Scene Search*) | Import MKV, intervalle fixe et saisie manuelle existent, mais pas la détection automatique de scènes pour les vidéos familiales ou personnelles sans chapitres. |
| **P2 — Sous-titres & son** | **Position/taille des sous-titres en images (PGS/VobSub) et normalisation Loudness** | BDSup2Sub, TMPGEnc 7 | Le style ne s'applique qu'aux sous-titres texte (SRT/ASS/VTT) ; les PGS/VobSub ne sont ni déplaçables ni redimensionnables. Le volume audio reste un gain manuel sans normalisation Loudness. |
| **P3 — Éditeur avancé** | **Import graphique PSD multi-calques et boutons animés riches** | Blu-Disc Studio, Scenarist Designer, oncemore, MAGIX Video Pro X | L'éditeur gère PNG/JPG/BMP/SVG, curseur animé et fondu de page BD-J, mais pas l'import direct de PSD (états normal/sélectionné/activé) ni les transitions élaborées. |
| **P3 — Formats** | **Sortie AVCHD dédiée (clé USB / carte SD)** | TMPGEnc 7, Power2Go, tsMuxeR, dvd-forge | Non proposée comme profil de sortie dédié. |
| **P3 — Moteur** | **Veille et alternative au multiplexeur tsMuxeR** | Blu-Disc Studio MX (muxeur interne), bdmvauthor, bdmvkit | Le dépôt amont `justdan96/tsMuxer` est archivé depuis avril 2025. BurnSmith embarque déjà le fork patché `bdmvauthor 0.1.133` épinglé par SHA-256 ; maintenir un banc comparatif pour suivre ses évolutions ou un remplaçant. |

**Hors périmètre assumé pour la 1.x** : gravure en série (abandonnée), portage macOS/Linux (couplage WPF `net10.0-windows`), pressage industriel CMF, multi-angle, création de jaquettes, amélioration IA SDR→HDR.

## Qualité du code et dette technique (audit du 22 novembre 2026)

La base compte **88 fichiers (~30 400 lignes)** en C#, XAML et Java, avec `<Nullable>enable</Nullable>` activé et une compilation `Release -warnaserror` sans avertissement. Plusieurs chantiers de qualité restent toutefois nécessaires pour fiabiliser et maintenir le logiciel dans la durée :

1. **Fichiers monolithiques à découper.** Sept fichiers dépassent 1 000 lignes et mélangent des responsabilités distinctes : `BurnSmith.Cli/Program.cs` (2 513 lignes), `MainWindow.xaml.cs` (1 854 lignes), `MenuEditorWindow.xaml.cs` (1 762 lignes), `BurnSmith.Core/BluRay.cs` (1 628 lignes), `InstalledTemplate.cs` (1 383 lignes), `BdCheck.cs` (1 145 lignes) et `Menu.java` (1 003 lignes). Découper en modules ciblés (parseur CLI par sous-commande, services de file/gravure/projet séparés du code-behind WPF, pipeline Blu-ray séparé entre analyse, encodage et multiplexage) réduira le risque de régression.
2. **Couplage plateforme de `BurnSmith.Core`.** `BurnSmith.Core.csproj` active `<UseWPF>true</UseWPF>` pour le rendu d'images/menus. Isoler le rendu derrière une abstraction clarifie la séparation entre logique métier (disques, projets, validation) et couche graphique.
3. **Couverture de tests et trous dans la CI.** Les vérifications reposent sur les commandes CLI (`check-update`, `check-edit`, `check-elements`, `check-highlights`, `check-templates`, `check-conversion`, `check-library`, `check-fidelity`, `check-bd`, `check-reliability`) :
   - En CI GitHub Actions, `check-edit`, `check-bd` et `check-reliability` tournent avec `--no-dvd` : les essais DVD réels ne sont pas exécutés automatiquement.
   - Les ajouts récents manquent encore de tests automatisés en CI : `volumes` (répartition manuelle + seuil de suggestion +30 %), `check-navigation`, sous-menus personnalisés, liens directionnels, `--auto-start`, `--remember-position`.
   - Aucun test automatisé ne couvre la couche WPF (sauvegarde/rechargement, undo/redo, file d'attente, `SeasonSplitWindow`) ni la compilation du xlet Java (`assets/bdj/menu.jar` est commité en binaire sans étape `javac`/signature en CI).
4. **Raccourcis assumés (`shortcut:`) à résorber.** Le code documente proprement **35 commentaires `shortcut:`**, dont plusieurs affectent directement l'usage réel :
   - **Projets non portables** (`Project.cs`) : chemins absolus ; un projet déplacé ou un dossier renommé perd ses vidéos (résolution relative ou dialogue de relocalisation à prévoir).
   - **Photos de téléphone** (`MediaMaker.cs`) : rotation EXIF ignorée dans les diaporamas.
   - **Estimation 4K** (`MainWindow.xaml.cs`) : les pistes audio décochées ne sont pas soustraites de la taille estimée en UHD.
   - **Annulation dans l'éditeur** (`MenuEditorWindow.xaml.cs`) : le renommage des vidéos sur les pages échappe à Ctrl+Z.
   - **Persistance de la file d'attente** (`MainWindow.xaml.cs`) : la file disparaît si la fenêtre est fermée.
   - **Gestion du cache d'encodage** (`EncodeCache.cs`) : budget de 100 Go sans éviction automatique (LRU) ni bouton « Vider le cache » dans l'interface.
   - **Gravure et progression** (`Iso.cs`, `Imapi.cs`) : pas de remontée de progression fine depuis ImgBurn vers la barre de la fenêtre, et chemin IMAPI encore non validé sur graveur physique.
   - **DVD à pistes hétérogènes** (`Dvd.cs`) : un seul title set limite à 8 descripteurs audio communs.
5. **Hygiène du dépôt et sécurité de publication.** Normaliser les fins de ligne (`.gitattributes` / `.editorconfig` — plusieurs fichiers mélangent LF et CRLF), nettoyer les fichiers d'inspection temporaires et anciens worktrees avant release, et trancher la signature de code de l'installateur Windows.

### Cache des encodages

Le cache est partagé entre l’interface, la CLI et la file de fabrication du même build. Il se trouve dans `%LOCALAPPDATA%\BurnSmith\encode-cache`. La clé inclut le build du moteur, les réglages de préparation, le débit retenu et le chemin, la taille et la date de modification des médias, des fichiers d’effets et des outils utilisés. Les états de menu et le dossier de sortie sont hors de cette clé. Un changement de capacité qui modifie le débit peut donc imposer un nouvel encodage.

Chaque préparation complète est publiée par renommage de dossier après copie. Une interruption pendant l’écriture ne rend pas la préparation partielle réutilisable. En cas de lecture impossible ou d’entrée incomplète, BurnSmith refait la préparation. Le journal indique les réutilisations. Le cache utilise un budget de 100 Go sans éviction automatique (éviction LRU et vidage depuis l'interface à ajouter). L’identification par taille et date suppose qu’un fichier modifié reçoit un nouvel horodatage.

## Plan d'action priorisé vers un logiciel complètement fonctionnel

| Phase | Objectif | Livrables & critères de passage |
|---|---|---|
| **Phase 1 — Consolidation qualité & CI** *(fait)* | Sécuriser tout ce qui vient d'être livré avant d'ajouter de nouvelles fonctions | **Fait** : suite `check-plan` ajoutée en CI (`menu.jar` Java 1.3 major 47, `volumes` manuel + suggestion +30 %, `check-navigation`, sous-menus, liens directionnels, `--auto-start`, `--remember-position`, `LineUpAudio`, `ResolvePaths`, `MenuNavigationMap`, HDMV animé/pop-up, calibration HDR) ; déclenchement CI nocturne (`schedule` + `workflow_dispatch`) sans `--no-dvd` ; `.gitattributes` normalisant LF/CRLF. |
| **Phase 2 — Finitions moteur & simulateur** *(fait)* | Fermer les écarts fonctionnels critiques sur les disques réels | **Fait** : réalignement des pistes audio sur les séries (`DiscTitle.LineUpAudio` en Blu-ray et DVD) ; éviction LRU + bouton « Vider le cache » dans Paramètres avancés (`EncodeCache.Stats/Clear`) ; résolution des chemins relatifs / déplacés dans `.burnsmith` (`Project.ResolvePaths`) ; rotation EXIF dans les diaporamas (`MediaMaker.ExifRotationFilter`) ; soustraction des pistes audio décochées dans l'estimation 4K ; persistance de la file d'attente (`queue.json`) ; carte de navigation unifiée (`MenuNavigationMap`) et simulateur interactif unifié BD-J / HDMV / DVD (`DiscSimulator` + journal en direct dans `3 · Aperçu`). |
| **Phase 3 — Compatibilité platine, HDR & HDMV avancé** *(code fait ; essais platine ouverts)* | Rendre le mode HDMV aussi fiable que le BD-J et préparer la qualification matérielle | **Fait côté code et contrôles ISO** : fond animé HDMV (`HdmvMenu.Add` avec `Loop`) et menu pop-up HDMV natif avec libellés rendus (`HdmvMenu.BuildPopupIgs`, `MenuRenderer.PopupButtonPixels`, `IgStream`, `Nav.PopupOff`, `Nav.LinkMark`) vérifiés par `DiscCheck.Folder` ; calibration de luminance HDR (`HdrGraphicsNits`, `SubtitleStyle.HdrNits`, `--hdr-nits`, référence 203 nits ITU-R BT.2408) pour menus BD-J/HDMV, pop-up et sous-titres texte/PGS. **Reste hors poste (matériel requis)** : qualification physique sur BD-RE/platine consignée dans [COMPATIBILITE.md](COMPATIBILITE.md) (reprise entre sessions, rendu du fondu BD-J, pop-up/fond HDMV, HDR10+, DV profil 7 natif, DD+ autonome). |
| **Phase 4 — Refactoring architectural** *(fait)* | Réduire la dette technique pour maintenir facilement la 1.x | **Fait** : découpage en modules et classes partielles cohérentes de `BurnSmith.Cli` (`Program.cs` ~90 lignes + `CliBuild.cs`, `CliDisc.cs`, `CliTools.cs`, `HighlightsCheck.cs`, `ElementsCheck.cs`, `UpdateCheck.cs`, `EditCheck.cs`, `QualifPack.cs`), `MainWindow` (`MainWindow.xaml.cs`, `MainWindow.Project.cs`, `MainWindow.Build.cs`), `VideoEditWindow` (`VideoEditWindow.xaml.cs`, `VideoEditWindow.Preview.cs`, `VideoEditWindow.Chapters.cs`), `MenuEditorWindow` (`MenuEditorWindow.xaml.cs`, `MenuEditorWindow.Elements.cs`, `MenuEditorWindow.Zones.cs`, `MenuEditorWindow.Canvas.cs`, `MenuEditorWindow.Layers.cs`, `MenuEditorWindow.Library.cs`), `InstalledTemplate` (`InstalledTemplate.cs`, `InstalledTemplate.Assets.cs`, `InstalledTemplate.Layout.cs`), `MenuBuilder`/`MenuRenderer` (`MenuBuilder.Overrides.cs`, `MenuRenderer.Effects.cs`) et `BluRay` (`BluRay.cs`, `BluRay.Encode.cs`, `BluRay.Menu.cs`, `BluRay.MenuMedia.cs`, `BluRay.DiscImport.cs`), plus modules autonomes (`PlanChecks.cs`, `MenuNavigationMap.cs`, `DiscSimulator.cs`, `MenuValidator.cs`, `SoundBank.cs`, `PsdReader.cs`, `ChapterFiles.cs`, `SeasonSplitWindow.xaml(.cs)`, `DiscImportWindow.xaml(.cs)`) ; projet de tests unitaires xUnit [BurnSmith.Tests](file:///c:/Users/Marc/Documents/1G1R/_Programmation/BurnSmith/tests/BurnSmith.Tests/BurnSmith.Tests.csproj) (24 tests) intégré à `BurnSmith.slnx` et à la CI (`dotnet test`), incluant le garde-fou automatique 100 % `en.tsv`. |
| **Phase 5 — Interface guidée & confort d'authoring** *(fait)* | Rendre BurnSmith évident dès la première ouverture (pages 11–12 du bilan) | **Fait** : fenêtre principale dotée des 3 étapes guidées (`1 · Vidéos`, `2 · Menu`, `3 · Sortie`) avec garde-fous dynamiques sur `Démarrer`, choix unique de sortie (`Obtenir : Dossier / Image ISO / Disque gravé / Gravé + ISO`), réglages courants sortis de *Paramètres avancés* (`Mode de lecture`, `Chapitres auto`), colonne HDR masquée en mode DVD ; éditeur de menus en 3 étapes (`Modèle · Personnaliser · Aperçu`) avec sélecteur unifié *Affichage des scènes*, accordéons avancés, pastilles couleur cliquables (palette 24 teintes), inspecteur contextuel avec barre d'alignement 6 directions (zone sûre TV / centre), tiroir bibliothèque, bande de pages sur 1 rangée, menu `+ Ajouter ▾` et undo/redo sur le renommage des vidéos ; détection automatique de chapitres aux changements de scène (`VideoEditWindow` → « Détecter les scènes »), import/export de fichiers de chapitres OGM/MKV XML/CSV (`ChapterFiles.cs`, `--chapters-file`) et sélecteur de titres pour l'import de disques Blu-ray/DVD (`DiscImportWindow`). |
| **Phase 6 — Ouverture prosumer & finition base d'authoring (Lots 1–5)** *(fait)* | Rejoindre les meilleurs outils payants sur les fonctions de finition et verrouiller le moteur avant le chantier dédié aux menus | **Fait** : repositionnement, redimensionnement et calibration HDR des sous-titres en images PGS/VobSub (`PgsStream.Transform`) + fermeture propre des PGS coupés (`CloseGaps`) ; normalisation Loudness audio EBU R128 (`EncodeOptions.NormalizeAudio`, `--loudnorm`) ; encodage matériel multi-GPU auto-détecté (`NVENC → QuickSync → AMF → CPU`, `BluRay.Encode.cs`) ; désentrelacement (`bwdif`) et débruitage (`hqdn3d`) dans *Montage vidéo* ; `CheckRoom` Blu-ray avant travail + réserve bitrate menu DVD + synchronisation audio/keyframe en coupe 4K passthrough ; DVD multi-VTS complet (`VTS_01..VTS_nn` + routeur VMGM) quand > 8 pistes/configurations audio ; `DiscCheck` durci (cohérence versions UHD `0300`, fréquences audio Blu-ray 48/96/192 kHz, limites DVD 8 audio / 32 SPU) ; diaporamas > 600 photos découpés par lots (`MediaMaker.cs`) ; filtrage des graveurs virtuels et progression ImgBurn temps réel ; import graphique Photoshop PSD multi-calques (`PsdReader`) ; sortie AVCHD dédiée (`AVCHD (USB / SD)`, `--avchd`) ; générateur `make-qualif-pack` (4 ISO prêts à graver). |
| **Phase 7 — Atelier de création de menus avancé (Lots 1–6)** *(fait)* | Allier la simplicité d'accueil à la personnalisation poussée des menus BD-J, HDMV, UHD HDR et DVD | **Fait** : panneau des calques unifié (ordre Z, masquer, verrouiller, Tout afficher), multi-sélection (`Maj+clic` + lasso), distribution égale horizontale/verticale et grille magnétique (40 px / aimantation 20 px) ; effets visuels par élément (`ElementOpacity`, `Rotation`, `Outline`, `Shadow`, `ImageBlur`, `GradientAngle`) ; 3 états de bouton (`Normal`/`Sélection`/`Activé`) + import PSD 3 calques + curseurs BD-J intégrés (`pulse`/`ring`/`arrow` + PNG) + éditeur visuel Pop-up Blu-ray (`PopupStyle`, `IgStream`, `HdmvMenu`) ; validateur normatif temps réel (`MenuValidator` : superpositions, plafonds DVD 18/36, orphelins, zone sûre), cibles catégorisées, focus par défaut, liens 4 directions + recalage auto, flèches sur canevas et mode DVD fidèle (masquage contrôles BD + aperçu SPU 4 couleurs) ; fonds vidéo/audio par page (`PageMedias` avec conservation audio via `-map 0:a:0`), vignettes animées Blu-ray (`AnimatedThumbs` / découpes alpha) et banque de bruitages `sound.bdmv` (`BCLK0200`) + `HSound` WAV 48 kHz (`SoundBank`) avec aperçu audio temps réel. |

## Références des projets comparables

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

**Repères Blu-ray hors clip.** Une copie de test avec un chapitre placé après la fin de son élément de lecture passait aussi `check-disc`. Le contrôle vérifie maintenant que chaque repère de chapitre est dans l'intervalle de l'élément référencé. `check-bd` rejette les repères placés juste avant le début ou juste après la fin. [Interprétation des repères dans libbluray](https://raw.githubusercontent.com/ShiftMediaProject/libbluray/master/src/libbluray/bdnav/navigation.c)

**Démarrage et titres Blu-ray.** Une copie de l'UHD avec un objet HDMV de démarrage inexistant passait le contrôle de dossier et bloquait dans VLC avant le menu. Le contrôle vérifie maintenant les tables de `index.bdmv` et `MovieObject.bdmv`, ainsi que les cibles HDMV et BD-J de la première lecture, du menu principal et des titres. Dans VLC, une ISO Blu-ray 1080p BD-J neuve a enchaîné l'introduction, le menu, la lecture du film et le retour automatique. La commande Top Menu pendant la lecture a aussi rouvert le menu. [Index libbluray](https://raw.githubusercontent.com/ShiftMediaProject/libbluray/master/src/libbluray/bdnav/index_parse.c)

**ISO HDMV 2D.** Le multiplexeur place des informations de découpe dans les clips vidéo et graphiques des menus HDMV. BurnSmith vérifie désormais la table `ProgramInfo_SS` de la vue dépendante avant de fabriquer un `.ssif`. La nouvelle ISO HDMV 2D est écrite par IMAPI, conserve ses clips de menu et ne contient plus de `.ssif`. L'essai MVC synthétique vérifie toujours l'assemblage des deux vues dans l'image. [Écriture des extensions CLPI dans tsMuxeR](https://github.com/justdan96/tsMuxer/blob/master/tsMuxer/tsPacket.cpp)

**Chapitre suivant dans VLC 3.0.24.** Les listes HDMV et BD-J 1080p contiennent les repères 0, 8 et 16 s et les boutons du disque ouvrent le chapitre de 8 s. Avec la mire à très bas débit, la lecture anticipée amène libbluray à signaler le deuxième chapitre alors que VLC affiche 0 ou 1 s. La commande « Suivant » incrémente ce numéro et lance le troisième chapitre. Avec une mire à environ 8 Mbit/s de même durée, la même commande atteint le repère de 8 s sur les deux ISO. Les repères produits par BurnSmith restent aux positions demandées. [Suivi des repères par libbluray](https://raw.githubusercontent.com/ShiftMediaProject/libbluray/master/src/libbluray/bluray.c) · [Commande Suivant de VLC 3.0.24](https://github.com/videolan/vlc/blob/3.0.24/src/input/input.c#L2111)

**UHD HDR10+ sans menu.** L'ISO de la mire démarre directement dans VLC et produit des images décodées. FFprobe retrouve les métadonnées dynamiques SMPTE ST 2094-40 dans le flux gravable. Le rendu HDR10+ sur écran reste à vérifier. En mode navigation Blu-ray, VLC atteint la fin du titre et reste ouvert malgré `--play-and-exit`. Un Blu-ray SDR sans menu reproduit ce comportement. En lecture directe du titre (`--no-bluray-menu`), VLC quitte à la fin des deux ISO.

**Tout lire.** Sur quatre nouvelles ISO, DVD PAL, Blu-ray HDMV, Blu-ray BD-J et UHD HDR10 BD-J, VLC a enchaîné les deux épisodes dans l'ordre et est revenu au menu après le deuxième. Le bonus est resté accessible depuis sa page. Les captures DVD et les journaux Blu-ray confirment le parcours.

## Qualification ISO du 19 novembre 2026

Le candidat a été compilé en Release avec le SDK .NET 10.0.404 à partir de 7221b6f, des modifications locales déjà en cours et des correctifs ci-dessous. La CLI installée 1.0.36 et une compilation témoin du même état local ont servi à reproduire les défauts. Le multiplexeur utilisé est tsMuxeR git-c6b1186-bdmvauthor, SHA-256 785C40C2B232B3F167D9C58842EB688C354FC289C09E9C7B8A057D644E540F31. FFmpeg et FFprobe 8.1.1, dovi_tool 2.3.4 et VLC 3.0.24 ont servi aux essais.

| Cas | Résultat |
| --- | --- |
| Piste Blu-ray déclarée sans paquets | Le témoin AVC/AC-3 passe sur dossier et ISO. La variante dont tous les paquets du PID 0x1100 ont été neutralisés échoue dans les deux cas avec la liste, le clip et le PID. Le contrôle recoupe les pistes des MPLS et CLPI avec la PMT et les débuts de PES. Une seconde ISO avec la PMT neutralisée est rejetée pour les PID 0x1011 et 0x1100. Après multiplexage, le moteur vérifie également le nombre de pistes audio demandé pour chaque titre. |
| HEVC Main 8 en UHD | La copie constatée sur le témoin a été remplacée par une conversion annoncée. ISO remontée en lecture seule, FFprobe confirme Main 10, 4:2:0 10 bits. FFmpeg décode le clip. |
| Comparaison IMAPI | La lecture partielle se poursuit à l'octet suivant. Un test couvre la lecture complète en plusieurs fragments, le volume trop court et la différence après un fragment identique. Dans l'essai Windows, le volume ISO monté expose 655 360 octets de moins que la référence allongée. Le témoin rendait 0, le candidat rend 1. Le montage virtuel peut aussi masquer le bourrage final de l'ISO d'origine. |
| Dolby Digital Plus | La piste autonome à 48 kHz est convertie en LPCM ou en AC-3 à 48 kHz, avec un message sur les éventuels objets Atmos. Sur une mire retardée, le décalage de 700 ms devient 695 ms après conversion LPCM et multiplexage. Depuis la version 1.0.37, BurnSmith copie aussi les pistes autonomes à 48 kHz. La lecture de ces pistes sur platine reste à vérifier. Le témoin DD+ Blu-ray 7.1 de FFmpeg porte des paires noyau AC-3 et extension dépendante. Il est copié avec le même SHA-256 audio 8151FE17C94EDE5960FF629305A43EBF8989520E562275CF7A6800DE4694489E. [Échantillon de décodage FFmpeg](https://samples.ffmpeg.org/A-codecs/AC3/eac3/dependentsubstreams/00007.m2ts) |
| Dolby Vision 5 et 8.4 | Le témoin supprimait les RPU et annonçait du HDR10 dans les deux cas. Le candidat identifie les profils et refuse copie et conversion avant de préparer la sortie. Les mires servent à vérifier la signalisation et le diagnostic. [Profils Dolby Vision](https://ott.dolby.com/OnDelKits/Dolby_Vision_Online_Delivery_Kit/v1/Documentation/Specs/Visio_Profiles/help_files/topics/c_dovi_profiles_public.html) |
| Dolby Vision 8.1 | Avec base PQ, BT.2020 et matrice BT.2020 non constante, la copie conserve la base HDR10 sur 0x1011 et retire les RPU. La conversion conserve un profil 7 MEL avec 24 paquets vidéo sur chacun des PID 0x1011 et 0x1015 sur la mire d'une seconde. Les deux ISO passent le contrôle et leur base se décode. La suite vérifie aussi la structure et le nombre d'images de l'extension. |

Le lot reconstruit comprend DVD PAL 25 i/s, DVD NTSC 29,97 i/s, Blu-ray 1080p HDMV, Blu-ray 1080p BD-J et UHD HDR10 BD-J. Les cinq ISO passent `check-disc`. FFprobe relève deux pistes audio de 48 kHz avec paquets dans chacun des deux épisodes et une piste dans le bonus. FFmpeg décode 880 Hz pour la piste française, 440 Hz pour l'anglaise et 660 Hz pour le japonais. Les pistes sources inversées du deuxième épisode retrouvent le même ordre à la fabrication selon le choix de piste par défaut. Sur les DVD, le bonus reçoit un SPU transparent avec un paquet réel pour garder le numéro de sous-titre commun.

Dans VLC, « Tout lire » joue les deux épisodes dans l'ordre et revient au menu sur les cinq ISO. Le bonus est lancé séparément depuis sa page. Les captures distinguent les mires rouge, bleue et jaune. Le WAV de VLC confirme le repli japonais à 660 Hz sur chaque format. Les journaux libbluray renseignent les changements de listes HDMV et BD-J et les journaux de la CLI recensent les contrôles de chapitres, Top Menu, pop-up et associations de sous-titres. Deux ISO HDMV et BD-J supplémentaires portent des repères à 0, 8 et 16 s. VLC atteint les deux repères suivants et rouvre le menu principal. L'afficheur indique 7 puis 16 s en HDMV et 7 puis 15 s en BD-J.

La reprise du travail sur les menus a porté sur le curseur animé BD-J déjà en cours. Le paquet contient les images et les coordonnées mises à l'échelle de la page quand les images sont disponibles. En cas de fichier manquant ou d'en-tête d'image illisible, le menu utilise sa surbrillance fixe et le journal l'indique. `check-highlights` vérifie le paquet valide et le repli.

La compilation intégrée passe sans avertissement. Les suites complètes `check-bd`, `check-reliability`, `check-edit`, `check-recap` et `check-highlights` passent. Les journaux et les SHA-256 du lot sont sous `%TEMP%\BurnSmith-moteur-20261119\lot-master`, avec les diagnostics appariés et les ISO UHD sans menu sous `%TEMP%\BurnSmith-moteur-20261119`. Les essais du candidat restent logiciels. La qualification sur installateur propre, les platines, le rendu HDR, le bitstream HDMI, le Dolby Vision profil 7 natif et les lectures longues restent à couvrir. Aucun essai de gravure.

**Curseur animé vérifié dans VLC 3.0.24.** Les deux images de la mire alternent sur Chapitres et le curseur suit la sélection vers Bonus. Le chapitre 2 démarre à 2 s et le bonus ouvre sa propre liste de lecture. Réglages permet de choisir l'anglais, puis le journal confirme la lecture de la piste 2. Une seconde ISO avec une image manquante affiche le curseur fixe blanc. En fin de titre, VLC conserve la dernière image lorsque le menu ne lance aucun clip de fond, aussi bien avec le curseur animé qu'avec le témoin fixe. Le journal BD-J confirme le retour à la page principale. Sur la troisième ISO, une boucle de fond créée avec une piste silencieuse rétablit le retour visible dans VLC. Les trois ISO et les journaux se trouvent dans `%TEMP%\BurnSmith-curseur-BDJ-20261119`.

Le choix d'un modèle dans l'éditeur reprend désormais ses images de curseur. Les copies du modèle possèdent leur propre liste d'images et le passage à un modèle statique retire l'animation précédente. `check-elements` et `check-highlights` couvrent le changement de modèle et l'empaquetage.

**Boucle BD-J automatique.** Les menus BD-J sans vidéo de fond ni musique démarrent désormais un clip noir de 10 s avec une piste AC-3 stéréo silencieuse à 48 kHz. Le graphisme du menu reste opaque. Sur deux ISO reconstruites sans piste de menu fournie, VLC 3.0.24 a réaffiché le menu après la fin du titre et restauré le bouton Lecture, avec le curseur animé puis avec le curseur fixe. Les journaux confirment le retour à la liste 00099. Les ISO, journaux et SHA-256 sont dans `%TEMP%\BurnSmith-boucle-BDJ-20261119`.

## Ordre des corrections et critères de passage

1. **Durcir la validation du dossier et de l'ISO.** Commencer par le contrôle BDJO et couvrir ensuite les références entre titres, listes de lecture, clips et cibles des boutons, les chapitres hors limites et les flux déclarés absents. Pour chaque contrôle, une sortie conforme doit passer et le cas problématique doit produire un diagnostic qui identifie le fichier ou le titre en cause. Contrôler les flux contenus dans l'ISO et sa capacité après multiplexage.
2. **Corriger et vérifier les choix de lecture par titre.** Partir des cas d'échec ci-dessus et écrire les corrections dans le moteur BurnSmith. Fabriquer des ISO avec film, épisode et bonus aux pistes différentes. Vérifier la correspondance entre le libellé, l'index de piste et la langue réellement jouée, ainsi que le choix de repli si la piste n'existe pas. Exercer les sous-titres forcés, complets et désactivés à partir de sources SRT et PGS. Vérifier dans le même scénario les chapitres, Tout lire sans le bonus, la fin de titre et Top Menu.
3. **Passer un lot représentatif sur ISO.** Créer des DVD PAL et NTSC, des Blu-ray 1080p BD-J et HDMV et des UHD HDR10, HDR10+ et Dolby Vision. Inclure au moins un cas sans menu et une introduction. Vérifier les profils Dolby Vision converti et copié ainsi que les pistes TrueHD/Atmos et DTS-HD MA avec les outils d'inspection. Exercer la lecture et les menus dans VLC, puis essayer PowerDVD lorsqu'il ouvre le format. Consigner par ISO la version du lecteur, les étapes réussies et les blocages observés. `check-disc` valide la structure. Le décodage, les menus et la sortie audio/vidéo ont chacun leur propre preuve.
4. **Qualifier le cache sur des projets longs.** Le test de fiabilité couvre l’annulation après le premier titre, la reprise, le changement de menu et l’invalidation par titre. Mesurer le temps et l’espace sur une saison réelle et vérifier les pistes HD ainsi que les variantes HDR dans les sorties reconstruites.
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
