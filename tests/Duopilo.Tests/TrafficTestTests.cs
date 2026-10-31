using System.Net;
using System.Text.Json;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace Duopilo.Tests;

/// <summary>
/// Testwerkzeug für eigenen KI-Verkehr (traffic_test_*): Bahnbezeichnung, Schwelle, Anflugplan und Auswertung. Ob MSFS das Flugzeug
/// wirklich so fliegt, zeigt erst der Test im Sim.
/// </summary>
public sealed class TrafficTestTests
{
    // Bahn ähnlich EDDL 05R/23L: Mitte, Kurs 47° (primäres Ende 05R), 3000 m lang, 45 m breit, 45 m hoch
    private static readonly AirportRunway Runway = new(51.2895, 6.7668, 45, 47, 3000, 45, "05R", "23L");

    private static RunwayEnd End(string designation) => RunwayEnd.Find("EDDL", [Runway], designation)!;

    [Theory]
    [InlineData(5, 2, "05R")]
    [InlineData(23, 1, "23L")]
    [InlineData(18, 3, "18C")]
    [InlineData(36, 0, "36")]
    [InlineData(37, 0, "N")]
    [InlineData(0, 1, "")]
    public void Bahnbezeichnung_aus_Nummer_und_Zusatz(int number, int designator, string expected) =>
        Assert.Equal(expected, AirportRunway.Designation(number, designator));

    [Theory]
    [InlineData("5R", "05R")]
    [InlineData("05r", "05R")]
    [InlineData("rwy 23L", "23L")]
    [InlineData(" RWY23L ", "23l")]
    public void Bahnbezeichnung_ohne_fuehrende_Null_und_Schreibweise(string a, string b) =>
        Assert.Equal(AirportRunway.Normalize(a), AirportRunway.Normalize(b));

    [Fact]
    public void Primaeres_Ende_Schwelle_entgegen_dem_Kurs()
    {
        var end = End("5R");

        Assert.Equal("05R", end.Designation);
        Assert.Equal(47, end.HeadingTrueDeg, 3);
        Assert.Equal(1500, RouteMath.DistanceNm(Runway.Latitude, Runway.Longitude, end.ThresholdLatitude, end.ThresholdLongitude) * RunwayEnd.MetersPerNm, 0);
        Assert.Equal(227, RouteMath.BearingDeg(Runway.Latitude, Runway.Longitude, end.ThresholdLatitude, end.ThresholdLongitude), 0);
        Assert.Equal(45 * RunwayEnd.FeetPerMeter, end.ElevationFt, 1);
    }

    [Fact]
    public void Sekundaeres_Ende_Kurs_umgedreht_Schwelle_am_anderen_Ende()
    {
        var end = End("23L");

        Assert.Equal("23L", end.Designation);
        Assert.Equal(227, end.HeadingTrueDeg, 3);
        Assert.Equal(47, RouteMath.BearingDeg(Runway.Latitude, Runway.Longitude, end.ThresholdLatitude, end.ThresholdLongitude), 0);
    }

    [Fact]
    public void Unbekannte_Bahn_gibt_null()
    {
        Assert.Null(RunwayEnd.Find("EDDL", [Runway], "23R"));
        Assert.Null(RunwayEnd.Find("EDDL", [Runway], " "));
    }

    [Fact]
    public void Lage_zur_Mittellinie_davor_negativ_rechts_positiv()
    {
        var end = End("23L");
        var (lat, lon) = end.Point(-1852);
        var (along, cross) = end.Offsets(lat, lon);
        Assert.Equal(-1852, along, 0);
        Assert.Equal(0, cross, 0);

        // 50 m rechts der Mittellinie, 300 m hinter der Schwelle
        var (tdLat, tdLon) = end.Point(300);
        var (rLat, rLon) = RouteMath.Destination(tdLat, tdLon, end.HeadingTrueDeg + 90, 50 / RunwayEnd.MetersPerNm);
        var (along2, cross2) = end.Offsets(rLat, rLon);
        Assert.Equal(300, along2, 0);
        Assert.Equal(50, cross2, 0);
    }

    [Fact]
    public void Plan_A1_spawn_5_NM_Fahrwerk_ab_Spawn_Gleitpfad_und_Ausrollen()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1);

        Assert.Equal(5, plan.SpawnNm);
        Assert.True(plan.GearAtSpawn);
        Assert.Null(plan.GearDownNm);
        Assert.Empty(plan.Warnings);
        Assert.Equal(end.HeadingTrueDeg, plan.Spawn.HeadingTrueDeg, 3);
        Assert.False(plan.Spawn.OnGround);

        // 3° auf 5 NM ≈ 1590 ft über der Schwelle, plus 50 ft Überflughöhe
        Assert.InRange(plan.Spawn.AltitudeFt - end.ElevationFt, 1600, 1700);
        var (spawnAlong, _) = end.Offsets(plan.Spawn.Latitude, plan.Spawn.Longitude);
        Assert.Equal(-5 * RunwayEnd.MetersPerNm, spawnAlong, 0);

        var names = plan.Waypoints.Select(w => w.Name).ToList();
        Assert.Equal(["4nm", "3nm", "2nm", "1nm", "0.5nm", "threshold", "touchdown", "rollout1", "rollout2", "stop", "beyondStop"], names);
        Assert.Equal(300, plan.Waypoints.Single(w => w.Name == "touchdown").AlongM);

        // Entlang der Mittellinie aufsteigend, in der Luft fallende Höhe
        Assert.Equal(plan.Waypoints.Select(w => w.AlongM).OrderBy(a => a), plan.Waypoints.Select(w => w.AlongM));
        var air = plan.Waypoints.TakeWhile(w => w.Name != "touchdown").ToList();
        Assert.All(air, w => Assert.False(w.Waypoint.Flags.HasFlag(AiWaypointFlags.OnGround)));
        Assert.Equal(air.Select(w => w.Waypoint.AltitudeFt).OrderByDescending(a => a), air.Select(w => w.Waypoint.AltitudeFt));
        Assert.Equal(end.ElevationFt + 50, plan.Waypoints.Single(w => w.Name == "threshold").Waypoint.AltitudeFt, 3);

        var ground = plan.Waypoints.SkipWhile(w => w.Name != "touchdown").ToList();
        Assert.All(ground, w => Assert.True(w.Waypoint.Flags.HasFlag(AiWaypointFlags.OnGround)));
        Assert.Equal(ground.Select(w => w.Waypoint.SpeedKt).OrderByDescending(s => s), ground.Select(w => w.Waypoint.SpeedKt));

        // Halt mit Abstand zum Bahnende, letzter Wegpunkt 60 m dahinter
        Assert.Equal(plan.StopM, plan.Waypoints.Single(w => w.Name == "stop").AlongM);
        Assert.Equal(plan.StopM + 60, plan.Waypoints[^1].AlongM, 3);
        Assert.True(plan.Waypoints[^1].AlongM <= end.LengthM - 100);
        Assert.All(plan.Waypoints, w => Assert.True(w.Waypoint.Flags.HasFlag(AiWaypointFlags.SpeedRequested)));
    }

    [Fact]
    public void Plan_A2_spawn_15_NM_Fahrwerk_bei_10_NM()
    {
        var plan = TrafficTestPlan.Build(End("23L"), TrafficTestVariant.A2);

        Assert.Equal(15, plan.SpawnNm);
        Assert.False(plan.GearAtSpawn);
        Assert.Equal(10, plan.GearDownNm);
        Assert.Equal(["12nm", "10nm", "8nm", "6nm", "4nm"], plan.Waypoints.Take(5).Select(w => w.Name));
        Assert.Equal(160, plan.Waypoints[0].Waypoint.SpeedKt);
        Assert.Equal(140, plan.Waypoints.Single(w => w.Name == "4nm").Waypoint.SpeedKt);
        Assert.Equal(160u, plan.Spawn.AirspeedKt);
    }

    [Fact]
    public void Kurze_Bahn_mit_Warnung_zu_kurze_wirft()
    {
        var shortRunway = Runway with { LengthM = 1500 };
        var plan = TrafficTestPlan.Build(RunwayEnd.Find("EDXX", [shortRunway], "23L")!, TrafficTestVariant.A1);
        Assert.Contains("shortRunway", plan.Warnings);
        Assert.True(plan.Waypoints[^1].AlongM <= 1500 - 100);

        var tooShort = Runway with { LengthM = 900 };
        Assert.Throws<ArgumentException>(() => TrafficTestPlan.Build(RunwayEnd.Find("EDXX", [tooShort], "23L")!, TrafficTestVariant.A1));
    }

    [Fact]
    public void Auswertung_Anflug_Aufsetzen_Halt()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1);
        var start = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, start);
        var t = 0;

        // Anflug: von 5 NM bis zur Schwelle auf dem Gleitpfad, 10 kt zu schnell, Fahrwerk draußen
        for (var nm = 5.0; nm > 0; nm -= 0.25)
        {
            Add(analysis, end, start, t++, -nm * RunwayEnd.MetersPerNm, TrafficTestPlan.GlidePathFt(end, nm), ias: 150, gs: 150, onGround: false, agl: TrafficTestPlan.GlidePathFt(end, nm) - end.ElevationFt);
        }

        Add(analysis, end, start, t++, 100, end.ElevationFt + 20, ias: 135, gs: 135, onGround: false, agl: 20, vs: -700);
        var touchdown = Add(analysis, end, start, t++, 450, end.ElevationFt, ias: 128, gs: 128, onGround: true, agl: 0);
        Add(analysis, end, start, t++, 900, end.ElevationFt, ias: 70, gs: 70, onGround: true, agl: 0);
        Add(analysis, end, start, t++, 1500, end.ElevationFt, ias: 20, gs: 20, onGround: true, agl: 0);
        var stopped = Add(analysis, end, start, t++, plan.StopM + 14, end.ElevationFt, ias: 0, gs: 0.3, onGround: true, agl: 0);

        Assert.Contains("touchdown", touchdown.Events);
        Assert.Contains("stopped", stopped.Events);

        var summary = analysis.Summary();
        Assert.Equal("stopped", summary.Verdict);
        Assert.Equal(450, summary.Touchdown!.AlongM);
        Assert.Equal(700, summary.Touchdown.SinkRateFpm);
        Assert.Equal(0, summary.FloatSeconds);
        Assert.False(summary.AirborneBeyondRunwayEnd);
        Assert.Equal(46, summary.StopShortOfLastWaypointM);
        Assert.Equal(5, summary.GearExtendedAtNm);
        Assert.InRange(summary.MeanSpeedDeviationKt!.Value, 9, 11);
        Assert.Contains(summary.Gates, g => g.Name == "2nm" && g.IndicatedKt == 150 && g.TargetKt == 140);
        Assert.Contains(summary.Gates, g => g.Name == "threshold");
        Assert.Equal(["SIMPLE_LANDING"], summary.AiStates);
    }

    [Fact]
    public void Auswertung_schweben_ueber_der_Bahn()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1);
        var start = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, start);

        // Wie in mrlm-net/simconnect #289 ohne Fahrwerk: 12 ft, 132 kt, über das Bahnende hinaus
        var t = 0;
        for (var along = 0.0; along <= end.LengthM + 400; along += 68)
        {
            Add(analysis, end, start, t++, along, end.ElevationFt + 12, ias: 132, gs: 132, onGround: false, agl: 12, gear: 0);
        }

        var summary = analysis.Summary();
        Assert.Equal("floated", summary.Verdict);
        Assert.True(summary.AirborneBeyondRunwayEnd);
        Assert.True(summary.FloatSeconds >= 30);
        Assert.Null(summary.Touchdown);
        Assert.Null(summary.GearExtendedAtNm);
    }

    [Fact]
    public void Fahrwerk_zaehlt_der_am_weitesten_ausgefahrene_Wert()
    {
        // Wie beim vollen A320neo als KI: GEAR TOTAL PCT EXTENDED bleibt bei 1 %, die einzelnen Beine melden mehr
        var sample = new AiProbeSample(1, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, true, true, 1, 0, 100, 98, null);
        Assert.Equal(100, sample.GearExtendedPercent);

        var retracted = sample with { GearLeftPercent = 0, GearRightPercent = 0 };
        Assert.Equal(1, retracted.GearExtendedPercent);
    }

    [Theory]
    [InlineData(AiGearMethod.All, "all")]
    [InlineData(AiGearMethod.Down, "down")]
    [InlineData(AiGearMethod.Set, "set")]
    [InlineData(AiGearMethod.Handle, "handle")]
    public void Fahrwerks_Methode_heisst_wie_der_Tool_Parameter(AiGearMethod method, string name)
    {
        Assert.Equal(name, TrafficTestService.GearName(method));
        Assert.True(Enum.TryParse<AiGearMethod>(name, ignoreCase: true, out var parsed));
        Assert.Equal(method, parsed);
    }

    [Fact]
    public void Standardmodell_bevorzugt_FSLTL_A320()
    {
        AiModel[] models =
        [
            new("Asobo A320neo", "Default"),
            new("FSLTL A320 CFM", "Lufthansa"),
            new("FSLTL B738", "Ryanair"),
        ];

        Assert.Equal("FSLTL A320 CFM", TrafficTestService.PickDefaultModel(models)!.Title);
        Assert.Equal("Asobo A320neo", TrafficTestService.PickDefaultModel(models[..1])!.Title);
        Assert.Null(TrafficTestService.PickDefaultModel([models[2]]));
    }

    [Fact]
    public void Modellfilter_alle_Woerter_ohne_Doppelte()
    {
        AiModel[] models =
        [
            new("FSLTL A320 CFM", "Lufthansa"),
            new("FSLTL A320 CFM", "Lufthansa"),
            new("FSLTL A320 CFM", "Eurowings"),
            new("FSLTL B738", "Ryanair"),
        ];

        Assert.Equal(3, TrafficTestService.Filter(models, null).Count);
        Assert.Single(TrafficTestService.Filter(models, "a320 lufthansa"));
        Assert.Empty(TrafficTestService.Filter(models, "A321"));
    }

    [Fact]
    public async Task Endpunkt_meldet_aus_und_leer()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"duopilo-test-{Guid.NewGuid():N}.json");
        await using var app = await TestApp.StartAsync(stateFile);
        try
        {
            using var response = await app.Local().GetAsync("/traffic-test");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("idle", json.RootElement.GetProperty("status").GetProperty("state").GetString());

            var tests = app.App.Services.GetRequiredService<TrafficTestService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tests.StartTestAsync(new TrafficTestRequest("EDDL", "23L", TrafficTestVariant.A1), CancellationToken.None));
            Assert.Equal(Loc.T("tools.trafficTest.disabled"), ex.Message);
        }
        finally
        {
            if (File.Exists(stateFile))
            {
                File.Delete(stateFile);
            }
        }
    }

    private static TrafficTestPoint Add(
        TrafficTestAnalysis analysis,
        RunwayEnd end,
        DateTimeOffset start,
        int seconds,
        double alongM,
        double altitudeFt,
        double ias,
        double gs,
        bool onGround,
        double agl,
        double vs = -700,
        double gear = 100)
    {
        var (lat, lon) = end.Point(alongM);
        return analysis.Add(new AiProbeSample(1, start.AddSeconds(seconds), lat, lon, altitudeFt, agl, ias, gs, vs, end.HeadingTrueDeg, onGround, gear > 0, gear, 0, 0, 0, "SIMPLE_LANDING"));
    }
}
