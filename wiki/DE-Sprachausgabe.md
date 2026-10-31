[English](Speech-Output) | **Deutsch**

# Sprachausgabe

Duopilo spricht Callouts, Antworten und ATC selbst – auf Englisch, wie eine echte Crew. Claude.ai und der Sprachmodus können keine Push-Nachrichten von einem Server empfangen. So erreichen dich Warnungen, ohne dass du nachfragen musst.

## Vorrang

Alles läuft durch eine Warteschlange:

**Warnungen > Callouts > Antworten > ATC > Smalltalk**

Warnungen unterbrechen alles; ältere Meldungen verfallen nach 8 s. Eine unterbrochene Antwort oder ATC-Meldung kommt nach der Warnung einmal erneut.

Gesprochen wird nur von der **Tray-Instanz** – nicht von der stdio-Instanz, die Claude Desktop startet. Sonst wäre alles doppelt zu hören.

## Sprach-Engines und Stimmen

| | Windows-Stimme | Kokoro | Supertonic 3 |
|---|---|---|---|
| Klang | Sprachausgabe von Windows | neuronal, natürlich | neuronal, natürlich |
| Sprachen | englische Stimme nötig | Englisch | Englisch **und** Deutsch mit jeder Stimme |
| Download | – (englische Windows-Stimme installieren) | Modell ~310 MB beim ersten Bedarf | Modell ~400 MB, sobald der First Officer eine Supertonic-Stimme bekommt |
| Genutzt für | Callouts | Callouts, ATC-Stationen, First Officer | First Officer, wenn der Smalltalk deutsch ist |
| CPU | praktisch keine | 2 Threads (`Speech:KokoroThreads`), feste Callouts vorab berechnet | 2 Threads (`Smalltalk:SupertonicThreads`), ~1,3 s pro kurzem Satz |

**Automatisch** (Standard) nimmt die Windows-Stimme, wenn eine englische installiert ist, sonst Kokoro. Solange Kokoro lädt, liest der Browser im Dashboard vor.

Mit eingeschaltetem [First Officer](DE-First-Officer-und-Smalltalk) spricht die **Stimme des First Officers** Callouts, Antworten und Smalltalk – ein Sprecher für alles. ATC-Stationen bekommen immer eigene Kokoro-Stimmen.

### Englische Windows-Stimme installieren (optional)

Einstellungen → Zeit und Sprache → Sprache und Region → Sprache hinzufügen → *English (United Kingdom)*, dabei „Sprachausgabe“ auswählen. Danach Duopilo neu starten.

## Einstellungen

Dashboard → **Ansagen → Stimme**:

- Sprachausgabe: Automatisch, Windows oder Kokoro
- Windows-Stimme (z. B. George, Hazel) oder Kokoro-Stimme (z. B. George, Emma, Michael)
- **Ausgabegerät** – z. B. das Headset statt der Lautsprecher
- Lautstärke und Tempo
- ab welcher Stufe gesprochen wird: alle, ab Caution, nur Warnings
- Radiohöhen / Minimum / Retard ergänzen (für Flugzeuge ohne eigene Ansagen)

Das Panel zeigt auch, welche Stimme gerade spricht und wie lange Kokoro für den letzten Callout gerechnet hat. Tray-Menü → **Callouts sprechen** schaltet die Sprachausgabe ein und aus.

Gespeichert wird in `%LOCALAPPDATA%\Duopilo\speech.json`, die Modelle liegen in `%LOCALAPPDATA%\Duopilo\models`.

## Aussprache

ATC, Callouts, Fuel Checks und die Antworten der Sprachschleife haben eine gemeinsame Aussprache (`Speech/Pronunciation.cs`):

| Was | Gesprochen |
|---|---|
| Fixe mit fünf Buchstaben | als Wort: HENDY → „Hendy“ |
| Funkfeuer (bis drei Buchstaben) | Name aus dem OFP-Navlog: YWK → „Wabush“; ohne Namen im NATO-Alphabet: FFM → „foxtrot foxtrot mike“ |
| ICAO-Codes | immer buchstabiert: KLAX → „kilo lima alpha x-ray“ |
| Koordinatenpunkte | 53N050W → „fifty three north fifty west“ |
| Bahnen | 25R → „two five right“, 09 → „zero niner“ |
| Squawks, Kurse, Speeds, Frequenzen | Ziffer für Ziffer, 9 als „niner“, Frequenzen mit „decimal“ |
| Flugflächen | „flight level three four zero“, volle Hunderter „flight level one hundred“ |
| Höhen | in Tausendern: „one zero thousand“, „two thousand five hundred“ |
| ECAM-Titel | ausgeschrieben: „ENG 1 FIRE“ → „engine one fire“, „APU“ → „A P U“ |

Geschwindigkeiten immer in Knoten, nie Mach.

## Stummschalten

| Ziel | So geht's |
|---|---|
| Einen Callout stumm schalten (bleibt sichtbar) | `"mutedRules": [ "PM_GEAR" ]` in `speech.json` |
| Eine Regel ganz abschalten | `Duopilo:Monitoring:DisabledRules` – siehe [Pilot Monitoring](DE-Pilot-Monitoring#regeln-abschalten) |
| Sprachausgabe komplett aus | Tray-Menü → **Callouts sprechen** |

## Lizenzen

Kokoro-82M und Stimmen: Apache 2.0 · KokoroSharp: MIT · Supertonic-3-Gewichte: BigScience OpenRAIL-M (mit Nutzungsbeschränkungen, z. B. maschinell erzeugte Stimme in veröffentlichten Inhalten kennzeichnen) · Supertonic-Inferenzbeispiel: MIT.
