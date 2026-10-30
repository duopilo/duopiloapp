#Requires -Version 7
<#
  SimConnect-DLLs aus dem MSFS-2024-SDK für den Release-Workflow als GitHub-Secrets hinterlegen.
  Einmal ausführen (und nach einem SDK-Update), auf dem PC mit dem SDK, im Repo-Ordner:

    pwsh installer/simconnect-secret.ps1
    pwsh installer/simconnect-secret.ps1 -Sdk "D:\MSFS 2024 SDK"

  Packt Microsoft.FlightSimulator.SimConnect.dll und SimConnect.dll in ein ZIP, kodiert es als Base64 und verteilt es
  auf die Secrets SIMCONNECT_ZIP_1 bis SIMCONNECT_ZIP_6 (GitHub erlaubt höchstens 48 KB pro Secret). Der Workflow setzt
  die Teile wieder zusammen. Die DLLs landen so nicht im Repo.

  Braucht die GitHub CLI (gh), angemeldet mit Schreibrechten auf das Repo.
  SDK-Suche wie beim Build: lib\ im Repo, $env:MSFS2024_SDK, C:\MSFS 2024 SDK.
#>
param(
    [string] $Sdk,
    [string] $Repo
)

$ErrorActionPreference = 'Stop'
$repoOrdner = Split-Path -Parent $PSScriptRoot
$maxTeile = 6
$teilGroesse = 40000

$quellen = @()
if ($Sdk) { $quellen += , @("$Sdk\SimConnect SDK\lib\managed", "$Sdk\SimConnect SDK\lib") }
$quellen += , @("$repoOrdner\lib", "$repoOrdner\lib")
if ($env:MSFS2024_SDK) { $quellen += , @("$env:MSFS2024_SDK\SimConnect SDK\lib\managed", "$env:MSFS2024_SDK\SimConnect SDK\lib") }
$quellen += , @('C:\MSFS 2024 SDK\SimConnect SDK\lib\managed', 'C:\MSFS 2024 SDK\SimConnect SDK\lib')

$managed = $null
$native = $null
foreach ($q in $quellen) {
    $m = Join-Path $q[0] 'Microsoft.FlightSimulator.SimConnect.dll'
    $n = Join-Path $q[1] 'SimConnect.dll'
    if ((Test-Path $m) -and (Test-Path $n)) { $managed = $m; $native = $n; break }
}
if (-not $managed) { throw "SimConnect-DLLs nicht gefunden. SDK-Ordner mit -Sdk angeben." }
Write-Output "SimConnect aus:"
Write-Output "  $managed"
Write-Output "  $native"

$tmp = Join-Path ([IO.Path]::GetTempPath()) "simconnect-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Copy-Item $managed, $native -Destination $tmp
    $zip = Join-Path ([IO.Path]::GetTempPath()) "simconnect-$([guid]::NewGuid().ToString('N')).zip"
    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip -CompressionLevel Optimal
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($zip))
    Remove-Item $zip -Force
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$anzahl = [math]::Ceiling($b64.Length / $teilGroesse)
if ($anzahl -gt $maxTeile) {
    throw "Das ZIP ist zu groß ($($b64.Length) Zeichen Base64, passt in höchstens $maxTeile Secrets). Alternative: DLLs nach lib\ legen und einchecken."
}

$repoArg = @()
if ($Repo) { $repoArg = @('--repo', $Repo) }
for ($i = 1; $i -le $maxTeile; $i++) {
    $secret = "SIMCONNECT_ZIP_$i"
    if ($i -le $anzahl) {
        $teil = $b64.Substring(($i - 1) * $teilGroesse, [math]::Min($teilGroesse, $b64.Length - ($i - 1) * $teilGroesse))
        $teil | gh secret set $secret @repoArg
        if ($LASTEXITCODE -ne 0) { throw "gh secret set $secret ist fehlgeschlagen." }
        Write-Output "$secret gesetzt ($($teil.Length) Zeichen)"
    } else {
        # Reste einer älteren, größeren Fassung entfernen (fehlt das Secret, ist das kein Fehler).
        gh secret delete $secret @repoArg 2>$null | Out-Null
    }
}
Write-Output "Fertig: $anzahl Secret(s). Der nächste Release-Lauf baut mit echtem SimConnect."
