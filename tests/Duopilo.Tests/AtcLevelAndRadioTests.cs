using Duopilo.Atc;
using Duopilo.FlightPlan;

namespace Duopilo.Tests;

/// <summary>
/// Duopilo-ATC nach dem Testflug CFG2205 (v0.9): Höhenanfragen im Reiseflug, Stufensteigflüge aus dem OFP, gemeldete Höhe
/// beim Erstanruf, Squawk- und COM1-Überwachung und Funksprüche an die falsche Station.
/// </summary>
public sealed class AtcLevelAndRadioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 22, 0, 0, TimeSpan.Zero);

    private const int Alpha = 2;
    private const int Bravo = 3;
    private const int Ywk = 4;

    private static OfpFix Fix(string ident, double lat, double lon, string fir, string? stage, int? altitude, string type = "wpt") => new()
    {
        Ident = ident,
        Lat = lat,
        Lon = lon,
        Type = type,
        Fir = fir,
        Stage = stage,
        AltitudeFt = altitude,
    };

    /// <summary>Wie über Labrador im Testflug: Gander Center → Montreal Center, Stufe auf FL340 bei YWK.</summary>
    private static Ofp Labrador() => new()
    {
        RequestId = "cfg2205-labrador",
        Callsign = "DE2205",
        FlightNumber = "CFG2205",
        Origin = new OfpAirport("CYYR", "Goose Bay", "26", 53.3192, -60.4258, 160, 18000, 18000, null),
        Destination = new OfpAirport("CYUL", "Montreal Trudeau", "24R", 45.4706, -73.7408, 118, 18000, 18000, null),
        InitialAltitudeFt = 32000,
        Fixes =
        [
            Fix("DEPAR", 53.10, -61.20, "CZQX", "CLB", 12000),
            Fix("TOC", 52.80, -62.20, "CZQX", "CLB", 32000, type: "ltlg"),
            Fix("ALPHA", 52.00, -64.50, "CZQX", "CRZ", 32000),
            Fix("BRAVO", 51.20, -66.50, "CZUL", "CRZ", 32000),
            Fix("YWK", 50.00, -69.00, "CZUL", "CRZ", 34000),
            Fix("DELTA", 48.50, -71.00, "CZUL", "CRZ", 34000),
            Fix("TOD", 47.80, -71.80, "CZUL", "DSC", 34000, type: "ltlg"),
            Fix("CYUL", 45.4706, -73.7408, "CZUL", "DSC", 118, type: "apt"),
        ],
    };

    private static AtcSession Session(AtcTemplates? templates = null, bool squawkFromSim = false)
    {
        templates ??= TestData.Templates();
        return new AtcSession(
            templates,
            AtcPlan.Build(Labrador(), null, templates),
            new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george", SquawkFromSim: squawkFromSim));
    }

    /// <summary>Templates mit festen Frequenzen für Gander und Montreal (sonst Platzhalter – die prüft Duopilo nicht).</summary>
    private static AtcTemplates WithFrequencies()
    {
        var templates = TestData.Templates();
        templates.Firs["CZQX"].Frequency = "133.300";
        templates.Firs["CZUL"].Frequency = "132.250";
        return templates;
    }

    private static string WrongSquawk(string squawk) => squawk[..3] + (char)('0' + ((squawk[3] - '0' + 1) % 8));

    /// <summary>Reiseflug entlang der Route; Höhe, Transponder und COM1 lassen sich setzen.</summary>
    private sealed class Cruise(AtcSession session)
    {
        public DateTimeOffset Now { get; private set; } = T0;

        public double AltitudeFt { get; set; } = 32000;

        public double VerticalSpeedFpm { get; set; }

        public string? Transponder { get; set; }

        public double? Com1 { get; set; }

        public bool OnGround { get; set; }

        public AtcInput At(double nm)
        {
            var plan = session.Plan;
            var (lat, lon) = (plan.Points[^1].Lat, plan.Points[^1].Lon);
            for (var i = 1; i < plan.Points.Count; i++)
            {
                if (plan.PointDistanceNm[i] >= nm)
                {
                    var a = plan.Points[i - 1];
                    var b = plan.Points[i];
                    var bearing = RouteMath.BearingDeg(a.Lat, a.Lon, b.Lat, b.Lon);
                    (lat, lon) = RouteMath.Destination(a.Lat, a.Lon, bearing, Math.Max(0, nm - plan.PointDistanceNm[i - 1]));
                    break;
                }
            }

            return new AtcInput
            {
                TimeUtc = Now,
                OnGround = OnGround,
                Lat = lat,
                Lon = lon,
                AltitudeFt = AltitudeFt,
                AglFt = AltitudeFt - 500,
                GroundSpeedKt = 450,
                IasKt = 270,
                VerticalSpeedFpm = VerticalSpeedFpm,
                QnhHpa = 1013.25,
                ZuluTimeSec = (22 * 3600) + (Now - T0).TotalSeconds,
                TransponderCode = Transponder,
                Com1ActiveMhz = Com1,
            };
        }

        public double Fix(int index) => session.Plan.FixDistanceNm(index)!.Value;

        public double Boundary => session.Plan.Firs.Single(f => f.Fir == "CZUL").StartNm;

        public void Start(double nm) => session.Start(At(nm));

        /// <summary>Zeit läuft an einer Stelle der Route (1 s je Takt). Rückgabe: alle ATC-Sprüche dabei.</summary>
        public List<AtcTransmission> Run(double nm, int seconds)
        {
            var output = new List<AtcTransmission>();
            for (var i = 0; i < seconds; i++)
            {
                Now = Now.AddSeconds(1);
                output.AddRange(session.Tick(At(nm)));
            }

            return output;
        }

        /// <summary>Zeit vergeht ohne Takt (z. B. Minuten bis zum nächsten Anruf).</summary>
        public void Advance(TimeSpan time) => Now += time;

        public IReadOnlyList<AtcTransmission> Say(string text, double nm) => session.PilotTransmission(text, At(nm));

        public IReadOnlyList<AtcRendered> Expected(double nm) => session.ExpectedPilotCalls(At(nm));
    }

    /// <summary>Bei Montreal Center eingecheckt, FL320.</summary>
    private static (AtcSession Session, Cruise Flight, double Nm) AtMontreal(AtcTemplates? templates = null, bool squawkFromSim = false)
    {
        var session = Session(templates, squawkFromSim);
        var flight = new Cruise(session);
        var nm = flight.Fix(Bravo) + 5;
        flight.Start(nm);
        Assert.Equal("Montreal Center", session.Station.Name);
        Assert.Equal(32000, session.ClearedAltitudeFt);
        Assert.Equal("CFG2205, identified, maintain FL320",
            Assert.Single(flight.Say("Montreal Center, Condor 2205, maintaining flight level three two zero", nm)).Text);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        return (session, flight, nm);
    }

    // ------------------------------------------------------------------ 1. Höhenanfragen

    [Theory]
    [InlineData("Montreal Center, Condor 2205, request climb flight level 340", 34000, 1)]
    [InlineData("Montreal Center, Condor 2205, request climb FL340", 34000, 1)]
    [InlineData("Condor two two zero five, requesting flight level three four zero", 34000, 0)]
    [InlineData("Condor 2205, request descent to one two thousand feet", 12000, -1)]
    [InlineData("Condor 2205, maintaining FL320, request climb FL340", 34000, 1)]
    [InlineData("Condor 2205, request higher", null, 1)]
    [InlineData("Condor 2205, request lower", null, -1)]
    [InlineData("Lufthansa four alpha bravo, request descent", null, -1)]
    public void Hoehenanfrage_wird_erkannt(string text, int? feet, int direction)
    {
        var request = AtcSession.ParseLevelRequest(HeardText.Parse(text));

        Assert.NotNull(request);
        Assert.Equal(feet, request!.Feet);
        Assert.Equal(direction, request.Direction);
    }

    [Theory]
    [InlineData("Montreal Center, Condor 2205, request direct YWK")]
    [InlineData("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin")]
    [InlineData("Shanwick, Condor 2205, request oceanic clearance, estimating ETARI at 1830, requesting flight level three seven zero")]
    [InlineData("Montreal Center, Condor 2205, maintaining flight level three two zero")]
    public void Keine_Hoehenanfrage(string text) => Assert.Null(AtcSession.ParseLevelRequest(HeardText.Parse(text)));

    [Theory]
    [InlineData("Montreal Center, Condor 2205, maintaining flight level three four zero", 34000)]
    [InlineData("Montreal Center, Condor 2205, maintaining FL320, request climb FL340", 32000)]
    [InlineData("Montreal Center, Condor 2205, passing FL250, climbing FL320", null)]
    public void Gemeldete_Hoehe(string text, int? feet) => Assert.Equal(feet, AtcSession.ReportedLevel(HeardText.Parse(text)));

    [Fact]
    public void Steigflug_Anfrage_im_Reiseflug_bekommt_Freigabe_mit_Readback()
    {
        var (session, flight, nm) = AtMontreal();

        // Testflug 22:03Z: bisher nur "roger", clearedAltitude blieb 32000
        var clearance = Assert.Single(flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm));
        Assert.Equal(AtcMessageIds.CenterLevelChange, clearance.MessageId);
        Assert.Equal("CFG2205, climb and maintain FL340", clearance.Text);
        Assert.Equal("Condor two two zero five, climb and maintain flight level three four zero", clearance.Spoken);
        Assert.Equal("Montreal Center", clearance.StationName);
        Assert.Equal(34000, session.ClearedAltitudeFt);
        Assert.Equal(new[] { "level" }, session.PendingReadback!.Items.Select(i => i.Name));

        Assert.Equal("CFG2205, negative, climb FL340",
            Assert.Single(flight.Say("climb and maintain flight level three five zero, Condor 2205", nm)).Text);
        Assert.Empty(flight.Say("flight level three four zero, Condor 2205", nm));
        Assert.Null(session.PendingReadback);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        Assert.Equal(34000, session.ClearedAltitudeFt);

        // Sinkflug und "request higher"/"lower" ohne Höhe
        Assert.Equal("CFG2205, descend and maintain FL300",
            Assert.Single(flight.Say("Montreal Center, Condor 2205, request descent flight level 300", nm)).Text);
        flight.Say("descend flight level three zero zero, Condor 2205", nm);
        Assert.Equal(30000, session.ClearedAltitudeFt);
        Assert.Equal("CFG2205, climb and maintain FL340", Assert.Single(flight.Say("Montreal Center, Condor 2205, request higher", nm)).Text);
    }

    [Fact]
    public void Hoehenanfrage_beim_Erstanruf_wird_gleich_beantwortet()
    {
        var session = Session();
        var flight = new Cruise(session);
        var nm = flight.Fix(Bravo) + 5;
        flight.Start(nm);
        Assert.Equal(AtcStep.CenterContact, session.Step);

        var reply = Assert.Single(flight.Say("Montreal Center, Condor 2205, maintaining flight level three two zero, request climb flight level three four zero", nm));
        Assert.Equal("CFG2205, climb and maintain FL340", reply.Text);
        Assert.Equal(AtcStep.CenterReadback, session.Step);
    }

    [Fact]
    public void Unter_der_Uebergangshoehe_mit_QNH()
    {
        var (session, flight, nm) = AtMontreal();

        var clearance = Assert.Single(flight.Say("Montreal Center, Condor 2205, request descent one six thousand feet", nm));
        Assert.Equal(AtcMessageIds.CenterLevelChangeQnh, clearance.MessageId);
        Assert.Equal("CFG2205, descend and maintain 16000 ft, A2992", clearance.Text);
        Assert.Equal("Condor two two zero five, descend and maintain one six thousand feet, altimeter two niner niner two", clearance.Spoken);
        Assert.Equal(new[] { "level", "qnh" }, session.PendingReadback!.Items.Select(i => i.Name));

        Assert.Equal("CFG2205, negative, A2992",
            Assert.Single(flight.Say("descend one six thousand feet, Condor 2205", nm)).Text);
        Assert.Empty(flight.Say("altimeter two niner niner two, Condor 2205", nm));
        Assert.Equal(16000, session.ClearedAltitudeFt);
    }

    [Fact]
    public void Ablehnung_mit_expect_und_spaeterer_Freigabe()
    {
        var templates = TestData.Templates();
        templates.Rules.LevelRequestUnablePercent = 100;
        templates.Rules.LevelRequestDelayMinutes = 10;
        var (session, flight, nm) = AtMontreal(templates);

        var unable = Assert.Single(flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm));
        Assert.Equal(AtcMessageIds.CenterLevelUnable, unable.MessageId);
        Assert.Equal("CFG2205, unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes", unable.Text);
        Assert.Contains("expect flight level three four zero in ten minutes", unable.Spoken, StringComparison.Ordinal);
        Assert.Equal(32000, session.ClearedAltitudeFt);
        Assert.Null(session.PendingReadback);
        Assert.Equal(34000, session.ExpectedLevel!.AltitudeFt);

        // Noch einmal gefragt: dieselbe Antwort mit der Restzeit
        Assert.Empty(flight.Run(nm, 240));
        Assert.EndsWith("expect FL340 in 6 minutes", Assert.Single(flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm)).Text);

        // Nach Ablauf gibt ATC von selbst frei
        Assert.Empty(flight.Run(nm, 355));
        var later = Assert.Single(flight.Run(nm, 10));
        Assert.Equal("CFG2205, climb and maintain FL340", later.Text);
        Assert.Equal(34000, session.ClearedAltitudeFt);
        Assert.Null(session.ExpectedLevel);
        Assert.NotNull(session.PendingReadback);
    }

    [Fact]
    public void Eigene_Vorlagen_ohne_die_neuen_Sprueche_nehmen_climb_level()
    {
        var templates = TestData.Templates();
        templates.Messages.Remove(AtcMessageIds.CenterLevelChange);
        templates.Messages.Remove(AtcMessageIds.CenterLevelChangeQnh);
        var (session, flight, nm) = AtMontreal(templates);

        var clearance = Assert.Single(flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm));
        Assert.Equal(AtcMessageIds.CenterClimb, clearance.MessageId);
        Assert.Equal("CFG2205, climb FL340", clearance.Text);
        Assert.Equal(34000, session.ClearedAltitudeFt);
    }

    // ------------------------------------------------------------------ 2. Stufensteigflüge aus dem OFP

    [Fact]
    public void Stufen_kommen_aus_den_Hoehen_im_Reiseflug()
    {
        var plan = Session().Plan;
        var step = Assert.Single(plan.StepClimbs);
        Assert.Equal("YWK", step.Fix);
        Assert.Equal(34000, step.AltitudeFt);
        Assert.Equal(plan.FixDistanceNm(Ywk)!.Value, step.AlongNm, 3);

        // Ohne Stages: zwischen TOC und TOD
        var noStages = Labrador() with { Fixes = Labrador().Fixes.Select(f => f with { Stage = null }).ToList() };
        Assert.Equal(new[] { 34000 }, AtcPlan.Build(noStages, null, TestData.Templates()).StepClimbs.Select(s => s.AltitudeFt));
    }

    [Fact]
    public void Stufensteigflug_wird_vor_dem_Stufenpunkt_angeboten()
    {
        var (session, flight, _) = AtMontreal();
        var ywk = flight.Fix(Ywk);

        // 25 nm davor: Hinweis für den Piloten, noch keine Freigabe
        Assert.Empty(flight.Run(ywk - 25, 2));
        Assert.Contains(flight.Expected(ywk - 25), c => c.MessageId == AtcMessageIds.CenterLevelRequest && c.Text == "Montreal Center, CFG2205, request climb FL340");

        // 10 nm davor: "when ready"
        var offer = Assert.Single(flight.Run(ywk - 9, 2));
        Assert.Equal(AtcMessageIds.CenterLevelWhenReady, offer.MessageId);
        Assert.Equal("CFG2205, when ready climb and maintain FL340", offer.Text);
        Assert.Equal(34000, session.ClearedAltitudeFt);
        Assert.Empty(flight.Say("when ready climb flight level three four zero, Condor 2205", ywk - 9));
        Assert.Equal(AtcStep.CenterCruise, session.Step);

        Assert.Empty(flight.Run(ywk + 5, 5));
        Assert.DoesNotContain(flight.Expected(ywk + 5), c => c.MessageId == AtcMessageIds.CenterLevelRequest);
    }

    [Fact]
    public void Stufensteigflug_ohne_Angebot_und_schon_angefragt()
    {
        var templates = TestData.Templates();
        templates.Rules.OfferStepClimbs = false;
        var (session, flight, nm) = AtMontreal(templates);
        var ywk = flight.Fix(Ywk);
        Assert.Empty(flight.Run(ywk - 5, 5));
        Assert.Equal(32000, session.ClearedAltitudeFt);

        // Mit Angebot, aber schon vorher angefragt: kein zweites Mal
        var (offered, flight2, nm2) = AtMontreal();
        flight2.Say("Montreal Center, Condor 2205, request climb flight level 340", nm2);
        flight2.Say("climb flight level three four zero, Condor 2205", nm2);
        Assert.Empty(flight2.Run(flight2.Fix(Ywk) - 5, 5));
        Assert.Equal(34000, offered.ClearedAltitudeFt);
        Assert.Equal(nm, nm2);
    }

    // ------------------------------------------------------------------ Höhe beim Erstanruf (Höhen-Bug)

    [Fact]
    public void Erstanruf_mit_hoeherer_Hoehe_wird_uebernommen()
    {
        var session = Session();
        var flight = new Cruise(session);
        flight.Start(flight.Fix(Alpha) - 30);
        Assert.Equal("Gander Center", session.Station.Name);
        flight.Say("Gander Center, Condor 2205, maintaining flight level three two zero", flight.Fix(Alpha) - 30);

        // Stufe ohne Duopilo-ATC, dann Übergabe an Montreal
        flight.AltitudeFt = 34000;
        Assert.StartsWith("CFG2205, contact Montreal Center", Assert.Single(flight.Run(flight.Boundary + 1, 1)).Text);
        var reply = Assert.Single(flight.Say("Montreal Center, Condor 2205, maintaining flight level three four zero", flight.Boundary + 2));
        Assert.Equal("CFG2205, identified, maintain FL340", reply.Text);
        Assert.Equal(34000, session.ClearedAltitudeFt);
    }

    [Fact]
    public void Gemeldete_Hoehe_ohne_Bestaetigung_im_Sim_bleibt_bei_der_Freigabe()
    {
        var session = Session();
        var flight = new Cruise(session);
        flight.Start(flight.Fix(Alpha) - 30);
        flight.Say("Gander Center, Condor 2205, maintaining flight level three two zero", flight.Fix(Alpha) - 30);
        flight.Run(flight.Boundary + 1, 1);

        // Laut Sim weiter FL320
        Assert.Equal("CFG2205, identified, maintain FL320",
            Assert.Single(flight.Say("Montreal Center, Condor 2205, maintaining flight level three four zero", flight.Boundary + 2)).Text);
        Assert.Equal(32000, session.ClearedAltitudeFt);
    }

    [Fact]
    public void Einstieg_ueber_der_Reiseflughoehe_nimmt_die_aktuelle_Hoehe()
    {
        var session = Session();
        var flight = new Cruise(session) { AltitudeFt = 36000 };
        flight.Start(flight.Fix(Bravo) + 5);
        Assert.Equal(36000, session.ClearedAltitudeFt);

        // Im Steigflug zählt weiter die Reiseflughöhe
        var climbing = Session();
        new Cruise(climbing) { AltitudeFt = 36000, VerticalSpeedFpm = 1500 }.Start(flight.Fix(Bravo) + 5);
        Assert.Equal(32000, climbing.ClearedAltitudeFt);
    }

    // ------------------------------------------------------------------ 3./5. Squawk

    [Fact]
    public void Falscher_Squawk_check_squawk_mit_Wiederholung_und_Ende()
    {
        var (session, flight, nm) = AtMontreal();
        var assigned = session.Squawk;
        session.TakeDeviationEvents();

        // Wie im Testflug: MSFS setzt einen eigenen Code
        flight.Transponder = WrongSquawk(assigned);
        Assert.Empty(flight.Run(nm, 14));
        Assert.Null(session.SquawkDeviation);

        var check = Assert.Single(flight.Run(nm, 2));
        Assert.Equal(AtcMessageIds.CommonCheckSquawk, check.MessageId);
        Assert.Equal($"CFG2205, check squawk, should be {assigned}", check.Text);
        Assert.Equal($"Condor two two zero five, check squawk, should be {AtcSpeech.Spell(assigned)}", check.Spoken);

        var deviation = session.SquawkDeviation!;
        Assert.Equal(AtcDeviationKind.Squawk, deviation.Kind);
        Assert.Equal(assigned, deviation.Expected);
        Assert.Equal(flight.Transponder, deviation.Actual);
        Assert.Equal(1, deviation.Calls);
        var action = Assert.Single(deviation.Actions);
        Assert.Equal(("xpdr.code", assigned), (action.Action, action.Value));
        var raised = Assert.Single(session.TakeDeviationEvents());
        Assert.Equal(AtcDeviationChange.Raised, raised.Change);

        // Duopilo setzt nie selbst zurück; ATC wiederholt nach 120 s, höchstens dreimal
        Assert.Empty(flight.Run(nm, 100));
        Assert.Single(flight.Run(nm, 25));
        Assert.Single(flight.Run(nm, 125));
        Assert.Empty(flight.Run(nm, 400));
        Assert.Equal(3, session.SquawkDeviation!.Calls);
        Assert.Equal(assigned, session.Squawk);

        // Pilot stellt den Code: Abweichung aufgehoben
        flight.Transponder = assigned;
        Assert.Empty(flight.Run(nm, 1));
        Assert.Null(session.SquawkDeviation);
        Assert.Equal(AtcDeviationChange.Cleared, Assert.Single(session.TakeDeviationEvents()).Change);
    }

    [Fact]
    public void Squawk_beim_Drehen_Notfallcode_und_am_Boden_ohne_Hinweis()
    {
        var (session, flight, nm) = AtMontreal();
        var assigned = session.Squawk;

        // Der Pilot dreht: jeder neue Wert startet die Wartezeit neu
        foreach (var code in new[] { "1000", "1200", "1230", "1234" })
        {
            flight.Transponder = code == assigned ? "4444" : code;
            Assert.Empty(flight.Run(nm, 10));
        }

        flight.Transponder = "7700";
        Assert.Empty(flight.Run(nm, 60));
        Assert.Null(session.SquawkDeviation);

        flight.Transponder = WrongSquawk(assigned);
        flight.OnGround = true;
        Assert.Empty(flight.Run(nm, 60));
        Assert.Null(session.SquawkDeviation);
    }

    [Fact]
    public void Squawk_vom_Sim_uebernehmen()
    {
        var (session, flight, nm) = AtMontreal(squawkFromSim: true);
        var assigned = session.Squawk;
        session.TakeDeviationEvents();
        var simCode = assigned == "3172" ? "3173" : "3172"; // wie im Testflug

        flight.Transponder = simCode;
        Assert.Empty(flight.Run(nm, 20));
        Assert.Equal(simCode, session.Squawk);
        Assert.Equal("sim", session.SquawkSource);
        Assert.Null(session.SquawkDeviation);
        var adopted = Assert.Single(session.TakeDeviationEvents());
        Assert.Equal(AtcDeviationChange.Adopted, adopted.Change);
        Assert.Equal(assigned, adopted.Previous);

        // Allgemeine Codes übernimmt Duopilo nicht – dann wie sonst "check squawk"
        flight.Transponder = "2000";
        Assert.Equal($"CFG2205, check squawk, should be {simCode}", Assert.Single(flight.Run(nm, 20)).Text);

        // Gesichert und wiederhergestellt bleibt der übernommene Squawk
        var restored = Session(squawkFromSim: true);
        restored.Restore(AtcSessionStore.Deserialize(AtcSessionStore.Serialize(session.Snapshot(flight.Now)))!, flight.At(nm));
        Assert.Equal(simCode, restored.Squawk);
        Assert.Equal("sim", restored.SquawkSource);
    }

    // ------------------------------------------------------------------ 5. COM1

    [Fact]
    public void Frequenz_vom_Sim_verstellt_check_frequency()
    {
        var (session, flight, nm) = AtMontreal(WithFrequencies());
        Assert.Equal("templates", session.Station.FrequencySource);
        flight.Com1 = 132.25;
        Assert.Empty(flight.Run(nm, 30));
        session.TakeDeviationEvents();

        // MSFS stellt COM1 ohne Freigabe um
        flight.Com1 = 119.4;
        Assert.Empty(flight.Run(nm, 14));
        var check = Assert.Single(flight.Run(nm, 2));
        Assert.Equal(AtcMessageIds.CommonCheckFrequency, check.MessageId);
        Assert.Equal("CFG2205, check frequency, should be Montreal Center 132.25", check.Text);
        Assert.Equal("Condor two two zero five, check frequency, should be Montreal Center one three two decimal two five", check.Spoken);

        var deviation = session.FrequencyDeviation!;
        Assert.Equal(("132.25", "119.4", "Montreal Center"), (deviation.Expected, deviation.Actual, deviation.Station));
        Assert.Equal(new[] { ("com1.standby", (string?)"132.250"), ("com1.swap", null) }, deviation.Actions.Select(a => (a.Action, a.Value)));
        Assert.Equal(AtcDeviationChange.Raised, Assert.Single(session.TakeDeviationEvents()).Change);

        flight.Com1 = 132.25;
        Assert.Empty(flight.Run(nm, 1));
        Assert.Null(session.FrequencyDeviation);
        Assert.Equal(AtcDeviationChange.Cleared, Assert.Single(session.TakeDeviationEvents()).Change);
    }

    [Fact]
    public void Nach_der_Uebergabe_gilt_eine_Schonfrist()
    {
        var session = Session(WithFrequencies());
        var flight = new Cruise(session) { Com1 = 133.3 };
        flight.Start(flight.Fix(Alpha) - 30);
        flight.Say("Gander Center, Condor 2205, maintaining flight level three two zero", flight.Fix(Alpha) - 30);
        Assert.Empty(flight.Run(flight.Fix(Alpha) - 30, 30));

        var handoff = Assert.Single(flight.Run(flight.Boundary + 1, 1));
        Assert.Equal("CFG2205, contact Montreal Center 132.25", handoff.Text);

        // Quittung noch auf der alten Frequenz – kein Anruf bei Montreal, die Schonfrist läuft weiter
        Assert.Empty(flight.Say("contact Montreal Center one three two decimal two five, Condor 2205", flight.Boundary + 1));
        Assert.Empty(flight.Run(flight.Boundary + 1, 60));
        Assert.Equal("CFG2205, check frequency, should be Montreal Center 132.25", Assert.Single(flight.Run(flight.Boundary + 1, 20)).Text);
    }

    [Fact]
    public void Platzhalter_und_Oceanic_werden_nicht_geprueft()
    {
        var (session, flight, nm) = AtMontreal();
        Assert.Equal("placeholder", session.Station.FrequencySource);
        flight.Com1 = 119.4;
        Assert.Empty(flight.Run(nm, 60));
        Assert.Null(session.FrequencyDeviation);
    }

    // ------------------------------------------------------------------ 4. Falsche Station

    [Fact]
    public void Positionsmeldung_an_die_alte_Station_wiederholt_die_Uebergabe()
    {
        var session = Session();
        var flight = new Cruise(session);
        flight.Start(flight.Fix(Alpha) - 30);
        flight.Say("Gander Center, Condor 2205, maintaining flight level three two zero", flight.Fix(Alpha) - 30);
        Assert.StartsWith("CFG2205, contact Montreal Center", Assert.Single(flight.Run(flight.Boundary + 1, 1)).Text);

        // Testflug 21:18Z: 18 s nach der Übergabe an "Gander" adressiert – bisher antwortete Montreal "identified"
        flight.Run(flight.Boundary + 2, 18);
        var repeat = Assert.Single(flight.Say("Gander, Condor 2205, position BRAVO at 2118, flight level three two zero, estimating YWK at 2140", flight.Boundary + 2));
        Assert.Equal("Gander Center", repeat.StationName);
        Assert.Equal("CFG2205, contact Montreal Center 132.6", repeat.Text);
        Assert.Equal("Montreal Center", session.Station.Name);
        Assert.Equal(AtcStep.CenterContact, session.Step);

        // Danach richtig gerufen
        Assert.Equal("CFG2205, identified, maintain FL320",
            Assert.Single(flight.Say("Montreal Center, Condor 2205, maintaining flight level three two zero", flight.Boundary + 3)).Text);

        // Viel später: Montreal weist darauf hin, wer hier spricht
        flight.Advance(TimeSpan.FromMinutes(20));
        var wrong = Assert.Single(flight.Say("Gander, Condor 2205, request direct YWK", flight.Boundary + 50));
        Assert.Equal(AtcMessageIds.CommonWrongStation, wrong.MessageId);
        Assert.Equal("CFG2205, this is Montreal Center, say again", wrong.Text);
        Assert.Equal("Montreal Center", wrong.StationName);
    }

    [Theory]
    [InlineData("Edmonton Center, Condor 2205, request direct YWK", true)]
    [InlineData("Toronto Center, Condor 2205, request direct YWK", true)]
    [InlineData("Montreal, Condor 2205, request direct YWK", false)]
    [InlineData("Montreal Centre, good day, Condor 2205, request direct YWK", false)]
    [InlineData("Center, Condor 2205, request direct YWK", false)]
    [InlineData("Condor 2205, request direct YWK", false)]
    [InlineData("Expect ILS approach, Condor 2205", false)]
    [InlineData("Good day, Condor 2205", false)]
    public void Andere_Station_wird_erkannt(string text, bool wrongStation)
    {
        var (_, flight, nm) = AtMontreal();
        var replies = flight.Say(text, nm);
        Assert.Equal(wrongStation, replies.Any(r => r.MessageId == AtcMessageIds.CommonWrongStation));
        if (wrongStation)
        {
            Assert.Equal("CFG2205, this is Montreal Center, say again", Assert.Single(replies).Text);
        }
    }

    [Fact]
    public void Readback_mit_Stationsname_ist_kein_falscher_Anruf()
    {
        var (session, flight, nm) = AtMontreal();
        flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm);

        // Offenes Readback – ein vorangestellter Name zählt nicht als Anruf
        Assert.Empty(flight.Say("Gander, climb flight level three four zero, Condor 2205", nm));
        Assert.Null(session.PendingReadback);
    }

    // ------------------------------------------------------------------ Sinkflug am Top of Descent (Testlog 04:03Z)

    private const int Tod = 6;

    /// <summary>Wie nach KLAX: STAR mit geplanten Höhen im Navlog.</summary>
    private static Ofp WithStar()
    {
        var ofp = Labrador();
        var fixes = ofp.Fixes.ToList();
        fixes.InsertRange(7,
        [
            Fix("HOLLE", 46.80, -72.80, "CZUL", "DSC", 11000) with { IsSidStar = true, Airway = "ANJLL4" },
            Fix("ANJLL", 46.00, -73.30, "CZUL", "DSC", 8000) with { IsSidStar = true, Airway = "ANJLL4" },
        ]);
        return ofp with { StarIdent = "ANJLL4", Fixes = fixes };
    }

    private static AtcSession SessionFor(Ofp ofp)
    {
        var templates = TestData.Templates();
        return new AtcSession(templates, AtcPlan.Build(ofp, null, templates), new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));
    }

    /// <summary>Bei Montreal eingecheckt, dann bis kurz vor den Top of Descent; Rückgabe: die Sinkflugfreigabe.</summary>
    private static (AtcSession Session, Cruise Flight, AtcTransmission Descent) ToTopOfDescent(Ofp ofp)
    {
        var session = SessionFor(ofp);
        var flight = new Cruise(session);
        var nm = flight.Fix(Bravo) + 5;
        flight.Start(nm);
        flight.Say("Montreal Center, Condor 2205, maintaining flight level three two zero", nm);
        flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm);
        flight.Say("climb flight level three four zero, Condor 2205", nm);
        flight.AltitudeFt = 34000;
        return (session, flight, Assert.Single(flight.Run(flight.Fix(Tod) - 3, 1)));
    }

    [Fact]
    public void Mit_STAR_descend_via_und_QNH()
    {
        var (session, flight, descent) = ToTopOfDescent(WithStar());
        Assert.Equal(8000, session.Plan.StarBottomFt);

        Assert.Equal(AtcMessageIds.CenterDescendViaQnh, descent.MessageId);
        Assert.Equal("CFG2205, descend via ANJLL4 arrival, A2992", descent.Text);
        Assert.Equal("Condor two two zero five, descend via Anjll four arrival, altimeter two niner niner two", descent.Spoken);
        Assert.Equal(8000, session.ClearedAltitudeFt); // unterste Höhe der STAR laut Navlog
        Assert.Equal(new[] { "star", "qnh" }, session.PendingReadback!.Items.Select(i => i.Name));

        var tod = flight.Fix(Tod) - 3;
        Assert.Equal("CFG2205, negative, A2992", Assert.Single(flight.Say("descend via ANJLL four arrival, Condor 2205", tod)).Text);
        Assert.Empty(flight.Say("altimeter two niner niner two, Condor 2205", tod));
        Assert.Equal(AtcStep.CenterCruise, session.Step);
    }

    [Fact]
    public void Ohne_STAR_unter_der_Uebergangshoehe_mit_QNH()
    {
        var (session, flight, descent) = ToTopOfDescent(Labrador());
        Assert.Equal(AtcMessageIds.CenterDescendQnh, descent.MessageId);
        Assert.Equal("CFG2205, descend 11000 ft, A2992", descent.Text);
        Assert.Equal(11000, session.ClearedAltitudeFt);
        Assert.Empty(flight.Say("descend one one thousand feet, altimeter two niner niner two, Condor 2205", flight.Fix(Tod) - 3));
    }

    [Fact]
    public void Ohne_STAR_ueber_der_Uebergangshoehe_wie_bisher()
    {
        // Ziel mit europäischer Übergangshöhe: FL110 ohne QNH
        var ofp = Labrador() with { Destination = new OfpAirport("EDDB", "Berlin", "25R", 45.4706, -73.7408, 157, 5000, 6000, null) };
        var (session, _, descent) = ToTopOfDescent(ofp);
        Assert.Equal(AtcMessageIds.CenterDescend, descent.MessageId);
        Assert.Equal("CFG2205, descend FL110", descent.Text);
        Assert.Equal(new[] { "level" }, session.PendingReadback!.Items.Select(i => i.Name));
    }

    [Fact]
    public void Descend_via_mit_STAR_ueber_der_Uebergangshoehe_ohne_QNH()
    {
        // STAR endet laut Navlog auf FL120, Ziel mit Übergangshöhe 5000 ft
        var ofp = WithStar() with { Destination = new OfpAirport("EDDB", "Berlin", "25R", 45.4706, -73.7408, 157, 5000, 6000, null) };
        ofp = ofp with
        {
            Fixes = ofp.Fixes.Select(f => f.Ident switch
            {
                "HOLLE" => f with { AltitudeFt = 13000 },
                "ANJLL" => f with { AltitudeFt = 12000 },
                _ => f,
            }).ToList(),
        };
        var (session, _, descent) = ToTopOfDescent(ofp);
        Assert.Equal(AtcMessageIds.CenterDescendVia, descent.MessageId);
        Assert.Equal("CFG2205, descend via ANJLL4 arrival", descent.Text);
        Assert.Equal(12000, session.ClearedAltitudeFt);
        Assert.Equal(new[] { "star" }, session.PendingReadback!.Items.Select(i => i.Name));
    }

    // ------------------------------------------------------------------ Sicherung

    [Fact]
    public void Stufe_und_Erwartung_ueberstehen_den_Neustart()
    {
        var templates = TestData.Templates();
        templates.Rules.LevelRequestUnablePercent = 100;
        var (session, flight, nm) = AtMontreal(templates);
        flight.Say("Montreal Center, Condor 2205, request climb flight level 340", nm);
        Assert.NotNull(session.ExpectedLevel);

        var snapshot = AtcSessionStore.Deserialize(AtcSessionStore.Serialize(session.Snapshot(flight.Now)))!;
        var restored = Session(templates);
        restored.Restore(snapshot, flight.At(nm));
        Assert.Equal(session.ExpectedLevel, restored.ExpectedLevel);
        Assert.Equal(session.Squawk, restored.Squawk);
        Assert.Equal("duopilo", restored.SquawkSource);
    }

    [Fact]
    public void Alte_Sicherung_mit_Squawk_von_Kopilot_wird_uebernommen()
    {
        // atc-session.json aus 0.9.x: "squawkSource": "kopilot"
        var templates = TestData.Templates();
        var (session, flight, nm) = AtMontreal(templates);
        var snapshot = AtcSessionStore.Deserialize(AtcSessionStore.Serialize(session.Snapshot(flight.Now)))! with { SquawkSource = "kopilot" };

        var restored = Session(templates);
        restored.Restore(snapshot, flight.At(nm));

        Assert.Equal(session.Squawk, restored.Squawk);
        Assert.Equal("duopilo", restored.SquawkSource);
    }
}
