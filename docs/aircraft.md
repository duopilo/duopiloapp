# Flugzeuge: was Kopilot lesen kann

Stand: Kopilot 0.5, Testflüge vom 28.10.2026. **Geprüft** heißt: im Sim gegen das Cockpit verglichen. **Doku** heißt: Name aus der Entwicklerdoku übernommen, noch nicht im Sim geprüft (`"verified": false` im Profil).

## FlyByWire A32NX

Stand v0.5 nach dem Testflug EDDL–EDDM (Cold and Dark, Air-Start, FL310, ILS-Autoland 26R), ergänzt um die Callout-Variablen aus v0.6 (noch nicht im Flug geprüft).

| Wert | Quelle | Status |
|---|---|---|
| AP 1 / AP 2 | `L:A32NX_AUTOPILOT_1_ACTIVE`, `_2_ACTIVE` | geprüft |
| FMA lateral, vertikal, armed, Schub | `L:A32NX_FMA_*`, `L:A32NX_AUTOTHRUST_MODE` | geprüft |
| ALT CRZ | `L:A32NX_FMGC_1_DISCRETE_WORD_1` (ARINC 429, Bit 26) – wie im FBW-PFD | neu, Code |
| Master Warning / Caution | `L:A32NX_MASTER_WARNING`, `_CAUTION` | geprüft |
| ECAM (EWD unten links/rechts) | `L:A32NX_EWD_LOWER_LEFT_LINE_1..7`, `RIGHT_LINE_1..7` → Klartext | geprüft |
| Managed Speed | `L:A32NX_SPEEDS_MANAGED_PFD` mit Einheit **knots** (intern m/s) | korrigiert |
| FCU SPD/ALT, Managed-Punkt | `L:A32NX_FCU_AFS_DISPLAY_*` | geprüft |
| FCU Heading | `L:A32NX_FCU_AFS_DISPLAY_HDG_TRK_VALUE`, Striche aus `_DASHES` | neu, Doku |
| Mach-Ziel | FCU-Anzeige im Mach-Modus, umgerechnet über `AMBIENT PRESSURE` in kt | neu |
| V1 / VR / V2 | `L:AIRLINER_V1_SPEED`, `_VR_`, `_V2_` mit Einheit **knots** (intern m/s) | korrigiert |
| FLEX | `L:A32NX_AIRLINER_TO_FLEX_TEMP` (der Name ohne Präfix ist veraltet) | korrigiert |
| VLS, Autobrake | `L:A32NX_SPEEDS_VLS`, `L:A32NX_AUTOBRAKES_ARMED_MODE` | geprüft |
| Fuel Flow | `L:A32NX_ENGINE_FF:1/2` (kg/h) | geprüft |
| EGT | `L:A32NX_ENGINE_EGT:1/2` – `ENG EXHAUST GAS TEMPERATURE` zeigt Unsinn | neu |
| Parkbremse | `L:A32NX_PARK_BRAKE_LEVER_POS` – `BRAKE PARKING POSITION` bleibt false | neu |
| Ground Spoiler armed | `L:A32NX_SPOILERS_ARMED` – `SPOILERS ARMED` bleibt false | geprüft |
| Klappen | `L:A32NX_FLAPS_CONF_INDEX`: 0 / 1 / 1+F / 2 / 3 / FULL | neu, Doku |
| T/D REACHED | `L:A32NX_PFD_MSG_TD_REACHED` → Regel TOP_OF_DESCENT | neu, Doku |
| Aktiver FMS-Wegpunkt | `L:A32NX_EFIS_L_TO_WPT_DISTANCE` / `_BEARING` → Abgleich mit dem SimBrief-Plan (Name gibt FBW nicht heraus) | neu, Doku |
| Elektrik | `L:A32NX_ELEC_*_POTENTIAL`, `_FREQUENCY`, `_BUS_IS_POWERED` | neu, Doku |
| Fire Pushbutton / Guard ENG 1 | `L:A32NX_FIRE_BUTTON_ENG1` (1 = released), `L:A32NX_FIRE_GUARD_ENG1` | geprüft |
| Anti-Ice | `ENG ANTI ICE:1/2`, `STRUCTURAL DEICE SWITCH`, Wing-Pushbutton | geprüft |
| Ground Spoiler ausgefahren | `L:A32NX_SEC_1_GROUND_SPOILER_OUT` → „Spoilers“ (Ersatz: `SPOILERS LEFT POSITION`) | neu, Code (v0.6) |
| DECEL | `L:A32NX_AUTOBRAKES_DECEL_LIGHT` → „Decel“ | neu, Code (v0.6) |
| Schubumkehr | `TURB ENG REVERSE NOZZLE PERCENT:1/2` → „Reverse green“ ab 90 % | neu (v0.6) |
| TA / TL | `L:A32NX_FM1_TRANS_ALT`, `_TRANS_LVL` (ARINC 429, TL in FL) – leer: SimBrief | neu, Code (v0.6) |
| DH / MDA | `L:A32NX_FM1_DECISION_HEIGHT`, `_MINIMUM_DESCENT_ALTITUDE` (ARINC 429) | neu, Code (v0.6) |

Nicht nutzbar beim A32NX: `AUTOPILOT MASTER`, `BRAKE PARKING POSITION`, `SPOILERS ARMED`, `ENG EXHAUST GAS TEMPERATURE`, `ELECTRICAL MAIN BUS VOLTAGE` (fest 25,4 V), `AUTOPILOT HEADING LOCK DIR`, `ENG ON FIRE:1/2`. Engine Fire lässt sich über die Flypad-Failures nicht auslösen und hat keine eigene Variable – ein Brand käme als ECAM-Meldung „ENG 1 FIRE“ und damit als Master-Warning-Callout.

**Einheiten-Falle:** FBW schreibt viele Geschwindigkeiten mit der Einheit „knots“. MSFS speichert L:-Variablen dann in der Basiseinheit m/s. Wer sie als „number“ liest, bekommt m/s (74,6 statt 145 kt). Im Profil deshalb `"unit": "knots"`.

**FMS/Route:** Das FBW-FMS füllt die GPS-Flugplanvariablen nicht. Route und Fuel-Vergleich kommen aus SimBrief; der nächste Wegpunkt kommt aus Peilung und Distanz des FMS, sofern der Punkt im Plan steht. Die TOD-Entfernung gibt das FMS nicht heraus – die bleibt aus SimBrief, der Callout „Top of descent“ dagegen kommt vom FMS.

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
| `ATC RUNWAY AIRPORT NAME` | Text-SimVar; `read_simvar` erkennt sie automatisch als Text |
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
