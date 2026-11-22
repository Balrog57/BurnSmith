# Compatibilité des disques BurnSmith

Les résultats concernent les supports, lecteurs et parcours indiqués. Les essais sur ISO vérifient le décodage ou la navigation dans le lecteur logiciel indiqué. La relecture ImgBurn vérifie que le support correspond à l’image.

## Essais sur platine

| Référence | Version | Support et lecteur | Image et menu | Résultat observé |
| --- | --- | --- | --- | --- |
| DVD de série avec bonus | Non précisée | DVD-R, platine de salon dont le modèle n’est pas précisé | DVD-Video, menu animé | Menus, touches Menu/Titre/Pop-up, deux langues et quatre sous-titres. |
| *Bright* | Non précisée | BD-RE, Panasonic | Blu-ray 1080p SDR, BD-J | Lecture et chapitres. |
| Lot *Bright*, *Axel F*, *Cobra Kai* S1–S6 et *The Originals* S5 | Non précisée | Panasonic 820 et 9000 | Dix images de référence | Menus validés pour le lot. Le détail par image et par modèle reste à consigner. |
| Mire Blu-ray HD, BD-J | Non précisée | BD-RE, Panasonic, 55 040 secteurs relus | 1080p, menu animé | Pop-up et reprise pendant la lecture. |
| Mire Blu-ray HD, HDMV | Non précisée | BD-RE, Panasonic, 57 056 secteurs relus | 1080p SDR, menu HDMV | Disque et menu lisibles. |
| Mire UHD HDR10 | Non précisée | BD-RE, Panasonic, 8 608 secteurs relus | UHD sans menu | Démarrage et lecture HDR de la mire de neuf secondes. |
| Mire UHD HDR10 HDMV | Non précisée | BD-RE, Panasonic, 11 968 secteurs relus | Menu SDR, film HDR10 | Menu visible et passage au HDR au lancement du film. |
| Mire UHD HDR10 BD-J | Validation intégrée à la 1.0.35 | BD-RE, Panasonic, 8 800 secteurs relus | BD-J profil 6 avec navigation BurnSmith | Démarrage du menu en SDR, lecture HDR et retour automatique au menu en SDR. Des variantes antérieures restaient bloquées au démarrage. |
| Lot UHD HDR10 BD25 avec pop-up | Non précisée | BD-RE gravé à 2×, Panasonic, 13 952 secteurs relus | Fond animé, deux pistes audio, un sous-titre | Ouverture du pop-up en HDR, reprise et retour au menu. |
| Mire Dolby Vision et Atmos | Non précisée | BD-RE gravé à 2×, Panasonic, 44 192 secteurs relus | Dolby Vision 8.1 converti en 7 MEL, TrueHD Atmos | Affichage Dolby Vision et effets audio observés. Flux TrueHD Atmos identifié avec FFprobe. |
| Extrait *Blade Runner 2049* | Non précisée | BD-RE gravé à 2×, Panasonic 9000, Denon 3800, LG OLED C2 et C5, 76 384 secteurs relus | Dolby Vision 7 MEL, BD-J, VO TrueHD Atmos 7.1 et VF DTS-HD MA 5.1 | Les deux flux audio ont été validés en passant par le pop-up. |

## Essais sur ISO

| Version et logiciel | Sortie testée | Résultat observé |
| --- | --- | --- |
| 1.0.36, VLC | UHD HDR10 et Dolby Vision avec BD-J | Lecture des deux ISO. Pop-up ouvert avec une touche de remplacement. Sous-titres du film et du bonus activés puis désactivés en HDR10. Changement TrueHD Atmos vers DTS-HD MA avec sortie PCM stéréo audible dans les deux cas. |
| 1.0.36, PowerDVD 24 | UHD HDR10 et Dolby Vision, témoins 1080p HDMV et BD-J | UHD refusé comme format non pris en charge. Écran noir sur les deux témoins 1080p. |
| Candidat du 19 novembre, VLC 3.0.24 | DVD PAL et NTSC, Blu-ray 1080p HDMV et BD-J, UHD HDR10 BD-J | « Tout lire » enchaîne les épisodes et retourne au menu. Bonus accessible séparément et repli audio japonais contrôlé. |
| Candidat du 19 novembre, VLC et FFprobe | UHD HDR10+ sans menu | Démarrage et décodage dans VLC. Métadonnées dynamiques détectées dans le flux. |
| 1.0.37, inspection des flux | Dolby Digital Plus autonome à 48 kHz | Copie vérifiée avec les outils du moteur. Lecture sur platine à qualifier. |

Le build de travail du 22 novembre a également produit deux ISO BD-J numérotées pour une saison synthétique. Le même jour, le journal libbluray d’une autre fabrication ouverte en dossier dans VLC a confirmé le démarrage automatique après une seconde, le passage du premier titre au deuxième et le retour au menu après le deuxième. Ces essais de développement ne comprennent aucune nouvelle gravure.

La reprise de lecture BD-J a été essayée sur un dossier Blu-ray avec deux processus VLC distincts et un dossier de stockage propre à l’essai. À la deuxième ouverture, le journal libbluray a confirmé le saut à 9,002 secondes. La reprise sur platine reste à vérifier.

Restent à qualifier sur matériel (BD-RE / platine de salon) :
- la reprise de lecture BD-J entre sessions (stockage persistant du lecteur) ;
- le rendu du fondu de page BD-J ;
- le pop-up HDMV natif et le fond animé HDMV ;
- la calibration HDR des graphismes/sous-titres (203 nits ITU-R BT.2408) sur téléviseur HDR ;
- le rendu HDR10+, le Dolby Vision profil 7 natif copié, le Dolby Digital Plus autonome, les lectures longues, la sortie AVCHD sur clé USB/carte SD, et d'autres appareils que les Panasonic documentés.

Les essais détaillés et les variantes écartées figurent dans le [plan de production](PLAN-PRODUCTION.md#historique-des-essais-matériels).
