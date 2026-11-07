# Feuerwehr: Stand und Test im Sim

Stand: 07.11.2026 (Nachtest beta.44.feuerwehr.22, gebaut .23). Status: erster Stand gebaut (Alarm, Bereitstellung, Folgen, Umstellen, Einrücken), Tests ohne Sim grün. Im Sim
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
  Militärische Wachen fahren keine zivilen Einsätze: in einer militärischen Fläche (`landuse=military`, `military=airfield/barracks/
  base/naval_base/office`), selbst mit `military=*` oder `operator:type=military`, oder mit militärischem Betreiber (Bundeswehr, Air
  Force …). Sind alle Wachen militärisch (Militärflugplatz), bleiben alle; ist nichts eingetragen, fällt nichts weg. Wachen abseits
  der Mitte der Bahnen (Schwerpunkt der Bahnmitten) kosten bei der Wahl einen Aufschlag von 0,5 m je Meter Abstand: Wachen am Rand
  kommen nur, wenn sie deutlich näher am Ziel sind; Außenwachen an entfernten Bahnen (EDDL Wache Nord, EDDF) bleiben im Spiel.
- **Fahrzeuge:** nach Feuerwehr-Kategorie aus der Länge des Flugzeugtyps (A320 = 6, A321 = 7, B77W = 9). Bis 5: 1 FLF, 6–7: 2 FLF
  und ELW, ab 8: 3 FLF und ELW (Mindestzahl nach ICAO Annex 14), bei Full Emergency ab 8 vier FLF; bei Full Emergency dazu HLF,
  eine Rettungstreppe und RTW (bis .19 ab 8 zwei Rettungstreppen und drei FLF).
- **Bereitstellung:** Haltepunkte der Landebahn: FLF 1 an der Aufsetzzone, FLF 2 hinter der erwarteten Stoppstelle, FLF 3 am
  Bahnende, FLF 4, ELW und der Rest an der Mitte (hintereinander, je 22 m). Haltepunkte auf einer Bahn (Hold-Short-Punkte an Kreuzungen)
  und solche näher als halbe Bahnbreite plus 15 m an der Mittellinie fallen weg.
- **Folgen:** Ab dem Aufsetzen zeichnet Duopilo die Spur des Flugzeugs auf. Ist das Flugzeug an einem Fahrzeug vorbei, fährt das
  Fahrzeug gleich auf die Spur, egal wie schnell das Flugzeug noch ist (bis .19 erst unter 40 kt), und folgt mit Abstand (Bahn 80 m
  bei Medium, 90 m bei Heavy, Rollweg 50 bzw. 65 m, Kolonne je 25 m mehr). Ist es noch unterwegs und das Flugzeug schon an ihm und
  seinem Haltepunkt vorbei (später Alarm), fährt es gleich auf die Spur. Die Kolonne ordnet sich nach dem Abstand zum Flugzeug: Wer
  später kommt, reiht sich hinten ein.
- **Entgegen vom Bahnende:** Das FLF am Bahnende (FLF 3) fährt ab dem Aufsetzen über die Bahn dem Flugzeug entgegen, auch wenn es
  selbst noch unterwegs war (Log: `approaching over runway …`), zur Mittellinie an der erwarteten Stoppstelle (mit 2,0 m/s²
  gerechnet; bis .20 dorthin, wo das Flugzeug gerade war). Solange das Flugzeug rollt, bleibt es vor der Stelle, an der es mit
  1,2 m/s² zum Stehen käme, plus 150 m (quer: halbe Bahnbreite plus 25 m). Startet das Flugzeug durch, fährt es zurück an seinen
  Haltepunkt.
- **Umstellen:** Steht das Flugzeug 6 s auf der Bahn (20 s daneben), fahren alle auf ihre Plätze, auch wer noch unterwegs ist (bis
  .19 erst ab dem Haltepunkt, wenn es weiter als 300 m weg war), seit .21 auch, wer hinter dem Flugzeug zu einem Haltepunkt vor ihm
  unterwegs ist: FLF vorn links, hinten rechts, hinten links, vorn rechts, ELW schräg
  vor der Nase, die anderen weiter weg. Fahrzeuge vor dem Flugzeug kommen ihm entgegen. Von weiter als 800 m geht es über das Netz,
  und darin zählt die Bahn des Notfalls nach der Landung wie ein Rollweg (sonst achtfach); das gilt auch für den Weg auf die Spur.
- **Weiter:** Rollt das Flugzeug wieder, folgt nur FLF 1, die anderen fahren ohne Blaulicht zurück zur Wache und verschwinden dort.
  Ist ein anderes FLF schon beim Flugzeug und mehr als 200 m näher dran als FLF 1, folgt dieses. Steht das Flugzeug danach eine
  Minute abseits der Bahn, rückt auch der Begleiter ein. „cancel Mayday“ / „cancel Pan Pan“: alle zurück.
- **Bahnen kreuzen:** Bei Mayday kreuzen die Fahrzeuge mit Blaulicht jede Bahn ohne Halt und ohne Verkehrsabfrage (Vorfahrt; der
  Flughafen ist dann für den eigenen KI-Verkehr gesperrt, siehe unten), seit .22 auch vor der Landung. Sonst: Führt ein Weg (Anfahrt, auf die Spur, Aufstellen,
  Rückfahrt) auf eine Bahn, hält das Fahrzeug an der Stelle, die halbe Bahnbreite plus 40 m neben der Mittellinie liegt. Mit Blaulicht
  (Pan Pan, Notfall am Boden) fragt Duopilo den Verkehr schon ab 200 m davor ab und lässt es durchfahren, wenn die Bahn frei ist;
  ohne Blaulicht (Rückfahrt) hält es mindestens 5 s und fragt dann. Frei heißt: nichts auf der Bahn und nichts im Endanflug (mit
  Blaulicht bis 2 NM, sonst bis 4 NM, wie beim eigenen KI-Verkehr). Ohne Antwort aus der Abfrage nach 20 s Halt trotzdem. Die Bahn des Notfalls ist nach der Landung frei, solange der Einsatz
  läuft; davor (das Flugzeug im Endanflug) und auf der Rückfahrt gilt sie wie jede andere. Das Flugzeug des Notfalls zählt nach der
  Landung nicht als Verkehr, solange es steht (unter 3 kt; erkannt an der Objekt-ID des Verkehrstests bzw. als eigenes Flugzeug).
  Rollt es über eine Bahn, wartet das Fahrzeug, bis es von ihr herunter ist. Wer der Spur folgt, kreuzt hinter dem Flugzeug ohne
  eigenen Halt. Im Status steht `holdingShort`, das Log schreibt `holding short of runway …`, `waiting for runway …` und
  `crossing runway … after … s` (ohne Halt: `after 0 s`).
- **Sperre für den eigenen KI-Verkehr:** Bei Mayday ist der Flughafen gesperrt, sobald die Fahrzeuge ausrücken, bis sie einrücken
  (Log `Airport … closed for own AI traffic` bzw. `open again`). Der Verkehrstest landet dort nicht (Durchstarten an der
  Entscheidungshöhe, `landing_runway_busy …: airport closed (fire service, Mayday)`), kreuzt keine Bahn und reiht sich nicht zum Start
  auf (`runway_busy …`). Ausgenommen ist das Flugzeug des Notfalls selbst. Den Live-Verkehr des Sims steuert Duopilo nicht.
- **Fahrwege:** Für die Feuerwehr lädt Duopilo auch die Betriebsstraßen der Bodenfahrzeuge (TAXI_PATH VEHICLE und ROAD), damit
  die Fahrzeuge von der Wache aus aufs Rollweg-Netz finden. Flugzeuge rollen weiter nur über Rollwege. Ist der nächste Rollpunkt
  einer Wache abgeschnitten (in EDDL ein Stück am Helipad), probiert Duopilo die nächsten bis 400 m.
- **Einsatzort Parkposition:** `alert` mit `parking` (z. B. `V61A`) ist ein Notfall am Boden an dieser Position statt an einem
  Flugzeug. Die Fahrzeuge stellen sich auf, als stünde dort ein Flugzeug, und rücken nach 3 Minuten wieder ein.
- **Ausrücken nebeneinander:** Jedes Fahrzeug hat an seiner Wache einen eigenen Platz, 6 m nebeneinander statt ineinander. Der
  Reihe nach: Tore aus OSM (`door=overhead`, `entrance=garage` … zuerst), dann ein Fahrzeugstellplatz der Szenerie bis 80 m um
  die Wache, dann die Hallenseite, die zum nächsten Rollpunkt zeigt (aus dem Gebäudeumriss in OSM), sonst quer um den Mittelpunkt.
  Vor Toren und Hallenwand stehen sie 4 m davor, Front nach außen, und rollen 12 m geradeaus heraus. Ist die Fläche der Wache in
  OSM das ganze Gelände (ein Gebäude darauf hat höchstens 80 % ihrer Fläche und mindestens 150 m²), gilt das größte Gebäude darauf
  als Halle, die Plätze liegen auf ihrer Seite zum Hof (zur Mitte des Geländes), und das Gelände ist kein Hindernis (Log: `(hall)`).
  Vor einer Hallenwand liegen in einer Reihe nur so viele Plätze, wie auf die Wand passen (1,5 m Rand an den Enden), und keiner im
  Gebäude oder so, dass das Herausrollen durch die Halle führt; sonst die nächste Reihe, 9 m weiter vorn.
- **Betriebsstraßen bevorzugt:** Auf der Anfahrt kostet ein Meter Straße 0,7 Meter Rollweg (Umweg über die Straße bis zum 1,4-Fachen),
  auf der Rückfahrt ohne Blaulicht 0,5. Bahnen kosten weiter das Achtfache, außer der Bahn des Notfalls nach der Landung.
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
- **Abstellen nach dem Notfall (seit .23):** Ein KI-Flugzeug mit Feuerwehreinsatz (Pan Pan oder Mayday, `tail`, ohne `parking`)
  parkt frei auf dem Vorfeld statt am Gebäude. Duopilo fragt die Fluggastbrücken des Platzes beim Sim ab (Facility JETWAY:
  PARKING_GATE, PARKING_SUFFIX, PARKING_SPOT) und wählt bis zu 12 Positionen: keine mit Brücke, keine Docks, Tankstellen oder
  Fahrzeug-Plätze, keine belegte, groß genug für die Wirbelschleppenklasse; Vorfeld vor Gate, mindestens 50 m bis zum nächsten
  Gebäude aus OSM vor näher dran, dann die nächste zur Landebahn. Der Verkehrstest nimmt beim Abrollen die erste erreichbare und
  meldet „parking_changed … to remote stand … for the fire service“. Rollt er schon, bleibt es beim alten Platz
  (`warning:remote_stand_too_late`). Klappt die Abfrage der Brücken nicht, gilt nur Vorfeld vor Gate und der Abstand.
- **Licht:** `L:1:DUOPILO_BLAULICHT` und `L:1:DUOPILO_FAHRLICHT` je Fahrzeug und die Events (seit beta.44.feuerwehr.13 ohne
  `A:LIGHT BEACON`/`A:LIGHT LANDING`), 2, 3 und 5 s nach dem Spawn. Die Z-Variablen gehen erst 2 s nach dem Spawn hinaus, und jedes
  Fahrzeug hat eigene Data Definitions (seit beta.44.feuerwehr.14, siehe Ergebnis 05.11.).
- **Bodenhöhe:** Gespawnt wird auf der Platzhöhe, auf der Höhe unter dem eigenen Flugzeug nur, wenn es höchstens 300 m vom Spawnpunkt
  am Boden steht. In den ersten 3 s übernimmt das Fahrzeug den gemessenen Boden sofort, danach gleitet es mit 6 ft/s.
- **Räder und Lenkung:** Jeden Frame, solange sich etwas ändert, `L:1:DUOPILO_WEG` (gefahrene Strecke seit dem Spawn) und
  `L:1:DUOPILO_KURVE` (Krümmung der Fahrspur, rechts positiv, über 0,4 s geglättet, höchstens 1/6 m). Im Stand bleibt die Lenkung
  stehen. Die Modelle rechnen daraus Drehwinkel und Lenkeinschlag ([Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md#räder-und-lenkung)).

Noch nicht gebaut: Freies Abstellen des eigenen Flugzeugs nach einem Notfall (bisher nur für KI-Flugzeuge der Verkehrstests),
ATC-Meldungen der Feuerwehr, Hochstufen von Pan Pan auf Mayday (mehr Fahrzeuge), heiße Bremsen und Startabbruch als Auslöser,
Notfälle der eigenen KI, Effekte.

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
  auch das fehlende Blau- und Fahrlicht am 04.11. erklären. Zu prüfen im Behaviors-Fenster des Sims.
  Nachgesehen (Quelle und gebautes Paket in `Documents\Duopilo-Modelle`): Das RTF-XML legt die Variablen wie alle anderen in fester
  Reihenfolge an (`Duopilo_Variablen`, `Update Once`), die Treppe-Animation liest `DUOPILO_TREPPE`, im glTF bewegt `Treppe` nur die
  vier Treppen-Knoten. Modell und Paket sind also in Ordnung. Einziger Unterschied zu den anderen: Beim RTF ist `Treppe` die erste
  Animation (vor `Raeder` und `Lenkung`). Legt MSFS die Variablen in der Reihenfolge an, in der die Animationen sie zuerst benutzen,
  ist beim RTF TREPPE die erste und WEG die zweite Stelle, bei FLF, ELW, HLF und RTW ist WEG die erste. Bindet SimConnect eine Data
  Definition beim ersten Setzen an die Stelle im ersten Fahrzeug (FLF 1), landet WEG beim RTF genau auf TREPPE, KURVE auf WEG und
  BLAULICHT/FAHRLICHT eine Stelle daneben. Das passt zu beiden Beobachtungen. **Geändert in beta.44.feuerwehr.14:** Duopilo legt
  die Data Definitions je Objekt an statt je Variablenname und gibt sie beim Entfernen frei (`ClearDataDefinition`). Bleibt die
  Treppe beim Fahren eingefahren und gehen Blau- und Fahrlicht am RTF, war es das. Sonst im Modell `Anim_Treppe` hinter
  `Anim_Lenkung` stellen.
- **DATA_ERROR:** Beim Einrücken (Blaulicht aus) gab es keinen DATA_ERROR mehr, auch nicht beim Folgen und Umstellen. Der Fix aus .13
  greift. Beim Spawn kommen weiter 3 bis 9 je Fahrzeug, fast alle in der Sekunde des Spawns, mit fortlaufenden SendIDs und `index 1`
  (43 und 54 bei 8 Fahrzeugen, 12 bei 3). Das zuerst gespawnte Fahrzeug eines Einsatzes hatte in allen drei Läufen keinen. Das passt
  zu den 7 Z-Variablen aus `FireVehicle.Vars()`: Im ersten Frame lädt das Objekt noch. Beim ersten Fahrzeug läuft der Pose-Takt erst
  danach an. Folge:
  `_aiVarSent` merkt sich die verworfenen Werte trotzdem, unveränderte Variablen (HORN, LICHTFARBE) gehen nie mehr hinaus. Heute
  sind beide 0, das fällt also noch nicht auf. **Geändert in beta.44.feuerwehr.14:** Z-Variablen erst 2 s nach dem Spawn
  (`FireVehicle.VarsDelayS`), das Licht nach 2, 3 und 5 s statt 1, 3 und 8 s. Im nächsten Test prüfen, ob die DATA_ERROR damit weg sind.

**Befunde:**
- **FLF 2 auf der Landebahn:** Der Haltepunkt 744 (2105 m) liegt 2 m neben der Mittellinie der 14L, also auf der Bahn. Welcher Knoten
  der Szenerie das genau ist (Hold-Short-Punkt einer Kreuzung auf der Bahn?), ist noch offen. `TaxiGraph.Holds()` ordnet jeden
  Hold-Knoten der nächsten Bahn zu (bis 400 m), auch wenn er auf ihr liegt, und `FirePlanner` filtert nur nach der Lage entlang der
  Bahn. Der A330 hielt 540 m davor. Bei längerem Ausrollen wäre er durch FLF 2 gerollt. **Behoben in beta.44.feuerwehr.14:** Der
  Planer verwirft Haltepunkte, die auf einer Bahn liegen (`Graph.RunwayAt`) oder näher als halbe Bahnbreite plus 15 m an der
  Mittellinie (`FirePlanner.MinHoldEdgeClearM`).
- **Spawnhöhe:** Florian sah die Fahrzeuge an der Hauptwache aus dem Boden kommen. Duopilo nimmt als Boden für alle Fahrzeuge die
  Höhe unter dem eigenen Flugzeug, wenn es am Boden steht. Der Jetson One stand 1,3 km entfernt auf 223 ft MSL, Bahn und Wache liegen
  bei etwa 253 ft. Die Fahrzeuge erschienen also rund 30 ft zu tief. Die Bodenabfrage je Fahrzeug liegt unter der Grenze von 60 ft
  und wird übernommen, dann steigen sie mit 6 ft/s (`GroundSlewFtPerSec`), also etwa 5 s sichtbar. In EDDL war der Platz flach.
  **Behoben in beta.44.feuerwehr.14:** Die Höhe unter dem eigenen Flugzeug gilt nur bis 300 m vom Spawnpunkt (`OwnGroundRangeM`),
  sonst die Platzhöhe. In den ersten 3 s nach dem Spawn springt das Fahrzeug auf den gemessenen Boden (`FireVehicle.GroundSnapS`).
- **Begleiter bei spätem Alarm:** `FireOperation.Escort()` ist immer FLF 1, auch wenn es noch unterwegs ist. In Test 2 folgten FLF 2
  und ELW schon direkt hinter dem A320 und wurden eingerückt, während FLF 1 2 km aufholen musste. Besser: das FLF, das auf der Spur
  am nächsten am Flugzeug ist. Ein Fahrzeug, an dessen Haltepunkt das Flugzeug schon vorbei ist, könnte auch gleich auf die Spur
  gehen, statt erst zum Haltepunkt zu fahren. **Behoben in beta.44.feuerwehr.14:** Ein FLF, das schon beim Flugzeug ist und mehr als
  200 m näher dran als FLF 1, begleitet; nach dem Abrollen bleibt es dabei. Wer noch unterwegs ist und dessen Haltepunkt das Flugzeug
  schon passiert hat, fährt gleich auf die Spur; die Kolonne ordnet sich nach dem Abstand, ein spät kommendes FLF 1 überholt nicht.
  Wer nicht begleitet, rückt nach dem Abrollen auch von unterwegs ein.
- **Gebäudebrandschutzwache:** In OSM ist sie als Feuerwache mit Umriss getaggt und zählt deshalb als volle Wache. FLF 1 kam bei
  Mayday von dort. `amenity` und `building` werden gleich behandelt: Treffer unter 80 m werden zusammengelegt, ein Umriss gewinnt für
  die Lage, der Name kommt vom zuerst gefundenen Treffer. Offen: ob Wachen nach Name oder einem weiteren Tag gefiltert werden sollen.
  Ein Tag dafür gibt es in OSM nicht: `fire_station:type` kennt nur `occupation` (Berufsfeuerwehr), `voluntary` und `concern`
  (Werkfeuerwehr), eine Flughafenfeuerwehr ist meist `concern` wie eine Gebäudebrandschutzwache. Zuverlässig wäre nur eine Liste je
  Flughafen; ein Namensfilter (z. B. „Gebäudebrandschutz“) fängt nur Fälle, die man schon kennt. Welche Wache FLF hat, steht in OSM
  nicht. **Gebaut in beta.44.feuerwehr.16:** kein Filter, sondern ein Aufschlag für Wachen abseits der Mitte (siehe „Wache“ oben). In
  EDDK liegt die Hauptwache rund 0,5 km vom Bezugspunkt, die Gebäudebrandschutzwache 2,7 km: Für FLF 1 zur Aufsetzzone der 14L
  stehen grob 1,6 + 1,35 km gegen 1,9 + 0,25 km, die Hauptwache gewinnt.
- **Wache der Bundeswehr:** Die Wache im Südwesten von EDDK („Flughafenfeuerwehr“ in OSM) ist sehr wahrscheinlich die Feuerwache
  Köln-Wahn der Bundeswehr im militärischen Teil; sie sichert die Flugbereitschaft und fährt keine zivilen Einsätze. **Gebaut in
  beta.44.feuerwehr.16:** militärische Wachen fallen für zivile Einsätze weg, wenn OSM es hergibt (militärische Fläche, Betreiber).
  Ob das in EDDK greift, zeigt das Log (`military fire stations left out`); der Zwischenspeicher wird dafür einmal neu abgefragt.
- **Kreuzende Bahnen:** Die Feuerwehrfahrzeuge halten vor kreuzenden Bahnen nicht (keine Logik dafür, Bahnen kosten im Weg nur das
  Achtfache). Ob ein Fahrzeug auf der Anfahrt eine Bahn gekreuzt hat, ist nicht ausgewertet. FLF 1 hielt in Test 2 vor der 06/24 nur,
  weil es dem A320 folgte. **Gebaut in beta.44.feuerwehr.15:** Halt an der Haltelinie und Freigabe nach der Verkehrsabfrage (siehe
  „Bahnen kreuzen“ oben).
- **Heavy im Verkehrstest:** Mit der Wegpunkt-KI startete der A330 durch (an 0,5 NM 110 ft, an der Schwelle 157 ft zu hoch). Für
  Feuerwehr-Tests mit Heavies `kinematicNm` nehmen.

Nachgetestet mit beta.44.feuerwehr.16, siehe nächster Abschnitt.

## Nachtest im Sim (05.11.2026, beta.44.feuerwehr.16, EDDK)

Dieselben zwei Tests wie oben, Bahn 14L, gefahren von Claude; vorher ein kurzer Notfall am Boden an A14 (4 Fahrzeuge, nach dem
Ausrücken mit `reset` abgebrochen). Der erste Alarm nach dem Update nahm noch den alten Zwischenspeicher (ohne Prüfung auf
militärische Flächen) und fragte OSM dabei neu ab; ab dem zweiten Alarm galt der neue Stand.

**Test 1 – Mayday, A330-300 (Kategorie 9) bleibt auf der 14L stehen (A1, kinematisch ab 5 NM):**
- **Wachen:** `military fire stations left out for a civil emergency: Flughafenfeuerwehr` – die Wache der Bundeswehr im Südwesten
  fiel weg (in OSM mit Betreiber Bundeswehr). Alle 8 Fahrzeuge kamen von der Hauptwache (Flughafenfeuerwehr Köln), auch FLF 1, das
  in .13 von der Gebäudebrandschutzwache kam. Die Spawns liefen im Abstand von 4 s, alle mit Weg.
- **Bereitstellung:** FLF 1 bei 240 m, FLF 2 jetzt am Haltepunkt bei 1673 m (in .13 auf der Bahn bei 2105 m), FLF 3 am Bahnende bei
  3569 m, der Rest bei 1673 m. FLF 3 hielt auf der Anfahrt vor der 06/24 und kreuzte nach 2 s (Blaulicht, frei). Alle standen
  2:33 min nach dem Alarm, FLF 3 nach 3:30 min.
- **Landung und Umstellen:** Aufsetzen bei 458 m, Stopp bei 1560 m. FLF 1 ging 8 s vor dem Stopp auf die Spur. 6 s nach dem Stopp
  wurden die Plätze vergeben (dieselben wie in .13). ELW stand nach 23 s, FLF 1 nach 1:13 min, FLF 3 nach 3:00 min (vom Bahnende über
  den Rollweg, wieder mit 2 s Halt vor der 06/24).
- **Einrücken:** Nach `cancel` hielten FLF 1–3, HLF und beide RTF an ihrem Platz vor der 14L (die Rückfahrt führt über die Bahn) und
  warteten auf `object 217448451 on the runway` – das war der A330 selbst. ELW und RTW fuhren ohne Bahn. Den Fehler siehe Befunde;
  nach Entfernen des A330 kreuzten alle sechs nach 97 s. Alle 8 waren 1:37 bis 3:47 min nach dem `cancel` an der Wache verschwunden.
- **DATA_ERROR:** keiner, weder beim Spawn (8 Fahrzeuge, in .13 noch 43 bis 54) noch beim Umstellen und Einrücken, auch nicht beim
  Notfall am Boden und in Test 2. Der verzögerte Start der Z-Variablen (.14) greift.

**Test 2 – Pan Pan, A320neo (Kategorie 6) mit Rollen zu D24 (A3), Alarm 69 s vor dem Aufsetzen:**
- **Wache und Bereitstellung:** FLF 1@240, FLF 2 und ELW @1673, alle von der Hauptwache. FLF 2 und ELW erreichten den Haltepunkt 7 und
  13 s nach dem Aufsetzen, FLF 1 war noch unterwegs.
- **Folgen bei spätem Alarm:** Alle drei gingen 36 s nach dem Aufsetzen auf die Spur (`joining`), FLF 1 direkt von unterwegs, ohne
  erst zur Aufsetzzone zu fahren (neu in .14). FLF 2 und ELW folgten mit 538 und 562 m, FLF 1 mit 1971 m.
- **Begleiter:** Nach dem Abrollen über A2 (2243 m) begleitete FLF 2 (deutlich näher als FLF 1), FLF 1 und ELW rückten ein, FLF 1
  von unterwegs. FLF 2 hielt danach 47 bis 59 m (Soll 50) bis zum Gate.
- **06/24:** Der A320 hielt 4 s vor der 06/24 auf A. FLF 2 kreuzte als Begleiter hinter ihm ohne eigenen Halt. Der ELW auf der
  Rückfahrt hielt an der Haltelinie und wartete 9 s, bis der A320 von der 06/24 herunter war – richtig, ein rollendes Flugzeug soll
  zählen.
- **Am Gate:** Der A320 stand auf D24, FLF 2 48 m dahinter, bis zum `cancel`. Die Rückfahrt von D24 zur Hauptwache führte über
  keine Bahn, FLF 2 war nach 4:11 min an der Wache.

**Prüfpunkte aus beta.44.feuerwehr.16:**
- **Haltepunkte:** in Ordnung, kein Fahrzeug auf einer Bahn.
- **Begleiter, später Alarm, Kolonne:** in Ordnung (Test 2).
- **Halt vor Bahnen:** in Ordnung auf der Anfahrt und beim Aufstellen (06/24, je 2 s), Fehler auf der Rückfahrt über die Bahn des
  Notfalls (siehe Befunde).
- **Wachenwahl:** Bundeswehr raus, Hauptwache für alle. Auch der Notfall am Boden an A14 (bei den Terminals) kam von der Hauptwache,
  nicht von der näheren Gebäudebrandschutzwache – so gewollt, solange unklar ist, ob dort Fahrzeuge stehen.
- **DATA_ERROR:** keiner.
- **Spawnhöhe:** in Ordnung, laut Florian kommen die Fahrzeuge nicht mehr aus dem Boden.
- **RTF-Treppe und -Licht:** Auf Florians Screenshot von der Hauptwache (07:24) wirkt die Treppe eingefahren; die Bestätigung im Sim
  steht noch aus.

**Befunde:**
- **Rückfahrt über die Bahn des Notfalls:** Die Verkehrsabfrage für die Freigabe läuft ohne Texte (`withText: false`), die Kennung
  DUO01 fehlte also, und `IsEmergencyAircraft` verglich nur sie. Der stehende A330 zählte damit als Verkehr auf der 14L, und alle
  sechs Fahrzeuge mit der Bahn auf dem Rückweg warteten, bis der Test beendet war. **Behoben in beta.44.feuerwehr.17:** Duopilo merkt
  sich die Objekt-ID des Verkehrstests und erkennt das Flugzeug daran. **Dazu in beta.44.feuerwehr.18:** Das Flugzeug zählt nur
  nicht, solange es steht (unter 3 kt); rollt es noch (Rollout, Pan Pan über eine Bahn), wartet das Fahrzeug wie vor jedem anderen.
  Sonst hätte .17 den ELW in Test 2 direkt hinter dem rollenden A320 über die 06/24 geschickt.
- **Plätze an der Hauptwache:** Die Fahrzeuge kommen nicht mehr aus dem Boden, erscheinen aber wie auf Florians Screenshot vom
  Morgen auf der Fläche vor der Wache statt vor den Hallentoren. In OSM liegt `amenity=fire_station` auf dem ganzen Grundstück (rund
  10 400 m² mit Hof und Zufahrten), die Halle (L-förmig, rund 2 400 m²) ist ein eigenes Gebäude darauf, Tore sind nicht eingetragen.
  Duopilo nahm das Grundstück als Hallenumriss und stellte die Fahrzeuge 4 m vor dessen Rand; dazu zählte das Grundstück als
  Gebäude-Hindernis. **Behoben in beta.44.feuerwehr.19:** Das größte Gebäude auf dem Gelände ist die Halle, die Plätze liegen 4 m vor
  ihrer Seite zum Hof, das Gelände selbst ist kein Hindernis mehr. Liegen die Tore in der Szenerie woanders, helfen nur Tore in OSM
  (`door=overhead` an der Hallenwand), die gewinnen immer.
- **Halteposition direkt am Platz:** Die Plätze am Flugzeug liegen 42 m neben der Mittellinie, also innerhalb von halber Breite plus
  40 m. `FireCrossings` findet davor keine Stelle mit genug Abstand und lässt die Fahrzeuge an ihrem Platz halten. Das ist in Ordnung
  (sie stehen ohnehin an der Bahn), im Status steht dann aber schon am Platz `holdingShort: 14L/32R`.

Nachgetestet mit beta.44.feuerwehr.19, siehe nächster Abschnitt.

## Nachtest im Sim (05.11.2026, beta.44.feuerwehr.19, EDDK)

Dieselben zwei Tests wie oben, Bahn 14L, gefahren von Claude, Florian sah im Sim zu.

**Test 1 – Mayday, A330-300 (GE, Kategorie 9) bleibt auf der 14L stehen (A1, kinematisch ab 5 NM, Alarm 2 s nach dem Start):**
- **Wachen:** `military fire stations left out for a civil emergency: Flughafenfeuerwehr`, danach `Fire stations at EDDK from
  OpenStreetMap: Flughafenfeuerwehr Köln (hall), Gebäudebrandschutzwache (outline)`. Die Hauptwache gilt jetzt als Halle auf ihrem
  Gelände. Alle 8 Fahrzeuge (3 FLF, ELW, HLF, 2 RTF, RTW) kamen von dort, alle mit Weg, Spawns im Abstand von 4 s.
- **Plätze an der Halle:** Nachgerechnet aus dem Zwischenspeicher (`cache\fire-stations\EDDK.json`): Gelände 10 400 m², Halle
  2 383 m². Gewählt ist die 45 m lange Wand im Winkel zwischen den beiden Flügeln, die zum Hof zeigt (Blick 47°, nach Nordosten),
  die Plätze 4 m davor im Abstand von 6 m. FLF 1 stand 3 s nach dem Spawn 2–3 m vor Platz 1 und rollte in Blickrichtung heraus.
  Laut Florian kamen alle Fahrzeuge außer dem RTW aus der richtigen Richtung. Der RTW stand auf Platz 8, und der liegt im Gebäude
  (siehe Befunde).
- **Bereitstellung:** wie in .16: FLF 1 bei 240 m (2:09 min nach dem Alarm), FLF 2 und der Rest bei 1673 m (FLF 2 nach 1:31 min,
  RTF 2 als letztes nach 2:05 min), FLF 3 am Bahnende bei 3569 m. FLF 3 hielt auf der Anfahrt 2 s vor der 06/24.
- **Landung:** Aufsetzen bei 458 m mit 135 kt, Stopp bei 1559 m 30 s später. FLF 1 ging erst 21 s nach dem Aufsetzen auf die Spur,
  rund 9 s vor dem Stopp, als der A330 langsamer als 40 kt war (siehe Befunde).
- **Umstellen:** 6 s nach dem Stopp wurden die Plätze vergeben (dieselben wie in .13 und .16). ELW stand nach 30 s, HLF, FLF 2, RTW
  und RTF 1 nach 38 bis 47 s, RTF 2 nach 69 s, FLF 1 nach 1:19 min. **FLF 3 war beim Stopp noch nicht am Bahnende:** Es erreichte
  seinen Haltepunkt erst 68 s nach dem Stopp (3:44 min nach dem Alarm), drehte dort um und fuhr über das Rollweg-Netz (2,3 km, wieder
  2 s Halt vor der 06/24) zum Flugzeug. Es stand 4:07 min nach dem Stopp.
- **Rückfahrt über die 14L:** Nach `cancel` meldeten FLF 1–3, HLF und beide RTF eine Sekunde später an ihrem Platz `holding short
  of runway 14L/32R` und kreuzten alle 5 s danach (`crossing runway 14L/32R after 5 s`). Der stehende A330 zählte nicht mehr als
  Verkehr. ELW und RTW fuhren ohne Bahn. Alle 8 waren 1:48 bis 2:33 min nach dem `cancel` an der Wache verschwunden (in .16: 1:37 bis
  3:47 min, davon 97 s Warten).
- **DATA_ERROR:** keiner im Log während des Tests, weder beim Spawn noch beim Umstellen und Einrücken;
  `traffic-messages.csv` ohne Fehler.

**Test 2 – Pan Pan, A320neo (Kategorie 6) mit Rollen zu D20 (A3, kinematisch ab 5 NM), Alarm 83 s vor dem Aufsetzen:**
- **Wache und Bereitstellung:** FLF 1@240, FLF 2 und ELW @1673, alle von der Hauptwache. FLF 2 und ELW erreichten den Haltepunkt 8
  und 14 s nach dem Aufsetzen, FLF 1 war noch unterwegs.
- **Folgen:** Alle drei gingen 35 s nach dem Aufsetzen auf die Spur, als der A320 langsamer als 40 kt war, FLF 1 von unterwegs. FLF 2
  und ELW folgten mit 541 und 562 m, FLF 1 als Letztes der Kolonne mit 1769 m (Soll 130 m). Laut Florian wie in Test 1: FLF 1 fährt
  zu spät los und hängt weit zurück.
- **Begleiter:** Nach dem Abrollen über A2 (2243 m) begleitete FLF 2 mit 52 m (Soll 50). FLF 1 und ELW rückten ein, FLF 1 war nach
  2:13 min an der Wache.
- **06/24:** Der A320 hielt 5 s vor der 06/24 auf A, rollte dann auf der Bahn gut 30 m an ihrer Mittellinie entlang und verließ sie
  auf der anderen Seite. FLF 2 kreuzte als Begleiter hinter ihm ohne eigenen Halt. Der ELW fuhr auf der Rückfahrt denselben Weg hinter
  FLF 2 her, hielt an der Haltelinie und kreuzte nach 5 s, ohne zu warten. Nach der Spur des Verkehrstests (Schwellen der 06/24 von
  [SkyVector](https://skyvector.com/airport/EDDK/Koeln-Bonn-Airport), gut ±15 m) war der A320 beim Halt des ELW noch rund 9 m neben
  der Mittellinie, beim Kreuzen 5 s später 23 bis 27 m, also gerade an der Kante (halbe Breite 22,5 m). Ob ein rollendes Flugzeug des
  Notfalls auf der Bahn als Verkehr zählt (.18), zeigt dieser Fall nicht eindeutig.
- **Am Gate:** Der A320 stand um 17:51:27 auf D20. FLF 2 rückte 61 s später ein, zugleich mit dem Ende des Verkehrstests (`parked`,
  A320 nach 60 s am Stand entfernt), und war nach 3:49 min an der Wache.
- **DATA_ERROR:** keiner.

**Prüfpunkte aus beta.44.feuerwehr.19:**
- **Rückfahrt über die Bahn des Notfalls bei stehendem Flugzeug:** in Ordnung (Halt am Platz, Freigabe nach 5 s).
- **Hauptwache als Halle:** in Ordnung, Log `Flughafenfeuerwehr Köln (hall)`, Plätze vor der Hallenwand zum Hof, die Fahrzeuge
  kamen laut Florian aus der richtigen Richtung; Ausnahme Platz 8 (RTW).
- **DATA_ERROR:** keiner.
- **RTF-Treppe und -Licht:** in Ordnung laut Florian. Die gemeinsamen Data Definitions je Variablenname waren also die Ursache
  (geändert in .14, siehe Ergebnis 05.11.).

**Befunde** (Florian im Sim, dazu Log und Quelle):
- **FLF 1 fährt zu spät los:** Es blieb an der Aufsetzzone stehen, bis der A330 fast stand. Heute fährt ein Fahrzeug erst auf die Spur,
  wenn das Flugzeug vorbei und langsamer als 40 kt ist (`FireOperation.JoinBelowMps`). Gewünscht: losfahren, sobald das Flugzeug
  vorbei ist, und rund 90 m dahinter folgen (heute Soll auf der Bahn bei Heavy 100 m, `FireCategory.RunwayGapM`). In Test 2 dasselbe:
  FLF 1 war beim Aufsetzen noch unterwegs und ging erst mit 1769 m Abstand auf die Spur, als Drittes der Kolonne mit Soll 130 m.
  **Geändert in beta.44.feuerwehr.20:** auf die Spur, sobald das Flugzeug vorbei ist, ohne Grenze für sein Tempo; auf der Bahn bei
  Heavy 90 m.
- **FLF 3 fährt zu spät los:** Es soll schon beim Aufsetzen losfahren, nicht erst, wenn das Flugzeug steht. Heute fährt es auf jeden
  Fall zu seinem Haltepunkt am Bahnende: Ein Fahrzeug, das noch unterwegs und mehr als 300 m (`DirectRangeM`) vom stehenden Flugzeug
  weg ist, fährt seinen Weg zu Ende und bekommt seinen Platz erst am Haltepunkt (`UpdateWaiting`). Mit 3:44 min Anfahrt kam es hier
  erst 68 s nach dem Stopp dort an. **Geändert in beta.44.feuerwehr.20:** Das FLF am Bahnende fährt ab dem Aufsetzen über die Bahn
  entgegen (auch von unterwegs) und hält vor der vorausgerechneten Stoppstelle; steht das Flugzeug, fahren alle hin, egal wie weit
  (siehe „Entgegen vom Bahnende“ und „Umstellen“ oben).
- **FLF 3 über die Bahn statt über Rollwege und Straßen:** Vom Bahnende soll es über die 14L zum Flugzeug fahren. Heute nimmt `Position()`
  von weiter als 800 m (`PositionRouteM`) den Weg über das Rollweg-Netz, und darin kostet jede Bahn das Achtfache, auch die Bahn des
  Notfalls. Geradeaus fährt nur, wer schon auf derselben Bahn steht; der Haltepunkt am Bahnende liegt 151 m neben der Mittellinie.
  **Geändert in beta.44.feuerwehr.20:** Nach der Landung zählt die Bahn des Notfalls beim Routen wie ein Rollweg
  (`TaxiGraph.FreeRunway`), beim Aufstellen und auf dem Weg auf die Spur.
- **Kein Halt an Kreuzungen mit Blaulicht:** FLF 3 hielt auf der Anfahrt und auf dem Weg zum Flugzeug je 2 s vor der 06/24 (Mindesthalt
  mit Blaulicht, dann Verkehrsabfrage). Laut Florian hat die Feuerwehr im Einsatz Vorfahrt und soll nicht warten; bei Mayday sei der
  Flughafen ohnehin zu. Vorschlag: Mayday ohne Halt und ohne Verkehrsabfrage, dafür die Bahnsperre für den eigenen KI-Verkehr bauen
  (steht unter „Noch nicht gebaut“; Live-Verkehr des Sims steuert Duopilo nicht); Pan Pan ohne Mindesthalt, aber mit Verkehrsabfrage,
  weil dort nichts gesperrt ist. Von Florian so bestätigt. **Gebaut in beta.44.feuerwehr.20** (siehe „Bahnen kreuzen“ und „Sperre für
  den eigenen KI-Verkehr“ oben).
- **Fahrzeuge je Kategorie:** Zwei RTF und nur drei FLF passen laut Florian nicht, die meisten Flughäfen haben nur ein RTF. Nachgesehen:
  Düsseldorf hat 8 FLF (6 im ICAO-Löschzug, 3 je Wache, 2 in Reserve) und 1 RTF
  ([Wikipedia](https://de.wikipedia.org/wiki/Flughafenfeuerwehr_D%C3%BCsseldorf)), Stuttgart 4 FLF und 1 RTF
  ([Feuerwehrmagazin](https://www.feuerwehrmagazin.de/wissen/werkfeuerwehr-flughafen-stuttgart-95061)), BER 12 FLF, davon 8 im Dienst,
  an 3 Wachen ([Faktenblatt FBB](https://corporate.berlin-airport.de/content/dam/corporate/de/unternehmen-presse/newsroom/2022-03-31%20Faktenbaltt_Werkfeuerwehr%20FBB.pdf)).
  Die 3 FLF ab Kategorie 8 in `FireCategory.Lineup` sind die Mindestzahl nach ICAO Annex 14, die ein Flughafen vorhalten muss, nicht
  die Zahl, die zu einem Mayday ausrückt; RTF gibt es dort ab Kategorie 8 zwei. Zu ändern: ein RTF. Entschieden (Florian, 05.11.): bei
  Mayday ab Kategorie 8 vier FLF. **Geändert in beta.44.feuerwehr.20:** ein RTF, bei Mayday ab Kategorie 8 vier FLF (FLF 4 an der
  Mitte, am Flugzeug vorn rechts).
- **Platz 8 in der Halle:** `FireExits.ForOsm` legt an die 45-m-Wand 8 Plätze in eine Reihe (`(Länge − 2) / 6 + 1`), von der Mitte
  aus 6, 12, 18 und 24 m nach den Seiten. Platz 8 liegt damit 24 m von der Mitte, 1,5 m über das Ende der Wand hinaus. Dort springt
  ein Flügel der Halle 19 m nach vorn: Der Platz liegt 1,1 m in diesem Flügel, und sein Herausrollen (12 m) führt durch ihn. Der RTW
  auf diesem Platz kam als einziges Fahrzeug aus der falschen Richtung. Es fehlt die Prüfung, ob Platz und Herausrollen frei vom
  Gebäude sind (sonst die nächste Reihe, 9 m weiter vorn), und die Reihe darf nicht über die Wand hinausgehen. **Behoben in
  beta.44.feuerwehr.20:** beides; an der Hauptwache liegen jetzt 7 Plätze in der ersten Reihe (höchstens 18 m von der Mitte), der achte
  in der zweiten.
- **Abstellen nach einem Notfall:** Laut Florian parkt ein Flugzeug mit Feuerwehreinsatz aus Sicherheitsgründen frei auf dem Vorfeld,
  nicht am Gebäude; nur bei Feuerwehreinsätzen, sonst bleibt die Wahl der Parkposition wie bisher. In Test 2 rollte der A320 an das
  Gate D20. Vorschlag: Ist die Feuerwehr für ein KI-Flugzeug alarmiert, plant der Verkehrstest das Rollziel neu (nach dem Abrollen ab
  der aktuellen Stelle): eine Position ohne Fluggastbrücke, lieber Vorfeld als Gate, mit Abstand zu den Gebäuden aus OSM und passend
  zur Größe. Ob eine Position eine Brücke hat, weiß Duopilo heute nicht: Es fragt nur `TAXI_PARKING` ab, und `gate_small` sagt dazu
  nichts. SimConnect hat am Flughafen `JETWAY`-Einträge (im Stub vorhanden, nicht abgefragt); Rückfall wären die Fluggastbrücken aus
  OSM (`aeroway=jet_bridge`), die die Feuerwehr schon als Hindernisse lädt.

Nachgetestet mit beta.44.feuerwehr.20, siehe nächster Abschnitt.

## Nachtest im Sim (07.11.2026, beta.44.feuerwehr.20, EDDK)

Dieselben zwei Tests, Bahn 14L, gefahren von Claude, Florian sah im Sim zu.

**Test 1 – Mayday, A330-300 (GE, Kategorie 9) bleibt auf der 14L stehen (A1, kinematisch ab 5 NM, Alarm 2 s nach dem Start):**
- **Sperre:** Mit dem Alarm `Airport EDDK closed for own AI traffic: fire service (Mayday)`, nach `cancel` `Airport EDDK open again
  for own AI traffic`. Der A330 selbst ist ausgenommen und landete (`landing_clear 14L`).
- **Fahrzeuge:** `FLF 1@240, FLF 2@1673, FLF 3@3569, FLF 4@1673, ELW 1@1673, HLF 1@1673, RTF 1@1673, RTW 1@1673` – vier FLF, ein
  RTF, alle von der Hauptwache (`(hall)`), alle mit Weg, Spawns im Abstand von 4 s.
- **Bereitstellung:** an der Mitte FLF 2 nach 1:31 min, FLF 4 1:38, ELW 1:53, HLF 1:59, RTF 2:05, RTW 2:12. FLF 1 war beim Aufsetzen
  noch unterwegs zur Aufsetzzone. Bis zur Rückfahrt hielt kein Fahrzeug vor einer Bahn (in .19 FLF 3 zweimal 2 s vor der 06/24).
- **Landung:** Aufsetzen bei 458 m mit 135 kt, Stopp bei 1559 m 29 s später. FLF 1 ging im selben Moment auf die Spur (in .19 erst
  9 s vor dem Stopp), von unterwegs, und folgte 24 s später mit 1012 m.
- **Umstellen:** Plätze 6 s nach dem Stopp, FLF 4 vorn rechts (42/42). FLF 4 stand nach 30 s, ELW 36, HLF 37, FLF 2 40, RTW 44,
  RTF 50 s, FLF 1 nach 54 s (in .19 1:19 min).
- **FLF 3:** Beim Aufsetzen `approaching over runway 14L/32R while the aircraft rolls out`. Es war noch unterwegs zum Bahnende, westlich
  der Bahnmitte, und bekam als Ziel die Mittellinie dort, wo der A330 gerade war – an der Aufsetzzone. Damit fuhr es nach Nordwesten,
  vom Flugzeug weg. Nach dem Stopp lag es hinter dem Flugzeug, sein Haltepunkt aber vor ihm, und dafür gab es keine Regel: Es bekam
  seinen Platz erst am Ende des Wegs, 2:00 min nach dem Stopp, und stand 2:57 min nach dem Stopp (in .19 4:07 min). Siehe Befunde.
- **Rückfahrt:** Nach `cancel` hielten FLF 1–4, HLF und RTF an ihrem Platz vor der 14L und kreuzten nach 5 s, ELW und RTW fuhren ohne
  Bahn. Alle 8 waren 1:58 bis 2:27 min nach dem `cancel` an der Wache verschwunden.
- **DATA_ERROR:** keiner.

**Test 2 – Pan Pan, A320neo (Kategorie 6) mit Rollen zu D18 (A3, kinematisch ab 5 NM), Alarm 81 s vor dem Aufsetzen:**
- **Wache und Bereitstellung:** FLF 1@240, FLF 2 und ELW @1673, alle von der Hauptwache. FLF 2 und ELW erreichten den Haltepunkt 10
  und 16 s nach dem Aufsetzen, FLF 1 war noch unterwegs.
- **Folgen:** FLF 1 ging 1 s nach dem Aufsetzen auf die Spur (in .19 35 s danach), brauchte aber 65 s bis auf die Spur (im Mayday
  24 s; 40 s nach dem Aufsetzen standen im Status noch 5 m Rest bei 10 kt, Ursache offen) und folgte dann mit 1769 m. FLF 2 und ELW
  gingen 23 s nach dem Aufsetzen auf die Spur, als der A320 an ihnen vorbei war (in .19 35 s, unter 40 kt), und folgten mit 553 und
  563 m.
- **Begleiter:** Nach dem Abrollen über A2 begleitete FLF 2 mit 54 m (Soll 50), FLF 1 und ELW rückten ein.
- **06/24:** wie in .19. Der A320 hielt 4 s vor der 06/24 auf A und rollte gut 30 m auf ihr entlang. Der ELW fuhr hinter FLF 2 her,
  hielt an der Haltelinie, als der A320 noch 8 m neben der Mittellinie war, und kreuzte 5 s später bei 24 m (Kante). .18 bleibt damit
  offen. Mit Blaulicht kreuzte in diesem Ablauf niemand eine Bahn, die Abfrage im Anrollen (Pan Pan) kam nicht vor.
- **Am Gate:** Der A320 stand um 07:57:06 auf D18, FLF 2 rückte 61 s später ein (zugleich mit dem Ende des Verkehrstests).
- **DATA_ERROR:** keiner.

**Prüfpunkte aus beta.44.feuerwehr.20:**
- **FLF 1 früher auf der Spur:** in Ordnung (beide Tests beim Aufsetzen); im Pan Pan wegen des späten Alarms weiter weit zurück.
- **FLF 3 entgegen über die Bahn:** Fehler, siehe Befunde.
- **Kreuzen ohne Halt bei Mayday, Sperre für den Verkehrstest:** in Ordnung.
- **Vier FLF und ein RTF:** in Ordnung.
- **DATA_ERROR:** keiner.

**Befunde:**
- **FLF 3 fährt vom Flugzeug weg:** `StartApproach` nahm als Ziel die Mittellinie an der Stelle des Flugzeugs beim Aufsetzen. Das passt
  nur für ein FLF, das schon am Bahnende steht; von unterwegs (westlich der Bahnmitte) führte der Weg zur Aufsetzzone. Und
  `UpdateWaiting` gab einem Fahrzeug hinter dem stehenden Flugzeug nur dann einen Platz, wenn das Flugzeug auch an seinem Haltepunkt
  vorbei war. **Behoben in beta.44.feuerwehr.21:** Ziel ist die Mittellinie an der erwarteten Stoppstelle (2,0 m/s²), und steht das
  Flugzeug, fährt auch hin, wer hinter ihm zu einem Haltepunkt vor ihm unterwegs ist (Test
  `Spaeter_Mayday_FLF_3_faehrt_von_unterwegs_zur_Stoppstelle_nicht_zur_Aufsetzstelle`).
- **FLF 1 im Pan Pan 65 s bis auf die Spur:** offen; im Status 40 s nach dem Aufsetzen noch 5 m Rest. Beim nächsten Test die Lage von
  FLF 1 beim Aufsetzen festhalten.

Nachgetestet mit beta.44.feuerwehr.21, siehe nächster Abschnitt.

## Nachtest im Sim (07.11.2026, beta.44.feuerwehr.21, EDDK)

Nur Test 1 (Mayday, A330-300, A1 kinematisch ab 5 NM, Alarm 1 s nach dem Start), Bahn 14L, gefahren von Claude, Florian sah im Sim zu.
Ablauf wie in .20: dieselben 8 Fahrzeuge von der Hauptwache, Sperre mit dem Alarm, Aufsetzen bei 457 m mit 135 kt, Stopp bei 1560 m
30 s später.

- **FLF 3 entgegen:** Beim Aufsetzen stand es noch vor der 06/24 (siehe Befunde), dann `approaching over runway 14L/32R` – diesmal zur
  erwarteten Stoppstelle, also von vorn auf das Flugzeug zu. Es stand 30 s nach dem Stopp an seinem Platz (in .20 2:57 min, in .19
  4:07 min). Der Fix aus .21 greift.
- **Umstellen:** Plätze 5 s nach dem Stopp. Nach dem Stopp standen FLF 4 nach 29 s, FLF 3 30, ELW 35, HLF 36, FLF 2 39, RTW 43, RTF
  49 s, FLF 1 nach 61 s.
- **FLF 1:** erreichte seinen Haltepunkt an der Aufsetzzone genau beim Aufsetzen, ging 1 s später auf die Spur und brauchte 32 s bis auf
  sie (Anfahren mit 1,1 m/s², `FireProfile`). Die 65 s im Pan Pan unter .20 waren wohl ein längerer Weg von unterwegs; nicht weiter
  untersucht.
- **Rückfahrt:** Nach `cancel` `Airport EDDK open again for own AI traffic`, FLF 1–4, HLF und RTF kreuzten die 14L nach 5 s.
- **DATA_ERROR:** keiner.

**Befunde:**
- **Halt vor der 06/24 vor der Landung:** FLF 3 stand bei Mayday mit Blaulicht rund 20 s vor der 06/24, bis der A330 aufsetzte, ohne
  Eintrag im Log (`holdingShort: 06/24` im Status). `NeedsClearance` ist bei Mayday falsch, aber die Freigabe ohne Abfrage gab
  `FireOperation.Update` erst nach der Landung; der FireService fragt bei Mayday nicht ab. In .20 war es genauso (FLF 3 war deshalb
  beim Aufsetzen noch unterwegs). Mit einem Alarm 20 NM vor der Landung hätten alle vor jeder Bahn bis zur Landung gewartet.
  **Behoben in beta.44.feuerwehr.22:** Diese Freigaben gibt der Einsatz in jedem Takt, auch vor der Landung (Test
  `Mayday_kreuzt_schon_vor_der_Landung_ohne_Halt`).

Nachgetestet mit beta.44.feuerwehr.22, siehe nächster Abschnitt.

## Nachtest im Sim (07.11.2026, beta.44.feuerwehr.22, EDDK)

Test 1 (Mayday, A330-300, A1 kinematisch ab 5 NM, Alarm 1 s nach dem Start), Bahn 14L, gefahren von Claude, Florian sah im Sim zu.
Aufsetzen bei 458 m mit 135 kt, Stopp bei 1560 m 30 s später.

- **Kein Halt vor der Landung:** FLF 3 fuhr 1:40 min nach dem Alarm mit 38 kt über die 06/24 (in .20 und .21 rund 20 s Halt bis zum
  Aufsetzen). Bis zur Rückfahrt stand kein Fahrzeug vor einer Bahn. Der Fix aus .22 greift.
- **FLF 3:** Am Bahnende war es beim Aufsetzen trotzdem noch nicht (Weg rund 3,5 km, Alarm nur 2 min vor dem Aufsetzen); es kam von
  unterwegs zur erwarteten Stoppstelle entgegen und stand 40 s nach dem Stopp an seinem Platz. Bei einem Alarm ab 20 NM ist es
  rechtzeitig am Bahnende.
- **Bereitstellung und Umstellen:** An der Mitte standen FLF 2 nach 1:32 min, FLF 4 1:39, ELW 1:53, HLF 1:59, RTF 2:06, RTW 2:12, FLF 1
  an der Aufsetzzone 2:09 (beim Aufsetzen, gleich auf die Spur, 31 s bis auf sie). Nach dem Stopp standen FLF 4 nach 30 s, ELW 36, HLF
  37, FLF 2 und FLF 3 40, RTW 45, RTF 50, FLF 1 nach 60 s.
- **Rückfahrt:** wie in .21 (Sperre aufgehoben, 14L nach 5 s gekreuzt), alle 8 nach 1:57 bis 2:30 min an der Wache verschwunden.
- **Hauptwache:** Laut Florian kommen jetzt alle Fahrzeuge aus der richtigen Richtung, auch der RTW. Der Fix aus .20 (Plätze nie im
  Gebäude) ist damit bestätigt.
- **DATA_ERROR:** keiner.

Noch offen: freies Abstellen nach dem Notfall (.23); ob ein rollendes Flugzeug des Notfalls auf der Bahn als Verkehr zählt (.18);
Abfrage im Anrollen mit Blaulicht (Pan Pan, eine Anfahrt über eine Bahn); Landung auf der anderen Bahn, Alarm per Funk (Mayday/Pan Pan
im ATC-Modul), Ausrücken ab 20 NM bei eigenem Anflug (dann auch FLF 3 vor dem Aufsetzen am Bahnende), Abstand zum Abgasstrahl.

Test 2 (Mayday ab 20 NM, A330-300, A1 kinematisch ab 12 NM, Alarm 1 s nach dem Start), Bahn 14L, gefahren von Claude. Aufsetzen
7:21 min nach dem Alarm bei 458 m mit 135 kt, Stopp bei 1558 m 31 s später.

- **Bereitstellung:** Alle 8 standen vor dem Aufsetzen: FLF 2 nach 1:31 min, FLF 4 1:38, ELW 1:52, HLF 1:58, RTF 2:05, FLF 1 2:08,
  RTW 2:11, FLF 3 am Bahnende nach 3:32 (3:49 min vor dem Aufsetzen). Damit ist auch FLF 3 vor dem Aufsetzen am Bahnende bestätigt.
  Die 5 min Wartezeit auf den Posten überstanden alle ohne Halt oder Umweg.
- **Nach dem Aufsetzen:** FLF 3 fuhr 2 s danach über die 14L entgegen (`approaching over runway 14L/32R`), FLF 1 schloss gleich an
  (bei 47 s 1060 m Abstand). Nach dem Stopp standen FLF 4 nach 29 s, ELW 35, HLF 37, FLF 2 39, RTW 44, RTF 50, FLF 1 57, FLF 3 78 s
  (rund 2 km vom Bahnende, im Mittel 37 kt). Vor keiner Bahn ein Halt.
- **Rückfahrt:** Sperre aufgehoben, 14L nach 5 s gekreuzt (6 Fahrzeuge hielten 5 s, ELW und RTW mussten nicht halten).
- **Modell:** Das A330-300 (GE) ist ein volles Modell (Lichter alle 15 s neu gesendet). Für die nächsten Tests nehmen wir lieber
  „Asobo PassiveAircraft A330-300“ (gleiche Länge, Kategorie 9; steht noch auf `untested`).

Noch offen: freies Abstellen nach dem Notfall (.23, Pan Pan mit A3 und Rollen zur Position); .18; Abfrage im Anrollen mit Blaulicht;
Landung auf der anderen Bahn; Alarm per Funk; Abstand zum Abgasstrahl.
