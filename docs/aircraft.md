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

### A32NX – Eingaben (v0.7/v0.8.1)

Im Sim geprüft (28.10.2026): fcu.heading, flaps, lights.beacon, antiice.engines, apu.bleed. Alles andere ist ungeprüft.

Abgeleitet aus `fbw-a32nx/docs/a320-events.md`, `a320-simvars.md`, den FBW-Presets (`aircraft_preset_procedures.xml`) und den Rust-Systemen. Steht im Profil unter `actions`.

| Bedienelement | Schreiben | Zustand lesen |
|---|---|---|
| FCU SPD/HDG/ALT/VS | `A32NX.FCU_*_SET`, `_PUSH`, `_PULL` | `L:A32NX_FCU_AFS_DISPLAY_*_VALUE`, `_MANAGED` |
| LOC, APPR, EXPED, AP 1/2, A/THR | `A32NX.FCU_*_PUSH` (nur wenn nötig) | `L:A32NX_FCU_*_LIGHT_ON` |
| QNH, STD/QNH | `A32NX.FCU_EFIS_{L,R}_BARO_SET` (hPa × 16), `_PULL`/`_PUSH` | `L:A32NX_FCU_EFIS_L_DISPLAY_BARO_MODE` (0 STD, 1 QNH) |
| LS, FD | `A32NX.FCU_EFIS_{L,R}_{LS,FD}_PUSH` | `L:A32NX_FCU_EFIS_{L,R}_{LS,FD}_LIGHT_ON` |
| ND-Modus/-Reichweite | `L:A32NX_FCU_EFIS_L_EFIS_MODE` (0–4), `_RANGE` (0–5) | gleich |
| Fahrwerk | `GEAR_UP` / `GEAR_DOWN` | `L:A32NX_GEAR_HANDLE_POSITION` |
| Klappen | `FLAPS_UP`, `FLAPS_1`…`FLAPS_3`, `FLAPS_DOWN` | `L:A32NX_FLAPS_HANDLE_INDEX` |
| Speedbrake, Ground Spoiler | `SPOILERS_SET` (0/8192/16384), `SPOILERS_ARM_SET` | `L:A32NX_SPOILERS_HANDLE_POSITION`, `L:A32NX_SPOILERS_ARMED` |
| Autobrake | `A32NX.AUTOBRAKE_SET` (1–4) | `L:A32NX_AUTOBRAKES_ARMED_MODE` |
| Beacon, Wing | `BEACON_LIGHTS_SET`, `WING_LIGHTS_SET` | `LIGHT BEACON`, `LIGHT WING` |
| Runway Turn-off | `ELECTRICAL_CIRCUIT_TOGGLE` 21/22 | `CIRCUIT SWITCH ON:21/22` |
| Landing L/R | `L:LIGHTING_LANDING_2/3`, `L:LANDING_2/3_RETRACTED`, Stromkreis 18/19 | `L:LIGHTING_LANDING_2` |
| Nose | `L:LIGHTING_LANDING_1`, Stromkreis 17 (T.O) und 20 (Taxi) | `L:LIGHTING_LANDING_1` |
| Strobe | `L:STROBE_0_AUTO` + `STROBES_OFF`/`STROBES_ON` | `L:LIGHTING_STROBE_0` (0 ON, 1 AUTO, 2 OFF) |
| Seat Belts | `CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE` (nur wenn nötig) | `CABIN SEATBELTS ALERT SWITCH` |
| No Smoking | `L:XMLVAR_SWITCH_OVHD_INTLT_NOSMOKING_Position` | gleich |
| Anti-Ice ENG/WING, Probe Heat | `L:A32NX_BUTTON_OVHD_ANTI_ICE_*_POSITION`, `L:A32NX_MAN_PITOT_HEAT` | gleich |
| Packs, APU Master/Bleed | `L:A32NX_OVHD_COND_PACK_*_PB_IS_ON`, `L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON`, `L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON` | gleich (nur Schalterstellung) |
| APU Start | `L:A32NX_OVHD_APU_START_PB_IS_ON` = 1 | ON-Licht muss 2,5 s stehen – ohne Strom/Master schaltet die APU-Elektronik es aus |
| APU N/EGT (get_systems) | – | `L:A32NX_APU_N`, `L:A32NX_APU_EGT` (ARINC 429, wie ECAM); `APU PCT RPM` folgt nur verzögert |
| ENG 1/2 Anti-Ice einzeln | `L:A32NX_BUTTON_OVHD_ANTI_ICE_ENG_n_POSITION` | `ENG ANTI ICE:n` (FBW setzt es per `K:ANTI_ICE_SET_ENGn`) |
| External Power | `L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON`, nur mit `L:A32NX_EXT_PWR_AVAIL:1` | `L:A32NX_ELEC_CONTACTOR_3XG_IS_CLOSED` |
| Transponder, TCAS | `XPNDR_SET` (BCD), `L:A32NX_TRANSPONDER_MODE`, `L:A32NX_SWITCH_ATC_ALT`, `L:A32NX_SWITCH_TCAS_Position`, `_Traffic_Position` | gleich |
| COM 1 | `COM_STBY_RADIO_SET_HZ`, `COM1_RADIO_SWAP` | `COM STANDBY FREQUENCY:1` |

Nicht dabei: NAV & LOGO (nur `B:A32NX_OVH_LIGHTS_NAV_LOGO_SW`). Bei Umschalttastern liest Kopilot vorher den Zustand und drückt nur, wenn nötig.

## FlyByWire A380X

| Wert | Quelle | Status |
|---|---|---|
| AP 1 | `L:A32NX_AUTOPILOT_1_ACTIVE` | geprüft |
| Master Warning | `L:A32NX_MASTER_WARNING` | geprüft |
| Fire-Test-Taste | `L:A32NX_OVHD_FIRE_TEST_PB_IS_PRESSED` | geprüft |
| AP 2, Master Caution, A/THR, FMA, Speeds, FCU-Anzeige, Autobrake | gemeinsame `A32NX_`-Variablen | Doku/angenommen |
| Fuel Flow | `L:A32NX_ENGINE_FF:1..4` (laut A380X-SD-Quellcode) | Doku |

Noch nicht eingebunden: **ECAM-Klartext.** Das A380X hat ein anderes Warnsystem (FWS mit eigener Meldungstabelle). Fire-Variablen (`A32NX_FIRE_TEST_ENG1`, `ENG ON FIRE`) bleiben 0.

## Headwind A330-900neo

Profil `headwind-a339.json`, Testsession 28.10.2026. Basiert auf FlyByWire, viele `A32NX_`-Variablen gelten, die FCU-Werte kommen aber aus den älteren FBW-Variablen.

| Wert | Quelle | Status |
|---|---|---|
| Parkbremse | `L:A32NX_PARK_BRAKE_LEVER_POS` | geprüft |
| EXT PWR, BAT 1/2, GEN 1 | `L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON`, `_BAT_1/2_PB_IS_AUTO`, `_ENG_GEN_1_PB_IS_ON` | geprüft |
| APU | `L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON`, `_START_PB_IS_ON`, `_START_PB_IS_AVAILABLE`, `_MASTER_SW_PB_HAS_FAULT`, `L:A32NX_APU_N` (ARINC 429) | geprüft |
| ADIRU 1 | `L:A32NX_ADIRS_ADIRU_1_STATE` (1 aligning, 2 aligned) | geprüft |
| AP 1 / AP 2 | `L:A32NX_AUTOPILOT_1_ACTIVE`, `_2_ACTIVE` | geprüft |
| Selected Speed | `L:A32NX_AUTOPILOT_SPEED_SELECTED` (kt, −1 bei managed) | geprüft |
| Managed Speed / Heading | `L:A32NX_FCU_SPD_MANAGED_DOT`, `L:A32NX_FCU_HDG_MANAGED_DASHES` | geprüft |
| Selected Heading | `L:A32NX_FCU_HEADING_SELECTED` | geprüft |
| FCU-Höhe | `AUTOPILOT ALTITUDE LOCK VAR:3` | geprüft |
| FMA lateral/vertikal, armed | `L:A32NX_FMA_*` (Werte wie A32NX) | geprüft |
| A/THR | `L:A32NX_AUTOTHRUST_STATUS` (1 armed, 2 active) | geprüft |
| Master Warning / Caution | `L:A32NX_MASTER_WARNING`, `_CAUTION` | Warning geprüft, Caution 1 noch nicht |
| Autobrake | `L:A32NX_AUTOBRAKES_ARMED_MODE` (1 = LO) | geprüft |
| Fuel Flow / EGT | `L:A32NX_ENGINE_FF:n` (vermutlich kg/h), `L:A32NX_ENGINE_EGT:n` (°C) | geprüft |
| Thrust-Modus | `L:A32NX_AUTOTHRUST_MODE`, `_MODE_MESSAGE` bleiben 0 | offen – nicht im Profil |
| ALT CRZ | Vertical Mode 0, `L:A32NX_FMA_CRUISE_ALT_MODE` 0 | offen |
| ECAM-Meldungen | – | offen – nicht im Profil |
| Grenzwerte | VFE 240/215/196/186/180, VLO/VLE 250 kt (A330) | für den -900neo prüfen |

Unbrauchbar (generisch): `BRAKE PARKING POSITION/INDICATOR`, `EXTERNAL POWER ON:1`, `AUTOPILOT MASTER` (blitzt beim Start kurz auf), generischer Fuel Flow und EGT, `AUTO BRAKE SWITCH CB`.

Eingaben: alle Aktionen des A32NX-Profils. External Power und APU sind eigene A330-Fassungen (Schalterstellung bzw. BAT 1 statt Schütz 3XG und DC BAT BUS).

| Bereich | Status |
|---|---|
| FCU, Lights, Anti-Ice, External Power, APU Master/Start | im Sim getestet |
| Seatbelts | angepasst, ungeprüft: Schalter mit drei Stellungen `L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position` (0 ON, 1 AUTO, 2 OFF). Bei ON/OFF schaltet Kopilot `CABIN SEATBELTS ALERT SWITCH` mit, in AUTO das Flugzeug. |
| No Smoking | angepasst, ungeprüft: Stellung wird mit der Einheit `Enum` gelesen (so liest sie auch die FWC) |
| Landing Lights | angepasst, ungeprüft: Prüfung über das Licht `LIGHT LANDING:2` (Stromkreis 18). Der Schalter kennt nur ON/OFF. |
| Managed NAV | angepasst, ungeprüft: Prüfung wartet bis zu 10 s |
| Gear, Flaps, Speedbrake, Spoiler, Autobrake, EFIS, Packs, APU Bleed, Transponder/TCAS, COM 1 | aus dem A32NX übernommen, ungeprüft. Sperren wie beim A32NX mit den A330-Grenzwerten. |

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
