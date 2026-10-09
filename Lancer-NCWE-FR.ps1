# Lance NCWE Studio en francais (plugin NCWE.FR.dll), quelle que soit la version installee.
# Version utilisee : celle de chemin-ncwe.txt si present, sinon la plus recente trouvee
# dans les dossiers voisins de ce dossier (ex. E:\Nouveau dossier\NCWE-Studio-*\NCWE.Studio.exe).
param([switch]$Afficher)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$config = Join-Path $here 'chemin-ncwe.txt'

# mise a jour automatique depuis GitHub (silencieuse ; hors ligne = rien ne change)
try { & (Join-Path $here 'Mise-a-jour.ps1') } catch { }

# mise a jour du plugin en attente (copiee pendant que NCWE etait ouvert)
$pending = Join-Path $here 'NCWE.FR.new.dll'
if (Test-Path -LiteralPath $pending) { try { Move-Item -LiteralPath $pending -Destination (Join-Path $here 'NCWE.FR.dll') -Force } catch { } }

function Find-Ncwe {
    if (Test-Path -LiteralPath $config) {
        $p = (Get-Content -LiteralPath $config -TotalCount 1).Trim()
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    # la plus recente a cote de ce dossier (ou un niveau plus bas)
    $root = Split-Path $here -Parent
    $found = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter 'NCWE.Studio.exe' -File -Depth 1 -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    # sinon on demande une fois
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = 'Choisissez NCWE.Studio.exe'; $dlg.Filter = 'NCWE Studio|NCWE.Studio.exe'
    if ($dlg.ShowDialog() -eq 'OK') { [IO.File]::WriteAllText($config, $dlg.FileName); return $dlg.FileName }
    throw 'NCWE.Studio.exe introuvable.'
}

$exe = Find-Ncwe
if ($Afficher) { "NCWE utilise : $exe"; return }
$env:DOTNET_STARTUP_HOOKS = Join-Path $here 'NCWE.FR.dll'
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
