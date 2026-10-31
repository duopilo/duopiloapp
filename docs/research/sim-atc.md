# Recherche: Sim-ATC in MSFS 2024 abschalten oder umgehen

Stand: 30.10.2026. Anlass sind die Beobachtungen aus dem Testflug CFG2205 EDDF–KLAX ([Testlog 0.9](../testlog-0.9.md), Abschnitt „Beobachtungen ca. 00:00Z“):

- Sim-ATC spricht Verkehr an, den SimConnect und TCAS nicht kennen (Kulissenverkehr).
- Sim-ATC gibt Live-Traffic Höhenanweisungen, die nie umgesetzt werden. Der Live-Traffic folgt den realen ADS-B-Daten.
- Sim-ATC wechselt mehrfach innerhalb weniger Meilen zwischen Sektoren hin und her.
- Step Climbs gingen nur ohne Freigabe, weil weder Kopilot- noch Sim-ATC Höhenanfragen beantwortet.

## Ergebnis

Das Sim-ATC lässt sich über SimConnect oder das SDK nicht abschalten. Es gibt nur Einstellungen im Menü. BeyondATC und SayIntentions lösen das genauso: Sim-ATC stumm schalten, Sim-Verkehr abschalten und eigenen Verkehr einspeisen.

## SimConnect und SDK

- Es gibt keinen Befehl und keine SimVar, die das ATC deaktiviert. Über Key-Events lässt sich nur das ATC-Menü bedienen.
- Eigene KI-Flugzeuge lassen sich ohne ATC-Kontrolle erzeugen (`SimConnect_AICreateNonATCAircraft_EX1`, für MSFS 2024 empfohlen).
- `SimConnect_AIRemoveObject` wirkt nur auf Objekte, die der Client selbst erzeugt hat. Den Live-Traffic des Sims kann Kopilot also nur lesen, nicht steuern oder entfernen.

## Einstellungen in MSFS 2024

Die Vorgaben von BeyondATC und SayIntentions für Drittanbieter-ATC:

| Einstellung | Pfad | Wert |
|---|---|---|
| AI Radio Communications (ATC) | Assistance > Realism | Off |
| ATC Enforce Flight Plan | Assistance > Realism | Off |
| Character Voices | General > Sound | 0 |
| Subtitles | Accessibility | Off |
| Aircraft Traffic Quantity, Parked Aircraft Quantity | General > Graphics (VR: VR Graphics) | Off |
| Aircraft Traffic Type | General > Online | Off |

Grenzen:

- Damit ist das ATC nur stumm, es läuft weiter.
- *Character Voices 0* schaltet auch die Dialoge im Career-Modus stumm. Im Career-Modus setzen Punkte die Interaktion mit dem Sim-ATC voraus.
- Squawk und Frequenz: Nutzer berichten, dass der Sim beide trotz ausgeschalteter AI-ATC-Optionen selbst ändert. Einen bestätigten Fix gibt es nicht.

## BeyondATC und SayIntentions

**BeyondATC**

- Speist eigenen KI-Verkehr aus einem separaten Prozess ein (Modelle von FSLTL, AIG, FS Traffic oder BravoAirspace).
- Laut Wiki erkennt und steuert BATC nur Verkehr, den es selbst eingespeist hat. MSFS-Live-Traffic wird nicht unterstützt, Sim-Verkehr muss aus.
- Funkhilfen: *Auto Respond* (beantwortet ATC-Anweisungen automatisch) und *Auto Tune Radio* (stellt bei Übergaben die Frequenz nach).

**SayIntentions**

- Eigener KI-Verkehr nach realen Flugplänen (laut Doku noch in Entwicklung, nur IFR-Linienverkehr).
- SkyNet zeigt andere SI-Nutzer im Sim an.
- Der eigene Co-Pilot übernimmt den Funk auf Zuruf („Can you handle the comms please?“).

Zum Transponder steht in beiden Dokus nichts.

## Empfohlene Einstellungen für Kopilot

Nur das ATC stumm schalten, den Verkehr anlassen:

| Einstellung | Pfad | Wert |
|---|---|---|
| AI Radio Communications (ATC) | Assistance > Realism | Off |
| ATC Enforce Flight Plan | Assistance > Realism | Off |
| Character Voices | General > Sound | 0 |
| Subtitles | Accessibility | Off |
| Aircraft Traffic (Graphics und Online) | General > Graphics, General > Online | **an lassen** |

Kopilot liest den Live-Traffic des Sims (`get_traffic`, Monitoring-Regel `TRAFFIC_PROXIMITY`). Die Vorgabe von BeyondATC und SayIntentions, den Sim-Verkehr abzuschalten, gilt für Kopilot deshalb nicht.

## Folgen für Kopilot

- **Kulissenverkehr, Höhenanweisungen an Live-Traffic, Sektor-Hin-und-Her:** verschwinden für den Piloten, sobald das Sim-ATC stumm ist. Damit fehlen aber die Sim-ATC-Referenzpunkte für FIR- und Sektorgrenzen, die im Testlog als Vergleich dienen.
- **Live-Traffic:** Kopilot kann ihn nur lesen. Die inkonsistenten Höhen im Live-Traffic bleiben. Konsistenter Verkehr ginge nur mit eigener Einspeisung wie bei BeyondATC, das wäre ein großer eigener Schritt (siehe [Eigener KI-Verkehr](eigener-verkehr.md)).
- **Squawk und Frequenz:** Kopilot kann nicht verhindern, dass der Sim sie überschreibt. Es kann `TRANSPONDER CODE` und `COM ACTIVE FREQUENCY` aber überwachen und Abweichungen melden oder zurücksetzen (siehe Idee „Squawk-Abgleich“ im Testlog).
- **Step Climbs:** haben mit dem Sim-ATC nichts zu tun. Dafür braucht Kopilot ein eigenes Template für Höhenanfragen (siehe „Bug: Steigflug-Anfrage im Reiseflug“ im Testlog).

## Quellen

- [SayIntentions – MSFS 2020/2024 Recommended Settings](https://sayintentionsai.freshdesk.com/support/solutions/articles/154000187068)
- [SayIntentions – How do I disable the MSFS 2024 built-in ATC?](https://sayintentionsai.freshdesk.com/support/solutions/articles/154000217112-how-do-i-disable-the-msfs-2024-built-in-atc-)
- [SayIntentions – Features Overview](https://sayintentionsai.freshdesk.com/support/solutions/articles/154000219433)
- [BeyondATC Wiki – Getting Started](https://wiki.beyondatc.net/onboarding/getting-started/)
- [BeyondATC Wiki – MSFS 2024](https://wiki.beyondatc.net/announcements/msfs2024/)
- [MSFS 2024 SDK – AI Objects](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simconnect/api-reference/ai-object/ai-objects/index.md)
- [MSFS 2024 SDK – SimConnect_AICreateNonATCAircraft](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/ai-object/simconnect_aicreatenonatcaircraft/index.md)
- [MSFS-Forum – ATC automatically changes frequency and squawk code](https://forums.flightsimulator.com/t/atc-automatically-changes-frequency-and-squawk-code/679090)
- [MSFS-Forum – Ability to fully disable ATC in Free Flight](https://forums.flightsimulator.com/t/ability-to-fully-disable-atc-in-free-flight/696477)
- [AVSIM – MSFS 2024: how to disable ATC](https://www.avsim.com/forums/topic/660461-msfs2024-how-to-disable-atc/)
