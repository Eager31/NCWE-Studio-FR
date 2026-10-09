# Construire plus vite, plus logique, plus beau

Ce document est une proposition. Rien n'est codé tant que tu ne l'as pas validé.

## Le problème aujourd'hui

À chaque lieu, je refais à la main les mêmes recherches :

- chercher les chemins des modèles ;
- vérifier leur taille avec `asset_info` ;
- tester les orientations ;
- deviner les couleurs (apparences).

C'est lent, ça coûte cher en tokens, et ça produit des erreurs : chemin faux, couleur `#hex` ignorée, trait posé à plat au lieu d'être collé au mur…

**L'idée :** le travail de recherche est fait **une seule fois par un script**, rangé dans des fichiers, puis réutilisé. Je ne fais plus que choisir et placer.

---

## 1. Les kits de quartier (bibliothèques automatiques)

**Un kit**, c'est la liste des objets réellement utilisés par le jeu dans un lieu donné. Par exemple : un restaurant de Japantown, la charcudoc de Watson, une salle de combat.

**Comment on le crée : en un clic, sans moi.**

1. Tu mets la caméra dans le lieu modèle. Tu peux aussi le choisir dans la liste des **Lieux**, qui contient déjà 935 points dont les restaurants, les charcudocs et les fixers.
2. Bouton **« Créer un kit ici »** dans l'onglet Monde. Le plugin interroge NCWE en une seule fois sur un rayon de 30 à 80 m.
3. Il regroupe les objets par modèle, avec leur nombre, et les range par famille :
   - sol, murs, plafond ;
   - comptoir, tables, assises ;
   - lumières, déco, enseignes, végétation.
4. Il enregistre aussi, pour chaque modèle :
   - les apparences utilisées sur place (les vraies couleurs du lieu) ;
   - la taille ;
   - l'orientation habituelle par rapport au mur (le plugin calcule de quel côté se trouve le mur le plus proche).

**Résultat :** un fichier `kits\japantown-restaurant.tsv`, lisible et **modifiable à la main**.

**Dans l'éditeur :**

- Un menu **Kits** dans l'onglet Ajouter liste les modèles du kit, triés par famille.
- Un clic sur un modèle le pose devant la caméra, avec la bonne apparence.
- **« Ajouter la sélection au kit »** enrichit un kit avec ce que tu as choisi toi-même, comme les familles perso du Nettoyage.
- **« Kit le plus proche »** : quand tu construis dans un bâtiment, le plugin propose automatiquement les kits du même quartier et du même type de lieu.

**Exemple de flux :** « photo du bâtiment + je veux un restaurant à l'intérieur ».

1. Je lis le kit `restaurant` du quartier, qui existe déjà ou est créé en 1 clic depuis le restaurant le plus proche dans **Lieux**.
2. Je fais le plan avec ces seuls modèles. Ce sont des chemins connus, aucune recherche.
3. Je construis.

## 2. Le cache des modèles

Un fichier unique, `modeles.tsv` : chemin, taille, apparences, pivot, et orientation vérifiée (face avant, collé au mur).

- Il est rempli automatiquement à chaque création de kit, et chaque fois que je mesure un modèle.
- Je ne refais **jamais** deux fois un `asset_info` sur le même modèle.

## 3. Les styles

**Un style**, c'est un petit fichier (`styles\art-deco-cacciatori.tsv`) qui contient :

- **Matériaux :** murs `paint_black`, paravents `marble_black`, sol `marble_dark`.
- **Accents :** traits `gold` (avec la règle de pose : mur face à -X, `pitch ±45, roll 90`).
- **Lumières :** chaude 2700 K, néon vert `[0.18, 0.91, 0.42]`.
- **Motif :** doubles traits à 45°, cercle à barres.
- **Densité :** déco légère ou chargée, plantes oui ou non.

**Comment ça s'utilise :**

- **Appliquer un style** à un lieu : le plugin change les apparences et les couleurs des lumières de la sélection. Ctrl+Z annule.
- **Créer un style depuis un lieu :** même principe que les kits, on lit les apparences dominantes.
- **Styles de départ :**
  - art déco Cacciatori ;
  - néon Kabuki ;
  - corpo City Center ;
  - industriel Watson ;
  - Pacifica abandonné.

**Kit + style :** le kit dit **quoi** poser, le style dit **comment** ça doit être habillé.

## 4. Les plans exécutables (le gros gain de tokens)

Aujourd'hui j'écris de longs scripts PowerShell à chaque étape. À la place :

- **J'écris un plan court**, le fichier `plans\arene.json` : les zones, le kit, le style, et les objets avec leurs positions.
- **Un script fixe**, `construire.ps1 plan.json`, l'exécute **étape par étape** :
  - il vérifie les chemins dans le cache ;
  - il applique le style ;
  - il fait une capture en 960×540 à la fin de chaque étape ;
  - une étape = un Ctrl+Z.
- **Motifs prêts à l'emploi** dans le script :
  - rangée de tables + chaises ;
  - banquettes le long d'un mur ;
  - frise de traits ;
  - rangée de lumières ;
  - paravents ;
  - gradins.

  Une ligne de plan suffit, par exemple `{"motif":"tables", "de":[x,y], "a":[x,y], "kit":"restaurant"}`.
- **Avant de construire**, le plan est dessiné dans la vue avec `annotate` (zones, cotes), pour que tu le valides.

**Gain estimé :** de 5 à 10 fois moins de texte par étape. Les erreurs de syntaxe (nombres négatifs, accents, `` `b ``…) disparaissent, puisque le script est fixe.

## 5. Le relevé de site en un appel

Un script `releve.ps1`, à la place de 5 à 10 appels séparés. Il rend un résumé compact :

- sol, plafond, largeur, ouvertures ;
- quartier, lieux proches et kits disponibles.

## 6. Les règles d'économie (pour moi)

- Lire `MEMO-CONSTRUCTION.md` une fois, puis travailler avec les kits et le cache. Pas de recherche libre.
- Réponses des outils en **résumé** : des comptes, pas de JSON complet.
- Captures **petites**, et seulement en fin d'étape.
- Plan validé **avant** la construction. Pas de retouches en série.

---

## Projet : l'arène de katana sous la carte

**Pourquoi sous la carte :** il n'y a aucune géométrie du jeu à nettoyer, donc on garde le contrôle total.

En contrepartie, tout est à nous :

- le sol, les murs et le plafond ;
- les collisions (sinon on tombe dans le vide) ;
- l'éclairage (pas de ciel) ;
- un accès depuis la surface. NCWE sait créer de **vrais ascenseurs**, et c'est l'accès le plus logique.

**Les kits à récupérer dans le jeu :**

1. **Fight clubs** (Beat on the Brat, dans la liste Lieux, catégorie activités) : ring, cages, grillages, projecteurs, gradins, foule.
2. **Japantown / dojo / Arasaka** pour l'esprit katana : lanternes, torii, bois sombre, paravents japonais, sol tatami ou pierre.
3. **Sous-sols / parkings / égouts** pour l'enveloppe brute : béton, piliers, tuyaux. C'est crédible pour un lieu caché sous la ville.

**Étapes**, chacune avec un plan validé puis une capture :

1. **Emplacement :** un point sous un quartier choisi, à environ 30 m sous le sol, avec un ascenseur depuis une ruelle. J'ajoute le point dans **Mes points**.
2. **Kits :** je scanne 2 ou 3 lieux modèles, une seule fois.
3. **Plan :**
   - arène centrale ronde ou carrée de 12 à 16 m ;
   - gradins sur 3 côtés ;
   - un côté vestiaires / bar ;
   - une loge VIP en hauteur.
4. **Enveloppe :** salle de 40 × 30 × 12 m, collisions, occluder pour ne pas voir le vide.
5. **Arène :** sol, bordure, éclairage zénithal fort, ombre autour.
6. **Gradins, foule, bar, déco japonaise.**
7. **Accès :** ascenseur et couloir d'entrée.

## À valider avant que je code

1. Faut-il **kits + cache + plans exécutables d'abord** (environ une session) puis l'arène avec ces outils ? Ou l'arène tout de suite, à la main ?
2. **Arène :** sous quel quartier ? Quelle taille et quelle capacité ? Quel style : japonais traditionnel, underground industriel, ou un mélange des deux ?
3. **Accès :** ascenseur depuis une ruelle, ou téléportation seulement (point de spawn) ?
