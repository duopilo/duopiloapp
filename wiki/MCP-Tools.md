**English** | [Deutsch](DE-MCP-Tools)

# MCP Tools

The tools Claude (or any MCP client) sees. Every response also contains **`activeAlerts`** – the monitoring alerts active right now – so warnings reach Claude even while it asks for something else.

Tool messages (`message`/`note` fields) follow the [language setting](Dashboard-and-Settings#language); values are language-neutral, speeds always in knots.

## Status and flight data

| Tool | Content |
|---|---|
| `duopilo_status` | connection, pause, loaded aircraft (title + path), active profile, profile warnings, monitoring, SimBrief, ECAM and voice loop status |
| `get_flight_state` | altitude (indicated, FL, AGL, radio), IAS/TAS/GS/VS in kt, attitude, heading, position, flaps, gear, spoilers, parking brake, autobrake, AP/A-THR with FMA, speed and altitude target, warnings |
| `get_systems` | engines (N1, N2, EGT, fuel flow kg/h), APU, fuel, weight, lights, signs, transponder, COM, plausibility notes |
| `get_warnings` | master warning/caution, ECAM messages in plain text (FBW A32NX, Headwind A330-900neo), stall/overspeed |
| `get_environment` | wind, OAT, QNH, altimeter setting, visibility, UTC/local time |
| `get_navigation` | the sim's GPS flight plan variables (usually empty with airliner FMSs – use `get_progress`) |
| `read_simvar` | reads any SimVar or `L:` variable once (text variables with `unit: "string"`) |
| `list_input_events` | input events (cockpit controls, `B:` variables) of the loaded aircraft with values; `changesOnly` shows what moved since the last call |

## Monitoring

| Tool | Content |
|---|---|
| `get_alerts` | monitoring alerts since an ID, each with a callout to read aloud |
| `wait_for_alerts` | waits up to 25 s for the next alert – for continuous monitoring in a loop |
| `get_monitoring_status` | flight phase, active/disabled rules, quiet period |
| `get_fma_log` | history of FMA changes with time, IAS and radio altitude (e.g. was ROLL OUT active after the autoland?) |

See [Pilot Monitoring](Pilot-Monitoring).

## Flight plan

| Tool | Content |
|---|---|
| `get_flight_plan` | SimBrief OFP: route, runways, cruise altitude, cost index, fuel, weights, times, navlog |
| `get_progress` | next fix (A32NX: the active FMS waypoint), distance/ETA, TOD, cross-track, fuel against plan, fuel check history (`fuelCheck`), `etaSource` |

See [SimBrief & Flight Plan](SimBrief-and-Flight-Plan).

## Traffic and in-game ATC

| Tool | Content |
|---|---|
| `get_traffic` | traffic nearby (AI, live, multiplayer): callsign, type, distance, clock position, altitude difference, trend, GS, callout for the closest target |
| `traffic_test_start` | developer test, off by default: spawns an AI aircraft (`DUO01`–`DUO04`) that flies a final approach and lands – variant A1 or A2, up to 4 at once, one per runway |
| `traffic_test_status` | state and evaluation of the traffic tests (touchdown, flare, floating, speed against request, stop point, CSV file) |
| `traffic_test_stop` | ends the traffic tests (or the one on a runway) and removes the aircraft |
| `traffic_test_models` | installed aircraft models (title, livery) for the traffic test |
| `get_atc` | MSFS in-game ATC: callsign, clearances, assigned runway (incl. L/C/R from SimBrief) |

See [Traffic](Traffic).

## Aircraft profile

| Tool | Content |
|---|---|
| `get_aircraft_profile` | profile bindings with raw value, plain text and verified status, extra variables, limits, per-callout status (`verified`/`unverified`/`missing`) |
| `reload_profiles` | reloads the profile files |

See [Aircraft Profiles](Aircraft-Profiles).

## Input module

| Tool | Content |
|---|---|
| `list_actions` | which switches Duopilo may operate in this aircraft, whether input is unlocked, verified status per action |
| `prepare_action` | checks value and interlocks, returns the readback – nothing changes yet |
| `confirm_action` | executes after the pilot confirmed the readback, then checks the aircraft state |
| `cancel_action` | discards the prepared input |
| `mark_action_verified` | remembers an action as verified after the pilot saw it work in the cockpit |

See [Input Module](Input-Module).

## Duopilo ATC

| Tool | Content |
|---|---|
| `atc_transmit` | passes on the pilot's transmission; the station's reply comes back (and is spoken) |
| `get_atc_controller` | current station with frequency and voice, expected next pilot call, pending readback, squawk, deviations, history, template errors |
| `atc_control` | start / stop / restart the session, `reload` templates and data, repeat the last transmission |
| `atc_set_value` | set gate, holding point, taxi route, pushback direction, approach type or runway by hand |

See [ATC](ATC).

## Notes for prompt writers

- There is **no tool that writes arbitrary events or variables** – only the profile's predefined actions, through `prepare_action`/`confirm_action`.
- `confirm_action` may return `status: "awaiting_caution_confirmation"` with a `caution` text: read it to the pilot and only confirm again with the same token after a second "confirm".
- Errors always come back as `status: "error"` with a reason.
