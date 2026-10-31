# SimBrief & Flight Plan

Airliner FMSs (FlyByWire, iniBuilds) don't expose their route via SimConnect. So Duopilo fetches your **latest SimBrief OFP** (public API, no login).

## Setup

Dashboard → **Settings → SimBrief** – applies immediately. Or in `appsettings.Local.json`:

```jsonc
"Duopilo": { "SimBrief": { "Username": "your-simbrief-name" } }   // or "PilotId": "123456"
```

The plan is cached for 10 minutes (`SimBrief:CacheMinutes`). Weights and fuel are always converted to kg.

## What uses the plan

| Feature | Uses |
|---|---|
| `get_flight_plan` | route, runways, cruise altitude, cost index, fuel, weights, times, navlog |
| `get_progress`, voice queries | next fix, distance/ETA, TOD, cross-track, fuel against plan |
| [Fuel check](Pilot-Monitoring#fuel-check) | planned fuel per waypoint, minimum landing fuel |
| `TOP_OF_DESCENT` | TOD from the plan (A32NX: the FMS message T/D REACHED) |
| `PM_TRANSITION` | TA/TL if the FMS doesn't provide them |
| [ATC](ATC) | callsign, SID/STAR, runways, FIRs, oceanic part, step climbs, QNH from the METAR |
| Pronunciation | navaid names from the navlog ("Wabush" instead of YWK) |
| In-game ATC runway | adds L/C/R to the runway MSFS assigns |
| First officer | a new plan = a new flight = a new first officer |

## Progress

- **Next fix:** on the A32NX the active FMS waypoint (e.g. after DIR TO), matched against the plan; otherwise from position along the route.
- **ETA:** from the navlog times (planned winds), corrected by the deviation at the last fix passed. Without navlog times: from ground speed. `etaSource` says which.
- **TOD:** distance and ETA from the plan.
- **Fuel:** on board against plan, history of automatic checks (`fuelCheck`).

The dashboard shows the route with a progress bar and the fuel deviation trend below it.

## Without a plan

Monitoring, PM callouts, traffic, input and the voice loop work without a plan. Plan-based voice queries answer "Unable", and ATC says "ATC unavailable".
