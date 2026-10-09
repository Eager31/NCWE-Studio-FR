# Prépare le « passage qui monte » : les objets d'un dossier NCWE deviennent l'apparence d'une cabine
# d'ascenseur (common_lift) qui monte de -Hauteur mètres quand on appelle l'ascenseur depuis la rue.
#   powershell -File passage-ascenseur.ps1 [-Dossier passage] [-Hauteur 3.2] [-Sortie <dossier mod>]
# Produit : spec.tsv (meshes en coordonnées de la cabine) puis le mod ArchiveXL via redtool, et affiche
# les réglages de l'ascenseur à créer dans NCWE (position, orientation, étages).
param([string]$Dossier = 'passage', [double]$Hauteur = 3.2, [string]$Nom = 'passage_ncwe', [string]$Sortie)
. "$PSScriptRoot\lib.ps1"
$racinePlugin = Split-Path $PSScriptRoot -Parent
if (-not $Sortie) { $Sortie = Join-Path $racinePlugin 'mods\passage' }
$jeu = 'D:\SteamLibrary\steamapps\common\Cyberpunk 2077'
$ncwe = (Get-ChildItem (Split-Path $racinePlugin -Parent) -Directory -Filter 'NCWE-Studio-*' | Sort-Object Name | Select-Object -Last 1).FullName
$redtool = Join-Path $racinePlugin 'outils\redtool'

# ---------- quaternions (convention NCWE : q = lacet × tangage × roulis) ----------
function QAxe([double[]]$axe, [double]$deg) { $a = $deg * [Math]::PI / 360; $s = [Math]::Sin($a); return @(($axe[0] * $s), ($axe[1] * $s), ($axe[2] * $s), [Math]::Cos($a)) }
function QMul($a, $b) {   # a × b (b appliqué d'abord), composantes i;j;k;r
    return @(
        ($a[3] * $b[0] + $a[0] * $b[3] + $a[1] * $b[2] - $a[2] * $b[1]),
        ($a[3] * $b[1] - $a[0] * $b[2] + $a[1] * $b[3] + $a[2] * $b[0]),
        ($a[3] * $b[2] + $a[0] * $b[1] - $a[1] * $b[0] + $a[2] * $b[3]),
        ($a[3] * $b[3] - $a[0] * $b[0] - $a[1] * $b[1] - $a[2] * $b[2]))
}
function QEuler($yaw, $pitch, $roll) { QMul (QMul (QAxe @(0, 0, 1) $yaw) (QAxe @(1, 0, 0) $pitch)) (QAxe @(0, 1, 0) $roll) }

try {
    # ---------- objets du dossier ----------
    $org = Ncwe 'organize' @{ action = 'list'; limit = 20000 }
    $f = $org.folders | Where-Object { $_.name -eq $Dossier -or $_.path -eq $Dossier } | Select-Object -First 1
    if (-not $f) { throw "Dossier « $Dossier » introuvable dans l'arborescence du projet." }
    $ids = @($org.objects | Where-Object { $_.folder -eq $f.id } | ForEach-Object { $_.id })
    $objs = @((Ncwe 'get_objects' @{ ids = $ids }).objects | Where-Object { $_.asset -like '*.mesh' })
    if (-not $objs.Count) { throw "Aucun mesh dans le dossier « $Dossier »." }

    # ---------- emprise, sol, façade ----------
    $mn = @(1e9, 1e9, 1e9); $mx = @(-1e9, -1e9, -1e9)
    foreach ($o in $objs) { for ($k = 0; $k -lt 3; $k++) { $mn[$k] = [Math]::Min($mn[$k], [double]$o.bounds.min[$k]); $mx[$k] = [Math]::Max($mx[$k], [double]$o.bounds.max[$k]) } }
    $cx = ($mn[0] + $mx[0]) / 2; $cy = ($mn[1] + $mx[1]) / 2
    $sol = (Ncwe 'measure_space' @{ position = @($cx, $cy, ($mn[2] + 1)); directions = 4; ignore = $ids; show = $false }).floor.z
    if ($null -eq $sol) { $sol = $mn[2] }
    # façade : le lacet le plus fréquent des pièces, son avant (+Y local) regarde la rue
    $lacet = ($objs | Group-Object { [Math]::Round([double]$_.rotation.yaw) } | Sort-Object Count -Descending | Select-Object -First 1).Name
    $yawAsc = [double]$lacet + 180            # l'ascenseur ouvre vers -Y local : on tourne de 180° pour qu'il regarde la rue
    while ($yawAsc -gt 180) { $yawAsc -= 360 }
    $base = @($cx, $cy, ($sol - $Hauteur))     # étage 0 (fermé) sous la rue ; étage 1 = rue (ouvert)

    # ---------- coordonnées dans le repère de l'apparence (lacet ascenseur + 90° de l'AppearanceSlot) ----------
    $rep = $yawAsc + 90; $a = - $rep * [Math]::PI / 180; $ca = [Math]::Cos($a); $sa = [Math]::Sin($a)
    $qInv = QAxe @(0, 0, 1) (- $rep)
    $appPath = "mod\ncwe_fr\passage\$Nom.app"; $entPath = 'mod\ncwe_fr\passage\common_lift_passage.ent'
    $l = New-Object Collections.Generic.List[string]
    $l.Add(($Nom, $appPath, $entPath) -join "`t")
    foreach ($o in $objs) {
        $dx = $o.position[0] - $base[0]; $dy = $o.position[1] - $base[1]; $dz = $o.position[2] - $base[2]
        $lx = $dx * $ca - $dy * $sa; $ly = $dx * $sa + $dy * $ca
        $q = QMul $qInv (QEuler $o.rotation.yaw $o.rotation.pitch $o.rotation.roll)
        $sc = if ($o.scale) { @($o.scale) } else { @(1, 1, 1) }
        $app = if ($o.appearance) { $o.appearance } else { 'default' }
        $l.Add(($o.asset, $app, ((@($lx, $ly, $dz) | ForEach-Object { N $_ 4 }) -join ';'), (($q | ForEach-Object { N $_ 6 }) -join ';'), (($sc | ForEach-Object { N $_ 4 }) -join ';')) -join "`t")
    }
    [IO.Directory]::CreateDirectory($Sortie) | Out-Null
    $spec = Join-Path $Sortie 'spec.tsv'
    [IO.File]::WriteAllLines($spec, $l, (New-Object Text.UTF8Encoding($false)))
    Write-Output ("{0} meshes · sol z={1} · ascenseur : base {2} · lacet {3}° · étages [0 ; {4}]" -f $objs.Count, (N $sol 3), ((@($base) | ForEach-Object { N $_ 3 }) -join ';'), (N $yawAsc 1), $Hauteur)
}
finally { Ncwe-Stop }

# ---------- fichiers du jeu + mod ----------
$x = Join-Path $Sortie 'jeu'
$env:REDTOOL_GAME = $jeu
& dotnet exec --runtimeconfig "$redtool\redtool.runtimeconfig.json" "$redtool\redtool.exe" extract $jeu $x 'base\gameplay\devices\elevators\appearances\common_lift_appearances.app' 'base\gameplay\devices\elevators\common_elevator\common_lift.ent'
& dotnet exec --runtimeconfig "$redtool\redtool.runtimeconfig.json" "$redtool\redtool.exe" passage $ncwe (Join-Path $x 'base_gameplay_devices_elevators_appearances_common_lift_appearances.app') (Join-Path $x 'base_gameplay_devices_elevators_common_elevator_common_lift.ent') $spec $Sortie
