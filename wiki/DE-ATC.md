[English](ATC) | **Deutsch**

# ATC

Duopilo bringt ein eigenes ATC mit: eine **Zustandsmaschine mit festen Textvorlagen – kein Sprachmodell**. Du funkst wie gewohnt; die [Sprachschleife](DE-Sprachschleife) (oder Claude über `atc_transmit`) gibt deinen Funkspruch weiter, und Duopilo antwortet als die zuständige Stelle – jede Station mit eigener Kokoro-Stimme.

Die Phraseologie ist Englisch im ICAO-Stil: Zahlen Ziffer für Ziffer („squawk two two zero five“), Geschwindigkeiten immer in Knoten, nie Mach, Fixe und Funkfeuer wie unter [Sprachausgabe](DE-Sprachausgabe#aussprache) beschrieben.

**Voraussetzung** ist ein SimBrief-Plan ([SimBrief & Flugplan](DE-SimBrief-und-Flugplan)). ATC ist standardmäßig an (`Duopilo:Atc:Enabled`).

## Ablauf

Die Übergänge kommen aus deinen Funksprüchen und aus dem Flugzustand.

| Station | Was passiert | Übergabe |
|---|---|---|
| **Delivery** | IFR-Freigabe mit SID, Bahn, Steigflughöhe, Squawk; Readback wird geprüft | nach korrektem Readback: „readback correct, contact Ground“ |
| **Ground** | Push and start, Rollen zum Rollhalt (Readback Rollhalt/Bahn) | am Rollhalt stehen geblieben (8 s), oder du rufst „ready“ |
| **Tower** | Line up and wait, Startfreigabe mit Wind | ab 1500 ft AGL: „contact Departure“ |
| **Departure** | „identified, climb FL150“, optional direkt zum ersten Fix nach der SID | 2000 ft unter der freigegebenen Höhe → Center |
| **Center** | Steigflug auf Reiseflughöhe, Höhenanfragen, Stufensteigflüge aus dem OFP, Sinkflugfreigabe am TOD, QNH unter der Übergangshöhe | an jeder FIR-Grenze und Sektorgrenze |
| **Oceanic** | Freigabe ca. 30 min vor dem Eintrittspunkt, Positionsmeldungen an jedem Oceanic-Fix | am Austrittspunkt an die Domestic-Stelle |
| **Approach** | Sinkflug, STAR, erwarteter Anflug und Bahn, QNH; Anflugfreigabe ab 20 nm | ab 12 nm: „contact Tower“ |
| **Tower** | Landefreigabe mit Wind (spätestens 4 nm vorher) | nach der Landung unter 40 kt: „vacate via next exit, contact Ground“ |
| **Ground** | Rollen zum Gate | Ende, wenn das Flugzeug mit gesetzter Parkbremse steht |

- Kurzstrecken ohne Oceanic-Fixe überspringen Oceanic.
- Ohne Freigaben gestartet → ATC springt beim Startlauf zum Tower nach.
- Sitzung in der Luft gestartet → sie beginnt bei der Stelle, die dort zuständig ist; deine aktuelle Höhe gilt als freigegeben.

## Readback

Bei Freigaben prüft Duopilo die Werte, die in der Vorlage stehen (z. B. SID, Höhe, Squawk). Duopilo versteht Ziffern („2205“, „FL110“), gesprochene Zahlen („two two zero five“, „one two one decimal eight“) und das NATO-Alphabet.

Ist ein Wert falsch oder fehlt er, kommt „negative“ mit dem richtigen Wert – danach musst du nur diesen Wert noch einmal zurücklesen:

> „Lufthansa four alpha bravo, negative, squawk two two zero five“

- „Say again“ wiederholt den letzten Funkspruch.
- „Radio check“ / „how do you read“ → „read you five“, ohne dass sich etwas ändert.
- Reine Quittungen ohne Freigabe bekommen keine Antwort.

## Höhen im Reiseflug

Departure, Center und Oceanic beantworten Höhenanfragen:

| Du | ATC |
|---|---|
| „request climb flight level three four zero“ / „requesting FL340“ | „climb and maintain FL340“ |
| „request descent to one two thousand feet“ | unter der Übergangshöhe: „descend and maintain 12000 ft, altimeter …“ |
| „request higher“ / „request lower“ | nächste Stufe aus dem OFP, sonst 2000 ft |

- **Stufensteigflüge** aus dem OFP: 10 nm vor dem Stufenpunkt bietet ATC von sich aus „when ready climb and maintain FL340“ an.
- Optional lehnt ATC ab („unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes“, `levelRequestUnablePercent`, standardmäßig aus) und gibt später frei.
- Die freigegebene Höhe fließt auch in die FCU-Höhenprüfung des Eingabe-Moduls ein.

## Sinkflug

Am Top of Descent gibt Center „descend via ANJLL4 arrival“ frei, wenn das OFP eine STAR hat (freigegeben bis zur niedrigsten geplanten Höhe der STAR), sonst „descend FL110“. Endet die Freigabe unter der Übergangshöhe des Ziels, kommt das QNH dazu und gehört zum Readback.

## Squawk- und Frequenzprüfung

MSFS ändert Transponder und COM1 manchmal von selbst. Duopilo behält beides im Blick:

- Squawk weicht im Flug länger als 15 s ab → „check squawk, should be two three four zero“
- COM1 nicht auf der Frequenz der Station (nach einer Übergabe 60 s Schonfrist) → „check frequency, should be Montreal Center one three two decimal two five“

Der Hinweis kommt alle 2 min wieder, höchstens dreimal. **Duopilo setzt nie selbst zurück:** `get_atc_controller` listet die Korrektur als `prepare_action`-Schritte, eine Caution `ATC_SQUAWK`/`ATC_FREQUENCY` erscheint, und der Pilot bestätigt. Mit `Duopilo:Atc:SquawkFromSim = true` übernimmt Duopilo stattdessen einen vergebenen Code, den MSFS setzt, als zugewiesenen Squawk.

## Falsche Station

Rufst du eine andere Station als die zuständige:

- die Station, die dich in den letzten 15 min übergeben hat → wiederholt die Übergabe,
- sonst → „this is Montreal Center, say again“.

## Notfälle

„**Mayday**“ oder „**Pan Pan**“ geht immer vor – mit beliebiger, falscher oder ohne Stationsanrede:

> „Lufthansa four alpha bravo, Langen Radar, roger Mayday, squawk seven seven zero zero, say intentions“

- Nach deinem nächsten Funkspruch fragt ATC einmal nach Personen an Bord und Fuel Endurance.
- Solange der Notfall läuft, gibt es keine automatischen Übergaben, Stufensteigflüge oder Squawk-/Frequenzprüfungen; deine Wünsche werden erfüllt („returning to Frankfurt“, „nearest suitable airport“, „request descent FL120“, „request landing runway 25R“).
- **Squawk 7700 ohne Funkspruch:** „confirm squawking seven seven zero zero, say nature of emergency“.
- **Ende:** „cancel Mayday“ / „cancel Pan Pan“ → vorheriger Squawk, der normale Ablauf geht weiter.
- 7600 und 7500 werden noch nicht behandelt.

## Sim-Zeit

ATC arbeitet nur, solange der Sim läuft – nicht verbunden, kein Flug geladen oder pausiert: ATC schweigt, und seine Timer stehen.

## Stimmen

Jede Station bekommt pro Flug eine zufällige Kokoro-Stimme aus `voices.pool` – möglichst jede eine andere, nie die des First Officers. Feste Stimmen legst du unter `voices.stations` in den Vorlagen fest, z. B. `"EDDF/tower": "bf_emma"`, `"FIR/EGGX": "bm_daniel"`, `"delivery": "af_heart"`.

Gesprochen wird nur von der Tray-Instanz; über die stdio-Instanz von Claude Desktop kommen die Antworten nur als Text.

## Sitzung sichern

Die laufende Sitzung wird fortlaufend in `%LOCALAPPDATA%\Duopilo\atc-session.json` gesichert. Nach einem Neustart oder Absturz geht es im selben Flug (gleicher SimBrief-Plan) weiter, sobald MSFS verbunden ist – ohne neuen Erstanruf. `atc_control stop`/`restart` oder ein neuer SimBrief-Plan beginnen neu. Einstellungen: `Duopilo:Atc:SessionFile` (leer = aus), `SessionMaxAgeHours` (24).

## Woher die Daten kommen

| Daten | Quelle |
|---|---|
| Rufzeichen, Ziel, SID/STAR, Bahnen, Reiseflughöhe, FIRs | SimBrief-OFP |
| Gesprochenes Airline-Rufzeichen | OpenFlights (~5.000 Airlines) + `telephony`-Ausnahmen in den Vorlagen; unbekannt → ICAO-Code buchstabiert |
| FIR-Grenzen | VAT-Spy (VATSIM) |
| Sektoren und Streckenfrequenzen | VATSIM-Sektordaten (VATGlasses) |
| Flughafen-Frequenzen | Facilities-API von MSFS, sonst Vorlagen oder ein Platzhalter |
| Höhe, Position, Wind, QNH, Zeit | SimConnect; QNH am Ziel aus dem METAR des OFP |

Die Frequenzen sind die von VATSIM und können vom MSFS-ATC abweichen. `get_atc_controller` zeigt die Quelle jeder Frequenz (`frequencySource`) und die Stationen entlang der Route.

Rollrouten werden noch nicht berechnet – die Vorlagen nehmen eine Variante ohne sie. Gate oder Rollhalt setzt du von Hand mit `atc_set_value`.

Falsche FIR-Grenze, Frequenz oder falsches Rufzeichen gefunden? **Fehler melden** im Dashboard – siehe [Fehlersuche](DE-Fehlersuche#fehler-melden).

## Anpassen

Vorlagen, Airline-Rufzeichen, Stationsnamen, Frequenzen, Regeln und Stimmen: [ATC-Vorlagen](DE-ATC-Vorlagen).

## Einstellungen

`Duopilo:Atc` in `appsettings.Local.json`: `Enabled`, `TemplatesFile`, `Speak`, `SquawkFromSim`, `SessionFile`, `SessionMaxAgeHours`. Zustand im Browser: `http://127.0.0.1:5155/atc/controller`.
