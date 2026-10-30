**English** | [Deutsch](aircraft.de.md)

# Aircraft: what Duopilo can read

As of version 0.5 (then still called Kopilot), test flights on 2026-10-28. **Verified** means: compared against the cockpit in the sim. **Docs** means: name taken from the developer documentation, not yet checked in the sim (`"verified": false` in the profile).

## FlyByWire A32NX

State of v0.5 after the test flight EDDL–EDDM (cold and dark, air start, FL310, ILS autoland 26R), plus the callout variables from v0.6 (not yet checked in flight).

| Value | Source | Status |
|---|---|---|
| AP 1 / AP 2 | `L:A32NX_AUTOPILOT_1_ACTIVE`, `_2_ACTIVE` | verified |
| FMA lateral, vertical, armed, thrust | `L:A32NX_FMA_*`, `L:A32NX_AUTOTHRUST_MODE` | verified |
| ALT CRZ | `L:A32NX_FMGC_1_DISCRETE_WORD_1` (ARINC 429, bit 26) – as in the FBW PFD | new, code |
| Master Warning / Caution | `L:A32NX_MASTER_WARNING`, `_CAUTION` | verified |
| ECAM (EWD lower left/right) | `L:A32NX_EWD_LOWER_LEFT_LINE_1..7`, `RIGHT_LINE_1..7` → plain text | verified |
| Managed speed | `L:A32NX_SPEEDS_MANAGED_PFD` with unit **knots** (stored as m/s) | corrected |
| FCU SPD/ALT, managed dot | `L:A32NX_FCU_AFS_DISPLAY_*` | verified |
| FCU heading | `L:A32NX_FCU_AFS_DISPLAY_HDG_TRK_VALUE`, dashes from `_DASHES` | new, docs |
| Mach target | FCU display in Mach mode, converted to kt via `AMBIENT PRESSURE` | new |
| V1 / VR / V2 | `L:AIRLINER_V1_SPEED`, `_VR_`, `_V2_` with unit **knots** (stored as m/s) | corrected |
| FLEX | `L:A32NX_AIRLINER_TO_FLEX_TEMP` (the name without prefix is outdated) | corrected |
| VLS, autobrake | `L:A32NX_SPEEDS_VLS`, `L:A32NX_AUTOBRAKES_ARMED_MODE` | verified |
| Fuel flow | `L:A32NX_ENGINE_FF:1/2` (kg/h) | verified |
| EGT | `L:A32NX_ENGINE_EGT:1/2` – `ENG EXHAUST GAS TEMPERATURE` shows nonsense | new |
| Parking brake | `L:A32NX_PARK_BRAKE_LEVER_POS` – `BRAKE PARKING POSITION` stays false | new |
| Ground spoilers armed | `L:A32NX_SPOILERS_ARMED` – `SPOILERS ARMED` stays false | verified |
| Flaps | `L:A32NX_FLAPS_CONF_INDEX`: 0 / 1 / 1+F / 2 / 3 / FULL | new, docs |
| T/D REACHED | `L:A32NX_PFD_MSG_TD_REACHED` → rule TOP_OF_DESCENT | new, docs |
| Active FMS waypoint | `L:A32NX_EFIS_L_TO_WPT_DISTANCE` / `_BEARING` → matched against the SimBrief plan (FBW doesn't expose the name) | new, docs |
| Electrical | `L:A32NX_ELEC_*_POTENTIAL`, `_FREQUENCY`, `_BUS_IS_POWERED` | new, docs |
| Fire pushbutton / guard ENG 1 | `L:A32NX_FIRE_BUTTON_ENG1` (1 = released), `L:A32NX_FIRE_GUARD_ENG1` | verified |
| Anti-ice | `ENG ANTI ICE:1/2`, `STRUCTURAL DEICE SWITCH`, wing pushbutton | verified |
| Ground spoilers extended | `L:A32NX_SEC_1_GROUND_SPOILER_OUT` → "Spoilers" (fallback: `SPOILERS LEFT POSITION`) | new, code (v0.6) |
| DECEL | `L:A32NX_AUTOBRAKES_DECEL_LIGHT` → "Decel" | new, code (v0.6) |
| Thrust reversers | `TURB ENG REVERSE NOZZLE PERCENT:1/2` → "Reverse green" from 90 % | new (v0.6) |
| TA / TL | `L:A32NX_FM1_TRANS_ALT`, `_TRANS_LVL` (ARINC 429, TL in FL) – empty: SimBrief | new, code (v0.6) |
| DH / MDA | `L:A32NX_FM1_DECISION_HEIGHT`, `_MINIMUM_DESCENT_ALTITUDE` (ARINC 429) | new, code (v0.6) |

Not usable on the A32NX: `AUTOPILOT MASTER`, `BRAKE PARKING POSITION`, `SPOILERS ARMED`, `ENG EXHAUST GAS TEMPERATURE`, `ELECTRICAL MAIN BUS VOLTAGE` (fixed at 25.4 V), `AUTOPILOT HEADING LOCK DIR`, `ENG ON FIRE:1/2`. An engine fire can't be triggered through the flyPad failures and has no variable of its own – a fire would show up as the ECAM message "ENG 1 FIRE" and thus as a Master Warning callout.

**Unit trap:** FBW writes many speeds with the unit "knots". MSFS then stores the L: variable in the base unit m/s. Reading it as "number" gives m/s (74.6 instead of 145 kt). So the profile uses `"unit": "knots"`.

**FMS/route:** The FBW FMS doesn't fill the GPS flight plan variables. Route and fuel comparison come from SimBrief; the next waypoint comes from the FMS bearing and distance, as long as the point is in the plan. The FMS doesn't expose the distance to TOD – that stays from SimBrief, while the "Top of descent" callout comes from the FMS.

### A32NX – inputs (v0.7/v0.8.1)

Verified in the sim (2026-10-28): fcu.heading, flaps, lights.beacon, antiice.engines, apu.bleed. Everything else is unverified.

Derived from `fbw-a32nx/docs/a320-events.md`, `a320-simvars.md`, the FBW presets (`aircraft_preset_procedures.xml`) and the Rust systems. Defined in the profile under `actions` (conditions and their texts: see [Texts in profiles](#texts-in-profiles)).

| Control | Write | Read state |
|---|---|---|
| FCU SPD/HDG/ALT/VS | `A32NX.FCU_*_SET`, `_PUSH`, `_PULL` | `L:A32NX_FCU_AFS_DISPLAY_*_VALUE`, `_MANAGED` |
| LOC, APPR, EXPED, AP 1/2, A/THR | `A32NX.FCU_*_PUSH` (only when needed) | `L:A32NX_FCU_*_LIGHT_ON` |
| QNH, STD/QNH | `A32NX.FCU_EFIS_{L,R}_BARO_SET` (hPa × 16), `_PULL`/`_PUSH` | `L:A32NX_FCU_EFIS_L_DISPLAY_BARO_MODE` (0 STD, 1 QNH) |
| LS, FD | `A32NX.FCU_EFIS_{L,R}_{LS,FD}_PUSH` | `L:A32NX_FCU_EFIS_{L,R}_{LS,FD}_LIGHT_ON` |
| ND mode/range | `L:A32NX_FCU_EFIS_L_EFIS_MODE` (0–4), `_RANGE` (0–5) | same |
| Gear | `GEAR_UP` / `GEAR_DOWN` | `L:A32NX_GEAR_HANDLE_POSITION` |
| Flaps | `FLAPS_UP`, `FLAPS_1`…`FLAPS_3`, `FLAPS_DOWN` | `L:A32NX_FLAPS_HANDLE_INDEX` |
| Speedbrake, ground spoilers | `SPOILERS_SET` (0/8192/16384), `SPOILERS_ARM_SET` | `L:A32NX_SPOILERS_HANDLE_POSITION`, `L:A32NX_SPOILERS_ARMED` |
| Autobrake | `A32NX.AUTOBRAKE_SET` (1–4) | `L:A32NX_AUTOBRAKES_ARMED_MODE` |
| Beacon, Wing | `BEACON_LIGHTS_SET`, `WING_LIGHTS_SET` | `LIGHT BEACON`, `LIGHT WING` |
| Runway turn-off | `ELECTRICAL_CIRCUIT_TOGGLE` 21/22 | `CIRCUIT SWITCH ON:21/22` |
| Landing L/R | `L:LIGHTING_LANDING_2/3`, `L:LANDING_2/3_RETRACTED`, circuit 18/19 | `L:LIGHTING_LANDING_2` |
| Nose | `L:LIGHTING_LANDING_1`, circuit 17 (T.O) and 20 (Taxi) | `L:LIGHTING_LANDING_1` |
| Strobe | `L:STROBE_0_AUTO` + `STROBES_OFF`/`STROBES_ON` | `L:LIGHTING_STROBE_0` (0 ON, 1 AUTO, 2 OFF) |
| Seat Belts | `CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE` (only when needed) | `CABIN SEATBELTS ALERT SWITCH` |
| No Smoking | `L:XMLVAR_SWITCH_OVHD_INTLT_NOSMOKING_Position` | same |
| Anti-ice ENG/WING, probe heat | `L:A32NX_BUTTON_OVHD_ANTI_ICE_*_POSITION`, `L:A32NX_MAN_PITOT_HEAT` | same |
| Packs, APU master/bleed | `L:A32NX_OVHD_COND_PACK_*_PB_IS_ON`, `L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON`, `L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON` | same (switch position only) |
| APU start | `L:A32NX_OVHD_APU_START_PB_IS_ON` = 1 | The ON light must stay on for 2.5 s – without power/master the APU electronics switch it off |
| APU N/EGT (get_systems) | – | `L:A32NX_APU_N`, `L:A32NX_APU_EGT` (ARINC 429, as on the ECAM); `APU PCT RPM` lags behind |
| ENG 1/2 anti-ice individually | `L:A32NX_BUTTON_OVHD_ANTI_ICE_ENG_n_POSITION` | `ENG ANTI ICE:n` (FBW sets it via `K:ANTI_ICE_SET_ENGn`) |
| External power | `L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON`, only with `L:A32NX_EXT_PWR_AVAIL:1` | `L:A32NX_ELEC_CONTACTOR_3XG_IS_CLOSED` |
| Transponder, TCAS | `XPNDR_SET` (BCD), `L:A32NX_TRANSPONDER_MODE`, `L:A32NX_SWITCH_ATC_ALT`, `L:A32NX_SWITCH_TCAS_Position`, `_Traffic_Position` | same |
| COM 1 | `COM_STBY_RADIO_SET_HZ`, `COM1_RADIO_SWAP` | `COM STANDBY FREQUENCY:1` |

Not included: NAV & LOGO (only `B:A32NX_OVH_LIGHTS_NAV_LOGO_SW`). For toggle buttons, Duopilo reads the state first and only presses when needed.

## FlyByWire A380X

| Value | Source | Status |
|---|---|---|
| AP 1 | `L:A32NX_AUTOPILOT_1_ACTIVE` | verified |
| Master Warning | `L:A32NX_MASTER_WARNING` | verified |
| Fire test button | `L:A32NX_OVHD_FIRE_TEST_PB_IS_PRESSED` | verified |
| AP 2, Master Caution, A/THR, FMA, speeds, FCU display, autobrake | shared `A32NX_` variables | docs/assumed |
| Fuel flow | `L:A32NX_ENGINE_FF:1..4` (per the A380X SD source code) | docs |

Not integrated yet: **ECAM plain text.** The A380X has a different warning system (FWS with its own message table). The fire variables (`A32NX_FIRE_TEST_ENG1`, `ENG ON FIRE`) stay 0.

## Headwind A330-900neo

Profile `headwind-a339.json`, test session 2026-10-28, fixes after the test flight on 2026-10-29. Based on FlyByWire; many `A32NX_` variables apply, but the FCU values come from the older FBW variables.

Sources for the fixes: the Headwind source code (`headwindsim/aircraft`, folder `hdw-a339x`: cockpit model `A330_NEO_INTERIOR.xml`, `aircraft_preset_procedures.xml`, systems WASM) and the FBW base Headwind builds on. Values cross-checked in cruise with `read_simvar` ("read").

| Value | Source | Status |
|---|---|---|
| Parking brake | `L:A32NX_PARK_BRAKE_LEVER_POS` | verified |
| EXT PWR, BAT 1/2 | `L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON`, `_BAT_1/2_PB_IS_AUTO` | verified |
| GEN 1/2 pushbutton | `GENERAL ENG MASTER ALTERNATOR:1/2` (model: `FBW_Airbus_Engine_Generator_Switch`) – `L:A32NX_OVHD_ELEC_ENG_GEN_1_PB_IS_ON` read 0 in flight with GEN 1 running | read |
| Electrical (get_systems) | `L:A32NX_ELEC_*_POTENTIAL`, `_FREQUENCY`, `_LOAD`, `_BUS_IS_POWERED` as on the A32NX (Headwind uses the FBW electrical system) | read: GEN 115 V/400 Hz, BAT 27 V |
| APU GEN | pushbutton `APU GENERATOR SWITCH:1`, values `L:A32NX_ELEC_APU_GEN_1_POTENTIAL`, `_FREQUENCY`, `_LOAD` | read (APU off) |
| APU bleed | `L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON` (same variable as `apu.bleed`) | code, 0 in flight |
| APU EGT, air intake | `L:A32NX_APU_EGT` (ARINC 429), `L:A32NX_APU_FLAP_OPEN_PERCENTAGE` | docs |
| Fuel pumps (get_systems → `fuel.pumps`) | no L: variables: `FUELSYSTEM PUMP SWITCH:2/5` (L TK 1/2), `:3/6` (R TK 1/2), `FUELSYSTEM PUMP ACTIVE:n`, CTR TK XFR `FUELSYSTEM VALVE SWITCH:9/10`, X FEED `FUELSYSTEM VALVE OPEN:3` | read |
| APU | `L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON`, `_START_PB_IS_ON`, `_START_PB_IS_AVAILABLE`, `_MASTER_SW_PB_HAS_FAULT`, `L:A32NX_APU_N` (ARINC 429) | verified |
| ADIRU 1 | `L:A32NX_ADIRS_ADIRU_1_STATE` (1 aligning, 2 aligned) | verified |
| AP 1 / AP 2 | `L:A32NX_AUTOPILOT_1_ACTIVE`, `_2_ACTIVE` | verified |
| Selected speed | `L:A32NX_AUTOPILOT_SPEED_SELECTED` (kt, −1 when managed) | verified |
| Managed speed / heading | `L:A32NX_FCU_SPD_MANAGED_DOT`, `L:A32NX_FCU_HDG_MANAGED_DASHES` | verified |
| Selected heading | `L:A32NX_FCU_HEADING_SELECTED` | verified |
| FCU altitude | `AUTOPILOT ALTITUDE LOCK VAR:3` | verified |
| FMA lateral/vertical, armed | `L:A32NX_FMA_*` (values as on the A32NX) | verified |
| A/THR | `L:A32NX_AUTOTHRUST_STATUS` (1 armed, 2 active) | verified |
| Master Warning / Caution | `L:A32NX_MASTER_WARNING`, `_CAUTION` | Warning verified, Caution 1 not yet |
| Autobrake | `L:A32NX_AUTOBRAKES_ARMED_MODE` (1 = LO) | verified |
| Fuel flow / EGT | `L:A32NX_ENGINE_FF:n` (probably kg/h), `L:A32NX_ENGINE_EGT:n` (°C) | verified |
| Thrust mode | `L:A32NX_AUTOTHRUST_MODE`, `_MODE_MESSAGE` stay 0 | open – not in the profile |
| ALT CRZ | vertical mode 0, `L:A32NX_FMA_CRUISE_ALT_MODE` 0 | open |
| ECAM (EWD lower left/right) | `L:A32NX_EWD_LOWER_LEFT_LINE_1..7`, `RIGHT_LINE_1..7` (written by the Headwind FWC), plain text from the Headwind table `hdw-a339x/src/systems/shared/src/EwdMessages.ts` (format `hdw-a339x`) | read: memos IGNITION, PRED W/S OFF |
| Limits | VFE 240/215/196/186/180, VLO/VLE 250 kt (A330) | check for the -900neo |

Unusable (generic): `BRAKE PARKING POSITION/INDICATOR`, `EXTERNAL POWER ON:1`, `AUTOPILOT MASTER` (flashes briefly at startup), generic fuel flow and EGT, `AUTO BRAKE SWITCH CB`.

Inputs: all actions of the A32NX profile, plus `elec.battery`, `lights.nav` and `lights.logo`. External power and APU have their own A330 versions (switch position, or BAT 1 instead of contactor 3XG and DC BAT BUS).

`stateAfter` in the `confirm_action` response is the raw value of the checked variable – for selector switches the position, counted from the top (seatbelts: 0 = ON, 2 = OFF). `stateAfterText` names the matching choice.

| Area | Status |
|---|---|
| FCU, lights, anti-ice, external power, APU master/start | tested in the sim |
| Seatbelts | adjusted, unverified: three-position switch `L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position` (0 ON, 1 AUTO, 2 OFF). For ON/OFF Duopilo also sets `CABIN SEATBELTS ALERT SWITCH`; in AUTO the aircraft does. |
| No Smoking | adjusted, unverified: the position is read with the unit `Enum` (that's how the FWC reads it too) |
| Landing lights | adjusted, unverified: checked via the light `LIGHT LANDING:2` (circuit 18). The switch only knows ON/OFF. |
| Managed NAV | adjusted, unverified: the check waits up to 10 s |
| Batteries (`elec.battery`) | new, unverified: `L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO`, 1 s pause, then `_BAT_2_` (like Headwind's preset). Off only on the ground. |
| Autobrake | corrected, unverified: `A32NX.AUTOBRAKE_SET_DISARM`/`_LO`/`_MED`/`_MAX`. According to the WASM, `A32NX.AUTOBRAKE_SET` takes 0–3 (not 1–4 as in the docs); MAX = 4 was read as "off". MAX only on the ground; arming needs pressure on the normal brakes (green hydraulics) and anti-skid. |
| TCAS | corrected, unverified: writes `L:A32NX_SWITCH_TCAS_Position` (Enum), checks `L:A32NX_TCAS_MODE` (mode of the TCAS computer). TA and TA/RA only with transponder AUTO/ON (`TRANSPONDER STATE:1` 3–5) and ALT RPTG on – otherwise TCAS stays in STBY. |
| NAV & LOGO (`lights.nav`, `lights.logo`) | new, unverified: the A330 has one NAV & LOGO switch (`LOGO_LIGHTS_SET` + `NAV_LIGHTS_SET`, position follows `LIGHT LOGO`). `lights.nav` switches both, `lights.logo` only the logo. |
| Seatbelts, No Smoking, nose light | mapping checked against the cockpit model (0 ON/T.O, 1 AUTO/TAXI, 2 OFF) and read in flight – not swapped |
| Gear, flaps, speedbrake, spoilers, EFIS, packs, APU bleed, transponder, COM 1 | taken over from the A32NX, unverified. Interlocks as on the A32NX, with the A330 limits. |

## iniBuilds A321LR

| Value | Source | Status |
|---|---|---|
| FCU values ALT/HDG/SPD/VS | generic `AUTOPILOT … VAR` | verified |
| Navigation (GPS flight plan) | – | empty, the FMS doesn't feed it |
| A/THR | `AUTOPILOT THROTTLE ARM` | wrong (always false), so not used |
| FMA, Master Warning/Caution, ECAM, fuel flow | – | L: variables still to be found |

iniBuilds doesn't publish a list of its L: variables (around 3000), the MCDU/FMS can't be read from outside, and there is no failure menu. So the profile only contains detection and limits. Please check new bindings with `read_simvar` and a state change first (see [README](../README.md)).

## Autoflight callouts (v0.9.2)

The callouts from the KLAX approach in testlog 0.9 depend on these bindings. Without a binding Duopilo doesn't guess – the callout stays off for that aircraft. `get_aircraft_profile` → `callouts` shows the same status live.

| Rule | Needs | A32NX | A330-900neo | A380X | A321LR |
|---|---|---|---|---|---|
| `TCAS_RA` | `fmaVertical` | verified | verified | unverified | missing |
| `ATHR_OFF` | `athrStatus` | unverified | verified | unverified | missing |
| `FMA_CHANGE` | `fmaLateral`, `fmaVertical` (armed modes with `fmaLateralArmed`/`fmaVerticalArmed`) | verified | verified | unverified | missing |
| `LOC_DEVIATION` | `locDeviation`, `fmaLateral` | unverified | missing | missing | missing |
| `GS_DEVIATION` | `gsDeviation`, `fmaVertical` | unverified | missing | missing | missing |
| `APPR_NOT_ACTIVE` | `fmgcPhase`, `speedManaged` | unverified | missing | missing | missing |

New A32NX bindings (names from the FBW documentation, not yet checked in the sim): `fmgcPhase` = `L:A32NX_FMGC_FLIGHT_PHASE` (0 PREFLIGHT … 5 APPROACH, 6 GO AROUND, 7 DONE), `locDeviation` = `L:A32NX_RADIO_RECEIVER_LOC_DEVIATION` and `gsDeviation` = `L:A32NX_RADIO_RECEIVER_GS_DEVIATION` (degrees, converted to dots like the FBW PFD: LOC 0.8°/dot, G/S 0.4°/dot), valid flags `_LOC_IS_VALID`/`_GS_IS_VALID`. For the A330-900neo the variables for FMGC phase and LOC/GS deviation are not known yet – they will be added after a test flight.

## In-game ATC (all aircraft)

| Value | Status |
|---|---|
| `ATC RUNWAY SELECTED` | verified (0 → 1 after runway assignment) |
| `ATC RUNWAY AIRPORT NAME` | text SimVar; `read_simvar` detects it as text automatically |
| Runway number | from `ATC RUNWAY HEADING DEGREES TRUE` minus magnetic variation |
| L/C/R | not available as a SimVar – Duopilo takes it from the SimBrief plan if the number matches |
| `ATC CLEARED IFR`, `ATC CLEARED LANDING` | stayed 0 in the test |
| Assigned altitude, heading, squawk | **not** available as SimVars |
| `ATC CURRENT WAYPOINT ALTITUDE`, `ATC FLIGHTPLAN DIFF …` | in `get_atc` under `experimental` – meaning still to be checked in flight |

External ATC programs (BeyondATC, SayIntentions) aren't visible through SimConnect.

## Texts in profiles

Profiles are language-neutral: IDs, variable names, units and limits (speeds in knots). Texts the pilot sees come from the language files (`src/Duopilo/i18n/<code>.json`, see [i18n.md](i18n.md)).

- **Conditions (`requires`)** of an input action reference their refusal text by key:
  ```json
  "requires": [
    { "variable": "L:A32NX_FCU_AFS_DISPLAY_TRK_FPA_MODE", "equalTo": 0, "messageKey": "profile.requires.trkFpaMode" }
  ]
  ```
  The text is stored under `profile.requires.<name>` in the language files and follows the language setting. Other fields: `unit` (default `number`), `tolerance` (default 0.5), `only` (check only for these choices, e.g. `[ "on" ]`).
- **Your own profiles** can still use `"message": "literal text"` instead of `messageKey`. Duopilo shows that text as is, in any language.
- `description` on bindings and `notes` in the profile are technical metadata in English; they aren't translated.

## General observations

- **Pause:** Duopilo uses the SimConnect event `Pause_EX1` (normal pause, active pause, sim pause). `paused: true` appears in `get_flight_state` and `duopilo_status`.
- **Extreme weather** (e.g. −100 °C) makes EGT implausible. `get_systems` reports this under `plausibility`.
- **Air start:** Values are inconsistent in the first seconds. Monitoring stays silent for 10 seconds after loading.
