using Kopilot.Aircraft;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;

namespace Kopilot.Tests;

/// <summary>
/// Fuel Check im Reiseflug: Ansage an OFP-Wegpunkten, Intervall-Check, Warnungen bei wachsender Abweichung oder knapper
/// Landemenge, Nachtanken (Testlog 0.9, Prompt F) ist kein Leck.
/// </summary>
public sealed class FuelCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Route auf dem Äquator nach Osten (1° ≈ 60 nm): AAAA (0) – TOC (1, CLB) – HENDY (2) – TOBAK (3) – YWK (4) – TOD (5) – BBBB (6, DSC).
    /// Plan: 20 t beim Start, 400 kg je Grad, Landung 17,6 t; Minimum am Ziel 12 t.
    /// </summary>
    private static Ofp Plan(double minimumAtDestinationKg = 12000) => new()
    {
        RequestId = "fuel-test",
        Origin = new OfpAirport("AAAA", null, "09", 0, 0, null, null, null, null),
        Destination = new OfpAirport("BBBB", null, "27", 0, 6, null, null, null, null),
        Fuel = new OfpFuel { PlanTakeoffKg = 20000, PlanLandingKg = 17600 },
        Fixes =
        [
            Fix("TOC", 1, "CLB", type: "ltlg"),
            Fix("HENDY", 2, "CRZ"),
            Fix("TOBAK", 3, "CRZ"),
            Fix("YWK", 4, "CRZ", name: "WABUSH", type: "vor"),
            Fix("TOD", 5, "CRZ", type: "ltlg"),
            Fix("BBBB", 6, "DSC", type: "apt") with { FuelMinOnboardKg = minimumAtDestinationKg },
        ],
    };

    private static OfpFix Fix(string ident, double lon, string stage, string? name = null, string type = "wpt") => new()
    {
        Ident = ident,
        Name = name ?? ident,
        Type = type,
        Stage = stage,
        Lat = 0,
        Lon = lon,
        FuelPlanOnboardKg = 20000 - (400 * lon),
    };

    /// <summary>Planwert an der Position (linear wie im ProgressCalculator).</summary>
    private static double PlanAt(double lon) => 20000 - (400 * lon);

    private static FuelPlanSample Sample(Ofp ofp, double lon, double fuelKg, DateTimeOffset t)
    {
        var points = ofp.RoutePoints();
        var route = RouteTracker.Locate(points, 0, lon);
        Assert.NotNull(route);
        var progress = ProgressCalculator.Compute(ofp, points, route, fuelKg);
        var sample = FuelPlanSample.From(ofp, points, route, progress, t);
        Assert.NotNull(sample);
        return sample;
    }

    /// <summary>Reiseflug auf FL360 an Position <paramref name="lon"/> mit <paramref name="diffKg"/> gegen Plan.</summary>
    private static FlightSnapshot At(Ofp ofp, double sec, double lon, double diffKg, bool onGround = false, double? fuelKg = null)
    {
        var t = T0.AddSeconds(sec);
        var fuel = fuelKg ?? PlanAt(lon) + diffKg;
        return new FlightSnapshot
        {
            TimeUtc = t,
            Connected = true,
            FlightRunning = true,
            DataAgeSec = 0.1,
            AircraftKey = "test",
            OnGround = onGround,
            IasKt = onGround ? 0 : 270,
            GroundSpeedKt = onGround ? 0 : 480,
            VerticalSpeedFpm = 0,
            AltitudeFt = onGround ? 300 : 36000,
            PressureAltitudeFt = onGround ? 300 : 36000,
            AglFt = onGround ? 0 : 35000,
            BankRightDeg = 0,
            FlapsIndex = 0,
            GearExtendedPct = onGround ? 100 : 0,
            GearHandleDown = onGround,
            ApEngaged = !onGround,
            SelectedAltitudeFt = 36000,
            Limits = new AircraftLimits { VmoKt = 330 },
            FuelTotalKg = fuel,
            ZuluTimeSec = 79200 + sec,
            FuelPlan = onGround ? null : Sample(ofp, lon, fuel, t),
        };
    }

    private static List<AlertEvent> Fuel(MonitorEngine engine, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).Where(e => e.Rule is Rules.FuelCheck or Rules.FuelWarning).ToList();

    /// <summary>
    /// Ruhezeit nach dem Laden abwarten und den Bezug im Reiseflug setzen (Standard: zwischen TOC und HENDY).
    /// Danach liegen die Schritte der Tests eine Minute auseinander – so viel verbrennt ein Airliner auch.
    /// </summary>
    private static MonitorEngine Cruising(Ofp ofp, double diffKg, MonitorSettings? settings = null, double lon = 1.5)
    {
        var engine = new MonitorEngine(settings);
        Assert.Empty(Fuel(engine, At(ofp, 0, lon - 0.1, diffKg), At(ofp, 11, lon - 0.1, diffKg), At(ofp, 12, lon, diffKg)));
        return engine;
    }

    [Fact]
    public void Ueberflug_eines_Wegpunkts_im_Reiseflug_sagt_Ist_gegen_Plan_an()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -2200);

        Assert.Empty(Fuel(engine, At(ofp, 80, 1.9, -2200)));
        var e = Assert.Single(Fuel(engine, At(ofp, 140, 2.05, -2200)));

        Assert.Equal(Rules.FuelCheck, e.Rule);
        Assert.Equal(AlertSeverity.Info, e.Severity);
        Assert.Equal(AlertKind.Event, e.Kind);
        Assert.Equal("HENDY, fuel 17.0, 2.2 below plan", e.Callout);
        Assert.Contains("Fuel Check HENDY", e.Message);
        Assert.Contains("−2.200 kg", e.Message);

        var entry = Assert.Single(engine.FuelChecks.Last(10));
        Assert.Equal(FuelCheckKind.Waypoint, entry.Kind);
        Assert.Equal("HENDY", entry.Ident);
        Assert.InRange(entry.DiffKg ?? 0, -2201, -2199);
        Assert.False(entry.IsWarning);

        // Derselbe Wegpunkt nur einmal, der nächste wieder
        Assert.Empty(Fuel(engine, At(ofp, 200, 2.3, -2200), At(ofp, 260, 2.6, -2200)));
        var next = Assert.Single(Fuel(engine, At(ofp, 320, 3.05, -2150)));
        Assert.StartsWith("TOBAK, fuel ", next.Callout);
    }

    [Fact]
    public void Funkfeuer_wird_mit_Namen_angesagt()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, 30);

        Assert.Equal(2, Fuel(engine, At(ofp, 80, 2.5, 30), At(ofp, 140, 3.5, 30)).Count); // HENDY, TOBAK
        var e = Assert.Single(Fuel(engine, At(ofp, 200, 4.05, 30)));
        Assert.Equal("Wabush, fuel 18.4, on plan", e.Callout);
    }

    [Fact]
    public void Kein_Check_im_Steigflug_am_TOC_und_am_TOD()
    {
        var ofp = Plan();
        var engine = new MonitorEngine();

        // Startabschnitt (Flughafennähe) und TOC: keine Ansage
        Assert.Empty(Fuel(engine, At(ofp, 0, 0.5, -500), At(ofp, 11, 0.6, -500), At(ofp, 12, 0.9, -500), At(ofp, 80, 1.05, -500)));

        // TOD hat seine eigene Ansage, danach ist Sinkflug
        engine = Cruising(ofp, -500, lon: 4.5);
        Assert.Empty(Fuel(engine, At(ofp, 80, 4.9, -500), At(ofp, 140, 5.05, -500), At(ofp, 200, 5.5, -500)));
    }

    [Fact]
    public void Wachsende_Abweichung_warnt_sofort_und_nur_einmal_je_Schwelle()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -200);

        Assert.Empty(Fuel(engine, At(ofp, 80, 1.7, -450)));
        var e = Assert.Single(Fuel(engine, At(ofp, 140, 1.8, -600)));

        Assert.Equal(Rules.FuelWarning, e.Rule);
        Assert.Equal(AlertSeverity.Caution, e.Severity);
        Assert.Equal("Fuel 18.7, 0.6 below plan, deviation increased by 0.4", e.Callout);
        Assert.Contains("Leck möglich", e.Message);

        var entry = engine.FuelChecks.Last(1)[0];
        Assert.Equal(FuelCheckKind.Deviation, entry.Kind);
        Assert.Equal(new[] { "deviation" }, entry.Warnings);

        // Bezug ist jetzt die Warnung: 50 kg mehr sind keine neue Warnung, weitere 300+ kg schon
        Assert.Empty(Fuel(engine, At(ofp, 200, 1.85, -650)));
        Assert.Equal(Rules.FuelWarning, Assert.Single(Fuel(engine, At(ofp, 260, 1.9, -950))).Rule);
    }

    [Fact]
    public void Warnung_am_Wegpunkt_kommt_als_eine_Ansage_mit_Ort()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -200);

        var e = Assert.Single(Fuel(engine, At(ofp, 80, 2.05, -700)));
        Assert.Equal(Rules.FuelWarning, e.Rule);
        Assert.Equal("HENDY, fuel 18.5, 0.7 below plan, deviation increased by 0.5", e.Callout);
        Assert.Equal(FuelCheckKind.Waypoint, engine.FuelChecks.Last(1)[0].Kind);
    }

    [Fact]
    public void Landemenge_unter_Minimum_plus_Puffer_und_unter_Minimum()
    {
        // Plan landet 2,4 t über dem Minimum – Puffer 500 kg bleibt ungedeckelt: Grenze 15,7 t
        var ofp = Plan(minimumAtDestinationKg: 15200);
        var settings = new MonitorSettings { Fuel = new FuelCheckSettings { DeviationIncreaseKg = 0 } };
        var engine = Cruising(ofp, -1800, settings);

        var buffer = Assert.Single(Fuel(engine, At(ofp, 80, 1.7, -2000)));
        Assert.Equal(Rules.FuelWarning, buffer.Rule);
        Assert.Equal("Fuel 17.3, 2.0 below plan, landing fuel 15.6, minimum 15.2", buffer.Callout);
        Assert.Contains("unter Minimum + Puffer", buffer.Message);
        Assert.Equal(FuelCheckKind.Landing, engine.FuelChecks.Last(1)[0].Kind);

        // Nicht bei jedem Takt wiederholen
        Assert.Empty(Fuel(engine, At(ofp, 140, 1.75, -2050), At(ofp, 200, 1.8, -2020)));

        var minimum = Assert.Single(Fuel(engine, At(ofp, 260, 1.85, -2500)));
        Assert.Equal("Fuel 16.8, 2.5 below plan, landing fuel 15.1, below minimum", minimum.Callout);
        Assert.Equal(new[] { "landing_minimum" }, engine.FuelChecks.Last(1)[0].Warnings);
    }

    [Fact]
    public void Knapp_geplanter_Flug_warnt_nicht_schon_beim_ersten_Check()
    {
        // Nur 200 kg über dem Minimum geplant: Puffer höchstens 100 kg statt 500
        var ofp = Plan(minimumAtDestinationKg: 17400);
        var engine = Cruising(ofp, 0);

        var e = Assert.Single(Fuel(engine, At(ofp, 80, 2.05, -20)));
        Assert.Equal(Rules.FuelCheck, e.Rule);
        Assert.Equal("HENDY, fuel 19.2, on plan", e.Callout);
    }

    [Fact]
    public void Intervall_check_nach_30_Minuten_ohne_Wegpunkt()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -1000);

        // 30 min auf demselben Abschnitt (Sim-Zeit läuft im 20-s-Takt)
        var events = new List<AlertEvent>();
        for (var sec = 32; sec <= 12 + 1800 + 20; sec += 20)
        {
            events.AddRange(Fuel(engine, At(ofp, sec, 1.5, -1000)));
        }

        var e = Assert.Single(events);
        Assert.Equal(Rules.FuelCheck, e.Rule);
        Assert.Equal("Fuel 18.4, 1.0 below plan", e.Callout);
        Assert.Equal(FuelCheckKind.Interval, engine.FuelChecks.Last(1)[0].Kind);
    }

    [Fact]
    public void Nachtanken_im_Flug_ist_kein_Leck_und_beginnt_den_Vergleich_neu()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -400);

        // Toolbar/EFB: 3.526 → 11.090 kg sinngemäß, hier +7.564 kg auf einen Schlag
        var after = PlanAt(1.5) - 400 + 7564;
        var events = Fuel(engine,
            At(ofp, 13, 1.5, 0, fuelKg: after),
            At(ofp, 14, 1.5, 0, fuelKg: after),
            At(ofp, 16, 1.5, 0, fuelKg: after),
            At(ofp, 18, 1.5, 0, fuelKg: after));

        var e = Assert.Single(events);
        Assert.Equal(Rules.FuelCheck, e.Rule);
        Assert.Null(e.Callout);
        Assert.Contains("Nachtanken erkannt", e.Message);
        Assert.Contains("+7.560 kg", e.Message);
        Assert.Equal(FuelCheckKind.Refuel, engine.FuelChecks.Last(1)[0].Kind);

        // Neuer Bezug nach dem Tanken: ein wenig Verbrauch danach ist keine Warnung, wohl aber ein Verlust von 400 kg
        Assert.Empty(Fuel(engine, At(ofp, 80, 1.6, 7100), At(ofp, 140, 1.7, 7050), At(ofp, 200, 1.75, 6900)));
        var warn = Assert.Single(Fuel(engine, At(ofp, 260, 1.8, 6750)));
        Assert.Equal(Rules.FuelWarning, warn.Rule);
        Assert.Contains("seit dem Tanken", warn.Message);
    }

    [Fact]
    public void Nachtanken_am_Boden_wird_erkannt()
    {
        var ofp = Plan();
        var engine = new MonitorEngine();
        Assert.Empty(Fuel(engine, At(ofp, 0, 0, 0, onGround: true, fuelKg: 3526), At(ofp, 11, 0, 0, onGround: true, fuelKg: 3526)));

        // Realistisches Tanken: 50 kg/s über eine halbe Minute
        var events = new List<AlertEvent>();
        for (var i = 1; i <= 30; i++)
        {
            events.AddRange(Fuel(engine, At(ofp, 11 + i, 0, 0, onGround: true, fuelKg: 3526 + (50 * i))));
        }

        Assert.Empty(events);
        var e = Assert.Single(Fuel(engine, At(ofp, 45, 0, 0, onGround: true, fuelKg: 5026), At(ofp, 46, 0, 0, onGround: true, fuelKg: 5026)));
        Assert.Contains("3.530 kg → 5.030 kg", e.Message);
        Assert.Null(e.Callout);
    }

    [Fact]
    public void Sprung_nach_unten_ist_eine_manuelle_Aenderung_und_kein_Leck()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -100);

        var e = Assert.Single(Fuel(engine, At(ofp, 13, 1.5, -3100)));
        Assert.Equal(Rules.FuelCheck, e.Rule);
        Assert.Null(e.Callout);
        Assert.Contains("sprunghaft gesunken", e.Message);
        Assert.Equal(FuelCheckKind.Defuel, engine.FuelChecks.Last(1)[0].Kind);

        // Kein Leckverdacht für die 3 t: der Vergleich beginnt bei −3,1 t neu
        Assert.Empty(Fuel(engine, At(ofp, 80, 1.6, -3100), At(ofp, 140, 1.7, -3150)));
    }

    [Fact]
    public void Ohne_FUEL_CHECK_bleiben_Verlauf_und_Warnungen()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -200, new MonitorSettings { DisabledRules = new HashSet<string> { Rules.FuelCheck } });

        Assert.Empty(Fuel(engine, At(ofp, 80, 2.05, -250)));
        Assert.Equal(FuelCheckKind.Waypoint, Assert.Single(engine.FuelChecks.Last(10)).Kind);
        Assert.Null(engine.FuelChecks.Last(1)[0].Callout);

        Assert.Equal(Rules.FuelWarning, Assert.Single(Fuel(engine, At(ofp, 140, 2.3, -700))).Rule);
    }

    [Fact]
    public void Aenderung_waehrend_der_Pause_ist_kein_Leck()
    {
        var ofp = Plan();
        var engine = Cruising(ofp, -200);
        var paused = At(ofp, 20, 1.5, -200) with { Paused = true, PauseKind = "pause" };

        // In der Pause verbrennt der Sim nichts: 300 kg weniger danach (EFB) sind eine manuelle Änderung, kein wachsender Rückstand
        var events = Fuel(engine, paused, paused with { TimeUtc = T0.AddSeconds(600) }, At(ofp, 601, 1.5, -500), At(ofp, 610, 1.55, -520));
        Assert.DoesNotContain(events, e => e.Rule == Rules.FuelWarning);
    }

    [Theory]
    [InlineData("HENDY", null, null, 0.0, 0.0, "HENDY")]
    [InlineData("YWK", "WABUSH", "vor", 52.9, -66.9, "Wabush")]
    [InlineData("QX", null, "ndb", 50.0, 8.0, "quebec x-ray")]
    [InlineData("5350N", "53N050W", "wpt", 53.0, -50.0, "53 north 50 west")]
    [InlineData("H5330", null, "wpt", 53.5, -30.0, null)]
    public void Ort_fuer_die_Ansage(string ident, string? name, string? type, double lat, double lon, string? expected) =>
        Assert.Equal(expected, FuelCheckText.Place(new FuelCheckFix(ident, name, type, "CRZ", lat, lon)));

    [Theory]
    [InlineData(-30.0, 34000.0, "on plan")]
    [InlineData(-2180.0, 34120.0, "2.2 below plan")]
    [InlineData(420.0, 34120.0, "0.4 above plan")]
    [InlineData(-60.0, 34120.0, "0.1 below plan")]
    [InlineData(-120.0, 800.0, "120 kilos below plan")]
    public void Abweichung_fuer_die_Ansage(double diffKg, double actualKg, string expected) =>
        Assert.Equal(expected, FuelCheckText.Deviation(diffKg, actualKg));

    [Fact]
    public void Ansage_und_Meldung_formatieren()
    {
        Assert.Equal("Fuel 34.1, 2.2 below plan", FuelCheckText.Callout(null, 34120, -2180));
        Assert.Equal("HENDY, fuel 34.1, 2.2 below plan", FuelCheckText.Callout("HENDY", 34120, -2180));
        Assert.Equal("34.120 kg", FuelCheckText.Kg(34123));
        Assert.Equal("−2.180 kg", FuelCheckText.Kg(-2183));
        Assert.Equal("+1.200 kg", FuelCheckText.SignedKg(1200));
        Assert.Equal("0 kg", FuelCheckText.Kg(-3));
    }

    [Fact]
    public void Neuer_Plan_beginnt_einen_neuen_Verlauf()
    {
        var log = new FuelCheckLog();
        log.BeginPlan("a");
        log.Add(new FuelCheckEntry { Kind = FuelCheckKind.Waypoint, ActualKg = 1, Message = "x" });
        log.BeginPlan("a");
        Assert.Single(log.Last(10));
        log.BeginPlan("b");
        Assert.Empty(log.Last(10));
        Assert.Null(log.LastCheck);
    }
}
