[English](Quick-Start) | **Deutsch**

# Schnellstart

Dein erster Flug mit Duopilo. Voraussetzung: Duopilo ist [installiert](DE-Installation).

## 1. MSFS und Duopilo starten

1. MSFS 2024 starten und einen Flug laden.
2. **Duopilo** starten (Startmenü oder `C:\Tools\Duopilo\Duopilo.exe`). Es öffnet sich kein Fenster – Duopilo sitzt im Infobereich der Taskleiste.
3. **Doppelklick auf das Tray-Symbol.** Das Dashboard öffnet sich unter <http://127.0.0.1:5155/>.

Der Punkt am Tray-Symbol zeigt den Zustand: grau = wartet auf MSFS, grün = verbunden, gelb = Caution aktiv, rot = Warning aktiv.

Prüfen: Unter <http://127.0.0.1:5155/status> sollte `"connected": true` stehen.

## 2. SimBrief-Plan eintragen

Dashboard → **Einstellungen → SimBrief** → deinen SimBrief-Username oder deine Pilot-ID eintragen. Der Plan lädt sofort.

Ohne Plan laufen Monitoring und Callouts trotzdem – aber Routenfortschritt, Fuel Checks, Top of Descent und [ATC](DE-ATC) brauchen ihn.

## 3. Stimme wählen

Duopilo spricht die Callouts selbst. Dashboard → **Ansagen → Stimme**: Sprachausgabe wählen (Automatisch, Windows, Kokoro), dazu die Stimme und das **Ausgabegerät** (z. B. dein Headset). Beim ersten Mal lädt Kokoro sein Modell (~310 MB). Details: [Sprachausgabe](DE-Sprachausgabe).

## 4. Fliegen

Das reicht schon für Duopilo als PM, der sich nur meldet, wenn es nötig ist: Er sagt „One hundred knots“, „V1“, „Rotate“, „Positive climb“, „Gear up“ und „Ten thousand“ an, prüft die Speed beim Klappenfahren und meldet Abweichungen und Warnungen. Siehe [Pilot Monitoring](DE-Pilot-Monitoring).

## Optionale nächste Schritte

| Du willst … | So geht's |
|---|---|
| mit Duopilo sprechen | Dashboard → **Ansagen → Stimme → Sprachschleife** anhaken und das Mikrofon wählen → [Sprachschleife](DE-Sprachschleife) |
| dass Duopilo auf deine Ansage Schalter bedient | Eingabe freigeben: in der Ansagen-Leiste zweimal auf **Eingabe gesperrt** klicken → [Eingabe-Modul](DE-Eingabe-Modul) |
| ATC von Delivery bis Ground | ATC ist standardmäßig an – mit SimBrief-Plan rufst du „Frankfurt Delivery, Lufthansa four alpha bravo, request clearance“ → [ATC](DE-ATC) |
| Claude als PM | [Claude verbinden](DE-Claude-verbinden) |
| das Dashboard auf Deutsch | **Einstellungen → Sprache** (*Settings → Language*) → Deutsch (Callouts und Funk bleiben Englisch) |
| Smalltalk mit deinem First Officer | **Einstellungen → First Officer und Smalltalk** → [First Officer & Smalltalk](DE-First-Officer-und-Smalltalk) |

## Eine typische Sitzung mit der Sprachschleife

```
Du:   "Before start checklist"
F/O:  "Before start checklist, cockpit preparation" …
Du:   "Clearance Delivery, Lufthansa four alpha bravo, information A, request clearance to Munich"
ATC:  "Lufthansa four alpha bravo, cleared to Munich, …, squawk two two zero five"
Du:   "Cleared to Munich, …, squawk two two zero five, Lufthansa four alpha bravo"
…
Du:   "Flaps one"                         → Klappenhebel gesetzt, sobald die Klappen dort sind: "Flaps one"
Du:   "Speed two five zero"               → FCU gesetzt, "Speed two five zero set"
…
F/O:  "Fuel thirty four point one, two point two below plan"
Du:   "Check"
```
