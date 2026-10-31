[English](Troubleshooting) | **Deutsch**

# Fehlersuche

## Erster Blick

| Wo | Was |
|---|---|
| `%LOCALAPPDATA%\Duopilo\logs\duopilo.log` | Haupt-Log (Tray → *Logdatei öffnen*, oder Dashboard → *Einrichtung und Diagnose*) |
| `duopilo-stdio.log` | Log der Instanz, die Claude Desktop startet |
| `logs\crash.log` | unbehandelte Ausnahmen und harte Abstürze |
| <http://127.0.0.1:5155/status> | `"connected": true`? |
| `duopilo_status` | Verbindung, Profil, Profilwarnungen, Sprachschleife, SimBrief |
| `Duopilo.exe --console` | Konsolenausgabe live |

Logs sind immer Englisch – du kannst sie also so, wie sie sind, an Issues hängen.

## Häufige Probleme

### Duopilo verbindet sich nicht mit MSFS

- Der Punkt am Tray-Symbol bleibt **grau**: MSFS läuft nicht oder es ist noch kein Flug geladen – Duopilo versucht es alle 5 s erneut.
- Selbst gebaut und verbindet sich nie: Der Build hat womöglich den **SimConnect-Stub** genommen. Im Build-Log steht dann eine Warnung; ein richtiger Build schreibt `Duopilo: SimConnect aus …`. Kopier die beiden SDK-DLLs nach `lib\` und bau neu – siehe [Installation](DE-Installation#1-simconnect-sdk-besorgen).
- Im Ordner der `Duopilo.exe` müssen `Microsoft.FlightSimulator.SimConnect.dll` und `SimConnect.dll` liegen.

### Keine Sprachausgabe

- Tray-Menü → **Callouts sprechen** an? Dashboard → **Ansagen → Stimme**: richtiges **Ausgabegerät**? Mindeststufe?
- Lädt Kokoro noch (~310 MB beim ersten Mal)? So lange liest der Browser im Dashboard vor.
- Nur die **Tray-Instanz** spricht. Läuft bei dir nur die stdio-Instanz von Claude Desktop, starte Duopilo zusätzlich ganz normal.
- Ein einzelner Callout bleibt stumm? `mutedRules` in `speech.json` und `DisabledRules` prüfen.

### Ein Callout kommt nie

- Die Regel braucht vielleicht ein Profil-Binding, das das Flugzeug nicht hat: `get_aircraft_profile` → `callouts` zeigt `missing`.
- Nach dem Laden bzw. einem Air-Start schweigt das Monitoring 10 s, nach einer Pause 3 s.
- Callouts, die auf dem Plan beruhen (Fuel Check, TOD bei Flugzeugen ohne FBW, Transition), brauchen einen SimBrief-Plan.

### Die Sprachschleife reagiert nicht

- Dashboard → das Feld der Sprachschleife zeigt den Grund: *inaktiv – Sim nicht verbunden / Kein Flug geladen / Pausiert*.
- Kein Pegel am Mikrofon: falsches Gerät, stumm geschaltet, oder die Windows-Datenschutzeinstellung **Desktop-Apps den Zugriff auf Ihr Mikrofon erlauben** (*Let desktop apps access your microphone*) ist aus. Bei digitaler Stille warnt das Log.
- Stumm durch „Kopilot pause“ oder `Ctrl+Alt+K`? Sag „Kopilot resume“.
- Befehle werden mit „Input disabled“ beantwortet: Eingabe im Dashboard freigeben.
- Zu langsam oder Ruckler in VR: siehe [Sprachschleife → Leistung](DE-Sprachschleife#leistung).
- Mehr Details: `"Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }`.

### ATC sagt nichts / „ATC unavailable“

- SimBrief-Plan geladen? (**Einstellungen → SimBrief**)
- Sim pausiert oder kein Flug geladen? ATC läuft nur, solange der Sim läuft.
- Nach dem Bearbeiten von Vorlagen: `get_atc_controller` → `templateErrors`.

### Claude sieht Duopilo nicht

- **Claude Desktop:** `claude_desktop_config.json` prüfen (Pfad zur `Duopilo.exe`, `--stdio`), Claude Desktop komplett neu starten, `mcp-server-duopilo.log` ansehen.
- **Connector:** Im Log muss *Access control: OAuth active* stehen. Nach der Umbenennung von Kopilot den alten Connector entfernen und neu hinzufügen.
- Tunnel-Fehler:

| Symptom | Ursache |
|---|---|
| Cloudflare-Fehler **1033** | Tunnel läuft nicht (`sc query cloudflared`) |
| **502 Bad Gateway** | Tunnel läuft, Duopilo nicht (oder nicht im HTTP-Modus / auf einem anderen Port) |
| **400 Bad Request** | `PublicUrl` passt nicht genau zum Hostnamen |
| `/.well-known/…` **404** | OAuth nicht aktiv – das Log sagt, warum (z. B. Passwort zu kurz) |

Mehr in [docs/cloudflared.de.md](https://github.com/duopilo/duopiloapp/blob/main/docs/cloudflared.de.md#fehlersuche).

### Eingabe-Aktion „nicht bestätigt“ oder „ungeprüft“

- `failed`: Das Flugzeug hat den Wert nicht übernommen (z. B. APU START ohne Strom).
- `unverified`: Duopilo konnte nur die Schalterstellung lesen. Schau ins Cockpit und bestätige mit `mark_action_verified`, wenn es funktioniert hat.
- „Grenzwert fehlt“: Im Profil fehlt ein Grenzwert, deshalb braucht die Aktion eine zusätzliche Bestätigung.

## Abstürze

Unbehandelte Ausnahmen landen sofort in `logs\crash.log`. Harte Abstürze (z. B. eine AccessViolation in SimConnect) kann .NET nicht abfangen – dafür führt die Tray-Instanz ein kleines Journal (`logs\journal-http.bin`). Beim nächsten Start meldet Duopilo, dass der letzte Lauf nicht sauber endete, was zuletzt lief und die Ausnahme aus der Windows-Ereignisanzeige.

Windows startet die Tray-Instanz nach einem Absturz neu (erst nach mindestens 60 s Laufzeit, damit kein Kreislauf entsteht); die ATC-Sitzung läuft weiter.

## Fehler melden

Dashboard → **Fehler melden** hält den aktuellen Kontext fest: Position (mit Kartenlink), Höhe, Station, FIR und Frequenz laut Duopilo samt Herkunft, COM1, Transponder, echte und Sim-Zeit, Version, Flug und die letzten Funksprüche.

1. Art wählen: FIR-Grenze/Station, Frequenz, Airline/Rufzeichen oder Programmfehler.
2. Beschreiben, was falsch ist und was richtig wäre (mit Quelle, wenn du eine hast – Sim-ATC, Karte, AIP).
3. **Auf GitHub melden** öffnet ein vorausgefülltes Issue – du musst es nur noch absenden. Oder **Nur lokal speichern** (ohne Konto und ohne Internet).

Jede Meldung liegt zusätzlich als Markdown in `%LOCALAPPDATA%\Duopilo\reports`. Lange Meldungen werden im Link gekürzt; der vollständige Text steht in der lokalen Kopie und in der Zwischenablage (**Text kopieren**).

Datenkorrekturen werden nach Prüfung eingepflegt; die Basis bleiben VAT-Spy/VATSIM-Sektoren und die Facilities-API von MSFS.
