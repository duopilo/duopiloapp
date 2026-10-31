[English](Voice-Loop) | **Deutsch**

# Sprachschleife

Sprich direkt mit deinem Kopiloten – **ohne Claude, ohne Push-to-Talk** (VR-tauglich), komplett lokal und **ohne Sprachmodell**. Nur der optionale [Smalltalk](DE-First-Officer-und-Smalltalk) kann eins nutzen.

```
Microphone → Silero VAD (end of utterance) → Whisper (transcript) → Router ─┬→ copilot commands → Input module
                                                                            ├→ queries, checklists
                                                                            ├→ ATC module
                                                                            └→ small talk (optional, read-only)
                      Speech output (Kokoro / Supertonic) ←─────────────────┘
```

## Einschalten

1. Dashboard → **Ansagen → Stimme → Sprachschleife**: Haken setzen und Mikrofon wählen. Gilt sofort.
2. Beim ersten Start lädt Duopilo Whisper (`base-q5_1`, ~60 MB) und Silero VAD (~2 MB) nach `%LOCALAPPDATA%\Duopilo\models`.
3. Für Befehle, die das Flugzeug bedienen, muss außerdem [die Eingabe freigegeben sein](DE-Eingabe-Modul#standardmäßig-gesperrt).

Die Einstellungen liegen in `%LOCALAPPDATA%\Duopilo\voice-settings.json`. Bearbeite nicht die `voice-settings.json` neben der `Duopilo.exe` – das ist nur die Vorlage, Updates überschreiben sie.

Die Sprachschleife läuft nur in der Tray-Instanz.

## So funktioniert es

- **Offenes Mikrofon.** Silero VAD erkennt Sprache; nach 0,7 s Stille geht der Satz an Whisper.
- **Halbduplex.** Solange der Kopilot oder ATC spricht (und 350 ms danach), ist das Mikrofon stumm – Duopilo hört sich nicht selbst. Während *du* sprichst, warten Callouts kurz; nur Warnungen und zeitkritische PM-Callouts (100 knots, V1, Rotate, Positive climb, Spoilers, Reverse, Decel, 70 knots, Radiohöhen, Minimum, Retard) fallen dir ins Wort.
- **Deutsch oder Englisch** je Satz. Zahlen werden vereinheitlicht: „Flaps one“, „Flaps 1“, „Flaps won“, „Klappen eins“ ergeben dasselbe.
- **Router:** Ein Stationsname („Frankfurt Delivery“, „Langen Radar“, „Shanwick“) oder dein Rufzeichen am Anfang eines Anrufs oder am Ende eines Readbacks → ATC. Alles andere → Kopilot. „Duopilo, …“ geht immer an den Kopiloten.
- **Nur, wenn der Sim läuft.** Nicht verbunden, kein Flug geladen oder pausiert → das Mikrofon wird nicht ausgewertet (spart CPU), kein ATC, keine Callouts; offene Readbacks und Checklisten frieren ein, statt zu verfallen. Das Dashboard zeigt den Grund („inaktiv – Pausiert“).

## Weckwort und Anrede

Befehle funktionieren ohne Weckwort. Willst du den Kopiloten ausdrücklich ansprechen – z. B. während einer Checkliste –, nimm **„Kopilot, …“** (fest), den **Namen des First Officers** („Sam, gear up“) oder „Duopilo, …“, wenn Whisper es eindeutig so abschreibt. Mit `commands.requireWakeWord` wird das Weckwort Pflicht.

## Befehle

Feste Muster auf Englisch und Deutsch. **Jede Aktion jedes Flugzeugprofils hat einen Sprachbefehl** (ein Test stellt das sicher; die vollständige Liste mit Beispielen steht in [`VoiceCommandCatalog.cs`](https://github.com/duopilo/duopiloapp/blob/main/src/Duopilo/Voice/VoiceCommandCatalog.cs)).

| Bereich | Beispiele |
|---|---|
| Konfiguration | Gear up/down, Fahrwerk ein/aus · Flaps 0/1/2/3/Full, Klappen null/eins/voll · Speedbrake retract/half/full · Arm/Disarm spoilers |
| Bremsen, Licht, Schilder | Autobrake low/medium/max/off · Landing lights on/off · Strobe on/auto/off · Beacon on · Taxi light on · Nav lights on · Seat belts on/off · No smoking on/auto |
| Elektrik, APU | Batteries on/off · External power on/off · APU start · APU master on/off · APU bleed on/off |
| Enteisung | Engine anti ice on, Engine one anti ice on · Wing anti ice on · Probe heat on/auto |
| EFIS, FCU | QNH one zero one three · Baro standard · Altitude five thousand · Flight level one two zero · Speed two five zero · Heading zero niner zero · Managed speed · Vertical speed minus one thousand · Autopilot one on · Approach on · ND mode ARC/ROSE NAV/PLAN · ND range one hundred sixty · Expedite climb |
| Transponder, TCAS, Funk | Squawk seven thousand · Transponder standby/auto · Altitude reporting on/off · TCAS TA/RA · TCAS traffic all/above/below/threat · COM one standby one two one decimal five · Swap COM one |

Hinweis: „Klappen ein“ wird nicht angenommen (zu nah an „eins“).

### Ausführen + Callout (Standard)

Wie in einer echten Crew ist dein Befehl die Anweisung: Der Kopilot führt sofort aus und sagt es an, sobald das Flugzeug den Wert übernommen hat – „Speed two five zero set“, „Altitude five thousand set“. Bei Fahrwerk und Klappen kommt der Callout, sobald die Stellung erreicht ist („Gear down, three green“, „Flaps two“).

| Antwort | Bedeutung |
|---|---|
| „Not confirmed“ | das Flugzeug hat den Wert nicht übernommen |
| „Unable“ | eine Sperre hat es verhindert (z. B. Batterien aus im Flug) |
| „Input disabled“ | die Eingabe ist gesperrt |
| „Say again“ | nicht verstanden, oder zwei Befehle in einem Satz |

### Readback mit „Confirm“

Einen Readback gibt es nur, wenn die Erkennung unsicher war (Whisper-Konfidenz unter `commands.minConfidence`, Standard 0,6) oder die Aktion eine Rückfrage braucht (siehe [Eingabe-Modul](DE-Eingabe-Modul#rückfrage-mit-warnhinweis)):

1. Duopilo liest zurück: „Flaps one“.
2. **„Confirm“**, **„Correct“**, **„Affirm“** (auch „Korrekt“) → wird ausgeführt. **„Negative“** / **„Cancel“** → „Cancelled“.
3. „Yes“, „OK“, „Roger“ allein bestätigen nichts → „Say again“.

„Confirm“ zählt erst, wenn der Readback ganz gesprochen ist, und nur in einem Satz, den du danach begonnen hast. Ohne Antwort verfällt die Aktion nach 60 s. Kritische Aktionen fragen mit Warnhinweis ein zweites Mal nach („Caution, autothrust off. Confirm autothrust off?“).

Mit `"executeWithCallout": false` geht jeder Befehl über den Readback.

## Abfragen

Die Antwort kommt nur als reiner Callout:

| Frage | Antwort (Beispiel) |
|---|---|
| Speed check | Speed two five zero (am Boden: Ground speed one two) |
| Altitude check | Altitude five thousand / Flight level three five zero |
| Fuel check | Fuel twelve point four tons |
| Fuel versus plan, Landing fuel | Fuel thirty four point one, two point two below plan, landing fuel six point eight |
| Next waypoint | Hendy, one hundred twenty miles, E T A zero three four two |
| ETA | E T A zero four one eight |
| Top of descent | Top of descent eighty five miles, E T A zero three one two |
| Traffic check | Traffic, 2 o'clock, 5 miles, 1000 feet above, descending / No traffic |
| Wind check, Temperature | Wind two seven one at forty five · Temperature minus forty seven |
| Squawk check | Squawk two three four zero |
| Station check, Which frequency | Montreal Center, one three two decimal two five |
| Engine check | Engines normal / Engine two not running / N1 split |
| Gear, Flaps, Speedbrake, Spoilers, Seat belts, APU, Baro, Heading check | Gear down · Flaps one · Speedbrake retracted · … |

Antworten, die auf dem Plan beruhen, brauchen einen SimBrief-Plan – sonst „Unable“.

## Bestätigungspflicht („Check“ / „Copy“)

Wichtige Meldungen erwarten **„Check“** oder **„Copy“**: Fuel-Warnungen, Sicherheits-Cautions und -Warnungen, Squawk-/Frequenzabweichungen vom ATC, TCAS RA. Ohne Antwort wiederholt Duopilo nach ~10 s einmal und eskaliert dann mit einem Warnton und der Dashboard-Meldung `ACK_MISSING`. Ein spätes „Check“ oder das Ende des Zustands hebt sie auf. Einstellbar unter `acknowledge` in `voice-settings.json`.

## Checklisten

Challenge-Response nach der Airbus Normal Checklist:

| Sag | Deutsch |
|---|---|
| Before start checklist | Checkliste vor dem Anlassen |
| After start checklist | Checkliste nach dem Anlassen |
| Before takeoff checklist | Checkliste vor dem Start |
| Approach checklist | Checkliste Anflug |
| Landing checklist | Checkliste Landung |
| After landing checklist | Checkliste nach der Landung |
| Parking checklist | Checkliste Parken |

Duopilo liest die Challenge, wartet auf deine Antwort und **gleicht sie mit dem Sim ab**, wo es geht (Schilder, Sprit, Baro, Beacon, Klappen, Autothrust, Autobrake, Landing Memo, Spoiler, APU, Triebwerke). Eine Abweichung wird angesagt („Beacon off“), der Punkt bleibt offen. Weicht es danach immer noch ab, geht die Checkliste weiter und nennt am Ende die offenen Punkte.

„Standby“ hält an · „Skip“ / „Next item“ überspringt · „Say again“ wiederholt · „Stop checklist“ bricht ab. Solange eine Checkliste läuft, ist jeder Satz ohne Weckwort eine Antwort – Befehle brauchen „Kopilot, …“.

## Steuerung

| Sagen / drücken | Wirkung |
|---|---|
| „Kopilot pause“ / „Kopilot resume“ (auch „Kopilot weiter“, „Duopilo pause“) | Spracheingabe stumm / wieder an – Callouts laufen weiter |
| `Ctrl+Alt+K` (systemweit, `hotkeys.mute`) | dasselbe |
| „Say again“ | wiederholt die letzte Antwort |
| „Briefing“ | Vorstellung des First Officers |

## Funk

Funksprüche gehen direkt an das [ATC-Modul](DE-ATC) – wie `atc_transmit`, nur ohne Claude. „Say again“, „Affirm“ und „Negative“ ohne Station gehen an den, der zuletzt gesprochen hat. Ist ATC aus oder fehlt der SimBrief-Plan: „ATC unavailable“.

## Einstellungen (`voice-settings.json`)

Kommentare sind erlaubt. Alles gilt nur mit `"enabled": true`.

| Abschnitt | Was |
|---|---|
| `microphone` | Gerät (Teil des Namens, leer = Standard), VAD-Schwelle, Mindestlänge, Stille bis Satzende, Nachlauf nach Callouts |
| `recognition` | Whisper-Modell (`tiny`/`base`/`small`, `-q5_1`/`-q8_0`), `cpu`/`gpu`, Threads, Sprache `auto`/`de`/`en`, Wortschatz-Hinweis |
| `voices` | Stimme des Kopiloten (`kopilot`), Lautstärke von Kopilot und ATC |
| `hotkeys` | Tastenkürzel zum Stummschalten |
| `commands` | Weckwörter, `requireWakeWord`, Timeout für die Bestätigung, `executeWithCallout`, `minConfidence` |
| `acknowledge` | an/aus, `repeatAfterSec`, Regeln, die „check“/„copy“ erwarten |
| `atc` | zusätzliche Rufzeichen und Stationen, Rufzeichen aus MSFS |
| `callouts` | aktive und stumme Regeln, `silentWhenPaused`, Taxi-Speed-Warnung |

**Mikrofon:** Fehlt das gewählte Gerät (VR-Brille noch aus), nimmt Duopilo das Standardgerät und wechselt automatisch, sobald es auftaucht.

## Leistung

Standard: Whisper auf der CPU mit 4 Threads, gerechnet wird nur, nachdem du gesprochen hast. Schneller: feste Sprache (`"language": "en"`), `"audioContext": 768` oder `tiny-q5_1`. Besser Deutsch: `small-q5_1` (~190 MB). `gpu` versucht CUDA, dann Vulkan, sonst CPU – neben MSFS in VR kann das Ruckler kosten.

## Diagnose

- Status: `duopilo_status` → `voiceLoop`, im Dashboard neben dem Mikrofon oder unter `http://127.0.0.1:5155/voice`.
- Das Log zeigt jeden erkannten Satz mit Rechenzeit und Ziel, verworfene Sätze mit Grund und eine Warnung, wenn das Mikrofon nur Stille liefert (stummgeschaltet oder Windows-Datenschutzeinstellung *Desktop-Apps Zugriff auf das Mikrofon erlauben* aus).
- Mehr Details: `"Logging": { "LogLevel": { "Duopilo.Voice": "Debug" } }` auf oberster Ebene von `appsettings.Local.json`.

Lizenzen: Whisper.net und whisper.cpp (MIT), Silero VAD (MIT), NAudio (MIT).
