# Brief pour Claude Code — BurnSmith : menus DVD / Blu-ray

Document à transmettre tel quel. Il décrit ce qui reste à faire, avec le contexte, les fichiers, les critères d'acceptation et les pièges déjà rencontrés. Tout ce qui est affirmé ici a été vérifié dans une session précédente ; les sections « preuve » disent comment.

---

## 0. Contexte minimal

**BurnSmith** est un logiciel libre (GPL-3.0) de création de DVD et Blu-ray avec menu, écrit en C# / .NET 10 (WPF pour l'interface, une CLI pour les tests). Dépôt :

```
C:\Users\Marc\Documents\1G1R\_Programmation\BurnSmith
```

Il construit des menus réels : sur DVD par `dvdauthor` + `spumux`, sur Blu-ray par un **xlet BD-J** écrit en Java 1.3 (source `bdj\src\org\burnsmith\menu\Menu.java`, compilé par `bdj\build.ps1` en `assets\bdj\menu.jar`), plus tsMuxeR pour le multiplexage.

Une session précédente a décompilé DVDFab 13, Yuhan Blu-ray DVD Creator et Leawo Prof. Media pour en extraire les modèles de menu. Les résultats sont dans **`docs\ANALYSE-REA.md`** (avec la méthode et les limites de chaque constat) et la feuille de route dans **`docs\FEUILLE-DE-ROUTE-BLURAY.md`**. **Lis `docs\ANALYSE-REA.md` §12 à §28 avant de coder** : c'est la source des spécifications ci-dessous.

### Conventions

- Code, commentaires, messages d'interface et noms de tests : **en français**.
- Le style existant compte : commentaires qui expliquent *pourquoi* (pas *quoi*), pas de `TODO`, pas de méthode appelée mais non écrite.
- Ne jamais copier un binaire commercial dans le dépôt. `dvdauthor`, `spumux`, tsMuxeR et le JDK 8 sont dans `tools\`, ffmpeg dans le `PATH`. tsMuxeR est redistribué dans l'exe publié, pas `dvdauthor` ni `spumux` ; l'installateur télécharge ffmpeg s'il manque.
- `analysis\` est ignoré par git (copies de binaires, disques d'essai).

### Vérifications à lancer après **chaque** changement

```powershell
dotnet build BurnSmith.slnx                                   # doit rester à 0 erreur, 0 avertissement
dotnet run --project src\BurnSmith.Cli -v q -- check-update
dotnet run --project src\BurnSmith.Cli -v q -- check-edit
dotnet run --project src\BurnSmith.Cli -v q -- check-elements
dotnet run --project src\BurnSmith.Cli -v q -- check-highlights
dotnet run --project src\BurnSmith.Cli -v q -- check-bd
dotnet run --project src\BurnSmith.Cli -v q -- check-reliability
dotnet run --project src\BurnSmith.Cli -c Release -v q -- check-templates
#   → doit afficher : 144 sans problème, 0 avec problèmes, 0 refusés
```

Pour regarder une page de menu sans graver :

```powershell
dotnet run --project src\BurnSmith.Cli -v q -- preview "Birthday 01" analysis\out\x
#   options : --list (page de scènes en liste), --bonus N, --episodes N, --element "…"
```

Pour construire un disque d'essai (une compilation de développement trouve `tools\dvdauthor` toute seule) :

```powershell
dotnet run --project src\BurnSmith.Cli -v q -- dvd --video analysis\test\film.mkv --out analysis\test\essai `
    --design "Cinema Dark" --chapter-at 0,3,6,9,12
dotnet run --project src\BurnSmith.Cli -v q -- bd --video analysis\test\film.mkv --out analysis\test\essai-bd `
    --design "Birthday 01"
```

Un film d'essai se fabrique avec ffmpeg :

```powershell
ffmpeg -y -f lavfi -i "testsrc2=size=720x576:rate=25" -f lavfi -i "sine=frequency=440:sample_rate=48000" `
       -t 15 -c:v libx264 -pix_fmt yuv420p -c:a ac3 analysis\test\film.mkv
```

---

## Tâche 1 — Page de réglages Yuhan en deux sous-pages (faite : `2847db0`, `13de658`, `df38f81`)

> Le texte ci-dessous reste la demande d'origine. Trois choses ont changé en la faisant :
> - **La catégorie va dans le titre** (`df38f81`) : plus de libellé à 850,400 ni 850,540, où il recouvrait une ligne.
> - **Une page par choix, pas par paire** (`13de658`) : `settings_audio_A` et `settings_sub_S`, car une page par paire faisait dépasser 1 Go à un menu animé.
> - **Pas de pagination** : six lignes au plus (`MaxAudio` = 6, `MaxSubtitles` = 5 plus « Désactivé »), les lignes 7 et 8 du thème ne servent pas.

### Le problème

Les 30 thèmes Yuhan (`C:\Program Files\Yuhan Blu-ray DVD Creator\PresetMenu\THEMExx\config.dat`, déchiffrés par `YuhanSkin`) décrivent leur page audio / sous-titres ainsi, dans la branche 1920×1080 :

```xml
<Setup>
  <Background FileName="bg_menu.jpg" Width="1920" Height="1080"/>
  <ASLangTitleText   Left="750" Top="150" Size="45"/>     <!-- titre de la page -->
  <SetupAudioText    Left="850" Top="400" Size="54"/>     <!-- libellé AUDIO -->
  <SetupSubtitleText Left="850" Top="540" Size="54"/>     <!-- libellé SOUS-TITRES -->
  <LangItemText>                                          <!-- 8 lignes, x = 750 -->
    <ItemText0 Left="750" Top="270" Size="36"/>
    <ItemText1 Left="750" Top="345" Size="36"/>  … ItemText7 Top="795"
  </LangItemText>
  <ButtonPre  Left="627"  Top="900" Width="64" Height="64"/>
  <Return     Left="927"  Top="900" Width="64" Height="64"/>
  <ButtonNext Left="1227" Top="900" Width="64" Height="64"/>
</Setup>
```

C'est **une liste unique de huit langues**, pas deux colonnes : les deux libellés (y = 400 et 540) tombent **sur** les lignes 2 et 4. Afficher les deux listes et les libellés en même temps est donc impossible — c'est pourquoi la reproduction a été refusée jusqu'ici (§16.2 et §28 de `docs\ANALYSE-REA.md`).

**Décision à implémenter** : deux sous-pages, une par catégorie, comme le fait un lecteur de salon (« Audio » puis « Sous-titres », avec un bouton pour passer de l'une à l'autre). C'est le modèle de Yuhan lui-même : son pipeline écrit des clips séparés `setup_audio_%2.m2ts` et `setup_subtitle_%2.m2ts` (§2 de la note).

### Spécification

1. **Deux pages** au lieu d'une, quand le modèle porte `LangItemText` :
   - `settings_audio` : titre = `ASLangTitleText`, libellé = `SetupAudioText`, jusqu'à 8 lignes aux rectangles `ItemText0..7`, chacune = une piste audio (action `audio`, `Mark` = numéro 1-based, comme `MenuAction("audio", Mark: n)`), bouton Retour = `Return`, bouton « Sous-titres » = `ButtonNext`.
   - `settings_sub` : titre = `ASLangTitleText`, libellé = `SetupSubtitleText`, mêmes huit rectangles, chaque ligne = « Désactivé » puis les sous-titres (action `sub`, `Mark` = 0 pour désactivé, n pour le n-ième), bouton Retour = `Return`, bouton « Audio » = `ButtonPre`.
   - Plus de 8 choix : **paginer** (2 pages de 8) plutôt que tronquer. Si tu ne pagines pas, dis-le explicitement dans ton compte rendu et affiche les 8 premiers — mais ne perds jamais une piste en silence.
   - Les modèles sans `LangItemText` (DVDFab, Leawo, DVDStyler, et les thèmes Yuhan qui n'en ont pas) **gardent la page actuelle** : une seule page `settings`, deux colonnes. C'est le chemin éprouvé sur 144 modèles, il ne doit pas bouger.

2. **DVD** (`src\BurnSmith.Core\Dvd.cs`) : la page de réglages y est engendrée **par état** — `SettingsId(audio, sub)` et la double boucle `for (var a …) for (var s …)`. Avec deux sous-pages, il faut, pour chaque état, les deux pages, et le bouton « Réglages » du menu principal doit entrer dans la page **audio** de l'état courant. Regarde `MenuNumber(...)`, `Enter()` et la fonction `SettingsId` : c'est là que tout se câble. Les identifiants doivent rester stables et distincts (`settings_audio_{a}_{s}`, `settings_sub_{a}_{s}` — ou une convention équivalente que tu documentes).

3. **Blu-ray / BD-J** : `MenuFiles.WriteBdj` écrit n'importe quelle page et l'xlet navigue par identifiant (`MenuAction("page", id)`), donc rien à changer côté Java **si** les identifiants sont de simples pages. Vérifie quand même le **marqueur de piste active** : l'xlet dessine un point devant le bouton dont l'identifiant est `a<audio>` ou `s<sub>` (voir `MenuFiles.WriteBdj`, ligne `marker mark.png …` et le xlet). Si tu renommes les identifiants de lignes, le marqueur ne suivra plus.

4. **Éditeur** (`src\BurnSmith.App\MenuEditorWindow.xaml.cs`) : l'aperçu remplace la page `settings` par une page à l'état courant (`MenuBuilder.WithElements(d, MenuBuilder.Settings(d, content, audio, sub), content)`). Adapte-le aux deux nouvelles pages (le sélecteur de pages de l'éditeur les affichera comme deux vignettes).

### Critères d'acceptation

- `preview "Birthday 01" analysis\out\x` produit **deux** pages de réglages (`settings_audio`, `settings_sub`) ; sur la page audio, la catégorie est dans le titre à 750,150, les lignes aux y 270…645 (pas 75 px), le Retour à 927,900.
- `check-templates` reste à **144 sans problème, 0 refusé**.
- Un DVD construit (`dvd --design "Birthday 01" --chapter-at 0,3,6,9,12`) contient les deux pages et le bouton « Réglages » y mène ; un Blu-ray construit de même ne casse pas (le journal libbluray doit montrer `page settings_audio`).
- Un modèle DVDFab (ex. `01movie`) garde **une seule** page `settings`, inchangée.

### Pièges déjà rencontrés (ne les refais pas)

- Le mode à deux pages a son propre interrupteur, `HasSetupPages` (`setup/langitems`), dont découlent les disques, l'aperçu et l'éditeur.
- `MenuBuilder.Settings` commence par `if (pack is { HasSetupLists: true }) return pack.Settings(...)`. `HasSetupLists` est vrai dès que `setup/audiolist`, `subtitlelist` **ou** `setupaudio`/`setupsubtitle` existent : c'est l'interrupteur unique lu par `Dvd.cs`, l'éditeur et `MenuLayout.MarkerOffset`. Le basculer ailleurs donnerait l'aperçu d'un côté et l'ancienne mise en page sur le disque.
- Le marqueur de piste active se positionne avec `MenuBuilder.MarkerSize` et `MarkerOffset` ; la largeur de colonne 600 a été choisie pour que le marqueur tombe juste sans y toucher.
- `MenuDesign.Clone()` doit copier les éléments libres (`Elements`) — ne casse pas ça en ajoutant un champ.

---

## Tâche 2 — Prouver la reprise au dernier bouton (faite : `f89af21`)

> Faite. Les flèches n'étaient pas cassées : le bouton de départ n'a simplement pas de voisin à droite. La reprise sur un autre bouton est prouvée dans le journal. Le texte ci-dessous reste la demande d'origine.

### État

Le xlet mémorise le bouton de départ dans **GPR108** (`remember()` écrit `index + 1`, `0` = rien de mémorisé) et le relit au retour au menu (`show(firstPage)` journalise `resume on button N`). C'est dans `bdj\src\org\burnsmith\menu\Menu.java`, compilé et embarqué dans les disques. Voir §19 de `docs\ANALYSE-REA.md`.

Le journal libbluray montre bien : `page root` → `play playlist 1` → `playback ended` → `page root` → `resume on button 0`. **Ce qui n'est pas prouvé**, c'est qu'on revienne sur un *autre* bouton : le banc d'essai (`tools\vlctest.ps1`) envoie `key-nav-activate` (qui marche) mais `key-nav-right` / `key-nav-down` ne déplacent pas le focus, alors que le `menu.txt` du disque donne bien les voisins.

### Ce qu'il faut faire

1. Trouver pourquoi les flèches n'arrivent pas : noms de touches de l'interface RC de VLC, traduction libbluray, ou focus de la fenêtre. `tools\vlctest.ps1` accepte `-Keys` et écrit `libbluray.log` / `vlc.log`.
2. Une fois les flèches fonctionnelles : construire un disque dont la page racine porte **plusieurs boutons de lecture** (modèle « Streaming », deux vidéos — le `menu.txt` montre alors `e1` et `e2` avec leurs voisins), activer le second, attendre la fin du film, et lire `resume on button 1` dans le journal.

### Critère d'acceptation

Le journal contient `resume on button 1` (ou l'indice du bouton réellement activé), et le bouton remis en surbrillance est bien celui-là.

---

## Tâche 3 — Menu HDMV natif (gros morceau, à ne commencer qu'avec un accord)

Le menu Blu-ray actuel est un **xlet BD-J** non signé : ça fonctionne dans libbluray, mais c'est un risque de compatibilité sur lecteurs de salon. DVDFab, Yuhan et Leawo écrivent un menu **HDMV** (IG + MovieObject) pour cette raison.

Le dépôt contient déjà de quoi le faire : `src\BurnSmith.Core\Hdmv\` (`IgStream.cs`, `HdmvAuthor.cs`, `HdmvNav.cs`, `ClipFiles.cs`, `MenuClip.cs`, `TransportStream.cs`, `Pgs.cs`) et son vérificateur `BurnSmith.HdmvCheck`. La feuille de route (`docs\FEUILLE-DE-ROUTE-BLURAY.md` §3 à §5, et ses §4c/§4d pour le décodage des menus de Leawo et de Yuhan) décrit la structure attendue : un IG par page, `ui_model` 1 pour le menu contextuel, sous-chemin de type 3, clip IG impair.

**Prérequis honnête** : la phase 0 de la feuille de route — graver un disque et le lire sur un lecteur — n'est pas faite. Écrire un second moteur de menu sans savoir si le BD-J échoue est du travail à l'aveugle. Ne commence cette tâche que si on te le demande explicitement.

---

## Tâche 4 — Petites finitions (au choix, une par commit)

- **`--animated-thumbs` sur Blu-ray** : sans effet aujourd'hui, accepté puis ignoré (le xlet ne joue pas plusieurs vidéos). Si tu as une idée propre (par exemple une seule vidéo composée à l'avance, une par page de scènes), propose-la avant de coder.
- **Page Bonus paramétrable** (nombre d'éléments par page, mode d'affichage) : les thèmes Yuhan portent `BonusText`, DVDFab a un mode « bonus » dans ses modèles.
- **Sons de boutons** (`sound.bdmv`) : DVDFab en a, BurnSmith non.
- **`check-menus`** : une commande qui inspecte un disque **construit** (dossier `BDMV`) et vérifie `menu.txt` (pages, voisins symétriques, toutes les pages atteignables, `mark.png` présent), la présence du jar et, pour un DVD, le nombre de boutons des IFO. Utile pour attraper les régressions de navigation.

---

## Règles de travail

- **Un commit par tâche**, message en français, impératif, avec la preuve dans le corps (ce qui a été mesuré, pas seulement « fait »).
- **Ne touche pas** aux fichiers d'analyse (`docs\ANALYSE-REA.md`, `docs\FEUILLE-DE-ROUTE-BLURAY.md`, `docs\TACHES-CLAUDE-CODE.md`) sans le dire : ils sont la trace des preuves.
- **Ne casse pas les 144 modèles** : `check-templates` est le garde-fou. Un changement de mise en page qui fait tomber ce compte est un changement refusé, même s'il améliore un modèle.
- **Une affirmation sans mesure n'est pas une preuve** : dans cette session, deux « corrections » se sont révélées fausses à la vérification (des vignettes 4 à 10 déjà gérées, et une comparaison de taille de PNG qui échouait alors que le code marchait). Vérifie avant d'affirmer, et dis ce qui n'a pas pu être vérifié.

## Où trouver les spécifications décompilées

| Sujet | Section de `docs\ANALYSE-REA.md` |
|---|---|
| Modèle de menu DVDFab (QML, énumérations, `template.bd.xml`) | §12 |
| Schéma complet `AuthorData.xml`, registres GPR/PSR, types de bouton | §12.4, §18 |
| Thèmes Yuhan déchiffrés (schéma des 30) | §13 |
| Exécutable Yuhan (lecture de `config.dat`, éditeur) | §16 |
| Pipeline DVD de Leawo (`Img2m2v` → `mplex` → `spumux`), script dvdauthor | §23, §24 |
| Surbrillances DVD (survol + état choisi) | §6, §6 bis, §11.5 |
| Trois styles de surbrillance, couleur et transparence | §17, §21 |
| Page de scènes en liste | §15.2, §22.1 |
| Vignettes animées | §27 |
| Reprise au dernier bouton | §19, §22.2 |
| Page de réglages Yuhan (le problème à résoudre) | §16.2, §28 |
