# Feuerwehr: Stand und Test im Sim

Stand: 02.11.2026. Status: erster Stand gebaut (Alarm, Bereitstellung, Folgen, Umstellen, Einrücken), Tests ohne Sim grün. Der Test
im Sim ist offen. Planung: [Feuerwehr-Einsätze](feuerwehr-einsaetze.md) (PR #130), Fahrzeuge: [Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md).

## Was gebaut ist

- **Alarm:** Mayday im ATC-Modul = Full Emergency, Pan Pan = Local Standby, Notruf am Boden = Notfall am Boden. Zum Testen ohne
  Funk: `fire_service` mit `action: alert`.
- **Ausrücken:** sobald das Flugzeug höchstens `Duopilo:Fire:DispatchRangeNm` (20 NM) vom Landeflughafen entfernt ist (Ziel des
  Notfalls, sonst der nächste Flughafen des SimBrief-Plans). Mit dem Werkzeug sofort.
- **Wache:** echte Feuerwachen aus OpenStreetMap (`amenity=fire_station` bzw. `building=fire_station` im Flughafen-Umriss,
  Overpass API, 30 Tage zwischengespeichert in `cache\fire-stations`). Die Abfrage startet schon beim Alarm, die Planung wartet
  höchstens 8 s darauf (am 02.11. brauchte overpass-api.de über 20 s). Danach spawnen die Fahrzeuge dieses Mal auf einer freien
  Parkposition, und die Abfrage läuft für das nächste Mal weiter. Ein Fehlschlag wird nach 15 Minuten neu versucht, zuerst bei
  overpass-api.de, dann bei overpass.private.coffee. Pan Pan und Notfall am Boden: alles von der nächsten Wache;
  Mayday: jedes Fahrzeug von der Wache mit dem kürzesten Weg. Jedes Fahrzeug fährt zu seiner Wache zurück. Rückfall ohne
  OSM-Wache (oder ohne Weg von ihr, über 400 m neben dem Rollweg-Netz): freie Parkposition nahe der Bahnmitte, Fahrzeug-Parkposition
  zuerst, Gates zuletzt, außer Sicht. Die Fahrzeuge spawnen nacheinander im Abstand von 4 s.
- **Fahrzeuge:** nach Feuerwehr-Kategorie aus der Länge des Flugzeugtyps (A320 = 6, A321 = 7, B77W = 9). Bis 5: 1 FLF, 6–7: 2 FLF
  und ELW, ab 8: 3 FLF und ELW; bei Full Emergency dazu HLF, Rettungstreppe(n), RTW.
- **Bereitstellung:** Haltepunkte der Landebahn: FLF 1 an der Aufsetzzone, FLF 2 hinter der erwarteten Stoppstelle, FLF 3 am
  Bahnende, ELW und der Rest an der Mitte (hintereinander, je 22 m).
- **Folgen:** Ab dem Aufsetzen zeichnet Duopilo die Spur des Flugzeugs auf. Ist das Flugzeug an einem Fahrzeug vorbei und langsamer
  als 40 kt, fährt das Fahrzeug auf die Spur und folgt mit Abstand (Bahn 80 m, Rollweg 50 m bei Medium, Kolonne je 25 m mehr).
- **Umstellen:** Steht das Flugzeug 6 s auf der Bahn (20 s daneben), fahren alle auf ihre Plätze: FLF vorn links, hinten rechts,
  hinten links, ELW schräg vor der Nase, die anderen weiter weg. Fahrzeuge vor dem Flugzeug kommen ihm entgegen.
- **Weiter:** Rollt das Flugzeug wieder, folgt nur FLF 1, die anderen fahren ohne Blaulicht zurück zur Wache und verschwinden dort.
  Steht das Flugzeug danach eine Minute abseits der Bahn, rückt auch FLF 1 ein. „cancel Mayday“ / „cancel Pan Pan“: alle zurück.
- **Fahrwege:** Für die Feuerwehr lädt Duopilo auch die Betriebsstraßen der Bodenfahrzeuge (TAXI_PATH VEHICLE und ROAD), damit
  die Fahrzeuge von der Wache aus aufs Rollweg-Netz finden. Flugzeuge rollen weiter nur über Rollwege. Ist der nächste Rollpunkt
  einer Wache abgeschnitten (in EDDL ein Stück am Helipad), probiert Duopilo die nächsten bis 400 m.
- **Einsatzort Parkposition:** `alert` mit `parking` (z. B. `V61A`) ist ein Notfall am Boden an dieser Position statt an einem
  Flugzeug. Die Fahrzeuge stellen sich auf, als stünde dort ein Flugzeug, und rücken nach 3 Minuten wieder ein.
- **Alte Notfälle:** Ein Mayday oder Pan Pan, das älter als 3 Stunden ist (etwa aus einer wiederhergestellten ATC-Sitzung), löst
  nichts mehr aus. Die Bahn aus dem Plan gilt nur am Zielflughafen; am Boden und an einem anderen Flughafen gibt es keine.
- **KI-Flüge:** `alert` mit `tail` (DUO01 …) folgt einem KI-Flugzeug eines laufenden Verkehrstests statt dem eigenen.
- **Licht:** `L:1:DUOPILO_BLAULICHT` und `L:1:DUOPILO_FAHRLICHT` je Fahrzeug, dazu `A:LIGHT BEACON` und `A:LIGHT LANDING`.
- **Räder und Lenkung:** Jeden Frame, solange sich etwas ändert, `L:1:DUOPILO_WEG` (gefahrene Strecke seit dem Spawn) und
  `L:1:DUOPILO_KURVE` (Krümmung der Fahrspur, rechts positiv, über 0,4 s geglättet, höchstens 1/6 m). Im Stand bleibt die Lenkung
  stehen. Die Modelle rechnen daraus Drehwinkel und Lenkeinschlag ([Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md#räder-und-lenkung)).

Noch nicht gebaut: Bahnsperre und Schließung für den eigenen Verkehr, ATC-Meldungen der Feuerwehr, Hochstufen von Pan Pan auf
Mayday (mehr Fahrzeuge), heiße Bremsen und Startabbruch als Auslöser, Notfälle der eigenen KI, Effekte.

## Test im Sim

Voraussetzung: Paket `duopilo-fire` mit den `_Licht`-Varianten im Community-Ordner, Duopilo aus diesem Branch.

1. **Spawnen und Fahren:** Am Boden in EDDL, `fire_service` mit `action: alert`, `level: local`, `airport: EDDL`, `runway: 23R`.
   Spawnen die Fahrzeuge auf der Parkposition aus dem Status (`spawn`)? Fahren sie zu den Haltepunkten, stehen sie auf dem Boden?
2. **Licht:** Blitzt das Blaulicht, leuchtet das Fahrlicht? Auf der Rückfahrt muss das Blaulicht aus sein.
3. **Landung:** `alert` im Anflug (unter 20 NM), dann landen. Fahren FLF 1 und ELW hinter dir auf die Bahn, wenn du langsamer als
   40 kt bist? Bleibt der Abstand, überholt keins?
4. **Umstellen:** Auf der Bahn stehen bleiben. Stellen sich alle auf ihre Plätze, ohne durch das Flugzeug zu fahren? Kommt FLF 2
   (vor dir) entgegen?
5. **Weiterrollen:** Folgt nur FLF 1, fahren die anderen zurück? Nach einer Minute am Gate: rückt FLF 1 ein?
6. **Abweichen:** anderer Abrollweg als erwartet, Stopp mitten auf der Bahn, Landung auf der anderen Bahn.
7. **Funk:** derselbe Ablauf mit Mayday und Pan Pan im ATC-Modul.

Im Status von `fire_service` stehen je Fahrzeug Phase, Geschwindigkeit und Abstand; das Log schreibt jeden Phasenwechsel
(`Fire service FLF 1 following (gap 80 m)`).

Offen für den Test: ob `AICreateSimulatedObject` bei einem Flugzeug 20 NM entfernt schon Fahrzeuge am Flughafen erzeugt (sonst
später ausrücken), wie schnell die Fahrzeuge in Kurven wirken, und ob die Abstände zum Abgasstrahl passen.
