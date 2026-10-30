**English** | [Deutsch](cloudflared.de.md)

# Setting up a Cloudflare Tunnel for Duopilo

Goal: `https://duopilo.example.com` points to Duopilo (`http://127.0.0.1:5155`) on the sim PC through a **named tunnel**. cloudflared runs as a **Windows service**. **No port is opened** on the router or in Windows Firewall; cloudflared connects from the inside out.

```
Claude (Cloud) ──https──▶ Cloudflare ──Tunnel──▶ cloudflared (service) ──▶ http://127.0.0.1:5155 ──▶ Duopilo ──▶ MSFS
```

> **Coming from Kopilot?** An existing tunnel keeps working after the rename – its name and host name (e.g. `kopilot.…`) don't
> matter, only `PublicUrl` has to match. Only the connector in Claude has to be added again (see the README).

This guide follows the official Cloudflare docs for [locally-managed tunnels as a Windows service](https://developers.cloudflare.com/tunnel/advanced/local-management/as-a-service/windows/).

## Prerequisites

- The DNS of your domain (in this guide **example.com**) is hosted at Cloudflare. The Cloudflare dashboard shows the domain as *Active*.
- Duopilo runs in HTTP mode with `PublicUrl = https://duopilo.example.com` (see [README](../README.md), "Remote operation").
- A **Command Prompt run as administrator**. All commands below are for `cmd.exe`.

## 1. Install cloudflared

1. Create the folder:
   ```bat
   mkdir C:\Cloudflared\bin
   ```
2. Download the Windows 64-bit version from the [Cloudflare downloads page](https://developers.cloudflare.com/tunnel/downloads/), put it into `C:\Cloudflared\bin\` and rename it to `cloudflared.exe`.
3. Check:
   ```bat
   cd /d C:\Cloudflared\bin
   cloudflared.exe --version
   ```

> `winget install --id Cloudflare.cloudflared` works too. The EXE then lives somewhere else. In steps 2 to 7, call it directly as `cloudflared` without the `cd`, and adjust the EXE path in the `ImagePath` in step 8 (`where cloudflared` shows it).

## 2. Log in to Cloudflare

```bat
cloudflared.exe tunnel login
```

A browser window opens. Select **example.com** (your domain) there and confirm. The certificate is saved to `%USERPROFILE%\.cloudflared\cert.pem`.

## 3. Create the tunnel

```bat
cloudflared.exe tunnel create duopilo
```

The output contains the **tunnel ID** (a UUID). Write it down; below it appears as `<TUNNEL-ID>`. The credentials file is then in `%USERPROFILE%\.cloudflared\<TUNNEL-ID>.json`.

## 4. Point the subdomain to the tunnel

```bat
cloudflared.exe tunnel route dns duopilo duopilo.example.com
```

This creates the DNS record `duopilo` at Cloudflare as a CNAME to the tunnel.

## 5. Provide the files for the service

The service runs as *LocalSystem* and looks for its files in the system profile, not in your user folder:

```bat
mkdir C:\Windows\System32\config\systemprofile\.cloudflared
copy %USERPROFILE%\.cloudflared\cert.pem C:\Windows\System32\config\systemprofile\.cloudflared\
copy %USERPROFILE%\.cloudflared\<TUNNEL-ID>.json C:\Windows\System32\config\systemprofile\.cloudflared\
```

## 6. Create config.yml

Open Notepad **as administrator** and save this as `C:\Windows\System32\config\systemprofile\.cloudflared\config.yml`:

```yaml
tunnel: <TUNNEL-ID>
credentials-file: C:\Windows\System32\config\systemprofile\.cloudflared\<TUNNEL-ID>.json
logfile: C:\Cloudflared\cloudflared.log

ingress:
  - hostname: duopilo.example.com
    service: http://127.0.0.1:5155
  - service: http_status:404
```

Important:
- **Do not set `httpHostHeader`.** Duopilo needs the public hostname to tell tunnel requests from local ones.
- The last rule (`http_status:404`) catches all other hostnames.

Check the rules:

```bat
cloudflared.exe tunnel --config C:\Windows\System32\config\systemprofile\.cloudflared\config.yml ingress validate
```

## 7. Test run in the foreground

Duopilo must be running. Then start the tunnel in the window as a test:

```bat
cloudflared.exe tunnel --config C:\Windows\System32\config\systemprofile\.cloudflared\config.yml run
```

Check in a **second** window:

```bat
curl https://duopilo.example.com/.well-known/oauth-protected-resource
curl -i https://duopilo.example.com/mcp
curl -i https://duopilo.example.com/state
```

Expected:
1. JSON with `"resource":"https://duopilo.example.com/mcp"`
2. `401 Unauthorized` with `WWW-Authenticate: Bearer resource_metadata="…"`
3. `404 Not Found`, because the diagnostic pages are blocked through the tunnel.

Then stop the test run with `Ctrl+C`.

## 8. Set up as a Windows service

```bat
cd /d C:\Cloudflared\bin
cloudflared.exe service install
reg add HKLM\SYSTEM\CurrentControlSet\Services\Cloudflared /v ImagePath /t REG_EXPAND_SZ /d "C:\Cloudflared\bin\cloudflared.exe --config=C:\Windows\System32\config\systemprofile\.cloudflared\config.yml tunnel run" /f
sc stop cloudflared
sc start cloudflared
sc query cloudflared
```

`sc query` must show `STATE : 4 RUNNING`. From now on the service starts automatically with Windows. An error from `sc stop` is normal if the service wasn't running yet.

> Alternatively, set the `ImagePath` in Registry Editor under `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Cloudflared`. Make sure no extra spaces slip in.

After changing `config.yml`:

```bat
sc stop cloudflared
sc start cloudflared
```

Then repeat the three `curl` tests from step 7.

## Optional: let only Claude reach /mcp (Cloudflare WAF)

Claude's servers connect from the range `160.79.104.0/21` ([Anthropic IP addresses](https://platform.claude.com/docs/en/api/ip-addresses)). A WAF rule can block everything else. The only exception is the consent page `/authorize`, which you open yourself in the browser.

Cloudflare dashboard → example.com → **Security → WAF → Custom rules → Create rule**, edit the expression:

```
(http.host eq "duopilo.example.com" and not ip.src in {160.79.104.0/21} and http.request.uri.path ne "/authorize")
```

Action: **Block**.

Result: only Claude's servers get through the public URL. Claude Code on the sim PC uses the local address `http://127.0.0.1:5155/mcp` with the bearer token instead. If Anthropic changes its IP range, you have to update the rule.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Cloudflare error **1033** | The tunnel isn't running. Check with `sc query cloudflared` and in the log `C:\Cloudflared\cloudflared.log`. |
| **502 Bad Gateway** | The tunnel is running, but Duopilo isn't, or Duopilo isn't in HTTP mode or runs on a different port. |
| **400 Bad Request** (blank page) | Duopilo's host filter doesn't know the hostname. `PublicUrl` in `appsettings.Local.json` must be exactly `https://duopilo.example.com`. |
| `/.well-known/…` returns **404** | OAuth isn't active in Duopilo. The Duopilo log says why at startup (e.g. password too short). |
| Service doesn't start | Check the `ImagePath` (path to the EXE, no extra spaces), then test `config.yml` with `ingress validate`. |
