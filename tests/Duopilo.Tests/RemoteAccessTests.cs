using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Kopilot.Tests;

/// <summary>
/// Prüft Zugriffsschutz und OAuth-Ablauf so, wie Claude ihn als Custom Connector durchläuft.
/// Läuft ohne MSFS (SimConnect-Stub).
/// </summary>
public sealed partial class RemoteAccessTests : IAsyncLifetime
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"kopilot-test-{Guid.NewGuid():N}.json");
    private TestApp _app = null!;

    public async Task InitializeAsync() => _app = await TestApp.StartAsync(_stateFile);

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        if (File.Exists(_stateFile))
        {
            File.Delete(_stateFile);
        }
    }

    // ------------------------------------------------------------------ Bearer / 401

    [Fact]
    public async Task Mcp_ohne_Token_liefert_401_mit_Verweis_auf_Metadaten()
    {
        using var response = await TestApp.McpInitializeAsync(_app.Remote(), bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Challenge(response);
        Assert.Contains("Bearer", challenge);
        Assert.Contains("resource_metadata=\"https://kopilot.test/.well-known/oauth-protected-resource\"", challenge);
    }

    [Fact]
    public async Task Mcp_mit_statischem_Bearer_Token_ist_erlaubt()
    {
        using var response = await TestApp.McpInitializeAsync(_app.Remote(), TestApp.BearerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_mit_falschem_Token_liefert_401_invalid_token()
    {
        using var response = await TestApp.McpInitializeAsync(_app.Remote(), "falsch-" + TestApp.BearerToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", Challenge(response));
    }

    [Fact]
    public async Task Mcp_ist_auch_lokal_nur_mit_Token_erreichbar()
    {
        using var without = await TestApp.McpInitializeAsync(_app.Local(), bearer: null);
        using var with = await TestApp.McpInitializeAsync(_app.Local(), TestApp.BearerToken);

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    // ------------------------------------------------------------------ Prüfseiten

    [Fact]
    public async Task Pruefseiten_sind_nur_lokal_erreichbar()
    {
        using var local = await _app.Local().GetAsync("/state");
        using var remote = await _app.Remote().GetAsync("/state");
        using var remoteRoot = await _app.Remote().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remoteRoot.StatusCode);
    }

    [Fact]
    public async Task Fremder_Hostname_wird_abgelehnt()
    {
        var client = _app.Local();
        client.BaseAddress = new Uri("http://evil.example/");
        using var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ Metadaten

    [Fact]
    public async Task Metadaten_sind_konsistent()
    {
        var client = _app.Remote();
        using var prm = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource"));
        using var prmWithPath = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource/mcp"));
        using var asm = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        Assert.Equal(TestApp.ResourceUrl, prm.RootElement.GetProperty("resource").GetString());
        Assert.Equal(TestApp.ResourceUrl, prmWithPath.RootElement.GetProperty("resource").GetString());
        Assert.Equal(TestApp.PublicUrl, prm.RootElement.GetProperty("authorization_servers")[0].GetString());

        var root = asm.RootElement;
        Assert.Equal(TestApp.PublicUrl, root.GetProperty("issuer").GetString());
        Assert.Equal(TestApp.PublicUrl + "/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(TestApp.PublicUrl + "/token", root.GetProperty("token_endpoint").GetString());
        Assert.Equal(TestApp.PublicUrl + "/register", root.GetProperty("registration_endpoint").GetString());
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("none", root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
    }

    // ------------------------------------------------------------------ Kompletter OAuth-Ablauf

    [Fact]
    public async Task OAuth_Ablauf_liefert_funktionierende_Tokens_und_rotiert_Refresh_Token()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);
        var (verifier, challenge) = NewPkce();

        // Freigabeseite
        var requestId = await StartAuthorizeAsync(client, clientId, TestApp.ClaudeCallback, challenge, state: "xyz");

        // Falsches Passwort: keine Weiterleitung
        using (var wrong = await ApproveAsync(client, requestId, "falsches-passwort"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Null(wrong.Headers.Location);
        }

        // Richtiges Passwort: Weiterleitung zu Claude mit Code und State
        string code;
        using (var approved = await ApproveAsync(client, requestId, TestApp.Password))
        {
            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
            var location = approved.Headers.Location!;
            Assert.StartsWith(TestApp.ClaudeCallback + "?", location.ToString());
            var query = QueryHelpers.ParseQuery(location.Query);
            Assert.Equal("xyz", query["state"].ToString());
            code = query["code"].ToString();
            Assert.False(string.IsNullOrEmpty(code));
        }

        // Code einlösen
        var tokens = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = TestApp.ClaudeCallback,
            ["code_verifier"] = verifier,
            ["resource"] = TestApp.ResourceUrl,
        });
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        var access = tokens.Json.GetProperty("access_token").GetString()!;
        var refresh = tokens.Json.GetProperty("refresh_token").GetString()!;
        Assert.Equal("Bearer", tokens.Json.GetProperty("token_type").GetString());
        Assert.True(tokens.Json.GetProperty("expires_in").GetInt32() > 0);

        // Access-Token öffnet /mcp
        using (var mcp = await TestApp.McpInitializeAsync(client, access))
        {
            Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        }

        // Code ist nur einmal gültig
        var reuse = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = TestApp.ClaudeCallback,
            ["code_verifier"] = verifier,
        });
        Assert.Equal("invalid_grant", reuse.Json.GetProperty("error").GetString());

        // Refresh rotiert: neuer Refresh-Token, alter ist danach ungültig
        var refreshed = await TokenAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        var newAccess = refreshed.Json.GetProperty("access_token").GetString()!;
        var newRefresh = refreshed.Json.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh, newRefresh);

        var oldRefresh = await TokenAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, oldRefresh.Status);
        Assert.Equal("invalid_grant", oldRefresh.Json.GetProperty("error").GetString());

        using var mcpNew = await TestApp.McpInitializeAsync(client, newAccess);
        Assert.Equal(HttpStatusCode.OK, mcpNew.StatusCode);
    }

    [Fact]
    public async Task Falscher_PKCE_Verifier_wird_abgelehnt()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);
        var (_, challenge) = NewPkce();
        var code = await AuthorizeAndGetCodeAsync(client, clientId, TestApp.ClaudeCallback, challenge);

        var result = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = TestApp.ClaudeCallback,
            ["code_verifier"] = NewPkce().Verifier,
        });

        Assert.Equal("invalid_grant", result.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Ablehnen_leitet_mit_access_denied_zurueck()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);
        var requestId = await StartAuthorizeAsync(client, clientId, TestApp.ClaudeCallback, NewPkce().Challenge, state: "s1");

        using var response = await client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["action"] = "deny",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("s1", query["state"].ToString());
    }

    // ------------------------------------------------------------------ Redirect-URIs

    [Fact]
    public async Task Registrierung_mit_fremder_Redirect_URI_wird_abgelehnt()
    {
        using var response = await _app.Remote().PostAsJsonAsync("/register", new
        {
            client_name = "Böse",
            redirect_uris = new[] { "https://evil.example/callback" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_redirect_uri", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Nicht_registrierte_Redirect_URI_wird_nie_weitergeleitet()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);

        using var response = await client.GetAsync(AuthorizeUrl(clientId, "http://localhost:9999/callback", NewPkce().Challenge, "s"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Loopback_Redirect_passt_unabhaengig_vom_Port()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, "http://localhost/callback");

        var code = await AuthorizeAndGetCodeAsync(client, clientId, "http://localhost:4711/callback", NewPkce().Challenge);

        Assert.False(string.IsNullOrEmpty(code));
    }

    // ------------------------------------------------------------------ Sperre & Persistenz

    [Fact]
    public async Task Nach_fuenf_Fehlversuchen_ist_die_Freigabe_gesperrt()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);
        var requestId = await StartAuthorizeAsync(client, clientId, TestApp.ClaudeCallback, NewPkce().Challenge, state: "s");

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await ApproveAsync(client, requestId, "falsch" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await ApproveAsync(client, requestId, TestApp.Password);
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.Null(locked.Headers.Location);
    }

    [Fact]
    public async Task Tokens_ueberstehen_einen_Neustart()
    {
        var client = _app.Remote();
        var clientId = await RegisterAsync(client, TestApp.ClaudeCallback);
        var (verifier, challenge) = NewPkce();
        var code = await AuthorizeAndGetCodeAsync(client, clientId, TestApp.ClaudeCallback, challenge);
        var tokens = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = TestApp.ClaudeCallback,
            ["code_verifier"] = verifier,
        });
        var access = tokens.Json.GetProperty("access_token").GetString()!;
        var refresh = tokens.Json.GetProperty("refresh_token").GetString()!;

        await _app.DisposeAsync();
        _app = await TestApp.StartAsync(_stateFile);
        var restarted = _app.Remote();

        using var mcp = await TestApp.McpInitializeAsync(restarted, access);
        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);

        var refreshed = await TokenAsync(restarted, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);

        // Gespeichert werden nur Hashes, nie die Tokens selbst
        var file = await File.ReadAllTextAsync(_stateFile);
        Assert.DoesNotContain(access, file);
        Assert.DoesNotContain(refresh, file);
    }

    [Fact]
    public async Task Ohne_Passwort_ist_OAuth_aus_und_nur_Bearer_moeglich()
    {
        await _app.DisposeAsync();
        _app = await TestApp.StartAsync(_stateFile, new() { ["Kopilot:Remote:ConsentPassword"] = "" });

        using var metadata = await _app.Remote().GetAsync("/.well-known/oauth-authorization-server");
        using var unauthorized = await TestApp.McpInitializeAsync(_app.Remote(), bearer: null);
        using var bearer = await TestApp.McpInitializeAsync(_app.Remote(), TestApp.BearerToken);

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain("resource_metadata", Challenge(unauthorized));
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
    }

    // ------------------------------------------------------------------ Hilfen

    /// <summary>WWW-Authenticate roh lesen (ohne Parser, der ungewohnte Parameter verwerfen könnte).</summary>
    private static string Challenge(HttpResponseMessage response) =>
        response.Headers.NonValidated.TryGetValues("WWW-Authenticate", out var values) ? string.Join(", ", values) : "";

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static async Task<string> RegisterAsync(HttpClient client, string redirectUri)
    {
        using var response = await client.PostAsJsonAsync("/register", new
        {
            client_name = "Claude",
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("client_id").GetString()!;
    }

    private static string AuthorizeUrl(string clientId, string redirectUri, string challenge, string state) =>
        QueryHelpers.AddQueryString("/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["scope"] = "kopilot offline_access",
            ["resource"] = TestApp.ResourceUrl,
        });

    private static async Task<string> StartAuthorizeAsync(HttpClient client, string clientId, string redirectUri, string challenge, string state)
    {
        using var response = await client.GetAsync(AuthorizeUrl(clientId, redirectUri, challenge, state));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var match = RequestIdPattern().Match(html);
        Assert.True(match.Success, "request_id fehlt auf der Freigabeseite");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, string requestId, string password) =>
        client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["password"] = password,
            ["action"] = "approve",
        }));

    private static async Task<string> AuthorizeAndGetCodeAsync(HttpClient client, string clientId, string redirectUri, string challenge)
    {
        var requestId = await StartAuthorizeAsync(client, clientId, redirectUri, challenge, state: "s");
        using var response = await ApproveAsync(client, requestId, TestApp.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> TokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        using var response = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return (response.StatusCode, json);
    }

    [GeneratedRegex("name=\"request_id\" value=\"([^\"]+)\"")]
    private static partial Regex RequestIdPattern();
}
