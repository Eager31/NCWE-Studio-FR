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
function Maillage-Faces([string]$Id, [int]$Limite = 5000) {
    Ncwe-Api 'api.meshedit.begin' @{ id = $Id } | Out-Null
    try { return (Ncwe-Api 'api.meshedit.faces' @{ limit = $Limite }) }
    finally { try { Ncwe-Api 'api.meshedit.end' | Out-Null } catch { } }
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
