using Duopilo.Aircraft;
using Duopilo.FlightPlan;
using Duopilo.Monitoring;
using Duopilo.Sim;

namespace Duopilo.Tests;

/// <summary>Korrekturen nach dem Testflug EDDL–EDDM (v0.5).</summary>
public sealed class FlightTestFixesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static FlightSnapshot Air(double sec) => new()
    {
        TimeUtc = T0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
        DataAgeSec = 0.1,
        AircraftKey = "test",
        OnGround = false,
        IasKt = 250,
        GroundSpeedKt = 400,
        VerticalSpeedFpm = 0,
        AltitudeFt = 30000,
        PressureAltitudeFt = 30000,
        AglFt = 29000,
        BankRightDeg = 0,
        FlapsIndex = 0,
        GearExtendedPct = 0,
        ApEngaged = true,
        SelectedAltitudeFt = 30000,
        Limits = new AircraftLimits
        {
            VmoKt = 350,
            VleKt = 280,
            VfeKt = new Dictionary<string, double> { ["1"] = 230, ["1+F"] = 215, ["2"] = 200, ["3"] = 185, ["FULL"] = 177 },
            LandingFlaps = ["3", "FULL"],
        },
    };

    /// <summary>Endanflug: Fahrwerk unten, Klappen FULL, Spoiler armed.</summary>
    private static FlightSnapshot Final(double sec, double ra) => Air(sec) with
    {
        IasKt = 140,
        VerticalSpeedFpm = -750,
        AltitudeFt = ra + 1500,
        PressureAltitudeFt = ra + 1500,
        AglFt = ra,
        RadioAltFt = ra,
        FlapsIndex = 4,
        FlapsLabel = "FULL",
        GearExtendedPct = 100,
        SpoilersArmed = true,
        SelectedAltitudeFt = 5000,
    };

    private static List<AlertEvent> Feed(MonitorEngine engine, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).ToList();

    private static MonitorEngine WarmedUp(Func<double, FlightSnapshot>? start = null)
    {
        var engine = new MonitorEngine();
        var make = start ?? Air;
        Feed(engine, make(0), make(11));
        return engine;
    }

    // ------------------------------------------------------------------ ARINC 429 und Mach

    [Fact]
    public void Arinc429_Woerter_werden_wie_bei_FBW_dekodiert()
    {
        var raw = Arinc429Word.Encode(Arinc429Ssm.NormalOperation, 35000f);
        var word = Arinc429Word.Decode(raw);
        Assert.True(word.IsNormal);
        Assert.Equal(35000f, word.Value);

        var ncd = Arinc429Word.Decode(Arinc429Word.Encode(Arinc429Ssm.NoComputedData, 0));
        Assert.False(ncd.IsNormal);

        // Diskretes Wort: Bit 26 (Strich-Anzeige) und Bit 19 (ALT) gesetzt
        var discrete = Arinc429Word.Decode(Arinc429Word.Encode(Arinc429Ssm.NormalOperation, (1 << 25) | (1 << 18)));
        Assert.True(discrete.Bit(26));
        Assert.True(discrete.Bit(19));
        Assert.False(discrete.Bit(12));
    }

    [Theory]
    [InlineData(0.78, 238.4, 261, 268)]   // FL350
    [InlineData(0.5, 1013.25, 329, 332)]  // Meereshöhe: CAS = TAS
    [InlineData(0.80, 196.8, 245, 252)]   // FL390
    public void Mach_wird_in_Knoten_umgerechnet(double mach, double pressureHpa, double min, double max)
    {
        Assert.InRange(AirspeedMath.MachToCasKt(mach, pressureHpa) ?? 0, min, max);
    }

    [Fact]
    public void Ohne_Druck_keine_Umrechnung()
    {
        Assert.Null(AirspeedMath.MachToCasKt(0.78, null));
        Assert.Null(AirspeedMath.MachToCasKt(null, 238));
    }

    // ------------------------------------------------------------------ Grenzwerte und Text-SimVars

    [Fact]
    public void VFE_und_Landeklappen_nach_Klartext()
    {
        var limits = Air(0).Limits;

        Assert.Equal(215, limits.VfeFor(2, "1+F"));
        Assert.Equal(177, limits.VfeFor(5, "FULL"));
        Assert.Null(limits.VfeFor(0, "0"));
        Assert.Equal(230, limits.VfeFor(1));
        Assert.True(limits.IsLandingFlaps(5, "FULL"));
        Assert.True(limits.IsLandingFlaps(4, "3"));
        Assert.False(limits.IsLandingFlaps(3, "2"));
    }

    [Theory]
    [InlineData("ATC RUNWAY AIRPORT NAME", true)]
    [InlineData("A:TITLE", true)]
    [InlineData("NAV IDENT:1", true)]
    [InlineData("GPS WP NEXT ID", true)]
    [InlineData("INDICATED ALTITUDE", false)]
    [InlineData("L:SOME_NAME", false)]
    public void Text_SimVars_werden_erkannt(string name, bool expected)
    {
        Assert.Equal(expected, SimVarSpec.IsTextVariable(name));
    }

    // ------------------------------------------------------------------ Monitoring

    [Fact]
    public void Master_caution_nennt_die_neueste_ECAM_Meldung()
    {
        var engine = WarmedUp(s => Air(s) with { EcamTitles = ["BRAKES HOT"] });

        Assert.Empty(Feed(engine, Air(12) with { EcamTitles = ["BRAKES HOT", "ELEC GEN 1 OFF"], MasterCaution = true }));
        var e = Assert.Single(Feed(engine, Air(12.8) with { EcamTitles = ["BRAKES HOT", "ELEC GEN 1 OFF"], MasterCaution = true }));

        Assert.Equal(Rules.MasterCaution, e.Rule);
        Assert.Equal("Master caution, ELEC GEN 1 OFF", e.Callout);
    }

    [Fact]
    public void Weitere_ECAM_Meldung_bei_leuchtender_Caution_wird_angesagt()
    {
        var engine = WarmedUp();
        Feed(engine,
            Air(12) with { EcamTitles = ["ELEC GEN 1 OFF"], MasterCaution = true },
            Air(13) with { EcamTitles = ["ELEC GEN 1 OFF"], MasterCaution = true });

        var e = Assert.Single(Feed(engine, Air(14) with { EcamTitles = ["ELEC GEN 1 OFF", "HYD G RSVR LO LVL"], MasterCaution = true }));
        Assert.Equal(Rules.EcamMessage, e.Rule);
        Assert.Equal("HYD G RSVR LO LVL", e.Callout);
        Assert.Empty(Feed(engine, Air(15) with { EcamTitles = ["ELEC GEN 1 OFF", "HYD G RSVR LO LVL"], MasterCaution = true }));
    }

    [Fact]
    public void Top_of_descent_kommt_vom_FMS()
    {
        var engine = WarmedUp(s => Air(s) with { TdReached = false, PlanTodDistanceNm = 0.2 });

        var e = Assert.Single(Feed(engine, Air(12) with { TdReached = true }));
        Assert.Equal(Rules.TopOfDescent, e.Rule);
        Assert.Equal("Top of descent", e.Callout);
        Assert.Empty(Feed(engine, Air(13) with { TdReached = true }, Air(20) with { TdReached = true }));
    }

    [Fact]
    public void Top_of_descent_ohne_FMS_aus_dem_SimBrief_Plan()
    {
        var engine = WarmedUp(s => Air(s) with { PlanTodDistanceNm = 40 });

        Assert.Empty(Feed(engine, Air(12) with { PlanTodDistanceNm = 3 }));
        var e = Assert.Single(Feed(engine, Air(13) with { PlanTodDistanceNm = 0.3 }));
        Assert.Contains("SimBrief", e.Message);
        Assert.Empty(Feed(engine, Air(14) with { PlanTodDistanceNm = 0.1 }));
    }

    [Fact]
    public void Landing_check_bei_2000_ft_vollstaendig()
    {
        var engine = WarmedUp(s => Final(s, 2600));

        var e = Assert.Single(Feed(engine, Final(12, 2100), Final(13, 1990)));
        Assert.Equal(Rules.LandingCheck, e.Rule);
        Assert.Equal("Landing checklist complete", e.Callout);
        Assert.Empty(Feed(engine, Final(14, 1500), Final(15, 990)));
    }

    [Fact]
    public void Landing_check_meldet_was_fehlt_und_bei_1000_ft_erneut()
    {
        var engine = WarmedUp(s => Final(s, 2600) with { SpoilersArmed = false });

        var at2000 = Assert.Single(Feed(engine, Final(12, 2100) with { SpoilersArmed = false }, Final(13, 1990) with { SpoilersArmed = false }));
        Assert.Equal("Spoilers not armed", at2000.Callout);
        Assert.Equal(AlertSeverity.Caution, at2000.Severity);

        var at1000 = Assert.Single(Feed(engine, Final(14, 1100) with { SpoilersArmed = false, FlapsLabel = "2", FlapsIndex = 3 }, Final(15, 990) with { SpoilersArmed = false, FlapsLabel = "2", FlapsIndex = 3 }));
        Assert.Equal("Flaps not in landing configuration, Spoilers not armed", at1000.Callout);
        Assert.Equal(AlertSeverity.Warning, at1000.Severity);
    }

    [Fact]
    public void Flap_speed_nach_Klappenstellung_1_plus_F()
    {
        var engine = WarmedUp();
        var fast = (double s) => Air(s) with { AltitudeFt = 3000, AglFt = 2500, IasKt = 225, FlapsIndex = 2, FlapsLabel = "1+F" };

        Assert.Empty(Feed(engine, fast(12)));
        var e = Assert.Single(Feed(engine, fast(13.1)), x => x.Rule == Rules.FlapSpeed);
        Assert.Contains("1+F", e.Message);
    }

    // ------------------------------------------------------------------ FMA-Log

    [Fact]
    public void FMA_Log_zeichnet_Wechsel_auf()
    {
        var log = new FmaLog();
        var approach = new FmaState("SPEED", "G/S", null, "LOC", null, "AP1+2", true, "ACTIVE");

        Assert.NotNull(log.Update(Air(0) with { Fma = approach }));
        Assert.Null(log.Update(Air(1) with { Fma = approach }));

        var land = log.Update(Final(2, 350) with { Fma = approach with { Vertical = "LAND", Lateral = "LAND" } });
        Assert.NotNull(land);
        Assert.Contains("vertical: G/S → LAND", land.Changes);
        Assert.Contains("lateral: LOC → LAND", land.Changes);

        log.Update(Final(3, 0) with { OnGround = true, Fma = approach with { Vertical = "ROLL OUT", Lateral = "ROLL OUT", Thrust = "" } });
        Assert.True(log.Seen("ROLL OUT", T0));
        Assert.False(log.Seen("FLARE", T0));
        Assert.Equal(3, log.Last(10).Count);
    }

    // ------------------------------------------------------------------ FMS-Wegpunkt

    private static readonly RoutePoint[] Route = [new("A", 0, 0, -1), new("B", 0, 1, 0), new("C", 0, 2, 1), new("D", 0, 3, 2)];

    private static Ofp OfpFor() => new()
    {
        Origin = new OfpAirport("A", null, null, 0, 0, null, null, null, null),
        Destination = new OfpAirport("D", null, null, 0, 3, null, null, null, null),
        Fixes =
        [
            new OfpFix { Ident = "B", Lat = 0, Lon = 1 },
            new OfpFix { Ident = "C", Lat = 0, Lon = 2 },
            new OfpFix { Ident = "D", Lat = 0, Lon = 3 },
        ],
    };

    [Fact]
    public void Direct_to_aus_dem_FMS_wird_dem_Plan_zugeordnet()
    {
        var progress = RouteTracker.Locate(Route, 0, 0.5);
        Assert.NotNull(progress);

        // Nach DIR TO C: FMS meldet ca. 90 nm auf Kurs 090 (rechtweisend)
        var fp = ProgressCalculator.Compute(OfpFor(), Route, progress, null, new FmsWaypointInput(0, 0.5, 90, 90, 0));

        Assert.Equal("C", fp.Next.Ident);
        Assert.Equal("fms", fp.NextSource);
        Assert.Equal(90, fp.DistanceToNextNm);
        Assert.InRange(fp.DistanceToGoNm, 149, 151);
        Assert.Equal(new[] { "D" }, fp.Following);
    }

    [Fact]
    public void FMS_Peilung_darf_missweisend_sein()
    {
        var progress = RouteTracker.Locate(Route, 0, 0.5);
        Assert.NotNull(progress);

        // 5° Ost Missweisung: missweisend 085 = rechtweisend 090
        var match = ProgressCalculator.MatchFmsWaypoint(Route, progress, new FmsWaypointInput(0, 0.5, 90, 85, 5));
        Assert.Equal(2, match);
    }

    [Fact]
    public void FMS_Punkt_ausserhalb_des_Plans_wird_gemeldet()
    {
        var progress = RouteTracker.Locate(Route, 0, 0.5);
        Assert.NotNull(progress);

        var fp = ProgressCalculator.Compute(OfpFor(), Route, progress, null, new FmsWaypointInput(0, 0.5, 40, 0, 0));

        Assert.True(fp.FmsWaypointNotInPlan);
        Assert.Equal("plan", fp.NextSource);
        Assert.Equal("B", fp.Next.Ident);
    }

    [Fact]
    public void Ziel_aus_Kurs_und_Entfernung()
    {
        var (lat, lon) = RouteMath.Destination(0, 0, 90, 60);
        Assert.InRange(lat, -0.001, 0.001);
        Assert.InRange(lon, 0.99, 1.01);
    }
}
