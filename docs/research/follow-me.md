# Follow-me: Ablauf und Test im Sim

Stand: 02.11.2026. Status: Duopilo-Seite gebaut (ATC, Route, Fahrlogik, Tests ohne Sim). Der Test mit dem eigenen Modell im Sim ist offen.

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

- Modell: `Duopilo:FollowMe:Model`, Standard `Duopilo_FollowMe_Licht` (Variante mit schaltbarem Licht). Nach dem Spawn setzt Duopilo `L:1:DUOPILO_RUNDUMLICHT` und `L:1:DUOPILO_FAHRLICHT` auf 1, zur Sicherheit auch `A:LIGHT BEACON` und `A:LIGHT LANDING` (nach 1, 3 und 8 s, weil der Sim Werte beim Laden verwirft).
- Erzeugt mit `AICreateSimulatedObject_EX1` (Rückfall ohne Livery), bewegt wie die KI-Flugzeuge der Verkehrstests: eingefroren, Lage jeden Frame gesetzt, aber ohne `AIReleaseControl`.
- Boden: unter dem eigenen Flugzeug gemessen, danach aus der Abfrage des Fahrzeugs (Höhe minus Höhe über Grund), höchstens 60 ft vom Erwarteten. `Duopilo:FollowMe:HeightFt` hebt den Bezugspunkt des Modells an, falls es einsinkt.

## Route und Fahrt

1. Start: der Rollweg-Abschnitt, an dem das Flugzeug steht (höchstens 60 m daneben), Knoten in Blickrichtung.
2. Gate: das von ATC (von Hand gesetzt), sonst ein freies, passendes (wie bei den Verkehrstests) – das nächste unter den ersten zehn mit Route. Unter 200 m bis zur Einfahrt kein Follow-me.
3. Das Fahrzeug startet auf der Route bis 600 m vor dem Treffpunkt (sonst an der Einfahrt zum Gate), fährt entgegen und wendet vor dem Flugzeug (Radius 6,5 m). Treffpunkt und Abstand vorweg: 35 m (Light), 60 m (Medium), 80 m (Heavy), 95 m (Super) vom Bezugspunkt des Flugzeugs entlang der Route.
4. Vorweg höchstens 30 km/h, Abstandsregelung: Geschwindigkeit des Flugzeugs plus 0,25 m/s je Meter zu wenig Abstand; steht das Flugzeug, wartet es.
5. Am Gate 40 m an der Einfahrt vorbei, warten, bis das Flugzeug dort ist (höchstens 2 Minuten), 150 m weiter, dann entfernt. In einer Sackgasse fährt es quer zur Seite weg vom Gate.

## Test im Sim

1. `follow_me` mit `action: request` am Boden auf einem Rollweg (ohne Funk, beliebiger Flughafen): Spawnt das Modell? Folgt es der Route, wendet es sauber, steht es auf dem Boden (nicht schwebend, nicht eingesunken)?
2. Leuchten Rundumlicht und Fahrlicht? Wenn nicht: greift `L:1:` über SimConnect nicht (siehe [Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md)), dann sollte `A:LIGHT BEACON` helfen.
3. Losrollen: Fährt es vorweg, hält es den Abstand, wartet es beim Anhalten? Abstand und Phase in `follow_me` (status).
4. Am Gate: fährt es vorbei und verschwindet?
5. Danach der ganze Ablauf im Funk nach einer Landung (EDDL).

Offen: ob `AIReleaseControl` für Bodenfahrzeuge nötig ist (bewusst weggelassen) und ob die Abfrage `PLANE ALT ABOVE GROUND` für Bodenfahrzeuge Werte liefert.
