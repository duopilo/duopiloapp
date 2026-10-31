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

Ideas and requests: [issues](https://github.com/duopilo/duopiloapp/issues).
