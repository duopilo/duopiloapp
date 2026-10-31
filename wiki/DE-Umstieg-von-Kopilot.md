[English](Migrating-from-Kopilot) | **Deutsch**

# Umstieg von Kopilot

Bis Version 0.9.x hieß Duopilo **Kopilot**. Das meiste wird von selbst übernommen – **eine Sache braucht dich**:

> **⚠ Claude neu verbinden.** Der MCP-Server heißt jetzt `Duopilo`, und `kopilot_status` heißt jetzt `duopilo_status` – die Tool-Namen in Claude ändern sich also.
> - **claude.ai-Connector:** den alten Connector entfernen und [neu eintragen](DE-Claude-verbinden#4-connector-in-claude-eintragen) – Name `Duopilo`, gleiche URL (ein vorhandener Tunnel-Hostname funktioniert weiter).
> - **Claude Desktop:** in `claude_desktop_config.json` den Eintrag in `"duopilo"` umbenennen und auf `Duopilo.exe` zeigen lassen – siehe [Claude verbinden](DE-Claude-verbinden#claude-desktop-stdio).

## Was automatisch passiert

| | |
|---|---|
| **Installer** | übernimmt eine vorhandene Kopilot-Installation: zieht von `C:\Program Files\Kopilot` nach `C:\Program Files\Duopilo` um, behält `appsettings.Local.json`, eigene Profile und Sicherungen und entfernt die alte Installation; „Mit Windows starten“ geht mit über. In einem eigenen Ordner aktualisiert er an Ort und Stelle |
| **`update.cmd`** | baut jetzt nach `C:\Tools\Duopilo` und holt beim ersten Mal `appsettings.Local.json` und eigene Profile aus `C:\Tools\Kopilot`. Der allererste Lauf kann nach `git pull` abbrechen – dann einfach noch einmal starten. `C:\Tools\Kopilot` löschen, sobald alles läuft |
| **Datenordner** | beim ersten Start wird `%LOCALAPPDATA%\Kopilot` zu `%LOCALAPPDATA%\Duopilo` (Logs, Modelle, Fehlermeldungen, Overrides, ATC-Sitzung, Dashboard-Einstellungen, Claude-Anmeldungen). Hält ein altes Kopilot noch Dateien offen, nutzt Duopilo den alten Ordner weiter und versucht es beim nächsten Start erneut |
| **Einstellungen** | der Abschnitt `"Kopilot"`, Umgebungsvariablen `Kopilot__…`, `--Kopilot:…` und Log-Kategorien `Kopilot.*` gelten weiter. Speichern im Dashboard benennt den Abschnitt in `"Duopilo"` um |
| **Sprachschleife** | „Kopilot“ bleibt das feste Ansprachewort; „Duopilo, …“ geht auch, wenn Whisper es eindeutig so abschreibt |
| **Dashboard-API** | schreibende Aufrufe senden `X-Duopilo: 1`; `X-Kopilot: 1` wird weiter angenommen |
