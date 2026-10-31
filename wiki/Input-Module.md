**English** | [Deutsch](DE-Eingabe-Modul)

# Input Module

Duopilo can press buttons and set switches – as Pilot Monitoring, **on the pilot's call**, with readback and confirmation. Actions are currently defined for the **FlyByWire A32NX** and the **Headwind A330-900neo**; most of them are not yet verified in the sim.

## Locked by default

Unlock in the dashboard: click **Input locked** twice in the callouts bar ("Really unlock?"). The bar also shows the pending action with a countdown (including one prepared through Claude Desktop), lets you cancel it, and lists every input with ✓/✗.

Alternatively `Duopilo:Input:Enabled` in `appsettings.Local.json` – the dashboard switch takes precedence and applies to all Duopilo instances.

## Two steps

1. You say "Set speed two five zero". Claude calls `prepare_action`; Duopilo checks value and interlocks and returns the readback: "Speed 250 selected". **Nothing changes yet.**
2. Claude reads it back. Only after your **"confirm"** does Claude call `confirm_action`. Duopilo checks the interlocks again against the current state, executes, and reads back from the aircraft whether the value arrived.

"Negative" or a corrected value discards it. A prepared action expires after 60 s; there is only ever one.

With the [voice loop](Voice-Loop#execute--callout-default), confidently recognized commands are executed directly and called out afterwards ("Speed two five zero set"); the readback route is used when recognition is uncertain or a caution applies.

## What works (A32NX)

| Area | Actions |
|---|---|
| FCU | speed (selected/managed), heading (selected/managed), altitude, pull/push, increment 100/1000, V/S, LOC, APPR, EXPED, AP 1/2, A/THR |
| EFIS | QNH in hPa, STD/QNH, LS, FD, ND mode and range (captain) |
| Configuration | gear, flap lever, speedbrake, arm ground spoilers |
| Brakes | autobrake OFF/LO/MED/MAX |
| Lights | beacon, wing, runway turn-off, landing (on/off/retract), nose (T.O/taxi/off), strobe (on/auto/off) |
| Signs | seat belts, no smoking/no PED |
| Anti-ice | engines (both or individually), wing, probe/window heat |
| Air/APU/Elec | pack 1/2, APU master, APU start, APU bleed, external power (on the ground with GPU) |
| Transponder | squawk, mode, ALT RPTG, TCAS mode and traffic |
| Radio | COM 1 standby, swap |

NAV & LOGO is missing on the A32NX (only reachable as a `B:` input event). Details per aircraft: [docs/aircraft.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.md).

## What never works

Engine masters, ignition/start, thrust, fire handles and agents, fuel (pumps, crossfeed), hydraulics (pumps, PTU, RAT), IDG disconnect, MCDU, pause/slew/sim control – **not even with a confirmation**. Anything that can damage the aircraft or is irreversible, the pilots do themselves.

This is fixed in code; an edited profile can't get around it:

- Only events from a fixed list (FCU, autobrake, gear, flaps, spoilers, lights, signs, transponder, COM).
- Only `L:` variables are written, never standard SimVars – and the block list applies there too.
- Gear and flaps only through their events, and only together with the matching interlock.
- Areas and IDs like `Engine`, `Fuel`, `Fire`, `Hydraulic`, `eng.*`, `fuel.*`, `*.idg1` are removed on load.
- Safety-relevant switches only exist under their standard ID (`gear.down`, `air.pack1` …), so the interlocks always apply.
- There is no tool that writes arbitrary events or variables.

## Interlocks

Rejected with a reason:

- gear up only in the air
- gear down only up to VLO (A320/A330: 250 kt) and below `maxGearAltitudeFt` (25,000 ft)
- flaps only up to the VFE of the new setting
- no speedbrake in the inhibited flap settings (`speedbrakeInhibitFlaps`, A320/A330: FULL)
- batteries off and both packs off only on the ground (one pack off in flight is fine)
- APU master off not while APU bleed is on
- ADIRS off only on the ground

Plus per-action conditions, e.g. V/S and heading not in TRK/FPA mode, QNH only in QNH mode, APU start only with APU master on.

## Extra confirmation with a caution

A second "confirm" after the readback for:

- transponder STBY, ALT RPTG off, TCAS STBY in flight
- squawk 7500 / 7600 / 7700
- ADIRS off on the ground
- AP 1/2 and A/THR off in flight
- FCU altitude below the MSA or outside the ATC clearance

The first `confirm_action` returns `status: "awaiting_caution_confirmation"` with a caution ("Caution, transponder standby in flight. Confirm transponder standby?"). Only the second "confirm" with the same token executes. Skipped if the switch is already in that position.

**Missing limits** in the profile (VLO/VLE, VFE, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource`) also require the extra confirmation – shown as "Limit missing" in the dashboard and as `profileWarnings` in `duopilo_status`.

## How Duopilo checks the result

- Values are set absolutely. Already set → nothing is written, `alreadySet`.
- The new value has to hold (`holdMs`, default 1 s). If the aircraft resets it (e.g. APU START without power), it counts as not accepted.
- `verification` in the response:

| Value | Meaning |
|---|---|
| `confirmed` | a real aircraft state was read (FCU display, flap lever, light, contactor …) |
| `unverified` | only the switch position could be read; the effect isn't proven |
| `failed` | the aircraft didn't accept the value |

- **Gear and flaps:** after `gear.down` Duopilo waits until all legs are locked – "Gear down, three green" – otherwise "Gear disagree" after 20 s. Likewise "Flaps two" once flaps and slats have stopped, otherwise "Flaps disagree".
- With an old token, `confirm_action` tells you what happened to the action (replaced, expired, already used, other instance).

## Remembering verified actions

Actions marked `"verified": true` in the profile were tested in the sim. Duopilo remembers more in `%LOCALAPPDATA%\Duopilo\verified-actions.json`:

- automatically when an execution was `confirmed`;
- via `mark_action_verified` when you saw an `unverified` action work in the cockpit ("Seatbelts on", "APU avail", "check").

If an action's definition changes, its stored verification no longer applies. `list_actions` shows `verified` per action: `profile`, `sim`, `pilot` or empty.

## Defining actions

Actions live in the aircraft profile under `actions` (events, variables, readback, check, `requires`). See [Aircraft Profiles](Aircraft-Profiles).
