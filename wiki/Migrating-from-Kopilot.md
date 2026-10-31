**English** | [Deutsch](DE-Umstieg-von-Kopilot)

# Migrating from Kopilot

Up to version 0.9.x Duopilo was called **Kopilot**. Most things carry over by themselves – **one thing needs you**:

> **⚠ Reconnect Claude.** The MCP server is now called `Duopilo` and `kopilot_status` is now `duopilo_status`, so the tool names in Claude change.
> - **claude.ai connector:** remove the old connector and [add it again](Connecting-Claude#4-add-the-connector-in-claude) – name `Duopilo`, same URL (an existing tunnel host keeps working).
> - **Claude Desktop:** in `claude_desktop_config.json` rename the entry to `"duopilo"` and point it to `Duopilo.exe` – see [Connecting Claude](Connecting-Claude#claude-desktop-stdio).

## What happens automatically

| | |
|---|---|
| **Installer** | takes over an existing Kopilot installation: moves from `C:\Program Files\Kopilot` to `C:\Program Files\Duopilo`, keeps `appsettings.Local.json`, your own profiles and backups, removes the old installation; "Start with Windows" moves over. In a custom folder it updates in place |
| **`update.cmd`** | now builds to `C:\Tools\Duopilo` and copies `appsettings.Local.json` and your own profiles from `C:\Tools\Kopilot` the first time. The very first run may stop after `git pull` – just run it again. Delete `C:\Tools\Kopilot` once everything works |
| **Data folder** | on first start `%LOCALAPPDATA%\Kopilot` becomes `%LOCALAPPDATA%\Duopilo` (logs, models, reports, overrides, ATC session, dashboard settings, Claude sign-ins). If an old Kopilot still holds files open, Duopilo keeps using the old folder and retries on the next start |
| **Settings** | the `"Kopilot"` section, `Kopilot__…` environment variables, `--Kopilot:…` and `Kopilot.*` log categories keep working. Saving in the dashboard renames the section to `"Duopilo"` |
| **Voice loop** | "Kopilot" stays the fixed wake word; "Duopilo, …" works too when Whisper transcribes it clearly |
| **Dashboard API** | writing calls use `X-Duopilo: 1`; `X-Kopilot: 1` is still accepted |
