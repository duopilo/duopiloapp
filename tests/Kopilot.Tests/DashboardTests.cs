using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kopilot.FlightPlan;
using Kopilot.Hosting;

namespace Kopilot.Tests;

/// <summary>Dashboard im Browser: nur lokal, Schriften eingebettet, Knöpfe nur mit Header.</summary>
public sealed class DashboardTests : IAsyncLifetime
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

    [Fact]
    public async Task Dashboard_wird_lokal_ausgeliefert()
    {
        using var response = await _app.Local().GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Kopilot</title>", html);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Dashboard_ist_ueber_den_Tunnel_nicht_erreichbar()
    {
        using var page = await _app.Remote().GetAsync("/");
        using var api = await _app.Remote().GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
    }

    [Fact]
    public async Task Daten_API_liefert_JSON_auch_ohne_Sim()
    {
        using var response = await _app.Local().GetAsync("/api/dashboard?since=-1&log=true");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(root.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sim").ValueKind);
        Assert.Equal(0, root.GetProperty("alerts").GetProperty("lastId").GetInt64());
        Assert.False(root.GetProperty("plan").GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("log").ValueKind);
    }

    [Theory]
    [InlineData("/dashboard/fonts/B612-Regular.woff", "font/woff")]
    [InlineData("/dashboard/fonts/B612Mono-Bold.woff", "font/woff")]
    [InlineData("/favicon.ico", "image/x-icon")]
    public async Task Schriften_und_Symbol_sind_eingebettet(string path, string contentType)
    {
        using var response = await _app.Local().GetAsync(path);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public async Task Unbekannte_Schrift_gibt_404()
    {
        using var response = await _app.Local().GetAsync("/dashboard/fonts/..%2Fappsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Knoepfe_brauchen_den_Kopilot_Header()
    {
        var client = _app.Local();
        using var without = await client.PostAsync("/api/reload-profiles", null);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reload-profiles");
        request.Headers.Add("X-Kopilot", "1");
        using var with = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task Sprachausgabe_aus_dann_liest_der_Browser_und_Knoepfe_brauchen_den_Header()
    {
        var client = _app.Local();
        using var data = await client.GetAsync("/api/dashboard");
        using var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync());
        var speech = json.RootElement.GetProperty("speech");

        using var without = await client.PostAsync("/api/speech", JsonContent.Create(new { enabled = false }));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/speech") { Content = JsonContent.Create(new { enabled = false }) };
        request.Headers.Add("X-Kopilot", "1");
        using var with = await client.SendAsync(request);
        using var answer = JsonDocument.Parse(await with.Content.ReadAsStringAsync());

        using var remote = await _app.Remote().PostAsync("/api/speech/test", JsonContent.Create(new { }));

        Assert.False(speech.GetProperty("available").GetBoolean());
        Assert.Equal("Off", speech.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        Assert.Equal(JsonValueKind.String, answer.RootElement.GetProperty("error").ValueKind);
        Assert.False(answer.RootElement.GetProperty("speech").GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);
    }

    [Fact]
    public async Task Status_JSON_liegt_jetzt_unter_status()
    {
        using var response = await _app.Local().GetAsync("/status");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.RootElement.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public void Logpuffer_behaelt_nur_die_letzten_Zeilen()
    {
        var buffer = new LogBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(new LogLine(DateTimeOffset.Now, Microsoft.Extensions.Logging.LogLevel.Information, "Test", $"Zeile {i}"));
        }

        Assert.Equal(new[] { "Zeile 4", "Zeile 5" }, buffer.Tail(2).Select(l => l.Message));
        Assert.Equal(3, buffer.Tail(10).Count);
    }

    [Fact]
    public void Fortschritt_mit_TOD_und_Treibstoff_gegen_Plan()
    {
        // Start bei 0/0, drei Fixes nach Osten (je ca. 60 nm), TOD vor dem Ziel
        var ofp = new Ofp
        {
            Origin = new OfpAirport("AAAA", null, "09", 0, 0, null, null, null, null),
            Destination = new OfpAirport("BBBB", null, "27L", 0, 3, null, null, null, null),
            Fuel = new OfpFuel { PlanTakeoffKg = 10000, PlanLandingKg = 6000 },
            Fixes =
            [
                new OfpFix { Ident = "ONE", Lat = 0, Lon = 1, FuelPlanOnboardKg = 9000 },
                new OfpFix { Ident = "TOD", Lat = 0, Lon = 2, FuelPlanOnboardKg = 7000 },
                new OfpFix { Ident = "BBBB", Lat = 0, Lon = 3, FuelPlanOnboardKg = 6000 },
            ],
        };

        var points = ofp.RoutePoints();
        var route = RouteTracker.Locate(points, 0, 1.5);
        Assert.NotNull(route);

        // Halb zwischen ONE (9000 kg) und TOD (7000 kg) sind 8000 kg geplant – an Bord sind 8300
        var progress = ProgressCalculator.Compute(ofp, points, route, actualFuelKg: 8300);

        Assert.Equal("TOD", progress.Next.Ident);
        Assert.NotNull(progress.TopOfDescent);
        Assert.InRange(progress.TopOfDescent.DistanceNm, 29, 31);
        Assert.Null(progress.TopOfClimb);
        Assert.InRange(progress.PlannedFuelHereKg ?? 0, 7990, 8010);
        Assert.InRange(progress.FuelDifferenceKg ?? 0, 290, 310);
        Assert.InRange(progress.PredictedLandingFuelKg ?? 0, 6290, 6310);
        Assert.InRange(progress.TotalDistanceNm, 179, 181);
        Assert.True(progress.OnRoute);
        Assert.Equal(new[] { "BBBB" }, progress.Following);
    }
}
