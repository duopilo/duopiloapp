@echo off
rem Kopilot aktualisieren: laufenden Kopilot beenden, neuesten Stand holen, neu veroeffentlichen, starten.
rem Aufruf: update.cmd [Zielordner]   (Standard: C:\Tools\Kopilot)
setlocal
cd /d "%~dp0"
set "TARGET=C:\Tools\Kopilot"
if not "%~1"=="" set "TARGET=%~1"

echo [1/4] Beende laufenden Kopilot ...
taskkill /IM Kopilot.exe /F >nul 2>&1

echo [2/4] Hole neuesten Stand von GitHub ...
git pull || goto :error

echo [3/4] Baue und veroeffentliche nach %TARGET% ...
dotnet publish src\Kopilot -c Release -o "%TARGET%" || goto :error

echo [4/4] Starte Kopilot ...
start "Kopilot" "%TARGET%\Kopilot.exe"
echo.
echo Fertig. Falls Claude Desktop Kopilot nutzt: Claude Desktop neu starten.
pause
exit /b 0

:error
echo.
echo FEHLER - siehe Meldungen oben.
pause
exit /b 1
