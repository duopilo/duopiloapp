# Kopilot 0.9 – Testlog

Flug CFG2205 (Condor 2205), EDDF–KLAX, A330-900neo (Headwind), 29.10.2026

## Funktioniert

- Traffic-Tool (kein Verkehr im Umkreis von 108 NM, plausibel über dem Atlantik)
- ATC-Sitzung starten, Funksprüche über Claude weitergeben
- Automatische Übergabe Shanwick → Gander an der FIR-Grenze
- Monitoring aktiv, Phase Cruise korrekt erkannt

## Bugs und To-dos

- **Radio Check:** Es fehlt ein eigenes Template. Antwort sollte z. B. „Condor 2205, read you five“ sein statt nur „roger“.
- **Rufzeichen:** ATC spricht „delta echo 2205“. Korrekt ist „Condor 2205“. Rufzeichen und Flugnummer immer aus dem SimBrief-OFP übernehmen (Sim-ATC ist unzuverlässig, nennt aktuell „Condor KID 6710“). Ursache gefunden: Kopilot nimmt das OFP-Feld callsign (DE2205, IATA-Code). Das Feld flightNumber enthält CFG2205, daraus den ICAO-Code für die Airline-Liste nehmen.
- **Oceanic-Status:** bleibt auf „NotYetDue“, obwohl ETARI passiert und ein Positionsreport abgegeben ist.
- **Squawk:** Kopilot vergibt 2340, im Transponder steht 3172. Abgleich bzw. Hinweis fehlt.
- **Lokale Sprachschleife:** Direkt gesprochener Funk landet nicht im ATC-Transkript, also keine Weiterleitung ans ATC-Modul.
- **Timeout:** Der erste atc\_transmit-Aufruf ist mit Timeout fehlgeschlagen, der zweite ging durch.
- **Frequenzen:** Oceanic-Frequenz ist nur ein Platzhalter (127.65).

## Sprachschleife und Stabilität

- **Absturz:** Kopilot ist während des Tests der Sprachschleife abgestürzt (ca. 19:47Z), Neustart nötig. Die ATC-Sitzung war danach weg.
- **Sprachmodul stumm:** Kein Log, kein Input aus dem Sprachmodul, auch nach dem Neustart nicht. Test mit „Seatbelt signs on“ ohne Reaktion.
- **Mikrofonauswahl fehlt:** In Kopilot lässt sich kein Mikrofon einstellen. Ein Windows-Standardaufnahmegerät ist gesetzt, trotzdem kommt nichts an. Konfiguration in C:\\Tools\\Kopilot prüfen.
- **Status:** Die Sprachschleife taucht in kopilot\_status nicht auf, ihr Zustand ist von außen nicht sichtbar.

## ATC-Templates: Abdeckung

- **Problem:** In atc-templates.json fehlen sehr viele Flughäfen und vermutlich auch viele Airlines.
- **Vorschlag:** Keine Listen per Hand pflegen. Rufzeichen aus dem SimBrief-OFP übernehmen, Frequenzen über die MSFS-Facilities-API holen. Im Template stehen dann nur noch Ausnahmen und Overrides.

- **Flughäfen:** Die Facilities-API liefert Pisten und Frequenzen für jeden Sim-Flughafen (KLAX 30, EDDF 21). Für manche Flughäfen ist noch ein sauberer Funkname nötig.
- **FIRs:** Bisher sind nur wenige im Template, Oceanic- und Center-Stationen liefert die API nicht. Idee: das VAT-Spy-Datenprojekt (VATSIM) als offene Basis für FIR-Grenzen und Stationsnamen nutzen. Die Frequenzen pro Sektor müssen ergänzt werden.

- **Aktuell im Template:** Langen, Bremen, München, Rhein, Maastricht, Amsterdam, Montreal, Toronto und ein paar weitere große. Auf dieser Route fehlen Minneapolis, Denver und Los Angeles Center. An der Grenze zu Minneapolis wird beobachtet.
- **KLAX fehlt:** Die Frequenzen kommen aus dem Sim, die Stationsnamen fehlen aber (z. B. SoCal Approach). Nicht dramatisch, im Notfall passt der Funk dann nicht ganz.

- **EDVV fehlt:** Die Routen-Erkennung liefert EDVV (Hannover UIR), im Template gibt es dafür keinen Eintrag. Oben wird das je nach Sektor von Maastricht oder Bremen kontrolliert. Entweder EDVV eintragen oder auf die zuständige Station mappen.
- **Fehlende Center auf dieser Route:** KZMP (Minneapolis Center), KZDV (Denver Center), KZLA (Los Angeles Center).

- **Airlines:** Im Template fehlen viele Airlines (Condor ist drin). Das OFP liefert den ICAO-Code (z. B. CFG), aber vermutlich nicht das gesprochene Rufzeichen. Idee: die offene Airline-Datenbank von OpenFlights mit Spalte für Funkrufzeichen als Basis nutzen, das Template nur für Ausnahmen. Aktuelle Liste hat 43 Einträge, die Rufzeichen stimmen. Es fehlen u. a. US-Airlines (Alaska, Spirit, Frontier, SkyWest), Asien-Pazifik für LAX sowie Töchter wie easyJet Europe, TUI UK und KLM Cityhopper.

## Bugfix-Test (Agent-Update, ca. 20:33Z)

- [x] Rufzeichen aus dem OFP: ATC spricht jetzt „Condor two two zero five“
- [x] Radio Check: Antwort „read you five“
- [x] Oceanic-Status steht nach ETARI auf „Cleared“

- Versionsnummer meldet weiterhin 0.9.0, sollte hochgezählt werden.

## Sektoren und Frequenzen

- **Problem:** Pro FIR gibt es nur eine Frequenz, meist einen Platzhalter. Gander (und andere FIRs) haben mehrere Sektoren mit eigenen Frequenzen.
- **Vorschlag:** Das Template bekommt pro FIR eine Liste von Sektoren mit Frequenzen. Kopilot wählt entlang der Route den passenden Sektor und macht Übergaben innerhalb der FIR.
- **Danach:** Frequenzprüfung beim Funk. Ein Radio Check auf 124.725 statt der aktiven 127.65 wurde nicht beanstandet.

- **Zusatzquelle (optional):** 124.725 war die vom Sim-ATC zugewiesene Gander-Frequenz. Ist das Sim-ATC aktiv, kann Kopilot die zugewiesene bzw. aktive COM1-Frequenz übernehmen. Ohne Sim-ATC (nur SimBrief-Flug) fällt das weg, die Sektorliste im Template bleibt die Basis.

## ETA-Berechnung prüfen

- **Beobachtung:** Kopilot zeigte im Flugverlauf zeitweise 16 Minuten zu früh, ab ca. 19:36Z dann +39 und später +42 Minuten (ETA KLAX 04:18Z, geplant 03:37Z).
- **Erklärbar:** ca. 10 Minuten späterer Start, knapp 15 Minuten Sim-Pause, etwas mehr Gegenwind als geplant. Der Sprung von −16 auf +39 ist damit aber nicht erklärt.
- **Prüfen:** Welche Überflugzeit hat Kopilot für ETARI genommen (geplant 17:38Z)? Wie werden Pausen behandelt? Die −16 Minuten kamen noch von der Version vor 0.9, die +39/+42 erst mit 0.9. Die ETA-Logik hat sich mit 0.9 also offenbar geändert. Prüfen, ob jetzt gegen den Fahrplan statt gegen den tatsächlichen Abflug gerechnet wird.

## Übergabe bei HOIST (21:18Z)

- **Falsche Station akzeptiert:** Kopilot hat um 21:18:07Z an Montreal Center (132.6) übergeben. Der Positionsreport 18 Sekunden später war an „Gander“ adressiert, Montreal hat trotzdem mit „identified, maintain FL320“ geantwortet. Erwartet wäre ein Hinweis wie „this is Montreal Center“ oder ein Nachfragen.
- **Gander Domestic fehlt:** Nach dem Oceanic-Ausgang bei HOIST kommt real zuerst Gander Center (Domestic), das Sim-ATC hatte 133.3 und 128.325. Kopilot springt direkt von Gander Oceanic zu Montreal Center, zu früh: Im Sim-ATC lief um diese Zeit noch keine Übergabe, man war weiter bei Gander Center. CZQX braucht einen Domestic-Teil bzw. Sektoren (siehe Sektoren und Frequenzen).
- **Übergabe kaum hörbar:** Das „contact Montreal“ kam ohne Aufforderung kurz vor dem Report und wurde überhört. Eventuell wiederholen, wenn sich der Pilot bei der alten Station meldet.

## Traffic-Test über Labrador (ca. 21:40Z)

- **Gefunden:** Emirates 222 (B777), 14 NM bei 2 Uhr, Gegenverkehr. Bei einer früheren Abfrage im 108-NM-Radius war nichts zu sehen.
- **Höhendifferenz falsch:** Kopilot meldet 2.400 ft darüber. Die Emirates steht auf barometrisch 33.000 ft (Flightradar bestätigt), der eigene Wert ist aber die wahre Höhe (30.630 ft bei −47 °C). Richtig wären ca. 1.000 ft (FL330 gegen FL320). Fix: beim Traffic die Druckhöhe vergleichen.
- **Ground Speed falsch:** Kopilot meldet 169 kt, Flightradar 556 kt. Prüfen, ob die falsche SimVar gelesen wird oder der Sim Live-Traffic sprunghaft bewegt.
- **Nicht sichtbar:** Das Objekt liefert SimConnect, im Sim ist es aber weder draußen noch auf dem TCAS zu sehen. Vermutlich ein Live-Traffic-Objekt des Sims ohne Darstellung und TCAS-Transponder.

## Ideen für eine Veröffentlichung

- **Einstellungsseite im Browser-Dashboard:** Konfiguration nicht nur über die Config-Datei, sondern im bestehenden Dashboard. Inhalt: Verbindung zu Claude (MCP/HTTPS-Tunnel), SimBrief-Konto, Mikrofonauswahl, Module ein- und ausschalten (Monitoring, Input, ATC, Sprachschleife).
- **Vertrieb:** kostenlos, da lokal und ohne Serverkosten, z. B. Open Source auf GitHub mit Spendenlink.
- **Name prüfen:** Nähe zu Microsoft „Copilot“, ggf. umbenennen.

## Referenz: Übergabe Gander → Montreal (Sim-ATC)

- Sim-ATC übergibt an Montreal Center bei ca. **53,51° N / 64,65° W** (ca. 25 NM westlich von Churchill Falls, ca. 85 NM vor YWK), ca. 22:00Z.
- Kopilot hatte bereits bei HOIST (Meile 2377, 21:18Z) an CZUL übergeben, also rund 260 NM zu früh.
- Fix: FIR-Grenze CZQX/CZUL in den Daten korrigieren und Gander Domestic als eigene Station nach dem Oceanic-Ausgang einbauen.

## Bug: Steigflug-Anfrage im Reiseflug (22:03Z)

- Funkspruch: „Montreal Center, Condor 2205, request climb flight level 340“.
- Antwort nur „CFG2205, roger“, keine Freigabe; clearedAltitude bleibt 32000.
- Erwartet: „Condor 2205, climb and maintain flight level 340“ mit Readback-Prüfung, danach clearedAltitude aktualisieren (ggf. Ablehnung/„expect higher“ bei Konflikt).
- Fix: Template für Höhenänderungs-Anfragen (climb/descend, FL und Fuß) in der Phase CenterCruise; Step-Climbs aus dem OFP (hier FL340 bei YWK) optional proaktiv anbieten.

## Idee: Squawk-Abgleich

- Transponder steht auf 3172 (vom Sim-ATC), Kopilot hat 2340 zugewiesen; Kopilot bemerkt die Abweichung nicht.
- Vorschlag: eingestellten Squawk (get\_systems) laufend mit dem zugewiesenen vergleichen; bei Abweichung ATC-Rückfrage wie real („Condor 2205, check squawk, should be 2340“). Optional Einstellung, den Squawk vom Sim-ATC zu übernehmen, wenn beide parallel laufen.

## Referenz: Sektorwechsel Montreal (Sim-ATC)

- ca. 22:30Z, kurz nach YWK, ca. 210 NM vor HENDY: Sim-ATC wechselt innerhalb Montreal Center auf 119.4 (vorher 132.25). Kopilot bleibt auf Platzhalter 132.6, kein Sektorwechsel.

## Idee: Melde-Button im Dashboard

- Button „Fehler melden“ im Browser-Dashboard, der automatisch Kontext mitschickt: Position, Höhe, aktuelle FIR/Station laut Kopilot, Kopilot-Frequenz, COM1-Frequenz, Uhrzeit, Version, letzte Transkript-Zeilen, Freitext.
- Ziel: GitHub-Issue mit Vorlage (Labels z. B. data/fir, data/frequency, data/airline), Datenkorrekturen nach Prüfung einpflegen.
- Datenbasis bleibt VAT-Spy/VATSIM-Sektordateien + Facilities-API; Meldungen dienen als Stichproben und Korrekturen.

## Idee: Automatischer Fuel Check an Wegpunkten

- Beim Überflug jedes OFP-Wegpunkts (im Reiseflug) Ist-Sprit gegen Plan vergleichen und per Sprachausgabe ansagen, z. B. „HENDY, fuel 34.1, 2.2 below plan“.
- Warnung nur, wenn der Rückstand seit dem letzten Check um mehr als eine Schwelle wächst oder die prognostizierte Landemenge unter Minimum + Puffer fällt.
- Optional: Intervall-Check für lange Strecken ohne Wegpunkte (z. B. alle 30 min), Verlauf im Dashboard als Trend.

## Namensprüfung (Agent-Recherche)

- „Kopilot“/„Copilot“: rot, mehrere Einträge in den Registern (DPMA, EUIPO, TMview) → umbenennen.
- Crosscheck: verworfen, EU-Marke vorhanden.
- Zweitpilot: laut Agent rechtlich am saubersten, aber deutsch und international schwer.
- **Favorit: MonitAir** – Domain frei; GitHub-Name als User vergeben (Luftqualitäts-Hobbyprojekt), Repo unter eigenem Account/Org unproblematisch.
- To-do: vor Veröffentlichung finale Markenprüfung für MonitAir (Klassen 9/41/42), Repo und Projekt umbenennen.

## Veröffentlichung: Build, Installer, Webseite

- Domain: **monitair.app** (monitair.com abgelaufen, ggf. später per Backorder als Weiterleitung).
- Webseite über GitHub Pages, monitair.app zeigt darauf.
- GitHub Action: bei Versions-Tag (v\*) bauen, Windows-Installer erzeugen (z. B. Inno Setup), an GitHub Release hängen. Vorlage: Pipeline vom EulenSlicer.
- Versionsnummer aus dem Tag übernehmen (behebt nebenbei das Problem, dass immer 0.9.0 gemeldet wird).

## Idee: Realistischer Befehlsablauf

Real: PF gibt Anweisung → PM führt sofort aus und sagt an („Speed 250 set“) → PF prüft am FCU/FMA. Kopilot fragt derzeit vorher nach (prepare/confirm). Testen, ob ein Modus „ausführen + Callout“ ohne Rückfrage realistischer ist und gleich zuverlässig verstanden wird; Rückfrage ggf. nur bei unsicherer Erkennung oder kritischen Aktionen behalten.

Regel: Alles, was den Flieger potenziell beschädigen oder irreversibel sein kann (Triebwerk abschalten, Fuel Pumps/Crossfeed, Feuerlöscher, IDGs, Hydraulik), nie automatisch ausführen – immer gegenseitig bestätigen wie real.

Plausibilitätssperren je Phase: z. B. Fahrwerk nur unterhalb VLO und 25.000 ft (A330-Limit), Klappen nur unter VFE, Speedbrakes nicht mit Klappen FULL – sonst ablehnen und Grund ansagen. Prüfen, welche Interlocks prepare\_action schon abdeckt.

Weitere Rückfrage-Fälle: Squawk 7700/7500/7600 setzen, ADIRS ausschalten, FCU-Höhe unter MSA bzw. unter/über der ATC-Freigabe.

Aus list\_actions (rev 0.9) ohne Interlock, aber riskant: elec.battery off im Flug · beide Packs off im Flug (Druckverlust) · xpdr.mode stby, xpdr.altReporting off, tcas.mode stby im Flug · fcu.ap1/ap2/athr off ohne Ansage · gear.down nur mit Speed-, nicht Höhen-Interlock · speedbrake ohne Klappen-Check · apu.master off bei aktivem APU bleed · elec.extPwr nur onGround (ok).

Fehlt: gear.down hat keine Verifikation. Nach Ausfahren Gear-Position aller drei Fahrwerke lesen und „three green“ / „gear down and locked“ ansagen, bei Abweichung Warnung. Analog gear.up: „gear up“ erst, wenn alle eingefahren.

## Realistischer Copilot: Abgleich mit Monitoring (0.9)

Vorhanden: PM-Callouts Start (100 knots, V1, rotate, positive climb) und Landung (spoilers, reverse, decel, 70 knots), ten thousand, transition, flaps, gear, one thousand to go, sink rate, bank angle, speed deviation, landing checklist, GEAR\_DOWN\_HIGH (nur Warnung, keine Sperre). Deaktiviert: AUTO\_RA, AUTO\_MINIMUM, AUTO\_RETARD. Fehlt: FMA-Ansagen bei Moduswechsel (nur Log vorhanden), LOC/Glideslope-Abweichung, Checklisten Challenge-Response, Flows, „gear down and locked“.

Sektorwechsel Montreal (Sim-ATC) nach HENDY, ca. 23:20Z: 119.4 → 125.9.

Bekannte Profil-Lücke (seit Test 28.10.): Headwind befüllt L:A32NX\_FMA\_VERTICAL\_MODE bei ALT CRZ (bleibt 0) und L:A32NX\_AUTOTHRUST\_MODE (SPEED, THR CLB) nicht – im Cruise daher leere vertikale FMA und thrust null. Alternative Headwind-Variablen suchen.

Übergabe CZUL→CZYZ: Kopilot ca. 23:25Z bei 49,88 N / 77,97 W, Sim-ATC zu diesem Zeitpunkt noch Montreal – Kopilot wieder zu früh (reale Grenze notieren, sobald Sim übergibt). Außerdem: „contact Toronto Center 132.6“ auf derselben Platzhalterfrequenz wie Montreal; clearedAltitude/cruiseFt weiter 32000 trotz FL340.

Bug: Pilot meldet „maintaining FL340“, Toronto antwortet „identified, maintain FL320“ – gemeldete Höhe wird ignoriert, keine Abweichungsprüfung gegen tatsächliche Höhe (34.000 ft). Erwartet: Höhe aus Meldung/Sim übernehmen oder „say altitude“ / Freigabe auf FL340.

## Idee: Internationalisierung

Dashboard, Tool-Meldungen (z. B. „Kein Verkehr im Umkreis…“) und Doku standardmäßig auf Englisch; Texte in Sprachdateien auslagern, Deutsch als Übersetzung, Sprache im Dashboard wählbar. Werte in Profilen/Templates bleiben sprachneutral.

Sprachlogik wie reale Crew: Bei gewählter Sprache (z. B. Deutsch) freie Gespräche und Erklärungen in dieser Sprache; Callouts, Checklisten, FMA-Ansagen und Funk immer Englisch (SOP).

Eigene Testsitzung (nicht im Anflug): prüfen, welche Ansagen der Headwind A330 selbst macht – RA-Callouts, retard, minimum, GPWS (terrain, pull up, sink rate, glideslope), TCAS RA (climb, climb). Danach festlegen, welche Kopilot-Callouts doppelt wären.

Bestätigungspflicht: Wichtige Meldungen (Fuel Check, Abweichungen, Warnungen) brauchen ein „check“/„copy“ des Piloten. Ohne Antwort nach einigen Sekunden wiederholen; bei kritischen Abweichungen nach zweiter fehlender Antwort eskalieren (real: Two-Communication-Rule, Verdacht auf Incapacitation).

## Weitere Flugzeugprofile

Bisher nur Airbus-Profile. Testflüge in einer Boeing (z. B. PMDG 737/777) und einer Cessna (GA) machen, Bindings erfassen. Für GA eigene Callout-Logik und Flugphasen (keine Airliner-Callouts, kein FMA, andere Speeds).

Entscheidung: nur Airliner mit zwei Piloten; bei GA PM-Funktionen deaktiviert (ATC, Traffic, Progress bleiben). Profile pro Modell, Callout-Logik gemeinsam.

Vorhanden: A320neo, A321, A330, A380. Noch zu erfassen: 737, 747, 767, 777, 787, A318, A319, A340, Embraer.

Für MSFS 2024 verfügbar (Stand Okt. 2026, Quellen: msfsaddons.com, FSElite, stormbirds):

- PMDG 737-600/700/800/900, 777-200ER/-200LR/-300ER/F
- iFly 737 MAX
- iniBuilds A300-600R(F), A330-200/300 (im Sim enthalten), A340, A350-900/1000
- TFDi MD-11
- Fenix A320, FSL A321neo, LatinVFR A321ceo/A330-300
- Captain Sim 717/777, FSS 727, CSS 737-500
- Aerosoft CRJ

In Entwicklung: PMDG 747 (frühestens 2027).

Reihenfolge: zuerst alle Base-Game-Airliner (u. a. Asobo 747-8, 787, A320neo, iniBuilds A330), danach Add-ons.

Parallel: vorhandene FlyByWire-Profile A32NX und A380X auf Korrektheit prüfen.

Hinweis für den Agent: zuerst prüfen, welche Interlocks und Bestätigungen in prepare\_action schon existieren, nur Lücken ergänzen.

## Beobachtungen ca. 00:00Z (Nord-Ontario, nach YTS)

- Sim-ATC: Übergabe CZUL→CZYZ bei 49,49 N / 79,02 W (Kopilot ca. 40 NM zu früh). Danach Toronto-Sektorwechsel auf 135.4.
- Traffic: Laut Flightradar ist eine reale DHC8 etwa auf unserer Position (tiefer). Sim zeigt sie nicht, get\_traffic meldet auch mit Bodenverkehr 0 Ziele im Radius 108 NM → Live-Traffic des Sims lückenhaft, kein Kopilot-Fehler.

- Kurz danach weiterer Toronto-Sektorwechsel auf 124.075.

- Ca. 00:06Z: Porter 2355 (DHC8) taucht doch auf – 8 o'clock, 15 NM, FL240. Kopilot meldet 9.400 ft below statt ca. 10.000 ft (wahre Höhe, siehe Ergänzung A) und GS 92 kt (unplausibel, real ca. 300 kt) → GS-Bug weiterhin vorhanden. Ziel erscheint im Sim nur sporadisch.

- Ca. 00:10Z: Kopilot wechselt Richtung USA (Minneapolis/KZMP-Fallback) bereits ca. 30 NM vor der Grenze über dem Lake Superior; Sim-ATC noch bei Toronto. Wieder zu frühe Übergabe → Muster bei allen FIR-Wechseln.

- Kopilot-Wechsel auf KZMP um 00:08Z: Stationsname fehlt („contact Center 132.6“, Station nur „Center“). Erstruf „Minneapolis Center … maintaining FL340“ um 00:14Z → wieder „identified, maintain FL320“ (Höhen-Bug). Sim-ATC übergibt erst ca. 00:15Z an Minneapolis → Kopilot ca. 7 min zu früh.

- Ca. 00:20Z: Air Canada 148 (A220), 5 o'clock, 9 NM, FL310 level. Kopilot: 2.400 ft below (richtig ca. 3.000) und GS 93 kt. Zusammen mit Porter 2355 (92 kt) liefert die GS-Berechnung bei verschiedenen Zielen fast denselben falschen Wert → vermutlich systematischer Fehler (falsche SimVar, Einheit oder Berechnung aus Positionsdeltas), nicht Rauschen. Für Prompt A: GS direkt per GROUND VELOCITY des Ziels lesen und Einheit prüfen.

- Abgleich Flightradar: ACA148 real FL360 mit GS 410 kt, also über uns statt 3.000 ft darunter. Die Höhe von 30.997 ft stammt vom Sim-Live-Traffic oder ist ein Kopilot-Fehler → prüfen, welche SimVar gelesen wird (PLANE ALTITUDE vs. INDICATED/PRESSURE). Das Ziel wird im Sim offenbar nicht mehr dargestellt.

- Sim-ATC weist ACA148 an, FL260 zu halten. Damit drei verschiedene Höhen: Flightradar FL360, Sim-ATC FL260, Kopilot/SimConnect 30.997 ft. Der Sim-Live-Traffic selbst ist inkonsistent → für Traffic-Tests besser KI-Verkehr oder Multiplayer mit bekannter Höhe nutzen.

- Ca. 00:25Z: Sim-ATC Minneapolis-Sektorwechsel auf 119.1 bei 46,87 N / 86,12 W (Lake Superior, kurz vor SAW).

- Ca. 00:28Z (SAW, FL360): ETA KLAX +49 min. Ergänzung zu Prompt G: Florian vermutet die Ursache in der in 0.9 geänderten ETA-Berechnung (Navlog-Zeiten verschoben um die Abweichung am letzten passierten Fix). Agent soll per git diff die ETA-Logik vor/nach 0.9 vergleichen und gegen die Groundspeed-Rechnung (Restdistanz/GS ≈ 3:56 h) plausibilisieren.

- Klarstellung: Die alten −16 min (vor 0.9) waren ein Berechnungsfehler, der Sprung auf +48 min ist die Korrektur. Plausibilitätsprüfung passt (1.623 NM / 418 kt ≈ 04:21Z vs. Kopilot 04:25Z). Prompt G damit vermutlich erledigt, nur noch Gegenprobe am Ziel.

- Ca. 00:32Z: Sim-ATC Minneapolis-Sektorwechsel auf 121.25 bei 46,21 N / 87,86 W (kurz nach SAW, FL360).

- Ca. 00:35Z: **Zweiter Absturz** von Kopilot (MCP-Server antwortet nicht mehr), kurz nach SAW auf FL360. Letzte Aktionen davor: fcu.altitude FL360, get\_systems, get\_progress, get\_flight\_state. Für Prompt D: Crash-Logging/Absturzursache mit aufnehmen (globaler Exception-Handler, Log-Datei, automatischer Neustart der SimConnect-Verbindung).

- Kurz danach: Nach Neustart sofort erneuter Absturz → reproduzierbar, vermutlich fester Auslöser (bestimmter Sim-Wert/Zustand nach SAW, FL360). Ereignisanzeige (Anwendung, „.NET Runtime“) auslesen und Stacktrace an Prompt D geben.

- **Absturzursache (Ereignisanzeige, 2× identisch):** AccessViolationException in SimConnect.ReceiveMessage → MarshalRECVPtrToObject → PtrToStructure → CSTRMarshaler.ConvertFixedToManaged. Beim Umwandeln eines festen Strings aus einer SimConnect-Nachricht wird über das Nachrichtenende hinaus gelesen. Hauptverdacht: Hintergrund-Verkehrsabfrage (TrafficService, alle 3 s, Struct TrafficDatum mit vier ByValTStr-64-Strings: ATC ID/AIRLINE/FLIGHT NUMBER/MODEL). Abstürze treten auf, sobald Live-Traffic im Umkreis ist (19:47Z, 00:35Z nach Porter 2355/ACA148). Vermutung: MSFS 2024 liefert für manche Live-Traffic-Objekte einen kürzeren Datenblock als die Definition. AccessViolation ist in .NET nicht abfangbar → muss vorher verhindert werden.
    - Workaround sofort: in appsettings.Local.json `Monitoring.DisabledRules: ["TRAFFIC_PROXIMITY"]` und get\_traffic nicht aufrufen.
    - **Für Prompt A:** Hintergrundscan nur mit numerischer Definition (keine Strings). Strings nur bei get\_traffic und robust lesen: entweder eigener Dispatch über die native SimConnect.dll mit Größenprüfung gegen dwSize, oder Strings separat pro Objekt anfragen. Vorher reproduzieren und dwSize der BYTYPE-Nachrichten loggen, um die Annahme zu bestätigen.

- **Absturz 19:47Z (Ostatlantik, kein Traffic):** identischer Stacktrace (AccessViolation in CSTRMarshaler.ConvertFixedToManaged bei ReceiveDispatch). Damit drei Abstürze mit demselben Muster.
    - Neue, besser passende Hypothese: Auch ohne Verkehr läuft der Hintergrundscan alle 3 s. Die Antwort „kein Objekt im Umkreis“ (dwoutof = 0) enthält vermutlich keinen Datenblock, die SimConnect-Managed-Bibliothek marshalt aber trotzdem die komplette TrafficDatum-Struktur (328 Byte) über das Nachrichtenende hinaus. Meist ist der Speicher dahinter lesbar (Müll), selten liegt er auf einer nicht gemappten Seite → Absturz. Erklärt, warum es zufällig und auch ohne Traffic passiert.
    - Für den Agent: dwSize aller BYTYPE-Nachrichten loggen (auch dwoutof = 0) und beide Hypothesen prüfen. Fix muss das Marshalen ohne Größenprüfung vermeiden, siehe oben. Workaround (TRAFFIC\_PROXIMITY aus) deckt beide Fälle ab; get\_traffic bleibt riskant, auch ohne Verkehr.

- Ca. 00:45Z: Sim-ATC Minneapolis-Sektorwechsel auf 133.65 (zwischen SAW und EAU, FL360; Position nicht erfasst, da Kopilot abgestürzt).

- Ca. 00:45Z: Kopilot neu gestartet mit TRAFFIC\_PROXIMITY aus, läuft stabil. get\_traffic bei Rhinelander: 0 Ziele, kein Absturz (noch nicht aussagekräftig). Absturztest mit get\_traffic bei Minneapolis geplant.
    - Zweite Hypothese (Florian): Absturz hängt mit mehreren Zielen gleichzeitig zusammen. Unwahrscheinlich, da die AccessViolation vor Kopilots Code im SimConnect-Marshaling auftritt, aber im Fix mit testen: Scan mit vielen Zielen, je Nachricht dwObjectID und dwSize loggen.

- Ca. 00:50Z: Sim-ATC Minneapolis-Sektorwechsel auf 124.4 bei 45,43 N / 90,06 W (Nord-Wisconsin, vor EAU, FL360).

- Einschränkung Traffic-Hypothese: Beim ersten Absturz ca. 19:47Z (Ostatlantik) war laut Florian kein Verkehr in der Nähe. Ereignisanzeige-Eintrag von 20:47 Uhr Ortszeit prüfen. Bei gleichem Stacktrace (CSTRMarshaler) auch die anderen String-Lesungen untersuchen: SimString256 in DefineVariable (z. B. TITLE, ATC ID), FacilityFrequencyData.Name (CHAR\[64\]).

- Ca. 00:55Z: Sim-ATC Minneapolis-Sektorwechsel auf 125.3 bei 45,13 N / 90,87 W (kurz vor EAU, FL360).

- Ca. 01:05Z: Sim-ATC Minneapolis-Sektorwechsel auf 133.7 bei 44,61 N / 92,43 W (nach EAU, Mississippi bei Red Wing, FL360).

- Ca. 01:10Z: Sim-ATC Minneapolis-Sektorwechsel auf 132.35 bei 44,38 N / 93,20 W (südlich Minneapolis, Nähe Northfield, FL360).

- Ca. 01:15Z: Sim-ATC Minneapolis-Sektorwechsel auf 135.0 bei 44,21 N / 93,73 W (Nähe Mankato, FL360).

- Ca. 01:18Z: Sim-ATC Minneapolis-Sektorwechsel auf 127.75 bei 44,07 N / 94,16 W (westlich Mankato, FL360).

- Ca. 01:22Z: get\_traffic mit zwei GA-Zielen, kein Absturz. C172 N605SD und C441 N441E werden mit ident/callsign „1432126“ bzw. „1520435“ angezeigt (vermutlich ATC FLIGHT NUMBER ohne Airline). Für Prompt A: Ist ATC AIRLINE leer, ident/callsign = ATC ID (Registrierung); Callsign aus Airline + Flugnummer nur, wenn beide gesetzt. Typ teils als Rohstring („ATCCOM.AC MODEL C441.0.tts“) → bereinigen. GS (46/72 kt) wieder unplausibel.

- Ca. 01:30Z: Sim-ATC Minneapolis-Sektorwechsel auf 127.1 bei 43,79 N / 95,01 W (Süd-Minnesota, Nähe Windom, FL360).

- Ca. 01:32Z: Sim-ATC Minneapolis-Sektorwechsel auf 132.05 bei 43,72 N / 95,22 W (kurz vor Worthington, FL360) – nur ca. 10 NM nach dem Wechsel auf 127.1.

- Ca. 01:40Z: Sim-ATC Minneapolis-Sektorwechsel auf 124.1 bei 43,32 N / 96,37 W (Nordwest-Iowa, nahe Grenze zu South Dakota, FL360).

- Ca. 01:41Z: Sim-ATC zurück auf 132.05 bei 43,29 N / 96,47 W – nur ca. 5 NM nach dem Wechsel auf 124.1. Auffällig: Sektorwechsel hin und zurück innerhalb weniger Meilen; könnte Sim-ATC-Eigenheit sein (Grenzlinie entlang der Route) oder reales Sektor-Layout. Für Prompt B: Hysterese bei Sektorwechseln vorsehen, damit Kopilot bei Grenzverläufen entlang der Route nicht ständig hin- und herschickt.

- Ca. 01:44Z: Sim-ATC wieder auf 124.1 bei 43,19 N / 96,75 W (Grenze Iowa/South Dakota) – drittes Hin und Her innerhalb von ca. 25 NM (124.1 → 132.05 → 124.1). Bestätigt die Hysterese-Anforderung für Prompt B.

- Ca. 01:43Z: Sim-ATC Minneapolis-Sektorwechsel auf 128.0 bei 42,74 N / 97,99 W (Nord-Nebraska, ca. 35 NM vor ONL, FL360). Hinweis: Uhrzeiten der letzten Einträge (ab ca. 01:30Z) sind geschätzt und können einige Minuten zu spät notiert sein.

- Ca. 01:45Z: Alaska 1427 (B737) kreuzt hinter uns. Sim-ATC weist sie hörbar zum Sinken an, laut SimConnect bleibt sie aber auf FL340 (leicht steigend). Optisch nur Positionslichter, auf dem ND als weißes Symbol. Kopilot meldet wieder 2.279 ft statt 2.000 ft (Höhenfehler A). Erkenntnis: Live-Traffic folgt den realen ADS-B-Daten; der Sim-ATC-Funk dazu ist nur Kulisse und wird nie umgesetzt.

- Ca. 01:50Z (nach ONL): Delta 363 (B757), 12 o'clock, 12 NM, FL390, Gegenverkehr. Laut SimConnect vorhanden, Sim-ATC spricht sie an – aber weder außen sichtbar noch auf ND/TCAS. Im Gegensatz zu Alaska 1427 (TCAS ja, außen nur Lichter). Live-Traffic-Objekte haben also unterschiedlich vollständige Darstellung (Modell/Transponder). Für Kopilot: Ziele ohne TCAS-Darstellung ggf. kennzeichnen oder nur bei Sichtbarkeit melden; prüfen, ob eine SimVar (z. B. Transponder-State des Ziels) das unterscheidet. GS wieder unplausibel (169 kt).

- Referenz FIR-Wechsel KZMP→KZDV (Sim-ATC): Übergabe an Denver Center 132.7 bei 42,25 N / 99,19 W (Nebraska, nach ONL, FL360). Kopilot-Übergabezeitpunkt noch gegenprüfen.

- ATC-Sitzung nach Absturz-Neustart neu gestartet (ca. 01:53Z): Kopilot steht noch auf KZMP. Laut Kopilot beginnt KZDV bei Meile 4224, wir sind bei 4202,5 – also bereits im Denver-Sektor des Sim-ATC. Diesmal übergibt Kopilot ca. 20–25 NM zu SPÄT (bisher immer zu früh) → Grenzen sind nicht systematisch verschoben, sondern ungenau. Außerdem nach Neustart: clearedAltitude/cruiseFt wieder 32000 statt FL360, Squawk 2340, Station wieder nur „Center“.

- 01:55:02Z: Kopilot übergibt KZMP → KZDV bei Meile \~4224 („CFG2205, contact Center 132.6“), ca. 3 min bzw. \~20 NM nach dem Sim-ATC. Wieder ohne Stationsname, Platzhalterfrequenz 132.6 (gleiche Frequenz wie vorher), im Transkript „CFG2205“ statt „Condor two two zero five“ – prüfen, ob TTS das gesprochene Rufzeichen nutzt. Stimmenwechsel bf\_alice → am\_adam funktioniert.

- 01:56Z: Erstanruf Denver „maintaining FL360“ → „identified, maintain FL320“ (Höhen-Bug erneut). Laut Florian klang die Sprachausgabe gut – das Rufzeichen wird offenbar korrekt gesprochen, nur das Transkript zeigt „CFG2205“.

- Ca. 02:10Z: Step Climb FL360 → FL380 ohne Freigabe (weder Kopilot- noch Sim-ATC kann Höhenanfragen), FCU über Kopilot gesetzt, verification confirmed; Callout „one thousand to go“ kam korrekt. Danach Sim-ATC Denver-Sektorwechsel auf 118.475 bei ca. 41,00 N / 101,90 W (West-Nebraska, vor FQF, FL380).

- Ca. 02:15Z: Sim-ATC Denver-Sektorwechsel auf 125.925 bei 40,65 N / 102,64 W (Nordost-Colorado, FL380).

- Ca. 02:20Z: Sim-ATC Denver-Sektorwechsel auf 133.95 bei 40,41 N / 103,16 W (Nordost-Colorado, nahe Fort Morgan, FL380) – nur ca. 27 NM nach dem Wechsel auf 125.925.

- Ca. 02:22Z: Flexjet 482 (Citation, LXJ482), Flightradar bestätigt FL450 / 506 kt; Kopilot: Höhe 45.000 ft korrekt, GS 84 kt (Faktor \~6 daneben). Typ als Rohstring „ATCCOM.ATC NAME CITATION.0.tts“. Zusätzlich Delta 506 (A320) FL340, GS 128 kt laut Kopilot. GS-Fehler damit bei allen Zielen bestätigt.

- Ca. 02:27Z: Sim-ATC Denver-Sektorwechsel auf 125.95 bei 40,01 N / 103,98 W (östlich Denver, FL380).

- Ca. 02:40Z: Denver-Sektorwechsel auf 119.85 bei 39,36 N / 105,41 W (südwestlich Denver, FL380).

- Ca. 02:50Z: Denver-Sektorwechsel auf 134.5 bei 38,88 N / 106,55 W (zentrale Rockies, FL380).

- Ca. 02:51Z: direkt danach Wechsel auf 125.35 bei 38,86 N / 106,60 W — nur \~3 NM nach 134.5 (weiteres Beispiel für Hysterese).

- Ca. 03:03Z: Denver-Sektorwechsel auf 118.575 bei 38,00 N / 108,52 W (kurz vor DVC, FL380).

- Ca. 03:07Z: Denver-Sektorwechsel auf 127.55 bei 37,82 N / 108,90 W (über DVC, FL380).

- Ca. 03:12Z: FedEx 1290 (B767) laut SimConnect FL370, Sim-ATC-Funk nennt 9.200 ft → Sim-ATC behandelt Live-Traffic eigenständig, nicht nach realer Flugbahn (stützt Idee Hintergrundfunk aus Traffic-Daten).

- Idee (später, kein Prompt): eigenes Flugbuch im Dashboard (Flugzeiten Block/Air, Route, Sprit, Landungen).

- Ca. 03:23Z: FIR-Grenze KZDV→KZLA, Los Angeles Center 124.2 bei 36,85 N / 111,69 W (kurz vor PGA, FL380). Letzter Referenzpunkt. Kopilot noch auf KZDV (Meile 4864), KZLA erst ab Meile 4896 → \~32 NM ZU SPÄT.

- Ca. 03:30Z: Sim-ATC (LA Center) meldet B737, 11 Uhr, FL390 — weder SimConnect (0 Ziele in 40 NM) noch TCAS zeigen sie. Weiterer Fall von Kulissenverkehr.

- Ca. 03:31Z: Kopilot wechselt auf KZLA (Meile \~4896) — bestätigt \~32 NM zu spät gegenüber Sim-ATC.

- 03:35Z Traffic: Delta 2698 (SEA–PHX, B738) laut Kopilot 5.144 ft, climbing, GS 158 kt; real (Flightradar) FL330. Höhe massiv falsch — prüfen, ob Sim-Live-Traffic oder Kopilot-SimVar die Ursache ist (Prompt A).

- 03:37Z Traffic: Southwest 3469 (WN3469) Höhe FL380 korrekt, GS Kopilot 146 kt vs. real 464 kt. Delta 2698 steigt im Sim von 5.100 auf 12.000 ft (\~2.500 fpm) – Sim zieht falsch gespawnte Ziele offenbar zur Realhöhe nach.

- 04:03Z ATC: "descend 11000 ft" ohne QNH und ohne "descend via ANJLL4" (Prompt C). Readback-Freigabe lief einmal ab (Approval-Timeout).
- TOD: Master Caution NAV BARO REF DISCREPANCY (Captain/F/O Baro unterschiedlich), behoben.
- FL185: efis.baroMode QNH + efis.baro 1011 per Kopilot – confirmed, aber nur Captain-Seite. F/O-Seite fehlt im Profil (Prompt L / Profil A339). Gute Sperre: efis.baro verweigert, solange STD aktiv.

- Testpunkt Prompt F (Fuel Check): Plötzliche Spritänderung (Nachtanken über Toolbar/EFB, wird im Tablet übernommen) als Nachtanken erkennen, nicht als Leck/Fehler. Nur am Boden testen.

## Beim Anflug prüfen

- 16 unverifizierte Bindings im A339-Profil, u. a. Master Caution, Flaps, Spoilers Armed, VLS, Decision Height, Transition Altitude/Level
- Deaktivierte Regeln: AUTO\_RA, AUTO\_MINIMUM, AUTO\_RETARD

## Anflug und Landung KLAX (04:35–05:20Z)

Gelandet auf 24R nach zwei Durchstarts und Versetzen per Slew auf den Endanflug; Gate E10.

- Nachtanken in der Luft (3.526 → 11.090 kg) löste **keinen** Leck-Alarm aus — Testpunkt Prompt F bestanden (in der Luft statt am Boden).
- TCAS RA zweimal: FMA zeigt „TCAS“, danach bleibt der AP in V/S und APPR wird gelöscht. Kopilot meldete den RA **nicht** → Callout „TCAS RA“ / „Clear of conflict“ ergänzen (Prompt L).
- Autopilot-Trennung bei Speed unter VLS mit Flaps 0 wurde korrekt als „Autopilot off“ gemeldet.
- Autothrust ging im Endanflug unbemerkt auf OFF — Kopilot meldete nichts → Callout „Autothrust off“ ergänzen.
- Managed Speed im Anflug ohne Ziel (speedTargetKt null), Approach-Phase nicht aktiviert → Warnung „Approach phase not activated“ ab ca. 15 NM vor der Schwelle als Idee.
- Overspeed mit Flaps und „Speed limit“ unter FL100 korrekt gemeldet.
- Gate wird von MSFS nicht als SimVar geliefert, Taxi-Flag bleibt nach Rollfreigabe 0. atc\_set\_value gate = E10 funktioniert.
- Aktionen am Boden verifiziert: spoilers.arm disarm, flaps 0, lights.runwayTurnoff on (alle „confirmed“). apu.master on nur „unverified“ (nur Schaltervariable).
- **Bedienung**: Claude-Freigabedialoge blockieren das Sim-ATC-Fenster und laufen nach 30 s ab. Für Lese-Tools und atc\_set\_value „Immer erlauben“ nutzen; Readback-Schutz von Kopilot bleibt.
- Treibstoff am Gate: 9.555 kg.

Am Gate: Jetbridge/Treppe fehlte nur im Headwind-Modell, über das Sim-Menü ließ sich eine Treppe anfordern (kein Kopilot-Thema).
