# Follow-me: Ablauf und Test im Sim

Stand: 05.11.2026. Status: Duopilo-Seite gebaut (ATC, Route, Fahrlogik, Tests ohne Sim), dazu die SimConnect-Fixes aus den
Feuerwehr-Tests (siehe [Feuerwehr: Stand und Test](feuerwehr-test.md)). Der Test mit dem eigenen Modell im Sim ist offen.

## Grundsatz

Follow-me kommt **nur auf Anfrage**, wie an den meisten echten Flughäfen. ATC bietet es nie von selbst an, auch nicht in den erwarteten Pilotensprüchen (`get_atc_controller`).

## Funk

| Pilot | Ground |
| --- | --- |
| „… runway 23R vacated, request follow-me“ (auch später beim Rollen) | „hold position, follow-me is on the way“ |
| – (Fahrzeug steht gewendet vor dem Flugzeug) | „follow the follow-me to gate C2“ (Readback: Gate) |
| „cancel follow-me“ / „follow-me not required“ | wartend: normale Rollfreigabe; beim Hinterherrollen: „roger“ |
| Anfrage vor dem Abflug | „negative, follow-me not available“ |

Kein Fahrzeug (Paket fehlt, keine Route, Gate gleich voraus, stdio-Instanz, nach `followMeTimeoutSeconds` = 240 s nicht da): „follow-me not available, taxi to gate …“. Vorlagen: `ground.followMe*` in `atc-templates.json`.

## Fahrzeug

- Modell: `Duopilo:FollowMe:Model`, Standard `Duopilo_FollowMe_Licht` (Variante mit schaltbarem Licht). Nach dem Spawn setzt Duopilo `L:1:DUOPILO_RUNDUMLICHT` und `L:1:DUOPILO_FAHRLICHT` auf 1, dazu die Events `BEACON_LIGHTS_SET`/`LANDING_LIGHTS_SET` (nach 2, 3 und 5 s, weil der Sim Werte beim Laden verwirft). Die SimVars `A:LIGHT …` nicht: Bodenfahrzeuge lehnen sie ab (DATA_ERROR, Feuerwehr-Test EDDL 04.11.). Jedes Fahrzeug hat eigene Data Definitions für seine Z-Variablen, beim Entfernen freigegeben.
- Erzeugt mit `AICreateSimulatedObject_EX1` (Rückfall ohne Livery), bewegt wie die KI-Flugzeuge der Verkehrstests: eingefroren, Lage jeden Frame gesetzt, aber ohne `AIReleaseControl`.
- Boden: unter dem eigenen Flugzeug gemessen, danach aus der Abfrage des Fahrzeugs (Höhe minus Höhe über Grund), höchstens 60 ft vom Erwarteten. In den ersten 3 s nach dem Spawn springt es sofort auf den gemessenen Boden, danach gleitet es mit 6 ft/s. `Duopilo:FollowMe:HeightFt` hebt den Bezugspunkt des Modells an, falls es einsinkt.

## Route und Fahrt

1. Start: der Rollweg-Abschnitt, an dem das Flugzeug steht (höchstens 60 m daneben), Knoten in Blickrichtung.
2. Gate: das von ATC (von Hand gesetzt), sonst ein freies, passendes (wie bei den Verkehrstests) – das nächste unter den ersten zehn mit Route. Unter 200 m bis zur Einfahrt kein Follow-me.
3. Das Fahrzeug startet auf der Route bis 600 m vor dem Treffpunkt (sonst an der Einfahrt zum Gate), fährt entgegen und wendet vor dem Flugzeug (Radius 6,5 m). Treffpunkt und Abstand vorweg: 35 m (Light), 60 m (Medium), 80 m (Heavy), 95 m (Super) vom Bezugspunkt des Flugzeugs entlang der Route.
4. Vorweg höchstens 30 km/h, Abstandsregelung: Geschwindigkeit des Flugzeugs plus 0,25 m/s je Meter zu wenig Abstand; steht das Flugzeug, wartet es.
5. Am Gate 40 m an der Einfahrt vorbei, warten, bis das Flugzeug dort ist (höchstens 2 Minuten), 150 m weiter, dann entfernt. In einer Sackgasse fährt es quer zur Seite weg vom Gate.

## Test im Sim

1. `follow_me` mit `action: request` am Boden auf einem Rollweg (ohne Funk, beliebiger Flughafen): Spawnt das Modell? Folgt es der Route, wendet es sauber, steht es auf dem Boden (nicht schwebend, nicht eingesunken)?
2. Leuchten Rundumlicht und Fahrlicht? `L:1:` über SimConnect greift (Feuerwehr, EDDK 05.11.). Im Log keine `DATA_ERROR`.
3. Losrollen: Fährt es vorweg, hält es den Abstand, wartet es beim Anhalten? Abstand und Phase in `follow_me` (status).
4. Am Gate: fährt es vorbei und verschwindet?
5. Danach der ganze Ablauf im Funk nach einer Landung (EDDL).

Offen: ob `AIReleaseControl` für Bodenfahrzeuge nötig ist (bewusst weggelassen). Die Abfrage `PLANE ALT ABOVE GROUND` liefert für Bodenfahrzeuge Werte (Feuerwehr, EDDK 05.11.).

## Follow-me für den eigenen KI-Verkehr (in Arbeit)

Auch Landungen mit Rollen (A3, A4) sollen ein Follow-me bekommen können – ebenfalls nur „auf Anfrage“, also nicht jede.

Stand im Branch `feature/follow-me-ai`: `traffic_test_start` mit `followMe: true` (A3/A4) – Plan aus der Rollroute (`FollowMePlan.FromArrival`), Halt des Flugzeugs hinter dem Fahrzeug (nie näher als 60 % des Abstands; bevor das Fahrzeug gewendet hat, nur bis zur Stelle, an der die Bahn verlassen ist), Halt des Fahrzeugs vor gekreuzten Bahnen mit Verkehrsabfrage, höchstens zwei Fahrzeuge je Flughafen (`FollowMeFleet`, mit dem des Piloten zusammen), Status `taxi.followMe`, Ereignisse `follow_me_*`. Noch offen: Quote je Flughafen, Test im Sim.

**Wann**
- Quote je Flughafen in `data\traffic\airport-rules.json` (`followMePercent`), entschieden bei der Prüfung 2 NM vor der Schwelle (`RecheckParkingAsync`). Zuerst 0 – nur im Test mit `followMe: true` an A3/A4.
- Eher bei Allgemeiner Luftfahrt, Außen- und Frachtpositionen als an Gates mit Andockführung.

**Ablauf**
1. Bei 2 NM fährt das Fahrzeug los und wartet gewendet hinter dem Abrollweg, an dem das Flugzeug die Bahn verlässt (Übergabepunkt der Rollroute).
2. Weg des Fahrzeugs = Rollweg des Flugzeugs: `FollowMePlan` aus der fertigen `TaxiRoute` statt aus dem Rollweg-Netz neu – beide fahren dieselbe Linie.
3. Das Flugzeug folgt mit dem vorhandenen Halt hinter Verkehr: `TaxiKinematics.TrafficStopS` = Stelle des Fahrzeugs minus Abstand. Das Fahrzeug regelt wie beim Piloten nach der Stelle des Flugzeugs (`FollowMeVehicle.SetAircraft` aus der Kinematik statt aus SimConnect).
4. Gekreuzte Bahnen: Das Fahrzeug hält vor der Haltelinie und kreuzt erst, wenn die Bahn frei ist (dieselbe Prüfung wie `TaxiKinematics.RunwayClear`); das Flugzeug wartet dahinter.
5. Am Gate wie beim Piloten: vorbei, warten, weg.

**Grenzen**: höchstens zwei Fahrzeuge je Flughafen gleichzeitig (eins kann für den Piloten sein); ist keins frei, rollt das Flugzeug allein. Status in `traffic_test_status` (taxi.followMe), Ereignisse `follow_me_wait`, `follow_me_lead`, `follow_me_done`.

**Schritte**: `FollowMePlan` aus einer `TaxiRoute`, Kopplung an die Rollkinematik, Halt an Bahnkreuzungen fürs Fahrzeug, A3/A4 mit `followMe`, Quote je Flughafen. Test in EDDL 23R.
