# Construit lieux.tsv (categorie, nom, x, y, z) a partir de l'extraction des fichiers du jeu :
#   lieux-bruts.tsv        marqueurs de carte relies au journal
#   fast-travel-bruts.tsv  bornes de fast travel trouvees dans les secteurs
# Usage : powershell -File construire-lieux.ps1
$ErrorActionPreference = 'Stop'
$dir = Split-Path $PSScriptRoot -Parent
$inv = [Globalization.CultureInfo]::InvariantCulture

$districts = [ordered]@{
    'wat_kab' = 'Kabuki'; 'wat_lch' = 'Little China'; 'wat_nid' = 'Northside'; 'wat_awf' = 'Arasaka Waterfront'
    'wbr_jpn' = 'Japantown'; 'wbr_hil' = 'Charter Hill'; 'wbr_nok' = 'North Oak'
    'cct_dtn' = 'Downtown'; 'cct_cpz' = 'Corpo Plaza'
    'std_arr' = 'Arroyo'; 'std_rcr' = 'Rancho Coronado'
    'hey_gle' = 'The Glen'; 'hey_rey' = 'Vista del Rey'; 'hey_spr' = 'Wellsprings'
    'pac_cvi' = 'Coast View'; 'pac_wwd' = 'West Wind Estate'
    'bls_' = 'Badlands'; 'ne3_' = 'Badlands'; 'nw4_' = 'Badlands'; 'cz_' = 'Dogtown'
    'wat_' = 'Watson'; 'wbr_' = 'Westbrook'; 'cct_' = 'City Center'; 'std_' = 'Santo Domingo'; 'hey_' = 'Heywood'; 'pac_' = 'Pacifica'
}
function District([string]$id) {
    foreach ($k in $districts.Keys) { if ($id -match "(^|_)$([regex]::Escape($k))") { return $districts[$k] } }
    return $null
}

# type de service d'apres l'identifiant
$services = @(
    @('ripperdoc|rippdoc|ripdoc', 'Charcudocs', 'Charcudoc'),
    @('fixer', 'Fixers', 'Fixer'),
    @('gunsmith|guns|gun_trainer|black_market', 'Armuriers', 'Armurier'),
    @('melee', 'Armes blanches', 'Armes blanches'),
    @('cloth', 'Vêtements', 'Vêtements'),
    @('food|bar|drink', 'Nourriture et boissons', 'Nourriture'),
    @('medic|medical', 'Médecins et pharmacies', 'Médecin'),
    @('netrun', 'Netrunners', 'Netrunner'),
    @('junk', 'Brocanteurs', 'Brocanteur'),
    @('tech', 'Matériel technique', 'Technique'),
    @('prostitute', 'Joytoys', 'Joytoy')
)

$out = New-Object System.Collections.Generic.List[object]
$named = New-Object System.Collections.Generic.List[object]   # points avec quartier connu (pour nommer les bornes)
$count = @{}
function Add-Place($cat, $name, $x, $y, $z) {
    $script:out.Add([pscustomobject]@{ Cat = $cat; Name = $name; X = [double]$x; Y = [double]$y; Z = [double]$z })
}
function Number($cat, $base) { $k = "$cat|$base"; $script:count[$k] = 1 + [int]$script:count[$k]; return $script:count[$k] }

$raw = Import-Csv (Join-Path $PSScriptRoot 'extraction\lieux-bruts.tsv') -Delimiter "`t" -Encoding UTF8
foreach ($r in $raw) {
    $p = $r.chemin -split '/'
    $x = [double]::Parse($r.x, $inv); $y = [double]::Parse($r.y, $inv); $z = [double]::Parse($r.z, $inv)
    $isEp1 = $p[0] -eq 'ep1'; if ($isEp1) { $p = $p[1..($p.Length - 1)] }
    if ($p[0] -ne 'points_of_interest' -or $p.Length -lt 3) { continue }
    $kind = $p[1]; $id = $p[2]
    $dist = District $id; if (-not $dist -and $isEp1) { $dist = 'Dogtown' }
    if ($dist) { $named.Add([pscustomobject]@{ D = $dist; X = $x; Y = $y }) }
    $where = if ($dist) { " – $dist" } else { '' }
    switch -Regex ($kind) {
        '^service_points$' {
            $cat = 'Autres services'; $label = ($id -replace '_', ' ')
            foreach ($s in $services) { if ($id -match $s[0]) { $cat = $s[1]; $label = $s[2]; break } }
            if ($cat -eq 'Autres services') { Add-Place $cat "$label$where" $x $y $z }
            else { Add-Place $cat "$label$where $(Number $cat $where)" $x $y $z }
        }
        '^safehouses$' {
            $n = @{ v_room = 'Appartement de V (Megatower H10)'; kerry_villa = 'Villa de Kerry'; judys_apartment = 'Appartement de Judy'
                    nomad_camp_01 = 'Camp nomade (Aldecaldos)'; nomad_camp_02 = 'Camp nomade 2'; river_trailer_park = 'Caravane de River' }[$id]
            if (-not $n) { $n = "Appartement$where" }
            Add-Place 'Planques et appartements' $n $x $y $z
        }
        '^apartments_buying$' { Add-Place 'Planques et appartements' "Appartement à acheter$where" $x $y $z }
        '^(autofixer_spots|vehicles)$' { Add-Place 'Autofixers et véhicules' "Autofixer$where $(Number 'auto' $where)" $x $y $z }
        '^street_race$' { Add-Place 'Courses de rue' ("Course – " + (($id -replace '^mq056_|_poi$', '') -replace '_', ' ')) $x $y $z }
        '^tarot_collectibles$' { Add-Place 'Tarots' "Tarot$where $(Number 'tarot' $where)" $x $y $z }
        '^(minor_activities|sandbox_activities)$' { Add-Place 'Activités et contrats' "Activité$where $(Number 'act' $where)" $x $y $z }
        '^(street_stories|minor_quests)$' { Add-Place 'Missions de rue' "Mission$where $(Number 'sts' $where)" $x $y $z }
        default { Add-Place 'Autres lieux' ("$kind – $id" -replace '_', ' ') $x $y $z }
    }
}

# bornes de fast travel : noms officiels du jeu (fast-travel-noms.tsv, outil noms.exe)
foreach ($r in Import-Csv (Join-Path $PSScriptRoot 'extraction\fast-travel-noms.tsv') -Delimiter "`t" -Encoding UTF8) {
    $x = [double]::Parse($r.x, $inv); $y = [double]::Parse($r.y, $inv); $z = [double]::Parse($r.z, $inv)
    $n = if ($r.nom_fr) { $r.nom_fr } elseif ($r.nom_en) { $r.nom_en } else { ($r.fiche -replace '^FastTravelPoints\.', '') }
    Add-Place 'Fast travel' $n $x $y $z
}
$order = @('Fast travel', 'Charcudocs', 'Fixers', 'Armuriers', 'Armes blanches', 'Vêtements', 'Nourriture et boissons', 'Médecins et pharmacies',
           'Netrunners', 'Brocanteurs', 'Matériel technique', 'Joytoys', 'Autres services', 'Planques et appartements', 'Autofixers et véhicules',
           'Courses de rue', 'Tarots', 'Activités et contrats', 'Missions de rue', 'Autres lieux')
$sorted = $out | Sort-Object @{ e = { $i = $order.IndexOf($_.Cat); if ($i -lt 0) { 99 } else { $i } } }, Name
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('# Lieux de Cyberpunk 2077 extraits des fichiers du jeu : categorie, nom, x, y, z')
foreach ($p in $sorted) { [void]$sb.AppendLine(('{0}`t{1}`t{2}`t{3}`t{4}' -f $p.Cat, $p.Name, $p.X.ToString('0.###', $inv), $p.Y.ToString('0.###', $inv), $p.Z.ToString('0.###', $inv)).Replace('`t', "`t")) }
[IO.File]::WriteAllText((Join-Path $dir 'lieux.tsv'), $sb.ToString(), (New-Object Text.UTF8Encoding $false))
$sorted | Group-Object Cat | ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
"Total : $($sorted.Count) lieux"
