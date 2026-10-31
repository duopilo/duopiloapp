**English** | [Deutsch](DE-Pilot-Monitoring)

# Pilot Monitoring

Every 200 ms Duopilo checks a set of rules and raises alerts with a short English callout. Alerts are spoken ([Speech Output](Speech-Output)), shown in the dashboard and delivered to Claude (`activeAlerts`, `get_alerts`, `wait_for_alerts`).

After loading a flight, changing aircraft or an air start, monitoring stays silent for 10 s; after a pause for 3 s – that's when values jump. While the sim is paused, all rules rest (`SIM_PAUSED`).

## Safety and deviation rules

| Rule | Callout | Condition |
|---|---|---|
| `GEAR_SPEED` | Gear speed | gear not retracted and IAS above VLE |
| `FLAP_SPEED` | Flap speed | IAS above VFE for the flap setting |
| `OVERSPEED` | Overspeed | IAS above VMO or the sim's overspeed warning |
| `STALL` | Stall | the sim's stall warning in the air |
| `SINK_RATE` | Sink rate | below 2500 ft RA, sinking more than 1500–3000 ft/min |
| `BANK_ANGLE` | Bank angle | more than 35° (tighter below 150 ft RA, 45° above 2500 ft RA) |
| `MASTER_WARNING` / `MASTER_CAUTION` | Master caution, ELEC GEN 1 OFF | master light on, with the latest ECAM message |
| `ECAM_MESSAGE` | HYD G RSVR LO LVL | another ECAM message while master warning/caution is lit |
| `GEAR_DOWN_HIGH` | Gear still down | gear down in the climb above 2000 ft AGL or above FL150 |
| `SPEED_LIMIT_FL100` | Speed limit | faster than 250 kt below 10,000 ft |
| `SPEED_DEVIATION` | Speed | IAS off target (+15/−10 kt, on approach +10/−5 kt) and not converging |
| `AP_DISCONNECT` | Autopilot off | autopilot disconnected in the air |
| `ALT_1000_TO_GO` | One thousand to go | 1000 ft before the FCU altitude |
| `TOP_OF_DESCENT` | Top of descent | A32NX: FMS message T/D REACHED; otherwise TOD from SimBrief |
| `LANDING_CHECK` | Landing checklist complete / Spoilers not armed | 2000 ft RA in the descent: gear, landing flaps, ground spoilers; again at 1000 ft RA if something is missing |
| `RUNWAY_ASSIGNED` | Runway two three left assigned | MSFS in-game ATC assigned a runway |
| `TRAFFIC_PROXIMITY` | Traffic, 2 o'clock, 2 miles, 500 feet above, climbing | airborne target within 3 NM and ±1000 ft, own aircraft above 500 ft AGL; then 60 s quiet – see [Traffic](Traffic) |

## Autoflight rules

| Rule | Callout | Condition |
|---|---|---|
| `TCAS_RA` | TCAS RA / Clear of conflict | vertical FMA shows TCAS for 0.3 s; "Clear of conflict" 1.5 s after it ends |
| `ATHR_OFF` | Autothrust off | A/THR from armed/active to off in the air (not the disconnect below 100 ft RA at touchdown) |
| `FMA_CHANGE` | Loc star / Glideslope star / Alt blue | new lateral/vertical or newly armed mode in the air, after 1 s |
| `LOC_DEVIATION` | Loc | LOC or LAND engaged, 50–2000 ft RA, more than ¼ dot for 1 s |
| `GS_DEVIATION` | Glideslope | G/S or LAND engaged, 100–2000 ft RA, more than 1 dot for 1 s |
| `APPR_NOT_ACTIVE` | Approach phase not activated | from 15 NM before destination with managed speed, FMGC not in approach phase – once per approach |

These depend on profile bindings (FMA, A/THR, FMGC phase, LOC/GS deviation). **Duopilo doesn't guess:** without a binding the callout stays off for that aircraft. `get_aircraft_profile` → `callouts` shows per rule `verified`, `unverified` or `missing`; the overview per aircraft is in [docs/aircraft.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.md#autoflight-callouts-v092).

## Pilot Monitoring callouts

| Rule | Callout | When |
|---|---|---|
| `PM_100_KNOTS`, `PM_V1`, `PM_ROTATE` | One hundred knots / V1 / Rotate | takeoff roll (V1/VR from the MCDU on the A32NX) |
| `PM_POSITIVE_CLIMB` | Positive climb | after liftoff, once really climbing |
| `PM_SPOILERS` | Spoilers / No spoilers | after touchdown ("No spoilers" after 3 s without ground spoilers) |
| `PM_REVERSE` | Reverse green | thrust reversers deployed |
| `PM_DECEL` | Decel | autobrake DECEL (A32NX), otherwise noticeable deceleration |
| `PM_70_KNOTS` | Seventy knots | landing roll below 70 kt |
| `PM_TEN_THOUSAND` | Ten thousand | through 10,000 ft, climbing and descending |
| `PM_TRANSITION` | Transition altitude / Transition level | TA in the climb, TL in the descent (FMS, otherwise SimBrief) |
| `PM_FLAPS` | Speed checked, flaps two / Flaps zero | flap lever in a new position for 1.5 s; when extending with a speed check against VFE |
| `PM_GEAR` | Gear down / Gear up | gear lever in the air |
| `TAXI_SPEED` | Taxi speed | on the ground above 25 kt GS for more than 2 s, at most every 30 s (adjustable in `voice-settings.json`) |
| `FUEL_CHECK` | HENDY, fuel 34.1, 2.2 below plan | cruise, see [Fuel check](#fuel-check) |
| `FUEL_WARNING` | HENDY, fuel 31.2, 2.8 below plan, deviation increased by 0.6 | deviation 300 kg worse since the last check, or landing fuel below minimum + buffer |

Radio altitudes (2500, 1000, 500 …), "Hundred above", "Minimum" and "Retard" are called out by the A32NX itself. For aircraft without their own callouts Duopilo can add them: **Callouts → Voice** or `Duopilo:Monitoring:AircraftAutoCallouts`.

## Fuel check

With a SimBrief plan, Duopilo compares fuel on board with the plan during cruise – like a crew's fuel check:

- **At every OFP waypoint** in cruise (excluding TOC/TOD): "Hendy, fuel 34.1, 2.2 below plan" (tonnes, one decimal; below 50 kg deviation "on plan").
- **Interval:** no waypoint for 30 min (sim time) → check without a location.
- **Warning `FUEL_WARNING`** (checked continuously):
  - the deviation grew by more than `DeviationIncreaseKg` since the last check (suspected leak);
  - predicted landing fuel (planned + today's deviation) drops below minimum + buffer, or below the minimum. The buffer is at most half of the planned margin, so tightly planned flights don't warn at the first check.
- **Refueling** is recognized and reported silently ("Refuelling detected: 3,530 → 11,090 kg"); the comparison restarts.

The dashboard shows the deviation trend below the route; `get_progress` returns it as `fuelCheck`.

```jsonc
"Duopilo": { "Monitoring": { "FuelCheck": {
  "IntervalMinutes": 30,       // 0 = no interval check
  "DeviationIncreaseKg": 300,  // 0 = no warning when the deviation grows
  "LandingBufferKg": 500,
  "MinSpacingMinutes": 0       // e.g. 10 = don't call out every waypoint over Europe
} } }
```

Callouts off, warnings kept: put `FUEL_CHECK` into `DisabledRules`.

## Turning rules off

Disable completely (no alert, not spoken) in `appsettings.Local.json`:

```jsonc
"Duopilo": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

Keep visible but don't speak: `"mutedRules": [ "PM_GEAR" ]` in `%LOCALAPPDATA%\Duopilo\speech.json`.

With the [voice loop](Voice-Loop) on, important alerts also expect an acknowledgment ("Check" / "Copy").
