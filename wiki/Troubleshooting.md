**English** | [Deutsch](DE-Fehlersuche)

# Troubleshooting

## First look

| Where | What |
|---|---|
| `%LOCALAPPDATA%\Duopilo\logs\duopilo.log` | main log (tray → *Log file*, or Dashboard → *Setup and diagnostics*) |
| `duopilo-stdio.log` | log of the instance Claude Desktop starts |
| `logs\crash.log` | unhandled exceptions and hard crashes |
| <http://127.0.0.1:5155/status> | `"connected": true`? |
| `duopilo_status` | connection, profile, profile warnings, voice loop, SimBrief |
| `Duopilo.exe --console` | live console output |

Logs are always English, so you can attach them to issues as they are.

## Common problems

### Duopilo doesn't connect to MSFS

- Tray dot stays **gray**: MSFS isn't running or no flight is loaded yet – Duopilo reconnects every 5 s.
- Self-built and never connects: the build may have used the **SimConnect stub**. The build log shows a warning; a proper build logs `Duopilo: SimConnect aus …`. Copy the two SDK DLLs into `lib\` and rebuild – see [Installation](Installation#1-get-the-simconnect-sdk).
- Banner **SimConnect missing** / log `SimConnect not found`: the MSFS 2024 SDK isn't installed or is somewhere else. Use **Install MSFS SDK** in the dashboard or set its folder under **Settings → SimConnect**, then restart Duopilo. Self-built, the DLLs can also sit next to `Duopilo.exe`. The log line `SimConnect: loaded from …` shows where they came from.

### No speech

- Tray menu → **Speak callouts** on? Dashboard → **Callouts → Voice**: right **output device**? Minimum level?
- Kokoro still downloading (~310 MB on first use)? Meanwhile the browser reads aloud in the dashboard.
- Only the **tray instance** speaks. If you only run Claude Desktop's stdio instance, start Duopilo normally as well.
- A single callout silent? Check `mutedRules` in `speech.json` and `DisabledRules`.

### A callout never comes

- The rule may need a profile binding the aircraft doesn't have: `get_aircraft_profile` → `callouts` shows `missing`.
- Monitoring is silent for 10 s after loading/air start and 3 s after a pause.
- Plan-based callouts (fuel check, TOD on non-FBW aircraft, transition) need a SimBrief plan.

### Voice loop doesn't react

- Dashboard → voice panel shows the reason: *inactive – Sim not connected / No flight loaded / Paused*.
- No level on the microphone: wrong device, muted, or Windows privacy setting **Let desktop apps access your microphone** is off. The log warns about digital silence.
- Muted by "Kopilot pause" or `Ctrl+Alt+K`? Say "Kopilot resume".
- Commands answered with "Input disabled": unlock input in the dashboard.
- Too slow or stutters in VR: see [Voice Loop → Performance](Voice-Loop#performance).
- More detail: `"Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }`.

### ATC says nothing / "ATC unavailable"

- SimBrief plan loaded? (**Settings → SimBrief**)
- Sim paused or no flight loaded? ATC only runs while the sim runs.
- `get_atc_controller` → `templateErrors` after editing templates.

### Claude doesn't see Duopilo

- **Claude Desktop:** check `claude_desktop_config.json` (path to `Duopilo.exe`, `--stdio`), restart Claude Desktop completely, check `mcp-server-duopilo.log`.
- **Connector:** the log must show *Access control: OAuth active*. After the rename from Kopilot, remove the old connector and add it again.
- Tunnel errors:

| Symptom | Cause |
|---|---|
| Cloudflare error **1033** | tunnel not running (`sc query cloudflared`) |
| **502 Bad Gateway** | tunnel runs, Duopilo doesn't (or not in HTTP mode / other port) |
| **400 Bad Request** | `PublicUrl` doesn't exactly match the host name |
| `/.well-known/…` **404** | OAuth not active – the log says why (e.g. password too short) |

More in [docs/cloudflared.md](https://github.com/duopilo/duopiloapp/blob/main/docs/cloudflared.md#troubleshooting).

### Input action "not confirmed" or "unverified"

- `failed`: the aircraft didn't accept the value (e.g. APU START without power).
- `unverified`: only the switch position could be read. Watch the cockpit and confirm with `mark_action_verified` if it worked.
- "Limit missing": the profile lacks a limit, so the action needs an extra confirmation.

## Crashes

Unhandled exceptions go to `logs\crash.log` immediately. Hard crashes (e.g. an AccessViolation in SimConnect) can't be caught by .NET – the tray instance keeps a small journal (`logs\journal-http.bin`). On the next start Duopilo reports that the last run didn't end cleanly, what was running last and the exception from the Windows Event Viewer.

Windows restarts the tray instance after a crash (after at least 60 s uptime, so there's no loop); the ATC session resumes.

## Report a problem

Dashboard → **Report a problem** captures the current context: position (with map link), altitude, station, FIR and frequency according to Duopilo with their source, COM1, transponder, real and sim time, version, flight and recent transmissions.

1. Choose the type: FIR boundary/station, frequency, airline/callsign, or program bug.
2. Describe what's wrong and what would be right (with a source if you have one – sim ATC, chart, AIP).
3. **Report on GitHub** opens a pre-filled issue – you only need to submit it. Or **Save locally only** (no account or internet needed).

Every report is also saved as Markdown in `%LOCALAPPDATA%\Duopilo\reports`. Long reports are shortened in the link; the full text is in the local copy and on the clipboard (**Copy text**).

Data corrections are added after review; the basis remains VAT-Spy/VATSIM sectors and the MSFS facilities API.
