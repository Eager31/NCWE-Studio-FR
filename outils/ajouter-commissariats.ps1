# Regroupe les enseignes / portails NCPD trouves (scan-signage_ncpd.tsv) en commissariats,
# nomme chacun par le quartier le plus proche et ajoute la categorie "Commissariats" a lieux.tsv.
# Les points de spawn sont places au sol devant l'enseigne principale (point le plus bas du groupe).
$ErrorActionPreference = 'Stop'
$dir = Split-Path $PSScriptRoot -Parent
$inv = [Globalization.CultureInfo]::InvariantCulture
$scan = Join-Path $dir 'scan-signage_ncpd.tsv'
$hits = Import-Csv $scan -Delimiter "`t" -Encoding UTF8 | Where-Object { $_.ressource -notmatch 'statue' } | ForEach-Object {
    [pscustomobject]@{ X = [double]::Parse($_.x, $inv); Y = [double]::Parse($_.y, $inv); Z = [double]::Parse($_.z, $inv); R = $_.ressource; S = $_.secteur }
}
"$(@($hits).Count) elements NCPD trouves"

# regroupement : tout ce qui est a moins de 120 m appartient au meme site
$groups = New-Object System.Collections.Generic.List[object]
foreach ($h in $hits) {
    $g = $null
    foreach ($c in $groups) { if ([Math]::Sqrt(($c.X - $h.X) * ($c.X - $h.X) + ($c.Y - $h.Y) * ($c.Y - $h.Y)) -lt 120) { $g = $c; break } }
    if (-not $g) { $g = [pscustomobject]@{ X = $h.X; Y = $h.Y; Items = New-Object System.Collections.Generic.List[object] }; $groups.Add($g) }
    $g.Items.Add($h)
    $g.X = ($g.Items | Measure-Object X -Average).Average; $g.Y = ($g.Items | Measure-Object Y -Average).Average
}

# quartier le plus proche d'apres les lieux deja connus (services, planques...)
$known = Get-Content (Join-Path $dir 'lieux.tsv') -Encoding UTF8 | Where-Object { $_ -and $_[0] -ne '#' } | ForEach-Object {
    $c = $_.Split("`t"); if ($c[1] -match ' – (.+?)( \d+)?$') { [pscustomobject]@{ D = $Matches[1]; X = [double]::Parse($c[2], $inv); Y = [double]::Parse($c[3], $inv) } }
}
$lines = New-Object System.Collections.Generic.List[string]
$count = @{}
foreach ($g in ($groups | Sort-Object { -$_.Items.Count })) {
    $best = 'Night City'; $bd = [double]::MaxValue
    foreach ($k in $known) { $d = ($k.X - $g.X) * ($k.X - $g.X) + ($k.Y - $g.Y) * ($k.Y - $g.Y); if ($d -lt $bd) { $bd = $d; $best = $k.D } }
    $count[$best] = 1 + [int]$count[$best]
    $name = "Commissariat NCPD – $best" + $(if ($count[$best] -gt 1) { " $($count[$best])" } else { '' })
    # point de spawn : l'element le plus bas du groupe (niveau de la rue), 1 m au-dessus
    $low = $g.Items | Sort-Object Z | Select-Object -First 1
    $lines.Add(("Commissariats`t{0}`t{1}`t{2}`t{3}" -f $name, $low.X.ToString('0.###', $inv), $low.Y.ToString('0.###', $inv), ($low.Z + 1).ToString('0.###', $inv)))
    '{0,-45} {1,3} elements  ({2:0}, {3:0}, {4:0})' -f $name, $g.Items.Count, $low.X, $low.Y, $low.Z
}

# commissariat abandonne de Pacifica (Phantom Liberty, histoire de rue sts_ep1_10)
$lines.Add("Commissariats`tCommissariat NCPD abandonné – Pacifica (Dogtown)`t-2208.096`t-2271.035`t12.028")

# remplace l'ancienne categorie dans lieux.tsv
$file = Join-Path $dir 'lieux.tsv'
$keep = Get-Content $file -Encoding UTF8 | Where-Object { $_ -notmatch "^Commissariats`t" }
$out = New-Object System.Collections.Generic.List[string]
$inserted = $false
foreach ($l in $keep) { if (-not $inserted -and $l -match "^Charcudocs`t") { $out.AddRange($lines); $inserted = $true }; $out.Add($l) }
if (-not $inserted) { $out.AddRange($lines) }
[IO.File]::WriteAllLines($file, $out, (New-Object Text.UTF8Encoding $false))
"$($lines.Count) commissariats ajoutes a lieux.tsv"
