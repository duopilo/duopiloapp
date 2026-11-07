<#
.SYNOPSIS
    Flughafenfeuerwehr von Duopilo ohne MCP-Client testen (lokale Schnittstelle /fire, ab 0.10.0-beta.44.feuerwehr.7).

.EXAMPLE
    .\feuerwehr.ps1                              # Stand
    .\feuerwehr.ps1 -Parking B9                  # Notfall am Boden an Parkposition B9 (Flughafen aus dem SimBrief-Plan)
    .\feuerwehr.ps1 -Parking B9 -Airport EDDL -Watch
    .\feuerwehr.ps1 -Level ground                # Notfall am Boden am eigenen Flugzeug
    .\feuerwehr.ps1 -Level full -Runway 23L      # Mayday: Bereitstellung an 23L
    .\feuerwehr.ps1 -Level local -Tail DUO01     # Pan Pan für ein KI-Flugzeug aus einem Verkehrstest
    .\feuerwehr.ps1 -Cancel                      # einrücken (zurück zur Wache)
    .\feuerwehr.ps1 -Reset                       # alle Fahrzeuge sofort weg
    .\feuerwehr.ps1 -Watch                       # Stand alle 2 s, bis der Einsatz vorbei ist (Strg+C beendet)
#>
[CmdletBinding()]
param(
    [string]$Parking,
    [string]$Airport,
    [string]$Runway,
    [string]$Tail,
    [ValidateSet('local', 'full', 'ground')]
    [string]$Level,
    [switch]$Cancel,
    [switch]$Reset,
    [switch]$Watch,
    [string]$Url = 'http://127.0.0.1:5155'
)

$ErrorActionPreference = 'Stop'

function Invoke-Fire([string]$Method, [string]$Path) {
    try {
        return Invoke-RestMethod -Method $Method -Uri ($Url.TrimEnd('/') + $Path) -TimeoutSec 15
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 404) {
            throw "Duopilo kennt /fire nicht – läuft eine Version ab 0.10.0-beta.44.feuerwehr.7 im Tray (HTTP-Modus)?"
        }

        throw "Duopilo unter $Url nicht erreichbar: $($_.Exception.Message)"
    }
}

function Show-Fire($info) {
    if ($info.error -and -not $info.state) {
        Write-Host "Fehler: $($info.error)" -ForegroundColor Red
        $info = $info.fire
    }

    $line = "Stand: $($info.state)"
    if ($info.level) { $line += " | $($info.level)" }
    if ($info.airport) { $line += " | $($info.airport)" }
    if ($info.runway) { $line += " | Bahn $($info.runway)" }
    if ($info.target) { $line += " | Ziel $($info.target)" }
    if ($info.spawn) { $line += " | Wache $($info.spawn)" }
    Write-Host $line -ForegroundColor Cyan
    if ($info.error) { Write-Host "Fehler: $($info.error)" -ForegroundColor Red }
    if ($info.missing -and $info.missing.Count -gt 0) { Write-Host "Ohne Weg: $($info.missing -join ', ')" -ForegroundColor Yellow }

    if ($info.vehicles -and $info.vehicles.Count -gt 0) {
        $info.vehicles | ForEach-Object {
            [pscustomobject]@{
                Fahrzeug = $_.unit
                Phase    = $_.phase
                kt       = if ($null -ne $_.speedKt) { [math]::Round($_.speedKt) } else { '' }
                'Rest m' = if ($null -ne $_.remainingM) { [math]::Round($_.remainingM) } else { '' }
                'Abst. m' = if ($null -ne $_.gapM) { [math]::Round($_.gapM) } else { '' }
                Modell   = $_.model
            }
        } | Format-Table -AutoSize | Out-String | Write-Host
    }
}

function Get-Query([hashtable]$values) {
    $parts = foreach ($key in $values.Keys) {
        if ($values[$key]) { "$key=$([uri]::EscapeDataString($values[$key]))" }
    }

    if ($parts) { return '?' + ($parts -join '&') } else { return '' }
}

if ($Reset) {
    Show-Fire (Invoke-Fire 'Post' '/fire/reset')
}
elseif ($Cancel) {
    Show-Fire (Invoke-Fire 'Post' '/fire/cancel')
}
elseif ($Parking -or $Level -or $Tail -or $Runway -or $Airport) {
    $query = Get-Query @{ parking = $Parking; airport = $Airport; runway = $Runway; tail = $Tail; level = $Level }
    Show-Fire (Invoke-Fire 'Post' ('/fire/alert' + $query))
}
else {
    Show-Fire (Invoke-Fire 'Get' '/fire')
}

if ($Watch) {
    while ($true) {
        Start-Sleep -Seconds 2
        $info = Invoke-Fire 'Get' '/fire'
        Clear-Host
        Show-Fire $info
        if ($info.state -in @('idle', 'finished', 'unavailable')) { break }
    }
}
