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

    [Fact]
    public void Heli_Lichter_mit_Recognition_und_abstellen_schaltet_alles_aus()
    {
        Assert.Equal(("RECOGNITION_LIGHTS_SET", "LIGHT RECOGNITION"), AiLights.Names(AiLight.Recognition));
        Assert.Equal("nav recognition", AiLights.Describe(0x0041));
        Assert.Contains((AiLight.Recognition, true), TrafficLights.HeliApproach.Changes());
        Assert.Contains((AiLight.Landing, true), TrafficLights.HeliApproach.Changes());
        Assert.True(TrafficLights.HeliLanded.MatchedBy(0x0103));
        Assert.True(TrafficLights.HeliShutdown.MatchedBy(0));
        Assert.All(TrafficLights.HeliShutdown.Changes(), c => Assert.False(c.On));
        // Flugzeuge schalten das Recognition-Licht nicht
        Assert.DoesNotContain(TrafficLights.Approach.Changes(), c => c.Light == AiLight.Recognition);
    }

    [Fact]
    public void Heli_Rotor_abstellen_setzt_dieselben_SimVars_auf_aus()
    {
        Assert.Equal(HeliRotor.SimVars.Select(v => (v.Name, v.Unit)), HeliRotor.SimVarsOff.Select(v => (v.Name, v.Unit)));
        Assert.All(HeliRotor.SimVarsOff, v => Assert.Equal(0, v.Value));
        Assert.True(HeliRotor.StopAfterLandingSeconds < HeliRotor.LightsOffAfterLandingSeconds);
        Assert.True(HeliRotor.LightsOffAfterLandingSeconds < HeliRotor.EndAfterLandingSeconds);
        // Die Livery gehört nicht in den Titel (sonst CREATE_OBJECT_FAILED, beta.40)
        Assert.DoesNotContain("(", TrafficTestService.DefaultHeliTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Heli_Abflug_laesst_an_hebt_ab_dreht_beschleunigt_und_steigt()
    {
        var plan = TrafficTestPlan.BuildHeliDeparture(Pad, HeliProfile.Default, trackDeg: 90);
        var leg = plan.Heli!;
        Assert.Equal(TrafficTestVariant.H2, plan.Variant);
        Assert.True(leg.IsDeparture);
        Assert.Equal(90, plan.Runway.HeadingTrueDeg, 6);
        Assert.True(plan.Spawn.OnGround);
        Assert.Equal(230, plan.Spawn.HeadingTrueDeg);

        var heli = HeliKinematics.Departure(plan.Runway, leg.Profile, leg.StartHeightFt, plan.Spawn.HeadingTrueDeg, Pad.ElevationFt, 5, 1);
        var phases = new List<HeliPhase> { heli.Phase };
        double minPitch = double.MaxValue, maxClimb = 0, maxSpeed = 0;
        var dt = 1.0 / 30;
        var t = 0.0;
        for (; t < 400 && heli.DistanceM < leg.EndNm * Nm; t += dt)
        {
            heli.Advance(dt);
            if (phases[^1] != heli.Phase)
            {
                phases.Add(heli.Phase);
                if (heli.Phase == HeliPhase.Transition)
                {
                    // Erst nach der Drehung auf den Kurs und ohne Fahrt vom Pad weg
                    Assert.Equal(90, heli.HeadingTrueDeg, 6);
                    Assert.Equal(0, heli.DistanceM, 1);
                    Assert.Equal(HeliProfile.Default.TakeoffHoverFt, heli.HeightFt, 3);
                }
            }

            if (heli.Phase == HeliPhase.Startup)
            {
                Assert.True(heli.OnGround);
                Assert.Equal(0, heli.HeightFt);
            }

            minPitch = Math.Min(minPitch, heli.PitchDeg);
            maxClimb = Math.Max(maxClimb, heli.VerticalSpeedFpm);
            maxSpeed = Math.Max(maxSpeed, heli.SpeedKt);
        }

        Assert.Equal([HeliPhase.Startup, HeliPhase.Liftoff, HeliPhase.Hover, HeliPhase.Transition, HeliPhase.Climb, HeliPhase.Cruise], phases);
        Assert.InRange(t, 170, 200);
        Assert.Equal(1000, heli.HeightFt, 0);
        Assert.Equal(90, maxSpeed, 3);
        Assert.InRange(maxClimb, 990, 1001);
        Assert.InRange(minPitch, -9, -6);
        Assert.Equal(90, heli.HeadingTrueDeg, 6);

        // Auf dem Kurs vom Pad weg, Bezugspunkt über den Kufen
        var pose = heli.Pose();
        Assert.Equal(90, RouteMath.BearingDeg(Pad.Latitude, Pad.Longitude, pose.Latitude, pose.Longitude), 0);
        Assert.Equal(Pad.ElevationFt + 5 + 1000, pose.AltitudeFt, 0);
    }

    [Fact]
    public void Heli_Abflug_ohne_Kurs_nimmt_die_Ausrichtung_des_Pads()
    {
        var plan = TrafficTestPlan.BuildHeliDeparture(Pad, HeliProfile.Default, endNm: 50, heightFt: 100);
        Assert.Equal(230, plan.Heli!.TrackDeg!.Value);
        Assert.Equal(20, plan.Heli.EndNm);
        Assert.Equal(200, plan.Heli.StartHeightFt);

        var point = new HeliPad("POS", "HP1", 51.380228, 6.787324, 110, null, 20, 20, "point", Measured: true);
        var fromPoint = TrafficTestPlan.BuildHeliDeparture(point, HeliProfile.Default);
        Assert.Equal(0, fromPoint.Heli!.TrackDeg!.Value);
        Assert.Equal(0, fromPoint.Spawn.HeadingTrueDeg);
        Assert.Equal(TrafficTestPlan.DefaultHeliEndNm, fromPoint.Heli.EndNm);
    }

    [Fact]
    public void Heli_Abflug_Auswertung_meldet_abgehoben()
    {
        var plan = TrafficTestPlan.BuildHeliDeparture(Pad, HeliProfile.Default);
        var t0 = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, t0);
        var ground = analysis.Add(Sample(t0.AddSeconds(1), Pad.Latitude, Pad.Longitude, Pad.ElevationFt + 5, onGround: true));
        Assert.DoesNotContain(ground.Events, e => e.StartsWith("heli_touchdown", StringComparison.Ordinal));
        Assert.Equal("on_ground", analysis.Summary().Verdict);

        var air = analysis.Add(Sample(t0.AddSeconds(40), Pad.Latitude, Pad.Longitude, Pad.ElevationFt + 15, onGround: false));
        Assert.Contains("heli_liftoff", air.Events);
        Assert.Equal("airborne", analysis.Summary().Verdict);
    }

    [Fact]
    public void Heli_Einsatzflug_Station_Einsatzort_Station_ohne_Spruenge()
    {
        var station = new HeliPad("POS", "P1", 51.380228, 6.787324, 110, null, 20, 20, "point", Measured: true);
        var (sceneLat, sceneLon) = RouteMath.Destination(station.Latitude, station.Longitude, 75, 3);
        var scene = new HeliPad("POS", "P2", sceneLat, sceneLon, 110, null, 20, 20, "point", Measured: true);
        var back = station with { Label = "P3" };
        var plan = TrafficTestPlan.BuildHeliMission([station, scene, back], HeliProfile.Default, waitMinutes: 1);
        Assert.Equal(TrafficTestVariant.H3, plan.Variant);
        Assert.Equal(75, plan.Runway.HeadingTrueDeg, 0);
        Assert.Equal(60, plan.HeliWaitSeconds);
        Assert.True(plan.Spawn.OnGround);
        Assert.Equal(3, plan.Waypoints.Count);

        var mission = new HeliMission(plan.HeliStops!, plan.Heli!.Profile, plan.Heli.StartHeightFt, plan.HeliWaitSeconds, 110, plan.Spawn.HeadingTrueDeg, 5, 0);
        var phases = new List<(HeliMissionPhase Phase, int Leg)> { (mission.Phase, mission.Leg) };
        var dt = 1.0 / 30;
        var previous = mission.Pose();
        double? landedAtScene = null;
        double? departedScene = null;
        var t = 0.0;
        for (; t < 1200 && mission.Phase != HeliMissionPhase.Done; t += dt)
        {
            mission.Advance(dt);
            var pose = mission.Pose();

            // Keine Sprünge beim Wechsel zwischen Abflug, Anflug und Warten
            Assert.True(Math.Abs(pose.AltitudeFt - previous.AltitudeFt) < 2, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"altitude jump at {t:0.0} s"));
            Assert.True(RouteMath.DistanceNm(previous.Latitude, previous.Longitude, pose.Latitude, pose.Longitude) * Nm < 3, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"position jump at {t:0.0} s"));
            Assert.True(Math.Abs(pose.PitchUpDeg - previous.PitchUpDeg) < 0.2);
            previous = pose;

            if (phases[^1] != (mission.Phase, mission.Leg))
            {
                phases.Add((mission.Phase, mission.Leg));
                if (mission.Phase == HeliMissionPhase.Waiting)
                {
                    landedAtScene = t;
                    Assert.Equal(0, RouteMath.DistanceNm(pose.Latitude, pose.Longitude, scene.Latitude, scene.Longitude) * Nm, 1);
                }

                if (mission.Phase == HeliMissionPhase.Departure && mission.Leg == 2)
                {
                    departedScene = t;
                    Assert.Equal(255, mission.Current.Frame.HeadingTrueDeg, 0);
                }
            }
        }

        Assert.Equal(
            [
                (HeliMissionPhase.Departure, 1),
                (HeliMissionPhase.Approach, 1),
                (HeliMissionPhase.Waiting, 1),
                (HeliMissionPhase.Departure, 2),
                (HeliMissionPhase.Approach, 2),
                (HeliMissionPhase.Done, 2),
            ],
            phases);
        Assert.Equal(60, departedScene!.Value - landedAtScene!.Value, 0);
        var end = mission.Pose();
        Assert.Equal(0, RouteMath.DistanceNm(end.Latitude, end.Longitude, station.Latitude, station.Longitude) * Nm, 1);
        Assert.Equal(110 + 5, end.AltitudeFt, 3);
        Assert.InRange(t, 400, 700);
    }

    [Fact]
    public void Heli_Einsatzflug_kurze_Strecke_geht_direkt_in_den_Anflug()
    {
        var a = new HeliPad("POS", "P1", 51.38, 6.78, 110, null, 20, 20, "point", Measured: true);
        var (lat, lon) = RouteMath.Destination(a.Latitude, a.Longitude, 0, 0.6);
        var b = new HeliPad("EDXX", "H1", lat, lon, 150, 90, 20, 20, "h");
        var mission = new HeliMission([a, b], HeliProfile.Default, 1000, 0, 110, 0, 5, 0);
        var maxHeight = 0.0;
        for (var t = 0.0; t < 400 && mission.Phase != HeliMissionPhase.Done; t += 1.0 / 30)
        {
            mission.Advance(1.0 / 30);
            maxHeight = Math.Max(maxHeight, mission.Pose().AltitudeFt);
        }

        Assert.Equal(HeliMissionPhase.Done, mission.Phase);
        // Nicht erst auf 1000 ft, sondern am Anflugweg umgedreht; gelandet auf der Höhe des Pads, auf dessen Ausrichtung
        Assert.InRange(maxHeight, 200, 700);
        Assert.Equal(150 + 5, mission.Pose().AltitudeFt, 3);
        Assert.Equal(90, mission.Current.HeadingTrueDeg, 3);
    }

    [Theory]
    [InlineData("51.380228,6.787324", true, 51.380228, 6.787324)]
    [InlineData(" 51.380228, 6.787324 ", true, 51.380228, 6.787324)]
    [InlineData("51.38 6.78", true, 51.38, 6.78)]
    [InlineData("-33.9,151.2", true, -33.9, 151.2)]
    [InlineData("EDDL H2", false, 0, 0)]
    [InlineData("51,38 6,78", false, 0, 0)]
    [InlineData("95,10", false, 95, 10)]
    public void Heli_Route_Koordinaten(string text, bool ok, double lat, double lon)
    {
        Assert.Equal(ok, HeliPads.TryParseCoordinates(text, out var parsedLat, out var parsedLon));
        if (ok)
        {
            Assert.Equal(lat, parsedLat, 6);
            Assert.Equal(lon, parsedLon, 6);
        }
    }

    [Fact]
    public void Pad_der_Datenbank_nimmt_den_gemessenen_Boden_nur_direkt_darueber()
    {
        var roof = new HeliPad("ED34D", "H2", 51.38141, 6.786043, 226, 90, 29.5, 29.5, "none", Elevated: true);
        Assert.Equal(14.75, roof.RadiusM, 2);
        // beta.43: 60 m davor die Straße gemessen, 80 ft tiefer
        Assert.False(HeliPads.AcceptsMeasuredGround(roof, 60, 147));
        Assert.False(HeliPads.AcceptsMeasuredGround(roof, 5, 147));
        Assert.False(HeliPads.AcceptsMeasuredGround(roof, 20, 225));
        Assert.True(HeliPads.AcceptsMeasuredGround(roof, 5, 224.5));
        var point = new HeliPad("POS", "P2", 51.35, 6.76, 110, null, 20, 20, "point", Measured: true);
        Assert.True(HeliPads.AcceptsMeasuredGround(point, 500, 147));
    }

    [Fact]
    public void Dach_Pads_erkennt_es_an_der_Hoehe_ueber_dem_Heliport()
    {
        // ED34D: Heliport 110 ft, H1 am Boden, H2 auf dem Dach (226 ft)
        var data = new AirportHelipads(
            "ED34D",
            51.380212,
            6.78732,
            110 / RunwayEnd.FeetPerMeter,
            [
                new AirportHelipad(0, 51.380225, 6.787321, 112 / RunwayEnd.FeetPerMeter, 53, 18.1, 18.1, 0),
                new AirportHelipad(1, 51.38141, 6.786043, 226 / RunwayEnd.FeetPerMeter, 90, 29.5, 29.5, 0),
            ],
            []);

        var pads = HeliPads.From(data);

        Assert.False(pads[0].Elevated);
        Assert.True(pads[1].Elevated);
        Assert.Equal(0, HeliMission.TowerFor(pads[0]));
        Assert.Equal(HeliMission.TowerFt, HeliMission.TowerFor(pads[1]));
    }

    [Fact]
    public void Vom_Dach_auf_das_Pad_daneben_erst_hoch_dann_steil_ueber_die_Dachkante()
    {
        var roof = new HeliPad("ED34D", "H2", 51.38141, 6.786043, 226, 90, 29.5, 29.5, "none", Elevated: true);
        var station = new HeliPad("ED34D", "H1", 51.380225, 6.787321, 112, 53, 18.1, 18.1, "none");
        var mission = new HeliMission([roof, station], HeliProfile.Default, 1000, 0, 226, 90, 5, 0);
        var maxAngle = 0.0;
        var t = 0.0;
        for (; t < 300 && mission.Phase != HeliMissionPhase.Done; t += 1.0 / 30)
        {
            mission.Advance(1.0 / 30);
            var pose = mission.Pose();
            var fromRoof = RouteMath.DistanceNm(pose.Latitude, pose.Longitude, roof.Latitude, roof.Longitude) * Nm;
            if (mission.Phase == HeliMissionPhase.Departure)
            {
                // Senkrecht hoch, erst dann vom Pad weg
                Assert.True(fromRoof < 1 || pose.AltitudeFt - 5 >= 226 + HeliMission.TowerFt - 1);
            }
            else if (fromRoof < 60)
            {
                // Über dem Dach (bis 60 m vom Pad) mindestens 20 ft über dem Dach
                Assert.True(pose.AltitudeFt - 5 >= 226 + 20, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pose.AltitudeFt - 5:0} ft at {fromRoof:0} m from the roof pad"));
            }

            maxAngle = Math.Max(maxAngle, mission.Current.IsDeparture ? 0 : mission.Current.ApproachAngleDeg);
        }

        Assert.Equal(HeliMissionPhase.Done, mission.Phase);
        Assert.InRange(maxAngle, 15, HeliKinematics.MaxApproachAngleDeg);
        var end = mission.Pose();
        Assert.Equal(0, RouteMath.DistanceNm(end.Latitude, end.Longitude, station.Latitude, station.Longitude) * Nm, 1);
        Assert.Equal(112 + 5, end.AltitudeFt, 3);
        Assert.Equal(53, mission.Current.HeadingTrueDeg, 3);
        Assert.InRange(t, 40, 150);
    }

    private static AiProbeSample Sample(DateTimeOffset utc, double lat, double lon, double altitudeFt, bool onGround) =>
        new(1, utc, lat, lon, altitudeFt, onGround ? 5 : altitudeFt - Pad.ElevationFt, 0, 0, 0, 230, onGround, true, 100, 100, 100, 100, null);
}
