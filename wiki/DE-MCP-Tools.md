[English](MCP-Tools) | **Deutsch**

# MCP-Tools

Die Tools, die Claude (oder jeder andere MCP-Client) sieht. Jede Antwort enthält zusätzlich **`activeAlerts`** – die gerade aktiven Monitoring-Meldungen. So bekommt Claude Warnungen auch mit, wenn es eigentlich etwas anderes abfragt.

Tool-Meldungen (Felder `message`/`note`) folgen der [Spracheinstellung](DE-Dashboard-und-Einstellungen#sprache); Werte sind sprachneutral, Geschwindigkeiten immer in Knoten.

## Status und Flugdaten

| Tool | Inhalt |
|---|---|
| `duopilo_status` | Verbindung, Pause, geladenes Flugzeug (Titel + Pfad), aktives Profil, Profilwarnungen, Status von Monitoring, SimBrief, ECAM und Sprachschleife |
| `get_flight_state` | Höhe (IND, FL, AGL, Radio), IAS/TAS/GS/VS in kt, Lage, Kurs, Position, Klappen, Fahrwerk, Spoiler, Parkbremse, Autobrake, AP/A-THR mit FMA, Speed- und Höhenziel, Warnungen |
| `get_systems` | Triebwerke (N1, N2, EGT, Fuel Flow in kg/h), APU, Sprit, Gewicht, Lichter, Schilder, Transponder, COM, Plausibilitätshinweise |
| `get_warnings` | Master Warning/Caution, ECAM-Meldungen im Klartext (FBW A32NX, Headwind A330-900neo), Stall/Overspeed |
| `get_environment` | Wind, OAT, QNH, eingestellter Höhenmesser, Sicht, UTC-/Ortszeit |
| `get_navigation` | GPS-Flugplanvariablen des Sims (bei Airliner-FMS meist leer – dann `get_progress` nehmen) |
| `read_simvar` | liest eine beliebige SimVar oder `L:`-Variable einmalig (Text-Variablen mit `unit: "string"`) |

## Monitoring

| Tool | Inhalt |
|---|---|
| `get_alerts` | Monitoring-Meldungen seit einer ID, jeweils mit Callout zum Vorlesen |
| `wait_for_alerts` | wartet bis zu 25 s auf die nächste Meldung – für die Dauerüberwachung in einer Schleife |
| `get_monitoring_status` | Flugphase, aktive/abgeschaltete Regeln, Ruhezeit |
| `get_fma_log` | Verlauf der FMA-Wechsel mit Uhrzeit, IAS und Radiohöhe (z. B.: War nach dem Autoland ROLL OUT aktiv?) |

Siehe [Pilot Monitoring](DE-Pilot-Monitoring).

## Flugplan

| Tool | Inhalt |
|---|---|
| `get_flight_plan` | SimBrief-OFP: Route, Bahnen, Reiseflughöhe, Cost Index, Sprit, Gewichte, Zeiten, Navlog |
| `get_progress` | nächster Fix (A32NX: der aktive FMS-Wegpunkt), Distanz/ETA, TOD, Querablage, Sprit gegen Plan, Verlauf der Fuel Checks (`fuelCheck`), `etaSource` |

Siehe [SimBrief und Flugplan](DE-SimBrief-und-Flugplan).

## Verkehr und Ingame-ATC

| Tool | Inhalt |
|---|---|
| `get_traffic` | Verkehr im Umkreis (KI, Live, Multiplayer): Callsign, Typ, Abstand, Uhrzeit-Peilung, Höhendifferenz, Trend, GS, Callout fürs nächste Ziel |
| `get_atc` | Ingame-ATC von MSFS: Callsign, Freigaben, zugewiesene Bahn (inkl. L/C/R aus SimBrief) |

Siehe [Verkehr](DE-Verkehr).

## Flugzeugprofil

| Tool | Inhalt |
|---|---|
| `get_aircraft_profile` | Bindings des Profils mit Rohwert, Klartext und „geprüft“-Status, Zusatzvariablen, Grenzwerte, Status je Callout (`verified`/`unverified`/`missing`) |
| `reload_profiles` | lädt die Profildateien neu |

Siehe [Flugzeugprofile](DE-Flugzeugprofile).

## Eingabe-Modul

| Tool | Inhalt |
|---|---|
| `list_actions` | welche Schalter Duopilo in diesem Flugzeug bedienen darf, ob Eingaben freigegeben sind, Prüfstatus je Aktion |
| `prepare_action` | prüft Wert und Sperren, liefert den Readback – noch ändert sich nichts |
| `confirm_action` | führt aus, nachdem der Pilot den Readback bestätigt hat, und prüft danach den Zustand im Flugzeug |
| `cancel_action` | verwirft die vorbereitete Eingabe |
| `mark_action_verified` | merkt sich eine Aktion als geprüft, nachdem der Pilot im Cockpit gesehen hat, dass sie funktioniert |

Siehe [Eingabe-Modul](DE-Eingabe-Modul).

## Duopilo-ATC

| Tool | Inhalt |
|---|---|
| `atc_transmit` | gibt den Funkspruch des Piloten weiter; die Antwort der Station kommt zurück (und wird gesprochen) |
| `get_atc_controller` | aktuelle Station mit Frequenz und Stimme, erwarteter nächster Pilotenspruch, offenes Readback, Squawk, Abweichungen, Verlauf, Vorlagenfehler |
| `atc_control` | Sitzung starten / beenden / neu starten, Vorlagen und Daten neu laden (`reload`), letzten Funkspruch wiederholen |
| `atc_set_value` | Gate, Rollhalt, Rollroute, Pushback-Richtung, Anflugart oder Bahn von Hand setzen |

Siehe [ATC](DE-ATC).

## Hinweise zum Schreiben von Prompts

- Es gibt **kein Tool, das beliebige Events oder Variablen schreibt** – nur die vordefinierten Aktionen des Profils, über `prepare_action`/`confirm_action`.
- `confirm_action` kann `status: "awaiting_caution_confirmation"` mit einem `caution`-Text liefern: Lies ihn dem Piloten vor und bestätige erst nach einem zweiten „confirm“ erneut mit demselben Token.
- Fehler kommen immer als `status: "error"` mit Grund zurück.
