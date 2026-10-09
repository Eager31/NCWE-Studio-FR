# Exécute un plan de construction (JSON court, en mètres locaux) dans NCWE, étape par étape.
#   powershell -File construire.ps1 plans\arene.json                 liste les étapes (ne pose rien)
#   powershell -File construire.ps1 plans\arene.json -Verifier 2     vérifie l'étape 2 et la dessine dans la vue (rien n'est posé)
#   powershell -File construire.ps1 plans\arene.json -Etape 2        construit l'étape 2 (un Ctrl+Z annule l'étape) + capture
#   -Etape 1-3 : plusieurs étapes · -SansCapture : pas de capture
# Format du plan : voir LISEZMOI-CONSTRUCTION.md
param(
    [Parameter(Mandatory = $true)][string]$Plan,
    [string]$Etape,
    [string]$Verifier,
    [switch]$SansCapture
)
. "$PSScriptRoot\lib.ps1"

$TRAIT = 'base\environment\architecture\common\int\int_nkt_apartment_a\int_nkt_apartment_a_ceiling_light_l300_aa.mesh'
$MURS = @{ nord = 'north'; sud = 'south'; est = 'east'; ouest = 'west'; north = 'north'; south = 'south'; east = 'east'; west = 'west' }

function Champ($o, [string]$n, $defaut = $null) { if ($null -ne $o -and $o.PSObject.Properties[$n]) { return $o.$n }; return $defaut }
function Liste($v) { if ($null -eq $v) { return @() }; return @($v) }
function Etapes([string]$s, [int]$n) {
    if (-not $s) { return @() }
    $r = @(); foreach ($part in $s.Split(',')) { if ($part -match '^(\d+)-(\d+)$') { $r += [int]$Matches[1]..[int]$Matches[2] } else { $r += [int]$part } }
    return @($r | Where-Object { $_ -ge 1 -and $_ -le $n })
}

# ---------- plan ----------
$planFichier = (Resolve-Path $Plan).Path
$pl = [IO.File]::ReadAllText($planFichier, [Text.Encoding]::UTF8) | ConvertFrom-Json
$nomPlan = Champ $pl 'nom' ([IO.Path]::GetFileNameWithoutExtension($planFichier))
Repere (Liste (Champ $pl 'origine' @(0, 0, 0))) ([double](Champ $pl 'cap' 0))
$etapes = Liste $pl.etapes

# noms courts -> chemins : kits du plan puis cache des modèles
$noms = @{}
foreach ($k in (Liste (Champ $pl 'kits'))) {
    $f = Join-Path $Racine ("kits\$k.tsv")
    if (-not (Test-Path $f)) { throw "Kit introuvable : $f" }
    foreach ($l in [IO.File]::ReadAllLines($f, [Text.Encoding]::UTF8)) { if ($l -and $l[0] -ne '#') { $ch = $l.Split("`t")[1]; $b = [IO.Path]::GetFileNameWithoutExtension($ch).ToLowerInvariant(); if (-not $noms.ContainsKey($b)) { $noms[$b] = $ch } } }
}
foreach ($m in (Cache-Charger).Values) { $b = [IO.Path]::GetFileNameWithoutExtension($m.Chemin).ToLowerInvariant(); if (-not $noms.ContainsKey($b)) { $noms[$b] = $m.Chemin } }
function Chemin([string]$ref) {
    if (-not $ref) { throw "Objet sans modèle (champ m)." }
    if ($ref.Contains('\')) { return $ref }
    $b = $ref.ToLowerInvariant() -replace '\.(mesh|ent)$', ''
    if ($noms.ContainsKey($b)) { return $noms[$b] }
    throw "Modèle inconnu « $ref » : donnez le chemin complet, ou ajoutez un kit qui le contient."
}

# ---------- couleurs ----------
function Couleur($v) {
    if ($v -is [string] -and $v -match '^#?([0-9a-fA-F]{6})$') { $h = $Matches[1]; return @(([Convert]::ToInt32($h.Substring(0, 2), 16) / 255), ([Convert]::ToInt32($h.Substring(2, 2), 16) / 255), ([Convert]::ToInt32($h.Substring(4, 2), 16) / 255)) | ForEach-Object { [Math]::Round($_, 3) } }
    return @($v | ForEach-Object { [double]$_ })
}

# ---------- constructeurs ----------
$script:items = $null; $script:elems = $null; $script:boites = $null; $script:salles = $null
function Objet($m, $p, [double]$yaw = 0, [double]$pitch = 0, [double]$roll = 0, $s = $null, $app = $null, $col = $false, $nom = $null, $sol = $null) {
    $chemin = Chemin $m
    $pw = if (@($p).Count -eq 2) { $w = Monde @($p[0], $p[1], 0); @($w[0], $w[1]) } else { Monde $p }
    $it = @{ asset = $chemin; position = @($pw); rotation = @{ yaw = [Math]::Round($yaw + $script:Cap, 2); pitch = $pitch; roll = $roll }; ground = $(if ($sol) { $sol } elseif (@($p).Count -eq 2) { 'bottom' } else { 'none' }); name = $(if ($nom) { $nom } else { $script:nomCourant }) }
    if ($null -ne $s) { $it.scale = $(if (@($s).Count -eq 3) { @($s | ForEach-Object { [double]$_ }) } else { [double]$s }) }
    if ($app) { $it.appearance = [string]$app }
    if ($col) { $it.collision = 'box' }
    $script:items.Add($it)
}
function ObjetDe($o, $p, [double]$yawPlus = 0) {
    Objet (Champ $o 'm') $p ([double](Champ $o 'yaw' 0) + $yawPlus) ([double](Champ $o 'pitch' 0)) ([double](Champ $o 'roll' 0)) (Champ $o 's') (Champ $o 'app') ([bool](Champ $o 'col' $false)) (Champ $o 'n') (Champ $o 'sol')
}
function Lumiere($l, $p) {
    $e = @{ kind = 'light'; light_type = (Champ $l 'type' 'point'); position = @(Monde $p); name = (Champ $l 'n' ($script:nomCourant + '_lumiere')) }
    if (Champ $l 'k') { $e.temperature = [double]$l.k } elseif (Champ $l 'rgb') { $e.color = @(Couleur $l.rgb) } else { $e.temperature = 2700 }
    $e.intensity = [double](Champ $l 'i' 600); $e.radius = [double](Champ $l 'r' 6)
    if ($null -ne (Champ $l 'ombres')) { $e.shadows = [bool]$l.ombres }
    if ($null -ne (Champ $l 'yaw') -or $null -ne (Champ $l 'pitch')) { $e.rotation = @{ yaw = [double](Champ $l 'yaw' 0) + $script:Cap; pitch = [double](Champ $l 'pitch' -90); roll = 0 } }
    foreach ($a in 'inner_angle', 'outer_angle') { if (Champ $l $a) { $e[$a] = [double]$l.$a } }
    $script:elems.Add($e)
}
# Pose ce que décrit un motif à chaque point : un objet (m) et/ou une lumière (lumiere).
function Poser($o, $p, [double]$yawPlus = 0) {
    if (Champ $o 'm') { ObjetDe $o $p $yawPlus }
    if (Champ $o 'lumiere') { $lp = @($p); if ($lp.Count -eq 2) { $lp += 0 }; Lumiere $o.lumiere $lp }
}

function Motif($o) {
    $type = Champ $o 'motif' 'objet'
    switch ($type) {
        'ligne' {
            $de = @($o.de | ForEach-Object { [double]$_ }); $a = @($o.a | ForEach-Object { [double]$_ })
            $dx = $a[0] - $de[0]; $dy = $a[1] - $de[1]; $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
            $n = if (Champ $o 'nombre') { [int]$o.nombre } else { [int][Math]::Floor($len / [double](Champ $o 'pas' 1)) + 1 }
            $suivre = if ([bool](Champ $o 'suivre' $false)) { [Math]::Atan2(-$dx, $dy) * 180 / [Math]::PI } else { 0 }
            for ($i = 0; $i -lt $n; $i++) {
                $t = if ($n -gt 1) { $i / ($n - 1) } else { 0.5 }
                $p = @(($de[0] + $dx * $t), ($de[1] + $dy * $t))
                if ($de.Count -gt 2) { $p += $de[2] + ($a[2] - $de[2]) * $t }
                Poser $o $p $suivre
            }
        }
        'grille' {
            $c = @($o.centre | ForEach-Object { [double]$_ }); $pas = @($o.pas | ForEach-Object { [double]$_ }); if ($pas.Count -eq 1) { $pas += $pas[0] }
            $L = [int](Champ $o 'lignes' 1); $C = [int](Champ $o 'colonnes' 1)
            for ($i = 0; $i -lt $L; $i++) { for ($j = 0; $j -lt $C; $j++) {
                $p = @(($c[0] + ($j - ($C - 1) / 2) * $pas[0]), ($c[1] + ($i - ($L - 1) / 2) * $pas[1])); if ($c.Count -gt 2) { $p += $c[2] }
                Poser $o $p } }
        }
        'cercle' {
            $c = @($o.centre | ForEach-Object { [double]$_ }); $r = [double]$o.rayon; $n = [int]$o.nombre
            $a0 = [double](Champ $o 'de' 0); $a1 = [double](Champ $o 'a' 360); $plein = [Math]::Abs($a1 - $a0) -ge 359.9
            $face = Champ $o 'face' 'centre'
            for ($i = 0; $i -lt $n; $i++) {
                $t = if ($plein) { $i / $n } elseif ($n -gt 1) { $i / ($n - 1) } else { 0.5 }
                $th = $a0 + ($a1 - $a0) * $t; $rad = $th * [Math]::PI / 180
                $p = @(($c[0] + $r * [Math]::Cos($rad)), ($c[1] + $r * [Math]::Sin($rad))); if ($c.Count -gt 2) { $p += $c[2] }
                $y = switch ($face) { 'centre' { $th + 90 } 'exterieur' { $th - 90 } default { 0 } }
                Poser $o $p $y
            }
        }
        'traits' { Traits $o }
        'bande' { Bande $o.de $o.a ([double]$o.z) ([double](Champ $o 'largeur' 0.12)) (Champ $o 'app' 'gold') }
        'cadre' {
            $mn = @($o.min | ForEach-Object { [double]$_ }); $mx = @($o.max | ForEach-Object { [double]$_ }); $z = [double]$o.z
            $lg = [double](Champ $o 'largeur' 0.12); $app = Champ $o 'app' 'gold'
            $decs = @(0); if (Champ $o 'double') { $decs += [double]$o.double }
            foreach ($d in $decs) {
                $x0 = $mn[0] + $d; $y0 = $mn[1] + $d; $x1 = $mx[0] - $d; $y1 = $mx[1] - $d
                Bande @($x0, $y0) @($x1, $y0) $z $lg $app; Bande @($x1, $y0) @($x1, $y1) $z $lg $app
                Bande @($x1, $y1) @($x0, $y1) $z $lg $app; Bande @($x0, $y1) @($x0, $y0) $z $lg $app
            }
        }
        default { $p = @($o.p | ForEach-Object { [double]$_ }); Poser $o $p }
    }
}

# Bande plate (liseré de plafond, filet au sol) de « de » à « a » à la hauteur z.
function Bande($de, $a, [double]$z, [double]$largeur, $app) {
    $w0 = Monde @([double]$de[0], [double]$de[1], $z); $w1 = Monde @([double]$a[0], [double]$a[1], $z)
    $dx = $w1[0] - $w0[0]; $dy = $w1[1] - $w0[1]; $len = [Math]::Sqrt($dx * $dx + $dy * $dy); if ($len -lt 0.01) { return }
    $yaw = [Math]::Atan2(-$dy, -$dx) * 180 / [Math]::PI      # le trait s'étend vers -X local depuis son pivot
    $script:items.Add(@{ asset = $TRAIT; appearance = [string]$app; position = @($w0); rotation = @{ yaw = [Math]::Round($yaw, 2); pitch = 0; roll = 0 }; scale = @([Math]::Round($len / 3, 4), [Math]::Round($largeur / 0.985, 4), 1); ground = 'none'; name = $script:nomCourant + '_bande' })
}

# Traits du motif art déco collés sur un mur. face = normale du mur vers la salle (-X, +X, -Y, +Y en local).
function Traits($o) {
    $faces = @{ '-X' = @(-1, 0, 0); '+X' = @(1, 0, 180); '-Y' = @(0, -1, 90); '+Y' = @(0, 1, 270) }
    $f = $faces[[string](Champ $o 'face' '-Y')]; if (-not $f) { throw "traits : face doit être -X, +X, -Y ou +Y" }
    $nx = $f[0]; $ny = $f[1]; $yaw = $f[2] + $script:Cap
    $ux = - $ny; $uy = $nx      # u = droite du spectateur, qui regarde le mur (vers -n)
    $L = [double](Champ $o 'longueur' 0.75); $lg = [double](Champ $o 'largeur' 0.12); $dbl = [double](Champ $o 'double' 0.18)
    $forme = [string](Champ $o 'forme' 'chevron'); $z = [double]$o.z; $app = Champ $o 'app' 'gold'
    $h = $L * 0.7071; $g = 0.1
    $W = if ($forme -eq 'chevron') { 2 * $h + $g + 2 * $dbl } else { $h + $dbl }
    $pas = [double](Champ $o 'pas' ($W + 0.4))
    $de = @($o.de | ForEach-Object { [double]$_ }); $a = @($o.a | ForEach-Object { [double]$_ })
    $sa = ($a[0] - $de[0]) * $ux + ($a[1] - $de[1]) * $uy
    $smin = [Math]::Min(0, $sa); $smax = [Math]::Max(0, $sa); $len = $smax - $smin
    if ($len -lt $W) { return }
    $n = [int][Math]::Floor(($len - $W) / $pas) + 1
    $s0 = $smin + ($len - (($n - 1) * $pas + $W)) / 2 + $W / 2
    $sc = @([Math]::Round($L / 3, 4), [Math]::Round($lg / 0.985, 4), 1)
    for ($k = 0; $k -lt $n; $k++) {
        $c = $s0 + $k * $pas
        $poses = @()
        if ($forme -eq 'chevron') { $poses += , @(($c - $g / 2 - $h), 45, -1); $poses += , @(($c + $g / 2 + $h), -45, 1) }
        elseif ($forme -eq '/') { $poses += , @(($c - $h / 2), 45, -1) }
        else { $poses += , @(($c + $h / 2), -45, 1) }
        foreach ($ps in $poses) {
            foreach ($d in @(0) + $(if ($dbl -gt 0) { @($dbl) } else { @() })) {
                $s = $ps[0] + $ps[2] * $d
                $lp = @(($de[0] + $ux * $s + $nx * 0.04), ($de[1] + $uy * $s + $ny * 0.04), $z)
                $script:items.Add(@{ asset = $TRAIT; appearance = [string]$app; position = @(Monde $lp); rotation = @{ yaw = $yaw; pitch = $ps[1]; roll = 90 }; scale = $sc; ground = 'none'; name = $script:nomCourant + '_motif' })
            }
        }
    }
}

function Salle($s) {
    $b = @{ center = @(Monde @($s.centre | ForEach-Object { [double]$_ })); size = @($s.taille | ForEach-Object { [double]$_ }); height = [double]$s.hauteur; heading = $script:Cap }
    foreach ($kv in @(@('sol', 'floor_asset'), @('mur', 'wall_asset'), @('plafond', 'ceiling_asset'))) { $v = Champ $s $kv[0]; if ($v) { $b[$kv[1]] = Chemin $v } }
    if ($null -ne (Champ $s 'lumieres')) { $b.lights = [int]$s.lumieres }
    if (Champ $s 'k') { $b.light_temperature = [double]$s.k }
    if (Champ $s 'i') { $b.light_intensity = [double]$s.i }
    if ($null -ne (Champ $s 'collision')) { $b.collision = [bool]$s.collision }
    $portes = @(); foreach ($p in (Liste (Champ $s 'portes'))) {
        $d = @{ wall = $MURS[[string]$p.mur] }; if (Champ $p 'decalage') { $d.offset = [double]$p.decalage }
        if (Champ $p 'largeur') { $d.width = [double]$p.largeur }; if (Champ $p 'hauteur') { $d.height = [double]$p.hauteur }
        if (Champ $p 'm') { $d.asset = Chemin $p.m }; $portes += $d }
    if ($portes.Count) { $b.doors = $portes }
    $script:salles.Add($b)
}

function Preparer($e, [int]$i) {
    $script:items = New-Object Collections.Generic.List[object]; $script:elems = New-Object Collections.Generic.List[object]; $script:salles = New-Object Collections.Generic.List[object]
    $script:nomCourant = "{0}_{1}" -f $nomPlan, (Champ $e 'id' $i)
    if (Champ $e 'salle') { Salle $e.salle }
    foreach ($o in (Liste (Champ $e 'objets'))) { Motif $o }
    foreach ($l in (Liste (Champ $e 'lumieres'))) { Lumiere $l @($l.p | ForEach-Object { [double]$_ }) }
}

function Vue($e) {
    $v = Champ $e 'vue' (Champ $pl 'vue'); if (-not $v) { return $null }
    $a = @{ position = @(Monde @($v.de | ForEach-Object { [double]$_ })); look_at = @(Monde @($v.vers | ForEach-Object { [double]$_ })); wait = $true }
    if (Champ $v 'fov') { $a.fov = [double]$v.fov }
    return $a
}

# ---------- exécution ----------
try {
    if (-not $Etape -and -not $Verifier) {
        Write-Output ("Plan « {0} » : {1} étapes · origine {2} · cap {3}°" -f $nomPlan, $etapes.Count, ((Liste $pl.origine) -join ';'), (Champ $pl 'cap' 0))
        for ($i = 1; $i -le $etapes.Count; $i++) { $e = $etapes[$i - 1]; Preparer $e $i; Write-Output ("  {0}. {1} : {2} objets, {3} lumières{4}" -f $i, (Champ $e 'nom' ''), $script:items.Count, $script:elems.Count, $(if ($script:salles.Count) { ', salle' } else { '' })) }
        return
    }

    if ($Verifier) {
        foreach ($i in (Etapes $Verifier $etapes.Count)) {
            $e = $etapes[$i - 1]; Preparer $e $i
            $infos = Modeles @($script:items | ForEach-Object { $_.asset } | Where-Object { $_ -like '*.mesh' } | Sort-Object -Unique)
            $inconnus = @($script:items | ForEach-Object { $_.asset } | Where-Object { $_ -like '*.mesh' -and -not $infos.ContainsKey($_.ToLowerInvariant()) } | Sort-Object -Unique)
            $ann = New-Object Collections.Generic.List[object]
            foreach ($it in $script:items) {
                $inf = $infos[$it.asset.ToLowerInvariant()]; if (-not $inf -or @($it.position).Count -lt 3) { continue }
                $s = if ($it.scale -is [array]) { $it.scale } elseif ($it.scale) { @($it.scale, $it.scale, $it.scale) } else { @(1, 1, 1) }
                $rx = [Math]::Max([Math]::Abs($inf.Min[0] * $s[0]), [Math]::Abs($inf.Max[0] * $s[0])); $ry = [Math]::Max([Math]::Abs($inf.Min[1] * $s[1]), [Math]::Abs($inf.Max[1] * $s[1])); $r = [Math]::Max($rx, $ry)
                $p = $it.position
                $ann.Add(@{ type = 'box'; min = @(($p[0] - $r), ($p[1] - $r), ($p[2] + $inf.Min[2] * $s[2])); max = @(($p[0] + $r), ($p[1] + $r), ($p[2] + $inf.Max[2] * $s[2])); color = 'cyan' })
            }
            foreach ($b in $script:salles) {
                $hw = $b.size[0] / 2; $hd = $b.size[1] / 2; $c = $b.center
                $ann.Add(@{ type = 'box'; min = @(($c[0] - $hw), ($c[1] - $hd), $c[2]); max = @(($c[0] + $hw), ($c[1] + $hd), ($c[2] + $b.height)); color = 'yellow' })
                $r = Ncwe 'build_room' ($b + @{ dry_run = $true }); Write-Output ("  salle (simulation) : " + (($r | ConvertTo-Json -Compress -Depth 3).Substring(0, [Math]::Min(200, ($r | ConvertTo-Json -Compress -Depth 3).Length))))
            }
            foreach ($l in $script:elems) { $ann.Add(@{ type = 'point'; position = $l.position; color = 'orange' }) }
            if ($ann.Count) { Ncwe 'annotate' @{ items = @($ann | Select-Object -First 400); duration = 300; clear = $true } | Out-Null }
            Write-Output ("Vérif étape {0} « {1} » : {2} objets, {3} lumières, {4} salle(s) · dessinée dans la vue (5 min)" -f $i, (Champ $e 'nom' ''), $script:items.Count, $script:elems.Count, $script:salles.Count)
            if ($inconnus.Count) { Write-Output ("  MODÈLES INCONNUS : " + ($inconnus -join ', ')) }
            if (-not $SansCapture) { $v = Vue $e; if ($v) { Write-Output ("  capture : " + (Ncwe-Capture (Join-Path $Racine "plans\captures\$nomPlan-$i-verif.png") $v)) } }
        }
        return
    }

    foreach ($i in (Etapes $Etape $etapes.Count)) {
        $e = $etapes[$i - 1]; Preparer $e $i
        $ids = 0
        foreach ($b in $script:salles) { $r = Ncwe 'build_room' $b; $ids += @($r.ids).Count + @($r.created).Count }
        if ($script:items.Count -or $script:elems.Count) {
            $pa = @{ items = $script:items.ToArray() }; if ($script:elems.Count) { $pa.elements = $script:elems.ToArray() }
            $r = Ncwe 'place_objects' $pa
            $ids += @($r.ids).Count + @($r.element_ids).Count
            if (@($r.notes).Count) { Write-Output ("  notes : " + (@($r.notes) -join ' | ')) }
        }
        Write-Output ("Étape {0} « {1} » posée : {2} objets, {3} lumières, {4} salle(s) ({5} créés) · Ctrl+Z annule" -f $i, (Champ $e 'nom' ''), $script:items.Count, $script:elems.Count, $script:salles.Count, $ids)
        if (-not $SansCapture) { $v = Vue $e; if ($v) { Write-Output ("  capture : " + (Ncwe-Capture (Join-Path $Racine "plans\captures\$nomPlan-$i.png") $v)) } }
    }
}
finally { Ncwe-Stop }
