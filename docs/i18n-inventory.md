# Hard-coded texts before i18n (inventory)

Snapshot of string literals with German text on `main` before the i18n PR, found heuristically. "kept" = still literally in the code: speech-recognition patterns (German and English words the pilot may say) and Whisper hallucination filters. Everything else moved to src/Kopilot/i18n/*.json. Generated for PR feature/i18n.

## Startup

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Program.cs:135` | log | Kopilot wurde nach einem Absturz von Windows neu gestartet. | moved |
| `Program.cs:175` | text | Logdatei nicht verfügbar ({mode}): {ex.Message} | moved |

## Remote access / OAuth

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Remote/OAuthEndpoints.cs:88` | text | Ungültiges JSON. | moved |
| `Remote/OAuthEndpoints.cs:94` | text | redirect_uris fehlt. | moved |
| `Remote/OAuthEndpoints.cs:132` | text | Unbekannter Client. Bitte die Verbindung in Claude neu starten. | moved |
| `Remote/OAuthEndpoints.cs:138` | text | Diese Weiterleitungsadresse ist für den Client nicht registriert. | moved |
| `Remote/OAuthEndpoints.cs:143` | text | Nur response_type=code wird unterstützt. | moved |
| `Remote/OAuthEndpoints.cs:149` | text | PKCE mit S256 ist Pflicht. | moved |
| `Remote/OAuthEndpoints.cs:172` | text | Ungültige Anfrage. | moved |
| `Remote/OAuthEndpoints.cs:180` | text | Die Anfrage ist abgelaufen. Bitte die Verbindung in Claude neu starten. | moved |
| `Remote/OAuthEndpoints.cs:199` | log | OAuth: Falsches Freigabe-Passwort für {Client} | moved |
| `Remote/OAuthEndpoints.cs:206` | text | Die Anfrage ist abgelaufen. Bitte die Verbindung in Claude neu starten. | moved |
| `Remote/OAuthEndpoints.cs:210` | log | OAuth: Zugriff freigegeben für {Client} | moved |
| `Remote/OAuthEndpoints.cs:247` | text | Nur authorization_code und refresh_token. | moved |
| `Remote/OAuthEndpoints.cs:313` | text | Zu viele falsche Passwörter. Freigabe gesperrt bis {until.ToLocalTime():HH:mm} Uhr. | moved |
| `Remote/McpAuthMiddleware.cs:55` | text | Kopilot: Zugriff nur mit gültigem Token. | moved |
| `Remote/OAuthStore.cs:119` | text | code und code_verifier sind Pflicht. | moved |
| `Remote/OAuthStore.cs:124` | text | Code ungültig, abgelaufen oder schon benutzt. | moved |
| `Remote/OAuthStore.cs:129` | text | Code gehört zu einem anderen Client. | moved |
| `Remote/OAuthStore.cs:134` | text | redirect_uri stimmt nicht mit der Autorisierung überein. | moved |
| `Remote/OAuthStore.cs:139` | text | PKCE-Prüfung fehlgeschlagen. | moved |
| `Remote/OAuthStore.cs:157` | text | refresh_token fehlt. | moved |
| `Remote/OAuthStore.cs:166` | text | Refresh-Token ungültig oder abgelaufen. | moved |
| `Remote/OAuthStore.cs:171` | text | Refresh-Token gehört zu einem anderen Client. | moved |
| `Remote/OAuthStore.cs:241` | log | OAuth-Zustand geladen: {Clients} Client(s), {Tokens} aktive Verbindung(en) – {File} | moved |
| `Remote/OAuthStore.cs:247` | log | OAuth-Zustand konnte nicht gelesen werden, starte leer: {File} | moved |
| `Remote/OAuthStore.cs:264` | log | OAuth-Zustand konnte nicht gespeichert werden: {File} | moved |
| `Remote/ConsentPage.cs:14` | text | <p class="warn">Die Weiterleitung geht an eine lokale Adresse. Das ist normal für Claude Code – gib nur fre… | moved |
| `Remote/ConsentPage.cs:18` | text |              <h1>Zugriff auf Kopilot freigeben?</h1>             <p><strong>{E(clientName)}</strong> möchte… | moved |
| `Remote/ConsentPage.cs:39` | text | Kopilot – Fehler | moved |
| `Remote/ConsentPage.cs:39` | text | <h1>Das hat nicht geklappt</h1><p class="error">{E(message)}</p> | moved |

## Voice loop

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Voice/VoiceIntents.cs:91` | text | bitte | kept |
| `Voice/VoiceIntents.cs:97` | text | bitte | kept |
| `Voice/VoiceIntents.cs:108` | text | on\|an\|ein\|einschalten\|engage\|engaged\|off\|aus\|ausschalten\|disengage\|disconnect | kept |
| `Voice/VoiceIntents.cs:224` | text | altitude\|alt\|höhe\|hoehe\|level | kept |
| `Voice/VoiceIntents.cs:233` | text | baro\|qnh\|höhenmesser\|altimeter | kept |
| `Voice/VoiceIntents.cs:238` | text | ^(?:(?:{q.Pattern}) (?:check\|checks\|control\|prüfen\|pruefen\|kontrolle\|status)\|(?:check\|prüfe\|pruefe… | kept |
| `Voice/VoiceIntents.cs:268` | text | (?:gear\|fahrwerk) (?:up\|hoch\|rein\|ein\|einfahren) | kept |
| `Voice/VoiceIntents.cs:270` | text | (?:gear\|fahrwerk) (?:down\|runter\|raus\|aus\|ausfahren) | kept |
| `Voice/VoiceIntents.cs:278` | text | (?:flaps\|klappen)(?: to\| auf)? (?<v>0\|1\|2\|3\|up\|zero\|retract\|einfahren\|full\|voll) | kept |
| `Voice/VoiceIntents.cs:289` | text | (?:speedbrake\|bremsklappen\|luftbremse)(?: to\| auf)? (?<v>retract\|retracted\|in\|ein\|einfahren\|zero\|0… | kept |
| `Voice/VoiceIntents.cs:295` | text | (?:ground )?spoilers (?:disarm\|disarmed\|entschärfen\|entschaerfen\|unscharf\|aus) | kept |
| `Voice/VoiceIntents.cs:303` | text | (?:autobrake\|autobremse)(?: to\| auf)? (?<v>lo\|low\|niedrig\|med\|medium\|mittel\|max\|maximum\|off\|aus\… | kept |
| `Voice/VoiceIntents.cs:312` | text | (?:taxi lights?\|rollscheinwerfer\|taxilicht) (?<v>on\|an\|ein\|off\|aus) | kept |
| `Voice/VoiceIntents.cs:314` | text | (?:takeoff lights?\|startlicht) (?<v>on\|an\|ein\|off\|aus) | kept |
| `Voice/VoiceIntents.cs:316` | text | nose lights? (?<v>taxi\|takeoff\|to\|off\|aus) | kept |
| `Voice/VoiceIntents.cs:330` | text | (?:qnh\|baro\|altimeter\|höhenmesser)(?: to\| auf)? (?<v>\d{3,4}) | kept |
| `Voice/VoiceIntents.cs:331` | text | (?:(?:baro\|qnh\|altimeter\|höhenmesser) )?(?:standard\|std)(?: set)? | kept |
| `Voice/VoiceIntents.cs:335` | text | (?:altitude\|alt\|höhe\|hoehe)(?: to\| auf)? (?<v>\d{4,5}) | kept |
| `Voice/VoiceIntents.cs:336` | text | fl(?: to\| auf)? (?<v>\d{2,3}) | kept |
| `Voice/VoiceIntents.cs:337` | text | (?:speed\|geschwindigkeit\|fahrt\|spd)(?: to\| auf)? (?<v>\d{3}) | kept |
| `Voice/VoiceIntents.cs:339` | text | (?:heading\|hdg\|kurs\|steuerkurs)(?: to\| auf)? (?<v>\d{1,3}) | kept |
| `Voice/VoiceIntents.cs:341` | text | (?:vs\|steigrate\|sinkrate)(?: to\| auf)? (?<v>-?\d{2,4}) | kept |
| `Voice/VoiceIntents.cs:361` | text | (?:squawk\|transponder\|transponder code)(?: to\| auf)? (?<v>\d{4}) | kept |
| `Voice/VoiceIntents.cs:366` | text | tcas(?: to\| auf)? (?<v>tara\|ta\|standby\|stby)(?: only)? | kept |
| `Voice/VoiceIntents.cs:373` | text | ^(?:pause\|stop\|standby\|ruhe\|halt\|mute\|stumm\|schweig\|aus)$ | kept |
| `Voice/VoiceIntents.cs:376` | text | ^(?:weiter\|resume\|continue\|listen\|unmute\|zuhören\|zuhoeren\|an\|on\|back\|wieder da\|hör zu\|hoer zu)$ | kept |
| `Voice/VoiceIntents.cs:379` | text | ^(?:say again\|bitte say again)$ | kept |
| `Voice/WhisperRecognizer.cs:144` | text | vielen dank für ihre aufmerksamkeit | kept |
| `Voice/WhisperRecognizer.cs:144` | text | bis zum nächsten mal | kept |
| `Voice/VoiceText.cs:20` | text | fünf | kept |
| `Voice/VoiceText.cs:30` | text | zwölf | kept |
| `Voice/VoiceText.cs:30` | text | fünfzehn | kept |
| `Voice/VoiceText.cs:31` | text | dreißig | kept |
| `Voice/VoiceText.cs:32` | text | fünfzig | kept |
| `Voice/VoiceText.cs:57` | text | flight level\|flugfläche\|flugflaeche | kept |
| `Voice/VoiceText.cs:71` | text | say again\|repeat\|wiederholen\|noch ?mal\|nochmal | kept |
| `Voice/VoiceText.cs:423` | text | eine | kept |
| `Voice/VoiceText.cs:423` | text | fünf | kept |
| `Voice/VoiceText.cs:429` | text | zwölf | kept |
| `Voice/VoiceText.cs:429` | text | fünfzehn | kept |
| `Voice/VoiceText.cs:435` | text | dreißig | kept |
| `Voice/VoiceText.cs:435` | text | fünfzig | kept |
| `Voice/VoiceRouter.cs:34` | text | höhe | kept |
| `Voice/VoiceRouter.cs:36` | text | höhenmesser | kept |
| `Voice/VoiceRouter.cs:37` | text | bitte | kept |
| `Voice/VoiceRouter.cs:37` | text | noch | kept |
| `Voice/VoiceRouter.cs:88` | text | kein Stationsrufzeichen | moved |
| `Voice/VoiceStatus.cs:97` | text | Die Sprachschleife läuft nur in der Tray-Instanz (HTTP-Modus, Dashboard http://127.0.0.1:5155/),  | moved |
| `Voice/VoiceStatus.cs:98` | text | nicht in der stdio-Instanz von Claude Desktop. | moved |
| `Voice/SileroVad.cs:53` | exception | Silero VAD braucht {FrameSamples} Samples, nicht {frame.Length}. | moved |
| `Voice/VoiceLoop.cs:104` | log | Sprachschleife: Neustart ({Reason}). | moved |
| `Voice/VoiceLoop.cs:181` | log | Sprachschleife: neuer Versuch in {Seconds} s (oder sofort nach einer Änderung im Dashboard). | moved |
| `Voice/VoiceLoop.cs:226` | log | Sprachschleife: {Error} – es gelten die Standardwerte. | moved |
| `Voice/VoiceLoop.cs:232` | log | Sprachschleife: {Hint} | moved |
| `Voice/VoiceLoop.cs:242` | text | Aus. Einschalten im Dashboard (Ansagen → Stimme → Sprachschleife) oder in {_store.FilePath} mit "enabled": … | moved |
| `Voice/VoiceLoop.cs:245` | log | Sprachschleife aus ({File}: "enabled": false). Einschalten im Dashboard unter Ansagen → Stimme oder in dies… | moved |
| `Voice/VoiceLoop.cs:273` | log | Sprachschleife bereit: Whisper {Model} ({Runtime}), Sprache {Language}, VAD-Schwelle {Threshold}, ATC-Modul… | moved |
| `Voice/VoiceLoop.cs:279` | text | gesperrt (Befehle werden mit "Input disabled" beantwortet) | moved |
| `Voice/VoiceLoop.cs:304` | exception | Ein Teil der Sprachschleife hat sich unerwartet beendet. | moved |
| `Voice/VoiceLoop.cs:313` | log | Sprachschleife gestoppt: {Message} | moved |
| `Voice/VoiceLoop.cs:362` | log | Sprachschleife: Einstellungen aus {File} – enabled {Enabled}, Mikrofon "{Device}", Whisper {Model} ({Cpu}, … | moved |
| `Voice/VoiceLoop.cs:363` | text | VAD-Schwelle {Threshold}, Stille {SilenceMs} ms, Mindestlänge {MinSpeechMs} ms, Anrede Pflicht {Wake}, Tast… | moved |
| `Voice/VoiceLoop.cs:366` | text | (Windows-Standardgerät) | moved |
| `Voice/VoiceLoop.cs:378` | log | Aufnahmegeräte: {Devices} | moved |
| `Voice/VoiceLoop.cs:380` | text | keine gefunden | moved |
| `Voice/VoiceLoop.cs:396` | log | Silero VAD fehlt – lade nach {Path} … | moved |
| `Voice/VoiceLoop.cs:404` | log | Whisper-Modell {Model} fehlt – lade nach {Path} … | moved |
| `Voice/VoiceLoop.cs:426` | exception | Whisper-Modell nicht gefunden: {whisperPath} | moved |
| `Voice/VoiceLoop.cs:465` | log | Sprachschleife: {Problem} | moved |
| `Voice/VoiceLoop.cs:496` | log | Mikrofon "{Device}" nicht gefunden – nehme {Fallback}. Vorhanden: {Devices}. Kopilot wechselt, sobald es da… | moved |
| `Voice/VoiceLoop.cs:503` | log | Mikrofon offen: {Device} (WinMM-Gerät {Number}, 16 kHz mono). | moved |
| `Voice/VoiceLoop.cs:526` | log | Mikrofon "{Device}" ist jetzt verfügbar – wechsle. | moved |
| `Voice/VoiceLoop.cs:538` | log | Mikrofon {Device}: Aufnahme beendet – öffne es erneut. | moved |
| `Voice/VoiceLoop.cs:544` | log | Mikrofon nicht verfügbar: {Message} | moved |
| `Voice/VoiceLoop.cs:570` | log | Mikrofon liefert Audio: erster Block nach {Ms} ms, Pegel {Level:0} dBFS. | moved |
| `Voice/VoiceLoop.cs:609` | log | Mikrofon {Device}: seit {Seconds} s keine Daten. Gerät belegt oder defekt? Anderes Mikrofon im Dashboard wä… | moved |
| `Voice/VoiceLoop.cs:618` | log | Mikrofon {Device} liefert nur Stille (alle Werte 0). Mikrofon stummgeschaltet, falsches Gerät, oder in Wind… | moved |
| `Voice/VoiceLoop.cs:619` | text | Datenschutz → Mikrofon "Desktop-Apps Zugriff auf das Mikrofon erlauben" aus? | moved |
| `Voice/VoiceLoop.cs:626` | log | Mikrofon {Device}: seit {Seconds} s keine Daten mehr. | moved |
| `Voice/VoiceLoop.cs:641` | log | Sprachschleife {State}: Mikrofon {Device}, letzte {Minutes} min: {Blocks} Audioblöcke, Spitzenpegel {Peak} … | moved |
| `Voice/VoiceLoop.cs:642` | text | {Speech} s Sprache, {Utterances} Sätze, {Dropped} verworfen. | moved |
| `Voice/VoiceLoop.cs:747` | text | Whisper kommt nicht hinterher | moved |
| `Voice/VoiceLoop.cs:748` | log | Satz verworfen ({Seconds:0.0} s) – Whisper kommt nicht hinterher ({Max} Sätze warten schon). | moved |
| `Voice/VoiceLoop.cs:788` | log | Spracheingabe {State} (Tastenkürzel). | moved |
| `Voice/VoiceLoop.cs:806` | log | Sprachschleife: Satz nicht verarbeitet: {Message} | moved |
| `Voice/VoiceLoop.cs:827` | log | Satz verworfen ({Seconds:0.0} s) – Spracheingabe stumm (Tastenkürzel oder "Kopilot Pause"). | moved |
| `Voice/VoiceLoop.cs:876` | text | Fehler: {result.Error} | moved |
| `Voice/VoiceLoop.cs:911` | log | Antwort "{Text}" nicht gesprochen (keine Stimme oder Sim pausiert). | moved |
| `Voice/VoiceLoop.cs:969` | log | Aufnahmegeräte nicht lesbar: {Message} | moved |
| `Voice/VoiceSettings.cs:343` | text | In der Vorlage {TemplatePath} steht "enabled": true – wirksam ist aber nur {FilePath} (dort steht false).  | moved |
| `Voice/VoiceSettings.cs:344` | text | Die Vorlage überschreibt update.cmd bei jedem Update. | moved |
| `Voice/VoiceSettings.cs:349` | text | Mikrofon "{template.Microphone.Device}" steht nur in der Vorlage {TemplatePath} – wirksam ist {FilePath}. | moved |
| `Voice/ModelDownloader.cs:49` | exception | Download von {Path.GetFileName(path)} unvollständig ({length / 1048576.0:0.0} MB). | moved |
| `Voice/ModelDownloader.cs:67` | exception | Download hängt seit einer Minute. | moved |
| `Voice/VoiceDialog.cs:179` | text | Readback nicht gesprochen | moved |
| `Voice/VoiceDialog.cs:201` | text | Keine Bestätigung (Sprache) | moved |
| `Voice/GlobalHotkey.cs:53` | text | Tastenkürzel "{text}": mehr als eine Taste. | moved |
| `Voice/GlobalHotkey.cs:60` | text | Tastenkürzel "{text}": unbekannte Taste "{raw}". | moved |
| `Voice/GlobalHotkey.cs:67` | text | Tastenkürzel "{text}": keine Taste. | moved |
| `Voice/GlobalHotkey.cs:124` | text | Zeitüberschreitung | moved |
| `Voice/GlobalHotkey.cs:142` | text | Tastenkürzel {binding.Text} ist schon belegt (Fehler {Marshal.GetLastWin32Error()}). | moved |
| `Voice/VoiceDispatcher.cs:57` | text | ATC hat nach dem Readback gesprochen | moved |
| `Voice/VoiceDispatcher.cs:76` | text | ATC ist ausgeschaltet. | moved |
| `Voice/MicrophoneCapture.cs:91` | text | Standardgerät | moved |
| `Voice/MicrophoneCapture.cs:94` | text | Standardgerät | moved |
| `Voice/MicrophoneCapture.cs:103` | text | Standardgerät ({defaultName}) | moved |

## Bug reports

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Reports/BugReportService.cs:89` | text | Lokale Kopie nicht geschrieben: {ex.Message} | moved |
| `Reports/BugReportService.cs:102` | text | GitHub: [neues Issue mit diesem Inhalt]({issue.Url})\n\n | moved |
| `Reports/BugReport.cs:134` | text |  bei  | moved |
| `Reports/BugReport.cs:168` | text | nicht verbunden | moved |
| `Reports/BugReport.cs:204` | text | Höhe | moved |
| `Reports/BugReport.cs:211` | text | aus MSFS | moved |
| `Reports/BugReport.cs:212` | text | aus VATSIM-Sektordaten | moved |
| `Reports/BugReport.cs:213` | text | aus atc-templates.json | moved |
| `Reports/BugReport.cs:227` | text | nächste {next} bei Meile {at:0} | moved |
| `Reports/BugReport.cs:227` | text | nächste {next} | moved |
| `Reports/BugReport.cs:237` | text | keine Sitzung | moved |
| `Reports/BugReport.cs:247` | text | , CultureInfo.InvariantCulture)} aktiv | moved |
| `Reports/BugReport.cs:269` | text | Flug | moved |
| `Reports/BugReport.cs:274` | text |  (ohne Profil) | moved |
| `Reports/BugReport.cs:284` | text | Sprachschleife zuletzt | moved |
| `Reports/BugReport.cs:290` | text | Letzte Funksprüche: | moved |
| `Reports/BugReport.cs:300` | text | ({c.Transcript.Count - lines.Count} ältere Funksprüche in der lokalen Kopie) | moved |
| `Reports/BugReport.cs:375` | text | ### Was ist falsch, was wäre richtig? | moved |
| `Reports/BugReport.cs:376` | text | ### Kontext aus Kopilot | moved |
| `Reports/BugReport.cs:399` | text |  … (gekürzt, vollständig in der lokalen Kopie) | moved |
| `Reports/ReportEndpoints.cs:69` | text | Die Momentaufnahme ist nicht mehr da (Kopilot neu gestartet?). Bitte „Fehler melden“ noch einmal drücken. | moved |
| `Reports/ReportEndpoints.cs:76` | text | Bitte eine Art wählen oder kurz beschreiben, was nicht stimmt. | moved |

## Speech output

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Speech/CalloutSpeaker.cs:218` | log | Stimmen/Geräte konnten nicht gelesen werden: {Message} | moved |
| `Speech/CalloutSpeaker.cs:237` | log | speech.json konnte nicht gespeichert werden: {Message} | moved |
| `Speech/CalloutSpeaker.cs:270` | exception | Sprachausgabe ist nicht verfügbar. | moved |
| `Speech/CalloutSpeaker.cs:297` | text | keine | moved |
| `Speech/CalloutSpeaker.cs:298` | text |  (keine englische Stimme installiert) | moved |
| `Speech/CalloutSpeaker.cs:302` | text | Windows-Sprachausgabe nicht verfügbar: {ex.Message} | moved |
| `Speech/CalloutSpeaker.cs:371` | log | Kokoro konnte nicht geladen werden: {Message} | moved |
| `Speech/CalloutSpeaker.cs:526` | exception | Windows-Wiedergabe nicht verfügbar. | moved |
| `Speech/CalloutSpeaker.cs:744` | log | Ansage "{Text}" unterbrochen, kommt nach der Warnung erneut. | moved |
| `Speech/CalloutSpeaker.cs:775` | log | Rückmeldung nach "{Text}" fehlgeschlagen: {Message} | moved |
| `Speech/CalloutSpeaker.cs:812` | log | Spracheinstellungen nicht übernommen: {Message} | moved |
| `Speech/CalloutSpeaker.cs:839` | text | Kokoro wird geladen ({Volatile.Read(ref _kokoroProgress):P0} von ~310 MB) – bis dahin liest der Browser vor. | moved |
| `Speech/CalloutSpeaker.cs:841` | text | Kokoro konnte nicht geladen werden: {_kokoroError} | moved |
| `Speech/CalloutSpeaker.cs:843` | text | Keine englische Windows-Stimme installiert. Stimme auf „Kokoro“ oder „Automatisch“ stellen, oder Englisch (… | moved |
| `Speech/KokoroSpeechEngine.cs:92` | log | Sprachmodell fehlt – lade Kokoro (~310 MB) nach {Path} … | moved |
| `Speech/KokoroSpeechEngine.cs:110` | exception | Das Sprachmodell ist noch nicht geladen. | moved |
| `Speech/KokoroSpeechEngine.cs:178` | exception | Stimme "{name}" nicht gefunden (erwartet unter {path}). | moved |
| `Speech/KokoroSpeechEngine.cs:226` | exception | Download des Sprachmodells unvollständig ({length / 1048576.0:0} MB). | moved |
| `Speech/KokoroSpeechEngine.cs:244` | exception | Download des Sprachmodells hängt seit einer Minute. | moved |

## ATC

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Atc/Controller/AtcTemplates.cs:328` | exception | Die Vorlagendatei muss ein JSON-Objekt sein. | moved |
| `Atc/Controller/AtcTemplates.cs:359` | text | Funkspruch "{id}" fehlt. | moved |
| `Atc/Controller/AtcTemplates.cs:367` | text | {id}: "from" muss "atc" oder "pilot" sein. | moved |
| `Atc/Controller/AtcTemplates.cs:379` | text | {id}: Readback-Punkt "{item}" ist nicht prüfbar. | moved |
| `Atc/Controller/AtcTemplates.cs:383` | text | {id}: Für den Readback-Punkt "{item}" fehlt eine Korrektur unter "corrections". | moved |
| `Atc/Controller/AtcTemplates.cs:395` | text | voices.pool ist leer – jede Station braucht eine Stimme. | moved |
| `Atc/Controller/AtcTemplates.cs:413` | text | {id}: Geschwindigkeiten nur in Knoten, nicht in Mach. | moved |
| `Atc/Controller/AtcTemplateMigration.cs:35` | text | {label}: Gander Oceanic von "CZQX" nach "CZQO" übernommen – CZQX ist jetzt Gander Center (Domestic). | moved |
| `Atc/Controller/AtcTemplateMigration.cs:39` | text | {label}: alter Oceanic-Eintrag "CZQX" entfernt – CZQX ist jetzt Gander Center (Domestic), Oceanic steht unt… | moved |
| `Atc/Controller/AtcTemplateMigration.cs:49` | text | {label}: feste Stimme von FIR/CZQX nach FIR/CZQO übernommen. | moved |
| `Atc/Controller/AtcSessionStore.cs:88` | text | {Path.GetFileName(FilePath)} nicht gesichert: {ex.Message} | moved |
| `Atc/Controller/AtcSessionStore.cs:111` | text | {Path.GetFileName(FilePath)} nicht gelöscht: {ex.Message} | moved |
| `Atc/Controller/AtcService.cs:153` | text | {AtcTemplateLoader.FileName} nicht gefunden. | moved |
| `Atc/Controller/AtcService.cs:195` | log | ATC-Vorlagen geladen: {Path}{Base} ({Count} Funksprüche, {Airlines} Airlines aus OpenFlights) | moved |
| `Atc/Controller/AtcService.cs:197` | text |  über {basePath} | moved |
| `Atc/Controller/AtcService.cs:238` | text | Kein Funkspruch übergeben. | moved |
| `Atc/Controller/AtcService.cs:251` | text | ATC-Sitzung nicht verfügbar. | moved |
| `Atc/Controller/AtcService.cs:268` | text | ATC-Sitzung läuft. | moved |
| `Atc/Controller/AtcService.cs:291` | text | Vorlagen mit Fehlern:  | moved |
| `Atc/Controller/AtcService.cs:312` | text | Keine ATC-Sitzung – zuerst einen Funkspruch senden oder atc_control start. | moved |
| `Atc/Controller/AtcService.cs:319` | text | {key} zurückgesetzt. | moved |
| `Atc/Controller/AtcService.cs:326` | log | ATC ist ausgeschaltet (Kopilot:Atc:Enabled). | moved |
| `Atc/Controller/AtcService.cs:339` | log | Luftraumdaten nicht geladen | moved |
| `Atc/Controller/AtcService.cs:392` | log | Fehler in der ATC-Zustandsmaschine | moved |
| `Atc/Controller/AtcService.cs:428` | log | ATC-Sitzung nicht fortgesetzt: {Reason} | moved |
| `Atc/Controller/AtcService.cs:433` | log | ATC-Sitzung nicht fortgesetzt: {Message} | moved |
| `Atc/Controller/AtcService.cs:458` | log | ATC-Sitzungssicherung von {Saved:u} ist zu alt ({Hours:0.0} h) – neue Sitzung. | moved |
| `Atc/Controller/AtcService.cs:464` | log | ATC-Sitzungssicherung gehört zu einem anderen Flug ({Saved}) – neue Sitzung. | moved |
| `Atc/Controller/AtcService.cs:515` | text | ATC ist ausgeschaltet (Kopilot:Atc:Enabled). | moved |
| `Atc/Controller/AtcService.cs:527` | text | ATC-Vorlagen fehlen oder sind ungültig:  | moved |
| `Atc/Controller/AtcService.cs:552` | text | keine passende Sicherung für diesen Flug. | moved |
| `Atc/Controller/AtcService.cs:567` | text | freigegeben {Cleared} ft, Squawk {Squawk}, {Lines} Funksprüche{Note} | moved |
| `Atc/Controller/AtcService.cs:591` | text | , mit Oceanic-Teil | moved |
| `Atc/Controller/AtcService.cs:635` | log | ATC-Funkspruch nicht gesprochen: {Message} | moved |
| `Atc/Controller/AtcService.cs:680` | log | Facilities für {Icao} nicht verfügbar: {Message} | moved |
| `Atc/Controller/AtcSession.Persistence.cs:205` | text | Readback {pending} war beim Sichern noch offen – gilt als erledigt ({s.Step} → {Step}). | moved |
| `Atc/Airspace/AirspaceLoader.cs:38` | text | VAT-Spy-Daten ({VatSpyDat}, {VatSpyBoundaries}) nicht gefunden – FIR-Grenzen nur aus dem OFP. | moved |
| `Atc/Airspace/AirspaceLoader.cs:46` | text | VAT-Spy: {vatspy} ({firs.Count(f => f.HasBoundary)} FIRs mit Grenzen) | moved |
| `Atc/Airspace/AirspaceLoader.cs:58` | text | {SectorsFile} nicht gefunden – keine Sektoren, eine Frequenz je FIR. | moved |
| `Atc/Airspace/AirspaceLoader.cs:84` | text | {AirlinesFile} (OpenFlights) nicht gefunden – Rufzeichen nur aus atc-templates.json. | moved |
| `Atc/Airspace/AirspaceLoader.cs:273` | text | {SectorsFile}: {skipped} Sektoren unvollständig, übersprungen. | moved |

## Flight plan

| File:line | Kind | Text | Status |
|---|---|---|---|
| `FlightPlan/SimBriefService.cs:64` | log | SimBrief-Konto geändert (Dashboard): {Account} | moved |
| `FlightPlan/SimBriefService.cs:73` | text | SimBrief ist nicht eingerichtet. Im Dashboard unter Einstellungen → SimBrief (oder in appsettings.Local.jso… | moved |
| `FlightPlan/SimBriefService.cs:112` | text | SimBrief nicht erreichbar oder Antwort ungültig: {ex.Message} | moved |

## Hosting

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Hosting/KopilotConfiguration.cs:53` | text | Ungültige HttpUrl: '{url}'. | moved |
| `Hosting/KopilotConfiguration.cs:59` | text | HttpUrl '{url}' bindet nicht an localhost. Erlaubt sind nur 127.0.0.1, localhost und [::1] –  | moved |
| `Hosting/KopilotConfiguration.cs:60` | text | der Zugang von außen läuft ausschließlich über den Cloudflare Tunnel. | moved |
| `Hosting/HttpHost.cs:116` | log | Zugriffsschutz: Bearer-Token aktiv. | moved |
| `Hosting/HttpHost.cs:120` | log | Bearer-Token ist kürzer als {Min} Zeichen und wird ignoriert. Neuen erzeugen mit: Kopilot.exe --generate-se… | moved |
| `Hosting/HttpHost.cs:125` | log | Zugriffsschutz: OAuth aktiv. Connector-URL für Claude: {Url} | moved |
| `Hosting/HttpHost.cs:130` | text | PublicUrl muss eine https-Adresse ohne Pfad sein | moved |
| `Hosting/HttpHost.cs:132` | log | OAuth ist NICHT aktiv: {Reason}. | moved |
| `Hosting/HttpHost.cs:137` | log | Kein Token konfiguriert – /mcp ist gesperrt. Die Browser-Prüfseiten funktionieren lokal weiterhin. Siehe RE… | moved |
| `Hosting/AppRestart.cs:28` | text | Neustart ist nur in der Tray-Instanz unter Windows möglich. | moved |
| `Hosting/AppRestart.cs:54` | log | Neustart nicht möglich: {Message} | moved |
| `Hosting/AppRestart.cs:55` | log | Neustart nicht möglich: {ex.Message} | moved |
| `Hosting/CrashReporter.cs:22` | text | Ereignisanzeige nicht lesbar | moved |
| `Hosting/CrashReporter.cs:41` | text | Nicht beobachtete Task-Ausnahme (Kopilot läuft weiter) | moved |
| `Hosting/CrashReporter.cs:61` | log | Absturz-Journal nicht verfügbar ({Path}): {Message} | moved |
| `Hosting/CrashReporter.cs:88` | log | Neustart nach Absturz nicht registriert (HRESULT 0x{Hr:X8}). | moved |
| `Hosting/CrashReporter.cs:93` | log | Neustart nach Absturz nicht verfügbar: {Message} | moved |
| `Hosting/CrashReporter.cs:104` | text | (keine Ausnahme übergeben) | moved |
| `Hosting/CrashReporter.cs:131` | log | Kopilot wurde zuletzt NICHT sauber beendet: Lauf mit PID {Pid}, gestartet {Start:HH:mm:ss}Z, letztes Lebens… | moved |
| `Hosting/CrashReporter.cs:132` | text | Vermutlich ein Absturz – Details folgen und stehen in {CrashLog}. | moved |
| `Hosting/CrashReporter.cs:141` | text |  Bericht nach Neustart: Lauf PID  | moved |
| `Hosting/CrashReporter.cs:149` | text | Letzte Einträge im Journal: | moved |
| `Hosting/CrashReporter.cs:156` | log | Letzte Einträge vor dem Absturz: {Entries} | moved |
| `Hosting/CrashReporter.cs:180` | text | Ereignisanzeige: kein passender Eintrag für Kopilot gefunden. | moved |
| `Hosting/CrashReporter.cs:190` | log | {File} nicht geschrieben: {Message} | moved |

## Settings

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Settings/SettingsEndpoints.cs:69` | text | Neustart gibt es nur in der Tray-Instanz (HTTP-Modus). | moved |
| `Settings/LocalSettingsStore.cs:93` | text | {KopilotConfiguration.LocalSettingsFile} ist kein gültiges JSON ({ex.Message}). Bitte die Datei von Hand ko… | moved |
| `Settings/LocalSettingsStore.cs:108` | exception | {FilePath} nicht lesbar: {ex.Message} | moved |
| `Settings/LocalSettingsStore.cs:134` | text | Keine Schreibrechte für {FilePath}. Liegt Kopilot in einem geschützten Ordner (z. B. C:\\Program Files) ohn… | moved |
| `Settings/LocalSettingsStore.cs:135` | text | vom Installer, lege {KopilotConfiguration.LocalSettingsFile} einmal von Hand an oder gib deinem Benutzer Sc… | moved |
| `Settings/LocalSettingsStore.cs:140` | exception | {FilePath} nicht geschrieben: {ex.Message} | moved |
| `Settings/SettingsService.cs:36` | text | Öffentliche Adresse (Tunnel) | moved |
| `Settings/SettingsService.cs:175` | text | Der SimBrief-Username ist zu lang (höchstens 64 Zeichen). | moved |
| `Settings/SettingsService.cs:180` | text | Die Pilot-ID besteht nur aus Ziffern (simbrief.com → Account Settings). | moved |
| `Settings/SettingsService.cs:192` | text | SimBrief unverändert ({account}). | moved |
| `Settings/SettingsService.cs:208` | text | Die öffentliche Adresse muss mit https:// beginnen und darf keinen Pfad haben, z. B. https://kopilot.exampl… | moved |
| `Settings/SettingsService.cs:233` | text | Nichts geändert. | moved |
| `Settings/SettingsService.cs:242` | log | Einstellungen (Dashboard): Zugang für Claude geändert ({Keys}) – gilt nach Neustart. | moved |
| `Settings/SettingsService.cs:243` | text | Zugang für Claude gespeichert | moved |
| `Settings/SettingsService.cs:268` | log | Einstellungen (Dashboard): Bearer-Token {Action} – gilt nach Neustart. | moved |
| `Settings/SettingsService.cs:270` | text | Neuer Bearer-Token gespeichert – jetzt kopieren, er wird nur einmal angezeigt | moved |
| `Settings/SettingsService.cs:313` | text | Die Sprachschleife gibt es nur in der Tray-Instanz (HTTP-Modus). | moved |
| `Settings/SettingsService.cs:322` | text | {VoiceSettingsStore.FileName} nicht geschrieben: {ex.Message} | moved |
| `Settings/SettingsService.cs:325` | text | Dashboard-Einstellungen: Sprachschleife {(v ?  | moved |
| `Settings/SettingsService.cs:326` | text | Sprachschleife an | moved |
| `Settings/SettingsService.cs:326` | text | Sprachschleife aus | moved |
| `Settings/SettingsService.cs:331` | text | Nichts geändert. | moved |
| `Settings/SettingsService.cs:349` | text | Keine öffentliche Adresse eingetragen – Claude erreicht Kopilot nur lokal (Claude Desktop, Claude Code). | moved |
| `Settings/SettingsService.cs:350` | text | Die öffentliche Adresse ist ungültig (https:// ohne Pfad). | moved |
| `Settings/SettingsService.cs:366` | text | Cloudflare blockt diesen Rechner (HTTP 403) – vermutlich die WAF-Regel, die /mcp nur für Claude öffnet.  | moved |
| `Settings/SettingsService.cs:367` | text | Das ist so gewollt; von hier lässt sich der Tunnel dann nicht prüfen. | moved |
| `Settings/SettingsService.cs:368` | text | Kopilot hat den Hostnamen abgelehnt (HTTP 400). Passt die öffentliche Adresse zum Hostnamen des Tunnels? | moved |
| `Settings/SettingsService.cs:370` | text | Cloudflare erreicht Kopilot nicht (HTTP {code}). Läuft cloudflared, und zeigt der Tunnel auf {_options.Http… | moved |
| `Settings/SettingsService.cs:376` | text | {url} nicht erreichbar: {ex.Message} – DNS-Eintrag und Tunnel prüfen. | moved |
| `Settings/SettingsService.cs:380` | text | Keine Antwort von {url} nach 10 s. | moved |
| `Settings/SettingsService.cs:393` | text | {text} – gilt nach einem Neustart von Kopilot. | moved |
| `Settings/SettingsService.cs:406` | log | Einstellungen nicht gespeichert: {Message} | moved |

## Tray icon

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Desktop/TrayService.cs:52` | log | Tray-Symbol konnte nicht erstellt werden – Kopilot läuft ohne weiter. | moved |
| `Desktop/TrayService.cs:121` | text | Dashboard öffnen | moved |
| `Desktop/TrayService.cs:131` | text | Logdatei öffnen | moved |
| `Desktop/TrayService.cs:132` | text | Programmordner öffnen | moved |
| `Desktop/TrayService.cs:164` | text | Kopilot läuft | moved |
| `Desktop/TrayService.cs:165` | text | Kopilot sitzt jetzt hier im Infobereich. Doppelklick öffnet das Dashboard. | moved |
| `Desktop/TrayService.cs:188` | text | Kopilot – wartet auf MSFS | moved |
| `Desktop/TrayService.cs:207` | text | Wartet auf Microsoft Flight Simulator | moved |
| `Desktop/TrayService.cs:220` | text | {_profiles.All.Count} Profile geladen, {errors} mit Fehlern – Details im Dashboard. | moved |
| `Desktop/TrayService.cs:232` | text | Konnte den Autostart nicht ändern: {ex.Message} | moved |
| `Desktop/TrayService.cs:249` | text | Callouts sprechen (lädt Sprachmodell {st.Kokoro.Progress ?? 0:P0}) | moved |
| `Desktop/TrayService.cs:251` | text | Callouts sprechen (keine Stimme – siehe Dashboard) | moved |
| `Desktop/TrayService.cs:271` | text | Konnte die Sprachausgabe nicht umschalten: {ex.Message} | moved |

## Tool messages

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Tools/KopilotTools.cs:19` | text | Keine Verbindung zu Microsoft Flight Simulator. Kopilot verbindet sich automatisch, sobald MSFS läuft. | moved |
| `Tools/KopilotTools.cs:201` | text | ENG {i}: EGT {egt:0} °C bei laufendem Triebwerk – unplausibel (extremes Wetter oder Air-Start?). | moved |
| `Tools/KopilotTools.cs:207` | text | ENG {i}: Fuel Flow 0 bei laufendem Triebwerk – Variable fuelFlowKgh im Profil {profile.Name} prüfen. | moved |
| `Tools/KopilotTools.cs:208` | text | ENG {i}: Fuel Flow 0 bei laufendem Triebwerk – für dieses Flugzeug gibt es kein Profil, und die generische … | moved |
| `Tools/KopilotTools.cs:309` | text | Die GPS-Flugplanvariablen des Sims sind leer – das FMS dieses Flugzeugs speist sie nicht. Für Route und For… | moved |
| `Tools/KopilotTools.cs:380` | text | Für dieses Flugzeug gibt es kein Profil. Profile liegen in {profiles.DirectoryPath}. | moved |
| `Tools/KopilotTools.cs:546` | text | Generische SimVars – bei Study-Level-Airlinern evtl. nicht aussagekräftig. A/THR-Status ist ohne Profil unb… | moved |
| `Tools/AtcControllerTools.cs:88` | text | Die Sitzung beginnt mit dem ersten Funkspruch (atc_transmit) oder atc_control start.  | moved |
| `Tools/AtcControllerTools.cs:89` | text | Eine gesicherte Sitzung desselben Flugs wird dabei fortgesetzt. | moved |
| `Tools/AtcTools.cs:41` | text | Nicht jede Freigabe setzt die Variable – im Testflug blieben IFR und LANDING auf 0. | moved |
| `Tools/AtcTools.cs:57` | text | L/C/R ist über SimConnect nicht verfügbar und passt zu keiner Bahn im SimBrief-Plan. | moved |
| `Tools/FlightPlanTools.cs:103` | text | Keine Position vom Sim. | moved |
| `Tools/FlightPlanTools.cs:110` | text | Der SimBrief-Plan enthält keine verwertbare Route. | moved |
| `Tools/FlightPlanTools.cs:171` | text | Das FMS fliegt einen Punkt an, der nicht im SimBrief-Plan steht – nächster Fix und Distanzen kommen aus dem… | moved |
| `Tools/FlightPlanTools.cs:172` | text | Im SID/STAR- oder Flughafenbereich weicht die Flugbahn oft vom Plan ab (Vektoren). | moved |
| `Tools/InputTools.cs:47` | text | Eingaben sind gesperrt – der Pilot kann sie im Kopilot-Dashboard freigeben. | moved |
| `Tools/InputTools.cs:72` | text | Parameter 'action' fehlt – siehe list_actions. | moved |
| `Tools/InputTools.cs:119` | text | A confirmation or state word (confirm, check, on, off, avail, läuft) or the action's name is enough, as lon… | kept |
| `Tools/InputTools.cs:143` | text | Interner Fehler in {tool}: {ex.Message} | moved |
| `Tools/TrafficTools.cs:51` | text | Radius auf {Nm(TrafficService.MaxRadiusNm)} NM (200 km) begrenzt – mehr nimmt SimConnect nicht an. | moved |
| `Tools/TrafficTools.cs:64` | text | Kein Flug geladen (Hauptmenü oder Ladebildschirm). | moved |
| `Tools/TrafficTools.cs:128` | text | Eigene Position unbekannt – Verkehr kann nicht relativ zum Flugzeug berechnet werden. | moved |
| `Tools/TrafficTools.cs:129` | text | MSFS hat nicht auf die Verkehrsabfrage geantwortet. Läuft ein Flug? Sonst kopilot_status prüfen. | moved |
| `Tools/TrafficTools.cs:134` | text | MSFS hat nicht vollständig geantwortet – evtl. fehlen Ziele. | moved |
| `Tools/TrafficTools.cs:140` | text | Kein Verkehr im Umkreis von {radius} NM. Falls das nicht stimmen kann: KI-Verkehr bzw. Multiplayer ist im S… | moved |
| `Tools/TrafficTools.cs:144` | text | Kein Verkehr in der Luft im Umkreis von {radius} NM (nur Flugzeuge am Boden). | moved |
| `Tools/MonitoringTools.cs:43` | text | Für dieses Flugzeug sind keine ECAM-Zeilen im Profil hinterlegt – nur Master Warning/Caution. | moved |

## Monitoring

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Monitoring/MonitoringService.cs:91` | log | Monitoring ist ausgeschaltet (Kopilot:Monitoring:Enabled). | moved |
| `Monitoring/MonitoringService.cs:95` | log | Monitoring aktiv ({Interval} ms, {Rules} Regeln). | moved |
| `Monitoring/MonitoringService.cs:153` | log | Fehler im Monitoring | moved |
| `Monitoring/MonitorEngine.cs:151` | text | Kein Flug geladen. | moved |
| `Monitoring/MonitorEngine.cs:151` | text | Keine Verbindung zum Sim. | moved |
| `Monitoring/MonitorEngine.cs:191` | text | Sim läuft wieder. | moved |
| `Monitoring/MonitorEngine.cs:235` | text | Fahrwerk nicht eingefahren bei {Kt(ias)}, VLE {Kt(vle)}. | moved |
| `Monitoring/MonitorEngine.cs:240` | text | , CultureInfo.InvariantCulture)}: {Kt(ias)} über VFE {Kt(vfe)}. | moved |
| `Monitoring/MonitorEngine.cs:252` | text | {Kt(ias)} über VMO {Kt(vmo)}. | moved |
| `Monitoring/MonitorEngine.cs:255` | text | Overspeed-Warnung des Sims bei {Kt(ias)}. | moved |
| `Monitoring/MonitorEngine.cs:260` | text | Stall-Warnung bei {Kt(s.IasKt)}. | moved |
| `Monitoring/MonitorEngine.cs:273` | text | Sinkrate {Math.Round(-vs):0} ft/min bei {Math.Round(ra):0} ft Radiohöhe. | moved |
| `Monitoring/MonitorEngine.cs:409` | text | , CultureInfo.InvariantCulture)} ft darüber | moved |
| `Monitoring/MonitorEngine.cs:411` | text | gleiche Höhe | moved |
| `Monitoring/MonitorEngine.cs:421` | text | )}auf {target.Clock} Uhr, {target.DistanceNm.ToString( | moved |
| `Monitoring/MonitorEngine.cs:627` | text | Fahrwerk nicht unten | moved |
| `Monitoring/MonitorEngine.cs:637` | text | Ground Spoiler nicht armed | moved |
| `Monitoring/MonitorEngine.cs:649` | text | 2000 ft: Fahrwerk unten, Landeklappen gesetzt | moved |
| `Monitoring/MonitorEngine.Callouts.cs:104` | text | Positive Steigrate nach dem Abheben. | moved |
| `Monitoring/MonitorEngine.Callouts.cs:121` | text | Ground Spoiler 3 s nach dem Aufsetzen nicht ausgefahren. | moved |
| `Monitoring/MonitorEngine.Callouts.cs:148` | text | Verzögerung spürbar. | moved |
| `Monitoring/MonitorEngine.Callouts.cs:188` | text | Transition Altitude {ta:0} ft – Höhenmesser auf STD. | moved |
| `Monitoring/MonitorEngine.Callouts.cs:196` | text | Transition Level FL{tl / 100:000} – Höhenmesser auf QNH. | moved |
| `Monitoring/MonitorEngine.Callouts.cs:337` | text | 100 ft über Minimum ({m:0} ft). | moved |
| `Monitoring/MonitorEngine.Callouts.cs:361` | text | Radiohöhe {ft:0} ft. | moved |
| `Monitoring/MonitorEngine.Taxi.cs:86` | text | Am Boden mit {Kt(gs)} Groundspeed, Grenze {Kt(limit)}. | moved |
| `Monitoring/MonitorEngine.Fuel.cs:96` | text | Überwachungsbeginn | moved |
| `Monitoring/MonitorEngine.Fuel.cs:142` | text | Abweichung seit {baseline.Label} um {FuelCheckText.Kg(-change)} schlechter geworden (Schwelle {FuelCheckTex… | moved |
| `Monitoring/MonitorEngine.Fuel.cs:207` | text | der letzten Warnung | moved |
| `Monitoring/MonitorEngine.Fuel.cs:274` | text | der Treibstoffänderung | moved |
| `Monitoring/MonitorEngine.Fuel.cs:277` | text | Nachtanken erkannt: {FuelCheckText.Kg(fromKg)} → {FuelCheckText.Kg(toKg)} ({FuelCheckText.SignedKg(toKg - f… | moved |
| `Monitoring/MonitorEngine.Fuel.cs:278` | text | Treibstoff sprunghaft gesunken: {FuelCheckText.Kg(fromKg)} → {FuelCheckText.Kg(toKg)} ({FuelCheckText.Signe… | moved |

## Traffic

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Traffic/TrafficService.cs:104` | log | Verkehrswarnung (TRAFFIC_PROXIMITY) ist aus – keine Verkehrsabfragen im Hintergrund. | moved |

## Dashboard (server)

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Dashboard/DashboardEndpoints.cs:83` | text | Sprachausgabe gibt es nur in der Tray-Instanz (HTTP-Modus). | moved |
| `Dashboard/DashboardEndpoints.cs:144` | text | {VoiceSettingsStore.FileName} nicht geschrieben: {ex.Message} | moved |
| `Dashboard/DashboardEndpoints.cs:148` | text | Dashboard: Sprachschleife {(saved.Enabled ?  | moved |
| `Dashboard/DashboardEndpoints.cs:192` | text | Die Sprachschleife gibt es nur in der Tray-Instanz (HTTP-Modus). | moved |

## Input module

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Input/ActionSafety.cs:64` | text | FCU-Höhe | moved |
| `Input/ActionSafety.cs:106` | text | Event ohne Namen | moved |
| `Input/ActionSafety.cs:117` | text | {n} ist gesperrt (Triebwerk, Feuer, Treibstoff, Schub, MCDU, Sim-Steuerung) | moved |
| `Input/ActionSafety.cs:122` | text | {n} steht nicht auf der Liste erlaubter Events | moved |
| `Input/ActionSafety.cs:127` | text | Stromkreis {data} ist nicht freigegeben (nur Lichtstromkreise) | moved |
| `Input/ActionSafety.cs:138` | text | Variable ohne Namen | moved |
| `Input/ActionSafety.cs:144` | text | {n}: Es dürfen nur L:-Variablen geschrieben werden | moved |
| `Input/ActionSafety.cs:148` | text | {n} ist gesperrt (Triebwerk, Feuer, Treibstoff, MCDU, Sim-Steuerung; Fahrwerk/Klappen/Spoiler nur über ihre… | moved |
| `Input/ActionSafety.cs:157` | text | Bereich {action.Area} ist gesperrt (Triebwerk, Treibstoff, Feuer, Hydraulik, IDG – nie über Kopilot) | moved |
| `Input/ActionSafety.cs:161` | text | {action.Id} ist gesperrt (Triebwerk, Treibstoff, Feuer, Hydraulik, IDG – nie über Kopilot) | moved |
| `Input/ActionSafety.cs:184` | text | {raw.Trim()} ({what}) nur unter {string.Join( | moved |
| `Input/ActionSafety.cs:184` | text | , ids)} – dort greifen die Sicherheitssperren | moved |
| `Input/ActionSafety.cs:237` | text | {step.Event} braucht die Sperre "{needed}" | moved |
| `Input/ActionSafety.cs:315` | text | Unklar, ob das Flugzeug am Boden ist – gesperrt. | moved |
| `Input/ActionSafety.cs:325` | text | Nur am Boden möglich. | moved |
| `Input/ActionSafety.cs:326` | text | Unklar, ob das Flugzeug am Boden ist – gesperrt. | moved |
| `Input/ActionSafety.cs:345` | text | Im Profil fehlt VLO/VLE – Fahrwerk nur mit Rückfrage. | moved |
| `Input/ActionSafety.cs:350` | text | Fahrwerk gesperrt: {ias:0} kt über {label} {limit:0} kt. | moved |
| `Input/ActionSafety.cs:363` | text | Klappenstellung fehlt – gesperrt. | moved |
| `Input/ActionSafety.cs:374` | text | Im Profil fehlt die VFE für Flaps {option} – nur mit Rückfrage. | moved |
| `Input/ActionSafety.cs:378` | text | Flaps {option} gesperrt: {ias:0} kt über VFE {vfe:0} kt. | moved |
| `Input/ActionPlanner.cs:23` | text | {action.Id} hat im Profil keine Schritte. | moved |
| `Input/ActionPlanner.cs:34` | text | {action.Id} braucht einen Wert: {Describe(p)}. | moved |
| `Input/ActionPlanner.cs:73` | text | {action.Id}: Jeder Schritt braucht genau eines von "event" oder "set". | moved |
| `Input/ActionPlanner.cs:78` | text | {action.Id}: Kein Wert für "{option}" im Schritt {step.Event ?? step.Set}. | moved |
| `Input/ActionPlanner.cs:87` | text | {action.Id}: Kein Vergleichswert für "{option}". | moved |
| `Input/ActionPlanner.cs:103` | text | {action.Id}: Wert {scaled} passt nicht in ein Event. | moved |
| `Input/ActionPlanner.cs:125` | text | {action.Id}: Für "{option}" ist im Profil kein Schritt hinterlegt. | moved |
| `Input/ActionPlanner.cs:133` | text | {action.Id}: Keine Prüfung für "{option}" im Profil. | moved |
| `Input/ActionPlanner.cs:156` | text | {action.Id}: Keine Bedingung für "{option}". | moved |
| `Input/ActionPlanner.cs:159` | text | {r.Variable} passt nicht. | moved |
| `Input/ActionPlanner.cs:246` | text | {range}{unit} oder FLxxx | moved |
| `Input/ActionPlanner.cs:279` | text | "{input.Trim()}" gibt es nicht. Möglich: {string.Join( | moved |
| `Input/ActionPlanner.cs:292` | text | Squawk "{text}" ungültig – 4 Ziffern von 0 bis 7. | moved |
| `Input/ActionPlanner.cs:307` | text | "{input.Trim()}" ist keine Zahl. | moved |
| `Input/ActionPlanner.cs:318` | text | {Num(value)}{unit} liegt außerhalb {Describe(p)}. | moved |
| `Input/ActionPlanner.cs:326` | text | {Num(value)}{unit} passt nicht ins Raster von {Num(step)}{unit}. | moved |
| `Input/ConfigVerifier.cs:27` | text | )} wird nachgelesen – Callout "{SuccessCallout}" bei erreichter Stellung,  | moved |
| `Input/ConfigVerifier.cs:28` | text | sonst nach {Timeout.TotalSeconds:0} s "{FailCallout}". | moved |
| `Input/ConfigVerifier.cs:132` | text | Durch einen neuen Befehl ersetzt. | moved |
| `Input/ConfigVerifier.cs:175` | text | Fahrwerk unten und verriegelt ({values.Length}/{values.Length}). | moved |
| `Input/ConfigVerifier.cs:183` | text | Fahrwerksstellung nicht lesbar – keine Ansage. Profil: verification.gear prüfen. | moved |
| `Input/ConfigVerifier.cs:187` | text | Fahrwerk nach {job.Timeout.TotalSeconds:0} s nicht {(down ?  | moved |
| `Input/ConfigVerifier.cs:252` | text | Klappenstellung nicht lesbar – keine Ansage. Profil: verification.flaps prüfen. | moved |
| `Input/ConfigVerifier.cs:256` | text | Klappen {job.Target} nach {job.Timeout.TotalSeconds:0} s nicht erreicht ({(moved ?  | moved |
| `Input/ConfigVerifier.cs:282` | text | (keine Ansage) | moved |
| `Input/InputService.cs:91` | text | bestätigt | kept |
| `Input/InputService.cs:91` | text | bestätige | kept |
| `Input/InputService.cs:91` | text | bestätigen | kept |
| `Input/InputService.cs:97` | text | nicht | kept |
| `Input/InputService.cs:108` | text | läuft | kept |
| `Input/InputService.cs:115` | text | noch | kept |
| `Input/InputService.cs:224` | text | Aktion "{actionId}" gibt es für dieses Flugzeug nicht. | moved |
| `Input/InputService.cs:228` | text | ausgeführt | moved |
| `Input/InputService.cs:234` | text | Nur speichern, wenn der Pilot klar bestätigt, dass es im Cockpit funktioniert hat (z. B. "check", "confirm"… | moved |
| `Input/InputService.cs:240` | text | Diese Aktion wurde in den letzten 15 Minuten nicht ausgeführt – erst ausführen, dann im Cockpit prüfen. | moved |
| `Input/InputService.cs:245` | text | Das Flugzeug hat den Wert bei der letzten Ausführung nicht übernommen – erst die Ursache klären. | moved |
| `Input/InputService.cs:251` | text | geprüft | moved |
| `Input/InputService.cs:251` | text | vom Piloten im Cockpit bestätigt | moved |
| `Input/InputService.cs:254` | text | Als geprüft gespeichert. | moved |
| `Input/InputService.cs:284` | log | Eingabe: prepare_action {Action} = {Value} vom Client abgebrochen | moved |
| `Input/InputService.cs:285` | text | Aufruf abgebrochen – es wurde keine Aktion vorbereitet. Neu mit prepare_action. | moved |
| `Input/InputService.cs:290` | text | Interner Fehler: {ex.Message} | moved |
| `Input/InputService.cs:298` | text | Eingaben sind gesperrt. Freigabe im Kopilot-Dashboard (Eingabe) oder Kopilot:Input:Enabled. | moved |
| `Input/InputService.cs:322` | text | Aktion "{actionId}" gibt es für {profile.Name} nicht – siehe list_actions. | moved |
| `Input/InputService.cs:353` | text | Inzwischen wurde eine neuere Aktion angefordert – diese Vorbereitung ist verworfen und hat keinen Token. | moved |
| `Input/InputService.cs:357` | text | Aufruf abgebrochen – es wurde keine Aktion vorbereitet. Neu mit prepare_action. | moved |
| `Input/InputService.cs:375` | text | {plan.Readback} · danach Rückfrage: {CautionText(plan)} | moved |
| `Input/InputService.cs:421` | text | Keine offene Aktion – abgelaufen oder schon ausgeführt. Neu mit prepare_action. | moved |
| `Input/InputService.cs:429` | text | Token passt nicht zur offenen Aktion ({_pending.Plan.Summary}). Den Token aus der letzten prepare_action-An… | moved |
| `Input/InputService.cs:437` | text | Pilot hat nicht bestätigt – Aktion verworfen. | moved |
| `Input/InputService.cs:442` | text | Keine klare Bestätigung des Piloten. Readback vorlesen und auf "confirm" warten. | moved |
| `Input/InputService.cs:447` | text | Erst den Readback vorlesen und die Antwort des Piloten abwarten. | moved |
| `Input/InputService.cs:454` | text | schon mit confirm_action verwendet | moved |
| `Input/InputService.cs:482` | text | Flugzeug oder Profil hat gewechselt – bitte neu vorbereiten. | moved |
| `Input/InputService.cs:540` | text | Zustand {requirement.Variable} nicht lesbar. | moved |
| `Input/InputService.cs:579` | text | Zustand {condition.Variable} nicht lesbar. | moved |
| `Input/InputService.cs:628` | log | Eingabe: Prüfung von {Action} fehlgeschlagen: {Message} | moved |
| `Input/InputService.cs:648` | text | war schon so | moved |
| `Input/InputService.cs:649` | text | vom Flugzeug bestätigt | moved |
| `Input/InputService.cs:649` | text | Wert steht (nur Schaltervariable geprüft) | moved |
| `Input/InputService.cs:650` | text | vom Flugzeug zurückgesetzt (zeigt {Shown(after, afterText)}) | moved |
| `Input/InputService.cs:652` | text | war schon so | moved |
| `Input/InputService.cs:669` | log | verified-actions.json nicht geschrieben: {Message} | moved |
| `Input/InputService.cs:675` | text | ausgeführt | moved |
| `Input/InputService.cs:675` | text |  · als geprüft gespeichert | moved |
| `Input/InputService.cs:697` | text | Das Flugzeug hat den Wert kurz übernommen und wieder zurückgesetzt – Voraussetzungen im Cockpit prüfen (z. … | moved |
| `Input/InputService.cs:698` | text | Das Flugzeug hat den Wert (noch) nicht übernommen – bitte im Cockpit prüfen. | moved |
| `Input/InputService.cs:732` | text | nicht nachlesbar | moved |
| `Input/InputService.cs:762` | text | keine Daten | moved |
| `Input/InputService.cs:766` | log | Freigabe/MSA nicht lesbar: {Message} | moved |
| `Input/InputService.cs:813` | text | Inzwischen wurde eine neuere Aktion angefordert. | moved |
| `Input/InputService.cs:816` | text | Inzwischen wurde eine neuere Aktion angefordert – diese Rückfrage ist verworfen. | moved |
| `Input/InputService.cs:836` | text | Rückfrage | moved |
| `Input/InputService.cs:862` | log | Eingabe {Action}: Grenzwert fehlt ({Code}) – {Reason} | moved |
| `Input/InputService.cs:863` | text | Grenzwert fehlt | moved |
| `Input/InputService.cs:910` | text | Keine Verbindung zu MSFS. | moved |
| `Input/InputService.cs:915` | text | Für dieses Flugzeug gibt es kein Profil und damit keine Eingaben. | moved |
| `Input/InputService.cs:921` | text | Kein Flug geladen. | moved |
| `Input/InputService.cs:944` | log | Eingabe: {Action} = {Value} mit Ausnahme abgebrochen | moved |
| `Input/InputService.cs:945` | text | Zeitüberschreitung beim Ausführen. | moved |
| `Input/InputService.cs:945` | text | Interner Fehler: {ex.Message} | moved |
| `Input/InputService.cs:946` | text |  Möglicherweise teilweise ausgeführt – bitte im Cockpit prüfen. | moved |
| `Input/InputService.cs:957` | text |  Möglicherweise teilweise ausgeführt – bitte im Cockpit prüfen. | moved |
| `Input/InputService.cs:993` | text | durch eine neuere prepare_action-Anfrage ersetzt | moved |
| `Input/InputService.cs:994` | text | abgelaufen (keine Bestätigung im Zeitfenster) | moved |
| `Input/InputService.cs:1006` | text | Keine Bestätigung | moved |
| `Input/InputService.cs:1016` | text | Kein Token angegeben – den Token aus der letzten prepare_action-Antwort verwenden. | moved |
| `Input/InputService.cs:1020` | text |  Offen ist jetzt {p.Plan.Summary} – deren Readback dem Piloten vorlesen und seine Antwort abwarten. | moved |
| `Input/InputService.cs:1021` | text |  Neu mit prepare_action. | moved |
| `Input/InputService.cs:1025` | text | Token {t} gehört zu {retired.Summary} – {retired.Fate}.{open} | moved |
| `Input/InputService.cs:1030` | text | Token {t} gehört zu {foreign.Action} in einer anderen Kopilot-Instanz – confirm_action über dieselbe Verbin… | moved |
| `Input/InputService.cs:1057` | log | input-pending.json nicht geschrieben: {Message} | moved |
| `Input/SafetyRules.cs:68` | text | Entführung | moved |
| `Input/SafetyRules.cs:76` | text | nur unter VLO und unter der Höchsthöhe (Profil: maxGearAltitudeFt); danach Callout three green / gear disagree | moved |
| `Input/SafetyRules.cs:77` | text | nur in der Luft; danach Callout gear up / gear disagree | moved |
| `Input/SafetyRules.cs:78` | text | nur unter VFE der Stellung; Callout erst bei erreichter Stellung | moved |
| `Input/SafetyRules.cs:79` | text | ausfahren nicht in den gesperrten Klappenstellungen (Profil: speedbrakeInhibitFlaps) | moved |
| `Input/SafetyRules.cs:80` | text | aus nur am Boden | moved |
| `Input/SafetyRules.cs:81` | text | aus im Flug nur, solange das andere Pack an ist | moved |
| `Input/SafetyRules.cs:82` | text | aus nicht bei APU Bleed an | moved |
| `Input/SafetyRules.cs:83` | text | STBY im Flug nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:84` | text | aus im Flug nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:85` | text | STBY im Flug nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:86` | text | 7500/7600/7700 nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:87` | text | aus im Flug nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:88` | text | unter MSA (Profil: msaSource) oder außerhalb der ATC-Freigabe nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:89` | text | aus im Flug gesperrt, am Boden nur mit Rückfrage | moved |
| `Input/SafetyRules.cs:138` | text | Im Profil fehlt maxGearAltitudeFt – Fahrwerk nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:142` | text | Höhe unbekannt – Fahrwerk gesperrt. | moved |
| `Input/SafetyRules.cs:146` | text | Fahrwerk gesperrt: {alt:0} ft, ausfahren nur unter {maxAlt:0} ft. | moved |
| `Input/SafetyRules.cs:157` | text | Klappenstellung unbekannt – Speedbrake nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:164` | text | Klappen {flaps} und im Profil fehlt speedbrakeInhibitFlaps – Speedbrake nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:179` | text | Unklar, ob das Flugzeug am Boden ist – Batterien bleiben an. | moved |
| `Input/SafetyRules.cs:180` | text | Batterien im Flug ausschalten ist gesperrt – nur am Boden. | moved |
| `Input/SafetyRules.cs:191` | text | {other} ist schon aus – beide Packs aus nur am Boden (Druckverlust). | moved |
| `Input/SafetyRules.cs:194` | text | Zustand von {other} nicht lesbar – im Flug nur mit Rückfrage (beide aus = Druckverlust). | moved |
| `Input/SafetyRules.cs:205` | text | APU Bleed ist an – erst apu.bleed off, dann APU Master aus. | moved |
| `Input/SafetyRules.cs:208` | text | Zustand APU Bleed nicht lesbar – APU Master aus nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:217` | text | Transponder STBY im Flug – ATC sieht keine Höhe/Kennung mehr, TCAS ist aus. | moved |
| `Input/SafetyRules.cs:223` | text | ALT RPTG aus im Flug – ATC und TCAS anderer Flugzeuge sehen unsere Höhe nicht mehr. | moved |
| `Input/SafetyRules.cs:229` | text | TCAS STBY im Flug – keine Verkehrswarnungen und keine Ausweichanweisungen mehr. | moved |
| `Input/SafetyRules.cs:234` | text | Squawk {code} ist der Code für {squawk.Meaning} – löst bei ATC Alarm aus. | moved |
| `Input/SafetyRules.cs:243` | text | Unklar, ob das Flugzeug am Boden ist – ADIRS bleiben an. | moved |
| `Input/SafetyRules.cs:244` | text | ADIRS aus im Flug ist gesperrt – Lage- und Navigationsdaten weg, in der Luft kein neues Ausrichten möglich. | moved |
| `Input/SafetyRules.cs:247` | text | ADIRS aus – danach neu ausrichten (ca. 7–10 min). | moved |
| `Input/SafetyRules.cs:256` | text |  ? 1 : 2)} aus im Flug – der Pilot fliegt dann von Hand (sofern der andere AP nicht an ist). | moved |
| `Input/SafetyRules.cs:261` | text | Autothrust aus im Flug – Schub dann von Hand über die Hebel. | moved |
| `Input/SafetyRules.cs:285` | text | Im Profil fehlt msaSource – FCU-Höhe nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:290` | text | MSA nicht verfügbar ({ctx.Msa?.Problem ??  | moved |
| `Input/SafetyRules.cs:290` | text | }) – FCU-Höhe nur mit Rückfrage. | moved |
| `Input/SafetyRules.cs:295` | text | FCU-Höhe {target.ToString( | moved |
| `Input/SafetyRules.cs:295` | text | , inv)} ft liegt unter der MSA {msa.ToString( | moved |
| `Input/SafetyRules.cs:323` | text | FCU-Höhe {target.ToString( | moved |
| `Input/SafetyRules.cs:323` | text | , inv)} ft liegt außerhalb der Freigabe ({clearedFt.ToString( | moved |
| `Input/InputContext.cs:28` | text | keine MSA-Quelle im Profil | moved |
| `Input/InputContext.cs:38` | text | kein SimBrief-Plan geladen | moved |
| `Input/InputContext.cs:61` | text | Position nicht auf der Route | moved |
| `Input/InputContext.cs:66` | text | {Math.Abs(route.CrossTrackNm):0} nm neben der Route | moved |
| `Input/InputContext.cs:80` | text | keine MORA im SimBrief-Navlog | moved |

## SimConnect

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Sim/TrafficMessages.cs:271` | text | noch keine leere Antwort (dwoutof = 0) empfangen | moved |
| `Sim/TrafficMessages.cs:273` | text | bestätigt: {EmptyReplies} leere Antworten mit dwSize {Range(EmptyMinDwSize, EmptyMaxDwSize)} Byte statt {Le… | moved |
| `Sim/TrafficMessages.cs:278` | text | noch keine Datennachricht empfangen | moved |
| `Sim/TrafficMessages.cs:280` | text | bestätigt: {ShortMessages} von {DataMessages} Datennachrichten zu kurz (kleinster Datenblock {ShortMinPaylo… | moved |
| `Sim/TrafficMessages.cs:281` | text | bisher nicht beobachtet ({DataMessages} Datennachrichten, dwSize {Range(DataMinDwSize, DataMaxDwSize)} Byte) | moved |
| `Sim/SimConnectClient.Facilities.cs:28` | exception | Keine Verbindung zu MSFS. | moved |
| `Sim/SimConnectClient.Facilities.cs:99` | log | Facilities: {Count} Frequenzen für {Icao} | moved |
| `Sim/SimConnectClient.Facilities.cs:112` | exception | Keine Facility-Daten für {active.Icao}. | moved |
| `Sim/SimConnectClient.Facilities.cs:123` | exception | Verbindung zu MSFS getrennt. | moved |
| `Sim/SimConnectClient.Facilities.cs:127` | exception | Verbindung zu MSFS getrennt. | moved |
| `Sim/SimConnectClient.cs:102` | text | Keine Verbindung zu MSFS. | moved |
| `Sim/SimConnectClient.cs:154` | text | Keine Antwort von MSFS – Variablenname oder Einheit vermutlich ungültig. | moved |
| `Sim/SimConnectClient.cs:222` | exception | Keine Verbindung zu MSFS. | moved |
| `Sim/SimConnectClient.cs:247` | exception | MSFS hat den Befehl nicht rechtzeitig angenommen. | moved |
| `Sim/SimConnectClient.cs:282` | log | SimConnect-Bibliothek konnte nicht geladen werden. Siehe README, Abschnitt 'SimConnect-SDK'. | moved |
| `Sim/SimConnectClient.cs:289` | log | Kopilot wartet auf Microsoft Flight Simulator … | moved |
| `Sim/SimConnectClient.cs:327` | log | SimConnect-Verbindung abgebrochen: {Message} | moved |
| `Sim/SimConnectClient.cs:332` | log | Unerwarteter Fehler im SimConnect-Thread – Verbindung wird neu aufgebaut. | moved |
| `Sim/SimConnectClient.cs:372` | log | SimConnect kann nicht gestartet werden: {Message} | moved |
| `Sim/SimConnectClient.cs:389` | log | Fehler beim Schließen der SimConnect-Verbindung | moved |
| `Sim/SimConnectClient.cs:397` | log | Verbindung zu MSFS getrennt. | moved |
| `Sim/SimConnectClient.cs:419` | exception | Verbindung zu MSFS getrennt. | moved |
| `Sim/SimConnectClient.cs:528` | log | Flugzeug: {Title} ({Path}) – kein Profil | moved |
| `Sim/SimConnectClient.cs:584` | log | Verbunden mit {App} {Version} | moved |
| `Sim/SimConnectClient.cs:604` | log | MSFS wurde beendet. | moved |
| `Sim/SimConnectClient.cs:626` | log | Sim läuft weiter. | moved |
| `Sim/TrafficConnection.cs:75` | log | Verkehrs-Nachrichtenlog nicht verfügbar: {Message} | moved |
| `Sim/TrafficConnection.cs:95` | text | Keine Verbindung zu MSFS. | moved |
| `Sim/TrafficConnection.cs:114` | text | Kopilot wird beendet | moved |
| `Sim/TrafficConnection.cs:133` | text | MSFS-Verbindung wurde neu aufgebaut | moved |
| `Sim/TrafficConnection.cs:145` | text | MSFS hat die Verkehrsverbindung beendet. | moved |
| `Sim/TrafficConnection.cs:163` | text | MSFS hat die Verkehrsverbindung beendet. | moved |
| `Sim/TrafficConnection.cs:188` | text | dreimal keine vollständige Antwort | moved |
| `Sim/TrafficConnection.cs:201` | text | Native SimConnect.dll nicht nutzbar: {ex.Message} | moved |
| `Sim/TrafficConnection.cs:202` | log | Verkehrsabfrage nicht möglich – native SimConnect.dll fehlt oder passt nicht (siehe README, 'SimConnect-SDK'). | moved |
| `Sim/TrafficConnection.cs:218` | text | Verkehrsverbindung zu MSFS gerade nicht verfügbar – neuer Versuch in wenigen Sekunden. | moved |
| `Sim/TrafficConnection.cs:226` | text | Verkehrsverbindung zu MSFS nicht möglich (HRESULT 0x{hr:X8}). | moved |
| `Sim/TrafficConnection.cs:246` | text | keine Bestätigung von MSFS | moved |
| `Sim/TrafficConnection.cs:248` | text | MSFS hat die Verkehrsverbindung nicht bestätigt. | moved |
| `Sim/TrafficConnection.cs:257` | log | Eigene SimConnect-Verbindung für Verkehr geöffnet (native SimConnect.dll, Größenprüfung je Nachricht){Log}. | moved |
| `Sim/TrafficConnection.cs:322` | log | SimConnect-Exception auf der Verkehrsverbindung: {Exception} (SendID {SendId}, Index {Index}) | moved |
| `Sim/TrafficConnection.cs:356` | log | Verkehr BYTYPE auffällig ({Flags}): Objekt {ObjectId}, {Entry}/{OutOf}, dwSize {DwSize}, pcbData {Received}… | moved |
| `Sim/TrafficConnection.cs:399` | log | Fehler beim Schließen der Verkehrsverbindung | moved |

## ECAM

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Ecam/EcamCatalog.cs:86` | text | Fehler: {e} | moved |
| `Ecam/EcamCatalog.cs:87` | text | wird geladen | moved |
| `Ecam/EcamCatalog.cs:110` | log | ECAM-Tabelle {Format} konnte nicht geladen werden: {Message} | moved |
| `Ecam/EcamCatalog.cs:122` | text | kein Download und kein Cache | moved |

## Aircraft profiles

| File:line | Kind | Text | Status |
|---|---|---|---|
| `Aircraft/ProfileStore.cs:76` | text | Aktion "{action.Id}" ohne id oder Schritte | moved |
| `Aircraft/ProfileStore.cs:92` | text | Aktion {action.Id}: flapsSpeed braucht eine Auswahl der Klappenstellung | moved |
| `Aircraft/ProfileStore.cs:120` | text | limits.vloKt/vleKt fehlt – gear.down nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:125` | text | limits.maxGearAltitudeFt fehlt – gear.down in der Luft nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:134` | text | limits.vfeKt fehlt für {flaps.Id} {string.Join( | moved |
| `Aircraft/ProfileStore.cs:134` | text | , missing)} – nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:140` | text | limits.speedbrakeInhibitFlaps fehlt – Speedbrake mit ausgefahrenen Klappen nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:147` | text | limits.msaSource fehlt – FCU-Höhe nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:151` | text | limits.msaSource "{limits.MsaSource}" unbekannt (möglich: {string.Join( | moved |
| `Aircraft/ProfileStore.cs:151` | text | , AircraftLimits.MsaSources)}) – FCU-Höhe nur mit Rückfrage | moved |
| `Aircraft/ProfileStore.cs:195` | text | Profilordner nicht gefunden: {DirectoryPath} | moved |
| `Aircraft/ProfileStore.cs:202` | log | {Count} Flugzeugprofil(e) geladen aus {Dir} | moved |
| `Aircraft/ProfileStore.cs:210` | log | Profil: Grenzwert fehlt – {Warning} | moved |

## Dashboard (dashboard.html)

| Line | Kind | Text | Status |
|---|---|---|---|
| 433 | html | Verbinde … | moved |
| 435 | html | Sim pausiert | moved |
| 438 | attr | Hält Position, Höhe, ATC-Station, Frequenzen und die letzten Funksprüche jetzt fest – dann beschreiben und … | moved |
| 438 | html | Fehler melden | moved |
| 439 | html | Einstellungen | moved |
| 442 | attr | Master Warning – Klick quittiert das Blinken | moved |
| 443 | attr | Master Caution – Klick quittiert das Blinken | moved |
| 453 | html | Schub | moved |
| 460 | html | FMA-Verlauf | moved |
| 465 | attr | Flugdaten | moved |
| 466 | html | Flugdaten | moved |
| 469 | html | Höhe | moved |
| 471 | html | Kurs | moved |
| 476 | attr | Ansagen | moved |
| 477 | html | Ansagen | moved |
| 479 | html | Ansagen aus | moved |
| 480 | attr | Stimme im Browser | moved |
| 481 | html | Stimme | moved |
| 483 | attr | Eingaben: Kopilot darf nach Readback und Bestätigung Schalter bedienen | moved |
| 483 | html | Eingabe gesperrt | moved |
| 489 | html | Windows-Stimme | moved |
| 492 | html | Windows-Stimme | moved |
| 493 | html | Kokoro-Stimme | moved |
| 494 | html | Ausgabe | moved |
| 495 | html | Lautstärke | moved |
| 496 | html | Tempo | moved |
| 497 | html | Ansagen | moved |
| 498 | html | alle | moved |
| 500 | html | nur Warnings | moved |
| 502 | html | Radiohöhen, Minimum und Retard auch von Kopilot (nur für Flugzeuge ohne eigene Ansagen) | moved |
| 506 | html | Sprachschleife (Mikrofon → Kopilot und ATC) | moved |
| 507 | html | Sprachschleife an – Mikrofon dauerhaft offen, Befehle und Funk ohne Claude | moved |
| 508 | html | Mikrofon | moved |
| 509 | attr | Spitzenpegel der letzten Sekunden | moved |
| 509 | html | Pegel | moved |
| 516 | html | Abbrechen | moved |
| 519 | html | Verbinde mit Kopilot … | moved |
| 528 | attr | Warnungen | moved |
| 529 | html | Warnungen | moved |
| 537 | attr | Route | moved (word also used in English) |
| 538 | html | Route | moved (word also used in English) |
| 538 | html | Plan laden | moved |
| 541 | html | Fuel Check – Abweichung gegen Plan | moved |
| 542 | attr | Fuel Check: Abweichung gegen Plan entlang der Route. Pfeiltasten wählen einen Check. | moved |
| 547 | html | Zeit | moved |
| 547 | html | Ort | moved |
| 547 | html | Gegen Plan | moved |
| 553 | attr | Triebwerke | moved |
| 554 | html | Triebwerke | moved |
| 559 | attr | Konfiguration | moved |
| 560 | html | Konfiguration | moved |
| 564 | attr | ATC und Wetter | moved |
| 565 | html | ATC und Wetter | moved |
| 573 | html | Einrichtung und Diagnose | moved |
| 574 | html | Hier prüfst du, welche Werte aus dem Flugzeugprofil kommen. „Ungeprüft“ heißt: Der Variablenname stammt aus… | moved |
| 577 | html | SimBrief-Plan neu laden | moved |
| 586 | html | Wert | moved |
| 594 | html | Rohdaten: | moved |
| 594 | html | Systeme | moved |
| 594 | html | Warnungen | moved |
| 594 | html | Meldungen | moved |
| 594 | html | Fortschritt | moved |
| 594 | html | Profil | moved (word also used in English) |
| 602 | html | Einstellungen | moved |
| 603 | html | Zurück zum Cockpit | moved |
| 615 | html | Claude Desktop und Claude Code auf diesem PC verbinden sich lokal. Für claude.ai, die Handy-App und den Spr… | moved |
| 620 | html | nicht geprüft | moved |
| 620 | html | Prüfen | moved |
| 623 | html | Öffentliche Adresse (Tunnel) | moved |
| 626 | html | Freigabe-Passwort für den Claude-Connector | moved |
| 629 | html | Passwort entfernen (Connector aus) | moved |
| 631 | html | Speichern | moved |
| 635 | html | Bearer-Token für Claude Code und andere Clients | moved |
| 637 | html | Neu erzeugen | moved |
| 638 | html | Entfernen | moved |
| 646 | html | Kopieren | moved |
| 648 | html | eintragen. Der alte Token gilt bis zum Neustart. | moved |
| 654 | html | Für Route, Fortschritt, Fuel Check und ATC. Username oder Pilot-ID (simbrief.com → Account Settings) – die … | moved |
| 661 | html | Speichern und Plan laden | moved |
| 669 | html | Mikrofon | moved |
| 670 | html | Für die Sprachschleife (Befehle und Funk ohne Claude). Fehlt das Gerät (VR-Brille aus), nimmt Kopilot das W… | moved |
| 671 | html | Aufnahmegerät | moved |
| 672 | attr | Spitzenpegel der letzten Sekunden | moved |
| 672 | html | Pegel | moved |
| 678 | html | Module | moved (word also used in English) |
| 679 | html | Monitoring und ATC lesen ihre Einstellung beim Start – nach dem Umschalten Kopilot neu starten. Eingabe und… | moved |
| 681 | html | Callouts und Warnungen als Pilot Monitoring | moved |
| 684 | html | Eingabe | moved |
| 684 | html | Kopilot bedient Schalter – nur nach Readback und Bestätigung | moved |
| 685 | attr | Eingabe | moved |
| 687 | html | Fluglotsen von Kopilot (Funk, Übergaben, Freigaben) | moved |
| 690 | html | Mikrofon dauerhaft offen, Befehle und Funk direkt an Kopilot | moved |
| 701 | html | Fehler melden | moved |
| 703 | html | Was stimmt nicht? | moved |
| 704 | html | Titel | moved |
| 705 | html | Was ist falsch, was wäre richtig? | moved |
| 706 | attr | z. B. Sim-ATC übergibt erst hier an Montreal Center, Frequenz 132.25 – Kopilot schon 260 NM vorher bei HOIST. | moved |
| 708 | html | Kontext, der mitgeschickt wird | moved |
| 712 | html | Abbrechen | moved |
| 719 | html | Kopilot antwortet nicht. Läuft Kopilot noch? Das Symbol im Infobereich zeigt es – sonst Kopilot.exe neu sta… | moved |
| 729 | js | keine Daten | moved |
| 729 | js | nicht verbunden | moved |
| 774 | js | Wartet auf MSFS | moved |
| 777 | js | MSFS im Hauptmenü | moved |
| 777 | js | MSFS verbunden | moved |
| 782 | js | Flugzeug unbekannt | moved |
| 782 | js | <span class="tag">Profil ${esc(ac.profile)}</span> | moved |
| 782 | js | <span class="tag">ohne Profil</span> | moved |
| 789 | js | <strong>Warte auf Microsoft Flight Simulator.</strong> Kopilot verbindet sich automatisch, sobald MSFS läuft. | moved |
| 792 | js | <strong>Kein Flug geladen.</strong> Sobald ein Flug läuft, erscheinen hier die Werte. | moved |
| 838 | js | Für dieses Flugzeug sind keine FMA-Variablen im Profil – AP-Status aus den generischen SimVars, Schub und M… | moved |
| 844 | js | <li><span class="t"></span><span class="at">Noch keine FMA-Wechsel aufgezeichnet.</span></li> | moved |
| 852 | js |  aus Mach | moved |
| 852 | js |  gewählt | moved |
| 911 | js | Keine Meldungen | moved |
| 911 | js | Monitoring ist aus | moved |
| 912 | js | Einschalten unter Einstellungen → Module, danach Kopilot neu starten. | moved |
| 913 | js | Ruhezeit nach dem Laden: noch ${d.monitoring.quietForSec} s. | moved |
| 914 | js | Monitoring läuft. Phase: ${phase}. | moved |
| 924 | js | <li><span class="t"></span><span class="ms">Noch keine Meldungen in dieser Sitzung.</span></li> | moved |
| 939 | js | <option value="">Keine englische Stimme</option> | moved |
| 960 | js | Ansagen an | moved |
| 960 | js | Ansagen aus | moved |
| 961 | js | Kopilot spricht über Windows | moved |
| 972 | js | <option value="">Keine Stimme gefunden</option> | moved |
| 975 | js | <option value="">Windows-Standardgerät</option> | moved |
| 983 | js | <option value="">keine Stimmen gefunden</option> | moved |
| 990 | js | Kokoro lädt ${Math.round((k.progress \|\| 0) * 100)} % von ~310 MB | moved |
| 992 | js | Gerade liest der Browser vor. | moved |
| 1064 | js | lädt Modelle | moved |
| 1064 | js | hört zu | moved |
| 1064 | js | nicht verfügbar | moved |
| 1077 | js | <option value="">Windows-Standardgerät${st.defaultMicrophone ? " (" + esc(st.defaultMicrophone) + ")" : ""}… | moved |
| 1079 | js | <option value="${esc(configured)}">${esc(configured)} (nicht gefunden)</option> | moved |
| 1085 | js | Mikrofon: ${st.microphone} | moved |
| 1086 | js | Modell lädt ${Math.round(st.downloadProgress * 100)} % | moved |
| 1088 | js | ${st.recognized}/${st.utterances} Sätze erkannt${st.dropped ?  | moved |
| 1092 | js | Mikrofon „${st.microphoneConfigured}“ nicht gefunden – es läuft das Standardgerät. | moved |
| 1130 | js | <li class="msg dim">Für dieses Flugzeug liest Kopilot kein ECAM. Master Warning und Master Caution erschein… | moved |
| 1150 | js | <p class="empty">SimBrief ist nicht eingerichtet. Trag unter <a href="#einstellungen">Einstellungen → SimBr… | moved |
| 1156 | js | <p class="empty">Noch kein Flugplan geladen.${p.error ? " " + esc(p.error) : ""} Erzeuge den OFP auf simbri… | moved |
| 1164 | js | <p class="empty">Position noch nicht auf der Route – sobald der Sim Werte liefert, erscheint hier der Forts… | moved |
| 1185 | js |  der Route | moved |
| 1185 | js | auf der Route | moved |
| 1188 | js | <p class="empty">Das FMS fliegt einen Punkt an, der nicht im SimBrief-Plan steht – Route und Distanzen komm… | moved |
| 1191 | js | SimBrief-Plan geladen. | moved |
| 1197 | js | Treibstoff geändert | moved |
| 1241 | js | <text x="${L - 8}" y="${yy}" text-anchor="end" dominant-baseline="middle">${v === 0 ? "Plan" : esc(kgSigned… | moved |
| 1250 | js | <text x="${(x(e.nm) + (right ? -6 : 6)).toFixed(1)}" y="${T + 10}" text-anchor="${right ? "end" : "start"}"… | moved |
| 1301 | js |  gegen Plan | moved |
| 1382 | js |   aus | moved |
| 1386 | js | Fuel Flow aus dem Profil | moved |
| 1396 | js | fährt (${c.gearPct} %) | moved |
| 1412 | js | <div class="rwy">${esc(a.runway.ident)}</div><div class="rwy-sub">${esc(a.runway.spoken)}${a.runway.airport… | moved |
| 1413 | js | <div class="rwy-sub">Keine Bahn vom Ingame-ATC zugewiesen.</div> | moved |
| 1433 | js | Profil  | moved |
| 1434 | js | Keine Verbindung zum Sim. | moved |
| 1435 | js | Für „${ac.title \|\| "dieses Flugzeug"}“ gibt es kein Profil. Pfad: ${ac.path \|\| "unbekannt"} | moved |
| 1438 | js | <tr><td>${esc(x.key)}</td><td class="var">${esc(x.variable)}</td><td><span class="state ${x.verified ? "ok"… | moved |
| 1458 | js | Kopilot hat nicht geantwortet. | moved |
| 1462 | js | Profile neu geladen. | moved |
| 1463 | js | SimBrief-Plan geladen. | moved |
| 1477 | js | Eingabe frei | moved |
| 1477 | js | Eingabe gesperrt | moved |
| 1481 | js | Kopilot darf ${inp.actions} Aktionen nach Bestätigung ausführen – klicken zum Sperren | moved |
| 1481 | js | Kopilot darf nichts bedienen – zweimal klicken zum Freigeben | moved |
| 1487 | js | Wartet auf „confirm“ – noch ${p.expiresInSec} s · ${p.action}${p.value ? " = " + p.value : ""}${p.other ? "… | moved |
| 1558 | js | Kopilot antwortet nicht | moved |
| 1567 | js | Kopilot hat nicht geantwortet. | moved |
| 1573 | js |  (wird beim ersten Speichern angelegt) | moved |
| 1577 | js | Gespeichert, aber erst nach einem Neustart wirksam: ${s.restart.changes.join(", ")}. | moved |
| 1578 | js |  Neustart über das Symbol im Infobereich (Beenden) und Kopilot.exe. | moved |
| 1583 | js | Umgebungsvariablen überschreiben die Datei: ${s.environmentOverrides.join(", ")} – Änderungen an diesen Wer… | moved |
| 1590 | js | nach Neustart aktiv | moved |
| 1591 | js |  <span class="dim">braucht öffentliche Adresse und Passwort (mind. ${c.consentPassword.minLength} Zeichen)<… | moved |
| 1594 | js | neu – nach Neustart aktiv | moved |
| 1594 | js | entfernt – nach Neustart aus | moved |
| 1596 | js | <span class="dim">nicht geprüft</span> | moved |
| 1601 | js | gesetzt – leer lassen, um es zu behalten | moved |
| 1601 | js | nicht gesetzt (mind. ${c.consentPassword.minLength} Zeichen) | moved |
| 1609 | js | Plan geladen: <b>${esc([b.plan.callsign, b.plan.origin && b.plan.destination ? b.plan.origin + " → " + b.pl… | moved |
| 1610 | js | Noch kein Plan geladen. | moved |
| 1615 | js | ${x.enabled ? "Eingeschaltet" : "Ausgeschaltet"} – gilt nach dem Neustart, bis dahin ${x.running ? "läuft e… | moved |
| 1620 | js | Freigegeben – Kopilot darf nach Bestätigung schalten. | moved |
| 1623 | js | Nur in der Tray-Instanz. | moved |
| 1648 | js | <span class="dim">prüfe …</span> | moved |
| 1649 | js | Kopilot hat nicht geantwortet. | moved |
| 1665 | js | Lade Plan … | moved |
| 1668 | js | Gespeichert, Plan geladen. | moved |
| 1668 | js | Kein Plan gefunden. | moved |
| 1669 | js | Kopilot hat nicht geantwortet. | moved |
| 1701 | js | <option>nicht verfügbar</option> | moved |
| 1702 | js | Kopilot hat nicht geantwortet. | moved |
| 1712 | js | <option value="">Windows-Standardgerät${st.defaultMicrophone ? " (" + esc(st.defaultMicrophone) + ")" : ""}… | moved |
| 1714 | js | <option value="${esc(configured)}">${esc(configured)} (nicht gefunden)</option> | moved |
| 1719 | js | nur bei eingeschalteter Sprachschleife | moved |
| 1721 | js | nimmt auf von ${st.microphone} | moved |
| 1722 | js | zuletzt gehört: „${st.lastTranscript}“ | moved |
| 1724 | js | „${st.microphoneConfigured}“ nicht gefunden – es läuft das Standardgerät. | moved |
| 1729 | js | Kopilot hat nicht geantwortet. | moved |
| 1758 | js | Kopilot wurde neu gestartet. | moved |
| 1780 | js | Sim nicht verbunden | moved |
| 1824 | js | Kopilot hat nicht geantwortet. | moved |
| 1840 | js | ${openGithub ? "GitHub sollte sich in einem neuen Tab geöffnet haben – dort nur noch absenden. Kein Tab? " … | moved |
| 1841 | js |  <span class="dim">(Link gekürzt – vollständiger Text in der lokalen Kopie)</span> | moved |
| 1843 | js | <button class="btn" type="button">Text kopieren</button> <span class="dim">Labels: ${esc(j.labels.join(", "… | moved |
