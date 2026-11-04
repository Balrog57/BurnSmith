# Créateurs Blu-ray de DVDFab, Yuhan et Leawo, comparés à BurnSmith

Analyse du 2026-11-04. **Statique** pour les trois produits : aucun exécutable de DVDFab (repack « TrucNet »), de Yuhan ni de Leawo n'a été lancé. Rien de ces produits (code, Java, binaires, polices, images) n'est copié ici ni dans le dépôt ; le document décrit des comportements et des dispositions de fichiers, comme le veut la règle de BurnSmith (`THIRD-PARTY-NOTICES.md`). Les fichiers de licence et de thèmes opaques (`cert.dat`, `config.dat`, protecteur de `DVDFab64.exe`) n'ont pas été contournés.

Légende : **[octets]** lu dans les fichiers livrés (décodeur Python maison, `javap`, `certutil`, `xxd`) ; **[REA]** résultat REA/Ghidra (Evidence abrégée) ; **[chaînes]** texte trouvé dans un binaire sans le décompiler ; **[testé]** observé en exécutant BurnSmith ; **[déduit]** interprétation, à ne pas prendre pour un fait.

## 0. Résumé

1. **Risque principal de BurnSmith étayé par un indice nouveau** : le menu BD-J est non signé. Leawo contient une fonction (dans son chemin de copie de disques, voir 4) qui, quand un disque a un premier lancement BD-J avec des jars non signés ou absents, le réécrit en navigation HDMV seule et supprime `JAR`, `BDJO`, `AUXDATA`, `META`, `CERTIFICATE`, avec le message « PS3/PS5 hard_card red-screen fix » [REA `ev_c7247744`]. Les disques de BurnSmith correspondent exactement à ce déclencheur (premier lancement BD-J + jar sans `SIG-BD00.SF` [testé]). C'est une affirmation du fabricant lue dans le binaire, **pas une mesure** : aucun lecteur de salon n'a été essayé. Yuhan et Leawo ne livrent aucun jar ni BDJO (menus HDMV [déduit]) ; DVDFab signe le sien.
2. **Rectification d'une lecture précédente** : le « répartiteur » HDMV à registres GPR3/GPR4 de DVDFab n'est pas une invention de DVDFab, c'est le même objet 1 que tsMuxeR 2.7.0 écrit (9 commandes identiques, comparées commande par commande). Ce que DVDFab ajoute : la poignée de main **GPR100 = 0xFAB0 / 0xFAB1** et un objet de plus [octets, testé].
3. Le BDJO de BurnSmith a la même disposition que celui de DVDFab, champ par champ ; seuls diffèrent l'identifiant d'organisation, le nom de la classe initiale et les longueurs / décalages qui en dépendent (170 contre 166 octets) [octets].
4. Test de BurnSmith : compilation sans avertissement, 144 modèles sur 144 dessinés, disque d'essai valide, menu BD-J démarré par libbluray (VLC + JDK 8), disque `--no-menu` en HDMV pur [testé]. **Un seul défaut réel trouvé** : une miniature décorative interrompait toute la construction du disque quand la source n'a pas d'image clé après le point de recherche (cas limite : clips courts ou captures à GOP long). **Déjà corrigé entre-temps** par le commit `8f22da3` (20:02, « vignettes facultatives » : essai / avertissement autour de chaque extraction, DVD compris) ; je ne l'ai pas retesté après coup.
5. Convergences : x265 UHD-BD niveau 5.1 avec image clé ≈ 20–24 (les trois produits), mêmes options tsMuxeR de compatibilité chez Yuhan et Leawo. DVDFab a un débit cible « ajusté au disque » ; BurnSmith l'a déjà dans sa copie de travail (`FitDisc`, `BdKbps`).

## 1. Ce qui a été ouvert

| Produit | Ouvert dans REA | Balayage d'octets / chaînes seulement | Non ouvert |
|---|---|---|---|
| DVDFab 13.0.6.2 | `xdisc.dll`, `Options\FabAddonBluRay.exe` (installeur) ; le 2026-11-05 : `codecs.dll` (libx264 core 164), `fabvm.dll` (interpréteur HDMV) | `DVDFab64.exe` (octets seulement ; sections non standard `.upp0/.upp1/.upp2`, rôle inconnu) | `Plcore.dll`, `FabMenu*.exe` |
| Yuhan Blu-ray DVD Creator | `BlurayCreator.dll` (écriture de `index.bdmv`, `MovieObject.bdmv`, rebouclage des playlists) | `BlurayMuxer.dll`, `MediaHelper.dll`, `BlurayDVDCreator.exe`, `convert.dll`, `UTILITY.dll` | `config.dat` des thèmes (opaque) |
| Leawo Prof. Media | `panda.dll` (fonction de neutralisation BD-J) | `MakeM2ts.dll`, `BDFix.dll`, `DVDMaker.dll`, `libBurningCore.dll` | `cert.dat`, `reg`, `orchid.bin`, `hyacinth.bin`, `jasmine.bin`, `osmanthus.bin` (licence / données) |

Les sections PE de `xdisc.dll`, des modules Yuhan et de `panda.dll` sont normales (pas de protecteur) [octets, script de sections].

## 2. DVDFab

### 2.1 Où se trouve le Créateur

| Fichier | Taille | Rôle | Preuve |
|---|---|---|---|
| `DVDFab64.exe` | 93 Mo | Interface Qt + Python 3.10 embarqué, logique du Créateur (modes, gabarits, `index_bdav.bdmv`). Protecteur ≈ 20 Mo : non ouvert | [chaînes] |
| `xdisc.dll` | 11,5 Mo | Bibliothèque disque x64 (PDB `xdisc2017_x64`). Écrit/lit BDMV, playlists, CLPI, `bdmt_*.xml`, certificats, jar. Sert aussi à la copie de disques (hors sujet, non étudié) | [REA] `ev_7bab33e1` |
| `Options\FabAddonBluRay.exe` | 195 Ko | **Simple installeur** de module (sections empaquetées, 7 fonctions). Aucun code de création | [REA] `ev_7402a3c5`, `ev_4e25b982` |
| `BDMV\` | 0,4 Mo | **Squelette de disque** : variantes d'index, objets film, jar BD-J signé, certificats, playlist de boucle | [octets] |
| `template\DataDriven.xml`, `BDMV\x264_dp_*.xml`, `x265_dp_*.xml` | Ko | Débits par type de disque, préréglages | [octets] |
| `author\bdtemplates\` | 647 fichiers | Modèles de menu (déjà lus par `InstalledTemplate.cs`) | non repris ici |

Aucune occurrence de `tsMuxeR` dans `DVDFab64.exe`, `xdisc.dll`, `Plcore.dll` ni les autres binaires balayés [chaînes]. [déduit] DVDFab a son propre multiplexeur (la bibliothèque écrit les structures BDMV elle-même) ; ce n'est pas démontré, seule l'absence du nom est observée.

### 2.2 Le menu BD-J signé (`BDMV\00000.jar`, 75 Ko, 62 classes)

- **Signé** : `META-INF/MANIFEST.MF`, `SIG-BD00.SF`, `SIG-BD00.RSA`, créé avec JDK 1.7 [octets]. Fichier de permissions `org/homebrew/bluray.MenuTo.perm` : fichiers, cycle de vie, sélection de service, préférences utilisateur, `bd-vfs`, réseau (`*`), zone de binding [octets].
- **Certificats de disque** `CERTIFICATE\app.discroot.crt` et `bu.discroot.crt` : auto-signés, sujet « Studio / Codesigning Department / BDJCompany.56789abc / US », valables de l'an 0001 à l'an 9999 [octets]. Détail de `app.discroot.crt` (DER lu à la main, `certutil -dump`) : X.509 v3, numéro de série 0x40123212, RSA 1024 bits signé SHA-1, sujet = émetteur (`CN=Studio, OU=Codesigning Department, O=BDJCompany.56789abc, C=US`), extensions critiques `keyUsage` = signature numérique + signature de certificat et `basicConstraints` CA=VRAI, plus un nom alternatif (sujet et émetteur) `root@studio.com` [octets]. `CERTIFICATE\id.bdmv` porte l'identifiant d'organisation **0x56789abc**, le même que dans le fichier de permissions et dans le BDJO (application 0x4000) [octets]. [déduit] identité d'exemple, pas propre à DVDFab.
- `xdisc.dll` connaît ces noms : `CERTIFICATE/app.discroot.crt` et sa copie `BACKUP/`, `BDMV/BACKUP/JAR/00000.jar`, `META-INF/SIG-BD00.SF/.RSA`, `BDMV/AUXDATA`, `BDMV/META/DL/bdmt_eng.xml` [REA `ev_7bab33e1`]. Une petite fonction (`0x180404680`, `ev_0afd807a`) repère dans un jar les trois entrées MANIFEST / SIG-BD00.SF / SIG-BD00.RSA. [déduit] cohérent avec une personnalisation du jar qui rafraîchit les empreintes ; la clé privée de signature n'a pas été localisée. Que `xdisc.dll` **écrive** les `bdmt_*.xml` est [déduit] des chaînes (`ev_58c962ae`), pas observé dans une fonction.
- **BDJO** (166 octets) : comparé par `xxd` au BDJO (170 octets) d'un disque BurnSmith généré [testé] : même disposition, champ par champ (version `0200`, police `00000`, cache jar `00000` `*.*`, accès à toutes les playlists, autostart, type BD-J, profil 1 v1.0.0, priorité 3, binding `0xd0`, répertoire de base `00000`, accès fichiers `.`). Ne diffèrent que l'identifiant d'organisation (`56789abc` contre `42535430`, « BST0 »), la classe initiale (`org.homebrew.MenuTo`, 19 caractères, contre `org.burnsmith.menu.Menu`, 23) et les champs de longueur ou de décalage qui en dépendent : les deux décalages de section de l'en-tête (0x9e / 0xa2 contre 0xa2 / 0xa6), la longueur de la table (0x42 contre 0x46), celle du descripteur (0x32 contre 0x36) et celle du nom de classe (0x13 contre 0x17) [octets].
- **API utilisées** (`javap`, 13 498 lignes) : `RegisterAccess.getGPR/setGPR/getPSR`, `TitleContext.select` via `SIManager.getService` (le menu **sélectionne un titre** pour lancer le film), `Manager.createPlayer` + `PlayListChangeControl.selectPlayList` (boucle de fond), `PlaybackPlayItemEvent` / `PlaybackMarkEvent`, `AudioControl` / `SubtitlingControl.selectStreamNumber`, `HScene`, `HGraphicsConfigTemplate` [octets].
- **Piloté par données** : le menu lit un `AuthorData.xml` (nœuds `bg_image`, `button`, `description_frame`, `video_frame`, `arrow_mode`, `scroll_mode`, panneaux audio / sous-titres) et référence des **sons de boutons** `bd://SOUND:00..03` [octets, constantes du jar]. Le jar lit un registre « GPR 100 » avec les deux valeurs 0xFAB0 / 0xFAB1.

### 2.3 Le pont HDMV ↔ BD-J (squelette `BDMV\`)

Décodage de `index*.bdmv` et `MovieObject*.bdmv` avec l'énumération de libbluray (`hdmv_insn.h` : `JUMP_OBJECT=0, JUMP_TITLE=1, CALL_OBJECT=2, CALL_TITLE=3, RESUME=4` ; `PLAY_PL=0, PLAY_PL_PI=1, PLAY_PL_PM=2` ; option SET sur 5 bits) [octets]. Une première version de ce décodeur décalait ces énumérations d'un cran (JUMP lu comme NOP) ; elle est corrigée.

- Quatre variantes d'index : `index.bdmv` (INDX0100) et `index_bd.bdmv` (0200) = séquence HDMV (premier lancement = objet 2, menu principal = objet 1, 1 titre = objet 0), **la même que l'index d'origine de tsMuxeR 2.7.0** (disque BurnSmith `--no-menu` : mêmes références d'objets [testé]) ; `index_hdmv.bdmv` = 6 titres HDMV sans BD-J ; `index_bdav.bdmv` = premier lancement et menu principal sur l'objet HDMV 3, puis **10 titres BD-J** (`00000`). Le dossier `preview\` a la variante « aperçu » (1 titre BD-J).
- `MovieObject.bdmv` de DVDFab (MOBJ0100, 4 objets) :
  - objet 0 : `MOVE R10,R0 ; MOVE R0,0 ; PLAY_PL_PM <playlist>,R10 ; JUMP_TITLE 1` (numéro de playlist = **valeur de gabarit**, remplacée à la création) ;
  - objet 1 (répartiteur) : si GPR3 ≠ 0xFFFF → `PLAY_PL GPR3` ; sinon si GPR4 ≠ 0 → `JUMP_TITLE GPR4` ; sinon `JUMP_TITLE 1` (après avoir remis GPR3 et GPR4 à leur valeur neutre) ;
  - objet 2 : remet GPR 0/2/3/4 à zéro / 1 / 0xFFFF / 0, pose **GPR100 = 0xFAB0**, `JUMP_TITLE 1` ;
  - objet 3 : pose **GPR100 = 0xFAB1**, `JUMP_TITLE 1` (le dossier `preview\` en fait son premier lancement, d'où [déduit] « mode aperçu »).
- **Comparaison avec tsMuxeR 2.7.0** [testé] : `MovieObject.bdmv` (MOBJ0200, 3 objets) écrit par tsMuxeR pour un disque BurnSmith : l'objet 1 est identique commande par commande (9 sur 9) à l'objet 1 de DVDFab ; les objets 0 et 2 ne diffèrent que par quelques commandes (numéro de playlist, `BREAK` et `JUMP_TITLE 0` chez tsMuxeR contre `JUMP_TITLE 1` et l'écriture de GPR100 chez DVDFab) ; l'en-tête passe de `MOBJ0100` (DVDFab) à `MOBJ0200` (tsMuxeR 2.7.0). **Ce qui est propre à DVDFab** : la poignée de main GPR100 et l'objet 3. [déduit] DVDFab est parti de la même base que tsMuxeR (convention GPR3 = playlist à jouer, GPR4 = titre à appeler) ou d'une base commune.
- `index_bdav` porte dans les données d'application la marque `(Fab7062)` ; `xdisc.dll` contient le format `(Fab%s)` [octets, REA].
- [déduit] Le menu BD-J et l'HDMV se passent la main par ces registres ; le film est lancé comme un titre (HDMV), pas lu par le lecteur Java du menu. Le déroulé complet entre les 10 titres BD-J et les objets 4 à 12 n'a pas été reconstruit.

### 2.4 Menu animé

`PLAYLIST\00099.mpls` (22 474 octets) = **280 éléments** identiques (`00099.m2ts`, 24 s chacun, condition de raccord 1) + **280 repères** de chapitre ; `00099.m2ts` fait 301 056 octets, un seul flux vidéo H.264 1080p23,976 de ≈100 kb/s ; le CLPI ne décrit que ce flux (PID 0x1011) ; aucun flux de présentation graphique [octets, ffprobe]. [déduit] boucle de ≈1 h 52 déjà déroulée dans la playlist, avec une vidéo de remplacement ; le xlet suit les changements d'élément / de repère.

### 2.5 Encodage et débits

DVDFab (`DataDriven.xml`, [octets]) — créateur : DVD 2 000–10 000 kb/s (cible 5 500) ; BD 2 000–35 000 (cible 15 000) ; UHD 2 000–60 000 (cible 30 000) ; option « Ajuster au disque » (`DVDCreator_Bitrate_FitToDisc`) [chaînes].

| Réglage | DVDFab « Standalone Blu-ray » | BurnSmith (copie de travail, `BluRay.cs`) |
|---|---|---|
| Mode | débit moyen 15 000 kb/s, jusqu'à 3 passes | débit demandé (`BdKbps`, borné 2 000–35 000), ou ajusté au disque (`FitDisc`, ×0,94), sinon CRF 17 (10–28), 1 passe, `medium` |
| Profil / niveau | High / 4.1 | `bluray-compat`, niveau 4.1 |
| GOP | image clé max 24 | `keyint=24` |
| B-images / réf. | 5 / 2 réf. | `bframes=3`, `b-pyramid=strict`, `open-gop=0` |
| VBV | tampon 30 000, débit max 35 000 | tampon 30 000, débit max 30 000–40 000 selon le débit |
| Autres | `--mvrange 511`, `--sar 1:1`, AUD, lookahead 60 | `slices=4` |
| UHD (x265) | main10 niveau 5.1 high tier, indicateur UHD-BD, image clé max 24, VBV 41 000, CRF 28, `aq-mode 2` | **pas de ré-encodage** : source 2160p HEVC exigée |

### 2.6 Fonctions repérées [chaînes, `DVDFab64.exe`]

Modes : fichier→DVD / BD / UHD, **DVD→BD, DVD→UHD, BD→UHD**, BD personnalisé, **fusion de BD**, BDAV1 ; ligne de commande `/PROCESSMODE PASSTHROUGH|TRANSCODE`, `/OUTDISC` (DVD5/9, BD5/9, BD25/50/66/100 en 720p/1080p/2160p), `/MENUTEMPLATENAME` [`Commandline.txt`] ; amélioration HDR par IA (`AIHDRUpconverter`) ; créateur de vidéo de fond de menu ; livrés à côté : `dovi_tool.exe`, `libHdr2Sdr.dll`, `DtshdProcess.exe`, dossier `mvc` (3D) [noms de fichiers, non analysés].

## 3. Yuhan Blu-ray DVD Creator

- **Aucun jar, BDJO ni certificat** dans le dossier d'installation (`find` sur `*.jar`, `*.bdjo`, `*.perm`, `*.crt`) [octets]. Le Créateur n'écrit pas de menu BD-J ; les noms `BDMV/JAR`, `BDMV/BDJO` n'apparaissent que dans `BlurayMuxer.dll` (multiplexeur) [chaînes].
- **Multiplexeur** : `BlurayMuxer.dll` embarque tsMuxeR 2.6.16 [chaînes]. Options de compatibilité (identiques chez Leawo) : `--no-pcr-on-video-pid --new-audio-pes --hdmv-descriptors --vbv-len=500 --start-time=524280` [chaînes].
- **Navigation HDMV écrite par Yuhan** : `BlurayCreator.dll` contient un écrivain d'`index.bdmv` (`FUN_180006260`) et de `MovieObject.bdmv` (`FUN_180006c90`) qui produisent `INDX0200` / `MOBJ0200` (`0300` pour l'UHD), des titres HDMV, des commandes de 12 octets utilisant les registres 100–108, une chaîne de données d'application qui se termine par « …SOFTWARE CO.,LTD. », puis la copie dans `BACKUP` [REA, pseudo-code lu, commandes non toutes décodées]. Une fonction de 15 Ko (`FUN_18000b360`) réécrit playlists et infos de clip produites par tsMuxeR [REA, lecture partielle].
- **Menu** : dessiné par Qt (classes `CBDMenu`, `CBDButton`) avec images de bouton normale / sélectionnée / activée ; fichiers `menu_0.m2ts`, `menu_%2.m2ts`, `bf_menu_%2.m2ts` ; options « type de surbrillance », « mode vidéo du menu », « menu contextuel (popup) » [chaînes, `BlurayDVDCreator.exe`]. Aucune chaîne « IGS » n'a été trouvée ; [déduit] les trois états d'image et `BD_BUTTON_HEADER` / `BD_COMMAND` (Leawo, ci-dessous) décrivent des boutons de flux graphique interactif, mais **l'écriture de ce flux n'a pas été localisée**.
- **Thèmes** : `PresetMenu\THEME01..30`, chacun en PNG à 4 résolutions (768×576, 1280×720, 1920×1080, 3840×2160), `thumb_169.jpg` / `thumb_43.jpg` et un `config.dat` opaque non lu [octets]. BurnSmith en dessine déjà les 30 thèmes (`YuhanSkin.cs`, non suivi par git) [testé].
- **Encodage** [chaînes, `MediaHelper.dll`] : x264 `keyint=24:min-keyint=24:no-scenecut=1` (GOP fixe) ; x265 UHD-BD `uhd-bd=1:keyint=24:min-keyint=24:no-open-gop=1:no-scenecut=1:level=5.1:hrd=1:aud=1:no-info=1:sar='1:1':colorprim='bt2020':transfer='smpte2084':colormatrix='bt2020nc':master-display=…:max-cll=…:bframes=2`, et une variante `preset=medium:tune=grain:profile=main10:level-idc=51:high-tier=1:repeat-headers=1`. La chaîne `keyint=20:level=5.1` se retrouve aussi chez Leawo. `dovi_tool` et `hdr10plus_tool` livrés [noms de fichiers]. DVD par dvdauthor ; écrivain UDF propre [chaînes].
- `res\bdmt_eng.xml` : gabarit de métadonnées avec `DiscName`, miniatures en commentaire [octets].

## 4. Leawo Prof. Media

- **Modules** : `MakeM2ts.dll` = tsMuxeR (classes `TSMuxer`, `TSMuxerFactory`, `PGSStreamReader`, `AACStreamReader`), lignes `MUXOPT --no-pcr-on-video-pid --new-audio-pes --hdmv-descriptors --blu-ray[-v3] --vbr --vbv-len=500` [chaînes]. `BDFix.dll` réécrit `index.bdmv`, `MovieObject.bdmv`, playlists, CLPI et leurs copies `BACKUP` ; il exporte le modèle de menu `CBDMenu` / `CBDButton` / `CBDMixInfo` (images activée / sélectionnée, commandes `BD_COMMAND`, `BD_BUTTON_HEADER`, fichiers audio par menu, cadence) [chaînes]. **Mêmes noms de classes `CBDMenu` / `CBDButton` que chez Yuhan** : [déduit] code ou fournisseur communs.
- **DVD** : `DVDMaker.dll` (spumux, dvdauthor, mplex livrés dans `dvdauthor\`, fichiers `menu-%2.mpg`, `dvdauthor.xml`, `palette.dat`, commandes de navigation `jump titleset … menu;`), message « Generate Blu-ray menu » et fichier `menu-%2.m2ts` [chaînes].
- **Pas de BD-J à la création** : aucun `.jar`, `.bdjo` ni `.crt` livré [octets]. La seule trace de BD-J est la fonction de neutralisation ci-dessous.
- **Neutralisation d'un BD-J non signé** (`panda.dll`, `FUN_180a16480`, un seul appelant `0x180a07620`) [REA `ev_c7247744`, `ev_773ae958`, `ev_b6e072ed`] : lit le premier lancement de `BDMV\index.bdmv` (type 2 = BD-J), compte les jars de `BDMV\JAR` et ceux sans `SIG-BD00.SF` ; si premier lancement BD-J **et** (aucun jar **ou** au moins un jar non signé), elle choisit la playlist principale la plus longue, réécrit `index.bdmv` (`INDX0200`, 0x78 octets) et `MovieObject.bdmv` (`MOBJ0200`) pour que premier lancement et menu principal exécutent l'objet HDMV 0 → `PLAY_PL(<playlist>)`, supprime `JAR`, `BDJO`, `AUXDATA`, `META` et `CERTIFICATE`, puis reconstruit `BACKUP`. Messages : « BD+ breakage detected (BD-J FirstPlay + unsigned/missing jars) … (PS3/PS5 hard_card red-screen fix) ». Son code d'échec est `MDES_ERR_COPY_INCOMPLETE (-397)` [chaînes] : [déduit] elle sert à la **copie** de disques, pas à la sortie du Créateur ; l'appelant n'a pas été analysé.
- **Encodage** [chaînes, `panda.dll`] : x264 et x265 logiciels ; encodeurs matériels NVENC (préréglage « Blue Ray »), QSV et AMF avec un garde `[BDGuard] hw H.264 stream not BD-compliant → fallback to sw x264` et repli `hevc_qsv` → libx265 ; paramètres x265 (déduit d'après `master-display`, `max-cll`, `uhd-bd`) : `keyint=20:level=5.1:hrd=1:aud=1:no-info=1:sar='1:1':colorprim='bt2020':transfer='smpte2084':colormatrix='bt2020nc':master-display=…:max-cll='0,0':bframes=2` et `uhd-bd=1:keyint=20:level=5.1`. Option x264 `bluray-compat` présente.
- **Modèles** : `MenuTemplate\Build-in\{16-9, 16-9-2, 4-3}\` × 11 familles (business, childhood[01], education[01], holiday[01], nature, sports, standard, wedding), 165 PNG, 66 JPG, 6 `template.xml` (33 dossiers de modèles = 11 familles × 3 formats d'image) ; chaque `template.xml` décrit page de menu et pages de scènes en 1920×1080 (fond, cadre, rectangle de vignette vidéo, textes avec police / taille / alignement, couleurs de surbrillance et de sélection) [octets]. BurnSmith les lit déjà (`LeawoSkin.cs`).
- **Gravure** : `libBurningCore.dll`, `StarBurn.dll`, `ImgBurn\` livré, `libudf.dll`, `dvd_iso_creator.dll` [noms de fichiers] ; non étudiée.

## 5. Tests de BurnSmith (2026-11-04, ≈ 19:45–20:00)

Les essais ont tourné sur la copie de travail de ≈ 19:50, commitée ensuite par ailleurs (`fba77a4`, `b95758a` à 19:54) ; le commit `8f22da3` de 20:02 est postérieur à la compilation. Aucun fichier suivi par git n'a été modifié par ces essais : la compilation et tous les disques d'essai sont dans le dossier temporaire de session.

| Essai | Résultat |
|---|---|
| `dotnet build src/BurnSmith.Cli -c Release -o <temp>` | réussi, 0 avertissement, 0 erreur [testé] |
| `BurnSmith.Cli check-templates` | **144 sans problème, 0 avec problèmes, 0 refusés** : DVDFab 27 (22 Blu-ray + 5 DVD), Leawo 33, Yuhan 30, DVDStyler 49, DVD Forge 5 [testé] |
| `BurnSmith.Cli bd --video essai.mp4 …` (8 s, 1080p24, GOP 24, ffmpeg 8.1.1 du PATH, tsMuxeR 2.7.0 de `tools\`) | disque produit en 1,8 s : `index.bdmv`, `MovieObject.bdmv`, `PLAYLIST\00001.mpls`, `CLIPINF\00001.clpi`, `STREAM\00001.m2ts`, `JAR\00000.jar`, `BDJO\00000.bdjo`, `META\DL\` (`bdmt_eng.xml`, 2 miniatures), copies `BACKUP\` [testé] |
| Décodage du disque | `INDX0200` : premier lancement = menu principal = titre 1 = BD-J `00000` ; `MOBJ0200` de tsMuxeR (voir 2.3) [testé] |
| Contenu du jar | `Menu` + 4 classes internes, `bg_root.jpg`, `hl_root_play.png`, `mark.png`, `menu.txt`, MANIFEST seul : **pas de `SIG-BD00.*`, pas de fichier de permissions, pas de dossier `CERTIFICATE`** [testé] |
| `BurnSmith.Cli bd … --no-menu` (même source) | disque **HDMV pur** : `index.bdmv` d'origine de tsMuxeR (premier lancement = objet 2, menu principal = objet 1, titre 1 = objet 0, qui joue la playlist 1), pas de `JAR` / `BDJO`, `META\DL` présent [testé]. Décodé seulement, **non lu dans un lecteur** |
| `tools\vlctest.ps1` (VLC 3.0.24 portable + JDK 8, libbluray), disque avec menu | `BD-J check: OK`, xlet démarré (`burnsmith: page root`) ; lignes d'erreur non fatales : `CERTIFICATE\id.bdmv` introuvable (et sa copie), `sync(): not visible`. Aucune capture n'était demandée (seulement `sleep:3`), donc le rendu du menu n'a pas été vérifié à l'image [testé] |
| Source de 8 s avec **une seule image clé** | la construction s'arrête à `BluRay.cs:135` (miniature `thumbnail_416x240.jpg`) : ffmpeg répond « Non full-range YUV is non-standard … Error while opening encoder ». Cause isolée par comparaison : la même commande réussit sur le MP4, avec `-ss` placé **après** `-i` sur le flux TS, et avec une image clé toutes les 24 images ; `-strict unofficial` et `yuvj420p` n'y changent rien. [déduit] la recherche en entrée (`-ss` avant `-i`) dans le TS de travail tombe après la dernière image clé : aucune image n'atteint l'encodeur mjpeg, dont le message n'est que le symptôme. `Tools.Run` lève une exception, donc une miniature décorative fait échouer tout le disque [testé] |

Non testé : lecteur de salon ou console (le point décisif pour la signature), gravure, DVD, disque UHD, plusieurs titres, sous-titres, le menu rendu à l'écran, le disque `--no-menu` dans un lecteur.

## 6. Comparaison à trois produits

| Domaine | DVDFab | Yuhan | Leawo | BurnSmith |
|---|---|---|---|---|
| Menu Blu-ray | BD-J **signé** + certificats + HDMV auxiliaire, piloté par `AuthorData.xml` | HDMV, dessiné par Qt, vidéo de menu + boutons à 3 états [déduit] | HDMV, même modèle de menu que Yuhan [déduit] | BD-J **non signé** (xlet générique + images) |
| Lancement du film | titre HDMV (sélection de titre, registres) | titres / objets HDMV | objets HDMV | lecteur JMF dans le xlet |
| Multiplexeur | interne [déduit] | tsMuxeR 2.6.16 | tsMuxeR (classes visibles) | tsMuxeR 2.7.0 |
| Options tsMuxeR de compatibilité | non applicable | `--no-pcr-on-video-pid --new-audio-pes --hdmv-descriptors --vbv-len=500 --start-time` | mêmes, sans `--start-time` | `--blu-ray[-v3] --vbr --label --custom-chapters --mplsOffset --m2tsOffset` |
| Boucle de fond | playlist de 280 éléments + xlet | non établi | non établi | playlist 99 relancée par le xlet |
| Sons de boutons, cadre vidéo, défilement | oui | non établi | non établi | non |
| Titre du disque / miniatures (`META/DL`) | oui [déduit] | gabarit `bdmt_eng.xml` | non établi | oui, par langue, 2 miniatures |
| Débit cible Blu-ray, ajusté au disque | oui [chaînes] | non établi | non établi | **oui dans la copie de travail** (`FitDisc`, `BdKbps`) |
| Encodeur matériel (NVENC / QSV / AMF) | non établi | non établi | oui, avec garde de conformité et repli | non |
| UHD | passthrough ou x265 | x265 UHD-BD | x265 UHD-BD, HDR10 | passthrough seulement |
| Modes DVD→BD, fusion, BDAV1, IA / HDR | oui | non établi | non établi | non |
| Gravure | oui, non étudiée | oui, non étudiée | oui (StarBurn, ImgBurn), non étudiée | IMAPI2 intégré |
| Modèles de menu | 647 fichiers, lus | 30 thèmes × 4 résolutions | 33 dossiers (11 familles × 3 formats) | 144 dessinés : DVDFab 27, Leawo 33, Yuhan 30, DVDStyler 49, DVD Forge 5 [testé] |
| Jar BD-J signé / certificat de disque | oui | sans objet | sans objet (et neutralise un BD-J non signé) | non |

## 7. Pistes pour BurnSmith, par ordre d'intérêt

1. **Lever le risque du BD-J non signé.** Deux voies, non exclusives :
   - *Filet de sécurité immédiat* : proposer un mode « compatibilité » qui garde l'`index.bdmv` écrit par tsMuxeR (HDMV, lance le film directement) et omet jar / BDJO. C'est ce que fait déjà `--no-menu` : disque HDMV pur, vérifié par décodage [testé] ; il manque de le nommer comme repli dans l'interface et la documentation, et de le lire dans un vrai lecteur.
   - *Signer le jar* : `keytool` et `jarsigner` sont dans `tools\jdk8`, mais ils **ne suffisent pas** : le BD-J demande un fichier de permissions `bluray.<Classe>.perm`, des certificats de disque `CERTIFICATE\app.discroot.crt` (+ `BACKUP\`) et un `id.bdmv` dont l'organisation égale celle du BDJO. Les certificats de DVDFab donnent le modèle observé [octets, voir 2.2] : auto-signé, sujet `O=BDJCompany.<identifiant en hexadécimal>`, `keyUsage` signature numérique + signature de certificat, `basicConstraints` CA=VRAI, nom alternatif e-mail ; `keytool` sait les poser par l'option `-ext` (à vérifier sur le JDK 8 de `tools\`). Compléter par une référence ouverte comme le projet « HD Cookbook » plutôt que par essais à l'aveugle. Demander dans la permission le strict nécessaire (DVDFab demande beaucoup plus).
   - Dans les deux cas, **un essai sur un vrai lecteur (et si possible une PS3 ou une PS5) tranchera** : tout ce qui est dit plus haut vient des chaînes de Leawo et des fichiers de DVDFab, rien n'a été mesuré sur matériel.
2. **Miniatures** (seul défaut réel trouvé, voir 5) : l'échec ne coûte plus le disque depuis `8f22da3`. Reste une amélioration facultative : en cas d'échec, refaire l'extraction avec `-ss` après `-i` (essayé à la main : produit une image sur le cas qui échouait ; plus lent, d'où un repli et non l'ordre par défaut) avant de renoncer à la miniature, pour que le disque en ait une quand même.
3. **Options tsMuxeR de compatibilité** (`--no-pcr-on-video-pid --new-audio-pes --hdmv-descriptors --vbv-len=500`) : Yuhan et Leawo (probablement de même origine, voir 4) les emploient toutes les quatre ; à essayer sur lecteur, une ligne dans `MUXOPT`.
4. **Lancer le film comme un titre** (`TitleContext.select` vers un titre HDMV) plutôt que par le JMF du xlet : meilleure tenue de la touche « menu », de la reprise et des chapitres sur lecteurs de salon [déduit] (le commit `8f22da3` traite déjà la touche « menu » côté xlet, en lisant le registre PSR4 pour ne lancer le film qu'au premier lancement ; non essayé sur lecteur). Les objets HDMV de tsMuxeR (objet 0 = jouer la playlist 1, objet 1 = répartiteur GPR3 / GPR4) sont bien dans `MovieObject.bdmv`, mais **inaccessibles aujourd'hui** : `Bdmv.PatchIndex` remplace toute la table des titres par un seul titre BD-J. Il faudrait que `PatchIndex` garde (ou rajoute) un titre HDMV pointant l'objet 0, puis que le xlet le sélectionne. Chantier moyen, à essayer sur lecteur.
5. **UHD avec ré-encodage x265** (main10, niveau 5.1, `uhd-bd=1`, image clé ≈ 20–24, VBV selon le profil) quand la source n'est pas du 2160p HEVC ; les trois produits convergent sur ces valeurs.
6. **Boucle de fond déroulée dans la playlist** (N éléments identiques) au lieu de la relance par le xlet : retire le redémarrage entre deux tours ; à tester sur un vrai lecteur.
7. Plus tard : encodeur matériel (NVENC / QSV / AMF) avec contrôle de conformité et repli x264 ; sons de boutons (`sound.bdmv`) ; modes DVD→BD / fusion de BD.

## 8. Limites

- `DVDFab64.exe` (code non ouvert), `Plcore.dll`, `FabMenu*.exe` non ouverts (`fabvm.dll` et `codecs.dll` l'ont été le 2026-11-05, voir plus bas) ; Yuhan et Leawo : seuls `BlurayCreator.dll` et `panda.dll` ont été décompilés, une partie seulement de leurs fonctions ; le reste vient de balayages de chaînes, qui prouvent la présence d'un texte, pas l'usage.
- Rien n'a été **exécuté** côté produits : l'enchaînement exact (quels fichiers selon le mode, valeurs de gabarit) est déduit.
- Comment Yuhan écrit les boutons de son menu HDMV n'est pas établi. Pour Leawo, `BDFix.dll` a été décompilé ensuite (2026-11-04) : il écrit lui-même le flux IG dans un clip dédié ; voir `FEUILLE-DE-ROUTE-BLURAY.md`, section 4c (constats de code, aucun disque Leawo produit).
- Leawo existe en **deux exemplaires différents** sur la machine (installé v1.2.0.0 et portable d'un site tiers, build de 2016) ; l'exemplaire de `panda.dll` analysé plus haut n'est pas consigné. Détail : `FEUILLE-DE-ROUTE-BLURAY.md`, section 4c.
- Un bilan collé par l'utilisateur (autre IA, REA CLI + Ghidra, 27 modules des trois produits) ne contient que des vues d'ensemble : nombre de fonctions et de chaînes, hachages. Les chiffres ont été recoupés avec ses fichiers et sont exacts ; il ne change aucune conclusion de ce rapport. Son « DVDFab64.exe protégé » est un délai dépassé de Ghidra (330 s), pas une protection constatée.
- La fonction de neutralisation de Leawo : son unique appelant (`0x180a07620`) n'a **volontairement pas été suivi**, car il mène au chemin de copie de disques protégés, hors du périmètre fixé (pas d'étude de la protection contre la copie) ; sa place dans la copie est déduite du code d'erreur `COPY_INCOMPLETE`. Le texte « PS3/PS5 » est une affirmation du binaire, non vérifiée ; cela ne change pas la décision pour BurnSmith, qui est de tester sur un lecteur.
- `config.dat` de Yuhan : je ne l'ai pas lu, mais BurnSmith le lit déjà (`YuhanSkin.cs`, note mémoire « Yuhan templates », clé relevée dans l'exécutable de Yuhan). La licence de cette lecture n'est pas évaluée ici ; elle relève de la règle « images embarquées, code et programmes exclus » à confirmer pour ce cas.
- Les noms Java du jar DVDFab sont brouillés ; seules les API appelées et les constantes sont lisibles.

**Compléments du 2026-11-05 (REA sur `codecs.dll`, `fabvm.dll`, octets de `DVDFab64.exe` ; DVDFab 13.0.6.2 portable, SHA-256 débutant par `3f9b43f0` pour `codecs.dll`, `79a1aac8` pour `fabvm.dll`) :**
- **`codecs.dll` et `codecs_main10.dll` sont des builds de libx264 « core 164 »** (exports `x264_param_default_preset`, `x264_param_parse`, `x264_encoder_open_164`… ; texte « x264 - core %d », copyright 2003-2021 ; `codecs.dll` refuse l'entrée au-dessus de 8 bits ; `codecs_main10.dll` est, d'après son nom, la variante 10 bits, non vérifiée). Le nom `veryfast` y est la table de préréglages standard de x264 : ce ne sont **pas** des valeurs propres à DVDFab. Une recherche d'octets dans les DLL et EXE du dossier `DVDFab13` (≤ 120 Mo) ne trouve ni nom `x265_encoder_open_*` ni chaîne de version x265 ; cela ne prouve pas l'absence d'un encodeur H.265.
- **Qui choisit les valeurs** : `DVDFab64.exe` (93 Mo ; `.text` 46 Mo, `.rdata` lisible : les chaînes y sont en clair). Il contient des **gabarits de ligne de commande x264** (`--crf `, `--bitrate `, `--pass 1/2/3 --bitrate `, `--keyint `, `--b-adapt `, `--b-pyramid `, `--rc-lookahead `, `--vbv-bufsize `, `--aq-strength `, `--nal-hrd `, `--aud 1 `, `--output "`…) et des clés de réglages (`x264AdvancedSettings`, `Lookahead`, `NoMBTree`, `Scenecut`, `X264Nalhrd`, `SlicesNb`…) ; une seconde famille de clés pour le H.265 (`helper_vencode_hevc` : `Preset`, `Tune`, `StrProfile`, `HighTier`, `BEnableUHDBD`, `MaxCUSize`, `Bframes`, `BFrameAdaptive`, `LookaheadDepth`…). Ce sont des **noms d'options et des gabarits** : les valeurs viennent de données d'exécution (profils de disque, `x264_dp_*.xml` déjà lus), pas de constantes lues ici. **Le moteur H.265 n'est pas identifié.**
- **Incohérence relevée, non expliquée** : `DVDFab64.exe` porte le nom `x264_encoder_open_107` (core 107) alors que les deux DLL livrées exportent `x264_encoder_open_164` ; l'exécutable construit aussi des lignes de commande, ce qui évoque un encodeur lancé comme programme, mais aucun module du dossier ne l'a prouvé.
- **`fabvm.dll` n'est pas un constructeur de playlists** : c'est un **interpréteur / lecteur de commandes HDMV** (table de mnémoniques `LINK_MK`, `PLAY_PL`, `PLAY_PL_MK`, `PLAY_PL_PI`, `JUMP_TITLE`, `CALL_OBJECT`, `SET_STREAM`, `ENABLE_BUTTON`…, et chemins `BACKUP/id.bdmv`, `BACKUP/MovieObject.bdmv`). Le rôle exact (analyse de disque, lecture) est déduit ; la correction vaut pour la ligne « fabvm.dll » du tableau de la section 1 et pour la piste « construction de playlists » évoquée dans les notes d'une autre IA.
- `DVDFab64.exe` : trois sections non standard `.upp0` (11,5 Mo), `.upp1` (47 Ko), `.upp2` (9,7 Mo). Le rôle de ces sections est **inconnu** ; le mot « protecteur » de la section 1 est une lecture de leur nom, pas une analyse. Le code du programme n'a pas été ouvert dans Ghidra (le dernier essai d'une autre IA a dépassé 330 s) : toute conclusion sur le code de DVDFab64 reste hors de portée de ce rapport.
- Les occurrences de `AACS`, `Unit_Key`, `MKB` dans `xdisc.dll` relèvent de la copie de disques protégés ; non étudiées, sans rapport avec la création.
- Outils de travail dans le dossier temporaire de session (`bdmv_dump.py`, `javap` du JDK 8 de `tools\`, `strs.py`), non versionnés. Le dépôt a bougé pendant l'analyse (quatre commits entre 19:16 et 20:02, faits par ailleurs) : les constats de la section 5 portent sur l'état de ≈ 19:50 ; au moment de l'écriture, seul ce rapport est non suivi.
