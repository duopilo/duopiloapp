**English** | [Deutsch](DE-Roadmap)

# Roadmap

## Done

- **v0.1** – MCP server with flight state, systems, navigation, weather, `L:` variable profiles
- **v0.3** – Pilot Monitoring (rules, callouts, alerts), FBW profiles, ECAM plain text, SimBrief, in-game ATC runway
- **v0.4** – tray icon and browser dashboard, callouts read aloud in the browser
- **v0.5** – fixes after the EDDL–EDDM test flight, TOD, landing check, FMA log, FMS waypoint
- **v0.6** – local speech output (Windows voice, output device) and PM callouts
- **v0.7** – input module with readback and confirmation (A32NX first)
- **v0.8** – Kokoro neural voice, ECAM titles spelled out, tray toggle
- **Traffic** – `get_traffic` and the proximity warning
- **ATC module** – state machine with templates from Delivery to Ground incl. Oceanic, readback check, voice per station; FIR boundaries (VAT-Spy), VATSIM sectors, OpenFlights callsigns
- **Remote operation** – Streamable HTTP with bearer token and OAuth, Cloudflare Tunnel
- **v0.9** – local voice loop (Silero VAD, Whisper, fixed commands, readback by voice), priority queue, taxi speed warning, radio to the ATC module
- **v0.9.1** – voice loop switch and microphone selection in the dashboard, ATC session survives restarts, crash journal and restart after a crash
- **Since** – rename to Duopilo, English as default language with German translation, checklists, acknowledgments, emergencies in ATC, first officer with small talk (Claude, OpenAI-compatible, Ollama), Supertonic voice, installer and release workflow

## Next

- [ ] Test flight in VR with the voice loop
- [ ] `L:` variables and actions for the iniBuilds A321LR (FMA, A/THR, master warning, ECAM)
- [ ] Input module: verify in the sim, NAV & LOGO via input events, more aircraft
- [ ] ATC: taxi route from the facilities API's taxiway graph, gate from the parking position – graph and route search exist since the traffic taxi test (beta.22), the ATC wiring is still open
- [ ] ATC: handle squawk 7600/7500
- [ ] WASimCommander integration (list `L:` variables, events)
- [ ] Test voice mode with the remote connector
- [ ] First public release

## Own traffic

Duopilo's own AI traffic instead of the simulator's, built step by step with the traffic test tool and tested at EDDL and EDDF ([research](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md), [Traffic](Traffic#traffic-test-developer)):

- [x] **Approach and landing** (v0.10 betas) – AI traffic models fly the final approach by waypoints and stop on the runway: gear down by 2500 ft, lights, approval list of the MSFS 2024 AI traffic models (about 250 types) with an approach speed per type, speed control, up to 4 at once
- [x] **Go-around** (beta.15, in beta) – more than 150 ft high or Vapp + 20 kt fast over the threshold, or not on the ground by 900 m: climb straight ahead to 3000 ft, gear up; the AI takes the new waypoints in flight
- [x] **Takeoff** (beta.16, in beta) – spawn on the runway, takeoff roll, liftoff, climb to 3000 ft, gear up, lights; evaluation of the liftoff point and the climb
- [x] **Kinematic final** (beta.17, in beta) – the waypoint AI flies no slower than about 169 kt and lands at a fixed 133 kt, so Duopilo moves the aircraft itself from a few miles out: speed per type, 3° path, flare, soft touchdown, braking to a stop
- [x] **Overflights at cruise altitude** (beta.18, in beta) – aircraft crossing your area at their flight level, flown by the waypoint AI; away from airports, over any point or your own aircraft, since beta.36
- [x] **Airport rules** (beta.19, in beta) – which aircraft may use which airport and runway, for all airports: military airfields only military traffic (A400M, Super Hornet), heliports and closed fields none, small fields general aviation only, runway rules (EDDF 07L/25R landings only without B747, A380, MD-11), traffic mix per airport (Finkenwerder mostly Belugas)
- [x] **Rotation on takeoff** (beta.21, in beta) – Duopilo flies the takeoff itself (kinematic, like the final): takeoff roll, rotation at Vr with 3°/s, liftoff at 8°, climb at about 15°; tested with A400M, MD-11 and A320
- [x] **Taxi to the runway** (beta.22 to beta.28, in beta) – Duopilo moves the aircraft over the airport's taxiways (MSFS taxiway network): gate chosen by size and type, pushback along the route to the next junction, taxi at 15 kt and slower in turns, around parked aircraft (beta.23), stop at the holding line of the taxiway it enters the runway from (beta.28), hold short of crossed runways until they are free (beta.30)
- [x] **Departure from the gate** (beta.24, in beta) – pushback, taxi, line-up on the centerline and kinematic takeoff in one run; tested at EDDL 23R from gate A15
- [x] **Taxi after landing** (beta.29, in beta) – landing, leave the runway at a suitable exit while still rolling, taxi to a free gate; tested at EDDL 23R to gate C2
- [x] **Departure queue** (beta.31, in beta) – several aircraft from different gates to one runway: spacing on the taxiways, line-up in the order of arrival, one departure at a time; tested at EDDL 23R with three departures (beta.33)
- [x] **Landing sequence and mixed mode** (beta.34, in beta) – landings one after another with wake spacing, gaps for departures, departures and landings on one runway (like Düsseldorf), go-around when the runway is still occupied
- [ ] **Follow-me** – own vehicle model (van with full checker livery), leads your aircraft from the exit to the gate
- [ ] **Airport fire service** – European crash tenders on emergencies from the ATC module
- [ ] **Own passive aircraft models**
- [ ] **Helicopters** – kinematic: approach, hover, vertical landing and takeoff on heliports and airports
- [ ] **Schedule traffic** – flights mapped to the installed models by ICAO type
- [x] Test at Frankfurt as well (three parallel runways and the Startbahn West)

Ideas and requests: [issues](https://github.com/duopilo/duopiloapp/issues).
