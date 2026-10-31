[English](FAQ) | **Deutsch**

# FAQ

### Brauche ich Claude, um Duopilo zu nutzen?

Nein. Callouts, Monitoring, Sprachausgabe, Sprachschleife, Checklisten, Eingaben und ATC laufen alle in Duopilo selbst, lokal. Claude ist optional – es bringt einen PM dazu, mit dem du reden kannst, der alles ablesen und einordnen kann. Nur der optionale Smalltalk nutzt ein Sprachmodell, und welches, entscheidest du.

### Kostet das etwas?

Duopilo selbst ist kostenlos. Claude (mit einem Claude-Plan) oder Smalltalk über einen API-Schlüssel rechnet der jeweilige Anbieter ab. Ollama auf deinem eigenen zweiten Rechner kostet nichts.

### Welcher Simulator, welche Flugzeuge?

MSFS 2024 unter Windows. Generische Werte gehen mit jedem Flugzeug; Airliner-Details (FMA, ECAM, Eingaben) brauchen ein Profil – mitgeliefert für FlyByWire A32NX und A380X, Headwind A330-900neo und iniBuilds A321LR. Siehe [Flugzeugprofile](DE-Flugzeugprofile).

### MSFS 2020?

Nicht unterstützt und nicht getestet. Duopilo ist gegen das MSFS-2024-SDK gebaut.

### Kann Duopilo das Flugzeug fliegen?

Nein. Es ist ein Pilot Monitoring, kein Autopilot. Schalter bedient es nur auf deine Ansage und nach einem Readback, und Triebwerke, Schub, Sprit, Feuer und Hydraulik rührt es nie an. Siehe [Eingabe-Modul](DE-Eingabe-Modul).

### Geht das in VR?

Darauf ist es ausgelegt: Die Sprachschleife braucht kein Push-to-Talk, und ein Hotkey zum Stummschalten (`Ctrl+Alt+K`) gilt systemweit. Ist das Mikrofon deines VR-Headsets noch nicht da, nimmt Duopilo das Standardgerät und wechselt, sobald es auftaucht. Ein kompletter Testflug in VR steht noch auf der [Roadmap](DE-Roadmap).

### Bremst es MSFS aus?

Duopilo hält die CPU-Last bewusst niedrig: Kokoro und Supertonic rechnen mit 2 Threads, Whisper mit 4 Threads und erst, nachdem du gesprochen hast, und während der Sim pausiert ist, wird das Mikrofon nicht ausgewertet. Ein lokales Sprachmodell gibt es nicht. Whisper auf der GPU geht, kann in VR aber Ruckler kosten.

### Kann ich Deutsch sprechen?

Befehle, Abfragen und Checklisten: ja, Deutsch und Englisch werden beide verstanden. Callouts, Checklisten, FMA-Ansagen, Warnungen und Funk kommen immer auf Englisch zurück (Crew-Sprache). Dashboard und Smalltalk können Deutsch sein.

### Warum sind Callouts immer Englisch?

Wie in der SOP einer echten Crew: eine Cockpitsprache für alles, was sicherheitsrelevant ist. Dashboard, Tool-Meldungen und Smalltalk folgen deiner Spracheinstellung.

### Verträgt es sich mit BeyondATC oder SayIntentions?

Externe ATC-Programme sind über SimConnect nicht sichtbar. Du kannst sie fürs ATC nutzen und das ATC von Duopilo abschalten (`Duopilo:Atc:Enabled: false`); Monitoring und Callouts laufen weiter.

### Und das eigene ATC von MSFS?

Duopilo liest die Bahn, die das MSFS-ATC zuweist (`get_atc`, Callout „Runway two three left assigned“). Nutzt du das ATC von Duopilo, schalte das MSFS-ATC stumm. Achtung: MSFS kann trotzdem Squawk und COM1 ändern – Duopilo merkt das und bittet dich, es zu korrigieren.

### Warum hat mein First Officer jeden Flug einen anderen Namen?

Jeder SimBrief-Plan ist ein neuer Flug mit neuer Crew. Du willst einen festen Namen? **Einstellungen → First Officer und Smalltalk**.

### Wo liegen meine Einstellungen? Überschreibt ein Update sie?

Deine Einstellungen liegen in `appsettings.Local.json` und in `%LOCALAPPDATA%\Duopilo`. Updates (Installer oder `update.cmd`) überschreiben sie nie. Siehe [Konfiguration](DE-Konfiguration).

### Werden meine Daten irgendwohin geschickt?

Nur dorthin, wo du es einstellst: SimBrief (dein öffentliches OFP), ECAM-Texttabellen und Modelle kommen von GitHub/Hugging Face, Smalltalk geht an den Anbieter deiner Wahl, und Claude spricht über den Connector mit Duopilo, den du eingerichtet hast. **Fehler melden** öffnet nur ein vorausgefülltes GitHub-Issue, das du selbst absendest.

### Ich habe vorher Kopilot genutzt.

Siehe [Umstieg von Kopilot](DE-Umstieg-von-Kopilot). „Kopilot“ bleibt das Ansprachewort.
