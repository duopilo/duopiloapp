**English** | [Deutsch](DE-Startseite)

# Duopilo Wiki

**Duopilo** turns Microsoft Flight Simulator 2024 into a two-person cockpit. It reads the aircraft via SimConnect and acts as your **Pilot Monitoring** – you stay **Pilot Flying**.

It does this in two ways, which you can use together or separately:

- **With Claude** – Duopilo is an MCP server. Claude reads altitude, speed, configuration, navigation, weather and ATC itself instead of you reading them out.
- **On its own** – Duopilo calls out, monitors, flies the radio and listens to you through a fully local voice loop. No Claude, no push-to-talk, VR-friendly.

> Website: <https://duopilo.app> · Repository: <https://github.com/duopilo/duopiloapp> · Formerly **Kopilot** (up to 0.9.x)

## What it does

| Area | In short |
|---|---|
| [Pilot Monitoring](Pilot-Monitoring) | Checks the flight 5× per second and calls out like a real PM: gear/flap speed, sink rate, "One hundred knots", "Positive climb", "Ten thousand", fuel checks … |
| [Speech Output](Speech-Output) | Speaks every callout itself – Windows voice, Kokoro or Supertonic, warnings first |
| [Voice Loop](Voice-Loop) | Talk to your first officer: commands, queries, checklists, radio – local, without a language model |
| [First Officer & Small Talk](First-Officer-and-Small-Talk) | A first officer with name and voice per flight; optional small talk via Claude, an OpenAI-compatible server or Ollama |
| [Input Module](Input-Module) | Sets FCU, EFIS, lights, gear, flaps … on your call – with readback, confirmation and hard interlocks. Locked by default |
| [ATC](ATC) | Own ATC from Delivery to Ground incl. Oceanic, readback check, a voice per station – template-based, no language model |
| [Traffic](Traffic) | AI, live and multiplayer traffic with clock position and a proximity warning |
| [SimBrief & Flight Plan](SimBrief-and-Flight-Plan) | Route, next fix, ETA, top of descent and fuel against the plan |
| [Aircraft Profiles](Aircraft-Profiles) | L: variables for study-level aircraft (FBW A32NX/A380X, Headwind A330-900neo, iniBuilds A321LR) |

## Start here

1. [Installation](Installation) – installer or build from source
2. [Quick Start](Quick-Start) – your first flight with Duopilo in ten minutes
3. [Connecting Claude](Connecting-Claude) – Claude Desktop, claude.ai, phone, Claude Code
4. [Dashboard & Settings](Dashboard-and-Settings) – everything you can set without editing files

Something wrong? → [Troubleshooting](Troubleshooting) · [FAQ](FAQ)

## Principles

- **The pilot decides.** Duopilo reads everything, but operates switches only on your call, after a readback, and never anything that can damage the aircraft or is irreversible (engines, fuel, fire, hydraulics …). See [Input Module](Input-Module).
- **Crew language is English.** Callouts, checklists, FMA calls, warnings and radio are always English, like in a real cockpit. The dashboard and tool messages can be German. Logs are always English.
- **No guessing.** If an aircraft doesn't expose a value, the related callout stays off instead of being wrong.
- **MSFS comes first.** Speech recognition and voices run on the CPU with limited threads; there is deliberately no local language model.

## Status

Version **0.9.1** · Windows 10/11 x64 · MSFS 2024 · .NET 10. Releases are currently created as drafts only (see [Installation](Installation)). What's next: [Roadmap](Roadmap).

## For developers

[Development](Development) – building, tests, project structure, translations, releases and how to contribute.
