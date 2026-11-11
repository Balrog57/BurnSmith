---
name: burnsmith
description: Pilote BurnSmith en ligne de commande, sans sa fenêtre - fabriquer un Blu-ray, un Blu-ray 4K ou un DVD avec menu à partir de vidéos, choisir et prévisualiser un modèle de menu, régler pistes audio / sous-titres / chapitres / coupes, créer l'image ISO, graver, effacer, contrôler un disque fini. À utiliser dès qu'on demande de créer, graver, vérifier un disque ou de manipuler les modèles de menu de BurnSmith.
---

# BurnSmith en ligne de commande

Tout ce que fait la fenêtre de BurnSmith se fait aussi ici. Le programme est `BurnSmith.Cli.exe` (écrit `burnsmith` plus bas). 

- **BurnSmith installé** (depuis la version 1.0.19) : l'exe est à côté de l'application, `C:\Program Files\BurnSmith\BurnSmith.Cli.exe` sauf autre dossier choisi à l'installation. Rien à compiler ni à télécharger. Il se sert des outils et des modèles que l'application déballe à son premier lancement : si une commande répond « introuvable » ou si `templates` ne liste que quelques modèles, demander à l'utilisateur d'ouvrir BurnSmith une fois. Ce fichier est installé dans `C:\Program Files\BurnSmith\skill\burnsmith\SKILL.md` ; on le donne à son IA en copiant le dossier `burnsmith` dans son dossier de skills (`%USERPROFILE%\.claude\skills\` pour Claude).
- **Depuis le dépôt** : `dotnet build src/BurnSmith.Cli -c Release` donne `src/BurnSmith.Cli/bin/Release/net10.0-windows/BurnSmith.Cli.exe`. Il lui faut `ffmpeg`, `ffprobe` et `tsMuxeR` dans `tools\` ou sur le `PATH`. `docs/COMPILER-ET-LIGNE-DE-COMMANDE.md` et les commentaires en tête de `src/BurnSmith.Cli/Program.cs` détaillent chaque option.

Sans argument, l'exe affiche son usage et sort avec 2.

## Règles

- Un build dure de quelques minutes à plusieurs heures : le lancer en arrière-plan, jamais deux à la fois vers le même dossier `--out`.
- `--out DOSSIER` est remplacé, et `DOSSIER_work` à côté est effacé : ne jamais viser un dossier qui contient autre chose.
- Graver et effacer sont irréversibles : demander confirmation à l'utilisateur avant `burn` et `erase`, en nommant le lecteur.
- Code de sortie : 0 = réussi, 1 = le disque fini a un problème (lisible dans la sortie), 2 = commande mal écrite. Une exception = échec.
- Les options écrites après un `--video` (jusqu'au suivant) sont celles de cette vidéo. Avec une seule vidéo, l'ordre est libre.
- Les nombres décimaux s'écrivent avec un point (`--fps 23.976`, `--capacity 7.6`).

## Déroulé habituel

1. **Lire la vidéo** : `burnsmith probe film.mkv` donne en JSON la vidéo et ses pistes. `Index` est le numéro que prennent `--audio` et `--sub`.
2. **Choisir un modèle** : `burnsmith templates` les liste (`OK Catégorie | Nom`). `--design` prend le nom, un dossier de modèle, ou un `.json` de modèle enregistré.
3. **Regarder le menu avant d'encoder** : ajouter `--preview` à la commande `bd` / `dvd` écrit seulement les pages en JPEG dans `--out` (`menu_root.jpg`…). Les lire avec l'outil de lecture d'images pour vérifier.
4. **Fabriquer le disque** : `burnsmith bd …` ou `burnsmith dvd …`. La commande contrôle le dossier à la fin.
5. **Image ISO** : `burnsmith iso DOSSIER sortie.iso --label NOM` (ajouter `--dvd` pour un DVD).
6. **Contrôler** : `burnsmith check-disc sortie.iso DOSSIER`.
7. **Graver** (après accord) : `burnsmith drives` puis `burnsmith burn sortie.iso F: [--speed 4]`. La gravure relit le disque.

## Fabriquer un disque

```bash
burnsmith bd --video film.mkv --audio 0,1 --sub 0 --default-audio 0 --out D:\Disques\Film --design "Birthday1" --title "Mon film" --label MON_FILM
```

```bash
burnsmith dvd --video ep1.mkv --name "Épisode 1" --video ep2.mkv --name "Épisode 2" --out D:\Disques\Serie --pal
```

| But | Options |
|---|---|
| Type de disque | `bd` (Blu-ray), `bd --uhd` (4K), `dvd` |
| Taille | bd : `--profile BD25\|BD50\|BD9\|BD5` (ajouter ` 720p`), `--capacity GO` (taille utilisable : 46.5 = BD-50, 7.6 = DVD-9) |
| Qualité | bd : `--quality fit` (défaut, « Adapter au disque ») `\|source\|15\|20\|30\|KBPS` ; `--reencode --crf N --preset NOM` ; dvd : `--dvd-kbps N` ; `--cpu` (sans carte NVIDIA) ; `--fps R` |
| DVD | `--pal` \| `--ntsc`, `--4x3` |
| Pistes (par vidéo) | `--audio N,N`, `--sub N,N` \| `--sub none`, `--default-audio N`, `--default-sub N`, `--track-lang a0=fre,s1=eng`, `--sub-file fra=film.srt` (srt, ass, vtt, sup, idx) |
| Coupe et son (par vidéo) | `--trim 90-1:30:00` (ou `A-B,C-D`), `--volume 120` |
| Chapitres | `--chapters MIN` (disque), par vidéo : `--chapter-every MIN`, `--chapter-at 0,300,725`, `--no-chapters` |
| Rôle d'une vidéo | `--bonus` (page Bonus), `--intro` (lue au démarrage, avant le menu) |
| Au menu (par vidéo) | `--name TEXTE`, `--overview TEXTE`, `--thumb-at 12:30` \| `--thumb image.jpg` |
| Effets (par vidéo) | `--effects reglages.json` (les réglages de l'éditeur vidéo : recadrage, filigrane, sous-titre incrusté…) |
| Lecture | `--play-mode 0\|1\|2` (menu puis tout \| premier titre puis tout \| retour au menu), `--no-rewind` |

## Le menu

| But | Options |
|---|---|
| Sans menu | `--no-menu` (une seule vidéo) |
| Textes | `--title TEXTE`, `--titles "fra=Mon disque\|eng=My disc"`, `--credits "Ligne 1\|Ligne 2"` |
| Fonds par page | `--page-bg root=fond.jpg` (répétable ; `root`, `scenes`, `settings`, `credits`, `bonus`) |
| Flou du fond | `--background-blur 12` (0 à 40 : l'image de fond floutée, pour que le texte posé dessus se lise ; le modèle `Saison` en a 12) |
| Menu animé | `--loop [--loop-file video] [--loop-start S] [--loop-seconds S\|music]` |
| Musique | `--music morceau [--music-start S] [--music-volume 80] [--music-fade 2]` |
| Présentation | `--list` (scènes en liste), `--scenes-only`, `--skip-first-scene`, `--bonus-text`, `--animated-thumbs` (DVD), `--highlight box\|underline\|textcolor` |
| Pop-up Blu-ray | `--popup [--popup-main TEXTE] [--popup-resume TEXTE]` |
| Élément libre | `--element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"` (répétable ; `kind` = `text`, `image`, `box`, `button` ; un bouton prend `target=movie\|chapter:3\|scenes1\|settings\|bonus1\|credits`) |

Pour modifier un modèle en profondeur : `burnsmith design-export NOM modele.json`, éditer le JSON, puis `--design modele.json`. `burnsmith preview NOM|modele.json dossier [--picture image.jpg] [--episodes N] [--overview "résumé"] [--title TEXTE] [--bonus N]` dessine toutes les pages d'un modèle avec un contenu d'exemple ; `burnsmith dump NOM` liste ce qui est dessiné, rectangle par rectangle.

## Préparer des vidéos

- `burnsmith slideshow sortie.mkv [--seconds 5] [--transition fondu] [--music morceau]… photo…` : un diaporama.
- `burnsmith song sortie.mkv audio [--cover image]` : un morceau avec sa pochette.
- `burnsmith join sortie.mkv a.mkv b.mkv…` : des vidéos de mêmes flux bout à bout.

Le résultat se donne ensuite à `--video`.

## Image, gravure, disque

- `burnsmith drives` : les graveurs et le disque qu'ils contiennent.
- `burnsmith iso DOSSIER sortie.iso [--label X] [--dvd]`
- `burnsmith burn image.iso F: [--speed 4]` : grave un disque vierge puis le relit.
- `burnsmith verify image.iso F:` : compare un disque à son image.
- `burnsmith erase F:` : efface un disque réinscriptible.
- `burnsmith check-disc DOSSIER|image.iso [DOSSIER]` : structure d'un disque fini, sans lecteur.

## Modèles de menu

- `burnsmith templates` : tous les modèles trouvés, et ceux refusés avec la raison.
- `burnsmith convert-templates [--out DOSSIER] [--force]` : convertit les modèles du PC au format unique (`template.json` + images) dans `Documents\BurnSmith\Templates\converted`. `--force` remplace les paquets existants, modifications de l'utilisateur comprises : demander avant.
- Contrôles, tous à 0 quand tout va bien : `check-conversion` (paquets lisibles et complets), `check-fidelity` (chaque modèle d'origine et son paquet dessinent le même menu), `check-library`, `check-templates [--matrix] [--only NOM]` (pages, boutons, télécommande ; `--matrix` dure plus d'une heure).

## Vérifier le logiciel après une modification du code

Depuis le dépôt seulement. `check-update`, `check-edit`, `check-elements`, `check-highlights`, `check-templates`, `check-fidelity`, `check-bd`, `check-reliability` (ajouter `--no-dvd` aux trois qui l'acceptent pour passer les vrais builds DVD, lents). Les lancer un par un : l'exe est verrouillé pendant qu'un contrôle tourne, donc pas de `dotnet build` en même temps.
