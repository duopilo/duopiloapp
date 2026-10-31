**English** | [Deutsch](DE-Flugzeugprofile)

# Aircraft Profiles

Study-level aircraft keep much of their state (AP, FMA, autobrake, ECAM, fuel flow) not in standard SimVars but in their own **`L:` variables**. A profile in `profiles\*.json` tells Duopilo where the values come from and which inputs exist. Without a matching profile Duopilo falls back to standard SimVars.

## Supported aircraft

| Aircraft | Reading | ECAM text | Inputs | Autoflight callouts |
|---|---|---|---|---|
| FlyByWire A32NX | extensive, mostly verified | yes | 53 actions | most bindings present |
| Headwind A330-900neo | extensive | yes | 56 actions | TCAS RA, A/THR off, FMA verified |
| FlyByWire A380X | basic | – | – | unverified |
| iniBuilds A321LR | detection, limits, generic FCU values | – | – | missing (L: variables unknown) |

Detailed per-value status: [docs/aircraft.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.md). Live: `get_aircraft_profile` and Dashboard → *Setup and diagnostics*.

## Format

```jsonc
{
  "name": "FlyByWire A32NX",
  "match": [
    { "path": [ "FlyByWire", "A320" ] },     // aircraft.cfg path contains all strings (checked first)
    { "title": [ "A32NX" ] }                 // or the TITLE contains all strings
  ],
  "bindings": {
    "ap1": { "name": "L:A32NX_AUTOPILOT_1_ACTIVE", "type": "bool", "verified": true },
    "fmaVertical": {
      "name": "L:A32NX_FMA_VERTICAL_MODE", "type": "enum", "verified": false,
      "values": { "14": "V/S", "22": "CLB" }
    },
    "fuelFlowKgh": { "name": "L:A32NX_ENGINE_FF:{i}", "type": "number" }   // {i} = engine 1–4
  },
  "ecam": { "format": "fbw-a32nx", "leftLines": "L:A32NX_EWD_LOWER_LEFT_LINE_{n}", "rightLines": "L:A32NX_EWD_LOWER_RIGHT_LINE_{n}", "lines": 7 },
  "limits": { "vmoKt": 350, "vleKt": 280, "vfeKt": { "1": 230, "2": 200, "3": 185, "4": 177 } },
  "variables": [ { "key": "vapp", "name": "L:A32NX_SPEEDS_VAPP", "unit": "number" } ]
}
```

| Section | Purpose |
|---|---|
| `match` | detection by `aircraft.cfg` path or title |
| `bindings` | fixed keys that tools and monitoring understand (below) |
| `electrical`, `fuel` | extra values for `get_systems` |
| `limits` | `vmoKt`, `vleKt`, `vloKt`, `vfeKt` per setting, `landingFlaps`, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource` – used by monitoring and input interlocks |
| `verification` | optional own variables/thresholds for reading back gear and flaps |
| `ecam` | where ECAM lines are and which text table to use |
| `variables` | free extra values that `get_aircraft_profile` outputs raw |
| `actions` | input actions: events, variables, readback, check, `requires` – see [Input Module](Input-Module) |

### Binding keys

`ap1`, `ap2`, `flightDirector`, `athrStatus`, `athrMode`, `fmaLateral`, `fmaLateralArmed`, `fmaVertical`, `fmaVerticalArmed`, `fmgcDiscreteWord1`, `fmgcPhase`, `fmAltitudeConstraint`, `locDeviation`, `gsDeviation`, `autobrake`, `masterWarning`, `masterCaution`, `speedTarget`, `speedManaged`, `selectedSpeed`, `machMode`, `selectedAltitude`, `selectedHeading`, `selectedHeadingDashes`, `vls`, `v1`, `vr`, `v2`, `flexTemp`, `tdReached`, `fmsToWptDistance`, `fmsToWptBearing`, `fuelFlowKgh`, `egtC`, `parkingBrake`, `spoilersArmed`, `flaps`

### Types

`number`, `bool`, `enum` (value → text), `bitmask` (bit → text), `string`, `arinc429` (FBW data word, only valid with Normal Operation). `scale` converts units (lbs/h → kg/h = 0.45359237).

> **Unit trap:** some `L:` variables store speeds in m/s internally (74.6 instead of 145 kt). Set `"unit": "knots"` – don't convert by hand.

### Texts

Profiles are language-neutral. Refusal texts of `requires` conditions reference a key (`"messageKey": "profile.requires.trkFpaMode"`) from the language files; your own profiles may use `"message": "literal text"`.

## Your own profiles

Put your profile into the `profiles` folder of the program (under `C:\Program Files` this needs admin rights) and call `reload_profiles` (or tray → *Reload profiles*). Updates keep your own profiles; the installer backs up changed bundled ones to `backup\<date>\`.

## Finding L: variables

1. In MSFS Developer Mode open the aircraft's **Behaviors → LocalVariables** window. It lists all `L:` variables with current values.
2. Switch something in the cockpit (e.g. NAV on the FCU) and watch which variable changes.
3. Cross-check with `read_simvar` or `http://127.0.0.1:5155/read?name=L:NAME`.
4. Add it to the profile (`"verified": true` once checked) and call `reload_profiles`.

> SimConnect reads an `L:` variable that doesn't exist as **0**. A 0 doesn't prove the name is right – always cross-check with a state change.

Every variable has its own SimConnect data definition: an unknown name only loses that one value.

## ECAM plain text

The A32NX only writes codes (e.g. `220000501`) into the EWD variables. Duopilo downloads the text table from the FlyByWire repository (`EwdMessages.ts`, GPL-3.0; Headwind's own version for the A339) on first use, caches it in `%LOCALAPPDATA%\Duopilo\ecam\` and renews it every 14 days. The table is deliberately not part of the repo.

## Contributing a profile

Profiles for more aircraft are very welcome – see [Development](Development#contributing). Please mark only values you checked in the sim as `"verified": true`.
