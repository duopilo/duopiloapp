using System.Net;
using System.Text.Json;
using Duopilo.Aircraft;
using Duopilo.Dashboard;
using Duopilo.FlightPlan;
using Duopilo.Hosting;
using Duopilo.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace Duopilo.Tests;

/// <summary>Dashboard im Browser: nur lokal, Schriften eingebettet, Knöpfe nur mit Header.</summary>
public sealed class DashboardTests : IAsyncLifetime
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"duopilo-test-{Guid.NewGuid():N}.json");
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
        Assert.Contains("<title>Duopilo</title>", html);
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
    public async Task Knoepfe_brauchen_den_Duopilo_Header()
    {
        var client = _app.Local();
        using var without = await client.PostAsync("/api/reload-profiles", null);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reload-profiles");
        request.Headers.Add("X-Duopilo", "1");
        using var with = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task Alter_Kopilot_Header_gilt_weiter()
    {
        // Ein noch offener Dashboard-Tab aus 0.9.x schickt "X-Kopilot: 1"
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reload-profiles");
        request.Headers.Add("X-Kopilot", "1");
        using var response = await _app.Local().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Eingabe_Schalter_nur_lokal_und_mit_Header()
    {
        var client = _app.Local();
        using var dashboard = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard"));
        Assert.False(dashboard.RootElement.GetProperty("input").GetProperty("enabled").GetBoolean());

        using var without = await client.PostAsync("/api/input", new StringContent("""{"enabled":true}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);

        using var remote = await _app.Remote().PostAsync("/api/input", new StringContent("""{"enabled":true}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/input")
        {
            Content = new StringContent("""{"enabled":true}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Duopilo", "1");
        using var with = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);

        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard"));
        Assert.True(after.RootElement.GetProperty("input").GetProperty("enabled").GetBoolean());

        using var status = JsonDocument.Parse(await client.GetStringAsync("/status"));
        Assert.True(status.RootElement.GetProperty("input").GetProperty("enabled").GetBoolean());
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

    // ------------------------------------------------------------------ Großanzeige und Pause

    private static FlightSnapshot Cruise(DateTimeOffset t0, double sec, bool paused = false) => new()
    {
        TimeUtc = t0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
        Paused = paused,
        PauseKind = paused ? "full" : null,
        DataAgeSec = 0.1,
        AircraftKey = "test",
        OnGround = false,
        IasKt = 250,
        GroundSpeedKt = 420,
        VerticalSpeedFpm = 0,
        AltitudeFt = 30000,
        PressureAltitudeFt = 30000,
        AglFt = 29000,
        BankRightDeg = 0,
        FlapsIndex = 0,
        GearExtendedPct = 0,
        GearHandleDown = false,
        ApEngaged = true,
        SelectedAltitudeFt = 30000,
        Limits = new AircraftLimits { VmoKt = 350, VleKt = 280 },
    };

    [Fact]
    public void Nach_der_Pause_steht_nicht_mehr_Sim_paused_gross_da()
    {
        var engine = new MonitorEngine();
        var bus = new AlertBus();
        var t0 = DateTimeOffset.UtcNow;

        bus.Publish(engine.Evaluate(Cruise(t0, 0)));
        bus.Publish(engine.Evaluate(Cruise(t0, 11)));
        Assert.Null(DashboardData.Headline(bus, t0.AddSeconds(11)));

        bus.Publish(engine.Evaluate(Cruise(t0, 12, paused: true)));
        var paused = DashboardData.Headline(bus, t0.AddSeconds(12));
        Assert.NotNull(paused);
        Assert.Equal("Sim paused", paused.Alert.Callout);
        Assert.True(paused.Active);

        // Pause vorbei: SIM_PAUSED endet, die Großanzeige ist sofort wieder ruhig (nicht erst nach 60 s)
        var resumed = engine.Evaluate(Cruise(t0, 20));
        Assert.Contains(resumed, e => e.Rule == Rules.SimPaused && e.Kind == AlertKind.Clear);
        bus.Publish(resumed);
        Assert.Null(DashboardData.Headline(bus, t0.AddSeconds(20)));
        Assert.Null(DashboardData.Headline(bus, t0.AddSeconds(40)));

        // Erneute Pause wird wieder angezeigt
        bus.Publish(engine.Evaluate(Cruise(t0, 50, paused: true)));
        Assert.Equal("Sim paused", DashboardData.Headline(bus, t0.AddSeconds(50))?.Alert.Callout);
    }

    [Fact]
    public void Einmalige_Ansage_bleibt_eine_Minute_beendeter_Zustand_nicht()
    {
        var bus = new AlertBus();
        var t0 = DateTimeOffset.UtcNow;
        bus.Publish([new AlertEvent(0, t0, Rules.ApDisconnect, AlertSeverity.Caution, AlertKind.Event, "Autopilot off", "Autopilot aus.")]);
        bus.Publish([new AlertEvent(0, t0.AddSeconds(5), Rules.SimPaused, AlertSeverity.Info, AlertKind.Raise, "Sim paused", "Simulator pausiert (full).")]);
        bus.Publish([new AlertEvent(0, t0.AddSeconds(25), Rules.SimPaused, AlertSeverity.Info, AlertKind.Clear, null, "Sim läuft wieder.")]);

        var headline = DashboardData.Headline(bus, t0.AddSeconds(30));
        Assert.NotNull(headline);
        Assert.Equal("Autopilot off", headline.Alert.Callout);
        Assert.False(headline.Active);
        Assert.Null(DashboardData.Headline(bus, t0.AddSeconds(61)));
    }

    [Fact]
    public async Task Daten_API_setzt_die_Grossanzeige_nach_der_Pause_zurueck()
    {
        var bus = _app.App.Services.GetRequiredService<AlertBus>();
        var now = DateTimeOffset.UtcNow;

        bus.Publish([new AlertEvent(0, now, Rules.SimPaused, AlertSeverity.Info, AlertKind.Raise, "Sim paused", "Simulator pausiert (full).")]);
        using (var json = await DashboardJson())
        {
            var headline = json.RootElement.GetProperty("alerts").GetProperty("headline");
            Assert.Equal("Sim paused", headline.GetProperty("alert").GetProperty("callout").GetString());
            Assert.True(headline.GetProperty("active").GetBoolean());
        }

        bus.Publish([new AlertEvent(0, now, Rules.SimPaused, AlertSeverity.Info, AlertKind.Clear, null, "Sim läuft wieder.")]);
        using (var json = await DashboardJson())
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("alerts").GetProperty("headline").ValueKind);
        }
    }

    private async Task<JsonDocument> DashboardJson()
    {
        using var response = await _app.Local().GetAsync("/api/dashboard?since=-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
