using System.Net;

namespace Kopilot.Remote;

/// <summary>HTML für die Freigabeseite und Fehlerseiten des OAuth-Ablaufs.</summary>
public static class ConsentPage
{
    public static IResult Render(HttpContext context, string requestId, string clientName, string redirectUri, string? error, int statusCode = 200)
    {
        var redirectHost = Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) ? uri.Host : redirectUri;
        var isLoopback = uri is not null && RequestOrigin.IsLoopbackHost(uri.Host);

        var warning = isLoopback
            ? """<p class="warn">Die Weiterleitung geht an eine lokale Adresse. Das ist normal für Claude Code – gib nur frei, wenn du die Verbindung gerade selbst gestartet hast.</p>"""
            : "";
        var errorHtml = error is null ? "" : $"""<p class="error">{E(error)}</p>""";

        var body = $"""
            <h1>Zugriff auf Kopilot freigeben?</h1>
            <p><strong>{E(clientName)}</strong> möchte die Flugdaten aus deinem Microsoft Flight Simulator lesen.</p>
            <p class="target">Weiterleitung nach der Freigabe an:<br><strong>{E(redirectHost)}</strong></p>
            {warning}
            {errorHtml}
            <form method="post" action="/authorize">
              <input type="hidden" name="request_id" value="{E(requestId)}">
              <label for="pw">Freigabe-Passwort</label>
              <input id="pw" type="password" name="password" autocomplete="current-password" autofocus required>
              <div class="buttons">
                <button type="submit" name="action" value="approve">Freigeben</button>
                <button type="submit" name="action" value="deny" class="secondary" formnovalidate>Ablehnen</button>
              </div>
            </form>
            """;

        return Html(context, "Kopilot – Zugriff freigeben", body, statusCode);
    }

    public static IResult Error(HttpContext context, string message, int statusCode = 400) =>
        Html(context, "Kopilot – Fehler", $"""<h1>Das hat nicht geklappt</h1><p class="error">{E(message)}</p>""", statusCode);

    private static IResult Html(HttpContext context, string title, string body, int statusCode)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
        headers["Referrer-Policy"] = "no-referrer";

        var html = $$"""
            <!doctype html>
            <html lang="de">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{E(title)}}</title>
              <style>
                :root { color-scheme: light dark; --bg:#f4f5f7; --card:#fff; --fg:#1b1d21; --muted:#5b616e; --accent:#1f6feb; --err:#c62828; --warn:#a15c00; }
                @media (prefers-color-scheme: dark) { :root { --bg:#111317; --card:#1b1e24; --fg:#e8eaed; --muted:#9aa0aa; --accent:#4c8dff; --err:#ff6b6b; --warn:#f0a43a; } }
                body { margin:0; min-height:100vh; display:grid; place-items:center; background:var(--bg); color:var(--fg); font:16px/1.5 system-ui, "Segoe UI", sans-serif; }
                main { background:var(--card); max-width:420px; width:calc(100% - 32px); padding:28px; border-radius:14px; box-shadow:0 4px 24px rgba(0,0,0,.12); }
                h1 { font-size:1.3rem; margin:0 0 12px; }
                .target { color:var(--muted); }
                .warn { color:var(--warn); }
                .error { color:var(--err); font-weight:600; }
                label { display:block; margin:16px 0 6px; font-weight:600; }
                input[type=password] { width:100%; box-sizing:border-box; padding:10px 12px; font-size:1rem; border:1px solid var(--muted); border-radius:8px; background:transparent; color:var(--fg); }
                .buttons { display:flex; gap:10px; margin-top:18px; }
                button { flex:1; padding:10px; font-size:1rem; border-radius:8px; border:0; cursor:pointer; background:var(--accent); color:#fff; }
                button.secondary { background:transparent; color:var(--fg); border:1px solid var(--muted); }
              </style>
            </head>
            <body><main>{{body}}</main></body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
