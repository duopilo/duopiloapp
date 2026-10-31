[English](Home) | **Deutsch**

# Duopilo-Wiki

**Duopilo** macht aus dem Microsoft Flight Simulator 2024 ein Cockpit für zwei. Es liest das Flugzeug über SimConnect aus und ist dein **Pilot Monitoring** – du bleibst **Pilot Flying**.

Dafür gibt es zwei Wege, die du zusammen oder einzeln nutzen kannst:

- **Mit Claude** – Duopilo ist ein MCP-Server. Claude liest Höhe, Geschwindigkeit, Konfiguration, Navigation, Wetter und ATC selbst ab, statt dass du sie vorlesen musst.
- **Allein** – Duopilo sagt an, überwacht, funkt und hört dir über eine komplett lokale Sprachschleife zu. Kein Claude, kein Push-to-Talk, VR-tauglich.

> Webseite: <https://duopilo.app> · Repository: <https://github.com/duopilo/duopiloapp> · Früher **Kopilot** (bis 0.9.x)

## Was Duopilo kann

| Bereich | Kurz gesagt |
|---|---|
| [Pilot Monitoring](DE-Pilot-Monitoring) | Prüft den Flug 5× pro Sekunde und sagt an wie ein echter PM: Gear/Flap speed, Sink rate, „One hundred knots“, „Positive climb“, „Ten thousand“, Fuel Checks … |
| [Sprachausgabe](DE-Sprachausgabe) | Spricht jeden Callout selbst – Windows-Stimme, Kokoro oder Supertonic, Warnungen zuerst |
| [Sprachschleife](DE-Sprachschleife) | Sprich mit deinem First Officer: Befehle, Abfragen, Checklisten, Funk – lokal, ohne Sprachmodell |
| [First Officer & Smalltalk](DE-First-Officer-und-Smalltalk) | Ein First Officer mit Name und Stimme pro Flug; optional Smalltalk über Claude, einen OpenAI-kompatiblen Server oder Ollama |
| [Eingabe-Modul](DE-Eingabe-Modul) | Stellt FCU, EFIS, Licht, Fahrwerk, Klappen … auf deine Ansage – mit Readback, Bestätigung und harten Sperren. Standardmäßig gesperrt |
| [ATC](DE-ATC) | Eigenes ATC von Delivery bis Ground inkl. Oceanic, Readback-Prüfung, eine Stimme pro Station – mit Vorlagen, ohne Sprachmodell |
| [Verkehr](DE-Verkehr) | KI-, Live- und Multiplayer-Verkehr mit Uhrzeit-Peilung und Verkehrswarnung |
| [SimBrief & Flugplan](DE-SimBrief-und-Flugplan) | Route, nächster Fix, ETA, Top of Descent und Sprit gegen den Plan |
| [Flugzeugprofile](DE-Flugzeugprofile) | L:-Variablen für Study-Level-Flugzeuge (FBW A32NX/A380X, Headwind A330-900neo, iniBuilds A321LR) |

## Hier anfangen

1. [Installation](DE-Installation) – Installer oder aus dem Quellcode bauen
2. [Schnellstart](DE-Schnellstart) – dein erster Flug mit Duopilo in zehn Minuten
3. [Claude verbinden](DE-Claude-verbinden) – Claude Desktop, claude.ai, Handy, Claude Code
4. [Dashboard & Einstellungen](DE-Dashboard-und-Einstellungen) – alles, was du einstellen kannst, ohne Dateien zu bearbeiten

Etwas stimmt nicht? → [Fehlersuche](DE-Fehlersuche) · [FAQ](DE-FAQ)

## Grundsätze

- **Der Pilot entscheidet.** Duopilo liest alles, bedient Schalter aber nur auf deine Ansage und nach einem Readback – und nie etwas, das das Flugzeug beschädigen kann oder irreversibel ist (Triebwerke, Sprit, Feuer, Hydraulik …). Siehe [Eingabe-Modul](DE-Eingabe-Modul).
- **Crew-Sprache ist Englisch.** Callouts, Checklisten, FMA-Ansagen, Warnungen und Funk sind immer Englisch, wie in einem echten Cockpit. Dashboard und Tool-Meldungen können Deutsch sein. Logs sind immer Englisch.
- **Kein Raten.** Liefert ein Flugzeug einen Wert nicht, bleibt der zugehörige Callout aus, statt falsch zu sein.
- **MSFS geht vor.** Spracherkennung und Stimmen laufen auf der CPU mit begrenzten Threads; ein lokales Sprachmodell gibt es bewusst nicht.

## Stand

Version **0.9.1** · Windows 10/11 x64 · MSFS 2024 · .NET 10. Releases entstehen im Moment nur als Entwurf (siehe [Installation](DE-Installation)). Was als Nächstes kommt: [Roadmap](DE-Roadmap).

## Für Entwickler

[Entwicklung](DE-Entwicklung) – Bauen, Tests, Projektaufbau, Übersetzungen, Releases und wie du mitmachen kannst.
