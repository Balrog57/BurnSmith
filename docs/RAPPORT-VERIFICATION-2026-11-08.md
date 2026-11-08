# Vérification de BurnSmith face à DVDFab, Leawo et Yuhan (8 novembre 2026)

Point de départ : les rapports d'inspection `DVDFABINSPECT.txt` (DVDFab 13.0.6.2), `LEAWOINSPECT.txt` (Leawo Prof. Media 13.0.1.6) et `YUHANINSPECT.txt` (Yuhan Blu-ray DVD Creator 5.0.0). Ces fichiers restent locaux, hors du dépôt : ils décrivent les installations de ce PC. Chaque fonction de BurnSmith y est comparée, puis vérifiée sur de vrais disques construits ce jour, avec leurs ISO.

Conventions : **[FAIT]** mesuré ou observé ce jour, **[DÉDUCTION]** tiré de faits, **[NON TESTÉ]** ce qui n'a pas pu l'être. Les colonnes concurrentes ne reprennent que ce que leurs rapports prouvent ; « ? » = le rapport ne le dit pas.

## En bref

- Trois écarts gênaient la lecture sur platine ou l'adaptation au disque. Ils sont corrigés :
  1. **Le menu BD-J n'était pas signé** alors que son BDJO le déclarait signé (application 0x4000). Aucun fichier dans `CERTIFICATE`. DVDFab, le seul des trois à faire du BD-J, signe le sien. BurnSmith le signe désormais de la même façon.
  2. **Le PCR passait par le PID de la vidéo** (0x1011). Leawo et Yuhan demandent `--no-pcr-on-video-pid`, et le disque commercial de référence porte son PCR sur 0x1001 : c'est maintenant le cas des disques BurnSmith.
  3. **La ligne de commande ne s'adaptait pas au disque par défaut** (`--quality source`), contrairement à la fenêtre. `fit` est désormais le défaut des deux.
- Deux garde-fous ont été ajoutés :
  - le « Test avant gravure » vérifie la signature du menu comme le ferait un lecteur ;
  - il refuse un dossier ou une ISO plus grand que le support choisi (la gravure n'a alors pas lieu).
- Les essais ont montré trois défauts de menu ou d'adaptation. Ils sont corrigés :
  - **Titre coupé** : un titre trop long pour sa zone devient plus petit, puis passe sur deux lignes, au lieu d'être coupé (« Cobra... »).
  - **Pistes indiscernables** : deux pistes d'une même langue gardent le détail de leur titre. La saison affichait deux boutons « Français 5.1 », dont l'un était l'audiodescription.
  - **DVD trop petit** : le DVD annonce dès le départ que le disque est trop petit quand même le débit plancher ne suffit pas. Le Blu-ray le faisait déjà.
- **Testé ce jour** : une saison complète (10 épisodes) sur BD25, un film copié tel quel sur BD25, le même film réduit pour tenir sur un BD5, et un DVD court. Chaque disque a été construit, mis en ISO et contrôlé : structure, UDF, signature (jarsigner et openssl), en-têtes H.264 et tampon vidéo. Les menus ont été parcourus dans VLC/libbluray.
- **Pas testé** : aucune gravure, aucun lecteur de salon, ni PS3/PS5. VLC ne vérifie pas les signatures BD-J. Leur validité est prouvée par jarsigner et openssl, et leur profil est celui de DVDFab, mais l'acceptation par une vraie platine reste à voir.

## 1. Fonction par fonction

| Fonction | DVDFab 13 | Leawo 13 | Yuhan 5 | BurnSmith |
|---|---|---|---|---|
| Analyse des sources | interne + BDInfo | FFmpeg 4.x (sonde de cadence) | FFmpeg | ffprobe 8.1.1 (JSON) |
| Encodage H.264 Blu-ray | libx264 statique | ? | FFmpeg / encodeurs externes | x264 : `bluray-compat`, niveau 4.1, VBV 30 Mbit, 1 image clé / s, 4 tranches, `b-pyramid=strict`, HRD ; PsF en 25 et 29,97 i/s. **[FAIT]** voir § 3 |
| Copie sans réencodage | ? | ? | `BDCreatorCheckPassthrough` (comportement inconnu) | seulement un H.264 8 bits 4:2:0 de niveau ≤ 4.1, à la taille et à la cadence du disque, sans coupe ni effet. **[FAIT]** film copié conforme (§ 3) |
| Audio | ? | ? | ? | AC-3, DTS (et DTS-HD) et TrueHD copiés en 48 kHz, le reste refait en AC-3 640/448 kb/s |
| Muxage M2TS | libtsmux interne (patché MVC) | MakeM2ts (classes TSMuxer) | tsMuxeR 2.6.16 (fork) | tsMuxeR 2.7.0 |
| Options du muxage | ? | `--no-pcr-on-video-pid --new-audio-pes --hdmv-descriptors --vbr --vbv-len=500`, piste `insertSEI, contSPS` | les mêmes, `--start-time=524280` | **avant** : PCR sur la vidéo. **Après** : `--no-pcr-on-video-pid`, piste AVC `insertSEI, contSPS` ; `--new-audio-pes` et les descripteurs HDMV sont déjà le défaut de tsMuxeR 2.7 en Blu-ray |
| Navigation BDMV (index, MovieObject, BACKUP) | module interne | BDFix (`CBDMixInfo::createBDMovie`) | BlurayCreator | tsMuxeR, puis `index.bdmv` modifié pour démarrer le menu ; copies BACKUP vérifiées identiques |
| Menus | BD-J (879 fichiers) | HDMV (pas de jar ni de BDJO) | HDMV (idem) | BD-J (un xlet + images) |
| Signature du menu BD-J | oui : racine auto-signée « HD Cookbook » (`O=BDJCompany.56789abc`), RSA 1024 / SHA-1, validité an 1 → 9999, `SIG-BD00` | sans objet | sans objet | **avant** : aucune. **Après** : même profil que DVDFab, avec l'organisation de BurnSmith (`42535430`) et des clés neuves à chaque disque. **[FAIT]** § 2 |
| Image ISO | ImgBurn 2.5.8.0 intégré | dvd_iso_creator + libudf | mkisofs 2.01.01a76 | IMAPI2FS (Windows) en UDF 2.50 pour le Blu-ray ; UDF 1.02 écrit par BurnSmith pour le DVD |
| Anatomie UDF de l'ISO Blu-ray | mesurée : BEA01/NSR03/TEA01 secteurs 16-18, ancre 256, VDS 32, partition 288, UDF 2.50 | ? | ? | **[FAIT]** identique : BEA01/NSR03/TEA01, ancre 256 et dernier secteur, VDS 32, partition 288, UDF 2.50 avec partition de métadonnées. L'étiquette est celle demandée (chez DVDFab, c'est le nom du dossier) |
| Gravure | moteur propre (SPTI, OPC, vitesse, tampon) | StarBurn (+ VSO, ImgBurn, growisofs) | IMAPI2 | ImgBurn en ligne de commande, IMAPI2 en secours. **[NON TESTÉ]** |
| Vérification après gravure | `VerifyBDBurnData` | StarBurn `Verify*` (appel non prouvé) | aucune | ImgBurn `/VERIFY YES` (IMAPI2 : relecture). **[NON TESTÉ]** |
| Test avant gravure | ? | ? | ? | structure du dossier et de l'ISO ; **ajouté** : signature du menu, taille face au support |
| Adaptation du débit au disque | ? | ? | ? | Blu-ray, « Adapter au disque » : la source est gardée si elle tient, sinon refaite au débit qui remplit le disque (marge de 6 %, plancher 2 Mb/s). DVD : toujours au débit qui remplit le disque, de 1 à 8 Mb/s. Sous le plancher, un avertissement le dit dès le départ. **[FAIT]** § 3 : 18,8 Go gardés tels quels sur BD25, refaits à 5,1 Mb/s pour un BD5, saison à 7,7 Mb/s pour un BD25, DVD-5 trop petit annoncé |
| DVD-Video | interne (vobs_writer4) | dvdauthor, mplex, spumux | dvdauthor + spumux | écrit par BurnSmith (IFO, navigation, surlignage) |
| Journaux | par module | burning.log, run.log | API common_log | journal de la fenêtre, `burnsmith.log` |
| Garde-fou de durée (PTS 33 bits, 26 h 23 min) | ? | oui | ? | non : un titre de plus de 26 h n'est pas un cas réel |

## 2. La signature du menu BD-J

- **Pourquoi.** Le BDJO de BurnSmith déclare l'application 0x4000, c'est-à-dire la plage des applications signées. **[DÉDUCTION]** Sans signature, une platine qui suit la norme ne la lance pas. VLC (libbluray) retire les signatures sans les lire : il ne pouvait pas le montrer.
- **Ce qui est écrit** :
  - `CERTIFICATE\app.discroot.crt` (racine auto-signée, CA, `keyCertSign`) ;
  - `bu.discroot.crt` ;
  - `id.bdmv` (89 octets, organisation à l'octet 40, identifiant du disque au hasard) ;
  - leurs copies `CERTIFICATE\BACKUP` ;
  - dans le jar : `MANIFEST.MF`, `SIG-BD00.SF` (avec `BDJ-Signature-Version: 1.0`) et `SIG-BD00.RSA` (PKCS#7 détaché, sans attribut signé, certificat du menu puis racine).

  Le relevé champ par champ du jar et des certificats de DVDFab a servi de modèle.
- **[FAIT]** Vérifications du jar signé :
  - jarsigner (JDK 8, SHA-1 autorisé comme sur une platine, racine du disque déclarée de confiance) : « jar verified », 44 fichiers sur 44 marqués `smk` pour le film ;
  - openssl : chaîne « OK » et PKCS#7 « Verification successful » ;
  - jarsigner donne le même verdict au jar de DVDFab dans les mêmes conditions ;
  - le contrôle de BurnSmith accepte la signature de DVDFab et refuse un fichier modifié après signature ou un jar signé par une autre racine (`check-bd`, 57/57).
- **[FAIT]** Windows ne sait pas ouvrir un certificat daté de l'an 1 (celui de DVDFab non plus) : le contrôle lit donc les certificats lui-même.
- **[DÉDUCTION]** Le profil est celui que DVDFab grave sur des disques vendus et lus sur platine. **[NON TESTÉ]** L'acceptation par une vraie platine.

## 3. Disques construits et mesurés

Chaque Blu-ray passe par le même contrôle (script de vérification, résultats ci-dessous) :
- « Test avant gravure » de BurnSmith sur le dossier, puis sur l'ISO ;
- taille de l'ISO face au support ;
- anatomie UDF de l'ISO ;
- signature : jarsigner et openssl ;
- par fichier `.m2ts` :
  - PID du PCR ;
  - en-têtes H.264 des 48 premières images ;
  - tampon vidéo : 40 Mb/s dans 30 Mbit, le modèle du Blu-ray ;
  - écart entre images clés.

Les menus sont ensuite parcourus dans VLC 3.0.24 + libbluray (BD-J activé), piloté par son interface RC.

### 3.1 Avant les corrections : disque court de deux épisodes (BD25)

**[FAIT]**
- ISO de 792 788 992 octets, anatomie UDF identique à celle de DVDFab.
- PCR sur le PID 4113 (0x1011, celui de la vidéo).
- Jar non signé, `CERTIFICATE` vide.
- Le H.264 refait était déjà conforme : un délimiteur (AUD) par image, 4 tranches, HRD, niveau 4.1, 4 images de référence.

Le même disque refait après les corrections :
- PCR sur 4097 (0x1001) ;
- jarsigner « jar verified », 20 fichiers sur 20 ;
- openssl OK.

### 3.2 Film copié tel quel (BD25)

- **Source** : un Blu-ray du commerce monté en lecture seule, « La boîte à musique », 18,8 Go.
  - Vidéo H.264 High 4.1 1080p 23,976 ; 3 AC-3, 2 sous-titres PGS ; 1 h 15.
  - Son PCR est sur 4097.
- **Menu** : modèle « standard (16-9) ».

**[FAIT]**
- **Décision d'adaptation** : « la source (18,8 Go) tient telle quelle ». La vidéo est copiée, pas réencodée.
- **ISO** : 18 694 406 144 octets, soit 74,8 % de 25 Go, faite en 12 s.
- **UDF 2.50** : même anatomie que DVDFab. L'étiquette est `BOITE_MUSIQUE`.
- **Signature** :
  - jarsigner : 44 fichiers sur 44 ;
  - openssl : chaîne OK et PKCS#7 OK ;
  - `id.bdmv` avec l'organisation `42535430` ; sa copie BACKUP est identique.
- **Vidéo** :
  - PCR sur 4097, comme le disque d'origine ;
  - AUD 48/48, 192 tranches, HRD, niveau 41, 4 références ;
  - plus forte surcharge du tampon : 14,7 Mbit sur 30 ;
  - images clés espacées d'au plus 1,00 s.
- **VLC sur l'ISO** :
  - le menu démarre (xlet lancé, fichiers `SIG-BD00` présents) ;
  - pages Principale, Scènes 1 à 3 et Réglages ;
  - « Lecture » lance le film, avec la piste audio 1 et le sous-titre 0 choisis.
- **Refait avec le binaire final** (`insertSEI, contSPS` sur la vidéo copiée) :
  - construit en 2,6 min, ISO en 33 s ;
  - les mêmes mesures, le fichier vidéo ayant grossi de 117 Ko ;
  - le même parcours dans VLC.

### 3.3 Le même film réduit pour un BD5 (DVD-5, 4,2 Go utiles)

Même source, même menu, profil BD5 (adaptation au disque par défaut).

**[FAIT]**
- **Décision d'adaptation** : « 18,8 GB do not fit 4,2 GB, re-encoded at 5139 kb/s ».
- **Construction** : 19,7 min, en parallèle d'autres encodages.
- **ISO** : 4 108 976 128 octets, soit 87,4 % de 4,7 Go. Elle tient, avec 0,6 Go de marge.
- **Vidéo** :
  - moyenne mesurée de 5,13 Mb/s, pour 5,139 visés ;
  - plus forte surcharge du tampon : 3,7 Mbit sur 30 ;
  - images clés espacées d'au plus 1,00 s ;
  - AUD 48/48, 4 tranches, HRD, niveau 41 ;
  - PCR sur 4097.
  - tsMuxeR a ajouté les SEI de temps d'image et de tampon qui manquaient au flux de x264 (« insert pict timing and buffering period SEI units »), comme sur les disques du commerce.
- **Signature** : 44/44 avec jarsigner, openssl OK.
- **VLC** :
  - pages Principale puis Scènes 1 ;
  - lecture depuis la scène 1 (`play playlist 1 mark 0`) ;
  - piste audio 1 et sous-titre 0 choisis ;
  - image propre.

### 3.4 Saison entière (10 épisodes) sur BD25

- **Source** : « Cobra Kai » saison 1, dix fichiers mkv de 24 à 38 min, 5 h 06 en tout.
  - Vidéo HEVC 1080p 23,976 ; trois pistes E-AC-3 5.1 (français, anglais, français audiodescription) ; sous-titres texte.
- **Menu** : modèle « Streaming », titre « Cobra Kai Saison 1 ».

**[FAIT]**
- **Décision d'adaptation** : « the video must be re-encoded, at 7662 kb/s to fill 23,3 GB at most ».
  - Le HEVC ne va pas sur un Blu-ray 1080p : il est refait en H.264.
  - L'E-AC-3 est refait en AC-3 5.1 à 640 kb/s, le format que toute platine décode.
- **Construction** : 1 h, sans erreur.
  - Avertissement attendu : « --sub 5 » n'existe pas dans cinq épisodes ; ceux-là n'ont que le français.
- **ISO** : 22 270 312 448 octets, soit 89,1 % de 25 Go. Épisodes de 1,76 à 2,74 Go. Faite en 12 s.
- **Signature** : 36/36 avec jarsigner, openssl OK.
- **Les dix épisodes** :
  - PCR sur 4097 ;
  - AUD 48/48, 4 tranches, HRD, niveau 41 ;
  - plus forte surcharge du tampon : 6,1 à 7,9 Mbit sur 30 ;
  - images clés espacées d'au plus 1,00 s ;
  - débit vidéo moyen de 7,09 à 7,25 Mb/s, pour 7,66 visés. Le disque reste 1 Go sous la cible : la marge est du côté sûr.
  - Comparaison de SEI entre ce flux (sans `insertSEI`) et le film BD5 (avec) : 2 SEI de tampon et 30 de temps d'image sur 30 images dans les deux. x264 les écrit déjà, et tsMuxeR n'en ajoute pas en double.
- **Menu dans VLC** :
  - pages Principale → épisodes 5-8 → épisodes 9-10 ;
  - lecture de l'épisode 10 (`play playlist 10`). La capture est noire, mais l'épisode commence par 8 s de noir (luminance moyenne 16,5 puis 90 à 12 s).
  - Page Réglages : audio 2 et sous-titre 1 choisis, retour, puis « Tout lire » → `play playlist 1`, `audio stream 2 selected`, `subtitle stream 1 selected`.
- **Page principale** :
  - titre ;
  - « 10 Episodes | 3 Audio | 2 Subtitles » ;
  - quatre vignettes par page ;
  - boutons « Play all » et « Settings ».
- **Défaut vu sur ce disque, corrigé ensuite** : la page Réglages montrait deux « Français 5.1 ». Le titre de la piste (« French (Audio-Description) ») était remplacé par le nom de la langue.
  - Corrigé, et vérifié sur un disque court refait avec le binaire final : « Français (Audio-Description) 5.1 », et « Français (forcés) » pour les sous-titres forcés.

### 3.5 DVD court (modèle 01movie)

Deux extraits de 1 min 30, trois pistes audio et un sous-titre.

**[FAIT]**
- **ISO UDF 1.02** : 156 968 960 octets, contrôle OK.
- **VLC** :
  - le menu s'affiche ;
  - « Play » lance l'épisode 1.
- **Titre « Cobra Kai Saison 1 »** : sa zone fait 240 × 70 px pour une police de 48.
  - Au premier essai, il s'affichait « Cobra... ».
  - Après une première correction (réduction jusqu'à 60 %), il s'affichait « Cobra Kai Sai… ».
  - Il s'affiche maintenant en entier, sur deux lignes, quand la zone est assez haute.

### 3.6 Saison entière sur DVD-5 (adaptation impossible)

- **Contenu** : dix épisodes, trois langues.
- **[FAIT]** BurnSmith annonce dès le départ :
  - « about 5,4 GB, more than the 4,2 GB of the disc: keep fewer audio tracks or take a bigger disc » ;
  - « movie: NTSC 1000 kb/s ».
- **Pourquoi** : la vidéo est au plancher de 1 Mb/s, et les trois pistes AC-3 à 448 kb/s pèsent à elles seules 1,3 Mb/s.
- Avant cette correction, rien ne le disait avant la fin de l'encodage (le Blu-ray, lui, prévenait déjà).
- Le test avant gravure refuse un tel disque.
- La construction a été arrêtée après le message.

### 3.7 Contrôles automatiques

**[FAIT]** Avec le binaire final :
- `check-edit` : 92/92, dont le nouveau contrôle des noms de pistes ;
- `check-elements` : 85/85, dont la page Réglages de 01movie ;
- `check-templates` : 144 modèles sans problème. Chacun est construit en film (9 chapitres) et en série (14 épisodes), pages dessinées comprises ; c'est là que passe le dessin des textes.

`BurnSmith.Cli check-bd --no-dvd` : 57 contrôles sur 57 réussis. Ils couvrent notamment :
- la copie d'un H.264 avec les nouvelles options de tsMuxeR ;
- un menu signé accepté ;
- un menu signé par une autre racine refusé ;
- un fichier modifié après signature refusé ;
- un nom de fichier accentué plus long qu'une ligne de manifeste (72 octets).

## 4. Ce qui reste non prouvé

- **Gravure et platine.** Aucun disque n'a été gravé ni lu sur un lecteur de salon ou une console. C'est le seul test qui dira si une platine lance le menu signé.
- **BD5 / BD9 sur DVD gravable.** **[HYPOTHÈSE]** Certaines platines lisent un dossier BDMV sur DVD comme de l'AVCHD, format sans BD-J.
  - Sur une telle platine, le film BD5 pourrait se lire sans que le menu démarre. Cela ne prouverait rien contre un BD25.
  - Le test qui tranche est alors un BD-RE.
- **Débit des pistes sans perte.** Pour l'audio copié, la taille vient désormais du débit moyen que mkvmerge inscrit (`BPS`). Faute de ce débit, BurnSmith garde une estimation fixe (DTS 1536 kb/s, TrueHD 3500 kb/s), qui peut sous-estimer un DTS-HD MA. Le contrôle de taille empêche alors la gravure au lieu de rater le disque, mais ne relance pas l'encodage plus bas.
- **Noms des épisodes.** Le menu reprend le nom du fichier (« Cobra.Kai.S01E01.Le.roi… »), comme le titre du menu et l'étiquette du disque proposés par défaut. Il se corrige dans l'éditeur de menu (« Nom de la vidéo N »). Rien ne nettoie encore automatiquement un nom de fichier de ce genre.
- **Textes du modèle en anglais.** « Play all », « Settings », « Episodes », « Back », « Off » viennent du modèle et se changent dans l'éditeur. Les noms de langues, eux, sont dans la langue elle-même (« Français », « English »).
- **Sous-titres d'une série. [DÉDUCTION]** La page Réglages choisit une piste par son numéro, le même pour tous les épisodes.
  - Une piste de sous-titres vide dans la partie gardée d'un épisode est retirée de cet épisode, car tsMuxeR refuse un fichier vide.
  - Les pistes suivantes changent alors de numéro pour cet épisode seulement : le choix du menu peut y tomber sur une autre piste.
  - Vu sur des extraits de 1 min 30, pas sur la saison entière, où les pistes gardées avaient toutes du texte.
- **Saison sur DVD-5.** Avec trois pistes audio, même 1 Mb/s de vidéo ne suffit pas (5,4 Go pour 4,2). BurnSmith ne réduit pas l'audio de lui-même : il faut garder moins de pistes, ou prendre un DVD-9 ou un Blu-ray.
- **Non essayés aujourd'hui** : le Blu-ray 4K (UHD), le diaporama, la gravure en elle-même (ImgBurn et IMAPI2) et le DVD complet en VLC au-delà du menu et du premier titre.

## 5. Pour la suite

1. **Graver le film BD5 sur un DVD-RW** et l'essayer sur la platine du salon : le menu signé démarre-t-il, le choix de scène et la lecture fonctionnent-ils ? Il fait 4,11 Go et un DVD-RW est réinscriptible. Voir la réserve AVCHD plus haut.
2. **Si le menu ne démarre pas sur le DVD**, graver la saison (22,27 Go) sur un BD-RE pour trancher entre « BD-J sur DVD » et « signature ».
3. **Facultatif** : ouvrir les ISO dans un second lecteur logiciel qui exécute le BD-J (PowerDVD est installé sur ce PC). C'est à lancer à la main.
