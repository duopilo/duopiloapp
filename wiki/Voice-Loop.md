# Voice Loop

Talk to your copilot directly – **without Claude, without push-to-talk** (VR-friendly), fully local and **without a language model**. Only the optional [small talk](First-Officer-and-Small-Talk) can use one.

```
Microphone → Silero VAD (end of utterance) → Whisper (transcript) → Router ─┬→ copilot commands → Input module
                                                                            ├→ queries, checklists
                                                                            ├→ ATC module
                                                                            └→ small talk (optional, read-only)
                      Speech output (Kokoro / Supertonic) ←─────────────────┘
```

## Switching it on

1. Dashboard → **Callouts → Voice → Voice loop**: tick the box and choose the microphone. Applies immediately.
2. On first start Duopilo downloads Whisper (`base-q5_1`, ~60 MB) and Silero VAD (~2 MB) to `%LOCALAPPDATA%\Duopilo\models`.
3. For commands that operate the aircraft, [input must be unlocked](Input-Module#locked-by-default) too.

Settings live in `%LOCALAPPDATA%\Duopilo\voice-settings.json`. Don't edit the `voice-settings.json` next to `Duopilo.exe` – that's only the template and gets overwritten by updates.

The voice loop runs only in the tray instance.

## How it works

- **Open mic.** Silero VAD detects speech; after 0.7 s of silence the utterance goes to Whisper.
- **Half duplex.** While the copilot or ATC speaks (and 350 ms after), the mic is muted – Duopilo doesn't hear itself. While *you* speak, callouts wait briefly; only warnings and time-critical PM callouts (100 knots, V1, Rotate, Positive climb, Spoilers, Reverse, Decel, 70 knots, radio altitudes, Minimum, Retard) cut in.
- **German or English** per utterance. Numbers are normalized: "Flaps one", "Flaps 1", "Flaps won", "Klappen eins" are the same.
- **Router:** a station name ("Frankfurt Delivery", "Langen Radar", "Shanwick") or your callsign at the start of a call or end of a readback → ATC. Everything else → copilot. "Duopilo, …" always goes to the copilot.
- **Only while the sim runs.** Not connected, no flight loaded or paused → the mic isn't evaluated (saves CPU), no ATC, no callouts; pending readbacks and checklists freeze instead of expiring. The dashboard shows why ("inactive – Paused").

## Wake word and addressing

Commands work without a wake word. To address the copilot explicitly – e.g. during a checklist – use **"Kopilot, …"** (fixed), the **first officer's name** ("Sam, gear up") or "Duopilo, …" if Whisper transcribes it clearly. `commands.requireWakeWord` makes the wake word mandatory.

## Commands

Fixed patterns in English and German. **Every action of every aircraft profile has a voice command** (enforced by a test; full list with examples in [`VoiceCommandCatalog.cs`](https://github.com/duopilo/duopiloapp/blob/main/src/Duopilo/Voice/VoiceCommandCatalog.cs)).

| Area | Examples |
|---|---|
| Configuration | Gear up/down, Fahrwerk ein/aus · Flaps 0/1/2/3/Full, Klappen null/eins/voll · Speedbrake retract/half/full · Arm/Disarm spoilers |
| Brakes, lights, signs | Autobrake low/medium/max/off · Landing lights on/off · Strobe on/auto/off · Beacon on · Taxi light on · Nav lights on · Seat belts on/off · No smoking on/auto |
| Electrics, APU | Batteries on/off · External power on/off · APU start · APU master on/off · APU bleed on/off |
| Anti-ice | Engine anti ice on, Engine one anti ice on · Wing anti ice on · Probe heat on/auto |
| EFIS, FCU | QNH one zero one three · Baro standard · Altitude five thousand · Flight level one two zero · Speed two five zero · Heading zero niner zero · Managed speed · Vertical speed minus one thousand · Autopilot one on · Approach on · ND mode ARC/ROSE NAV/PLAN · ND range one hundred sixty · Expedite climb |
| Transponder, TCAS, radio | Squawk seven thousand · Transponder standby/auto · Altitude reporting on/off · TCAS TA/RA · TCAS traffic all/above/below/threat · COM one standby one two one decimal five · Swap COM one |

Note: "Klappen ein" is not accepted (too close to "eins").

### Execute + callout (default)

As in a real crew, your command is the instruction: the copilot executes right away and calls it out once the aircraft has accepted it – "Speed two five zero set", "Altitude five thousand set"; gear and flaps once the position is reached ("Gear down, three green", "Flaps two").

| Reply | Meaning |
|---|---|
| "Not confirmed" | the aircraft didn't accept the value |
| "Unable" | an interlock blocked it (e.g. batteries off in flight) |
| "Input disabled" | input is locked |
| "Say again" | not understood, or two commands in one utterance |

### Readback with "Confirm"

A readback only comes when recognition was uncertain (Whisper confidence below `commands.minConfidence`, default 0.6) or the action needs a confirmation (see [Input Module](Input-Module#extra-confirmation-with-a-caution)):

1. Duopilo reads back: "Flaps one".
2. **"Confirm"**, **"Correct"**, **"Affirm"** (also "Korrekt") → executed. **"Negative"** / **"Cancel"** → "Cancelled".
3. "Yes", "OK", "Roger" alone confirm nothing → "Say again".

"Confirm" only counts after the readback has been spoken completely, in an utterance started afterwards. Without an answer the action expires after 60 s. Critical actions ask a second time with a caution ("Caution, autothrust off. Confirm autothrust off?").

`"executeWithCallout": false` sends every command through the readback.

## Queries

Answered with the plain callout only:

| Question | Answer (example) |
|---|---|
| Speed check | Speed two five zero (on the ground: Ground speed one two) |
| Altitude check | Altitude five thousand / Flight level three five zero |
| Fuel check | Fuel twelve point four tons |
| Fuel versus plan, Landing fuel | Fuel thirty four point one, two point two below plan, landing fuel six point eight |
| Next waypoint | Hendy, one hundred twenty miles, E T A zero three four two |
| ETA | E T A zero four one eight |
| Top of descent | Top of descent eighty five miles, E T A zero three one two |
| Traffic check | Traffic, 2 o'clock, 5 miles, 1000 feet above, descending / No traffic |
| Wind check, Temperature | Wind two seven one at forty five · Temperature minus forty seven |
| Squawk check | Squawk two three four zero |
| Station check, Which frequency | Montreal Center, one three two decimal two five |
| Engine check | Engines normal / Engine two not running / N1 split |
| Gear, Flaps, Speedbrake, Spoilers, Seat belts, APU, Baro, Heading check | Gear down · Flaps one · Speedbrake retracted · … |

Plan-based answers need a SimBrief plan – otherwise "Unable".

## Acknowledgment ("Check" / "Copy")

Important messages expect **"Check"** or **"Copy"**: fuel warnings, safety cautions and warnings, ATC squawk/frequency deviations, TCAS RA. Without an answer Duopilo repeats once after ~10 s, then escalates with an attention tone and the dashboard alert `ACK_MISSING`. A late "Check" or the end of the condition clears it. Configure under `acknowledge` in `voice-settings.json`.

## Checklists

Challenge-response after the Airbus Normal Checklist:

| Say | German |
|---|---|
| Before start checklist | Checkliste vor dem Anlassen |
| After start checklist | Checkliste nach dem Anlassen |
| Before takeoff checklist | Checkliste vor dem Start |
| Approach checklist | Checkliste Anflug |
| Landing checklist | Checkliste Landung |
| After landing checklist | Checkliste nach der Landung |
| Parking checklist | Checkliste Parken |

Duopilo reads the challenge, waits for your answer and **compares it with the sim** where it can (signs, fuel, baro, beacon, flaps, autothrust, autobrake, landing memo, spoilers, APU, engines). A deviation is called out ("Beacon off") and the item stays open; if it still deviates, the checklist continues and lists open items at the end.

"Standby" holds · "Skip" / "Next item" skips · "Say again" repeats · "Stop checklist" interrupts. While a checklist runs, every utterance without the wake word is an answer – commands need "Kopilot, …".

## Control

| Say / press | Effect |
|---|---|
| "Kopilot pause" / "Kopilot resume" (also "Kopilot weiter", "Duopilo pause") | mute / unmute voice input – callouts keep running |
| `Ctrl+Alt+K` (system-wide, `hotkeys.mute`) | same |
| "Say again" | repeats the last reply |
| "Briefing" | first officer introduction |

## Radio

Calls go straight to the [ATC module](ATC) – like `atc_transmit`, just without Claude. "Say again", "Affirm" and "Negative" without a station go to whoever spoke last. If ATC is off or the SimBrief plan is missing: "ATC unavailable".

## Settings (`voice-settings.json`)

Comments are allowed. Everything applies only with `"enabled": true`.

| Section | What |
|---|---|
| `microphone` | device (part of the name, empty = default), VAD threshold, minimum length, silence until end of utterance, tail after callouts |
| `recognition` | Whisper model (`tiny`/`base`/`small`, `-q5_1`/`-q8_0`), `cpu`/`gpu`, threads, language `auto`/`de`/`en`, vocabulary hint |
| `voices` | copilot voice (`kopilot`), copilot and ATC volume |
| `hotkeys` | mute hotkey |
| `commands` | wake words, `requireWakeWord`, confirmation timeout, `executeWithCallout`, `minConfidence` |
| `acknowledge` | on/off, `repeatAfterSec`, rules that expect "check"/"copy" |
| `atc` | additional callsigns and stations, callsign from MSFS |
| `callouts` | active and muted rules, `silentWhenPaused`, taxi speed warning |

**Microphone:** if the chosen device is missing (VR headset still off), Duopilo uses the default device and switches automatically once it appears.

## Performance

Default: Whisper on the CPU with 4 threads, only after you've spoken. Faster: a fixed language (`"language": "en"`), `"audioContext": 768` or `tiny-q5_1`. Better German: `small-q5_1` (~190 MB). `gpu` tries CUDA, then Vulkan, else CPU – next to MSFS in VR that can cause stutters.

## Diagnostics

- Status: `duopilo_status` → `voiceLoop`, the dashboard next to the microphone, or `http://127.0.0.1:5155/voice`.
- The log shows every recognized utterance with timing and target, discarded ones with the reason, and a warning if the mic only delivers silence (muted, or Windows privacy setting *Let desktop apps access your microphone* off).
- More detail: `"Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }` at the top level of `appsettings.Local.json`.

Licenses: Whisper.net and whisper.cpp (MIT), Silero VAD (MIT), NAudio (MIT).
