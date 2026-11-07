# Modèles de menu et bibliothèque

Trois provenances, gardées séparées :

- **Livrés dans l'exe (libres)** : les modèles libres. Cinq d'entre eux ne fournissent que des paramètres — grille, couleurs, police, marges — le dessin est celui de BurnSmith ; les autres sont livrés tels quels en fichiers `.dvdt` et en images, et convertis à l'exécution : positions mises à l'échelle, 4:3 centré, texte réduit à sa boîte. Ils sont dépaquetés une fois, et de nouveau quand leur contenu change, dans `Documents\BurnSmith\Templates\open`. Quelques-uns n'ont pas de page de scènes ou pas de miniatures : BurnSmith leur ajoute sa propre page de scènes (grille 3 x 2) sur leur fond, avec leur police et leur couleur. Ce sont des menus **DVD** : en Blu-ray, BurnSmith garde leurs fonds, couleurs et boutons mais redessine la page selon son propre moteur.
- **Images et mises en page portées par l'exe publié** : 90 modèles recueillis sur le PC de publication, avec leurs images (pas dans le dépôt ; aucun code, aucune interface, aucune police, aucun programme). Ils sont dépaquetés une fois, et de nouveau quand leur contenu change, dans `Documents\BurnSmith\Templates\carried`. Un modèle installé sur le PC, ou un dossier ajouté à la main dans `%APPDATA%\BurnSmith\template-folders.txt`, est lu sur place et remplace la copie portée du même nom. Les polices propres à un modèle ne sont pas embarquées : BurnSmith utilise alors une police de Windows.
- **Les vôtres** : « Mes modèles » (JSON dans `Documents\BurnSmith\Templates\mine`) et « Mes images » (copies dans `Documents\BurnSmith\Templates\images`) et « Mes boutons » (`Documents\BurnSmith\Templates\buttons`).

## Où sont les modèles sur le disque

Toute la bibliothèque tient dans un dossier visible, **`Documents\BurnSmith\Templates`** (le vrai dossier Documents, même si Windows ou OneDrive l'a déplacé), qu'on peut sauvegarder, copier sur un autre PC ou enrichir :

| Sous-dossier | Contenu | Quand il change |
|---|---|---|
| `open\` | modèles libres | refait quand son contenu change |
| `carried\` | images et mises en page portées par l'exe publié | refait quand son contenu change |
| `mine\` | « Mes modèles » (JSON) | **jamais effacé**, même à la désinstallation |
| `images\` | « Mes images » | **jamais effacé**, même à la désinstallation |
| `buttons\` | « Mes boutons » (`nom.png`, et `nom.sub.png` pour l'état choisi) | **jamais effacé**, même à la désinstallation |
| `converted\` | tous les modèles réunis dans le format unique (`template.json` + images PNG) | créé par « Convertir tous les modèles… » ; un pack existant n'est jamais écrasé |

N'ajoutez donc pas vos propres fichiers dans `open\` ni `carried\`. Si Documents n'est pas inscriptible, la bibliothèque est créée sous le dossier de données de BurnSmith. Les anciens emplacements (`BurnSmith-data\open`, `BurnSmith-data\templates`, `%APPDATA%\BurnSmith\templates`, `%APPDATA%\BurnSmith\library\backgrounds` et `%APPDATA%\BurnSmith\library\buttons`) ne servent plus ; le contenu des trois derniers est copié une fois. Le fichier `%APPDATA%\BurnSmith\template-folders.txt` (dossiers de modèles ajoutés à la main) n'a pas bougé.

La **bibliothèque** de l'éditeur catalogue les fonds (les fonds libres, les fonds des modèles portés, **tous les fonds des packs convertis**, vos images, avec *Importer…*), les boutons (les 5 styles de BurnSmith : capsules, texte seul, cartes, cadre classique, verre néon ; les images de boutons des modèles et **celles des packs convertis, avec leur état sélectionné** ; « Mes boutons »), la grille des scènes (1-5 colonnes x 1-4 lignes, avec ou sans images, coins carrés), la police (celles de Windows) et les couleurs. Le bouton **« Convertir tous les modèles… »** de l'onglet *Modèles* fait la conversion en une fois ; le catalogue et les listes *Fonds* / *Boutons* se rafraîchissent ensuite. « Composer à partir de ce fond » sort d'un modèle en gardant son fond (par son chemin, sans copie). Les **éléments libres** (texte, image, cadre, bouton) se posent sur n'importe quelle page, se déplacent et se redimensionnent à la souris sur l'aperçu et se règlent aux curseurs (position, taille, police, couleurs, bordure, opacité) ; un bouton reçoit son action (« Mène à »). Pas encore dans cet éditeur : annuler / rétablir, et l'alignement magnétique.

État des modèles (144 utilisables, aucun refusé, même sans modèle tiers installé) :

- **Blu-ray et 4K** (`template.bd.xml`) : 22 modèles, avec les images et positions du modèle. Quand un modèle n'a pas de page de réglages (`none_setup`, ex. Birthday1), BurnSmith ajoute lui-même une pastille « Settings » et des panneaux sombres. Un modèle tout-en-un sans menu principal (« Streaming ») a sa première page de scènes pour menu principal (affiche, nom du volume, boutons Lecture / Scènes dans sa police) ; le bouton Accueil de cette page est celui de BurnSmith.
- **DVD** (`template.xml`) : 5 modèles. Les surbrillances DVD viennent de l'image de survol du modèle (et l'état choisi de son marqueur) ; le cadre générique à 4 couleurs ne sert que sans cette image.
- **Thèmes 16:9 et 4:3 qui livrent un `template.xml`** : 6 modèles, dessinés **aux positions du fichier** — cadre, aperçu, titre et boutons Lecture / Scènes du menu principal, cadres et miniatures de la page de scènes (vérifié : cadre à 699,307 en 752x498 et titre à 392,420 pour childhood01 16:9, exactement ce que dit le fichier). Quand le fichier fait chevaucher ses propres miniatures, ou quand il n'y en a pas, BurnSmith dessine la page lui-même. Un chevauchement du titre et du cadre imposé par le fichier est reproduit tel quel.
- **Thèmes livrés en images seules** : 27 dossiers (4:3, et les anciens modèles business, childhood, education, holiday, nature, sports, standard, wedding) dont la mise en page n'est pas fournie. BurnSmith les met en page lui-même d'après leurs images : il mesure l'ouverture transparente de chaque cadre (toujours 640x360 pour l'aperçu et 320x180 pour les miniatures, ramenés au 1920x1080), y place l'aperçu et les miniatures, centre le cadre (accroché en haut quand il a des fils), met Lecture / Scènes dessous et une grille 3 x 2 sur la page de scènes. **Les images sont celles du modèle, les positions sont celles de BurnSmith.** Les 4:3 sont centrés avec des bandes noires.
- **Thèmes à `config.xml`** : 30 thèmes « Birthday » et « Natural », une mise en page en clair avec les images 1920x1080 seulement. Menu principal : aperçu dans son cadre, titre, Lecture / Scènes / Réglages en texte ; pages de scènes : 3 cadres, noms dessous, précédent / retour / suivant. Le bouton « Bonus » se place où le thème le dit. Les réglages tiennent en deux pages, Audio puis Sous-titres, aux positions du thème, la catégorie dans le titre ; six lignes au plus, les lignes 7 et 8 du thème ne servent pas. La page de scènes peut être la liste du thème (`--list`).
- Les libellés des boutons sont dans les images du modèle (souvent en anglais) ; certains PNG du modèle peuvent être corrompus et sont alors ignorés.

## Un seul format : `template.json` + images

« Convertir tous les modèles… » (onglet *Modèles* de l'éditeur) réunit chaque modèle installé — Blu-ray, DVD, thème `config.xml`, `.dvdt`, modèle libre ou porté — dans un dossier de `Documents\BurnSmith\Templates\converted\` :

```
converted\
  Blu-ray\Birthday1\
    template.json      le nom, la catégorie, le genre, l'arbre de mise en page et la liste des images
    ...png             les fonds, aux chemins du modèle d'origine, convertis en PNG
    play\0.normal.png  les boutons, et 0.sub.png pour l'état sélectionné
    scenes\0.normal.png
    thumbnail.png      la vignette de la galerie
```

`template.json` est du JSON lisible, avec les mêmes informations que les fichiers d'origine du modèle une fois normalisées : `name`, `category`, `kind`, `width` / `height`, `design` (couleurs, police, grille, surbrillance), `assets` (une entrée par image, avec son `role` — `background`, `button`, `image` ou `thumbnail` —, son chemin, l'image de l'état sélectionné `focus`, la page `page` et sa taille), et `tree`, l'arbre de mise en page (nœud, attributs, texte, enfants). Chaque image citée par l'arbre est copiée en PNG et l'arbre est réécrit vers son nouveau chemin : le pack ne dépend plus du dossier d'origine.

Les images restent **séparées par rôle** : un fond reste un fond, un bouton garde son image normale et son image sélectionnée, les cadres et vignettes restent des images. Rien n'est aplati dans une seule photo, donc chaque élément se remplace ou se retouche individuellement.

Un pack est **modifiable** de trois façons :

- **dans l'éditeur** : les fonds du pack apparaissent dans *Fonds*, ses boutons (avec leur état sélectionné) dans *Boutons* ; on les applique à un menu, on ajoute textes, cadres et boutons, et on enregistre le résultat dans *Mes modèles* ;
- **dans les fichiers** : on remplace un PNG par le sien (même chemin), ou on change dans `template.json` un libellé, une position, une couleur, une police, une taille d'image ;
- **en repartant du modèle** : « Composer à partir de ce fond » détache le modèle pour en faire un menu entièrement réglable.

Un pack déjà présent **n'est jamais écrasé** par une nouvelle conversion : vos retouches survivent. Pour tout refaire depuis les modèles d'origine, supprimez le dossier du pack (ou lancez `convert-templates --force` en ligne de commande). Le catalogue lit les packs en premier : un pack remplace le modèle d'origine du même nom, si bien que l'application n'a plus besoin des lecteurs de formats propriétaires pour l'afficher.

En ligne de commande :

- `convert-templates [--out DOSSIER] [--force]` convertit tous les modèles trouvés ; sans `--force`, un pack existant est gardé ;
- `check-conversion` vérifie que chaque pack est lisible, que ses images sont là et qu'il a au moins un fond et un bouton ;
- `check-library` vérifie que **chaque fond et chaque bouton de chaque pack** est bien proposé par la bibliothèque de l'éditeur.
