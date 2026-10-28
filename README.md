# Kopilot

Kopilot verbindet **Microsoft Flight Simulator 2024** über SimConnect mit **Claude**. Er läuft als MCP-Server auf dem Sim-PC. Claude kann damit Höhe, Geschwindigkeit, Konfiguration, Navigation und Wetter selbst ablesen, statt dass du sie vorlesen musst. Claude ist so der **Pilot Monitoring**, du bleibst Pilot Flying.

> Kopilot liest nur. Er schaltet nichts im Flugzeug.

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

**Aktualisieren:** `update.cmd` im Repo-Ordner doppelklicken. Das Skript holt den neuesten Stand, baut in einen Zwischenordner, beendet alle laufenden Kopilot-Instanzen (auch die von Claude Desktop), kopiert nach `C:\Tools\Kopilot` und startet Kopilot neu. Ein anderer Zielordner geht als Parameter: `update.cmd D:\Kopilot`.

Im Ordner `C:\Tools\Kopilot` müssen nach dem Veröffentlichen `Kopilot.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` und der Ordner `profiles` liegen.

## Erster Test im Browser

1. MSFS 2024 starten und einen Flug laden.
2. `C:\Tools\Kopilot\Kopilot.exe` starten. Ohne Parameter läuft der HTTP-Modus (einstellbar über `Kopilot:Transport`).
3. Im Browser öffnen. Die Prüfseiten funktionieren nur lokal, über den Tunnel sind sie gesperrt:
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
  Program.cs              Start: --stdio (Claude Desktop) oder HTTP, --generate-secret
  Hosting/                Transport-Auswahl, Konfiguration, HTTP-Host
  Remote/                 Bearer-Token, OAuth-Server, Freigabeseite, Token-Speicher
  Sim/SimConnectClient.cs SimConnect auf eigenem Thread, Auto-Reconnect, Werte-Cache
  Sim/SimVarCatalog.cs    Standard-SimVars (1x pro Sekunde abonniert)
  Aircraft/               Profile laden und zuordnen
  Tools/KopilotTools.cs   MCP-Tools
  profiles/               Flugzeugprofile (JSON)
src/SimConnectStub/       Platzhalter-API für CI-Builds ohne MSFS-SDK
tests/Kopilot.Tests/      Integrationstests (OAuth-Ablauf, Zugriffsschutz) – laufen ohne MSFS
docs/cloudflared.md       Anleitung Cloudflare Tunnel
```

Jede Variable hat eine eigene SimConnect-Data-Definition. Ist ein Name in MSFS unbekannt, fehlt nur dieser eine Wert, alles andere läuft weiter.

## Roadmap

- [x] v0.1: MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, L:-Variablen-Profilen
- [ ] L:-Variablen des iniBuilds A321LR (FMA, Autobrake, ECAM-Meldungen)
- [ ] Callouts mit eigener Sprachausgabe (z. B. „20 Meilen – Flaps 1", 1000/500/Minimum, Retard, 100 Knoten, V1)
- [x] Remote-Betrieb: Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- [ ] Sprachmodus mit dem Remote-Connector testen
