[English](SimBrief-and-Flight-Plan) | **Deutsch**

# SimBrief & Flugplan

Die Airliner-FMS (FlyByWire, iniBuilds) geben ihre Route nicht über SimConnect heraus. Duopilo holt deshalb dein **letztes SimBrief-OFP** (öffentliche API, kein Login).

## Einrichten

Dashboard → **Einstellungen → SimBrief** – gilt sofort. Oder in `appsettings.Local.json`:

```jsonc
"Duopilo": { "SimBrief": { "Username": "your-simbrief-name" } }   // oder "PilotId": "123456"
```

Der Plan wird 10 Minuten zwischengespeichert (`SimBrief:CacheMinutes`). Gewichte und Sprit rechnet Duopilo immer in kg um.

## Was den Plan nutzt

| Funktion | Nutzt |
|---|---|
| `get_flight_plan` | Route, Bahnen, Reiseflughöhe, Cost Index, Sprit, Gewichte, Zeiten, Navlog |
| `get_progress`, Sprachabfragen | nächster Fix, Distanz/ETA, TOD, Querablage, Sprit gegen Plan |
| [Fuel Check](DE-Pilot-Monitoring#fuel-check) | geplanter Sprit je Wegpunkt, Mindest-Landesprit |
| `TOP_OF_DESCENT` | TOD aus dem Plan (A32NX: die FMS-Meldung T/D REACHED) |
| `PM_TRANSITION` | TA/TL, wenn das FMS sie nicht liefert |
| [ATC](DE-ATC) | Rufzeichen, SID/STAR, Bahnen, FIRs, Oceanic-Teil, Stufensteigflüge, QNH aus dem METAR |
| Aussprache | Funkfeuer-Namen aus dem Navlog („Wabush“ statt YWK) |
| Bahn vom Ingame-ATC | ergänzt L/C/R bei der Bahn, die MSFS zuweist |
| First Officer | neuer Plan = neuer Flug = neuer First Officer |

## Fortschritt

- **Nächster Fix:** beim A32NX der aktive FMS-Wegpunkt (z. B. nach DIR TO), abgeglichen mit dem Plan; sonst aus der Position entlang der Route.
- **ETA:** aus den Navlog-Zeiten (geplante Winde), korrigiert um die Abweichung am letzten überflogenen Fix. Ohne Navlog-Zeiten: aus der Groundspeed. `etaSource` sagt, woher.
- **TOD:** Distanz und ETA aus dem Plan.
- **Sprit:** an Bord gegen Plan, Verlauf der automatischen Checks (`fuelCheck`).

Das Dashboard zeigt die Route mit Fortschrittsbalken und darunter den Trend der Sprit-Abweichung.

## Ohne Plan

Monitoring, PM-Callouts, Verkehr, Eingabe-Modul und Sprachschleife funktionieren ohne Plan. Planbasierte Sprachabfragen antworten „Unable“, und ATC sagt „ATC unavailable“.
