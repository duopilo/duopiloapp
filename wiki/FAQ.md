# FAQ

### Do I need Claude to use Duopilo?

No. Callouts, monitoring, speech output, the voice loop, checklists, inputs and ATC all run inside Duopilo, locally. Claude is optional – it adds a conversational PM that can read everything and reason about it. Only the optional small talk uses a language model, and you choose which.

### Does it cost anything?

Duopilo itself is free. Claude (with a Claude plan) or small talk via an API key are billed by the respective provider. Ollama on your own second machine is free.

### Which simulator and aircraft?

MSFS 2024 on Windows. Generic values work with any aircraft; airliner details (FMA, ECAM, inputs) need a profile – bundled for the FlyByWire A32NX and A380X, Headwind A330-900neo and iniBuilds A321LR. See [Aircraft Profiles](Aircraft-Profiles).

### MSFS 2020?

Not supported or tested. Duopilo is built against the MSFS 2024 SDK.

### Can Duopilo fly the aircraft?

No. It's a Pilot Monitoring, not an autopilot. It operates switches only on your call, after a readback, and never touches engines, thrust, fuel, fire or hydraulics. See [Input Module](Input-Module).

### Does it work in VR?

That's a design goal: the voice loop needs no push-to-talk, and a mute hotkey (`Ctrl+Alt+K`) works system-wide. If your VR headset's microphone isn't on yet, Duopilo uses the default device and switches when it appears. A full VR test flight is still on the [Roadmap](Roadmap).

### Does it slow down MSFS?

Duopilo keeps CPU use deliberately low: Kokoro and Supertonic use 2 threads, Whisper 4 threads and only after you've spoken, and the microphone isn't evaluated while the sim is paused. There's no local language model. GPU mode for Whisper is possible but can cause stutters in VR.

### Can I speak German?

Commands, queries and checklists: yes, German and English are both understood. Callouts, checklists, FMA calls, warnings and radio are always answered in English (crew language). The dashboard and small talk can be German.

### Why are callouts always in English?

Like a real crew's SOP: one cockpit language for everything safety-relevant. The dashboard, tool messages and small talk follow your language setting.

### Is it compatible with BeyondATC or SayIntentions?

External ATC programs aren't visible through SimConnect. You can use them for ATC and turn Duopilo's ATC off (`Duopilo:Atc:Enabled: false`); monitoring and callouts keep working.

### What about MSFS's own ATC?

Duopilo reads the runway MSFS ATC assigns (`get_atc`, callout "Runway two three left assigned"). If you use Duopilo's ATC, mute MSFS ATC. Note that MSFS may still change squawk and COM1 – Duopilo notices and asks you to fix it.

### Why does my first officer have a different name every flight?

Each SimBrief plan is a new flight with a new crew. Want a fixed name? **Settings → First officer and small talk**.

### Where are my settings? Will an update overwrite them?

Your settings live in `appsettings.Local.json` and in `%LOCALAPPDATA%\Duopilo`. Updates (installer or `update.cmd`) never overwrite them. See [Configuration Reference](Configuration-Reference).

### Is my data sent anywhere?

Only where you configure it: SimBrief (your public OFP), ECAM text tables and models are downloaded from GitHub/Hugging Face, small talk goes to the provider you choose, and Claude talks to Duopilo through the connector you set up. **Report a problem** only opens a pre-filled GitHub issue that you submit yourself.

### I used Kopilot before.

See [Migrating from Kopilot](Migrating-from-Kopilot). "Kopilot" stays the wake word.
