# Outils de construction NCWE

Ces outils travaillent avec NCWE Studio ouvert. Ils passent par `..\ncwe-mcp.cmd`.

Lancement : `powershell -ExecutionPolicy Bypass -File <outil>.ps1 …`

| Outil | Rôle |
|---|---|
| `releve.ps1 [-Position "x;y;z"]` | Relevé du site : sol, plafond, largeurs, murs, lieux du jeu et kits proches. Sans position, il utilise la caméra. |
| `kit.ps1 -Nom <nom> [-Rayon 40] [-Centre "x;y;z"] [-Lieu "nom"] [-Source game\|all\|project]` | Crée `kits\<nom>.tsv` : les modèles utilisés autour du point, par famille, avec leurs apparences et tailles, et les couleurs des lumières. |
| `construire.ps1 plans\x.json` | Liste les étapes du plan. Ne pose rien. |
| `construire.ps1 plans\x.json -Verifier 2` | Vérifie les modèles et dessine l'étape dans la vue pendant 5 min, avec une capture. Ne pose rien. |
| `construire.ps1 plans\x.json -Etape 2` (ou `1-3`) | Construit. Un Ctrl+Z annule une étape, une salle compte pour une étape de plus. Capture dans `plans\captures\`. |

Fichiers produits :

- **`modeles.tsv`** : le cache des modèles (taille, apparences). Il se remplit tout seul, et chaque modèle n'est mesuré qu'une fois.
- **`kits\*.tsv`** : modifiables à la main. On peut supprimer des lignes ou en ajouter. Colonnes : famille, chemin, nombre, apparences, taille…

## Infos de tout le jeu (`..\infos\`)

Produites une fois par `redtool infos` (à refaire après une mise à jour du jeu) :

- `tailles-modeles.tsv` : taille et origine des ~100 000 modèles. `Modeles` les lit d'abord, sans appel à NCWE.
- `effets.tsv` : chaque `.particle` / `.effect`, en boucle ou durée, taille approximative.
- `sons.tsv` : chaque événement sonore, en boucle ou ponctuel, portée, durée, étiquettes.

Recherches sans NCWE (dans `lib.ps1`) :

```powershell
Chercher-Modeles -Famille 'Assises' -Mots 'bar','stool' -HauteurMin 0.6 -HauteurMax 0.9
Ensemble 'base\environment\furniture\kitchen\kitchen_a_cabinet.mesh'   # pièces assorties du même dossier
Chercher-Effets -Mots 'steam' -Boucle $true -TailleMax 3
Chercher-Sons -Mots 'fan' -Boucle $true -PorteeMax 20
```

Régénérer : `$env:REDTOOL_GAME='<dossier du jeu>'; dotnet exec --runtimeconfig outils\redtool\redtool.runtimeconfig.json outils\redtool\redtool.exe infos <dossier NCWE> <dossier du jeu> infos` (environ 20 min).

## Format d'un plan (`plans\*.json`)

Les positions sont en **mètres locaux** :

- x vers la droite, y vers l'avant (le « nord » du plan), z vers le haut ;
- relatives à `origine` ;
- le plan entier est tourné de `cap` degrés (sens trigo, comme le yaw).

`yaw` vaut 0 quand l'objet regarde +y local.

```json
{ "nom": "arene", "kits": ["fightclub-watson"], "origine": [x, y, z], "cap": 0,
  "vue": { "de": [0,-10,3], "vers": [0,0,1], "fov": 80 },
  "etapes": [ { "nom": "Salle", "salle": {…}, "objets": [ … ], "lumieres": [ … ], "vue": {…} } ] }
```

### Modèles et objets

Le modèle `m` est soit un chemin complet, soit un **nom court** (nom du fichier sans `.mesh`) pris dans les kits du plan ou dans le cache.

Objet simple :

```json
{ "m": "bar_table_a", "p": [x,y,z], "yaw": 0, "pitch": 0, "roll": 0, "s": 1 ou [x,y,z],
  "app": "apparence", "col": true, "n": "nom", "sol": "bottom" }
```

- Avec `p: [x,y]` (sans z), l'objet est posé sur la surface en dessous.
- `col: true` ajoute une boîte de collision.

### Motifs (dans `objets`)

Chaque motif porte un `m` (objet) et/ou une `lumiere` (même format que `lumieres`) à chaque point.

| Motif | Champs |
|---|---|
| `ligne` | `de`, `a`, `nombre` ou `pas`, `suivre: true` pour tourner les objets le long de la ligne. |
| `grille` | `centre`, `lignes`, `colonnes`, `pas: [dx,dy]`. |
| `cercle` | `centre`, `rayon`, `nombre`, `de`/`a` (angles en degrés, 0 = +x), `face: centre\|exterieur\|aucun`. Sert pour les gradins, les sièges autour d'un ring ou les piliers. |
| `traits` | Motif art déco collé au mur : `face` (`-X`, `+X`, `-Y`, `+Y` = normale du mur vers la salle), `de`/`a` (segment sur la surface du mur), `z` (bas des traits), `forme: chevron\|/\|\`, `longueur` 0.75, `largeur` 0.12, `double` 0.18 (0 = simple), `pas`, `app` (gold). |
| `bande` | Filet plat : `de`, `a`, `z`, `largeur`, `app`. Sert pour les liserés au plafond (z = plafond − 0,04) ou au sol. |
| `cadre` | Rectangle de bandes : `min`, `max`, `z`, `largeur`, `double` (2ᵉ cadre décalé vers l'intérieur), `app`. |

### Lumières

```json
{ "p": [x,y,z], "type": "point|spot|area", "k": 2700 }
```

Pour la couleur, on peut remplacer `k` par `"rgb": [r,g,b]` ou par `"rgb": "#2EE86A"` : le script convertit le `#hex`.

Autres champs : `i` (lumen), `r` (portée), `ombres`, `yaw`/`pitch` (spots, pitch −90 = vers le bas), `inner_angle`, `outer_angle`.

### Salle (`build_room`, tournée avec le plan)

```json
{ "centre": [x,y,z sol], "taille": [largeur x, profondeur y], "hauteur": 6,
  "sol": "…", "mur": "…", "plafond": "…", "lumieres": 4, "k": 2700, "collision": true,
  "portes": [ { "mur": "nord|sud|est|ouest", "decalage": 0, "largeur": 2.4, "hauteur": 3, "m": "porte.ent" } ] }
```

- La face intérieure d'un mur de salle se trouve à environ 0,13 m à l'intérieur du bord. Pour des `traits`, placer `de`/`a` à bord − 0,15.
- Murs lisses : `int_common_a_wall_h400_l300_a` (`paint_black`). Pour leur donner une couleur, les poser en `objets`, puisque `build_room` n'applique pas d'apparence.

## Méthode (la plus rapide et la moins coûteuse)

1. Faire `releve.ps1` sur le site.
2. Créer 1 à 3 kits de lieux modèles avec `kit.ps1`. C'est fait une seule fois, puis réutilisé.
3. L'utilisateur donne un **croquis vu de dessus avec une cote** et 1 ou 2 images d'ambiance.
4. Écrire le plan JSON : positions locales, noms courts du kit.
5. Lancer `-Verifier n` : le plan est dessiné dans la vue et l'utilisateur valide.
6. Lancer `-Etape n` et regarder la capture. Pour corriger, Ctrl+Z (outil `undo`), modifier le JSON et relancer.

Testé le 2026-10-09 sur une salle d'essai tournée de 30° : salle, ligne, cercle, chevrons (faces -Y et +X), cadre et lumière `#hex`. Tout a été annulé ensuite.
