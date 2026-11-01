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
- [ ] ATC: taxi route calculation from the facilities API's taxiway graph, gate from the parking position
- [ ] ATC: handle squawk 7600/7500
- [ ] WASimCommander integration (list `L:` variables, events)
- [ ] Test voice mode with the remote connector
- [ ] First public release

## Own traffic

Duopilo's own AI traffic instead of the simulator's, built step by step with the traffic test tool and tested at EDDL ([research](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md), [Traffic](Traffic#traffic-test-developer)):

- [x] **Approach and landing** (v0.10 betas) – AI traffic models fly the final approach by waypoints and stop on the runway: gear down by 2500 ft, lights, approval list of the MSFS 2024 AI traffic models (about 250 types) with an approach speed per type, speed control, up to 4 at once
- [ ] **Go-around** (beta.15) – more than 150 ft high or Vapp + 20 kt fast over the threshold, or not on the ground by 900 m: climb straight ahead to 3000 ft, gear up
- [ ] **Takeoff** (beta.16) – spawn on the runway, takeoff roll, rotate, climb to 3000 ft, gear up, lights; evaluation of the liftoff point and the climb
- [ ] **Overflights at cruise altitude** – aircraft crossing your area at their flight level
- [ ] **Taxi** – pushback and taxi between gate and runway on the airport's taxiways
- [ ] **Schedule traffic** – flights mapped to the installed models by ICAO type
- [ ] Test at Frankfurt as well; softer touchdowns (the AI touches down at about 1400 ft/min)

Ideas and requests: [issues](https://github.com/duopilo/duopiloapp/issues).
