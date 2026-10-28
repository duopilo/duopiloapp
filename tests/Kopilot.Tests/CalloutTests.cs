using Kopilot.Aircraft;
using Kopilot.Monitoring;
using Kopilot.Speech;

namespace Kopilot.Tests;

/// <summary>Pilot-Monitoring-Callouts (v0.6) und die Warteschlange der Sprachausgabe.</summary>
public sealed class CalloutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly AircraftLimits Limits = new()
    {
        VmoKt = 350,
        VleKt = 280,
        VfeKt = new Dictionary<string, double> { ["1"] = 230, ["1+F"] = 215, ["2"] = 200, ["3"] = 185, ["FULL"] = 177 },
        LandingFlaps = ["3", "FULL"],
    };

    private static FlightSnapshot Base(double sec) => new()
    {
        TimeUtc = T0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
        DataAgeSec = 0.1,
        AircraftKey = "test",
        Limits = Limits,
        ApEngaged = false,
    };

    /// <summary>Am Boden, steht.</summary>
    private static FlightSnapshot Gate(double sec) => Base(sec) with
    {
        OnGround = true, IasKt = 0, GroundSpeedKt = 0, VerticalSpeedFpm = 0, AltitudeFt = 150, PressureAltitudeFt = 150,
        AglFt = 0, RadioAltFt = 0, FlapsIndex = 2, FlapsLabel = "1+F", GearExtendedPct = 100, GearHandleDown = true,
        V1Kt = 140, VrKt = 145,
    };

    /// <summary>In der Luft.</summary>
    private static FlightSnapshot Air(double sec, double alt = 12000, double vs = 0, double ias = 250) => Base(sec) with
    {
        OnGround = false, IasKt = ias, GroundSpeedKt = ias + 40, VerticalSpeedFpm = vs, AltitudeFt = alt, PressureAltitudeFt = alt,
        AglFt = alt - 100, FlapsIndex = 0, FlapsLabel = "0", GearExtendedPct = 0, GearHandleDown = false,
    };

    private static List<AlertEvent> Feed(MonitorEngine engine, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).ToList();

    private static List<string?> Callouts(IEnumerable<AlertEvent> events) => events.Select(e => e.Callout).ToList();

    [Fact]
    public void Startlauf_100_knots_V1_Rotate()
    {
        var engine = new MonitorEngine();
        Feed(engine, Gate(0), Gate(11));

        var roll = (double s, double ias) => Gate(s) with { IasKt = ias, GroundSpeedKt = ias };
        var events = Feed(engine, roll(20, 60), roll(21, 95), roll(22, 105), roll(23, 138), roll(24, 142), roll(25, 147));

        Assert.Equal(new[] { "One hundred knots", "V1", "Rotate" }, Callouts(events));
    }

    [Fact]
    public void Positive_climb_nach_dem_Abheben()
    {
        var engine = new MonitorEngine();
        Feed(engine, Gate(0), Gate(11) with { IasKt = 150, GroundSpeedKt = 150 });

        var climb = (double s, double ra, double vs) => Gate(s) with { OnGround = false, IasKt = 155, GroundSpeedKt = 155, RadioAltFt = ra, AglFt = ra, VerticalSpeedFpm = vs };
        var events = Feed(engine, climb(12, 10, 200), climb(13, 40, 900), climb(14, 120, 1500));

        Assert.Equal(new[] { "Positive climb" }, Callouts(events));
    }

    [Fact]
    public void Landerollen_Spoilers_Reverse_Decel_70_knots()
    {
        var engine = new MonitorEngine();
        var final = (double s) => Air(s, alt: 500, vs: -700, ias: 135) with { RadioAltFt = 50, AglFt = 50, FlapsIndex = 5, FlapsLabel = "FULL", GearExtendedPct = 100, GearHandleDown = true };
        Feed(engine, final(0), final(11));

        var roll = (double s, double ias) => final(s) with { OnGround = true, IasKt = ias, GroundSpeedKt = ias, VerticalSpeedFpm = 0, RadioAltFt = 0, AglFt = 0 };
        var events = Feed(engine,
            roll(12, 130),
            roll(12.5, 128) with { GroundSpoilersOut = true },
            roll(13, 125) with { GroundSpoilersOut = true, ReverserPct = 95 },
            roll(14, 110) with { GroundSpoilersOut = true, ReverserPct = 95, DecelLight = true },
            roll(16, 75) with { GroundSpoilersOut = true, ReverserPct = 20, DecelLight = true },
            roll(17, 65) with { GroundSpoilersOut = true, DecelLight = true });

        Assert.Equal(new[] { "Spoilers", "Reverse green", "Decel", "Seventy knots" }, Callouts(events));
    }

    [Fact]
    public void No_spoilers_wenn_sie_nach_dem_Aufsetzen_fehlen()
    {
        var engine = new MonitorEngine();
        var final = (double s) => Air(s, alt: 500, vs: -700, ias: 135) with { RadioAltFt = 50, AglFt = 50 };
        Feed(engine, final(0), final(11));

        var roll = (double s) => final(s) with { OnGround = true, IasKt = 120, GroundSpeedKt = 120, RadioAltFt = 0, GroundSpoilersOut = false, DecelLight = false };
        var events = Feed(engine, roll(12), roll(14), roll(15.5));

        var e = Assert.Single(events);
        Assert.Equal("No spoilers", e.Callout);
        Assert.Equal(AlertSeverity.Caution, e.Severity);
    }

    [Fact]
    public void Ten_thousand_und_Transition_altitude_im_Steigflug()
    {
        var engine = new MonitorEngine();
        var climb = (double s, double alt) => Air(s, alt, vs: 2500, ias: 250) with { TransitionAltitudeFt = 5000, TransitionLevelFt = 7000 };
        Feed(engine, climb(0, 3000), climb(11, 4000));

        var events = Feed(engine, climb(12, 4900), climb(13, 5100), climb(20, 9900), climb(21, 10100), climb(30, 10600));

        Assert.Equal(new[] { "Transition altitude", "Ten thousand" }, Callouts(events));
    }

    [Fact]
    public void Transition_level_im_Sinkflug()
    {
        var engine = new MonitorEngine();
        var descent = (double s, double alt) => Air(s, alt, vs: -1800, ias: 250) with { TransitionLevelFt = 7000 };
        Feed(engine, descent(0, 9000), descent(11, 8000));

        var events = Feed(engine, descent(12, 7200), descent(13, 6900));

        Assert.Equal(new[] { "Transition level" }, Callouts(events));
    }

    [Fact]
    public void Kein_Ten_thousand_bei_Hoehensprung()
    {
        // Positionswechsel/Slew von FL350 auf 1500 ft: kein echtes Durchfliegen
        var engine = new MonitorEngine();
        Feed(engine, Air(0, 35000, -2000, 250), Air(11, 35000, -2000, 250));

        Assert.DoesNotContain(Feed(engine, Air(12, 1500, -2000, 250), Air(13, 1470, -2000, 250)), e => e.Rule == Rules.PmTenThousand);
    }

    [Fact]
    public void Klappen_mit_Speed_check_erst_wenn_der_Hebel_steht()
    {
        var engine = new MonitorEngine();
        Feed(engine, Air(0, 3000, -500, 200), Air(11, 3000, -500, 200));

        var flaps = (double s, string label, double index) => Air(s, 3000, -500, 200) with { FlapsLabel = label, FlapsIndex = index };

        // Hebel über 1 auf 2 – angesagt wird nur die Endstellung
        Assert.Empty(Feed(engine, flaps(12, "1", 1), flaps(12.5, "2", 3), flaps(13.5, "2", 3)));
        var events = Feed(engine, flaps(14.2, "2", 3));
        Assert.Equal(new[] { "Speed checked, flaps two" }, Callouts(events));

        // Einfahren ohne Speed-Check
        Feed(engine, flaps(20, "0", 0));
        Assert.Equal(new[] { "Flaps zero" }, Callouts(Feed(engine, flaps(21.6, "0", 0))));
    }

    [Fact]
    public void Klappen_zu_schnell()
    {
        var engine = new MonitorEngine();
        Feed(engine, Air(0, 3000, -500, 225), Air(11, 3000, -500, 225));

        var flaps = (double s) => Air(s, 3000, -500, 225) with { FlapsLabel = "2", FlapsIndex = 3 };
        var events = Feed(engine, flaps(12), flaps(13.6));

        Assert.Contains("Flaps two, speed high", Callouts(events));
    }

    [Fact]
    public void Fahrwerkshebel_wird_angesagt()
    {
        var engine = new MonitorEngine();
        Feed(engine, Air(0, 2500, -700, 160), Air(11, 2500, -700, 160));

        var gear = (double s) => Air(s, 2500, -700, 160) with { GearHandleDown = true };
        Assert.Empty(Feed(engine, gear(12), gear(12.5)));
        Assert.Equal(new[] { "Gear down" }, Callouts(Feed(engine, gear(13.1))));
    }

    [Fact]
    public void Radiohoehen_und_Minimum_nur_wenn_eingeschaltet()
    {
        // Landekonfiguration gesetzt, damit nur die Höhenansagen kommen
        var approach = (double s, double ra) => Air(s, ra + 500, -700, 140) with
        {
            RadioAltFt = ra, AglFt = ra, DecisionHeightFt = 200, GearExtendedPct = 100, GearHandleDown = true, FlapsLabel = "FULL", FlapsIndex = 5,
        };
        FlightSnapshot[] path = [approach(12, 990), approach(13, 490), approach(14, 395), approach(15, 295), approach(16, 195)];

        var off = new MonitorEngine();
        Feed(off, approach(0, 1200), approach(11, 1100));
        Assert.Empty(Feed(off, path));

        var on = new MonitorEngine(new MonitorSettings { AircraftAutoCallouts = true });
        Feed(on, approach(0, 1200), approach(11, 1100));
        var events = Feed(on, path);

        Assert.Equal(new[] { "One thousand", "Five hundred", "Four hundred", "Hundred above", "Minimum" }, Callouts(events));
    }

    // ------------------------------------------------------------------ Warteschlange der Sprachausgabe

    private static AlertEvent Alert(long id, AlertSeverity severity, string callout, double ageSec = 0, string rule = "TEST", AlertKind kind = AlertKind.Event) =>
        new(id, T0.AddSeconds(-ageSec), rule, severity, kind, callout, "x");

    [Fact]
    public void Warteschlange_filtert_und_ordnet()
    {
        var queue = new CalloutQueue();
        var settings = new SpeechSettings { MinSeverity = AlertSeverity.Info, MutedRules = ["PM_GEAR"] };

        Assert.False(queue.Offer(Alert(1, AlertSeverity.Info, "Gear down", rule: "PM_GEAR"), settings, T0, null).Accepted);
        Assert.False(queue.Offer(Alert(2, AlertSeverity.Info, "Old", ageSec: 20), settings, T0, null).Accepted);
        Assert.False(queue.Offer(Alert(3, AlertSeverity.Caution, "Speed", kind: AlertKind.Clear), settings, T0, null).Accepted);

        Assert.True(queue.Offer(Alert(4, AlertSeverity.Info, "Ten thousand"), settings, T0, null).Accepted);
        Assert.True(queue.Offer(Alert(5, AlertSeverity.Caution, "Speed limit"), settings, T0, null).Accepted);

        Assert.Equal("Speed limit", queue.Take(T0)?.Callout);
        Assert.Equal("Ten thousand", queue.Take(T0)?.Callout);
        Assert.Null(queue.Take(T0));
    }

    [Fact]
    public void Warning_unterbricht_und_verdraengt()
    {
        var queue = new CalloutQueue();
        var settings = new SpeechSettings();

        queue.Offer(Alert(1, AlertSeverity.Info, "Flaps two"), settings, T0, null);
        var (accepted, preempt) = queue.Offer(Alert(2, AlertSeverity.Warning, "Master warning, ENG 1 FIRE"), settings, T0, AlertSeverity.Info);

        Assert.True(accepted);
        Assert.True(preempt);
        Assert.Equal(1, queue.Count);
        Assert.Equal("Master warning, ENG 1 FIRE", queue.Take(T0)?.Callout);
    }

    [Fact]
    public void Gleiche_Ansage_nicht_doppelt_kurz_hintereinander()
    {
        var queue = new CalloutQueue();
        var settings = new SpeechSettings { MinSeverity = AlertSeverity.Caution };

        Assert.False(queue.Offer(Alert(1, AlertSeverity.Info, "Ten thousand"), settings, T0, null).Accepted);
        Assert.True(queue.Offer(Alert(2, AlertSeverity.Caution, "Speed"), settings, T0, null).Accepted);
        Assert.False(queue.Offer(Alert(3, AlertSeverity.Caution, "Speed"), settings, T0.AddSeconds(2), null).Accepted);
        Assert.True(queue.Offer(Alert(4, AlertSeverity.Caution, "Speed"), settings, T0.AddSeconds(5), null).Accepted);
    }

    [Fact]
    public void Einstellungen_werden_begrenzt()
    {
        var s = new SpeechSettings { Volume = 3, Rate = 0.1, Voice = "  ", MutedRules = ["pm_gear", "PM_GEAR", " "] }.Normalized();

        Assert.Equal(1, s.Volume);
        Assert.Equal(0.5, s.Rate);
        Assert.Null(s.Voice);
        Assert.Equal(new[] { "PM_GEAR" }, s.MutedRules);
    }
}
