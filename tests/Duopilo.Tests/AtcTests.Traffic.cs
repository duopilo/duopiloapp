using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>
/// Verkehr im Duopilo-ATC: Traffic-Info von sich aus (Departure, Center, Approach), Rückfragen des Piloten ("request traffic
/// information", "confirm traffic 12 o'clock") ohne Weiterschalten des Ablaufs und die Reihenfolge im Endanflug beim Tower.
/// </summary>
public sealed partial class AtcTests
{
    private const double CruiseHeadingTrue = 50;

    /// <summary>Reiseflug bei Langen Radar nach dem Erstanruf (CenterCruise), FL350, 450 kt.</summary>
    private static (AtcSession Session, Flight Pilot) InCenterCruise()
    {
        var (session, flight) = InCenter();
        Assert.Equal("DLH4AB, identified, maintain FL350",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero")).Text);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        return (session, flight);
    }

    /// <summary>Ziel relativ zum eigenen Flugzeug (Reiseflug, eigener Kurs <see cref="CruiseHeadingTrue"/>).</summary>
    private static AtcTrafficTarget Relative(
        uint id, double relativeBearingDeg, double distanceNm, double altitudeDiffFt, double headingTrueDeg,
        double? groundSpeedKt = 450, string? type = "A320", string trend = "level") => new()
    {
        ObjectId = id,
        Type = type,
        DistanceNm = distanceNm,
        BearingTrueDeg = TrafficMath.Normalize(CruiseHeadingTrue + relativeBearingDeg),
        RelativeBearingDeg = TrafficMath.Normalize(relativeBearingDeg),
        Clock = TrafficMath.ClockPosition(relativeBearingDeg),
        AltitudeFt = 35000 + altitudeDiffFt,
        AltitudeDiffFt = altitudeDiffFt,
        VerticalTrend = trend,
        GroundSpeedKt = groundSpeedKt,
        HeadingTrueDeg = headingTrueDeg,
    };

    private static AtcTrafficPicture Cruise(params AtcTrafficTarget[] targets) => new(CruiseHeadingTrue, targets);

    // ------------------------------------------------------------------ Rechnungen und Sprechweise

    [Theory]
    [InlineData(50.0, 50.0, "same direction")]
    [InlineData(50.0, 75.0, "same direction")]
    [InlineData(50.0, 230.0, "opposite direction")]
    [InlineData(350.0, 160.0, "opposite direction")]
    [InlineData(50.0, 140.0, "crossing left to right")]
    [InlineData(50.0, 320.0, "crossing right to left")]
    [InlineData(10.0, 280.0, "crossing right to left")]
    public void Richtung_des_Verkehrs(double own, double target, string expected) =>
        Assert.Equal(expected, AtcTrafficMath.Direction(own, target));

    [Fact]
    public void Annaeherung_Gegenverkehr_und_sich_entfernender_Verkehr()
    {
        var picture = Cruise();

        // Gegenverkehr 12 nm voraus, leicht versetzt: in rund 47 s auf gut 2 nm
        var head = AtcTrafficMath.ClosestApproach(picture, Relative(1, 10, 12, 1000, 230), 450);
        Assert.NotNull(head);
        Assert.True(head.Value.Closing);
        Assert.InRange(head.Value.TimeSec, 40, 55);
        Assert.InRange(head.Value.MissNm, 1.8, 2.4);

        // Langsamer in gleicher Richtung hinter uns: entfernt sich
        var behind = AtcTrafficMath.ClosestApproach(picture, Relative(2, 180, 5, 0, 50, groundSpeedKt: 300), 450);
        Assert.False(behind!.Value.Closing);

        // Ohne verlässliche Groundspeed keine Rechnung
        Assert.Null(AtcTrafficMath.ClosestApproach(picture, Relative(3, 60, 8, 0, 230, groundSpeedKt: null), 450));
    }

    [Theory]
    [InlineData("A320", "Airbus three twenty")]
    [InlineData("A321", "Airbus three twenty one")]
    [InlineData("A359", "Airbus three fifty nine")]
    [InlineData("A20N", "Airbus three twenty neo")]
    [InlineData("B738", "Boeing seven thirty eight")]
    [InlineData("B744", "Boeing seven forty four")]
    [InlineData("C172", "charlie one seven two")]
    [InlineData("CITATION", "citation")]
    public void Flugzeugtyp_wie_im_Funk(string type, string spoken) =>
        Assert.Equal(spoken, AtcTrafficMath.TypeSpoken(type), ignoreCase: true);

    [Theory]
    [InlineData(-1000.0, "1000 feet below", "one thousand feet below")]
    [InlineData(1540.0, "1500 feet above", "one thousand five hundred feet above")]
    [InlineData(30.0, "same level", "same level")]
    public void Hoehenunterschied(double diff, string written, string spoken)
    {
        var value = AtcTrafficMath.LevelValue(diff);
        Assert.Equal(written, value.Written);
        Assert.Equal(spoken, value.Spoken);
    }

    [Theory]
    [InlineData("Langen Radar, Lufthansa four alpha bravo, request traffic information", true, null, 0)]
    [InlineData("Lufthansa four alpha bravo, any traffic for us?", true, null, 0)]
    [InlineData("confirm traffic twelve o'clock, Lufthansa four alpha bravo", true, 12, 0)]
    [InlineData("we have traffic at 2 o'clock on TCAS", true, 2, 0)]
    [InlineData("traffic one o'clock above us?", true, 1, 1)]
    [InlineData("request traffic below", true, null, -1)]
    [InlineData("any traffic ahead?", true, 12, 0)]
    [InlineData("traffic in sight, Lufthansa four alpha bravo", false, null, 0)]
    [InlineData("negative contact, looking out", false, null, 0)]
    [InlineData("unable due traffic, maintain flight level three five zero", false, null, 0)]
    [InlineData("request climb flight level three seven zero", false, null, 0)]
    public void Rueckfrage_nach_Verkehr_wird_erkannt(string text, bool query, int? clock, int vertical)
    {
        var parsed = AtcTrafficQuery.Parse(HeardText.Parse(text));
        Assert.Equal(query, parsed is not null);
        if (parsed is not null)
        {
            Assert.Equal(clock, parsed.Clock);
            Assert.Equal(vertical, parsed.Vertical);
        }
    }

    [Fact]
    public void Verkehrslage_aus_der_Abfrage_mit_Position_und_ohne_unzuverlaessige_Groundspeed()
    {
        var own = new AircraftSample(1, "aircraft", "D-AIAB", "DLH", "4AB", "A320", 50, 8, 35000, 90, 88, 450, 0, false, true);
        var (lat, lon) = RouteMath.Destination(50, 8, 30, 6);
        var other = new AircraftSample(7, "aircraft", "D-ABCD", "BOX", "123", "B77L", lat, lon, 36000, 270, 268, 150, -1200, false, false);
        var picture = TrafficPictureBuilder.Build(T0, [own, other], null, 25);

        var converted = AtcTrafficPicture.From(picture);

        Assert.NotNull(converted);
        Assert.InRange(converted.OwnHeadingTrueDeg, 89.999, 90.001);
        var target = Assert.Single(converted.Targets);
        Assert.Equal(7u, target.ObjectId);
        Assert.Equal("B77L", target.Type);
        Assert.InRange(target.Lat, lat - 1e-4, lat + 1e-4);
        Assert.InRange(target.Lon, lon - 1e-4, lon + 1e-4);
        Assert.Equal(10, target.Clock); // 30° bei Kurs 90° = 300° relativ
        Assert.Equal("descending", target.VerticalTrend);
        Assert.Null(target.GroundSpeedKt); // nur GROUND VELOCITY – bei Live-Traffic viel zu klein
    }

    [Fact]
    public void Traffic_Info_mit_und_ohne_Typ_und_Trend()
    {
        var (session, flight) = InCenterCruise();
        flight.State = flight.State with { Traffic = Cruise(Relative(1, 60, 8, -2000, 140, type: null, trend: "descending")) };

        var reply = Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, request traffic information"));
        Assert.Equal("DLH4AB, traffic 2 o'clock, 8 miles, crossing left to right, 2000 feet below, descending", reply.Text);

        flight.State = flight.State with { Traffic = Cruise(Relative(2, 300, 0.6, 400, 50, type: "B738")) };
        reply = Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, request traffic information"));
        Assert.Equal("DLH4AB, traffic 10 o'clock, less than 1 mile, same direction, B738, 400 feet above", reply.Text);
        Assert.Equal(
            "Lufthansa four alpha bravo, traffic ten o'clock, less than one mile, same direction, Boeing seven thirty eight, four hundred feet above",
            reply.Spoken);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
    }

    // ------------------------------------------------------------------ Traffic-Info von sich aus

    [Fact]
    public void Center_meldet_Gegenverkehr_von_sich_aus_einmal()
    {
        var (session, flight) = InCenterCruise();
        flight.State = flight.State with { Traffic = Cruise(Relative(1, 10, 12, 1000, 230)) };

        var info = Assert.Single(flight.Run(20));
        Assert.Equal(AtcMessageIds.CommonTrafficInfo, info.MessageId);
        Assert.Equal("DLH4AB, traffic 12 o'clock, 12 miles, opposite direction, A320, 1000 feet above", info.Text);
        Assert.Equal(
            "Lufthansa four alpha bravo, traffic twelve o'clock, twelve miles, opposite direction, Airbus three twenty, one thousand feet above",
            info.Spoken);
        Assert.Equal("Langen Radar", info.StationName);

        // Quittung ohne Antwort, dasselbe Flugzeug nicht noch einmal
        Assert.Empty(flight.Say("looking out, Lufthansa four alpha bravo"));
        Assert.Empty(flight.Run(90));
        Assert.Equal(AtcStep.CenterCruise, session.Step);
    }

    [Fact]
    public void Kein_Traffic_Info_fuer_Verkehr_der_nicht_herankommt()
    {
        var (_, flight) = InCenterCruise();
        flight.State = flight.State with
        {
            Traffic = Cruise(
                Relative(1, 180, 5, 0, 50, groundSpeedKt: 300),      // hinter uns, langsamer – entfernt sich
                Relative(2, 0, 10, 3000, 230),                      // Gegenverkehr, aber 3000 ft darüber
                Relative(3, 60, 8, 0, 230, groundSpeedKt: null),    // Groundspeed noch unbekannt
                Relative(4, 90, 14, 0, 50),                         // parallel in gleicher Richtung, 14 nm daneben
                Relative(5, 30, 40, 0, 230)),                       // zu weit weg
        };

        Assert.Empty(flight.Run(30));
    }

    [Fact]
    public void Traffic_Info_wartet_auf_Funkstille_und_kommt_nicht_beim_Erstanruf()
    {
        var (session, flight) = InCenter();
        flight.State = flight.State with { Traffic = Cruise(Relative(1, 10, 12, 1000, 230)) };

        // Noch nicht gemeldet: keine Traffic-Info
        Assert.Empty(flight.Run(30));
        Assert.Equal(AtcStep.CenterContact, session.Step);

        // Nach dem Erstanruf erst nach einer Pause
        flight.Say("Langen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero");
        Assert.Empty(flight.Run(10));
        Assert.Single(flight.Run(10), t => t.MessageId == AtcMessageIds.CommonTrafficInfo);
    }

    [Fact]
    public void Traffic_Info_laesst_sich_abschalten()
    {
        var templates = Templates();
        templates.Rules.TrafficInfo = false;
        var session = new AtcSession(templates, AtcPlan.Build(ShortHaul(), null, templates), new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));
        var flight = new Flight(session);
        var (lat, lon) = Along(0.35);
        flight.State = flight.State with
        {
            OnGround = false, Lat = lat, Lon = lon, AltitudeFt = 35000, AglFt = 34500, GroundSpeedKt = 450, IasKt = 280, ParkingBrake = false,
        };
        flight.Start();
        flight.Say("Langen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero");
        flight.State = flight.State with { Traffic = Cruise(Relative(1, 10, 12, 1000, 230)) };

        Assert.Empty(flight.Run(30));
    }

    // ------------------------------------------------------------------ Rückfragen

    [Fact]
    public void Rueckfrage_mit_Uhrzeit_und_unten_nennt_das_passende_Flugzeug()
    {
        var (session, flight) = InCenterCruise();
        flight.State = flight.State with
        {
            Traffic = Cruise(Relative(1, 0, 10, 1000, 230), Relative(2, 90, 4, -500, 50, type: "C172")),
        };

        Assert.Equal("DLH4AB, traffic 12 o'clock, 10 miles, opposite direction, A320, 1000 feet above",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, confirm traffic twelve o'clock")).Text);
        Assert.Equal("DLH4AB, traffic 3 o'clock, 4 miles, same direction, C172, 500 feet below",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, request traffic below")).Text);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
    }

    [Fact]
    public void Rueckfrage_ohne_passenden_Verkehr_oder_ohne_Lage()
    {
        var (_, flight) = InCenterCruise();
        Assert.Equal("DLH4AB, no reported traffic", Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, request traffic information")).Text);

        flight.State = flight.State with { Traffic = Cruise(Relative(1, 0, 30, 0, 230), Relative(2, 90, 5, 6000, 50)) };
        Assert.Equal("DLH4AB, no reported traffic", Assert.Single(flight.Say("Lufthansa four alpha bravo, any traffic for us?")).Text);
        Assert.Empty(flight.Say("roger, Lufthansa four alpha bravo"));
    }

    [Fact]
    public void Rueckfrage_am_Boden_schaltet_den_Ablauf_nicht_weiter()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();

        Assert.Equal("DLH4AB, no reported traffic",
            Assert.Single(flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request traffic information")).Text);
        Assert.Equal(AtcStep.ClearanceRequest, session.Step);

        // Danach normal weiter
        Assert.Equal(AtcMessageIds.DeliveryClearance,
            Assert.Single(flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin")).MessageId);
    }

    [Fact]
    public void Rueckfrage_bei_offenem_Readback_laesst_das_Readback_offen()
    {
        var (session, flight) = InCenterCruise();
        Assert.Equal("DLH4AB, climb and maintain FL370",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, request climb flight level three seven zero")).Text);
        flight.State = flight.State with { Traffic = Cruise(Relative(1, 0, 10, 2000, 230)) };

        Assert.Equal(AtcMessageIds.CommonTrafficInfo,
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, any traffic above?")).MessageId);
        Assert.NotNull(session.PendingReadback);
        Assert.Equal(AtcStep.CenterReadback, session.Step);

        Assert.Empty(flight.Say("climb and maintain flight level three seven zero, Lufthansa four alpha bravo"));
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        Assert.Equal(37000, session.ClearedAltitudeFt);
    }

    [Fact]
    public void Erstanruf_mit_Rueckfrage_wird_zuerst_normal_beantwortet()
    {
        var (session, flight) = InCenter();

        var replies = flight.Say("Langen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero, any traffic?");

        Assert.Equal(["DLH4AB, identified, maintain FL350", "DLH4AB, no reported traffic"], replies.Select(r => r.Text));
        Assert.Equal(AtcStep.CenterCruise, session.Step);
    }

    // ------------------------------------------------------------------ Reihenfolge im Endanflug

    /// <summary>7 nm vor Berlin im Endanflug, 3000 ft, beim Tower (noch nicht gerufen).</summary>
    private static (AtcSession Session, Flight Pilot, double HeadingTrue) OnFinalToBerlin()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        var (lat, lon) = Along(0.97);
        flight.State = flight.State with
        {
            OnGround = false, Lat = lat, Lon = lon, AltitudeFt = 3000, AglFt = 2850, GroundSpeedKt = 150, IasKt = 140, VerticalSpeedFpm = -700,
            ParkingBrake = false,
        };
        flight.Start();
        Assert.Equal(AtcPhase.TowerArrival, session.Phase);
        Assert.Equal("Berlin Tower", session.Station.Name);
        return (session, flight, RouteMath.BearingDeg(lat, lon, BerLat, BerLon));
    }

    /// <summary>Ziel an einer Stelle der Strecke Frankfurt–Berlin, gesehen vom eigenen Flugzeug.</summary>
    private static AtcTrafficTarget AlongRoute(
        uint id, Flight flight, double f, double headingTrueDeg, double ownHeadingTrueDeg, double altitudeDiffFt,
        string trend = "descending", bool onGround = false)
    {
        var (lat, lon) = Along(f);
        var own = flight.State;
        var distance = RouteMath.DistanceNm(own.Lat!.Value, own.Lon!.Value, lat, lon);
        var bearing = RouteMath.BearingDeg(own.Lat.Value, own.Lon.Value, lat, lon);
        var relative = TrafficMath.RelativeBearingDeg(bearing, ownHeadingTrueDeg);
        return new AtcTrafficTarget
        {
            ObjectId = id,
            Type = "A320",
            DistanceNm = distance,
            BearingTrueDeg = bearing,
            RelativeBearingDeg = relative,
            Clock = TrafficMath.ClockPosition(relative),
            AltitudeFt = 3000 + altitudeDiffFt,
            AltitudeDiffFt = altitudeDiffFt,
            VerticalTrend = trend,
            GroundSpeedKt = 140,
            HeadingTrueDeg = headingTrueDeg,
            OnGround = onGround,
            Lat = lat,
            Lon = lon,
        };
    }

    [Fact]
    public void Tower_nennt_die_Reihenfolge_und_gibt_nach_der_Landung_des_Vorausfliegenden_frei()
    {
        var (session, flight, heading) = OnFinalToBerlin();
        var ahead = AlongRoute(9, flight, 0.985, heading, heading, -1000);
        flight.State = flight.State with { Traffic = new AtcTrafficPicture(heading, [ahead]) };

        var sequence = Assert.Single(flight.Say("Berlin Tower, Lufthansa four alpha bravo, ILS runway two five right"));
        Assert.Equal(AtcMessageIds.TowerSequence, sequence.MessageId);
        Assert.Equal("DLH4AB, number 2, follow the A320 on 3 miles final", sequence.Text);
        Assert.Equal("Lufthansa four alpha bravo, number two, follow the Airbus three twenty on three miles final", sequence.Spoken);
        Assert.Equal(AtcStep.LandingSequenced, session.Step);
        Assert.Equal(9u, session.SequenceTarget);

        // Quittung ohne Antwort; solange der Vorausfliegende in der Luft ist, keine Landefreigabe
        Assert.Empty(flight.Say("number two, traffic in sight, Lufthansa four alpha bravo"));
        Assert.Empty(flight.Run(10));

        // Gelandet: Landefreigabe
        flight.State = flight.State with { Traffic = new AtcTrafficPicture(heading, [ahead with { OnGround = true, VerticalTrend = "level" }]) };
        var landing = Assert.Single(flight.Run(2));
        Assert.Equal("DLH4AB, wind 250/12, runway 25R, cleared to land", landing.Text);
        Assert.Equal(AtcStep.LandingReadback, session.Step);
    }

    [Fact]
    public void Vorausfliegender_verschwindet_aus_der_Abfrage_Landefreigabe()
    {
        var (session, flight, heading) = OnFinalToBerlin();
        flight.State = flight.State with { Traffic = new AtcTrafficPicture(heading, [AlongRoute(9, flight, 0.985, heading, heading, -1000)]) };
        Assert.Equal(AtcMessageIds.TowerSequence, Assert.Single(flight.Say("Berlin Tower, Lufthansa four alpha bravo, ILS runway two five right")).MessageId);

        flight.State = flight.State with { Traffic = new AtcTrafficPicture(heading, []) };
        Assert.Equal(AtcMessageIds.TowerLanding, Assert.Single(flight.Run(2)).MessageId);
        Assert.Equal(AtcStep.LandingReadback, session.Step);
    }

    [Fact]
    public void Abflug_und_Gegenverkehr_zaehlen_nicht_als_Vorausfliegende()
    {
        var (_, flight, heading) = OnFinalToBerlin();
        flight.State = flight.State with
        {
            Traffic = new AtcTrafficPicture(heading,
            [
                AlongRoute(1, flight, 1.01, heading, heading, -1500, trend: "climbing"),          // startet in gleicher Richtung
                AlongRoute(2, flight, 0.985, TrafficMath.Normalize(heading + 180), heading, 0),   // Gegenrichtung
                AlongRoute(3, flight, 0.95, heading, heading, 500),                               // hinter uns
            ]),
        };

        Assert.Equal("DLH4AB, wind 250/12, runway 25R, cleared to land",
            Assert.Single(flight.Say("Berlin Tower, Lufthansa four alpha bravo, ILS runway two five right")).Text);
    }

    [Fact]
    public void Ohne_Verkehrslage_gibt_der_Tower_wie_bisher_sofort_frei()
    {
        var (session, flight, _) = OnFinalToBerlin();

        Assert.Equal(AtcMessageIds.TowerLanding, Assert.Single(flight.Say("Berlin Tower, Lufthansa four alpha bravo, ILS runway two five right")).MessageId);
        Assert.Null(session.SequenceTarget);
    }
}
