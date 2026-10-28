# Flugzeuge: was Kopilot lesen kann

Stand: Kopilot 0.3, Testflüge vom 28.10.2026. **Geprüft** heißt: im Sim gegen das Cockpit verglichen. **Doku** heißt: Name aus der Entwicklerdoku übernommen, noch nicht im Sim geprüft (`"verified": false` im Profil).

## FlyByWire A32NX

| Wert | Quelle | Status |
|---|---|---|
| AP 1 / AP 2 | `L:A32NX_AUTOPILOT_1_ACTIVE`, `_2_ACTIVE` | geprüft |
| Master Warning / Caution | `L:A32NX_MASTER_WARNING`, `_CAUTION` | geprüft |
| ECAM (EWD unten links/rechts) | `L:A32NX_EWD_LOWER_LEFT_LINE_1..7`, `RIGHT_LINE_1..7` → Klartext | Codes geprüft, Klartext neu |
| A/THR-Status und -Modus | `L:A32NX_AUTOTHRUST_STATUS`, `_MODE` | Doku |
| FMA lateral/vertikal + armed | `L:A32NX_FMA_LATERAL_MODE`, `_ARMED`, `L:A32NX_FMA_VERTICAL_MODE`, `_ARMED` | Doku |
| Autobrake | `L:A32NX_AUTOBRAKES_ARMED_MODE` | Doku |
| Managed Speed, VLS | `L:A32NX_SPEEDS_MANAGED_PFD`, `L:A32NX_SPEEDS_VLS` | Doku |
| FCU-Anzeige SPD/ALT, Managed-Punkt, Mach-Modus | `L:A32NX_FCU_AFS_DISPLAY_*` | Doku |
| Fuel Flow | `L:A32NX_ENGINE_FF:1/2` (kg/h) – `ENG FUEL FLOW PPH` liefert 0 | Doku |
| Fire Pushbutton / Guard ENG 1 | `L:A32NX_FIRE_BUTTON_ENG1` (1 = released), `L:A32NX_FIRE_GUARD_ENG1` | geprüft |
| Anti-Ice | `ENG ANTI ICE:1/2`, `STRUCTURAL DEICE SWITCH`, Wing-Pushbutton | geprüft |

Nicht nutzbar: `AUTOPILOT MASTER` (falsch), `ENG ON FIRE:1/2`, `L:A32NX_ENG_1_FIRE_PB`, `L:A32NX_OVHD_FIRE_ENG_1_PB_IS_RELEASED` (bleiben 0). Engine Fire lässt sich über die Flypad-Failures nicht auslösen.

**FMS/Route:** Das FBW-FMS füllt die GPS-Flugplanvariablen nicht. Route, Distanzen und Fuel-Vergleich kommen aus SimBrief (`get_progress`).

## FlyByWire A380X

| Wert | Quelle | Status |
|---|---|---|
| AP 1 | `L:A32NX_AUTOPILOT_1_ACTIVE` | geprüft |
| Master Warning | `L:A32NX_MASTER_WARNING` | geprüft |
| Fire-Test-Taste | `L:A32NX_OVHD_FIRE_TEST_PB_IS_PRESSED` | geprüft |
| AP 2, Master Caution, A/THR, FMA, Speeds, FCU-Anzeige, Autobrake | gemeinsame `A32NX_`-Variablen | Doku/angenommen |
| Fuel Flow | `L:A32NX_ENGINE_FF:1..4` (laut A380X-SD-Quellcode) | Doku |

Noch nicht eingebunden: **ECAM-Klartext.** Das A380X hat ein anderes Warnsystem (FWS mit eigener Meldungstabelle). Fire-Variablen (`A32NX_FIRE_TEST_ENG1`, `ENG ON FIRE`) bleiben 0.

## iniBuilds A321LR

| Wert | Quelle | Status |
|---|---|---|
| FCU-Werte ALT/HDG/SPD/VS | generische `AUTOPILOT … VAR` | geprüft |
| Navigation (GPS-Flugplan) | – | leer, das FMS speist sie nicht |
| A/THR | `AUTOPILOT THROTTLE ARM` | falsch (immer false), daher nicht verwendet |
| FMA, Master Warning/Caution, ECAM, Fuel Flow | – | L:-Variablen noch zu suchen |

iniBuilds veröffentlicht keine L:-Variablenliste (rund 3000 Stück), das MCDU/FMS ist von außen nicht lesbar und es gibt kein Failure-Menü. Das Profil enthält deshalb nur die Erkennung und die Grenzwerte. Neue Bindings bitte erst mit `read_simvar` und einer Zustandsänderung prüfen (siehe README).

## Ingame-ATC (alle Flugzeuge)

| Wert | Status |
|---|---|
| `ATC RUNWAY SELECTED` | geprüft (0 → 1 nach Bahnzuweisung) |
| `ATC RUNWAY AIRPORT NAME` | Text-SimVar, jetzt lesbar (`unit: "string"`) |
| Bahnnummer | aus `ATC RUNWAY HEADING DEGREES TRUE` minus Missweisung |
| L/C/R | nicht als SimVar verfügbar – Kopilot nimmt ihn aus dem SimBrief-Plan, wenn die Nummer passt |
| `ATC CLEARED IFR`, `ATC CLEARED LANDING` | blieben im Test 0 |
| Zugewiesene Höhe, Kurs, Squawk | **nicht** als SimVar verfügbar |
| `ATC CURRENT WAYPOINT ALTITUDE`, `ATC FLIGHTPLAN DIFF …` | in `get_atc` unter `experimental` – Bedeutung im Flug prüfen |

Externe ATC-Programme (BeyondATC, SayIntentions) sind über SimConnect nicht sichtbar.

## Allgemeine Beobachtungen

- **Pause:** Kopilot nutzt das SimConnect-Ereignis `Pause_EX1` (normale Pause, Active Pause, Sim-Pause). `paused: true` steht in `get_flight_state` und `kopilot_status`.
- **Extremes Wetter** (z. B. −100 °C) macht EGT unplausibel. `get_systems` meldet das unter `plausibility`.
- **Air-Start:** Die Werte sind in den ersten Sekunden inkonsistent. Das Monitoring schweigt nach dem Laden 10 Sekunden.
