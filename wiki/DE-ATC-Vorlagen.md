[English](ATC-Templates) | **Deutsch**

# ATC-Vorlagen

Alle Texte des [Duopilo-ATC](DE-ATC) stehen in `atc-templates.json` neben der `Duopilo.exe`.

## Eigene Änderungen

Die gehören in **`%LOCALAPPDATA%\Duopilo\atc-templates.json`** – Updates überschreiben diese Datei nie. Duopilo legt sie über die mitgelieferte Datei:

- Objekte werden je Schlüssel gemischt (deine Einträge gewinnen),
- Listen wie `variants` werden ganz ersetzt,
- ein Eintrag mit `null` entfernt einen mitgelieferten.

Die Datei braucht also nur die Abweichungen:

```json
{ "telephony": { "XYZ": "Testair" } }
```

Danach `atc_control` mit `reload` aufrufen (oder Duopilo neu starten). Eine ältere vollständige Kopie funktioniert weiter – Duopilo hebt sie beim Laden auf das aktuelle Format (`version` 2) und vermerkt das unter `templateNotes`.

## Funksprüche

```jsonc
"delivery.clearance": {
  "variants": [
    "{callsign}, cleared to {dest}, {sid} departure, runway {runway}, climb {initialAlt}, squawk {squawk}",
    "{callsign}, cleared to {dest}, runway {runway}, climb {initialAlt}, squawk {squawk}"   // ohne SID
  ],
  "readback": [ "sid", "initialAlt", "squawk" ]
}
```

- **`variants`:** Die erste, deren Variablen alle bekannt sind, gewinnt.
- **`readback`:** Werte, die der Pilot zurücklesen muss.
- **`"from": "pilot"`**-Einträge sind die erwarteten Pilotensprüche – Hinweise für dich und Claude, keine Pflicht.
- **`corrections`:** was nach „negative“ je Readback-Punkt gesagt wird.

## Abschnitte

| Abschnitt | Zweck |
|---|---|
| `telephony` | nur Ausnahmen: ICAO-Code → Rufzeichen, wo OpenFlights fehlt oder falsch liegt (`"EJU": "Alpine"`, `"THY": "Turkish"`) |
| `airports` | Rufname, erste Steigflughöhe, Stationsnamen (`"stations": { "approach": "SoCal Approach" }`), feste Frequenzen (`"frequencies": { "tower": "119.900" }`) |
| `firs` | Name der Kontrollstelle je FIR, `frequency` für FIRs ohne Sektordaten, `"oceanic": true`, `oceanicFir` für geteilte FIRs (Gander `CZQX` → `CZQO`), `members` (Stellen, deren Sektoren in der FIR mitzählen) |
| `firSuffixes` | ICAO-Präfix → Stationsart für FIRs ohne Eintrag (`"K": "Center"`, `"ED": "Radar"`, `"": "Control"`) – der längste Präfix gewinnt |
| `stationTypes` | Namen der Stationsarten, Platzhalter-Frequenzen |
| `rules` | Schwellwerte der Zustandsmaschine: Höhen, Entfernungen, Zeiten, `sectorMinNm`, Stufensteigflüge und Höhenanfragen, Squawk-/COM1-Prüfung |
| `words` | was `{levelVerb}` sagt |
| `voices` | `pool` der Stationsstimmen, feste Stimmen unter `stations` |

## Variablen

`{callsign}` `{station}` `{dest}` `{sid}` `{star}` `{approach}` `{runway}` `{initialAlt}` `{squawk}` `{freq}` `{taxiRoute}` `{holdingPoint}` `{gate}` `{level}` `{speedKt}` `{entryFix}` `{heading}` `{wind}` `{qnh}` `{fix}` `{eta}` `{nextFix}` `{direction}` `{altitude}` `{nextCenter}` `{route}` `{time}` `{corrections}` `{levelVerb}` `{requested}` `{minutes}`

Notfälle: `{emergencySquawk}` `{emergency}` `{previousSquawk}` `{emergencyTarget}` `{emergencyRunway}`

## Prüfung

Beim Laden prüft Duopilo auf fehlende Funksprüche, unbekannte Variablen, Readback-Punkte ohne Korrektur und **jede Erwähnung von Mach**. Probleme stehen in `get_atc_controller` → `templateErrors` und im Log.

## Wichtige Regeln

| Regel | Wirkung |
|---|---|
| `offerStepClimbs`, `stepClimbLeadNm` | ATC bietet Stufensteigflüge aus dem OFP an (standardmäßig 10 nm vorher) |
| `levelRequestUnablePercent`, `levelRequestDelayMinutes` | Wahrscheinlichkeit, dass ATC eine Höhenanfrage ablehnt, und wann es später freigibt (standardmäßig aus) |
| `checkSquawk`, `checkFrequency` | Squawk-/COM1-Prüfung an/aus |
| `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls` | Timing dieser Prüfungen |
| `centerDescentFt` | Sinkflugfreigabe ohne STAR (Standard FL110) |
| `sectorMinNm` | Sektoren, die die Route kürzer als dieser Wert (20 nm) berührt, bekommen keine eigene Übergabe |

## Luftraum- und Airline-Daten

Liegen in `data\` neben der `Duopilo.exe` (Quellen, Stand und Lizenzen in [`data/README.md`](https://github.com/duopilo/duopiloapp/blob/main/src/Duopilo/data/README.md)):

| Ordner | Inhalt | Lizenz |
|---|---|---|
| `vatspy/` | FIR-Grenzen und -Namen (VAT-Spy) | CC BY-SA 4.0 |
| `sectors/` | Streckensektoren mit Höhenband, Frequenz, Rufname (VATGlasses, über `tools/import_sectors.py`) | CC BY-NC-SA 4.0 – nur nicht-kommerziell |
| `openflights/` | Airlines mit Telephony-Rufzeichen | ODbL 1.0 |

Ein neuerer Stand in `%LOCALAPPDATA%\Duopilo\data\…` (gleiche Ordner) hat Vorrang und übersteht Updates; danach `atc_control reload`.

Sektoren neu erzeugen:

```
git clone --depth 1 https://github.com/lennycolton/vatglasses-data
python tools/import_sectors.py --vatglasses vatglasses-data
```
