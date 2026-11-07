**English** | [Deutsch](DE-ATC)

# ATC

Duopilo has its own ATC: a **state machine with fixed text templates – no language model**. You call on the radio as usual; the [voice loop](Voice-Loop) (or Claude via `atc_transmit`) passes your transmission on, and Duopilo answers as the responsible unit – each station with its own Kokoro voice.

Phraseology is English, ICAO style: numbers digit by digit ("squawk two two zero five"), speeds always in knots, never Mach, fixes and navaids as described under [Speech Output](Speech-Output#pronunciation).

**Requires** a SimBrief plan ([SimBrief & Flight Plan](SimBrief-and-Flight-Plan)). ATC is on by default (`Duopilo:Atc:Enabled`).

## Flow

Transitions come from your transmissions and from the flight state.

| Station | What happens | Handoff |
|---|---|---|
| **Delivery** | IFR clearance with SID, runway, initial climb, squawk; readback checked | after a correct readback: "readback correct, contact Ground" |
| **Ground** | push and start, taxi to the holding point (readback holding point/runway) | stopped at the holding point (8 s), or you call "ready" |
| **Tower** | line up and wait, takeoff clearance with wind | from 1500 ft AGL: "contact Departure" |
| **Departure** | "identified, climb FL150", optionally direct to the first fix after the SID | 2000 ft below the cleared altitude → Center |
| **Center** | climb to cruise, level requests, step climbs from the OFP, descent clearance at TOD, QNH below transition | at every FIR boundary and sector boundary |
| **Oceanic** | clearance ~30 min before the entry point, position reports at every oceanic fix | at the exit point to the domestic unit |
| **Approach** | descent, STAR, expected approach and runway, QNH; approach clearance from 20 nm | from 12 nm: "contact Tower" |
| **Tower** | landing clearance with wind (at the latest 4 nm out); with traffic ahead on final first the sequence ("number two, follow …") | below 40 kt after landing: "vacate via next exit, contact Ground" |
| **Ground** | taxi to the gate – or, on request, a [follow-me](#follow-me) | ends when stopped with the parking brake set |

- Short routes without oceanic fixes skip Oceanic.
- Take off without clearances → ATC catches up to Tower during the takeoff roll.
- Start a session in the air → it begins with the unit responsible there; your current level counts as cleared.

## Readback

For clearances Duopilo checks the values listed in the template (e.g. SID, altitude, squawk). Digits ("2205", "FL110"), spoken numbers ("two two zero five", "one two one decimal eight") and the NATO alphabet are understood.

A wrong or missing value gets "negative" with the correct value – then only that value needs reading back:

> "Lufthansa four alpha bravo, negative, squawk two two zero five"

- "Say again" repeats the last transmission.
- "Radio check" / "how do you read" → "read you five", without changing anything.
- Plain acknowledgements without a clearance get no answer.

## Levels in cruise

Departure, Center and Oceanic handle level requests:

| You | ATC |
|---|---|
| "request climb flight level three four zero" / "requesting FL340" | "climb and maintain FL340" |
| "request descent to one two thousand feet" | below transition: "descend and maintain 12000 ft, altimeter …" |
| "request higher" / "request lower" | next OFP step climb, otherwise 2000 ft |

- **Step climbs** from the OFP: 10 nm before the step point ATC offers "when ready climb and maintain FL340" on its own.
- Optionally ATC declines ("unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes", `levelRequestUnablePercent`, off by default) and clears later.
- The cleared altitude also feeds the input module's FCU altitude check.

## Descent

At top of descent Center clears "descend via ANJLL4 arrival" if the OFP has a STAR (cleared down to the lowest planned STAR altitude), otherwise "descend FL110". If the clearance ends below the destination's transition altitude, the QNH comes with it and is part of the readback.

## Follow-me

Only on request, as at most real airports – ATC never sends one on its own. After vacating, ask ground (also later while taxiing in):

> "Düsseldorf Ground, Lufthansa four alpha bravo, runway two three right vacated, request follow-me"
> → "Lufthansa four alpha bravo, hold position, follow-me is on the way"

Duopilo's own van comes towards you over the taxiways and turns around in front of you; then ground says "follow the follow-me to gate C2" (readback: the gate). It drives ahead with its beacon on and keeps its distance – faster when you close in, it waits when you stop. At the gate it drives past the stand entry, waits until you are there and disappears.

- "cancel follow-me" / "follow-me not required" sends it away (still holding: the normal taxi clearance).
- Gate: the one ATC knows (set with `atc_set_value gate`), otherwise a free one that fits your type.
- No vehicle (package missing, no route, gate right ahead, not in front of you after 4 minutes, `followMeTimeoutSeconds`): "follow-me not available, taxi to …".
- Before departure there is none ("negative, follow-me not available").
- Needs Duopilo's vehicle package in the Community folder and runs in the tray instance only; settings `Duopilo:FollowMe` ([Configuration](Configuration-Reference)). `follow_me` shows the state and requests one without the radio for testing.

## Traffic

Duopilo ATC mentions traffic the way real controllers do. The traffic picture comes from the same scan as `get_traffic` (AI traffic, live traffic and multiplayer), refreshed about every 10 s in the air.

- **Traffic information:** Departure, Center and Approach call traffic on their own once you have checked in, when an aircraft within 15 nm and 1500 ft will pass within 5 nm in the next three minutes – each aircraft once, at most one call a minute, never into an open readback:
  > "Lufthansa four alpha bravo, traffic twelve o'clock, twelve miles, opposite direction, Airbus three twenty, one thousand feet above"
- **Asking:** "request traffic information", "any traffic for us?", "confirm traffic twelve o'clock", "traffic below?" → the matching aircraft (clock position ± 1 hour, above/below), otherwise "no reported traffic". The flow doesn't move on – asking at the holding point gets no takeoff clearance, and an open readback stays open.
- **Sequence on final:** if someone is ahead of you on final when you call the tower, the tower says "number two, follow the A320 on 3 miles final" and clears you to land once that aircraft has landed (at the latest 2 nm out).
- "looking out", "traffic in sight", "negative contact" are acknowledgements and get no answer.
- Oceanic has no radar and gives no traffic information.

Traffic in MSFS isn't really separated – ATC reports what the scan shows, including conflicts a real controller would have resolved long ago. Rules: `trafficInfo`, `trafficInfoRangeNm`, `trafficInfoAltitudeFt`, `trafficInfoMissNm`, `trafficSequencing` (see [Customizing](#customizing)).

## Squawk and frequency checks

MSFS sometimes changes the transponder and COM1 by itself. Duopilo watches both:

- squawk differs for more than 15 s in flight → "check squawk, should be two three four zero"
- COM1 not on the station's frequency (60 s grace after a handoff) → "check frequency, should be Montreal Center one three two decimal two five"

Repeated every 2 min, at most three times. **Duopilo never resets anything itself:** `get_atc_controller` lists the fix as `prepare_action` steps, a caution `ATC_SQUAWK`/`ATC_FREQUENCY` appears, and the pilot confirms. `Duopilo:Atc:SquawkFromSim = true` instead accepts a discrete code MSFS sets as the assigned squawk.

## Wrong station

Calling a station other than the responsible one:

- the station that handed you off within 15 min → repeats the handoff,
- otherwise → "this is Montreal Center, say again".

## Emergencies

"**Mayday**" or "**Pan Pan**" always comes first – with any, the wrong or no station:

> "Lufthansa four alpha bravo, Langen Radar, roger Mayday, squawk seven seven zero zero, say intentions"

- After your next call ATC asks once for souls on board and fuel endurance.
- No automatic handoffs, step climbs or squawk/frequency checks while it lasts; your requests are granted ("returning to Frankfurt", "nearest suitable airport", "request descent FL120", "request landing runway 25R").
- **Squawk 7700 without a call:** "confirm squawking seven seven zero zero, say nature of emergency".
- **End:** "cancel Mayday" / "cancel Pan Pan" → previous squawk, normal flow resumes.
- 7600 and 7500 aren't handled yet.

## Sim time

ATC only works while the sim is running – not connected, no flight loaded or paused: ATC is silent and its timers stand still.

## Voices

Each station gets a random Kokoro voice from `voices.pool` per flight – different ones where possible, never the first officer's. Fixed voices under `voices.stations` in the templates, e.g. `"EDDF/tower": "bf_emma"`, `"FIR/EGGX": "bm_daniel"`, `"delivery": "af_heart"`.

Only the tray instance speaks; through Claude Desktop's stdio instance replies are text only.

## Session persistence

The running session is saved continuously to `%LOCALAPPDATA%\Duopilo\atc-session.json`. After a restart or crash it continues in the same flight (same SimBrief plan) as soon as MSFS is connected – no new initial call. `atc_control stop`/`restart` or a new SimBrief plan start fresh. Settings: `Duopilo:Atc:SessionFile` (empty = off), `SessionMaxAgeHours` (24).

## Where the data comes from

| Data | Source |
|---|---|
| Callsign, destination, SID/STAR, runways, cruise level, FIRs | SimBrief OFP |
| Spoken airline callsign | OpenFlights (~5,000 airlines) + `telephony` exceptions in the templates; unknown → ICAO code spelled |
| FIR boundaries | VAT-Spy (VATSIM) |
| Sectors and en-route frequencies | VATSIM sector data (VATGlasses) |
| Airport frequencies | MSFS facilities API, otherwise templates or a placeholder |
| Altitude, position, wind, QNH, time | SimConnect; QNH at the destination from the OFP METAR |

Frequencies are VATSIM's and may differ from MSFS ATC. `get_atc_controller` shows the source of every frequency (`frequencySource`) and the stations along the route.

Taxi routes aren't computed yet – the templates use a variant without them. Set gate or holding point by hand with `atc_set_value`.

Found a wrong FIR boundary, frequency or callsign? **Report a problem** in the dashboard – see [Troubleshooting](Troubleshooting#report-a-problem).

## Customizing

Templates, airline callsigns, station names, frequencies, rules and voices: [ATC Templates](ATC-Templates).

## Settings

`Duopilo:Atc` in `appsettings.Local.json`: `Enabled`, `TemplatesFile`, `Speak`, `SquawkFromSim`, `SessionFile`, `SessionMaxAgeHours`. State in the browser: `http://127.0.0.1:5155/atc/controller`.

`Duopilo:FollowMe`: `Enabled` (default on), `Model` (vehicle title from its `sim.cfg`, default `Duopilo_FollowMe_Licht`), `HeightFt` (model reference point above the ground, default 0).
