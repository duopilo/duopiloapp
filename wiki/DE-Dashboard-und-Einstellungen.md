[English](Dashboard-and-Settings) | **Deutsch**

# Dashboard & Einstellungen

## Tray-Symbol

Duopilo läuft ohne Fenster im Infobereich der Taskleiste.

| Punkt | Bedeutung |
|---|---|
| grau | wartet auf MSFS |
| grün | verbunden |
| gelb | Caution aktiv |
| rot | Warning aktiv |

- **Doppelklick:** öffnet das Dashboard
- **Rechtsklick:** Dashboard, Profile neu laden, Logdatei, Programmordner, **Callouts sprechen** (an/aus), **Mit Windows starten**, Beenden

Ein zweiter Start von Duopilo öffnet nur das Dashboard der laufenden Instanz.

## Dashboard

<http://127.0.0.1:5155/> – läuft im Browser, **nur lokal auf dem Sim-PC** (über den Tunnel gesperrt).

- **FMA-Zeile** (Schub, vertikal, lateral, AP/FD/A-THR) in den Airbus-Farben
- **Flugdaten** mit Speed- und Höhenziel; Master Warning/Caution als Tasten oben rechts
- **Ansagen**: aktive Meldungen und Verlauf. Mit *Vorlesen an* spricht zusätzlich der Browser jeden Callout. In der Ansagen-Leiste sitzen außerdem der Schalter **Eingabe gesperrt/frei**, die offene Eingabe-Aktion mit Countdown und das Protokoll der Eingaben
- **Stimme**: Sprachausgabe, Sprachschleife, Mikrofon mit Pegel, First Officer
- **ECAM** im Klartext, **Route** mit Fortschritt und Sprit-Trend aus SimBrief, Triebwerke, Konfiguration, ATC-Bahn, Wetter
- **Einrichtung und Diagnose**: alle Profil-Bindings mit Status geprüft/ungeprüft und aktuellem Wert, Log, Knöpfe zum Neuladen
- Regler **Helligkeit** – dimmt die Anzeigen für Nachtflüge
- Oben rechts: **Fehler melden** und **Einstellungen**

## Einstellungsseite

`http://127.0.0.1:5155/#einstellungen` (Knopf **Einstellungen**) – statt Dateien von Hand zu bearbeiten.

| Bereich | Inhalt | Gilt |
|---|---|---|
| **Verbindung zu Claude** | lokaler MCP-Endpunkt, öffentliche Adresse des Tunnels, Freigabe-Passwort, Bearer-Token erzeugen/entfernen, Tunnel prüfen | nach Neustart |
| **SimBrief** | Username oder Pilot-ID, lädt gleich den Plan | sofort |
| **Mikrofon** | Aufnahmegerät der Sprachschleife, mit Pegel | sofort |
| **Module** | Monitoring, Eingabe (zweimal klicken), ATC, Sprachschleife | Monitoring/ATC nach Neustart, sonst sofort |
| **First Officer und Smalltalk** | Name und Geschlecht, Vorstellung, Stimme (mit **Probehören**), Smalltalk-Anbieter, Modell, Adresse, API-Schlüssel, Grenzen, **Testen** | sofort |
| **Sprache** | Dashboard, Tool-Meldungen, Tray-Menü, Meldungen aus „Fehler melden“ | sofort |

So wird gespeichert:

- `appsettings.Local.json` neben der `Duopilo.exe` – Kommentare und Reihenfolge bleiben erhalten; vorher landet eine Sicherung in `%LOCALAPPDATA%\Duopilo\appsettings.Local.backup.json`.
- Eingabe und Sprachschleife landen in `input.json` und `voice-settings.json` im Datenordner.
- Einstellungen, die nur beim Start gelesen werden, sind mit *gilt nach Neustart* markiert. Der Knopf **Duopilo neu starten** startet die Tray-Instanz neu (die ATC-Sitzung macht an ihrem gespeicherten Stand weiter).
- Passwörter, Tokens und API-Schlüssel gehen nie an den Browser zurück. Ein neu erzeugter Token wird genau einmal angezeigt.
- Setzt eine Umgebungsvariable (`Duopilo__…`) denselben Wert, sagt dir die Seite das – die Variable hat Vorrang.

## Sprache

Englisch ist Standard, Deutsch die erste Übersetzung. Umstellen unter **Einstellungen → Sprache** oder:

```jsonc
"Duopilo": { "Language": "de" }   // Standard "en"
```

| Folgt der Einstellung | Immer Englisch |
|---|---|
| Dashboard, Tool-Meldungen an Claude, Tray-Menü, Meldungen aus „Fehler melden“, Smalltalk | Callouts, Checklisten, FMA-Ansagen, Warnungen, Funk (ATC), **Logs** |

Die Sprachschleife versteht deutsche und englische Befehle, egal was hier eingestellt ist. Du willst Duopilo übersetzen? Siehe [Entwicklung → Übersetzungen](DE-Entwicklung#übersetzungen).

## Rohdaten (nur lokal)

| URL | Inhalt |
|---|---|
| `/status` | Verbindungsstatus (`"connected": true`) |
| `/state` | Flugzustand |
| `/systems`, `/nav`, `/env`, `/profile`, `/warnings`, `/alerts`, `/monitoring`, `/atc`, `/plan`, `/progress` | dieselben Daten, die die Tools liefern |
| `/read?name=INDICATED ALTITUDE&unit=feet` | eine einzelne SimVar oder `L:`-Variable |
| `/input-events?filter=FCU&changes=1` | Input Events des Flugzeugs mit Werten; `changes=1` nur die, die sich seit dem letzten Aufruf geändert haben |
| `/atc/controller` | Zustand des Duopilo-ATC |
| `/voice` | Status der Sprachschleife |

Schreibende Aufrufe des Dashboards senden den Header `X-Duopilo: 1`.
