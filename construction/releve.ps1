# Relevé de site compact : sol, plafond, largeurs, ouvertures, lieux du jeu et kits proches.
#   powershell -File releve.ps1 [-Position "x;y;z"]      (sans -Position : la caméra de NCWE)
param([string]$Position, [double]$Proches = 400)
. "$PSScriptRoot\lib.ps1"
try {
    $p = if ($Position) { Vec $Position } else { @((Ncwe 'studio_status').camera.position) }
    Ncwe 'wait_for_streaming' @{} | Out-Null
    $m = Ncwe 'measure_space' @{ position = @($p); directions = 8; max_distance = 60; show = $false }
    Write-Output ("Point " + ((@($p) | ForEach-Object { N $_ 1 }) -join ';'))
    $sol = if ($m.floor) { N $m.floor.z 2 } else { 'aucun (vide dessous)' }
    $haut = if ($m.ceiling) { (N $m.ceiling.z 2) + " (hauteur libre " + (N $m.clear_height 2) + " m)" } else { 'ciel ouvert' }
    Write-Output ("Sol z={0} · plafond {1}" -f $sol, $haut)
    Write-Output ("Largeur E-O {0} m · N-S {1} m" -f (N $m.width_east_west 1), (N $m.width_north_south 1))
    $dirs = @{ 0 = 'N'; 45 = 'NO'; 90 = 'O'; 135 = 'SO'; 180 = 'S'; 225 = 'SE'; 270 = 'E'; 315 = 'NE' }
    $rays = @($m.rays | ForEach-Object { $d = $dirs[[int][Math]::Round($_.heading)]; if (-not $d) { $d = [int]$_.heading }; "{0} {1}" -f $d, $(if ($_.distance) { (N $_.distance 1) + 'm' } else { 'libre' }) }) -join ' · '
    Write-Output ("Murs : " + $rays)
    if (@($m.open_headings).Count) { Write-Output ("Ouvertures (caps) : " + (@($m.open_headings) -join ', ')) }

    # lieux du jeu les plus proches
    $lieux = [IO.File]::ReadAllLines((Join-Path (Split-Path $Racine -Parent) 'lieux.tsv'), [Text.Encoding]::UTF8) | Where-Object { $_ -and $_ -notmatch '^#' } | ForEach-Object {
        $c = $_.Split("`t"); $dx = (P $c[2]) - $p[0]; $dy = (P $c[3]) - $p[1]
        [pscustomobject]@{ Cat = $c[0]; Nom = $c[1]; D = [Math]::Sqrt($dx * $dx + $dy * $dy) } } | Where-Object { $_.D -le $Proches } | Sort-Object D | Select-Object -First 6
    if ($lieux) { Write-Output ("Lieux proches : " + (($lieux | ForEach-Object { "{0} ({1}, {2} m)" -f $_.Nom, $_.Cat, [int]$_.D }) -join ' · ')) }

    # kits existants à proximité (centre lu dans l'en-tête des kits)
    $kits = Get-ChildItem (Join-Path $Racine 'kits') -Filter *.tsv -ErrorAction SilentlyContinue | ForEach-Object {
        $h = [IO.File]::ReadAllLines($_.FullName, [Text.Encoding]::UTF8)[0]
        if ($h -match 'centre (-?[\d.]+);(-?[\d.]+)') { $dx = (P $Matches[1]) - $p[0]; $dy = (P $Matches[2]) - $p[1]; [pscustomobject]@{ Nom = $_.BaseName; D = [Math]::Sqrt($dx * $dx + $dy * $dy) } } } | Sort-Object D
    if ($kits) { Write-Output ("Kits : " + (($kits | Select-Object -First 8 | ForEach-Object { "{0} ({1} m)" -f $_.Nom, [int]$_.D }) -join ' · ')) } else { Write-Output "Kits : aucun (créez-en avec kit.ps1)" }
}
finally { Ncwe-Stop }
