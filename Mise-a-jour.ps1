# Mise à jour automatique du plugin depuis GitHub (appelée par le lanceur, avant d'ouvrir NCWE).
# Compare la dernière version du dépôt à version.txt ; si elle a changé, télécharge et remplace les fichiers.
# Vos fichiers personnels ne sont jamais touchés. Hors ligne ou en cas d'erreur : rien ne change, NCWE se lance.
# Désactiver : créer un fichier pas-de-maj.txt dans ce dossier. (Ignorée aussi dans un dossier git de développement.)
param([string]$Depot = 'Eager31/NCWE-Studio-FR', [string]$Branche = 'main')
$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
if ((Test-Path (Join-Path $ici '.git')) -or (Test-Path (Join-Path $ici 'pas-de-maj.txt'))) { return }

$perso = @('mes-points.tsv', 'equipe.txt', 'chemin-ncwe.txt', 'chemin-mcp.txt', 'familles-perso.tsv', 'nettoyage-reglages.txt',
           'panneau-selection.txt', 'rayon-x.txt', 'version.txt', 'ncwe-fr.log')
$versionFichier = Join-Path $ici 'version.txt'
function Journal([string]$m) { try { Add-Content -LiteralPath (Join-Path $ici 'ncwe-fr.log') -Value ((Get-Date -Format 'HH:mm:ss ') + 'mise a jour : ' + $m) -Encoding UTF8 } catch { } }

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $sha = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Depot/commits/$Branche" -Headers @{ 'User-Agent' = 'NCWE-FR' } -TimeoutSec 6).sha
    if (-not $sha) { return }
    $actuelle = if (Test-Path $versionFichier) { (Get-Content $versionFichier -TotalCount 1).Trim() } else { '' }
    if ($actuelle -eq $sha) { return }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('ncwe-fr-maj-' + $sha.Substring(0, 7))
    if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp 'depot.zip'
    Invoke-WebRequest -Uri "https://codeload.github.com/$Depot/zip/$sha" -OutFile $zip -UseBasicParsing -TimeoutSec 60 -Headers @{ 'User-Agent' = 'NCWE-FR' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $tmp 'x'))
    $source = Get-ChildItem -LiteralPath (Join-Path $tmp 'x') -Directory | Select-Object -First 1
    if (-not $source -or -not (Test-Path (Join-Path $source.FullName 'NCWE.FR.dll'))) { Journal "archive incomplète, ignorée"; return }

    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $source.FullName -Recurse -File) {
        $rel = $f.FullName.Substring($source.FullName.Length + 1)
        if ($perso -contains $rel.ToLowerInvariant()) { continue }
        $dest = Join-Path $ici $rel
        [IO.Directory]::CreateDirectory((Split-Path $dest -Parent)) | Out-Null
        if (Test-Path -LiteralPath $dest) {
            $a = Get-Item -LiteralPath $dest
            if ($a.Length -eq $f.Length -and (Get-FileHash -LiteralPath $dest).Hash -eq (Get-FileHash -LiteralPath $f.FullName).Hash) { continue }
        }
        try { Copy-Item -LiteralPath $f.FullName -Destination $dest -Force }
        catch { if ($rel -eq 'NCWE.FR.dll') { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $ici 'NCWE.FR.new.dll') -Force } else { throw } }
        $n++
    }
    [IO.File]::WriteAllText($versionFichier, $sha)
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Journal ("version {0} installée ({1} fichiers mis à jour)" -f $sha.Substring(0, 7), $n)
}
catch { Journal ("pas de mise à jour (" + $_.Exception.Message + ")") }
