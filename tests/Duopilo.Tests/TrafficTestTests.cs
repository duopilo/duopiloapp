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
        // Erster Boden-Wegpunkt 150 m hinter der Schwelle, der Luft-Wegpunkt an der Schwelle liegt 150 m davor
        Assert.Equal(["4nm", "3nm", "2nm", "1nm", "0.5nm", "threshold", "touchdown", "rollout1", "rollout2", "stop", "beyondStop"], names);
        Assert.Equal(TrafficTestPlan.DefaultTouchdownM, plan.Waypoints.Single(w => w.Name == "touchdown").AlongM);
        Assert.Equal(150, TrafficTestPlan.DefaultTouchdownM);
        Assert.Equal(["4nm", "3nm", "2nm", "1nm", "0.5nm", "threshold"], plan.Checkpoints.Select(c => c.Name));

        // Entlang der Mittellinie aufsteigend, in der Luft fallende Höhe
        Assert.Equal(plan.Waypoints.Select(w => w.AlongM).OrderBy(a => a), plan.Waypoints.Select(w => w.AlongM));
        var air = plan.Waypoints.TakeWhile(w => w.Name != "touchdown").ToList();
        Assert.All(air, w => Assert.False(w.Waypoint.Flags.HasFlag(AiWaypointFlags.OnGround)));
        Assert.Equal(air.Select(w => w.Waypoint.AltitudeFt).OrderByDescending(a => a), air.Select(w => w.Waypoint.AltitudeFt));

        // Letzte Meile tiefer gelegt (Standard 100 ft, 0,5 NM 60 %), gemessen wird weiter am Gleitpfad
        var half = plan.Waypoints.Single(w => w.Name == "0.5nm");
        Assert.Equal(TrafficTestPlan.DefaultFinalLowFt, plan.FinalLowFt);
        Assert.Equal(TrafficTestPlan.GlidePathFt(end, 0.5), half.GlidePathAltitudeFt, 3);
        Assert.Equal(TrafficTestPlan.GlidePathFt(end, 0.5) - 60, half.Waypoint.AltitudeFt, 3);
        Assert.Equal(end.ElevationFt + 50, plan.Checkpoints.Single(c => c.Name == "threshold").GlidePathFt, 3);

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
        Assert.Equal(3000, plan.GearDownFt);
        Assert.Equal(9.3, plan.GearDownNm); // 3000 ft über der Bahn auf dem 3°-Pfad

        // Liegt der Spawn schon unter 3000 ft, kommt das Fahrwerk gleich beim Spawn
        var low = TrafficTestPlan.Build(End("23L"), TrafficTestVariant.A2, spawnNm: 8);
        Assert.True(low.GearAtSpawn);
        Assert.Null(low.GearDownFt);
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
        Assert.Equal(1640, summary.GearExtendedAtFt);
        Assert.False(summary.GearDownInTime); // A1 startet unter 2500 ft
        Assert.InRange(summary.MeanSpeedDeviationKt!.Value, 9, 11);
        Assert.Contains(summary.Gates, g => g.Name == "2nm" && g.IndicatedKt == 150 && g.TargetKt == 140);
        Assert.Contains(summary.Gates, g => g.Name == "threshold");
        // Auf dem Pfad, obwohl die Wegpunkte der letzten Meile tiefer liegen (an der Schwelle 20 ft statt 50 ft: 30 ft zu tief)
        Assert.All(summary.Gates.Where(g => g.Name != "threshold"), g => Assert.InRange(g.AltitudeDeviationFt, -1, 1));
        Assert.Equal(-30, summary.Gates.Single(g => g.Name == "threshold").AltitudeDeviationFt);
        Assert.Equal(["SIMPLE_LANDING"], summary.AiStates);
    }

    [Fact]
    public void Letzte_Meile_tiefer_ein_Viertel_60_Prozent_voll()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: 300, finalLowFt: 200);

        double Drop(string name)
        {
            var w = plan.Waypoints.Single(x => x.Name == name);
            return w.GlidePathAltitudeFt - w.Waypoint.AltitudeFt;
        }

        Assert.Equal(0, Drop("2nm"), 3);
        Assert.Null(plan.Waypoints.Single(w => w.Name == "2nm").PathAltitudeFt);
        Assert.Equal(50, Drop("1nm"), 3);
        Assert.Equal(120, Drop("0.5nm"), 3);
        Assert.Equal(200, Drop("threshold"), 3);

        var exact = TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: 300, finalLowFt: 0);
        Assert.All(exact.Waypoints, w => Assert.Null(w.PathAltitudeFt));
        Assert.Equal(end.ElevationFt + 50, exact.Waypoints.Single(w => w.Name == "threshold").Waypoint.AltitudeFt, 3);

        Assert.Equal(TrafficTestPlan.MaxFinalLowFt, TrafficTestPlan.Build(end, TrafficTestVariant.A1, finalLowFt: 999).FinalLowFt);
        Assert.Equal(0, TrafficTestPlan.Build(end, TrafficTestVariant.A1, finalLowFt: -5).FinalLowFt);
    }

    [Fact]
    public void Erster_Boden_Wegpunkt_verschiebbar_Luft_Wegpunkte_davor()
    {
        var end = End("23L");

        var before = TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: -300);
        Assert.Equal(-300, before.TouchdownM);
        Assert.Equal(-300, before.Waypoints.Single(w => w.Name == "touchdown").AlongM);
        Assert.Contains(before.Waypoints, w => w.Name == "0.5nm");
        Assert.All(before.Waypoints.TakeWhile(w => w.Name != "touchdown"), w => Assert.True(w.AlongM <= -400));
        Assert.Equal(before.Waypoints.Select(w => w.AlongM).OrderBy(a => a), before.Waypoints.Select(w => w.AlongM));

        var behind = TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: 300);
        Assert.Contains(behind.Waypoints, w => w.Name == "threshold");

        // An der Schwelle: dort kein Luft-Wegpunkt, gemessen wird die Schwelle trotzdem
        var atThreshold = TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: 0);
        Assert.DoesNotContain(atThreshold.Waypoints, w => w.Name == "threshold");
        Assert.Contains(atThreshold.Checkpoints, c => c.Name == "threshold");

        Assert.Equal(TrafficTestPlan.MinTouchdownM, TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: -2000).TouchdownM);
        Assert.Equal(TrafficTestPlan.MaxTouchdownM, TrafficTestPlan.Build(end, TrafficTestVariant.A1, touchdownM: 5000).TouchdownM);
    }

    [Theory]
    [InlineData(2700, true)]
    [InlineData(2000, false)]
    public void Auswertung_Fahrwerk_bis_2500_ft_draussen(double extendedAtFt, bool inTime)
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A2);
        var start = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, start);
        var t = 0;

        for (var nm = 12.0; nm > 4; nm -= 0.5)
        {
            var alt = TrafficTestPlan.GlidePathFt(end, nm);
            var gear = alt - end.ElevationFt <= extendedAtFt ? 100 : 0;
            Add(analysis, end, start, t++, -nm * RunwayEnd.MetersPerNm, alt, ias: 160, gs: 160, onGround: false, agl: alt - end.ElevationFt, gear: gear);
        }

        var summary = analysis.Summary();
        Assert.Equal(inTime, summary.GearDownInTime);
        Assert.InRange(summary.GearExtendedAtFt!.Value, extendedAtFt - 170, extendedAtFt);
    }

    [Fact]
    public void Auswertung_Abfangen_aus_der_Laengsneigung()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1);
        var start = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, start);
        var t = 0;

        // Anflug mit 2,5° Nase hoch und 1,5° Querneigung, auf den letzten Sekunden Nase auf 6° (Abfangen)
        for (var nm = 3.0; nm > 0; nm -= 0.1)
        {
            var bank = nm < 1.5 ? -4 : 1.5;
            Add(analysis, end, start, t++, -nm * RunwayEnd.MetersPerNm, TrafficTestPlan.GlidePathFt(end, nm), ias: 140, gs: 140, onGround: false, agl: 100, pitch: 2.5, bank: bank);
        }

        Add(analysis, end, start, t++, 150, end.ElevationFt + 15, ias: 135, gs: 135, onGround: false, agl: 15, vs: -400, pitch: 5);
        Add(analysis, end, start, t++, 250, end.ElevationFt + 5, ias: 132, gs: 132, onGround: false, agl: 5, vs: -200, pitch: 6);
        Add(analysis, end, start, t++, 320, end.ElevationFt, ias: 130, gs: 130, onGround: true, agl: 0, pitch: 4);

        var summary = analysis.Summary();
        Assert.Equal(2.5, summary.ApproachPitchDeg);
        Assert.Equal(6, summary.Touchdown!.PitchDeg);
        Assert.Equal(3.5, summary.Touchdown.FlarePitchGainDeg);
        Assert.Equal(200, summary.Touchdown.SinkRateFpm);
        Assert.Equal(4, summary.MaxFinalBankDeg);
        Assert.Contains(summary.Gates, g => g.Name == "1nm" && g.PitchDeg == 2.5);
    }

    [Fact]
    public void Abfrage_Struktur_passt_zu_den_SimVars()
    {
        var doubles = typeof(AiProbeData).GetFields().Count(f => f.FieldType == typeof(double));
        Assert.Equal(AiProbeVariables.Numeric.Count, doubles);
        Assert.Contains(AiProbeVariables.Numeric, v => v.Name == "PLANE PITCH DEGREES");
        Assert.Contains(AiProbeVariables.Numeric, v => v.Name == "PLANE BANK DEGREES");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("23L", true)]
    [InlineData("rwy 23l", true)]
    [InlineData("EDDL 23L", true)]
    [InlineData("eddl", true)]
    [InlineData("05R", false)]
    [InlineData("EDDF 23L", false)]
    [InlineData("EDDF", false)]
    public void Bahnfilter_fuer_Status_und_Stop(string? filter, bool expected) =>
        Assert.Equal(expected, TrafficTestService.Matches(End("23L"), filter));

    [Fact]
    public void Kennzeichen_DUO01_bis_DUO04()
    {
        Assert.Equal(TrafficTestService.TailNumber, TrafficTestService.Tail(0));
        Assert.Equal("DUO04", TrafficTestService.Tail(TrafficTestService.MaxConcurrent - 1));
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

    [Theory]
    [InlineData("FlyByWire A380X", "", "FlyByWire")]
    [InlineData("Pride A380X", "", "A380X")]
    [InlineData("Airbus A330-900neo Condor Beach", "", "A330-900neo")]
    [InlineData("Headwind A339", "Condor", "Headwind")]
    [InlineData("A330-300 (RR)", "Lufthansa", null)]
    [InlineData("Asobo PassiveAircraft A330-900", "A330_900_CONDOR", null)]
    [InlineData("FSLTL A320 CFM", "Lufthansa", null)]
    public void Gesperrte_Modelle_FBW_und_Headwind(string title, string livery, string? pattern) =>
        Assert.Equal(pattern, TrafficModels.ExcludedBy(title, livery));

    [Fact]
    public void Standardmodell_ueberspringt_gesperrte()
    {
        AiModel[] models =
        [
            new("FlyByWire A32NX A320", "Lufthansa"),
            new("Asobo A320neo", "Default"),
        ];

        Assert.Equal("Asobo A320neo", TrafficTestService.PickDefaultModel(models)!.Title);
        Assert.Null(TrafficTestService.PickDefaultModel(models[..1]));
    }

    [Theory]
    [InlineData("Asobo PassiveAircraft A320 NEO", "", "A20N", "verified")]
    [InlineData("Asobo PassiveAircraft B737-800", "B737_800_SUNEXPRESS", "B738", "verified")]
    [InlineData("Asobo PassiveAircraft A350-900", "", "A359", "untested")]
    [InlineData("Asobo PassiveAircraft MD11F", "", "MD11", "untested")]
    [InlineData("Microsoft PassiveAircraft S340B Passenger", "", "SF34", "untested")]
    [InlineData("Asobo PassiveAircraft A330-900", "", "A339", "untested")]
    [InlineData("A330-300 (RR)", "Lufthansa", "A333", "candidate")]
    [InlineData("a330-300 (ge)", "", "A333", "candidate")]
    [InlineData("A320neo V2", "", "A20N", "candidate")]
    [InlineData("A330-300 VIP (RR)", "", null, "unknown")]
    [InlineData("A330-300P2F (RR)", "", null, "unknown")]
    [InlineData("Airbus A330-900neo Condor Beach", "", null, "excluded")]
    [InlineData("Cessna 172 Skyhawk (G1000)", "", "C172", "untested")]
    [InlineData("Beechcraft King Air 350i", "", "B350", "untested")]
    [InlineData("King Air C90 GTX", "", "BE9L", "untested")]
    [InlineData("Pilatus PC12 NGX", "", "PC12", "untested")]
    public void Freigabeliste_exakte_Titel_mit_ICAO_Typ(string title, string livery, string? type, string status)
    {
        Assert.Equal(type, TrafficModels.TypeOf(title, livery)?.IcaoType);
        Assert.Equal(status, TrafficModels.StatusName(title, livery));
    }

    [Fact]
    public void Freigabeliste_ohne_gesperrte_und_ohne_doppelte_Titel()
    {
        var titles = TrafficModels.Approved.SelectMany(t => t.Titles).ToList();
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(titles, t => Assert.False(TrafficModels.IsExcluded(t)));
        Assert.All(TrafficModels.Approved, t => Assert.Matches("^[A-Z0-9]{2,4}$", t.IcaoType));
    }

    [Fact]
    public void GA_Titelwoerter_uebernehmen_keine_exakten_Titel()
    {
        var exact = TrafficModels.Approved.SelectMany(t => t.Titles);
        Assert.All(exact, title => Assert.Equal(
            TrafficModels.Approved.First(t => t.MatchesExactly(title)).IcaoType,
            TrafficModels.TypeOf(title)!.IcaoType));

        // Typen ohne exakte Titel sind die vollen GA-Modelle aus MSFS 2024: ungetestet, an Titelwörtern erkannt
        var byWords = TrafficModels.Approved.Where(t => t.Titles.Count == 0).ToList();
        Assert.NotEmpty(byWords);
        Assert.All(byWords, t =>
        {
            Assert.Equal(TrafficModelStatus.Untested, t.Status);
            Assert.Equal(TrafficCategory.GeneralAviation, t.Category);
            Assert.NotEmpty(t.TitleWords);
        });

        // Ein Passive-Titel gewinnt gegen die Titelwörter der vollen GA-Modelle
        Assert.Equal("C172", TrafficModels.TypeOf("Asobo PassiveAircraft C172")!.IcaoType);
        Assert.True(TrafficModels.TypeOf("Asobo PassiveAircraft C172")!.Passive);
        Assert.Equal("C72R", TrafficModels.TypeOf("Asobo PassiveAircraft C172RG")!.IcaoType);
    }

    [Fact]
    public void Standardmodell_nimmt_ohne_FSLTL_ein_gepruefte_Modell()
    {
        AiModel[] models =
        [
            new("A310", "Lufthansa"),
            new("Cessna 172 Skyhawk", ""),
            new("A320neo V2", ""),
            new("Asobo PassiveAircraft A320 NEO", "Lufthansa"),
        ];

        // Geprüft sind die Passive-Modelle – der volle A320neo V2 zeigt als KI keine Lichter
        Assert.Equal("Asobo PassiveAircraft A320 NEO", TrafficTestService.PickDefaultModel(models)!.Title);
        Assert.Equal("A320neo V2", TrafficTestService.PickDefaultModel(models[..3])!.Title);
    }

    [Theory]
    [InlineData("Asobo PassiveAircraft ATR72-600", 115, TrafficCategory.Airliner)]
    [InlineData("Asobo PassiveAircraft A320 NEO", 135, TrafficCategory.Airliner)]
    [InlineData("Asobo PassiveAircraft C172", 65, TrafficCategory.GeneralAviation)]
    [InlineData("Asobo PassiveAircraft Citation CJ4", 115, TrafficCategory.GeneralAviation)]
    public void Freigabeliste_Anfluggeschwindigkeit_und_Kategorie(string title, double approachKt, TrafficCategory category)
    {
        var type = TrafficModels.TypeOf(title)!;
        Assert.Equal(approachKt, type.ApproachKt);
        Assert.Equal(category, type.Category);
    }

    [Fact]
    public void Plan_Geschwindigkeiten_aus_der_Anfluggeschwindigkeit()
    {
        var end = End("23L");

        // Standard 135 kt: wie bisher 160 / 140 / 135 / 130
        var standard = TrafficTestPlan.Build(end, TrafficTestVariant.A2);
        Assert.Equal(135, standard.ApproachKt);
        Assert.Equal(160, standard.Waypoints.Single(w => w.Name == "12nm").Waypoint.SpeedKt);
        Assert.Equal(140, standard.Waypoints.Single(w => w.Name == "4nm").Waypoint.SpeedKt);

        // ATR 72: 140 ab 6 NM, 120 darunter, 115 an der Schwelle, 110 beim Aufsetzen
        var atr = TrafficTestPlan.Build(end, TrafficTestVariant.A2, approachKt: 115);
        Assert.Equal(140, atr.Waypoints.Single(w => w.Name == "8nm").Waypoint.SpeedKt);
        Assert.Equal(120, atr.Waypoints.Single(w => w.Name == "2nm").Waypoint.SpeedKt);
        Assert.Equal(115, atr.Waypoints.Single(w => w.Name == "threshold").Waypoint.SpeedKt);
        Assert.Equal(110, atr.Waypoints.Single(w => w.Name == "touchdown").Waypoint.SpeedKt);
        Assert.Equal(140u, atr.Spawn.AirspeedKt);

        // Cessna 172: Ausrollen beginnt unter der Aufsetzgeschwindigkeit
        var cessna = TrafficTestPlan.Build(end, TrafficTestVariant.A2, spawnNm: 8, approachKt: 65);
        Assert.Equal(60, cessna.Waypoints.Single(w => w.Name == "touchdown").Waypoint.SpeedKt);
        Assert.Equal(55, cessna.Waypoints.Single(w => w.Name == "rollout1").Waypoint.SpeedKt);
        Assert.Equal(90u, cessna.Spawn.AirspeedKt);
    }

    [Fact]
    public void Lichter_Bits_Text_und_Soll()
    {
        // LIGHT STATES: nav 0x1, beacon 0x2, landing 0x4, taxi 0x8, strobe 0x10, logo 0x100
        Assert.Equal("nav beacon landing strobe logo", AiLights.Describe(0x0117));
        Assert.Equal("", AiLights.Describe(0));
        Assert.Equal(("STROBES_SET", "LIGHT STROBE"), AiLights.Names(AiLight.Strobe));
        Assert.Equal(("LANDING_LIGHTS_SET", "LIGHT LANDING"), AiLights.Names(AiLight.Landing));

        Assert.True(TrafficLights.Approach.MatchedBy(0x011F));
        Assert.False(TrafficLights.Approach.MatchedBy(0x0003));
        Assert.Equal(6, TrafficLights.Approach.Changes().Count());

        // Nach der Landung nur drei Lichter schalten, der Rest bleibt
        Assert.Equal([(AiLight.Strobe, false), (AiLight.Landing, false), (AiLight.Taxi, true)], TrafficLights.Rollout.Changes());
        Assert.True(TrafficLights.Rollout.MatchedBy(0x010B));
        Assert.False(TrafficLights.Rollout.MatchedBy(0x011F));
    }

    [Fact]
    public void Auswertung_Lichter_im_Anflug_und_nach_der_Landung()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A1);
        var start = DateTimeOffset.UtcNow;
        var analysis = new TrafficTestAnalysis(plan, start);

        Add(analysis, end, start, 0, -2 * RunwayEnd.MetersPerNm, TrafficTestPlan.GlidePathFt(end, 2), ias: 140, gs: 140, onGround: false, agl: 600, lights: 0x011F);
        Add(analysis, end, start, 1, -1 * RunwayEnd.MetersPerNm, TrafficTestPlan.GlidePathFt(end, 1), ias: 140, gs: 140, onGround: false, agl: 300, lights: 0x011F);
        Add(analysis, end, start, 2, 400, end.ElevationFt, ias: 128, gs: 128, onGround: true, agl: 0, lights: 0x011F);
        Add(analysis, end, start, 3, 900, end.ElevationFt, ias: 50, gs: 50, onGround: true, agl: 0, lights: 0x010B);

        var summary = analysis.Summary();
        Assert.True(analysis.TouchedDown);
        Assert.Equal("nav beacon landing taxi strobe logo", summary.LightsOnFinal);
        Assert.Equal("nav beacon taxi logo", summary.LightsAfterLanding);
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
            Assert.Equal(0, json.RootElement.GetProperty("running").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("tests").GetArrayLength());

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
        double gear = 100,
        double pitch = 0,
        double bank = 0,
        int lights = 0)
    {
        var (lat, lon) = end.Point(alongM);
        return analysis.Add(new AiProbeSample(1, start.AddSeconds(seconds), lat, lon, altitudeFt, agl, ias, gs, vs, end.HeadingTrueDeg, onGround, gear > 0, gear, 0, 0, 0, "SIMPLE_LANDING")
        {
            PitchDeg = pitch,
            BankDeg = bank,
            LightStates = lights,
        });
    }
}
