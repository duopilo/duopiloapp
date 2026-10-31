# Speech Output

Duopilo speaks callouts, replies and ATC itself – in English, like a real crew. Claude.ai and voice mode can't receive push messages from a server, so this is how warnings reach you without asking.

## Priority

Everything runs through one queue:

**Warnings > Callouts > Replies > ATC > Small talk**

Warnings interrupt everything; older alerts expire after 8 s. An interrupted reply or ATC message is repeated once after the warning.

Only the **tray instance** speaks – not the stdio instance Claude Desktop starts, otherwise everything would be heard twice.

## Engines and voices

| | Windows voice | Kokoro | Supertonic 3 |
|---|---|---|---|
| Sound | Windows text-to-speech | neural, natural | neural, natural |
| Languages | English voice needed | English | English **and** German with every voice |
| Download | – (install an English Windows voice) | model ~310 MB on first use | model ~400 MB when the first officer gets a Supertonic voice |
| Used for | callouts | callouts, ATC stations, first officer | first officer when small talk is German |
| CPU | practically none | 2 threads (`Speech:KokoroThreads`), fixed callouts pre-computed | 2 threads (`Smalltalk:SupertonicThreads`), ~1.3 s per short sentence |

**Automatic** (default) uses the Windows voice if an English one is installed, otherwise Kokoro. While Kokoro is loading, the browser reads aloud in the dashboard.

With the [first officer](First-Officer-and-Small-Talk) on, the **first officer's voice** speaks callouts, replies and small talk – one speaker for everything. ATC stations always get their own Kokoro voices.

### Installing an English Windows voice (optional)

Settings → Time & language → Language & region → Add a language → *English (United Kingdom)* with "Text-to-speech". Restart Duopilo.

## Settings

Dashboard → **Callouts → Voice**:

- speech output: Automatic, Windows or Kokoro
- Windows voice (e.g. George, Hazel) or Kokoro voice (e.g. George, Emma, Michael)
- **output device** – e.g. the headset instead of the speakers
- volume and rate
- minimum level: all, caution and above, warnings only
- add radio altitudes / minimum / retard (for aircraft without their own)

The panel also shows which voice is speaking and how long Kokoro took for the last callout. Tray menu → **Speak callouts** switches speech on and off.

Stored in `%LOCALAPPDATA%\Duopilo\speech.json`; models in `%LOCALAPPDATA%\Duopilo\models`.

## Pronunciation

ATC, callouts, fuel checks and voice-loop replies share one pronunciation (`Speech/Pronunciation.cs`):

| What | Spoken |
|---|---|
| Five-letter fixes | as a word: HENDY → "Hendy" |
| Navaids (up to three letters) | name from the OFP navlog: YWK → "Wabush"; without a name in the NATO alphabet: FFM → "foxtrot foxtrot mike" |
| ICAO codes | always spelled: KLAX → "kilo lima alpha x-ray" |
| Coordinate points | 53N050W → "fifty three north fifty west" |
| Runways | 25R → "two five right", 09 → "zero niner" |
| Squawks, headings, speeds, frequencies | digit by digit, 9 as "niner", frequencies with "decimal" |
| Flight levels | "flight level three four zero", whole hundreds "flight level one hundred" |
| Altitudes | in thousands: "one zero thousand", "two thousand five hundred" |
| ECAM titles | spelled out: "ENG 1 FIRE" → "engine one fire", "APU" → "A P U" |

Speeds are always in knots, never Mach.

## Muting

| Goal | How |
|---|---|
| Mute one callout (still shown) | `"mutedRules": [ "PM_GEAR" ]` in `speech.json` |
| Turn a rule off completely | `Duopilo:Monitoring:DisabledRules` – see [Pilot Monitoring](Pilot-Monitoring#turning-rules-off) |
| All speech off | tray menu → **Speak callouts** |

## Licenses

Kokoro-82M and voices: Apache 2.0 · KokoroSharp: MIT · Supertonic 3 weights: BigScience OpenRAIL-M (use restrictions apply, e.g. disclose machine-generated voice in published content) · Supertonic inference example: MIT.
