@echo off
rem Kopilot aktualisieren: neuesten Stand holen, in Zwischenordner bauen, laufende Kopilot-Instanzen beenden,
rem nach C:\Tools\Kopilot kopieren und starten.
rem Aufruf: update.cmd [Zielordner]   (Standard: C:\Tools\Kopilot)

rem Aus einer temporaeren Kopie laufen, damit "git pull" dieses Skript waehrend der Ausfuehrung aendern darf.
if /i not "%~1"=="--run" (
  copy /y "%~f0" "%TEMP%\kopilot-update.cmd" >nul
  "%TEMP%\kopilot-update.cmd" --run "%~dp0." "%~1"
)

setlocal
cd /d "%~2"
set "TARGET=C:\Tools\Kopilot"
if not "%~3"=="" set "TARGET=%~3"
set "STAGE=%TEMP%\kopilot-publish"

echo [1/5] Hole neuesten Stand von GitHub ...
git pull || goto :error

echo [2/5] Baue in Zwischenordner ...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish src\Kopilot -c Release -o "%STAGE%" || goto :error

echo [3/5] Beende laufende Kopilot-Instanzen ...
for /l %%i in (1,1,10) do (
  taskkill /IM Kopilot.exe /F >nul 2>&1
  tasklist /FI "IMAGENAME eq Kopilot.exe" | find /I "Kopilot.exe" >nul || goto :copy
  timeout /t 1 /nobreak >nul
)
echo Kopilot laeuft immer noch - vermutlich startet Claude Desktop seine Instanz sofort neu.
echo Claude Desktop ueber das Tray-Symbol beenden und update.cmd erneut starten.
goto :error

:copy
echo [4/5] Kopiere nach %TARGET% ...
robocopy "%STAGE%" "%TARGET%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :error

echo [5/5] Starte Kopilot ...
start "" "%TARGET%\Kopilot.exe"
echo.
echo Fertig. Kopilot laeuft jetzt ohne Konsolenfenster im Infobereich (Symbol unten rechts).
echo Dashboard: http://127.0.0.1:5155/   Log: %LOCALAPPDATA%\Kopilot\logs\kopilot.log
echo Claude Desktop startet seine eigene Kopilot-Instanz beim naechsten Neustart von Claude Desktop.
pause
exit /b 0

:error
echo.
echo FEHLER - siehe Meldungen oben.
pause
exit /b 1
