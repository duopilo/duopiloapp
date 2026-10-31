**English** | [Deutsch](DE-Claude-verbinden)

# Connecting Claude

Duopilo is an MCP server. Claude then reads the aircraft itself, sees every monitoring alert and can prepare inputs. Pick the way that fits your client:

| Client | Way | Tunnel needed? |
|---|---|---|
| Claude Desktop on the sim PC | [stdio](#claude-desktop-stdio) | no |
| claude.ai in the browser, phone app, voice mode, Claude Desktop via connector | [Remote connector (OAuth)](#remote-connector-claudeai-phone-voice-mode) | yes |
| Claude Code, MCP Inspector, your own scripts | [Bearer token](#claude-code-and-other-clients-bearer-token) | only from another machine |

You don't need Claude at all for callouts, ATC and the voice loop – those run inside Duopilo.

## Claude Desktop (stdio)

Claude Desktop starts its own Duopilo instance in stdio mode. In Claude Desktop open **Settings → Developer → Edit Config** (`%APPDATA%\Claude\claude_desktop_config.json`) and add:

```json
{
  "mcpServers": {
    "duopilo": {
      "command": "C:\\Tools\\Duopilo\\Duopilo.exe",
      "args": ["--stdio"]
    }
  }
}
```

Use your actual path (e.g. `C:\\Program Files\\Duopilo\\Duopilo.exe`). If there's already an `mcpServers` block, only add the `duopilo` entry. Fully restart Claude Desktop.

Good to know:

- The stdio instance **doesn't speak** and doesn't run the voice loop – only the tray instance does, so nothing is heard twice. Keep the tray instance running for speech.
- Both instances can be connected to MSFS at the same time. They share the input switch and the pending action.
- Logs: Claude Desktop's log folder (`mcp-server-duopilo.log`) and `%LOCALAPPDATA%\Duopilo\logs\duopilo-stdio.log`.

## Remote connector (claude.ai, phone, voice mode)

Claude's cloud reaches Duopilo through a **Cloudflare Tunnel**. Duopilo itself still only listens on `127.0.0.1`.

```
Claude (cloud) ──▶ https://duopilo.example.com/mcp ──▶ Cloudflare ──▶ cloudflared (service) ──▶ 127.0.0.1:5155 ──▶ Duopilo ──▶ MSFS
```

### Access control

`/mcp` is always protected, even locally – without a valid token the answer is `401`.

| Way | For | Configuration |
|---|---|---|
| **OAuth** | Claude connector (claude.ai, Desktop, phone, voice mode) | `PublicUrl` + `ConsentPassword` |
| **Bearer token** | Claude Code, MCP Inspector, scripts | `BearerToken` |

With OAuth Claude registers itself (Dynamic Client Registration). When connecting, a **Duopilo consent page** asks for your consent password. Afterwards Claude renews access by itself: access token 1 hour, refresh token 90 days, rotated on every renewal. Five wrong passwords lock consent for 15 minutes.

The dashboard and the raw data pages stay local-only. Duopilo only accepts requests to `localhost` and to the host name of `PublicUrl`, stores tokens only as SHA-256 hashes and never accepts tokens in the URL.

### 1. Set the secrets

Easiest: Dashboard → **Settings → Connection to Claude** – enter the public address and a consent password (at least 12 characters), create a token with **Generate new**, then **Restart Duopilo**.

By hand: `Duopilo.exe --generate-secret | more` prints a random bearer token. Then in `appsettings.Local.json` (copy from `appsettings.Local.example.json`):

```json
{
  "Duopilo": {
    "Transport": "http",
    "Remote": {
      "PublicUrl": "https://duopilo.example.com",
      "BearerToken": "<output of --generate-secret>",
      "ConsentPassword": "<your consent password>"
    }
  }
}
```

Environment variables take precedence: `setx Duopilo__Remote__BearerToken "<token>"`, `setx Duopilo__Remote__ConsentPassword "<password>"`.

### 2. Check the log

Start Duopilo (HTTP mode is the default). The log (Dashboard → Setup and diagnostics) must show:

```
Access control: bearer token active.
Access control: OAuth active. Connector URL for Claude: https://duopilo.example.com/mcp
```

If a line is missing, a warning right below it says why.

### 3. Set up the tunnel

Step by step in **[docs/cloudflared.md](https://github.com/duopilo/duopiloapp/blob/main/docs/cloudflared.md)**: named tunnel, subdomain, cloudflared as a Windows service, optionally a Cloudflare WAF rule that lets only Claude's IP range reach `/mcp`.

### 4. Add the connector in Claude

1. In Claude open **Customize → Connectors → Add custom connector** (Team/Enterprise: an owner adds it under *Organization settings → Connectors*).
2. Name `Duopilo`, URL `https://duopilo.example.com/mcp`.
3. If asked: Authentication **Sign in now**, OAuth client **Register automatically** (Duopilo doesn't support "Use Claude's published identity").
4. **Add**, then **Connect**. The Duopilo consent page opens with `claude.ai` as target – enter the password, **Approve**.
5. In a new chat enable Duopilo via **+ → Connectors** and ask: *"What's our flight state?"*

The connector applies to your whole account, so also to Desktop and phone. Voice mode with the remote connector hasn't been tested yet.

## Claude Code and other clients (bearer token)

On the sim PC, without the tunnel:

```bat
claude mcp add --transport http duopilo http://127.0.0.1:5155/mcp --header "Authorization: Bearer <Token>"
```

From elsewhere, `https://duopilo.example.com/mcp` works with the same header – unless the optional WAF rule is active.

## Revoking access

| Goal | How |
|---|---|
| Disconnect all Claude connectors | quit Duopilo, delete `%LOCALAPPDATA%\Duopilo\oauth-state.json`, restart |
| Change the bearer token | generate a new one, enter it, restart |
| Shut everything down immediately | stop the tunnel service: `sc stop cloudflared` |

## Working with Claude in the cockpit

- Every tool response contains `activeAlerts`, so Claude notices warnings even while asking for something else.
- For continuous monitoring, Claude can loop on `wait_for_alerts` (waits up to 25 s for the next alert).
- Claude prepares inputs with `prepare_action`, reads you the readback and only calls `confirm_action` after your "confirm".

All tools: [MCP Tools](MCP-Tools).
