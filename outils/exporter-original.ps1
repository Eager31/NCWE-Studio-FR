# Export "version originale" : identifiants d'origine du jeu (chemins du journal avec des points) + coordonnees.
# Source : outils\extraction\lieux-bruts.tsv et fast-travel-bruts.tsv (extraits des fichiers du jeu).
# Usage : powershell -File exporter-original.ps1 [-Sortie <dossier>]
param([string]$Sortie = (Join-Path (Split-Path $PSScriptRoot -Parent) 'export-original'))
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
$pts = New-Object System.Collections.Generic.List[object]

# points d'interet du journal : id = chemin du journal, '/' remplace par '.'
foreach ($r in Import-Csv (Join-Path $PSScriptRoot 'extraction\lieux-bruts.tsv') -Delimiter "`t" -Encoding UTF8) {
    $p = $r.chemin -split '/'
    $q = if ($p[0] -eq 'ep1') { $p[1..($p.Length - 1)] } else { $p }
    if ($q[0] -ne 'points_of_interest' -or $q.Length -lt 3) { continue }
    $pts.Add([pscustomobject][ordered]@{
        id = ($r.chemin -replace '/', '.'); category = $q[1]; name = $q[$q.Length - 1]
        name_en = ''; name_fr = ''
        dlc = [bool]($p[0] -eq 'ep1')
        x = [double]::Parse($r.x, $inv); y = [double]::Parse($r.y, $inv); z = [double]::Parse($r.z, $inv) })
}

# bornes de fast travel : fiche officielle du jeu (FastTravelPoints.xxx) + noms officiels EN/FR
$ftFile = Join-Path $PSScriptRoot 'extraction\fast-travel-noms.tsv'
foreach ($r in Import-Csv $ftFile -Delimiter "`t" -Encoding UTF8) {
    $id = if ($r.fiche) { $r.fiche } else { 'fast_travel.' + [IO.Path]::GetFileNameWithoutExtension($r.secteur) }
    $pts.Add([pscustomobject][ordered]@{
        id = $id; category = if ($r.fiche -match 'metro') { 'fast_travel_subway' } else { 'fast_travel' }
        name = ($id -replace '^FastTravelPoints\.', ''); name_en = $r.nom_en; name_fr = $r.nom_fr
        dlc = [bool]($r.modele -like 'ep1\*' -or $r.fiche -like '*combat_zone*')
        x = [double]::Parse($r.x, $inv); y = [double]::Parse($r.y, $inv); z = [double]::Parse($r.z, $inv) })
}
# commissariats NCPD (trouves par leurs enseignes, outil ajouter-commissariats.ps1)
foreach ($line in Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'lieux.tsv') -Encoding UTF8) {
    $c = $line.Split("`t"); if ($c[0] -ne 'Commissariats') { continue }
    $slug = (($c[1] -replace '^Commissariat NCPD (abandonné )?– ', '') -replace '[^A-Za-z0-9]+', '_').Trim('_').ToLower()
    $pts.Add([pscustomobject][ordered]@{
        id = "ncpd_precinct.$slug"; category = 'ncpd_precinct'; name = $slug; name_en = ($c[1] -replace 'Commissariat NCPD abandonné', 'Abandoned NCPD Precinct' -replace 'Commissariat NCPD', 'NCPD Precinct'); name_fr = $c[1]
        dlc = [bool]($c[1] -like '*Dogtown*')
        x = [double]::Parse($c[2], $inv); y = [double]::Parse($c[3], $inv); z = [double]::Parse($c[4], $inv) })
}
# identifiants uniques : _2, _3... si deux stations partagent le meme secteur et le meme modele
$seen = @{}
foreach ($p in $pts) { if ($seen.ContainsKey($p.id)) { $seen[$p.id]++; $p.id = "$($p.id)_$($seen[$p.id])" } else { $seen[$p.id] = 1 } }

New-Item -ItemType Directory -Force $Sortie | Out-Null
function N($v) { $v.ToString('0.###', $inv) }
function S($s) { '"' + ($s -replace '\\', '\\' -replace '"', '\"') + '"' }
$utf8 = New-Object Text.UTF8Encoding $false

$lua = New-Object Text.StringBuilder
[void]$lua.AppendLine('-- Cyberpunk 2077 : points d''origine du jeu (identifiants du journal / bornes de fast travel)')
[void]$lua.AppendLine("-- $($pts.Count) points. Coordonnees monde : metres, Z vers le haut. dlc = Phantom Liberty.")
[void]$lua.AppendLine('return {')
foreach ($p in $pts) { [void]$lua.AppendLine("  { id = $(S $p.id), category = $(S $p.category), name = $(S $p.name), name_en = $(S $p.name_en), name_fr = $(S $p.name_fr), dlc = $(([string]$p.dlc).ToLower()), x = $(N $p.x), y = $(N $p.y), z = $(N $p.z) },") }
[void]$lua.AppendLine('}')
[IO.File]::WriteAllText((Join-Path $Sortie 'points-original.lua'), $lua.ToString(), $utf8)
[IO.File]::WriteAllText((Join-Path $Sortie 'points-original.json'), ($pts | ConvertTo-Json -Depth 3), $utf8)
$csv = New-Object Text.StringBuilder; [void]$csv.AppendLine('id;category;name;name_en;name_fr;dlc;x;y;z')
foreach ($p in $pts) { [void]$csv.AppendLine("$($p.id);$($p.category);$($p.name);$($p.name_en);$($p.name_fr);$($p.dlc);$(N $p.x);$(N $p.y);$(N $p.z)") }
[IO.File]::WriteAllText((Join-Path $Sortie 'points-original.csv'), $csv.ToString(), (New-Object Text.UTF8Encoding $true))

$pts | Group-Object category | Sort-Object Count -Descending | ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
"Export : $($pts.Count) points -> $Sortie"
