**English** | [Deutsch](DE-ATC-Vorlagen)

# ATC Templates

All texts of the [Duopilo ATC](ATC) are in `atc-templates.json` next to `Duopilo.exe`.

## Your own changes

Put them into **`%LOCALAPPDATA%\Duopilo\atc-templates.json`** – updates never overwrite it. Duopilo lays it over the bundled file:

- objects are merged key by key (your entries win),
- lists such as `variants` are replaced entirely,
- an entry set to `null` removes a bundled one.

So the file only needs the differences:

```json
{ "telephony": { "XYZ": "Testair" } }
```

Then run `atc_control` with `reload` (or restart Duopilo). An older full copy keeps working – Duopilo upgrades it to the current format (`version` 2) on load and notes it under `templateNotes`.

## Transmissions

```jsonc
"delivery.clearance": {
  "variants": [
    "{callsign}, cleared to {dest}, {sid} departure, runway {runway}, climb {initialAlt}, squawk {squawk}",
    "{callsign}, cleared to {dest}, runway {runway}, climb {initialAlt}, squawk {squawk}"   // without SID
  ],
  "readback": [ "sid", "initialAlt", "squawk" ]
}
```

- **`variants`:** the first one whose variables are all known wins.
- **`readback`:** values the pilot must read back.
- **`"from": "pilot"`** entries are the expected pilot transmissions – hints for you and Claude, not mandatory.
- **`corrections`:** what is said after "negative" for each readback item.

## Sections

| Section | Purpose |
|---|---|
| `telephony` | exceptions only: ICAO code → callsign where OpenFlights is missing or wrong (`"EJU": "Alpine"`, `"THY": "Turkish"`) |
| `airports` | callsign name, first climb altitude, station names (`"stations": { "approach": "SoCal Approach" }`), fixed frequencies (`"frequencies": { "tower": "119.900" }`) |
| `firs` | unit name per FIR, `frequency` for FIRs without sector data, `"oceanic": true`, `oceanicFir` for split FIRs (Gander `CZQX` → `CZQO`), `members` (units whose sectors count within the FIR) |
| `firSuffixes` | ICAO prefix → station type for FIRs without an entry (`"K": "Center"`, `"ED": "Radar"`, `"": "Control"`) – longest prefix wins |
| `stationTypes` | names of station types, placeholder frequencies |
| `rules` | thresholds of the state machine: altitudes, distances, times, `sectorMinNm`, step climbs and level requests, squawk/COM1 checks |
| `words` | what `{levelVerb}` says |
| `voices` | `pool` of station voices, fixed `stations` voices |

## Variables

`{callsign}` `{station}` `{dest}` `{sid}` `{star}` `{approach}` `{runway}` `{initialAlt}` `{squawk}` `{freq}` `{taxiRoute}` `{holdingPoint}` `{gate}` `{level}` `{speedKt}` `{entryFix}` `{heading}` `{wind}` `{qnh}` `{fix}` `{eta}` `{nextFix}` `{direction}` `{altitude}` `{nextCenter}` `{route}` `{time}` `{corrections}` `{levelVerb}` `{requested}` `{minutes}`

Emergencies: `{emergencySquawk}` `{emergency}` `{previousSquawk}` `{emergencyTarget}` `{emergencyRunway}`

## Validation

On load Duopilo checks for missing transmissions, unknown variables, readback items without a correction and **any mention of Mach**. Problems appear in `get_atc_controller` → `templateErrors` and in the log.

## Rules worth knowing

| Rule | Effect |
|---|---|
| `offerStepClimbs`, `stepClimbLeadNm` | ATC offers OFP step climbs (default 10 nm before) |
| `levelRequestUnablePercent`, `levelRequestDelayMinutes` | chance that ATC declines a level request, and when it clears it later (off by default) |
| `checkSquawk`, `checkFrequency` | squawk / COM1 checks on/off |
| `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls` | timing of those checks |
| `centerDescentFt` | descent clearance without a STAR (default FL110) |
| `sectorMinNm` | sectors touched for less than this (20 nm) get no own handoff |

## Airspace and airline data

In `data\` next to `Duopilo.exe` (sources, dates and licenses in [`data/README.md`](https://github.com/duopilo/duopiloapp/blob/main/src/Duopilo/data/README.md)):

| Folder | Content | License |
|---|---|---|
| `vatspy/` | FIR boundaries and names (VAT-Spy) | CC BY-SA 4.0 |
| `sectors/` | en-route sectors with altitude band, frequency, callsign (VATGlasses, via `tools/import_sectors.py`) | CC BY-NC-SA 4.0 – non-commercial only |
| `openflights/` | airlines with telephony callsign | ODbL 1.0 |

A newer copy in `%LOCALAPPDATA%\Duopilo\data\…` (same folders) takes precedence and survives updates; then `atc_control reload`.

Regenerating the sectors:

```
git clone --depth 1 https://github.com/lennycolton/vatglasses-data
python tools/import_sectors.py --vatglasses vatglasses-data
```
