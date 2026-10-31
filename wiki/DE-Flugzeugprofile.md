[English](Aircraft-Profiles) | **Deutsch**

# Flugzeugprofile

Study-Level-Flugzeuge legen vieles (AP, FMA, Autobrake, ECAM, Fuel Flow) nicht in Standard-SimVars ab, sondern in eigenen **`L:`-Variablen**. Ein Profil unter `profiles\*.json` sagt Duopilo, woher die Werte kommen und welche Eingaben es gibt. Ohne passendes Profil greift Duopilo auf die Standard-SimVars zurück.

## Unterstützte Flugzeuge

| Flugzeug | Lesen | ECAM-Text | Eingaben | Autoflug-Callouts |
|---|---|---|---|---|
| FlyByWire A32NX | umfangreich, größtenteils geprüft | ja | 53 Aktionen | Bindings größtenteils vorhanden |
| Headwind A330-900neo | umfangreich | ja | 56 Aktionen | TCAS RA, A/THR off, FMA geprüft |
| FlyByWire A380X | Grundwerte | – | – | ungeprüft |
| iniBuilds A321LR | Erkennung, Grenzwerte, generische FCU-Werte | – | – | fehlen (L:-Variablen unbekannt) |

Status je Wert im Detail: [docs/aircraft.de.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.de.md). Live: `get_aircraft_profile` und Dashboard → *Einrichtung und Diagnose*.

## Format

```jsonc
{
  "name": "FlyByWire A32NX",
  "match": [
    { "path": [ "FlyByWire", "A320" ] },     // aircraft.cfg-Pfad enthält alle Texte (wird zuerst geprüft)
    { "title": [ "A32NX" ] }                 // oder der TITLE enthält alle Texte
  ],
  "bindings": {
    "ap1": { "name": "L:A32NX_AUTOPILOT_1_ACTIVE", "type": "bool", "verified": true },
    "fmaVertical": {
      "name": "L:A32NX_FMA_VERTICAL_MODE", "type": "enum", "verified": false,
      "values": { "14": "V/S", "22": "CLB" }
    },
    "fuelFlowKgh": { "name": "L:A32NX_ENGINE_FF:{i}", "type": "number" }   // {i} = Triebwerk 1–4
  },
  "ecam": { "format": "fbw-a32nx", "leftLines": "L:A32NX_EWD_LOWER_LEFT_LINE_{n}", "rightLines": "L:A32NX_EWD_LOWER_RIGHT_LINE_{n}", "lines": 7 },
  "limits": { "vmoKt": 350, "vleKt": 280, "vfeKt": { "1": 230, "2": 200, "3": 185, "4": 177 } },
  "variables": [ { "key": "vapp", "name": "L:A32NX_SPEEDS_VAPP", "unit": "number" } ]
}
```

| Abschnitt | Zweck |
|---|---|
| `match` | Erkennung über den `aircraft.cfg`-Pfad oder den Titel |
| `bindings` | feste Schlüssel, die Tools und Monitoring verstehen (siehe unten) |
| `electrical`, `fuel` | zusätzliche Werte für `get_systems` |
| `limits` | `vmoKt`, `vleKt`, `vloKt`, `vfeKt` je Stellung, `landingFlaps`, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource` – für das Monitoring und die Sperren des Eingabe-Moduls |
| `verification` | optional eigene Variablen/Schwellen zum Nachlesen von Fahrwerk und Klappen |
| `ecam` | wo die ECAM-Zeilen stehen und welche Texttabelle gilt |
| `variables` | freie Zusatzwerte, die `get_aircraft_profile` roh ausgibt |
| `actions` | Eingabe-Aktionen: Events, Variablen, Readback, Prüfung, `requires` – siehe [Eingabe-Modul](DE-Eingabe-Modul) |

### Binding-Schlüssel

`ap1`, `ap2`, `flightDirector`, `athrStatus`, `athrMode`, `fmaLateral`, `fmaLateralArmed`, `fmaVertical`, `fmaVerticalArmed`, `fmgcDiscreteWord1`, `fmgcPhase`, `fmAltitudeConstraint`, `locDeviation`, `gsDeviation`, `autobrake`, `masterWarning`, `masterCaution`, `speedTarget`, `speedManaged`, `selectedSpeed`, `machMode`, `selectedAltitude`, `selectedHeading`, `selectedHeadingDashes`, `vls`, `v1`, `vr`, `v2`, `flexTemp`, `tdReached`, `fmsToWptDistance`, `fmsToWptBearing`, `fuelFlowKgh`, `egtC`, `parkingBrake`, `spoilersArmed`, `flaps`

### Typen

`number`, `bool`, `enum` (Wert → Text), `bitmask` (Bit → Text), `string`, `arinc429` (FBW-Datenwort, gilt nur bei Normal Operation). `scale` rechnet Einheiten um (lbs/h → kg/h = 0.45359237).

> **Einheitenfalle:** Manche `L:`-Variablen speichern Geschwindigkeiten intern in m/s (74,6 statt 145 kt). Setz `"unit": "knots"` – nicht von Hand umrechnen.

### Texte

Profile sind sprachneutral. Ablehnungstexte von `requires`-Bedingungen verweisen auf einen Schlüssel aus den Sprachdateien (`"messageKey": "profile.requires.trkFpaMode"`); eigene Profile dürfen auch `"message": "literal text"` nutzen.

## Eigene Profile

Leg dein Profil in den Ordner `profiles` des Programms (unter `C:\Program Files` brauchst du dafür Adminrechte) und ruf `reload_profiles` auf (oder Tray → *Profile neu laden*). Updates behalten deine eigenen Profile; der Installer sichert geänderte mitgelieferte Profile nach `backup\<date>\`.

## L:-Variablen finden

1. Im Developer Mode von MSFS das Fenster **Behaviors → LocalVariables** des Flugzeugs öffnen. Dort stehen alle `L:`-Variablen mit ihren aktuellen Werten.
2. Im Cockpit etwas umschalten (z. B. NAV am FCU) und schauen, welche Variable sich ändert.
3. Gegenprobe mit `read_simvar` oder `http://127.0.0.1:5155/read?name=L:NAME`.
4. Ins Profil eintragen (`"verified": true`, sobald geprüft) und `reload_profiles` aufrufen.

> Eine `L:`-Variable, die es nicht gibt, liest SimConnect als **0**. Eine 0 beweist also nicht, dass der Name stimmt – immer mit einer Zustandsänderung gegenprüfen.

Jede Variable hat ihre eigene SimConnect-Data-Definition: Ein unbekannter Name kostet nur diesen einen Wert.

## ECAM-Klartext

Das A32NX schreibt nur Codes (z. B. `220000501`) in die EWD-Variablen. Duopilo lädt die Texttabelle beim ersten Bedarf aus dem FlyByWire-Repository (`EwdMessages.ts`, GPL-3.0; für den A339 die eigene Fassung von Headwind), speichert sie in `%LOCALAPPDATA%\Duopilo\ecam\` und erneuert sie alle 14 Tage. Die Tabelle liegt bewusst nicht im Repo.

## Profil beisteuern

Profile für weitere Flugzeuge sind sehr willkommen – siehe [Entwicklung](DE-Entwicklung#mitmachen). Bitte markiere nur Werte als `"verified": true`, die du im Sim geprüft hast.
