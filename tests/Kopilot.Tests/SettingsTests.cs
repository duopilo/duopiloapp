using System.Net;
using System.Text;
using System.Text.Json;
using Kopilot.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kopilot.Tests;

/// <summary>Einstellungsseite im Dashboard: schreibt in appsettings.Local.json, ohne Kommentare zu verlieren, und verrät keine Geheimnisse.</summary>
public sealed class SettingsTests : IAsyncLifetime
{
    /// <summary>So legt der Installer die Datei an (nur Kommentare im Objekt).</summary>
    private const string InstallerFile = """
        {
          // Eigene Einstellungen (SimBrief, Zugang für Claude, abgeschaltete Regeln …).
          // Alle Felder mit Erklärung: appsettings.Local.example.json im selben Ordner.
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kopilot-settings-tests", Guid.NewGuid().ToString("N"));
    private string _stateFile = "";
    private string _settingsFile = "";
    private TestApp _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _stateFile = Path.Combine(_directory, "oauth-state.json");
        _settingsFile = Path.Combine(_directory, "appsettings.Local.json");
        File.WriteAllText(_settingsFile, InstallerFile);
        var backup = Path.Combine(_directory, "backup.json");
        _app = await TestApp.StartAsync(_stateFile, services: s => s.AddSingleton(new LocalSettingsStore(_settingsFile, backup)));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Einstellungen_nur_lokal_und_ohne_Geheimnisse()
    {
        using var remote = await _app.Remote().GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);

        var text = await _app.Local().GetStringAsync("/api/settings");
        using var json = JsonDocument.Parse(text);
        var claude = json.RootElement.GetProperty("claude");

        Assert.Equal(_settingsFile, json.RootElement.GetProperty("file").GetString());
        Assert.Equal("http://127.0.0.1:5155/mcp", claude.GetProperty("mcpUrl").GetString());
        Assert.Equal(TestApp.ResourceUrl, claude.GetProperty("connectorUrl").GetString());
        Assert.True(claude.GetProperty("oauth").GetProperty("active").GetBoolean());
        Assert.True(claude.GetProperty("bearer").GetProperty("active").GetBoolean());
        Assert.True(claude.GetProperty("consentPassword").GetProperty("set").GetBoolean());
        Assert.False(json.RootElement.GetProperty("restart").GetProperty("required").GetBoolean());
        Assert.DoesNotContain(TestApp.BearerToken, text);
        Assert.DoesNotContain(TestApp.Password, text);
    }

    [Theory]
    [InlineData("/api/settings/simbrief", """{"username":"flo"}""")]
    [InlineData("/api/settings/claude", """{"publicUrl":""}""")]
    [InlineData("/api/settings/claude/token", """{"action":"new"}""")]
    [InlineData("/api/settings/claude/check", "{}")]
    [InlineData("/api/settings/modules", """{"monitoring":false}""")]
    [InlineData("/api/restart", "{}")]
    [InlineData("/api/report/capture", "{}")]
    [InlineData("/api/report", """{"id":"x","description":"y"}""")]
    public async Task Schreibende_Aufrufe_brauchen_den_Header_und_sind_ueber_den_Tunnel_gesperrt(string url, string body)
    {
        using var without = await _app.Local().PostAsync(url, Json(body));
        using var remote = await _app.Remote().PostAsync(url, Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);
        Assert.Equal(InstallerFile, File.ReadAllText(_settingsFile));
    }

    [Fact]
    public async Task SimBrief_wird_gespeichert_und_gilt_sofort()
    {
        using var dashboardBefore = JsonDocument.Parse(await _app.Local().GetStringAsync("/api/dashboard"));
        Assert.False(dashboardBefore.RootElement.GetProperty("plan").GetProperty("configured").GetBoolean());

        var result = await PostAsync("/api/settings/simbrief", """{"username":"flo210500","pilotId":"123456"}""");

        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var simBrief = result.GetProperty("settings").GetProperty("simBrief");
        Assert.Equal("flo210500", simBrief.GetProperty("username").GetString());
        Assert.Equal("123456", simBrief.GetProperty("pilotId").GetString());
        Assert.False(result.GetProperty("settings").GetProperty("restart").GetProperty("required").GetBoolean());

        var file = File.ReadAllText(_settingsFile);
        Assert.Contains("// Eigene Einstellungen", file);
        var saved = Load();
        Assert.Equal("flo210500", saved["Kopilot:SimBrief:Username"]);
        Assert.Equal("123456", saved["Kopilot:SimBrief:PilotId"]);

        using var dashboardAfter = JsonDocument.Parse(await _app.Local().GetStringAsync("/api/dashboard"));
        Assert.True(dashboardAfter.RootElement.GetProperty("plan").GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Falsche_Pilot_ID_wird_abgelehnt_und_nichts_geschrieben()
    {
        var result = await PostAsync("/api/settings/simbrief", """{"pilotId":"12ab"}""");

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("Ziffern", result.GetProperty("error").GetString());
        Assert.Equal(InstallerFile, File.ReadAllText(_settingsFile));
    }

    [Fact]
    public async Task Monitoring_und_ATC_gelten_erst_nach_dem_Neustart()
    {
        var off = await PostAsync("/api/settings/modules", """{"monitoring":false,"atc":false}""");

        Assert.True(off.GetProperty("ok").GetBoolean(), off.ToString());
        var restart = off.GetProperty("settings").GetProperty("restart");
        Assert.True(restart.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "Monitoring", "ATC" }, restart.GetProperty("changes").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.False(restart.GetProperty("available").GetBoolean());
        var modules = off.GetProperty("settings").GetProperty("modules");
        Assert.False(modules.GetProperty("monitoring").GetProperty("enabled").GetBoolean());
        Assert.True(modules.GetProperty("monitoring").GetProperty("running").GetBoolean());
        Assert.Equal("False", Load()["Kopilot:Monitoring:Enabled"]);
        Assert.Equal("False", Load()["Kopilot:Atc:Enabled"]);

        // Zurück auf den laufenden Stand: kein Neustart mehr nötig
        var on = await PostAsync("/api/settings/modules", """{"monitoring":true,"atc":true}""");
        Assert.False(on.GetProperty("settings").GetProperty("restart").GetProperty("required").GetBoolean());
        Assert.Equal("True", Load()["Kopilot:Monitoring:Enabled"]);
    }

    [Fact]
    public async Task Eingabe_gilt_sofort_Sprachschleife_gibt_es_nur_in_der_Tray_Instanz()
    {
        var input = await PostAsync("/api/settings/modules", """{"input":true}""");
        Assert.True(input.GetProperty("ok").GetBoolean());
        Assert.True(input.GetProperty("settings").GetProperty("modules").GetProperty("input").GetProperty("enabled").GetBoolean());

        using var dashboard = JsonDocument.Parse(await _app.Local().GetStringAsync("/api/dashboard"));
        Assert.True(dashboard.RootElement.GetProperty("input").GetProperty("enabled").GetBoolean());

        var voice = await PostAsync("/api/settings/modules", """{"voice":true}""");
        Assert.False(voice.GetProperty("ok").GetBoolean());
        Assert.Contains("Tray", voice.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Neuer_Bearer_Token_kommt_genau_einmal_zurueck()
    {
        var result = await PostAsync("/api/settings/claude/token", """{"action":"new"}""");

        var token = result.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 43);
        Assert.Equal(token, Load()["Kopilot:Remote:BearerToken"]);
        Assert.True(result.GetProperty("settings").GetProperty("restart").GetProperty("required").GetBoolean());

        var view = await _app.Local().GetStringAsync("/api/settings");
        Assert.DoesNotContain(token, view);

        var removed = await PostAsync("/api/settings/claude/token", """{"action":"remove"}""");
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("token").ValueKind);
        Assert.Equal("", Load()["Kopilot:Remote:BearerToken"]);
    }

    [Theory]
    [InlineData("""{"publicUrl":"http://kopilot.example.com"}""", "https://")]
    [InlineData("""{"publicUrl":"https://kopilot.example.com/mcp"}""", "ohne /mcp")]
    [InlineData("""{"consentPassword":"zu-kurz"}""", "12 Zeichen")]
    public async Task Ungueltiger_Zugang_wird_abgelehnt(string body, string message)
    {
        var result = await PostAsync("/api/settings/claude", body);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains(message, result.GetProperty("error").GetString());
        Assert.Equal(InstallerFile, File.ReadAllText(_settingsFile));
    }

    [Fact]
    public async Task Zugang_fuer_Claude_wird_gespeichert_und_gilt_nach_dem_Neustart()
    {
        var result = await PostAsync("/api/settings/claude", """{"publicUrl":"https://kopilot.example.com/","consentPassword":"ein-langes-passwort"}""");

        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var claude = result.GetProperty("settings").GetProperty("claude");
        Assert.Equal("https://kopilot.example.com", claude.GetProperty("publicUrl").GetString());
        Assert.Equal("https://kopilot.example.com/mcp", claude.GetProperty("connectorUrl").GetString());
        var changes = result.GetProperty("settings").GetProperty("restart").GetProperty("changes").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("Öffentliche Adresse (Tunnel)", changes);
        Assert.Contains("Freigabe-Passwort", changes);
        Assert.Equal("https://kopilot.example.com", Load()["Kopilot:Remote:PublicUrl"]);
        Assert.Equal("ein-langes-passwort", Load()["Kopilot:Remote:ConsentPassword"]);
        Assert.DoesNotContain("ein-langes-passwort", result.ToString());
    }

    [Fact]
    public async Task Neustart_gibt_es_nur_in_der_Tray_Instanz()
    {
        var result = await PostAsync("/api/restart", "{}");

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("Tray", result.GetProperty("error").GetString());
    }

    [Fact]
    public void Leere_Datei_bekommt_lesbar_eingerueckte_Objekte()
    {
        var text = LocalSettingsStore.Patch("", [SettingValue.Of(["Kopilot", "SimBrief", "Username"], "flo"), SettingValue.Of(["Kopilot", "Monitoring", "Enabled"], false)]);

        var config = Parse(text);
        Assert.Equal("flo", config["Kopilot:SimBrief:Username"]);
        Assert.Equal("False", config["Kopilot:Monitoring:Enabled"]);
        Assert.Contains("\n    \"SimBrief\": {", text.ReplaceLineEndings("\n"));
        Assert.Contains("\n      \"Username\": \"flo\"", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Kommentare_und_vorhandene_Werte_bleiben_erhalten()
    {
        const string original = """
            {
              // SimBrief-Konto
              "Kopilot": {
                "SimBrief": { "Username": "alt" }, // hinten
                "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] }
              }
            }
            """;

        var text = LocalSettingsStore.Patch(original, [SettingValue.Of(["Kopilot", "SimBrief", "Username"], "neu"), SettingValue.Of(["Kopilot", "Atc", "Enabled"], false)]);

        Assert.Contains("// SimBrief-Konto", text);
        Assert.Contains("// hinten", text);
        var config = Parse(text);
        Assert.Equal("neu", config["Kopilot:SimBrief:Username"]);
        Assert.Equal("SPEED_DEVIATION", config["Kopilot:Monitoring:DisabledRules:0"]);
        Assert.Equal("False", config["Kopilot:Atc:Enabled"]);
    }

    [Theory]
    [InlineData("""{ "Kopilot": { "SimBrief": """)]
    [InlineData("""[ 1, 2 ]""")]
    public void Kaputte_Datei_wird_nicht_ueberschrieben(string broken)
    {
        Assert.Throws<SettingsWriteException>(() => LocalSettingsStore.Patch(broken, [SettingValue.Of(["Kopilot", "SimBrief", "Username"], "flo")]));
    }

    [Fact]
    public void Schreiben_in_die_vorhandene_Datei_mit_Sicherung()
    {
        var file = Path.Combine(_directory, "inplace.json");
        var backup = Path.Combine(_directory, "inplace.backup.json");
        File.WriteAllText(file, "{ /* x */ }");
        var store = new LocalSettingsStore(file, backup);

        store.Set([SettingValue.Of(["Kopilot", "SimBrief", "PilotId"], "42")]);

        Assert.Equal("{ /* x */ }", File.ReadAllText(backup));
        Assert.Equal("42", Parse(File.ReadAllText(file))["Kopilot:SimBrief:PilotId"]);
        Assert.NotNull(store.LastWriteUtc);
    }

    private async Task<JsonElement> PostAsync(string url, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(body) };
        request.Headers.Add("X-Kopilot", "1");
        using var response = await _app.Local().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private IConfiguration Load() => Parse(File.ReadAllText(_settingsFile));

    private static IConfiguration Parse(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
}
