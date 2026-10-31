[English](Installation) | **Deutsch**

# Installation

## Voraussetzungen

- Windows 10/11 (x64) mit **Microsoft Flight Simulator 2024**
- Zum Bauen aus dem Quellcode: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`) und das **MSFS-2024-SDK** (für SimConnect)
- Optional: ein Mikrofon für die [Sprachschleife](DE-Sprachschleife), ein [SimBrief](https://www.simbrief.com)-Konto für alles, was einen Plan braucht

## Variante A – Installer

> Releases entstehen im Moment als **Entwurf** und sind noch nicht öffentlich. Bis zum ersten öffentlichen Release nimm Variante B.

Der Installer (Inno Setup):

- installiert nach `C:\Program Files\Duopilo` – oder ohne Adminrechte nur für deinen eigenen Benutzer,
- legt einen Startmenü-Eintrag an und auf Wunsch den Autostart (derselbe wie **Mit Windows starten** im Tray-Menü),
- nimmt die Sprache von Windows (sonst Englisch).

Bei Updates bleibt `appsettings.Local.json` unangetastet. Mitgelieferte Profile, `atc-templates.json` und `appsettings.json` werden erneuert; deine eigenen Änderungen daran landen vorher als Sicherung in `backup\<date>\`.

Die Deinstallation entfernt Programm, Startmenü-Eintrag und Autostart und fragt, ob auch die Einstellungen und `%LOCALAPPDATA%\Duopilo` weg sollen.

Außerdem gibt es ein ZIP mit demselben self-contained Build.

**SimConnect** ist weder im Setup noch im ZIP – es gehört zum MSFS-2024-SDK von Microsoft. Duopilo lädt es aus dem installierten SDK (`C:\MSFS 2024 SDK`, die Variable `MSFS2024_SDK` oder der Ordner unter **Einstellungen → SimConnect**). Ohne SDK bietet das Dashboard **MSFS-SDK installieren** an: Duopilo lädt Microsofts Installer direkt von Microsoft, prüft Microsofts Signatur und startet ihn; die Lizenz von Microsoft bestätigst du dort selbst. Danach **Duopilo neu starten**.

## Variante B – Aus dem Quellcode bauen

### 1. SimConnect-SDK besorgen

Duopilo braucht zwei Dateien aus dem MSFS-2024-SDK:

| Datei | Ort im SDK |
|---|---|
| `Microsoft.FlightSimulator.SimConnect.dll` | `SimConnect SDK\lib\managed\` |
| `SimConnect.dll` (nativ, die Verkehrsabfrage nutzt sie direkt) | `SimConnect SDK\lib\` |

Das SDK bekommst du über den **Developer Mode** von MSFS 2024: in den Optionen einschalten, dann in der Entwickler-Leiste über das Menü **Help** den SDK-Installer starten.

Der Build sucht die DLLs in dieser Reihenfolge:

1. `lib\` im Repo (die beiden DLLs einfach hineinkopieren – Git ignoriert sie)
2. Umgebungsvariable `MSFS2024_SDK`
3. `C:\MSFS 2024 SDK\`

Findet er nichts, baut er gegen einen **Stub** – der ist nur für GitHub Actions gedacht. Dieser Build kann sich nicht mit MSFS verbinden; im Build-Log steht dann eine Warnung. Ein richtiger Build schreibt `Duopilo: SimConnect aus …` ins Log.

### 2. Bauen

```powershell
git clone https://github.com/duopilo/duopiloapp
cd duopiloapp
dotnet publish src/Duopilo -c Release -o C:\Tools\Duopilo
```

Danach müssen in `C:\Tools\Duopilo` `Duopilo.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` und der Ordner `profiles` liegen.

### 3. Später aktualisieren

**`update.cmd`** im Repo-Ordner doppelklicken. Das Skript

1. holt den neuesten Stand,
2. baut in einen Zwischenordner,
3. beendet alle laufenden Duopilo-Instanzen (auch die, die Claude Desktop gestartet hat, und alte Kopilot-Instanzen),
4. kopiert nach `C:\Tools\Duopilo` und startet Duopilo neu.

Anderer Zielordner: `update.cmd D:\Duopilo`. `update.cmd` überschreibt nie `appsettings.Local.json` oder etwas in `%LOCALAPPDATA%\Duopilo`.

## Wo was liegt

| Pfad | Inhalt |
|---|---|
| Programmordner (`C:\Program Files\Duopilo` oder `C:\Tools\Duopilo`) | `Duopilo.exe`, `appsettings.json` (Standardwerte), **`appsettings.Local.json`** (deine Einstellungen), `profiles\`, `atc-templates.json`, `data\`, `voice-settings.json` (nur Vorlage) |
| `%LOCALAPPDATA%\Duopilo` | Datenordner: Logs, heruntergeladene Modelle, `speech.json`, `voice-settings.json`, ATC-Sitzung, Anmeldungen, Fehlermeldungen, eigene Overrides für Vorlagen und Daten |

Vollständige Liste: [Konfiguration](DE-Konfiguration).

## Du kommst von Kopilot?

Das meiste wird automatisch übernommen, aber den Claude-Connector musst du neu eintragen. Siehe [Umstieg von Kopilot](DE-Umstieg-von-Kopilot).

**Weiter:** [Schnellstart](DE-Schnellstart)
