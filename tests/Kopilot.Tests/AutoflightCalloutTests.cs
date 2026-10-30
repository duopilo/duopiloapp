using Kopilot.Aircraft;
using Kopilot.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kopilot.Tests;

/// <summary>
/// Fehlende Callouts aus dem Anflug KLAX (Testlog 0.9, v0.9.2 Punkt 5): "TCAS RA" und "Clear of conflict", "Autothrust off",
/// FMA-Moduswechsel, LOC- und Glideslope-Abweichung im Endanflug, "Approach phase not activated". Bindings je Profil.
/// </summary>
public sealed class AutoflightCalloutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 4, 35, 0, TimeSpan.Zero);

    private static readonly FmaState Cruise = new("SPEED", "ALT CRZ", null, "NAV", null, "AP1", true, "ACTIVE");

    private static FlightSnapshot Air(double sec, FmaState? fma = null) => new()
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
        Fma = fma ?? Cruise,
        Limits = new AircraftLimits { VmoKt = 350, VleKt = 280, LandingFlaps = ["3", "FULL"] },
    };

    /// <summary>Endanflug: ILS gefangen, Fahrwerk unten, Klappen FULL.</summary>
    private static FlightSnapshot Final(double sec, double ra, double loc = 0, double gs = 0) => Air(sec, new FmaState("SPEED", "G/S", null, "LOC", null, "AP1+2", true, "ACTIVE")) with
    {
        IasKt = 140,
        VerticalSpeedFpm = -750,
        AltitudeFt = ra + 100,
        PressureAltitudeFt = ra + 100,
        AglFt = ra,
        RadioAltFt = ra,
        FlapsIndex = 4,
        FlapsLabel = "FULL",
        GearExtendedPct = 100,
        SpoilersArmed = true,
        SelectedAltitudeFt = 5000,
        LocDeviationDots = loc,
        GsDeviationDots = gs,
    };

    private static MonitorEngine WarmedUp(Func<double, FlightSnapshot>? start = null)
    {
        var engine = new MonitorEngine();
        var make = start ?? (t => Air(t));
        engine.Evaluate(make(0));
        engine.Evaluate(make(11));
        return engine;
    }

    private static List<AlertEvent> Feed(MonitorEngine engine, string rule, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).Where(e => e.Rule == rule).ToList();

    // ------------------------------------------------------------------ TCAS RA

    [Fact]
    public void TCAS_RA_und_Clear_of_conflict()
    {
        var engine = WarmedUp();
        var ra = Cruise with { Vertical = "TCAS" };

        Assert.Empty(Feed(engine, Rules.TcasRa, Air(12, ra), Air(12.2, ra)));

        var raise = Assert.Single(Feed(engine, Rules.TcasRa, Air(12.4, ra), Air(13, ra), Air(18, ra)));
        Assert.Equal(AlertKind.Raise, raise.Kind);
        Assert.Equal(AlertSeverity.Warning, raise.Severity);
        Assert.Equal("TCAS RA", raise.Callout);

        // Erst wenn die RA 1,5 s vorbei ist
        var back = Cruise with { Vertical = "V/S" };
        Assert.Empty(Feed(engine, Rules.TcasRa, Air(20, back), Air(21, back)));
        var clear = Feed(engine, Rules.TcasRa, Air(21.6, back));
        Assert.Equal(2, clear.Count);
        Assert.Equal(AlertKind.Clear, clear[0].Kind);
        Assert.Equal(AlertKind.Event, clear[1].Kind);
        Assert.Equal("Clear of conflict", clear[1].Callout);
        Assert.Equal(AlertSeverity.Info, clear[1].Severity);
    }

    [Fact]
    public void Kurzes_Flackern_ist_keine_RA()
    {
        var engine = WarmedUp();

        Assert.Empty(Feed(engine, Rules.TcasRa, Air(12, Cruise with { Vertical = "TCAS" }), Air(12.2, Cruise), Air(20, Cruise)));
    }

    // ------------------------------------------------------------------ Autothrust

    [Fact]
    public void Autothrust_off_in_der_Luft()
    {
        var engine = WarmedUp();
        var off = Cruise with { Autothrust = "OFF" };

        Assert.Empty(Feed(engine, Rules.AutothrustOff, Air(12, off), Air(12.5, off)));
        var e = Assert.Single(Feed(engine, Rules.AutothrustOff, Air(13, off), Air(20, off)));
        Assert.Equal("Autothrust off", e.Callout);
        Assert.Equal(AlertSeverity.Caution, e.Severity);
    }

    [Fact]
    public void Kein_Autothrust_off_beim_Aufsetzen()
    {
        var engine = WarmedUp(t => Final(t, 300));
        var off = Final(12, 20) with { Fma = Final(0, 0).Fma! with { Autothrust = "OFF" } };

        Assert.Empty(Feed(engine, Rules.AutothrustOff, off, off with { TimeUtc = T0.AddSeconds(14) }));
    }

    // ------------------------------------------------------------------ FMA

    [Fact]
    public void FMA_Wechsel_im_Anflug_wie_im_Cockpit()
    {
        var descent = new FmaState("SPEED", "DES", "ALT", "NAV", null, "AP1", true, "ACTIVE");
        var engine = WarmedUp(t => Air(t, descent) with { AltitudeFt = 8000, AglFt = 7000 });
        FlightSnapshot At(double t, FmaState fma) => Air(t, fma) with { AltitudeFt = 5000, AglFt = 4000 };

        // APPR gedrückt: LOC und G/S blau
        var appr = descent with { VerticalArmed = "G/S", LateralArmed = "LOC" };
        Assert.Empty(Feed(engine, Rules.FmaChange, At(12, appr), At(12.5, appr)));
        Assert.Equal("Loc blue, glideslope blue", Assert.Single(Feed(engine, Rules.FmaChange, At(13, appr))).Callout);

        var locStar = appr with { Lateral = "LOC*", LateralArmed = null };
        Assert.Equal("Loc star", Assert.Single(Feed(engine, Rules.FmaChange, At(20, locStar), At(21, locStar))).Callout);

        var gsStar = locStar with { Lateral = "LOC", Vertical = "G/S*", VerticalArmed = null };
        Assert.Equal("Loc, glideslope star", Assert.Single(Feed(engine, Rules.FmaChange, At(30, gsStar), At(31, gsStar))).Callout);

        var land = gsStar with { Lateral = "LAND", Vertical = "LAND" };
        var e = Assert.Single(Feed(engine, Rules.FmaChange, At(40, land), At(41, land)));
        Assert.Equal("Land", e.Callout);
        Assert.Equal(AlertSeverity.Info, e.Severity);
    }

    [Fact]
    public void ALT_blue_und_Zwischenzustaende_nicht_ansagen()
    {
        var climb = new FmaState("THR CLB", "OP CLB", null, "NAV", null, "AP1", true, "ACTIVE");
        var engine = WarmedUp(t => Air(t, climb));

        var altArmed = climb with { VerticalArmed = "ALT" };
        Assert.Equal("Alt blue", Assert.Single(Feed(engine, Rules.FmaChange, Air(12, altArmed), Air(13, altArmed))).Callout);

        // ALT* nur einen Moment, dann ALT: nur "Alt" ansagen
        var altStar = altArmed with { Vertical = "ALT*", VerticalArmed = null };
        var alt = altStar with { Vertical = "ALT" };
        Assert.Equal("Alt", Assert.Single(Feed(engine, Rules.FmaChange, Air(20, altStar), Air(20.5, alt), Air(21, alt), Air(21.6, alt))).Callout);

        // TCAS kommt als eigener Callout, nicht als FMA-Wechsel
        var tcas = alt with { Vertical = "TCAS" };
        Assert.Empty(Feed(engine, Rules.FmaChange, Air(30, tcas), Air(32, tcas)));
    }

    [Theory]
    [InlineData("LOC*", "Loc star")]
    [InlineData("G/S*", "Glideslope star")]
    [InlineData("G/S", "Glideslope")]
    [InlineData("ALT CST*", "Alt constraint star")]
    [InlineData("OP DES", "Open descent")]
    [InlineData("V/S", "Vertical speed")]
    [InlineData("APP NAV", "App nav")]
    [InlineData("FOO*", "Foo star")]
    public void FMA_Woerter(string mode, string word)
    {
        Assert.Equal(word, MonitorEngine.FmaWord(mode));
    }

    // ------------------------------------------------------------------ LOC / Glideslope

    [Fact]
    public void LOC_Abweichung_im_Endanflug()
    {
        var engine = WarmedUp(t => Final(t, 1500));

        Assert.Empty(Feed(engine, Rules.LocDeviation, Final(12, 1400, loc: 0.2), Final(13, 1300, loc: 0.4)));
        var e = Assert.Single(Feed(engine, Rules.LocDeviation, Final(14, 1200, loc: 0.4)));
        Assert.Equal("Loc", e.Callout);
        Assert.Equal(AlertSeverity.Caution, e.Severity);

        var clear = Feed(engine, Rules.LocDeviation, Final(15, 1100, loc: 0.1), Final(17.5, 900, loc: 0.1));
        Assert.Equal(AlertKind.Clear, Assert.Single(clear).Kind);
    }

    [Fact]
    public void Glideslope_Abweichung_ab_einem_Dot()
    {
        var engine = WarmedUp(t => Final(t, 1500));

        Assert.Empty(Feed(engine, Rules.GsDeviation, Final(12, 1400, gs: -0.8), Final(13, 1300, gs: -0.9)));
        Assert.Empty(Feed(engine, Rules.GsDeviation, Final(14, 1200, gs: -1.2)));
        Assert.Equal("Glideslope", Assert.Single(Feed(engine, Rules.GsDeviation, Final(15, 1100, gs: -1.3))).Callout);
    }

    [Fact]
    public void Keine_Abweichung_ohne_gefangenes_ILS_oder_ausserhalb_des_Fensters()
    {
        var engine = WarmedUp(t => Final(t, 1500));
        var nav = Final(0, 0).Fma! with { Lateral = "NAV", Vertical = "FINAL" };

        Assert.Empty(Feed(engine, Rules.LocDeviation, Final(12, 1400, loc: 1) with { Fma = nav }, Final(14, 1300, loc: 1) with { Fma = nav }));
        Assert.Empty(Feed(engine, Rules.LocDeviation, Final(20, 2500, loc: 1), Final(22, 2400, loc: 1)));
        Assert.Empty(Feed(engine, Rules.GsDeviation, Final(30, 80, gs: 2), Final(32, 60, gs: 2)));
    }

    // ------------------------------------------------------------------ Approach-Phase

    [Fact]
    public void Approach_phase_not_activated_einmal_je_Anflug()
    {
        FlightSnapshot At(double t, double nm, string phase = "DESCENT", bool managed = true) => Air(t) with
        {
            AltitudeFt = 5000, AglFt = 4800, DestinationDistanceNm = nm, FmgcPhase = phase, SpeedManaged = managed,
        };
        var engine = WarmedUp(t => At(t, 40));

        Assert.Empty(Feed(engine, Rules.ApproachPhase, At(12, 16)));
        var e = Assert.Single(Feed(engine, Rules.ApproachPhase, At(13, 14.8), At(14, 14), At(20, 10)));
        Assert.Equal("Approach phase not activated", e.Callout);
        Assert.Equal(AlertSeverity.Caution, e.Severity);

        // Wieder scharf erst ab 20 nm (z. B. nach einem Durchstarten mit neuem Anflug)
        Assert.Empty(Feed(engine, Rules.ApproachPhase, At(30, 18), At(31, 12)));
        Assert.Single(Feed(engine, Rules.ApproachPhase, At(40, 25), At(41, 14)));
    }

    [Theory]
    [InlineData("APPROACH", true)]
    [InlineData("GO AROUND", true)]
    [InlineData("DESCENT", false)]
    public void Approach_phase_aktiv_oder_Selected_Speed_kein_Callout(string phase, bool managed)
    {
        FlightSnapshot At(double t, double nm) => Air(t) with
        {
            AltitudeFt = 5000, AglFt = 4800, DestinationDistanceNm = nm, FmgcPhase = phase, SpeedManaged = managed,
        };
        var engine = WarmedUp(t => At(t, 40));

        Assert.Empty(Feed(engine, Rules.ApproachPhase, At(12, 14), At(13, 10)));
    }

    // ------------------------------------------------------------------ Bindings je Profil

    private static IReadOnlyList<AircraftProfile> RepoProfiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Kopilot", "profiles")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var options = Options.Create(new KopilotOptions { ProfilesDirectory = Path.Combine(dir!.FullName, "src", "Kopilot", "profiles") });
        return new ProfileStore(options, NullLogger<ProfileStore>.Instance).All;
    }

    [Fact]
    public void A339_LOC_GS_und_Approach_phase_fehlen_statt_geraten()
    {
        var a339 = RepoProfiles().Single(p => p.Name.Contains("A330", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(CalloutCoverage.Verified, CalloutCoverage.For(a339, Rules.TcasRa).Status);
        Assert.Equal(CalloutCoverage.Verified, CalloutCoverage.For(a339, Rules.AutothrustOff).Status);
        Assert.Equal(CalloutCoverage.Verified, CalloutCoverage.For(a339, Rules.FmaChange).Status);

        var loc = CalloutCoverage.For(a339, Rules.LocDeviation);
        Assert.Equal(CalloutCoverage.Missing, loc.Status);
        Assert.Equal(new[] { BindingKeys.LocDeviation }, loc.Missing);
        Assert.Equal(CalloutCoverage.Missing, CalloutCoverage.For(a339, Rules.GsDeviation).Status);
        Assert.Equal(new[] { BindingKeys.FmgcPhase }, CalloutCoverage.For(a339, Rules.ApproachPhase).Missing);
    }

    [Fact]
    public void A32NX_neue_Bindings_sind_als_ungeprueft_markiert()
    {
        var a32nx = RepoProfiles().Single(p => p.Name.Contains("A32NX", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(CalloutCoverage.Verified, CalloutCoverage.For(a32nx, Rules.TcasRa).Status);
        foreach (var rule in new[] { Rules.LocDeviation, Rules.GsDeviation, Rules.ApproachPhase })
        {
            var support = CalloutCoverage.For(a32nx, rule);
            Assert.Equal(CalloutCoverage.Unverified, support.Status);
            Assert.Empty(support.Missing);
        }

        Assert.Contains(BindingKeys.FmgcPhase, CalloutCoverage.For(a32nx, Rules.ApproachPhase).Unverified);
        Assert.False(a32nx.Bindings[BindingKeys.LocDeviation].Verified);
    }

    [Fact]
    public void Jedes_Profil_hat_einen_Stand_fuer_jeden_Callout()
    {
        foreach (var profile in RepoProfiles())
        {
            var coverage = CalloutCoverage.For(profile);
            Assert.Equal(CalloutCoverage.Requirements.Count, coverage.Count);
            Assert.All(coverage, c => Assert.Contains(c.Status, new[] { CalloutCoverage.Verified, CalloutCoverage.Unverified, CalloutCoverage.Missing }));
        }

        Assert.All(CalloutCoverage.For(null), c => Assert.Equal(CalloutCoverage.Missing, c.Status));
        Assert.All(CalloutCoverage.Requirements.Keys, r => Assert.Contains(r, Rules.All));
    }
}
