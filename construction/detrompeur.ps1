# Détrompeur de faces pour une pièce déjà construite (sans plan) : les murs, sols et plafonds du projet
# autour du point intérieur ont-ils leur face visible tournée vers l'intérieur ?
#   powershell -File detrompeur.ps1 -Interieur "-1212.99;-567.16;14.5" [-Rayon 15] [-Tout]
#   -Interieur : un point au milieu de la pièce (monde) · -Tout : aussi les objets du jeu (pas seulement le projet)
# Lecture seule : rien n'est modifié. Les objets dont le nom contient « sanstain » sont ignorés (fenêtres sans tain voulues).
param(
    [Parameter(Mandatory = $true)][string]$Interieur,
    [double]$Rayon = 15,
    [switch]$Tout
)
. "$PSScriptRoot\lib.ps1"

$pi = @($Interieur.Split(';') | ForEach-Object { P $_ })
$q = Ncwe-Api 'api.query' @{ center = @($pi); radius = $Rayon; kinds = @('mesh', 'instanced_mesh'); source = $(if ($Tout) { 'all' } else { 'project' }); limit = 2000; show = $false }
$ann = New-Object Collections.Generic.List[object]; $n = 0; $mauvais = 0
foreach ($o in @($q.objects)) {
    if (-not $o.asset -or $o.editable -eq $false) { continue }
    $fam = Famille $o.asset; $bx = $o.bounds
    $fin = $bx -and ([Math]::Min([Math]::Min($bx.size[0], $bx.size[1]), $bx.size[2]) -lt 0.6) -and ([Math]::Max([Math]::Max($bx.size[0], $bx.size[1]), $bx.size[2]) -gt 1.5)
    if ($fam -notin 'Murs', 'Sol', 'Plafond' -and -not $fin) { continue }
    $n++
    if ($o.name -like '*sanstain*') { Write-Output ("{0} : sans tain (voulu)" -f $o.name); continue }
    $r = Maillage-Faces $o.id
    $vers = 0.0; $dos = 0.0
    foreach ($fc in @($r.faces)) {
        $nm = $fc.normal; $c = $(if ($fc.center) { $fc.center } else { $fc.centre })
        if (-not $nm -or -not $c) { continue }
        $a = $(if ($fc.area) { [double]$fc.area } else { 1.0 })
        $d = [double]$nm[0] * ($pi[0] - [double]$c[0]) + [double]$nm[1] * ($pi[1] - [double]$c[1]) + [double]$nm[2] * ($pi[2] - [double]$c[2])
        if ($d -gt 0) { $vers += $a } else { $dos += $a }
    }
    $tot = $vers + $dos
    $verdict = if ($tot -le 0) { 'faces illisibles' } elseif ($vers / $tot -ge 0.8) { 'OK' } elseif ($dos / $tot -ge 0.8) { "À L'ENVERS" } else { 'deux faces / mixte' }
    if ($verdict -eq "À L'ENVERS") { $mauvais++; if ($bx) { $ann.Add(@{ type = 'box'; min = @($bx.min); max = @($bx.max); color = 'red' }) } }
    Write-Output ("{0} [{1}] {2} : {3} (vers l'intérieur {4:P0})" -f $o.name, $o.id, [IO.Path]::GetFileName($o.asset), $verdict, $(if ($tot) { $vers / $tot } else { 0 }))
}
$ann.Add(@{ type = 'point'; position = @($pi); color = 'green' })
try { Ncwe 'annotate' @{ items = $ann.ToArray(); duration = 300; clear = $true } | Out-Null } finally { Ncwe-Stop }
Write-Output ("{0} surfaces contrôlées, {1} à l'envers (encadrées en rouge dans la vue pendant 5 min, point intérieur en vert)." -f $n, $mauvais)
