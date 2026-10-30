# Testplan Sicherheitssperren (Eingabe-Modul)

Kurzer Testflug für die Sperren, Rückfragen und Callouts aus `feature/action-interlocks`.
Flugzeug **FlyByWire A32NX**, Strecke **EDDF–EDDS**, SimBrief-OFP mit Reiseflughöhe **FL260 oder höher** (für den 25.000-ft-Test).

Ergebnis je Punkt im Testlog notieren: ✓ wie erwartet / ✗ mit Uhrzeit (Z), Antwort von Kopilot und Eintrag im Dashboard-Protokoll.

Legende: **erlaubt** = normaler Readback, ein „confirm“ führt aus · **gesperrt** = `refused` mit Grund · **Rückfrage** = nach dem ersten „confirm“ kommt „Caution, … Confirm …?“, erst das zweite „confirm“ führt aus.

## Vorbereitung

- [ ] Branch bauen, Kopilot starten, Eingabe im Dashboard freigeben.
- [ ] `kopilot_status`: `profileErrors` und `profileWarnings` leer.
- [ ] `list_actions`: Feld `safety` bei gear.down, flaps, speedbrake, air.pack1/2, apu.master, xpdr.*, tcas.mode, fcu.ap1/ap2/athr, fcu.altitude.
- [ ] SimBrief-OFP geladen (`get_flight_plan`).

## 1. Am Boden (Gate EDDF)

| # | Aktion | Erwartet |
|---|---|---|
| 1.1 | APU an, `apu.bleed on`, dann `apu.master off` | gesperrt („APU Bleed ist an“) |
| 1.2 | `apu.bleed off`, dann `apu.master off` (danach wieder an) | erlaubt |
| 1.3 | `air.pack1 off`, dann `air.pack2 off` (danach beide wieder an) | beide erlaubt (am Boden) |
| 1.4 | `xpdr.mode stby`, `tcas.mode stby`, `xpdr.altReporting off` | erlaubt, keine Rückfrage |
| 1.5 | `xpdr.code 7700` | Rückfrage „Caution, squawk seven seven zero zero, emergency code …“ → **„negative“** (nicht ausführen) |
| 1.6 | `fcu.altitude 5000` ohne ATC-Sitzung | je nach MORA der ersten Strecke: erlaubt oder Rückfrage „below minimum safe altitude …“. Kommt „minimum safe altitude unknown“ mit „keine MORA im SimBrief-Navlog“, liefert SimBrief das Feld `mora` nicht → im Testlog vermerken |
| 1.7 | ATC-Sitzung starten, Freigabe holen, `fcu.altitude` = Initial Climb | erlaubt (Freigabe gilt) |

## 2. Start und Steigflug

| # | Aktion | Erwartet |
|---|---|---|
| 2.1 | Nach „positive climb“: `gear.up` | Callout **„Gear up“** erst, wenn alle drei Beine oben sind; kein zusätzliches „Gear up“ vom Monitoring beim Hebel |
| 2.2 | `flaps 1`, später `flaps 0` | Callout **„Flaps one“** bzw. **„Flaps zero“** erst bei erreichter Stellung (nicht schon beim Hebel) |
| 2.3 | Über 250 kt: `gear.down` | gesperrt („… über VLO 250 kt“) |
| 2.4 | `air.pack1 off` (Pack 2 an) | erlaubt; danach `air.pack2 off` → gesperrt („Pack 1 ist schon aus …“); dann `air.pack1 on` |
| 2.5 | `xpdr.mode stby` | Rückfrage „transponder standby in flight“ → „negative“ |
| 2.6 | `xpdr.altReporting off` | Rückfrage → „negative“ |
| 2.7 | `tcas.mode stby` | Rückfrage → „negative“ |
| 2.8 | `fcu.athr off` | Rückfrage → zweimal „confirm“ → ausgeführt; danach `fcu.athr on` |
| 2.9 | `fcu.ap1 off` (nur wenn AP 1 an) | Rückfrage → „negative“ |
| 2.10 | Freigabe FL250, `fcu.altitude FL270` | Rückfrage „altitude not cleared, cleared flight level 250“ → „negative“ |

## 3. Reiseflug (über FL250)

| # | Aktion | Erwartet |
|---|---|---|
| 3.1 | Speed unter 250 kt, `gear.down` | gesperrt („… ausfahren nur unter 25000 ft“) – **nicht** bestätigen, falls doch ein Readback kommt |
| 3.2 | `fcu.altitude 2000` | Rückfrage „below minimum safe altitude …“ (MORA-Wert plausibel?) → „negative“ |
| 3.3 | `speedbrake half`, danach `speedbrake retract` | erlaubt (Klappen 0) |
| 3.4 | Sim pausieren, `kopilot_status`, weiter | keine Ansagen während der Pause |

## 4. Sinkflug und Anflug EDDS

| # | Aktion | Erwartet |
|---|---|---|
| 4.1 | `fcu.altitude` = freigegebene Höhe (auch unter MORA, z. B. 5000 ft) | erlaubt (Freigabe gilt) |
| 4.2 | `flaps 1`, `flaps 2` | Callouts „Flaps one“ / „Flaps two“ jeweils nach Stillstand der Klappen; kein doppeltes „Speed checked, flaps …“ |
| 4.3 | Mit Klappen 2: `speedbrake half` | erlaubt; danach retract |
| 4.4 | Unter FL250 und VLO: `gear.down` | Callout **„Gear down, three green“**, sobald ECAM drei grüne Dreiecke zeigt (ca. 10 s) |
| 4.5 | Während 4.4 kurz pausieren | kein „Gear disagree“ wegen der Pause |
| 4.6 | `flaps 3`, `flaps FULL` | Callouts „Flaps three“ / „Flaps full“ |
| 4.7 | Mit Klappen FULL: `speedbrake half` | gesperrt („Klappen FULL“) |

## 5. Nach der Landung

| # | Aktion | Erwartet |
|---|---|---|
| 5.1 | `xpdr.mode stby` am Boden | erlaubt, keine Rückfrage |
| 5.2 | Dashboard-Protokoll | Einträge „Rückfrage“, „nachgelesen“; kein „Grenzwert fehlt“ |
| 5.3 | `get_alerts` | PM_GEAR/PM_FLAPS-Callouts aus 2.1, 2.2, 4.2, 4.4, 4.6 mit Meldung (z. B. „Fahrwerk unten und verriegelt (3/3)“) |

## Nur per Unit-Test (nicht im Flug)

- „Gear disagree“ / „Flaps disagree“ (Bein bleibt hängen, Klappen bewegen sich nicht) – optional über die Failures im FBW-EFB nachstellen.
- Batterien aus im Flug (nur A339-Profil hat `elec.battery`), ADIRS aus (im Flug gesperrt, am Boden Rückfrage – kein Profil hat ADIRS-Aktionen).
- Fehlende Grenzwerte im Profil (Rückfrage + „Grenzwert fehlt“ im Protokoll).

## Offene Punkte, auf die der Flug antworten soll

- Liefert SimBrief `mora` je Navlog-Fix (1.6, 3.2)? Wenn nicht: andere MSA-Quelle wählen.
- Lesen `GEAR CENTER/LEFT/RIGHT POSITION` und `TRAILING/LEADING EDGE FLAPS LEFT PERCENT` beim A32NX die echten Stellungen (2.1, 2.2, 4.4)? Sonst die FBW-L:-Variablen unter `verification` im Profil eintragen.
- Passt die Klappen-Zeitgrenze (40 s) zu CONF 0 → 1 mit Vorflügeln?
