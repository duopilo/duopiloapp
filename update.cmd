@echo off
rem Duopilo aktualisieren: neuesten Stand holen, in Zwischenordner bauen, laufende Instanzen beenden,
rem nach C:\Tools\Duopilo kopieren und starten.
rem Aufruf: update.cmd [Zielordner]   (Standard: C:\Tools\Duopilo)
rem Bis 0.9.x hiess Duopilo "Kopilot" (C:\Tools\Kopilot): Beim ersten Mal uebernimmt das Skript von dort
rem appsettings.Local.json und eigene Profile. Den Datenordner %LOCALAPPDATA%\Kopilot uebernimmt Duopilo selbst.

rem Aus einer temporaeren Kopie laufen, damit "git pull" dieses Skript waehrend der Ausfuehrung aendern darf.
if /i not "%~1"=="--run" (
  copy /y "%~f0" "%TEMP%\duopilo-update.cmd" >nul
  "%TEMP%\duopilo-update.cmd" --run "%~dp0." "%~1"
)

setlocal
cd /d "%~2"
set "TARGET=C:\Tools\Duopilo"
if not "%~3"=="" set "TARGET=%~3"
set "LEGACY=C:\Tools\Kopilot"
set "STAGE=%TEMP%\duopilo-publish"

echo [1/6] Hole neuesten Stand von GitHub ...
git pull || goto :error

echo [2/6] Baue in Zwischenordner ...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish src\Duopilo -c Release -o "%STAGE%" || goto :error

echo [3/6] Beende laufende Duopilo- und Kopilot-Instanzen ...
for /l %%i in (1,1,10) do (
  taskkill /IM Duopilo.exe /F >nul 2>&1
  taskkill /IM Kopilot.exe /F >nul 2>&1
  tasklist /FI "IMAGENAME eq Duopilo.exe" | find /I "Duopilo.exe" >nul || tasklist /FI "IMAGENAME eq Kopilot.exe" | find /I "Kopilot.exe" >nul || goto :migrate
  timeout /t 1 /nobreak >nul
)
echo Duopilo laeuft immer noch - vermutlich startet Claude Desktop seine Instanz sofort neu.
echo Claude Desktop ueber das Tray-Symbol beenden und update.cmd erneut starten.
goto :error

:migrate
echo [4/6] Uebernehme Einstellungen aus %LEGACY% (falls vorhanden) ...
if /i "%TARGET%"=="%LEGACY%" goto :copy
if not exist "%LEGACY%\Kopilot.exe" goto :copy
if not exist "%TARGET%" mkdir "%TARGET%" || goto :error
if exist "%LEGACY%\appsettings.Local.json" if not exist "%TARGET%\appsettings.Local.json" (
  copy /y "%LEGACY%\appsettings.Local.json" "%TARGET%\appsettings.Local.json" >nul || goto :error
  echo   appsettings.Local.json uebernommen.
)
if exist "%LEGACY%\profiles" (
  rem Nur fehlende Dateien (eigene Profile) - die mitgelieferten kommen gleich neu aus dem Build
  robocopy "%LEGACY%\profiles" "%TARGET%\profiles" /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 goto :error
)
set "MIGRATED=1"

:copy
echo [5/6] Kopiere nach %TARGET% ...
robocopy "%STAGE%" "%TARGET%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :error

echo [6/6] Starte Duopilo ...
start "" "%TARGET%\Duopilo.exe"
echo.
echo Fertig. Duopilo laeuft jetzt ohne Konsolenfenster im Infobereich (Symbol unten rechts).
echo Dashboard: http://127.0.0.1:5155/   Log: %LOCALAPPDATA%\Duopilo\logs\duopilo.log
echo Claude Desktop startet seine eigene Duopilo-Instanz beim naechsten Neustart von Claude Desktop.
if defined MIGRATED (
  echo.
  echo Kopilot heisst jetzt Duopilo. Bitte noch:
  echo  - in claude_desktop_config.json den Pfad auf %TARGET%\Duopilo.exe aendern,
  echo  - den Connector in Claude neu verbinden ^(die Tool-Namen haben sich geaendert^),
  echo  - den alten Ordner %LEGACY% loeschen, wenn alles laeuft.
)
pause
exit /b 0

:error
echo.
echo FEHLER - siehe Meldungen oben.
pause
exit /b 1
