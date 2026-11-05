# Reproduire la création de Blu-ray avec menu de DVDFab, Yuhan et Leawo : inventaire, briques libres, feuille de route

Date : 2026-11-04. Suite de `COMPARAISON-DVDFAB-BLURAY.md` (qui compare les moteurs). Ici : **toutes les fonctions** des trois créateurs, ce que BurnSmith a déjà, quelles briques libres utiliser, et dans quel ordre.

Étiquettes : **[chaînes]** texte lu dans les fichiers de traduction ou l'exécutable (prouve qu'un libellé existe, pas son comportement) ; **[REA]** décompilation ; **[octets]** fichier décodé ; **[testé]** exécuté ou décodé ici ; **[déduit]** raisonnement. Rien n'a été lancé côté Yuhan, Leawo ou DVDFab (analyse statique seulement) ; bdmvkit et le VLC portable du projet ont, eux, été exécutés (section 4b).

## 0. Résumé

1. **MultiAVCHD n'est pas libre.** C'est un freeware fermé ; son auteur n'a fait qu'évoquer une publication du code en décembre 2023 (forum Doom9), rien n'est sorti. Le dépôt GitHub homonyme `donnafaye0323/MultiAVCHD` n'a pas de lien établi avec l'auteur : à ne pas télécharger. On ne peut donc pas bâtir BurnSmith « sous le capot de MultiAVCHD ».
2. **Les vraies briques libres pour un menu HDMV** sont **bdmvauthor** (Apache-2.0, C++20/Qt6) et **bdmvkit** (LGPL-3.0-ou-plus, Python). Les deux sont récents, expérimentaux et **non essayés sur un lecteur réel** par leurs auteurs. Licences compatibles avec la GPL-3.0-ou-plus de BurnSmith. **Essai ici** : bdmvkit (2 539 lignes de Python, aucune dépendance) a produit un disque à menu de trois boutons que libbluray a joué en boucle complète (menu → sélection → activation → film → retour au menu) [testé, 4b]. bdmvauthor a été cloné mais **pas compilé** : sa construction Windows exige MSYS2 + Qt 6 (non installés ; plusieurs Go à autoriser).
3. **tsMuxeR 2.7.0 (celui de BurnSmith) ne sait pas écrire un menu** : son aide ne liste que `S_HDMV/PGS` (sous-titres) et `S_TEXT/UTF8` pour les graphiques [testé : aide du programme et chaînes de l'exe] ; et un flux de menu donné comme piste PGS (le tour de passe-passe de bdmvkit) est déclaré dans la table et le fichier CLPI mais **tsMuxeR n'écrit aucun paquet pour lui** [testé, 4b]. Le clip de menu doit donc être muxé autrement (ffmpeg, comme le fait bdmvkit) ; tsMuxeR continue de servir pour le film.
4. **Un vrai menu HDMV a été décodé** (ISO de référence de l'utilisateur, clip `01001.m2ts`) : une référence concrète à reproduire, voir 4. Attention : c'est un disque d'amateur (« Fans Edition »), dont la lecture sur lecteur de salon n'est pas documentée ; ce n'est pas une preuve de compatibilité.
5. **Recommandation** : d'abord gagner en ergonomie sans nouveau moteur (phase 1-2 : projets, sous-titres externes, éditeur libre, première vidéo…), pendant qu'un **essai sur lecteur réel** (phase 0) dit si le BD-J non signé suffit ; puis écrire le menu HDMV en C# (option B, 5) en **portant bdmvkit** (LGPL-3.0-ou-plus : son écrivain IG, son quantificateur 255 couleurs, son RLE et sa navigation tiennent en quelques centaines de lignes) et en gardant libbluray comme juge de test.

## 1. Sources

- **État de BurnSmith lu au commit `e16e752`** (HEAD à la fin de ce travail). Une autre session commite dans le même dépôt (quatre commits entre le début et la fin) : les cellules « ✘ / ~ » de la section 2 sont à revérifier avant de s'en servir.
- Libellés des interfaces, lus dans les `.qm` de Qt : Yuhan ≈ 419 libellés / 24 écrans, Leawo ≈ 222 / 30 (+ `custom.ts` complet), DVDFab ≈ 451 / 80 [chaînes]. Un libellé ne prouve pas que la fonction marche ni qu'elle est complète.
- Moteurs : décompilation REA et décodage de fichiers, voir `COMPARAISON-DVDFAB-BLURAY.md` [REA] [octets].
- Disque de référence : `BR_LBAMVFVOST.iso` (« La boîte à musique (Fans Edition) », disque d'amateur, HDMV pur ; extraits : index, objets, playlists, `CLIPINF`, et les clips de menu `01001.m2ts` / `01002.m2ts` de 1,3 Mo ; `00001.m2ts` (18 Mo) aussi ; le film principal de 18 Go n'a pas été extrait).
- Briques libres : dépôts lus sur GitHub ; avec votre accord (2026-11-04) `blenheimears/bdmvauthor` (commit `e8b9f0d`) et `kajisho5/bdmvkit` (0.7.0, commit `5789076`) ont été clonés chacun dans un dossier neuf du dossier de travail de session. bdmvkit a été exécuté ; bdmvauthor non (voir 4b).

## 2. Inventaire des fonctions et état de BurnSmith

Y = Yuhan, L = Leawo, D = DVDFab (créateur). ✔ fait · ~ en partie · ✘ pas fait. « Brique » = ce qui évite de tout écrire.

### 2.1 Sources et projet

| Fonction | Y | L | D | BurnSmith | Brique / remarque |
|---|---|---|---|---|---|
| Ajouter vidéos (fichiers, dossier), ordre, supprimer, haut/bas | ✔ | ✔ | ✔ | ✔ | — |
| Enregistrer / ouvrir un projet | ✔ | ✔ | — | **✘** (aucune commande ouvrir / enregistrer ; la barre ne propose que « Ajouter une source » ; `grep -i project` sur `*.cs` et `*.xaml` ne trouve que des libellés) | JSON ; petit, gros gain d'ergonomie |
| Photos, dossier de photos → diaporama (durée, **22 effets de transition**, musique, boucle) | ✔ | ✔ | — | ✘ | ffmpeg `xfade` et filtres : rendre le diaporama en vidéo (simple) plutôt qu'un diaporama HDMV natif |
| Disque de musique (pochette plein/demi-écran) | ✔ | — | — | ✘ | ffmpeg : image + audio → vidéo |
| Sous-titres externes (SRT, ASS, SUP) | ✔ | ✔ | ✔ (police, position, délai) | ~ (pistes du fichier seulement : PGS copié, texte → SRT) | tsMuxeR accepte SRT et PGS [testé] ; ASS à convertir |
| Source ISO / dossier DVD / Blu-ray | — | ✔ (« Add DVD », « Add iso file ») | n.é. | ✘ | hors périmètre ici (copie) ; seulement sans protection |
| Titres depuis les métadonnées du fichier | ✔ | — | — | à vérifier | ffprobe |

### 2.2 Édition par titre

| Fonction | Y | L | D | BurnSmith | Brique / remarque |
|---|---|---|---|---|---|
| Début / fin, volume, chapitres (intervalle ou liste) | ✔ | ✔ | ✔ | ✔ (commits `492345d`, `e2a5a48`) | — |
| Langues audio / sous-titres par titre | ✔ | ✔ | ✔ | ✔ (choix des pistes) | — |
| Plusieurs extraits d'une vidéo, fusion, inversion de la coupe | — | — | ✔ (écran du Convertisseur) | ✘ | ffmpeg `concat` |
| Recadrage, luminosité / contraste / saturation, rotation, filigrane | — | — | ✔ (écran du Convertisseur) | ✘ | ffmpeg `crop`, `eq`, `transpose`, `overlay` ; secondaire pour un créateur |
| Amélioration IA (HDR, résolution, fluidité, sous-titres IA) | ✔ (HDR) | — | ✔ | ✘ | **non étudié** ; hors périmètre |

### 2.3 Sortie et disque

| Fonction | Y | L | D | BurnSmith | Brique / remarque |
|---|---|---|---|---|---|
| DVD / Blu-ray 1080p / 720p / 4K UHD | ✔ | ✔ | ✔ | ✔ | — |
| Profils BD50/25/9/5, qualité, « adapter au disque » | ✔ | ✔ | ✔ | ✔ | — |
| Copie du flux (passthrough) ou ré-encodage, UHD HDR | ✔ | ✔ | ✔ | ~ (UHD en copie seulement) | x265 `uhd-bd=1` ; `dovi_tool` / `hdr10plus_tool` (MIT) plus tard |
| Encodeur matériel NVENC / QSV / AMF avec repli | ✔ | ✔ (garde « BD-compliant ») | ✔ | ✘ | ffmpeg ; contrôler le flux produit avant de le garder |
| Étiquette, titre unique ou plusieurs titres, langue par défaut | ✔ | ✔ | ✔ | ~ | à compléter |
| Mode de lecture (menu puis tous, 1er titre puis suite, rembobiner) | ~ (« passer le menu ») | n.é. | ✔ | ✔ | — |
| NTSC / PAL, 16:9 / 4:3 (DVD) | — | n.é. | ✔ | ✔ | — |
| Dossier / ISO / graveur, vérification | ✔ | ✔ | ✔ | ✔ (IMAPI2, **jamais essayé sur disque réel**) | — |
| **ISO d'essai des 5 premières minutes** | ✔ | — | — | ✘ | existe déjà via `--trim` : un bouton suffit |
| Après la fin : éteindre / ne rien faire | ✔ | — | ✔ | ✘ | trivial |
| BDInfo (analyse d'un disque), BDFix (réparation d'un Blu-ray) | — | — | ✔ (outils séparés) | ✘ | hors cœur ; libbluray / `bd_info` peut servir d'analyseur |

### 2.4 Menus et modèles

| Fonction | Y | L | D | BurnSmith | Brique / remarque |
|---|---|---|---|---|---|
| Modèles intégrés, choix, aperçu | 30 thèmes | 33 dossiers | 22 + 5 | ✔ 144 | — |
| Mes modèles / enregistrer mon menu | ✔ | ✔ (Mon BD / DVD / UHD) | ✔ (« My Blu-ray ») | ✔ | — |
| Télécharger d'autres modèles | — | ✔ | ✔ | ✘ | sources en ligne repérées (mémoire « Menu templates ») ; téléchargement = demande d'accord |
| Pages : principal, scènes, réglages (audio / sous-titres) | ✔ | ~ (principal + scènes) | ✔ | ✔ (+ page Crédits, que les trois n'ont pas) | — |
| Page **Bonus** (nombre d'éléments, mode d'affichage) | ✔ | — | — | ✘ | modèle de données à étendre |
| **Menu contextuel (pop-up)** | ✔ | — | — | ✘ | n'existe qu'en HDMV (`ui_model` pop-up) : phase 3 |
| Scènes : par chapitre ou par fichier, icône ou texte, éléments par page, « exclure la 1re vidéo », « une vidéo par page » | ✔ | ~ (liste de chapitres) | ~ | ~ (chapitres / épisodes, grille) | mise en page |
| Fond image ou vidéo, boucle, début / fin, durée max, volume, fondu | ✔ | ✔ | ~ (boucle ; reste non établi) | ✔ | — |
| **Vidéo de début (first-play)**, « passer le menu au démarrage » | ✔ | — | n.é. | ✘ | BD-J : le xlet joue une playlist avant le menu ; DVD : PGC de départ |
| Texte libre : police, taille, gras, italique, souligné, contour, alignement, **position 0-1000**, « appliquer à tous », « cacher sur le disque » | ✔ | ✔ (police, couleur) | ✔ (« Add Texts ») | ~ (polices, couleurs et tailles du modèle, pas de placement libre) | cœur de l'ergonomie : phase 2 |
| Image libre (taille, position) ; boîtes, soulignement | ✔ | — | ~ (« Add Widgets », sens non établi) | ✘ | idem |
| Style de surbrillance (cadre / soulignement), style des boutons de navigation, vignette de bouton | ✔ | ✔ | ✔ | ~ (5 styles) | — |
| Sons de boutons | — | — | ✔ (`AuthorData.xml`) | ✘ | `sound.bdmv` ; plus tard |

**Volontairement exclu** : la détection et la suppression du filigrane Cinavia (Leawo : 3 écrans ; DVDFab : « remove the Cinavia watermark from this audio track »), AACS / BD+ / copie de disques protégés, tout contournement de protection [chaînes]. Hors périmètre de la création.

## 3. Comment leurs menus et leurs modèles fonctionnent

- **DVDFab** : écran « BDAuthor » avec *Add Texts*, *Add Widgets*, *Customize Background Art*, *Customize Set*, *Customize Thumbnail*, *Save My Menu*, *Download More* [chaînes] ; modèles `template.bd.xml` + images ; menu en BD-J signé piloté par `AuthorData.xml`, boutons à sons, poignée de main avec le registre GPR100 [octets, REA : `COMPARAISON-DVDFAB-BLURAY.md` 2.2-2.3].
- **Yuhan** : un seul dialogue de menu (≈ 78 libellés) qui règle les menus Top / Scenes / Setup / Audio / Subtitle / Bonus / Pop-up, avec des modes *Basic* et *Advanced* ; les coordonnées des textes et images sont **relatives, de 0 à 1000** ; menu HDMV dessiné par Qt (classes `CBDMenu`, `CBDButton`), clips de menu `menu_N.m2ts`, 30 thèmes × 4 résolutions + `config.dat` chiffré [chaînes, REA, octets]. **Où et comment le flux graphique du menu est écrit n'est pas établi** (`BlurayCreator.dll` n'expose que les classes ; son muxeur ne traite que le PGS).
- **Leawo** : modèles `template.xml` en 1920×1080, éditeur à fond (image ou vidéo, musique, boucle, fondus, durée), police et couleur « appliquer à tous », liste de chapitres par titre, enregistrement dans *Mon BD / Mon DVD / Mon UHD* ; `BDFix.dll` porte le modèle de menu HDMV et **écrit lui-même le flux IG** dans un clip dédié (section 4c) [chaînes, REA].
- **Ergonomie commune à reprendre** : flux en trois temps (médias → menu → sortie), aperçu dans le dialogue, « appliquer à tous », positions relatives, projet enregistrable, « ISO d'essai », action de fin.

## 4. Un menu HDMV réel, décodé (référence d'amateur, non prouvée sur matériel)

Disque de référence de l'utilisateur (édition d'amateur), `STREAM\01001.m2ts`, flux graphique PID `0x1400` (type `0x91`), vidéo `0x1011` (type `0x1b`) ; décodé avec un analyseur de travail (`igdump.py`, dans le dossier temporaire de session) [testé]. Que ce disque se lise sur un lecteur de salon n'est pas établi ici ; si l'utilisateur l'a déjà lu sur le sien, c'est la seule preuve matérielle disponible.

| Élément | Valeur relevée |
|---|---|
| Image | 1920×1080 ; `01001.clpi` déclare vidéo H.264, format 4 (1080i) et cadence 4 (29,97) [octets] ; l'ICS porte le même code de cadence 4 |
| ICS | début d'époque (`composition_state` = 2), `stream_model` 0 (multiplexé), `ui_model` 0 (toujours affiché), `user_timeout` 0, 1 page, 7 groupes de boutons d'un bouton chacun, bouton par défaut `0xFFFF` |
| Masque d'opérations interdites | `3cb805ff40000000` [octets] |
| Palette | 1 seule, 255 entrées |
| Objets | 21 : 3 par bouton (normal, sélectionné, activé), hauteur 92 px, largeurs 204 à 901 px, RLE |
| Boutons | 7 : deux colonnes (x = 260 et x = 1315, y entre 326 et 659) + un bouton centré en bas (855, 912) ; voisins haut / bas / gauche / droite renseignés |
| Calendrier | PTS de l'ICS 1,506 s, après ceux de la palette (1,247 s) et des objets (1,257-1,359 s) |
| Programme de bouton | 21 à 80 instructions de 12 octets (80 pour les trois boutons de gauche, 72 pour trois autres, 21 pour le bouton du bas). Les sept finissent par **`JUMP_OBJECT 1`** vers un répartiteur de `MovieObject.bdmv` [testé : dernière instruction de chaque bouton]. Les boutons 0 à 4 (les seuls relus en détail) mémorisent le bouton (R15), manipulent R12 / R13 / R16 et parcourent une table de neuf cas qui charge R8 (1003 à 1011) et R10 (4, 5 ou 6) ; le bouton du bas, plus court, n'a pas été relu |

À retenir : un éditeur de menu HDMV doit produire palette ≤ 255 couleurs + objets RLE ×3 par bouton + voisins + programme de bouton + répartiteur. C'est faisable sans magie ; ce qui reste à prouver, c'est la **lecture sur lecteur de salon** de notre propre sortie (et de ce disque d'amateur lui-même).

## 4b. bdmvkit essayé ici (2026-11-04)

Protocole : clone (version 0.7.0) → `bdmvkit encode` (une vidéo de 8 s, 1080p24 → `feat.m2ts`) → `bdmvkit build disc.json out` avec un menu de 3 boutons × 3 états (images unies) → décodage avec mes analyseurs → lecture dans le VLC portable du projet (libbluray, journal complet) avec les touches « bas » puis « valider ». Le script d'essai est à part du clone (`python -I`). Tout est dans le dossier de session [testé].

**Ce que libbluray a fait** (journal `libbluray.log`) : premier lancement → `JUMP_TITLE 2` (le menu est un titre) → `PLAY_PL 2` → flux IG trouvé (PID `0x1400`) → bouton 0 rendu « sélectionné » → touche bas : PSR10 passe de 0 à 1, bouton 0 « normal », bouton 1 « sélectionné » → valider : bouton 1 « activé » → `JUMP_OBJECT 4` → `PLAY_PL_PM` du film à partir du repère de chapitre 1 → fin du film → `JUMP_TITLE 2` → retour au menu. Aucune erreur hors fichiers facultatifs absents (`META\DL`, `AACS`).

**Comparaison avec le menu de référence (section 4)** :

| | bdmvkit (essai) | Référence d'amateur |
|---|---|---|
| Palette | 4 entrées (images unies ; le quantificateur monte à 255) | 255 |
| Programme d'un bouton | **1 instruction** (`JUMP_TITLE n` ou `JUMP_OBJECT 4`) | 21 à 80, répartiteur par registres |
| Navigation du disque | un titre par menu, `index.bdmv` : premier lancement = objet 2, menu = objet 3, titre 1 = objet 0, menu = titre 2 = objet 1 | `JUMP_OBJECT 1` vers un répartiteur |
| Masque d'opérations interdites | 0 | `3cb805ff40000000` |
| Horodatage des segments IG | PTS seul, ICS à 1,400 s, objets 11 µs plus loin, **pas de DTS** | ICS PTS 1,506 s / DTS 1,247 s, objets avec DTS |
| Bouton par défaut | 0 | `0xFFFF` |

Ces différences d'horodatage et de masque sont ce que je regarderais d'abord si un lecteur de salon refusait la sortie de bdmvkit [déduit] ; aucun des deux disques n'est prouvé sur matériel.

**Essai tsMuxeR** : le flux IG de bdmvkit (3 953 octets, au format `.sup`) donné à tsMuxeR 2.7.0 comme piste `S_HDMV/PGS` avec une vidéo de 6 s : réussite annoncée (« Mux successful complete »), PID `0x1200` de type `0x90` déclaré dans la PMT et le CLPI, mais **zéro paquet TS** sur ce PID. tsMuxeR ne convient donc pas pour muxer un menu ; ffmpeg le fait (bdmvkit le montre) à condition de corriger ensuite le type de flux.

**Disque d'essai pour la phase 0** : une image UDF 2.50 de 15 Mo, `bdmvkit-run\bdmvkit-test.iso` dans le dossier de session, faite par `BurnSmith.Cli iso` à partir du dossier généré ; menu de trois boutons de couleur unie, sans texte, pour voir ce qu'un lecteur en fait.

## 4c. Leawo : comment `BDFix.dll` écrit un menu HDMV (décompilé le 2026-11-04)

Source : `C:\Program Files\Leawo\Prof. Media\BDFix.dll` (x64, sha256 `217823fc…6e23`, 488 fonctions), ouvert avec REA/Ghidra 12.1.4, analyse statique seule. Le bilan collé par l'utilisateur (sandbox d'une autre IA, dossier `.unsloth\studio\sandbox\__LOCALID_0qGwmBF\reanalysis`) ne contenait que des **vues d'ensemble** (nombre de fonctions et de chaînes) de 27 modules ; je les ai recoupées avec les fichiers (les 27 chiffres sont exacts), mais elles ne disent rien du menu. Ce qui suit vient de la décompilation de `BDFix.dll`. Aucun disque Leawo n'a été produit ni décodé : ce sont des constats de **code**, pas d'un clip observé.

**Deux exemplaires de Leawo sur la machine** [octets] : l'installé (`C:\Program Files\Leawo\Prof. Media\`, `BDFix.dll` v1.2.0.0, 143 336 octets, sha256 `217823fc…`) et un portable d'un site tiers (`C:\Users\Marc\Documents\BD\Leawo Prof. Media 7.8.0 Portable_TrucNet.com\…\App\LeawoProfMedia\`, `BDFix.dll` v1.0.0.9, 181 760 octets, sha256 `e5a91228…`, date de compilation 2016). Tous les fichiers comparés diffèrent (`panda.dll`, `BDFix.dll`, `DVDMaker.dll`, l'exécutable) ; `EncStr.dll`, `RealEncodeApp.dll` et `ParseVideolib.dll` n'existent que dans le portable. Les deux `BDFix.dll` contiennent les mêmes marqueurs de menu (`CBDMenu`, `createBDMovie`, le chemin `index.bdmv`, le nom du fabricant) : la conception est ancienne et stable, mais **seule la version installée a été décompilée**. L'autre IA a analysé `panda.dll` du portable (sha256 `7226c747…`) ; l'exemplaire sur lequel ma propre décompilation de `panda.dll` portait n'est pas consigné (journal de preuves perdu), à ne pas supposer identique.

**Qui fait quoi** [REA] : `CBDMixInfo::createBDMovie` orchestre. Les films passent par `make_m2ts` (la classe tsMuxeR de `MakeM2ts.dll`, avec des fichiers `.meta` dans `temp\`) et `srt2sup` (sous-titres SRT → SUP). Le **menu**, lui, est écrit par `BDFix.dll` lui-même, en C++, sans tsMuxeR ni ffmpeg : `FUN_18000a000` produit un `.m2ts` qui ne contient que le flux graphique. Les noms de classes (`CBDMenu`, `CBDButton`, `CBDMixInfo`, `BD_BUTTON_HEADER`, `BD_COMMAND`) sont lisibles ; chaque bouton porte une image « sélectionné », une image « activé », une liste de commandes et son en-tête.

**Le clip de menu** [REA + octets du modèle de paquets, lus dans la DLL] :
- quatre paquets d'en-tête fixes (192 octets : 4 d'horodatage d'arrivée + 188 de TS) : PAT (programme 1 → PID `0x0100`), PMT avec **un seul flux : type `0x91` (IG), PID `0x1400`**, PCR sur le PID `0x1001`, table SIT sur le PID `0x001F` ;
- puis les segments IG découpés en paquets sur le PID `0x1400`, puis des paquets nuls jusqu'à un multiple de 6 144 octets (32 paquets source) ;
- un CLPI minimal (`FUN_180008600`) : un flux, PID `0x1400`, codage `0x91`, langue « eng », `application_type` = 5 (que je lis comme « sous-flux de menu interactif », valeur à confirmer dans la norme).

**Le flux IG** [REA ; la répartition des horodatages est un calcul de ma part sur les octets de modèle] :
- ICS : état de composition = début d'époque, `stream_model` = 1 (**hors multiplexage**, donc sans les deux horodatages d'échéance du cas multiplexé), `ui_model` = 0 (toujours affiché), une page, **masque d'opérations interdites = 0**, bouton sélectionné par défaut = 0, bouton activé par défaut = `0xFFFF`, palette 0 ;
- **un groupe de boutons par bouton** (chaque BOG ne contient qu'un bouton) ; en-tête de bouton de 35 octets suivi de ses commandes de 12 octets ;
- une palette unique de 255 entrées (PDS), quantifiée à partir de toutes les images de boutons ; un ODS par image (sélectionné, activé) en RLE ; segment de fin ;
- les PES portent **PTS et DTS** (premier ICS : PTS ≈ 11,65 s, DTS ≈ 11,44 s, puis +3 574 ticks à 90 kHz par segment suivant) : comme le disque de référence de la section 4 (PTS 1,506 s, DTS 1,247 s), et à l'inverse de bdmvkit qui n'écrit qu'un PTS.

**Deux listes, pas une** [REA, corrigé le 2026-11-05 ; la lecture du 2026-11-04 (« un IG par titre ») était fausse] : `CBDMixInfo` tient `this+0x10` = les **menus** (`CBDMenu`, créés par `createMenu`, chacun avec un fichier vidéo de fond et des boutons) et `this+0x18` = les **films** (`addVideo`, chacun avec son vecteur de chapitres). C'est `DVDMaker.dll` (`FUN_180015760`, qui journalise « generateBDParam1() called ») qui remplit l'objet : `addVideo` pour chaque film, puis, sauf si `m_menuDisabled`, `createMenu` pour l'accueil et pour chaque page de chapitres. Le test `local_3cc = (nombre de menus == 0)` de `createBDMovie` confirme le sens des deux listes. Numérotation, pour N menus et M films :
- menu *i* : clip de fond `2i`, clip IG `2i+1`, playlist `0000i.mpls` ;
- film *k* : clip `2N+k` (`k` s'il n'y a aucun menu) ; soit **une seule** playlist `N.mpls` dont les M films sont les éléments de lecture, soit une playlist `N+k.mpls` par film, selon un indicateur de l'objet (`+0x45`, non nommé) ; avec l'autre indicateur (`+0x44`, non nommé), les sous-titres SRT d'un film sont convertis en SUP avant le muxage.

**Navigation** [REA] : `index.bdmv` (`INDX0200`, fabricant « LEAWO SOFTWARE CO.,LTD. ») : premier lancement → objet 0, menu principal → objet 1 (type de lecture « interactif »), titre *n* → objet *n*+1 (type de lecture « film »), tous HDMV ; `MovieObject.bdmv` (`MOBJ0200`, M+2 objets), avec la numérotation de libbluray (`JUMP_TITLE` = 1, `CALL_TITLE` = 3 dans le champ d'option de branchement ; `PLAY_PL` = 0, `PLAY_PL_PI` = 1, `PLAY_PL_PM` = 2 ; l'opérande de `JUMP_TITLE` / `CALL_TITLE` est écrit en « valeur + 1 », d'où `0xFFFF` → titre 0 = menu principal) :
- objet 0 : `MOVE R101, 0` ; `JUMP_TITLE 0` ;
- objet 1 (menu principal) : `MOVE R100, R101` ; `PLAY_PL R100` ; `GOTO 1`. **R101 est le numéro du menu à afficher** : la playlist *R101* est la playlist du menu *R101* (clip de fond `2R101` + IG `2R101+1`), qui tourne en boucle grâce au `GOTO` ; au départ R101 = 0, donc l'accueil ;
- objet de titre *k* : une seule playlist : `PLAY_PL_PI N, k` ; une playlist par film : `PLAY_PL N+k` ; puis dans les deux cas `JUMP_TITLE 0`, qui ramène au menu principal et donc, par R101, à la page de menu d'où l'on venait. Avec une playlist unique, `PLAY_PL_PI` continue après la fin de l'élément *k* jusqu'à la fin de la playlist : c'est une lecture « à partir du titre *k* » [déduit de la sémantique HDMV, non joué] ;
- **sans menu** : objet 0 `JUMP_TITLE 0`, objet 1 `JUMP_TITLE 1`, objet de titre *k* `PLAY_PL_PM 0, repère k` puis `JUMP_TITLE 0` : tout renvoie au titre 1, donc le disque tourne en boucle [lu dans le code, non joué].

**Boutons** [REA, `DVDMaker.dll` `FUN_180015760`] : l'application construit chaque `CBDButton` (images « sélectionné » et « activé » recadrées dans les images du modèle, `buttonHeader`, `addCommand`) ; `BDFix.dll` n'a **aucun appelant** de `addCommand` : il n'écrit que ce qu'on lui donne (12 octets par commande, copiés tels quels). Dans l'en-tête de bouton, l'état normal est **vide** (objets `0xFFFF`), l'état sélectionné utilise l'objet `2j` et l'état activé l'objet `2j+1` : l'aspect « repos » des boutons est donc dans la vidéo de fond, le flux IG ne porte que les surcouches. Un seul modèle de menu :
- **accueil** (menu 0), deux boutons : le premier `MOVE R101, 0` ; `CALL_TITLE 1` (lecture) ; le second `MOVE R101, 1` ; `CALL_TITLE 0` (vers la liste de chapitres) ;
- **pages de chapitres** (menus 1 à P, un fond vidéo par page), au plus 6 vignettes par page (la table de voisins, codée en dur, est celle d'une grille de 3 colonnes : voisin de droite / gauche = ±1, du bas / haut = ±3, et ne prévoit que six cases) plus des boutons de navigation : chaque vignette fait `MOVE R101, p+1` (retenir la page) puis `CALL_TITLE (vignettes_par_page × p + j + 1)` ; une page : un bouton « accueil » (`MOVE R101, 0` ; `JUMP_TITLE 0`) ; plusieurs pages : accueil, **page précédente** (`MOVE R101, (p == 0 ? P : p)` ; `JUMP_TITLE 0`) et **page suivante** (`MOVE R101, (p+2 > P ? 1 : p+2)` ; `JUMP_TITLE 0`), avec retour circulaire. Le rôle « précédent » / « suivant » est lu sur les valeurs, pas sur les images ;
- les « chapitres » de ces pages sont en réalité les **films** de la liste (le nombre d'éléments de la dernière page est `nombre de films % vignettes par page`). Pas de menu audio ni sous-titres, pas de menu contextuel.

**Playlists** [REA ; `FUN_18000b410`, `FUN_18000b860` et `FUN_18000bf00` lus en entier les 2026-11-05 ; octets décodés à la main, longueurs de champs recoupées] : trois écrivains, tous `MPLS0200`, en-tête de 58 octets (adresse de départ de la playlist `0x3A`) :
- **`FUN_18000b410` : la playlist d'un menu** (une par menu, `0000i.mpls`, appelée une fois par `CBDMenu`) : **1 élément de lecture** = le clip de fond `2i` (« M2TS », entrée et sortie du clip, ticks de 45 kHz = ms × 45), puis le tableau des flux du clip ; l'entrée de flux IG de ce tableau a `stream_entry_type` = 2 (**sous-chemin** 0, entrée de sous-clip 0), PID `0x1400`, codage `0x91` ; **`number_of_SubPaths` = 1**, de type 3 (que je lis comme « présentation de menu à graphiques interactifs » : valeur à confirmer dans la norme), avec **1 élément de sous-lecture = le clip IG `2i+1`**, mêmes entrée et sortie que l'élément principal, synchronisé sur l'élément 0 à l'instant 0 ; un repère de chapitre au début ;
- **`FUN_18000b860` : la playlist unique des films** : un élément de lecture par film (clip `2N+k`, entrée / sortie fournies par `FUN_18000e970` d'après le fichier produit : fonction non lue), `number_of_SubPaths` = **0** ; le tableau des flux est celui du clip sondé (le type de chaque flux est déduit de son codage : `FUN_180002680` range `0x01 / 0x02 / 0x1b / 0x24 / 0xea` en vidéo, les codages audio en audio, `0x90` en PG, `0x91` en IG, `0x92` en sous-titres texte) ; les repères de chapitre de chaque film sont écrits en repères de type 1 (entrée) rattachés à l'élément du film, à (chapitre + entrée du clip) × 45, PID `0xFFFF`, durée 0 ;
- **`FUN_18000bf00` : une playlist par film** : même contenu mais un seul élément de lecture et aucun sous-chemin (`number_of_SubPaths` = 0) ; le repère des chapitres se rattache à l'élément 0.
- Ensuite `createBDMovie` peut rouvrir le dernier fichier de playlist écrit et passer la version `0200` en `0300` (indicateur `+0x44`, non nommé ; le même motif d'ouverture de fichier existe pour `index.bdmv` et `MovieObject.bdmv`, contenu non relu).

Ce que cela établit : **le menu de Leawo est rattaché à la playlist du menu lui-même** (un IG hors multiplexage dans un second clip, `2i+1`, avec un fond `2i`), et non à celle des films ; les films n'ont ni IG ni sous-chemin. Il y a donc un clip IG **par page de menu**, numéros impairs (`FUN_18000a000` l'écrit avec 1, 3, 5…), et les clips des films sont numérotés après tous les menus (`2N + k`). L'objet 1 du `MovieObject.bdmv` lance la playlist du menu R101, ce qui répond à la question restée ouverte le 2026-11-05 matin : **l'IG par menu est bien le menu principal et ses pages**. Ce n'est qu'un constat de code : aucun disque Leawo n'a été produit ni lu sur un lecteur. La copie portable (BDFix 1.0.0.9, 32 bits, 2016) contient le même écrivain de menu (`FUN_10014390`, mêmes constantes : un sous-chemin de type 3, un clip `2i+1`) ; elle n'a que deux écrivains de playlist contre trois dans la copie installée (recherche de l'octet `MPLS`) : sa propre structure de films n'a pas été lue, ni `DVDMaker.dll` du portable (178 176 octets, 2017 : il importe aussi `CBDButton::addCommand`).

**Ce que cela change pour BurnSmith** [déduit] :
- une **route plus courte que celle de bdmvkit** existe pour la phase 3b : écrire soi-même le `.m2ts` du menu (PAT/PMT/SIT/PCR de modèle + PES), sans ffmpeg ni correction du type de flux dans la PMT. Les quatre paquets de modèle sont récupérables par construction (ce sont des tables MPEG standard), pas par copie ;
- le modèle de navigation de Leawo (objet 0 → menu → un objet par titre ; le registre R101 retient la page de menu, donc on revient à la même page après un film) est plus simple que le répartiteur de registres du disque de référence ;
- **réserve** : ni l'IG hors multiplexage ni ce mode d'écriture n'ont été rejoués dans libbluray ou sur un lecteur ; bdmvkit (multiplexé, éprouvé dans libbluray) reste la route à tester d'abord. Le choix hors multiplexage ajoute le sous-chemin de la playlist et un second clip à gérer (c'est ce que fait Leawo : sous-chemin de type 3 et clip IG impair, voir plus haut).

## 4d. Yuhan : ce que `BlurayCreator.dll` écrit (décompilé le 2026-11-05)

Source : `C:\Program Files\Yuhan Blu-ray DVD Creator\BlurayCreator.dll` (x64, 265 216 octets, sha256 `d2332624…22fb`). Lu avec REA/Ghidra 12.1.4, puis **avec Ghidra en ligne de commande** : le pont REA a cessé de répondre en cours de route (poignée de main refusée à chaque ouverture, même après l'arrêt d'un Ghidra orphelin d'hier), j'ai donc lancé `analyzeHeadless` avec un petit script qui décompile des adresses données (script et sortie `yuhan_ig.txt` dans le dossier de travail de la session, non versionnés). Analyse statique seule : aucun disque Yuhan n'a été produit ni lu.

**Correction** [REA] : une première lecture de cette DLL n'y avait trouvé aucun écrivain de flux IG. Il existe : `FUN_18002e990` (pages → clip IG), `FUN_18002e900` (un seul menu → même fonction), `FUN_18002b580` (CLPI du clip). Je ne l'avais pas trouvé parce que le texte « IG » n'y figure pas ; c'est l'orchestrateur `FUN_18000b360` qui les appelle, juste avant l'écriture de `index.bdmv` (`FUN_180006260`) et de `MovieObject.bdmv` (`FUN_180006c90`).

**Parenté avec Leawo** [REA] : même lignée de code. Mêmes noms de classes (`CBDMenu`, `CBDButton`, uniquement dans cette DLL : ni l'exécutable ni les autres modules ne les contiennent), même numérotation des clips IG (1, 3, 5… pour les menus à un IG par page), mêmes quatre paquets de modèle (PAT → PMT `0x100`, PMT avec un seul flux de type `0x91` sur le PID `0x1400` et PCR sur `0x1001`, SIT sur `0x001F`, paquet de PCR : octets lus dans la DLL), même pas d'horodatage (+3 574 ticks à 90 kHz par segment), même remplissage jusqu'à un multiple de 6 144 octets, même CLPI minimal (`application_type` 5, flux `0x91` PID `0x1400`, langue « eng », débit 6 000 000 octets/s, aucune table d'entrées CPI), mêmes numéros de registre pour le menu (R100, R101). Seule la version change : `0200`, ou `0300` si l'indicateur `+0xe` de l'objet est levé.

**Ce que Yuhan ajoute au flux IG** [REA, `FUN_18002d790` lu en entier ; offsets recoupés avec la structure d'un ICS] :
- **plusieurs pages dans un même ICS** (une `page` par élément de la liste donnée, `page_id` = rang) ; en-tête de page de 21 octets, **aucun effet d'entrée ni de sortie**, bouton sélectionné par défaut = celui que la page désigne (`0xFFFF` s'il est hors limites), bouton activé par défaut = `0xFFFF`, palette 0 ; toujours **un groupe de boutons par bouton** ;
- **trois états par bouton** : normal (+0x10 de l'objet bouton), sélectionné (+0x28), activé (+0x40) ; un état dont l'image est vide devient `0xFFFF`. Si la première image « normal » est vide, tout le menu fonctionne en deux états (comme Leawo) ; sinon en trois ;
- **les identifiants d'objets sont globaux** : pour chaque page, les six références d'objet de chaque bouton (début / fin des états normal, sélectionné, activé) sont décalées du nombre d'objets des pages précédentes ; les voisins (haut, bas, gauche, droite) ne le sont pas. Les commandes du bouton (12 octets) sont copiées à la suite de son en-tête de 35 octets ;
- `stream_model` = 1 (hors multiplexage) **pour tous les clips** ; `user_interface_model` = 0 (menu toujours affiché) pour les menus ordinaires et **1 (menu contextuel) pour deux listes de pages écrites à part, clips n° 90001 et 90002** (même fonction, indicateur à 1). Je lis ces deux listes comme « menu contextuel du film » et « menu contextuel des bonus » [déduit : le guide de Yuhan annonce un menu contextuel avec un bouton « Principal » ; la sélection dépend d'un indicateur non nommé `+0x1f` et de la présence des listes, et la taille vidéo est lue dans le premier flux vidéo du titre, plafonnée à 1920×1080] ;
- une palette unique de 255 entrées (indices 0 à 254, 5 octets chacune) construite sur toutes les images (`FUN_18002c450`), un ODS par image (`FUN_18002cec0` puis `FUN_18002dfe0`), segment de fin. Non lus : le quantificateur et le codeur RLE.

**Tous les écrivains de commandes** [REA, 14 petits constructeurs `FUN_18002f990` à `FUN_18002ff80`, 12 octets chacun ; numérotation de libbluray ; pas confrontés à un décodeur] :

| Constructeur | Commande | Opérandes |
|---|---|---|
| `f990` | `BREAK` | — |
| `f9b0` | `GOTO` | étiquette (immédiat ou registre) |
| `fa00` | `JUMP_TITLE` | titre, écrit en valeur + 1 (`0xFFFF` → 0 = menu) |
| `fa60` | `PLAY_PL_PI` | liste de lecture, élément |
| `fae0` | `PLAY_PL` | liste de lecture |
| `fb30` | `PLAY_PL_PM` | liste de lecture, repère |
| `fbb0` / `fc70` | `CMP` égal / différent (si faux, la commande suivante est sautée) | registre ou PSR (drapeau), immédiat ou registre |
| `fdb0` | `SET_BUTTON_PAGE` | deux registres, drapeau de validité 0x80 en tête de chaque opérande |
| `fe10` / `fed0` / `fe60` | `SET_STREAM` | piste audio / sous-titre / les deux, tirées d'un registre ou immédiates (drapeaux `0x80` audio, `0xC0` sous-titre affiché) |
| `ff80` / `ff20` | `MOVE` / `BITSET` | registre, immédiat ou registre |

**`MovieObject.bdmv`** [REA, `FUN_180006c90` lu en entier]. Registres, tels que je les déduis des usages : R100 liste de lecture à lancer ; R101 menu courant ; **R102 piste audio choisie** ; **R103 sous-titre choisi (0 = aucun)** ; R104 page courante ; R105 dernier bouton sélectionné ; R106 / R107 temporaires ; R108 « déjà initialisé ». Objets (pour un disque avec menu, N objets de titre, un de plus pour « tout lire ») :
- **objet 0** (premier lancement) : `MOVE R101, 0` ; `MOVE R102, audio par défaut` ; `MOVE R103, sous-titre par défaut` ; si une vidéo de début est demandée, `PLAY_PL 0x400` (liste de lecture n° 1024 : lien avec un fichier `01024.mpls` non vérifié) ; `JUMP_TITLE` vers le menu, ou directement vers le titre 1 si le réglage « ne pas afficher le menu au démarrage » est levé ;
- **objet 1, variante « un IG par page »** (celle de Leawo, plus la mémoire du bouton) : `MOVE R100, R101` ; `MOVE R106, 0` ; `BITSET R106, 31` ; `MOVE R107, R105` ; `BITSET R107, 31` ; `SET_BUTTON_PAGE R106, R107` ; `PLAY_PL R100` ; `GOTO 1`. On revient donc au menu avec le dernier bouton choisi déjà sélectionné ;
- **objet 1, variante « un seul IG à plusieurs pages »** (liste de pages non vide et mode 1) : 13 commandes. `MOVE R100, 0` (la liste de lecture 0 porte tout) ; `CMP R108 ≠ 0` ; `GOTO 6` (saute l'initialisation) ; `MOVE R104, 0` ; `MOVE R105, 0` ; `MOVE R108, 1` ; puis, à partir de la commande 6, `MOVE R106, R104` ; `BITSET R106, 31` ; `MOVE R107, R105` ; `BITSET R107, 31` ; `SET_BUTTON_PAGE R106, R107` ; `PLAY_PL R100` ; `GOTO 6`. **Les pages sont des pages de l'IG, choisies par R104** ; les boutons « page suivante / précédente » n'ont donc pas besoin de relancer une liste de lecture (leurs commandes ne sont pas dans cette DLL) ;
- **objets de titre** (un par film, ou un par chapitre selon un mode `+0xa4` valant 2 ou 3, puis un par bonus) : **7 commandes** : `SET_STREAM audio ← R102` ; `CMP R103 ≠ 0` ; `SET_STREAM sous-titre ← R103` (affiché) ; `CMP R103 = 0` ; `SET_STREAM sous-titre 1, masqué` [déduit des drapeaux ; octets `51 C0 00 01  00 00 80 01  00 00 00 00`] ; `PLAY_PL_PI` (film : liste de lecture des menus + k, élément 0 ; liste unique : liste M, élément k) ou `PLAY_PL_PM` (chapitre : liste, repère) ; `JUMP_TITLE 0`. **C'est le mécanisme de choix de langue audio / sous-titre : le menu (réglages) écrit R102 / R103, chaque titre les applique avant de jouer** ;
- **dernier objet, « tout lire »** : les cinq commandes de préparation de langue, puis un `PLAY_PL_PI` par film à la suite (une liste de lecture par film) ou un seul `PLAY_PL` (liste unique), puis `JUMP_TITLE 0` ;
- **sans menu** : objets 0 et 1 = `JUMP_TITLE 0` et `JUMP_TITLE 1` (ou, avec vidéo de début, objet 0 = `PLAY_PL 0x400` ; `JUMP_TITLE 0`). Avec **une liste de lecture par film** : **un seul** objet de plus (trois en tout) : `SET_STREAM` audio + sous-titre en immédiat, puis un `PLAY_PL_PM n, 0` par liste à la suite, puis `BREAK`, ou `JUMP_TITLE 0` si « lecture en boucle » (`+0x1e`). Avec une **liste unique** : un objet par film : le même `SET_STREAM`, `PLAY_PL_PM 0, k`, puis la même fin.

**Ce que cela change pour BurnSmith** [déduit] :
- **le menu de Yuhan est un cas déjà couvert par la route B** : deux produits d'éditeurs différents (même lignée) écrivent le même clip IG minimal, hors multiplexage, avec la même PMT à un flux. Cela renforce l'idée d'écrire notre propre `.m2ts` de menu, mais ne prouve rien sur lecteur : ni Leawo ni Yuhan n'ont été lus sur un lecteur ici ;
- **choix de la langue** (phase 3d) : copier le motif des sept commandes (R102 / R103 appliqués par `SET_STREAM` avant chaque lecture) est plus simple que de multiplier les titres ; un bouton de réglages n'a qu'à faire `MOVE R102, n` ou `MOVE R103, n` ;
- **mémoire du bouton** (R105 + `SET_BUTTON_PAGE`) et **IG à plusieurs pages** (R104) : à envisager pour limiter le nombre de clips IG et de listes de lecture ; mais le premier coûte un IG plus gros et un lecteur qui gère bien les pages multiples (non essayé) ;
- le **menu contextuel** (pop-up) de Yuhan est un second clip IG avec `ui_model` = 1 ; **comment il est rattaché aux listes de lecture des films n'est pas lu** (écrivain `FUN_180006720` et compagnons non ouverts).

**Reste non lu pour Yuhan** : `FUN_180006720` (écrivain de MPLS) et `FUN_180008c50` / `FUN_1800099e0` / `FUN_1800048e0` / `FUN_180005880` (listes de lecture), le quantificateur de palette (`FUN_18002c450`), le codeur d'image (`FUN_18002cec0`), l'écrivain d'ODS (`FUN_18002dfe0`), la classe concrète qui implémente l'interface virtuelle (menus à `+0x78`, titres à `+0x88`, liste de pages à `+0x80`) et, plus généralement, tout `BlurayDVDCreator.exe` : l'analyse Ghidra de cet exécutable (1,7 Mo) a abouti mais le pont REA a refusé la connexion, donc **aucun code de l'exécutable n'a été lu**. Un balayage de chaînes sur tous les modules ne trouve `CBDButton` / `CBDMenu` que dans `BlurayCreator.dll`.

## 5. Briques libres, licences, décision

| Brique | Licence | Rôle | Maturité | Décision |
|---|---|---|---|---|
| MultiAVCHD | fermé (freeware) | — | — | **inutilisable** (code ni binaire) |
| bdmvauthor (`blenheimears`) | Apache-2.0 | menus HDMV natifs, projet JSON `.bdmvproject` v29, CLI | expérimental, non testé sur lecteur ; **cloné, non compilé** (MSYS2 + Qt 6 requis) | référence de conception ; option A en sous-processus, peu séduisante |
| bdmvkit (`kajisho5`) | LGPL-3.0-ou-plus | écriture de menus IG + navigation HDMV, pop-up, menus animés, en Python (2 539 lignes, aucune dépendance, ffmpeg pour l'encodage) | récent, non testé sur matériel ; **un disque à menu produit ici et joué en boucle par libbluray** | **base de portage en C#** (4b) |
| udfkit (`kajisho5`) | Apache-2.0 | bibliothèque UDF du même auteur que bdmvkit (contenu non relu en détail) | — | peu utile : IMAPI2 fait déjà l'ISO |
| HD Cookbook, `oliverlietz/bd-j` | BSD-3 (selon dépôt) | outils BD-J : signature, permissions | ancien | si on garde le BD-J, pour le signer |
| tsMuxeR | Apache-2.0 | multiplexeur du film (aucune piste IG) | éprouvé | garder |
| libbluray / VLC | LGPL | lecteur de test (`tools\vlctest.ps1`) | éprouvé | garder comme juge ; **ne remplace pas un lecteur de salon** |
| ffmpeg, x264, x265 | GPL / LGPL | encodage, filtres, diaporama | éprouvé | déjà utilisés |
| dovi_tool, hdr10plus_tool | MIT | métadonnées HDR | éprouvé | plus tard (UHD) |
| DVD Forge, DVDStyler (modèles) | GPL | modèles de menu | — | déjà repris |
| BDedit, DVD-Logic, TMPGEnc, code de DVDFab / Yuhan / Leawo | fermé | — | — | **à ne pas utiliser** |

Compatibilité : Apache-2.0, LGPL-3.0-ou-plus et BSD-3 se combinent avec la GPL-3.0-ou-plus de BurnSmith (avis à ajouter à `THIRD-PARTY-NOTICES.md` si du code est repris).

**Architecture du menu HDMV, deux voies :**

- **A. bdmvauthor en sous-processus.** Rapide à brancher (BurnSmith écrit un `.bdmvproject`), mais ajoute une chaîne C++/Qt6 à embarquer, double le pipeline (playlists, clips, index) et dépend d'un projet récent dont la sortie n'a jamais été vue sur matériel.
- **B. Écrire le menu en C# dans BurnSmith** (recommandée). Elle garde le seul `BurnSmith.exe`, réutilise `MenuRenderer` et `MenuFiles` (qui dessinent déjà le fond, l'image de focus de chaque bouton `hl_<page>_<bouton>.png` et le marqueur ; **l'état « activé » n'existe pas encore** et peut partir de l'image de focus) et l'arithmétique de chapitres, et prend bdmvkit (à porter) et bdmvauthor (à lire) comme références. Morceaux à écrire, avec leur équivalent dans bdmvkit : quantificateur de palette ≤ 255 couleurs avec transparence (`igs.quantize` ; `IndexedPng.cs` n'écrit que des PNG à 2 bits pour le DVD, il ne convient pas) ; encodeur RLE et conversion RVB → YCbCr (`pgs.rle`, `pgs.ycbcr`) ; sérialiseur ICS / PDS / ODS / END (`igs.ig_stream`, ≈ 110 lignes) ; clip de menu : **ffmpeg mux vidéo + audio + le flux IG donné comme un flux PGS**, puis correction du type de flux 0x90 → 0x91 dans chaque PMT avec recalcul du CRC (`igs.patch_ig_stream_type`, `m2ts.set_stream_types`) et écriture du CLPI / MPLS du clip (`m2ts.py`, `clip.py`, `playlist.py`) ; `MovieObject` et `index.bdmv` avec un titre HDMV réellement atteignable (`nav.py`, `program.py` ; `Bdmv.PatchIndex` écrase aujourd'hui toute la table des titres par un titre BD-J). Pour le film, tsMuxeR reste en place.

## 6. Feuille de route

| Phase | Contenu | Fini quand |
|---|---|---|
| **0. Preuve sur lecteur** (aucun code ; **demande un lecteur de salon et un disque inscriptible de l'utilisateur**) | Graver et lire : (1) un disque BurnSmith avec menu BD-J, (2) un disque `--no-menu` (HDMV pur), (3) si possible l'ISO d'amateur de référence (ou noter qu'il a déjà été lu). Noter ce qui s'affiche, ce que fait la touche « menu ». Ajouter l'ISO d'essai de bdmvkit (4b) : c'est le test qui dit si un menu HDMV écrit par un outil libre passe sur votre lecteur. Compiler bdmvauthor demande d'installer MSYS2 + Qt 6 (à autoriser). | une page de résultats par lecteur |
| **1. Ergonomie sans nouveau moteur** | Enregistrer / ouvrir un projet ; sous-titres externes (SRT, ASS → SRT, SUP) ; ISO d'essai 5 minutes ; action de fin ; vidéo de début ; disque de musique ; diaporama rendu en vidéo (22 transitions via `xfade`) ; « appliquer à tous » | chaque fonction a sa commande CLI et son essai sur un disque généré |
| **2. Éditeur libre** | Éléments texte / image / cadre placés par l'utilisateur (coordonnées 0-1000), style par bouton, page Bonus, scènes par fichier, sons de boutons ; le modèle (`MenuDesign`) porte les éléments, le BD-J et le DVD les lisent | `check-templates --matrix` couvre aussi les éléments libres |
| **3. Menu HDMV natif (option B)** | 3a quantificateur + RLE + ICS / PDS / ODS (port de `igs.py` / `pgs.py` de bdmvkit) ; 3b clip de menu (ffmpeg + correction du type de flux dans la PMT + CLPI / MPLS) ; 3c `MovieObject` / `index` avec titres HDMV ; 3d pop-up, menus audio et sous-titres (`SET_STREAM`) ; 3e réglage « compatibilité maximale (HDMV) » proposé par défaut tant que le BD-J n'est pas prouvé | sortie décodée identique en structure au disque de référence (section 4), lue dans libbluray **puis** sur lecteur réel |
| **4. Plus tard** | Encodeurs matériels avec garde de conformité, UHD x265 + HDR, Dolby Vision, fusion de BD | — |

Ordre conseillé : 0 en parallèle de 1 ; 2 avant 3 (le modèle de données doit connaître les éléments libres avant d'être rendu en IG) ; 3 seulement si la phase 0 montre que le BD-J non signé échoue ou si l'on veut le pop-up.

## 7. Limites

- Aucun produit n'a été exécuté ; les inventaires viennent de libellés et de la décompilation.
- Le décodeur `igdump.py` n'a été validé que sur **un** menu (7 boutons ; `01002.m2ts` donne le même résultat, donc une variante de langue du même menu, pas un second modèle) ; ce n'est pas un contrôleur de conformité, et il n'est pas versionné.
- Colonne « n.é. » = non établi par les libellés.
- Leawo : l'écrivain du flux IG est localisé et lu dans `BDFix.dll` (section 4c), mais aucun disque Leawo n'a été produit ; les trois écrivains de playlist, l'écrivain de `MovieObject.bdmv` / `index.bdmv` et les commandes des boutons (dans `DVDMaker.dll`) sont lus (section 4c) ; restent non lus : `FUN_18000e970` (lecture du clip produit), le contenu des fonctions de `make_m2ts` / tsMuxeR, la structure de films de la copie portable. Yuhan : `BlurayCreator.dll` est lu pour l'IG, les commandes et `MovieObject.bdmv` (section 4d) ; non lus : les écrivains de listes de lecture, le quantificateur et le codeur d'image, tout `BlurayDVDCreator.exe` (le pont REA a refusé la connexion sur cet exécutable ; seule une recherche de chaînes a été faite). Pour trancher : faire produire un disque par leurs versions d'essai puis décoder le clip de menu (demande d'accord : lancement de logiciels tiers).
- `DVDFab64.exe` : l'analyse Ghidra de l'autre IA a échoué par **délai dépassé** au démarrage (330 s, 93 Mo) ; sa conclusion « binaire protégé » est une supposition, non vérifiée ici.
- bdmvauthor : cloné, lu, **jamais compilé ni exécuté**. bdmvkit : un seul disque d'essai (trois boutons de couleur unie, une seule page, sans menu animé ni pop-up) ; ses propres tests (`python -m unittest discover -s tests -t .`) n'ont pas été lancés. libbluray valide la logique, pas la tolérance d'un lecteur de salon.
- Le rendu visuel du menu n'a pas été vérifié : les captures du VLC portable (`snap*.png`) ne montrent que la vidéo de fond, pas la surcouche graphique ; ce sont les traces de libbluray (état des boutons, registres, commandes) qui prouvent le fonctionnement.
- Estimations de taille des phases volontairement absentes : aucune mesure n'a été faite.
- La phase 0 est la seule qui lève le risque principal, et elle exige un lecteur de salon et un disque inscriptible.
- Les phases 1 à 3 touchent les fichiers que l'autre session modifie en ce moment (`MenuDesign`, `BluRay.cs`, `Dvd.cs`, `MenuFiles.cs`, l'application WPF). Rien n'a été implémenté ici ; décider où et quand les lancer évite les conflits.

## 8. Phase 2, première brique : les éléments libres du menu (2026-11-05)

Fait dans BurnSmith (phase 2 de la feuille de route, point « placement libre ») :

- `MenuElement` (texte / image / cadre) : rectangle en pixels du canevas 1920x1080, page visée (`all`, `root`, `scenes`, `bonus`, `settings`, `credits`), police, taille, couleurs, contour, bordure, opacité, rayon, alignement, proportions ; `Behind` met l'élément **sous** les boutons (panneau, cadre) au lieu de dessus.
- `MenuBuilder.WithElements` les ajoute à chaque page au moment de la construction ; un élément `Behind` est inséré juste après l'artwork du modèle (le premier `Image { Backdrop: true }`), donc au-dessus du fond et sous les boutons. Les pages fabriquées hors de `Build` (les pages de réglages par choix du DVD) passent par la même fonction, sinon les éléments y manqueraient.
- Comme le rendu est le même pour tous les lecteurs, les éléments sont **fondus dans l'image de page** : ils apparaissent sur un DVD (dvdauthor/spumux) et sur un Blu-ray (menu BD-J) sans code côté lecteur. C'est le choix qui évite de toucher le xlet Java et les flux IG pour cette brique.
- Éditeur : ajout (+ Texte / + Image / + Cadre), liste des éléments, curseurs position / taille / épaisseur de bordure / opacité, couleur, remplissage, bordure, alignement, gras / italique, case « Derrière les boutons », suppression, déplacement à la souris sur l'aperçu (le contour se déplace, l'aperçu se refait au relâchement).
- Ligne de commande : `--element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"`, clés `kind, page, rect, text, file, font, size, color, align, bold, italic, outline, outlinew, fill, opacity, radius, border, borderw, fit, behind`.
- Épreuves faites : `BurnSmith.Cli check-elements` (11 vérifications, `all ok`) ; rendu d'un modèle DVDFab (`Birthday1`) avec un texte sur la racine et un texte sur les seules pages de scènes (vérifié à l'image) ; **DVD réellement construit** (`dvdauthor` + `spumux`) dont le VOB de menu montre le texte et le panneau, le panneau restant derrière le bouton Lecture ; `check-templates` toujours à 144 sans problème.

Ce qui reste de la phase 2 : style par bouton, boutons interactifs supplémentaires (« ajouter un bouton » et non seulement des éléments décoratifs), page Bonus paramétrable (nombre d'éléments, mode d'affichage), scènes par fichier / par texte, sons de boutons. La note de décompilation de la même date est dans `ANALYSE-REA.md`.
## 9. Surbrillances DVD prises aux modèles (2026-11-05)

Suite du §1 : la lecture de `FUN_180015840` a donné la structure, et le modèle DVDFab DVD donne les images. Ce qui a été fait :

- `MenuButton` porte `DvdHighlightImage` / `DvdHighlightMask` ; `InstalledTemplate` les remplit depuis l'image de survol déjà connue (`Selected()` : `frame/0.dvd.normal.png` → `frame/0.dvd.sub.png`) et son jumeau `.mask.`, aux six endroits où un bouton reçoit un artwork. Un bouton-texte à marqueur en étoile n'a pas de `.sub.` : il garde le cadre plat.
- `MenuRenderer.Raster` rend l'artwork à la taille exacte du bouton, masque appliqué, en pixels BGRA (WPF, prémultipliés) ; appelé dans le `MenuRenderer.Sta` de `Dvd.cs`, puisque la boucle de construction n'est pas sur un thread STA.
- `IndexedPng.Canvas.Stamp` colle ce raster sur le canevas de page en un seul index opaque (alpha ≥ 128), et `IndexedPng.Average` déprémultiplie pour trouver la teinte moyenne de l'artwork — la seule couleur qu'une surbrillance DVD peut consacrer à une image.
- `Dvd.cs` : par bouton, artwork tamponné s'il existe, sinon le cadre à quatre couleurs d'avant (même palette : transparent, teinte, blanc, transparent). Le script spumux est inchangé (`<spu highlight select force>` + `<button>`), conforme à ce que Yuhan écrit.

**Épreuves** : `BurnSmith.Cli check-highlights` (7 vérifications, `all ok`) vérifie que le modèle nomme bien ses images, que le raster a la taille du bouton (120 × 80 demandés), que la page ne reçoit que la forme du modèle (107 pixels opaques, ni plus ni moins), que le PNG produit est indexé 2 bits à quatre couleurs avec `tRNS`, et qu'un design sans modèle n'a aucune image de survol. Un DVD construit avec `DVDFab DVD | 01movie` (`dvdauthor` + `spumux`) produit un VOB de menu dont `ffprobe` annonce la piste `dvd_subtitle` (index 3) : la surbrillance est bien dans le disque, pas seulement dans un fichier intermédiaire. `check-templates` reste à 144 sans problème.

**Limite à connaître** : la surbrillance ne peut pas être plus fidèle qu'**une teinte opaque + transparence par bouton**. Une page qui mélange un bouton avec artwork et des pastilles BurnSmith dessine les unes à plat et l'autre en silhouette teintée, avec une seule palette pour la page — c'est une contrainte du format DVD (quatre couleurs), pas un choix. Le rendu exact de DVDFab (souvent deux teintes) demanderait une page homogène.