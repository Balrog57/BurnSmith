# Documentation de BurnSmith

Présentation courte : [README](../README.md).

## Pour utiliser et compiler

| Document | Contenu |
|---|---|
| [FONCTIONNALITES.md](FONCTIONNALITES.md) | Tout ce que fait BurnSmith, en détail, et ce qui reste à faire |
| [MODELES.md](MODELES.md) | Modèles de menu : trois provenances, où ils sont sur le disque (`Documents\BurnSmith\Templates`), bibliothèque de l'éditeur, état de chaque famille |
| [COMPILER-ET-LIGNE-DE-COMMANDE.md](COMPILER-ET-LIGNE-DE-COMMANDE.md) | Installateur et mise à jour automatique, copie portable, emplacement des fichiers, compilation et publication, ligne de commande, vérifications, régénération des captures |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Licences des programmes et des modèles embarqués (copié dans l'exe publié) |

## Notes de conception et d'analyse

| Document | Contenu |
|---|---|
| [FEUILLE-DE-ROUTE-BLURAY.md](FEUILLE-DE-ROUTE-BLURAY.md) | Fonctions des créateurs Blu-ray étudiés, briques libres, ordre des phases |
| [COMPARAISON-DVDFAB-BLURAY.md](COMPARAISON-DVDFAB-BLURAY.md) | Comparaison des moteurs DVDFab, Yuhan et Leawo avec BurnSmith (analyse statique) |
| [ANALYSE-REA.md](ANALYSE-REA.md) | Constats de décompilation sur les trois suites, et ce que BurnSmith en fait |
| [TACHES-CLAUDE-CODE.md](TACHES-CLAUDE-CODE.md) | File d'implémentation : ce qui est fait, ce qui reste |

À côté du code : [menus HDMV (module `Hdmv`)](../src/BurnSmith.Core/Hdmv/README.md).

Les captures du [README](../README.md) sont dans [`images/`](images/) ; la façon de les refaire est décrite dans [COMPILER-ET-LIGNE-DE-COMMANDE.md](COMPILER-ET-LIGNE-DE-COMMANDE.md).
