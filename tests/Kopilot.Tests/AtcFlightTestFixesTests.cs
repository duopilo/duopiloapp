using Kopilot.Atc;
using Kopilot.FlightPlan;

namespace Kopilot.Tests;

/// <summary>Korrekturen am Kopilot-ATC nach dem Testflug CFG2205 EDDF–KLAX (v0.9): Rufzeichen, Radio Check, Oceanic-Status, Center-Fallback.</summary>
public sealed class AtcFlightTestFixesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    private static AtcTemplates Templates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Kopilot", AtcTemplateLoader.FileName)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return AtcTemplateLoader.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "Kopilot", AtcTemplateLoader.FileName)));
    }

    private static OfpFix Fix(string ident, double lat, double lon, string fir, string type = "wpt", bool sidStar = false) => new()
    {
        Ident = ident,
        Lat = lat,
        Lon = lon,
        Type = type,
        IsSidStar = sidStar,
        Fir = fir,
    };

    /// <summary>
    /// Wie im Testflug: atc.callsign mit IATA-Code (DE2205), Flugnummer mit ICAO-Code (CFG2205), FIRs aus dem OFP –
    /// Nordatlantik über ETARI, danach Kanada und US-Center, die nicht in den Vorlagen stehen (CZWG, KZMP, KZDV, KZLA).
    /// </summary>
    private static Ofp Cfg2205() => new()
    {
        RequestId = "cfg2205-eddf-klax",
        Callsign = "DE2205",
        FlightNumber = "CFG2205",
        Origin = new OfpAirport("EDDF", "Frankfurt/Main International", "25C", 50.0333, 8.5706, 364, 5000, 6000, null),
        Destination = new OfpAirport("KLAX", "Los Angeles International", "24R", 33.9425, -118.4081, 128, 18000, 18000, null),
        InitialAltitudeFt = 37000,
        Fixes =
        [
            Fix("TOBAK", 50.15, 8.25, "EDGG", sidStar: true),
            Fix("TOC", 50.60, 7.50, "EDGG", type: "ltlg"),
            Fix("GOW", 55.87, -4.43, "EGPX"),
            Fix("ETARI", 56.00, -15.00, "EGPX"),
            Fix("56N020W", 56.00, -20.00, "EGGX", type: "ltlg"),
            Fix("57N030W", 57.00, -30.00, "CZQX", type: "ltlg"),
            Fix("57N040W", 57.00, -40.00, "CZQX", type: "ltlg"),
            Fix("56N050W", 56.00, -50.00, "CZQX", type: "ltlg"),
            Fix("NALDI", 55.00, -57.00, "CZQX"),
            Fix("YYR", 53.32, -60.42, "CZUL"),
            Fix("YQT", 48.37, -89.32, "CZWG"),
            Fix("DLH", 46.84, -92.19, "KZMP"),
            Fix("SNY", 41.10, -102.98, "KZDV"),
            Fix("HEC", 34.80, -116.46, "KZLA"),
            Fix("TOD", 34.30, -117.30, "KZLA", type: "ltlg"),
            Fix("KLAX", 33.9425, -118.4081, "KZLA", type: "apt"),
        ],
    };

    private const int Etari = 3;
    private const int Report56N020W = 4;
    private const int W030 = 5;
    private const int Yyr = 9;
    private const int Yqt = 10;
    private const int Dlh = 11;

    private static AtcSession Session(Ofp ofp, out AtcTemplates templates)
    {
        templates = Templates();
        return new AtcSession(templates, AtcPlan.Build(ofp, null, templates), new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));
    }

    /// <summary>Reiseflug an einer Streckenposition (nm ab dem Startflughafen).</summary>
    private sealed class Cruise(AtcSession session)
    {
        private DateTimeOffset _now = T0;

        public AtcInput At(double nm)
        {
            var plan = session.Plan;
            var (lat, lon) = (plan.Points[^1].Lat, plan.Points[^1].Lon);
            for (var i = 1; i < plan.Points.Count; i++)
            {
                if (plan.PointDistanceNm[i] >= nm)
                {
                    // Auf dem Großkreis – die Strecken über Kanada sind zu lang für eine lineare Interpolation
                    var a = plan.Points[i - 1];
                    var b = plan.Points[i];
                    var bearing = RouteMath.BearingDeg(a.Lat, a.Lon, b.Lat, b.Lon);
                    (lat, lon) = RouteMath.Destination(a.Lat, a.Lon, bearing, Math.Max(0, nm - plan.PointDistanceNm[i - 1]));
                    break;
                }
            }

            return new AtcInput
            {
                TimeUtc = _now,
                OnGround = false,
                Lat = lat,
                Lon = lon,
                AltitudeFt = 37000,
                AglFt = 36500,
                GroundSpeedKt = 450,
                IasKt = 270,
                VerticalSpeedFpm = 0,
                ZuluTimeSec = 18 * 3600,
            };
        }

        public double Fix(int index) => session.Plan.FixDistanceNm(index)!.Value;

        public void Start(double nm) => session.Start(At(nm));

        public IReadOnlyList<AtcTransmission> Tick(double nm)
        {
            _now = _now.AddSeconds(1);
            return session.Tick(At(nm));
        }

        public IReadOnlyList<AtcTransmission> Say(string text, double nm) => session.PilotTransmission(text, At(nm));

        public IReadOnlyList<AtcRendered> Expected(double nm) => session.ExpectedPilotCalls(At(nm));
    }

    // ------------------------------------------------------------------ 1. Rufzeichen aus dem OFP

    [Theory]
    [InlineData("DE2205", "CFG2205", null, "CFG2205")] // IATA im callsign-Feld: ICAO-Flugnummer gewinnt
    [InlineData("DLH4AB", "DLH400", null, "DLH4AB")] // alphanumerisches Rufzeichen derselben Airline bleibt
    [InlineData("DLH4AB", null, null, "DLH4AB")]
    [InlineData(null, "CFG2205", null, "CFG2205")]
    [InlineData("DE2205", "2205", null, "DE2205")] // Flugnummer ohne ICAO-Kennung
    [InlineData(null, null, "BAW12", "BAW12")]
    public void Rufzeichen_kommt_aus_der_Flugnummer_mit_ICAO_Kennung(string? callsign, string? flightNumber, string? sim, string expected)
    {
        var ofp = Cfg2205() with { Callsign = callsign, FlightNumber = flightNumber };
        Assert.Equal(expected, AtcPlan.CallsignCode(ofp, sim));
    }

    [Fact]
    public void Condor_statt_delta_echo()
    {
        var session = Session(Cfg2205(), out var templates);

        Assert.Equal("CFG2205", session.Plan.Callsign);
        Assert.Equal("Condor two two zero five", session.Value("callsign")!.Spoken);
        Assert.Contains("Condor two two zero five", AtcRoutingInfo.From(templates, null, Cfg2205(), null).Callsigns);
        Assert.DoesNotContain(AtcRoutingInfo.From(templates, null, Cfg2205(), null).Callsigns, c => c.Contains("delta echo", StringComparison.Ordinal));
    }

    [Fact]
    public void Airline_nicht_in_der_Liste_wird_buchstabiert()
    {
        var telephony = Templates().Telephony;
        Assert.False(telephony.ContainsKey("ASA"));
        Assert.Equal("alpha sierra alpha one two three", AtcSpeech.Callsign("ASA123", telephony).Spoken);
    }

    [Fact]
    public void SimBrief_OFP_mit_IATA_Rufzeichen()
    {
        const string json = """
            {
              "fetch": { "status": "Success" },
              "general": { "icao_airline": "CFG", "flight_number": "2205", "initial_altitude": "37000" },
              "atc": { "callsign": "DE2205" },
              "origin": { "icao_code": "EDDF", "pos_lat": "50.03", "pos_long": "8.57" },
              "destination": { "icao_code": "KLAX", "pos_lat": "33.94", "pos_long": "-118.41" },
              "navlog": { "fix": [ { "ident": "KLAX", "type": "apt", "pos_lat": "33.94", "pos_long": "-118.41" } ] }
            }
            """;

        var ofp = OfpParser.Parse(json);

        Assert.Equal("DE2205", ofp.Callsign);
        Assert.Equal("CFG2205", ofp.FlightNumber);
        Assert.Equal("CFG2205", AtcPlan.CallsignCode(ofp, "CFGKID6710"));
    }

    // ------------------------------------------------------------------ 2. Radio Check

    [Fact]
    public void Radio_check_bekommt_read_you_five_und_aendert_den_Ablauf_nicht()
    {
        var session = Session(Cfg2205(), out _);
        var atGate = new AtcInput { TimeUtc = T0, OnGround = true, AltitudeFt = 364, AglFt = 0, GroundSpeedKt = 0, Lat = 50.0333, Lon = 8.5706, ParkingBrake = true };
        session.Start(atGate);
        Assert.Equal(AtcStep.ClearanceRequest, session.Step);

        var check = Assert.Single(session.PilotTransmission("Frankfurt Delivery, Condor 2205, radio check on 121 decimal 9", atGate));
        Assert.Equal(AtcMessageIds.CommonRadioCheck, check.MessageId);
        Assert.Equal("CFG2205, read you five", check.Text);
        Assert.Equal("Condor two two zero five, read you five", check.Spoken);
        Assert.Equal("Frankfurt Delivery", check.StationName);
        Assert.Equal(AtcStep.ClearanceRequest, session.Step);

        // Danach normal weiter – auch während ein Readback offen ist
        var clearance = Assert.Single(session.PilotTransmission("Frankfurt Delivery, Condor 2205, request IFR clearance to Los Angeles", atGate));
        Assert.Equal(AtcMessageIds.DeliveryClearance, clearance.MessageId);
        Assert.Equal(AtcMessageIds.CommonRadioCheck, Assert.Single(session.PilotTransmission("Condor 2205, how do you read", atGate)).MessageId);
        Assert.NotNull(session.PendingReadback);
    }

    [Fact]
    public void Radio_check_ohne_Vorlage_bleibt_roger()
    {
        var templates = Templates();
        templates.Messages.Remove(AtcMessageIds.CommonRadioCheck);
        Assert.Contains(AtcTemplateLoader.Validate(templates), e => e.Contains(AtcMessageIds.CommonRadioCheck, StringComparison.Ordinal));

        var session = new AtcSession(templates, AtcPlan.Build(Cfg2205(), null, templates), new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));
        var atGate = new AtcInput { TimeUtc = T0, OnGround = true, Lat = 50.0333, Lon = 8.5706 };
        session.Start(atGate);
        Assert.Equal("CFG2205, roger", Assert.Single(session.PilotTransmission("Frankfurt Delivery, Condor 2205, radio check", atGate)).Text);
    }

    // ------------------------------------------------------------------ 3. Oceanic-Status

    [Fact]
    public void Sitzung_im_Oceanic_Luftraum_gestartet_gilt_als_freigegeben()
    {
        var session = Session(Cfg2205(), out _);
        var flight = new Cruise(session);
        var etari = flight.Fix(Etari);

        // z. B. nach einem Neustart von Kopilot über dem Atlantik
        flight.Start(etari + 30);
        Assert.Equal(AtcPhase.Oceanic, session.Phase);
        Assert.Equal("Shanwick Oceanic", session.Station.Name);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);

        // Positionsmeldung an 56N020W fällig; ein Radio Check verbraucht sie nicht
        var report56 = flight.Fix(Report56N020W);
        flight.Tick(report56 - 1);
        Assert.Equal("CFG2205, read you five", Assert.Single(flight.Say("Shanwick, Condor 2205, radio check on 127 decimal 65", report56 - 1)).Text);
        var report = Assert.Single(flight.Expected(report56 - 1));
        Assert.StartsWith("Shanwick Oceanic, CFG2205, position 56N020W at ", report.Text);

        var roger = Assert.Single(flight.Say(report.Text, report56 - 1));
        Assert.Equal(AtcMessageIds.OceanicRoger, roger.MessageId);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);

        Assert.Equal("CFG2205, contact Gander Oceanic 127.65", Assert.Single(flight.Tick(flight.Fix(W030))).Text);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);
    }

    [Fact]
    public void Freigabe_nie_angefordert_Positionsmeldung_ist_keine_Freigabe_Anfrage()
    {
        var session = Session(Cfg2205(), out _);
        var flight = new Cruise(session);
        var etari = flight.Fix(Etari);

        flight.Start(etari - 300);
        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal(OceanicClearanceState.NotYetDue, session.Oceanic);
        flight.Tick(etari - 200);
        Assert.Equal(OceanicClearanceState.Due, session.Oceanic);

        // Ohne Anfrage an ETARI (laut OFP noch Scottish, die Grenze liegt also auf ETARI): Übergabe an Shanwick, Freigabe gilt als erteilt
        Assert.Equal("CFG2205, contact Shanwick Oceanic 127.65", Assert.Single(flight.Tick(etari + 0.5)).Text);
        Assert.Equal(AtcPhase.Oceanic, session.Phase);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);

        flight.Tick(etari + 1);
        var roger = Assert.Single(flight.Say("Shanwick Oceanic, Condor 2205, position ETARI at 1845, flight level 370, estimating 56N020W at 1910", etari + 1));
        Assert.Equal(AtcMessageIds.OceanicRoger, roger.MessageId);
        Assert.Equal("CFG2205, roger", roger.Text);
    }

    [Fact]
    public void Sitzung_nach_dem_Oceanic_Teil_verlangt_keine_Freigabe_mehr()
    {
        var session = Session(Cfg2205(), out _);
        var flight = new Cruise(session);

        flight.Start(flight.Fix(Yyr) + 50);
        flight.Tick(flight.Fix(Yyr) + 51);

        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal(OceanicClearanceState.Cleared, session.Oceanic);
        Assert.DoesNotContain(flight.Expected(flight.Fix(Yyr) + 51), c => c.MessageId == AtcMessageIds.OceanicRequest);
    }

    // ------------------------------------------------------------------ 4. Center-Fallback für Land-FIRs ohne Eintrag

    [Fact]
    public void Land_FIR_ohne_Eintrag_bekommt_die_generische_Center_Station()
    {
        var templates = Templates();
        var stations = new AtcStations(templates, _ => null, new AtcVoiceAssigner(templates.Voices, "cfg2205", null, null));

        var kzmp = stations.Fir("KZMP", oceanic: false);
        Assert.Equal("Center", kzmp.Name);
        Assert.Equal("FIR/KZMP", kzmp.Key);
        Assert.Equal(AtcStationType.Center, kzmp.Type);
        Assert.Equal("132.6", kzmp.Frequency.Written);
        Assert.NotEqual(kzmp.Key, stations.Fir("KZDV", oceanic: false).Key);
        Assert.Equal("Montreal Center", stations.Fir("CZUL", oceanic: false).Name); // Einträge der Vorlagen bleiben
    }

    [Fact]
    public void Uebergaben_zwischen_FIRs_ohne_Eintrag()
    {
        var session = Session(Cfg2205(), out var templates);
        var flight = new Cruise(session);
        var yyr = flight.Fix(Yyr);
        var yqt = flight.Fix(Yqt);
        var dlh = flight.Fix(Dlh);

        flight.Start(yyr + 50);
        Assert.Equal("Montreal Center", session.Station.Name);

        Assert.Equal("CFG2205, contact Center 132.6", Assert.Single(flight.Tick(((yyr + yqt) / 2) + 5)).Text);
        Assert.Equal("FIR/CZWG", session.Station.Key);
        Assert.Equal("CFG2205, contact Center 132.6", Assert.Single(flight.Tick(((yqt + dlh) / 2) + 5)).Text);
        Assert.Equal("FIR/KZMP", session.Station.Key);

        // "Center" allein ist kein Stationsrufzeichen für die Sprachschleife ("runway two four right center" …)
        Assert.DoesNotContain(AtcRoutingInfo.From(templates, session, null, null).Stations, s => s.Equals("Center", StringComparison.OrdinalIgnoreCase));
    }
}
