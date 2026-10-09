# Copie vos « Mes points » dans points-projet.tsv (catégorie « Points du projet », partagée par GitHub)
# puis prépare l'envoi : commit git. Envoyer ensuite avec : git push
#   powershell -ExecutionPolicy Bypass -File outils\publier-points.ps1
$ErrorActionPreference = 'Stop'
$racine = Split-Path $PSScriptRoot -Parent
$source = Join-Path $racine 'mes-points.tsv'
$cible = Join-Path $racine 'points-projet.tsv'
if (-not (Test-Path $source)) { throw "mes-points.tsv introuvable : ajoutez d'abord des points dans NCWE." }
$lignes = @('# Points du projet (partagés par GitHub) : catégorie, nom, x, y, z, cap, inclinaison')
$lignes += Get-Content $source -Encoding UTF8 | Where-Object { $_ -and $_[0] -ne '#' } | ForEach-Object { 'Points du projet' + $_.Substring($_.IndexOf("`t")) }
[IO.File]::WriteAllLines($cible, $lignes, (New-Object Text.UTF8Encoding($false)))
Write-Output ("{0} points copiés dans points-projet.tsv" -f ($lignes.Count - 1))
if (Test-Path (Join-Path $racine '.git')) {
    git -C $racine add points-projet.tsv
    git -C $racine commit -q -m "Points du projet mis a jour ($($lignes.Count - 1) points)"
    Write-Output "Commit fait. Envoyez avec : git -C `"$racine`" push"
}
