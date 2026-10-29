# Kopilot

Kopilot verbindet **Microsoft Flight Simulator 2024** über SimConnect mit **Claude**. Er läuft als MCP-Server auf dem Sim-PC. Claude kann damit Höhe, Geschwindigkeit, Konfiguration, Navigation und Wetter selbst ablesen, statt dass du sie vorlesen musst. Claude ist so der **Pilot Monitoring**, du bleibst Pilot Flying.

> Kopilot liest nur. Er schaltet nichts im Flugzeug.

## Tools

| Tool | Inhalt |
|---|---|
| `kopilot_status` | Verbindung, Pause, geladenes Flugzeug (Titel + Pfad), aktives Profil, Monitoring-, SimBrief- und ECAM-Status |
| `get_flight_state` | Höhe (IND, FL, AGL, Radio), IAS/TAS/GS/VS in kt, Lage, Kurs, Position, Klappen, Fahrwerk, Spoiler, Parkbremse, Autobrake, AP/A-THR mit FMA, Speed- und Höhenziel, Warnungen |
| `get_systems` | Triebwerke (N1, N2, EGT, Fuel Flow in kg/h), APU, Treibstoff, Gewicht, Lichter, Anschnallzeichen, Transponder, COM, Plausibilitätshinweise |
| `get_warnings` | Master Warning/Caution, ECAM-Meldungen im Klartext (FBW A32NX, Headwind A330-900neo), Stall/Overspeed |
| `get_alerts` | Meldungen des Monitorings seit einer ID, mit Callout zum Vorlesen |
| `wait_for_alerts` | Wartet bis zu 25 s auf die nächste Monitoring-Meldung (Dauerüberwachung in einer Schleife) |
| `get_monitoring_status` | Flugphase, aktive/abgeschaltete Regeln, Ruhezeit |
| `get_fma_log` | Verlauf der FMA-Wechsel mit Uhrzeit, IAS und Radiohöhe – z. B. ob nach dem Autoland ROLL OUT aktiv war |
| `get_flight_plan` | SimBrief-OFP: Route, Bahnen, Reiseflughöhe, Cost Index, Fuel, Gewichte, Zeiten, Navlog |
| `get_progress` | Fortschritt gegen den Plan: nächster Fix (beim A32NX der aktive FMS-Wegpunkt, z. B. nach DIR TO), Distanz/ETA, TOD, Querablage, Fuel gegen Plan |
| `get_atc` | Ingame-ATC: Callsign, Freigaben, zugewiesene Bahn (inkl. L/C/R aus SimBrief) |
| `get_traffic` | Verkehr im Umkreis (KI und Multiplayer): Callsign, Typ, Abstand, Uhrzeit-Peilung, Höhendifferenz, Trend, GS, Callout fürs nächste Ziel |
| `get_navigation` | GPS-Flugplanvariablen des Sims (bei Airliner-FMS meist leer – dann `get_progress`) |
| `get_environment` | Wind, OAT, QNH, eingestellter Höhenmesser, Sicht, UTC-/Ortszeit |
| `get_aircraft_profile` | Bindings des Profils mit Rohwert, Klartext und „geprüft“-Status, Zusatzvariablen, Grenzwerte |
| `read_simvar` | Liest eine beliebige SimVar oder L:-Variable einmalig (Text-Variablen mit `unit: "string"`) |
| `reload_profiles` | Lädt die Profildateien neu |
| `list_actions` | Eingabe-Modul: welche Schalter Kopilot im aktuellen Flugzeug bedienen darf, und ob Eingaben freigegeben sind |
| `prepare_action` | Eingabe vorbereiten: prüft Wert und Sperren, liefert den Readback – im Cockpit ändert sich noch nichts |
| `confirm_action` | Eingabe ausführen, nachdem der Pilot den Readback bestätigt hat; danach wird der Zustand im Flugzeug geprüft |
| `cancel_action` | Vorbereitete Eingabe verwerfen |
| `mark_action_verified` | Aktion als geprüft merken, nachdem du im Cockpit gesehen hast, dass sie funktioniert |
| `atc_transmit` | Kopilot-ATC: Funkspruch des Piloten weitergeben, Antwort der Station kommt zurück (und wird gesprochen) |
| `get_atc_controller` | Kopilot-ATC: aktuelle Station mit Frequenz und Stimme, erwarteter nächster Pilotenspruch, offenes Readback, Squawk, Verlauf |
| `atc_control` | Kopilot-ATC: Sitzung starten/beenden/neu starten, Vorlagen neu laden, letzten Funkspruch wiederholen |
| `atc_set_value` | Kopilot-ATC: Gate, Rollhalt, Rollroute, Pushback-Richtung, Anflugart oder Bahn von Hand setzen |

Jede Antwort enthält `activeAlerts`: die gerade aktiven Monitoring-Meldungen. So bekommt Claude Warnungen auch mit, wenn es eigentlich etwas anderes abfragt.

## Pilot Monitoring

Kopilot prüft alle 200 ms ein paar Regeln und legt Meldungen mit einem kurzen englischen Callout ab:

| Regel | Callout | Bedingung |
|---|---|---|
| `GEAR_SPEED` | Gear speed | Fahrwerk nicht eingefahren und IAS über VLE |
| `FLAP_SPEED` | Flap speed | IAS über VFE der Klappenstellung |
| `OVERSPEED` | Overspeed | IAS über VMO oder Overspeed-Warnung des Sims |
| `STALL` | Stall | Stall-Warnung des Sims in der Luft |
| `SINK_RATE` | Sink rate | unter 2500 ft RA mehr als 1500–3000 ft/min Sinken |
| `BANK_ANGLE` | Bank angle | mehr als 35° (unter 150 ft RA enger, über 2500 ft RA 45°) |
| `MASTER_WARNING` / `MASTER_CAUTION` | Master caution, ELEC GEN 1 OFF | Master-Licht an, mit der zuletzt aufgetauchten ECAM-Meldung |
| `ECAM_MESSAGE` | HYD G RSVR LO LVL | weitere ECAM-Meldung, während Master Warning/Caution schon leuchtet |
| `GEAR_DOWN_HIGH` | Gear still down | Fahrwerk draußen im Steigflug über 2000 ft AGL oder über FL150 |
| `SPEED_LIMIT_FL100` | Speed limit | unter 10.000 ft schneller als 250 kt |
| `SPEED_DEVIATION` | Speed | IAS weicht vom Ziel ab (+15/−10 kt, im Anflug +10/−5 kt) und läuft nicht darauf zu |
| `AP_DISCONNECT` | Autopilot off | Autopilot in der Luft getrennt |
| `ALT_1000_TO_GO` | One thousand to go | 1000 ft vor der FCU-Höhe |
| `TOP_OF_DESCENT` | Top of descent | A32NX: FMS-Meldung T/D REACHED; sonst TOD laut SimBrief-Plan |
| `LANDING_CHECK` | Landing checklist complete / Spoilers not armed | bei 2000 ft RA im Sinkflug: Fahrwerk, Landeklappen, Ground Spoiler; bei 1000 ft RA erneut, falls noch etwas fehlt |
| `RUNWAY_ASSIGNED` | Runway two three left assigned | Ingame-ATC hat eine Bahn zugewiesen |
| `TRAFFIC_PROXIMITY` | Traffic, 2 o'clock, 2 miles, 500 feet above, climbing | Ziel in der Luft näher als 3 NM und innerhalb ±1000 ft, eigenes Flugzeug über 500 ft AGL; danach 60 s Ruhe (siehe [Verkehr](#verkehr)) |
| `SIM_PAUSED` | Sim paused | Sim pausiert – alle anderen Regeln ruhen |

Dazu kommen die Callouts, die ein Pilot Monitoring macht:

| Regel | Callout | Wann |
|---|---|---|
| `PM_100_KNOTS`, `PM_V1`, `PM_ROTATE` | One hundred knots / V1 / Rotate | Startlauf (V1/VR aus der MCDU, beim A32NX) |
| `PM_POSITIVE_CLIMB` | Positive climb | nach dem Abheben, sobald es wirklich steigt |
| `PM_SPOILERS` | Spoilers / No spoilers | nach dem Aufsetzen (No spoilers nach 3 s ohne Ground Spoiler) |
| `PM_REVERSE` | Reverse green | Schubumkehr ausgefahren |
| `PM_DECEL` | Decel | Autobrake DECEL (A32NX), sonst spürbare Verzögerung |
| `PM_70_KNOTS` | Seventy knots | Landerollen unter 70 kt |
| `PM_TEN_THOUSAND` | Ten thousand | durch 10.000 ft, steigend und sinkend |
| `PM_TRANSITION` | Transition altitude / Transition level | TA im Steigflug, TL im Sinkflug (aus dem FMS, sonst SimBrief) |
| `PM_FLAPS` | Speed checked, flaps two / Flaps zero | Klappenhebel steht 1,5 s in neuer Stellung; beim Ausfahren mit Speed-Check gegen VFE |
| `PM_GEAR` | Gear down / Gear up | Fahrwerkshebel in der Luft |
| `TAXI_SPEED` | Taxi speed | am Boden länger als 2 s über 25 kt Groundspeed (nicht im Start- und Landerollen), höchstens alle 30 s – einstellbar in `voice-settings.json` |

Radiohöhen (2500, 1000, 500 …), „Hundred above“, „Minimum“ und „Retard“ sagt der A32NX selbst an. Kopilot kann sie zusätzlich sprechen – für Flugzeuge ohne eigene Ansagen: Dashboard → Ansagen → Stimme, oder `Kopilot:Monitoring:AircraftAutoCallouts`.

Nach dem Laden eines Flugs, einem Flugzeugwechsel oder einem Air-Start schweigt das Monitoring 10 Sekunden, nach einer Pause 3 Sekunden – dann springen die Werte. Einzelne Regeln schaltest du in `appsettings.Local.json` ab:

```jsonc
"Kopilot": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

**Push:** Claude.ai und der Sprachmodus können keine Nachrichten vom Server empfangen. Deshalb spricht Kopilot die Callouts selbst (siehe unten). Claude sieht die Meldungen zusätzlich über `activeAlerts` in jeder Antwort oder über `wait_for_alerts`.

## Verkehr

`get_traffic` fragt per SimConnect (`RequestDataOnSimObjectType`) alle Flugzeuge und Hubschrauber im Umkreis ab – KI-Verkehr und Multiplayer, soweit im Sim eingeschaltet. Das eigene Flugzeug ist ausgeschlossen.

| Parameter | Standard | Bedeutung |
|---|---|---|
| `radiusNm` | 40 | Suchradius, höchstens 108 NM (200 km, Grenze von SimConnect) |
| `limit` | 10 | höchstens so viele Ziele, nächstes zuerst (1–50) |
| `airborneOnly` | false | Flugzeuge am Boden (Gate, Rollweg) weglassen |

Pro Ziel: `ident` (Callsign, sonst ATC-ID), `type` (ATC MODEL, z. B. `A320`), `distanceNm`, `clock` („2 o'clock“), `relativeBearingDeg`, `bearingTrueDeg`, `altitudeFt`, `altitudeDiffFt` (+ = darüber), `verticalTrend` (climbing/descending/level ab ±500 ft/min), `groundSpeedKt`, `headingTrueDeg`/`headingMagDeg`, `onGround`. Das nächste Ziel hat `closest: true`; `callout` ist die Verkehrsmeldung dazu:

```
Traffic, 2 o'clock, 5 miles, 1000 feet above, descending
```

- **Uhrzeit:** relativ zum eigenen Steuerkurs, je 30°: 12 Uhr = 345° bis unter 15°.
- **Abstand:** horizontal (Großkreis). Höhen sind bei beiden Flugzeugen wahre Höhe über MSL (`PLANE ALTITUDE`), deshalb weicht die eigene Höhe leicht von `get_flight_state` (indicated) ab.
- **Trend:** aus `VERTICAL SPEED`. Liefert ein Ziel dort 0 (oft bei Multiplayer), rechnet Kopilot die V/S aus der Höhenänderung zwischen zwei Abfragen (`verticalSpeedSource: "derived"`).
- **Keine Daten:** `status: "no_traffic"` mit Hinweis, wenn MSFS kein fremdes Flugzeug liefert – meist ist dann KI-Verkehr bzw. Multiplayer im Sim aus. `no_response`, wenn MSFS gar nicht antwortet.

**Verkehrswarnung `TRAFFIC_PROXIMITY`:** Solange die Regel an ist und das Flugzeug fliegt, fragt Kopilot alle 3 s den Verkehr im Umkreis von 6 NM ab. Unter 500 ft über Grund, in der Pause und am Boden ruht sie. Abschalten mit `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]`, Grenzen anpassen:

```jsonc
"Kopilot": { "Monitoring": { "Traffic": { "ProximityRangeNm": 3, "ProximityAltitudeFt": 1000, "ProximityCooldownSec": 60, "ScanIntervalSec": 3 } } }
```

Das ist kein TCAS: keine Ausweichempfehlung, keine Vorhersage des Annäherungspunkts – nur Abstand und Höhe im Moment.

## Sprachausgabe

Kopilot spricht die Callouts selbst, auf Englisch. Warnings unterbrechen alles andere, ältere Meldungen verfallen nach 8 Sekunden. ECAM-Titel werden ausgeschrieben, z. B. „ENG 1 FIRE“ → „engine 1 fire“.

Es gibt zwei Stimmen:

| | Windows-Stimme | Kokoro |
|---|---|---|
| Klang | Sprachausgabe von Windows | neuronal, natürlicher |
| Voraussetzung | englische Windows-Stimme installiert | nichts – Modell (~310 MB) lädt Kopilot beim ersten Bedarf |
| Verzögerung | praktisch keine | Rechenzeit auf der CPU; feste Callouts werden vorab berechnet |

**Automatisch** (Standard) nimmt die Windows-Stimme, wenn eine englische installiert ist, sonst Kokoro. Solange Kokoro lädt, liest der Browser im Dashboard vor.

Im Dashboard unter **Ansagen → Stimme** stellst du ein:
- Sprachausgabe: Automatisch, Windows oder Kokoro
- Windows-Stimme (z. B. George oder Hazel für en-GB) bzw. Kokoro-Stimme (z. B. George, Emma, Michael)
- Ausgabegerät, z. B. das Headset statt der Lautsprecher
- Lautstärke und Tempo
- ab welcher Stufe gesprochen wird (alle, ab Caution, nur Warnings)
- ob Radiohöhen/Minimum/Retard zusätzlich kommen

Dort steht auch, welche Stimme gerade spricht und wie lange Kokoro für die letzte Ansage gerechnet hat. Ein- und ausschalten geht auch im Tray-Menü (**Callouts sprechen**).

Die Einstellungen liegen in `%LOCALAPPDATA%\Kopilot\speech.json`, das Kokoro-Modell unter `%LOCALAPPDATA%\Kopilot\models`. Gesprochen wird nur von der Tray-Instanz, nicht von der stdio-Instanz, die Claude Desktop startet – sonst käme alles doppelt. Kokoro rechnet mit 2 CPU-Threads (`Kopilot:Speech:KokoroThreads`), damit MSFS nicht ausgebremst wird.

**Englische Windows-Stimme nachinstallieren** (optional, für die Windows-Stimme): Einstellungen → Zeit und Sprache → Sprache und Region → Sprache hinzufügen → *English (United Kingdom)*, dabei „Sprachausgabe“ auswählen. Danach Kopilot neu starten.

Einzelne Callouts stumm schalten (weiter sichtbar, aber nicht gesprochen): `"mutedRules": [ "PM_GEAR" ]` in `speech.json`. Ganz abschalten: `Kopilot:Monitoring:DisabledRules`.

Lizenzen: Kokoro-82M und seine Stimmen stehen unter Apache 2.0, KokoroSharp unter MIT.

## Eingabe-Modul

Kopilot kann im Cockpit Knöpfe drücken und Schalter stellen – als Pilot Monitoring auf Ansage des Piloten. Jede Eingabe hat zwei Schritte:

1. Du sagst z. B. „Set speed two five zero“. Claude ruft `prepare_action` auf, Kopilot prüft Wert und Sperren und gibt den Readback zurück: „Speed 250 selected“.
2. Claude liest den Readback vor. Erst wenn du „confirm“ sagst, ruft Claude `confirm_action` auf. Kopilot prüft die Sperren noch einmal mit dem aktuellen Zustand, führt aus und liest danach im Flugzeug nach, ob der Wert angekommen ist.

Sagst du „negative“ oder korrigierst den Wert, wird die Aktion verworfen. Eine vorbereitete Aktion verfällt nach 60 Sekunden, es gibt immer nur eine.

**Standardmäßig gesperrt.** Freigeben im Dashboard: in der Ansagen-Leiste zweimal auf **Eingabe gesperrt** klicken („Wirklich freigeben?“). Dort siehst du auch die offene Aktion mit Countdown – auch eine, die über Claude Desktop vorbereitet wurde – und kannst sie abbrechen. Darunter steht das Protokoll aller Eingaben. Alternativ `Kopilot:Input:Enabled` in `appsettings.Local.json`; der Schalter im Dashboard hat Vorrang und gilt für alle Kopilot-Instanzen.

**Was geht (A32NX):**

| Bereich | Aktionen |
|---|---|
| FCU | Speed (selected/managed), Heading (selected/managed), Altitude setzen, Pull/Push, Increment 100/1000, V/S, LOC, APPR, EXPED, AP 1/2, A/THR |
| EFIS | QNH in hPa, STD/QNH, LS, FD, ND-Modus und -Reichweite (Kapitän) |
| Konfiguration | Fahrwerk, Klappenhebel, Speedbrake, Ground Spoiler armen |
| Bremsen | Autobrake OFF/LO/MED/MAX |
| Licht | Beacon, Wing, Runway Turn-off, Landing (on/off/retract), Nose (T.O/Taxi/off), Strobe (on/auto/off) |
| Schilder | Seat Belts, No Smoking/No PED |
| Anti-Ice | Triebwerke (beide oder einzeln), Flügel, Probe/Window Heat |
| Air/APU/Elec | Pack 1/2, APU Master, APU Start, APU Bleed, External Power (nur am Boden, mit GPU) |
| Transponder | Squawk, Modus, ALT RPTG, TCAS-Modus und Traffic |
| Funk | COM 1 Standby, Swap |

NAV & LOGO fehlt: Der Schalter ist beim A32NX nur als Input Event (`B:`) erreichbar, das kann Kopilot noch nicht.

**Was nie geht:** Triebwerks-Master, Zündung/Start, Schub, Feuer-Handles und -Agents, Treibstoff, MCDU, Pause/Slew/Sim-Steuerung. Das ist im Code festgelegt, auch ein bearbeitetes Profil kommt nicht daran vorbei:
- Kopilot sendet nur Events aus einer festen Liste (FCU, Autobrake, Fahrwerk, Klappen, Spoiler, Licht, Schilder, Transponder, COM).
- Kopilot schreibt nur L:-Variablen, nie Standard-SimVars, und auch dort greift die Sperrliste.
- Fahrwerk und Klappen gehen nur über ihre Events, und nur zusammen mit der passenden Sperre.
- Es gibt kein Tool, das beliebige Events oder Variablen schreibt.

**Sperren:**
- Fahrwerk einfahren nur in der Luft
- Fahrwerk ausfahren nur bis VLO (A320: 250 kt)
- Klappen nur bis zur VFE der neuen Stellung

Fehlt ein Grenzwert im Profil, ist die Aktion gesperrt. Dazu kommen Bedingungen je Aktion, z. B. V/S und Heading nicht im TRK/FPA-Modus, QNH nur im QNH-Modus, APU Start nur mit APU Master.

Die Aktionen stehen im Flugzeugprofil unter `actions` (Events, Variablen, Readback, Prüfung). Sie sind aus dem FBW-Quellcode abgeleitet. Ob eine Aktion angekommen ist, zeigt das Protokoll im Dashboard (✓/✗).

**Wie Kopilot prüft:**
- Werte werden absolut gesetzt. Steht der Wert schon, schreibt Kopilot nichts und meldet `alreadySet`.
- Der neue Wert muss eine Weile stehen bleiben (`holdMs`, Standard 1 s). Setzt das Flugzeug ihn sofort zurück – z. B. APU START ohne Strom –, gilt die Aktion als nicht übernommen.
- `verification` in der Antwort:
  - `confirmed`: Kopilot hat einen echten Flugzeugzustand gelesen (FCU-Anzeige, Klappenhebel, Licht, ENG ANTI ICE, Schütz …).
  - `unverified`: Kopilot konnte nur die Schalterstellung lesen, die Wirkung ist nicht nachgewiesen.
  - `failed`: Das Flugzeug hat den Wert nicht übernommen.
- `stateAfter` ist der Rohwert der geprüften Variable – bei Wahlschaltern die Stellung (z. B. Seatbelts 0 = ON). `stateAfterText` nennt die passende Auswahl (`on`, `auto`, `off` …).
- Es ist immer nur eine Aktion offen. Jedes neue `prepare_action` verwirft die offene – auch wenn es selbst abgelehnt wird. Ein älterer, langsamer Aufruf legt danach keine Aktion mehr an; der Token aus der Antwort gehört also immer zur offenen Aktion. Mit einem alten Token sagt `confirm_action`, was aus der Aktion geworden ist (ersetzt, abgelaufen, schon verwendet, andere Kopilot-Instanz).
- Fehler kommen immer als `status: "error"` mit Grund zurück, Details im Log.

**Geprüfte Aktionen merken:** Im Sim getestet sind fcu.heading, flaps, lights.beacon, antiice.engines und apu.bleed (`"verified": true` im Profil). Weitere merkt sich Kopilot in `%LOCALAPPDATA%\Kopilot\verified-actions.json` – das überschreibt `update.cmd` nicht:
- automatisch, wenn eine Ausführung `confirmed` war;
- per `mark_action_verified`, wenn du nach einer `unverified`-Ausführung im Cockpit siehst, dass es funktioniert hat. Dafür reicht ein Bestätigungs- oder Zustandswort („confirm“, „check“, „on“, „off“, „avail“, „läuft“), der ausgeführte Wert oder der Name der Aktion („Seatbelts on“, „APU avail“, „Confirm, no smoking, auto“) – solange nichts verneint wird. `confirm_action` bleibt streng.

Ändert sich die Definition einer Aktion im Profil, gilt die gespeicherte Prüfung nicht mehr. `list_actions` zeigt je Aktion `verified`: `profile`, `sim`, `pilot` oder leer.

## ATC

Kopilot bringt ein eigenes ATC mit: eine Zustandsmaschine mit festen Textvorlagen, **kein Sprachmodell**. Du funkst wie gewohnt, Claude gibt deinen Spruch mit `atc_transmit` weiter, Kopilot antwortet als die zuständige Stelle und spricht die Antwort selbst – jede Station mit eigener Kokoro-Stimme. Phraseologie auf Englisch im ICAO-Stil, Zahlen Ziffer für Ziffer („squawk two two zero five“), Geschwindigkeiten immer in Knoten, nie Mach.

**Ablauf** (Übergänge kommen aus deinen Sprüchen und aus dem Flugzustand):

| Station | Was passiert | Weiter zur nächsten Station |
|---|---|---|
| Delivery | IFR-Freigabe mit SID, Bahn, Steigflughöhe und Squawk; Readback wird geprüft | nach korrektem Readback: „readback correct, contact Ground“ |
| Ground | Push and start, Rollfreigabe zum Rollhalt (Readback Rollhalt/Bahn) | am Rollhalt stehen geblieben (8 s), oder du rufst „ready“ |
| Tower | Line up and wait, Startfreigabe mit Wind (automatisch, sobald du auf der Bahn stehst, spätestens nach 60 s) | ab 1500 ft AGL: „contact Departure“ |
| Departure | „identified, climb FL150“, optional „proceed direct“ zum ersten Fix nach der SID | 2000 ft unter der freigegebenen Höhe → Center der FIR |
| Center | Steigflug auf Reiseflughöhe, am Top of Descent Sinkflug auf FL110 | an jeder FIR-Grenze ans nächste Center |
| Oceanic | Freigabe ca. 30 Minuten vor dem Eintrittspunkt, danach Positionsmeldungen an jedem Oceanic-Fix („roger“) | am Austritt ans nächste Center |
| Approach | Sinkflug, STAR, erwarteter Anflug und Bahn, QNH; ab 20 nm Anflugfreigabe | ab 12 nm: „contact Tower“ |
| Tower | Landefreigabe mit Wind (spätestens 4 nm vor der Schwelle) | nach der Landung unter 40 kt: „vacate via next exit, contact Ground“ |
| Ground | Rollen zum Gate | Ende, wenn das Flugzeug mit Parkbremse steht |

Auf Kurzstrecken ohne Oceanic-Fixes (Koordinaten wie `55N020W` oder eine FIR, die in den Vorlagen als `oceanic` markiert ist) entfällt Oceanic. Startest du ohne Freigaben, springt das ATC beim Startlauf zum Tower nach. Startest du die Sitzung in der Luft, beginnt sie bei der Stelle, die dort zuständig ist.

**Readback:** Bei Freigaben prüft Kopilot die Werte aus der Vorlage (`readback`), z. B. SID, Höhe und Squawk bei der Streckenfreigabe. Kopilot versteht Ziffern („2205“, „FL110“) wie gesprochene Zahlen („two two zero five“, „five thousand feet“, „one two one decimal eight“) und das NATO-Alphabet. Ist ein Wert falsch oder fehlt, kommt „negative“ mit dem richtigen Wert, z. B. „Lufthansa four alpha bravo, negative, squawk two two zero five“ – danach musst du nur diesen Wert noch einmal zurücklesen. Quittungen ohne Freigabe („contact Ground one two one decimal eight“) beantwortet ATC nicht. „Say again“ wiederholt den letzten Funkspruch.

**Daten:**
- Rufzeichen, Ziel, SID/STAR, Bahnen, Reiseflughöhe, FIRs und Oceanic-Teil aus dem SimBrief-OFP (SimBrief muss eingerichtet sein). Ohne FIR-Angaben im OFP teilt Kopilot den Nordatlantik bei 30°W in Shanwick und Gander.
- Höhe, Position, Speed, Wind, QNH und Zeit aus SimConnect. QNH am Ziel aus dem METAR des OFP, in der Nähe des Flughafens aus dem Sim. Nordamerika: „altimeter“ in inHg.
- Frequenzen aus der Facilities-API von MSFS (Clearance, Ground, Tower, Departure, Approach). Fehlen sie, nimmt Kopilot die Frequenz aus der Vorlage oder einen Platzhalter. `get_atc_controller` zeigt die Quelle (`frequencySource`).
- Squawk: pro Flug fest, ohne Sondercodes.
- Rollroute (`{taxiRoute}`), Rollhalt, Gate und Pushback-Richtung kommen über die Schnittstelle `ITaxiRouteProvider`. Die Routenberechnung kommt in einem eigenen Schritt; bis dahin nehmen die Vorlagen eine Variante ohne diese Angaben („taxi to holding point runway two five center“). Gate oder Rollhalt kannst du mit `atc_set_value` von Hand setzen („wir stehen an A26“).

**Stimmen:** Jede Station (z. B. Frankfurt Delivery, Langen Radar, Shanwick Oceanic) bekommt pro Flug zufällig eine Stimme aus `voices.pool` – möglichst jede eine andere, nie die Stimme der Callouts. Nach einem Neustart von Kopilot bleiben die Stimmen im selben Flug gleich. Feste Stimmen unter `voices.stations`, z. B. `"EDDF/tower": "bf_emma"`, `"FIR/EGGX": "bm_daniel"` oder `"delivery": "af_heart"` für alle Delivery-Stationen. Für ATC lädt Kopilot Kokoro auch dann, wenn die Callouts die Windows-Stimme nehmen; solange Kokoro lädt, spricht die englische Windows-Stimme. Eine Warning unterbricht einen laufenden Funkspruch. Gesprochen wird nur von der Tray-Instanz (HTTP-Modus); über die stdio-Instanz von Claude Desktop gibt es die Antwort nur als Text.

**Vorlagen bearbeiten:** Alle Texte stehen in `atc-templates.json` neben der `Kopilot.exe`. Eigene Änderungen am besten in einer Kopie unter `%LOCALAPPDATA%\Kopilot\atc-templates.json` – die hat Vorrang und überschreibt `update.cmd` nicht. Danach `atc_control` mit `reload` (oder Kopilot neu starten).

```jsonc
"delivery.clearance": {
  "variants": [
    "{callsign}, cleared to {dest}, {sid} departure, runway {runway}, climb {initialAlt}, squawk {squawk}",
    "{callsign}, cleared to {dest}, runway {runway}, climb {initialAlt}, squawk {squawk}"   // ohne SID
  ],
  "readback": [ "sid", "initialAlt", "squawk" ]
}
```

- Mehrere Varianten: Die erste, deren Variablen alle bekannt sind, gewinnt.
- `"from": "pilot"` sind die erwarteten Pilotensprüche – Hinweise für dich und Claude, keine Pflicht.
- `corrections`: was nach „negative“ je Readback-Punkt gesagt wird.
- `telephony`: ICAO-Kennung → Rufzeichen („DLH“ → „Lufthansa“).
- `airports`: Rufname, Höhe der ersten Steigflugfreigabe, abweichende Stationsnamen (`"stations": { "departure": "Langen Radar" }`) und feste Frequenzen (`"frequencies": { "tower": "119.900" }`, Vorrang vor der Facilities-API).
- `firs`: Name und Frequenz der Kontrollstelle je FIR, `"oceanic": true` für Oceanic-Gebiete.
- `stationTypes`: Namen der Stationsarten und Platzhalter-Frequenzen.
- `rules`: Schwellwerte der Zustandsmaschine (Höhen, Entfernungen, Zeiten).

Variablen: `{callsign}` `{station}` `{dest}` `{sid}` `{star}` `{approach}` `{runway}` `{initialAlt}` `{squawk}` `{freq}` `{taxiRoute}` `{holdingPoint}` `{gate}` `{level}` `{speedKt}` `{entryFix}` `{heading}` `{wind}` `{qnh}` `{fix}` `{eta}` `{nextFix}`, dazu `{direction}` (Pushback), `{altitude}` (aktuelle Höhe), `{nextCenter}` (nächste Stelle bei Übergaben), `{route}` (Oceanic-Route), `{time}` (UTC), `{corrections}` und `{levelVerb}` (climb/descend/maintain). Kopilot prüft die Datei beim Laden: fehlende Funksprüche, unbekannte Variablen, Readback-Punkte ohne Korrektur und jede Erwähnung von Mach werden gemeldet (`get_atc_controller` → `templateErrors`, Log).

Einstellungen in `appsettings.Local.json` unter `Kopilot:Atc`: `Enabled` (Standard an), `TemplatesFile`, `Speak` (Funksprüche sprechen). Im Browser zeigt `http://127.0.0.1:5155/atc/controller` den Zustand.

## Sprachschleife

Du sprichst direkt mit Kopilot – ohne Claude, ohne Push-to-Talk (VR-tauglich), komplett lokal und ohne Sprachmodell:

```
Mikrofon → Silero VAD (Satzgrenzen) → Whisper (Abschrift) → Router ─┬→ Kopilot-Befehle → Eingabe-Modul
                                                                    └→ ATC-Modul
                                     Sprachausgabe (Kokoro) ←───────┘   Warnungen > Callouts > Antworten > ATC
```

**Einschalten:** in `%LOCALAPPDATA%\Kopilot\voice-settings.json` (legt Kopilot beim ersten Start aus der Vorlage an) `"enabled": true` setzen und Kopilot neu starten. Beim ersten Start lädt Kopilot das Whisper-Modell (`base-q5_1`, ~60 MB) und Silero VAD (~2 MB) nach `%LOCALAPPDATA%\Kopilot\models`. Für Befehle muss außerdem die Eingabe im Dashboard freigegeben sein. Status: `http://127.0.0.1:5155/voice` (zuletzt erkannter Satz, Zuordnung, Antwort, Rechenzeit).

**So läuft es:**
- Das Mikrofon ist dauerhaft offen. Silero VAD erkennt, wann du sprichst; nach 0,7 s Stille geht der Satz an Whisper.
- Halbduplex: Solange Kopilot spricht (und 350 ms danach), ist das Mikrofon stumm – Kopilot hört sich nicht selbst. Während du sprichst, warten Callouts und Antworten kurz; nur Warnungen fallen dir ins Wort.
- Whisper erkennt je Satz Deutsch oder Englisch. Zahlen werden vereinheitlicht: „Flaps one“, „Flaps 1“, „Flaps won“, „Klappen eins“ ergeben dasselbe.
- **Router:** Enthält der Satz ein Stationsrufzeichen („Frankfurt Tower“, „Langen Radar“, „Shanwick“) oder beginnt er mit dem eigenen Rufzeichen (aus `voice-settings.json` oder MSFS), geht er ans ATC-Modul. Alles andere ist ein Kopilot-Befehl. Mit „Kopilot, …“ immer Kopilot.
- Sim pausiert → Kopilot sagt nichts und verarbeitet nichts.

**Befehle** (feste Muster, deutsch und englisch, Beispiele):

| Bereich | Sätze |
|---|---|
| Konfiguration | Gear up/down, Fahrwerk ein/aus · Flaps 0/1/2/3/Full, Klappen eins/voll · Speedbrake retract/half/full · Arm/Disarm spoilers, Spoiler armen |
| Bremsen, Licht, Schilder | Autobrake low/medium/max/off · Landing lights on/off, Landelichter an · Strobe on/auto/off · Beacon on · Taxi light on · Seat belts on/off, Anschnallzeichen an |
| APU | APU start · APU master on/off · APU bleed on/off |
| EFIS, FCU | QNH 1013 / QNH one zero one three · Baro standard · Altitude five thousand · Flight level one two zero · Speed two five zero · Heading zero niner zero · Managed speed · Vertical speed minus one thousand · Autopilot one on · Approach on |
| Transponder | Squawk seven thousand / Squawk 1 2 3 4 · TCAS TA/RA · TCAS TA only |

Jeder Befehl läuft über den bestehenden Zwei-Schritt-Ablauf: Kopilot ruft `prepare_action` auf und spricht den Readback („Flaps one“). Erst auf **„Confirm“**, **„Correct“** oder **„Affirm“** (auch „Ja“, „Korrekt“) folgt `confirm_action`; **„Negative“** oder **„Cancel“** verwirft die Aktion („Cancelled“). Ohne Antwort verfällt sie nach 60 Sekunden still. Ausgeführt sagt Kopilot nichts – nur „Not confirmed“, wenn das Flugzeug den Wert nicht übernommen hat, „Unable“ bei einer Sperre und „Input disabled“, wenn die Eingabe gesperrt ist. Zwei Befehle in einem Satz oder Unbekanntes → „Say again“ (längeres Gespräch im Raum wird still verworfen).

**Abfragen** – Antwort nur als reiner Callout, Geschwindigkeiten immer in Knoten:

| Frage | Antwort (Beispiel) |
|---|---|
| Speed check | Speed two five zero (am Boden: Ground speed one two) |
| Altitude check | Altitude five thousand / Flight level three five zero (bei STD) |
| Fuel check | Fuel twelve point four tons |
| Engine check | Engines normal / Engine two not running / N1 split |
| Gear, Flaps, Speedbrake, Spoilers, Seat belts, APU, Baro, Heading check | Gear down · Flaps one · Speedbrake retracted · Spoilers armed · … |

**Steuerung:** „Kopilot Pause“ / „Kopilot weiter“ oder das Tastenkürzel (Standard `Ctrl+Alt+K`, systemweit) schalten die Spracheingabe stumm und wieder an – Callouts laufen weiter. „Say again“ wiederholt die letzte Antwort.

**Sprachausgabe:** Kokoro mit fester Stimme für Kopilot (`voices.kopilot`, leer = Dashboard-Stimme), ATC-Stationen mit eigenen Stimmen (je Station fest). Zahlen, NATO-Alphabet und Funk-Phraseologie auf Englisch: Speed, Heading, Squawk und QNH Ziffer für Ziffer, Höhen in Tausendern, 9 als „niner“. Eine unterbrochene Antwort oder ATC-Meldung kommt nach der Warnung einmal erneut.

**ATC-Modul** (`feature/atc-templates`): implementiert `IAtcVoiceModule` (`src/Kopilot/Voice/AtcVoice.cs`) und registriert sich vor `AddKopilotVoice()`. Es bekommt jeden Funkspruch als `PilotTransmission` (Text, erkannte Station, Rufzeichen), gibt Antworten als `AtcTransmission` zurück und meldet sich über `IAtcRadio` auch von selbst. Ohne ATC-Modul bleiben Funksprüche unbeantwortet (nur im Log).

**Einstellungen** (`voice-settings.json`, Kommentare erlaubt):

| Abschnitt | Was |
|---|---|
| `microphone` | Gerät (Teil des Namens, leer = Standard), VAD-Schwelle, Mindestlänge, Stille bis Satzende, Nachlauf nach Ansagen |
| `recognition` | Whisper-Modell (`tiny`/`base`/`small`, auch `-q5_1`/`-q8_0`), `cpu`/`gpu`, Threads, Sprache `auto`/`de`/`en`, Wortschatz-Hinweis |
| `voices` | Kokoro-Stimme für Kopilot, Stimmen für ATC, Lautstärke je Seite |
| `hotkeys` | Tastenkürzel für stumm/an |
| `commands` | Anrede, Pflicht zur Anrede (`requireWakeWord`), Timeout für die Bestätigung |
| `atc` | eigene Rufzeichen, weitere Stationen, Rufzeichen aus MSFS |
| `callouts` | aktive und stumme Regeln, Stille bei Pause, Taxi-Speed-Warnung |

**Ressourcen** (MSFS hat Vorrang): Standard ist Whisper auf der CPU mit 4 Threads – gerechnet wird nur, wenn du gesprochen hast. `gpu` versucht CUDA, dann Vulkan, und fällt sonst auf die CPU zurück; neben MSFS in VR kann das Ruckler kosten. Schneller wird es mit fester Sprache (`"language": "de"`), `"audioContext": 768` oder dem Modell `tiny-q5_1`; besser Deutsch versteht `small-q5_1` (~190 MB).

Lizenzen: Whisper.net und whisper.cpp (MIT), Silero VAD (MIT), NAudio (MIT).

## SimBrief

Die Airliner-FMS (iniBuilds, FlyByWire) geben ihre Route nicht über SimConnect heraus. Kopilot holt deshalb das letzte OFP von SimBrief (öffentliche API, kein Login). In `appsettings.Local.json`:

```jsonc
"Kopilot": { "SimBrief": { "Username": "dein-simbrief-name" } }   // oder "PilotId": "123456"
```

Gewichte und Treibstoff rechnet Kopilot immer in kg um. Der Plan dient auch dazu, bei der vom ATC zugewiesenen Bahn L/C/R zu ergänzen.

## Voraussetzungen

- Windows 10/11 (x64) mit MSFS 2024
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), z. B. `winget install Microsoft.DotNet.SDK.10`
- **MSFS-2024-SDK** (für SimConnect), siehe unten

### SimConnect-SDK

Kopilot braucht zwei Dateien aus dem MSFS-2024-SDK:

- `SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll`
- `SimConnect SDK\lib\SimConnect.dll`

Das SDK bekommst du über den **Developer Mode** von MSFS 2024: Developer Mode in den Optionen einschalten, dann in der Entwickler-Leiste über das Hilfe-Menü den SDK-Installer starten.

Der Build sucht die DLLs in dieser Reihenfolge:

1. `lib\` im Repo (DLLs einfach hineinkopieren)
2. Umgebungsvariable `MSFS2024_SDK`
3. `C:\MSFS 2024 SDK\`

Findet er nichts, baut er gegen einen **Stub**. Das ist nur für GitHub Actions gedacht, eine Verbindung zu MSFS ist damit nicht möglich. Im Build-Log steht dann eine Warnung. Ein richtiger Build zeigt `Kopilot: SimConnect aus …`.

## Bauen

```powershell
git clone https://github.com/Shin-Jin-Jin/Kopilot
cd Kopilot
dotnet publish src/Kopilot -c Release -o C:\Tools\Kopilot
```

**Aktualisieren:** `update.cmd` im Repo-Ordner doppelklicken. Das Skript holt den neuesten Stand, baut in einen Zwischenordner, beendet alle laufenden Kopilot-Instanzen (auch die von Claude Desktop), kopiert nach `C:\Tools\Kopilot` und startet Kopilot neu. Ein anderer Zielordner geht als Parameter: `update.cmd D:\Kopilot`.

Im Ordner `C:\Tools\Kopilot` müssen nach dem Veröffentlichen `Kopilot.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` und der Ordner `profiles` liegen.

## Erster Test im Browser

1. MSFS 2024 starten und einen Flug laden.
2. `C:\Tools\Kopilot\Kopilot.exe` starten. Ohne Parameter läuft der HTTP-Modus (einstellbar über `Kopilot:Transport`). Es öffnet sich kein Fenster – Kopilot sitzt im Infobereich der Taskleiste.
3. Doppelklick auf das Symbol öffnet das **Dashboard** unter <http://127.0.0.1:5155/>.

## Dashboard und Tray-Symbol

Das Symbol im Infobereich zeigt den Zustand am Punkt unten rechts: grau = wartet auf MSFS, grün = verbunden, gelb = Caution aktiv, rot = Warning aktiv. Rechtsklick öffnet das Menü: Dashboard, Profile neu laden, Logdatei, Programmordner, **Mit Windows starten** und Beenden.

Das Dashboard läuft im Browser, nur lokal auf dem Sim-PC (über den Tunnel ist es gesperrt). Es zeigt:

- FMA-Zeile (Schub, vertikal, lateral, AP/FD/A-THR) in den Airbus-Farben
- Flugdaten mit Speed- und Höhenziel, Master Warning/Caution als Tasten oben rechts
- **Ansagen**: aktive Meldungen und Verlauf. Mit „Vorlesen an“ spricht der Browser jeden Callout mit einer englischen Stimme – Warnings unterbrechen alles andere. Das ist im Moment der einzige echte Push-Weg.
- ECAM im Klartext, Route mit Fortschritt aus SimBrief, Triebwerke, Konfiguration, ATC-Bahn und Wetter
- „Einrichtung und Diagnose“: alle Profil-Bindings mit Status geprüft/ungeprüft und aktuellem Wert, Log, Knöpfe zum Neuladen

Der Regler „Helligkeit“ dimmt nur die Anzeigen – für Nachtflüge.

**Log:** ohne Konsolenfenster schreibt Kopilot nach `%LOCALAPPDATA%\Kopilot\logs\kopilot.log` (die stdio-Instanz von Claude Desktop nach `kopilot-stdio.log`). Zur Fehlersuche gibt es weiterhin eine Konsole: `Kopilot.exe --console`. Ohne Tray-Symbol: `--no-tray`.

Ein zweiter Start von Kopilot öffnet nur das Dashboard der laufenden Instanz.

Die rohen JSON-Daten gibt es weiterhin (nur lokal):

- <http://127.0.0.1:5155/status> zeigt den Status, dort sollte `"connected": true` stehen
- <http://127.0.0.1:5155/state> zeigt den Flugzustand
- `/systems`, `/nav`, `/env`, `/profile`, `/warnings`, `/alerts`, `/monitoring`, `/atc`, `/plan`, `/progress`
- `http://127.0.0.1:5155/read?name=INDICATED ALTITUDE&unit=feet` liest eine einzelne Variable

### Prüfliste

Ein paar Werte habe ich ohne Sim nicht prüfen können:

- [ ] `altitude.indicatedFt` und `speed.iasKt` stimmen mit dem PFD überein
- [ ] Gewicht (`grossWeightKg`) stimmt mit der MCDU (FUEL PRED, GW) überein
- [ ] `pitchUpDeg` ist im Steigflug **positiv**, `bankRightDeg` in einer Rechtskurve **positiv**
- [x] Der Transponder-Code stimmt, z. B. `2000` (getestet 28.10.)
- [ ] `flapsHandleIndex` folgt dem Klappenhebel (0 bis 4 bei 0/1/2/3/FULL)
- [x] Beim iniBuilds A321LR sind die GPS-Navigationswerte leer, das FMS speist sie nicht. Route und Distanz kommen aus SimBrief (`get_progress`).
- [x] Beim iniBuilds A321LR passen die Standard-AP-Werte (ALT/HDG/SPD/VS) zum FCU (getestet 28.10.)
- [ ] FBW: Werte mit `"verified": false` im Profil prüfen (siehe [docs/aircraft.md](docs/aircraft.md))
- [ ] Pause wird erkannt (`paused: true` in `get_flight_state`), auch Active Pause
- [ ] ECAM-Klartext im A32NX (`get_warnings`), z. B. nach AP-Disconnect „AUTO FLT AP OFF“

## Mit Claude Desktop verbinden

Claude Desktop startet Kopilot selbst im stdio-Modus. Trag in `%APPDATA%\Claude\claude_desktop_config.json` ein (in Claude Desktop unter Einstellungen → Entwickler → Konfiguration bearbeiten):

```json
{
  "mcpServers": {
    "kopilot": {
      "command": "C:\\Tools\\Kopilot\\Kopilot.exe",
      "args": ["--stdio"]
    }
  }
}
```

Wenn es schon einen `mcpServers`-Block gibt, kommt nur der `kopilot`-Eintrag dazu. Danach Claude Desktop komplett neu starten. Die Logs schreibt Kopilot nach stderr, Claude Desktop legt sie in seinem Log-Ordner ab (`mcp-server-kopilot.log`).

Für den Sprachmodus, claude.ai im Browser und das Handy gibt es den Remote-Betrieb, siehe nächster Abschnitt.

## Remote-Betrieb / Connector in Claude eintragen

Damit Claude Kopilot auch **im Sprachmodus, im Browser und auf dem Handy** nutzen kann, wird der HTTP-Modus über einen Cloudflare Tunnel erreichbar gemacht. Kopilot selbst bleibt auf dem Sim-PC und hört weiterhin nur auf `127.0.0.1`.

```
Claude (Cloud) ──▶ https://kopilot.flo210500.com/mcp ──▶ Cloudflare ──▶ cloudflared (Dienst) ──▶ 127.0.0.1:5155 ──▶ Kopilot ──▶ MSFS
```

### Zugriffsschutz

`/mcp` ist immer geschützt, auch lokal. Ohne gültigen Token antwortet Kopilot mit `401`. Es gibt zwei Zugangswege:

| Weg | Für | Konfiguration |
|---|---|---|
| **OAuth** | Claude-Connector (claude.ai, Desktop, Handy, Sprachmodus) | `PublicUrl` + `ConsentPassword` |
| **Bearer-Token** | Claude Code, MCP Inspector, eigene Skripte | `BearerToken` |

Beim OAuth-Weg registriert sich Claude selbst (Dynamic Client Registration). Beim Verbinden öffnet sich eine **Freigabeseite von Kopilot**, auf der du das Freigabe-Passwort eingibst. Danach erneuert Claude seinen Zugang selbst: Access-Token 1 Stunde, Refresh-Token 90 Tage, bei jeder Erneuerung ausgetauscht. Nach fünf falschen Passwörtern ist die Freigabe 15 Minuten gesperrt.

Dashboard und Prüfseiten (`/`, `/state`, …) sind nur lokal erreichbar. Kopilot nimmt nur Anfragen an `localhost` und an den Hostnamen aus `PublicUrl` an.

### 1. Geheimnisse anlegen

```bat
C:\Tools\Kopilot\Kopilot.exe --generate-secret | more
```

Das gibt einen zufälligen Bearer-Token aus. (`| more` sorgt dafür, dass die Eingabeaufforderung auf die Ausgabe wartet – Kopilot ist eine Windows-Anwendung ohne eigene Konsole.) Das Freigabe-Passwort denkst du dir selbst aus, mindestens 12 Zeichen.

Dann `appsettings.Local.example.json` im Ordner `C:\Tools\Kopilot` nach **`appsettings.Local.json`** kopieren und ausfüllen:

```json
{
  "Kopilot": {
    "Transport": "http",
    "Remote": {
      "PublicUrl": "https://kopilot.flo210500.com",
      "BearerToken": "<Ausgabe von --generate-secret>",
      "ConsentPassword": "<dein Freigabe-Passwort>"
    }
  }
}
```

Die Datei wird nie eingecheckt und von `update.cmd` nicht überschrieben. Statt der Datei gehen auch Umgebungsvariablen, die haben Vorrang:

```bat
setx Kopilot__Remote__BearerToken "<Token>"
setx Kopilot__Remote__ConsentPassword "<Passwort>"
```

### 2. Kopilot im HTTP-Modus starten

`C:\Tools\Kopilot\Kopilot.exe` starten. Im Log (Dashboard → Einrichtung und Diagnose, oder `kopilot.log`) muss stehen:

```
Zugriffsschutz: Bearer-Token aktiv.
Zugriffsschutz: OAuth aktiv. Connector-URL für Claude: https://kopilot.flo210500.com/mcp
```

Fehlt eine der Zeilen, sagt eine Warnung direkt darunter, was nicht stimmt.

> Die Kopilot-Instanz, die Claude Desktop per `--stdio` startet, bedient kein HTTP. Für den Remote-Betrieb muss Kopilot zusätzlich im HTTP-Modus laufen. Beide Instanzen können gleichzeitig mit MSFS verbunden sein.

### 3. Cloudflare Tunnel einrichten

Schritt für Schritt in **[docs/cloudflared.md](docs/cloudflared.md)**: Named Tunnel, Subdomain `kopilot.flo210500.com`, cloudflared als Windows-Dienst, optional eine WAF-Regel, die `/mcp` nur für Claudes IP-Bereich öffnet.

### 4. Connector in Claude eintragen

1. In Claude **Customize → Connectors** öffnen, dann **Add custom connector**. Bei Team- und Enterprise-Plänen trägt das ein Owner unter *Organization settings → Connectors* ein.
2. Name `Kopilot`, URL **`https://kopilot.flo210500.com/mcp`**.
3. Falls der Dialog danach fragt:
   - Authentication: **Sign in now**
   - OAuth client: **Register automatically**. „Use Claude's published identity“ (CIMD) unterstützt Kopilot nicht.
4. **Add**, dann **Connect**. Es öffnet sich die Kopilot-Freigabeseite. Dort steht als Ziel `claude.ai`. Passwort eingeben, **Freigeben**.
5. In einem neuen Chat über **+ → Connectors** Kopilot einschalten und fragen: „Wie ist unser Flugzustand?“

Der Connector gilt für dein ganzes Konto, also auch für Desktop und Handy. Ob er im Sprachmodus auftaucht, ist noch ungetestet.

### Claude Code und andere Clients (Bearer-Token)

Auf dem Sim-PC direkt, ohne Tunnel:

```bat
claude mcp add --transport http kopilot http://127.0.0.1:5155/mcp --header "Authorization: Bearer <Token>"
```

Von woanders geht `https://kopilot.flo210500.com/mcp` mit demselben Header. Das klappt aber nicht, wenn die optionale WAF-Regel aktiv ist.

### Zugang widerrufen

- **Alle Claude-Verbindungen trennen:** Kopilot beenden, `%LOCALAPPDATA%\Kopilot\oauth-state.json` löschen und Kopilot neu starten. Jeder Connector muss sich danach neu anmelden.
- **Bearer-Token wechseln:** neuen Token erzeugen, in `appsettings.Local.json` eintragen und Kopilot neu starten.
- **Alles sofort zu:** den Tunnel-Dienst stoppen (`sc stop cloudflared`).

Kopilot speichert Tokens nur als SHA-256-Hash. Tokens in der URL (`?token=`) nimmt er grundsätzlich nicht an.

## Flugzeugprofile und L:-Variablen

Study-Level-Flugzeuge legen vieles (AP-Status, FMA, Autobrake, ECAM, Fuel Flow) nicht in Standard-SimVars ab, sondern in eigenen **L:-Variablen**. Ein Profil unter `profiles\*.json` sagt Kopilot, woher die Werte kommen. Mitgeliefert: FlyByWire A32NX, FlyByWire A380X, Headwind A330-900neo, iniBuilds A321LR. Was bei welchem Flugzeug geht, steht in [docs/aircraft.md](docs/aircraft.md).

```jsonc
{
  "name": "FlyByWire A32NX",
  "match": [
    { "path": [ "FlyByWire", "A320" ] },     // aircraft.cfg-Pfad enthält alle Texte (zählt vor dem Titel)
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

- **Bindings** sind feste Schlüssel, die Tools und Monitoring verstehen: `ap1`, `ap2`, `flightDirector`, `athrStatus`, `athrMode`, `fmaLateral`, `fmaLateralArmed`, `fmaVertical`, `fmaVerticalArmed`, `fmgcDiscreteWord1`, `fmAltitudeConstraint`, `autobrake`, `masterWarning`, `masterCaution`, `speedTarget`, `speedManaged`, `selectedSpeed`, `machMode`, `selectedAltitude`, `selectedHeading`, `selectedHeadingDashes`, `vls`, `v1`, `vr`, `v2`, `flexTemp`, `tdReached`, `fmsToWptDistance`, `fmsToWptBearing`, `fuelFlowKgh`, `egtC`, `parkingBrake`, `spoilersArmed`, `flaps`. Typen: `number`, `bool`, `enum` (Wert → Text), `bitmask` (Bit → Text), `string`, `arinc429` (FBW-Datenwort, gilt nur bei Normal Operation). `scale` rechnet um (z. B. lbs/h → kg/h = 0.45359237). Achtung bei Geschwindigkeiten: Manche L:-Variablen speichern intern m/s – dann `"unit": "knots"` setzen, nicht umrechnen.
- **electrical** listet Elektrik-Werte für `get_systems` (Spannung, Frequenz, Busse versorgt; `apuGenOn`/`apuGenVolt`/`apuGenHz`/`apuGenLoad` landen unter `apu.generator`).
- **fuel** listet Treibstoffpumpen und -ventile für `get_systems` (`fuel.pumps`), gleiches Format wie `electrical`.
- **limits.landingFlaps** sind die Klappenstellungen für den Landecheck, `vfeKt` gilt je Stellung (Klartext aus `flaps` oder Hebel-Index).
- **variables** sind freie Zusatzwerte, die `get_aircraft_profile` roh ausgibt.
- **limits** nutzt das Monitoring (Gear/Flap Speed, Overspeed, 250 kt unter FL100).

**ECAM-Klartext:** Das A32NX schreibt nur Codes (z. B. `220000501`) in die EWD-Variablen. Die Texte stehen in der FlyByWire-Tabelle `EwdMessages.ts` (GPL-3.0), beim Headwind A330-900neo in dessen eigener Fassung (Format `hdw-a339x`). Kopilot lädt sie beim ersten Bedarf von GitHub, speichert sie unter `%LOCALAPPDATA%\Kopilot\ecam\` und erneuert sie alle 14 Tage. Ohne Internet bleibt der Cache gültig. Die Tabelle liegt bewusst nicht im Repo.

So findest du neue L:-Variablen:

1. Im Developer Mode das Fenster mit den Behaviors / LocalVariables des Flugzeugs öffnen. Dort sind alle L:-Variablen mit ihren aktuellen Werten gelistet.
2. Im Cockpit etwas umschalten (z. B. NAV am FCU) und schauen, welche Variable sich ändert.
3. Gegenprobe mit `read_simvar` oder im Browser: `/read?name=L:NAME`.
4. Ins Profil eintragen (`"verified": true`, wenn geprüft), dann `reload_profiles` aufrufen.

Wichtig: Eine L:-Variable, die es nicht gibt, liest SimConnect als `0`. Eine 0 beweist also nicht, dass der Name stimmt. Deshalb immer mit einer Zustandsänderung gegenprüfen.

## Aufbau

```
src/Kopilot/
  Program.cs              Start: --stdio (Claude Desktop) oder HTTP mit Tray, --console, --generate-secret
  Dashboard/              Dashboard im Browser (HTML, eingebettete Schrift B612), Daten-API
  Desktop/                Tray-Symbol, Autostart
  Speech/                 Sprachausgabe: Windows-Stimme oder Kokoro, Warteschlange mit Prioritäten, Ausgabegerät, ECAM-Text zum Sprechen
  Voice/                  Sprachschleife: Mikrofon, Silero VAD, Whisper, Normalisierung, Router, Befehle, Dialog, ATC-Schnittstelle
  Input/                  Eingabe-Modul: Aktionen planen, Sperren, Bestätigung, Ausführung über SimConnect
  Hosting/                Transport-Auswahl, Konfiguration, HTTP-Host
  Remote/                 Bearer-Token, OAuth-Server, Freigabeseite, Token-Speicher
  Sim/SimConnectClient.cs SimConnect auf eigenem Thread, Auto-Reconnect, Werte-Cache
  Sim/SimVarCatalog.cs    Standard-SimVars (1x pro Sekunde, Monitoring-Werte ca. 10x pro Sekunde)
  Sim/AircraftSignals.cs  Semantische Sicht: Profil-Bindings mit Rückfall auf generische SimVars
  Aircraft/               Profile laden und zuordnen
  Ecam/                   ECAM-Codes → Klartext (FBW-Tabelle, zur Laufzeit geladen)
  FlightPlan/             SimBrief-OFP, Routen- und Fortschrittsrechnung
  Atc/                    Ingame-ATC, Bahnnummer/-bezeichner, Phraseologie
  Atc/Controller/         Kopilot-ATC: Zustandsmaschine, Vorlagen, Sprechweise, Readback-Prüfung, Stationen und Stimmen
  atc-templates.json      Textvorlagen des Kopilot-ATC
  Monitoring/             Regeln (MonitorEngine), Alert-Bus, Hintergrunddienst
  Traffic/                Verkehrsabfrage, Peilung/Uhrzeit/Callout, Hintergrundabfrage für TRAFFIC_PROXIMITY
  Tools/                  MCP-Tools
  profiles/               Flugzeugprofile (JSON)
src/SimConnectStub/       Platzhalter-API für CI-Builds ohne MSFS-SDK
tests/Kopilot.Tests/      Tests (OAuth, Zugriffsschutz, Monitoring-Regeln, ECAM, SimBrief, Profile, Verkehr, ATC, Sprachschleife) – laufen ohne MSFS
docs/cloudflared.md       Anleitung Cloudflare Tunnel
```

Jede Variable hat eine eigene SimConnect-Data-Definition. Ist ein Name in MSFS unbekannt, fehlt nur dieser eine Wert, alles andere läuft weiter.

## Roadmap

- [x] v0.1: MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, L:-Variablen-Profilen
- [x] v0.3: Pilot Monitoring (Regeln, Callouts, Alerts), FBW-Profile, ECAM-Klartext, SimBrief, Ingame-ATC-Bahn
- [ ] L:-Variablen des iniBuilds A321LR (FMA, A/THR, Master Warning, ECAM)
- [x] v0.4: Tray-Symbol und Dashboard im Browser statt Konsolenfenster, Callouts per Sprachausgabe im Browser
- [x] v0.5: Korrekturen nach Testflug EDDL–EDDM (A32NX-Bindings, Knoten statt m/s, ALT CRZ), TOD, Landecheck, FMA-Log, FMS-Wegpunkt
- [x] v0.6: Lokale Sprachausgabe (Windows, Stimme und Ausgabegerät wählbar) und PM-Callouts (Startlauf, Landung, Höhenmarken, Konfiguration)
- [x] v0.7: Eingabe-Modul mit Readback und Bestätigung (FCU, EFIS, Konfiguration, Licht, Schilder, Anti-Ice, Packs, APU, Transponder, Funk) – zuerst A32NX
- [x] v0.8: Kokoro als zweite Stimme (neuronal, ohne Windows-Sprachpaket), ECAM-Titel ausgeschrieben, Tray-Schalter
- [x] Verkehr: `get_traffic` (KI und Multiplayer) und Verkehrswarnung `TRAFFIC_PROXIMITY` – im Sim noch zu prüfen
- [x] ATC-Modul: Zustandsmaschine mit Textvorlagen (Delivery bis Ground, Oceanic), Readback-Prüfung, Stimme je Station
- [ ] ATC: Rollroutenberechnung aus dem Taxiway-Graphen der Facilities-API (`ITaxiRouteProvider`), Gate aus der Parkposition
- [x] v0.9: Lokale Sprachschleife (Silero VAD, Whisper, feste Befehlsmuster, Readback und Bestätigung per Sprache), Prioritäts-Warteschlange, Taxi-Speed-Warnung, Funk an das ATC-Modul
- [ ] Sprachschleife: Testflug in VR, Aktionen für den iniBuilds A321LR
- [ ] Eingabe-Modul: im Sim prüfen, NAV & LOGO über Input Events, weitere Flugzeuge
- [ ] WASimCommander-Anbindung (L:-Variablen auflisten, Events)
- [x] Remote-Betrieb: Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- [ ] Sprachmodus mit dem Remote-Connector testen
