# Kopilot

Kopilot verbindet **Microsoft Flight Simulator 2024** über SimConnect mit **Claude**. Er läuft als MCP-Server auf dem Sim-PC. Claude kann damit Höhe, Geschwindigkeit, Konfiguration, Navigation und Wetter selbst ablesen, statt dass du sie vorlesen musst. Claude ist so der **Pilot Monitoring**, du bleibst Pilot Flying.

> Kopilot liest nur. Er schaltet nichts im Flugzeug.

## Tools

| Tool | Inhalt |
|---|---|
| `kopilot_status` | Verbindung, Pause, geladenes Flugzeug (Titel + Pfad), aktives Profil, Monitoring-, SimBrief- und ECAM-Status |
| `get_flight_state` | Höhe (IND, FL, AGL, Radio), IAS/TAS/GS/VS in kt, Lage, Kurs, Position, Klappen, Fahrwerk, Spoiler, Parkbremse, Autobrake, AP/A-THR mit FMA, Speed- und Höhenziel, Warnungen |
| `get_systems` | Triebwerke (N1, N2, EGT, Fuel Flow in kg/h), APU, Treibstoff, Gewicht, Lichter, Anschnallzeichen, Transponder, COM, Plausibilitätshinweise |
| `get_warnings` | Master Warning/Caution, ECAM-Meldungen im Klartext (FBW A32NX), Stall/Overspeed |
| `get_alerts` | Meldungen des Monitorings seit einer ID, mit Callout zum Vorlesen |
| `wait_for_alerts` | Wartet bis zu 25 s auf die nächste Monitoring-Meldung (Dauerüberwachung in einer Schleife) |
| `get_monitoring_status` | Flugphase, aktive/abgeschaltete Regeln, Ruhezeit |
| `get_flight_plan` | SimBrief-OFP: Route, Bahnen, Reiseflughöhe, Cost Index, Fuel, Gewichte, Zeiten, Navlog |
| `get_progress` | Fortschritt gegen den Plan: nächster Fix, Distanz/ETA, TOD, Querablage, Fuel gegen Plan |
| `get_atc` | Ingame-ATC: Callsign, Freigaben, zugewiesene Bahn (inkl. L/C/R aus SimBrief) |
| `get_navigation` | GPS-Flugplanvariablen des Sims (bei Airliner-FMS meist leer – dann `get_progress`) |
| `get_environment` | Wind, OAT, QNH, eingestellter Höhenmesser, Sicht, UTC-/Ortszeit |
| `get_aircraft_profile` | Bindings des Profils mit Rohwert, Klartext und „geprüft“-Status, Zusatzvariablen, Grenzwerte |
| `read_simvar` | Liest eine beliebige SimVar oder L:-Variable einmalig (Text-Variablen mit `unit: "string"`) |
| `reload_profiles` | Lädt die Profildateien neu |

Jede Antwort enthält `activeAlerts`: die gerade aktiven Monitoring-Meldungen. So bekommt Claude Warnungen auch mit, wenn es eigentlich etwas anderes abfragt.

## Pilot Monitoring

Kopilot prüft alle 200 ms ein paar Regeln und legt Meldungen mit einem kurzen englischen Callout ab:

| Regel | Callout | Bedingung |
|---|---|---|
| `GEAR_SPEED` | Gear speed | Fahrwerk nicht eingefahren und IAS über VLE |
| `FLAP_SPEED` | Flap speed | IAS über VFE der Klappenstellung |
| `OVERSPEED` | Overspeed | IAS über VMO oder Overspeed-Warnung des Sims |
| `STALL` | Stall | Stall-Warnung des Sims in der Luft |
| `SINK_RATE` | Sink rate | unter 2500 ft RA mehr als 1500–3000 ft/min Sinken |
| `BANK_ANGLE` | Bank angle | mehr als 35° (unter 150 ft RA enger, über 2500 ft RA 45°) |
| `MASTER_WARNING` / `MASTER_CAUTION` | Master warning, ENG 1 FIRE | Master-Licht an, mit dem obersten ECAM-Titel |
| `GEAR_DOWN_HIGH` | Gear still down | Fahrwerk draußen im Steigflug über 2000 ft AGL oder über FL150 |
| `SPEED_LIMIT_FL100` | Speed limit | unter 10.000 ft schneller als 250 kt |
| `SPEED_DEVIATION` | Speed | IAS weicht vom Ziel ab (+15/−10 kt, im Anflug +10/−5 kt) und läuft nicht darauf zu |
| `AP_DISCONNECT` | Autopilot off | Autopilot in der Luft getrennt |
| `ALT_1000_TO_GO` | One thousand to go | 1000 ft vor der FCU-Höhe |
| `RUNWAY_ASSIGNED` | Runway two three left assigned | Ingame-ATC hat eine Bahn zugewiesen |
| `SIM_PAUSED` | Sim paused | Sim pausiert – alle anderen Regeln ruhen |

Nach dem Laden eines Flugs, einem Flugzeugwechsel oder einem Air-Start schweigt das Monitoring 10 Sekunden, nach einer Pause 3 Sekunden – dann springen die Werte. Einzelne Regeln schaltest du in `appsettings.Local.json` ab:

```jsonc
"Kopilot": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

**Push:** Claude.ai und der Sprachmodus können keine Nachrichten vom Server empfangen. Claude sieht Meldungen über `activeAlerts` in jeder Antwort oder ruft `wait_for_alerts` in einer Schleife auf. Echte Push-Callouts kommen mit der eigenen Sprachausgabe (Roadmap).

## SimBrief

Die Airliner-FMS (iniBuilds, FlyByWire) geben ihre Route nicht über SimConnect heraus. Kopilot holt deshalb das letzte OFP von SimBrief (öffentliche API, kein Login). In `appsettings.Local.json`:

```jsonc
"Kopilot": { "SimBrief": { "Username": "dein-simbrief-name" } }   // oder "PilotId": "123456"
```

Gewichte und Treibstoff rechnet Kopilot immer in kg um. Der Plan dient auch dazu, bei der vom ATC zugewiesenen Bahn L/C/R zu ergänzen.

## Voraussetzungen

- Windows 10/11 (x64) mit MSFS 2024
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), z. B. `winget install Microsoft.DotNet.SDK.10`
- **MSFS-2024-SDK** (für SimConnect), siehe unten

### SimConnect-SDK

Kopilot braucht zwei Dateien aus dem MSFS-2024-SDK:

- `SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll`
- `SimConnect SDK\lib\SimConnect.dll`

Das SDK bekommst du über den **Developer Mode** von MSFS 2024: Developer Mode in den Optionen einschalten, dann in der Entwickler-Leiste über das Hilfe-Menü den SDK-Installer starten.

Der Build sucht die DLLs in dieser Reihenfolge:

1. `lib\` im Repo (DLLs einfach hineinkopieren)
2. Umgebungsvariable `MSFS2024_SDK`
3. `C:\MSFS 2024 SDK\`

Findet er nichts, baut er gegen einen **Stub**. Das ist nur für GitHub Actions gedacht, eine Verbindung zu MSFS ist damit nicht möglich. Im Build-Log steht dann eine Warnung. Ein richtiger Build zeigt `Kopilot: SimConnect aus …`.

## Bauen

```powershell
git clone https://github.com/Shin-Jin-Jin/Kopilot
cd Kopilot
dotnet publish src/Kopilot -c Release -o C:\Tools\Kopilot
```

**Aktualisieren:** `update.cmd` im Repo-Ordner doppelklicken. Das Skript holt den neuesten Stand, baut in einen Zwischenordner, beendet alle laufenden Kopilot-Instanzen (auch die von Claude Desktop), kopiert nach `C:\Tools\Kopilot` und startet Kopilot neu. Ein anderer Zielordner geht als Parameter: `update.cmd D:\Kopilot`.

Im Ordner `C:\Tools\Kopilot` müssen nach dem Veröffentlichen `Kopilot.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` und der Ordner `profiles` liegen.

## Erster Test im Browser

1. MSFS 2024 starten und einen Flug laden.
2. `C:\Tools\Kopilot\Kopilot.exe` starten. Ohne Parameter läuft der HTTP-Modus (einstellbar über `Kopilot:Transport`).
3. Im Browser öffnen. Die Prüfseiten funktionieren nur lokal, über den Tunnel sind sie gesperrt:
   - <http://127.0.0.1:5155/> zeigt den Status, dort sollte `"connected": true` stehen
   - <http://127.0.0.1:5155/state> zeigt den Flugzustand
   - <http://127.0.0.1:5155/systems>, `/nav`, `/env`, `/profile`
   - `/warnings`, `/alerts`, `/monitoring`, `/atc`, `/plan`, `/progress`
   - `http://127.0.0.1:5155/read?name=INDICATED ALTITUDE&unit=feet` liest eine einzelne Variable

### Prüfliste

Ein paar Werte habe ich ohne Sim nicht prüfen können:

- [ ] `altitude.indicatedFt` und `speed.iasKt` stimmen mit dem PFD überein
- [ ] Gewicht (`grossWeightKg`) stimmt mit der MCDU (FUEL PRED, GW) überein
- [ ] `pitchUpDeg` ist im Steigflug **positiv**, `bankRightDeg` in einer Rechtskurve **positiv**
- [x] Der Transponder-Code stimmt, z. B. `2000` (getestet 28.10.)
- [ ] `flapsHandleIndex` folgt dem Klappenhebel (0 bis 4 bei 0/1/2/3/FULL)
- [x] Beim iniBuilds A321LR sind die GPS-Navigationswerte leer, das FMS speist sie nicht. Route und Distanz kommen aus SimBrief (`get_progress`).
- [x] Beim iniBuilds A321LR passen die Standard-AP-Werte (ALT/HDG/SPD/VS) zum FCU (getestet 28.10.)
- [ ] FBW: Werte mit `"verified": false` im Profil prüfen (siehe [docs/aircraft.md](docs/aircraft.md))
- [ ] Pause wird erkannt (`paused: true` in `get_flight_state`), auch Active Pause
- [ ] ECAM-Klartext im A32NX (`get_warnings`), z. B. nach AP-Disconnect „AUTO FLT AP OFF“

## Mit Claude Desktop verbinden

Claude Desktop startet Kopilot selbst im stdio-Modus. Trag in `%APPDATA%\Claude\claude_desktop_config.json` ein (in Claude Desktop unter Einstellungen → Entwickler → Konfiguration bearbeiten):

```json
{
  "mcpServers": {
    "kopilot": {
      "command": "C:\\Tools\\Kopilot\\Kopilot.exe",
      "args": ["--stdio"]
    }
  }
}
```

Wenn es schon einen `mcpServers`-Block gibt, kommt nur der `kopilot`-Eintrag dazu. Danach Claude Desktop komplett neu starten. Die Logs schreibt Kopilot nach stderr, Claude Desktop legt sie in seinem Log-Ordner ab (`mcp-server-kopilot.log`).

Für den Sprachmodus, claude.ai im Browser und das Handy gibt es den Remote-Betrieb, siehe nächster Abschnitt.

## Remote-Betrieb / Connector in Claude eintragen

Damit Claude Kopilot auch **im Sprachmodus, im Browser und auf dem Handy** nutzen kann, wird der HTTP-Modus über einen Cloudflare Tunnel erreichbar gemacht. Kopilot selbst bleibt auf dem Sim-PC und hört weiterhin nur auf `127.0.0.1`.

```
Claude (Cloud) ──▶ https://kopilot.flo210500.com/mcp ──▶ Cloudflare ──▶ cloudflared (Dienst) ──▶ 127.0.0.1:5155 ──▶ Kopilot ──▶ MSFS
```

### Zugriffsschutz

`/mcp` ist immer geschützt, auch lokal. Ohne gültigen Token antwortet Kopilot mit `401`. Es gibt zwei Zugangswege:

| Weg | Für | Konfiguration |
|---|---|---|
| **OAuth** | Claude-Connector (claude.ai, Desktop, Handy, Sprachmodus) | `PublicUrl` + `ConsentPassword` |
| **Bearer-Token** | Claude Code, MCP Inspector, eigene Skripte | `BearerToken` |

Beim OAuth-Weg registriert sich Claude selbst (Dynamic Client Registration). Beim Verbinden öffnet sich eine **Freigabeseite von Kopilot**, auf der du das Freigabe-Passwort eingibst. Danach erneuert Claude seinen Zugang selbst: Access-Token 1 Stunde, Refresh-Token 90 Tage, bei jeder Erneuerung ausgetauscht. Nach fünf falschen Passwörtern ist die Freigabe 15 Minuten gesperrt.

Die Prüfseiten (`/`, `/state`, …) sind nur lokal erreichbar. Kopilot nimmt nur Anfragen an `localhost` und an den Hostnamen aus `PublicUrl` an.

### 1. Geheimnisse anlegen

```bat
C:\Tools\Kopilot\Kopilot.exe --generate-secret
```

Das gibt einen zufälligen Bearer-Token aus. Das Freigabe-Passwort denkst du dir selbst aus, mindestens 12 Zeichen.

Dann `appsettings.Local.example.json` im Ordner `C:\Tools\Kopilot` nach **`appsettings.Local.json`** kopieren und ausfüllen:

```json
{
  "Kopilot": {
    "Transport": "http",
    "Remote": {
      "PublicUrl": "https://kopilot.flo210500.com",
      "BearerToken": "<Ausgabe von --generate-secret>",
      "ConsentPassword": "<dein Freigabe-Passwort>"
    }
  }
}
```

Die Datei wird nie eingecheckt und von `update.cmd` nicht überschrieben. Statt der Datei gehen auch Umgebungsvariablen, die haben Vorrang:

```bat
setx Kopilot__Remote__BearerToken "<Token>"
setx Kopilot__Remote__ConsentPassword "<Passwort>"
```

### 2. Kopilot im HTTP-Modus starten

`C:\Tools\Kopilot\Kopilot.exe` starten. Im Log muss stehen:

```
Zugriffsschutz: Bearer-Token aktiv.
Zugriffsschutz: OAuth aktiv. Connector-URL für Claude: https://kopilot.flo210500.com/mcp
```

Fehlt eine der Zeilen, sagt eine Warnung direkt darunter, was nicht stimmt.

> Die Kopilot-Instanz, die Claude Desktop per `--stdio` startet, bedient kein HTTP. Für den Remote-Betrieb muss Kopilot zusätzlich im HTTP-Modus laufen. Beide Instanzen können gleichzeitig mit MSFS verbunden sein.

### 3. Cloudflare Tunnel einrichten

Schritt für Schritt in **[docs/cloudflared.md](docs/cloudflared.md)**: Named Tunnel, Subdomain `kopilot.flo210500.com`, cloudflared als Windows-Dienst, optional eine WAF-Regel, die `/mcp` nur für Claudes IP-Bereich öffnet.

### 4. Connector in Claude eintragen

1. In Claude **Customize → Connectors** öffnen, dann **Add custom connector**. Bei Team- und Enterprise-Plänen trägt das ein Owner unter *Organization settings → Connectors* ein.
2. Name `Kopilot`, URL **`https://kopilot.flo210500.com/mcp`**.
3. Falls der Dialog danach fragt:
   - Authentication: **Sign in now**
   - OAuth client: **Register automatically**. „Use Claude's published identity“ (CIMD) unterstützt Kopilot nicht.
4. **Add**, dann **Connect**. Es öffnet sich die Kopilot-Freigabeseite. Dort steht als Ziel `claude.ai`. Passwort eingeben, **Freigeben**.
5. In einem neuen Chat über **+ → Connectors** Kopilot einschalten und fragen: „Wie ist unser Flugzustand?“

Der Connector gilt für dein ganzes Konto, also auch für Desktop und Handy. Ob er im Sprachmodus auftaucht, ist noch ungetestet.

### Claude Code und andere Clients (Bearer-Token)

Auf dem Sim-PC direkt, ohne Tunnel:

```bat
claude mcp add --transport http kopilot http://127.0.0.1:5155/mcp --header "Authorization: Bearer <Token>"
```

Von woanders geht `https://kopilot.flo210500.com/mcp` mit demselben Header. Das klappt aber nicht, wenn die optionale WAF-Regel aktiv ist.

### Zugang widerrufen

- **Alle Claude-Verbindungen trennen:** Kopilot beenden, `%LOCALAPPDATA%\Kopilot\oauth-state.json` löschen und Kopilot neu starten. Jeder Connector muss sich danach neu anmelden.
- **Bearer-Token wechseln:** neuen Token erzeugen, in `appsettings.Local.json` eintragen und Kopilot neu starten.
- **Alles sofort zu:** den Tunnel-Dienst stoppen (`sc stop cloudflared`).

Kopilot speichert Tokens nur als SHA-256-Hash. Tokens in der URL (`?token=`) nimmt er grundsätzlich nicht an.

## Flugzeugprofile und L:-Variablen

Study-Level-Flugzeuge legen vieles (AP-Status, FMA, Autobrake, ECAM, Fuel Flow) nicht in Standard-SimVars ab, sondern in eigenen **L:-Variablen**. Ein Profil unter `profiles\*.json` sagt Kopilot, woher die Werte kommen. Mitgeliefert: FlyByWire A32NX, FlyByWire A380X, iniBuilds A321LR. Was bei welchem Flugzeug geht, steht in [docs/aircraft.md](docs/aircraft.md).

```jsonc
{
  "name": "FlyByWire A32NX",
  "match": [
    { "path": [ "FlyByWire", "A320" ] },     // aircraft.cfg-Pfad enthält alle Texte (zählt vor dem Titel)
    { "title": [ "A32NX" ] }                 // oder der TITLE enthält alle Texte
  ],
  "bindings": {
    "ap1": { "name": "L:A32NX_AUTOPILOT_1_ACTIVE", "type": "bool", "verified": true },
    "fmaVertical": {
      "name": "L:A32NX_FMA_VERTICAL_MODE", "type": "enum", "verified": false,
      "values": { "14": "V/S", "22": "CLB" }
    },
    "fuelFlowKgh": { "name": "L:A32NX_ENGINE_FF:{i}", "type": "number" }   // {i} = Triebwerk 1–4
  },
  "ecam": { "format": "fbw-a32nx", "leftLines": "L:A32NX_EWD_LOWER_LEFT_LINE_{n}", "rightLines": "L:A32NX_EWD_LOWER_RIGHT_LINE_{n}", "lines": 7 },
  "limits": { "vmoKt": 350, "vleKt": 280, "vfeKt": { "1": 230, "2": 200, "3": 185, "4": 177 } },
  "variables": [ { "key": "vapp", "name": "L:A32NX_SPEEDS_VAPP", "unit": "number" } ]
}
```

- **Bindings** sind feste Schlüssel, die Tools und Monitoring verstehen: `ap1`, `ap2`, `flightDirector`, `athrStatus`, `athrMode`, `fmaLateral`, `fmaLateralArmed`, `fmaVertical`, `fmaVerticalArmed`, `autobrake`, `masterWarning`, `masterCaution`, `speedTarget`, `speedManaged`, `selectedSpeed`, `machMode`, `selectedAltitude`, `vls`, `fuelFlowKgh`. Typen: `number`, `bool`, `enum` (Wert → Text), `bitmask` (Bit → Text), `string`. `scale` rechnet um (z. B. lbs/h → kg/h = 0.45359237).
- **variables** sind freie Zusatzwerte, die `get_aircraft_profile` roh ausgibt.
- **limits** nutzt das Monitoring (Gear/Flap Speed, Overspeed, 250 kt unter FL100).

**ECAM-Klartext:** Das A32NX schreibt nur Codes (z. B. `220000501`) in die EWD-Variablen. Die Texte stehen in der FlyByWire-Tabelle `EwdMessages.ts` (GPL-3.0). Kopilot lädt sie beim ersten Bedarf von GitHub, speichert sie unter `%LOCALAPPDATA%\Kopilot\ecam\` und erneuert sie alle 14 Tage. Ohne Internet bleibt der Cache gültig. Die Tabelle liegt bewusst nicht im Repo.

So findest du neue L:-Variablen:

1. Im Developer Mode das Fenster mit den Behaviors / LocalVariables des Flugzeugs öffnen. Dort sind alle L:-Variablen mit ihren aktuellen Werten gelistet.
2. Im Cockpit etwas umschalten (z. B. NAV am FCU) und schauen, welche Variable sich ändert.
3. Gegenprobe mit `read_simvar` oder im Browser: `/read?name=L:NAME`.
4. Ins Profil eintragen (`"verified": true`, wenn geprüft), dann `reload_profiles` aufrufen.

Wichtig: Eine L:-Variable, die es nicht gibt, liest SimConnect als `0`. Eine 0 beweist also nicht, dass der Name stimmt. Deshalb immer mit einer Zustandsänderung gegenprüfen.

## Aufbau

```
src/Kopilot/
  Program.cs              Start: --stdio (Claude Desktop) oder HTTP, --generate-secret
  Hosting/                Transport-Auswahl, Konfiguration, HTTP-Host
  Remote/                 Bearer-Token, OAuth-Server, Freigabeseite, Token-Speicher
  Sim/SimConnectClient.cs SimConnect auf eigenem Thread, Auto-Reconnect, Werte-Cache
  Sim/SimVarCatalog.cs    Standard-SimVars (1x pro Sekunde, Monitoring-Werte ca. 10x pro Sekunde)
  Sim/AircraftSignals.cs  Semantische Sicht: Profil-Bindings mit Rückfall auf generische SimVars
  Aircraft/               Profile laden und zuordnen
  Ecam/                   ECAM-Codes → Klartext (FBW-Tabelle, zur Laufzeit geladen)
  FlightPlan/             SimBrief-OFP, Routen- und Fortschrittsrechnung
  Atc/                    Ingame-ATC, Bahnnummer/-bezeichner, Phraseologie
  Monitoring/             Regeln (MonitorEngine), Alert-Bus, Hintergrunddienst
  Tools/                  MCP-Tools
  profiles/               Flugzeugprofile (JSON)
src/SimConnectStub/       Platzhalter-API für CI-Builds ohne MSFS-SDK
tests/Kopilot.Tests/      Tests (OAuth, Zugriffsschutz, Monitoring-Regeln, ECAM, SimBrief, Profile) – laufen ohne MSFS
docs/cloudflared.md       Anleitung Cloudflare Tunnel
```

Jede Variable hat eine eigene SimConnect-Data-Definition. Ist ein Name in MSFS unbekannt, fehlt nur dieser eine Wert, alles andere läuft weiter.

## Roadmap

- [x] v0.1: MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, L:-Variablen-Profilen
- [x] v0.3: Pilot Monitoring (Regeln, Callouts, Alerts), FBW-Profile, ECAM-Klartext, SimBrief, Ingame-ATC-Bahn
- [ ] L:-Variablen des iniBuilds A321LR (FMA, A/THR, Master Warning, ECAM)
- [ ] Tray-Icon und Dashboard im Browser statt Konsolenfenster
- [ ] WASimCommander-Anbindung (L:-Variablen auflisten, Events)
- [ ] Callouts mit eigener Sprachausgabe (z. B. „20 Meilen – Flaps 1", 1000/500/Minimum, Retard, 100 Knoten, V1)
- [x] Remote-Betrieb: Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- [ ] Sprachmodus mit dem Remote-Connector testen
