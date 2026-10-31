# First Officer & Small Talk

## First officer

With the [voice loop](Voice-Loop) on, every flight gets its own first officer:

- a **name** picked at random from internationally easy-to-pronounce names (e.g. Anna, Emma, Sophie / Sam, Ben, Daniel),
- a **voice** of the same gender that is never the voice of an ATC station.

Name and voice stay for the whole flight – also across Duopilo restarts (`%LOCALAPPDATA%\Duopilo\first-officer.json`). A new SimBrief plan means a new flight and a new first officer. The voice is used for callouts, replies and small talk.

| | |
|---|---|
| **Shown** | in the dashboard header ("F/O Sam"), the voice panel and Settings |
| **Introduction** | once per flight at the gate (on the ground, within 5 NM of the departure airport) – "Hi, I'm Sam, I'll be your first officer today" – or when you say **"Briefing"** |
| **Addressing** | "Sam, gear up" works like "Kopilot, gear up". Whisper's spelling variants are tolerated (Erik/Eric, Jonah/Jonas), ordinary words never count as the name ("nice" isn't Nico) |
| **Fixed name** | Settings → **First officer and small talk** (with gender for names not on the list) |
| **Off** | same place – Duopilo then speaks with the dashboard voice |

### Voice

Settings → **First officer and small talk → Voice**: *Automatic* (per flight, matching the name) or a fixed voice, with **Listen** to preview. Saved as `voices.kopilot` in `voice-settings.json`, applies immediately. A fixed voice decides the gender.

| Engine | Voices | Speaks |
|---|---|---|
| **Kokoro** | female `af_heart`, `af_bella`, `bf_emma`, `af_aoede`, `af_kore`, `af_sarah`; male `am_michael`, `am_fenrir`, `am_puck` (reserve `bm_george`, `bm_fable`) | English only – small talk is then English too |
| **Supertonic 3** | `M1`, `M2` (male), `F1` (female) | callouts in English, small talk in German |

*Automatic* picks Supertonic when small talk is German, otherwise Kokoro. Until the Supertonic model (~400 MB, SHA-256-checked) is downloaded, the first officer speaks with a Kokoro voice. Don't want Supertonic: `Smalltalk:Supertonic: false`.

Callouts and small talk always come from **the same speaker**.

## Small talk (optional, off by default)

A sentence that is neither a command, a query nor a radio call goes to a language model instead of "Say again":

> "Hey Sam, how's the weather in Berlin?"

| Provider | What you need |
|---|---|
| **Claude** | your own API key ([console.anthropic.com](https://console.anthropic.com)); model empty = default Haiku model |
| **OpenAI-compatible** | base URL (default `https://api.openai.com/v1`; also Groq, LM Studio, vLLM …), model, API key if required, **Reasoning** |
| **Ollama** | address of another computer (e.g. `http://192.168.1.20:11434`) and the model |

There is deliberately **no local model inside Duopilo** – it would cost too much GPU and RAM next to MSFS. Run Ollama on a second machine if you want it local.

Set up under Settings → **First officer and small talk** (applies immediately; **Test** asks a short question). The API key is stored only in `appsettings.Local.json` (or `Duopilo__Smalltalk__ApiKey`), never sent back to the browser and never written to logs or reports.

### Rules

- **Read-only.** The model gets read-only context (flight phase, position, route, next waypoint, ETA, fuel and fuel against plan, wind and temperature, METARs of the plan, ATC station) and the last few sentences. Its answer is only spoken – **never** executed as a command and never prepared for "Confirm".
- **Lowest priority.** Callouts, warnings, acknowledgments, checklists and radio always come first; anything interrupts small talk (no repeat). Nothing goes to the model while a readback waits, a checklist runs, a message waits for "Check" or the mic is muted.
- **No room chatter.** Sentences without addressing go to the model only up to `UnaddressedMaxWords` words (default 12).
- **Limits.** `MaxReplyWords` (40), `RequestsPerMinute` (6), `TimeoutSec` (10). Above the limit, offline or on any error: "I can't chat right now".
- **Language.** Small talk follows the dashboard language (if the voice can speak it); callouts and radio stay English.

### Reasoning models

Reasoning models (e.g. `openai/gpt-oss-20b` on Groq) spend tokens thinking, which can leave nothing for the answer. **Reasoning** (OpenAI-compatible only, `Smalltalk:Reasoning`):

| Setting | Behaviour |
|---|---|
| Automatic (default) | sends `reasoning_effort: "low"` for models with "gpt-oss" in the name, nothing otherwise |
| Off | never sends it |
| Low / Medium / High | always sends it; adds token headroom (+512 / +1024 / +2048) |

An empty answer at the token limit gets its own warning in the log and on the **Test** button.

### Privacy and costs

The settings page states where the conversation goes. With Claude or a cloud OpenAI-compatible service, your sentences and the read-only flight context are sent to that provider and billed to your key.

## Settings reference

```jsonc
"Duopilo": {
  "FirstOfficer": { "Enabled": true, "Name": "", "Gender": "auto", "Introduce": true },
  "Smalltalk": {
    "Provider": "off",          // off | claude | openai | ollama
    "Model": "", "Url": "",
    "MaxReplyWords": 40, "RequestsPerMinute": 6, "TimeoutSec": 10, "UnaddressedMaxWords": 12,
    "Supertonic": true, "SupertonicThreads": 2,
    "Reasoning": "auto"
  }
}
```
