using Microsoft.Extensions.Options;

namespace Kopilot.Remote;

/// <summary>
/// Schützt den MCP-Endpunkt (/mcp). Zugelassen sind:
///   1. der statische Bearer-Token aus der Konfiguration,
///   2. OAuth-Access-Tokens, die Kopilot selbst ausgestellt hat (Claude-Connector).
/// Alles andere bekommt 401 – mit Verweis auf die OAuth-Metadaten, damit Claude die Anmeldung starten kann.
/// </summary>
public sealed class McpAuthMiddleware(RequestDelegate next, IOptions<KopilotOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var remote = options.Value.Remote;
        var token = ReadBearerToken(context);

        if (token is not null)
        {
            if (remote.HasBearerToken && Secrets.FixedTimeEquals(token, remote.BearerToken.Trim()))
            {
                await next(context);
                return;
            }

            if (remote.OAuthEnabled && context.RequestServices.GetRequiredService<OAuthStore>().ValidateAccessToken(token))
            {
                await next(context);
                return;
            }
        }

        var challenge = new List<string>();
        if (token is not null)
        {
            challenge.Add("error=\"invalid_token\"");
        }

        if (remote.OAuthEnabled)
        {
            challenge.Add($"resource_metadata=\"{remote.Issuer}{OAuthEndpoints.ProtectedResourcePath}\"");
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = challenge.Count > 0 ? "Bearer " + string.Join(", ", challenge) : "Bearer";
        await context.Response.WriteAsJsonAsync(new
        {
            error = token is null ? "unauthorized" : "invalid_token",
            message = "Kopilot: Zugriff nur mit gültigem Token.",
        });
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header[7..].Trim();
            return token.Length > 0 ? token : null;
        }

        return null;
    }
}
