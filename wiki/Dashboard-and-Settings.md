**English** | [Deutsch](DE-Dashboard-und-Einstellungen)

# Dashboard & Settings

## Tray icon

Duopilo runs without a window, in the notification area of the taskbar.

| Dot | Meaning |
|---|---|
| gray | waiting for MSFS |
| green | connected |
| yellow | caution active |
| red | warning active |

- **Double-click:** opens the dashboard
- **Right-click:** Dashboard, Reload profiles, Log file, Program folder, **Speak callouts** (on/off), **Start with Windows**, Exit

Starting Duopilo a second time only opens the dashboard of the running instance.

## Dashboard

<http://127.0.0.1:5155/> – runs in the browser, **only locally on the sim PC** (blocked through the tunnel).

- **FMA line** (thrust, vertical, lateral, AP/FD/A-THR) in Airbus colors
- **Flight data** with speed and altitude target; master warning/caution as buttons top right
- **Callouts**: active alerts and history. With *read aloud* on, the browser also speaks each callout. The callouts bar also holds the **Input locked/unlocked** switch, the pending input action with countdown and the input log
- **Voice**: speech output, voice loop, microphone with level, first officer
- **ECAM** in plain text, **route** with progress and fuel trend from SimBrief, engines, configuration, ATC runway, weather
- **Setup and diagnostics**: all profile bindings with verified/unverified status and current value, log, reload buttons
- **Brightness** slider – dims the displays for night flights
- Top right: **Report a problem** and **Settings**

## Settings page

`http://127.0.0.1:5155/#einstellungen` (button **Settings**) – instead of editing files by hand.

| Area | Content | Applies |
|---|---|---|
| **Connection to Claude** | local MCP endpoint, public tunnel address, consent password, generate/remove bearer token, check the tunnel | after restart |
| **SimBrief** | username or pilot ID, loads the plan right away | immediately |
| **Microphone** | recording device of the voice loop, with level | immediately |
| **Modules** | Monitoring, Input (click twice), ATC, Voice loop | Monitoring/ATC after restart, otherwise immediately |
| **First officer and small talk** | name and gender, introduction, voice (with **Listen**), small talk provider, model, address, API key, limits, **Test** | immediately |
| **Language** | dashboard, tool messages, tray menu, problem reports | immediately |

How it saves:

- `appsettings.Local.json` next to `Duopilo.exe` – comments and order are preserved; a backup goes to `%LOCALAPPDATA%\Duopilo\appsettings.Local.backup.json` first.
- Input and voice loop go to `input.json` and `voice-settings.json` in the data folder.
- Settings that are only read at startup are marked *applies after restart*. The **Restart Duopilo** button restarts the tray instance (the ATC session resumes from its saved state).
- Passwords, tokens and API keys are never sent back to the browser. A newly generated token is shown exactly once.
- If an environment variable (`Duopilo__…`) sets the same value, the page tells you – the variable wins.

## Language

English is the default, German the first translation. Switch under **Settings → Language** or:

```jsonc
"Duopilo": { "Language": "de" }   // default "en"
```

| Follows the setting | Always English |
|---|---|
| Dashboard, tool messages to Claude, tray menu, problem reports, small talk | Callouts, checklists, FMA calls, warnings, radio (ATC), **logs** |

The voice loop understands German and English commands regardless of this setting. Want to translate Duopilo? See [Development → Translations](Development#translations).

## Raw data (local only)

| URL | Content |
|---|---|
| `/status` | connection status (`"connected": true`) |
| `/state` | flight state |
| `/systems`, `/nav`, `/env`, `/profile`, `/warnings`, `/alerts`, `/monitoring`, `/atc`, `/plan`, `/progress` | the same data the tools return |
| `/read?name=INDICATED ALTITUDE&unit=feet` | a single SimVar or `L:` variable |
| `/input-events?filter=FCU&changes=1` | the aircraft's input events with values; `changes=1` only those that changed since the last call |
| `/atc/controller` | state of the Duopilo ATC |
| `/voice` | voice loop status |

Writing dashboard calls use the header `X-Duopilo: 1`.
