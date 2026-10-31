[English](Connecting-Claude) | **Deutsch**

# Claude verbinden

Duopilo ist ein MCP-Server. Damit liest Claude das Flugzeug selbst aus, sieht jede Monitoring-Meldung und kann Eingaben vorbereiten. Wähl den Weg, der zu deinem Client passt:

| Client | Weg | Tunnel nötig? |
|---|---|---|
| Claude Desktop auf dem Sim-PC | [stdio](#claude-desktop-stdio) | nein |
| claude.ai im Browser, Handy-App, Sprachmodus, Claude Desktop über Connector | [Remote-Connector (OAuth)](#remote-connector-claudeai-handy-sprachmodus) | ja |
| Claude Code, MCP Inspector, eigene Skripte | [Bearer-Token](#claude-code-und-andere-clients-bearer-token) | nur von einem anderen Rechner aus |

Für Callouts, ATC und die Sprachschleife brauchst du Claude gar nicht – die laufen in Duopilo selbst.

## Claude Desktop (stdio)

Claude Desktop startet eine eigene Duopilo-Instanz im stdio-Modus. Öffne in Claude Desktop **Einstellungen → Entwickler → Konfiguration bearbeiten** (*Settings → Developer → Edit Config*, `%APPDATA%\Claude\claude_desktop_config.json`) und trag ein:

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

Nimm deinen tatsächlichen Pfad (z. B. `C:\\Program Files\\Duopilo\\Duopilo.exe`). Gibt es schon einen `mcpServers`-Block, kommt nur der `duopilo`-Eintrag dazu. Danach Claude Desktop komplett neu starten.

Gut zu wissen:

- Die stdio-Instanz **spricht nicht** und betreibt keine Sprachschleife – das macht nur die Tray-Instanz, damit nichts doppelt zu hören ist. Lass für die Sprachausgabe die Tray-Instanz laufen.
- Beide Instanzen können gleichzeitig mit MSFS verbunden sein. Sie teilen sich den Eingabe-Schalter und die offene Aktion.
- Logs: der Log-Ordner von Claude Desktop (`mcp-server-duopilo.log`) und `%LOCALAPPDATA%\Duopilo\logs\duopilo-stdio.log`.

## Remote-Connector (claude.ai, Handy, Sprachmodus)

Claudes Cloud erreicht Duopilo über einen **Cloudflare Tunnel**. Duopilo selbst hört weiterhin nur auf `127.0.0.1`.

```
Claude (cloud) ──▶ https://duopilo.example.com/mcp ──▶ Cloudflare ──▶ cloudflared (service) ──▶ 127.0.0.1:5155 ──▶ Duopilo ──▶ MSFS
```

### Zugriffsschutz

`/mcp` ist immer geschützt, auch lokal – ohne gültigen Token kommt `401` zurück.

| Weg | Für | Konfiguration |
|---|---|---|
| **OAuth** | Claude-Connector (claude.ai, Desktop, Handy, Sprachmodus) | `PublicUrl` + `ConsentPassword` |
| **Bearer-Token** | Claude Code, MCP Inspector, Skripte | `BearerToken` |

Bei OAuth registriert sich Claude selbst (Dynamic Client Registration). Beim Verbinden fragt eine **Freigabeseite von Duopilo** nach deinem Freigabe-Passwort. Danach erneuert Claude seinen Zugang selbst: Access-Token 1 Stunde, Refresh-Token 90 Tage, bei jeder Erneuerung ausgetauscht. Nach fünf falschen Passwörtern ist die Freigabe 15 Minuten gesperrt.

Dashboard und Rohdaten-Seiten bleiben nur lokal erreichbar. Duopilo nimmt nur Anfragen an `localhost` und an den Hostnamen aus `PublicUrl` an, speichert Tokens nur als SHA-256-Hash und nimmt Tokens in der URL nie an.

### 1. Geheimnisse anlegen

Am einfachsten: Dashboard → **Einstellungen → Verbindung zu Claude** – öffentliche Adresse und ein Freigabe-Passwort (mindestens 12 Zeichen) eintragen, mit **Neu erzeugen** einen Token anlegen, dann **Duopilo neu starten**.

Von Hand: `Duopilo.exe --generate-secret | more` gibt einen zufälligen Bearer-Token aus. Dann in `appsettings.Local.json` (eine Kopie von `appsettings.Local.example.json`):

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

Umgebungsvariablen haben Vorrang: `setx Duopilo__Remote__BearerToken "<token>"`, `setx Duopilo__Remote__ConsentPassword "<password>"`.

### 2. Log prüfen

Starte Duopilo (der HTTP-Modus ist Standard). Im Log (Dashboard → Einrichtung und Diagnose) muss stehen:

```
Access control: bearer token active.
Access control: OAuth active. Connector URL for Claude: https://duopilo.example.com/mcp
```

Fehlt eine Zeile, sagt eine Warnung direkt darunter, warum.

### 3. Tunnel einrichten

Schritt für Schritt in **[docs/cloudflared.de.md](https://github.com/duopilo/duopiloapp/blob/main/docs/cloudflared.de.md)**: Named Tunnel, Subdomain, cloudflared als Windows-Dienst, optional eine Cloudflare-WAF-Regel, die nur Claudes IP-Bereich an `/mcp` lässt.

### 4. Connector in Claude eintragen

1. In Claude **Customize → Connectors → Add custom connector** öffnen (Team/Enterprise: Ein Owner trägt ihn unter *Organization settings → Connectors* ein).
2. Name `Duopilo`, URL `https://duopilo.example.com/mcp`.
3. Falls gefragt: Authentication **Sign in now**, OAuth client **Register automatically** („Use Claude's published identity“ unterstützt Duopilo nicht).
4. **Add**, dann **Connect**. Die Freigabeseite von Duopilo öffnet sich mit `claude.ai` als Ziel – Passwort eingeben, **Freigeben** (*Approve*).
5. In einem neuen Chat Duopilo über **+ → Connectors** einschalten und fragen: *„Wie ist unser Flugzustand?“*

Der Connector gilt für dein ganzes Konto, also auch für Desktop und Handy. Den Sprachmodus mit dem Remote-Connector hat noch niemand getestet.

## Claude Code und andere Clients (Bearer-Token)

Auf dem Sim-PC, ohne Tunnel:

```bat
claude mcp add --transport http duopilo http://127.0.0.1:5155/mcp --header "Authorization: Bearer <Token>"
```

Von woanders geht `https://duopilo.example.com/mcp` mit demselben Header – außer die optionale WAF-Regel ist aktiv.

## Zugang widerrufen

| Ziel | So geht's |
|---|---|
| Alle Claude-Connectors trennen | Duopilo beenden, `%LOCALAPPDATA%\Duopilo\oauth-state.json` löschen, neu starten |
| Bearer-Token wechseln | neuen erzeugen, eintragen, neu starten |
| Alles sofort dichtmachen | Tunnel-Dienst stoppen: `sc stop cloudflared` |

## Mit Claude im Cockpit arbeiten

- Jede Tool-Antwort enthält `activeAlerts`. So bemerkt Claude Warnungen auch, während es eigentlich etwas anderes abfragt.
- Für die Dauerüberwachung kann Claude `wait_for_alerts` in einer Schleife aufrufen (wartet bis zu 25 s auf die nächste Meldung).
- Claude bereitet Eingaben mit `prepare_action` vor, liest dir den Readback vor und ruft `confirm_action` erst nach deinem „confirm“ auf.

Alle Tools: [MCP-Tools](DE-MCP-Tools).
