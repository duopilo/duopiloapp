using Duopilo.FlightPlan;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>Heli-Landung (H1): Anflug, Schweben und Absetzen auf einem Helipad, Bezeichnungen der Helipads.</summary>
public sealed class HeliTests
{
    private const double Nm = RunwayEnd.MetersPerNm;

    // Helipad ähnlich EDDL H2
    private static readonly HeliPad Pad = new("EDDL", "H2", 51.2820, 6.7570, 147, 230, 20, 20, "h");

    private static (HeliKinematics Heli, List<HeliPhase> Phases, double MaxSinkFpm, double MaxPitch, double MinPitch) Fly(TrafficTestPlan plan, double dt = 1.0 / 30)
    {
        var leg = plan.Heli!;
        var heli = new HeliKinematics(plan.Runway, leg.Profile, plan.SpawnNm * Nm, leg.StartHeightFt, leg.Profile.CruiseKt, leg.Pad.HeadingDeg, leg.Pad.ElevationFt, 5);
        var phases = new List<HeliPhase> { heli.Phase };
        double maxSink = 0, maxPitch = double.MinValue, minPitch = double.MaxValue;
        for (var t = 0.0; t < 600 && heli.Phase != HeliPhase.Landed; t += dt)
        {
            heli.Advance(dt);
            if (phases[^1] != heli.Phase)
            {
                phases.Add(heli.Phase);
            }

            maxSink = Math.Min(maxSink, heli.VerticalSpeedFpm);
            maxPitch = Math.Max(maxPitch, heli.PitchDeg);
            minPitch = Math.Min(minPitch, heli.PitchDeg);
        }

        return (heli, phases, maxSink, maxPitch, minPitch);
    }

    [Fact]
    public void Heli_fliegt_an_schwebt_dreht_und_setzt_auf_dem_Pad_ab()
    {
        var plan = TrafficTestPlan.BuildHeli(Pad, HeliProfile.Default, fromDeg: 140);
        var (heli, phases, maxSink, maxPitch, minPitch) = Fly(plan);

        Assert.Equal([HeliPhase.Cruise, HeliPhase.Approach, HeliPhase.Hover, HeliPhase.Descent, HeliPhase.Landed], phases);
        Assert.Equal(0, heli.DistanceM);
        Assert.Equal(0, heli.HeightFt);
        Assert.Equal(HeliProfile.Default.FinalDescentFpm, heli.TouchdownFpm);
        // Im Schweben auf die Ausrichtung des Pads gedreht (Anflugkurs 320)
        Assert.Equal(230, heli.HeadingTrueDeg, 0);
        // Nie steiler als gut 1000 ft/min, Nase zwischen −3° (Fahrt) und gut 5° (Verzögern)
        Assert.InRange(maxSink, -1050, 0);
        Assert.InRange(minPitch, -3.1, 0);
        Assert.InRange(maxPitch, 2, 6);
        // Verzögert erst in der letzten Meile
        Assert.InRange(heli.DecelFromM!.Value, 0.8 * Nm, 1.1 * Nm);

        var pose = heli.Pose();
        Assert.Equal(Pad.Latitude, pose.Latitude, 6);
        Assert.Equal(Pad.Longitude, pose.Longitude, 6);
        Assert.Equal(Pad.ElevationFt + 5, pose.AltitudeFt, 3);
    }

    [Fact]
    public void Heli_steht_beim_Schweben_ueber_dem_Pad_auf_Schwebehoehe()
    {
        var plan = TrafficTestPlan.BuildHeli(Pad, HeliProfile.Default);
        var leg = plan.Heli!;
        var heli = new HeliKinematics(plan.Runway, leg.Profile, 3 * Nm, 1000, 90, Pad.HeadingDeg, Pad.ElevationFt, 5);
        while (heli.Phase is HeliPhase.Cruise or HeliPhase.Approach)
        {
            // Sinken nur auf dem Anflugweg: nie unter dem Profil, das am Pad auf der Schwebehöhe endet
            heli.Advance(0.1);
            Assert.True(heli.HeightFt >= heli.ProfileHeightFt(heli.DistanceM) - 0.5);
            Assert.True(heli.SpeedKt <= heli.AllowedKt(heli.DistanceM) + 1);
        }

        Assert.Equal(HeliPhase.Hover, heli.Phase);
        Assert.InRange(heli.HeightFt, 40, 46);
        Assert.Equal(0, heli.SpeedKt);
    }

    [Fact]
    public void Heli_Plan_spawnt_gegen_die_Ausrichtung_des_Pads()
    {
        var plan = TrafficTestPlan.BuildHeli(Pad, HeliProfile.Default);

        Assert.Equal(TrafficTestVariant.H1, plan.Variant);
        Assert.True(plan.IsHeli);
        Assert.Equal(50, plan.Heli!.FromDeg, 6);
        Assert.Equal(230, plan.Runway.HeadingTrueDeg, 6);
        Assert.Equal("H2", plan.Runway.Designation);
        Assert.Equal(TrafficTestPlan.DefaultHeliNm, RouteMath.DistanceNm(Pad.Latitude, Pad.Longitude, plan.Spawn.Latitude, plan.Spawn.Longitude), 2);
        Assert.Equal(50, RouteMath.BearingDeg(Pad.Latitude, Pad.Longitude, plan.Spawn.Latitude, plan.Spawn.Longitude), 0);
        Assert.Equal(Pad.ElevationFt + 1000, plan.Spawn.AltitudeFt);
        Assert.Equal(230, plan.Spawn.HeadingTrueDeg, 6);
        Assert.Equal(90u, plan.Spawn.AirspeedKt);
        Assert.Single(plan.Waypoints);
        Assert.Equal(0, plan.Next(-500).Waypoint.SpeedKt);
    }

    [Fact]
    public void Heli_Plan_auf_einen_Punkt_ohne_Ausrichtung_kommt_von_Sueden()
    {
        var point = new HeliPad("POS", "HP1", 50.0, 7.0, 600, null, 20, 20, "point", Measured: true);
        var plan = TrafficTestPlan.BuildHeli(point, HeliProfile.Default.WithCruise(200), spawnNm: 50, heightFt: 50);

        Assert.Equal(180, plan.Heli!.FromDeg);
        Assert.Equal(0, plan.Runway.HeadingTrueDeg, 6);
        Assert.Equal(20, plan.SpawnNm);
        Assert.Equal(200, plan.Heli.StartHeightFt);
        Assert.Equal(150, plan.Heli.Profile.CruiseKt);
    }

    [Fact]
    public void Heli_Auswertung_meldet_gelandet()
    {
        var plan = TrafficTestPlan.BuildHeli(Pad, HeliProfile.Default);
        var t0 = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, t0);
        var (lat, lon) = plan.Runway.Point(-1000);
        var air = analysis.Add(Sample(t0.AddSeconds(1), lat, lon, Pad.ElevationFt + 300, onGround: false));
        Assert.Equal(1000 / Nm, air.DistanceToThresholdNm!.Value, 2);
        Assert.Equal("approaching", analysis.Summary().Verdict);

        var down = analysis.Add(Sample(t0.AddSeconds(2), Pad.Latitude, Pad.Longitude, Pad.ElevationFt + 5, onGround: true));
        Assert.Contains(down.Events, e => e.StartsWith("heli_touchdown", StringComparison.Ordinal));
        Assert.Equal("landed", analysis.Summary().Verdict);
    }

    [Fact]
    public void Helipads_heissen_nach_ihren_Startpunkten()
    {
        var data = new AirportHelipads(
            "EDDL",
            51.28,
            6.76,
            45,
            [
                new AirportHelipad(0, 51.2800, 6.7500, 45, 90, 20, 20, 1),
                new AirportHelipad(1, 51.2810, 6.7510, 0, 180, 25, 25, 4),
                new AirportHelipad(2, 51.2820, 6.7520, 46, 270, 15, 15, 3),
            ],
            [
                new HelipadStart(51.2810, 6.7510, 45, 180, 1, HelipadStart.HelipadType),
                new HelipadStart(51.28201, 6.75201, 46, 270, 3, HelipadStart.HelipadType),
            ]);

        var pads = HeliPads.From(data);

        Assert.Equal(["H1", "H2", "H3"], pads.Select(p => p.Label));
        Assert.Equal(51.2810, pads[0].Latitude);
        Assert.Equal("medical", pads[0].Type);
        // Ohne eigene Höhe die des Flughafens
        Assert.Equal(45 * RunwayEnd.FeetPerMeter, pads[0].ElevationFt, 3);
        Assert.Equal(51.2800, pads[1].Latitude);
        Assert.Equal(51.2820, pads[2].Latitude);
        Assert.Equal(270, pads[2].HeadingDeg!.Value);
    }

    [Theory]
    [InlineData("H02", "H2")]
    [InlineData("h2", "H2")]
    [InlineData("2", "H2")]
    [InlineData(" H 2 ", "H2")]
    [InlineData("hp01", "HP1")]
    [InlineData("05L", "05L")]
    [InlineData("", "")]
    public void Helipad_Bezeichnungen_ohne_fuehrende_Null(string label, string expected)
    {
        Assert.Equal(expected, HeliPads.NormalizeLabel(label));
    }

    [Fact]
    public void Helipad_finden_und_Filter_im_Status()
    {
        var pads = new[]
        {
            Pad with { Label = "H1" },
            Pad,
        };

        Assert.Same(pads[1], HeliPads.Find(pads, "H02"));
        Assert.Same(pads[0], HeliPads.Find(pads, null));
        Assert.Null(HeliPads.Find(pads, "H7"));

        var frame = TrafficTestPlan.BuildHeli(Pad, HeliProfile.Default).Runway;
        Assert.True(TrafficTestService.Matches(frame, "EDDL H02"));
        Assert.True(TrafficTestService.Matches(frame, "h2"));
        Assert.True(TrafficTestService.Matches(frame, "EDDL"));
        Assert.False(TrafficTestService.Matches(frame, "EDDL H1"));
        Assert.False(TrafficTestService.Matches(frame, "05L"));
    }

    private static AiProbeSample Sample(DateTimeOffset utc, double lat, double lon, double altitudeFt, bool onGround) =>
        new(1, utc, lat, lon, altitudeFt, onGround ? 5 : altitudeFt - Pad.ElevationFt, 0, 0, 0, 230, onGround, true, 100, 100, 100, 100, null);
}
