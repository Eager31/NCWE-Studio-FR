# Crée un kit : les modèles réellement utilisés par le jeu autour d'un point, par famille, avec leurs apparences.
#   powershell -File kit.ps1 -Nom japantown-restaurant [-Rayon 40] [-Centre "x;y;z"] [-Lieu "Nom dans lieux.tsv"]
# Sans -Centre ni -Lieu : autour de la caméra de NCWE. Résultat : kits\<Nom>.tsv (modifiable à la main).
param(
    [Parameter(Mandatory = $true)][string]$Nom,
    [double]$Rayon = 40,
    [string]$Centre,
    [string]$Lieu,
    [ValidateSet('all', 'game', 'project')][string]$Source = 'game'
)
. "$PSScriptRoot\lib.ps1"
try {
    # ---- centre ----
    if ($Lieu) {
        $l = [IO.File]::ReadAllLines((Join-Path (Split-Path $Racine -Parent) 'lieux.tsv'), [Text.Encoding]::UTF8) | Where-Object { $_ -notmatch '^#' } |
            ForEach-Object { $c = $_.Split("`t"); [pscustomobject]@{ Cat = $c[0]; Nom = $c[1]; P = @((P $c[2]), (P $c[3]), (P $c[4])) } } |
            Where-Object { $_.Nom -like "*$Lieu*" } | Select-Object -First 1
        if (-not $l) { throw "Lieu introuvable dans lieux.tsv : $Lieu" }
        $c = $l.P; Write-Output ("Lieu : {0} ({1})" -f $l.Nom, $l.Cat)
        Ncwe 'set_camera' @{ position = @($c[0], $c[1] - 8, $c[2] + 4); look_at = $c; wait = $true } | Out-Null
    }
    elseif ($Centre) { $c = Vec $Centre }
    else { $c = @((Ncwe 'studio_status').camera.position) }
    Ncwe 'wait_for_streaming' @{} | Out-Null

    # ---- objets autour ----
    $r = Ncwe 'query_objects' @{ center = @($c); radius = $Rayon; limit = 2000; source = $Source; show = $false; kinds = @('mesh', 'instanced_mesh', 'entity', 'door', 'light', 'decal') }
    $objs = @($r.objects)
    if ($r.total -gt $objs.Count) { Write-Output ("Attention : {0} objets dans le rayon, seuls les {1} plus proches sont lus (réduisez -Rayon)." -f $r.total, $objs.Count) }

    $groupes = @{}
    $lumieres = New-Object Collections.Generic.List[object]
    foreach ($o in $objs) {
        if ($o.kind -eq 'light') { $lumieres.Add($o); continue }
        $chemin = if ($o.asset) { [string]$o.asset } elseif ($o.resource) { [string]$o.resource } else { $null }
        if (-not $chemin) { continue }
        $k = $chemin.ToLowerInvariant()
        if (-not $groupes.ContainsKey($k)) { $groupes[$k] = [pscustomobject]@{ Chemin = $chemin; Nombre = 0; App = @{}; Ech = @{}; Ex = $o.position } }
        $g = $groupes[$k]; $g.Nombre++
        $a = if ($o.appearance) { [string]$o.appearance } else { 'default' }
        $g.App[$a] = 1 + [int]$g.App[$a]
        if ($o.scale) { $s = (@($o.scale) | ForEach-Object { N $_ 2 }) -join ';'; if ($s -ne '1;1;1') { $g.Ech[$s] = 1 + [int]$g.Ech[$s] } }
    }

    # ---- tailles (cache) ----
    $infos = Modeles @($groupes.Values | Where-Object { $_.Chemin -like '*.mesh' } | ForEach-Object { $_.Chemin })

    # ---- lumières : couleurs et intensités (échantillon) ----
    $lumLignes = @()
    if ($lumieres.Count) {
        $ids = @($lumieres | Select-Object -First 80 | ForEach-Object { $_.id })
        $det = Ncwe 'get_objects' @{ ids = $ids }
        $types = @{}
        foreach ($d in @($det.objects)) {
            $pr = $d.properties; if (-not $pr) { continue }
            $coul = if ($pr.temperature -and $pr.temperature -gt 0) { "k=" + [int]$pr.temperature } else { "rgb=" + ((@($pr.color) | ForEach-Object { N $_ 2 }) -join ';') }
            $cle = "{0}`t{1}" -f $pr.light_type, $coul
            if (-not $types.ContainsKey($cle)) { $types[$cle] = [pscustomobject]@{ N = 0; I = 0.0; R = 0.0 } }
            $t = $types[$cle]; $t.N++; $t.I += [double]$pr.intensity; $t.R += [double]$pr.radius
        }
        foreach ($k in $types.Keys) { $t = $types[$k]; $lumLignes += ("Lumière (élément)`t{0}`t{1}`tintensité≈{2} rayon≈{3}" -f $k, $t.N, [int]($t.I / $t.N), (N ($t.R / $t.N) 1)) }
    }

    # ---- écriture du kit ----
    $lignes = New-Object Collections.Generic.List[string]
    $lignes.Add("# Kit « $Nom » : centre " + ((@($c) | ForEach-Object { N $_ 1 }) -join ';') + ", rayon $Rayon m, source $Source, " + (Get-Date -Format 'yyyy-MM-dd'))
    $lignes.Add("# famille`tmodèle`tnombre`tapparences (les plus utilisées d'abord)`ttaille x;y;z`téchelles vues`texemple de position")
    $tri = $groupes.Values | ForEach-Object { $_ | Add-Member -NotePropertyName Famille -NotePropertyValue (Famille $_.Chemin) -PassThru } | Sort-Object Famille, @{ e = { $_.Nombre }; Descending = $true }
    foreach ($g in $tri) {
        $info = $infos[$g.Chemin.ToLowerInvariant()]
        $taille = if ($info) { ($info.Taille | ForEach-Object { N $_ 2 }) -join ';' } else { '' }
        $apps = ($g.App.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { if ($_.Value -gt 1) { "$($_.Key) x$($_.Value)" } else { $_.Key } }) -join ', '
        $echs = ($g.Ech.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 | ForEach-Object { $_.Key }) -join ' | '
        $lignes.Add(($g.Famille, $g.Chemin, $g.Nombre, $apps, $taille, $echs, ((@($g.Ex) | ForEach-Object { N $_ 1 }) -join ';')) -join "`t")
    }
    foreach ($l in $lumLignes) { $lignes.Add($l) }
    $dossier = Join-Path $Racine 'kits'; [IO.Directory]::CreateDirectory($dossier) | Out-Null
    $fichier = Join-Path $dossier ($Nom + '.tsv')
    [IO.File]::WriteAllLines($fichier, $lignes, (New-Object Text.UTF8Encoding($true)))

    # ---- résumé compact ----
    Write-Output ("Kit {0} : {1} modèles, {2} objets, {3} lumières -> {4}" -f $Nom, $groupes.Count, ($objs.Count - $lumieres.Count), $lumieres.Count, $fichier)
    $tri | Group-Object Famille | Sort-Object Count -Descending | ForEach-Object {
        $top = ($_.Group | Select-Object -First 3 | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Chemin) + " x" + $_.Nombre }) -join ', '
        Write-Output ("  {0} ({1}) : {2}" -f $_.Name, $_.Count, $top)
    }
}
finally { Ncwe-Stop }
