[English](README.md) | **Deutsch**

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
| `get_progress` | Fortschritt gegen den Plan: nächster Fix (beim A32NX der aktive FMS-Wegpunkt, z. B. nach DIR TO), Distanz/ETA, TOD, Querablage, Fuel gegen Plan, Verlauf der automatischen Fuel Checks (`fuelCheck`). ETA aus den Navlog-Zeiten (geplante Winde), korrigiert um die Abweichung am letzten überflogenen Fix; ohne Navlog-Zeiten aus der Groundspeed (`etaSource`) |
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
| `TRAFFIC_PROXIMITY` | Traffic, 2 o'clock, 2 miles, 500 feet above, climbing | Ziel in der Luft näher als 3 NM und innerhalb ±1000 ft (gegen die eigene Druckhöhe), eigenes Flugzeug über 500 ft AGL; danach 60 s Ruhe (siehe [Verkehr](#verkehr)) |
| `TCAS_RA` | TCAS RA / Clear of conflict | vertikale FMA zeigt 0,3 s TCAS (Resolution Advisory) – Warnung; „Clear of conflict“ 1,5 s nach dem Ende |
| `ATHR_OFF` | Autothrust off | A/THR geht in der Luft von armed/active auf aus (nicht beim Trennen unter 100 ft RA im Aufsetzen) |
| `FMA_CHANGE` | Loc star / Glideslope star / Alt blue | neuer lateraler oder vertikaler Modus oder neu gearmter Modus („… blue“) in der Luft, sobald er 1 s steht – Info |
| `LOC_DEVIATION` | Loc | LOC oder LAND aktiv, 50–2000 ft RA, Localizer-Abweichung 1 s über ¼ Dot |
| `GS_DEVIATION` | Glideslope | G/S oder LAND aktiv, 100–2000 ft RA, Glideslope-Abweichung 1 s über 1 Dot |
| `APPR_NOT_ACTIVE` | Approach phase not activated | ab 15 NM vor dem Ziel mit Managed Speed, aber der FMGC ist noch nicht in der Approach-Phase – einmal je Anflug |
| `SIM_PAUSED` | Sim paused | Sim pausiert – alle anderen Regeln ruhen |

Die Autoflug-Callouts (`TCAS_RA` bis `APPR_NOT_ACTIVE`) hängen an Profil-Bindings (FMA, A/THR, FMGC-Phase, LOC/GS-Abweichung). Kopilot rät nicht: Ohne Binding bleibt der Callout für dieses Flugzeug aus. `get_aircraft_profile` → `callouts` zeigt je Regel, ob sie `verified`, `unverified` (Binding vorhanden, im Sim noch nicht geprüft) oder `missing` ist – die Übersicht je Flugzeug steht in [docs/aircraft.de.md](docs/aircraft.de.md#autoflug-callouts-v092).

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
| `FUEL_CHECK` | HENDY, fuel 34.1, 2.2 below plan | Reiseflug: Überflug eines OFP-Wegpunkts, auf langen Strecken ohne Wegpunkt alle 30 min (siehe [Fuel Check](#fuel-check)) |
| `FUEL_WARNING` | HENDY, fuel 31.2, 2.8 below plan, deviation increased by 0.6 | Abweichung seit dem letzten Check um mehr als 300 kg schlechter, oder Landemenge unter Minimum + Puffer |

Radiohöhen (2500, 1000, 500 …), „Hundred above“, „Minimum“ und „Retard“ sagt der A32NX selbst an. Kopilot kann sie zusätzlich sprechen – für Flugzeuge ohne eigene Ansagen: Dashboard → Ansagen → Stimme, oder `Kopilot:Monitoring:AircraftAutoCallouts`.

Nach dem Laden eines Flugs, einem Flugzeugwechsel oder einem Air-Start schweigt das Monitoring 10 Sekunden, nach einer Pause 3 Sekunden – dann springen die Werte. Einzelne Regeln schaltest du in `appsettings.Local.json` ab:

```jsonc
"Kopilot": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

### Fuel Check

Mit SimBrief-Plan vergleicht Kopilot im Reiseflug den Treibstoff an Bord mit dem Plan – wie die Crew beim Fuel Check:

- **An jedem OFP-Wegpunkt** (Abschnitt mit SimBrief-Phase CRZ, ohne TOC/TOD) sagt Kopilot an: „Hendy, fuel 34.1, 2.2 below plan“ (Tonnen, eine Nachkommastelle; unter 50 kg Abweichung „on plan“). Der Wegpunkt klingt wie überall (siehe Sprachausgabe): Funkfeuer mit Namen („Wabush“ statt YWK), Koordinatenpunkte „fifty three north fifty west“.
- **Intervall:** Kommt im Reiseflug 30 min (Sim-Zeit, Pausen zählen nicht) kein Wegpunkt, gibt es einen Check ohne Ort: „Fuel 34.1, 2.2 below plan“.
- **Warnung (`FUEL_WARNING`, Caution)** – laufend geprüft, nicht nur an Wegpunkten:
  - die Abweichung ist seit dem letzten Check um mehr als `DeviationIncreaseKg` schlechter geworden (Leckverdacht): „…, deviation increased by 0.6“;
  - die prognostizierte Landemenge (geplante Landemenge + heutige Abweichung) fällt unter Minimum + Puffer bzw. unter das Minimum: „…, landing fuel 6.8, minimum 6.5“ / „…, landing fuel 6.2, below minimum“. Minimum ist `fuel_min_onboard` am Ziel laut Navlog (sonst Ausweich- plus Endreserve). Der Puffer ist höchstens die Hälfte dessen, was der Plan über dem Minimum landet – sonst würde ein knapp geplanter Flug schon beim ersten Check warnen. Dieselbe Warnung kommt erst wieder, wenn die Landemenge zwischendurch 100 kg über der Grenze war.
- **Nachtanken** über Toolbar oder EFB (Treibstoff steigt) und Sprünge nach unten, die schneller sind als jeder Verbrauch, sind keine Lecks: Kopilot meldet sie stumm („Nachtanken erkannt: 3.530 → 11.090 kg“) und beginnt den Vergleich neu.

Im Dashboard steht unter der Route der Trend der Abweichung gegen Plan (x = Strecke wie die Routenleiste, Warnungen amber, Tanken als cyan Linie) mit den letzten Checks als Tabelle; `get_progress` liefert ihn als `fuelCheck`. Einstellbar in `appsettings.Local.json`:

```jsonc
"Kopilot": { "Monitoring": { "FuelCheck": {
  "IntervalMinutes": 30,       // 0 = kein Intervall-Check
  "DeviationIncreaseKg": 300,  // 0 = keine Warnung bei wachsender Abweichung
  "LandingBufferKg": 500,
  "MinSpacingMinutes": 0       // z. B. 10 = über Europa nicht an jedem Wegpunkt ansagen
} } }
```

Nur die Ansagen abschalten, Warnungen behalten: `FUEL_CHECK` in `DisabledRules` (der Verlauf im Dashboard läuft weiter).

**Push:** Claude.ai und der Sprachmodus können keine Nachrichten vom Server empfangen. Deshalb spricht Kopilot die Callouts selbst (siehe unten). Claude sieht die Meldungen zusätzlich über `activeAlerts` in jeder Antwort oder über `wait_for_alerts`.

## Verkehr

`get_traffic` fragt per SimConnect (`RequestDataOnSimObjectType`) alle Flugzeuge und Hubschrauber im Umkreis ab – KI-Verkehr, Live-Traffic und Multiplayer, soweit im Sim eingeschaltet. Das eigene Flugzeug ist ausgeschlossen.

| Parameter | Standard | Bedeutung |
|---|---|---|
| `radiusNm` | 40 | Suchradius, höchstens 108 NM (200 km, Grenze von SimConnect) |
| `limit` | 10 | höchstens so viele Ziele, nächstes zuerst (1–50) |
| `airborneOnly` | false | Flugzeuge am Boden (Gate, Rollweg) weglassen |

Pro Ziel: `ident`/`callsign` (Airline + Flugnummer, wenn beide gesetzt sind, z. B. `Lufthansa 123`; sonst die ATC ID, also die Registrierung wie `N605SD`), `atcId`, `type` (ATC MODEL, z. B. `A320`), `distanceNm`, `clock` („2 o'clock“), `relativeBearingDeg`, `bearingTrueDeg`, `altitudeFt`, `altitudeDiffFt` (+ = darüber), `verticalTrend` (climbing/descending/level ab ±500 ft/min), `groundSpeedKt` mit `groundSpeedSource`, `headingTrueDeg`/`headingMagDeg`, `onGround`. Das nächste Ziel hat `closest: true`; `callout` ist die Verkehrsmeldung dazu:

```
Traffic, 2 o'clock, 5 miles, 1000 feet above, descending
```

- **Uhrzeit:** relativ zum eigenen Steuerkurs, je 30°: 12 Uhr = 345° bis unter 15°.
- **Rufzeichen und Typ:** Live-Traffic-GA liefert als `ATC FLIGHT NUMBER` oft eine interne Nummer ohne Airline (z. B. `1432126`) – die ist kein Rufzeichen, dann gilt die Registrierung. Typen kommen teils als Lokalisierungsschlüssel (`ATCCOM.AC MODEL C441.0.tts`, `ATCCOM.ATC NAME CITATION.0.tts`, `TT:ATCCOM.AC_MODEL_A320.0.text`); Kopilot macht daraus `C441`, `CITATION`, `A320`.
- **Abstand:** horizontal (Großkreis).
- **Höhe:** `altitudeFt` ist die Höhe des Ziels, wie SimConnect sie liefert (`PLANE ALTITUDE`). Beim Live-Traffic von MSFS ist das die barometrische Höhe aus ADS-B, also die Flugfläche – nicht die wahre Höhe. `altitudeDiffFt` rechnet deshalb wie TCAS gegen die eigene Druckhöhe (`PRESSURE ALTITUDE`, 1013,25 hPa). Gegen die wahre Höhe lag die Differenz bei kalter Luft oder tiefem Luftdruck weit daneben: auf FL320 mit 30.630 ft wahrer Höhe erschien ein Ziel auf FL330 2.400 ft statt 1.000 ft darüber. `ownAircraft` zeigt Druckhöhe, wahre Höhe und mit `altitudeReference` (`pressure`/`true`), welche gezählt hat; ohne Druckhöhe nimmt Kopilot die wahre Höhe.
- **Groundspeed:** Die SimVar `GROUND VELOCITY` ist beim Live-Traffic unbrauchbar (im Testflug meist rund Faktor 3,3 zu klein, z. B. 169 statt 556 kt). Kopilot rechnet die Groundspeed deshalb aus der Positionsänderung zwischen zwei Abfragen, 5–120 s auseinander, bevorzugt etwa 20 s (`groundSpeedSource: "position"`, Abstand in `groundSpeedBaselineSec`, Rohwert der SimVar zum Vergleich in `groundSpeedSimVarKt`). Läuft die Hintergrundabfrage der Verkehrswarnung, gibt es das nach wenigen Sekunden; sonst ab dem zweiten `get_traffic`-Aufruf. Bis dahin steht dort die SimVar (`groundSpeedSource: "simvar"`) – mit Vorsicht lesen.
- **Trend:** aus `VERTICAL SPEED`. Liefert ein Ziel dort 0 (oft bei Multiplayer), rechnet Kopilot die V/S aus der Höhenänderung zwischen zwei Abfragen (`verticalSpeedSource: "derived"`).
- **Keine Daten:** `status: "no_traffic"` mit Hinweis, wenn MSFS kein fremdes Flugzeug liefert – meist ist dann KI-Verkehr bzw. Multiplayer im Sim aus. `no_response`, wenn MSFS gar nicht antwortet.

**Verkehrswarnung `TRAFFIC_PROXIMITY`:** Solange die Regel an ist und das Flugzeug fliegt, fragt Kopilot alle 3 s den Verkehr im Umkreis von 6 NM ab. Unter 500 ft über Grund, in der Pause und am Boden ruht sie. Abschalten mit `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]`, Grenzen anpassen:

```jsonc
"Kopilot": { "Monitoring": { "Traffic": { "ProximityRangeNm": 3, "ProximityAltitudeFt": 1000, "ProximityCooldownSec": 60, "ScanIntervalSec": 3 } } }
```

`"AltitudeReference": "True"` im selben Abschnitt stellt die Höhendifferenz (get_traffic und Verkehrswarnung) zurück auf die eigene wahre Höhe – für Ziele, deren `PLANE ALTITUDE` eine wahre Höhe ist. Bei KI-Verkehr und Multiplayer ist das noch nicht im Sim geprüft; Standard ist `"Pressure"`.

Das ist kein TCAS: keine Ausweichempfehlung, keine Vorhersage des Annäherungspunkts – nur Abstand und Höhe im Moment.

**Eigene Verbindung für Verkehr:** Bis 0.9 lief die Abfrage über die Hauptverbindung. Deren verwaltete SimConnect-Bibliothek liest jede Antwort mit der vollen Struktur, ohne die Größe der Nachricht (`dwSize`) zu prüfen – die vermutete Ursache der Abstürze im Testflug (AccessViolation in `CSTRMarshaler.ConvertFixedToManaged`, in .NET nicht abfangbar). Das passt auch zu Abstürzen ohne Verkehr: Die Antwort „kein Objekt im Umkreis“ (`dwoutof = 0`) kommt bei jeder Hubschrauber-Abfrage. Jetzt öffnet Kopilot für den Verkehr eine eigene SimConnect-Verbindung direkt über die native `SimConnect.dll` und liest nur, was laut `dwSize` und Empfangsgröße zur Nachricht gehört. Felder, die nicht vollständig ankommen, bleiben leer.

- Der Hintergrundscan der Verkehrswarnung fragt nur Zahlen ab (Position, Höhe, Kurs, Speed, V/S, Boden). Callsign und Typ kennt die Warnung, wenn `get_traffic` das Ziel in den letzten 15 Minuten gesehen hat.
- Texte (ATC ID, Airline, Flugnummer, Typ) fragt nur `get_traffic` ab.
- `TRAFFIC_PROXIMITY` kann damit wieder an – den Workaround `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]` aus `appsettings.Local.json` entfernen.

**Prüfung der Antworten:** Kopilot zählt alle Antworten und schreibt jede nach `%LOCALAPPDATA%\Kopilot\logs\traffic-messages.csv` (Größe, Objekt, Eintrag, Flags `empty`/`short`/`mismatch`; rund 250 KB pro Flugstunde, bei 10 MB rotiert). `get_traffic` zeigt unter `messageCheck` das Urteil zu den beiden Absturz-Hypothesen: ob leere Antworten kürzer sind als die alte Struktur (368 Byte) und ob Live-Traffic zu kurze Datenblöcke liefert. Die ersten Antworten und alle Auffälligkeiten stehen zusätzlich im normalen Log, alle 30 Minuten eine Zusammenfassung. Abschalten mit `"Kopilot": { "Monitoring": { "Traffic": { "MessageLog": false } } }`.

## Sprachausgabe

Kopilot spricht die Callouts selbst, auf Englisch. Warnungen unterbrechen alles andere, ältere Meldungen verfallen nach 8 Sekunden.

**Aussprache:** ATC, Callouts, Fuel Check und die Antworten der Sprachschleife haben eine gemeinsame Aussprache (`Speech/Pronunciation.cs`), die vor der Sprachausgabe greift:

| Was | Gesprochen |
|---|---|
| Fixe mit fünf Buchstaben | als Wort: HENDY → „Hendy“ |
| Funkfeuer (bis drei Buchstaben) | mit dem Namen aus dem OFP-Navlog: YWK → „Wabush“; ohne Namen im NATO-Alphabet: FFM → „foxtrot foxtrot mike“ |
| ICAO-Codes | immer buchstabiert: KLAX → „kilo lima alpha x-ray“ |
| Koordinatenpunkte | 53N050W, 5350N → „fifty three north fifty west“; 5530N → „fifty five thirty north …“ |
| Bahnen | 25R → „two five right“, 09 → „zero niner“ |
| Squawks, Kurse, Speeds, Frequenzen | Ziffer für Ziffer, 9 als „niner“, Frequenzen mit „decimal“ |
| Flugflächen | „flight level three four zero“, volle Hunderter „flight level one hundred“ (ICAO) |
| Höhen | in Tausendern: „one zero thousand“, „two thousand five hundred“ |
| ECAM-Titel | ausgeschrieben: „ENG 1 FIRE“ → „engine one fire“, „APU“ → „A P U“ |

Namen aus dem Navlog und die Flughäfen des Plans kommen aus dem geladenen SimBrief-Plan. Im Readback gelten auch der Name („direct Wabush“) und natürlich gesprochene Koordinaten („fifty five north twenty west“).

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

**Was nie geht:** Triebwerks-Master, Zündung/Start, Schub, Feuer-Handles und -Agents, Treibstoff (Pumpen, Crossfeed), Hydraulik (Pumpen, PTU, RAT), IDG-Trennung, MCDU, Pause/Slew/Sim-Steuerung – auch nicht mit Rückfrage. Was den Flieger beschädigen kann oder irreversibel ist, bestätigen sich die Piloten selbst. Das ist im Code festgelegt, auch ein bearbeitetes Profil kommt nicht daran vorbei:
- Kopilot sendet nur Events aus einer festen Liste (FCU, Autobrake, Fahrwerk, Klappen, Spoiler, Licht, Schilder, Transponder, COM).
- Kopilot schreibt nur L:-Variablen, nie Standard-SimVars, und auch dort greift die Sperrliste.
- Fahrwerk und Klappen gehen nur über ihre Events, und nur zusammen mit der passenden Sperre.
- Bereiche und IDs wie `Engine`, `Fuel`, `Fire`, `Hydraulic`, `eng.*`, `fuel.*`, `*.idg1` werden beim Laden entfernt.
- Sicherheitsrelevante Schalter (Fahrwerk, Klappen, Speedbrake, Batterien, Packs, APU Master, Transponder, TCAS, AP/ATHR, FCU-Höhe, ADIRS) stehen nur unter ihrer Standard-ID (`gear.down`, `air.pack1` …) – sonst würden die Sperren unten nicht greifen.
- Es gibt kein Tool, das beliebige Events oder Variablen schreibt.

**Sperren** (abgelehnt mit Grund):
- Fahrwerk einfahren nur in der Luft
- Fahrwerk ausfahren nur bis VLO (A320/A330: 250 kt) und unter der Höchsthöhe (`maxGearAltitudeFt`, A320/A330: 25.000 ft Druckhöhe)
- Klappen nur bis zur VFE der neuen Stellung
- Speedbrake ausfahren nicht in den gesperrten Klappenstellungen (`speedbrakeInhibitFlaps`, A320/A330: FULL)
- Batterien aus nur am Boden, beide Packs aus nur am Boden (ein Pack aus im Flug geht, solange das andere an ist)
- APU Master aus nicht bei APU Bleed an
- ADIRS aus nur am Boden (im Flug irreversibel – in der Luft kein neues Ausrichten)

**Rückfrage mit Warnhinweis** (nach dem Readback ein zweites „confirm“): Transponder STBY, ALT RPTG aus und TCAS STBY im Flug · Squawk 7500/7600/7700 · ADIRS aus am Boden · AP 1/2 und A/THR aus im Flug · FCU-Höhe unter der MSA oder außerhalb der ATC-Freigabe (`clearedAltitude`; stimmt die Höhe mit der Freigabe, entfällt die MSA-Prüfung). Das erste `confirm_action` führt dann nicht aus, sondern liefert `status: "awaiting_caution_confirmation"` mit `caution`, z. B. „Caution, transponder standby in flight. Confirm transponder standby?“. Erst das zweite „confirm“ mit demselben Token führt aus. Steht der Schalter schon so, entfällt die Rückfrage. Die Rückfrage gilt in jedem Ablauf – auch ein späterer Modus „ausführen + Callout“ muss sie stellen (`ActionPlan.NeedsCrossConfirmation`).

**Fehlt ein Grenzwert im Profil** (VLO/VLE, VFE einer Stellung, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource`), geht die Aktion nur mit Rückfrage. Das steht im Dashboard-Protokoll („Grenzwert fehlt“), im Log und in `kopilot_status` (`profileWarnings`). Dazu kommen Bedingungen je Aktion, z. B. V/S und Heading nicht im TRK/FPA-Modus, QNH nur im QNH-Modus, APU Start nur mit APU Master.

**Fahrwerk und Klappen nachlesen:** Nach `gear.down` wartet Kopilot, bis alle Beine verriegelt sind, und sagt dann „Gear down, three green“ – sonst nach 20 s „Gear disagree“. Analog „Gear up“ und „Flaps two“ erst bei erreichter Stellung (Klappen und Vorflügel stehen still), sonst „Flaps disagree“. Pausen verlängern die Wartezeit. Die Callouts laufen als PM_GEAR/PM_FLAPS über die Sprachausgabe und `get_alerts`; den Hebelwechsel sagt das Monitoring dann nicht zusätzlich an. Gelesen werden die MSFS-Standardwerte (`GEAR CENTER/LEFT/RIGHT POSITION`, `TRAILING/LEADING EDGE FLAPS LEFT PERCENT`), im Profil unter `verification` überschreibbar.

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

Kopilot bringt ein eigenes ATC mit: eine Zustandsmaschine mit festen Textvorlagen, **kein Sprachmodell**. Du funkst wie gewohnt, Claude gibt deinen Spruch mit `atc_transmit` weiter (oder die Sprachschleife direkt, siehe unten), Kopilot antwortet als die zuständige Stelle und spricht die Antwort selbst – jede Station mit eigener Kokoro-Stimme. Phraseologie auf Englisch im ICAO-Stil, Zahlen Ziffer für Ziffer („squawk two two zero five“), Geschwindigkeiten immer in Knoten, nie Mach; Fixe, Funkfeuer und Koordinaten wie unter Sprachausgabe beschrieben („proceed direct Wabush“).

**Ablauf** (Übergänge kommen aus deinen Sprüchen und aus dem Flugzustand):

| Station | Was passiert | Weiter zur nächsten Station |
|---|---|---|
| Delivery | IFR-Freigabe mit SID, Bahn, Steigflughöhe und Squawk; Readback wird geprüft | nach korrektem Readback: „readback correct, contact Ground“ |
| Ground | Push and start, Rollfreigabe zum Rollhalt (Readback Rollhalt/Bahn) | am Rollhalt stehen geblieben (8 s), oder du rufst „ready“ |
| Tower | Line up and wait, Startfreigabe mit Wind (automatisch, sobald du auf der Bahn stehst, spätestens nach 60 s) | ab 1500 ft AGL: „contact Departure“ |
| Departure | „identified, climb FL150“, optional „proceed direct“ zum ersten Fix nach der SID | 2000 ft unter der freigegebenen Höhe → Center der FIR |
| Center | Steigflug auf Reiseflughöhe, Höhenanfragen („request climb flight level three four zero“ → „climb and maintain FL340“), Stufensteigflüge aus dem OFP („when ready climb and maintain FL340“), am Top of Descent „descend via ANJLL4 arrival“ (mit STAR im OFP) bzw. „descend FL110“, unter der Übergangshöhe mit QNH | an jeder FIR-Grenze ans nächste Center, innerhalb der FIR an jeder Sektorgrenze („contact Montreal Center one three three decimal zero two five“) |
| Oceanic | Freigabe ca. 30 Minuten vor dem Eintrittspunkt, danach Positionsmeldungen an jedem Oceanic-Fix („roger“) | am Austrittspunkt an die Domestic-Stelle, z. B. nach HOIST an Gander Center (kurze Anmeldung, keine Positionsmeldungen mehr) |
| Approach | Sinkflug, STAR, erwarteter Anflug und Bahn, QNH; ab 20 nm Anflugfreigabe | ab 12 nm: „contact Tower“ |
| Tower | Landefreigabe mit Wind (spätestens 4 nm vor der Schwelle) | nach der Landung unter 40 kt: „vacate via next exit, contact Ground“ |
| Ground | Rollen zum Gate | Ende, wenn das Flugzeug mit Parkbremse steht |

Auf Kurzstrecken ohne Oceanic-Fixes (Koordinaten wie `55N020W` oder eine FIR, die in den Vorlagen als `oceanic` markiert ist) entfällt Oceanic. Startest du ohne Freigaben, springt das ATC beim Startlauf zum Tower nach. Startest du die Sitzung in der Luft, beginnt sie bei der Stelle, die dort zuständig ist – liegst du im Horizontalflug über der geplanten Reiseflughöhe (z. B. nach einer Stufe), gilt deine aktuelle Höhe als freigegeben. Startest du sie erst im Oceanic-Luftraum oder hinter dem Eintrittspunkt (z. B. nach einem Neustart von Kopilot), gilt die Oceanic-Freigabe als erteilt – ebenso, wenn du den Eintrittspunkt ohne Anfrage erreichst.

**Readback:** Bei Freigaben prüft Kopilot die Werte aus der Vorlage (`readback`), z. B. SID, Höhe und Squawk bei der Streckenfreigabe. Kopilot versteht Ziffern („2205“, „FL110“) wie gesprochene Zahlen („two two zero five“, „five thousand feet“, „one two one decimal eight“) und das NATO-Alphabet. Ist ein Wert falsch oder fehlt, kommt „negative“ mit dem richtigen Wert, z. B. „Lufthansa four alpha bravo, negative, squawk two two zero five“ – danach musst du nur diesen Wert noch einmal zurücklesen. Quittungen ohne Freigabe („contact Ground one two one decimal eight“) beantwortet ATC nicht. „Say again“ wiederholt den letzten Funkspruch. Einen „radio check“ (oder „how do you read“) beantwortet die Station mit „read you five“ (`common.radioCheck`), ohne dass sich am Ablauf oder an einem offenen Readback etwas ändert.

**Höhen im Reiseflug:** Departure (nach dem Erstanruf), Center und Oceanic beantworten Höhenanfragen: „request climb flight level three four zero“, „requesting FL340“, „request descent to one two thousand feet“, „request higher“/„request lower“ (ohne Höhe: die nächste Stufe des OFP, sonst 2000 ft). Die Freigabe lautet „climb and maintain FL340“ (`center.levelChange`), unter der Übergangshöhe in Fuß mit QNH („descend and maintain 16000 ft, altimeter two niner niner two“, `center.levelChangeQnh`). Das Readback wird geprüft, die freigegebene Höhe gilt sofort – auch für die FCU-Höhenprüfung des Eingabe-Moduls. „request descent“ kurz vor dem Top of Descent bekommt den üblichen Sinkflug auf FL110. Optional lehnt ATC eine Anfrage ab („unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes“, `center.levelUnable`, Regel `levelRequestUnablePercent`, standardmäßig aus) und gibt nach `levelRequestDelayMinutes` von selbst frei. Stufensteigflüge aus dem OFP (geplante Höhe der Fixe im Reiseflug) gibt ATC 10 nm vor dem Stufenpunkt von sich aus frei: „when ready climb and maintain FL340“ (`center.levelWhenReady`, Regeln `offerStepClimbs`, `stepClimbLeadNm`); davor schlägt `get_atc_controller` die Anfrage vor („request climb FL340“). Meldest du beim Erstanruf eine andere Höhe als die freigegebene („maintaining FL340“ bei Freigabe FL320) und zeigt MSFS dich dort, übernimmt ATC sie („identified, maintain FL340“).

**Sinkflug:** Am Top of Descent gibt Center „descend via ANJLL4 arrival“ frei, wenn das OFP eine STAR hat (`center.descendVia`); freigegeben ist dann die niedrigste geplante Höhe der STAR-Fixe im Navlog, mindestens die Höhe der Anflugfreigabe. Ohne STAR bleibt es bei „descend FL110“ (`rules.centerDescentFt`). Endet die Freigabe unter der Übergangshöhe des Ziels, kommt das QNH dazu und gehört zum Readback („descend via ANJLL4 arrival, altimeter two niner niner two“, `center.descendViaQnh`, `center.descendQnh`) – im Testflug nach KLAX kam nur „descend 11000 ft“.

**Squawk und Frequenz:** MSFS setzt Transpondercode und COM1 teils selbst – auch bei stumm geschaltetem Sim-ATC (im Testflug Squawk 3172 statt 2340). Kopilot liest `TRANSPONDER CODE` und `COM ACTIVE FREQUENCY:1` laufend. Weicht der Code im Flug länger als 15 s vom zugewiesenen ab, sagt ATC „Condor two two zero five, check squawk, should be two three four zero“ (`common.checkSquawk`); steht COM1 nach dem ersten Anruf (nach einer Übergabe: nach 60 s Schonfrist) nicht auf der Frequenz der Station, „check frequency, should be Montreal Center one three two decimal two five“ (`common.checkFrequency`). Der Hinweis kommt alle 2 Minuten wieder, höchstens dreimal; Notfallcodes, Platzhalter-Frequenzen und Oceanic (HF) werden nicht geprüft. **Kopilot setzt nie selbst zurück:** `get_atc_controller` zeigt die Abweichung unter `deviations` mit den Schritten für `prepare_action` (`xpdr.code 2340` bzw. `com1.standby 132.250`, dann `com1.swap`), und in `activeAlerts` und `wait_for_alerts` steht eine Caution `ATC_SQUAWK`/`ATC_FREQUENCY` – Claude bietet das Zurücksetzen an, der Pilot bestätigt. Mit `Kopilot:Atc:SquawkFromSim` = `true` übernimmt Kopilot stattdessen einen vergebenen Code, den MSFS im Flug setzt, als zugewiesenen Squawk (nie 2000/1200/7000 oder Notfallcodes). Regeln: `checkSquawk`, `checkFrequency`, `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls`.

**Falsche Station:** Rufst du eine andere Station als die zuständige („Gander, Condor two two zero five, position …“ bei Montreal), antwortet Kopilot nicht wie sonst: Ist es die Station, die dich in den letzten 15 Minuten übergeben hat, wiederholt sie die Übergabe („contact Montreal Center one three two decimal six“); sonst sagt die aktuelle „Condor two two zero five, this is Montreal Center, say again“ (`common.wrongStation`). Es zählen nur Anrufe der Form „Station, Rufzeichen, …“, keine Readbacks.

**Daten:**
- Rufzeichen, Ziel, SID/STAR, Bahnen, Reiseflughöhe, FIRs und Oceanic-Teil aus dem SimBrief-OFP (SimBrief muss eingerichtet sein). Das Rufzeichen kommt aus der Flugnummer mit ICAO-Airline-Kennung (`CFG2205` → „Condor two two zero five“) – das Feld `callsign` kann den IATA-Code tragen (`DE2205`) und zählt nur, wenn es mit derselben ICAO-Kennung beginnt (z. B. `DLH4AB` zur Flugnummer `DLH400`). Das gesprochene Rufzeichen kommt aus der Airline-Datenbank von OpenFlights (gut 5.000 Airlines), Ausnahmen und Korrekturen aus `telephony`. Kennt keine der beiden die Airline, wird die ICAO-Kennung buchstabiert.
- **FIR-Grenzen** aus VAT-Spy (VATSIM, `data/vatspy`). Welche FIRs die Route durchfliegt, sagt das OFP; wo die Grenze liegt, VAT-Spy: Kopilot schneidet die Route mit beiden FIR-Grenzen. Überlappen sich die VAT-Spy-Grenzen (z. B. reicht CZQX bis hinter Wabush), zählt der Eintritt in die neue FIR, wenn der ganze Abschnitt noch in der alten liegt – so liegt die Übergabe Gander → Montreal bei ca. 53,5 N / 64,8 W wie beim MSFS-ATC statt bei HOIST. Liegt zwischen zwei Fixes eine dritte FIR (mindestens 30 nm), kommt sie dazwischen. Ohne FIR-Angaben im OFP ordnet Kopilot die Fixe nach VAT-Spy zu (Nordatlantik: 30°W Shanwick/Gander). Ohne VAT-Spy-Daten liegt die Grenze wie früher in der Mitte zwischen den Fixes.
- **Sektoren** aus den VATSIM-Sektordaten (VATGlasses, `data/sectors`): je FIR die Sektoren mit Höhenband, Frequenz und Rufname. Kopilot nimmt entlang der Route (geplante Höhe aus dem OFP) den passenden Sektor und übergibt auch innerhalb einer FIR. Sektoren, die die Route kürzer als `sectorMinNm` (20 nm) berührt, bekommen keine eigene Übergabe – kein Hin und Her an Grenzen entlang der Route. Stellen ohne eigene FIR wie Maastricht (EDYY) oder Rhein (EDUU) übernehmen den oberen Luftraum, eine UIR aus dem OFP wie EDVV (Hannover) wird so zu Maastricht oder Bremen Radar. Die Frequenzen sind die von VATSIM und können vom MSFS-ATC abweichen; wo es keine Sektordaten gibt (u. a. Minneapolis, Denver, Los Angeles), gilt die Frequenz aus `firs`.
- Höhe, Position, Speed, Wind, QNH und Zeit aus SimConnect. QNH am Ziel aus dem METAR des OFP, in der Nähe des Flughafens aus dem Sim. Nordamerika: „altimeter“ in inHg.
- Flughafen-Frequenzen aus der Facilities-API von MSFS (Clearance, Ground, Tower, Departure, Approach), Center-Frequenzen aus den Sektordaten. Fehlen sie, nimmt Kopilot die Frequenz aus der Vorlage oder einen Platzhalter. `get_atc_controller` zeigt die Quelle (`frequencySource`: `facility`, `sectors`, `templates`, `placeholder`), die Stationen der Route mit Sektor, Frequenz und Streckenposition (`flight.firs`) und die Herkunft der Daten (`airspace`).
- Squawk: pro Flug fest, ohne Sondercodes. Mit `Kopilot:Atc:SquawkFromSim` folgt er MSFS (siehe oben).
- Rollroute (`{taxiRoute}`), Rollhalt, Gate und Pushback-Richtung kommen über die Schnittstelle `ITaxiRouteProvider`. Die Routenberechnung kommt in einem eigenen Schritt; bis dahin nehmen die Vorlagen eine Variante ohne diese Angaben („taxi to holding point runway two five center“). Gate oder Rollhalt kannst du mit `atc_set_value` von Hand setzen („wir stehen an A26“).

**Stimmen:** Jede Station (z. B. Frankfurt Delivery, Langen Radar, Shanwick Oceanic) bekommt pro Flug zufällig eine Stimme aus `voices.pool` – möglichst jede eine andere, nie die Stimme der Callouts. Nach einem Neustart von Kopilot bleiben die Stimmen im selben Flug gleich. Feste Stimmen unter `voices.stations`, z. B. `"EDDF/tower": "bf_emma"`, `"FIR/EGGX": "bm_daniel"` oder `"delivery": "af_heart"` für alle Delivery-Stationen. Für ATC lädt Kopilot Kokoro auch dann, wenn die Callouts die Windows-Stimme nehmen; solange Kokoro lädt, spricht die englische Windows-Stimme. Eine Warning unterbricht einen laufenden Funkspruch. Gesprochen wird nur von der Tray-Instanz (HTTP-Modus); über die stdio-Instanz von Claude Desktop gibt es die Antwort nur als Text.

**Sitzung sichern:** Kopilot sichert die laufende ATC-Sitzung laufend in `%LOCALAPPDATA%\Kopilot\atc-session.json` (Station, Phase, freigegebene Höhe, Oceanic-Freigabe, Werte von Hand wie das Gate, Stimmen, Funkverlauf). Nach einem Neustart oder Absturz geht es im selben Flug (gleicher SimBrief-Plan) weiter, sobald MSFS verbunden ist – ohne neuen Erstanruf. Ein beim Absturz noch offenes Readback gilt als erledigt; eine angeforderte, aber nicht zurückgelesene Oceanic-Freigabe fordert der Pilot neu an. `atc_control stop` oder `restart` löscht die Sicherung, ein neuer SimBrief-Plan beginnt eine neue Sitzung. Einstellungen: `Kopilot:Atc:SessionFile` (leer = aus), `Kopilot:Atc:SessionMaxAgeHours` (Standard 24). Die stdio-Instanz von Claude Desktop sichert in eine eigene Datei (`atc-session-stdio.json`). `get_atc_controller` zeigt unter `persistence`, wann zuletzt gesichert und ob fortgesetzt wurde.

**Vorlagen bearbeiten:** Alle Texte stehen in `atc-templates.json` neben der `Kopilot.exe`. Eigene Änderungen gehören in eine Datei `%LOCALAPPDATA%\Kopilot\atc-templates.json` – `update.cmd` überschreibt sie nicht. Kopilot legt sie über die mitgelieferte Datei: Objekte werden je Schlüssel gemischt (deine Einträge gewinnen), Listen wie `variants` ersetzt deine Datei ganz. Sie braucht also nur die Abweichungen, z. B. `{ "telephony": { "XYZ": "Testair" } }`; ein Eintrag mit `null` entfernt einen mitgelieferten. Eine ältere vollständige Kopie funktioniert weiter: Kopilot hebt sie beim Laden auf das aktuelle Format (`version` 2) – ein alter Eintrag `CZQX` als Gander Oceanic wandert samt Frequenz und fester Stimme nach `CZQO`, die Datei selbst bleibt unverändert (Hinweis im Log und unter `templateNotes`). Danach `atc_control` mit `reload` (oder Kopilot neu starten).

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
- `telephony`: nur noch Ausnahmen – ICAO-Kennung → Rufzeichen, wenn OpenFlights es nicht oder falsch kennt („EJU“ → „Alpine“, „THY“ → „Turkish“). Hat Vorrang vor OpenFlights.
- `airports`: Rufname, Höhe der ersten Steigflugfreigabe, abweichende Stationsnamen (`"stations": { "approach": "SoCal Approach" }` für KLAX, so auch NorCal und New York) und feste Frequenzen (`"frequencies": { "tower": "119.900" }`, Vorrang vor der Facilities-API).
- `firs`: Name der Kontrollstelle je FIR (hat Vorrang vor dem Rufnamen aus den Sektordaten), `frequency` für FIRs bzw. Stellen ohne Sektordaten, `"oceanic": true` für Oceanic-Gebiete. `oceanicFir` für eine FIR mit Domestic- und Oceanic-Teil: Gander ist `CZQX` (Gander Center) mit `"oceanicFir": "CZQO"` (Gander Oceanic) – die Oceanic-Fixe, die das OFP unter CZQX führt, gehören zu CZQO. `members`: Stellen, deren Sektoren in der FIR mitzählen (EDVV: Maastricht, Bremen, Langen). FIRs ohne eigenen Eintrag heißen wie in VAT-Spy plus Stationsart nach `firSuffixes` („Winnipeg Center“, „Praha Radar“); kennt auch VAT-Spy sie nicht, gilt `OCEANIC` bzw. `CENTER` (generische Station; ohne eigene `frequency` der Platzhalter aus `stationTypes`).
- `firSuffixes`: ICAO-Präfix → Stationsart für FIRs ohne eigenen Eintrag (`"K": "Center"`, `"ED": "Radar"`, `"": "Control"`), der längste passende Präfix gewinnt.
- `stationTypes`: Namen der Stationsarten und Platzhalter-Frequenzen.
- `rules`: Schwellwerte der Zustandsmaschine (Höhen, Entfernungen, Zeiten), `sectorMinNm` für die Sektor-Hysterese, Stufen und Höhenanfragen (`offerStepClimbs`, `stepClimbLeadNm`, `levelRequestUnablePercent`, `levelRequestDelayMinutes`), Squawk- und COM1-Prüfung (`checkSquawk`, `checkFrequency`, `deviationSeconds`, `frequencyGraceSeconds`, `deviationRepeatSeconds`, `deviationMaxCalls`).
- `words`: was `{levelVerb}` sagt – climb/descend/maintain in Korrekturen, `climbAndMaintain`/`descendAndMaintain`/`maintain` in Höhenfreigaben.

**Luftraum- und Airline-Daten** liegen in `data` neben der `Kopilot.exe` (Quellen, Stand und Lizenzen in `data/README.md`): VAT-Spy im Originalformat (CC BY-SA 4.0), Sektoren als `atc-sectors.json` aus `tools/import_sectors.py` (VATGlasses, CC BY-NC-SA 4.0 – nur nicht-kommerziell) und OpenFlights `airlines.dat` (ODbL). Einen neueren Stand legst du nach `%LOCALAPPDATA%\Kopilot\data\…` (gleiche Ordner), er hat Vorrang und übersteht Updates; danach `atc_control reload`. Die Daten lädt Kopilot einmal im Hintergrund (etwa eine Sekunde).

Variablen: `{callsign}` `{station}` `{dest}` `{sid}` `{star}` `{approach}` `{runway}` `{initialAlt}` `{squawk}` `{freq}` `{taxiRoute}` `{holdingPoint}` `{gate}` `{level}` `{speedKt}` `{entryFix}` `{heading}` `{wind}` `{qnh}` `{fix}` `{eta}` `{nextFix}`, dazu `{direction}` (Pushback), `{altitude}` (aktuelle Höhe), `{nextCenter}` (nächste Stelle bei Übergaben), `{route}` (Oceanic-Route), `{time}` (UTC), `{corrections}`, `{levelVerb}` (climb/descend/maintain, in Höhenfreigaben „climb and maintain“), `{requested}` (angefragte Höhe) und `{minutes}` (Wartezeit nach „unable“). Kopilot prüft die Datei beim Laden: fehlende Funksprüche, unbekannte Variablen, Readback-Punkte ohne Korrektur und jede Erwähnung von Mach werden gemeldet (`get_atc_controller` → `templateErrors`, Log).

Einstellungen in `appsettings.Local.json` unter `Kopilot:Atc`: `Enabled` (Standard an), `TemplatesFile`, `Speak` (Funksprüche sprechen), `SquawkFromSim` (von MSFS gesetzten Squawk übernehmen, standardmäßig aus). Im Browser zeigt `http://127.0.0.1:5155/atc/controller` den Zustand.

## Sprachschleife

Du sprichst direkt mit Kopilot – ohne Claude, ohne Push-to-Talk (VR-tauglich), komplett lokal und ohne Sprachmodell (nur der optionale [Smalltalk](#first-officer-und-smalltalk) kann eins nutzen):

```
Mikrofon → Silero VAD (Satzgrenzen) → Whisper (Abschrift) → Router ─┬→ Kopilot-Befehle → Eingabe-Modul
                                                                    ├→ ATC-Modul
                                                                    └→ Smalltalk (optional, Sprachmodell – nur lesend)
                                     Sprachausgabe (Kokoro, Piper) ←┘   Warnungen > Callouts > Antworten > ATC > Smalltalk
```

**Einschalten:** im Dashboard unter **Ansagen → Stimme → Sprachschleife** den Haken setzen und das Mikrofon wählen – das gilt sofort, ohne Neustart von Kopilot. Gespeichert wird in `%LOCALAPPDATA%\Kopilot\voice-settings.json` (legt Kopilot beim ersten Start aus der Vorlage an); dort geht es auch von Hand mit `"enabled": true` und anschließendem Neustart. **Nicht** die `voice-settings.json` neben der `Kopilot.exe` bearbeiten – das ist nur die Vorlage, `update.cmd` überschreibt sie (Kopilot warnt im Log, wenn dort etwas anderes steht). Beim ersten Start lädt Kopilot das Whisper-Modell (`base-q5_1`, ~60 MB) und Silero VAD (~2 MB) nach `%LOCALAPPDATA%\Kopilot\models`. Für Befehle muss außerdem die Eingabe im Dashboard freigegeben sein. Die Sprachschleife läuft nur in der Tray-Instanz, nicht in der stdio-Instanz von Claude Desktop.

**Mikrofon:** leer = Windows-Standardaufnahmegerät, sonst ein Teil des Gerätenamens (`microphone.device`). Die vorhandenen Geräte stehen im Dashboard, in `kopilot_status` und beim Start im Log („Aufnahmegeräte: …“). Fehlt das gewählte Gerät (z. B. VR-Brille noch aus), nimmt Kopilot das Standardgerät und wechselt automatisch, sobald es auftaucht.

**Status:** `kopilot_status` → `voiceLoop` (läuft sie, Mikrofon, Pegel, Sätze erkannt/verworfen, zuletzt erkannter Satz, Zuordnung, Antwort), im Dashboard neben dem Mikrofon, roh unter `http://127.0.0.1:5155/voice`.

**Log:** Start, Einstellungen, Geräte, Modell-Download, erster Audioblock, jeder erkannte Satz mit Rechenzeit und Ziel (Kopilot oder ATC), verworfene Sätze mit Grund (Sim pausiert, stumm, Whisper zu langsam, nichts erkannt) und alle 5 Minuten ein Lagebericht. Kommen keine Daten oder nur digitale Stille vom Mikrofon (stummgeschaltet, Windows-Datenschutz „Desktop-Apps Zugriff auf das Mikrofon“ aus), steht eine Warnung im Log. Noch genauer (jede Satzgrenze, rohe Whisper-Abschrift): in `appsettings.Local.json` auf oberster Ebene `"Logging": { "LogLevel": { "Kopilot.Voice": "Debug" } }`.

**So läuft es:**
- Das Mikrofon ist dauerhaft offen. Silero VAD erkennt, wann du sprichst; nach 0,7 s Stille geht der Satz an Whisper.
- Halbduplex: Solange Kopilot spricht (und 350 ms danach), ist das Mikrofon stumm – Kopilot hört sich nicht selbst. Während du sprichst, warten Callouts und Antworten kurz; nur Warnungen und die zeitkritischen PM-Callouts (100 knots, V1, Rotate, Positive climb, Spoilers, Reverse, Decel, 70 knots, Radiohöhe, Minimum, Retard) fallen dir ins Wort. Ist die Spracheingabe stumm, wartet nichts.
- Whisper erkennt je Satz Deutsch oder Englisch. Zahlen werden vereinheitlicht: „Flaps one“, „Flaps 1“, „Flaps won“, „Klappen eins“ ergeben dasselbe.
- **Router:** Enthält der Satz ein Stationsrufzeichen („Frankfurt Delivery“, „Langen Radar“, „Shanwick“) oder das eigene Rufzeichen – am Anfang beim Anruf oder hinten beim Readback („…, squawk two two zero five, Lufthansa four alpha bravo“) –, geht er ans ATC-Modul. Alles andere ist ein Kopilot-Befehl („Squawk two two zero five“ ohne Rufzeichen setzt den Transponder). Mit „Kopilot, …“ immer Kopilot.
- Sim pausiert → Kopilot sagt nichts und verarbeitet nichts.

**Befehle** (feste Muster, deutsch und englisch, Beispiele) – die Sprachschleife kann alles, was Claude über die Kopilot-Tools kann: **Jede Aktion jedes Flugzeugprofils hat einen Sprachbefehl**, jeder Schalter mit jeder Stellung (ein Test prüft das für alle Profile; die vollständige Liste mit je einem englischen und deutschen Beispiel steht in `src/Kopilot/Voice/VoiceCommandCatalog.cs`):

| Bereich | Sätze |
|---|---|
| Konfiguration | Gear up/down, Fahrwerk ein/aus · Flaps 0/1/2/3/Full, Klappen null/eins/voll/einfahren (nicht „Klappen ein“ – zu nah an „eins“) · Speedbrake retract/half/full · Arm/Disarm spoilers, Spoiler armen |
| Bremsen, Licht, Schilder | Autobrake low/medium/max/off · Landing lights on/off, Landelichter an · Strobe on/auto/off · Beacon on · Taxi light on · Nav lights on, Nav and logo lights off · Logo lights on · Seat belts on/off, Anschnallzeichen an · No smoking on/auto, Rauchverbot an |
| Elektrik, APU | Batteries on/off, Batterien aus · External power on/off, Bodenstrom an · APU start · APU master on/off · APU bleed on/off |
| Enteisung | Engine anti ice on, Engine one anti ice on, Triebwerk zwei Enteisung aus · Wing anti ice on · Probe heat on/auto, Sondenheizung auto |
| EFIS, FCU | QNH 1013 / QNH one zero one three · Baro standard · Altitude five thousand · Flight level one two zero · Speed two five zero · Heading zero niner zero · Managed speed · Vertical speed minus one thousand · Autopilot one on · Approach on · ND mode ARC/ROSE NAV/PLAN, ND Modus Plan · ND range one hundred sixty, Range forty · Altitude increment one thousand · Expedite climb, Expedite off |
| Transponder, TCAS, Funk | Squawk seven thousand / Squawk 1 2 3 4 · Transponder standby/auto · Altitude reporting on/off, Höhenübermittlung aus · TCAS TA/RA · TCAS TA only · TCAS traffic all/above/below/threat · COM one standby one two one decimal five · Swap COM one |

**Ausführen + Callout** (Standard, `commands.executeWithCallout`): Wie in einer echten Crew ist dein Befehl die Anweisung – Kopilot führt ihn sofort aus und sagt ihn an, sobald das Flugzeug ihn übernommen hat („Speed two five zero set“, „Altitude five thousand set“). Bei Fahrwerk und Klappen kommt der Callout mit der erreichten Stellung („Gear down, three green“, „Flaps two“). „Not confirmed“ sagt Kopilot, wenn das Flugzeug den Wert nicht übernommen hat, „Unable“ bei einer Sperre (gesperrte Aktionen bleiben gesperrt, z. B. Batterien aus oder Bodenstrom im Flug) und „Input disabled“, wenn die Eingabe gesperrt ist. Das Eingabe-Modul protokolliert jeden dieser Befehle wie `prepare_action` → `confirm_action`.

Einen **Readback mit „Confirm“** gibt es nur, wenn die Erkennung unsicher war (Whisper-Konfidenz unter `commands.minConfidence`, Standard 0,6 – die Konfidenz steht bei jedem Satz im Log), und bei jeder Aktion, die laut Plausibilitätssperren eine Rückfrage braucht (siehe Eingabe-Modul, z. B. Transponder Standby oder Höhenübermittlung aus im Flug). Dann gilt der Zwei-Schritt-Ablauf: Kopilot spricht den Readback („Flaps one“); erst auf **„Confirm“**, **„Correct“** oder **„Affirm“** (auch „Korrekt“) wird ausgeführt; **„Negative“** oder **„Cancel“** verwirft die Aktion („Cancelled“). „Ja“, „OK“ oder „Roger“ allein bestätigen nichts – nach einem Readback fragt Kopilot dann „Say again“. Bestätigt wird nur, was du gehört hast: „Confirm“ zählt erst, wenn der Readback ganz gesprochen ist, und nur in einem Satz, den du danach begonnen hast. Ohne Antwort verfällt die Aktion 60 Sekunden nach dem Readback still; wird der Readback gar nicht gesprochen (keine Stimme, Pause), sofort. Kritische Aktionen fragen nach dem ersten „Confirm“ mit Warnhinweis nach („Caution, autothrust off. Confirm autothrust off?“); erst ein zweites „Confirm“ führt aus. Mit `"executeWithCallout": false` nimmt jeder Befehl diesen Weg. Zwei Befehle in einem Satz oder Unbekanntes → „Say again“ (längeres Gespräch im Raum wird still verworfen).

**Abfragen** – Antwort nur als reiner Callout (ohne Zusatzinfos), gesprochen mit derselben Aussprache wie alle Callouts, Geschwindigkeiten immer in Knoten:

| Frage | Antwort (Beispiel) |
|---|---|
| Speed check | Speed two five zero (am Boden: Ground speed one two) |
| Altitude check | Altitude five thousand / Flight level three five zero (bei STD) |
| Fuel check | Fuel twelve point four tons |
| Fuel versus plan, Landing fuel, Landemenge | Fuel thirty four point one, two point two below plan, landing fuel six point eight (ggf. „, below minimum“) |
| Next waypoint, Nächster Wegpunkt | Hendy, one hundred twenty miles, E T A zero three four two |
| ETA, Ankunftszeit | E T A zero four one eight |
| Top of descent, Sinkflugbeginn | Top of descent eighty five miles, E T A zero three one two / Top of descent passed |
| Traffic check, Verkehr | Traffic, 2 o'clock, 5 miles, 1000 feet above, descending (wie `get_traffic`) / No traffic |
| Wind check, Temperatur, Wind and temperature | Wind two seven one at forty five · Temperature minus forty seven |
| Squawk check, Transponder Code | Squawk two three four zero (ggf. „, standby“, wenn der Transponder auf Standby steht) |
| Station check, Which frequency, Aktive Station | Montreal Center, one three two decimal two five (aktive ATC-Station; ohne ATC-Sitzung: Active frequency …) |
| Engine check | Engines normal / Engine two not running / N1 split |
| Gear, Flaps, Speedbrake, Spoilers, Seat belts, APU, Baro, Heading check | Gear down · Flaps one · Speedbrake retracted · Spoilers armed · … |

Planwerte (Sprit gegen Plan, Wegpunkt, ETA, Top of Descent) rechnet Kopilot wie `get_progress` aus dem SimBrief-Plan – ohne Plan: „Unable“.

**Bestätigungspflicht:** Wichtige Meldungen erwarten **„Check“** oder **„Copy“** (auch „Checked“, „Copied“): Fuel-Warnungen, Sicherheits-Cautions und -Warnungen (Stall, Overspeed, Gear/Flap speed, Sink rate, Bank angle, Master Warning/Caution, Speed-Abweichung, Verkehr, Landing Check, Autothrust off, Approach-Phase, LOC/GS-Abweichung), Squawk- und Frequenzabweichungen, die das ATC-Modul meldet („Check squawk two three four zero“), und TCAS RA. Ohne Antwort wiederholt Kopilot die Meldung nach etwa 10 s einmal; kommt dann noch immer nichts, eskaliert er – Warnton und Meldung `ACK_MISSING` (Warnung) im Dashboard – und wiederholt nicht weiter. Ein späteres „Check“ oder das Ende des Zustands (z. B. Sink rate korrigiert) hebt die Meldung auf. Bei pausiertem Sim steht die Wartezeit; ist die Spracheingabe stumm, entfallen offene Quittungen (du kannst nicht antworten). Welche Meldungen zählen und die Wartezeit: `acknowledge` in `voice-settings.json`.

**Checklisten** (Challenge-Response nach der Airbus Normal Checklist): Start mit „Before start checklist“ („Checkliste vor dem Anlassen“), „After start“ („nach dem Anlassen“), „Before takeoff“ („vor dem Start“), „Approach“ („Anflug“), „Landing“ („Landung“), „After landing“ („nach der Landung“), „Parking“ („Parken“). Kopilot liest die Challenge („Before start checklist, cockpit preparation“), wartet auf deine Antwort und gleicht sie – wo möglich – mit dem Sim ab: Schilder und Anschnallzeichen, Spritmenge (gesagte Tonnen gegen Sprit an Bord), Baro-Referenz (Standard oder QNH gegen den Höhenmesser), Beacon, Klappenstellung, Autothrust, Autobrake, Landing Memo (Fahrwerk unten, Schilder, Ground Spoiler armed, Landeklappen), Klappen, Spoiler und APU nach der Landung, APU Bleed, Triebwerke aus. Eine Abweichung sagt Kopilot an („Beacon off“), der Punkt bleibt offen – nach dem Korrigieren noch einmal antworten; weicht es dann noch ab, wird der Punkt als offen vermerkt und es geht weiter („… checklist complete, open: beacon“). „Standby“ hält den Punkt, „Skip“ („Next item“, „Überspringen“) überspringt ihn (offen), „Say again“ wiederholt die Challenge, „Stop checklist“ („Checkliste abbrechen“) bricht ab. Ohne Antwort wiederholt Kopilot die Challenge nach 30 s einmal. Solange eine Checkliste läuft, ist jeder Satz ohne Anrede die Antwort („Seat belts on“ wird nicht ausgeführt) – Befehle und Abfragen gehen mit „Kopilot, …“; „Check“/„Copy“ quittieren weiterhin Meldungen.

**Steuerung:** „Kopilot Pause“ / „Kopilot weiter“ oder das Tastenkürzel (Standard `Ctrl+Alt+K`, systemweit) schalten die Spracheingabe stumm und wieder an – Callouts laufen weiter. „Say again“ wiederholt die letzte Antwort.

**Sprachausgabe:** Kokoro mit der Stimme des [First Officers](#first-officer-und-smalltalk) (eine feste mit `voices.kopilot`; First Officer aus und leer = Dashboard-Stimme); die ATC-Stationen sprechen mit den Stimmen, die das ATC-Modul vergibt. Alles läuft durch eine Warteschlange: Warnungen > Callouts > Antworten > ATC > Smalltalk – auch ATC-Funksprüche halten das Mikrofon stumm, und alles andere unterbricht Smalltalk. Zahlen, NATO-Alphabet und Funk-Phraseologie auf Englisch: Speed, Heading, Squawk und QNH Ziffer für Ziffer, Höhen in Tausendern, 9 als „niner“. Eine unterbrochene Antwort oder ATC-Meldung kommt nach der Warnung einmal erneut.

**ATC:** Funksprüche gehen direkt an das ATC-Modul (`AtcService.TransmitAsync`, wie `atc_transmit` – nur ohne Claude). Stationsnamen und eigenes Rufzeichen (Code und Sprechform, z. B. „Lufthansa four alpha bravo“) liefert das ATC-Modul aus SimBrief-Plan und `atc-templates.json`; die Antwort der Station kommt über die Warteschlange. „Say again“, „Affirm“ und „Negative“ ohne Station gehen an den, der zuletzt gesprochen hat – ATC oder Kopilot. Funkt ATC nach einem Readback von Kopilot, verfällt die offene Aktion, damit ein „Affirm“ an ATC nichts im Cockpit auslöst. Ist ATC aus oder fehlt der SimBrief-Plan, sagt Kopilot „ATC unavailable“.

**Einstellungen** (`voice-settings.json`, Kommentare erlaubt; alles gilt nur mit `"enabled": true` – sonst verhalten sich die Callouts wie ohne Sprachschleife):

| Abschnitt | Was |
|---|---|
| `microphone` | Gerät (Teil des Namens, leer = Standard), VAD-Schwelle, Mindestlänge, Stille bis Satzende, Nachlauf nach Ansagen |
| `recognition` | Whisper-Modell (`tiny`/`base`/`small`, auch `-q5_1`/`-q8_0`), `cpu`/`gpu`, Threads, Sprache `auto`/`de`/`en`, Wortschatz-Hinweis |
| `voices` | Kokoro-Stimme für Kopilot, Lautstärke für Kopilot und ATC |
| `hotkeys` | Tastenkürzel für stumm/an |
| `commands` | Anrede, Pflicht zur Anrede (`requireWakeWord`), Timeout für die Bestätigung, ausführen + Callout (`executeWithCallout`, Standard an), Konfidenz-Schwelle für den Readback (`minConfidence`, 0–1, Standard 0,6) |
| `acknowledge` | Bestätigungspflicht an/aus, Wartezeit bis zur Wiederholung und zur Eskalation (`repeatAfterSec`, Standard 10), Regeln, die „check“/„copy“ erwarten |
| `atc` | zusätzliche Rufzeichen und Stationen (die des Flugs kommen vom ATC-Modul), Rufzeichen aus MSFS |
| `callouts` | aktive und stumme Regeln, Stille bei Pause, Taxi-Speed-Warnung |

**Ressourcen** (MSFS hat Vorrang): Standard ist Whisper auf der CPU mit 4 Threads – gerechnet wird nur, wenn du gesprochen hast. `gpu` versucht CUDA, dann Vulkan, und fällt sonst auf die CPU zurück; neben MSFS in VR kann das Ruckler kosten. Schneller wird es mit fester Sprache (`"language": "de"`), `"audioContext": 768` oder dem Modell `tiny-q5_1`; besser Deutsch versteht `small-q5_1` (~190 MB).

Lizenzen: Whisper.net und whisper.cpp (MIT), Silero VAD (MIT), NAudio (MIT).

### First Officer und Smalltalk

**First Officer:** Mit Sprachschleife bekommt jeder Flug einen eigenen First Officer – einen zufälligen Namen aus einer Liste international gut aussprechbarer Namen, getrennt nach weiblich und männlich (z. B. Anna, Emma, Sophie / Sam, Ben, Daniel), und eine Kokoro-Stimme desselben Geschlechts (`af_`/`bf_` weiblich, `am_`/`bm_` männlich), die nie die Stimme einer ATC-Station ist (auch später vergebene Stationen meiden sie). Name und Stimme bleiben den ganzen Flug gleich – auch nach einem Neustart von Kopilot (`%LOCALAPPDATA%\Kopilot\first-officer.json`); ein neuer SimBrief-Plan ist ein neuer Flug mit neuem First Officer. Die Stimme gilt für Callouts, Antworten und Smalltalk. Spricht eine Windows-Stimme statt Kokoro, passt der Name zu ihrem Geschlecht.

- **Name im Dashboard:** in der Kopfzeile („F/O Sam“), im Stimme-Panel und unter Einstellungen.
- **Vorstellung beim Briefing:** einmal pro Flug am Gate (am Boden, höchstens 5 NM vom Startflughafen) – „Hi, I'm Sam, I'll be your first officer today“ – oder wenn du **„Briefing“** sagst („Start briefing“, „Let's do the briefing“, „Briefing starten“).
- **Ansprache mit Namen:** „Sam, gear up“, „Hey Sam, wie ist das Wetter in Berlin?“ wirken wie „Kopilot, …“ – „Kopilot“ bleibt immer gültig.
- **Fester Name:** Einstellungen → **First Officer und Smalltalk** (mit Geschlecht für Namen, die nicht in der Liste stehen). Eine feste Kokoro-Stimme (`voices.kopilot` in `voice-settings.json`) bestimmt das Geschlecht. Der First Officer lässt sich auch ausschalten (dann spricht Kopilot wie bisher mit der Dashboard-Stimme).

**Smalltalk (optional, standardmäßig aus):** Ein Satz, der weder Befehl noch Abfrage noch Funkspruch ist, geht an ein Sprachmodell statt „Say again“:

| Anbieter | Was du brauchst |
|---|---|
| **Claude** | eigener API-Schlüssel (console.anthropic.com); Modell leer = `claude-haiku-5-5` |
| **OpenAI-kompatibel** | Basis-URL (Standard `https://api.openai.com/v1`, auch LM Studio, vLLM …), Modell, API-Schlüssel, falls der Server einen will |
| **Ollama** | Adresse des anderen Rechners (z. B. `http://192.168.1.20:11434`) und das Modell |

Ein lokales Modell im Kopilot selbst gibt es bewusst nicht – zu viel GPU und RAM neben MSFS. Einrichten unter Einstellungen → **First Officer und Smalltalk** (gilt sofort, **Testen** stellt eine kurze Frage). Der API-Schlüssel steht nur in `appsettings.Local.json` (oder in der Umgebungsvariable `Kopilot__Smalltalk__ApiKey`), geht nie zurück an den Browser und taucht nie in Logs oder Fehlerberichten auf.

- **Vorrang:** Callouts, Warnungen, Bestätigungspflicht, Checklisten und Funk haben immer Vorrang – Smalltalk steht ganz unten in der Warteschlange, alles andere unterbricht ihn sofort (er wird nicht wiederholt). Solange ein Readback auf „Confirm“ wartet, eine Checkliste läuft, eine Meldung auf „Check“ wartet oder die Spracheingabe stumm ist, geht nichts ans Modell. Sätze ohne Anrede gehen nur bis `UnaddressedMaxWords` Wörter (Standard 12) ans Modell; längere sind meist Gespräche im Raum.
- **Nur lesend:** Das Modell bekommt nur lesenden Kontext (Flugphase, Position, Route, nächster Wegpunkt, ETA, Sprit und Sprit gegen Plan, Wind und Temperatur, METARs aus dem Plan, ATC-Station) und die letzten Sätze. Seine Antwort wird nur gesprochen – nie als Befehl ausgewertet, nie mit „Confirm“ vorbereitet; ein „Confirm“ danach bestätigt nichts.
- **Sprache:** Smalltalk in der Sprache des Dashboards; Callouts und Funk immer Englisch. Englisch spricht Kokoro (die Stimme des First Officers). Für Sprachen ohne Kokoro-Stimme (Deutsch) nimmt Kopilot **Piper** als zweite lokale Stimme (z. B. `de_DE-thorsten-medium` männlich, `de_DE-kerstin-low` weiblich – Geschlecht passend zum Namen). Piper (~20 MB, rhasspy/piper 2023.11.14-2, MIT) und die Stimme (~60 MB, rhasspy/piper-voices, Lizenz je Stimme in ihrer Model Card) lädt Kopilot beim ersten Bedarf nach `%LOCALAPPDATA%\Kopilot\piper` bzw. `…\models\piper`; bis dahin und für Sprachen ganz ohne Stimme läuft der Smalltalk auf Englisch mit kurzem Hinweis im Dashboard. Eigene `piper.exe`: `Smalltalk:PiperPath`. Ohne Smalltalk ist die Vorstellung Englisch.
- **Datenschutz und Kosten:** Die Einstellungsseite sagt, wohin das Gespräch geht. Antwortlänge (`MaxReplyWords`, Standard 40 Wörter) und Anfragen pro Minute (`RequestsPerMinute`, Standard 6) sind einstellbar. Über der Grenze, ohne Netz oder bei jedem Fehler antwortet Kopilot sofort mit „I can't chat right now“ (deutsch: „Ich kann gerade nicht plaudern“), statt zu hängen; jede Anfrage hat ein Zeitlimit (`TimeoutSec`, Standard 10 s).

## SimBrief

Die Airliner-FMS (iniBuilds, FlyByWire) geben ihre Route nicht über SimConnect heraus. Kopilot holt deshalb das letzte OFP von SimBrief (öffentliche API, kein Login). Am einfachsten im Dashboard unter **Einstellungen → SimBrief** (gilt sofort), oder in `appsettings.Local.json`:

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
- `SimConnect SDK\lib\SimConnect.dll` (native Bibliothek; die Verkehrsabfrage nutzt sie direkt)

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

## Release und Installer

Ein Tag `v…` startet den Workflow „Release“ (`.github/workflows/release.yml`): self-contained Build für win-x64 mit der Version aus dem Tag, Installer (Inno Setup, `installer/`) und ZIP, beides an ein GitHub Release mit Changelog aus den Commits seit dem letzten Tag. Das Release entsteht als **Entwurf** (nicht öffentlich); veröffentlichen bewusst über „Publish release“ auf GitHub oder `gh release edit v0.9.1 --draft=false`. Tags mit Zusatz (`v1.0.0-beta.1`) werden Pre-releases.

```powershell
git switch main; git pull
git tag v0.9.1
git push origin v0.9.1
```

**Einmalig vorher:** SimConnect für GitHub hinterlegen – auf dem PC mit dem MSFS-2024-SDK im Repo-Ordner `pwsh installer/simconnect-secret.ps1` (braucht die GitHub CLI). Das legt die DLLs als Secrets `SIMCONNECT_ZIP_*` ab; ohne sie bricht ein Release ab, statt gegen den Stub zu bauen.

**Testen ohne Release:** Actions → Release → Run workflow (Version angeben). Setup und ZIP liegen dann 14 Tage unter „Artifacts“.

**Der Installer** installiert nach `C:\Program Files\Kopilot` (oder ohne Adminrechte nur für den eigenen Benutzer), legt einen Startmenü-Eintrag an und auf Wunsch den Autostart (derselbe Eintrag wie „Mit Windows starten“ im Tray-Menü). Bei Updates bleibt `appsettings.Local.json` unangetastet; mitgelieferte Profile, `atc-templates.json` und `appsettings.json` werden erneuert, eigene Änderungen daran vorher nach `backup\<Datum>\` gesichert. Die Deinstallation entfernt Programm, Startmenü-Eintrag und Autostart und fragt, ob auch Einstellungen und `%LOCALAPPDATA%\Kopilot` weg sollen.

Der Produktname für Installer, ZIP und Release steht zentral als `<Product>` in `Directory.Build.props`.

## Erster Test im Browser

1. MSFS 2024 starten und einen Flug laden.
2. `C:\Tools\Kopilot\Kopilot.exe` starten. Ohne Parameter läuft der HTTP-Modus (einstellbar über `Kopilot:Transport`). Es öffnet sich kein Fenster – Kopilot sitzt im Infobereich der Taskleiste.
3. Doppelklick auf das Symbol öffnet das **Dashboard** unter <http://127.0.0.1:5155/>.

## Dashboard und Tray-Symbol

Das Symbol im Infobereich zeigt den Zustand am Punkt unten rechts: grau = wartet auf MSFS, grün = verbunden, gelb = Caution aktiv, rot = Warning aktiv. Rechtsklick öffnet das Menü: Dashboard, Profile neu laden, Logdatei, Programmordner, **Mit Windows starten** und Beenden.

Das Dashboard läuft im Browser, nur lokal auf dem Sim-PC (über den Tunnel ist es gesperrt). Oben rechts sitzen **Fehler melden** und **Einstellungen** (beides unten erklärt). Es zeigt:

- FMA-Zeile (Schub, vertikal, lateral, AP/FD/A-THR) in den Airbus-Farben
- Flugdaten mit Speed- und Höhenziel, Master Warning/Caution als Tasten oben rechts
- **Ansagen**: aktive Meldungen und Verlauf. Mit „Vorlesen an“ spricht der Browser jeden Callout mit einer englischen Stimme – Warnings unterbrechen alles andere. Das ist im Moment der einzige echte Push-Weg.
- ECAM im Klartext, Route mit Fortschritt aus SimBrief, Triebwerke, Konfiguration, ATC-Bahn und Wetter
- „Einrichtung und Diagnose“: alle Profil-Bindings mit Status geprüft/ungeprüft und aktuellem Wert, Log, Knöpfe zum Neuladen

Der Regler „Helligkeit“ dimmt nur die Anzeigen – für Nachtflüge.

### Einstellungen

`http://127.0.0.1:5155/#einstellungen` (Knopf **Einstellungen**) – statt `appsettings.Local.json` von Hand zu bearbeiten:

| Bereich | Inhalt | Gilt |
|---|---|---|
| **Verbindung zu Claude** | lokaler MCP-Endpunkt, öffentliche Adresse des Tunnels, Freigabe-Passwort (Claude-Connector), Bearer-Token neu erzeugen oder entfernen, Tunnel prüfen | nach Neustart |
| **SimBrief** | Username oder Pilot-ID, lädt danach gleich den Plan | sofort |
| **Mikrofon** | Aufnahmegerät der Sprachschleife mit Pegel | sofort |
| **Module** | Monitoring, Eingabe (zweimal klicken), ATC, Sprachschleife | Monitoring/ATC nach Neustart, sonst sofort |
| **First Officer und Smalltalk** | fester Name und Geschlecht, Vorstellung, Smalltalk-Anbieter (Claude, OpenAI-kompatibel, Ollama), Modell, Adresse, API-Schlüssel, Antwortlänge, Anfragen pro Minute, Test | sofort |
| **Sprache** | Dashboard, Tool-Meldungen, Tray-Menü, Meldungen aus „Fehler melden“ (siehe [Sprache](#sprache)); Logs bleiben Englisch | sofort |

Geschrieben wird in die vorhandenen Dateien: `appsettings.Local.json` neben der `Kopilot.exe` (Kommentare und Reihenfolge bleiben erhalten, vorher landet eine Sicherung in `%LOCALAPPDATA%\Kopilot\appsettings.Local.backup.json`), Eingabe und Sprachschleife wie bisher in `input.json` bzw. `voice-settings.json`. Was erst beim Start gelesen wird, zeigt die Seite als „gilt nach Neustart“ – der Knopf **Kopilot neu starten** startet die Tray-Instanz neu (die ATC-Sitzung kommt aus der Sicherung zurück). Passwort und Token gehen nie an den Browser zurück; ein neu erzeugter Token wird genau einmal zum Kopieren angezeigt. Setzt eine Umgebungsvariable (`Kopilot__…`) denselben Wert, weist die Seite darauf hin – die Variable hat Vorrang vor der Datei.

### Sprache

Kopilots eigene Texte sind standardmäßig Englisch, Deutsch ist optional. Umstellen im Dashboard unter **Einstellungen → Sprache** (gilt sofort) oder in `appsettings.Local.json`:

```jsonc
"Kopilot": { "Language": "de" }   // Standard "en"
```

- **Folgt der Einstellung:** Dashboard, Tool-Meldungen an Claude (Felder `message`/`note`), Tray-Menü und Meldungen aus **Fehler melden**.
- **Logs sind immer Englisch** (`kopilot.log`, Absturzberichte, das Log im Dashboard) – damit sie sich an Issues hängen lassen und alle Mitwirkenden sie lesen können.
- **Bleibt immer Englisch**, wie bei einer echten Crew (SOP): Callouts, Checklisten, FMA-Ansagen, Warnungen und Funk (ATC-Vorlagen). Claude selbst antwortet in der Sprache, in der du mit ihm sprichst.
- Werte in Flugzeugprofilen und ATC-Vorlagen sind sprachneutral (IDs, Einheiten); Geschwindigkeiten immer in Knoten.
- Die Sprachschleife versteht Befehle unabhängig davon auf Deutsch und Englisch (`recognition.language` in `voice-settings.json`).
- Übersetzungen: `src/Kopilot/i18n/<code>.json`, Details in [docs/i18n.md](docs/i18n.md).

Die Beschriftungen von Dashboard und Tray-Menü in diesem README sind die deutschen – mit der Standardsprache Englisch heißt es z. B. „Settings“ statt „Einstellungen“ und „Report a problem“ statt „Fehler melden“.

### Fehler melden

Der Knopf hält sofort fest, was Kopilot gerade weiß – Position (mit Kartenlink), Höhe und Flugfläche, Station, FIR und Frequenz laut Kopilot (samt Herkunft: MSFS, Vorlage oder Platzhalter), COM1 aktiv/standby, Transponder gegen Kopilot-Squawk, Uhrzeit (echt und Sim), Version, Flug und die letzten Funksprüche. Danach Art wählen (FIR-Grenze/Station, Frequenz, Airline/Rufzeichen, Programmfehler), kurz beschreiben, was falsch ist und was richtig wäre, und **Auf GitHub melden**: Es öffnet sich ein vorausgefülltes Issue mit der Vorlage `.github/ISSUE_TEMPLATE/kopilot-meldung.yml` und den Labels `data/fir`, `data/frequency`, `data/airline` bzw. `bug` – dort nur noch absenden. Jede Meldung liegt zusätzlich als Markdown in `%LOCALAPPDATA%\Kopilot\reports` (auch ohne GitHub-Konto oder Internet, **Nur lokal speichern**).

Datenkorrekturen werden nach Prüfung eingepflegt; die Datenbasis bleiben VAT-Spy/VATSIM-Sektoren und die Facilities-API, Meldungen sind Stichproben. Wird der Link zu lang (viele Funksprüche, langer Text), kürzt Kopilot ihn – vollständig steht alles in der lokalen Kopie und in der Zwischenablage (**Text kopieren**). Die Labels setzt der Link direkt nur für Leute mit Triage-Rechten; für alle anderen setzt die Action `issue-labels.yml` sie anhand einer unsichtbaren Markierung im Kontext. Repository und Vorlage stehen unter `Kopilot:Reports` (`GitHubRepository` leer = nur lokal).

**Log:** ohne Konsolenfenster schreibt Kopilot nach `%LOCALAPPDATA%\Kopilot\logs\kopilot.log` (die stdio-Instanz von Claude Desktop nach `kopilot-stdio.log`). Zur Fehlersuche gibt es weiterhin eine Konsole: `Kopilot.exe --console`. Ohne Tray-Symbol: `--no-tray`.

**Abstürze:** Unbehandelte Ausnahmen stehen sofort in `logs\crash.log`. Harte Abstürze (z. B. eine AccessViolation in SimConnect) kann .NET nicht abfangen – dafür führt die Tray-Instanz ein kleines Journal (`logs\journal-http.bin`), das auch einen harten Absturz übersteht. Beim nächsten Start meldet Kopilot im Log und in `crash.log`, dass der letzte Lauf nicht sauber endete, was zuletzt lief (z. B. „SimConnect.ReceiveMessage“, die letzten Verkehrsabfragen mit `dwSize`) und die Ausnahme samt Stacktrace aus der Windows-Ereignisanzeige. Windows startet die Tray-Instanz nach einem Absturz neu (`RestartAfterCrash`, erst nach 60 s Laufzeit, damit kein Absturz-Kreislauf entsteht); die ATC-Sitzung kommt dabei zurück.

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
- [ ] FBW: Werte mit `"verified": false` im Profil prüfen (siehe [docs/aircraft.de.md](docs/aircraft.de.md))
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

> Einfacher: Dashboard → **Einstellungen → Verbindung zu Claude**. Dort öffentliche Adresse und Passwort eintragen, den Token mit **Neu erzeugen** anlegen und Kopilot über den Knopf neu starten. Die Schritte unten machen dasselbe von Hand.

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

Schritt für Schritt in **[docs/cloudflared.de.md](docs/cloudflared.de.md)**: Named Tunnel, Subdomain `kopilot.flo210500.com`, cloudflared als Windows-Dienst, optional eine WAF-Regel, die `/mcp` nur für Claudes IP-Bereich öffnet.

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

Study-Level-Flugzeuge legen vieles (AP-Status, FMA, Autobrake, ECAM, Fuel Flow) nicht in Standard-SimVars ab, sondern in eigenen **L:-Variablen**. Ein Profil unter `profiles\*.json` sagt Kopilot, woher die Werte kommen. Mitgeliefert: FlyByWire A32NX, FlyByWire A380X, Headwind A330-900neo, iniBuilds A321LR. Was bei welchem Flugzeug geht, steht in [docs/aircraft.de.md](docs/aircraft.de.md).

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
- **limits** nutzt das Monitoring (Gear/Flap Speed, Overspeed, 250 kt unter FL100) und das Eingabe-Modul: `vloKt`, `vfeKt`, `maxGearAltitudeFt` (Fahrwerk ausfahren nur darunter), `speedbrakeInhibitFlaps` (z. B. `["FULL"]`), `msaSource` (`"ofp"` = MORA aus dem SimBrief-Navlog, höchster Wert der beiden Fixes des aktuellen Abschnitts). Fehlt ein Wert, gehen die betroffenen Aktionen nur mit Rückfrage.
- **verification** (optional): `gear.legs` und `flaps.surfaces` mit eigenen Variablen, Schwellen (`downAt`, `upAt`, `tolerance`, `settleSec`) und Zeitgrenzen (`timeoutSec`) für das Nachlesen nach Fahrwerk und Klappen.

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
  Speech/                 Sprachausgabe: Windows-Stimme oder Kokoro (Piper für Smalltalk in anderen Sprachen), Warteschlange mit Prioritäten, Ausgabegerät, gemeinsame Aussprache für ATC, Callouts und Fuel Check
  Voice/                  Sprachschleife: Mikrofon, Silero VAD, Whisper, Normalisierung, Router, Befehle, Dialog, ATC-Schnittstelle, First Officer
  Smalltalk/              Optionaler Smalltalk mit einem Sprachmodell (Claude, OpenAI-kompatibel, Ollama): lesender Kontext, Grenzen, Prompt
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
  Atc/Controller/         Kopilot-ATC: Zustandsmaschine, Vorlagen (mit Migration), Sprechweise, Readback-Prüfung, Stationen und Stimmen
  Atc/Airspace/           FIR-Grenzen (VAT-Spy), Sektoren, Airline-Rufzeichen (OpenFlights), Punkt-in-Polygon
  atc-templates.json      Textvorlagen des Kopilot-ATC
  data/                   Luftraum- und Airline-Daten mit Lizenzen (data/README.md)
  Monitoring/             Regeln (MonitorEngine), Alert-Bus, Hintergrunddienst
  Sim/TrafficConnection.cs Eigene SimConnect-Verbindung für Verkehr (native SimConnect.dll, Größenprüfung je Nachricht)
  Traffic/                Verkehrsabfrage, Peilung/Uhrzeit/Callout, Hintergrundabfrage für TRAFFIC_PROXIMITY
  Tools/                  MCP-Tools
  Localization/           Spracheinstellung, Texte nachschlagen (Loc)
  i18n/                   Texte je Sprache (en.json, de.json)
  profiles/               Flugzeugprofile (JSON)
src/SimConnectStub/       Platzhalter-API für CI-Builds ohne MSFS-SDK
tools/import_sectors.py   Erzeugt data/sectors/atc-sectors.json aus den VATGlasses-Sektordaten
tests/Kopilot.Tests/      Tests (OAuth, Zugriffsschutz, Monitoring-Regeln, ECAM, SimBrief, Profile, Verkehr, ATC, Sprachschleife) – laufen ohne MSFS
docs/cloudflared.de.md    Anleitung Cloudflare Tunnel
docs/i18n.md              Übersetzungen und Sprachdateien
installer/                Windows-Installer (Inno Setup) und Hilfsskripte für den Release-Workflow
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
- [x] ATC: FIR-Grenzen aus VAT-Spy, Sektoren mit Frequenzen (VATSIM), Gander Domestic, Rufzeichen aus OpenFlights, Vorlagen-Migration
- [ ] ATC: Rollroutenberechnung aus dem Taxiway-Graphen der Facilities-API (`ITaxiRouteProvider`), Gate aus der Parkposition
- [x] v0.9: Lokale Sprachschleife (Silero VAD, Whisper, feste Befehlsmuster, Readback und Bestätigung per Sprache), Prioritäts-Warteschlange, Taxi-Speed-Warnung, Funk an das ATC-Modul
- [x] v0.9.1: Sprachschleife im Dashboard ein- und ausschalten, Mikrofonauswahl, Status in `kopilot_status`, ausführliches Log; ATC-Sitzung übersteht Neustarts; Absturz-Journal und Neustart nach Absturz
- [ ] Sprachschleife: Testflug in VR, Aktionen für den iniBuilds A321LR
- [ ] Eingabe-Modul: im Sim prüfen, NAV & LOGO über Input Events, weitere Flugzeuge
- [ ] WASimCommander-Anbindung (L:-Variablen auflisten, Events)
- [x] Remote-Betrieb: Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- [ ] Sprachmodus mit dem Remote-Connector testen
