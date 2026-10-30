using Kopilot.Atc;
using Kopilot.FlightPlan;

namespace Kopilot.Tests;

/// <summary>Kopilot-ATC: Vorlagen, Sprechweise, Readback-Prüfung und Zustandsübergänge – ohne MSFS.</summary>
public sealed partial class AtcTests
{
    // ------------------------------------------------------------------ Testdaten

    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Kopilot", AtcTemplateLoader.FileName)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    private static AtcTemplates Templates() => TestData.Templates();

    private static OfpAirport Airport(string icao, string name, string runway, double lat, double lon, int elevation, int ta, string? metar = null) =>
        new(icao, name, runway, lat, lon, elevation, ta, ta + 1000, metar);

    private static OfpFix Fix(string ident, double lat, double lon, string? type = "wpt", bool sidStar = false, string? fir = null, string? airway = null) => new()
    {
        Ident = ident,
        Lat = lat,
        Lon = lon,
        Type = type,
        IsSidStar = sidStar,
        Fir = fir,
        Airway = airway,
    };

    private const double FraLat = 50.0333;
    private const double FraLon = 8.5706;
    private const double BerLat = 52.3667;
    private const double BerLon = 13.5033;

    private static (double Lat, double Lon) Along(double f) => (FraLat + (BerLat - FraLat) * f, FraLon + (BerLon - FraLon) * f);

    /// <summary>Kurzstrecke EDDF → EDDB mit FIR-Wechsel Langen → Bremen, ohne Oceanic-Teil.</summary>
    private static Ofp ShortHaul()
    {
        OfpFix At(string ident, double f, string? type = "wpt", bool sidStar = false, string? fir = "EDGG")
        {
            var (lat, lon) = Along(f);
            return Fix(ident, lat, lon, type, sidStar, fir);
        }

        return new Ofp
        {
            RequestId = "fra-ber-1",
            Callsign = "DLH4AB",
            Origin = Airport("EDDF", "Frankfurt/Main International", "25C", FraLat, FraLon, 364, 5000),
            Destination = Airport("EDDB", "Berlin Brandenburg", "25R", BerLat, BerLon, 157, 5000, "EDDB 291150Z 25012KT 9999 FEW030 12/06 Q1013 NOSIG"),
            InitialAltitudeFt = 35000,
            SidIdent = "MARUN7G",
            StarIdent = "KETAP2B",
            Fixes =
            [
                At("MARUN", 0.10, sidStar: true),
                At("TOC", 0.20, type: "ltlg"),
                At("ERNAS", 0.35),
                At("LEVBU", 0.50, fir: "EDWW"),
                At("TOD", 0.65, type: "ltlg", fir: "EDWW"),
                At("KETAP", 0.80, sidStar: true, fir: "EDWW"),
                At("EDDB", 1.0, type: "apt", fir: "EDWW"),
            ],
        };
    }

    /// <summary>Nordatlantik EGLL → KJFK ohne FIR-Angaben im OFP (Shanwick/Gander über die 30°W-Regel).</summary>
    private static Ofp Transatlantic() => new()
    {
        RequestId = "lhr-jfk-1",
        Callsign = "DLH400",
        Origin = Airport("EGLL", "London Heathrow", "27R", 51.4700, -0.4543, 83, 6000),
        Destination = Airport("KJFK", "John F Kennedy International", "22L", 40.6398, -73.7789, 13, 18000),
        InitialAltitudeFt = 35000,
        Fixes =
        [
            Fix("CPT", 51.49, -1.22, sidStar: true),
            Fix("TOC", 51.60, -2.50, type: "ltlg"),
            Fix("STU", 51.99, -4.85),
            Fix("RESNO", 55.00, -15.00),
            Fix("55N020W", 55.00, -20.00, type: "ltlg"),
            Fix("55N030W", 55.00, -30.00, type: "ltlg"),
            Fix("54N040W", 54.00, -40.00, type: "ltlg"),
            Fix("52N050W", 52.00, -50.00, type: "ltlg"),
            Fix("LOMSI", 50.00, -54.00),
            Fix("TOD", 42.50, -69.00, type: "ltlg"),
            Fix("KJFK", 40.6398, -73.7789, type: "apt"),
        ],
    };

    private sealed class FixedTaxiRoutes : ITaxiRouteProvider
    {
        public TaxiInstruction? Departure(TaxiRequest request) => new(["N", "N3"], HoldingPoint: "S", PushbackFacing: "east");

        public TaxiInstruction? Arrival(TaxiRequest request) => new(["L", "L5"], Gate: "A26");
    }

    private static AtcSession Session(Ofp ofp, ITaxiRouteProvider? taxi = null, Func<string, IReadOnlyList<AirportFrequency>?>? frequencies = null)
    {
        var templates = Templates();
        var plan = AtcPlan.Build(ofp, null, templates);
        return new AtcSession(templates, plan, new AtcSessionContext(frequencies ?? (_ => null), taxi ?? new NoTaxiRouteProvider(), "bm_george"));
    }

    /// <summary>Simuliert Zeit und Flugzustand für die Zustandsmaschine.</summary>
    private sealed class Flight(AtcSession session)
    {
        public DateTimeOffset Now { get; private set; } = T0;

        public AtcInput State { get; set; } = new()
        {
            OnGround = true,
            AltitudeFt = 364,
            AglFt = 0,
            GroundSpeedKt = 0,
            IasKt = 0,
            Lat = FraLat,
            Lon = FraLon,
            HeadingMagDeg = 250,
            WindDirectionDeg = 250,
            WindSpeedKt = 12,
            QnhHpa = 1013,
            ZuluTimeSec = 12 * 3600,
            ParkingBrake = true,
        };

        public List<AtcTransmission> Heard { get; } = [];

        public AtcInput Input => State with { TimeUtc = Now, ZuluTimeSec = 12 * 3600 + (Now - T0).TotalSeconds };

        public void Start() => session.Start(Input);

        /// <summary>Pilot spricht; Rückgabe: die Antworten von ATC.</summary>
        public IReadOnlyList<AtcTransmission> Say(string text)
        {
            var responses = session.PilotTransmission(text, Input);
            Heard.AddRange(responses);
            return responses;
        }

        /// <summary>Zeit läuft (1 s je Takt) mit dem gegebenen Zustand.</summary>
        public IReadOnlyList<AtcTransmission> Run(int seconds, Func<AtcInput, AtcInput>? change = null)
        {
            if (change is not null)
            {
                State = change(State);
            }

            var output = new List<AtcTransmission>();
            for (var i = 0; i < seconds; i++)
            {
                Now = Now.AddSeconds(1);
                output.AddRange(session.Tick(Input));
            }

            Heard.AddRange(output);
            return output;
        }

        public IReadOnlyList<AtcTransmission> FlyTo(double f, double altitudeFt, double gs = 450, double ias = 280, double vs = 0)
        {
            var (lat, lon) = Along(f);
            return Run(2, s => s with { OnGround = false, Lat = lat, Lon = lon, AltitudeFt = altitudeFt, AglFt = altitudeFt - 300, GroundSpeedKt = gs, IasKt = ias, VerticalSpeedFpm = vs, ParkingBrake = false });
        }
    }

    private static string WrongSquawk(string squawk) => squawk[..3] + (char)('0' + ((squawk[3] - '0' + 1) % 8));

    // ------------------------------------------------------------------ Vorlagen

    [Fact]
    public void Mitgelieferte_Vorlagen_sind_vollstaendig_und_gueltig()
    {
        var templates = Templates();

        Assert.Empty(AtcTemplateLoader.Validate(templates));
        Assert.All(AtcMessageIds.All, id => Assert.NotNull(templates.Message(id)));
        Assert.Equal(new[] { "sid", "initialAlt", "squawk" }, templates.Message(AtcMessageIds.DeliveryClearance)!.Readback);
        Assert.Contains("{callsign}, cleared to {dest}, {sid} departure, runway {runway}, climb {initialAlt}, squawk {squawk}", templates.Message(AtcMessageIds.DeliveryClearance)!.Variants);
        Assert.NotEmpty(templates.Voices.Pool);
        Assert.Equal("Lufthansa", templates.CallsignTelephony["dlh"]); // OpenFlights, Groß-/Kleinschreibung egal
        Assert.Equal(AtcTemplates.CurrentVersion, templates.Version);
    }

    [Fact]
    public void Pruefung_findet_unbekannte_Variablen_fehlende_Korrekturen_und_Mach()
    {
        var templates = Templates();
        templates.Messages["center.climb"] = new AtcMessageTemplate { Variants = ["{callsign}, climb {level}, {speedMach}"], Readback = ["level", "heading2"] };
        templates.Messages["departure.direct"] = new AtcMessageTemplate { Variants = ["{callsign}, maintain mach decimal seven eight"] };
        templates.Messages.Remove(AtcMessageIds.TowerLanding);

        var errors = AtcTemplateLoader.Validate(templates);

        Assert.Contains(errors, e => e.Contains("{speedMach}", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("heading2", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Mach", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(AtcMessageIds.TowerLanding, StringComparison.Ordinal));
    }

    [Fact]
    public void Renderer_nimmt_die_erste_Variante_mit_allen_Werten()
    {
        string[] variants = ["{callsign}, taxi to holding point {holdingPoint} runway {runway} via {taxiRoute}", "{callsign}, taxi to holding point runway {runway}"];
        var values = new Dictionary<string, AtcValue>
        {
            ["callsign"] = AtcSpeech.Callsign("DLH4AB", new Dictionary<string, string> { ["DLH"] = "Lufthansa" }),
            ["runway"] = AtcSpeech.Runway(25, "C"),
        };

        var withoutRoute = AtcRenderer.Render("ground.taxi", variants, n => values.GetValueOrDefault(n));
        Assert.NotNull(withoutRoute);
        Assert.Equal("DLH4AB, taxi to holding point runway 25C", withoutRoute.Text);
        Assert.Equal("Lufthansa four alpha bravo, taxi to holding point runway two five center", withoutRoute.Spoken);

        values["holdingPoint"] = AtcSpeech.Ident("S");
        values["taxiRoute"] = AtcSpeech.TaxiRoute(["N", "N3"]);
        var withRoute = AtcRenderer.Render("ground.taxi", variants, n => values.GetValueOrDefault(n));
        Assert.Equal("DLH4AB, taxi to holding point S runway 25C via N, N3", withRoute!.Text);
        Assert.Equal("Lufthansa four alpha bravo, taxi to holding point sierra runway two five center via november, november three", withRoute.Spoken);
    }

    [Fact]
    public void Feste_Abkuerzungen_im_Vorlagentext_werden_buchstabiert()
    {
        var rendered = AtcRenderer.Render("x", ["{callsign}, request IFR clearance"], n => n == "callsign" ? AtcSpeech.Callsign("BAW12", new Dictionary<string, string> { ["BAW"] = "Speedbird" }) : null);
        Assert.Equal("Speedbird one two, request I F R clearance", rendered!.Spoken);
    }

    // ------------------------------------------------------------------ Sprechweise

    [Theory]
    [InlineData(5000.0, 5000.0, "5000 ft", "five thousand feet")]
    [InlineData(4000.0, 5000.0, "4000 ft", "four thousand feet")]
    [InlineData(11000.0, 5000.0, "FL110", "flight level one one zero")]
    [InlineData(35000.0, 18000.0, "FL350", "flight level three five zero")]
    [InlineData(12500.0, 18000.0, "12500 ft", "one two thousand five hundred feet")]
    [InlineData(10000.0, 18000.0, "10000 ft", "one zero thousand feet")]
    [InlineData(7000.0, 5000.0, "FL070", "flight level seven zero")]
    public void Hoehen_nach_ICAO(double feet, double transition, string written, string spoken)
    {
        var value = AtcSpeech.Altitude(feet, transition);
        Assert.Equal(written, value.Written);
        Assert.Equal(spoken, value.Spoken);
    }

    [Theory]
    [InlineData(121.8, "121.8", "one two one decimal eight")]
    [InlineData(118.275, "118.275", "one one eight decimal two seven five")]
    [InlineData(132.6, "132.6", "one three two decimal six")]
    [InlineData(119.0, "119.0", "one one niner decimal zero")]
    public void Frequenzen_Ziffer_fuer_Ziffer(double mhz, string written, string spoken)
    {
        var value = AtcSpeech.Frequency(mhz);
        Assert.Equal(written, value.Written);
        Assert.Equal(spoken, value.Spoken);
    }

    [Fact]
    public void Zahlen_werden_einzeln_gesprochen_und_Speed_in_Knoten()
    {
        Assert.Equal("two two zero five", AtcSpeech.Squawk("2205").Spoken);
        Assert.Equal("two five zero", AtcSpeech.Speed(250).Spoken);
        Assert.Equal("250", AtcSpeech.Speed(249.6).Written);
        Assert.Equal("zero niner zero", AtcSpeech.Heading(90).Spoken);
        Assert.Equal("two five zero degrees, one two knots", AtcSpeech.Wind(248, 12).Spoken);
        Assert.Equal("calm", AtcSpeech.Wind(100, 1).Spoken);
        Assert.Equal("Q N H one zero one three", AtcSpeech.Qnh(1013.2, inchesOfMercury: false).Spoken);
        Assert.Equal("altimeter two niner niner two", AtcSpeech.Qnh(1013.25, inchesOfMercury: true).Spoken);
        Assert.Equal("one two three four", AtcSpeech.Time(new TimeSpan(12, 34, 10)).Spoken);
        Assert.Equal("1234Z", AtcSpeech.Time(new TimeSpan(12, 34, 10)).Written);
    }

    [Theory]
    [InlineData("MARUN7G", "Marun seven golf")]
    [InlineData("KETAP2B", "Ketap two bravo")]
    [InlineData("TOBAK", "Tobak")]
    [InlineData("FFM", "foxtrot foxtrot mike")]
    [InlineData("A26", "alpha two six")]
    [InlineData("S", "sierra")]
    public void Kennungen(string ident, string spoken) => Assert.Equal(spoken, AtcSpeech.Ident(ident).Spoken);

    [Fact]
    public void Rufzeichen_mit_Telephony()
    {
        var telephony = Templates().CallsignTelephony;
        Assert.Equal("Lufthansa four alpha bravo", AtcSpeech.Callsign("DLH4AB", telephony).Spoken);
        Assert.Equal("Speedbird one two three", AtcSpeech.Callsign("BAW123", telephony).Spoken);
        Assert.Equal("delta echo alpha bravo charlie", AtcSpeech.Callsign("D-EABC", telephony).Spoken); // Kennzeichen, keine Airline "DEA"
        Assert.Equal("golf alpha bravo charlie delta", AtcSpeech.Callsign("G-ABCD", telephony).Spoken);
    }

    [Fact]
    public void Koordinaten_fuer_Oceanic()
    {
        var value = AtcSpeech.Position(55, -20);
        Assert.Equal("55N020W", value.Written);
        Assert.Equal("fifty five north twenty west", value.Spoken);
        Assert.Equal("fifty five thirty north twenty west", AtcSpeech.Position(55.5, -20).Spoken);
        Assert.True(AtcSpeech.IsLatLonIdent("55N020W"));
        Assert.True(AtcSpeech.IsLatLonIdent("5520N"));
        Assert.False(AtcSpeech.IsLatLonIdent("RESNO"));
        Assert.False(AtcSpeech.IsLatLonIdent("N3"));
    }

    // ------------------------------------------------------------------ Readback-Prüfung

    private static readonly ReadbackItem[] Clearance =
    [
        new("sid", AtcSpeech.Ident("MARUN7G")),
        new("initialAlt", AtcSpeech.Altitude(5000, 5000)),
        new("squawk", AtcSpeech.Squawk("2205")),
    ];

    [Theory]
    [InlineData("cleared to Berlin, MARUN seven golf departure, runway two five center, climb five thousand feet, squawk two two zero five, Lufthansa four alpha bravo")]
    [InlineData("Cleared Berlin MARUN7G, climb 5000 ft, squawk 2205, DLH4AB")]
    [InlineData("marun seven golf, five thousand, two two zero five")]
    [InlineData("MARUN 7G departure, climbing to five thousand, squawking 2205")]
    public void Readback_richtig(string readback)
    {
        var result = ReadbackChecker.Check(readback, Clearance);
        Assert.True(result.Correct, string.Join("; ", result.Issues.Select(i => $"{i.Item}: {i.Problem} {i.Heard}")));
    }

    [Fact]
    public void Readback_falscher_Squawk()
    {
        var result = ReadbackChecker.Check("cleared to Berlin, MARUN seven golf, climb five thousand feet, squawk two two zero four", Clearance);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("squawk", issue.Item);
        Assert.Equal(ReadbackProblem.Wrong, issue.Problem);
        Assert.Equal("2204", issue.Heard);
    }

    [Fact]
    public void Readback_falsche_Hoehe_und_fehlende_SID()
    {
        var result = ReadbackChecker.Check("cleared to Berlin, climb six thousand feet, squawk two two zero five", Clearance);

        Assert.Equal(2, result.Issues.Count);
        Assert.Contains(result.Issues, i => i.Item == "sid" && i.Problem == ReadbackProblem.Missing);
        Assert.Contains(result.Issues, i => i.Item == "initialAlt" && i.Problem == ReadbackProblem.Wrong && i.Heard == "6000");
    }

    [Theory]
    [InlineData("climb flight level one one zero", true)]
    [InlineData("climb FL110", true)]
    [InlineData("climbing one one zero", true)]
    [InlineData("climb flight level one two zero", false)]
    [InlineData("climb one one thousand feet", true)]
    public void Readback_Flight_Level(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("level", AtcSpeech.Altitude(11000, 5000))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("runway two five center", true)]
    [InlineData("runway 25C", true)]
    [InlineData("runway two five left", false)]
    [InlineData("runway two three center", false)]
    [InlineData("line up and wait", false)]
    public void Readback_Bahn(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("runway", AtcSpeech.Runway(25, "C"))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("one two one decimal eight", true)]
    [InlineData("121.8", true)]
    [InlineData("one two one point eight", true)]
    [InlineData("one two one decimal niner", false)]
    public void Readback_Frequenz(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("freq", AtcSpeech.Frequency(121.8))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("QNH one zero one three", true)]
    [InlineData("Q N H 1013", true)]
    [InlineData("Q1013", true)]
    [InlineData("QNH one zero one two", false)]
    public void Readback_QNH(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("qnh", AtcSpeech.Qnh(1013, false))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("altimeter two niner niner two", true)]
    [InlineData("altimeter two niner decimal niner two", true)]
    [InlineData("altimeter three zero zero one", false)]
    public void Readback_Altimeter(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("qnh", AtcSpeech.Qnh(1013.25, true))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("holding point sierra runway two five center", true)]
    [InlineData("holding point S, runway 25C", true)]
    [InlineData("taxi holding point runway two five center, squawk set", false)]
    public void Readback_kurze_Kennung_muss_als_Wort_kommen(string readback, bool correct)
    {
        var result = ReadbackChecker.Check(readback, [new ReadbackItem("holdingPoint", AtcSpeech.Ident("S"))]);
        Assert.Equal(correct, result.Correct);
    }

    [Theory]
    [InlineData("via RESNO", "entryFix", true)]
    [InlineData("speed two eight zero knots", "speedKt", true)]
    [InlineData("speed two seven zero knots", "speedKt", false)]
    [InlineData("position five five north two zero west", "fix", true)]
    [InlineData("position fifty five north twenty west", "fix", true)]
    [InlineData("position 55N020W", "fix", true)]
    [InlineData("position five five north three zero west", "fix", false)]
    public void Readback_Oceanic(string readback, string item, bool correct)
    {
        var expected = item switch
        {
            "entryFix" => AtcSpeech.Ident("RESNO"),
            "speedKt" => AtcSpeech.Speed(280),
            _ => AtcSpeech.Position(55, -20),
        };

        Assert.Equal(correct, ReadbackChecker.Check(readback, [new ReadbackItem(item, expected)]).Correct);
    }

    // ------------------------------------------------------------------ Plan

    [Fact]
    public void Kurzstrecke_hat_keinen_Oceanic_Teil()
    {
        var plan = AtcPlan.Build(ShortHaul(), null, Templates());

        Assert.Null(plan.Oceanic);
        Assert.Equal("DLH4AB", plan.Callsign);
        Assert.Equal("MARUN7G", plan.Sid);
        Assert.Equal("KETAP2B", plan.Star);
        Assert.Equal(new[] { "EDGG", "EDWW" }, plan.Firs.Select(f => f.Fir));
        Assert.All(plan.Firs, f => Assert.False(f.Oceanic));
        Assert.Equal(2, plan.DirectFix); // ERNAS – erster Fix nach der SID, TOC zählt nicht
    }

    [Fact]
    public void Nordatlantik_Shanwick_und_Gander_ohne_FIR_Angaben()
    {
        var ofp = Transatlantic();
        var plan = AtcPlan.Build(ofp, null, Templates());

        Assert.NotNull(plan.Oceanic);
        Assert.Equal("RESNO", ofp.Fixes[plan.Oceanic.EntryFix].Ident);
        Assert.Equal("LOMSI", ofp.Fixes[plan.Oceanic.ExitFix].Ident);
        Assert.Equal(new[] { "RESNO", "55N020W", "55N030W", "54N040W", "52N050W", "LOMSI" }, plan.Oceanic.ReportFixes.Select(i => ofp.Fixes[i].Ident));
        // Oceanic-Teil westlich 30°W ist Gander Oceanic (CZQO), ab dem Austrittspunkt LOMSI Gander Center (CZQX)
        Assert.Equal(new[] { "", "EGGX", "CZQO", "CZQX", "" }, plan.Firs.Select(f => f.Fir));
        Assert.Equal(new[] { false, true, true, false, false }, plan.Firs.Select(f => f.Oceanic));
        Assert.Equal(plan.FixDistanceNm(8)!.Value, plan.Firs[3].StartNm, 3);

        // Grenze zu Shanwick kurz vor RESNO, zu Gander zwischen 20°W und 30°W
        var resno = plan.FixDistanceNm(3)!.Value;
        Assert.InRange(plan.Firs[1].StartNm, resno - 1.5, resno);
        Assert.InRange(plan.Firs[2].StartNm, plan.FixDistanceNm(4)!.Value, plan.FixDistanceNm(5)!.Value);
    }

    [Fact]
    public void SimBrief_liefert_SID_STAR_und_FIR()
    {
        const string json = """
            {
              "fetch": { "status": "Success" },
              "general": { "initial_altitude": "35000", "sid_ident": "marun7g", "star_ident": "KETAP2B" },
              "origin": { "icao_code": "EDDF", "pos_lat": "50.03", "pos_long": "8.57" },
              "destination": { "icao_code": "EDDB", "pos_lat": "52.36", "pos_long": "13.50" },
              "navlog": { "fix": [
                { "ident": "MARUN", "type": "wpt", "pos_lat": "50.2", "pos_long": "9.0", "fir": "EDGG", "is_sid_star": "1" },
                { "ident": "EDDB", "type": "apt", "pos_lat": "52.36", "pos_long": "13.50", "fir": "eddw" }
              ] }
            }
            """;

        var ofp = OfpParser.Parse(json);

        Assert.Equal("MARUN7G", ofp.SidIdent);
        Assert.Equal("KETAP2B", ofp.StarIdent);
        Assert.Equal("EDGG", ofp.Fixes[0].Fir);
        Assert.Equal("EDDW", ofp.Fixes[1].Fir);
    }

    // ------------------------------------------------------------------ Stationen, Stimmen, Frequenzen

    [Fact]
    public void Stimmen_pro_Station_fest_verschieden_und_nicht_die_Callout_Stimme()
    {
        var config = Templates().Voices;
        var a = new AtcVoiceAssigner(config, "flight-1", "bm_george", null);
        string[] stations = ["EDDF/delivery", "EDDF/ground", "EDDF/tower", "EDDF/departure", "FIR/EDGG", "FIR/EDWW", "EDDB/approach", "EDDB/tower", "EDDB/ground"];
        var voices = stations.Select(s => a.VoiceFor(s, AtcStationType.Center)).ToList();

        Assert.Equal(voices.Count, voices.Distinct().Count());
        Assert.DoesNotContain("bm_george", voices);
        Assert.Equal(voices[2], a.VoiceFor("EDDF/tower", AtcStationType.Tower)); // fest im Flug

        // Gleicher Flug nach einem Neustart: gleiche Stimmen
        var again = new AtcVoiceAssigner(config, "flight-1", "bm_george", null);
        Assert.Equal(voices, stations.Select(s => again.VoiceFor(s, AtcStationType.Center)));
    }

    [Fact]
    public void Feste_Stimme_aus_den_Vorlagen_und_nur_installierte_Stimmen()
    {
        var config = new AtcVoiceConfig
        {
            Pool = ["bf_emma", "am_adam", "zz_unknown"],
            Stations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["EDDF/tower"] = "af_heart", ["delivery"] = "bm_daniel" },
        };
        var a = new AtcVoiceAssigner(config, "f", "bm_george", ["bf_emma", "af_heart", "bm_daniel"]);

        Assert.Equal("af_heart", a.VoiceFor("EDDF/tower", AtcStationType.Tower));
        Assert.Equal("bm_daniel", a.VoiceFor("EDDB/delivery", AtcStationType.Delivery));
        Assert.Equal("bf_emma", a.VoiceFor("EDDF/ground", AtcStationType.Ground)); // am_adam ist nicht installiert
    }

    [Fact]
    public void Frequenz_aus_Facilities_API_sonst_Platzhalter()
    {
        var templates = Templates();
        var book = new AtcFrequencyBook();
        var stations = new AtcStations(templates, book.Get, new AtcVoiceAssigner(templates.Voices, "f", null, null));

        var before = stations.Airport("EDDF", "Frankfurt/Main International", AtcStationType.Tower);
        Assert.Equal("placeholder", before.FrequencySource);
        Assert.Equal("118.7", before.Frequency.Written);
        Assert.Equal("Frankfurt Tower", before.Name);

        book.Set("EDDF", [new AirportFrequency(FacilityFrequencyType.Ground, 121.8, "GROUND"), new AirportFrequency(FacilityFrequencyType.Tower, 119.9, "TOWER")]);
        var after = stations.Airport("EDDF", null, AtcStationType.Tower);
        Assert.Equal("facility", after.FrequencySource);
        Assert.Equal("119.9", after.Frequency.Written);
        Assert.Equal(before.Voice, after.Voice); // Stimme bleibt

        var delivery = stations.Airport("EDDF", null, AtcStationType.Delivery);
        Assert.Equal("placeholder", delivery.FrequencySource); // keine Clearance-Frequenz in den Daten
        Assert.Equal("Berlin", stations.AirportName("EDDB", "Berlin Brandenburg International Airport"));
    }

    // ------------------------------------------------------------------ Zustandsmaschine

    [Fact]
    public void Delivery_falscher_Squawk_wird_korrigiert_dann_Ground()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        Assert.Equal(AtcPhase.Delivery, session.Phase);
        Assert.Equal("Frankfurt Delivery", session.Station.Name);
        Assert.Contains("Frankfurt Delivery, DLH4AB, request IFR clearance to EDDB", session.ExpectedPilotCalls(flight.Input).Select(c => c.Text));

        var clearance = Assert.Single(flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin"));
        var squawk = session.Squawk;
        Assert.Equal($"DLH4AB, cleared to EDDB, MARUN7G departure, runway 25C, climb 5000 ft, squawk {squawk}", clearance.Text);
        Assert.Equal(
            $"Lufthansa four alpha bravo, cleared to Berlin, Marun seven golf departure, runway two five center, climb five thousand feet, squawk {AtcSpeech.Spell(squawk)}",
            clearance.Spoken);
        Assert.Equal(AtcStep.ClearanceReadback, session.Step);
        Assert.NotEqual("bm_george", clearance.Voice);

        var wrong = WrongSquawk(squawk);
        var negative = Assert.Single(flight.Say($"cleared to Berlin, MARUN seven golf departure, runway two five center, climb five thousand feet, squawk {AtcSpeech.Spell(wrong)}, Lufthansa four alpha bravo"));
        Assert.Equal($"DLH4AB, negative, squawk {squawk}", negative.Text);
        Assert.Equal(AtcPhase.Delivery, session.Phase);
        Assert.Equal(new[] { "squawk" }, session.PendingReadback!.Items.Select(i => i.Name));

        var correct = Assert.Single(flight.Say($"squawk {AtcSpeech.Spell(squawk)}, Lufthansa four alpha bravo"));
        Assert.Equal("DLH4AB, readback correct, contact Ground 121.8", correct.Text);
        Assert.Equal(AtcPhase.GroundDeparture, session.Phase);
        Assert.Equal(AtcStep.PushRequest, session.Step);
        Assert.Equal("Frankfurt Ground", session.Station.Name);

        // Quittung der Übergabe – ATC antwortet nicht
        Assert.Empty(flight.Say("contact Ground one two one decimal eight, Lufthansa four alpha bravo"));
    }

    [Fact]
    public void Squawk_ist_pro_Flug_fest_und_gueltig()
    {
        var a = AtcSession.GenerateSquawk("flight-1");
        Assert.Equal(a, AtcSession.GenerateSquawk("flight-1"));
        Assert.Matches("^[1-6][0-7]{3}$", a);
        Assert.DoesNotContain(Enumerable.Range(0, 300).Select(i => AtcSession.GenerateSquawk("f" + i)), s => s is "7500" or "7600" or "7700" or "2000" or "1200" || s.EndsWith("00", StringComparison.Ordinal));
    }

    [Fact]
    public void Kompletter_Flug_Kurzstrecke_ohne_Oceanic()
    {
        var session = Session(ShortHaul());
        session.SetOverride("gate", "A26");
        var flight = new Flight(session);
        flight.Start();
        var squawk = AtcSpeech.Spell(session.Squawk);

        // Delivery
        flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin");
        flight.Say($"cleared to Berlin, MARUN seven golf, climb five thousand feet, squawk {squawk}");
        Assert.Equal(AtcPhase.GroundDeparture, session.Phase);

        // Ground: Pushback ohne Richtung (keine Rollroute), dann Rollen
        Assert.Equal("DLH4AB, push and start approved", Assert.Single(flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, gate alpha two six, request push and start")).Text);
        Assert.Empty(flight.Say("push and start approved, Lufthansa four alpha bravo"));
        Assert.Equal("DLH4AB, taxi to holding point runway 25C", Assert.Single(flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, request taxi")).Text);
        Assert.Empty(flight.Say("taxi holding point runway two five center, Lufthansa four alpha bravo"));
        Assert.Equal(AtcStep.Taxiing, session.Step);

        flight.Run(60, s => s with { GroundSpeedKt = 15, ParkingBrake = false });
        var handoffTower = Assert.Single(flight.Run(10, s => s with { GroundSpeedKt = 0 }));
        Assert.Equal("DLH4AB, contact Tower 118.7", handoffTower.Text);
        Assert.Equal(AtcPhase.TowerDeparture, session.Phase);
        Assert.Equal("Frankfurt Tower", session.Station.Name);

        // Tower: Line-up, Startfreigabe automatisch, sobald das Flugzeug auf der Bahn steht
        Assert.Equal("DLH4AB, line up and wait runway 25C",
            Assert.Single(flight.Say("Frankfurt Tower, Lufthansa four alpha bravo, holding point runway two five center, ready for departure")).Text);
        Assert.Empty(flight.Say("line up and wait runway two five center, Lufthansa four alpha bravo"));
        flight.Run(10, s => s with { GroundSpeedKt = 6 });
        var takeoff = Assert.Single(flight.Run(5, s => s with { GroundSpeedKt = 0 }));
        Assert.Equal("DLH4AB, wind 250/12, runway 25C, cleared for takeoff", takeoff.Text);
        Assert.Equal("Lufthansa four alpha bravo, wind two five zero degrees, one two knots, runway two five center, cleared for takeoff", takeoff.Spoken);
        Assert.Empty(flight.Say("cleared for takeoff runway two five center, Lufthansa four alpha bravo"));

        // Abheben: Übergabe an Departure ab 1500 ft AGL
        flight.Run(20, s => s with { GroundSpeedKt = 150, IasKt = 150 });
        Assert.Empty(flight.Run(2, s => s with { OnGround = false, AglFt = 800, AltitudeFt = 1200 }));
        var toDeparture = Assert.Single(flight.Run(2, s => s with { AglFt = 1600, AltitudeFt = 2000 }));
        Assert.Equal("DLH4AB, contact Departure 124.35", toDeparture.Text);
        Assert.Equal("Frankfurt Departure", session.Station.Name);

        // Departure: identifiziert, Steigflug FL150, Direct
        Assert.Equal("DLH4AB, identified, climb FL150",
            Assert.Single(flight.Say("Frankfurt Departure, Lufthansa four alpha bravo, passing two thousand, climbing five thousand feet")).Text);
        Assert.Empty(flight.Say("climb flight level one five zero, Lufthansa four alpha bravo"));
        Assert.Equal(15000, session.ClearedAltitudeFt);
        var direct = Assert.Single(flight.FlyTo(0.15, 8000, gs: 300, ias: 250, vs: 2000));
        Assert.Equal("DLH4AB, proceed direct ERNAS", direct.Text);
        Assert.Equal("Lufthansa four alpha bravo, proceed direct Ernas", direct.Spoken);
        Assert.Empty(flight.Say("direct ERNAS, Lufthansa four alpha bravo"));

        // Center Langen: Steigflug auf Reiseflughöhe
        var toCenter = Assert.Single(flight.FlyTo(0.2, 13500, gs: 380, ias: 280, vs: 2000));
        Assert.Equal("DLH4AB, contact Langen Radar 132.6", toCenter.Text);
        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal("DLH4AB, climb FL350",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, passing flight level one three five, climbing flight level one five zero")).Text);
        Assert.Empty(flight.Say("climb flight level three five zero, Lufthansa four alpha bravo"));

        // FIR-Wechsel Langen → Bremen
        Assert.Empty(flight.FlyTo(0.4, 35000));
        var toBremen = Assert.Single(flight.FlyTo(0.55, 35000));
        Assert.Equal("DLH4AB, contact Bremen Radar 132.6", toBremen.Text);
        Assert.Equal("DLH4AB, identified, maintain FL350", Assert.Single(flight.Say("Bremen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero")).Text);

        // Top of Descent: mit STAR "descend via", unter der Übergangshöhe mit QNH
        var descend = Assert.Single(flight.FlyTo(0.64, 35000));
        Assert.Equal("DLH4AB, descend via KETAP2B arrival, Q1013", descend.Text);
        Assert.Equal("Lufthansa four alpha bravo, descend via Ketap two bravo arrival, Q N H one zero one three", descend.Spoken);
        Assert.Equal(5000, session.ClearedAltitudeFt);
        Assert.Empty(flight.Say("descend via KETAP two bravo arrival, QNH one zero one three, Lufthansa four alpha bravo"));

        // Approach ab 40 nm vor dem Ziel
        var toApproach = Assert.Single(flight.FlyTo(0.85, 12000, gs: 300, ias: 250, vs: -1500));
        Assert.Equal("DLH4AB, contact Berlin Approach 119.1", toApproach.Text);
        var approach = Assert.Single(flight.Say("Berlin Approach, Lufthansa four alpha bravo, descending flight level one one zero"));
        Assert.Equal("DLH4AB, descend 5000 ft, KETAP2B arrival, expect ILS runway 25R, Q1013", approach.Text);
        Assert.Equal(
            "Lufthansa four alpha bravo, descend five thousand feet, Ketap two bravo arrival, expect I L S runway two five right, Q N H one zero one three",
            approach.Spoken);

        // Falsches Readback der Bahn
        Assert.Equal("DLH4AB, negative, runway 25R",
            Assert.Single(flight.Say("descend five thousand feet, KETAP two bravo, ILS runway two five left, QNH one zero one three")).Text);
        Assert.Empty(flight.Say("runway two five right, Lufthansa four alpha bravo"));
        Assert.Equal(AtcStep.ApproachVectoring, session.Step);

        // Anflugfreigabe ab 20 nm, Tower ab 12 nm
        Assert.Equal("DLH4AB, cleared ILS runway 25R", Assert.Single(flight.FlyTo(0.93, 5000, gs: 200, ias: 180, vs: -800)).Text);
        Assert.Empty(flight.Say("cleared ILS runway two five right, Lufthansa four alpha bravo"));
        Assert.Equal("DLH4AB, contact Tower 118.7", Assert.Single(flight.FlyTo(0.96, 3000, gs: 150, ias: 140, vs: -700)).Text);
        Assert.Equal("Berlin Tower", session.Station.Name);

        // Landung
        Assert.Equal("DLH4AB, wind 250/12, runway 25R, cleared to land",
            Assert.Single(flight.Say("Berlin Tower, Lufthansa four alpha bravo, ILS runway two five right")).Text);
        Assert.Empty(flight.Say("cleared to land runway two five right, Lufthansa four alpha bravo"));
        flight.Run(5, s => s with { OnGround = true, Lat = BerLat, Lon = BerLon, AltitudeFt = 157, AglFt = 0, GroundSpeedKt = 120, IasKt = 120, VerticalSpeedFpm = 0 });
        var vacate = Assert.Single(flight.Run(2, s => s with { GroundSpeedKt = 30, IasKt = 30 }));
        Assert.Equal("DLH4AB, vacate via next exit, contact Ground 121.8", vacate.Text);
        Assert.Equal(AtcPhase.GroundArrival, session.Phase);

        // Rollen zum Gate (von Hand gesetzt)
        Assert.Equal("DLH4AB, taxi to gate A26",
            Assert.Single(flight.Say("Berlin Ground, Lufthansa four alpha bravo, runway two five right vacated")).Text);
        Assert.Empty(flight.Say("taxi to gate alpha two six, Lufthansa four alpha bravo"));
        flight.Run(30, s => s with { GroundSpeedKt = 12 });
        flight.Run(15, s => s with { GroundSpeedKt = 0, ParkingBrake = true });
        Assert.Equal(AtcPhase.Done, session.Phase);

        // Nie Oceanic, jede Station eine eigene Stimme, nie die Callout-Stimme
        Assert.Equal(OceanicClearanceState.NotRequired, session.Oceanic);
        var atcVoices = flight.Heard.Where(t => t.From == "atc").GroupBy(t => t.StationKey).ToDictionary(g => g.Key, g => g.Select(t => t.Voice).Distinct().Single());
        Assert.True(atcVoices.Count >= 8);
        Assert.Equal(atcVoices.Count, atcVoices.Values.Distinct().Count());
        Assert.DoesNotContain("bm_george", atcVoices.Values);
        Assert.DoesNotContain(flight.Heard, t => t.Spoken.Contains("mach", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rollroute_kommt_ueber_die_Schnittstelle()
    {
        var session = Session(ShortHaul(), new FixedTaxiRoutes());
        var flight = new Flight(session);
        flight.Start();
        flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin");
        flight.Say($"MARUN seven golf, climb five thousand feet, squawk {AtcSpeech.Spell(session.Squawk)}");

        Assert.Equal("DLH4AB, push and start approved, facing east", Assert.Single(flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, request push and start")).Text);
        var taxi = Assert.Single(flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, request taxi"));
        Assert.Equal("DLH4AB, taxi to holding point S runway 25C via N, N3", taxi.Text);
        Assert.Equal("Lufthansa four alpha bravo, taxi to holding point sierra runway two five center via november, november three", taxi.Spoken);
        Assert.Equal(new[] { "holdingPoint", "runway" }, session.PendingReadback!.Items.Select(i => i.Name));

        Assert.Equal("DLH4AB, negative, holding point S",
            Assert.Single(flight.Say("taxi holding point runway two five center via november, november three")).Text);
        Assert.Empty(flight.Say("holding point sierra"));
        Assert.Equal(AtcStep.Taxiing, session.Step);
    }

    [Fact]
    public void Ohne_Pushback_gleich_rollen_und_Tower_direkt_rufen()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin");
        flight.Say($"MARUN seven golf, climb five thousand feet, squawk {AtcSpeech.Spell(session.Squawk)}");

        Assert.Equal("DLH4AB, taxi to holding point runway 25C", Assert.Single(flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, request taxi")).Text);
        flight.Run(15, s => s with { GroundSpeedKt = 15, ParkingBrake = false });
        Assert.Equal(AtcStep.Taxiing, session.Step); // rollt – Readback gilt als erfolgt

        Assert.Equal("DLH4AB, line up and wait runway 25C",
            Assert.Single(flight.Say("Frankfurt Tower, Lufthansa four alpha bravo, ready for departure runway two five center")).Text);
        Assert.Equal(AtcPhase.TowerDeparture, session.Phase);
    }

    [Fact]
    public void Ohne_Freigaben_gestartet_springt_ATC_nach()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        Assert.Equal(AtcPhase.Delivery, session.Phase);

        flight.Run(3, s => s with { GroundSpeedKt = 140, IasKt = 140, ParkingBrake = false });
        Assert.Equal(AtcPhase.TowerDeparture, session.Phase);
        Assert.Equal(AtcStep.TakeoffRoll, session.Step);

        var handoff = Assert.Single(flight.Run(2, s => s with { OnGround = false, AglFt = 1700, AltitudeFt = 2100 }));
        Assert.Equal("DLH4AB, contact Departure 124.35", handoff.Text);
    }

    [Fact]
    public void Einstieg_in_der_Luft_bei_der_passenden_Stelle()
    {
        var session = Session(ShortHaul());
        var (lat, lon) = Along(0.55);
        session.Start(new AtcInput { TimeUtc = T0, OnGround = false, Lat = lat, Lon = lon, AltitudeFt = 35000, AglFt = 34500, GroundSpeedKt = 450 });

        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal("Bremen Radar", session.Station.Name);
        Assert.Equal(35000, session.ClearedAltitudeFt);
    }

    [Fact]
    public void Say_again_wiederholt_den_letzten_Funkspruch()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        var clearance = Assert.Single(flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin"));

        var repeat = Assert.Single(flight.Say("Say again, Lufthansa four alpha bravo"));
        Assert.Equal(clearance.Text, repeat.Text);
        Assert.Equal(clearance.Voice, repeat.Voice);
        Assert.Equal(AtcStep.ClearanceReadback, session.Step);
    }

    [Fact]
    public void Oceanic_Freigabe_Positionsmeldungen_und_Uebergaben()
    {
        var ofp = Transatlantic();
        var session = Session(ofp);
        var plan = session.Plan;
        var points = plan.Points;

        // Position auf der Route bei einer Streckenposition (nm)
        (double Lat, double Lon) At(double nm)
        {
            for (var i = 1; i < points.Count; i++)
            {
                if (plan.PointDistanceNm[i] >= nm)
                {
                    var a = points[i - 1];
                    var b = points[i];
                    var f = (nm - plan.PointDistanceNm[i - 1]) / (plan.PointDistanceNm[i] - plan.PointDistanceNm[i - 1]);
                    return (a.Lat + (b.Lat - a.Lat) * f, a.Lon + (b.Lon - a.Lon) * f);
                }
            }

            return (points[^1].Lat, points[^1].Lon);
        }

        var now = T0;
        var resno = plan.FixDistanceNm(3)!.Value;
        IReadOnlyList<AtcTransmission> TickAt(double nm)
        {
            var (lat, lon) = At(nm);
            now = now.AddSeconds(1);
            return session.Tick(Input(lat, lon));
        }

        AtcInput Input(double lat, double lon) => new()
        {
            TimeUtc = now,
            OnGround = false,
            Lat = lat,
            Lon = lon,
            AltitudeFt = 35000,
            AglFt = 34500,
            GroundSpeedKt = 450,
            IasKt = 280,
            VerticalSpeedFpm = 0,
            ZuluTimeSec = 12 * 3600,
        };

        var (startLat, startLon) = At(resno - 400);
        session.Start(Input(startLat, startLon));
        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal(OceanicClearanceState.NotYetDue, session.Oceanic);

        // 30 Minuten vor RESNO (450 kt → 225 nm): Freigabe anfordern
        Assert.Empty(TickAt(resno - 300));
        Assert.Equal(OceanicClearanceState.NotYetDue, session.Oceanic);
        TickAt(resno - 200);
        Assert.Equal(OceanicClearanceState.Due, session.Oceanic);
        var request = session.ExpectedPilotCalls(Input(At(resno - 200).Lat, At(resno - 200).Lon)).First();
        Assert.StartsWith("Shanwick Oceanic, DLH400, request oceanic clearance, estimating RESNO at ", request.Text);
        Assert.EndsWith(", requesting FL350, speed 280 knots", request.Text);

        var clearance = Assert.Single(session.PilotTransmission(
            "Shanwick, Lufthansa four zero zero, request oceanic clearance, estimating RESNO at one two three zero, requesting flight level three seven zero, speed two eight zero knots",
            Input(At(resno - 200).Lat, At(resno - 200).Lon)));
        Assert.Equal("DLH400, cleared to KJFK via RESNO, 55N020W 55N030W 54N040W 52N050W LOMSI, maintain FL370, speed 280 knots", clearance.Text);
        Assert.Contains("fifty five north twenty west", clearance.Spoken, StringComparison.Ordinal);
        Assert.Contains("speed two eight zero knots", clearance.Spoken, StringComparison.Ordinal);
        Assert.Equal("Shanwick Oceanic", clearance.StationName);
        Assert.Equal(OceanicClearanceState.ReadbackPending, session.Oceanic);

        var negative = Assert.Single(session.PilotTransmission(
            "cleared to Kennedy via RESNO, five five north two zero west, maintain flight level three five zero, speed two eight zero knots",
            Input(At(resno - 199).Lat, At(resno - 199).Lon)));
        Assert.Equal("DLH400, negative, climb FL370", negative.Text);
        var correct = Assert.Single(session.PilotTransmission("flight level three seven zero, Lufthansa four zero zero", Input(At(resno - 198).Lat, At(resno - 198).Lon)));
        Assert.Equal("DLH400, readback correct", correct.Text);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);
        Assert.Equal(AtcPhase.Center, session.Phase); // die Freigabe ändert die zuständige Stelle nicht

        // Eintritt bei RESNO: Übergabe an Shanwick, Positionsmeldung fällig
        var toShanwick = Assert.Single(TickAt(resno - 0.5));
        Assert.Equal("DLH400, contact Shanwick Oceanic 127.65", toShanwick.Text);
        Assert.Equal(AtcPhase.Oceanic, session.Phase);
        Assert.Empty(TickAt(resno));
        var report = session.ExpectedPilotCalls(Input(At(resno).Lat, At(resno).Lon)).Single();
        Assert.StartsWith("Shanwick Oceanic, DLH400, position RESNO at 1200Z, FL350, estimating 55N020W at ", report.Text);
        Assert.Equal("DLH400, roger", Assert.Single(session.PilotTransmission(report.Text, Input(At(resno).Lat, At(resno).Lon))).Text);

        // 30°W: Gander, nach LOMSI: zurück an Domestic Center
        var w30 = plan.FixDistanceNm(5)!.Value;
        var toGander = Assert.Single(TickAt(w30));
        Assert.Equal("DLH400, contact Gander Oceanic 127.65", toGander.Text);
        Assert.Equal(AtcPhase.Oceanic, session.Phase);

        var lomsi = plan.FixDistanceNm(8)!.Value;
        var exit = Assert.Single(TickAt(lomsi + 5));
        Assert.StartsWith("DLH400, contact ", exit.Text);
        Assert.Equal(AtcPhase.Center, session.Phase);
    }
}
