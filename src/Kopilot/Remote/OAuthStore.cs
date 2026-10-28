using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kopilot.Remote;

/// <summary>
/// Minimaler OAuth-2.1-Zustand für genau einen Nutzer (dich):
/// registrierte Clients (DCR), Autorisierungscodes, Access- und Refresh-Tokens.
///
/// Clients und Tokens werden – Tokens nur als SHA-256-Hash – in einer JSON-Datei gespeichert,
/// damit der Claude-Connector einen Neustart von Kopilot übersteht. Codes und offene Freigaben bleiben nur im Speicher.
/// </summary>
public sealed class OAuthStore
{
    public const string Scope = "kopilot";
    private const int MaxClients = 100;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly RemoteOptions _options;
    private readonly ILogger<OAuthStore> _log;
    private readonly TimeProvider _time;
    private readonly string _file;
    private readonly Lock _lock = new();
    private State _state;

    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new();
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new();

    public OAuthStore(IOptions<KopilotOptions> options, ILogger<OAuthStore> log, TimeProvider? time = null)
    {
        _options = options.Value.Remote;
        _log = log;
        _time = time ?? TimeProvider.System;
        _file = _options.ResolveStateFile();
        _state = Load();
    }

    public string StateFile => _file;

    private DateTimeOffset Now => _time.GetUtcNow();

    // ------------------------------------------------------------------ Clients (Dynamic Client Registration)

    public RegisteredClient RegisterClient(string? clientName, IReadOnlyList<string> redirectUris)
    {
        var client = new RegisteredClient
        {
            ClientId = Secrets.NewToken("kpc_"),
            ClientName = string.IsNullOrWhiteSpace(clientName) ? "Unbenannter Client" : clientName.Trim()[..Math.Min(clientName.Trim().Length, 100)],
            RedirectUris = [.. redirectUris],
            CreatedUtc = Now,
        };

        lock (_lock)
        {
            _state.Clients.Add(client);
            PruneLocked();
            SaveLocked();
        }

        _log.LogInformation("OAuth: Client registriert: {Name} ({Redirects})", client.ClientName, string.Join(", ", client.RedirectUris));
        return client;
    }

    public RegisteredClient? FindClient(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        lock (_lock)
        {
            return _state.Clients.FirstOrDefault(c => c.ClientId == clientId);
        }
    }

    // ------------------------------------------------------------------ Offene Freigaben (zwischen GET und POST /authorize)

    public string AddPending(PendingAuthorization pending)
    {
        foreach (var (key, value) in _pending)
        {
            if (value.CreatedUtc + PendingLifetime < Now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        var id = Secrets.NewToken();
        _pending[id] = pending with { CreatedUtc = Now };
        return id;
    }

    public PendingAuthorization? TakePending(string? requestId) =>
        requestId is not null && _pending.TryRemove(requestId, out var p) && p.CreatedUtc + PendingLifetime >= Now ? p : null;

    public PendingAuthorization? PeekPending(string? requestId) =>
        requestId is not null && _pending.TryGetValue(requestId, out var p) && p.CreatedUtc + PendingLifetime >= Now ? p : null;

    // ------------------------------------------------------------------ Autorisierungscodes

    public string IssueCode(PendingAuthorization pending)
    {
        var code = Secrets.NewToken("kpcode_");
        _codes[Secrets.Hash(code)] = new AuthorizationCode(pending.ClientId, pending.RedirectUri, pending.CodeChallenge, pending.Resource, Now + CodeLifetime);
        return code;
    }

    /// <summary>Löst einen Code genau einmal ein. Prüft Client, Redirect-URI und PKCE.</summary>
    public TokenResult RedeemCode(string? code, string? clientId, string? redirectUri, string? codeVerifier, string? resource)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(codeVerifier))
        {
            return TokenResult.Error("invalid_request", "code und code_verifier sind Pflicht.");
        }

        if (!_codes.TryRemove(Secrets.Hash(code), out var entry) || entry.ExpiresUtc < Now)
        {
            return TokenResult.Error("invalid_grant", "Code ungültig, abgelaufen oder schon benutzt.");
        }

        if (entry.ClientId != clientId)
        {
            return TokenResult.Error("invalid_grant", "Code gehört zu einem anderen Client.");
        }

        if (redirectUri is not null && redirectUri != entry.RedirectUri)
        {
            return TokenResult.Error("invalid_grant", "redirect_uri stimmt nicht mit der Autorisierung überein.");
        }

        if (!Secrets.FixedTimeEquals(Secrets.PkceChallenge(codeVerifier), entry.CodeChallenge))
        {
            return TokenResult.Error("invalid_grant", "PKCE-Prüfung fehlgeschlagen.");
        }

        if (resource is not null && !ResourceMatches(resource))
        {
            return TokenResult.Error("invalid_target", "Unbekannte Ressource.");
        }

        return IssueTokens(entry.ClientId);
    }

    // ------------------------------------------------------------------ Tokens

    /// <summary>Tauscht einen Refresh-Token gegen neue Tokens. Der alte Refresh-Token wird dabei ungültig (Rotation).</summary>
    public TokenResult Refresh(string? refreshToken, string? clientId)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return TokenResult.Error("invalid_request", "refresh_token fehlt.");
        }

        var hash = Secrets.Hash(refreshToken);
        lock (_lock)
        {
            var entry = _state.RefreshTokens.FirstOrDefault(t => t.Hash == hash);
            if (entry is null || entry.ExpiresUtc < Now)
            {
                return TokenResult.Error("invalid_grant", "Refresh-Token ungültig oder abgelaufen.");
            }

            if (clientId is not null && entry.ClientId != clientId)
            {
                return TokenResult.Error("invalid_grant", "Refresh-Token gehört zu einem anderen Client.");
            }

            _state.RefreshTokens.Remove(entry);
            return IssueTokensLocked(entry.ClientId);
        }
    }

    public bool ValidateAccessToken(string token)
    {
        var hash = Secrets.Hash(token);
        lock (_lock)
        {
            return _state.AccessTokens.Any(t => t.Hash == hash && t.ExpiresUtc > Now);
        }
    }

    public bool ResourceMatches(string resource) =>
        string.Equals(resource.Trim().TrimEnd('/'), _options.ResourceUrl, StringComparison.OrdinalIgnoreCase);

    private TokenResult IssueTokens(string clientId)
    {
        lock (_lock)
        {
            return IssueTokensLocked(clientId);
        }
    }

    private TokenResult IssueTokensLocked(string clientId)
    {
        var access = Secrets.NewToken("kpa_");
        var refresh = Secrets.NewToken("kpr_");
        var accessLifetime = TimeSpan.FromMinutes(Math.Clamp(_options.AccessTokenMinutes, 5, 24 * 60));
        var refreshLifetime = TimeSpan.FromDays(Math.Clamp(_options.RefreshTokenDays, 1, 365));

        _state.AccessTokens.Add(new StoredToken { Hash = Secrets.Hash(access), ClientId = clientId, ExpiresUtc = Now + accessLifetime });
        _state.RefreshTokens.Add(new StoredToken { Hash = Secrets.Hash(refresh), ClientId = clientId, ExpiresUtc = Now + refreshLifetime });
        PruneLocked();
        SaveLocked();

        return TokenResult.Success(access, refresh, (int)accessLifetime.TotalSeconds);
    }

    // ------------------------------------------------------------------ Persistenz

    private void PruneLocked()
    {
        var now = Now;
        _state.AccessTokens.RemoveAll(t => t.ExpiresUtc < now);
        _state.RefreshTokens.RemoveAll(t => t.ExpiresUtc < now);

        if (_state.Clients.Count > MaxClients)
        {
            var activeClients = _state.RefreshTokens.Select(t => t.ClientId).ToHashSet();
            var removable = _state.Clients
                .Where(c => !activeClients.Contains(c.ClientId))
                .OrderBy(c => c.CreatedUtc)
                .Take(_state.Clients.Count - MaxClients)
                .ToHashSet();
            _state.Clients.RemoveAll(removable.Contains);
        }
    }

    private State Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_file), FileJson) ?? new State();
                _log.LogInformation("OAuth-Zustand geladen: {Clients} Client(s), {Tokens} aktive Verbindung(en) – {File}", state.Clients.Count, state.RefreshTokens.Count, _file);
                return state;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "OAuth-Zustand konnte nicht gelesen werden, starte leer: {File}", _file);
        }

        return new State();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, FileJson));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "OAuth-Zustand konnte nicht gespeichert werden: {File}", _file);
        }
    }

    // ------------------------------------------------------------------ Typen

    private sealed class State
    {
        public List<RegisteredClient> Clients { get; set; } = [];

        public List<StoredToken> AccessTokens { get; set; } = [];

        public List<StoredToken> RefreshTokens { get; set; } = [];
    }

    private sealed class StoredToken
    {
        public string Hash { get; set; } = "";

        public string ClientId { get; set; } = "";

        public DateTimeOffset ExpiresUtc { get; set; }
    }

    private sealed record AuthorizationCode(string ClientId, string RedirectUri, string CodeChallenge, string? Resource, DateTimeOffset ExpiresUtc);
}

public sealed class RegisteredClient
{
    public string ClientId { get; set; } = "";

    public string ClientName { get; set; } = "";

    public List<string> RedirectUris { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed record PendingAuthorization(
    string ClientId,
    string ClientName,
    string RedirectUri,
    string? State,
    string CodeChallenge,
    string? Resource,
    DateTimeOffset CreatedUtc = default);

public sealed record TokenResult(bool Ok, string? AccessToken, string? RefreshToken, int ExpiresIn, string? ErrorCode, string? ErrorDescription)
{
    public static TokenResult Success(string access, string refresh, int expiresIn) => new(true, access, refresh, expiresIn, null, null);

    public static TokenResult Error(string code, string description) => new(false, null, null, 0, code, description);
}
