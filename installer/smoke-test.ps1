<#
.SYNOPSIS
  Startet ein veröffentlichtes Duopilo ohne SimConnect und prüft, dass es trotzdem läuft.

.DESCRIPTION
  Release-Pakete enthalten SimConnect nicht (MSFS-SDK), und auf dem Runner gibt es kein SDK – genau die Lage eines Benutzers ohne
  SDK. Duopilo muss dann starten und melden, dass SimConnect fehlt (Banner, Installation anbieten), statt beim Start abzustürzen.

  - stdio (wie Claude Desktop): MCP initialize und duopilo_status werden beantwortet.
  - http (Tray-Instanz): /api/simconnect meldet found=false, /api/dashboard antwortet.

  Fehler erscheinen als ::error:: (GitHub-Annotation) mit den passenden Zeilen aus dem Log.

.EXAMPLE
  pwsh installer/smoke-test.ps1 -Exe out/Duopilo/Duopilo.exe
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("duopilo-smoke-" + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Path $data | Out-Null

# Auf einer Kopie laufen lassen: Duopilo darf neben der exe schreiben (z. B. appsettings.Local.json), das gehört nicht ins Paket
$app = Join-Path $root 'app'
Copy-Item (Split-Path $Exe) $app -Recurse
$Exe = Join-Path $app (Split-Path -Leaf $Exe)

# Kein SDK finden lassen, selbst wenn der Rechner eines hätte (nur dieser Prozess)
Remove-Item Env:MSFS2024_SDK -ErrorAction SilentlyContinue
if (Test-Path 'C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll') {
    Write-Output "::warning::C:\MSFS 2024 SDK ist vorhanden – der Test prüft dann den Start mit SDK statt ohne."
}
foreach ($datei in 'Microsoft.FlightSimulator.SimConnect.dll', 'SimConnect.dll') {
    if (Test-Path (Join-Path (Split-Path $Exe) $datei)) {
        Write-Output "::warning::$datei liegt neben $(Split-Path -Leaf $Exe) – der Test prüft dann nicht den Start ohne SimConnect."
    }
}

$common = @("--Duopilo:DataDirectory=$data", '--Duopilo:Updates:Check=false')
$failed = $false

function Fail([string] $message, [string[]] $details) {
    $script:failed = $true
    $text = (@($message) + ($details | Select-Object -Last 15)) -join '%0A'
    Write-Output "::error::$text"
}

function Relevant([string[]] $lines) {
    $lines | Where-Object { $_ -match 'SimConnect|Exception|CRT|ERR|crash|   at ' } | Select-Object -First 40
}

# ------------------------------------------------------------------ stdio
Write-Output "== stdio =="
$psi = [Diagnostics.ProcessStartInfo]::new($Exe)
foreach ($a in @('--Duopilo:Transport=stdio') + $common) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$p = [Diagnostics.Process]::Start($psi)
$stderr = $p.StandardError.ReadToEndAsync()
$answer = $null
try {
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"duopilo-smoke","version":"1.0"}}}')
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"duopilo_status","arguments":{}}}')
    $p.StandardInput.Flush()

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $pending = $p.StandardOutput.ReadLineAsync()
    while ((Get-Date) -lt $deadline) {
        if ($pending.Wait(1000)) {
            $line = $pending.Result
            if ($null -eq $line) { break }
            if ($line -match '"id"\s*:\s*2\b') { $answer = $line; break }
            $pending = $p.StandardOutput.ReadLineAsync()
        }
        elseif ($p.HasExited) { break }
    }
}
finally {
    try { $p.StandardInput.Close() } catch { }
    if (-not $p.WaitForExit(15000)) { $p.Kill($true) }
}
$errLines = if ($stderr.Wait(5000)) { $stderr.Result -split "`r?`n" } else { @() }
Relevant $errLines | ForEach-Object { Write-Output "  $_" }

if (-not $answer) {
    Fail "stdio: keine Antwort auf duopilo_status (Prozess beendet: $($p.HasExited), Exit-Code: $(if ($p.HasExited) { $p.ExitCode } else { '-' }))." (Relevant $errLines)
}
elseif ($answer -notmatch '"result"') {
    Fail "stdio: duopilo_status mit Fehler beantwortet: $($answer.Substring(0, [Math]::Min(300, $answer.Length)))" @()
}
else {
    Write-Output "stdio: duopilo_status beantwortet ($($answer.Length) Zeichen)."
}

# ------------------------------------------------------------------ http
Write-Output "== http =="
$port = 5199
$url = "http://127.0.0.1:$port"
$psi = [Diagnostics.ProcessStartInfo]::new($Exe)
foreach ($a in @('--Duopilo:Transport=http', "--Duopilo:HttpUrl=$url") + $common) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false
$p = [Diagnostics.Process]::Start($psi)
$sc = $null
$dashboard = $null
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        try { $sc = Invoke-RestMethod "$url/api/simconnect" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 2 }
    }
    if ($sc) {
        $dashboard = Invoke-RestMethod "$url/api/dashboard?since=0&log=false" -TimeoutSec 10
        Start-Sleep -Seconds 5 # SimConnect-Thread anlaufen lassen: ein Ladefehler dort darf Duopilo nicht beenden
    }
}
catch {
    Fail "http: $($_.Exception.Message)" @()
}
finally {
    $alive = -not $p.HasExited
    if ($alive) { $p.Kill($true); $p.WaitForExit(10000) | Out-Null }
}

$log = Join-Path $data 'logs\duopilo.log'
$logLines = if (Test-Path $log) { Get-Content $log } else { @() }
$crash = Join-Path $data 'logs\crash.log'
$crashLines = if (Test-Path $crash) { Get-Content $crash } else { @() }
Relevant $logLines | ForEach-Object { Write-Output "  $_" }

if (-not $sc) {
    Fail "http: /api/simconnect antwortet nicht (Prozess beendet: $($p.HasExited))." ((Relevant $logLines) + (Relevant $crashLines))
}
elseif (-not $alive) {
    Fail "http: Duopilo hat sich während des Tests beendet." ((Relevant $logLines) + (Relevant $crashLines))
}
elseif ($sc.simconnect.found) {
    Write-Output "::warning::http: SimConnect wurde gefunden ($($sc.simconnect.source)) – kein Test ohne SimConnect."
}
elseif ($null -eq $dashboard -or $null -eq $dashboard.simconnect) {
    Fail "http: /api/dashboard ohne simconnect." @()
}
else {
    Write-Output "http: läuft ohne SimConnect, Dashboard meldet found=false, Installation angeboten: $($sc.simconnect.canInstall)."
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
if ($failed) { exit 1 }
Write-Output "Start ohne SimConnect: ok."
