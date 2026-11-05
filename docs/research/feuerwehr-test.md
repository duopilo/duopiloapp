# Feuerwehr: Stand und Test im Sim

Stand: 05.11.2026. Status: erster Stand gebaut (Alarm, Bereitstellung, Folgen, Umstellen, Einrücken), Tests ohne Sim grün. Im Sim
getestet in EDDL (04.11.) und EDDK (05.11.), Ergebnisse unten. Planung: [Feuerwehr-Einsätze](feuerwehr-einsaetze.md) (PR #130),
Fahrzeuge: [Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md).

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
- **Ausrücken nebeneinander:** Jedes Fahrzeug hat an seiner Wache einen eigenen Platz, 6 m nebeneinander statt ineinander. Der
  Reihe nach: Tore aus OSM (`door=overhead`, `entrance=garage` … zuerst), dann ein Fahrzeugstellplatz der Szenerie bis 80 m um
  die Wache, dann die Hallenseite, die zum nächsten Rollpunkt zeigt (aus dem Gebäudeumriss in OSM), sonst quer um den Mittelpunkt.
  Vor Toren und Hallenwand stehen sie 4 m davor, Front nach außen, und rollen 12 m geradeaus heraus.
- **Betriebsstraßen bevorzugt:** Auf der Anfahrt kostet ein Meter Straße 0,7 Meter Rollweg (Umweg über die Straße bis zum 1,4-Fachen),
  auf der Rückfahrt ohne Blaulicht 0,5. Bahnen kosten weiter das Achtfache.
- **Betriebsstraßen aus OSM:** Bringt die Szenerie weniger als 20 Fahrweg-Abschnitte mit (Aerosoft EDDL: keine, hu277: 514), nimmt
  Duopilo die Straßen aus OpenStreetMap (`highway=service` im Flughafen, ohne Parkplatzgassen, Drive-in, Flächen, Tunnel und
  überdachte Stücke). Nur die Luftseite: Abschnitte, deren Mitte außerhalb des Flughafen-Umrisses liegt, die einen Zaun oder eine
  Mauer kreuzen oder an einer Schranke liegen (`barrier=gate`, `lift_gate` …), fallen weg. Anschluss ans Rollweg-Netz: Wo eine
  Straße einen Rollweg kreuzt, bekommen beide einen gemeinsamen Punkt; ein Straßenpunkt höchstens 20 m (ein Straßenende 50 m) neben
  einem Rollweg wird mit dessen nächstem Punkt verbunden, wenn kein Gebäude und kein Zaun dazwischen ist. Der Rollweg wird dafür
  geteilt (bis 3 m an einem Rollpunkt: dieser), nie an Bahnen und Zufahrten zu Parkpositionen. Straßenteile ohne Anschluss fallen
  weg. Erster Test EDDL (Aerosoft, 03.11.): mit Anschluss nur an Rollpunkten 978 Abschnitte, aber nur 15 Anschlüsse – die Straßen
  waren fast Inseln, die Fahrzeuge fuhren nur an der Wache darauf. Liegen mehr als 8 % der Straßenmeter mitten auf Parkpositionen,
  passt OSM nicht zur Szenerie und es bleibt bei den Rollwegen. Kreuzt eine Straße eine Bahn, kostet das Stück auf der Bahn wie eine
  Bahn. Das Log sagt, wie viele Abschnitte dazukamen und wie viele wegfielen.
- **Gebäude:** Die Overpass-Abfrage holt alle Gebäude (ohne Dächer/Carports) und Fluggastbrücken im Flughafen. Gerade Stücke
  abseits des Netzes – von der Wache zum ersten Rollpunkt, vom letzten Rollpunkt zum Ziel, zum Platz am Flugzeug – meiden sie: erst
  Rollpunkte mit freier Strecke, und führt der Weg zum Platz doch hinein, hält das Fahrzeug 5 m davor. Rollwege und
  Betriebsstraßen selbst bleiben frei (sie führen unter Brücken durch, und OSM und Szenerie passen nicht überall genau).
- **Abstand untereinander:** Wer einen Weg fährt (Anfahrt, Aufstellen, Rückfahrt), schaut 40 m auf seinem Weg voraus. Steht oder
  fährt dort ein eigenes Fahrzeug (bis 3,5 m neben dem Weg), bremst es und hält 16 m Mitte zu Mitte dahinter bzw. folgt mit dessen
  Tempo. Entgegenkommende zählen nicht. Stehen zwei ineinander oder sehen sich gegenseitig, fährt das mit Blaulicht zuerst, sonst das
  zuerst alarmierte. Hinter einem Fahrzeug am Platz wartet es 6 s und fährt dann weiter (nichts bleibt hängen). Test EDDL 03.11.:
  FLF 2 und ELW fuhren gleichzeitig zurück und hingen ineinander.
- **Notfall am Boden:** Alle stellen sich auf, auch wenn sie über die Zufahrt von hinten kommen (Test V81 03.11.: FLF 2 und ELW
  drehten um, weil die Regel „ohne Halt auf der Bahn abgerollt, nur FLF 1 folgt“ griff).
- **Am Gate:** Bei einer Parkposition als Einsatzort, und wenn das eigene Flugzeug auf einer steht, fahren die Fahrzeuge über die
  Zufahrt der Position von hinten an (nie geradeaus vom nächsten Rollpunkt, der hinter dem Terminal liegen kann) und stellen sich
  nur hinter und rechts neben das Flugzeug: FLF rechts am Flügel und hinter dem Heck, ELW hinter dem Heck. Vor der Nase steht das
  Terminal, links vorn die Fluggastbrücke (Test B9, 03.11.: ein FLF fuhr durch den Abflug und stand halb in der Brücke).
- **Alte Notfälle:** Ein Mayday oder Pan Pan, das älter als 3 Stunden ist (etwa aus einer wiederhergestellten ATC-Sitzung), löst
  nichts mehr aus. Die Bahn aus dem Plan gilt nur am Zielflughafen; am Boden und an einem anderen Flughafen gibt es keine.
- **KI-Flüge:** `alert` mit `tail` (DUO01 …) folgt einem KI-Flugzeug eines laufenden Verkehrstests statt dem eigenen.
- **Licht:** `L:1:DUOPILO_BLAULICHT` und `L:1:DUOPILO_FAHRLICHT` je Fahrzeug, dazu `A:LIGHT BEACON` und `A:LIGHT LANDING`.
- **Räder und Lenkung:** Jeden Frame, solange sich etwas ändert, `L:1:DUOPILO_WEG` (gefahrene Strecke seit dem Spawn) und
  `L:1:DUOPILO_KURVE` (Krümmung der Fahrspur, rechts positiv, über 0,4 s geglättet, höchstens 1/6 m). Im Stand bleibt die Lenkung
  stehen. Die Modelle rechnen daraus Drehwinkel und Lenkeinschlag ([Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md#räder-und-lenkung)).

Noch nicht gebaut: Bahnsperre und Schließung für den eigenen Verkehr, ATC-Meldungen der Feuerwehr, Hochstufen von Pan Pan auf
Mayday (mehr Fahrzeuge), heiße Bremsen und Startabbruch als Auslöser, Notfälle der eigenen KI, Effekte.

## Test ohne MCP-Client

`tools\feuerwehr.ps1` spricht die lokale Schnittstelle `/fire` an (nur von diesem Rechner, kein Token):
`.\feuerwehr.ps1 -Parking B9 -Watch`, `-Level ground`, `-Level full -Runway 23L`, `-Level local -Tail DUO01`, `-Cancel`,
`-Reset`, ohne Schalter der Stand. Heruntergeladen ggf. vorher `Unblock-File .\feuerwehr.ps1`.

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

## Ergebnis Test im Sim (04.11.2026, beta.44.feuerwehr.12, EDDL)

Beide Tests mit einem KI-A320 (`traffic_test_start`) und `fire_service alert` mit `tail: DUO01`, Bahn 23R, Nacht.

**Test 1 – Mayday, A320 bleibt auf der Bahn stehen (A1):** Kategorie 6, alle sechs Fahrzeuge (2 FLF, ELW, HLF, RTF, RTW) von der
Flughafenfeuerwehr Düsseldorf (OSM) über die Betriebsstraßen aus OSM, ohne Fahrzeug ohne Weg.
- Bereitstellung: FLF 1 an der Aufsetzzone (329 m), die anderen hintereinander am Haltepunkt bei 2064 m, rund 100 m neben der
  Mittellinie. Die Wege waren laut Florian sinnvoll.
- Licht: Blaulicht und Fahrlicht an (FLF, ELW, HLF, RTW), auf der Rückfahrt alle Blaulichter aus.
- Umstellen: Der A320 stand nach 29 s Ausrollen bei 1480 m. FLF 1 kam von hinten, die anderen fünf kamen entgegen. Alle standen nach
  gut einer Minute auf ihren Plätzen: FLF 1 vorn links, FLF 2 und HLF hinten rechts, ELW schräg vor der Nase, RTW vorn links, RTF
  links neben dem Rumpf. Keins stand unter dem Flugzeug oder in der Linie der Hauptfahrwerke.
- Einrücken: Nach `cancel` sind alle zur Wache gefahren und dort verschwunden.

**Test 2 – Pan Pan, Landung mit Rollen zu B10 (A3):** 2 FLF und ELW, diesmal von der **Feuerwache Nord**.
- FLF 1 ist ab der Aufsetzzone gefolgt: Es lag erst 1,3 km zurück, hatte mit 30 kt nach rund einer Minute aufgeholt, danach
  48–51 m (Soll 50). Die 23L hat es direkt hinter dem A320 gekreuzt.
- FLF 2 und ELW standen hinter dem Abrollweg (2064 m, K3 bei 1786 m) und sind zurückgefahren, als der A320 abgerollt war.
- Am Gate: Nach gut einer Minute ist auch FLF 1 eingerückt.

**Befunde:**
- **RTF (Modell):** kein Fahrlicht und kein Blaulicht; die Treppe steht bei `L:1:DUOPILO_TREPPE` = 0 nach Florian ausgefahren
  (Duopilo setzt die Variable nicht). Das Rücklicht leuchtet. Die XML und die Lichtknoten im glTF sehen aus wie bei HLF/RTW. Es
  hängt also am Modell und nicht an Duopilo. Prüfen im Behaviors-Fenster des Sims am RTF.
- **Wache bei Pan Pan:** Ohne `allStations` kommen alle Fahrzeuge von der Wache, die in Luftlinie der Bahnmitte am nächsten ist
  (23R: Wache Nord). Bei Mayday nimmt Duopilo je Fahrzeug den kürzesten Weg (Hauptwache). Besser: auch bei Pan Pan nach dem Fahrweg
  wählen. **Behoben in beta.44.feuerwehr.13:** Pan Pan und Notfall am Boden nehmen die Wache mit dem kürzesten Weg.
- **SimConnect DATA_ERROR:** bei jedem Lichtwechsel (Spawn: dreimal je Fahrzeug, Einrücken: einmal) zwei je Fahrzeug, vermutlich
  `LIGHT BEACON`/`LIGHT LANDING` über `SetDataOnSimObject` an Bodenfahrzeugen. Die `L:1:`-Variablen greifen, der Weg über
  `A:LIGHT` ist bei den eigenen Fahrzeugen nicht nötig. **Behoben in beta.44.feuerwehr.13:** Feuerwehr und Follow-me schalten nur
  noch über `L:1:` und die Events, ohne die SimVars.
- Kosmetik im Verkehrstest: `taxi_crossings 05R/23L hold on  at …` und `holding_short 05R/23L on ` ohne Rollweg-Namen.

Noch offen: Landung auf der anderen Bahn, anderer Abrollweg als erwartet, Alarm per Funk (Mayday/Pan Pan im ATC-Modul), Ausrücken
ab 20 NM bei eigenem Anflug, Abstand zum Abgasstrahl.

## Ergebnis Test im Sim (05.11.2026, beta.44.feuerwehr.13, EDDK)

Beide Tests mit KI-Flugzeugen (`traffic_test_start`) und `fire_service alert` mit `tail: DUO01`, Bahn 14L, gefahren von Claude. Das
eigene Flugzeug (Jetson One) stand am Boden im Westen des Platzes. Die Szenerie bringt 214 Fahrweg-Abschnitte mit, Straßen aus OSM
waren nicht nötig. Die 06/24 kreuzt die 14L.

**Test 1 – Mayday, A330-300 (Kategorie 9) bleibt auf der 14L stehen (A1):**
- **Erster Alarm ohne Cache:** Die Overpass-Abfrage für EDDK brauchte rund 50 s. Nach 8 s plante Duopilo wie vorgesehen mit der
  Parkposition F32 als Wache. Danach waren 3 Wachen und 373 Betriebsstraßen im Cache. Mit `reset` abgebrochen und neu alarmiert.
- **Wachen aus OSM:** Flughafenfeuerwehr Köln (Hauptwache, Platzmitte), Flughafenfeuerwehr (Südwesten) und Gebäudebrandschutzwache
  (Norden, an den Terminals). Mit dem kürzesten Weg je Fahrzeug kam FLF 1 von der Gebäudebrandschutzwache, alle anderen von der
  Hauptwache. Es rückten 8 Fahrzeuge aus (3 FLF, ELW, HLF, 2 RTF, RTW), alle mit Weg.
- **Anflug:** Mit der Wegpunkt-KI lag der A330 157 ft zu hoch an der Schwelle und startete durch. Die Feuerwehr blieb dabei in
  Bereitstellung. Der dritte Anflug ging kinematisch ab 4 NM. DUO01 war ein neues Objekt, und die Feuerwehr folgte ihm ohne neuen
  Alarm. Aufsetzen bei 457 m, Stopp bei 1558 m.
- **Bereitstellung** (Abstand zur Mittellinie aus dem Überflug des A330): FLF 1 bei 240 m (92 m), ELW, HLF, beide RTF und RTW
  hintereinander am Haltepunkt bei 1673 m (98–152 m), FLF 3 am Bahnende bei 3569 m (151 m). **FLF 2 stand bei 2105 m auf der Bahn**,
  2 m neben der Mittellinie (siehe Befunde).
- **Umstellen:** FLF 1 kam von hinten (Spur ab 40 kt), alle anderen kamen entgegen. Nach 6 s Stand wurden die Plätze vergeben. Nach
  1:20 min standen alle außer FLF 3. FLF 3 kam vom Bahnende über den Rollweg und war nach knapp 3 min auf seinem Platz. Plätze in
  Metern (vorn/rechts vom Flugzeug): FLF 1 42/−42, FLF 2 −42/42, FLF 3 −42/−42, ELW 54/25, HLF −59/50, RTF 1 19/−57, RTF 2 −37/−59,
  RTW 71/−21. Keins stand im Bereich von Rumpf und Flügeln (Spannweite 60 m).
- **Einrücken:** Nach `cancel` fuhren alle ohne Blaulicht zurück. Die 7 von der Hauptwache verschwanden nach 1:40 bis 2:15 min, FLF 1
  an der Gebäudebrandschutzwache nach 5:40 min (3,4 km).

**Test 2 – Pan Pan, A320neo (Kategorie 6) mit Rollen zu D12 (A3):**
- **Wache nach Fahrweg:** Alle drei Fahrzeuge (2 FLF, ELW) kamen von der Hauptwache. Der Alarm kam erst 60 s vor dem Aufsetzen.
- **Bereitstellung:** FLF 2 und ELW erreichten den Haltepunkt bei 1673 m 17 s nach dem Aufsetzen. FLF 1 war noch auf dem Weg zur
  Aufsetzzone (240 m).
- **Folgen:** Der A320 rollte ohne Halt über A2 (2243 m) ab. FLF 2 und ELW, an denen er vorbeigekommen war, gingen auf die Spur und
  folgten mit gut 500 m Abstand (Soll 80 bzw. 105 m). Rund eine Minute nach dem Abrollen rückten beide ein. FLF 1 fuhr erst weiter
  Richtung Aufsetzzone und ging dann mit 2088 m Abstand auf die Spur. Es holte mit bis zu 48 kt auf und lag nach gut 2 min bei 50 bis
  52 m (Soll 50). Beim Halt des A320 vor der 06/24 auf A (4 s) blieb es dahinter.
- **Am Gate:** Der A320 stand um 06:03:02 auf D12. FLF 1 rückte 61 s später ein und war nach knapp 4 min an der Wache.
- Die Ereignisse des Verkehrstests nennen hier den Rollweg (`taxi_crossings 06/24 hold on A …`, `holding_short 06/24 on A`). Die
  leeren Namen aus EDDL hängen also an der Szenerie.

**Prüfpunkte aus beta.44.feuerwehr.13:**
- **RTF:** Die Treppe ist weiter ausgefahren. Laut Florian nimmt sie den Wert von `L:1:DUOPILO_WEG` an (Fahrstrecke). Duopilo setzt
  `L:1:DUOPILO_TREPPE` gar nicht, und am RTF gab es keine eigenen Fehler. Vermutung: Duopilo legt je Variablenname eine einzige
  DataDefinition an und schreibt sie an alle Modelle. Löst MSFS `L:1:` über die Reihenfolge der ersten Verwendung im Modell auf, dann
  landet WEG beim RTF auf TREPPE, sobald TREPPE dort vor WEG registriert wird, und alle Variablen dahinter rutschen mit. Das würde
  auch das fehlende Blau- und Fahrlicht am 04.11. erklären. Zu prüfen im Behaviors-Fenster des Sims. Abhilfe im Modell: TREPPE ans
  Ende hinter FAHRLICHT, oder in allen Modellen dieselbe vollständige Liste in derselben Reihenfolge, TREPPE eingeschlossen.
- **DATA_ERROR:** Beim Einrücken (Blaulicht aus) gab es keinen DATA_ERROR mehr, auch nicht beim Folgen und Umstellen. Der Fix aus .13
  greift. Beim Spawn kommen weiter 3 bis 9 je Fahrzeug, fast alle in der Sekunde des Spawns, mit fortlaufenden SendIDs und `index 1`
  (43 und 54 bei 8 Fahrzeugen, 12 bei 3). Das zuerst gespawnte Fahrzeug eines Einsatzes hatte in allen drei Läufen keinen. Das passt
  zu den 7 Z-Variablen aus `FireVehicle.Vars()`: Im ersten Frame lädt das Objekt noch. Beim ersten Fahrzeug läuft der Pose-Takt erst
  danach an. Folge:
  `_aiVarSent` merkt sich die verworfenen Werte trotzdem, unveränderte Variablen (HORN, LICHTFARBE) gehen nie mehr hinaus. Heute
  sind beide 0, das fällt also noch nicht auf. Vorschlag: Z-Variablen erst etwa 2 s nach dem Spawn senden, oder bei den
  Lichtschaltungen (1, 2, 5 s) den Cache des Objekts leeren.

**Befunde:**
- **FLF 2 auf der Landebahn:** Der Haltepunkt 744 (2105 m) liegt 2 m neben der Mittellinie der 14L, also auf der Bahn. Welcher Knoten
  der Szenerie das genau ist (Hold-Short-Punkt einer Kreuzung auf der Bahn?), ist noch offen. `TaxiGraph.Holds()` ordnet jeden
  Hold-Knoten der nächsten Bahn zu (bis 400 m), auch wenn er auf ihr liegt, und `FirePlanner` filtert nur nach der Lage entlang der
  Bahn. Der A330 hielt 540 m davor. Bei längerem Ausrollen wäre er durch FLF 2 gerollt. Vorschlag: im Planer Haltepunkte verwerfen,
  die auf einer Bahn liegen (`Graph.RunwayAt`) oder näher als die halbe Bahnbreite plus Sicherheitsabstand an der Mittellinie.
- **Spawnhöhe:** Florian sah die Fahrzeuge an der Hauptwache aus dem Boden kommen. Duopilo nimmt als Boden für alle Fahrzeuge die
  Höhe unter dem eigenen Flugzeug, wenn es am Boden steht. Der Jetson One stand 1,3 km entfernt auf 223 ft MSL, Bahn und Wache liegen
  bei etwa 253 ft. Die Fahrzeuge erschienen also rund 30 ft zu tief. Die Bodenabfrage je Fahrzeug liegt unter der Grenze von 60 ft
  und wird übernommen, dann steigen sie mit 6 ft/s (`GroundSlewFtPerSec`), also etwa 5 s sichtbar. In EDDL war der Platz flach.
  Vorschlag: die Höhe unter dem eigenen Flugzeug nur nahe der Wache nehmen, sonst die Platzhöhe, und die erste Bodenabfrage direkt
  übernehmen (oder das Fahrzeug erst danach zeigen).
- **Begleiter bei spätem Alarm:** `FireOperation.Escort()` ist immer FLF 1, auch wenn es noch unterwegs ist. In Test 2 folgten FLF 2
  und ELW schon direkt hinter dem A320 und wurden eingerückt, während FLF 1 2 km aufholen musste. Besser: das FLF, das auf der Spur
  am nächsten am Flugzeug ist. Ein Fahrzeug, an dessen Haltepunkt das Flugzeug schon vorbei ist, könnte auch gleich auf die Spur
  gehen, statt erst zum Haltepunkt zu fahren.
- **Gebäudebrandschutzwache:** In OSM ist sie als Feuerwache mit Umriss getaggt und zählt deshalb als volle Wache. FLF 1 kam bei
  Mayday von dort. `amenity` und `building` werden gleich behandelt: Treffer unter 80 m werden zusammengelegt, ein Umriss gewinnt für
  die Lage, der Name kommt vom zuerst gefundenen Treffer. Offen: ob Wachen nach Name oder einem weiteren Tag gefiltert werden sollen.
- **Kreuzende Bahnen:** Die Feuerwehrfahrzeuge halten vor kreuzenden Bahnen nicht (keine Logik dafür, Bahnen kosten im Weg nur das
  Achtfache). Ob ein Fahrzeug auf der Anfahrt eine Bahn gekreuzt hat, ist nicht ausgewertet. FLF 1 hielt in Test 2 vor der 06/24 nur,
  weil es dem A320 folgte.
- **Heavy im Verkehrstest:** Mit der Wegpunkt-KI startete der A330 durch (an 0,5 NM 110 ft, an der Schwelle 157 ft zu hoch). Für
  Feuerwehr-Tests mit Heavies `kinematicNm` nehmen.

Noch offen: FLF auf Haltepunkten auf der Bahn, Spawnhöhe, Begleiter bei spätem Alarm, RTF-Variablen im Modell, DATA_ERROR beim
Spawn, Landung auf der anderen Bahn, Alarm per Funk (Mayday/Pan Pan im ATC-Modul), Ausrücken ab 20 NM bei eigenem Anflug, Abstand
zum Abgasstrahl.
