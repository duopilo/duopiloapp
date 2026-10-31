**English** | [Deutsch](DE-Verkehr)

# Traffic

`get_traffic` lists aircraft and helicopters nearby – AI traffic, live traffic and multiplayer, as far as enabled in the sim. Your own aircraft is excluded. The voice loop answers "Traffic check" the same way.

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `radiusNm` | 40 | search radius, max. 108 NM (SimConnect's limit) |
| `limit` | 10 | at most this many targets, closest first (1–50) |
| `airborneOnly` | false | leave out aircraft on the ground |

## Per target

`ident`/`callsign`, `atcId`, `type`, `distanceNm`, `clock`, `relativeBearingDeg`, `bearingTrueDeg`, `altitudeFt`, `altitudeDiffFt` (+ = above), `verticalTrend`, `groundSpeedKt` with `groundSpeedSource`, `headingTrueDeg`/`headingMagDeg`, `onGround`. The closest target has `closest: true` and a `callout`:

```
Traffic, 2 o'clock, 5 miles, 1000 feet above, descending
```

## How values are derived

| Value | Notes |
|---|---|
| **Callsign** | airline + flight number if both are set (`Lufthansa 123`), otherwise the registration. Internal live-traffic numbers like `1432126` are not used as callsigns |
| **Type** | localization keys (`ATCCOM.AC MODEL C441.0.tts`) are cleaned up to `C441` |
| **Clock** | relative to your heading, 30° each |
| **Distance** | horizontal (great circle) |
| **Altitude difference** | like TCAS: against your **pressure altitude** (1013.25 hPa), because live traffic reports flight levels. `"AltitudeReference": "True"` switches to true altitude |
| **Ground speed** | computed from position changes (5–120 s apart), because the SimVar is useless for live traffic; until then the SimVar with `groundSpeedSource: "simvar"` |
| **Trend** | from vertical speed (±500 ft/min); if a target reports 0, derived from altitude changes |

No other aircraft → `status: "no_traffic"` (AI traffic or multiplayer usually off in the sim). MSFS not answering → `no_response`.

## Traffic warning `TRAFFIC_PROXIMITY`

While the aircraft is flying, Duopilo scans within 6 NM every 3 s. An airborne target closer than 3 NM and within ±1000 ft triggers a caution with callout; then 60 s quiet. It rests below 500 ft AGL, on the ground and while paused.

```jsonc
"Duopilo": { "Monitoring": { "Traffic": {
  "ProximityRangeNm": 3, "ProximityAltitudeFt": 1000, "ProximityCooldownSec": 60, "ScanIntervalSec": 3,
  "AltitudeReference": "Pressure", "MessageLog": true
} } }
```

Off: `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]`.

> This is **not TCAS**: no resolution advisory, no prediction of the closest point of approach – just distance and altitude right now. Real TCAS RAs from the aircraft's FMA are called out separately (`TCAS_RA`).

## Separate SimConnect connection

Traffic uses its own SimConnect connection directly via the native `SimConnect.dll` and only reads what each message actually contains (`dwSize`). This replaced the managed library for traffic after crashes in test flights. The background scan only queries numbers; callsign and type are known if `get_traffic` saw the target in the last 15 minutes.

Every response is logged to `%LOCALAPPDATA%\Duopilo\logs\traffic-messages.csv` (~250 KB per flight hour, rotated at 10 MB); `get_traffic` → `messageCheck` summarizes anomalies. Off: `"MessageLog": false`.

## Traffic test (developer)

First step towards Duopilo's own traffic: `traffic_test_start` spawns an AI aircraft (`DUO01`–`DUO04`) that flies a final approach by waypoints, lands and stops on the runway – up to 4 at once, one per runway (e.g. a parallel approach on 05L and 05R). Off by default – switch it on with `"TrafficTest": { "Enabled": true }` in `appsettings.Local.json`.

- **A1:** spawn 5 NM out with the gear down. **A2:** spawn 15 NM out, gear down at 10 NM.
- The waypoints of the last mile lie up to 100 ft below the glide path (`finalLowFt`), because the AI levels off on short final.
- `traffic_test_status` shows the evaluation (touchdown, flare, floating, speed, stop point), samples go to `%LOCALAPPDATA%\Duopilo\traffic-tests\`.
- `traffic_test_stop` removes the aircraft, with `runway` only that one (also after 15 minutes and when Duopilo exits).

Background and plan: [research on own traffic](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md).
