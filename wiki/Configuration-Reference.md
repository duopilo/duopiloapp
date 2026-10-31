**English** | [Deutsch](DE-Konfiguration)

# Configuration Reference

Most settings can be changed in the [dashboard](Dashboard-and-Settings#settings-page). This page lists what's behind it.

## Precedence

1. Environment variables `Duopilo__Section__Key` (e.g. `setx Duopilo__Language de`)
2. `appsettings.Local.json` next to `Duopilo.exe` – **your settings**, never overwritten by updates
3. `appsettings.json` next to `Duopilo.exe` – defaults, renewed on updates (don't edit)

Command-line overrides like `--Duopilo:Transport=http` work too. The old names (`"Kopilot"` section, `Kopilot__…`) are still read.

## `appsettings.Local.json`

Only put in what you change. Example:

```jsonc
{
  "Duopilo": {
    "Language": "de",
    "SimBrief": { "Username": "your-simbrief-name" },
    "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] },
    "Remote": { "PublicUrl": "https://duopilo.example.com" }
  },
  "Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Transport` | `http` | `http` (tray, dashboard, remote) or `stdio` (Claude Desktop uses `--stdio`) |
| `Language` | `en` | `en`, `de` – see [Language](Dashboard-and-Settings#language) |
| `HttpUrl` | `http://127.0.0.1:5155` | local address of dashboard and `/mcp` |
| `ProfilesDirectory` | `profiles` | aircraft profiles |
| `DataDirectory` | empty | data folder; empty = `%LOCALAPPDATA%\Duopilo` |
| `RestartAfterCrash` | `true` | Windows restarts the tray instance after a crash (after 60 s uptime) |
| `SimBrief:Username` / `PilotId` | empty | [SimBrief](SimBrief-and-Flight-Plan) |
| `Monitoring:Enabled` | `true` | [Pilot Monitoring](Pilot-Monitoring) |
| `Monitoring:DisabledRules` | `[]` | rules to switch off completely |
| `Monitoring:AircraftAutoCallouts` | `false` | add radio altitudes / minimum / retard |
| `Monitoring:Traffic:*` | | [Traffic](Traffic#traffic-warning-traffic_proximity) |
| `Monitoring:FuelCheck:*` | | [Fuel check](Pilot-Monitoring#fuel-check) |
| `Input:Enabled` | `false` | [Input Module](Input-Module) – the dashboard switch takes precedence |
| `Input:ConfirmWindowSeconds` | `60` | lifetime of a prepared action |
| `Atc:Enabled`, `Speak`, `SquawkFromSim`, `SessionFile`, `SessionMaxAgeHours`, `TemplatesFile` | | [ATC](ATC#settings) |
| `Speech:Engine` | `auto` | `auto`, `windows`, `kokoro` |
| `Speech:KokoroVoice` | `bm_george` | default Kokoro voice for callouts |
| `Speech:KokoroThreads` | `2` | CPU threads for Kokoro |
| `FirstOfficer:*` | | [First Officer](First-Officer-and-Small-Talk) |
| `Smalltalk:*` | `Provider: off` | [Small talk](First-Officer-and-Small-Talk#small-talk-optional-off-by-default) |
| `Remote:PublicUrl`, `BearerToken`, `ConsentPassword` | | [Remote connector](Connecting-Claude#remote-connector-claudeai-phone-voice-mode) |
| `Reports:GitHubRepository` | `duopilo/duopiloapp` | target of **Report a problem**; empty = local only |

## Data folder `%LOCALAPPDATA%\Duopilo`

| File / folder | Content |
|---|---|
| `logs\duopilo.log`, `duopilo-stdio.log` | logs (always English) |
| `logs\crash.log`, `journal-http.bin` | crash reports and crash journal |
| `logs\traffic-messages.csv` | traffic response log |
| `speech.json` | speech output settings, `mutedRules` |
| `voice-settings.json` | [voice loop](Voice-Loop#settings-voice-settingsjson) settings (created from the template on first start) |
| `input.json`, `input-pending.json` | input switch and pending action, shared by all instances |
| `verified-actions.json` | actions verified in the sim or by the pilot |
| `first-officer.json` | first officer of the current flight |
| `atc-session.json`, `atc-session-stdio.json` | saved ATC session |
| `atc-templates.json` | **your** [template overrides](ATC-Templates) |
| `data\…` | **your** newer airspace/airline data |
| `i18n\<code>.json` | **your** translation overrides |
| `models\` | Kokoro, Whisper, Silero VAD, Supertonic |
| `ecam\` | cached ECAM text tables |
| `oauth-state.json` | Claude connector sign-ins (delete to revoke all) |
| `reports\` | saved problem reports |
| `appsettings.Local.backup.json` | backup before the dashboard writes settings |

## Command line

| Option | Effect |
|---|---|
| *(none)* | mode from `Duopilo:Transport` – by default HTTP with tray icon and dashboard |
| `--http` | force HTTP mode |
| `--stdio` | stdio MCP server for Claude Desktop (no speech, no voice loop) |
| `--console` | with a console window, for troubleshooting |
| `--no-tray` | without tray icon |
| `--generate-secret` | prints a random bearer token (use `\| more`) |
