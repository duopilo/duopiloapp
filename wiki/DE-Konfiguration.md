[English](Configuration-Reference) | **Deutsch**

# Konfiguration

Die meisten Einstellungen änderst du im [Dashboard](DE-Dashboard-und-Einstellungen#einstellungsseite). Diese Seite zeigt, was dahintersteckt.

## Rangfolge

1. Umgebungsvariablen `Duopilo__Section__Key` (z. B. `setx Duopilo__Language de`)
2. `appsettings.Local.json` neben der `Duopilo.exe` – **deine Einstellungen**, Updates überschreiben sie nie
3. `appsettings.json` neben der `Duopilo.exe` – Standardwerte, werden bei Updates erneuert (nicht bearbeiten)

Werte per Kommandozeile überschreiben, z. B. `--Duopilo:Transport=http`, geht auch. Die alten Namen (Abschnitt `"Kopilot"`, `Kopilot__…`) werden weiter gelesen.

## `appsettings.Local.json`

Trag nur ein, was du änderst. Beispiel:

```jsonc
{
  "Duopilo": {
    "Language": "de",
    "SimBrief": { "Username": "your-simbrief-name" },
    "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] },
    "Remote": { "PublicUrl": "https://duopilo.example.com" }
  },
  "Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }
}
```

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Transport` | `http` | `http` (Tray, Dashboard, Remote) oder `stdio` (Claude Desktop nutzt `--stdio`) |
| `Language` | `en` | `en`, `de` – siehe [Sprache](DE-Dashboard-und-Einstellungen#sprache) |
| `HttpUrl` | `http://127.0.0.1:5155` | lokale Adresse von Dashboard und `/mcp` |
| `ProfilesDirectory` | `profiles` | Flugzeugprofile |
| `DataDirectory` | leer | Datenordner; leer = `%LOCALAPPDATA%\Duopilo` |
| `RestartAfterCrash` | `true` | Windows startet die Tray-Instanz nach einem Absturz neu (nach 60 s Laufzeit) |
| `SimBrief:Username` / `PilotId` | leer | [SimBrief](DE-SimBrief-und-Flugplan) |
| `Monitoring:Enabled` | `true` | [Pilot Monitoring](DE-Pilot-Monitoring) |
| `Monitoring:DisabledRules` | `[]` | Regeln, die ganz aus sein sollen |
| `Monitoring:AircraftAutoCallouts` | `false` | Radiohöhen / Minimum / Retard zusätzlich sprechen |
| `Monitoring:Traffic:*` | | [Verkehr](DE-Verkehr#verkehrswarnung-traffic_proximity) |
| `Monitoring:FuelCheck:*` | | [Fuel Check](DE-Pilot-Monitoring#fuel-check) |
| `Input:Enabled` | `false` | [Eingabe-Modul](DE-Eingabe-Modul) – der Schalter im Dashboard hat Vorrang |
| `Input:ConfirmWindowSeconds` | `60` | wie lange eine vorbereitete Aktion gilt |
| `Atc:Enabled`, `Speak`, `SquawkFromSim`, `SessionFile`, `SessionMaxAgeHours`, `TemplatesFile` | | [ATC](DE-ATC#einstellungen) |
| `Speech:Engine` | `auto` | `auto`, `windows`, `kokoro` |
| `Speech:KokoroVoice` | `bm_george` | Standard-Kokoro-Stimme für Callouts |
| `Speech:KokoroThreads` | `2` | CPU-Threads für Kokoro |
| `FirstOfficer:*` | | [First Officer](DE-First-Officer-und-Smalltalk) |
| `Smalltalk:*` | `Provider: off` | [Smalltalk](DE-First-Officer-und-Smalltalk#smalltalk-optional-standardmäßig-aus) |
| `Remote:PublicUrl`, `BearerToken`, `ConsentPassword` | | [Remote-Connector](DE-Claude-verbinden#remote-connector-claudeai-handy-sprachmodus) |
| `Reports:GitHubRepository` | `duopilo/duopiloapp` | Ziel von **Fehler melden**; leer = nur lokal |

## Datenordner `%LOCALAPPDATA%\Duopilo`

| Datei / Ordner | Inhalt |
|---|---|
| `logs\duopilo.log`, `duopilo-stdio.log` | Logs (immer Englisch) |
| `logs\crash.log`, `journal-http.bin` | Absturzberichte und Absturz-Journal |
| `logs\traffic-messages.csv` | Log der Verkehrsantworten |
| `speech.json` | Einstellungen der Sprachausgabe, `mutedRules` |
| `voice-settings.json` | Einstellungen der [Sprachschleife](DE-Sprachschleife#einstellungen-voice-settingsjson) (beim ersten Start aus der Vorlage angelegt) |
| `input.json`, `input-pending.json` | Eingabe-Schalter und offene Aktion, gemeinsam für alle Instanzen |
| `verified-actions.json` | im Sim oder vom Piloten geprüfte Aktionen |
| `first-officer.json` | First Officer des aktuellen Flugs |
| `atc-session.json`, `atc-session-stdio.json` | gesicherte ATC-Sitzung |
| `atc-templates.json` | **deine** [Vorlagen-Overrides](DE-ATC-Vorlagen) |
| `data\…` | **deine** neueren Luftraum-/Airline-Daten |
| `i18n\<code>.json` | **deine** Übersetzungs-Overrides |
| `models\` | Kokoro, Whisper, Silero VAD, Supertonic |
| `ecam\` | zwischengespeicherte ECAM-Texttabellen |
| `oauth-state.json` | Anmeldungen der Claude-Connectoren (löschen = alle widerrufen) |
| `reports\` | gespeicherte Meldungen aus **Fehler melden** |
| `appsettings.Local.backup.json` | Sicherung, bevor das Dashboard Einstellungen schreibt |

## Kommandozeile

| Option | Wirkung |
|---|---|
| *(keine)* | Modus aus `Duopilo:Transport` – standardmäßig HTTP mit Tray-Symbol und Dashboard |
| `--http` | HTTP-Modus erzwingen |
| `--stdio` | stdio-MCP-Server für Claude Desktop (keine Sprachausgabe, keine Sprachschleife) |
| `--console` | mit Konsolenfenster, zur Fehlersuche |
| `--no-tray` | ohne Tray-Symbol |
| `--generate-secret` | gibt einen zufälligen Bearer-Token aus (mit `\| more` aufrufen) |
