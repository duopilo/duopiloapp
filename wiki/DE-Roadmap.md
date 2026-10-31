[English](Roadmap) | **Deutsch**

# Roadmap

## Erledigt

- **v0.1** – MCP-Server mit Flugzustand, Systemen, Navigation, Wetter, Profilen mit `L:`-Variablen
- **v0.3** – Pilot Monitoring (Regeln, Callouts, Alerts), FBW-Profile, ECAM-Klartext, SimBrief, Bahn vom Ingame-ATC
- **v0.4** – Tray-Symbol und Dashboard im Browser, Callouts im Browser vorgelesen
- **v0.5** – Korrekturen nach dem Testflug EDDL–EDDM, TOD, Landecheck, FMA-Log, FMS-Wegpunkt
- **v0.6** – lokale Sprachausgabe (Windows-Stimme, Ausgabegerät) und PM-Callouts
- **v0.7** – Eingabe-Modul mit Readback und Bestätigung (zuerst A32NX)
- **v0.8** – neuronale Stimme Kokoro, ECAM-Titel ausgeschrieben, Tray-Schalter
- **Verkehr** – `get_traffic` und die Verkehrswarnung
- **ATC-Modul** – Zustandsmaschine mit Vorlagen von Delivery bis Ground inkl. Oceanic, Readback-Prüfung, Stimme je Station; FIR-Grenzen (VAT-Spy), VATSIM-Sektoren, Rufzeichen aus OpenFlights
- **Remote-Betrieb** – Streamable HTTP mit Bearer-Token und OAuth, Cloudflare Tunnel
- **v0.9** – lokale Sprachschleife (Silero VAD, Whisper, feste Befehle, Readback per Sprache), Prioritäts-Warteschlange, Taxi-Speed-Warnung, Funk an das ATC-Modul
- **v0.9.1** – Sprachschleife ein/aus und Mikrofonauswahl im Dashboard, ATC-Sitzung übersteht Neustarts, Absturz-Journal und Neustart nach einem Absturz
- **Seitdem** – Umbenennung in Duopilo, Englisch als Standardsprache mit deutscher Übersetzung, Checklisten, Quittungen, Notfälle im ATC, First Officer mit Smalltalk (Claude, OpenAI-kompatibel, Ollama), Supertonic-Stimme, Installer und Release-Workflow

## Als Nächstes

- [ ] Testflug in VR mit der Sprachschleife
- [ ] `L:`-Variablen und Aktionen für den iniBuilds A321LR (FMA, A/THR, Master Warning, ECAM)
- [ ] Eingabe-Modul: im Sim prüfen, NAV & LOGO über Input Events, weitere Flugzeuge
- [ ] ATC: Rollroute aus dem Taxiway-Graphen der Facilities-API berechnen, Gate aus der Parkposition
- [ ] ATC: Squawk 7600/7500 behandeln
- [ ] WASimCommander-Anbindung (`L:`-Variablen auflisten, Events)
- [ ] Sprachmodus mit dem Remote-Connector testen
- [ ] Erstes öffentliches Release

Ideen und Wünsche: [Issues](https://github.com/duopilo/duopiloapp/issues).
