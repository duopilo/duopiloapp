[English](Traffic) | **Deutsch**

# Verkehr

`get_traffic` listet Flugzeuge und Hubschrauber in der Nähe – KI-Verkehr, Live-Traffic und Multiplayer, soweit im Sim eingeschaltet. Das eigene Flugzeug ist ausgeschlossen. Die Sprachschleife beantwortet „Traffic check“ genauso.

## Parameter

| Parameter | Standard | Bedeutung |
|---|---|---|
| `radiusNm` | 40 | Suchradius, höchstens 108 NM (Grenze von SimConnect) |
| `limit` | 10 | höchstens so viele Ziele, nächstes zuerst (1–50) |
| `airborneOnly` | false | Flugzeuge am Boden weglassen |

## Pro Ziel

`ident`/`callsign`, `atcId`, `type`, `distanceNm`, `clock`, `relativeBearingDeg`, `bearingTrueDeg`, `altitudeFt`, `altitudeDiffFt` (+ = darüber), `verticalTrend`, `groundSpeedKt` mit `groundSpeedSource`, `headingTrueDeg`/`headingMagDeg`, `onGround`. Das nächste Ziel hat `closest: true` und einen `callout`:

```
Traffic, 2 o'clock, 5 miles, 1000 feet above, descending
```

## Wie die Werte entstehen

| Wert | Hinweise |
|---|---|
| **Rufzeichen** | Airline + Flugnummer, wenn beide gesetzt sind (`Lufthansa 123`), sonst die Registrierung. Interne Live-Traffic-Nummern wie `1432126` gelten nicht als Rufzeichen |
| **Typ** | Lokalisierungsschlüssel (`ATCCOM.AC MODEL C441.0.tts`) werden zu `C441` bereinigt |
| **Uhrzeit** | relativ zu deinem Steuerkurs, je 30° |
| **Abstand** | horizontal (Großkreis) |
| **Höhendifferenz** | wie TCAS: gegen deine **Druckhöhe** (1013,25 hPa), weil Live-Traffic Flugflächen meldet. `"AltitudeReference": "True"` schaltet auf die wahre Höhe um |
| **Groundspeed** | aus der Positionsänderung berechnet (5–120 s auseinander), weil die SimVar beim Live-Traffic unbrauchbar ist; bis dahin die SimVar mit `groundSpeedSource: "simvar"` |
| **Trend** | aus der Vertikalgeschwindigkeit (±500 ft/min); meldet ein Ziel 0, aus der Höhenänderung abgeleitet |

Kein anderes Flugzeug → `status: "no_traffic"` (KI-Verkehr oder Multiplayer ist im Sim meist aus). MSFS antwortet nicht → `no_response`.

## Verkehrswarnung `TRAFFIC_PROXIMITY`

Solange das Flugzeug fliegt, sucht Duopilo alle 3 s im Umkreis von 6 NM. Ein Ziel in der Luft, das näher als 3 NM und innerhalb ±1000 ft ist, löst eine Caution mit Callout aus; danach 60 s Ruhe. Unter 500 ft AGL, am Boden und in der Pause ruht die Warnung.

```jsonc
"Duopilo": { "Monitoring": { "Traffic": {
  "ProximityRangeNm": 3, "ProximityAltitudeFt": 1000, "ProximityCooldownSec": 60, "ScanIntervalSec": 3,
  "AltitudeReference": "Pressure", "MessageLog": true
} } }
```

Abschalten: `"DisabledRules": [ "TRAFFIC_PROXIMITY" ]`.

> Das ist **kein TCAS**: keine Ausweichempfehlung, keine Vorhersage des Annäherungspunkts – nur Abstand und Höhe im Moment. Echte TCAS RAs aus der FMA des Flugzeugs werden getrennt angesagt (`TCAS_RA`).

## Eigene SimConnect-Verbindung

Der Verkehr nutzt eine eigene SimConnect-Verbindung direkt über die native `SimConnect.dll` und liest nur, was jede Nachricht tatsächlich enthält (`dwSize`). Nach Abstürzen in Testflügen hat das für den Verkehr die verwaltete Bibliothek abgelöst. Der Hintergrundscan fragt nur Zahlen ab; Rufzeichen und Typ sind bekannt, wenn `get_traffic` das Ziel in den letzten 15 Minuten gesehen hat.

Jede Antwort wird in `%LOCALAPPDATA%\Duopilo\logs\traffic-messages.csv` protokolliert (~250 KB pro Flugstunde, bei 10 MB rotiert); `get_traffic` → `messageCheck` fasst Auffälligkeiten zusammen. Abschalten: `"MessageLog": false`.

## Verkehrstest (Entwicklung)

Erster Schritt zu eigenem Verkehr in Duopilo: `traffic_test_start` erzeugt ein KI-Flugzeug (`DUO01`–`DUO04`), das über Wegpunkte einen Endanflug fliegt, landet und auf der Bahn hält – bis zu 4 gleichzeitig, je Bahn eins (z. B. Parallelanflug auf 05L und 05R). Standardmäßig aus – einschalten mit `"TrafficTest": { "Enabled": true }` in `appsettings.Local.json`.

- **A1:** Spawn 5 NM vor der Bahn mit Fahrwerk unten. **A2:** Spawn 15 NM vor der Bahn, Fahrwerk bei 3000 ft über der Bahn (bei 2500 ft draußen). **B1:** Start – Spawn auf der Bahn, Startlauf, Rotieren und Steigflug geradeaus auf 3000 ft fliegt Duopilo selbst (Kinematik, ab beta.21), Fahrwerk rein. **C1:** Überflug in Reiseflughöhe (FL350, Allgemeine Luftfahrt FL85) geradeaus über die Bahnmitte. **C2:** dasselbe abseits der Flughäfen (ab beta.36) – über genannte Koordinaten oder genau über dein Flugzeug, quer zu deinem Steuerkurs oder auf `trackDeg`, mit `offsetNm` daneben, ohne Flugfläche mindestens 2000 ft über dir. **D1:** Rollen – Spawn an einer Parkposition, bei Bedarf Pushback, über die Rollwege zum Haltepunkt der Bahn (Duopilo bewegt das Flugzeug selbst, ab beta.22); `traffic_taxi_info` zeigt Netz und Route. **B2:** Abflug vom Gate – D1, dann aufreihen und mit der Start-Kinematik starten (ab beta.24). **A3:** Landung mit Rollen – wie A1, ab dem Spawn von Duopilo geflogen, auf Rollgeschwindigkeit bremsen, noch rollend am ersten passenden Abrollweg von der Bahn und weiter zu einer freien Parkposition (ab beta.29). Ab beta.30 halten die Rollvarianten vor jeder Bahn, die sie kreuzen, und kreuzen erst, wenn sie frei ist (nichts auf ihr und nichts im Endanflug bis 4 NM); B2 reiht sich erst auf, wenn die eigene Bahn frei ist. Ab beta.31 stellen sich Rolltests auf einer Bahn an, statt einander zu ersetzen: Abstand zum Vordermann, Aufreihen der Reihe nach, immer nur ein Abflug. Ab beta.34 stellen sich auch Landungen (A3) auf einer Bahn an – Abstand nach Wirbelschleppen hinter dem Anflug davor (3 NM, hinter Heavies mehr), eine Lücke, die zu einem wartenden Abflug passt – und Starts und Landungen teilen sich eine Bahn (Mischbetrieb): B2 reiht sich erst auf, wenn die nächste Landung weit genug weg ist (etwa 5–6 NM), eine Landung startet bei 0,4 NM durch, wenn die Bahn noch belegt ist. Ab beta.35 lässt Gegenverkehr beim Rollen Vorfahrt: Wer später an ein gemeinsames Stück Rollweg kommt, wartet davor. Bahnregeln (`data\traffic\airport-rules.json`, dazu Länge und Breite) lehnen Flugzeuge ab, die nicht zur Bahn passen, z. B. eine MD-11 auf EDDF 07L.
- Die Wegpunkte der letzten Meile liegen bis zu 100 ft unter dem Gleitpfad (`finalLowFt`), weil die KI im kurzen Endanflug abfängt.
- Zu hoch (mehr als 150 ft) oder zu schnell (mehr als Vapp + 20 kt) an der Schwelle oder bis 900 m nicht am Boden: Das Flugzeug startet durch – geradeaus, Steigflug auf 3000 ft, Fahrwerk rein (`goAround: false` landet immer).
- `traffic_test_status` zeigt die Auswertung (Aufsetzen, Abfangen, Schweben, Speed, Haltepunkt, Durchstarten), die Messpunkte liegen unter `%LOCALAPPDATA%\Duopilo\traffic-tests\`.
- `traffic_test_stop` entfernt die Flugzeuge, mit `runway` nur das eine (auch nach 15 Minuten und beim Beenden von Duopilo).

Hintergrund und Plan: [Recherche zu eigenem Verkehr](https://github.com/duopilo/duopiloapp/blob/main/docs/research/eigener-verkehr.md).
