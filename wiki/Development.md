# Development

Duopilo is C# on **.NET 10** (`net10.0-windows`), an ASP.NET Core host with the official MCP C# SDK, a tray app and a browser dashboard. Tests run without MSFS.

## Build and test

```powershell
git clone https://github.com/duopilo/duopiloapp
cd duopiloapp
dotnet build Duopilo.slnx -c Release
dotnet test Duopilo.slnx -c Release
```

Without the MSFS SDK (CI, Linux tooling, quick checks) build against the stub:

```powershell
dotnet build Duopilo.slnx -c Release -p:UseSimConnectStub=true
dotnet test  Duopilo.slnx -c Release --no-build -p:UseSimConnectStub=true
```

For a real build see [Installation → SimConnect SDK](Installation#1-get-the-simconnect-sdk). Local deploy to `C:\Tools\Duopilo`: `update.cmd`.

## Project structure

```
src/Duopilo/
  Program.cs              startup: --stdio (Claude Desktop) or HTTP with tray, --console, --generate-secret
  Dashboard/              browser dashboard (HTML, embedded B612 font), data API
  Desktop/                tray icon, autostart
  Speech/                 speech output: Windows, Kokoro, Supertonic; priority queue; one pronunciation
  Voice/                  voice loop: microphone, Silero VAD, Whisper, normalization, router, commands, dialog, checklists, first officer
  Smalltalk/              optional small talk (Claude, OpenAI-compatible, Ollama): read-only context, limits, prompt
  Input/                  input module: planning, interlocks, confirmation, execution via SimConnect
  Hosting/                transport selection, configuration, HTTP host, crash journal
  Remote/                 bearer token, OAuth server, consent page, token store
  Sim/                    SimConnect client (own thread, auto-reconnect, cache), SimVar catalog, aircraft signals, traffic connection
  Aircraft/               loading and matching profiles
  Ecam/                   ECAM codes → plain text
  FlightPlan/             SimBrief OFP, route, progress, ETA, fuel samples
  Monitoring/             rules (MonitorEngine), alert bus, FMA log, fuel check
  Traffic/                traffic query, clock/callout, proximity scan
  Atc/                    in-game ATC reader, runway designator
  Atc/Controller/         Duopilo ATC: state machine, templates, renderer, readback check, stations, voices, persistence
  Atc/Airspace/           FIR boundaries (VAT-Spy), sectors, airlines (OpenFlights)
  Tools/                  MCP tools
  Localization/, i18n/    language setting, Loc, en.json/de.json
  profiles/               aircraft profiles
  atc-templates.json      ATC texts
  data/                   airspace and airline data with licenses
src/SimConnectStub/       placeholder API for builds without the MSFS SDK
tests/Duopilo.Tests/      xUnit tests – run without MSFS
tools/import_sectors.py   generates data/sectors/atc-sectors.json
installer/                Inno Setup script and release helpers
docs/                     aircraft notes, tunnel guide, i18n, test logs, research
```

## Contributing

- **Branches and pull requests:** don't push to `main`. Work on a feature branch (`feature/…`, `fix/…`, `docs/…`) and open a pull request against `main`. The **Build** workflow builds and tests every push and PR against the stub.
- **Tests:** add tests for new behaviour; they must run without MSFS. Several tests are based on situations from real test flights.
- **Safety first:** the input module's block list and interlocks are deliberate. Changes that would let Duopilo operate engines, fuel, fire, hydraulics or sim control won't be accepted.
- **Crew language:** callouts, checklists, warnings and radio stay English; speeds in knots, never Mach (template validation enforces this).
- **Logs are English** – use `log.` keys with `Loc.Log`.
- **Aircraft profiles:** only mark values you checked in the sim as `"verified": true`.
- **Data corrections** (FIRs, frequencies, callsigns): best via **Report a problem** in the dashboard, with a source.
- **Docs:** README and docs are English (German versions optional). Test logs (`docs/testlog-*.md`) are pure flight logs; research goes into `docs/research/`.

Bugs and data errors: [issues](https://github.com/duopilo/duopiloapp/issues) – the template is pre-filled by the dashboard.

## Translations

Language files are flat JSON in `src/Duopilo/i18n/<code>.json`. English is complete and the per-key fallback.

1. Copy `en.json` to `<code>.json` (ISO 639-1, e.g. `fr.json`).
2. Set `"language.name"` (`"Français"`).
3. Translate; keep keys and placeholders, remove `log.` keys. Missing keys fall back to English.
4. Build and run the tests (`LocalizationTests` checks placeholders and completeness).

Try without building: put the file into `%LOCALAPPDATA%\Duopilo\i18n\` and restart. Full guide: [docs/i18n.md](https://github.com/duopilo/duopiloapp/blob/main/docs/i18n.md).

## Releases

A tag `v…` starts the **Release** workflow: self-contained win-x64 build with the version from the tag, Inno Setup installer and ZIP, attached to a **draft** release with a changelog since the last tag. Tags with a suffix (`v1.0.0-beta.1`) become pre-releases.

```powershell
git switch main; git pull
git tag v0.9.1
git push origin v0.9.1
```

Publish deliberately: "Publish release" on GitHub or `gh release edit v0.9.1 --draft=false`.

- **Once beforehand:** `pwsh installer/simconnect-secret.ps1` on a PC with the MSFS SDK stores the SimConnect DLLs as secrets `SIMCONNECT_ZIP_*`. Without them a release aborts instead of building against the stub.
- **Test build without a release:** Actions → Release → Run workflow; setup and ZIP appear under *Artifacts* for 14 days.
- The product name is set centrally as `<Product>` in `Directory.Build.props`; program name, data folder and config section in `src/Duopilo/Hosting/AppNames.cs`.

## Wiki

This wiki is maintained in the repository under [`wiki/`](https://github.com/duopilo/duopiloapp/tree/main/wiki) and published automatically when changes reach `main`. Edit pages there via pull request – edits made directly in the GitHub wiki are overwritten.
