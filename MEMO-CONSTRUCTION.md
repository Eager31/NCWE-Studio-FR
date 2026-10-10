# Mémo construction NCWE : erreurs à ne pas refaire

À relire avant chaque construction de lieu (restaurant, boutique, appartement…) via l'API / MCP de NCWE.

## Avant de poser quoi que ce soit
- **Faire le plan d'abord** et le faire valider (vue de dessus, zones, props choisis), avec le **référentiel visuel** de l'utilisateur.
- **Vérifier que le projet est enregistré** (`studio_status` → `dirty`) ; ne jamais fermer/relancer NCWE si `dirty = true`.
- **Attendre le chargement de la zone** (`wait_for_streaming`) : sinon les rayons touchent des maquettes lointaines (`ProxyMesh`) et les mesures sont fausses.
- **Mesurer l'espace** (`measure_space`, `raycast`) : sol, plafond, largeur, ouvertures. Un bâtiment peut être **creux** derrière sa façade → il faut alors construire la salle (`build_room`).

## Chemins et modèles
- **Toujours prendre les chemins exacts** avec `search_assets` / `browse_assets` (certains modèles sont sous `ep1\…`, d'autres sous `base\…`). Un chemin faux fait refuser **toute** la commande.
- **Mesurer chaque modèle** avec `asset_info` (taille, origine min/max, apparences) avant de le placer.
- **Les apparences donnent les couleurs** : ex. `bar_counter_03_c` → `lime` (néon vert), `bar_sofa_01_*` → `black_no_emissive` (cuir noir), lettres `konpeki_letters_*` → `neon_yellow`.
- `preview_asset` ne montre que la forme (pas les matériaux) : ne pas s'en servir pour juger les couleurs.

## Échelle (astuce)
- **Les objets peuvent changer d'échelle** : `scale` (un nombre, ou `[x, y, z]`) dans `place_objects`.
  - Lettres d'enseigne trop petites → `scale: 3`.
  - Tuiles de sol ajustées à la largeur exacte → `scale: [largeur/longueur_tuile, 1, 1]`.
  - Arbres / meubles trop grands pour la hauteur sous plafond → réduire (ex. `0.85`).
- Vérifier la hauteur après mise à l'échelle (arbre `joshua_med` = 5,78 m pour un plafond de 6 m : limite).

## Commandes
- Passer par les **outils MCP** (`build_room`, `place_objects`, `asset_info`…) : les noms internes du pipe sont différents (`api.build_room` n'existe pas).
- `build_room` : toujours `dry_run: true` d'abord. Il n'accepte pas d'apparence → poser le sol à part avec `place_objects` (ex. marbre `marble_dark`).
- **`delete` n'a PAS de mode simulation** (`dry_run` ignoré) : ne jamais l'utiliser pour tester. `clear_area` a un vrai `dry_run`.
- Construire **par étapes** (salle → bar → tables → déco), une capture (`screenshot`) après chaque étape, chaque étape = un Ctrl+Z.

## PowerShell (scripts qui appellent NCWE)
- **Ne jamais écrire `` ` `` + lettre dans une chaîne entre guillemets doubles** : `` `b ``, `` `t ``, `` `n ``, `` `r ``, `` `a ``… sont des caractères spéciaux.
  Écrire `$R + 'bar_counter…'` (concaténation, guillemets simples) plutôt que `"$R`bar_counter…"`.
- Les nombres se formatent avec la culture française (`1213,4`) : utiliser `ToString(..., [CultureInfo]::InvariantCulture)` dans le JSON.

- Fonction PowerShell appelée avec un nombre négatif : `St -1203.85` passe une **chaîne** → typer les paramètres `[double]` ou écrire `(-1203.85)`.

## Lumières
- **La couleur des lumières se donne en RVB 0-1** : `color: [0.18, 0.91, 0.42]` (vert néon). Un `"#2EE86A"` est ignoré (lumière blanche 4000 K).
- Vérifier après coup avec `get_objects` (`properties.color`) ; corriger avec `edit_element`.

## Murs
- Murs lisses : `int_common_a_wall_h400_l300_a` (`paint_black`, 3,8 m de haut → `scale z = hauteur/3,8`). Les murs `int_mlt_office_wall_*` (arêtes) et `int_nkt_jp_apartment_a_wall_*` (rainures) ne sont pas lisses.
- Paravent minimaliste : `int_nkt_jp_apartment_a_wall_l200_a` `marble_black`, `scale [1, 0.35, 0.579]` = 2 m × 2,2 m × 8 cm.

## Motif « doubles traits » (logo I Cacciatori)
- Trait = `int_nkt_apartment_a_ceiling_light_l300_aa` (`gold`), `scale [longueur/3, 0.12, 1]`, pivot à un bout.
- Collé sur une face qui regarde **-X** : `yaw 0, pitch ±45, roll 90`. Face qui regarde **-Y** : `yaw 90, pitch ±45, roll 90`.
- Au plafond (à plat) : `yaw 0` ou `90`, sans pitch/roll, juste sous le plafond.

## Orientation
- `yaw` tourne autour de Z, 0 = face à +Y (nord). Un comptoir/mur posé le long de l'axe Y → `yaw: 90`. Vérifier sur une capture que la face « avant » regarde la salle, sinon `yaw + 180`.


## Intérieurs dans un bâtiment du jeu (leçons du commissariat de Little China, 2026-10-10)
- Les murs et dalles du **bâtiment du jeu** n'ont qu'une face (vers l'extérieur) : de l'intérieur on voit la ville au travers. Toujours construire ses propres murs intérieurs.
- `build_room` pose les murs `int_common_techpanel_a` **bonne face vers l'extérieur** : mettre `"inverser_murs": true` (flip_walls) dans chaque salle.
- Les dalles de plafond du jeu (`int_common_a_ceiling_tiles_*`) ont leur face vers le haut : `construire.ps1` les retourne (roll 180) automatiquement (`"inverser_plafond"`, vrai par défaut). Ce sont des grilles sombres : prévoir des dalles lumineuses `..._ceiling_tiles_a_light_...` ou des lampes.
- Salles voisines : laisser **0,3 m** entre deux salles (sinon deux murs superposés scintillent) et aligner les portes des deux côtés (même position, même largeur).
- Mesurer avant : carte des hauteurs par rayons (dalles existantes, rampes), hauteur sous toit, et position exacte des ouvertures (rayons horizontaux).
- Ne jamais ouvrir l'édition de maillage (`api.meshedit.begin`) sur les objets juste pour lire : chaque ouverture ajoute une étape « Edit mesh » dans l'historique. Le détrompeur lit l'orientation par rayon (normale).
- Annuler plusieurs étapes à soi : lire `api.status.project.undo` avant chaque `undo`. Pour défaire beaucoup d'objets, une seule suppression ciblée (`api.delete` de ses propres ids) est bien plus rapide que 100 annulations.