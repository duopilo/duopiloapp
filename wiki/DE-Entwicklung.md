[English](Development) | **Deutsch**

# Entwicklung

Duopilo ist C# auf **.NET 10** (`net10.0-windows`): ein ASP.NET-Core-Host mit dem offiziellen MCP-C#-SDK, eine Tray-App und ein Dashboard im Browser. Die Tests laufen ohne MSFS.

## Bauen und testen

```powershell
git clone https://github.com/duopilo/duopiloapp
cd duopiloapp
dotnet build Duopilo.slnx -c Release
dotnet test Duopilo.slnx -c Release
```

Ohne MSFS-SDK (CI, Linux-Tooling, schnelle Checks) baust du gegen den Stub:

```powershell
dotnet build Duopilo.slnx -c Release -p:UseSimConnectStub=true
dotnet test  Duopilo.slnx -c Release --no-build -p:UseSimConnectStub=true
```

Für einen richtigen Build siehe [Installation → SimConnect-SDK](DE-Installation#1-simconnect-sdk-besorgen). Lokal nach `C:\Tools\Duopilo` deployen: `update.cmd`.

## Projektstruktur

```
src/Duopilo/
  Program.cs              Start: --stdio (Claude Desktop) oder HTTP mit Tray, --console, --generate-secret
  Dashboard/              Dashboard im Browser (HTML, eingebettete Schrift B612), Daten-API
  Desktop/                Tray-Symbol, Autostart
  Speech/                 Sprachausgabe: Windows, Kokoro, Supertonic; Prioritäts-Warteschlange; gemeinsame Aussprache
  Voice/                  Sprachschleife: Mikrofon, Silero VAD, Whisper, Normalisierung, Router, Befehle, Dialog, Checklisten, First Officer
  Smalltalk/              optionaler Smalltalk (Claude, OpenAI-kompatibel, Ollama): lesender Kontext, Grenzen, Prompt
  Input/                  Eingabe-Modul: Planung, Sperren, Bestätigung, Ausführung über SimConnect
  Hosting/                Transport-Auswahl, Konfiguration, HTTP-Host, Absturz-Journal
  Remote/                 Bearer-Token, OAuth-Server, Freigabeseite, Token-Speicher
  Sim/                    SimConnect-Client (eigener Thread, Auto-Reconnect, Cache), SimVar-Katalog, Flugzeugsignale, Verkehrsverbindung
  Aircraft/               Profile laden und zuordnen
  Ecam/                   ECAM-Codes → Klartext
  FlightPlan/             SimBrief-OFP, Route, Fortschritt, ETA, Sprit-Messwerte
  Monitoring/             Regeln (MonitorEngine), Alert-Bus, FMA-Log, Fuel Check
  Traffic/                Verkehrsabfrage, Uhrzeit/Callout, Annäherungs-Scan
  Atc/                    Ingame-ATC lesen, Bahnbezeichner
  Atc/Controller/         Duopilo-ATC: Zustandsmaschine, Vorlagen, Renderer, Readback-Prüfung, Stationen, Stimmen, Speicherung
  Atc/Airspace/           FIR-Grenzen (VAT-Spy), Sektoren, Airlines (OpenFlights)
  Tools/                  MCP-Tools
  Localization/, i18n/    Spracheinstellung, Loc, en.json/de.json
  profiles/               Flugzeugprofile
  atc-templates.json      ATC-Texte
  data/                   Luftraum- und Airline-Daten mit Lizenzen
src/SimConnectStub/       Platzhalter-API für Builds ohne MSFS-SDK
tests/Duopilo.Tests/      xUnit-Tests – laufen ohne MSFS
tools/import_sectors.py   erzeugt data/sectors/atc-sectors.json
installer/                Inno-Setup-Skript und Release-Hilfen
docs/                     Flugzeugnotizen, Tunnel-Anleitung, i18n, Testlogs, Recherche
```

## Mitmachen

- **Branches und Pull Requests:** Nicht auf `main` pushen. Arbeite auf einem Feature-Branch (`feature/…`, `fix/…`, `docs/…`) und öffne einen Pull Request gegen `main`. Der Workflow **Build** baut und testet jeden Push und PR gegen den Stub.
- **Tests:** Für neues Verhalten Tests ergänzen; sie müssen ohne MSFS laufen. Etliche Tests beruhen auf Situationen aus echten Testflügen.
- **Sicherheit zuerst:** Sperrliste und Sperren des Eingabe-Moduls sind Absicht. Änderungen, mit denen Duopilo Triebwerke, Sprit, Feuer, Hydraulik oder die Sim-Steuerung bedienen könnte, werden nicht angenommen.
- **Crew-Sprache:** Callouts, Checklisten, Warnungen und Funk bleiben Englisch; Geschwindigkeiten in Knoten, nie Mach (die Prüfung der Vorlagen erzwingt das).
- **Logs sind Englisch** – nutze `log.`-Schlüssel mit `Loc.Log`.
- **Flugzeugprofile:** Nur Werte als `"verified": true` markieren, die du im Sim geprüft hast.
- **Datenkorrekturen** (FIRs, Frequenzen, Rufzeichen): am besten über **Fehler melden** im Dashboard, mit Quelle.
- **Doku:** README und Doku sind Englisch (deutsche Fassungen optional). Testlogs (`docs/testlog-*.md`) sind reine Fluglogs; Recherche kommt nach `docs/research/`.

Bugs und Datenfehler: [Issues](https://github.com/duopilo/duopiloapp/issues) – die Vorlage füllt das Dashboard vor.

## Übersetzungen

Sprachdateien sind flaches JSON in `src/Duopilo/i18n/<code>.json`. Englisch ist vollständig und dient je Schlüssel als Rückfall.

1. `en.json` nach `<code>.json` kopieren (ISO 639-1, z. B. `fr.json`).
2. `"language.name"` setzen (`"Français"`).
3. Übersetzen; Schlüssel und Platzhalter beibehalten, `log.`-Schlüssel entfernen. Fehlende Schlüssel fallen auf Englisch zurück.
4. Bauen und die Tests laufen lassen (`LocalizationTests` prüft Platzhalter und Vollständigkeit).

Ohne Bauen ausprobieren: Datei nach `%LOCALAPPDATA%\Duopilo\i18n\` legen und Duopilo neu starten. Ganze Anleitung: [docs/i18n.md](https://github.com/duopilo/duopiloapp/blob/main/docs/i18n.md).

## Releases

Ein Tag `v…` startet den Workflow **Release**: self-contained Build für win-x64 mit der Version aus dem Tag, Inno-Setup-Installer und ZIP, angehängt an ein Release als **Entwurf** mit Changelog seit dem letzten Tag. Tags mit Zusatz (`v1.0.0-beta.1`) werden Pre-releases.

```powershell
git switch main; git pull
git tag v0.9.1
git push origin v0.9.1
```

Bewusst veröffentlichen: „Publish release“ auf GitHub oder `gh release edit v0.9.1 --draft=false`.

- **Einmalig vorher:** `pwsh installer/simconnect-secret.ps1` auf einem PC mit dem MSFS-SDK legt die SimConnect-DLLs als Secrets `SIMCONNECT_ZIP_*` ab. Ohne sie bricht ein Release ab, statt gegen den Stub zu bauen.
- **Test-Build ohne Release:** Actions → Release → Run workflow; Setup und ZIP liegen dann 14 Tage unter *Artifacts*.
- Der Produktname steht zentral als `<Product>` in `Directory.Build.props`; Programmname, Datenordner und Konfigurationsabschnitt in `src/Duopilo/Hosting/AppNames.cs`.

## Wiki

Dieses Wiki wird im Repository unter [`wiki/`](https://github.com/duopilo/duopiloapp/tree/main/wiki) gepflegt und automatisch veröffentlicht, sobald Änderungen auf `main` landen. Bearbeite die Seiten dort per Pull Request – Änderungen direkt im GitHub-Wiki werden überschrieben.

Deutsche Seiten heißen `DE-<Name>.md` (z. B. `DE-Sprachschleife.md`) und beginnen mit einem Sprachumschalter zur englischen Seite. Wer eine englische Seite ändert, passt bitte die deutsche mit an – oder vermerkt es im Pull Request.
