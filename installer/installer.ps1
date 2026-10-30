#Requires -Version 7
<#
  Windows-Installer bauen (Inno Setup 6.3 oder neuer).

    pwsh installer/installer.ps1 -Quelle out/Kopilot -Version 0.9.1

  -Quelle   Ordner von "dotnet publish" (self-contained, win-x64)
  -Version  Version wie im Tag, ohne "v": 0.9.1 oder 1.0.0-beta.1
  -Ziel     Ordner für die Setup-Datei (Standard: aktueller Ordner)
  -Name     Produktname (Standard: <Product> aus Directory.Build.props)
  -Exe      Programmdatei (Standard: <AssemblyName> aus src/Kopilot/Kopilot.csproj plus .exe)

  Fehlt Inno Setup, installiert das Skript es in GitHub Actions über Chocolatey (lokal: winget install JRSoftware.InnoSetup).
  In GitHub Actions stehen Pfad und Name der Setup-Datei danach in $env:GITHUB_ENV (SETUP, SETUP_NAME).
#>
param(
    [Parameter(Mandatory = $true)] [string] $Quelle,
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Ziel = ".",
    [string] $Name,
    [string] $Exe
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# Name und Programmdatei aus dem Projekt – eine Stelle für alles.
if (-not $Name -or -not $Exe) {
    $json = dotnet msbuild (Join-Path $repo 'src/Kopilot/Kopilot.csproj') -getProperty:Product -getProperty:AssemblyName
    if ($LASTEXITCODE -ne 0) { Write-Output "::error::Could not read the product name and program file from the project."; exit 1 }
    $props = ($json | Out-String | ConvertFrom-Json).Properties
    if (-not $Name) { $Name = $props.Product }
    if (-not $Exe) { $Exe = "$($props.AssemblyName).exe" }
}

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    Write-Output "::error::Version '$Version' doesn't match 1.2.3 or 1.2.3-beta.1."
    exit 1
}
$teile = $Matches[1..3] | ForEach-Object { [int]$_ }
if ($teile | Where-Object { $_ -gt 65535 }) { Write-Output "::error::Version parts must not be greater than 65535."; exit 1 }
$numerisch = "$($teile -join '.').0"

$quelle = (Resolve-Path $Quelle).Path
if (-not (Test-Path (Join-Path $quelle $Exe))) { Write-Output "::error::$Exe is missing in $quelle."; exit 1 }
$dateien = @(Get-ChildItem $quelle -Recurse -File)
Write-Output "Source: $quelle – $($dateien.Count) files, $([math]::Round(($dateien | Measure-Object Length -Sum).Sum / 1MB)) MB"

# Mitgelieferte Dateien, die man bearbeiten kann. Ihre Prüfsummen kommen als shipped-files.sha256 mit – daran erkennt
# das nächste Update eigene Änderungen und sichert sie, bevor es die Dateien ersetzt (setup.iss, [Code]).
$profilDateien = @(Get-ChildItem (Join-Path $quelle 'profiles') -File -Recurse -Filter '*.json' -ErrorAction SilentlyContinue)
if ($profilDateien.Count -eq 0) { Write-Output "::error::The aircraft profiles are missing in $quelle\profiles."; exit 1 }
if (-not (Test-Path (Join-Path $quelle 'atc-templates.json'))) { Write-Output "::error::atc-templates.json is missing in $quelle."; exit 1 }
$bearbeitbar = @('appsettings.json', 'atc-templates.json' | ForEach-Object { Join-Path $quelle $_ } | Where-Object { Test-Path $_ } | Get-Item) + $profilDateien
$zeilen = $bearbeitbar | ForEach-Object {
    $relativ = [IO.Path]::GetRelativePath($quelle, $_.FullName) -replace '/', '\'
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relativ"
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) "setup-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tmp | Out-Null
$manifest = Join-Path $tmp 'shipped-files.sha256'
[IO.File]::WriteAllLines($manifest, [string[]]$zeilen)
Write-Output "Editable files with checksum: $($zeilen.Count)"

function Find-Iscc {
    $orte = @("${env:ProgramFiles(x86)}", "$env:ProgramFiles", "$env:LOCALAPPDATA\Programs") | Where-Object { $_ }
    $treffer = @($orte | ForEach-Object { Get-ChildItem -Path (Join-Path $_ 'Inno Setup *\ISCC.exe') -ErrorAction SilentlyContinue }) |
        Sort-Object { $_.Directory.Name } -Descending | Select-Object -First 1
    if ($treffer) { return $treffer.FullName }
    $befehl = Get-Command iscc -ErrorAction SilentlyContinue
    if ($befehl) { return $befehl.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc -and $env:GITHUB_ACTIONS -eq 'true') {
    choco install innosetup -y --no-progress
    if ($LASTEXITCODE -ne 0) { Write-Output "::error::Could not install Inno Setup."; exit 1 }
    $iscc = Find-Iscc
}
if (-not $iscc) { Write-Output "::error::Inno Setup (ISCC.exe) not found – winget install JRSoftware.InnoSetup"; exit 1 }
Write-Output "Inno Setup: $iscc"

New-Item -ItemType Directory -Force -Path $Ziel | Out-Null
$ziel = (Resolve-Path $Ziel).Path
$argumente = @(
    "/DAppName=$Name", "/DAppExe=$Exe", "/DAppVersion=$Version", "/DAppVersionNumeric=$numerisch",
    "/DQuelle=$quelle", "/DManifest=$manifest", "/O$ziel"
)
if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY) { $argumente += "/DRepoUrl=$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY" }
& $iscc @argumente (Join-Path $PSScriptRoot 'setup.iss')
$code = $LASTEXITCODE
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
if ($code -ne 0) { Write-Output "::error::Inno Setup failed (see above)."; exit $code }

$setup = Join-Path $ziel "$Name-$Version-Setup.exe"
if (-not (Test-Path $setup)) { Write-Output "::error::$setup is missing after the build."; exit 1 }
$groesse = (Get-Item $setup).Length
Write-Output "Setup: $setup, version $numerisch, $([math]::Round($groesse / 1MB)) MB"
if ($groesse -lt 30MB) {
    Write-Output "::error::The setup file is only $groesse bytes – the program is missing from it."
    exit 1
}
if ($env:GITHUB_ENV) {
    "SETUP=$setup" | Out-File -Append -FilePath $env:GITHUB_ENV -Encoding utf8
    "SETUP_NAME=$(Split-Path -Leaf $setup)" | Out-File -Append -FilePath $env:GITHUB_ENV -Encoding utf8
}
