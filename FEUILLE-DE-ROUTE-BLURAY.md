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

**Navigation** [REA] : `index.bdmv` (`INDX0200`, fabricant « LEAWO SOFTWARE CO.,LTD. ») : premier lancement → objet 0, menu principal → objet 1, titre *n* → objet *n*+1, tous en HDMV. `MovieObject.bdmv` (`MOBJ0200`), avec les numéros de la table de libbluray :
- objet 0 : `MOVE R101, 0` ; `JUMP_TITLE 0` (menu) ;
- objet 1 (menu) : `MOVE R100, R101` ; `PLAY_PL R100` ; `GOTO 1` (la playlist du menu tourne en boucle ; R101 vaut 0 au départ, donc la playlist 0) ;
- objets de titre : `PLAY_PL_PI` (playlist *P*+1, élément *i*) puis retour `JUMP_TITLE 0` ; variante « un titre par playlist » : `PLAY_PL`. Sans menu : les titres sont des repères d'une seule playlist (`PLAY_PL_PM` playlist 0, repère *i*) ;
- les **commandes des boutons** (`BD_COMMAND`) viennent de l'appelant (l'application Leawo) : non lues ici.

**Playlists** [REA, `FUN_18000b410` lu en entier le 2026-11-05 ; octets décodés à la main, longueurs de champs recoupées : en-tête 58 octets = adresse de départ de la playlist `0x3A`, longueur du sous-chemin `0x24` = 6 + 2 + 28] : cette fonction écrit **une playlist par titre** (elle est appelée une fois par film dans `createBDMovie`, fichier `0000i.mpls`), `MPLS0200` :
- **1 élément de lecture** : le clip de film numéro `2i`, « M2TS », entrée et sortie du film (ticks de 45 kHz = ms × 45), puis le tableau des flux du film ;
- l'entrée de flux IG du tableau : `stream_entry_type` = 2 (**sous-chemin** 0, entrée de sous-clip 0), PID `0x1400`, codage `0x91` ;
- **`number_of_SubPaths` = 1**, de type 3 (que je lis comme « présentation de menu à graphiques interactifs » : valeur à confirmer dans la norme), avec **1 élément de sous-lecture = le clip numéro `2i+1`**, mêmes entrée et sortie que l'élément principal, synchronisé sur l'élément 0 à l'instant 0 ;
- un repère de chapitre au début. Ensuite `createBDMovie` peut rouvrir le fichier et passer la version `0200` en `0300` selon un indicateur de l'objet (indicateur non identifié).

Ce que cela établit : **le menu de Leawo est rattaché à la playlist de chaque titre**, comme un IG hors multiplexage dans un second clip ; il y a **un clip IG par titre**, numéros impairs (`FUN_18000a000` l'écrit avec 1, 3, 5…), les clips de fond des pages de menu sont numérotés après les films (`2N + k`). **Non élucidé** : si cet IG par titre est le menu principal ou un menu qui s'affiche pendant le film, et quelle playlist l'objet de menu (`PLAY_PL R100`, R101 = 0 au départ) lance réellement. **Non lu** : les deux autres écrivains de playlist (`FUN_18000b860`, `FUN_18000bf00`) qui servent aux playlists de pages de menu : rien n'est établi sur leurs sous-chemins. La copie portable (BDFix 1.0.0.9, 32 bits, 2016) contient le même écrivain (`FUN_10014390`, mêmes constantes : un sous-chemin de type 3, un clip `2i+1`) ; elle n'a que deux écrivains de playlist contre trois dans la copie installée (recherche de l'octet `MPLS`), sans autre comparaison.

**Ce que cela change pour BurnSmith** [déduit] :
- une **route plus courte que celle de bdmvkit** existe pour la phase 3b : écrire soi-même le `.m2ts` du menu (PAT/PMT/SIT/PCR de modèle + PES), sans ffmpeg ni correction du type de flux dans la PMT. Les quatre paquets de modèle sont récupérables par construction (ce sont des tables MPEG standard), pas par copie ;
- le modèle de navigation de Leawo (objet 0 → menu → un objet par titre, un registre de reprise) est plus simple que le répartiteur de registres du disque de référence ;
- **réserve** : ni l'IG hors multiplexage ni ce mode d'écriture n'ont été rejoués dans libbluray ou sur un lecteur ; bdmvkit (multiplexé, éprouvé dans libbluray) reste la route à tester d'abord. Le choix hors multiplexage ajoute le sous-chemin de la playlist et un second clip à gérer (c'est ce que fait Leawo : sous-chemin de type 3 et clip IG impair, voir plus haut).

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
- Leawo : l'écrivain du flux IG est localisé et lu dans `BDFix.dll` (section 4c), mais aucun disque Leawo n'a été produit ; la playlist de titre avec son sous-chemin IG est lue (section 4c) ; les playlists de pages de menu (`FUN_18000b860`, `FUN_18000bf00`) et les commandes des boutons ne sont pas lues. Yuhan : toujours inconnu. Pour trancher : faire produire un disque par leurs versions d'essai puis décoder le clip de menu (demande d'accord : lancement de logiciels tiers).
- `DVDFab64.exe` : l'analyse Ghidra de l'autre IA a échoué par **délai dépassé** au démarrage (330 s, 93 Mo) ; sa conclusion « binaire protégé » est une supposition, non vérifiée ici.
- bdmvauthor : cloné, lu, **jamais compilé ni exécuté**. bdmvkit : un seul disque d'essai (trois boutons de couleur unie, une seule page, sans menu animé ni pop-up) ; ses propres tests (`python -m unittest discover -s tests -t .`) n'ont pas été lancés. libbluray valide la logique, pas la tolérance d'un lecteur de salon.
- Le rendu visuel du menu n'a pas été vérifié : les captures du VLC portable (`snap*.png`) ne montrent que la vidéo de fond, pas la surcouche graphique ; ce sont les traces de libbluray (état des boutons, registres, commandes) qui prouvent le fonctionnement.
- Estimations de taille des phases volontairement absentes : aucune mesure n'a été faite.
- La phase 0 est la seule qui lève le risque principal, et elle exige un lecteur de salon et un disque inscriptible.
- Les phases 1 à 3 touchent les fichiers que l'autre session modifie en ce moment (`MenuDesign`, `BluRay.cs`, `Dvd.cs`, `MenuFiles.cs`, l'application WPF). Rien n'a été implémenté ici ; décider où et quand les lancer évite les conflits.
