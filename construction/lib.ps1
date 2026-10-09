# Bibliothèque commune des outils de construction NCWE (à charger avec : . "$PSScriptRoot\lib.ps1")
#  - client MCP minimal vers NCWE Studio (déjà ouvert), via ..\ncwe-mcp.cmd
#  - cache des modèles (modeles.tsv) : un asset_info par modèle, jamais deux
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
