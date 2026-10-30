using Kopilot.Aircraft;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Traffic;

namespace Kopilot.Tests;

/// <summary>get_traffic: Peilung, Abstand, Uhrzeit-Angabe, Callout und die Monitoring-Regel TRAFFIC_PROXIMITY.</summary>
public sealed class TrafficTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const double OwnLat = 51.0;
    private const double OwnLon = 7.0;

    /// <summary>Fremdes Flugzeug in gegebener rechtweisender Peilung und Entfernung vom eigenen Flugzeug.</summary>
    private static AircraftSample Target(
        uint id,
        double bearingTrueDeg,
        double distanceNm,
        double altitudeFt,
        double verticalSpeedFpm = 0,
        bool onGround = false,
        string? atcId = null,
        string? model = null)
    {
        var (lat, lon) = RouteMath.Destination(OwnLat, OwnLon, bearingTrueDeg, distanceNm);
        return new AircraftSample(id, "aircraft", atcId ?? $"D-AI{id:00}", null, null, model, lat, lon, altitudeFt, 270, 268, 250, verticalSpeedFpm, onGround, false);
    }

    private static AircraftSample Own(double altitudeFt = 5000, double headingTrueDeg = 90, bool onGround = false) =>
        new(1, "aircraft", "D-KOPI", null, null, "A20N", OwnLat, OwnLon, altitudeFt, headingTrueDeg, headingTrueDeg - 2, 220, 0, onGround, true);

    private static TrafficPicture Picture(DateTimeOffset time, params AircraftSample[] samples) =>
        TrafficPictureBuilder.Build(time, samples, null, 40);

    // ------------------------------------------------------------------ Uhrzeit-Angabe und relative Peilung

    [Theory]
    [InlineData(0, 12)]
    [InlineData(14.9, 12)]
    [InlineData(15, 1)]
    [InlineData(44.9, 1)]
    [InlineData(45, 2)]
    [InlineData(60, 2)]
    [InlineData(90, 3)]
    [InlineData(135, 5)]
    [InlineData(180, 6)]
    [InlineData(270, 9)]
    [InlineData(300, 10)]
    [InlineData(344.9, 11)]
    [InlineData(345, 12)]
    [InlineData(359.9, 12)]
    [InlineData(-30, 11)]
    [InlineData(390, 1)]
    public void Relative_Peilung_wird_zur_Uhrzeit(double relativeBearingDeg, int expectedClock)
    {
        Assert.Equal(expectedClock, TrafficMath.ClockPosition(relativeBearingDeg));
    }

    [Fact]
    public void Uhrzeit_als_Text()
    {
        Assert.Equal("2 o'clock", TrafficMath.ClockText(2));
        Assert.Equal("12 o'clock", TrafficMath.ClockText(12));
    }

    [Theory]
    [InlineData(30, 350, 40)]
    [InlineData(350, 10, 340)]
    [InlineData(90, 90, 0)]
    [InlineData(0, 270, 90)]
    [InlineData(180, 0, 180)]
    [InlineData(10, 190, 180)]
    public void Relative_Peilung_bezieht_sich_auf_den_eigenen_Kurs(double bearingTrueDeg, double ownHeadingTrueDeg, double expected)
    {
        Assert.Equal(expected, TrafficMath.RelativeBearingDeg(bearingTrueDeg, ownHeadingTrueDeg), 6);
    }

    // ------------------------------------------------------------------ Abstand und wahre Peilung

    [Fact]
    public void Ein_Breitengrad_sind_sechzig_Meilen()
    {
        Assert.Equal(60.04, RouteMath.DistanceNm(50, 7, 51, 7), 2);
        Assert.Equal(60.04, RouteMath.DistanceNm(0, 0, 0, 1), 2);
        Assert.Equal(0, RouteMath.DistanceNm(51, 7, 51, 7), 6);
    }

    [Fact]
    public void Abstand_und_Peilung_Duesseldorf_Koeln()
    {
        // EDDL → EDDK: knapp 30 NM Richtung Südsüdost
        Assert.Equal(29.12, RouteMath.DistanceNm(51.2895, 6.7668, 50.8659, 7.1427), 2);
        Assert.Equal(150.7, RouteMath.BearingDeg(51.2895, 6.7668, 50.8659, 7.1427), 1);
    }

    [Theory]
    [InlineData(52, 7, 0)]
    [InlineData(50, 7, 180)]
    [InlineData(0.0, 1, 90)]
    [InlineData(0.0, -1, 270)]
    public void Wahre_Peilung_in_die_Himmelsrichtungen(double lat, double lon, double expected)
    {
        var fromLat = lat == 0 ? 0 : 51;
        var fromLon = lat == 0 ? 0 : 7;
        Assert.Equal(expected, RouteMath.BearingDeg(fromLat, fromLon, lat, lon), 6);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(45, 0.5)]
    [InlineData(150, 5)]
    [InlineData(225, 12.3)]
    [InlineData(330, 39)]
    public void Ziel_landet_in_Peilung_und_Abstand_an_der_richtigen_Stelle(double bearingTrueDeg, double distanceNm)
    {
        var picture = Picture(T0, Own(headingTrueDeg: 0), Target(2, bearingTrueDeg, distanceNm, 5000));

        var t = Assert.Single(picture.Targets);
        Assert.Equal(distanceNm, t.DistanceNm, 3);
        Assert.Equal(bearingTrueDeg, t.BearingTrueDeg, 2);

        // Kurs 0 → relative = wahre Peilung
        Assert.Equal(bearingTrueDeg, t.RelativeBearingDeg, 2);
    }

    // ------------------------------------------------------------------ Callout

    [Fact]
    public void Callout_wie_im_Beispiel()
    {
        // Eigener Kurs 090, Ziel in 150° rechtweisend → 60° rechts = 2 Uhr
        var picture = Picture(T0, Own(altitudeFt: 5000, headingTrueDeg: 90), Target(2, 150, 5.2, 6020, verticalSpeedFpm: -800));

        var t = Assert.Single(picture.Targets);
        Assert.Equal(2, t.Clock);
        Assert.Equal("2 o'clock", t.ClockText);
        Assert.Equal("descending", t.VerticalTrend);
        Assert.Equal(1020, t.AltitudeDiffFt, 6);
        Assert.Equal("Traffic, 2 o'clock, 5 miles, 1000 feet above, descending", t.Callout);
    }

    [Fact]
    public void Callout_ohne_Zusatz_im_Horizontalflug()
    {
        var picture = Picture(T0, Own(altitudeFt: 8000, headingTrueDeg: 0), Target(2, 270, 0.6, 7520));

        Assert.Equal("Traffic, 9 o'clock, less than 1 mile, 500 feet below", Assert.Single(picture.Targets).Callout);
    }

    [Fact]
    public void Callout_fuer_Ziel_am_Boden()
    {
        var picture = Picture(T0, Own(altitudeFt: 2500, headingTrueDeg: 0), Target(2, 330, 2.1, 160, verticalSpeedFpm: 0, onGround: true));

        var t = Assert.Single(picture.Targets);
        Assert.Equal("level", t.VerticalTrend);
        Assert.Equal("Traffic, 11 o'clock, 2 miles, on ground", t.Callout);
    }

    [Theory]
    [InlineData(0.4, "less than 1 mile")]
    [InlineData(0.96, "1 mile")]
    [InlineData(1.4, "1 mile")]
    [InlineData(2.5, "3 miles")]
    [InlineData(12.2, "12 miles")]
    public void Entfernung_im_Callout(double distanceNm, string expected)
    {
        Assert.Equal(expected, TrafficMath.DistanceText(distanceNm));
    }

    [Theory]
    [InlineData(1049, "1000 feet above")]
    [InlineData(1050, "1100 feet above")]
    [InlineData(-480, "500 feet below")]
    [InlineData(40, "same altitude")]
    [InlineData(-49, "same altitude")]
    [InlineData(-12300, "12300 feet below")]
    public void Hoehendifferenz_im_Callout(double diffFt, string expected)
    {
        Assert.Equal(expected, TrafficMath.AltitudeText(diffFt));
    }

    [Theory]
    [InlineData(600.0, false, "climbing")]
    [InlineData(500.0, false, "climbing")]
    [InlineData(499.0, false, "level")]
    [InlineData(-600.0, false, "descending")]
    [InlineData(-1500.0, true, "level")]
    [InlineData(null, false, "level")]
    public void Vertikaltrend(double? vs, bool onGround, string expected)
    {
        Assert.Equal(expected, TrafficMath.VerticalTrend(vs, onGround));
    }

    // ------------------------------------------------------------------ Verkehrslage

    [Fact]
    public void Eigenes_Flugzeug_fehlt_und_Ziele_sind_nach_Abstand_sortiert()
    {
        var picture = Picture(
            T0,
            Target(2, 10, 12, 5000),
            Own(),
            Target(3, 200, 3.5, 5000),
            Target(4, 100, 50, 5000), // außerhalb von 40 NM
            Target(5, 300, 8, 5000));

        Assert.Equal(new uint[] { 3, 5, 2 }, picture.Targets.Select(t => t.ObjectId));
        Assert.DoesNotContain(picture.Targets, t => t.ObjectId == 1);
        Assert.Equal(4, picture.OthersReceived);
        Assert.Equal(5000, picture.Own!.AltitudeFt);
    }

    [Fact]
    public void Ohne_IS_USER_SIM_wird_das_Objekt_an_der_eigenen_Position_ausgeblendet()
    {
        var ownWithoutFlag = Own() with { IsUser = false };
        var fallback = new OwnAircraft(OwnLat, OwnLon, 5000, 90, false);

        var picture = TrafficPictureBuilder.Build(T0, [ownWithoutFlag, Target(2, 0, 4, 5500)], fallback, 40);

        Assert.Equal(2u, Assert.Single(picture.Targets).ObjectId);
        Assert.Equal(1, picture.OthersReceived);
    }

    [Fact]
    public void Ohne_eigene_Position_gibt_es_keine_Ziele()
    {
        var picture = TrafficPictureBuilder.Build(T0, [Target(2, 0, 4, 5500)], null, 40);

        Assert.Null(picture.Own);
        Assert.Empty(picture.Targets);
    }

    [Fact]
    public void Callsign_aus_Airline_und_Flugnummer_und_Typ_ohne_Lokalisierungsschluessel()
    {
        var sample = Target(2, 0, 4, 5500, atcId: "D-AIUA", model: "TT:ATCCOM.AC_MODEL_A320.0.text") with
        {
            AtcAirline = "Lufthansa",
            AtcFlightNumber = "123",
        };

        var t = Assert.Single(Picture(T0, Own(), sample).Targets);
        Assert.Equal("Lufthansa 123", t.Callsign);
        Assert.Equal("Lufthansa 123", t.Ident);
        Assert.Equal("D-AIUA", t.AtcId);
        Assert.Equal("A320", t.Type);
    }

    [Theory]
    [InlineData("TT:ATCCOM.AC_MODEL_A320.0.text", "A320")]
    [InlineData("TT:ATCCOM.AC_MODEL A321.0.text", "A321")]
    [InlineData("ATCCOM.AC_MODEL_B738.0.text", "B738")]
    [InlineData("A20N", "A20N")]
    [InlineData("$$:C172", "C172")]
    [InlineData("ATCCOM.AC MODEL C441.0.tts", "C441")]
    [InlineData("ATCCOM.ATC NAME CITATION.0.tts", "CITATION")]
    [InlineData("ATCCOM.ATC_NAME_BOEING_737.0.text", "BOEING 737")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ATC_MODEL_wird_lesbar(string? raw, string? expected)
    {
        Assert.Equal(expected, TrafficMath.CleanAtcText(raw));
    }

    [Fact]
    public void VS_aus_der_Hoehenaenderung_wenn_die_SimVar_null_liefert()
    {
        var tracker = new VerticalSpeedTracker();
        Assert.Empty(tracker.Update([Own(), Target(2, 0, 4, 5000)], T0));

        // 100 ft in 6 s = 1000 ft/min; eine zweite Abfrage nach 0,5 s verschiebt den Bezugswert nicht
        Assert.Empty(tracker.Update([Target(2, 0, 4, 5010)], T0.AddSeconds(0.5)));
        var later = Target(2, 0, 4, 5100);
        var derived = tracker.Update([Own(), later], T0.AddSeconds(6));
        Assert.Equal(1000, derived[2], 3);

        var picture = TrafficPictureBuilder.Build(T0.AddSeconds(6), [Own(), later], null, 40, derived);
        var t = Assert.Single(picture.Targets);
        Assert.True(t.VerticalSpeedDerived);
        Assert.Equal("climbing", t.VerticalTrend);
    }

    [Fact]
    public void Gemeldete_VS_hat_Vorrang_vor_der_abgeleiteten()
    {
        var derived = new Dictionary<uint, double> { [2] = 2000 };
        var picture = TrafficPictureBuilder.Build(T0, [Own(), Target(2, 0, 4, 5000, verticalSpeedFpm: -900)], null, 40, derived);

        var t = Assert.Single(picture.Targets);
        Assert.False(t.VerticalSpeedDerived);
        Assert.Equal("descending", t.VerticalTrend);
    }

    [Fact]
    public void Radius_wird_auf_200_km_begrenzt()
    {
        Assert.Equal(200_000 / 1852.0, TrafficService.ClampRadius(500), 6);
        Assert.Equal(40, TrafficService.ClampRadius(40), 6);
        Assert.Equal(0.1, TrafficService.ClampRadius(0), 6);
        Assert.Equal(TrafficService.DefaultRadiusNm, TrafficService.ClampRadius(double.NaN), 6);
    }

    // ------------------------------------------------------------------ Monitoring-Regel TRAFFIC_PROXIMITY

    private static FlightSnapshot Cruise(double sec, params AircraftSample[] traffic) => new()
    {
        TimeUtc = T0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
        DataAgeSec = 0.1,
        AircraftKey = "test",
        OnGround = false,
        IasKt = 250,
        GroundSpeedKt = 300,
        VerticalSpeedFpm = 0,
        AltitudeFt = 5000,
        PressureAltitudeFt = 5000,
        AglFt = 4000,
        BankRightDeg = 0,
        FlapsIndex = 0,
        GearExtendedPct = 0,
        GearHandleDown = false,
        ApEngaged = true,
        SelectedAltitudeFt = 5000,
        Limits = new AircraftLimits { VmoKt = 350, VleKt = 280 },
        Traffic = traffic.Length == 0 ? null : Picture(T0.AddSeconds(sec), [Own(), .. traffic]),
    };

    private static List<AlertEvent> Feed(MonitorEngine engine, params FlightSnapshot[] snapshots) =>
        snapshots.SelectMany(engine.Evaluate).Where(e => e.Rule == Rules.TrafficProximity).ToList();

    private static MonitorEngine WarmedUp(MonitorSettings? settings = null)
    {
        var engine = new MonitorEngine(settings);
        Assert.Empty(Feed(engine, Cruise(0), Cruise(11)));
        return engine;
    }

    [Fact]
    public void Verkehr_naeher_als_3_NM_und_1000_ft_wird_gemeldet()
    {
        var engine = WarmedUp();
        var conflict = Target(2, 150, 2.1, 5500, verticalSpeedFpm: 700);

        Assert.Empty(Feed(engine, Cruise(12, conflict)));
        var e = Assert.Single(Feed(engine, Cruise(13.2, conflict)));
        Assert.Equal(AlertKind.Raise, e.Kind);
        Assert.Equal(AlertSeverity.Caution, e.Severity);
        Assert.Equal("Traffic, 2 o'clock, 2 miles, 500 feet above, climbing", e.Callout);
        Assert.Contains("2 Uhr", e.Message);

        // Bleibt aktiv, keine zweite Meldung
        Assert.Empty(Feed(engine, Cruise(14, conflict), Cruise(16, conflict)));
    }

    [Theory]
    [InlineData(3.5, 5500, false)] // zu weit weg
    [InlineData(2.0, 6200, false)] // mehr als 1000 ft darüber
    [InlineData(2.0, 3900, false)] // mehr als 1000 ft darunter
    [InlineData(2.0, 5000, true)] // am Boden
    public void Kein_Alarm_ausserhalb_der_Grenzen(double distanceNm, double altitudeFt, bool onGround)
    {
        var engine = WarmedUp();
        var target = Target(2, 0, distanceNm, altitudeFt, onGround: onGround);

        Assert.Empty(Feed(engine, Cruise(12, target), Cruise(14, target), Cruise(16, target)));
    }

    [Fact]
    public void Kein_Alarm_unter_500_ft_ueber_Grund_und_mit_veralteter_Verkehrslage()
    {
        var conflict = Target(2, 0, 1.0, 5100);

        var low = WarmedUp();
        Assert.Empty(Feed(low, Cruise(12, conflict) with { RadioAltFt = 400 }, Cruise(14, conflict) with { RadioAltFt = 400 }));

        var stale = WarmedUp();
        var old = Cruise(1, conflict).Traffic;
        Assert.Empty(Feed(stale, Cruise(12) with { Traffic = old }, Cruise(14) with { Traffic = old }));
    }

    [Fact]
    public void Regel_ist_abschaltbar()
    {
        var engine = WarmedUp(new MonitorSettings { DisabledRules = new HashSet<string> { Rules.TrafficProximity } });
        var conflict = Target(2, 0, 1.0, 5100);

        Assert.Empty(Feed(engine, Cruise(12, conflict), Cruise(14, conflict), Cruise(16, conflict)));
        Assert.DoesNotContain(Rules.TrafficProximity, engine.EnabledRules);
    }

    [Fact]
    public void Nach_dem_Ende_gilt_der_Cooldown()
    {
        var engine = WarmedUp(new MonitorSettings { TrafficCooldownSec = 60 });
        var conflict = Target(2, 0, 1.0, 5100);

        Feed(engine, Cruise(12, conflict));
        Assert.Single(Feed(engine, Cruise(13.2, conflict)));

        // Ziel weg → nach 6 s Clear
        Assert.Empty(Feed(engine, Cruise(14)));
        var clear = Assert.Single(Feed(engine, Cruise(20.1)));
        Assert.Equal(AlertKind.Clear, clear.Kind);

        // Neue Annäherung innerhalb von 60 s: still
        Assert.Empty(Feed(engine, Cruise(30, conflict), Cruise(32, conflict), Cruise(60, conflict)));

        // Nach dem Cooldown wieder gemeldet
        var again = Assert.Single(Feed(engine, Cruise(81, conflict)));
        Assert.Equal(AlertKind.Raise, again.Kind);
    }

    // ------------------------------------------------------------------ Höhendifferenz gegen die eigene Druckhöhe (Testlog 0.9)

    [Fact]
    public void Testflug_Labrador_FL330_gegen_FL320_ist_1000_ft_darueber()
    {
        // Emirates 222 auf barometrisch 33.000 ft (Flightradar), eigenes FL320 bei −47 °C mit 30.630 ft wahrer Höhe.
        // Kopilot 0.9 meldete 2.400 ft darüber, richtig ist FL330 gegen FL320.
        var own = Own(altitudeFt: 30_630, headingTrueDeg: 270);
        var emirates = Target(2, 330, 14, 33_000);

        var picture = TrafficPictureBuilder.Build(T0, [own, emirates], null, 40, ownPressureAltitudeFt: 32_000);

        var t = Assert.Single(picture.Targets);
        Assert.Equal(1000, t.AltitudeDiffFt, 6);
        Assert.Equal(33_000, t.AltitudeFt, 6);
        Assert.Equal("Traffic, 2 o'clock, 14 miles, 1000 feet above", t.Callout);
        Assert.Equal("pressure", picture.Own!.AltitudeReference);
        Assert.Equal(32_000, picture.Own.ReferenceAltitudeFt, 6);
        Assert.Equal(30_630, picture.Own.AltitudeFt, 6);
    }

    [Fact]
    public void Ohne_Druckhoehe_bleibt_es_bei_der_wahren_Hoehe()
    {
        // Das alte Verhalten: genau die 2.400 ft aus dem Testlog
        var picture = TrafficPictureBuilder.Build(T0, [Own(altitudeFt: 30_630, headingTrueDeg: 270), Target(2, 330, 14, 33_000)], null, 40);

        var t = Assert.Single(picture.Targets);
        Assert.Equal(2370, t.AltitudeDiffFt, 6);
        Assert.Equal("Traffic, 2 o'clock, 14 miles, 2400 feet above", t.Callout);
        Assert.Equal("true", picture.Own!.AltitudeReference);
    }

    [Theory]
    // Porter 2355 (DHC8) FL240, eigenes FL340: 0.9 meldete 9.400 ft darunter
    [InlineData(33_400, 34_000, 24_000, "10000 feet below")]
    // Air Canada 148 (A220) mit 30.997 ft, eigenes FL340: 0.9 meldete 2.400 ft darunter
    [InlineData(33_397, 34_000, 30_997, "3000 feet below")]
    // Alaska 1427 (B737) FL340, eigenes FL360: 0.9 meldete 2.279 ft
    [InlineData(36_279, 36_000, 34_000, "2000 feet below")]
    // Gleiche Flugfläche bei warmer Luft (wahre Höhe über der Druckhöhe)
    [InlineData(37_150, 36_000, 36_000, "same altitude")]
    public void Weitere_Ziele_aus_dem_Testflug(double ownTrueFt, double ownPressureFt, double targetFt, string expected)
    {
        var picture = TrafficPictureBuilder.Build(T0, [Own(altitudeFt: ownTrueFt), Target(2, 0, 10, targetFt)], null, 40, ownPressureAltitudeFt: ownPressureFt);

        var t = Assert.Single(picture.Targets);
        Assert.Equal(targetFt - ownPressureFt, t.AltitudeDiffFt, 6);
        Assert.Equal(expected, TrafficMath.AltitudeText(t.AltitudeDiffFt));
    }

    [Theory]
    [InlineData(32_000.0, 32_000.0, "pressure")]
    [InlineData(null, 30_630.0, "true")]
    [InlineData(double.NaN, 30_630.0, "true")]
    [InlineData(double.PositiveInfinity, 30_630.0, "true")]
    public void Bezugshoehe_des_eigenen_Flugzeugs(double? pressureAltitudeFt, double expectedFt, string expectedReference)
    {
        var own = new OwnAircraft(OwnLat, OwnLon, 30_630, 270, false, pressureAltitudeFt);

        Assert.Equal(expectedFt, own.ReferenceAltitudeFt, 6);
        Assert.Equal(expectedReference, own.AltitudeReference);
        Assert.Equal(1000, TrafficMath.AltitudeDifference(expectedFt + 1000, own), 6);
    }

    [Fact]
    public void Druckhoehe_gilt_auch_fuer_die_Ersatzposition()
    {
        // Eigenes Flugzeug fehlt in der Abfrage – die Werte aus den abonnierten SimVars bringen die Druckhöhe mit
        var fallback = new OwnAircraft(OwnLat, OwnLon, 30_630, 270, false, PressureAltitudeFt: 32_000);

        var picture = TrafficPictureBuilder.Build(T0, [Target(2, 330, 14, 33_000)], fallback, 40);
        Assert.Equal(1000, Assert.Single(picture.Targets).AltitudeDiffFt, 6);

        // Ein ausdrücklich übergebener Wert hat Vorrang
        var explicitPa = TrafficPictureBuilder.Build(T0, [Target(2, 330, 14, 33_000)], fallback, 40, ownPressureAltitudeFt: 32_500);
        Assert.Equal(500, Assert.Single(explicitPa.Targets).AltitudeDiffFt, 6);
    }

    [Fact]
    public void Eigenes_Objekt_wird_weiter_ueber_die_wahre_Hoehe_erkannt()
    {
        // Ohne IS USER SIM: Objekt an der eigenen Position mit der eigenen wahren Höhe – auch wenn die Druckhöhe 1.400 ft höher ist
        var ownWithoutFlag = Own(altitudeFt: 30_630) with { IsUser = false };
        var fallback = new OwnAircraft(OwnLat, OwnLon, 30_630, 90, false, PressureAltitudeFt: 32_000);

        var picture = TrafficPictureBuilder.Build(T0, [ownWithoutFlag, Target(2, 0, 4, 33_000)], fallback, 40);

        Assert.Equal(2u, Assert.Single(picture.Targets).ObjectId);
    }

    // ------------------------------------------------------------------ Groundspeed aus der Positionsänderung (Testlog 0.9)

    // 12 NM nördlich des eigenen Flugzeugs, von dort nach Osten
    private const double TrackStartLat = OwnLat + 0.2;
    private const double TrackStartLon = OwnLon;

    /// <summary>Ziel auf Kurs 090, das mit <paramref name="groundSpeedKt"/> fliegt; GROUND VELOCITY liefert <paramref name="simVarKt"/>.</summary>
    private static AircraftSample Moving(uint id, double seconds, double groundSpeedKt, double simVarKt, double startOffsetNm = 0)
    {
        var (lat, lon) = RouteMath.Destination(TrackStartLat, TrackStartLon, 90, startOffsetNm + groundSpeedKt * seconds / 3600);
        return Target(id, 0, 1, 33_000) with { Latitude = lat, Longitude = lon, GroundSpeedKt = simVarKt, HeadingTrueDeg = 90 };
    }

    [Fact]
    public void Groundspeed_aus_der_Positionsaenderung_Emirates_222()
    {
        // Flightradar 556 kt, die SimVar lieferte 169 kt. Hintergrundabfrage alle 3 s.
        var tracker = new GroundSpeedTracker();
        Assert.Empty(tracker.Update([Moving(2, 0, 556, 169)], T0));
        Assert.Empty(tracker.Update([Moving(2, 3, 556, 169)], T0.AddSeconds(3)));

        // Ab 5 s Abstand gibt es einen Wert
        var early = tracker.Update([Moving(2, 6, 556, 169)], T0.AddSeconds(6));
        Assert.Equal(556, early[2].Knots, 0);
        Assert.Equal(6, early[2].BaselineSec, 6);

        IReadOnlyDictionary<uint, DerivedGroundSpeed> derived = early;
        for (var sec = 9; sec <= 21; sec += 3)
        {
            derived = tracker.Update([Moving(2, sec, 556, 169)], T0.AddSeconds(sec));
        }

        // Bezug ist die Position, die am nächsten an 20 s zurückliegt
        Assert.Equal(556, derived[2].Knots, 0);
        Assert.Equal(21, derived[2].BaselineSec, 6);

        var now = T0.AddSeconds(21);
        var picture = TrafficPictureBuilder.Build(now, [Own(), Moving(2, 21, 556, 169)], null, 40, derivedGroundSpeed: derived);
        var t = Assert.Single(picture.Targets);
        Assert.Equal(556, t.GroundSpeedKt, 0);
        Assert.Equal(TrafficTarget.GroundSpeedFromPosition, t.GroundSpeedSource);
        Assert.Equal(169, t.GroundSpeedSimVarKt, 6);
        Assert.Equal(21, t.GroundSpeedBaselineSec!.Value, 6);
    }

    [Fact]
    public void Ohne_fruehere_Position_gilt_die_SimVar_mit_Quelle()
    {
        var picture = TrafficPictureBuilder.Build(T0, [Own(), Moving(2, 0, 464, 146)], null, 40);

        var t = Assert.Single(picture.Targets);
        Assert.Equal(146, t.GroundSpeedKt, 6);
        Assert.Equal(TrafficTarget.GroundSpeedFromSimVar, t.GroundSpeedSource);
        Assert.Null(t.GroundSpeedBaselineSec);
    }

    [Fact]
    public void Zwei_get_traffic_Aufrufe_ohne_Hintergrundabfrage_reichen()
    {
        // Verkehrswarnung aus (Workaround gegen den Absturz): nur get_traffic, 90 s auseinander
        var tracker = new GroundSpeedTracker();
        tracker.Update([Own(), Moving(2, 0, 410, 125)], T0);

        var derived = tracker.Update([Own(), Moving(2, 90, 410, 125)], T0.AddSeconds(90));
        Assert.Equal(410, derived[2].Knots, 0);
        Assert.Equal(90, derived[2].BaselineSec, 6);
        Assert.DoesNotContain(1u, derived.Keys);

        // Mehr als 120 s: zu alt, kein Wert
        Assert.Empty(tracker.Update([Moving(2, 300, 410, 125)], T0.AddSeconds(300)));
    }

    [Fact]
    public void Positionssprung_wird_verworfen()
    {
        var tracker = new GroundSpeedTracker();
        tracker.Update([Moving(2, 0, 450, 137)], T0);

        // 40 NM in 10 s (Sim setzt das Ziel um oder vergibt die ID neu) → kein Wert, alte Positionen verfallen
        Assert.Empty(tracker.Update([Moving(2, 10, 450, 137, startOffsetNm: 40)], T0.AddSeconds(10)));

        // Danach zählt nur die neue Bahn
        var derived = tracker.Update([Moving(2, 20, 450, 137, startOffsetNm: 40)], T0.AddSeconds(20));
        Assert.Equal(450, derived[2].Knots, 0);
        Assert.Equal(10, derived[2].BaselineSec, 6);
    }

    [Fact]
    public void Ziel_am_Boden_hat_Groundspeed_null()
    {
        var tracker = new GroundSpeedTracker();
        var parked = Target(3, 200, 1.5, 120, onGround: true);
        tracker.Update([parked], T0);

        var derived = tracker.Update([parked], T0.AddSeconds(15));
        Assert.Equal(0, derived[3].Knots, 6);
    }

    // ------------------------------------------------------------------ TRAFFIC_PROXIMITY mit Druckhöhe

    private static FlightSnapshot CruiseFl320(double sec, AircraftSample target, double? ownPressureAltitudeFt) => Cruise(sec) with
    {
        Traffic = TrafficPictureBuilder.Build(T0.AddSeconds(sec), [Own(altitudeFt: 30_630), target], null, 40, ownPressureAltitudeFt: ownPressureAltitudeFt),
    };

    [Fact]
    public void Verkehrswarnung_rechnet_mit_der_Druckhoehe()
    {
        // Ziel auf FL328, eigenes FL320 mit 30.630 ft wahrer Höhe: 800 ft darüber (gegen die wahre Höhe wären es 2.170).
        // Eigener Kurs 090, Ziel genau im Norden → 9 Uhr
        var target = Target(2, 0, 2.0, 32_800);

        var engine = WarmedUp();
        Assert.Empty(Feed(engine, CruiseFl320(12, target, 32_000)));
        var e = Assert.Single(Feed(engine, CruiseFl320(13.2, target, 32_000)));
        Assert.Equal("Traffic, 9 o'clock, 2 miles, 800 feet above", e.Callout);
        Assert.Contains("800 ft darüber", e.Message);

        var withTrueAltitude = WarmedUp();
        Assert.Empty(Feed(withTrueAltitude, CruiseFl320(12, target, null), CruiseFl320(13.2, target, null), CruiseFl320(16, target, null)));
    }

    // ------------------------------------------------------------------ Rufzeichen (Testlog 0.9, 01:22Z)

    [Theory]
    [InlineData("Lufthansa", "123", "D-AIUA", "Lufthansa 123")]
    [InlineData(null, "1432126", "N605SD", "N605SD")]
    [InlineData("", "1520435", "N441E", "N441E")]
    [InlineData("Lufthansa", null, "D-AIUA", "D-AIUA")]
    [InlineData("Lufthansa", " ", "D-AIUA", "D-AIUA")]
    [InlineData("TT:ATCCOM.AIRLINE_CONDOR.0.text", "2205", "D-AIKA", "CONDOR 2205")]
    [InlineData(null, "1432126", null, null)]
    [InlineData(null, null, null, null)]
    public void Callsign_nur_aus_Airline_und_Flugnummer_sonst_Registrierung(string? airline, string? flightNumber, string? atcId, string? expected)
    {
        Assert.Equal(expected, TrafficMath.Callsign(airline, flightNumber, atcId));
    }

    [Fact]
    public void Live_Traffic_GA_mit_Registrierung_und_bereinigtem_Typ()
    {
        // C172 N605SD und C441 N441E: Flugnummer ohne Airline, Typ als Rohstring
        var c172 = Target(2, 0, 4, 9500, atcId: "N605SD", model: "ATCCOM.AC MODEL C172.0.tts") with { AtcFlightNumber = "1432126" };
        var c441 = Target(3, 90, 6, 17_500, atcId: "N441E", model: "ATCCOM.AC MODEL C441.0.tts") with { AtcFlightNumber = "1520435" };
        var citation = Target(4, 180, 8, 45_000, atcId: "N482FX", model: "ATCCOM.ATC NAME CITATION.0.tts") with
        {
            AtcAirline = "Flexjet",
            AtcFlightNumber = "482",
        };

        var targets = Picture(T0, Own(), c172, c441, citation).Targets;

        Assert.Equal(new string?[] { "N605SD", "N441E", "Flexjet 482" }, targets.Select(t => t.Ident));
        Assert.Equal(new string?[] { "N605SD", "N441E", "Flexjet 482" }, targets.Select(t => t.Callsign));
        Assert.Equal(new string?[] { "C172", "C441", "CITATION" }, targets.Select(t => t.Type));
        Assert.Equal("N482FX", targets[2].AtcId);
    }

    // ------------------------------------------------------------------ Texte für den Hintergrundscan

    [Fact]
    public void Hintergrundscan_uebernimmt_Callsign_und_Typ_aus_get_traffic()
    {
        var cache = new TrafficIdentCache();
        var withText = Target(2, 0, 4, 5500, atcId: "D-AIUA", model: "A320") with { AtcAirline = "Lufthansa", AtcFlightNumber = "123" };
        cache.Remember([Own(), withText], T0);

        // Hintergrundscan: nur Zahlen
        var numeric = withText with { AtcId = null, AtcAirline = null, AtcFlightNumber = null, AtcModel = null };
        var unknown = Target(3, 90, 2, 5200, atcId: null) with { AtcId = null };
        var filled = cache.Fill([Own() with { AtcId = null }, numeric, unknown], T0.AddMinutes(5));

        Assert.Null(filled[0].AtcId);
        Assert.Equal("Lufthansa", filled[1].AtcAirline);
        Assert.Equal("123", filled[1].AtcFlightNumber);
        Assert.Equal("A320", filled[1].AtcModel);
        Assert.Equal(numeric.Latitude, filled[1].Latitude);
        Assert.Null(filled[2].AtcId);

        // Solange das Ziel gesehen wird, bleibt der Eintrag; 15 min ohne Sichtung verfällt er
        Assert.Equal("D-AIUA", cache.Fill([numeric], T0.AddMinutes(19))[0].AtcId);
        Assert.Null(cache.Fill([numeric], T0.AddMinutes(35))[0].AtcId);
    }

    [Fact]
    public void Verkehrswarnung_ohne_Callsign_nennt_nur_Position()
    {
        var engine = WarmedUp();
        var conflict = Target(2, 150, 2.1, 5500) with { AtcId = null };

        Feed(engine, Cruise(12, conflict));
        var e = Assert.Single(Feed(engine, Cruise(13.2, conflict)));
        Assert.StartsWith("Verkehr auf 2 Uhr, 2.1 NM, 500 ft darüber", e.Message);
    }
}
