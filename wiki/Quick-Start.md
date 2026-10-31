**English** | [Deutsch](DE-Schnellstart)

# Quick Start

Your first flight with Duopilo. Assumes Duopilo is [installed](Installation).

## 1. Start MSFS and Duopilo

1. Start MSFS 2024 and load a flight.
2. Start **Duopilo** (Start menu, or `C:\Tools\Duopilo\Duopilo.exe`). No window opens – Duopilo sits in the notification area of the taskbar.
3. **Double-click the tray icon.** The dashboard opens at <http://127.0.0.1:5155/>.

The dot on the tray icon shows the state: gray = waiting for MSFS, green = connected, yellow = caution active, red = warning active.

Check: <http://127.0.0.1:5155/status> should say `"connected": true`.

## 2. Add your SimBrief plan

Dashboard → **Settings → SimBrief** → enter your SimBrief username or pilot ID. The plan loads right away.

Without a plan, monitoring and callouts still work – but route progress, fuel checks, top of descent and [ATC](ATC) need it.

## 3. Choose your voice

Duopilo speaks callouts itself. Dashboard → **Callouts → Voice**: pick the engine (Automatic, Windows, Kokoro), the voice and the **output device** (e.g. your headset). On first use Kokoro downloads its model (~310 MB). Details: [Speech Output](Speech-Output).

## 4. Fly

That's enough for Duopilo as a silent-until-needed PM: it calls out "One hundred knots", "V1", "Rotate", "Positive climb", "Gear up", "Ten thousand", flap speed checks, deviations and warnings. See [Pilot Monitoring](Pilot-Monitoring).

## Optional next steps

| You want … | Do this |
|---|---|
| to talk to Duopilo | Dashboard → **Callouts → Voice → Voice loop**, tick it and choose the microphone → [Voice Loop](Voice-Loop) |
| Duopilo to operate switches on your call | Unlock input: click **Input locked** twice in the callouts bar → [Input Module](Input-Module) |
| ATC from Delivery to Ground | ATC is on by default – with a SimBrief plan, call "Frankfurt Delivery, Lufthansa four alpha bravo, request clearance" → [ATC](ATC) |
| Claude as your PM | [Connecting Claude](Connecting-Claude) |
| German dashboard | **Settings → Language** → Deutsch (callouts and radio stay English) |
| small talk with your first officer | **Settings → First officer and small talk** → [First Officer & Small Talk](First-Officer-and-Small-Talk) |

## A typical voice-loop session

```
You:  "Before start checklist"
F/O:  "Before start checklist, cockpit preparation" …
You:  "Clearance Delivery, Lufthansa four alpha bravo, information A, request clearance to Munich"
ATC:  "Lufthansa four alpha bravo, cleared to Munich, …, squawk two two zero five"
You:  "Cleared to Munich, …, squawk two two zero five, Lufthansa four alpha bravo"
…
You:  "Flaps one"                         → flap lever set, once the flaps are there: "Flaps one"
You:  "Speed two five zero"               → FCU set, "Speed two five zero set"
…
F/O:  "Fuel thirty four point one, two point two below plan"
You:  "Check"
```
