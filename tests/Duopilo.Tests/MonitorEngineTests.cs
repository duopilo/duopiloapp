using Kopilot.Aircraft;
using Kopilot.Atc;
using Kopilot.Monitoring;

namespace Kopilot.Tests;

public sealed class MonitorEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Reiseflug, alles normal.</summary>
    private static FlightSnapshot Cruise(double sec) => new()
    {
        TimeUtc = T0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
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
        Limits = new AircraftLimits { VmoKt = 350, VleKt = 280, VfeKt = new Dictionary<string, double> { ["1"] = 230 } },
    };

    private static List<AlertEvent> Feed(MonitorEngine engine, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).ToList();

    /// <summary>Ruhezeit nach dem Laden (10 s) abwarten.</summary>
    private static MonitorEngine WarmedUp()
    {
        var engine = new MonitorEngine();
        Assert.Empty(Feed(engine, Cruise(0), Cruise(11)));
        return engine;
    }

    [Fact]
    public void Nach_dem_Laden_wird_zehn_Sekunden_nichts_gemeldet()
    {
        var engine = new MonitorEngine();
        var gearOut = (double s) => Cruise(s) with { IasKt = 300, GearExtendedPct = 100, GearHandleDown = true };

        Assert.Empty(Feed(engine, gearOut(0), gearOut(5), gearOut(9.5)));

        var events = Feed(engine, gearOut(10.5));
        var e = Assert.Single(events);
        Assert.Equal(Rules.GearSpeed, e.Rule);
        Assert.Equal(AlertKind.Raise, e.Kind);
        Assert.Equal("Gear speed", e.Callout);
    }

    [Fact]
    public void Autopilot_aus_wird_einmal_gemeldet()
    {
        var engine = WarmedUp();
        var apOff = (double s) => Cruise(s) with { ApEngaged = false };

        Assert.Empty(Feed(engine, apOff(12), apOff(12.3)));
        var e = Assert.Single(Feed(engine, apOff(12.6)));
        Assert.Equal(Rules.ApDisconnect, e.Rule);
        Assert.Equal("Autopilot off", e.Callout);
        Assert.Empty(Feed(engine, apOff(13), apOff(20)));
    }

    [Fact]
    public void Kurz_aufblitzender_Autopilot_ist_kein_Disconnect()
    {
        // Generischer AP wird beim Start kurz aktiv (Headwind A330 ohne Profil)
        var engine = new MonitorEngine();
        var climb = (double s, bool ap) => Cruise(s) with { ApEngaged = ap, AltitudeFt = 400, AglFt = 250, VerticalSpeedFpm = 2000 };
        Feed(engine, climb(0, false), climb(11, false));

        Assert.DoesNotContain(Feed(engine, climb(12, true), climb(12.5, true), climb(13, false), climb(13.6, false), climb(15, false)), e => e.Rule == Rules.ApDisconnect);
    }

    [Fact]
    public void Kein_thousand_to_go_am_Boden()
    {
        // Beim Laden/Rollen springt "am Boden" kurz auf false und V/S ausschlägt – trotzdem keine Ansage
        var engine = WarmedUp();
        var taxi = (double s, double vs) => Cruise(s) with { OnGround = false, AltitudeFt = 150, AglFt = 0, RadioAltFt = 0, SelectedAltitudeFt = 3000, VerticalSpeedFpm = vs };

        Assert.DoesNotContain(Feed(engine, taxi(12, 0), taxi(13, 600), taxi(14, 900)), e => e.Rule == Rules.Alt1000ToGo);
    }

    [Fact]
    public void Autopilot_aus_am_Boden_ist_still()
    {
        var engine = WarmedUp();
        var landed = (double s, bool ap) => Cruise(s) with { OnGround = true, ApEngaged = ap, AglFt = 0, AltitudeFt = 100, IasKt = 120 };

        Assert.DoesNotContain(Feed(engine, landed(12, true), landed(13, false), landed(14, false)), e => e.Rule == Rules.ApDisconnect);
    }

    [Fact]
    public void Pause_meldet_nur_Sim_paused_und_haelt_alles_andere_an()
    {
        var engine = WarmedUp();
        var gearOut = (double s, bool paused) => Cruise(s) with { IasKt = 300, GearExtendedPct = 100, Paused = paused, PauseKind = paused ? "full" : null };

        var paused = Feed(engine, gearOut(12, true));
        var e = Assert.Single(paused);
        Assert.Equal(Rules.SimPaused, e.Rule);
        Assert.Equal("Sim paused", e.Callout);
        Assert.Equal("paused", engine.Phase);

        Assert.Empty(Feed(engine, gearOut(15, true), gearOut(30, true)));

        // Weiter: SIM_PAUSED endet still, danach 3 s Ruhe
        var resumed = Feed(engine, gearOut(31, false));
        Assert.All(resumed, r => Assert.Equal(AlertKind.Clear, r.Kind));
        Assert.Contains(resumed, r => r.Rule == Rules.SimPaused);
        Assert.Empty(Feed(engine, gearOut(33, false)));

        var later = Feed(engine, gearOut(34.5, false));
        Assert.Contains(later, r => r.Rule == Rules.GearSpeed && r.Kind == AlertKind.Raise);
    }

    [Fact]
    public void One_thousand_to_go_beim_Sinken_auf_die_gewaehlte_Hoehe()
    {
        var engine = WarmedUp();
        var descent = (double s, double alt) => Cruise(s) with { AltitudeFt = alt, SelectedAltitudeFt = 20000, VerticalSpeedFpm = -2000 };

        Assert.Empty(Feed(engine, descent(12, 29000), descent(13, 21500)));
        var e = Assert.Single(Feed(engine, descent(14, 20900)));
        Assert.Equal(Rules.Alt1000ToGo, e.Rule);
        Assert.Equal("One thousand to go", e.Callout);
        Assert.Empty(Feed(engine, descent(15, 20500), descent(16, 20100)));
    }

    [Fact]
    public void Kein_thousand_to_go_wenn_man_sich_entfernt()
    {
        var engine = WarmedUp();

        // Final: FCU steht auf der Fehlanflughöhe über dem Flugzeug, das Flugzeug sinkt weg
        var final = (double s, double alt) => Cruise(s) with { AltitudeFt = alt, AglFt = alt - 100, SelectedAltitudeFt = 5000, VerticalSpeedFpm = -800 };
        Assert.DoesNotContain(Feed(engine, final(12, 3000), final(20, 4200), final(30, 3900)), e => e.Rule == Rules.Alt1000ToGo);
    }

    [Fact]
    public void Sink_rate_in_Bodennaehe()
    {
        var engine = WarmedUp();
        var sink = (double s) => Cruise(s) with { AltitudeFt = 1500, AglFt = 800, RadioAltFt = 800, VerticalSpeedFpm = -2000, FlapsIndex = 3, GearExtendedPct = 100 };

        Assert.Empty(Feed(engine, sink(12), sink(13)));
        var events = Feed(engine, sink(13.6));
        Assert.Contains(events, e => e.Rule == Rules.SinkRate && e.Callout == "Sink rate");
    }

    [Fact]
    public void Bank_angle_ist_nahe_am_Boden_strenger()
    {
        var engine = WarmedUp();
        var bank = (double s) => Cruise(s) with { AltitudeFt = 400, AglFt = 100, RadioAltFt = 100, BankRightDeg = -30 };

        Assert.Empty(Feed(engine, bank(12)));
        var e = Assert.Single(Feed(engine, bank(13.1)), x => x.Rule == Rules.BankAngle);
        Assert.Equal("Bank angle", e.Callout);
        Assert.Contains("left", e.Message);
    }

    [Fact]
    public void Master_warning_nennt_den_ECAM_Titel()
    {
        var engine = WarmedUp();
        var fire = (double s) => Cruise(s) with { MasterWarning = true, EcamTitles = ["ENG 1 FIRE"] };

        Assert.Empty(Feed(engine, fire(12)));
        var e = Assert.Single(Feed(engine, fire(12.8)));
        Assert.Equal(Rules.MasterWarning, e.Rule);
        Assert.Equal("Master warning, ENG 1 FIRE", e.Callout);
    }

    [Fact]
    public void AP_OFF_wird_nicht_doppelt_angesagt()
    {
        var engine = WarmedUp();
        var apOff = (double s) => Cruise(s) with { ApEngaged = false, MasterWarning = true, EcamTitles = ["AUTO FLT AP OFF"] };

        var events = Feed(engine, apOff(12), apOff(12.6), apOff(12.8));
        Assert.Contains(events, e => e.Rule == Rules.ApDisconnect && e.Callout == "Autopilot off");
        var mw = Assert.Single(events, e => e.Rule == Rules.MasterWarning);
        Assert.Null(mw.Callout);
    }

    [Fact]
    public void Speed_limit_unter_FL100()
    {
        var engine = new MonitorEngine();
        var low = (double s, double ias) => Cruise(s) with { AltitudeFt = 8000, PressureAltitudeFt = 8000, AglFt = 7500, IasKt = ias };

        Assert.Empty(Feed(engine, low(0, 240), low(11, 240)));
        Assert.Empty(Feed(engine, low(12, 280), low(15, 280)));
        var e = Assert.Single(Feed(engine, low(17.1, 280)));
        Assert.Equal(Rules.SpeedLimitFl100, e.Rule);
        Assert.Equal("Speed limit", e.Callout);

        // Wieder langsam: nach der Abfallverzögerung gelöscht
        var cleared = Feed(engine, low(18, 245), low(21.5, 245));
        Assert.Contains(cleared, c => c.Rule == Rules.SpeedLimitFl100 && c.Kind == AlertKind.Clear);
    }

    [Fact]
    public void Speed_deviation_erst_wenn_das_Ziel_eine_Weile_steht()
    {
        var engine = new MonitorEngine();
        var fast = (double s) => Cruise(s) with { SpeedTargetKt = 250, IasKt = 270 };

        Assert.Empty(Feed(engine, fast(0), fast(11), fast(19)));
        Assert.Empty(Feed(engine, fast(21), fast(22)));
        var e = Assert.Single(Feed(engine, fast(25.5)));
        Assert.Equal(Rules.SpeedDeviation, e.Rule);
        Assert.Equal("Speed", e.Callout);
    }

    [Fact]
    public void Runway_assigned_wird_einmal_angesagt()
    {
        var engine = WarmedUp();
        var rwy = new AssignedRunway(23, "L", "simbrief", "EDDL", 233);
        var assigned = (double s) => Cruise(s) with { AssignedRunway = rwy };

        var e = Assert.Single(Feed(engine, assigned(12)));
        Assert.Equal(Rules.RunwayAssigned, e.Rule);
        Assert.Equal("Runway two three left assigned", e.Callout);
        Assert.Empty(Feed(engine, assigned(13), assigned(20)));
    }

    [Fact]
    public void Abgeschaltete_Regel_bleibt_still()
    {
        var engine = new MonitorEngine(new MonitorSettings { DisabledRules = new HashSet<string> { Rules.GearSpeed } });
        var gearOut = (double s) => Cruise(s) with { IasKt = 300, GearExtendedPct = 100 };

        Assert.DoesNotContain(Feed(engine, gearOut(0), gearOut(11), gearOut(20)), e => e.Rule == Rules.GearSpeed);
    }

    [Fact]
    public void Eingefrorene_Daten_werden_nicht_ausgewertet()
    {
        var engine = WarmedUp();
        var stale = (double s) => Cruise(s) with { DataAgeSec = 8, IasKt = 300, GearExtendedPct = 100 };

        Assert.Empty(Feed(engine, stale(12), stale(20)));
        Assert.Equal("stale", engine.Phase);
    }
}
