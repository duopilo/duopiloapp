using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Kopilot.Remote;

/// <summary>
/// Minimaler OAuth-2.1-Autorisierungsserver nach den Vorgaben für Claude-Connectors:
/// Protected Resource Metadata (RFC 9728), Authorization Server Metadata (RFC 8414),
/// Dynamic Client Registration (RFC 7591), Authorization Code + PKCE S256, Refresh-Token-Rotation.
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>Callback der gehosteten Claude-Apps (claude.ai, Desktop, Mobile, Cowork).</summary>
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    public const string ProtectedResourcePath = "/.well-known/oauth-protected-resource";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapOAuth(this WebApplication app)
    {
        // --- Discovery ------------------------------------------------------------------
        app.MapGet(ProtectedResourcePath, ProtectedResourceMetadata);
        app.MapGet(ProtectedResourcePath + "/mcp", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);

        // --- Registrierung, Freigabe, Tokens ----------------------------------------------
        app.MapPost("/register", Register);
        app.MapGet("/authorize", AuthorizeGet);
        app.MapPost("/authorize", AuthorizePost);
        app.MapPost("/token", Token);
    }

    // ------------------------------------------------------------------ Discovery

    private static IResult ProtectedResourceMetadata(IOptions<KopilotOptions> options)
    {
        var remote = options.Value.Remote;
        return Results.Json(
            new
            {
                resource = remote.ResourceUrl,
                authorization_servers = new[] { remote.Issuer },
                scopes_supported = new[] { OAuthStore.Scope },
                bearer_methods_supported = new[] { "header" },
                resource_name = "Kopilot (MSFS 2024)",
            },
            Json);
    }

    private static IResult AuthorizationServerMetadata(IOptions<KopilotOptions> options)
    {
        var issuer = options.Value.Remote.Issuer;
        return Results.Json(
            new
            {
                issuer,
                authorization_endpoint = issuer + "/authorize",
                token_endpoint = issuer + "/token",
                registration_endpoint = issuer + "/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                scopes_supported = new[] { OAuthStore.Scope, "offline_access" },
            },
            Json);
    }

    // ------------------------------------------------------------------ Dynamic Client Registration

    private static async Task<IResult> Register(HttpContext context, OAuthStore store)
    {
        RegistrationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RegistrationRequest>(context.Request.Body, Json, context.RequestAborted);
        }
        catch (JsonException)
        {
            return RegistrationError("invalid_client_metadata", "Ungültiges JSON.");
        }

        var redirectUris = request?.RedirectUris?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
        if (redirectUris.Count == 0)
        {
            return RegistrationError("invalid_redirect_uri", "redirect_uris fehlt.");
        }

        var rejected = redirectUris.FirstOrDefault(u => !IsAllowedRedirectUri(u));
        if (rejected is not null)
        {
            return RegistrationError("invalid_redirect_uri", $"Nicht erlaubte redirect_uri: {rejected}");
        }

        var client = store.RegisterClient(request!.ClientName, redirectUris);
        return Results.Json(
            new
            {
                client_id = client.ClientId,
                client_name = client.ClientName,
                client_id_issued_at = client.CreatedUtc.ToUnixTimeSeconds(),
                redirect_uris = client.RedirectUris,
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
                scope = OAuthStore.Scope,
            },
            Json,
            statusCode: StatusCodes.Status201Created);
    }

    // ------------------------------------------------------------------ Autorisierung

    private static IResult AuthorizeGet(HttpContext context, OAuthStore store, LoginThrottle throttle)
    {
        var query = context.Request.Query;
        string? clientId = query["client_id"];
        string? redirectUri = query["redirect_uri"];
        string? state = query["state"];

        var client = store.FindClient(clientId);
        if (client is null)
        {
            return ConsentPage.Error(context, "Unbekannter Client. Bitte die Verbindung in Claude neu starten.");
        }

        // Ohne gültige Redirect-URI darf auf keinen Fall weitergeleitet werden.
        if (string.IsNullOrEmpty(redirectUri) || !RedirectMatches(client, redirectUri))
        {
            return ConsentPage.Error(context, "Diese Weiterleitungsadresse ist für den Client nicht registriert.");
        }

        if (query["response_type"] != "code")
        {
            return RedirectWithError(redirectUri, state, "unsupported_response_type", "Nur response_type=code wird unterstützt.");
        }

        string? codeChallenge = query["code_challenge"];
        if (string.IsNullOrEmpty(codeChallenge) || query["code_challenge_method"] != "S256")
        {
            return RedirectWithError(redirectUri, state, "invalid_request", "PKCE mit S256 ist Pflicht.");
        }

        string? resource = query["resource"].FirstOrDefault();
        if (!string.IsNullOrEmpty(resource) && !store.ResourceMatches(resource))
        {
            return RedirectWithError(redirectUri, state, "invalid_target", "Unbekannte Ressource.");
        }

        var requestId = store.AddPending(new PendingAuthorization(client.ClientId, client.ClientName, redirectUri, state, codeChallenge, resource));
        var error = throttle.IsLocked(out var until) ? LockedMessage(until) : null;
        return ConsentPage.Render(context, requestId, client.ClientName, redirectUri, error);
    }

    private static async Task<IResult> AuthorizePost(
        HttpContext context,
        OAuthStore store,
        LoginThrottle throttle,
        IOptions<KopilotOptions> options,
        ILoggerFactory loggerFactory)
    {
        if (!context.Request.HasFormContentType)
        {
            return ConsentPage.Error(context, "Ungültige Anfrage.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        string? requestId = form["request_id"];
        var pending = store.PeekPending(requestId);
        if (pending is null)
        {
            return ConsentPage.Error(context, "Die Anfrage ist abgelaufen. Bitte die Verbindung in Claude neu starten.");
        }

        if (form["action"] == "deny")
        {
            store.TakePending(requestId);
            return RedirectWithError(pending.RedirectUri, pending.State, "access_denied", "Freigabe abgelehnt.");
        }

        if (throttle.IsLocked(out var until))
        {
            return ConsentPage.Render(context, requestId!, pending.ClientName, pending.RedirectUri, LockedMessage(until), StatusCodes.Status429TooManyRequests);
        }

        var log = loggerFactory.CreateLogger("Kopilot.Remote.OAuth");
        string password = form["password"].FirstOrDefault() ?? "";
        if (!Secrets.FixedTimeEquals(password, options.Value.Remote.ConsentPassword))
        {
            throttle.RegisterFailure();
            log.LogWarning("OAuth: Falsches Freigabe-Passwort für {Client}", pending.ClientName);
            return ConsentPage.Render(context, requestId!, pending.ClientName, pending.RedirectUri, "Falsches Passwort.", StatusCodes.Status401Unauthorized);
        }

        throttle.Reset();
        if (store.TakePending(requestId) is null)
        {
            return ConsentPage.Error(context, "Die Anfrage ist abgelaufen. Bitte die Verbindung in Claude neu starten.");
        }

        var code = store.IssueCode(pending);
        log.LogInformation("OAuth: Zugriff freigegeben für {Client}", pending.ClientName);
        return Results.Redirect(QueryHelpers.AddQueryString(pending.RedirectUri, WithState(pending.State, ("code", code))));
    }

    // ------------------------------------------------------------------ Token-Endpunkt

    private static async Task<IResult> Token(HttpContext context, OAuthStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (!context.Request.HasFormContentType)
        {
            return TokenError("invalid_request", "Content-Type muss application/x-www-form-urlencoded sein.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        string? grantType = form["grant_type"];
        var clientId = form["client_id"].FirstOrDefault() ?? ClientIdFromBasicAuth(context);

        TokenResult result;
        switch (grantType)
        {
            case "authorization_code":
                if (store.FindClient(clientId) is null)
                {
                    return TokenError("invalid_client", "Unbekannter Client.", StatusCodes.Status401Unauthorized);
                }

                result = store.RedeemCode(form["code"], clientId, form["redirect_uri"], form["code_verifier"], form["resource"].FirstOrDefault());
                break;

            case "refresh_token":
                result = store.Refresh(form["refresh_token"], clientId);
                break;

            default:
                return TokenError("unsupported_grant_type", "Nur authorization_code und refresh_token.");
        }

        if (!result.Ok)
        {
            return TokenError(result.ErrorCode!, result.ErrorDescription!);
        }

        return Results.Json(
            new
            {
                access_token = result.AccessToken,
                token_type = "Bearer",
                expires_in = result.ExpiresIn,
                refresh_token = result.RefreshToken,
                scope = OAuthStore.Scope,
            },
            Json);
    }

    // ------------------------------------------------------------------ Redirect-URIs

    /// <summary>Erlaubt sind nur Claudes Callback und Loopback-Adressen (Claude Code).</summary>
    public static bool IsAllowedRedirectUri(string uri)
    {
        if (uri == ClaudeCallback)
        {
            return true;
        }

        return TryLoopback(uri, out _);
    }

    /// <summary>Exakter Vergleich, bei Loopback-Adressen ohne Port (RFC 8252, Abschnitt 7.3).</summary>
    public static bool RedirectMatches(RegisteredClient client, string requested)
    {
        if (client.RedirectUris.Contains(requested, StringComparer.Ordinal))
        {
            return true;
        }

        return TryLoopback(requested, out var req)
            && client.RedirectUris.Any(r => TryLoopback(r, out var reg)
                && string.Equals(reg.Host, req.Host, StringComparison.OrdinalIgnoreCase)
                && reg.AbsolutePath == req.AbsolutePath);
    }

    private static bool TryLoopback(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttp
            && (parsed.Host == "localhost" || parsed.Host == "127.0.0.1")
            && string.IsNullOrEmpty(parsed.Fragment)
            && string.IsNullOrEmpty(parsed.UserInfo))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    // ------------------------------------------------------------------ Hilfen

    private static string LockedMessage(DateTimeOffset until) =>
        $"Zu viele falsche Passwörter. Freigabe gesperrt bis {until.ToLocalTime():HH:mm} Uhr.";

    private static IResult RedirectWithError(string redirectUri, string? state, string error, string description) =>
        Results.Redirect(QueryHelpers.AddQueryString(redirectUri, WithState(state, ("error", error), ("error_description", description))));

    private static Dictionary<string, string?> WithState(string? state, params (string Key, string Value)[] values)
    {
        var result = values.ToDictionary(v => v.Key, v => (string?)v.Value);
        if (!string.IsNullOrEmpty(state))
        {
            result["state"] = state;
        }

        return result;
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new { error, error_description = description }, Json, statusCode: StatusCodes.Status400BadRequest);

    private static IResult TokenError(string error, string description, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error, error_description = description }, Json, statusCode: statusCode);

    private static string? ClientIdFromBasicAuth(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var user = decoded.Split(':', 2)[0];
            return user.Length > 0 ? Uri.UnescapeDataString(user) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public sealed class RegistrationRequest
    {
        [JsonPropertyName("client_name")]
        public string? ClientName { get; set; }

        [JsonPropertyName("redirect_uris")]
        public List<string>? RedirectUris { get; set; }
    }
}
