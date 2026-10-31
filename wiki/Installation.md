**English** | [Deutsch](DE-Installation)

# Installation

## Requirements

- Windows 10/11 (x64) with **Microsoft Flight Simulator 2024**
- For building from source: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`) and the **MSFS 2024 SDK** (for SimConnect)
- Optional: a microphone for the [Voice Loop](Voice-Loop), a [SimBrief](https://www.simbrief.com) account for plan-based features

## Option A – Installer

> Releases are currently created as **drafts** and are not public yet. Until the first public release, use option B.

The installer (Inno Setup):

- installs to `C:\Program Files\Duopilo` – or, without admin rights, for your own user only,
- creates a Start menu entry and a desktop shortcut (preselected, can be turned off), optionally autostart (the same as **Start with Windows** in the tray menu),
- picks its language from the Windows language (English as fallback).

On updates `appsettings.Local.json` stays untouched. Bundled profiles, `atc-templates.json` and `appsettings.json` are renewed; your own changes to them are backed up to `backup\<date>\` first.

Uninstalling removes program, Start menu entry, desktop shortcut and autostart and asks whether settings and `%LOCALAPPDATA%\Duopilo` should be removed as well.

There is also a ZIP with the same self-contained build.

**SimConnect** isn't included in setup or ZIP – it belongs to Microsoft's MSFS 2024 SDK. Duopilo loads it from the installed SDK (`C:\MSFS 2024 SDK`, the variable `MSFS2024_SDK` or the folder under **Settings → SimConnect**). Without an SDK the dashboard offers **Install MSFS SDK**: Duopilo downloads Microsoft's installer directly from Microsoft, checks it against the known checksum and starts it; you accept Microsoft's license there. Then **Restart Duopilo**.

## Option B – Build from source

### 1. Get the SimConnect SDK

Duopilo needs two files from the MSFS 2024 SDK:

| File | Location in the SDK |
|---|---|
| `Microsoft.FlightSimulator.SimConnect.dll` | `SimConnect SDK\lib\managed\` |
| `SimConnect.dll` (native, used directly by the traffic query) | `SimConnect SDK\lib\` |

You get the SDK through MSFS 2024 **Developer Mode**: enable it in the options, then start the SDK installer from the **Help** menu of the developer toolbar.

The build looks for the DLLs in this order:

1. `lib\` in the repo (just copy the two DLLs in – they are git-ignored)
2. environment variable `MSFS2024_SDK`
3. `C:\MSFS 2024 SDK\`

If it finds nothing, it builds against a **stub** – meant only for GitHub Actions. That build can't connect to MSFS; the build log shows a warning. A proper build logs `Duopilo: SimConnect aus …`.

### 2. Build

```powershell
git clone https://github.com/duopilo/duopiloapp
cd duopiloapp
dotnet publish src/Duopilo -c Release -o C:\Tools\Duopilo
```

Afterwards `C:\Tools\Duopilo` must contain `Duopilo.exe`, `Microsoft.FlightSimulator.SimConnect.dll`, `SimConnect.dll` and the `profiles` folder.

### 3. Update later

Double-click **`update.cmd`** in the repo folder. It

1. pulls the latest version,
2. builds into a temporary folder,
3. stops all running Duopilo instances (including the one Claude Desktop started, and old Kopilot instances),
4. copies to `C:\Tools\Duopilo` and restarts Duopilo.

Other target folder: `update.cmd D:\Duopilo`. `update.cmd` never overwrites `appsettings.Local.json` or anything in `%LOCALAPPDATA%\Duopilo`.

## Where things live

| Path | Content |
|---|---|
| Program folder (`C:\Program Files\Duopilo` or `C:\Tools\Duopilo`) | `Duopilo.exe`, `appsettings.json` (defaults), **`appsettings.Local.json`** (your settings), `profiles\`, `atc-templates.json`, `data\`, `voice-settings.json` (template only) |
| `%LOCALAPPDATA%\Duopilo` | Data folder: logs, downloaded models, `speech.json`, `voice-settings.json`, ATC session, sign-ins, reports, your own template/data overrides |

Full list: [Configuration Reference](Configuration-Reference).

## Coming from Kopilot?

Most things carry over automatically, but the Claude connector has to be re-added. See [Migrating from Kopilot](Migrating-from-Kopilot).

**Next:** [Quick Start](Quick-Start)
