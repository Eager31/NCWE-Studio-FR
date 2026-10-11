# Scan des murs d'un bâtiment, depuis l'intérieur (lecture seule : rien n'est posé ni modifié).
# Des rayons horizontaux partent de points à l'intérieur de chaque contour tracé (sol / plafond de traces.tsv),
# à plusieurs hauteurs, et classent ce qu'ils touchent en premier (maquettes lointaines ignorées) :
#   ok        mur dont la face visible regarde l'intérieur
#   dos       mur vu de dos : invisible en jeu depuis l'intérieur (à doubler, ou voulu)
#   ouverture rien touché : trou vers l'extérieur
#   vitre     surface vitrée
# Les anomalies (dos / ouverture / vitre) sont regroupées en zones numérotées, dessinées dans la vue
# (30 min) et écrites dans scan-zones.tsv : l'utilisateur dit pour chaque zone « extérieur à fermer »,
# « fenêtre ouverte », « porte » ou « normal ».
#   powershell -File scan-murs.ps1 [-Contours 'plafond 15','plafond 18'] [-Pas 2] [-Directions 72]
param(
    [string[]]$Contours = @(),
    [double]$Pas = 2.0,
    [int]$Directions = 72,
    [double]$Portee = 80
)
. "$PSScriptRoot\lib.ps1"
$inv = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentCulture = $inv

$traces = @(Lire-Traces)
$polys = @($traces | Where-Object { $_.Type -in 'sol', 'plafond' -and ($Contours.Count -eq 0 -or $Contours -contains $_.Nom) })
if (-not $polys.Count) { throw "Aucun contour de sol/plafond dans traces.tsv (outil 📐)." }

# maquettes lointaines à ignorer : découvertes au fil des rayons
$ignorer = New-Object Collections.Generic.List[string]; $infos = @{}
function Lancer($rays) {
    for ($passe = 0; $passe -lt 6; $passe++) {
        $res = @(); for ($i = 0; $i -lt $rays.Count; $i += 450) { $res += (Ncwe 'raycast' @{ rays = @($rays[$i..([Math]::Min($i + 449, $rays.Count - 1))]); show = $false; normal = $true; ignore = $ignorer.ToArray() }).results }
        $nouveaux = @($res | Where-Object { $_.hit -and -not $infos.ContainsKey($_.id) } | ForEach-Object { $_.id } | Sort-Object -Unique)
        for ($i = 0; $i -lt $nouveaux.Count; $i += 200) { foreach ($o in (Ncwe-Api 'api.object' @{ ids = @($nouveaux[$i..([Math]::Min($i + 199, $nouveaux.Count - 1))]) }).objects) { $infos[$o.id] = $o } }
        $prox = @($res | Where-Object { $_.hit -and $infos[$_.id] -and ("$($infos[$_.id].kind) $($infos[$_.id].name)" -match 'ProxyMesh|^proxy') } | ForEach-Object { $_.id } | Sort-Object -Unique | Where-Object { -not $ignorer.Contains($_) })
        if (-not $prox.Count) { return $res }
        foreach ($p in $prox) { $ignorer.Add($p) }
    }
    return $res
}

$anomalies = New-Object Collections.Generic.List[object]; $total = 0; $okCount = 0
foreach ($pg in $polys) {
    $pts = $pg.Points
    $zs = @($pts | ForEach-Object { $_[2] }); $zSol = ($zs | Measure-Object -Average).Average
    # bas / haut du volume : un plafond donne le haut ; on cherche le sol tracé qui le recouvre le plus, sinon -20 m
    $zBas = $zSol; $zHaut = $zSol
    if ($pg.Type -eq 'plafond') { $zHaut = $zSol; $sols = @($traces | Where-Object { $_.Type -eq 'sol' }); $zBas = $zHaut - 20
        foreach ($s in $sols) { $c = $s.Points[0]; if ((DansPolygone $c[0] $c[1] $pts) -or (DansPolygone $pts[0][0] $pts[0][1] $s.Points)) { $zz = ($s.Points | ForEach-Object { $_[2] } | Measure-Object -Average).Average; if ($zz -lt $zHaut -and $zz -gt $zBas) { $zBas = $zz } } } }
    else { $zHaut = $zSol + 15 }
    # points d'origine : centre + 4 points à mi-chemin vers les coins extrêmes, gardés s'ils sont dans le contour
    $cx = ($pts | ForEach-Object { $_[0] } | Measure-Object -Average).Average; $cy = ($pts | ForEach-Object { $_[1] } | Measure-Object -Average).Average
    $orig = @(, @($cx, $cy)); foreach ($p in $pts) { $ox = ($cx + $p[0]) / 2; $oy = ($cy + $p[1]) / 2; if (DansPolygone $ox $oy $pts) { $orig += , @($ox, $oy) } }
    $orig = @($orig | Where-Object { DansPolygone $_[0] $_[1] $pts } | Select-Object -First 6)
    $hauteurs = @(); for ($z = $zBas + 1.0; $z -lt $zHaut - 0.3; $z += $Pas) { $hauteurs += $z }
    $rays = @(); $meta = @()
    foreach ($o in $orig) { foreach ($z in $hauteurs) { for ($k = 0; $k -lt $Directions; $k++) { $a = 2 * [Math]::PI * $k / $Directions
        $rays += @{ origin = @($o[0], $o[1], $z); direction = @([Math]::Cos($a), [Math]::Sin($a), 0); max_distance = $Portee }; $meta += , @($o[0], $o[1], $z) } } }
    Write-Output ("{0} : {1} points d'origine × {2} hauteurs ({3:0.#} → {4:0.#} m) × {5} directions = {6} rayons" -f $pg.Nom, $orig.Count, $hauteurs.Count, $zBas, $zHaut, $Directions, $rays.Count)
    $res = Lancer $rays
    for ($i = 0; $i -lt $res.Count; $i++) {
        $h = $res[$i]; $m = $meta[$i]; $total++
        if (-not $h.hit) { $d = $rays[$i].direction; $anomalies.Add([pscustomobject]@{ Type = 'ouverture'; X = $m[0] + $d[0] * 6; Y = $m[1] + $d[1] * 6; Z = $m[2]; Objet = '' }); continue }
        $o = $infos[$h.id]; $nom = if ($o) { "$($o.name)" } else { $h.id }; $asset = if ($o) { "$($o.asset)" } else { '' }
        if ($asset -match 'glass|window|vitre') { $anomalies.Add([pscustomobject]@{ Type = 'vitre'; X = $h.point[0]; Y = $h.point[1]; Z = $h.point[2]; Objet = $nom }); continue }
        if (-not $h.normal) { continue }
        $dot = $h.normal[0] * ($m[0] - $h.point[0]) + $h.normal[1] * ($m[1] - $h.point[1])
        if ($dot -gt 0) { $okCount++ } else { $anomalies.Add([pscustomobject]@{ Type = 'dos'; X = $h.point[0]; Y = $h.point[1]; Z = $h.point[2]; Objet = $nom }) }
    }
}

# regroupement en zones (même type, à moins de 3 m à plat et 3,5 m en hauteur)
$zones = New-Object Collections.Generic.List[object]
foreach ($a in $anomalies) {
    $z = $null; foreach ($zz in $zones) { if ($zz.Type -eq $a.Type -and [Math]::Abs($zz.Z - $a.Z) -lt 3.5 -and [Math]::Sqrt([Math]::Pow($zz.X - $a.X, 2) + [Math]::Pow($zz.Y - $a.Y, 2)) -lt 3) { $z = $zz; break } }
    if (-not $z) { $z = [pscustomobject]@{ Type = $a.Type; X = $a.X; Y = $a.Y; Z = $a.Z; N = 0; Min = @($a.X, $a.Y, $a.Z); Max = @($a.X, $a.Y, $a.Z); Objets = @{} }; $zones.Add($z) }
    $z.N++; $z.X = $z.X + ($a.X - $z.X) / $z.N; $z.Y = $z.Y + ($a.Y - $z.Y) / $z.N; $z.Z = $z.Z + ($a.Z - $z.Z) / $z.N
    $z.Min = @([Math]::Min($z.Min[0], $a.X), [Math]::Min($z.Min[1], $a.Y), [Math]::Min($z.Min[2], $a.Z)); $z.Max = @([Math]::Max($z.Max[0], $a.X), [Math]::Max($z.Max[1], $a.Y), [Math]::Max($z.Max[2], $a.Z))
    if ($a.Objet) { $z.Objets[$a.Objet] = 1 + [int]$z.Objets[$a.Objet] }
}
$zones = @($zones | Where-Object { $_.N -ge 2 } | Sort-Object Type, Z, X)

# dessin et fichier
$couleur = @{ dos = 'red'; ouverture = 'orange'; vitre = 'lime' }
$ann = New-Object Collections.Generic.List[object]; $lignes = New-Object Collections.Generic.List[string]
$lignes.Add("# Zones du scan des murs : numéro, type, centre x;y;z, boîte min / max, rayons, objets touchés, décision (extérieur à fermer / fenêtre ouverte / porte / normal)")
for ($i = 0; $i -lt $zones.Count; $i++) {
    $z = $zones[$i]; $n = $i + 1
    $ann.Add(@{ type = 'box'; min = @(($z.Min[0] - 0.3), ($z.Min[1] - 0.3), ($z.Min[2] - 0.6)); max = @(($z.Max[0] + 0.3), ($z.Max[1] + 0.3), ($z.Max[2] + 0.6)); color = $couleur[$z.Type] })
    $ann.Add(@{ type = 'point'; position = @($z.X, $z.Y, $z.Z); color = $couleur[$z.Type]; label = "$n" })
    $obj = ($z.Objets.Keys | Sort-Object { - $z.Objets[$_] } | Select-Object -First 3) -join ', '
    $lignes.Add(("{0}`t{1}`t{2:0.0};{3:0.0};{4:0.0}`t{5:0.0};{6:0.0};{7:0.0} / {8:0.0};{9:0.0};{10:0.0}`t{11}`t{12}`t" -f $n, $z.Type, $z.X, $z.Y, $z.Z, $z.Min[0], $z.Min[1], $z.Min[2], $z.Max[0], $z.Max[1], $z.Max[2], $z.N, $obj))
}
if ($ann.Count) { Ncwe 'annotate' @{ items = $ann.ToArray(); duration = 1800; clear = $true } | Out-Null }
$fichier = Join-Path (Split-Path $Racine -Parent) 'scan-zones.tsv'
[IO.File]::WriteAllLines($fichier, $lignes, (New-Object Text.UTF8Encoding($false)))
Ncwe-Stop
Write-Output ("{0} rayons : {1} murs vus du bon côté ; {2} zones à valider (rouge = mur vu de dos, orange = ouverture, vert = vitre) -> {3}" -f $total, $okCount, $zones.Count, $fichier)
foreach ($l in $lignes | Select-Object -Skip 1) { $c = $l.Split("`t"); Write-Output ("  zone {0} : {1,-9} vers {2}  ({3} rayons) {4}" -f $c[0], $c[1], $c[2], $c[4], $c[5]) }
