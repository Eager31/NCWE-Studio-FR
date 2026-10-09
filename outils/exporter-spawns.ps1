# Exporte les lieux (lieux.tsv + mes-points.tsv) en points de spawn pour un serveur : Lua, JSON, CSV.
# Positions monde Cyberpunk 2077 : metres, Z vers le haut.
# Usage : powershell -File exporter-spawns.ps1 [-Sortie <dossier>] [-Categories "Fast travel","Mes points"]
param([string]$Sortie = (Join-Path (Split-Path $PSScriptRoot -Parent) 'export-spawns'), [string[]]$Categories)
$ErrorActionPreference = 'Stop'
$dir = Split-Path $PSScriptRoot -Parent
$inv = [Globalization.CultureInfo]::InvariantCulture
$points = New-Object System.Collections.Generic.List[object]
foreach ($f in 'lieux.tsv', 'mes-points.tsv') {
    $p = Join-Path $dir $f
    if (-not (Test-Path -LiteralPath $p)) { continue }
    foreach ($line in [IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8)) {
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
        $c = $line.Split("`t"); if ($c.Length -lt 5) { continue }
        if ($Categories -and $Categories -notcontains $c[0]) { continue }
        $o = [ordered]@{ category = $c[0]; name = $c[1]; x = [double]::Parse($c[2], $inv); y = [double]::Parse($c[3], $inv); z = [double]::Parse($c[4], $inv) }
        if ($c.Length -ge 6) { $o.heading = [double]::Parse($c[5], $inv) }
        $points.Add([pscustomobject]$o)
    }
}
New-Item -ItemType Directory -Force $Sortie | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
function N($v) { $v.ToString('0.###', $inv) }
function LuaStr($s) { '"' + ($s -replace '\\', '\\' -replace '"', '\"') + '"' }

# Lua
$lua = New-Object Text.StringBuilder
[void]$lua.AppendLine('-- Points de spawn Cyberpunk 2077 extraits des fichiers du jeu (positions monde, metres, Z vers le haut)')
[void]$lua.AppendLine("-- $($points.Count) points. heading : cap en degres (0 = +Y), present seulement pour les points perso.")
[void]$lua.AppendLine('return {')
foreach ($p in $points) {
    $h = if ($p.PSObject.Properties['heading']) { ", heading = $(N $p.heading)" } else { '' }
    [void]$lua.AppendLine("  { category = $(LuaStr $p.category), name = $(LuaStr $p.name), x = $(N $p.x), y = $(N $p.y), z = $(N $p.z)$h },")
}
[void]$lua.AppendLine('}')
[IO.File]::WriteAllText((Join-Path $Sortie 'points-spawn.lua'), $lua.ToString(), $utf8)

# JSON
[IO.File]::WriteAllText((Join-Path $Sortie 'points-spawn.json'), ($points | ConvertTo-Json -Depth 3), $utf8)

# CSV (separateur ; pour Excel FR, points decimaux)
$csv = New-Object Text.StringBuilder
[void]$csv.AppendLine('categorie;nom;x;y;z')
foreach ($p in $points) { [void]$csv.AppendLine("$($p.category);$($p.name);$(N $p.x);$(N $p.y);$(N $p.z)") }
[IO.File]::WriteAllText((Join-Path $Sortie 'points-spawn.csv'), $csv.ToString(), (New-Object Text.UTF8Encoding $true))

$points | Group-Object category | ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
"Export : $($points.Count) points -> $Sortie"
