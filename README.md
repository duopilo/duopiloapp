# Kopilot

Kopilot verbindet **Microsoft Flight Simulator 2024** über SimConnect mit **Claude**. Er läuft als MCP-Server auf dem Sim-PC. Claude kann damit Höhe, Geschwindigkeit, Konfiguration, Navigation und Wetter selbst ablesen, statt dass du sie vorlesen musst. Claude ist so der **Pilot Monitoring**, du bleibst Pilot Flying.

> Stand v0.1: nur lesen. Kopilot schaltet nichts im Flugzeug.

## Tools

| Tool | Inhalt |
|---|---|
| `kopilot_status` | Verbindung, Sim-Version, geladenes Flugzeug, aktives Profil, Fehler |
| `get_flight_state` | Höhe (IND, FL, AGL, Radio), IAS/TAS/GS/Mach/VS, Lage, Kurs, Position, Klappen, Fahrwerk, Spoiler, Parkbremse, Standard-AP-Werte, Warnungen |
| `get_systems` | Triebwerke (N1, N2, EGT, Fuel Flow), APU, Treibstoff, Gewicht, Lichter, Anschnallzeichen, Transponder, COM-Frequenzen |
| `get_navigation` | Letzter/nächster Wegpunkt, Distanz, ETE, ETA am Ziel (aus den GPS-Variablen des Sims) |
| `get_environment` | Wind, OAT, QNH, eingestellter Höhenmesser, Sicht, UTC-/Ortszeit |
| `get_aircraft_profile` | Flugzeugspezifische L:-Variablen aus dem aktiven Profil, z. B. FMA-Modi |
| `read_simvar` | Liest eine beliebige SimVar oder L:-Variable einmalig, zum Erkunden |
| `reload_profiles` | Lädt die Profildateien neu |

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

**Aktualisieren:** `update.cmd` im Repo-Ordner doppelklicken. Das Skript beendet einen laufenden Kopilot, holt den neuesten Stand, veröffentlicht nach `C:\Tools\Kopilot` und startet Kopilot neu. Ein anderer Zielordner geht als Parameter: `update.cmd D:\Kopilot`.

Im Ordner `C:\Tools\Kopilot` müssen nach dem Veröffentlichen `Kopilot.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` und der Ordner `profiles` liegen.

## Erster Test im Browser

1. MSFS 2024 starten und einen Flug laden.
2. `C:\Tools\Kopilot\Kopilot.exe` starten. Ohne Parameter läuft der HTTP-Modus.
3. Im Browser öffnen:
   - <http://127.0.0.1:5155/> zeigt den Status, dort sollte `"connected": true` stehen
   - <http://127.0.0.1:5155/state> zeigt den Flugzustand
   - <http://127.0.0.1:5155/systems>, `/nav`, `/env`, `/profile`
   - `http://127.0.0.1:5155/read?name=INDICATED ALTITUDE&unit=feet` liest eine einzelne Variable

### Prüfliste

Ein paar Werte habe ich ohne Sim nicht prüfen können:

- [ ] `altitude.indicatedFt` und `speed.iasKt` stimmen mit dem PFD überein
- [ ] Gewicht (`grossWeightKg`) stimmt mit der MCDU (FUEL PRED, GW) überein
- [ ] `pitchUpDeg` ist im Steigflug **positiv**, `bankRightDeg` in einer Rechtskurve **positiv**
- [x] Der Transponder-Code stimmt, z. B. `2000` (getestet 28.10.)
- [ ] `flapsHandleIndex` folgt dem Klappenhebel (0 bis 4 bei 0/1/2/3/FULL)
- [x] Beim iniBuilds A321LR sind die GPS-Navigationswerte leer, das FMS speist sie nicht. Route und Distanz brauchen L:-Variablen oder die MCDU.
- [ ] Beim iniBuilds A321LR: Passen die Standard-AP-Werte (`autopilot.*`) zum FCU, oder brauchen wir dafür L:-Variablen?

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

Der HTTP-Modus (`/mcp`) ist für Tests mit anderen MCP-Clients und für einen späteren Remote-Connector gedacht. Er hört nur auf `127.0.0.1`.

## Flugzeugprofile und L:-Variablen

Study-Level-Flugzeuge wie der iniBuilds A321LR legen vieles (FMA-Modi, Autobrake, ECAM) nicht in Standard-SimVars ab, sondern in eigenen **L:-Variablen**. Diese tragen wir pro Flugzeug in ein Profil unter `profiles\*.json` ein:

```jsonc
{
  "name": "iniBuilds A321LR",
  "titleContains": [ "A321" ],          // passt, wenn der Flugzeug-TITLE das enthält
  "variables": [
    {
      "key": "fmaLateral",
      "name": "L:BEISPIEL_FMA_LATERAL",  // Platzhalter!
      "unit": "number",
      "description": "Lateraler FMA-Modus",
      "values": { "1": "HDG", "2": "NAV", "3": "LOC" }
    }
  ]
}
```

So findest du die Namen:

1. Im Developer Mode das Fenster mit den Behaviors / LocalVariables des Flugzeugs öffnen. Dort sind alle L:-Variablen mit ihren aktuellen Werten gelistet.
2. Im Cockpit etwas umschalten (z. B. NAV am FCU) und schauen, welche Variable sich ändert.
3. Gegenprobe mit `read_simvar` oder im Browser: `/read?name=L:NAME`.
4. Ins Profil eintragen, dann `reload_profiles` aufrufen (oder Kopilot neu starten).

Wichtig: Eine L:-Variable, die es nicht gibt, liest SimConnect als `0`. Eine 0 beweist also nicht, dass der Name stimmt. Deshalb immer mit einer Zustandsänderung gegenprüfen.

## Aufbau

```
src/Kopilot/
  Program.cs              Start: --stdio (Claude Desktop) oder HTTP
  Sim/SimConnectClient.cs SimConnect auf eigenem Thread, Auto-Reconnect, Werte-Cache
  Sim/SimVarCatalog.cs    Standard-SimVars (1x pro Sekunde abonniert)
  Aircraft/               Profile laden und zuordnen
  Tools/KopilotTools.cs   MCP-Tools
  profiles/               Flugzeugprofile (JSON)
src/SimConnectStub/       Platzhalter-API für CI-Builds ohne MSFS-SDK
```

Jede Variable hat eine eigene SimConnect-Data-Definition. Ist ein Name in MSFS unbekannt, fehlt nur dieser eine Wert, alles andere läuft weiter.

## Roadmap

- [x] v0.1: MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, L:-Variablen-Profilen
- [ ] L:-Variablen des iniBuilds A321LR (FMA, Autobrake, ECAM-Meldungen)
- [ ] Callouts mit eigener Sprachausgabe (z. B. „20 Meilen – Flaps 1", 1000/500/Minimum, Retard, 100 Knoten, V1)
- [ ] Remote-Connector, damit der Sprachmodus von Claude die Daten lesen kann
