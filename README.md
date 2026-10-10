# NCWE Studio FR

Plugin pour NCWE Studio (éditeur du monde de Cyberpunk 2077) :

- interface en français ;
- panneau Sélection ;
- outils gizmo ;
- lieux du jeu et points d'équipe ;
- Nettoyage ;
- Rayon X ;
- outils de construction.

Le plugin ne modifie aucun fichier de NCWE ni du jeu.

## Installation (une seule fois)

1. **Code → Download ZIP** sur cette page, puis décompresser **à côté** du dossier NCWE. Par exemple :
   - `D:\Jeux\NCWE-Studio-0.1.0-beta.3-win-x64\`
   - `D:\Jeux\NCWE-Studio-FR\`
2. Double-cliquer **`Creer le raccourci bureau.cmd`**.
3. Lancer NCWE avec le raccourci **« NCWE Studio (FR) »**.

## Mises à jour : automatiques

À chaque lancement par le raccourci, le plugin vérifie ce dépôt. Si une nouvelle version existe, il la télécharge et l'installe avant d'ouvrir NCWE.

- Vos fichiers personnels ne sont jamais touchés : `mes-points.tsv`, `equipe.txt`, réglages…
- Hors ligne, NCWE se lance normalement.
- Pour désactiver les mises à jour, créer un fichier `pas-de-maj.txt` dans le dossier.

Une nouvelle version de NCWE ? Décompressez-la à côté. Le raccourci prend toujours la plus récente.

## Points du projet (via GitHub)

Les points communs du projet sont dans `points-projet.tsv` : ils arrivent chez tout le monde avec les mises à jour, dans la catégorie « Points du projet » de Monde > Lieux.
Pour publier vos Mes points dedans : `powershell -ExecutionPolicy Bypass -File outils\publier-points.ps1` puis `git push`.

## Points d'équipe

1. Créer un dossier partagé et synchronisé : Google Drive pour ordinateur, OneDrive, Dropbox ou dossier réseau.
2. Chaque membre lance **`Configurer l'equipe.cmd`**, choisit ce dossier et tape son nom.

Les points de chacun apparaissent ensuite chez tous dans Monde > Lieux, catégories « Équipe – nom ». La synchro se fait toutes les 10 s.

## Détails

- `LISEZMOI.txt` : toutes les fonctions.
- `construction/LISEZMOI-CONSTRUCTION.md` : construction par plans.
- `MEMO-CONSTRUCTION.md` : erreurs à éviter.

Recompiler le plugin (si `src\*.cs` change) :

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:library /optimize+ /r:System.Core.dll /out:NCWE.FR.dll src\NcweFr.cs src\SelectionPanel.cs src\Tools.cs src\Extract.cs src\Places.cs src\Cleaning.cs src\XRay.cs src\Team.cs src\Associes.cs src\Camera.cs src\Effets.cs src\Infos.cs src\Bibliotheque.cs src\Trace.cs
```
