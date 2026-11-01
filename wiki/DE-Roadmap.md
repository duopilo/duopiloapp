[English](Roadmap) | **Deutsch**

# Roadmap

## Erledigt

- **v0.1** – MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, Profilen für `L:`-Variablen
- **v0.3** – Pilot Monitoring (Regeln, Callouts, Warnungen), FBW-Profile, ECAM im Klartext, SimBrief, Bahn aus der ATC im Spiel
- **v0.4** – Tray-Symbol und Dashboard im Browser, Callouts im Browser vorgelesen
- **v0.5** – Korrekturen nach dem Testflug EDDL–EDDM, TOD, Landecheck, FMA-Log, FMS-Wegpunkt
- **v0.6** – lokale Sprachausgabe (Windows-Stimme, Ausgabegerät) und PM-Callouts
- **v0.7** – Eingabe-Modul mit Readback und Bestätigung (zuerst A32NX)
- **v0.8** – neuronale Stimme Kokoro, ECAM-Titel ausgeschrieben, Schalter im Tray
- **Verkehr** – `get_traffic` und die Annäherungswarnung
- **ATC-Modul** – Zustandsautomat mit Vorlagen von Delivery bis Ground inkl. Oceanic, Readback-Prüfung, Stimme je Station; FIR-Grenzen (VAT-Spy), VATSIM-Sektoren, Rufzeichen aus OpenFlights
- **Fernbetrieb** – Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- **v0.9** – lokale Sprachschleife (Silero VAD, Whisper, feste Kommandos, Readback per Stimme), Prioritäts-Warteschlange, Warnung bei zu schnellem Rollen, Funk zum ATC-Modul
- **v0.9.1** – Schalter für die Sprachschleife und Mikrofonwahl im Dashboard, ATC-Sitzung übersteht Neustarts, Absturzprotokoll und Neustart nach einem Absturz
- **Seitdem** – Umbenennung in Duopilo, Englisch als Standardsprache mit deutscher Übersetzung, Checklisten, Bestätigungen, Notfälle in der ATC, First Officer mit Smalltalk (Claude, OpenAI-kompatibel, Ollama), Stimme Supertonic, Installer und Release-Workflow

## Als Nächstes

- [ ] Testflug in VR mit der Sprachschleife
- [ ] `L:`-Variablen und Aktionen für den iniBuilds A321LR (FMA, A/THR, Master Warning, ECAM)
- [ ] Eingabe-Modul: im Sim prüfen, NAV & LOGO über Input Events, weitere Flugzeuge
- [ ] ATC: Rollweg aus dem Rollweg-Graphen der Facilities-API berechnen, Gate aus der Parkposition – Graph und Routensuche gibt es seit dem Rolltest des Verkehrs (beta.22), die Anbindung an ATC fehlt noch
- [ ] ATC: Squawk 7600/7500 behandeln
- [ ] Anbindung von WASimCommander (`L:`-Variablen auflisten, Events)
- [ ] Sprachmodus mit dem Remote-Connector testen
- [ ] Erstes öffentliches Release

## Eigener Verkehr

Eigener KI-Verkehr von Duopilo statt des Verkehrs aus dem Simulator, Schritt für Schritt mit dem Verkehrstest aufgebaut und in EDDL und EDDF getestet ([Recherche](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md), [Verkehr](DE-Verkehr)):

- [x] **Anflug und Landung** (Betas von v0.10) – KI-Verkehrsmodelle fliegen den Endanflug über Wegpunkte und halten auf der Bahn: Fahrwerk bis 2500 ft draußen, Lichter, Freigabeliste der KI-Verkehrsmodelle aus MSFS 2024 (rund 250 Typen) mit Anfluggeschwindigkeit je Typ, Geschwindigkeitsregelung, bis zu 4 gleichzeitig
- [x] **Durchstarten** (beta.15, in der Beta) – über der Schwelle mehr als 150 ft zu hoch oder Vapp + 20 kt zu schnell, oder bis 900 m nicht am Boden: geradeaus Steigflug auf 3000 ft, Fahrwerk rein; die KI nimmt die neuen Wegpunkte im Flug an
- [x] **Start** (beta.16, in der Beta) – Spawn auf der Bahn, Startlauf, Abheben, Steigflug auf 3000 ft, Fahrwerk rein, Lichter; Auswertung von Abhebepunkt und Steigflug
- [x] **Kinematik im Endanflug** (beta.17, in der Beta) – die Wegpunkt-KI fliegt nicht langsamer als rund 169 kt und landet mit festen 133 kt, deshalb bewegt Duopilo das Flugzeug die letzten Meilen selbst: Geschwindigkeit je Typ, 3°-Pfad, Abfangen, weiches Aufsetzen, Bremsen bis zum Halt
- [x] **Überflüge in Reiseflughöhe** (beta.18, in der Beta) – Flugzeuge, die auf ihrer Flugfläche über deine Gegend ziehen, geflogen von der Wegpunkt-KI; abseits der Flughäfen, über einen beliebigen Punkt oder dein Flugzeug, ab beta.36
- [x] **Flughafenregeln** (beta.19, in der Beta) – welches Flugzeug welchen Platz und welche Bahn nutzen darf, für alle Flughäfen: Militärplätze nur Militärverkehr (A400M, Super Hornet), Heliports und geschlossene Plätze keinen, kleine Plätze nur Allgemeine Luftfahrt, Bahnregeln (EDDF 07L/25R nur Landungen ohne B747, A380, MD-11), Verkehrsmix je Flughafen (Finkenwerder überwiegend Beluga)
- [x] **Rotieren beim Start** (beta.21, in der Beta) – Duopilo fliegt den Start selbst (Kinematik wie im Endanflug): Startlauf, rotieren bei Vr mit 3°/s, abheben bei 8°, Steigflug um 15°; getestet mit A400M, MD-11 und A320
- [x] **Rollen zur Bahn** (beta.22 bis beta.28, in der Beta) – Duopilo bewegt das Flugzeug über die Rollwege des Flughafens (Rollweg-Netz aus MSFS): Gate nach Größe und Typ, Pushback entlang der Route bis zur nächsten Kreuzung, Rollen mit 15 kt und in Kurven langsamer, um geparkte Flugzeuge herum (beta.23), Halt an der Haltelinie des Rollwegs, über den es auf die Bahn rollt (beta.28), Halt vor gekreuzten Bahnen, bis sie frei sind (beta.30)
- [x] **Abflug vom Gate** (beta.24, in der Beta) – Pushback, Rollen, Aufreihen auf der Mittellinie und kinematischer Start in einem Lauf; getestet in EDDL 23R vom Gate A15
- [x] **Rollen nach der Landung** (beta.29, in der Beta) – Landung, noch rollend über einen passenden Abrollweg von der Bahn, Rollen zu einem freien Gate; getestet in EDDL 23R bis Gate C2
- [x] **Abflug-Reihenfolge** (beta.31, in der Beta) – mehrere Flugzeuge von verschiedenen Gates zu einer Bahn: Abstand auf den Rollwegen, Aufreihen in der Reihenfolge der Ankunft, immer nur ein Abflug; getestet in EDDL 23R mit drei Abflügen (beta.33)
- [x] **Takt der Landungen und Mischbetrieb** (beta.34, in der Beta) – Landungen nacheinander mit Abstand nach Wirbelschleppen, Lücken für Abflüge, Starts und Landungen auf einer Bahn (wie in Düsseldorf), Durchstarten, wenn die Bahn noch belegt ist
- [ ] **Follow-me** – eigenes Fahrzeugmodell (Bus mit Vollkaro), führt dein Flugzeug vom Abrollweg zum Gate
- [ ] **Flughafenfeuerwehr** – europäische Löschfahrzeuge bei Notfällen aus dem ATC-Modul
- [ ] **Eigene Passive-Flugzeugmodelle**
- [ ] **Helikopter** – kinematisch: Anflug, Schweben, senkrechtes Landen und Abheben auf Heliports und Flughäfen
- [ ] **Fahrplan-Verkehr** – Flüge, über den ICAO-Typ den installierten Modellen zugeordnet
- [x] Auch in Frankfurt testen (drei Parallelbahnen und die Startbahn West)

Ideen und Wünsche: [Issues](https://github.com/duopilo/duopiloapp/issues).
