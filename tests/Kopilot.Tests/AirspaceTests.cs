using Kopilot.Atc;
using Kopilot.Atc.Airspace;
using Kopilot.FlightPlan;

namespace Kopilot.Tests;

/// <summary>
/// Luftraumdaten im Kopilot-ATC: FIR-Grenzen aus VAT-Spy, Sektoren aus den VATSIM-Sektordaten, Gander Domestic,
/// Rufzeichen aus OpenFlights und das Anheben alter Vorlagen. Referenzpunkte aus dem Testflug CFG2205 (docs/testlog-0.9.md).
/// </summary>
public sealed class AirspaceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 21, 0, 0, TimeSpan.Zero);

    private static OfpFix Fix(string ident, double lat, double lon, string? fir, string type = "wpt") => new()
    {
        Ident = ident,
        Lat = lat,
        Lon = lon,
        Type = type,
        Fir = fir,
    };

    /// <summary>EDDF–KLAX wie im Testflug (Wegpunkte gerundet), FIRs so, wie SimBrief sie liefert (Gander-Oceanic-Fixe als CZQX).</summary>
    private static Ofp Cfg2205() => new()
    {
        RequestId = "cfg2205-airspace",
        Callsign = "DE2205",
        FlightNumber = "CFG2205",
        Origin = new OfpAirport("EDDF", "Frankfurt/Main International", "25C", 50.0333, 8.5706, 364, 5000, 6000, null),
        Destination = new OfpAirport("KLAX", "Los Angeles International", "24R", 33.9425, -118.4081, 128, 18000, 18000, null),
        InitialAltitudeFt = 37000,
        Fixes =
        [
            Fix("TOBAK", 50.15, 8.25, "EDGG"),
            Fix("TOC", 50.60, 7.50, "EDGG", type: "ltlg"),
            Fix("GOW", 55.87, -4.43, "EGPX"),
            Fix("ETARI", 56.00, -15.00, "EGPX"),
            Fix("56N020W", 56.00, -20.00, "EGGX", type: "ltlg"),
            Fix("57N030W", 57.00, -30.00, "CZQX", type: "ltlg"),
            Fix("57N040W", 57.00, -40.00, "CZQX", type: "ltlg"),
            Fix("56N050W", 56.00, -50.00, "CZQX", type: "ltlg"),
            Fix("HOIST", 55.00, -57.50, "CZQX"),
            Fix("YWK", 52.92, -66.86, "CZUL"),
            Fix("YTS", 48.57, -81.38, "CZYZ"),
            Fix("SAW", 46.35, -87.39, "KZMP"),
            Fix("EAU", 44.90, -91.48, "KZMP"),
            Fix("ONL", 42.47, -98.69, "KZMP"),
            Fix("FQF", 39.69, -104.62, "KZDV"),
            Fix("DVC", 37.81, -108.93, "KZDV"),
            Fix("PGA", 36.93, -111.45, "KZDV"),
            Fix("HEC", 34.80, -116.46, "KZLA"),
            Fix("TOD", 34.30, -117.30, "KZLA", type: "ltlg"),
            Fix("KLAX", 33.9425, -118.4081, "KZLA", type: "apt"),
        ],
    };

    private const int W030 = 5;
    private const int Hoist = 8;
    private const int Ywk = 9;

    private static AtcPlan Plan(Ofp? ofp = null, AtcTemplates? templates = null) =>
        AtcPlan.Build(ofp ?? Cfg2205(), null, templates ?? TestData.Templates(), TestData.Airspace);

    private static (double Lat, double Lon) PositionAt(AtcPlan plan, double nm)
    {
        for (var i = 1; i < plan.Points.Count; i++)
        {
            if (plan.PointDistanceNm[i] >= nm)
            {
                var a = plan.Points[i - 1];
                var b = plan.Points[i];
                var bearing = RouteMath.BearingDeg(a.Lat, a.Lon, b.Lat, b.Lon);
                return RouteMath.Destination(a.Lat, a.Lon, bearing, Math.Max(0, nm - plan.PointDistanceNm[i - 1]));
            }
        }

        return (plan.Points[^1].Lat, plan.Points[^1].Lon);
    }

    /// <summary>Entfernung des Beginns eines Abschnitts von einem Referenzpunkt (nm).</summary>
    private static double MissNm(AtcPlan plan, AtcFirSegment segment, double lat, double lon)
    {
        var (a, b) = PositionAt(plan, segment.StartNm);
        return RouteMath.DistanceNm(a, b, lat, lon);
    }

    private static AtcInput Cruise(AtcPlan plan, double nm, DateTimeOffset time)
    {
        var (lat, lon) = PositionAt(plan, nm);
        return new AtcInput
        {
            TimeUtc = time,
            OnGround = false,
            Lat = lat,
            Lon = lon,
            AltitudeFt = 37000,
            AglFt = 36500,
            GroundSpeedKt = 450,
            IasKt = 270,
            VerticalSpeedFpm = 0,
            ZuluTimeSec = 21 * 3600,
        };
    }

    private static AtcSession Session(AtcPlan plan, AtcTemplates templates) =>
        new(templates, plan, new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));

    // ------------------------------------------------------------------ VAT-Spy

    [Fact]
    public void VAT_Spy_im_Originalformat()
    {
        var airspace = TestData.Airspace;

        Assert.Equal("Gander", airspace.Fir("CZQX")!.Name);
        Assert.Equal("Montreal", airspace.Fir("czul")!.Name);
        Assert.True(airspace.HasBoundary("CZQO"));
        Assert.True(airspace.HasBoundary("KZMP"));
        Assert.Null(airspace.Fir("CZQO-GOTA")); // Unterteilung, keine eigene FIR
        Assert.Null(airspace.Fir("EDVV")); // UIR – VAT-Spy kennt sie nicht, die Sektoren entscheiden
        Assert.Equal("CZQO", airspace.SmallestFirAt(57, -40)!.Id); // nicht die NAT-Überlagerung
        Assert.Contains(airspace.Sources, s => s.Contains("VAT-Spy", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 1. FIR-Grenzen aus VAT-Spy

    [Fact]
    public void Uebergabe_Gander_an_Montreal_an_der_VAT_Spy_Grenze()
    {
        var plan = Plan();
        var montreal = plan.Firs.First(f => f.Fir == "CZUL");

        // MSFS-ATC übergab bei 53,51 N / 64,65 W – Kopilot 0.9 schon bei HOIST, ca. 260 nm zu früh
        Assert.InRange(MissNm(plan, montreal, 53.51, -64.65), 0, 15);
        Assert.InRange(montreal.StartNm - plan.FixDistanceNm(Hoist)!.Value, 240, 290);
        Assert.True(montreal.StartNm < plan.FixDistanceNm(Ywk)!.Value);
    }

    [Theory]
    [InlineData("CZYZ", 49.49, -79.02, 15)] // Montreal → Toronto
    [InlineData("KZMP", 46.87, -86.12, 10)] // Toronto → Minneapolis über dem Lake Superior
    [InlineData("KZDV", 42.25, -99.19, 10)] // Minneapolis → Denver nach ONL
    [InlineData("KZLA", 36.85, -111.69, 10)] // Denver → Los Angeles bei PGA
    public void Weitere_FIR_Grenzen_wie_beim_MSFS_ATC(string fir, double lat, double lon, double toleranceNm)
    {
        var plan = Plan();
        Assert.InRange(MissNm(plan, plan.Firs.First(f => f.Fir == fir), lat, lon), 0, toleranceNm);
    }

    [Fact]
    public void Shanwick_an_Gander_genau_bei_30_West()
    {
        var plan = Plan();
        var gander = plan.Firs.First(f => f.Fir == "CZQO");

        Assert.True(gander.Oceanic);
        Assert.Equal(plan.FixDistanceNm(W030)!.Value, gander.StartNm, 3);
    }

    [Fact]
    public void Ohne_FIR_Angaben_im_OFP_kommen_die_FIRs_aus_VAT_Spy()
    {
        var ofp = Cfg2205() with
        {
            Origin = new OfpAirport("EGLL", "London Heathrow", "27R", 51.4700, -0.4543, 83, 6000, 7000, null),
            Destination = new OfpAirport("KJFK", "John F Kennedy International", "22L", 40.6398, -73.7789, 13, 18000, 18000, null),
            Fixes =
            [
                Fix("CPT", 51.49, -1.22, null),
                Fix("STU", 51.99, -4.85, null),
                Fix("RESNO", 55.00, -15.00, null),
                Fix("55N020W", 55.00, -20.00, null, "ltlg"),
                Fix("55N030W", 55.00, -30.00, null, "ltlg"),
                Fix("54N040W", 54.00, -40.00, null, "ltlg"),
                Fix("52N050W", 52.00, -50.00, null, "ltlg"),
                Fix("LOMSI", 50.00, -54.00, null),
                Fix("TOD", 42.50, -69.00, null, "ltlg"),
                Fix("KJFK", 40.6398, -73.7789, null, "apt"),
            ],
        };

        // Nur die FIR-Grenzen, ohne Sektoren – hier geht es um die Zuordnung der Fixe
        var plan = AtcPlan.Build(ofp, null, TestData.Templates(), new AirspaceDatabase(TestData.Airspace.Firs, []));
        var firs = plan.Firs.Select(f => f.Fir).Distinct().ToList();

        Assert.Equal("EGTT", firs[0]); // London (Vorlage) vor kleineren VAT-Spy-Teilgebieten
        Assert.Contains("EGGX", firs);
        Assert.Contains("CZQO", firs);
        Assert.Equal("KZBW", firs[^1]);
        Assert.DoesNotContain("", firs);
    }

    // ------------------------------------------------------------------ 2. Sektoren

    [Fact]
    public void Sektoren_mit_eigenen_Frequenzen_und_Uebergaben_innerhalb_der_FIR()
    {
        var templates = TestData.Templates();
        var plan = Plan(templates: templates);
        var montreal = plan.Firs.Where(f => f.Fir == "CZUL").ToList();

        Assert.True(montreal.Count >= 2, string.Join(", ", montreal));
        Assert.All(montreal, f => Assert.NotNull(f.FrequencyMhz));
        Assert.True(montreal.Select(f => f.FrequencyMhz).Distinct().Count() >= 2);

        var session = Session(plan, templates);
        var now = T0;
        session.Start(Cruise(plan, montreal[0].StartNm + 5, now));
        Assert.Equal("Montreal Center", session.Station.Name);
        Assert.Equal("sectors", session.Station.FrequencySource);
        Assert.Equal(AtcSpeech.Frequency(montreal[0].FrequencyMhz!.Value).Written, session.Station.Frequency.Written);

        var handoff = Assert.Single(session.Tick(Cruise(plan, montreal[1].StartNm + 1, now.AddSeconds(1))));
        Assert.Equal($"CFG2205, contact Montreal Center {AtcSpeech.Frequency(montreal[1].FrequencyMhz!.Value).Written}", handoff.Text);
        Assert.Equal($"FIR/CZUL/{montreal[1].Sector}", session.Station.Key);
        Assert.Equal(AtcPhase.Center, session.Phase);
    }

    [Fact]
    public void UIR_ohne_Grenze_nimmt_die_Sektoren_Maastricht_oder_Bremen()
    {
        var ofp = Cfg2205() with
        {
            Destination = new OfpAirport("EDDH", "Hamburg", "23", 53.6304, 9.9882, 53, 5000, 6000, null),
            InitialAltitudeFt = 35000,
            Fixes =
            [
                Fix("TOBAK", 50.15, 8.25, "EDGG"),
                Fix("AAA", 51.50, 8.50, "EDVV"),
                Fix("BBB", 52.40, 9.70, "EDVV"),
                Fix("CCC", 53.30, 10.50, "EDVV"),
                Fix("EDDH", 53.6304, 9.9882, "EDWW", type: "apt"),
            ],
        };

        var templates = TestData.Templates();
        var plan = Plan(ofp, templates);
        var stations = new AtcStations(templates, _ => null, new AtcVoiceAssigner(templates.Voices, "edvv", null, null), plan.Airspace);
        var upper = plan.Firs.Where(f => f.StartNm >= plan.FixDistanceNm(1)!.Value - 30).ToList();

        Assert.NotEmpty(upper);
        Assert.All(upper, f => Assert.NotNull(f.FrequencyMhz));
        Assert.All(upper, f => Assert.Contains(stations.SegmentName(f), new[] { "Maastricht Radar", "Bremen Radar", "Rhein Radar", "Langen Radar" }));
        Assert.Contains(upper, f => stations.SegmentName(f) == "Maastricht Radar");
    }

    [Fact]
    public void Hysterese_kurze_Sektoren_entlang_der_Route_pendeln_nicht()
    {
        static GeoRing Box(double west, double east, double south = 42, double north = 44) =>
            new([west, south, east, south, east, north, west, north]);

        static AirspaceSector Sector(string position, double mhz, double west, double east) =>
            new("KZMP", position, position, "Minneapolis Center", mhz, 0, 600, Box(west, east));

        // Westwärts entlang 43°N: 127.1 → 132.05 (13 nm) → 124.1 (4 nm) → 132.05 (9 nm) → 128.0, wie bei Worthington im Testflug
        var airspace = new AirspaceDatabase(
            [
                new AirspaceFir("KZMP", "Minneapolis", [new GeoPolygon(Box(-100, -90, 38, 48))]),
                new AirspaceFir("KZDV", "Denver", [new GeoPolygon(Box(-110, -100, 38, 48))]),
            ],
            [
                Sector("EAST", 127.1, -96.0, -90),
                Sector("MID", 132.05, -96.3, -96.0),
                Sector("PING", 124.1, -96.4, -96.3),
                Sector("MID", 132.05, -96.6, -96.4),
                Sector("WEST", 128.0, -100, -96.6),
            ]);

        var ofp = new Ofp
        {
            RequestId = "hysterese",
            FlightNumber = "CFG2205",
            Origin = new OfpAirport("KXXX", "Origin", null, 43, -91, 1000, 18000, 18000, null),
            Destination = new OfpAirport("KYYY", "Destination", null, 43, -105, 1000, 18000, 18000, null),
            InitialAltitudeFt = 36000,
            Fixes =
            [
                Fix("AAA", 43, -92, "KZMP"),
                Fix("BBB", 43, -99, "KZMP"),
                Fix("CCC", 43, -104, "KZDV"),
                Fix("KYYY", 43, -105, "KZDV", type: "apt"),
            ],
        };

        var templates = TestData.Templates();
        var plan = AtcPlan.Build(ofp, null, templates, airspace);
        Assert.Equal(new double?[] { 127.1, 132.05, 128.0 }, plan.Firs.Where(f => f.Fir == "KZMP").Select(f => f.FrequencyMhz));

        // Grenze zu Denver bei 100°W, dort ohne Sektoren: Frequenz aus der Vorlage
        var denver = plan.Firs.Single(f => f.Fir == "KZDV");
        Assert.Null(denver.Sector);
        var (_, lon) = PositionAt(plan, denver.StartNm);
        Assert.InRange(lon, -100.05, -99.95);

        // Ohne Mindestlänge pendelt die Übergabe wie beim MSFS-ATC
        templates.Rules.SectorMinNm = 0;
        var pingPong = AtcPlan.Build(ofp, null, templates, airspace);
        Assert.Equal(new double?[] { 127.1, 132.05, 124.1, 132.05, 128.0 }, pingPong.Firs.Where(f => f.Fir == "KZMP").Select(f => f.FrequencyMhz));
    }

    // ------------------------------------------------------------------ 3. Gander Domestic

    [Fact]
    public void Gander_Center_nach_dem_Oceanic_Ausgang_ohne_Positionsmeldung()
    {
        var templates = TestData.Templates();
        var plan = Plan(templates: templates);
        var hoist = plan.FixDistanceNm(Hoist)!.Value;
        var oceanicIndex = plan.Firs.ToList().FindIndex(f => f.Fir == "CZQO");
        var domestic = plan.Firs[oceanicIndex + 1];
        Assert.Equal("CZQX", domestic.Fir);
        Assert.False(domestic.Oceanic);
        Assert.Equal(hoist, domestic.StartNm, 3);
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, plan.Oceanic!.ReportFixes); // ETARI … HOIST

        var session = Session(plan, templates);
        var now = T0;
        session.Start(Cruise(plan, hoist - 30, now));
        Assert.Equal(AtcPhase.Oceanic, session.Phase);
        Assert.Equal("Gander Oceanic", session.Station.Name);

        // Am Austrittspunkt: kurze Anmeldung bei Gander Center
        var handoff = Assert.Single(session.Tick(Cruise(plan, hoist + 1, now = now.AddSeconds(1))));
        Assert.Equal($"CFG2205, contact Gander Center {AtcSpeech.Frequency(domestic.FrequencyMhz!.Value).Written}", handoff.Text);
        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal(AtcStep.CenterContact, session.Step);

        var expected = Assert.Single(session.ExpectedPilotCalls(Cruise(plan, hoist + 2, now)));
        Assert.Equal(AtcMessageIds.CenterContactLevel, expected.MessageId);
        Assert.StartsWith("Gander Center, CFG2205, maintaining FL370", expected.Text);

        var reply = Assert.Single(session.PilotTransmission("Gander Center, Condor 2205, maintaining flight level three seven zero", Cruise(plan, hoist + 3, now = now.AddSeconds(1))));
        Assert.Equal(AtcMessageIds.CenterIdentified, reply.MessageId);
        Assert.DoesNotContain(session.ExpectedPilotCalls(Cruise(plan, hoist + 4, now)), c => c.MessageId == AtcMessageIds.OceanicPosition);

        // Weiter bis zur Grenze nach Montreal – direkt dorthin, auch wenn Gander unterwegs noch einen Sektor wechselt
        var montreal = plan.Firs.First(f => f.Fir == "CZUL");
        var next = Assert.Single(session.Tick(Cruise(plan, montreal.StartNm + 1, now.AddSeconds(1))));
        Assert.StartsWith("CFG2205, contact Montreal Center ", next.Text);
    }

    [Fact]
    public void Sprachschleife_kennt_die_Stationen_der_Route()
    {
        var templates = TestData.Templates();
        var plan = Plan(templates: templates);
        var session = Session(plan, templates);
        session.Start(Cruise(plan, plan.FixDistanceNm(Hoist)!.Value + 10, T0));

        var stations = AtcRoutingInfo.From(templates, session, null, null).Stations;

        Assert.Contains("Gander Center", stations);
        Assert.Contains("Gander Oceanic", stations);
        Assert.Contains("Montreal Center", stations);
        Assert.Contains("Minneapolis Center", stations);
        Assert.Contains("Los Angeles Center", stations);
    }

    // ------------------------------------------------------------------ 4. Fehlende FIRs und Fallback

    [Fact]
    public void FIR_ohne_Eintrag_heisst_wie_in_VAT_Spy()
    {
        var templates = TestData.Templates();
        var stations = new AtcStations(templates, _ => null, new AtcVoiceAssigner(templates.Voices, "names", null, null), TestData.Airspace);

        Assert.Equal("Winnipeg Center", stations.Fir("CZWG", oceanic: false).Name);
        Assert.Equal("Praha Radar", stations.Fir("LKAA", oceanic: false).Name);
        Assert.Equal("London Control", stations.Fir("EGTT", oceanic: false).Name); // Vorlage hat Vorrang
        Assert.Equal("Bremen Radar", stations.Fir("EDVV", oceanic: false).Name);
        Assert.Equal("Minneapolis Center", stations.Fir("KZMP", oceanic: false).Name);
        Assert.Equal("119.1", stations.Fir("KZMP", oceanic: false).Frequency.Written);
        Assert.Equal("Center", stations.Fir("XXXX", oceanic: false).Name); // ganz unbekannt: generische Station
        Assert.Equal("Oceanic", stations.Fir("YYYY", oceanic: true).Name);
    }

    // ------------------------------------------------------------------ 5. Rufzeichen aus OpenFlights

    [Fact]
    public void OpenFlights_im_Originalformat()
    {
        const string data = """"
            1,"Lufthansa",\N,"LH","DLH","LUFTHANSA","Germany","Y"
            2,"Old Air",\N,"","OLD","OLDIE","Nowhere","N"
            3,"New Air",\N,"","OLD","NEWBIE","Nowhere","Y"
            4,"Air Canada",\N,"AC","ACA","AIR CANADA","Canada","Y"
            439,"Alaska Airlines",\N,"AS","ASA"," Inc.","ALASKA","Y"
            5,"Quote ""Air""",\N,"","QAI","QUOTE AIR","X","Y"
            6,"No ICAO",\N,"NI","","NOICAO","X","Y"
            7,"No callsign",\N,"","NOC",\N,"X","Y"
            """";

        var airlines = AirlineDirectory.Parse(new StringReader(data));

        Assert.Equal(4, airlines.Count);
        Assert.Equal("Lufthansa", airlines.Telephony["dlh"]);
        Assert.Equal("Newbie", airlines.Telephony["OLD"]); // aktive Airline gewinnt
        Assert.Equal("Air Canada", airlines.Telephony["ACA"]);
        Assert.Equal("Quote Air", airlines.Telephony["QAI"]);
        Assert.False(airlines.Telephony.ContainsKey("ASA")); // verrutschte Zeile
    }

    [Fact]
    public void Rufzeichen_aus_OpenFlights_Vorlage_nur_fuer_Ausnahmen()
    {
        var templates = TestData.Templates();
        var telephony = templates.CallsignTelephony;

        Assert.True(TestData.Airlines.Count > 4000);
        Assert.False(templates.Telephony.ContainsKey("DLH")); // steht nicht mehr in der Vorlage …
        Assert.Equal("Lufthansa four alpha bravo", AtcSpeech.Callsign("DLH4AB", telephony).Spoken); // … kommt aus OpenFlights
        Assert.Equal("Spirit Wings one two", AtcSpeech.Callsign("NKS12", telephony).Spoken);
        Assert.Equal("City one two three", AtcSpeech.Callsign("KLC123", telephony).Spoken);
        Assert.Equal("Skywest one", AtcSpeech.Callsign("SKW1", telephony).Spoken);
        Assert.Equal("Airfrance one", AtcSpeech.Callsign("AFR1", telephony).Spoken); // Ausnahme schlägt OpenFlights ("Airfrans")
        Assert.Equal("Alpine one", AtcSpeech.Callsign("EJU1", telephony).Spoken); // fehlt in OpenFlights
        Assert.Equal("Porter two three five five", AtcSpeech.Callsign("POE2355", telephony).Spoken);
    }

    // ------------------------------------------------------------------ 6. Vorlagen: Format und Migration

    private const string OldUserCopy = """
        {
          "version": 1,
          "telephony": { "XQX": "Testair" },
          "firs": {
            "CZQX": { "name": "Gander Oceanic", "oceanic": true, "frequency": "128.700" },
            "CENTER": { "name": "Center" }
          },
          "voices": { "stations": { "FIR/CZQX": "bm_daniel" } },
          "messages": { "common.roger": { "variants": [ "{callsign}, roger roger" ] } }
        }
        """;

    [Fact]
    public void Eigene_Kopie_im_alten_Format_wird_angehoben_und_ueber_die_Grundlage_gelegt()
    {
        var notes = new List<string>();
        var templates = AtcTemplateLoader.ParseLayered(File.ReadAllText(TestData.RepoFile("src", "Kopilot", AtcTemplateLoader.FileName)), OldUserCopy, notes);

        Assert.Equal(AtcTemplates.CurrentVersion, templates.Version);
        Assert.Empty(AtcTemplateLoader.Validate(templates));
        Assert.Equal(2, notes.Count);

        // Alter Gander-Eintrag wandert nach CZQO, CZQX ist Gander Center aus der Grundlage
        Assert.Equal("Gander Oceanic", templates.Fir("CZQO")!.Name);
        Assert.True(templates.Fir("CZQO")!.Oceanic);
        Assert.Equal("128.700", templates.Fir("CZQO")!.Frequency);
        Assert.Equal("Gander Center", templates.Fir("CZQX")!.Name);
        Assert.Equal("CZQO", templates.Fir("CZQX")!.OceanicFir);
        Assert.False(templates.Fir("CZQX")!.Oceanic);
        Assert.Equal("bm_daniel", templates.Voices.Stations["FIR/CZQO"]);
        Assert.False(templates.Voices.Stations.ContainsKey("FIR/CZQX"));

        // Nur die Abweichungen der Kopie gelten, alles andere aus der Grundlage
        Assert.Equal("Testair", templates.Telephony["XQX"]);
        Assert.Equal("Airfrance", templates.Telephony["AFR"]);
        Assert.Equal(new[] { "{callsign}, roger roger" }, templates.Message(AtcMessageIds.CommonRoger)!.Variants);
        Assert.NotNull(templates.Message(AtcMessageIds.CommonRadioCheck));
        Assert.Equal("Minneapolis Center", templates.Fir("KZMP")!.Name);
        Assert.Equal(20.0, templates.Rules.SectorMinNm);
    }

    [Fact]
    public void Alte_Kopie_allein_funktioniert_weiter()
    {
        var notes = new List<string>();
        var full = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(TestData.RepoFile("src", "Kopilot", AtcTemplateLoader.FileName)))!.AsObject();
        full["version"] = 1;
        full["firs"]!.AsObject().Remove("CZQO");
        full["firs"]!["CZQX"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "Gander Oceanic", ["oceanic"] = true };

        var templates = AtcTemplateLoader.ParseLayered(null, full.ToJsonString(), notes);

        Assert.Empty(AtcTemplateLoader.Validate(templates));
        Assert.Equal("Gander Oceanic", templates.Fir("CZQO")!.Name);
        Assert.Null(templates.Fir("CZQX")); // ohne Grundlage: Domestic-Teil mit generischem bzw. VAT-Spy-Namen
        Assert.Single(notes);
    }

    [Fact]
    public void Datei_mit_Fehler_in_der_eigenen_Kopie_nimmt_die_Grundlage()
    {
        var dir = Directory.CreateTempSubdirectory("kopilot-atc-");
        try
        {
            var user = Path.Combine(dir.FullName, "atc-templates.json");
            File.WriteAllText(user, "{ \"firs\": { ");
            var errors = new List<string>();
            var templates = AtcTemplateLoader.Load(TestData.RepoFile("src", "Kopilot", AtcTemplateLoader.FileName), user, errors, []);

            Assert.NotNull(templates);
            Assert.Single(errors);
            Assert.Contains(user, errors[0], StringComparison.Ordinal);
            Assert.Equal("Gander Center", templates.Fir("CZQX")!.Name);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
