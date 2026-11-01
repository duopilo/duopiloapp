**English** | [Deutsch](README.de.md)

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/duopilo-logo.svg">
    <img src="branding/duopilo-logo-light.svg" alt="Duopilo" width="360">
  </picture>
</p>

# Duopilo

Duopilo connects **Microsoft Flight Simulator 2024** to **Claude** via SimConnect. It runs as an MCP server on the sim PC. With it, Claude reads altitude, speed, configuration, navigation and weather itself instead of you reading them out. That makes Claude the **Pilot Monitoring**, while you stay Pilot Flying.

Callouts, monitoring, ATC and the [voice loop](#voice-loop) also work on their own, without Claude.

> Duopilo reads everything, but it only operates switches on the pilot's call: locked by default, with readback and confirmation, and never engines, thrust, fuel, fire or hydraulics (see [Input module](#input-module)).

Website: <https://duopilo.app> · Repository: <https://github.com/duopilo/duopiloapp>

## Formerly Kopilot – what changes when you update

Up to version 0.9.x Duopilo was called **Kopilot**. Most things carry over by themselves; two need you:

> **⚠ Reconnect the connector in Claude.** The MCP server is now called `Duopilo`, and the tool `kopilot_status` is now
> `duopilo_status` – so the tool names in Claude change. In claude.ai remove the old connector and [add it again](#4-add-the-connector-in-claude)
> (name `Duopilo`, same URL – an existing tunnel host keeps working). In Claude Desktop rename the entry in
> `claude_desktop_config.json` to `"duopilo"` and point it to `Duopilo.exe` (see [Connecting Claude Desktop](#connecting-claude-desktop)).

- **Installer:** the new setup takes over an existing Kopilot installation. From `C:\Program Files\Kopilot` it moves to
  `C:\Program Files\Duopilo`, keeps `appsettings.Local.json`, your own profiles and backups, and removes the old installation;
  "Start with Windows" moves over.
- **Self-built (`update.cmd`):** now builds to `C:\Tools\Duopilo` and copies `appsettings.Local.json` and your own profiles from
  `C:\Tools\Kopilot` the first time. The very first run may stop after `git pull` (the old script still builds `src\Kopilot`) –
  just run `update.cmd` again. Delete `C:\Tools\Kopilot` once everything runs.
- **Data folder:** on the first start Duopilo takes over `%LOCALAPPDATA%\Kopilot` as `%LOCALAPPDATA%\Duopilo` (logs, models incl.
  Piper, reports, `data`/`i18n` overrides, ATC session backup, dashboard settings, Claude sign-ins). If an old Kopilot still holds
  files open, Duopilo keeps using the old folder and tries again on the next start (the log says so).
- **Settings:** the section `"Kopilot"` in `appsettings.Local.json`, environment variables `Kopilot__…`, `--Kopilot:…` and log
  categories `Kopilot.*` keep working. Saving on the dashboard's settings page renames the section to `"Duopilo"`.
- **Voice loop:** "Kopilot" stays the fixed wake word (next to the first officer's name). "Duopilo, …" works as well when Whisper
  transcribes it clearly ("Duopilo", "Duo Pilo"); please check that in the sim.
- **Dashboard:** writing calls use the header `X-Duopilo: 1`; `X-Kopilot: 1` is still accepted.

## Tools

| Tool | Content |
|---|---|
| `duopilo_status` | Connection, pause, loaded aircraft (title + path), active profile, monitoring, SimBrief and ECAM status |
| `get_flight_state` | Altitude (IND, FL, AGL, radio), IAS/TAS/GS/VS in kt, attitude, heading, position, flaps, gear, spoilers, parking brake, autobrake, AP/A-THR with FMA, speed and altitude target, warnings |
| `get_systems` | Engines (N1, N2, EGT, fuel flow in kg/h), APU, fuel, weight, lights, seatbelt signs, transponder, COM, plausibility notes |
| `get_warnings` | Master warning/caution, ECAM messages in plain text (FBW A32NX, Headwind A330-900neo), stall/overspeed |
| `get_alerts` | Monitoring alerts since an ID, with a callout to read aloud |
| `wait_for_alerts` | Waits up to 25 s for the next monitoring alert (continuous monitoring in a loop) |
| `get_monitoring_status` | Flight phase, active/disabled rules, quiet period |
| `get_fma_log` | History of FMA changes with time, IAS and radio altitude – e.g. whether ROLL OUT was active after the autoland |
| `get_flight_plan` | SimBrief OFP: route, runways, cruise altitude, cost index, fuel, weights, times, navlog |
| `get_progress` | Progress against the plan: next fix (on the A32NX the active FMS waypoint, e.g. after DIR TO), distance/ETA, TOD, cross-track, fuel vs. plan, history of the automatic fuel checks (`fuelCheck`). ETA from the navlog times (planned winds), corrected by the deviation at the last fix passed; without navlog times from the ground speed (`etaSource`) |
| `get_atc` | In-game ATC: callsign, clearances, assigned runway (incl. L/C/R from SimBrief) |
| `get_traffic` | Traffic nearby (AI and multiplayer): callsign, type, distance, clock bearing, altitude difference, trend, GS, callout for the closest target |
| `traffic_test_start` | Developer test, off by default: spawns an AI aircraft (`DUO01`–`DUO04`) that flies a final approach by waypoints and lands – variant A1 or A2, up to 4 at once, one per runway (see [Traffic test](#traffic-test-developer)) |
| `traffic_test_status` | State and evaluation of the traffic tests: touchdown, flare, floating, speed against request, stop point, CSV file |
| `traffic_test_stop` | Ends the traffic tests (or the one on a runway) and removes the aircraft |
| `traffic_test_models` | Installed aircraft models (title, livery) for the traffic test |
| `get_navigation` | The sim's GPS flight plan variables (usually empty with airliner FMSs – use `get_progress` then) |
| `get_environment` | Wind, OAT, QNH, altimeter setting, visibility, UTC/local time |
| `get_aircraft_profile` | Profile bindings with raw value, plain text and "verified" status, extra variables, limits |
| `read_simvar` | Reads any SimVar or L: variable once (text variables with `unit: "string"`) |
| `list_input_events` | Lists the input events (cockpit controls, B: variables) of the loaded aircraft with values; `changesOnly` shows what moved since the last call – for building profiles |
| `reload_profiles` | Reloads the profile files |
| `list_actions` | Input module: which switches Duopilo may operate in the current aircraft, and whether input is unlocked |
| `prepare_action` | Prepare an input: checks value and interlocks, returns the readback – nothing changes in the cockpit yet |
| `confirm_action` | Execute the input after the pilot has confirmed the readback; the aircraft state is checked afterwards |
| `cancel_action` | Discard a prepared input |
| `mark_action_verified` | Remember an action as verified after you have seen in the cockpit that it works |
| `atc_transmit` | Duopilo ATC: pass on the pilot's transmission, the station's reply comes back (and is spoken) |
| `get_atc_controller` | Duopilo ATC: current station with frequency and voice, expected next pilot transmission, pending readback, squawk, history |
| `atc_control` | Duopilo ATC: start/stop/restart the session, reload templates, repeat the last transmission |
| `atc_set_value` | Duopilo ATC: set gate, holding point, taxi route, pushback direction, approach type or runway by hand |

Every response contains `activeAlerts`: the monitoring alerts active right now. That way Claude catches warnings even while it is asking for something else.

## Pilot Monitoring

Every 200 ms Duopilo checks a few rules and stores alerts with a short English callout:

| Rule | Callout | Condition |
|---|---|---|
| `GEAR_SPEED` | Gear speed | gear not retracted and IAS above VLE |
| `FLAP_SPEED` | Flap speed | IAS above VFE for the flap setting |
| `OVERSPEED` | Overspeed | IAS above VMO or the sim's overspeed warning |
| `STALL` | Stall | the sim's stall warning in the air |
| `SINK_RATE` | Sink rate | below 2500 ft RA, sinking more than 1500–3000 ft/min |
| `BANK_ANGLE` | Bank angle | more than 35° (tighter below 150 ft RA, 45° above 2500 ft RA) |
| `MASTER_WARNING` / `MASTER_CAUTION` | Master caution, ELEC GEN 1 OFF | master light on, with the most recent ECAM message |
| `ECAM_MESSAGE` | HYD G RSVR LO LVL | another ECAM message while master warning/caution is already lit |
| `GEAR_DOWN_HIGH` | Gear still down | gear down in the climb above 2000 ft AGL or above FL150 |
| `SPEED_LIMIT_FL100` | Speed limit | faster than 250 kt below 10,000 ft |
| `SPEED_DEVIATION` | Speed | IAS deviates from the target (+15/−10 kt, on approach +10/−5 kt) and isn't converging on it |
| `AP_DISCONNECT` | Autopilot off | autopilot disconnected in the air |
| `ALT_1000_TO_GO` | One thousand to go | 1000 ft before the FCU altitude |
| `TOP_OF_DESCENT` | Top of descent | A32NX: FMS message T/D REACHED; otherwise TOD from the SimBrief plan |
| `LANDING_CHECK` | Landing checklist complete / Spoilers not armed | at 2000 ft RA in the descent: gear, landing flaps, ground spoilers; again at 1000 ft RA if something is still missing |
| `RUNWAY_ASSIGNED` | Runway two three left assigned | in-game ATC has assigned a runway |
| `TRAFFIC_PROXIMITY` | Traffic, 2 o'clock, 2 miles, 500 feet above, climbing | airborne target closer than 3 NM and within ±1000 ft (against own pressure altitude), own aircraft above 500 ft AGL; then 60 s quiet (see [Traffic](#traffic)) |
| `TCAS_RA` | TCAS RA / Clear of conflict | vertical FMA shows TCAS (resolution advisory) for 0.3 s – warning; "Clear of conflict" 1.5 s after it ends |
| `ATHR_OFF` | Autothrust off | A/THR goes from armed/active to off in the air (not when it disconnects below 100 ft RA at touchdown) |
| `FMA_CHANGE` | Loc star / Glideslope star / Alt blue | new lateral or vertical mode, or a newly armed mode ("… blue"), in the air once it has stood for 1 s – info |
| `LOC_DEVIATION` | Loc | LOC or LAND engaged, 50–2000 ft RA, localizer deviation more than ¼ dot for 1 s |
| `GS_DEVIATION` | Glideslope | G/S or LAND engaged, 100–2000 ft RA, glideslope deviation more than 1 dot for 1 s |
| `APPR_NOT_ACTIVE` | Approach phase not activated | from 15 NM before the destination with managed speed, but the FMGC is not in the approach phase yet – once per approach |
| `SIM_PAUSED` | Sim paused | sim paused – all other rules rest |

The autoflight callouts (`TCAS_RA` to `APPR_NOT_ACTIVE`) depend on profile bindings (FMA, A/THR, FMGC phase, LOC/GS deviation). Duopilo doesn't guess: without a binding, the callout stays off for that aircraft. `get_aircraft_profile` → `callouts` shows per rule whether it is `verified`, `unverified` (binding present, not yet checked in the sim) or `missing` – the overview per aircraft is in [docs/aircraft.md](docs/aircraft.md#autoflight-callouts-v092).

On top of that come the callouts a Pilot Monitoring makes:

| Rule | Callout | When |
|---|---|---|
| `PM_100_KNOTS`, `PM_V1`, `PM_ROTATE` | One hundred knots / V1 / Rotate | takeoff roll (V1/VR from the MCDU, on the A32NX) |
| `PM_POSITIVE_CLIMB` | Positive climb | after liftoff, as soon as it is really climbing |
| `PM_SPOILERS` | Spoilers / No spoilers | after touchdown (No spoilers after 3 s without ground spoilers) |
| `PM_REVERSE` | Reverse green | thrust reversers deployed |
| `PM_DECEL` | Decel | autobrake DECEL (A32NX), otherwise noticeable deceleration |
| `PM_70_KNOTS` | Seventy knots | landing roll below 70 kt |
| `PM_TEN_THOUSAND` | Ten thousand | through 10,000 ft, climbing and descending |
| `PM_TRANSITION` | Transition altitude / Transition level | TA in the climb, TL in the descent (from the FMS, otherwise SimBrief) |
| `PM_FLAPS` | Speed checked, flaps two / Flaps zero | flap lever in a new position for 1.5 s; when extending, with a speed check against VFE |
| `PM_GEAR` | Gear down / Gear up | gear lever in the air |
| `TAXI_SPEED` | Taxi speed | on the ground above 25 kt ground speed for more than 2 s (not during takeoff and landing roll), at most every 30 s – adjustable in `voice-settings.json` |
| `FUEL_CHECK` | HENDY, fuel 34.1, 2.2 below plan | cruise: passing an OFP waypoint, on long legs without a waypoint every 30 min (see [Fuel Check](#fuel-check)) |
| `FUEL_WARNING` | HENDY, fuel 31.2, 2.8 below plan, deviation increased by 0.6 | deviation more than 300 kg worse since the last check, or landing fuel below minimum + buffer |

Radio altitudes (2500, 1000, 500 …), "Hundred above", "Minimum" and "Retard" are called out by the A32NX itself. Duopilo can speak them as well – for aircraft without their own callouts: Dashboard → Callouts → Voice, or `Duopilo:Monitoring:AircraftAutoCallouts`.

After loading a flight, changing aircraft or an air start, monitoring stays silent for 10 seconds, after a pause for 3 seconds – that's when values jump. Disable individual rules in `appsettings.Local.json`:

```jsonc
"Duopilo": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

### Fuel Check

With a SimBrief plan, Duopilo compares the fuel on board with the plan during cruise – like the crew's fuel check:

- **At every OFP waypoint** (leg with SimBrief phase CRZ, excluding TOC/TOD) Duopilo calls out: "Hendy, fuel 34.1, 2.2 below plan" (tonnes, one decimal; below 50 kg deviation "on plan"). The waypoint is pronounced like everywhere else (see Speech output): navaids with their name ("Wabush" instead of YWK), coordinate points as "fifty three north fifty west".
- **Interval:** if no waypoint comes up for 30 min in cruise (sim time, pauses don't count), there is a check without a location: "Fuel 34.1, 2.2 below plan".
- **Warning (`FUEL_WARNING`, caution)** – checked continuously, not just at waypoints:
  - the deviation has grown worse by more than `DeviationIncreaseKg` since the last check (suspected leak): "…, deviation increased by 0.6";
  - the predicted landing fuel (planned landing fuel + today's deviation) drops below minimum + buffer or below the minimum: "…, landing fuel 6.8, minimum 6.5" / "…, landing fuel 6.2, below minimum". The minimum is `fuel_min_onboard` at the destination according to the navlog (otherwise alternate plus final reserve). The buffer is at most half of what the plan lands above the minimum – otherwise a tightly planned flight would warn at the very first check. The same warning only comes again once the landing fuel has been 100 kg above the limit in between.
- **Refueling** via the toolbar or EFB (fuel rises) and drops faster than any fuel burn are not leaks: Duopilo reports them silently ("Refuelling detected: 3,530 → 11,090 kg") and restarts the comparison.

Below the route, the dashboard shows the trend of the deviation against plan (x = distance like the route bar, warnings amber, refueling as a cyan line) with the latest checks as a table; `get_progress` returns it as `fuelCheck`. Configurable in `appsettings.Local.json`:

```jsonc
"Duopilo": { "Monitoring": { "FuelCheck": {
  "IntervalMinutes": 30,       // 0 = no interval check
  "DeviationIncreaseKg": 300,  // 0 = no warning when the deviation grows
  "LandingBufferKg": 500,
  "MinSpacingMinutes": 0       // e.g. 10 = don't call out every waypoint over Europe
} } }
```

To turn off only the callouts and keep the warnings: `FUEL_CHECK` in `DisabledRules` (the history in the dashboard keeps running).

**Push:** Claude.ai and voice mode can't receive messages from the server. That's why Duopilo speaks the callouts itself (see below). Claude also sees the alerts via `activeAlerts` in every response or via `wait_for_alerts`.

## Traffic

`get_traffic` queries all aircraft and helicopters nearby via SimConnect (`RequestDataOnSimObjectType`) – AI traffic, live traffic and multiplayer, as far as enabled in the sim. Your own aircraft is excluded.

| Parameter | Default | Meaning |
|---|---|---|
| `radiusNm` | 40 | search radius, at most 108 NM (200 km, SimConnect's limit) |
| `limit` | 10 | at most this many targets, closest first (1–50) |
| `airborneOnly` | false | leave out aircraft on the ground (gate, taxiway) |

Per target: `ident`/`callsign` (airline + flight number if both are set, e.g. `Lufthansa 123`; otherwise the ATC ID, i.e. the registration like `N605SD`), `atcId`, `type` (ATC MODEL, e.g. `A320`), `distanceNm`, `clock` ("2 o'clock"), `relativeBearingDeg`, `bearingTrueDeg`, `altitudeFt`, `altitudeDiffFt` (+ = above), `verticalTrend` (climbing/descending/level from ±500 ft/min), `groundSpeedKt` with `groundSpeedSource`, `headingTrueDeg`/`headingMagDeg`, `onGround`. The closest target has `closest: true`; `callout` is the traffic call for it:

```
Traffic, 2 o'clock, 5 miles, 1000 feet above, descending
```

- **Clock:** relative to your own heading, 30° each: 12 o'clock = 345° to below 15°.
- **Callsign and type:** live-traffic GA often reports an internal number without an airline as `ATC FLIGHT NUMBER` (e.g. `1432126`) – that is not a callsign, so the registration is used instead. Types sometimes arrive as localization keys (`ATCCOM.AC MODEL C441.0.tts`, `ATCCOM.ATC NAME CITATION.0.tts`, `TT:ATCCOM.AC_MODEL_A320.0.text`); Duopilo turns them into `C441`, `CITATION`, `A320`.
- **Distance:** horizontal (great circle).
- **Altitude:** `altitudeFt` is the target's altitude as SimConnect reports it (`PLANE ALTITUDE`). For MSFS live traffic that is the barometric altitude from ADS-B, i.e. the flight level – not the true altitude. That's why `altitudeDiffFt` is computed like TCAS against your own pressure altitude (`PRESSURE ALTITUDE`, 1013.25 hPa). Against the true altitude, the difference was way off in cold air or low pressure: at FL320 with a true altitude of 30,630 ft, a target at FL330 appeared 2,400 ft instead of 1,000 ft above. `ownAircraft` shows pressure altitude, true altitude and, via `altitudeReference` (`pressure`/`true`), which one was used; without a pressure altitude Duopilo uses the true altitude.
- **Ground speed:** the SimVar `GROUND VELOCITY` is useless for live traffic (in the test flight usually about a factor of 3.3 too small, e.g. 169 instead of 556 kt). Duopilo therefore computes the ground speed from the change in position between two queries, 5–120 s apart, preferably about 20 s (`groundSpeedSource: "position"`, interval in `groundSpeedBaselineSec`, raw SimVar value for comparison in `groundSpeedSimVarKt`). If the traffic warning's background scan is running, this is available after a few seconds; otherwise from the second `get_traffic` call. Until then the field holds the SimVar (`groundSpeedSource: "simvar"`) – read with caution.
- **Trend:** from `VERTICAL SPEED`. If a target reports 0 there (often in multiplayer), Duopilo computes the V/S from the altitude change between two queries (`verticalSpeedSource: "derived"`).
- **No data:** `status: "no_traffic"` with a hint if MSFS reports no other aircraft – usually AI traffic or multiplayer is then turned off in the sim. `no_response` if MSFS doesn't answer at all.

**Traffic warning `TRAFFIC_PROXIMITY`:** as long as the rule is on and the aircraft is flying, Duopilo queries traffic within 6 NM every 3 s. Below 500 ft above ground, while paused and on the ground it rests. Disable it with `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]`, adjust the limits:

```jsonc
"Duopilo": { "Monitoring": { "Traffic": { "ProximityRangeNm": 3, "ProximityAltitudeFt": 1000, "ProximityCooldownSec": 60, "ScanIntervalSec": 3 } } }
```

`"AltitudeReference": "True"` in the same section switches the altitude difference (get_traffic and the traffic warning) back to your own true altitude – for targets whose `PLANE ALTITUDE` is a true altitude. For AI traffic and multiplayer this hasn't been checked in the sim yet; the default is `"Pressure"`.

This is not TCAS: no resolution advisory, no prediction of the closest point of approach – just distance and altitude at this moment.

**Separate connection for traffic:** up to 0.9 the query ran over the main connection. Its managed SimConnect library reads every response with the full structure without checking the message size (`dwSize`) – the suspected cause of the crashes in the test flight (AccessViolation in `CSTRMarshaler.ConvertFixedToManaged`, not catchable in .NET). That also fits crashes without traffic: the response "no object in range" (`dwoutof = 0`) comes with every helicopter query. Now Duopilo opens a separate SimConnect connection for traffic directly via the native `SimConnect.dll` and reads only what belongs to the message according to `dwSize` and the received size. Fields that don't arrive completely stay empty.

- The traffic warning's background scan only queries numbers (position, altitude, heading, speed, V/S, on ground). The warning knows callsign and type if `get_traffic` has seen the target in the last 15 minutes.
- Text fields (ATC ID, airline, flight number, type) are only queried by `get_traffic`.
- `TRAFFIC_PROXIMITY` can therefore be switched on again – remove the workaround `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]` from `appsettings.Local.json`.

**Checking the responses:** Duopilo counts all responses and writes each one to `%LOCALAPPDATA%\Duopilo\logs\traffic-messages.csv` (size, object, entry, flags `empty`/`short`/`mismatch`; about 250 KB per flight hour, rotated at 10 MB). Under `messageCheck`, `get_traffic` shows the verdict on the two crash hypotheses: whether empty responses are shorter than the old structure (368 bytes) and whether live traffic delivers data blocks that are too short. The first responses and all anomalies also go to the normal log, plus a summary every 30 minutes. Disable with `"Duopilo": { "Monitoring": { "Traffic": { "MessageLog": false } } }`.

### Traffic test (developer)

First step towards Duopilo's own traffic (research: [docs/research/eigener-verkehr.md](docs/research/eigener-verkehr.md)): AI aircraft without ATC control fly a final approach by waypoints, land and stop on the runway. Off by default because it creates objects in the sim – switch it on in `appsettings.Local.json` and restart Duopilo:

```json
{ "Duopilo": { "TrafficTest": { "Enabled": true } } }
```

- **Start:** `traffic_test_start` with airport and runway end (`EDDL`, `23L`), at most 80 NM from your aircraft. Pick a runway you are not using.
  - **Several at once:** up to 4 tests, one per runway – e.g. a parallel approach with one call for `05L` and one for `05R`. A new test on a runway that already has one replaces it.
  - **A1:** spawn 5 NM out with the gear already down – without it, AI aircraft tend to float above the runway (see the research).
  - **A2:** spawn 15 NM out with the gear up, gear command at 3000 ft above the runway, so the gear is down by 2500 ft like in a real approach. The evaluation shows the height at which it was down (`gearExtendedAtFt`) and whether that was by 2500 ft (`gearDownInTime`). A1 starts below 2500 ft and can't make it.
  - **B1 – takeoff:** spawn lined up on the runway 60 m behind its start, takeoff roll, liftoff, climb straight ahead to 1000 ft 1 NM past the runway end and on to 3000 ft above the runway, gear up at a positive climb rate. Takeoff speed about V2 = Vapp + 10 of the type. The test ends at 2800 ft (`climbed`), on the ground past the runway end (`no_liftoff`) or if the aircraft doesn't start rolling within 90 s (`no_roll`). The evaluation has `takeoff`: roll start, liftoff point, speed and pitch, roll distance, height over the runway end, climb rate, pitch and bank up to 1500 ft, gear up, lights at liftoff. If the AI doesn't roll towards a first waypoint in the air, `rollM` puts a ground waypoint with the takeoff speed that far ahead.
  - **C1 – overflight:** at cruise altitude, flown by the waypoint AI: spawn `spawnNm` (default 40, general aviation 15) before the middle of the runway at FL350 (general aviation FL85, `flightLevel`), straight over it in the runway's direction (`trackDeg` for another track) and on, at 450 kt true (`cruiseKt`; general aviation twice the approach speed), handed to the AI as is – it takes the speed as true airspeed (beta.18 flew 265 kt over the ground with 265 kt asked on FL350). Nav, beacon, strobes and logo on, landing light off, gear up. The test ends the same distance behind the point (`passed`); the evaluation has `overflight` (when overhead, distance off track, altitude and speed there, max track and altitude deviation, mean IAS and ground speed). One test per runway applies here too – pick a runway designator nobody else uses.
- **Airport rules:** the aircraft must fit airport and runway, otherwise the start is refused with the reasons (`ignoreRunwayRules: true` tests anyway, the reasons go into the events). Every airport is classified from OurAirports (`data\ourairports\airports.csv`): military airfields (recognized by name, never with scheduled service) only military aircraft – A400M and Super Hornet as full models, there's no AI dummy –, heliports, seaplane bases and closed fields no traffic, small fields and fields without scheduled service general aviation only. Per airport in `data\traffic\airport-rules.json`: categories and military instead of the classification, a traffic mix (types with weights – EDHI mostly Belugas, now and then another Airbus; Toulouse with a Beluga now and then), runway rules (landings or takeoffs only, categories, excluded ICAO types with `B74*` as prefix, heaviest wake category) – bundled for EDDF: 07L/25R (Landebahn Nordwest) landings only without B747, A380, MD-11, An-124, L-1011, 18 (Startbahn West) takeoffs only, 36 closed. Without `title` the test picks the model by these rules. Your own file in `%LOCALAPPDATA%\Duopilo\data\traffic\` replaces the rules of an airport and applies at the next start. On top Duopilo checks length (landing about 0.075 × Vapp² m, e.g. A320 1370 m, takeoff 1.4 times that) and width (heavy and super 45 m, other airliners 30 m, heavy military transports 27 m). The overflight (C1) needs no runway.
- **Aircraft:** without `title` Duopilo takes an FSLTL A320 if installed, otherwise any A320; `traffic_test_models` lists the installed models with liveries. Tail numbers `DUO01` to `DUO04`, so `get_traffic` shows them as well. Full flyable aircraft (e.g. the A320neo) ignore the gear command as AI and only lower the gear themselves shortly before touchdown – simple AI models (FSLTL) are the better choice. FlyByWire and Headwind models are excluded from Duopilo's traffic (as AI their gear only comes half out and the engines don't spin): the automatic choice skips them, `traffic_test_models` marks them with `excluded`, and a test with one of them shows the warning `excludedModel`.
- **AI traffic models first:** the PassiveAircraft models of MSFS 2024 (`Asobo PassiveAircraft …`, about 250 types with airline liveries) are made for AI traffic – lights and gear follow the commands. Full flyable aircraft show no lights as AI (A320neo V2). The approval list therefore maps the PassiveAircraft titles to ICAO types with an approach speed per type; the full models stay as a fallback.
- **Speed:** the AI ignores the speed in the waypoints (every model flew 160–169 kt, the ATR too) and came in too high and too fast. Duopilo therefore sets `AI DESIRED SPEED` on approach to the speed of the next waypoint – from the approach speed of the type (`approachKt`): Vapp + 25 from 6 NM, Vapp + 5 below, Vapp at the threshold, not below Vapp − 5 behind it while still in the air. In beta.14 the AI overwrote that too (169 kt throughout), so `speedMethod` chooses between experiments: `desired` (default, as described), `throttle` (waypoints in the air with a fixed throttle, `throttlePercent`, default 30 %), `velocity` (`VELOCITY BODY Z` five times a second to the wanted speed as TAS) or `off`.
- **Kinematic final (`kinematicNm`):** the waypoint AI flies no slower than about 169 kt in the air (whatever `speedMethod` says – `throttle` made it 250 kt, `velocity` threw it off the path) and lands at a fixed 133 kt. With `kinematicNm` (1–12, e.g. 4) Duopilo takes over at that distance: the AI is released, the aircraft frozen and its position, height and attitude set every frame, like vPilot does. Speed down to Vapp + 5 and to Vapp from 0.5 NM, smoothly onto the 3° path and the centerline, flare from 30 ft (general aviation 15 ft) to about 150 ft/min with the nose coming up to about 5.5°, touchdown around 450 m behind the threshold, nose down to the attitude the model stands at on the ground (`STATIC PITCH`), braking to the stop point. Over the runway the runway height follows the measured ground smoothly (6 ft/s). With `kinematicNm` equal to `spawnNm` (A1: 5) the kinematics fly from the spawn on. The status shows `kinematics` (phase, take-over point, flare, touchdown); the CSV has `kin_phase`. Takeoff, climb and go-around stay with the waypoint AI.
- **Approval list:** aircraft types that may fly in Duopilo's traffic, with ICAO type and exact model titles – `verified` (gear and landing checked in the sim: A320neo V2 as A20N, A330-300 as A333) or `candidate` (landed in a test: A321, A310, A330 BelugaXL, 747-8i, Asobo 737-800); the general aviation of MSFS 2024 (Cessna 152/172/Caravan/Corvalis/Titan/SkyCourier/CJ4/Longitude, Bonanza, Baron, King Air C90/350, SR22, Vision Jet, TBM 930, DA40/DA62/DV20, PC-6/PC-12/PC-24, DR400, Beaver, Twin Otter) is on the list as `untested`, recognized by title words. `traffic_test_models` shows `type` and `status` per model (`approvedOnly` filters), the automatic choice prefers verified models, and models outside the list get the warning `notApproved`.
- **Lights:** Duopilo switches them itself (the sim doesn't do it for these aircraft): nav, beacon, strobes, landing, taxi and logo on at spawn, after touchdown below 60 kt landing lights and strobes off – via the `…_SET` events and the `LIGHT …` simvars together. The evaluation shows which lights were on (`lightsOnFinal`, `lightsAfterLanding`), the CSV has them per second. When the AI goes to sleep after landing it switches every light off – Duopilo then sends nav, beacon, taxi and logo again (up to three times).
- **Gear:** `gear` chooses the command – `all` (default: event `GEAR_DOWN`, event `GEAR_SET` 1 and the simvar `GEAR HANDLE POSITION`), `down`, `set` or `handle`. If the gear doesn't extend in the air, Duopilo sends it up to three more times. The CSV logs total, nose and both main gear positions, because not every model keeps all of them up to date.
- **Path:** 3° glide path to 50 ft over the threshold, first ground waypoint 150 m behind the threshold (`touchdownM`, −500 to 600 m – the AI switches to its landing mode about 1 km before it and touched down 100–700 m behind it), rollout with decreasing speed and stop on the runway. One waypoint lies 60 m past the stop point, because the AI stops short of the last one.
- **Last mile lower:** the AI levels off on short final and crossed the threshold 170–190 ft high in the first tests. That's why the waypoints of the last mile lie below the glide path: the threshold by `finalLowFt` (default 100 ft), 0.5 NM by 60 %, 1 NM by a quarter. `finalLowFt: 0` flies exactly on the glide path; the evaluation always measures against the glide path.
- **Go-around:** if the aircraft crosses the threshold more than 150 ft above the glide path or more than Vapp + 20 kt, or isn't on the ground 900 m behind the threshold (short runways: a third of the length), it goes around: new waypoints straight ahead, climb to 1500 ft and then 3000 ft above the runway, faster, gear up at a positive climb rate, landing light stays on. The test ends at 2800 ft (`go_around`) or 5 minutes later (`go_around_timeout`). The evaluation has `goAround` (reason, where and how high, lowest and highest point afterwards, seconds until the gear was up). `goAround: false` always lands.
- **Evaluation:** `traffic_test_status` (browser: `/traffic-test`, models: `/traffic-test/models?filter=A320`) – verdict `stopped`, `landed`, `floated` (hovers above the runway), `airborne`, `go_around` or `go_around_ignored` (went around but touched down anyway), speed against request, values at the gates (deviation from the glide path, pitch), touchdown point, speed, sink rate and pitch, flare (how far the nose came up before touchdown), max bank on final, float seconds, stop point. With `runway` only that test. Samples once a second in `%LOCALAPPDATA%\Duopilo\traffic-tests\` (CSV), the evaluation as JSON next to it.
- **End:** `traffic_test_stop` removes the aircraft (with `runway` only that one); they are also removed after `MaxMinutes` (default 15) and when Duopilo exits.

## Speech output

Duopilo speaks the callouts itself, in English. Warnings interrupt everything else, older alerts expire after 8 seconds.

**Pronunciation:** ATC, callouts, fuel check and the replies of the voice loop share one pronunciation (`Speech/Pronunciation.cs`), applied before the text goes to the voice:

| What | Spoken |
|---|---|
| Fixes with five letters | as a word: HENDY → "Hendy" |
| Navaids (up to three letters) | with the name from the OFP navlog: YWK → "Wabush"; without a name in the NATO alphabet: FFM → "foxtrot foxtrot mike" |
| ICAO codes | always spelled: KLAX → "kilo lima alpha x-ray" |
| Coordinate points | 53N050W, 5350N → "fifty three north fifty west"; 5530N → "fifty five thirty north …" |
| Runways | 25R → "two five right", 09 → "zero niner" |
| Squawks, headings, speeds, frequencies | digit by digit, 9 as "niner", frequencies with "decimal" |
| Flight levels | "flight level three four zero", whole hundreds "flight level one hundred" (ICAO) |
| Altitudes | in thousands: "one zero thousand", "two thousand five hundred" |
| ECAM titles | spelled out: "ENG 1 FIRE" → "engine one fire", "APU" → "A P U" |

Navlog names and the plan's airports come from the loaded SimBrief plan. Readbacks also accept the name ("direct Wabush") and naturally spoken coordinates ("fifty five north twenty west").

There are two voices:

| | Windows voice | Kokoro |
|---|---|---|
| Sound | Windows text-to-speech | neural, more natural |
| Requirement | English Windows voice installed | nothing – Duopilo downloads the model (~310 MB) the first time it's needed |
| Latency | practically none | CPU computing time; fixed callouts are computed in advance |

**Automatic** (default) uses the Windows voice if an English one is installed, otherwise Kokoro. While Kokoro is loading, the browser reads aloud in the dashboard.

In the dashboard under **Callouts → Voice** you set:
- Speech output: Automatic, Windows or Kokoro
- Windows voice (e.g. George or Hazel for en-GB) or Kokoro voice (e.g. George, Emma, Michael)
- Output device, e.g. the headset instead of the speakers
- Volume and rate
- From which level it speaks (all, caution and above, warnings only)
- Whether radio altitudes/minimum/retard are added

It also shows which voice is speaking right now and how long Kokoro took to compute the last callout. You can also switch speech on and off in the tray menu (**Speak callouts**).

The settings live in `%LOCALAPPDATA%\Duopilo\speech.json`, the Kokoro model under `%LOCALAPPDATA%\Duopilo\models`. Only the tray instance speaks, not the stdio instance that Claude Desktop starts – otherwise everything would be heard twice. Kokoro uses 2 CPU threads (`Duopilo:Speech:KokoroThreads`) so MSFS isn't slowed down.

**Installing an English Windows voice** (optional, for the Windows voice): Settings → Time & language → Language & region → Add a language → *English (United Kingdom)*, selecting "Text-to-speech". Then restart Duopilo.

Mute individual callouts (still visible, but not spoken): `"mutedRules": [ "PM_GEAR" ]` in `speech.json`. Turn them off completely: `Duopilo:Monitoring:DisabledRules`.

Licenses: Kokoro-82M and its voices are under Apache 2.0, KokoroSharp under MIT.

## Input module

Duopilo can press buttons and set switches in the cockpit – as Pilot Monitoring, on the pilot's call. Every input has two steps:

1. You say e.g. "Set speed two five zero". Claude calls `prepare_action`, Duopilo checks value and interlocks and returns the readback: "Speed 250 selected".
2. Claude reads the readback aloud. Only when you say "confirm" does Claude call `confirm_action`. Duopilo checks the interlocks again against the current state, executes, and then reads back from the aircraft whether the value has arrived.

If you say "negative" or correct the value, the action is discarded. A prepared action expires after 60 seconds, and there is only ever one.

**Locked by default.** Unlock in the dashboard: click **Input locked** twice in the callouts bar ("Really unlock?"). There you also see the pending action with a countdown – including one prepared via Claude Desktop – and can cancel it. Below it is the log of all inputs. Alternatively `Duopilo:Input:Enabled` in `appsettings.Local.json`; the dashboard switch takes precedence and applies to all Duopilo instances.

**What works (A32NX):**

| Area | Actions |
|---|---|
| FCU | Speed (selected/managed), heading (selected/managed), set altitude, pull/push, increment 100/1000, V/S, LOC, APPR, EXPED, AP 1/2, A/THR |
| EFIS | QNH in hPa, STD/QNH, LS, FD, ND mode and range (captain) |
| Configuration | Gear, flap lever, speedbrake, arm ground spoilers |
| Brakes | Autobrake OFF/LO/MED/MAX |
| Lights | Beacon, wing, runway turn-off, landing (on/off/retract), nose (T.O/taxi/off), strobe (on/auto/off) |
| Signs | Seat belts, no smoking/no PED |
| Anti-ice | Engines (both or individually), wing, probe/window heat |
| Air/APU/Elec | Pack 1/2, APU master, APU start, APU bleed, external power (on the ground only, with GPU) |
| Transponder | Squawk, mode, ALT RPTG, TCAS mode and traffic |
| Radio | COM 1 standby, swap |

NAV & LOGO is missing: on the A32NX that switch is only reachable as an input event (`B:`), which Duopilo can't do yet.

**What never works:** engine masters, ignition/start, thrust, fire handles and agents, fuel (pumps, crossfeed), hydraulics (pumps, PTU, RAT), IDG disconnect, MCDU, pause/slew/sim control – not even with a confirmation. Anything that can damage the aircraft or is irreversible, the pilots confirm themselves. This is fixed in code; even an edited profile can't get around it:
- Duopilo only sends events from a fixed list (FCU, autobrake, gear, flaps, spoilers, lights, signs, transponder, COM).
- Duopilo only writes L: variables, never standard SimVars, and the block list applies there too.
- Gear and flaps only go through their events, and only together with the matching interlock.
- Areas and IDs like `Engine`, `Fuel`, `Fire`, `Hydraulic`, `eng.*`, `fuel.*`, `*.idg1` are removed on load.
- Safety-relevant switches (gear, flaps, speedbrake, batteries, packs, APU master, transponder, TCAS, AP/ATHR, FCU altitude, ADIRS) are only available under their standard ID (`gear.down`, `air.pack1` …) – otherwise the interlocks below wouldn't apply.
- There is no tool that writes arbitrary events or variables.

**Interlocks** (rejected with a reason):
- Gear up only in the air
- Gear down only up to VLO (A320/A330: 250 kt) and below the maximum altitude (`maxGearAltitudeFt`, A320/A330: 25,000 ft pressure altitude)
- Flaps only up to the VFE of the new setting
- No speedbrake extension in the inhibited flap settings (`speedbrakeInhibitFlaps`, A320/A330: FULL)
- Batteries off only on the ground, both packs off only on the ground (one pack off in flight is fine as long as the other one is on)
- APU master off not while APU bleed is on
- ADIRS off only on the ground (irreversible in flight – no realignment in the air)

**Extra confirmation with a caution** (a second "confirm" after the readback): transponder STBY, ALT RPTG off and TCAS STBY in flight · squawk 7500/7600/7700 · ADIRS off on the ground · AP 1/2 and A/THR off in flight · FCU altitude below the MSA or outside the ATC clearance (`clearedAltitude`; if the altitude matches the clearance, the MSA check is skipped). The first `confirm_action` then doesn't execute but returns `status: "awaiting_caution_confirmation"` with `caution`, e.g. "Caution, transponder standby in flight. Confirm transponder standby?". Only the second "confirm" with the same token executes. If the switch is already in that position, the extra confirmation is skipped. It applies in every flow – a later "execute + callout" mode must ask it too (`ActionPlan.NeedsCrossConfirmation`).

**If a limit is missing in the profile** (VLO/VLE, VFE of a setting, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource`), the action only works with the extra confirmation. This is shown in the dashboard log ("Limit missing"), in the log and in `duopilo_status` (`profileWarnings`). On top of that come per-action conditions, e.g. V/S and heading not in TRK/FPA mode, QNH only in QNH mode, APU start only with APU master on.

**Reading back gear and flaps:** after `gear.down` Duopilo waits until all legs are locked and then says "Gear down, three green" – otherwise "Gear disagree" after 20 s. Likewise "Gear up" and "Flaps two" only once the position is reached (flaps and slats have stopped moving), otherwise "Flaps disagree". Pauses extend the waiting time. The callouts run as PM_GEAR/PM_FLAPS through speech output and `get_alerts`; monitoring then doesn't additionally call out the lever change. Duopilo reads the MSFS standard values (`GEAR CENTER/LEFT/RIGHT POSITION`, `TRAILING/LEADING EDGE FLAPS LEFT PERCENT`), which can be overridden in the profile under `verification`.

The actions are defined in the aircraft profile under `actions` (events, variables, readback, check). They are derived from the FBW source code. Whether an action has arrived is shown in the dashboard log (✓/✗).

**How Duopilo checks:**
- Values are set absolutely. If the value is already set, Duopilo writes nothing and reports `alreadySet`.
- The new value has to stay for a while (`holdMs`, default 1 s). If the aircraft resets it immediately – e.g. APU START without power – the action counts as not accepted.
- `verification` in the response:
  - `confirmed`: Duopilo read a real aircraft state (FCU display, flap lever, light, ENG ANTI ICE, contactor …).
  - `unverified`: Duopilo could only read the switch position; the effect isn't proven.
  - `failed`: the aircraft didn't accept the value.
- `stateAfter` is the raw value of the checked variable – for selector switches the position (e.g. seatbelts 0 = ON). `stateAfterText` names the matching choice (`on`, `auto`, `off` …).
- Only one action is ever pending. Every new `prepare_action` discards the pending one – even if it is rejected itself. An older, slow call won't create an action afterwards; so the token from the response always belongs to the pending action. With an old token, `confirm_action` tells you what became of the action (replaced, expired, already used, other Duopilo instance).
- Errors always come back as `status: "error"` with a reason, details in the log.

**Remembering verified actions:** tested in the sim are fcu.heading, flaps, lights.beacon, antiice.engines and apu.bleed (`"verified": true` in the profile). Duopilo remembers further ones in `%LOCALAPPDATA%\Duopilo\verified-actions.json` – `update.cmd` doesn't overwrite it:
- automatically when an execution was `confirmed`;
- via `mark_action_verified` when, after an `unverified` execution, you see in the cockpit that it worked. A confirmation or state word is enough ("confirm", "check", "on", "off", "avail", German "läuft"), as is the executed value or the action's name ("Seatbelts on", "APU avail", "Confirm, no smoking, auto") – as long as nothing is negated. `confirm_action` stays strict.

If an action's definition in the profile changes, the stored verification no longer applies. `list_actions` shows `verified` per action: `profile`, `sim`, `pilot` or empty.

## ATC

Duopilo comes with its own ATC: a state machine with fixed text templates, **no language model**. You talk on the radio as usual, Claude passes your transmission on with `atc_transmit` (or the voice loop does it directly, see below), and Duopilo answers as the responsible unit and speaks the reply itself – each station with its own Kokoro voice. Phraseology in English, ICAO style, numbers digit by digit ("squawk two two zero five"), speeds always in knots, never Mach; fixes, navaids and coordinates as described under Speech output ("proceed direct Wabush").

**Flow** (transitions come from your transmissions and from the flight state):

| Station | What happens | On to the next station |
|---|---|---|
| Delivery | IFR clearance with SID, runway, initial climb and squawk; readback is checked | after a correct readback: "readback correct, contact Ground" |
| Ground | Push and start, taxi clearance to the holding point (readback holding point/runway) | stopped at the holding point (8 s), or you call "ready" |
| Tower | Line up and wait, takeoff clearance with wind (automatically as soon as you're on the runway, at the latest after 60 s) | from 1500 ft AGL: "contact Departure" |
| Departure | "identified, climb FL150", optionally "proceed direct" to the first fix after the SID | 2000 ft below the cleared altitude → Center of the FIR |
| Center | Climb to cruise altitude, level requests ("request climb flight level three four zero" → "climb and maintain FL340"), step climbs from the OFP ("when ready climb and maintain FL340"), at top of descent "descend via ANJLL4 arrival" (with a STAR in the OFP) or "descend FL110", below the transition altitude with QNH | at every FIR boundary to the next Center, within the FIR at every sector boundary ("contact Montreal Center one three three decimal zero two five") |
| Oceanic | Clearance about 30 minutes before the entry point, then position reports at every oceanic fix ("roger") | at the exit point to the domestic unit, e.g. after HOIST to Gander Center (short check-in, no more position reports) |
| Approach | Descent, STAR, expected approach and runway, QNH; approach clearance from 20 nm | from 12 nm: "contact Tower" |
| Tower | Landing clearance with wind (at the latest 4 nm before the threshold) | after landing below 40 kt: "vacate via next exit, contact Ground" |
| Ground | Taxi to the gate | end, once the aircraft is stopped with the parking brake set |

On short routes without oceanic fixes (coordinates like `55N020W` or a FIR marked `oceanic` in the templates), Oceanic is skipped. If you take off without clearances, ATC catches up to Tower during the takeoff roll. If you start the session in the air, it begins with the unit responsible there – level above the planned cruise altitude (e.g. after a step climb), your current level counts as cleared. If you start it only in oceanic airspace or past the entry point (e.g. after restarting Duopilo), the oceanic clearance counts as issued – likewise if you reach the entry point without requesting it.

**Readback:** for clearances Duopilo checks the values listed in the template (`readback`), e.g. SID, altitude and squawk for the route clearance. Duopilo understands digits ("2205", "FL110") as well as spoken numbers ("two two zero five", "five thousand feet", "one two one decimal eight") and the NATO alphabet. If a value is wrong or missing, "negative" comes with the correct value, e.g. "Lufthansa four alpha bravo, negative, squawk two two zero five" – then you only need to read back that value again. ATC doesn't answer acknowledgements without a clearance ("contact Ground one two one decimal eight"). "Say again" repeats the last transmission. A "radio check" (or "how do you read") is answered by the station with "read you five" (`common.radioCheck`), without changing anything in the flow or a pending readback.

**Levels in cruise:** Departure (after the initial call), Center and Oceanic answer level requests: "request climb flight level three four zero", "requesting FL340", "request descent to one two thousand feet", "request higher"/"request lower" (without a level: the next step climb of the OFP, otherwise 2000 ft). The clearance is "climb and maintain FL340" (`center.levelChange`); below the transition altitude in feet with QNH ("descend and maintain 16000 ft, altimeter two niner niner two", `center.levelChangeQnh`). The readback is checked, and the cleared altitude applies at once – including for the FCU altitude check of the input module. "request descent" shortly before the top of descent gets the usual descent to FL110. Optionally ATC turns a request down ("unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes", `center.levelUnable`, rule `levelRequestUnablePercent`, off by default) and clears it on its own after `levelRequestDelayMinutes`. Step climbs from the OFP (planned level of the fixes in cruise) are cleared by ATC on its own 10 nm before the step point: "when ready climb and maintain FL340" (`center.levelWhenReady`, rules `offerStepClimbs`, `stepClimbLeadNm`); before that `get_atc_controller` suggests the request ("request climb FL340"). If you report a level on the initial call that differs from the clearance ("maintaining FL340" with FL320 cleared) and MSFS shows you there, ATC takes it over ("identified, maintain FL340").

**Descent:** At the top of descent Center clears "descend via ANJLL4 arrival" if the OFP has a STAR (`center.descendVia`); the cleared altitude is then the lowest planned altitude of the STAR fixes in the navlog, at least the altitude of the approach clearance. Without a STAR it stays "descend FL110" (`rules.centerDescentFt`). If the clearance ends below the transition altitude of the destination, the QNH comes with it and is part of the readback ("descend via ANJLL4 arrival, altimeter two niner niner two", `center.descendViaQnh`, `center.descendQnh`) – in the test flight to KLAX it was only "descend 11000 ft".

**Squawk and frequency:** MSFS sets the transponder code and COM1 on its own at times – even with its ATC muted (in the test flight squawk 3172 instead of 2340). Duopilo reads `TRANSPONDER CODE` and `COM ACTIVE FREQUENCY:1` continuously. If the code differs from the assigned one in flight for more than 15 s, ATC says "Condor two two zero five, check squawk, should be two three four zero" (`common.checkSquawk`); if COM1 is not on the frequency of the station after the first call (after a handoff: after 60 s grace), "check frequency, should be Montreal Center one three two decimal two five" (`common.checkFrequency`). The hint is repeated every 2 minutes, three times at most; emergency codes, placeholder frequencies and oceanic (HF) aren't checked. **Duopilo never resets anything on its own:** `get_atc_controller` lists the deviation under `deviations` with the `prepare_action` steps (`xpdr.code 2340`, or `com1.standby 132.250` then `com1.swap`), and a caution `ATC_SQUAWK`/`ATC_FREQUENCY` appears in `activeAlerts` and `wait_for_alerts` – Claude offers the reset, the pilot confirms. With `Duopilo:Atc:SquawkFromSim` = `true`, Duopilo takes over a discrete code MSFS sets in flight as the assigned squawk instead (never 2000/1200/7000 or emergency codes). Rules: `checkSquawk`, `checkFrequency`, `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls`.

**Wrong station:** If you call a different station than the responsible one ("Gander, Condor two two zero five, position …" on Montreal), Duopilo doesn't answer as usual: if it's the station that handed you off within the last 15 minutes, it repeats the handoff ("contact Montreal Center one three two decimal six"); otherwise the current one says "Condor two two zero five, this is Montreal Center, say again" (`common.wrongStation`). Only calls of the form "station, callsign, …" count, not readbacks.

**Emergencies (Mayday, Pan Pan):** "Mayday" or "Pan Pan" – also said only once or twice, with any, the wrong or no station – always comes first: before the wrong-station check, "say again", open readbacks, squawk and frequency checks (and in the voice loop before small talk and checklists). The current station answers right away in ICAO phraseology: "Lufthansa four alpha bravo, Langen Radar, roger Mayday, squawk seven seven zero zero, say intentions" (Pan Pan: "roger Pan Pan, say intentions", without 7700). After your next call it asks once "say souls on board and fuel endurance". While the emergency lasts there are no automatic handoffs, clearances, step climbs or squawk/frequency checks, and your requests come first without restriction: "returning to Frankfurt" / "request direct …" / "nearest suitable airport" (the nearest airport of the plan: departure, destination, alternate) → "cleared direct EDDF", "request descent FL120" → "descend and maintain FL120" (never "unable"), "request landing runway 25R" → "runway 25R, wind …, cleared to land" (any runway). **Squawk 7700 without a call:** ATC asks "confirm squawking seven seven zero zero, say nature of emergency"; "affirm …" starts the emergency ("roger Mayday, say intentions"), "negative, wrong code" → "roger, squawk 2340". **End:** "cancel Mayday" / "cancel distress" / "cancel Pan Pan" → "roger, Mayday cancelled, squawk 2340" (the code from before), then the normal flow resumes. 7600 and 7500 are deliberately not handled yet (only no "check squawk"). The emergency is part of the saved session and shows up in `get_atc_controller` (`flight.emergency`).

**Sim time:** ATC only works while the sim is running – not connected, no flight loaded (main menu, world map, loading screen) or paused: ATC says nothing, and its waiting times (deviation timers, takeoff clearance, "expect … in … minutes") stand still. The radio log keeps the real time.

**Data:**
- Callsign, destination, SID/STAR, runways, cruise altitude, FIRs and oceanic part from the SimBrief OFP (SimBrief must be set up). The callsign comes from the flight number with the ICAO airline code (`CFG2205` → "Condor two two zero five") – the `callsign` field may carry the IATA code (`DE2205`) and only counts if it starts with the same ICAO code (e.g. `DLH4AB` for flight number `DLH400`). The spoken callsign comes from the OpenFlights airline database (just over 5,000 airlines), exceptions and corrections from `telephony`. If neither knows the airline, the ICAO code is spelled out.
- **FIR boundaries** from VAT-Spy (VATSIM, `data/vatspy`). Which FIRs the route crosses comes from the OFP; where the boundary lies comes from VAT-Spy: Duopilo intersects the route with both FIR boundaries. Where the VAT-Spy boundaries overlap (e.g. CZQX extends past Wabush), the entry into the new FIR counts once the whole leg still lies within the old one – that puts the Gander → Montreal handoff at about 53.5 N / 64.8 W, as with MSFS ATC, instead of at HOIST. If a third FIR lies between two fixes (at least 30 nm), it is inserted in between. Without FIR information in the OFP, Duopilo assigns the fixes using VAT-Spy (North Atlantic: 30°W Shanwick/Gander). Without VAT-Spy data, the boundary lies halfway between the fixes as before.
- **Sectors** from the VATSIM sector data (VATGlasses, `data/sectors`): per FIR the sectors with altitude band, frequency and callsign name. Along the route (planned altitude from the OFP) Duopilo picks the matching sector and also hands off within a FIR. Sectors the route touches for less than `sectorMinNm` (20 nm) don't get their own handoff – no back and forth along boundaries that run alongside the route. Units without their own FIR such as Maastricht (EDYY) or Rhein (EDUU) take over the upper airspace; a UIR from the OFP such as EDVV (Hannover) thus becomes Maastricht or Bremen Radar. The frequencies are VATSIM's and may differ from MSFS ATC; where there's no sector data (including Minneapolis, Denver, Los Angeles), the frequency from `firs` applies.
- Altitude, position, speed, wind, QNH and time from SimConnect. QNH at the destination from the OFP's METAR, near the airport from the sim. North America: "altimeter" in inHg.
- Airport frequencies from the MSFS facilities API (Clearance, Ground, Tower, Departure, Approach), Center frequencies from the sector data. If they're missing, Duopilo takes the frequency from the template or a placeholder. `get_atc_controller` shows the source (`frequencySource`: `facility`, `sectors`, `templates`, `placeholder`), the stations along the route with sector, frequency and route position (`flight.firs`) and where the data came from (`airspace`).
- Squawk: fixed per flight, no special codes. With `Duopilo:Atc:SquawkFromSim` it follows MSFS (see below).
- Taxi route (`{taxiRoute}`), holding point, gate and pushback direction come through the `ITaxiRouteProvider` interface. Route computation comes in a separate step; until then the templates use a variant without these details ("taxi to holding point runway two five center"). You can set gate or holding point by hand with `atc_set_value` ("we're at A26").

**Voices:** each station (e.g. Frankfurt Delivery, Langen Radar, Shanwick Oceanic) gets a random voice from `voices.pool` per flight – a different one each where possible, never the callout voice. After a Duopilo restart the voices stay the same within the same flight. Fixed voices go under `voices.stations`, e.g. `"EDDF/tower": "bf_emma"`, `"FIR/EGGX": "bm_daniel"` or `"delivery": "af_heart"` for all Delivery stations. For ATC, Duopilo loads Kokoro even when the callouts use the Windows voice; while Kokoro is loading, the English Windows voice speaks. A warning interrupts a transmission in progress. Only the tray instance (HTTP mode) speaks; through the stdio instance of Claude Desktop the reply is text only.

**Saving the session:** Duopilo continuously saves the running ATC session to `%LOCALAPPDATA%\Duopilo\atc-session.json` (station, phase, cleared altitude, oceanic clearance, values set by hand such as the gate, voices, radio history). After a restart or crash it continues in the same flight (same SimBrief plan) as soon as MSFS is connected – without a new initial call. A readback still pending at the time of the crash counts as done; an oceanic clearance that was requested but not read back has to be requested again by the pilot. `atc_control stop` or `restart` deletes the saved session, a new SimBrief plan starts a new session. Settings: `Duopilo:Atc:SessionFile` (empty = off), `Duopilo:Atc:SessionMaxAgeHours` (default 24). The stdio instance of Claude Desktop saves to its own file (`atc-session-stdio.json`). Under `persistence`, `get_atc_controller` shows when the session was last saved and whether it was resumed.

**Editing templates:** all texts are in `atc-templates.json` next to `Duopilo.exe`. Your own changes go into a file `%LOCALAPPDATA%\Duopilo\atc-templates.json` – `update.cmd` doesn't overwrite it. Duopilo lays it over the bundled file: objects are merged key by key (your entries win), lists such as `variants` are replaced entirely by your file. So it only needs the differences, e.g. `{ "telephony": { "XYZ": "Testair" } }`; an entry set to `null` removes a bundled one. An older full copy keeps working: Duopilo upgrades it to the current format (`version` 2) on load – an old entry `CZQX` as Gander Oceanic moves to `CZQO` together with its frequency and fixed voice, while the file itself stays unchanged (note in the log and under `templateNotes`). Afterwards run `atc_control` with `reload` (or restart Duopilo).

```jsonc
"delivery.clearance": {
  "variants": [
    "{callsign}, cleared to {dest}, {sid} departure, runway {runway}, climb {initialAlt}, squawk {squawk}",
    "{callsign}, cleared to {dest}, runway {runway}, climb {initialAlt}, squawk {squawk}"   // without SID
  ],
  "readback": [ "sid", "initialAlt", "squawk" ]
}
```

- Several variants: the first one whose variables are all known wins.
- `"from": "pilot"` entries are the expected pilot transmissions – hints for you and Claude, not mandatory.
- `corrections`: what is said after "negative" for each readback item.
- `telephony`: exceptions only – ICAO code → callsign where OpenFlights doesn't know it or has it wrong ("EJU" → "Alpine", "THY" → "Turkish"). Takes precedence over OpenFlights.
- `airports`: callsign name, altitude of the first climb clearance, deviating station names (`"stations": { "approach": "SoCal Approach" }` for KLAX, likewise NorCal and New York) and fixed frequencies (`"frequencies": { "tower": "119.900" }`, takes precedence over the facilities API).
- `firs`: name of the control unit per FIR (takes precedence over the callsign name from the sector data), `frequency` for FIRs or units without sector data, `"oceanic": true` for oceanic areas. `oceanicFir` for a FIR with a domestic and an oceanic part: Gander is `CZQX` (Gander Center) with `"oceanicFir": "CZQO"` (Gander Oceanic) – the oceanic fixes the OFP lists under CZQX belong to CZQO. `members`: units whose sectors count within the FIR (EDVV: Maastricht, Bremen, Langen). FIRs without their own entry are named as in VAT-Spy plus the station type from `firSuffixes` ("Winnipeg Center", "Praha Radar"); if VAT-Spy doesn't know them either, `OCEANIC` or `CENTER` applies (generic station; without its own `frequency`, the placeholder from `stationTypes`).
- `firSuffixes`: ICAO prefix → station type for FIRs without their own entry (`"K": "Center"`, `"ED": "Radar"`, `"": "Control"`), the longest matching prefix wins.
- `stationTypes`: names of the station types and placeholder frequencies.
- `rules`: thresholds of the state machine (altitudes, distances, times), `sectorMinNm` for the sector hysteresis, step climbs and level requests (`offerStepClimbs`, `stepClimbLeadNm`, `levelRequestUnablePercent`, `levelRequestDelayMinutes`), squawk and COM1 checks (`checkSquawk`, `checkFrequency`, `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls`).
- `words`: what `{levelVerb}` says – climb/descend/maintain in corrections, `climbAndMaintain`/`descendAndMaintain`/`maintain` in level clearances.

**Airspace and airline data** live in `data` next to `Duopilo.exe` (sources, date and licenses in `data/README.md`): VAT-Spy in its original format (CC BY-SA 4.0), sectors as `atc-sectors.json` from `tools/import_sectors.py` (VATGlasses, CC BY-NC-SA 4.0 – non-commercial only) and OpenFlights `airlines.dat` (ODbL). Put a newer version into `%LOCALAPPDATA%\Duopilo\data\…` (same folders); it takes precedence and survives updates; then run `atc_control reload`. Duopilo loads the data once in the background (about a second).

Variables: `{callsign}` `{station}` `{dest}` `{sid}` `{star}` `{approach}` `{runway}` `{initialAlt}` `{squawk}` `{freq}` `{taxiRoute}` `{holdingPoint}` `{gate}` `{level}` `{speedKt}` `{entryFix}` `{heading}` `{wind}` `{qnh}` `{fix}` `{eta}` `{nextFix}`, plus `{direction}` (pushback), `{altitude}` (current altitude), `{nextCenter}` (next unit at handoffs), `{route}` (oceanic route), `{time}` (UTC), `{corrections}`, `{levelVerb}` (climb/descend/maintain, "climb and maintain" in level clearances), `{requested}` (requested level) and `{minutes}` (wait after "unable"), and for emergencies `{emergencySquawk}` (7700), `{emergency}` ("Mayday"/"Pan Pan"), `{previousSquawk}`, `{emergencyTarget}` and `{emergencyRunway}`. Duopilo checks the file on load: missing transmissions, unknown variables, readback items without a correction and any mention of Mach are reported (`get_atc_controller` → `templateErrors`, log).

Settings in `appsettings.Local.json` under `Duopilo:Atc`: `Enabled` (on by default), `TemplatesFile`, `Speak` (speak transmissions), `SquawkFromSim` (take over the squawk MSFS sets, off by default). In the browser, `http://127.0.0.1:5155/atc/controller` shows the state.

## Voice loop

You talk to Duopilo directly – without Claude, without push-to-talk (VR-friendly), fully local and without a language model (only the optional [small talk](#first-officer-and-small-talk) can use one):

```
Microphone → Silero VAD (utterance end) → Whisper (transcript) → Router ─┬→ copilot commands → Input module
                                                                         ├→ ATC module
                                                                         └→ small talk (optional, language model – read-only)
                         Speech output (Kokoro, Supertonic, Piper) ←─────┘   Warnings > Callouts > Replies > ATC > Small talk
```

**Switching it on:** in the dashboard under **Callouts → Voice → Voice loop**, tick the box and choose the microphone – this applies immediately, without restarting Duopilo. It is saved in `%LOCALAPPDATA%\Duopilo\voice-settings.json` (Duopilo creates it from the template on first start); you can also set `"enabled": true` there by hand and restart. Do **not** edit the `voice-settings.json` next to `Duopilo.exe` – that is only the template, and `update.cmd` overwrites it (Duopilo warns in the log if it contains something different). On first start Duopilo downloads the Whisper model (`base-q5_1`, ~60 MB) and Silero VAD (~2 MB) to `%LOCALAPPDATA%\Duopilo\models`. For commands, input must also be unlocked in the dashboard. The voice loop only runs in the tray instance, not in the stdio instance of Claude Desktop.

**Microphone:** empty = Windows default recording device, otherwise part of the device name (`microphone.device`). The available devices are listed in the dashboard, in `duopilo_status` and in the log at startup ("Recording devices: …"). If the chosen device is missing (e.g. the VR headset is still off), Duopilo uses the default device and switches automatically as soon as it appears.

**Status:** `duopilo_status` → `voiceLoop` (whether it's running, microphone, level, utterances recognized/discarded, last recognized utterance, routing, reply), in the dashboard next to the microphone, raw at `http://127.0.0.1:5155/voice`.

**Log:** start, settings, devices, model download, first audio block, every recognized utterance with computing time and target (copilot or ATC), discarded utterances with the reason (sim paused, muted, Whisper too slow, nothing recognized) and a status report every 5 minutes. If no data or only digital silence comes from the microphone (muted, Windows privacy setting "Let desktop apps access your microphone" off), the log shows a warning. Even more detail (every utterance boundary, raw Whisper transcript): in `appsettings.Local.json` at the top level `"Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }`.

**How it works:**
- The microphone is permanently open. Silero VAD detects when you're speaking; after 0.7 s of silence the utterance goes to Whisper.
- Half duplex: while the copilot is speaking (and for 350 ms afterwards), the microphone is muted – Duopilo doesn't hear itself. While you're speaking, callouts and replies wait briefly; only warnings and the time-critical PM callouts (100 knots, V1, Rotate, Positive climb, Spoilers, Reverse, Decel, 70 knots, radio altitude, Minimum, Retard) cut in. If voice input is muted, nothing waits.
- Whisper detects German or English per utterance. Numbers are normalized: "Flaps one", "Flaps 1", "Flaps won", "Klappen eins" all give the same result.
- **Router:** if the utterance contains a station callsign ("Frankfurt Delivery", "Langen Radar", "Shanwick") or your own callsign – at the start of an initial call or at the end of a readback ("…, squawk two two zero five, Lufthansa four alpha bravo") –, it goes to the ATC module. Everything else is a command for the copilot ("Squawk two two zero five" without a callsign sets the transponder). Starting with "Duopilo, …" always goes to Duopilo.
- **Only while the sim is running:** SimConnect not connected, no flight loaded (main menu, world map, loading screen) or paused (with `callouts.silentWhenPaused`, on by default) → the microphone isn't evaluated (no VAD, no Whisper – saves CPU), no ATC, no small talk, no introduction, no callouts and no acknowledgment repeats; anything being spoken stops. Open readbacks, checklists and acknowledgments freeze (their timers stand still) instead of expiring. There's no spoken announcement – only the dashboard shows the reason ("inactive – Sim not connected / No flight loaded / Paused"). The MCP tools for Claude are unchanged.

**Commands** (fixed patterns, German and English, examples) – the voice loop can do everything Claude can do via the Duopilo tools: **every action of every aircraft profile has a voice command**, every switch with every position (a test checks this for all profiles; the complete list with an English and a German example per action is in `src/Duopilo/Voice/VoiceCommandCatalog.cs`):

| Area | Phrases |
|---|---|
| Configuration | Gear up/down, Fahrwerk ein/aus · Flaps 0/1/2/3/Full, Klappen null/eins/voll/einfahren (not "Klappen ein" – too close to "eins") · Speedbrake retract/half/full · Arm/Disarm spoilers, Spoiler armen |
| Brakes, lights, signs | Autobrake low/medium/max/off · Landing lights on/off, Landelichter an · Strobe on/auto/off · Beacon on · Taxi light on · Nav lights on, Nav and logo lights off · Logo lights on · Seat belts on/off, Anschnallzeichen an · No smoking on/auto, Rauchverbot an |
| Electrics, APU | Batteries on/off, Batterien aus · External power on/off, Bodenstrom an · APU start · APU master on/off · APU bleed on/off |
| Anti-ice | Engine anti ice on, Engine one anti ice on, Triebwerk zwei Enteisung aus · Wing anti ice on · Probe heat on/auto, Sondenheizung auto |
| EFIS, FCU | QNH 1013 / QNH one zero one three · Baro standard · Altitude five thousand · Flight level one two zero · Speed two five zero · Heading zero niner zero · Managed speed · Vertical speed minus one thousand · Autopilot one on · Approach on · ND mode ARC/ROSE NAV/PLAN, ND Modus Plan · ND range one hundred sixty, Range forty · Altitude increment one thousand · Expedite climb, Expedite off |
| Transponder, TCAS, radio | Squawk seven thousand / Squawk 1 2 3 4 · Transponder standby/auto · Altitude reporting on/off, Höhenübermittlung aus · TCAS TA/RA · TCAS TA only · TCAS traffic all/above/below/threat · COM one standby one two one decimal five · Swap COM one |

**Execute + callout** (default, `commands.executeWithCallout`): as in a real crew, your command is the instruction – the copilot executes it right away and calls it out once the aircraft has accepted it ("Speed two five zero set", "Altitude five thousand set"). For gear and flaps the callout comes with the position reached ("Gear down, three green", "Flaps two"). Duopilo says "Not confirmed" if the aircraft didn't accept the value, "Unable" for an interlock (blocked actions stay blocked, e.g. batteries off or external power in flight) and "Input disabled" if input is locked. The input module logs every one of these commands like `prepare_action` → `confirm_action`.

A **readback with "Confirm"** only comes when the recognition was uncertain (Whisper confidence below `commands.minConfidence`, default 0.6 – the mean token probability, in the log for every utterance; a confidence Whisper couldn't compute counts as unknown, not as uncertain) and for every action that needs a confirmation according to the interlocks (see Input module, e.g. transponder standby or altitude reporting off in flight). Then the two-step flow applies: Duopilo speaks the readback ("Flaps one"); only on **"Confirm"**, **"Correct"** or **"Affirm"** (also German "Korrekt") is it executed; **"Negative"** or **"Cancel"** discards the action ("Cancelled"). "Yes"/"Ja", "OK" or "Roger" alone confirm nothing – after a readback Duopilo then asks "Say again". Only what you have heard gets confirmed: "Confirm" only counts once the readback has been spoken completely, and only in an utterance you started after that. Without an answer, the action expires silently 60 seconds after the readback; if the readback isn't spoken at all (no voice, pause), immediately. Critical actions ask again with a caution after the first "Confirm" ("Caution, autothrust off. Confirm autothrust off?"); only a second "Confirm" executes. With `"executeWithCallout": false` every command takes this route. Two commands in one utterance or something unknown → "Say again" (longer conversations in the room are silently discarded).

**Queries** – answered only with the plain callout (no extra information), spoken with the same pronunciation as all callouts, speeds always in knots:

| Question | Answer (example) |
|---|---|
| Speed check | Speed two five zero (on the ground: Ground speed one two) |
| Altitude check | Altitude five thousand / Flight level three five zero (with STD) |
| Fuel check | Fuel twelve point four tons |
| Fuel versus plan, Landing fuel, Landemenge | Fuel thirty four point one, two point two below plan, landing fuel six point eight (", below minimum" if so) |
| Next waypoint, Nächster Wegpunkt | Hendy, one hundred twenty miles, E T A zero three four two |
| ETA, Ankunftszeit | E T A zero four one eight |
| Top of descent, Sinkflugbeginn | Top of descent eighty five miles, E T A zero three one two / Top of descent passed |
| Traffic check, Verkehr | Traffic, 2 o'clock, 5 miles, 1000 feet above, descending (like `get_traffic`) / No traffic |
| Wind check, Temperature, Wind and temperature | Wind two seven one at forty five · Temperature minus forty seven |
| Squawk check, Transponder Code | Squawk two three four zero (", standby" if the transponder is in standby) |
| Station check, Which frequency, Aktive Station | Montreal Center, one three two decimal two five (active ATC station; without an ATC session: Active frequency …) |
| Engine check | Engines normal / Engine two not running / N1 split |
| Gear, Flaps, Speedbrake, Spoilers, Seat belts, APU, Baro, Heading check | Gear down · Flaps one · Speedbrake retracted · Spoilers armed · … |

Plan values (fuel against plan, waypoint, ETA, top of descent) are calculated like `get_progress` from the SimBrief plan – without a plan: "Unable".

**Acknowledgment:** important messages expect **"Check"** or **"Copy"** (also "Checked", "Copied"): fuel warnings, safety cautions and warnings (stall, overspeed, gear/flap speed, sink rate, bank angle, master warning/caution, speed deviation, traffic, landing check, autothrust off, approach phase, LOC/GS deviation), squawk and frequency deviations reported by the ATC module ("Check squawk two three four zero") and TCAS RA. Without an answer, Duopilo repeats the message once after about 10 s; if there is still no answer, it escalates – an attention tone and the dashboard alert `ACK_MISSING` (warning) – and stops repeating. A "Check" afterwards or the end of the condition (e.g. sink rate corrected) clears the alert. The waiting time stops while the sim is paused; muting voice input drops pending acknowledgments (you can't answer). Which messages count and the waiting time: `acknowledge` in `voice-settings.json`.

**Checklists** (challenge-response after the Airbus Normal Checklist): start with "Before start checklist" (German "Checkliste vor dem Anlassen"), "After start", "Before takeoff" ("vor dem Start"), "Approach" ("Anflug"), "Landing" ("Landung"), "After landing" ("nach der Landung"), "Parking" ("Parken"). Duopilo reads the challenge ("Before start checklist, cockpit preparation"), waits for your answer and compares it with the sim where it can: signs and seat belts, fuel quantity (said tonnes against fuel on board), baro reference (standard or QNH against the altimeter), beacon, flap setting, autothrust, autobrake, landing memo (gear down, signs, ground spoilers armed, landing flaps), flaps, spoilers and APU after landing, APU bleed, engines off. A deviation is called out ("Beacon off") and the item stays open – answer again after correcting it; if it still deviates, the item is noted as open and the checklist continues ("… checklist complete, open: beacon"). "Standby" holds the item, "Skip" ("Next item", "Überspringen") skips it (open), "Say again" repeats the challenge, "Stop checklist" ("Checkliste abbrechen") interrupts. Without an answer Duopilo repeats the challenge once after 30 s. While a checklist is running, every utterance without the wake word is the answer ("Seat belts on" is not executed) – commands and queries work with "Duopilo, …"; "Check"/"Copy" still acknowledge messages.

**Control:** "Kopilot pause" / "Kopilot resume" (German "Kopilot weiter"; "Duopilo pause" works too) or the hotkey (default `Ctrl+Alt+K`, system-wide) mute and unmute voice input – callouts keep running. "Say again" repeats the last reply.

**Speech output:** Kokoro with the voice of the [first officer](#first-officer-and-small-talk) (a fixed one with `voices.kopilot`; first officer off and empty = dashboard voice); the ATC stations speak with the voices the ATC module assigns. Everything runs through one queue: warnings > callouts > replies > ATC > small talk – ATC transmissions also keep the microphone muted, and anything interrupts small talk. Numbers, NATO alphabet and radio phraseology in English: speed, heading, squawk and QNH digit by digit, altitudes in thousands, 9 as "niner". An interrupted reply or ATC message is repeated once after the warning.

**ATC:** transmissions go directly to the ATC module (`AtcService.TransmitAsync`, like `atc_transmit` – just without Claude). Station names and your own callsign (code and spoken form, e.g. "Lufthansa four alpha bravo") come from the ATC module, based on the SimBrief plan and `atc-templates.json`; the station's reply comes through the queue. "Say again", "Affirm" and "Negative" without a station go to whoever spoke last – ATC or the copilot. If ATC transmits after a readback by the copilot, the pending action expires so that an "Affirm" to ATC doesn't trigger anything in the cockpit. If ATC is off or the SimBrief plan is missing, Duopilo says "ATC unavailable".

**Settings** (`voice-settings.json`, comments allowed; everything only applies with `"enabled": true` – otherwise the callouts behave as without the voice loop):

| Section | What |
|---|---|
| `microphone` | Device (part of the name, empty = default), VAD threshold, minimum length, silence until end of utterance, tail after callouts |
| `recognition` | Whisper model (`tiny`/`base`/`small`, also `-q5_1`/`-q8_0`), `cpu`/`gpu`, threads, language `auto`/`de`/`en`, vocabulary hint |
| `voices` | Kokoro voice for the copilot (`kopilot`), volume for the copilot (`kopilotVolume`) and ATC |
| `hotkeys` | Hotkey for mute/unmute |
| `commands` | Wake word ("Kopilot" is fixed; "Duopilo" also counts when Whisper transcribes it clearly), whether the wake word is required (`requireWakeWord`), timeout for the confirmation, execute + callout (`executeWithCallout`, default on), confidence threshold for the readback (`minConfidence`, 0–1, default 0.6) |
| `acknowledge` | Acknowledgment on/off, waiting time until the repeat and the escalation (`repeatAfterSec`, default 10), rules that expect "check"/"copy" |
| `atc` | Additional callsigns and stations (the flight's own come from the ATC module), callsign from MSFS |
| `callouts` | Active and muted rules, silence while paused, taxi speed warning |

**Resources** (MSFS has priority): the default is Whisper on the CPU with 4 threads – it only computes after you have spoken. `gpu` tries CUDA, then Vulkan, and otherwise falls back to the CPU; alongside MSFS in VR that can cause stutters. It gets faster with a fixed language (`"language": "en"` or `"de"`), `"audioContext": 768` or the model `tiny-q5_1`; `small-q5_1` (~190 MB) understands German better.

Licenses: Whisper.net and whisper.cpp (MIT), Silero VAD (MIT), NAudio (MIT).

### First officer and small talk

**First officer:** with the voice loop on, every flight gets its own first officer – a name picked at random from a list of internationally easy-to-pronounce names, split into female and male (e.g. Anna, Emma, Sophie / Sam, Ben, Daniel), and a Kokoro voice of the same gender (`af_`/`bf_` female, `am_`/`bm_` male) that is never the voice of an ATC station (stations assigned later also avoid it). Name and voice stay the same for the whole flight – also after a restart of Duopilo (`%LOCALAPPDATA%\Duopilo\first-officer.json`); a new SimBrief plan means a new flight and a new first officer. The voice is used for callouts, replies and small talk. If a Windows voice speaks instead of Kokoro, the name matches its gender.

- **Name in the dashboard:** in the header ("F/O Sam"), in the voice panel and under Settings.
- **Introduction at the briefing:** once per flight at the gate (on the ground, at most 5 NM from the departure airport) – "Hi, I'm Sam, I'll be your first officer today" – or when you say **"Briefing"** ("Start briefing", "Let's do the briefing", German "Briefing starten").
- **Addressed by name:** "Sam, gear up", "Hey Sam, how's the weather in Berlin?" work like "Kopilot, …" – "Kopilot" always stays valid (and "Duopilo, …" when Whisper transcribes it clearly). Whisper often spells names differently ("Erik" → "Eric"), so the name is compared tolerantly: spelling variants (k/c, ph/f, th/t, y/i, double letters, a silent h at the end) and, for names of 4 letters or more, one letter different with the same first letter ("Jonah" for Jonas) – but ordinary words never count as the name ("nice" isn't Nico, "pull" isn't Paul, "been" isn't Ben). The name is also put at the front of Whisper's vocabulary hint, so it gets spelled right in the first place.
- **Fixed name:** Settings → **First officer and small talk** (with gender for names not on the list). The first officer can also be switched off (Duopilo then speaks with the dashboard voice as before).
- **Voice:** only good Kokoro voices (grade C+ or better in [VOICES.md](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md)): female `af_heart`, `af_bella`, `bf_emma`, `af_aoede`, `af_kore`, `af_sarah`, male `am_michael`, `am_fenrir`, `am_puck` (`bm_george`/`bm_fable` only as a reserve) – never the voice of an ATC station. Under Settings → **First officer and small talk → Voice** choose *Automatic* (per flight, matching the name) or a fixed voice, with **Listen** to hear it first; this is saved as `voices.kopilot` in `voice-settings.json` and applies immediately. A fixed voice decides the gender; with the same gender, name and introduction stay. `af_nicole` (a whisper/ASMR voice) is used neither by the first officer nor by ATC.

**Small talk (optional, off by default):** a sentence that is neither a command, a query nor a radio call goes to a language model instead of "Say again":

| Provider | What you need |
|---|---|
| **Claude** | your own API key (console.anthropic.com); model empty = `claude-haiku-5-5` |
| **OpenAI-compatible** | base URL (default `https://api.openai.com/v1`, also Groq, LM Studio, vLLM …), model, API key if the server wants one, **Reasoning** |
| **Ollama** | the address of the other computer (e.g. `http://192.168.1.20:11434`) and the model |

There is deliberately no local model inside Duopilo – it would cost too much GPU and RAM next to MSFS. Set it up under Settings → **First officer and small talk** (applies immediately, **Test** asks a short question). The API key is stored only in `appsettings.Local.json` (or the environment variable `Duopilo__Smalltalk__ApiKey`), is never sent back to the browser and never appears in logs or problem reports.

- **Priority:** callouts, warnings, acknowledgments, checklists and radio always come first – small talk is at the very bottom of the queue and anything else interrupts it immediately (it is not repeated). While a readback waits for "Confirm", a checklist runs, a message waits for "Check" or voice input is muted, nothing goes to the model. Sentences without address go to the model only up to `UnaddressedMaxWords` words (default 12); longer ones are usually conversations in the room.
- **Reasoning models** (e.g. `openai/gpt-oss-20b` on Groq, `https://api.groq.com/openai/v1`) think before answering, and those thinking tokens count towards `max_tokens` – with the short limit for a few words nothing was left for the answer ("empty answer"). Setting **Reasoning** (OpenAI-compatible only, `Smalltalk:Reasoning`): *Automatic* (default) sends `reasoning_effort: "low"` for models with "gpt-oss" in the name and nothing for others (some servers reject unknown fields), *Off* never sends it, *Low/Medium/High* always do. With reasoning the token limit gets headroom (+512/+1024/+2048); the length of the answer is still limited by the system prompt and the cut to `MaxReplyWords`. An empty answer at the token limit (`finish_reason: "length"`) gets its own warning in the log and on the **Test** button.
- **Read-only:** the model only gets read-only context (flight phase, position, route, next waypoint, ETA, fuel and fuel against plan, wind and temperature, METARs of the plan, ATC station) and the last few sentences. Its answer is only spoken – never evaluated as a command, never prepared with "Confirm"; a "Confirm" afterwards confirms nothing.
- **Language and voice:** callouts and radio always in English, small talk in the dashboard language – and the first officer speaks both with **the same voice**. The voice decides (Settings → Small talk → Voice): a **Kokoro** voice only speaks English, so small talk is English too (note in the dashboard). **Supertonic 3** (Supertone; runs locally on the CPU in the ONNX Runtime Duopilo already ships, reads characters, no espeak) speaks English and German with every voice: callouts and replies in English, small talk in German. "Automatic" picks a Supertonic voice when small talk is in German – `M1` or `M2` for a male first officer, `F1` for a female one, gender matching the name – and Kokoro otherwise. The voices were chosen by ear and checked with Whisper (as intelligible as Piper's Thorsten, clearly better than the female Piper voices; about 1.3 s for a short sentence on two CPU cores – `Smalltalk:SupertonicThreads`, default 2; measurement in `docs/research/messung-supertonic.md`). Duopilo downloads the model (~400 MB) and the voice as soon as the first officer has a Supertonic voice – until then he speaks with his Kokoro voice (small talk too, in English), afterwards Duopilo pre-renders the fixed callouts – from a fixed revision of `supertone-oss-archive/supertonic-3` on Hugging Face to `%LOCALAPPDATA%\Duopilo\models\supertonic-3`; every file is checked against its SHA-256 (a mismatch is deleted and not retried until the next start). **License:** the model weights are under the BigScience OpenRAIL-M license (a copy is saved next to the model) – free to use, also commercially, but with use restrictions: among others no impersonation of real people, no deception, harassment or discrimination, and machine-generated content you publish (e.g. a stream or video with the first officer's voice) must be disclosed as such. The inference code follows Supertone's MIT-licensed example (`licenses\Supertonic-MIT.txt`). Don't want it? `Smalltalk:Supertonic: false` (Kokoro only, small talk in English). **Piper** is no longer used for small talk: it doesn't speak English, so it would be a different speaker than the callouts. With small talk off, the introduction is in English.
- **Privacy and costs:** the settings page states where the conversation goes. Answer length (`MaxReplyWords`, default 40 words) and requests per minute (`RequestsPerMinute`, default 6) are adjustable. Above the limit, without a network or on any error, Duopilo answers immediately with "I can't chat right now" instead of hanging; every request has a time limit (`TimeoutSec`, default 10 s).

## SimBrief

The airliner FMSs (iniBuilds, FlyByWire) don't expose their route via SimConnect. So Duopilo fetches your latest OFP from SimBrief (public API, no login). Easiest in the dashboard under **Settings → SimBrief** (applies immediately), or in `appsettings.Local.json`:

```jsonc
"Duopilo": { "SimBrief": { "Username": "your-simbrief-name" } }   // or "PilotId": "123456"
```

Duopilo always converts weights and fuel to kg. The plan is also used to add L/C/R to the runway assigned by ATC.

## Requirements

- Windows 10/11 (x64) with MSFS 2024
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), e.g. `winget install Microsoft.DotNet.SDK.10`
- **MSFS 2024 SDK** (for SimConnect), see below – Duopilo offers to install it

### SimConnect SDK

Duopilo needs two files from the MSFS 2024 SDK:

- `SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll`
- `SimConnect SDK\lib\SimConnect.dll` (native library; the traffic query uses it directly)

You get the SDK through the **Developer Mode** of MSFS 2024: enable Developer Mode in the options, then start the SDK installer from the Help menu in the developer toolbar.

**Not in the release packages:** setup and ZIP don't contain the two DLLs – they belong to Microsoft, and whether they may be passed on isn't settled. Duopilo loads them at startup from the installed SDK, in this order:

1. next to `Duopilo.exe` (self-built, or copied in by hand)
2. the SDK folder from the dashboard (**Settings → SimConnect**, `Duopilo:SimConnectSdkPath`)
3. environment variable `MSFS2024_SDK` (process, user or system)
4. `C:\MSFS 2024 SDK\`

If none of them has SimConnect, the dashboard shows a banner and the tray icon a notification: **Install MSFS SDK** downloads the SDK Core installer (version 1.8.1) directly from Microsoft (`sdk.flightsimulator.com`), checks its SHA-256 checksum (fixed for 1.8.1) and starts it – you accept Microsoft's license there, Windows asks for administrator rights. Once SimConnect is found, **Restart Duopilo** is enough. **Choose SDK folder** is for an SDK installed somewhere else. The log shows where SimConnect came from (`SimConnect: loaded from sdk (…)`).

The build looks for the DLLs in this order:

1. `lib\` in the repo (just copy the DLLs in)
2. Environment variable `MSFS2024_SDK`
3. `C:\MSFS 2024 SDK\`

If it finds nothing, it builds against a **stub**. That is only meant for GitHub Actions; a connection to MSFS isn't possible with it. The build log then shows a warning. A proper build shows `Duopilo: SimConnect aus …` (German for "SimConnect from …"). A self-built Duopilo copies the DLLs next to `Duopilo.exe`; with `-p:BundleSimConnect=false` it leaves them out and loads them from the SDK like the release packages.

## Building

```powershell
git clone https://github.com/duopilo/duopiloapp
cd duopiloapp
dotnet publish src/Duopilo -c Release -o C:\Tools\Duopilo
```

**Updating:** double-click `update.cmd` in the repo folder. The script fetches the latest version, builds into a temporary folder, stops all running Duopilo instances (including the one from Claude Desktop, and old Kopilot instances), copies to `C:\Tools\Duopilo` and restarts Duopilo. Coming from Kopilot, it first copies `appsettings.Local.json` and your own profiles from `C:\Tools\Kopilot`. A different target folder can be passed as a parameter: `update.cmd D:\Duopilo`.

After publishing, the folder `C:\Tools\Duopilo` must contain `Duopilo.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` and the `profiles` folder.

## Release and installer

A tag `v…` starts the "Release" workflow (`.github/workflows/release.yml`): self-contained build for win-x64 with the version from the tag, installer (Inno Setup, `installer/`) and ZIP, both attached to a GitHub release with a changelog from the commits since the last tag. The release is created as a **draft** (not public); publish it deliberately with "Publish release" on GitHub or `gh release edit v0.9.1 --draft=false`. Tags with a suffix (`v1.0.0-beta.1`) become pre-releases.

```powershell
git switch main; git pull
git tag v0.9.1
git push origin v0.9.1
```

Without Git there are two ways; the release is created as a draft as well:

- **Manually:** Actions → Release → Run workflow on `main`, enter the version (e.g. `0.10.0-beta.3`) and tick "Release anlegen". After a successful build the workflow sets the tag `v0.10.0-beta.3` on the built commit and creates the release. If the tag already exists, it stops before building.
- **Release PR:** a PR against `main` with the label `release` and the version in the title ("Release v0.10.0-beta.3"). After the merge, `.github/workflows/release-pr.yml` sets the tag on the merge commit and starts the "Release" workflow on the tag. Text above the first `---` line in the PR description goes at the top of the release description, followed by the changelog.

**Once, beforehand:** make SimConnect available to GitHub – on the PC with the MSFS 2024 SDK, run `pwsh installer/simconnect-secret.ps1` in the repo folder (needs the GitHub CLI). This stores the DLLs as secrets `SIMCONNECT_ZIP_*`; without them a release aborts instead of building against the stub. They are only used for compiling – the workflow checks that setup and ZIP contain neither DLL.

**Testing without a release:** Actions → Release → Run workflow (enter a version, leave the box unticked). Setup and ZIP are then available under "Artifacts" for 14 days.

**The installer** installs to `C:\Program Files\Duopilo` (or, without admin rights, for your own user only), creates a Start menu entry, a desktop shortcut (preselected, can be turned off) and optionally autostart (the same entry as "Start with Windows" in the tray menu). On updates `appsettings.Local.json` stays untouched; bundled profiles, `atc-templates.json` and `appsettings.json` are renewed, with your own changes to them backed up to `backup\<date>\` first. Uninstalling removes the program, Start menu entry, desktop shortcut and autostart and asks whether settings and `%LOCALAPPDATA%\Duopilo` (and an old `%LOCALAPPDATA%\Kopilot`) should be removed as well.

**Updates from within Duopilo:** a Duopilo installed with the installer checks the GitHub releases for a newer version at startup (after a minute) and then every 6 hours – published releases only, no drafts; pre-releases (beta, RC) are offered to those running a pre-release themselves (configurable). If there is one, the tray (balloon, menu item "Install update …") and the dashboard (button at the top, "Updates" card in the settings) show it. **Install** downloads the `Setup.exe` to `%LOCALAPPDATA%\Duopilo\updates`, checks it against the release's `SHA256SUMS.txt`, closes Duopilo (the Claude Desktop instance too) and installs silently, starting Duopilo again afterwards (under `C:\Program Files` with an admin prompt). The ZIP version only gets the link to the release page; self-built versions (`update.cmd`) don't check. Settings: `Duopilo:Updates` (`Check`, `Prereleases` = `auto`/`on`/`off`, `IntervalHours`, `Repository`) or in the dashboard.

**Updating from Kopilot** (up to 0.9.x): the AppId stays the same, so Setup recognizes a Kopilot installation. If it is in the old default folder `…\Kopilot`, Duopilo goes next to it into `…\Duopilo`, takes over `appsettings.Local.json` (still writable for normal users), your own profiles and backups, backs up changed bundled files and then removes the old installation (folder and Start menu entry; the autostart becomes "Duopilo"). In a folder of your own, Setup updates in place and only removes the old `Kopilot.*` program files. Website and support link in Setup point to <https://duopilo.app>.

The product name for installer, ZIP and release is set centrally as `<Product>` in `Directory.Build.props`.

## First test in the browser

1. Start MSFS 2024 and load a flight.
2. Start `C:\Tools\Duopilo\Duopilo.exe`. Without parameters it runs in HTTP mode (configurable via `Duopilo:Transport`). No window opens – Duopilo sits in the notification area of the taskbar.
3. Double-clicking the icon opens the **dashboard** at <http://127.0.0.1:5155/>.

## Dashboard and tray icon

The icon in the notification area shows the state with the dot at the bottom right: gray = waiting for MSFS, green = connected, yellow = caution active, red = warning active. Right-click opens the menu: Dashboard, Reload profiles, Log file, Program folder, **Start with Windows** and Exit.

The dashboard runs in the browser, only locally on the sim PC (it is blocked through the tunnel). At the top right are **Report a problem** and **Settings** (both explained below). It shows:

- FMA line (thrust, vertical, lateral, AP/FD/A-THR) in Airbus colors
- Flight data with speed and altitude target, master warning/caution as buttons at the top right
- **Callouts**: active alerts and history. With read-aloud switched on, the browser speaks every callout with an English voice – warnings interrupt everything else. At the moment this is the only real push path.
- ECAM in plain text, route with progress from SimBrief, engines, configuration, ATC runway and weather
- "Setup and diagnostics": all profile bindings with verified/unverified status and current value, log, reload buttons

The "Brightness" slider only dims the displays – for night flights.

### Settings

`http://127.0.0.1:5155/#einstellungen` (button **Settings**) – instead of editing `appsettings.Local.json` by hand:

| Area | Content | Applies |
|---|---|---|
| **Connection to Claude** | local MCP endpoint, public address of the tunnel, consent password (Claude connector), generate or remove the bearer token, check the tunnel | after restart |
| **SimBrief** | username or pilot ID, then loads the plan right away | immediately |
| **Microphone** | recording device of the voice loop, with level | immediately |
| **Modules** | Monitoring, Input (click twice), ATC, Voice loop | Monitoring/ATC after restart, otherwise immediately |
| **First officer and small talk** | fixed name and gender, introduction, small talk provider (Claude, OpenAI-compatible, Ollama), model, address, API key, answer length, requests per minute, test | immediately |
| **Language** | dashboard, tool messages, tray menu, problem reports (see [Language](#language)); logs stay English | immediately |
| **SimConnect** | where SimConnect was loaded from, SDK folder (empty = find automatically), install the MSFS SDK from Microsoft (see [SimConnect SDK](#simconnect-sdk)) | after restart |

The page writes to the existing files: `appsettings.Local.json` next to `Duopilo.exe` (comments and order are preserved; a backup goes to `%LOCALAPPDATA%\Duopilo\appsettings.Local.backup.json` first), and input and voice loop as before to `input.json` and `voice-settings.json`. Anything that is only read at startup is marked "applies after restart" on the page – the **Restart Duopilo** button restarts the tray instance (the ATC session comes back from its saved state). Password and token are never sent back to the browser; a newly generated token is shown exactly once for copying. If an environment variable (`Duopilo__…`) sets the same value, the page points this out – the variable takes precedence over the file.

### Language

Duopilo's own texts are English by default; German is optional. Switch it in the dashboard under **Settings → Language** (applies immediately) or in `appsettings.Local.json`:

```jsonc
"Duopilo": { "Language": "de" }   // default "en"
```

- **Follows the setting:** dashboard, tool messages to Claude (`message`/`note` fields), tray menu and reports from **Report a problem**.
- **Logs are always English** (`duopilo.log`, crash reports, the log in the dashboard) – so they can be attached to issues and read by every contributor.
- **Always stays English**, like a real crew (SOP): callouts, checklists, FMA calls, warnings and radio (ATC templates). Claude itself answers in whatever language you talk to it.
- Values in aircraft profiles and ATC templates are language-neutral (IDs, units); speeds are always in knots.
- The voice loop understands commands in German and English regardless of this setting (`recognition.language` in `voice-settings.json`).
- Translations: `src/Duopilo/i18n/<code>.json`, details in [docs/i18n.md](docs/i18n.md).

With German selected, the dashboard and tray menu show German labels; this README uses the English ones.

### Report a problem

The button immediately captures what Duopilo knows right now – position (with map link), altitude and flight level, station, FIR and frequency according to Duopilo (with their source: MSFS, template or placeholder), COM1 active/standby, transponder vs. Duopilo squawk, time (real and sim), version, flight and the latest transmissions. Then choose the type (FIR boundary/station, frequency, airline/callsign, program bug), briefly describe what's wrong and what would be right, and click **Report on GitHub**: a pre-filled issue opens with the template `.github/ISSUE_TEMPLATE/duopilo-meldung.yml` and the labels `data/fir`, `data/frequency`, `data/airline` or `bug` – you only need to submit it there. Every report is also saved as Markdown in `%LOCALAPPDATA%\Duopilo\reports` (also without a GitHub account or internet: **Save locally only**).

Data corrections are added after review; the data basis remains VAT-Spy/VATSIM sectors and the facilities API, and reports are spot checks. If the link gets too long (many transmissions, long text), Duopilo shortens it – everything is complete in the local copy and on the clipboard (**Copy text**). The link only sets the labels directly for people with triage rights; for everyone else the `issue-labels.yml` action sets them based on an invisible marker in the context. Repository and template are configured under `Duopilo:Reports` (`GitHubRepository` empty = local only).

**Log:** without a console window, Duopilo writes to `%LOCALAPPDATA%\Duopilo\logs\duopilo.log` (the stdio instance of Claude Desktop to `duopilo-stdio.log`). For troubleshooting there's still a console: `Duopilo.exe --console`. Without the tray icon: `--no-tray`.

**Crashes:** unhandled exceptions go to `logs\crash.log` immediately. Hard crashes (e.g. an AccessViolation in SimConnect) can't be caught by .NET – for those the tray instance keeps a small journal (`logs\journal-http.bin`) that survives even a hard crash. On the next start Duopilo reports in the log and in `crash.log` that the last run didn't end cleanly, what was running last (e.g. "SimConnect.ReceiveMessage", the latest traffic queries with `dwSize`) and the exception with stack trace from the Windows Event Viewer. Windows restarts the tray instance after a crash (`RestartAfterCrash`, only after 60 s of uptime so there's no crash loop); the ATC session comes back with it.

Starting Duopilo a second time only opens the dashboard of the running instance.

The raw JSON data is still available (local only):

- <http://127.0.0.1:5155/status> shows the status, it should say `"connected": true`
- <http://127.0.0.1:5155/state> shows the flight state
- `/systems`, `/nav`, `/env`, `/profile`, `/warnings`, `/alerts`, `/monitoring`, `/atc`, `/plan`, `/progress`
- `http://127.0.0.1:5155/read?name=INDICATED ALTITUDE&unit=feet` reads a single variable

### Checklist

A few values I couldn't check without the sim:

- [ ] `altitude.indicatedFt` and `speed.iasKt` match the PFD
- [ ] Weight (`grossWeightKg`) matches the MCDU (FUEL PRED, GW)
- [ ] `pitchUpDeg` is **positive** in the climb, `bankRightDeg` is **positive** in a right turn
- [x] The transponder code is correct, e.g. `2000` (tested 28 Oct)
- [ ] `flapsHandleIndex` follows the flap lever (0 to 4 for 0/1/2/3/FULL)
- [x] On the iniBuilds A321LR the GPS navigation values are empty, the FMS doesn't feed them. Route and distance come from SimBrief (`get_progress`).
- [x] On the iniBuilds A321LR the standard AP values (ALT/HDG/SPD/VS) match the FCU (tested 28 Oct)
- [ ] FBW: check values with `"verified": false` in the profile (see [docs/aircraft.md](docs/aircraft.md))
- [ ] Pause is detected (`paused: true` in `get_flight_state`), including Active Pause
- [ ] ECAM plain text in the A32NX (`get_warnings`), e.g. "AUTO FLT AP OFF" after an AP disconnect

## Connecting Claude Desktop

Claude Desktop starts Duopilo itself in stdio mode. Add this to `%APPDATA%\Claude\claude_desktop_config.json` (in Claude Desktop under Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "duopilo": {
      "command": "C:\\Tools\\Duopilo\\Duopilo.exe",
      "args": ["--stdio"]
    }
  }
}
```

If there's already an `mcpServers` block, only add the `duopilo` entry. Then fully restart Claude Desktop. Duopilo writes its logs to stderr; Claude Desktop stores them in its log folder (`mcp-server-duopilo.log`).

For voice mode, claude.ai in the browser and the phone there's remote operation, see the next section.

## Remote operation / adding the connector in Claude

So that Claude can also use Duopilo **in voice mode, in the browser and on the phone**, the HTTP mode is made reachable through a Cloudflare Tunnel. Duopilo itself stays on the sim PC and still only listens on `127.0.0.1`.

```
Claude (Cloud) ──▶ https://duopilo.example.com/mcp ──▶ Cloudflare ──▶ cloudflared (service) ──▶ 127.0.0.1:5155 ──▶ Duopilo ──▶ MSFS
```

### Access control

`/mcp` is always protected, even locally. Without a valid token Duopilo answers with `401`. There are two ways in:

| Way | For | Configuration |
|---|---|---|
| **OAuth** | Claude connector (claude.ai, Desktop, phone, voice mode) | `PublicUrl` + `ConsentPassword` |
| **Bearer token** | Claude Code, MCP Inspector, your own scripts | `BearerToken` |

With OAuth, Claude registers itself (Dynamic Client Registration). When connecting, a **Duopilo consent page** opens where you enter the consent password. After that Claude renews its access itself: access token 1 hour, refresh token 90 days, rotated on every renewal. After five wrong passwords, consent is locked for 15 minutes.

Dashboard and check pages (`/`, `/state`, …) are only reachable locally. Duopilo only accepts requests to `localhost` and to the host name from `PublicUrl`.

### 1. Create the secrets

```bat
C:\Tools\Duopilo\Duopilo.exe --generate-secret | more
```

This prints a random bearer token. (`| more` makes the command prompt wait for the output – Duopilo is a Windows application without its own console.) You make up the consent password yourself, at least 12 characters.

> Easier: Dashboard → **Settings → Connection to Claude**. Enter the public address and password there, create the token with **Generate new** and restart Duopilo with the button. The steps below do the same by hand.

Then copy `appsettings.Local.example.json` in the folder `C:\Tools\Duopilo` to **`appsettings.Local.json`** and fill it in:

```json
{
  "Duopilo": {
    "Transport": "http",
    "Remote": {
      "PublicUrl": "https://duopilo.example.com",
      "BearerToken": "<output of --generate-secret>",
      "ConsentPassword": "<your consent password>"
    }
  }
}
```

The file is never committed and isn't overwritten by `update.cmd`. Instead of the file you can also use environment variables, which take precedence:

```bat
setx Duopilo__Remote__BearerToken "<token>"
setx Duopilo__Remote__ConsentPassword "<password>"
```

### 2. Start Duopilo in HTTP mode

Start `C:\Tools\Duopilo\Duopilo.exe`. The log (Dashboard → Setup and diagnostics, or `duopilo.log`) must show:

```
Access control: bearer token active.
Access control: OAuth active. Connector URL for Claude: https://duopilo.example.com/mcp
```

If one of the lines is missing, a warning right below it says what's wrong.

> The Duopilo instance that Claude Desktop starts with `--stdio` doesn't serve HTTP. For remote operation Duopilo must also run in HTTP mode. Both instances can be connected to MSFS at the same time.

### 3. Set up the Cloudflare Tunnel

Step by step in **[docs/cloudflared.md](docs/cloudflared.md)**: named tunnel, subdomain `duopilo.example.com`, cloudflared as a Windows service, optionally a WAF rule that opens `/mcp` only to Claude's IP range.

### 4. Add the connector in Claude

1. In Claude, open **Customize → Connectors**, then **Add custom connector**. On Team and Enterprise plans an owner adds it under *Organization settings → Connectors*.
2. Name `Duopilo`, URL **`https://duopilo.example.com/mcp`**.
3. If the dialog asks:
   - Authentication: **Sign in now**
   - OAuth client: **Register automatically**. Duopilo doesn't support "Use Claude's published identity" (CIMD).
4. **Add**, then **Connect**. The Duopilo consent page opens, showing `claude.ai` as the target. Enter the password and click **Approve**.
5. In a new chat, enable Duopilo via **+ → Connectors** and ask: "What's our flight state?"

The connector applies to your whole account, so also to Desktop and phone. Whether it shows up in voice mode hasn't been tested yet.

### Claude Code and other clients (bearer token)

Directly on the sim PC, without the tunnel:

```bat
claude mcp add --transport http duopilo http://127.0.0.1:5155/mcp --header "Authorization: Bearer <Token>"
```

From elsewhere, `https://duopilo.example.com/mcp` works with the same header. That doesn't work, though, if the optional WAF rule is active.

### Revoking access

- **Disconnect all Claude connections:** quit Duopilo, delete `%LOCALAPPDATA%\Duopilo\oauth-state.json` and restart Duopilo. Every connector then has to sign in again.
- **Change the bearer token:** generate a new token, enter it in `appsettings.Local.json` and restart Duopilo.
- **Shut everything down immediately:** stop the tunnel service (`sc stop cloudflared`).

Duopilo stores tokens only as SHA-256 hashes. It never accepts tokens in the URL (`?token=`).

## Aircraft profiles and L: variables

Study-level aircraft keep a lot (AP status, FMA, autobrake, ECAM, fuel flow) not in standard SimVars but in their own **L: variables**. A profile under `profiles\*.json` tells Duopilo where the values come from. Bundled: FlyByWire A32NX, FlyByWire A380X, Headwind A330-900neo, iniBuilds A321LR. What works with which aircraft is described in [docs/aircraft.md](docs/aircraft.md).

```jsonc
{
  "name": "FlyByWire A32NX",
  "match": [
    { "path": [ "FlyByWire", "A320" ] },     // aircraft.cfg path contains all strings (checked before the title)
    { "title": [ "A32NX" ] }                 // or the TITLE contains all strings
  ],
  "bindings": {
    "ap1": { "name": "L:A32NX_AUTOPILOT_1_ACTIVE", "type": "bool", "verified": true },
    "fmaVertical": {
      "name": "L:A32NX_FMA_VERTICAL_MODE", "type": "enum", "verified": false,
      "values": { "14": "V/S", "22": "CLB" }
    },
    "fuelFlowKgh": { "name": "L:A32NX_ENGINE_FF:{i}", "type": "number" }   // {i} = engine 1–4
  },
  "ecam": { "format": "fbw-a32nx", "leftLines": "L:A32NX_EWD_LOWER_LEFT_LINE_{n}", "rightLines": "L:A32NX_EWD_LOWER_RIGHT_LINE_{n}", "lines": 7 },
  "limits": { "vmoKt": 350, "vleKt": 280, "vfeKt": { "1": 230, "2": 200, "3": 185, "4": 177 } },
  "variables": [ { "key": "vapp", "name": "L:A32NX_SPEEDS_VAPP", "unit": "number" } ]
}
```

- **Bindings** are fixed keys that tools and monitoring understand: `ap1`, `ap2`, `flightDirector`, `athrStatus`, `athrMode`, `fmaLateral`, `fmaLateralArmed`, `fmaVertical`, `fmaVerticalArmed`, `fmgcDiscreteWord1`, `fmAltitudeConstraint`, `autobrake`, `masterWarning`, `masterCaution`, `speedTarget`, `speedManaged`, `selectedSpeed`, `machMode`, `selectedAltitude`, `selectedHeading`, `selectedHeadingDashes`, `vls`, `v1`, `vr`, `v2`, `flexTemp`, `tdReached`, `fmsToWptDistance`, `fmsToWptBearing`, `fuelFlowKgh`, `egtC`, `parkingBrake`, `spoilersArmed`, `flaps`. Types: `number`, `bool`, `enum` (value → text), `bitmask` (bit → text), `string`, `arinc429` (FBW data word, only valid with Normal Operation). `scale` converts (e.g. lbs/h → kg/h = 0.45359237). Careful with speeds: some L: variables store m/s internally – in that case set `"unit": "knots"`, don't convert.
- **electrical** lists electrical values for `get_systems` (voltage, frequency, buses powered; `apuGenOn`/`apuGenVolt`/`apuGenHz`/`apuGenLoad` end up under `apu.generator`).
- **fuel** lists fuel pumps and valves for `get_systems` (`fuel.pumps`), same format as `electrical`.
- **limits.landingFlaps** are the flap settings for the landing check; `vfeKt` applies per setting (plain text from `flaps` or lever index).
- **variables** are free extra values that `get_aircraft_profile` outputs raw.
- **limits** is used by monitoring (gear/flap speed, overspeed, 250 kt below FL100) and the input module: `vloKt`, `vfeKt`, `maxGearAltitudeFt` (gear extension only below it), `speedbrakeInhibitFlaps` (e.g. `["FULL"]`), `msaSource` (`"ofp"` = MORA from the SimBrief navlog, the higher value of the two fixes of the current leg). If a value is missing, the affected actions only work with the extra confirmation.
- **verification** (optional): `gear.legs` and `flaps.surfaces` with their own variables, thresholds (`downAt`, `upAt`, `tolerance`, `settleSec`) and time limits (`timeoutSec`) for reading back gear and flaps.

**ECAM plain text:** the A32NX only writes codes (e.g. `220000501`) into the EWD variables. The texts are in the FlyByWire table `EwdMessages.ts` (GPL-3.0), and for the Headwind A330-900neo in its own version (format `hdw-a339x`). Duopilo downloads them from GitHub the first time they're needed, stores them under `%LOCALAPPDATA%\Duopilo\ecam\` and renews them every 14 days. Without internet the cache stays valid. The table is deliberately not part of the repo.

How to find new L: variables:

1. In Developer Mode, open the window with the aircraft's Behaviors / LocalVariables. It lists all L: variables with their current values.
2. Switch something in the cockpit (e.g. NAV on the FCU) and watch which variable changes.
3. Cross-check with `read_simvar` or in the browser: `/read?name=L:NAME`.
4. Add it to the profile (`"verified": true` once checked), then call `reload_profiles`.

Important: SimConnect reads an L: variable that doesn't exist as `0`. So a 0 doesn't prove the name is right. Always cross-check with a state change.

**Input events (MSFS 2024 aircraft):** many newer aircraft (e.g. the iniBuilds A321LR) work their cockpit controls through *input events* (`B:` variables) instead of classic events. `list_input_events` (or in the browser `/input-events?filter=FCU`) lists them all with their current values and saves the complete list to `%LOCALAPPDATA%\Duopilo\aircraft-dumps\<aircraft>.input-events.json`. To find a control: call it once (baseline), operate the switch in the cockpit, then call it again with `changesOnly: true` (browser: `&changes=1`) – the answer shows which input event moved and to which value. If the list stays empty, enable developer mode in MSFS and reload the aircraft. SimConnect can't list L: variables; those still come from the Behaviors window.

## Project structure

```
src/Duopilo/
  Program.cs              Startup: --stdio (Claude Desktop) or HTTP with tray, --console, --generate-secret
  Dashboard/              Dashboard in the browser (HTML, embedded B612 font), data API
  Desktop/                Tray icon, autostart
  Speech/                 Speech output: Windows voice, Kokoro or Supertonic (the first officer's multilingual voice: callouts in English, small talk in the dashboard language), priority queue, output device, one pronunciation for ATC, callouts and fuel check
  Voice/                  Voice loop: microphone, Silero VAD, Whisper, normalization, router, commands, dialog, ATC interface, first officer
  Smalltalk/              Optional small talk with a language model (Claude, OpenAI-compatible, Ollama): read-only context, limits, prompt
  Input/                  Input module: planning actions, interlocks, confirmation, execution via SimConnect
  Hosting/                Transport selection, configuration, HTTP host
  Remote/                 Bearer token, OAuth server, consent page, token store
  Sim/SimConnectClient.cs SimConnect on its own thread, auto-reconnect, value cache
  Sim/SimVarCatalog.cs    Standard SimVars (once per second, monitoring values about 10 times per second)
  Sim/AircraftSignals.cs  Semantic view: profile bindings with fallback to generic SimVars
  Aircraft/               Loading and matching profiles
  Ecam/                   ECAM codes → plain text (FBW table, loaded at runtime)
  FlightPlan/             SimBrief OFP, route and progress calculation
  Atc/                    In-game ATC, runway number/designator, phraseology
  Atc/Controller/         Duopilo ATC: state machine, templates (with migration), spoken forms, readback check, stations and voices
  Atc/Airspace/           FIR boundaries (VAT-Spy), sectors, airline callsigns (OpenFlights), point-in-polygon
  atc-templates.json      Text templates of the Duopilo ATC
  data/                   Airspace and airline data with licenses (data/README.md)
  Monitoring/             Rules (MonitorEngine), alert bus, background service
  Sim/TrafficConnection.cs Separate SimConnect connection for traffic (native SimConnect.dll, size check per message)
  Traffic/                Traffic query, bearing/clock/callout, background scan for TRAFFIC_PROXIMITY
  Tools/                  MCP tools
  Updates/                Update check (GitHub releases), download with SHA-256 check, silent install with restart
  Localization/           Language setting, text lookup (Loc)
  i18n/                   Texts per language (en.json, de.json)
  profiles/               Aircraft profiles (JSON)
src/SimConnectStub/       Placeholder API for CI builds without the MSFS SDK
tools/import_sectors.py   Generates data/sectors/atc-sectors.json from the VATGlasses sector data
tests/Duopilo.Tests/      Tests (OAuth, access control, monitoring rules, ECAM, SimBrief, profiles, traffic, ATC, voice loop) – run without MSFS
docs/cloudflared.md       Cloudflare Tunnel guide
docs/i18n.md              Translations and language files
installer/                Windows installer (Inno Setup) and helper scripts for the release workflow
```

Every variable has its own SimConnect data definition. If a name is unknown in MSFS, only that one value is missing; everything else keeps running.

## Roadmap

- [x] v0.1: MCP server with flight state, systems, navigation, weather, L: variable profiles
- [x] v0.3: Pilot Monitoring (rules, callouts, alerts), FBW profiles, ECAM plain text, SimBrief, in-game ATC runway
- [ ] L: variables of the iniBuilds A321LR (FMA, A/THR, master warning, ECAM)
- [x] v0.4: Tray icon and dashboard in the browser instead of a console window, callouts via speech output in the browser
- [x] v0.5: Fixes after the EDDL–EDDM test flight (A32NX bindings, knots instead of m/s, ALT CRZ), TOD, landing check, FMA log, FMS waypoint
- [x] v0.6: Local speech output (Windows, selectable voice and output device) and PM callouts (takeoff roll, landing, altitude marks, configuration)
- [x] v0.7: Input module with readback and confirmation (FCU, EFIS, configuration, lights, signs, anti-ice, packs, APU, transponder, radio) – A32NX first
- [x] v0.8: Kokoro as a second voice (neural, no Windows language pack needed), ECAM titles spelled out, tray toggle
- [x] Traffic: `get_traffic` (AI and multiplayer) and traffic warning `TRAFFIC_PROXIMITY` – still to be checked in the sim
- [x] ATC module: state machine with text templates (Delivery to Ground, Oceanic), readback check, voice per station
- [x] ATC: FIR boundaries from VAT-Spy, sectors with frequencies (VATSIM), Gander Domestic, callsigns from OpenFlights, template migration
- [ ] ATC: taxi route calculation from the facilities API's taxiway graph (`ITaxiRouteProvider`), gate from the parking position
- [x] v0.9: Local voice loop (Silero VAD, Whisper, fixed command patterns, readback and confirmation by voice), priority queue, taxi speed warning, radio to the ATC module
- [x] v0.9.1: Switch the voice loop on and off in the dashboard, microphone selection, status in `duopilo_status`, detailed log; ATC session survives restarts; crash journal and restart after a crash
- [ ] Voice loop: test flight in VR, actions for the iniBuilds A321LR
- [ ] Input module: check in the sim, NAV & LOGO via input events, more aircraft
- [ ] WASimCommander integration (list L: variables, events)
- [x] Remote operation: Streamable HTTP with bearer token and OAuth, Cloudflare Tunnel
- [ ] Test voice mode with the remote connector

## License

Duopilo is licensed under the [MIT License](LICENSE). Bundled data (VAT-Spy, VATSIM sectors – non-commercial only, OpenFlights), libraries, fonts and the models Duopilo downloads keep their own licenses – see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The SimConnect DLLs belong to the Microsoft Flight Simulator SDK and are not covered by the MIT License.
