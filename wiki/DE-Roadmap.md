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
- [ ] ATC: Rollweg aus dem Rollweg-Graphen der Facilities-API berechnen, Gate aus der Parkposition
- [ ] ATC: Squawk 7600/7500 behandeln
- [ ] Anbindung von WASimCommander (`L:`-Variablen auflisten, Events)
- [ ] Sprachmodus mit dem Remote-Connector testen
- [ ] Erstes öffentliches Release

## Eigener Verkehr

Eigener KI-Verkehr von Duopilo statt des Verkehrs aus dem Simulator, Schritt für Schritt mit dem Verkehrstest aufgebaut und in EDDL getestet ([Recherche](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md), [Verkehr](DE-Verkehr)):

- [x] **Anflug und Landung** (Betas von v0.10) – KI-Verkehrsmodelle fliegen den Endanflug über Wegpunkte und halten auf der Bahn: Fahrwerk bis 2500 ft draußen, Lichter, Freigabeliste der KI-Verkehrsmodelle aus MSFS 2024 (rund 250 Typen) mit Anfluggeschwindigkeit je Typ, Geschwindigkeitsregelung, bis zu 4 gleichzeitig
- [ ] **Durchstarten** (beta.15) – über der Schwelle mehr als 150 ft zu hoch oder Vapp + 20 kt zu schnell, oder bis 900 m nicht am Boden: geradeaus Steigflug auf 3000 ft, Fahrwerk rein
- [ ] **Start** (beta.16) – Spawn auf der Bahn, Startlauf, Rotieren, Steigflug auf 3000 ft, Fahrwerk rein, Lichter; Auswertung von Abhebepunkt und Steigflug
- [ ] **Überflüge in Reiseflughöhe** – Flugzeuge, die auf ihrer Flugfläche über deine Gegend ziehen
- [ ] **Rollen** – Pushback und Rollen zwischen Gate und Bahn über die Rollwege des Flughafens
- [ ] **Fahrplan-Verkehr** – Flüge, über den ICAO-Typ den installierten Modellen zugeordnet
- [ ] Auch in Frankfurt testen; weichere Landungen (die KI setzt mit rund 1400 ft/min auf)

Ideen und Wünsche: [Issues](https://github.com/duopilo/duopiloapp/issues).
