# Configure le partage des points en équipe : choisit le dossier partagé (Google Drive, OneDrive, Dropbox,
# dossier réseau…) et votre nom. Écrit equipe.txt à côté de ce script. Relancez NCWE ensuite.
Add-Type -AssemblyName System.Windows.Forms, Microsoft.VisualBasic
$ici = $PSScriptRoot
$fichier = Join-Path $ici 'equipe.txt'
$ancien = if (Test-Path $fichier) { [IO.File]::ReadAllLines($fichier, [Text.Encoding]::UTF8) } else { @() }

[System.Windows.Forms.MessageBox]::Show("Choisissez le dossier PARTAGÉ de l'équipe (le même pour tout le monde, synchronisé par Google Drive, OneDrive, Dropbox ou un dossier réseau).", "NCWE FR - Points d'équipe") | Out-Null
$dlg = New-Object System.Windows.Forms.FolderBrowserDialog
$dlg.Description = "Dossier partagé de l'équipe (points NCWE)"
$dlg.ShowNewFolderButton = $true
if ($ancien.Count -gt 0 -and (Test-Path $ancien[0])) { $dlg.SelectedPath = $ancien[0] }
if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 1 }

$defaut = if ($ancien.Count -gt 1 -and $ancien[1]) { $ancien[1] } else { $env:USERNAME }
$nom = [Microsoft.VisualBasic.Interaction]::InputBox("Votre nom (affiché aux autres : « Équipe – <nom> ») :", "NCWE FR - Points d'équipe", $defaut)
if (-not $nom.Trim()) { exit 1 }

[IO.File]::WriteAllLines($fichier, @($dlg.SelectedPath, $nom.Trim()), (New-Object Text.UTF8Encoding($false)))
[System.Windows.Forms.MessageBox]::Show("C'est réglé.`n`nDossier : $($dlg.SelectedPath)`nNom : $($nom.Trim())`n`nRelancez NCWE avec le raccourci : vos points sont publiés et ceux de l'équipe apparaissent dans Monde > Lieux (catégories « Équipe – … »), mis à jour toutes les 10 s.", "NCWE FR - Points d'équipe") | Out-Null
