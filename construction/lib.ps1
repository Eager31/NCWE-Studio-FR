# Bibliothèque commune des outils de construction NCWE (à charger avec : . "$PSScriptRoot\lib.ps1")
#  - client MCP minimal vers NCWE Studio (déjà ouvert), via ..\ncwe-mcp.cmd
#  - tailles des modèles : infos\tailles-modeles.tsv (tout le jeu, lu par redtool infos), sinon modeles.tsv / asset_info
#  - recherche de modèles par famille, mots et taille, sans appel à NCWE (Chercher-Modeles, Ensemble)
#  - familles d'objets (sol, murs, assises…) d'après le chemin du modèle
#  - repère local d'un plan (origine + cap) <-> monde

$ErrorActionPreference = 'Stop'
$script:Racine = $PSScriptRoot
$script:Inv = [Globalization.CultureInfo]::InvariantCulture
$script:CacheFichier = Join-Path $Racine 'modeles.tsv'
$script:Mcp = $null
$script:McpId = 1

# ---------------- MCP ----------------
function Ncwe-Start {
    if ($script:Mcp -and -not $script:Mcp.HasExited) { return }
    $relais = Join-Path (Split-Path $Racine -Parent) 'ncwe-mcp.cmd'
    $ligne = '/c ""' + $relais + '" --attach-only"'
    $psi = New-Object Diagnostics.ProcessStartInfo -ArgumentList 'cmd.exe', $ligne
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $script:Mcp = [Diagnostics.Process]::Start($psi)
    $script:Mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ncwe-fr-construction","version":"1"}}}')
    [void]$script:Mcp.StandardOutput.ReadLine()
    $script:Mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
}

function Ncwe-Stop { if ($script:Mcp -and -not $script:Mcp.HasExited) { $script:Mcp.Kill() }; $script:Mcp = $null }

# Appelle un outil ; renvoie le contenu brut (texte, ou objet image si -Image).
function Ncwe-Raw([string]$Nom, $Arguments, [switch]$Image) {
    Ncwe-Start
    $script:McpId++
    $req = @{ jsonrpc = '2.0'; id = $script:McpId; method = 'tools/call'; params = @{ name = $Nom; arguments = $Arguments } } | ConvertTo-Json -Depth 20 -Compress
    $script:Mcp.StandardInput.WriteLine($req)
    while ($true) {
        $ligne = $script:Mcp.StandardOutput.ReadLine()
        if ($null -eq $ligne) { throw "NCWE ne répond plus (serveur MCP arrêté)." }
        $o = $ligne | ConvertFrom-Json
        if ($o.id -ne $script:McpId) { continue }
        if ($o.error) { throw ("NCWE {0} : {1}" -f $Nom, $o.error.message) }
        if ($Image) { return ($o.result.content | Where-Object { $_.type -eq 'image' } | Select-Object -First 1) }
        return (($o.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n")
    }
}

# Appelle un outil et renvoie le JSON décodé.
function Ncwe([string]$Nom, $Arguments = @{}) {
    $t = Ncwe-Raw $Nom $Arguments
    try { return ($t | ConvertFrom-Json) } catch { throw ("NCWE {0} : {1}" -f $Nom, $t) }
}

function Ncwe-Capture([string]$Fichier, $Arguments = @{}) {
    if (-not $Arguments.ContainsKey('max_width')) { $Arguments.max_width = 960 }
    $img = Ncwe-Raw 'screenshot' $Arguments -Image
    if (-not $img) { return $null }
    [IO.Directory]::CreateDirectory((Split-Path $Fichier -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($Fichier, [Convert]::FromBase64String($img.data))
    return $Fichier
}

# ---------------- nombres ----------------
function N([double]$v, [int]$dec = 3) { return [Math]::Round($v, $dec).ToString($script:Inv) }
function P([string]$s) { return [double]::Parse($s, $script:Inv) }
function Vec([string]$s) { if (-not $s) { return $null }; return @($s.Split(';') | ForEach-Object { P $_ }) }

# ---------------- cache des modèles ----------------
# modeles.tsv : chemin, taille x;y;z, min x;y;z, max x;y;z, apparences (séparées par ,), apparence par défaut
$script:Cache = $null
function Cache-Charger {
    if ($script:Cache) { return $script:Cache }
    $script:Cache = @{}
    if (Test-Path $CacheFichier) {
        foreach ($l in [IO.File]::ReadAllLines($CacheFichier, [Text.Encoding]::UTF8)) {
            if ($l.Length -eq 0 -or $l[0] -eq '#') { continue }
            $c = $l.Split("`t"); if ($c.Count -lt 6) { continue }
            $script:Cache[$c[0].ToLowerInvariant()] = [pscustomobject]@{ Chemin = $c[0]; Taille = Vec $c[1]; Min = Vec $c[2]; Max = Vec $c[3]; Apparences = @($c[4].Split(',') | Where-Object { $_ }); Defaut = $c[5] }
        }
    }
    return $script:Cache
}

# Renvoie les infos de chaque chemin (asset_info groupé pour ceux qui manquent). Chemin inconnu -> absent du résultat.
function Modeles([string[]]$Chemins) {
    $cache = Cache-Charger
    foreach ($c in $Chemins) {
        if (-not $c) { continue }; $k = $c.ToLowerInvariant()
        if (-not $cache.ContainsKey($k)) { $m = Taille-Jeu $c; if ($m) { $cache[$k] = $m } }
    }
    $manque = @($Chemins | Where-Object { $_ -and -not $cache.ContainsKey($_.ToLowerInvariant()) } | Sort-Object -Unique)
    for ($i = 0; $i -lt $manque.Count; $i += 40) {
        $lot = $manque[$i..([Math]::Min($i + 39, $manque.Count - 1))]
        $r = Ncwe 'asset_info' @{ paths = @($lot) }
        $lignes = New-Object Collections.Generic.List[string]
        foreach ($a in @($r.assets)) {
            if (-not $a.path -or -not $a.size) { continue }
            $o = [pscustomobject]@{ Chemin = $a.path; Taille = @($a.size); Min = @($a.min); Max = @($a.max); Apparences = @($a.appearances); Defaut = [string]$a.default_appearance }
            $cache[$a.path.ToLowerInvariant()] = $o
            $lignes.Add(($a.path, (($a.size | ForEach-Object { N $_ }) -join ';'), (($a.min | ForEach-Object { N $_ }) -join ';'), (($a.max | ForEach-Object { N $_ }) -join ';'), (@($a.appearances) -join ','), $a.default_appearance) -join "`t")
        }
        if ($lignes.Count) {
            if (-not (Test-Path $CacheFichier)) { [IO.File]::WriteAllText($CacheFichier, "# Cache des modèles NCWE (rempli automatiquement) : chemin, taille, min, max, apparences, défaut`r`n", (New-Object Text.UTF8Encoding($true))) }
            [IO.File]::AppendAllLines($CacheFichier, $lignes, (New-Object Text.UTF8Encoding($false)))
        }
    }
    $res = @{}
    foreach ($c in $Chemins) { if ($c -and $cache.ContainsKey($c.ToLowerInvariant())) { $res[$c.ToLowerInvariant()] = $cache[$c.ToLowerInvariant()] } }
    return $res
}

# ---------------- tailles de tout le jeu (infos\tailles-modeles.tsv) ----------------
# chemin, largeur x, profondeur y, hauteur z, min x;y;z  -> mêmes champs que le cache (Taille, Min, Max)
$script:Jeu = $null
# lecture rapide (C#) : dictionnaire chemin (minuscules) -> ligne brute ; analysée seulement quand on s'en sert
if (-not ('NcweFrTailles' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.IO;
public static class NcweFrTailles {
    public static Dictionary<string, string> Lire(string f) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string l in File.ReadLines(f)) { if (l.Length == 0 || l[0] == '#') continue; int t = l.IndexOf('\t'); if (t > 0) d[l.Substring(0, t)] = l; }
        return d;
    }
    public static List<string> Filtrer(Dictionary<string, string> d, string[] mots, string dossier, double hMin, double hMax, double lMin, double lMax) {
        var res = new List<string>(); var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var kv in d) {
            string c = kv.Key.ToLowerInvariant();
            if (dossier != null && !c.StartsWith(dossier)) continue;
            if (c.Contains("proxy") || c.Contains("_lod") || c.Contains("\\_") || c.Contains("shadow") || c.Contains("collision")) continue;
            bool ok = true; foreach (string w in mots) if (!c.Contains(w)) { ok = false; break; } if (!ok) continue;
            string[] p = kv.Value.Split('\t'); if (p.Length < 5) continue;
            double x = double.Parse(p[1], inv), y = double.Parse(p[2], inv), z = double.Parse(p[3], inv), l = Math.Max(x, y);
            if (z < hMin || z > hMax || l < lMin || l > lMax) continue;
            res.Add(kv.Value);
        }
        return res;
    }
}
"@
}
function Ligne-Modele([string]$l) {
    $c = $l.Split("`t"); $taille = @((P $c[1]), (P $c[2]), (P $c[3])); $min = Vec $c[4]
    return [pscustomobject]@{ Chemin = $c[0]; Taille = $taille; Min = $min; Max = @(($min[0] + $taille[0]), ($min[1] + $taille[1]), ($min[2] + $taille[2])); Apparences = @(); Defaut = '' }
}
function Tailles-Jeu {
    if ($script:Jeu) { return $script:Jeu }
    $f = Join-Path (Split-Path $Racine -Parent) 'infos\tailles-modeles.tsv'
    $script:Jeu = if (Test-Path $f) { [NcweFrTailles]::Lire($f) } else { New-Object 'Collections.Generic.Dictionary[string,string]' }
    return $script:Jeu
}
function Taille-Jeu([string]$Chemin) { $l = $null; if ((Tailles-Jeu).TryGetValue($Chemin, [ref]$l)) { return Ligne-Modele $l }; return $null }
# Modèles du jeu par famille (voir Famille), mots du chemin (tous requis) et taille en mètres.
#   Chercher-Modeles -Famille 'Assises' -Mots 'bar','stool' -HauteurMin 0.6 -HauteurMax 0.9
function Chercher-Modeles([string]$Famille, [string[]]$Mots = @(), [double]$HauteurMin = 0, [double]$HauteurMax = 1e9,
                          [double]$LargeurMin = 0, [double]$LargeurMax = 1e9, [string]$Dossier, [int]$Max = 50) {
    $res = New-Object Collections.Generic.List[object]
    $d = if ($Dossier) { $Dossier.ToLowerInvariant() } else { $null }
    foreach ($l in [NcweFrTailles]::Filtrer((Tailles-Jeu), [string[]]@($Mots | ForEach-Object { $_.ToLowerInvariant() }), $d, $HauteurMin, $HauteurMax, $LargeurMin, $LargeurMax)) {
        $m = Ligne-Modele $l
        if ($Famille -and (Famille $m.Chemin) -ne $Famille) { continue }
        $res.Add($m); if ($res.Count -ge $Max) { break }
    }
    return $res
}
# Ensemble d'un modèle : les pièces de son dossier (dans le jeu, un dossier = un ensemble assorti).
function Ensemble([string]$Chemin, [int]$Max = 60) {
    $dossier = Split-Path $Chemin -Parent
    $nom = [IO.Path]::GetFileNameWithoutExtension($Chemin)
    $parts = $nom.Split('_'); $prefixe = if ($parts.Count -gt 2) { ($parts[0..($parts.Count - 2)] -join '_') } else { $nom }
    $res = New-Object Collections.Generic.List[object]
    foreach ($l in [NcweFrTailles]::Filtrer((Tailles-Jeu), [string[]]@(), $dossier.ToLowerInvariant() + '\', 0, 1e9, 0, 1e9)) {
        $m = Ligne-Modele $l
        if ((Split-Path $m.Chemin -Parent) -ne $dossier) { continue }
        $res.Add($m); if ($res.Count -ge $Max) { break }
    }
    return $res
}
# ---------------- effets et sons du jeu (infos\effets.tsv, infos\sons.tsv) ----------------
#   Chercher-Effets -Mots 'steam' -Boucle $true -TailleMax 3
#   Chercher-Sons -Mots 'fan' -Boucle $true -PorteeMax 20
function Lire-Infos([string]$Nom) {
    $f = Join-Path (Split-Path $Racine -Parent) "infos\$Nom"
    if (-not (Test-Path $f)) { return @() }
    return [IO.File]::ReadLines($f) | Where-Object { $_.Length -gt 0 -and $_[0] -ne '#' } | ForEach-Object { , $_.Split("`t") }
}
function Chercher-Effets([string[]]$Mots = @(), $Boucle = $null, [double]$TailleMax = 1e9, [int]$Max = 30) {
    $res = New-Object Collections.Generic.List[object]
    foreach ($c in (Lire-Infos 'effets.tsv')) {
        $p = $c[0].ToLowerInvariant(); $ok = $true; foreach ($w in $Mots) { if (-not $p.Contains($w.ToLowerInvariant())) { $ok = $false; break } }; if (-not $ok) { continue }
        $b = $c[1] -eq 'oui'; if ($null -ne $Boucle -and $b -ne $Boucle) { continue }
        $e = @($c[3].Split(';') | ForEach-Object { P $_ }); if (($e | Measure-Object -Maximum).Maximum -gt $TailleMax) { continue }
        $res.Add([pscustomobject]@{ Chemin = $c[0]; Boucle = $b; Duree = $c[2]; Taille = $e }); if ($res.Count -ge $Max) { break }
    }
    return $res
}
function Chercher-Sons([string[]]$Mots = @(), $Boucle = $null, [double]$PorteeMax = 1e9, [int]$Max = 30) {
    $res = New-Object Collections.Generic.List[object]
    foreach ($c in (Lire-Infos 'sons.tsv')) {
        $p = $c[0].ToLowerInvariant(); $ok = $true; foreach ($w in $Mots) { if (-not $p.Contains($w.ToLowerInvariant())) { $ok = $false; break } }; if (-not $ok) { continue }
        $b = $c[1] -eq 'oui'; if ($null -ne $Boucle -and $b -ne $Boucle) { continue }
        if ((P $c[2]) -gt $PorteeMax) { continue }
        $res.Add([pscustomobject]@{ Evenement = $c[0]; Boucle = $b; Portee = (P $c[2]); DureeMax = (P $c[4]); Etiquettes = $(if ($c.Count -gt 5) { $c[5] } else { '' }) }); if ($res.Count -ge $Max) { break }
    }
    return $res
}
# ---------------- canal direct de NCWE (opérations sans outil MCP : édition de maillage) ----------------
function Ncwe-Api([string]$Op, [hashtable]$Arguments = @{}) {
    $req = @{ op = $Op; client = 'Construction' } + $Arguments
    $p = New-Object IO.Pipes.NamedPipeClientStream('.', 'ncwe-studio', [IO.Pipes.PipeDirection]::InOut)
    try {
        $p.Connect(10000)
        $w = New-Object IO.StreamWriter($p, (New-Object Text.UTF8Encoding($false))); $w.AutoFlush = $true
        $r = New-Object IO.StreamReader($p, [Text.Encoding]::UTF8)
        $w.WriteLine(($req | ConvertTo-Json -Depth 20 -Compress))
        $o = $r.ReadLine() | ConvertFrom-Json
        if ($o.ok -eq $false) { throw ("NCWE {0} : {1}" -f $Op, $o.error) }
        return $o
    } finally { $p.Dispose() }
}

# Édition de maillage d'un objet : begin, opérations, end (une étape d'annulation). Revert si erreur.
#   Maillage-Editer $id { Ncwe-Api 'api.meshedit.two_sided' @{ all = $true } }
function Maillage-Editer([string]$Id, [scriptblock]$Ops) {
    Ncwe-Api 'api.meshedit.begin' @{ id = $Id } | Out-Null
    $ok = $false
    try { $res = & $Ops; $ok = $true; return $res }
    finally {
        if ($ok) { Ncwe-Api 'api.meshedit.end' | Out-Null }
        else { try { Ncwe-Api 'api.meshedit.revert' | Out-Null; Ncwe-Api 'api.meshedit.end' | Out-Null } catch { } }
    }
}

# Faces d'un objet (lecture seule) : liste brute renvoyée par NCWE.
# Ouvrir l'édition d'un objet du jeu le copie dans le projet (étape « Edit mesh ») : on l'annule après lecture.
function Maillage-Faces([string]$Id, [int]$Limite = 5000) {
    $b = Ncwe-Api 'api.meshedit.begin' @{ id = $Id }
    try { return (Ncwe-Api 'api.meshedit.faces' @{ limit = $Limite }) }
    finally {
        try { Ncwe-Api 'api.meshedit.end' | Out-Null } catch { }
        if ($b.id -and $b.id -ne $Id) { Ncwe 'undo' @{} | Out-Null }
    }
}
# Trémie : retire, dans une boîte monde {x0,x1,y0,y1,z0,z1}, ses propres dalles de sol / plafond (centre dedans)
# et découpe les boîtes de collision du projet autour (ascenseurs, escaliers). Deux étapes d'annulation.
function Tremie($G, [string]$Motif = 'int_ent_industrial_a_floor|int_common_a_ceiling_tiles|int_common_techpanel_a_wall') {
    $q = Ncwe-Api 'api.query' @{ min = @($G.x0, $G.y0, $G.z0); max = @($G.x1, $G.y1, $G.z1); source = 'project'; kinds = @('mesh'); limit = 2000; show = $false }
    $m = @($q.objects | Where-Object { $_.asset -match $Motif -and $_.bounds } | Where-Object {
        $cx = ($_.bounds.min[0] + $_.bounds.max[0]) / 2; $cy = ($_.bounds.min[1] + $_.bounds.max[1]) / 2; $cz = ($_.bounds.min[2] + $_.bounds.max[2]) / 2
        $cx -gt $G.x0 -and $cx -lt $G.x1 -and $cy -gt $G.y0 -and $cy -lt $G.y1 -and $cz -gt $G.z0 -and $cz -lt $G.z1 })
    $cx = ($G.x0 + $G.x1) / 2; $cy = ($G.y0 + $G.y1) / 2; $cz = ($G.z0 + $G.z1) / 2
    $qc = Ncwe-Api 'api.query' @{ center = @($cx, $cy, $cz); radius = 60; source = 'project'; kinds = @('collision'); limit = 2000; show = $false }
    $ids = @($qc.objects | Where-Object { $_.name -match 'collision$' } | ForEach-Object { $_.id })
    $det = @(); for ($i = 0; $i -lt $ids.Count; $i += 200) { $det += (Ncwe-Api 'api.object' @{ ids = $ids[$i..([Math]::Min($i + 199, $ids.Count - 1))] }).objects }
    $del = @(); $add = @()
    foreach ($o in $det) {
        if (@($o.shapes).Count -ne 1 -or $o.shapes[0].kind -ne 'box' -or [Math]::Abs([double]$o.rotation.yaw) -gt 0.5) { continue }
        $s = $o.shapes[0].size; $p = $o.position
        $bx0 = $p[0] - $s[0] / 2; $bx1 = $p[0] + $s[0] / 2; $by0 = $p[1] - $s[1] / 2; $by1 = $p[1] + $s[1] / 2; $bz0 = $p[2] - $s[2] / 2; $bz1 = $p[2] + $s[2] / 2
        if ($bx1 -le $G.x0 -or $bx0 -ge $G.x1 -or $by1 -le $G.y0 -or $by0 -ge $G.y1 -or $bz1 -le $G.z0 -or $bz0 -ge $G.z1) { continue }
        $del += $o.id; $parts = @()
        if ($bx0 -lt $G.x0) { $parts += , @($bx0, $G.x0, $by0, $by1) }
        if ($bx1 -gt $G.x1) { $parts += , @($G.x1, $bx1, $by0, $by1) }
        $ix0 = [Math]::Max($bx0, $G.x0); $ix1 = [Math]::Min($bx1, $G.x1)
        if ($by0 -lt $G.y0) { $parts += , @($ix0, $ix1, $by0, $G.y0) }
        if ($by1 -gt $G.y1) { $parts += , @($ix0, $ix1, $G.y1, $by1) }
        foreach ($pp in $parts) {
            $w = $pp[1] - $pp[0]; $dd = $pp[3] - $pp[2]; if ($w -lt 0.05 -or $dd -lt 0.05) { continue }
            $add += @{ name = $o.name; position = @((($pp[0] + $pp[1]) / 2), (($pp[2] + $pp[3]) / 2), $p[2]); shapes = @(@{ kind = 'box'; size = @($w, $dd, $s[2]); preset = $o.shapes[0].preset; material = $o.shapes[0].material }) }
        }
    }
    $tous = @($m | ForEach-Object { $_.id }) + $del
    if ($tous.Count) { Ncwe-Api 'api.delete' @{ ids = $tous } | Out-Null }
    if ($add.Count) { Ncwe 'add_collision' @{ items = $add } | Out-Null }
    return ("trémie : {0} modules retirés, {1} collisions découpées en {2}" -f $m.Count, $del.Count, $add.Count)
}
# ---------------- construction depuis les tracés (outil 📐 : traces.tsv) ----------------
# Lit les lignes tracées dans NCWE et pose tout en une étape :
#   mur : modules de mur, bonne face vers -Interieur ; fenêtre ouverte : même mur retourné (on voit dehors depuis l'intérieur)
#   fenêtre fermée : vitre ; porte : ouverture dans les murs qu'elle croise ; sol / plafond : dalles dans le contour fermé
#   Construire-Traces -Interieur @(x,y,z) [-Noms 'hall*'] [-Verifier]   (-Verifier : dessin seul, rien n'est posé)
$script:TraceMur = 'base\environment\architecture\common\int\int_common_techpanel_a\int_common_techpanel_a_wall_h400_l100_a.mesh'   # 1,04 x 0,25 x 3,8, pivot bout +X, face visible +Y
$script:TraceVitre = 'base\environment\architecture\common\int\int_common_a\int_common_a_wall_glass_l300_w380_a.mesh'
$script:TraceSol = 'base\environment\architecture\common\int\int_ent_industrial_a\int_ent_industrial_a_floor_l300_a.mesh'                 # 3 x 3 x 0,2, pivot coin max, dessus à z pivot
$script:TraceSolPetit = 'base\environment\architecture\common\int\int_ent_industrial_a\int_ent_industrial_a_floor_l100_a.mesh'
$script:TracePlafond = 'base\environment\architecture\common\int\int_common_a\int_common_a_ceiling_tiles_a_l300_w300_a.mesh'

function Lire-Traces([string]$Fichier) {
    if (-not $Fichier) { $Fichier = Join-Path (Split-Path $Racine -Parent) 'traces.tsv' }
    if (-not (Test-Path $Fichier)) { throw "Aucun tracé : $Fichier (outil 📐 dans NCWE)." }
    $res = @()
    foreach ($l in [IO.File]::ReadAllLines($Fichier, [Text.Encoding]::UTF8)) {
        if ($l.Length -eq 0 -or $l[0] -eq '#') { continue }
        $c = $l.Split("`t"); if ($c.Count -lt 4) { continue }
        $pts = @($c[3].Split('|') | ForEach-Object { , @($_.Split(';') | ForEach-Object { P $_ }) })
        $res += [pscustomobject]@{ Nom = $c[0]; Type = $c[1]; Hauteur = (P $c[2]); Points = $pts }
    }
    return $res
}

function DansPolygone([double]$x, [double]$y, $pts) {
    $in = $false; $n = $pts.Count
    for ($i = 0; $i -lt $n; $i++) { $a = $pts[$i]; $b = $pts[($i + 1) % $n]
        if ((($a[1] -gt $y) -ne ($b[1] -gt $y)) -and ($x -lt ($b[0] - $a[0]) * ($y - $a[1]) / ($b[1] - $a[1]) + $a[0])) { $in = -not $in } }
    return $in
}

function DistSegment($p, $a, $b) {
    $dx = $b[0] - $a[0]; $dy = $b[1] - $a[1]; $l2 = $dx * $dx + $dy * $dy
    $t = if ($l2 -gt 0) { [Math]::Max(0, [Math]::Min(1, (($p[0] - $a[0]) * $dx + ($p[1] - $a[1]) * $dy) / $l2)) } else { 0 }
    return [Math]::Sqrt([Math]::Pow($p[0] - ($a[0] + $t * $dx), 2) + [Math]::Pow($p[1] - ($a[1] + $t * $dy), 2))
}

$script:IgnorerRayons = @('s:42e0e66c0ea8c203:669:669:-1')
function Construire-Traces($Interieur, [string]$Noms = '*', [switch]$Verifier, [string]$Fichier, [string[]]$Types = @('mur', 'sol', 'plafond', 'fenêtre ouverte', 'fenêtre fermée'), [string[]]$SansNoms = @(), [double]$Decalage = 0.35) {
    $tous = @(Lire-Traces $Fichier)
    $portes = @($tous | Where-Object { $_.Type -eq 'porte' })
    $contours = @($tous | Where-Object { $_.Type -in 'sol', 'plafond' } | ForEach-Object { , $_.Points })
    $tr = @($tous | Where-Object { $_.Nom -like $Noms -and $_.Type -in $Types -and $SansNoms -notcontains $_.Nom })
    $items = New-Object Collections.Generic.List[object]; $cols = New-Object Collections.Generic.List[object]; $ann = New-Object Collections.Generic.List[object]
    foreach ($l in $tr) {
        $H = $l.Hauteur
        switch -Regex ($l.Type) {
            '^(mur|fenêtre ouverte|fenêtre fermée)$' {
                $vitre = $l.Type -eq 'fenêtre fermée'; $ouverte = $l.Type -eq 'fenêtre ouverte'
                # tracé vertical (coins du bas puis du haut) : ligne au sol = points du bas, hauteur = du bas au haut
                $zs = @($l.Points | ForEach-Object { $_[2] }); $zmin = ($zs | Measure-Object -Minimum).Minimum; $zmax = ($zs | Measure-Object -Maximum).Maximum
                if ($zmax - $zmin -gt 2) {
                    $suites = @(); $cur = @(); foreach ($pp in $l.Points) { if ($pp[2] -le $zmin + 1.2) { $cur += , $pp } else { if ($cur.Count) { $suites += , $cur }; $cur = @() } }; if ($cur.Count) { $suites += , $cur }
                    $pied = @(); $bl = -1; foreach ($su in $suites) { $lg = 0; for ($i = 1; $i -lt $su.Count; $i++) { $lg += [Math]::Sqrt([Math]::Pow($su[$i][0] - $su[$i - 1][0], 2) + [Math]::Pow($su[$i][1] - $su[$i - 1][1], 2)) }; if ($lg -gt $bl) { $bl = $lg; $pied = $su } }
                    $base = $zmin; $H = $zmax - $zmin } else { $pied = $l.Points; $base = $null }
                for ($s = 0; $s -lt $pied.Count - 1; $s++) {
                    $A = $pied[$s]; $B = $pied[$s + 1]
                    $dx = $B[0] - $A[0]; $dy = $B[1] - $A[1]; $long = [Math]::Sqrt($dx * $dx + $dy * $dy); if ($long -lt 0.3) { continue }
                    $ux = $dx / $long; $uy = $dy / $long
                    $mx = ($A[0] + $B[0]) / 2; $my = ($A[1] + $B[1]) / 2
                    # côté intérieur : dans un contour de sol/plafond tracé, sinon vers -Interieur
                    $gx = $mx - $uy * 0.8; $gy = $my + $ux * 0.8; $dxx = $mx + $uy * 0.8; $dyy = $my - $ux * 0.8
                    $inG = $false; $inD = $false; foreach ($pg in $contours) { if (DansPolygone $gx $gy $pg) { $inG = $true }; if (DansPolygone $dxx $dyy $pg) { $inD = $true } }
                    if ($inG -eq $inD) { $gx = $mx - $uy * 4; $gy = $my + $ux * 4; $dxx = $mx + $uy * 4; $dyy = $my - $ux * 4; $inG = $false; $inD = $false; foreach ($pg in $contours) { if (DansPolygone $gx $gy $pg) { $inG = $true }; if (DansPolygone $dxx $dyy $pg) { $inD = $true } } }
                    if ($inG -eq $inD) {
                        # sinon : vers le centre du contour le plus proche
                        $best = $null; $bd = 1e9; foreach ($pg in $contours) { $cxp = ($pg | ForEach-Object { $_[0] } | Measure-Object -Average).Average; $cyp = ($pg | ForEach-Object { $_[1] } | Measure-Object -Average).Average; $dd = [Math]::Pow($cxp - $mx, 2) + [Math]::Pow($cyp - $my, 2); if ($dd -lt $bd) { $bd = $dd; $best = @($cxp, $cyp) } }
                        if ($Interieur) { $best = $Interieur }
                        $inG = if ($best) { (-$uy) * ($best[0] - $mx) + $ux * ($best[1] - $my) -gt 0 } else { $true }; $inD = -not $inG
                    }
                    $gauche = $inG
                    if ($gauche -eq $ouverte) { $tmpA = $A; $A = $B; $B = $tmpA; $ux = -$ux; $uy = -$uy }
                    # doublage décalé vers l'intérieur (pas collé au mur tracé)
                    $nxi = - $uy; $nyi = $ux; if ($ouverte) { $nxi = -$nxi; $nyi = -$nyi }                    $yaw = [Math]::Round([Math]::Atan2($uy, $ux) * 180 / [Math]::PI, 2)
                    $n = [Math]::Max(1, [int][Math]::Ceiling($long / ($(if ($vitre) { 3.0 } else { 1.0 })))); $len = $long / $n
                    $z0 = if ($null -ne $base) { $base } else { [Math]::Min($A[2], $B[2]) }
                    # décalage de chaque module : face intérieure réelle du mur, mesurée à 4 hauteurs, lissée avec les voisins
                    $decs = @(); for ($k = 0; $k -lt $n; $k++) { $decs += $Decalage }
                    if (-not $Verifier -and -not $ouverte) {
                        $rays = @(); foreach ($k in 0..($n - 1)) { $qx = $A[0] + $ux * ($k + 0.5) * $len; $qy = $A[1] + $uy * ($k + 0.5) * $len
                            foreach ($fz in 0.08, 0.35, 0.65, 0.92) { $rays += @{ origin = @(($qx + $nxi * 6), ($qy + $nyi * 6), ($z0 + $H * $fz)); direction = @(-$nxi, -$nyi, 0); max_distance = 8 } } }
                        $res = @(); for ($i = 0; $i -lt $rays.Count; $i += 400) { $res += (Ncwe 'raycast' @{ rays = @($rays[$i..([Math]::Min($i + 399, $rays.Count - 1))]); show = $false; normal = $false; ignore = $script:IgnorerRayons }).results }
                        $brut = @(); for ($k = 0; $k -lt $n; $k++) { $m = $null; for ($j = 0; $j -lt 4; $j++) { $rayon = $res[$k * 4 + $j]; if ($rayon.hit -and $rayon.distance -gt 0.5) { $v = 6 - [double]$rayon.distance; if ($null -eq $m -or $v -gt $m) { $m = $v } } }; $brut += $(if ($null -ne $m) { $m } else { [double]::NaN }) }
                        for ($k = 0; $k -lt $n; $k++) { $vals = @(); foreach ($j in ($k - 1)..($k + 1)) { if ($j -ge 0 -and $j -lt $n -and -not [double]::IsNaN($brut[$j])) { $vals += $brut[$j] } }
                            if ($vals.Count) { $decs[$k] = [Math]::Max(-0.5, [Math]::Max(($vals | Measure-Object -Maximum).Maximum, ($vals | Measure-Object -Average).Average) + 0.05) } }
                    }
                    for ($k = 0; $k -lt $n; $k++) {
                        $ox = $nxi * $decs[$k]; $oy = $nyi * $decs[$k]
                        $cx = $A[0] + $ux * ($k + 0.5) * $len + $ox; $cy = $A[1] + $uy * ($k + 0.5) * $len + $oy
                        $px = $A[0] + $ux * ($k + 1) * $len + $ox; $py = $A[1] + $uy * ($k + 1) * $len + $oy
                        # tranches verticales : tout le mur, ou dessous / dessus d'une porte qui le traverse
                        $tranches = @(, @($z0, ($z0 + $H)))
                        foreach ($pt in $portes) {
                            $pz = @($pt.Points | ForEach-Object { $_[2] }); $p0 = ($pz | Measure-Object -Minimum).Minimum; $p1 = ($pz | Measure-Object -Maximum).Maximum
                            if ($p1 - $p0 -lt 1.5) { $p0 = $z0; $p1 = $z0 + 2.4 }
                            $proche = $false; for ($q = 0; $q -lt $pt.Points.Count - 1; $q++) { if ((DistSegment @($cx, $cy) $pt.Points[$q] $pt.Points[$q + 1]) -lt 0.6) { $proche = $true } }
                            if ($proche) { $nt = @(); foreach ($tr2 in $tranches) { if ($p0 -gt $tr2[0] + 0.2) { $nt += , @($tr2[0], [Math]::Min($p0, $tr2[1])) }; if ($p1 -lt $tr2[1] - 0.2) { $nt += , @([Math]::Max($p1, $tr2[0]), $tr2[1]) } }; $tranches = $nt }
                        }
                        foreach ($tr2 in $tranches) {
                            $hz = $tr2[1] - $tr2[0]; if ($hz -lt 0.15) { continue }
                            $asset = if ($vitre) { $script:TraceVitre } else { $script:TraceMur }; $mod = if ($vitre) { 3.0 } else { 1.04 }
                            $items.Add(@{ asset = $asset; position = @($px, $py, $tr2[0]); rotation = @{ yaw = $yaw; pitch = 0; roll = 0 }; scale = @(($len / $mod), 1, ($hz / 3.8)); ground = 'none'; name = "trace $($l.Nom)" })
                            $cols.Add(@{ name = "trace $($l.Nom)"; position = @($cx, $cy, ($tr2[0] + $hz / 2)); rotation = @{ yaw = $yaw; pitch = 0; roll = 0 }; shapes = @(@{ kind = 'box'; size = @($len, 0.25, $hz) }) })
                        }
                    }
                    $ann.Add(@{ type = 'line'; color = $(if ($ouverte) { 'lime' } elseif ($vitre) { 'green' } else { 'cyan' }); points = @(@($A[0], $A[1], ($z0 + 0.2)), @($B[0], $B[1], ($z0 + 0.2))) })
                }
            }            '^(sol|plafond)$' {
                $pts = $l.Points; $z = ($pts | ForEach-Object { $_[2] } | Measure-Object -Average).Average
                if ($l.Type -eq 'plafond') { $z += $H }
                $xs = $pts | ForEach-Object { $_[0] }; $ys = $pts | ForEach-Object { $_[1] }
                $x0 = ($xs | Measure-Object -Minimum).Minimum; $x1 = ($xs | Measure-Object -Maximum).Maximum; $y0 = ($ys | Measure-Object -Minimum).Minimum; $y1 = ($ys | Measure-Object -Maximum).Maximum
                for ($gx = $x0; $gx -lt $x1; $gx += 3) { for ($gy = $y0; $gy -lt $y1; $gy += 3) {
                    $coins = @(@($gx, $gy), @(($gx + 3), $gy), @($gx, ($gy + 3)), @(($gx + 3), ($gy + 3)))
                    $tous = @($coins | Where-Object { DansPolygone $_[0] $_[1] $pts }).Count -eq 4
                    if ($tous) { $cases = @(, @($gx, $gy, 3.0)) } else { $cases = @(); for ($i = 0; $i -lt 3; $i++) { for ($j = 0; $j -lt 3; $j++) { $cx = $gx + $i + 0.5; $cy = $gy + $j + 0.5; if ($cx -lt $x1 -and $cy -lt $y1 -and (DansPolygone $cx $cy $pts)) { $cases += , @(($gx + $i), ($gy + $j), 1.0) } } } }
                    foreach ($cs in $cases) {
                        $taille = $cs[2]; $asset = if ($l.Type -eq 'plafond') { $script:TracePlafond } elseif ($taille -eq 3) { $script:TraceSol } else { $script:TraceSolPetit }
                        if ($l.Type -eq 'plafond') {
                            $items.Add(@{ asset = $asset; position = @(($cs[0] + $taille / 2), ($cs[1] + $taille / 2), $z); rotation = @{ yaw = 0; pitch = 0; roll = 180 }; scale = @(($taille / 3), ($taille / 3), 1); ground = 'none'; name = "trace $($l.Nom)" })
                        } else {
                            $items.Add(@{ asset = $asset; position = @(($cs[0] + $taille), ($cs[1] + $taille), $z); rotation = @{ yaw = 0; pitch = 0; roll = 0 }; ground = 'none'; name = "trace $($l.Nom)" })
                            $cols.Add(@{ name = "trace $($l.Nom)"; position = @(($cs[0] + $taille / 2), ($cs[1] + $taille / 2), ($z - 0.1)); shapes = @(@{ kind = 'box'; size = @($taille, $taille, 0.2) }) })
                        }
                    } } }
                $ann.Add(@{ type = 'line'; closed = $true; color = $(if ($l.Type -eq 'sol') { 'white' } else { 'yellow' }); points = @($pts | ForEach-Object { , @($_[0], $_[1], ($z + 0.1)) }) })
            }
        }
    }
    if ($Verifier) { Ncwe 'annotate' @{ items = $ann.ToArray(); duration = 600; clear = $true } | Out-Null; return ("tracés : {0} lignes -> {1} objets, {2} collisions (dessin seul)" -f $tr.Count, $items.Count, $cols.Count) }
    $poses = 0
    for ($i = 0; $i -lt $items.Count; $i += 400) {
        $pa = @{ items = @($items.GetRange($i, [Math]::Min(400, $items.Count - $i))) }
        if ($i -eq 0 -and $cols.Count) { $pa.collisions = $cols.ToArray() }
        $r = Ncwe 'place_objects' $pa; $poses += @($r.ids).Count
    }
    return ("tracés : {0} lignes -> {1} objets posés, {2} collisions" -f $tr.Count, $poses, $cols.Count)
}
# ---------------- familles ----------------
$script:Familles = @(
    @('Sol', '\\floor|_floor|\\sol|tatami|carpet|rug'),
    @('Plafond', 'ceiling'),
    @('Murs', 'wall|pillar|column|beam|stairs|railing|fence'),
    @('Portes', '\\doors?\\|_door|door_'),
    @('Lumières', 'light|lamp|neon|lantern|chandelier|spotlight'),
    @('Végétation', '\\vegetation\\|plant|tree|flower|bamboo|bonsai'),
    @('Assises', 'chair|stool|sofa|bench|seat|couch|armchair|bleacher'),
    @('Tables et comptoirs', 'table|desk|counter|bar_|kiosk'),
    @('Rangements', 'shelf|shelves|cabinet|locker|rack|wardrobe|drawer'),
    @('Enseignes et pubs', 'sign|logo|letters|advert|poster|billboard|holo|banner|flag'),
    @('Décalques', '\\decals?\\'),
    @('Électronique', '\\electronics\\|screen|tv_|monitor|computer|speaker'),
    @('Cuisine et vaisselle', 'kitchen|plate|glass|bottle|cup|food|fridge|oven|cutlery'),
    @('Mobilier', '\\furniture\\'),
    @('Architecture', '\\architecture\\'),
    @('Déco', '\\decoration\\'),
    @('Personnages et entités', '\\characters?\\|\\gameplay\\|\\quest')
)
function Famille([string]$Chemin) {
    $c = $Chemin.ToLowerInvariant()
    foreach ($f in $script:Familles) { if ($c -match $f[1]) { return $f[0] } }
    return 'Divers'
}

# ---------------- repère local d'un plan ----------------
# Le plan est en mètres locaux : x vers la droite, y vers l'avant (le « nord » du plan), z vers le haut,
# relatifs à $Origine et tournés de $Cap degrés (sens trigo vu de dessus, comme le yaw de NCWE).
$script:Origine = @(0, 0, 0); $script:Cap = 0.0
function Repere($Origine, [double]$Cap) { $script:Origine = @($Origine | ForEach-Object { [double]$_ }); if ($script:Origine.Count -lt 3) { $script:Origine += 0 }; $script:Cap = $Cap }
function Monde($p) {
    $x = [double]$p[0]; $y = [double]$p[1]; $z = if ($p.Count -gt 2) { [double]$p[2] } else { 0 }
    $a = $script:Cap * [Math]::PI / 180
    return @(
        [Math]::Round($script:Origine[0] + $x * [Math]::Cos($a) - $y * [Math]::Sin($a), 3),
        [Math]::Round($script:Origine[1] + $x * [Math]::Sin($a) + $y * [Math]::Cos($a), 3),
        [Math]::Round($script:Origine[2] + $z, 3))
}
