[English](First-Officer-and-Small-Talk) | **Deutsch**

# First Officer und Smalltalk

## First Officer

Mit eingeschalteter [Sprachschleife](DE-Sprachschleife) bekommt jeder Flug einen eigenen First Officer:

- einen **Namen**, zufällig gewählt aus international leicht aussprechbaren Namen (z. B. Anna, Emma, Sophie / Sam, Ben, Daniel),
- eine **Stimme** desselben Geschlechts, die nie die Stimme einer ATC-Station ist.

Name und Stimme bleiben den ganzen Flug gleich – auch über Neustarts von Duopilo hinweg (`%LOCALAPPDATA%\Duopilo\first-officer.json`). Ein neuer SimBrief-Plan bedeutet einen neuen Flug und einen neuen First Officer. Die Stimme gilt für Callouts, Antworten und Smalltalk.

| | |
|---|---|
| **Anzeige** | in der Kopfzeile des Dashboards („F/O Sam“), im Stimme-Panel und unter **Einstellungen** |
| **Vorstellung** | einmal pro Flug am Gate (am Boden, höchstens 5 NM vom Startflughafen) – „Hi, I'm Sam, I'll be your first officer today“ – oder wenn du **„Briefing“** sagst |
| **Ansprache** | „Sam, gear up“ wirkt wie „Kopilot, gear up“. Schreibvarianten von Whisper werden toleriert (Erik/Eric, Jonah/Jonas), normale Wörter zählen nie als Name („nice“ ist nicht Nico) |
| **Fester Name** | Einstellungen → **First Officer und Smalltalk** (mit Geschlecht für Namen, die nicht in der Liste stehen) |
| **Aus** | an derselben Stelle – Duopilo spricht dann mit der Dashboard-Stimme |

### Stimme

Einstellungen → **First Officer und Smalltalk → Stimme**: *Automatisch* (pro Flug, passend zum Namen) oder eine feste Stimme, vorher anhören mit **Probehören**. Gespeichert als `voices.kopilot` in `voice-settings.json`, gilt sofort. Eine feste Stimme bestimmt das Geschlecht.

| Engine | Stimmen | Spricht |
|---|---|---|
| **Kokoro** | weiblich `af_heart`, `af_bella`, `bf_emma`, `af_aoede`, `af_kore`, `af_sarah`; männlich `am_michael`, `am_fenrir`, `am_puck` (Reserve `bm_george`, `bm_fable`) | nur Englisch – dann ist auch der Smalltalk englisch |
| **Supertonic 3** | `M1`, `M2` (männlich), `F1` (weiblich) | Callouts auf Englisch, Smalltalk auf Deutsch |

*Automatisch* nimmt Supertonic, wenn der Smalltalk deutsch ist, sonst Kokoro. Bis das Supertonic-Modell (~400 MB, per SHA-256 geprüft) heruntergeladen ist, spricht der First Officer mit einer Kokoro-Stimme. Kein Supertonic gewünscht: `Smalltalk:Supertonic: false`.

Callouts und Smalltalk kommen immer von **demselben Sprecher**.

## Smalltalk (optional, standardmäßig aus)

Ein Satz, der weder Befehl noch Abfrage noch Funkspruch ist, geht an ein Sprachmodell statt „Say again“:

> „Hey Sam, how's the weather in Berlin?“

| Anbieter | Was du brauchst |
|---|---|
| **Claude** | eigener API-Schlüssel ([console.anthropic.com](https://console.anthropic.com)); Modell leer = Standard-Haiku-Modell |
| **OpenAI-kompatibel** | Basis-URL (Standard `https://api.openai.com/v1`; auch Groq, LM Studio, vLLM …), Modell, API-Schlüssel, falls nötig, **Reasoning** |
| **Ollama** | Adresse eines anderen Rechners (z. B. `http://192.168.1.20:11434`) und das Modell |

Ein **lokales Modell in Duopilo selbst** gibt es bewusst nicht – es würde neben MSFS zu viel GPU und RAM kosten. Willst du es lokal, lass Ollama auf einem zweiten Rechner laufen.

Einrichten unter Einstellungen → **First Officer und Smalltalk** (gilt sofort; **Testen** stellt eine kurze Frage). Der API-Schlüssel steht nur in `appsettings.Local.json` (oder `Duopilo__Smalltalk__ApiKey`), geht nie zurück an den Browser und landet nie in Logs oder Fehlerberichten.

### Regeln

- **Nur lesend.** Das Modell bekommt nur lesenden Kontext (Flugphase, Position, Route, nächster Wegpunkt, ETA, Sprit und Sprit gegen Plan, Wind und Temperatur, METARs aus dem Plan, ATC-Station) und die letzten paar Sätze. Seine Antwort wird nur gesprochen – **nie** als Befehl ausgeführt und nie für „Confirm“ vorbereitet.
- **Niedrigster Vorrang.** Callouts, Warnungen, Quittungen, Checklisten und Funk gehen immer vor; alles unterbricht den Smalltalk (keine Wiederholung). Solange ein Readback wartet, eine Checkliste läuft, eine Meldung auf „Check“ wartet oder das Mikrofon stumm ist, geht nichts ans Modell.
- **Kein Gerede im Raum.** Sätze ohne Anrede gehen nur bis `UnaddressedMaxWords` Wörter (Standard 12) ans Modell.
- **Grenzen.** `MaxReplyWords` (40), `RequestsPerMinute` (6), `TimeoutSec` (10). Über der Grenze, offline oder bei jedem Fehler: „I can't chat right now“.
- **Sprache.** Smalltalk folgt der Dashboard-Sprache (wenn die Stimme sie sprechen kann); Callouts und Funk bleiben Englisch.

### Reasoning-Modelle

Reasoning-Modelle (z. B. `openai/gpt-oss-20b` bei Groq) verbrauchen Tokens zum Nachdenken – dann bleibt für die Antwort manchmal nichts übrig. Einstellung **Reasoning** (nur OpenAI-kompatibel, `Smalltalk:Reasoning`):

| Einstellung | Verhalten |
|---|---|
| Automatisch (Standard) | schickt `reasoning_effort: "low"` bei Modellen mit „gpt-oss“ im Namen, sonst nichts |
| Aus | schickt es nie |
| Niedrig / Mittel / Hoch | schickt es immer; gibt dem Tokenlimit Spielraum (+512 / +1024 / +2048) |

Eine leere Antwort am Tokenlimit bekommt eine eigene Warnung im Log und beim Knopf **Testen**.

### Datenschutz und Kosten

Die Einstellungsseite sagt, wohin das Gespräch geht. Mit Claude oder einem OpenAI-kompatiblen Cloud-Dienst gehen deine Sätze und der lesende Flugkontext an diesen Anbieter und werden über deinen Schlüssel abgerechnet.

## Einstellungsreferenz

```jsonc
"Duopilo": {
  "FirstOfficer": { "Enabled": true, "Name": "", "Gender": "auto", "Introduce": true },
  "Smalltalk": {
    "Provider": "off",          // off | claude | openai | ollama
    "Model": "", "Url": "",
    "MaxReplyWords": 40, "RequestsPerMinute": 6, "TimeoutSec": 10, "UnaddressedMaxWords": 12,
    "Supertonic": true, "SupertonicThreads": 2,
    "Reasoning": "auto"
  }
}
```
