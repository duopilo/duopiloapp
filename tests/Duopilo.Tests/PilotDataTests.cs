using Duopilo.Aircraft;
using Duopilo.Atc;
using Duopilo.Ecam;
using Duopilo.FlightPlan;
using Duopilo.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

public sealed class PilotDataTests
{
    // ------------------------------------------------------------------ ECAM

    // Eigenes Beispiel im Format der FlyByWire-Tabelle (EwdMessages.ts). Raw-String: \x1b bleibt wörtlich stehen wie im TypeScript.
    private const string EcamSource = """
        const EwdGroups: Record<string, string> = {
          'TEST SYS$1': '\x1b<2m\x1b4mTEST SYS\x1bm',
          'OTHER$2': '\x1b<4m\x1b4mOTHER\x1bm',
        };
        export const EcamMessages = {
          ['123000101', { group: 'TEST SYS$1', text: ' PUMP FAULT' }],
          ['123000102', { text: '\x1b<5m -PUMP.......OFF' }],
          ['123000201', { group: 'OTHER$2', text: '' }],
          ['000000001', { text: '              \x1b<3mNORMAL' }],
          ['000003002', { text: '\x1b)mIR IN ALIGN' }],
        };
        """;

    [Fact]
    public void ECAM_Tabelle_wird_gelesen_und_bereinigt()
    {
        var entries = EcamCatalog.Parse(EcamSource);

        Assert.Equal(5, entries.Count);
        Assert.Equal("TEST SYS PUMP FAULT", entries["123000101"].Display);
        Assert.True(entries["123000101"].IsTitle);
        Assert.Equal("-PUMP … OFF", entries["123000102"].Display);
        Assert.False(entries["123000102"].IsTitle);
        Assert.Equal("OTHER", entries["123000201"].Display);
        Assert.Equal("NORMAL", entries["000000001"].Display);
        Assert.Equal("IR IN ALIGN", entries["000003002"].Display);
    }

    [Fact]
    public void ECAM_Codes_werden_neunstellig_nachgeschlagen()
    {
        var table = new EcamTable(EcamCatalog.Parse(EcamSource), DateTimeOffset.UtcNow, "cache");

        var known = table.Decode(1);
        Assert.True(known.Known);
        Assert.Equal("NORMAL", known.Text);

        var title = table.Decode(123000101);
        Assert.True(title.IsTitle);

        var unknown = table.Decode(999);
        Assert.False(unknown.Known);
        Assert.Equal("Code 000000999", unknown.Text);
    }

    // ------------------------------------------------------------------ Runway / Phraseologie

    [Theory]
    [InlineData(236.0, 100.0, 98.0, 23)] // 2° Ost: 236 true → 234 mag
    [InlineData(3.0, 10.0, 5.0, 36)]     // 5° Ost: 3 true → 358 mag
    [InlineData(262.0, 50.0, 55.0, 27)]  // 5° West: 262 true → 267 mag
    public void Bahnnummer_aus_rechtweisendem_Kurs(double rwyTrue, double hdgTrue, double hdgMag, int expected)
    {
        var rwy = RunwayMath.FromAtc(rwyTrue, hdgTrue, hdgMag, "EDDL", []);
        Assert.NotNull(rwy);
        Assert.Equal(expected, rwy.Number);
        Assert.Null(rwy.Designator);
    }

    [Fact]
    public void Bezeichner_kommt_aus_dem_SimBrief_Plan_wenn_die_Nummer_passt()
    {
        var rwy = RunwayMath.FromAtc(233, 90, 88, "EDDL", ["05R", "23L"]);
        Assert.NotNull(rwy);
        Assert.Equal("23L", rwy.Ident);
        Assert.Equal("simbrief", rwy.DesignatorSource);
        Assert.Equal("two three left", rwy.Spoken);

        var other = RunwayMath.FromAtc(53, 90, 88, "EDDL", ["23L"]);
        Assert.NotNull(other);
        Assert.Equal("05", other.Ident);
        Assert.Equal("zero five", other.Spoken);
    }

    [Fact]
    public void Ohne_Bahnkurs_keine_Bahn()
    {
        Assert.Null(RunwayMath.FromAtc(null, 0, 0, "EDDL", []));
        Assert.Null(RunwayMath.FromAtc(0, 0, 0, "EDDL", []));
    }

    [Theory]
    [InlineData("23L", 23, "L")]
    [InlineData("RW05R", 5, "R")]
    [InlineData("RWY09", 9, null)]
    [InlineData("36C", 36, "C")]
    public void Bahnbezeichner_werden_erkannt(string ident, int number, string? designator)
    {
        var parsed = RunwayMath.Parse(ident);
        Assert.NotNull(parsed);
        Assert.Equal(number, parsed.Value.Number);
        Assert.Equal(designator, parsed.Value.Designator);
    }

    [Fact]
    public void Ziffern_werden_mit_niner_gesprochen()
    {
        Assert.Equal("zero niner", Duopilo.Speech.Pronunciation.Runway(9, null));
        Assert.Equal("two seven right", Duopilo.Speech.Pronunciation.Runway(27, "R"));
        Assert.Equal("one niner niner", Duopilo.Speech.Pronunciation.Digits(199));
    }

    // ------------------------------------------------------------------ SimBrief

    private const string OfpJson = """
        {
          "fetch": { "status": "Success" },
          "params": { "request_id": "42", "time_generated": "1761650000", "units": "lbs" },
          "general": { "icao_airline": "DLH", "flight_number": "4AB", "initial_altitude": "36000", "costindex": "30", "route": "DCT XYZ DCT" },
          "atc": { "callsign": "DLH4AB" },
          "aircraft": { "icaocode": "A21N", "reg": "D-AIEA" },
          "origin": { "icao_code": "EDDL", "plan_rwy": "23L", "pos_lat": "51.28", "pos_long": "6.77" },
          "destination": { "icao_code": "EDDB", "plan_rwy": "25R", "pos_lat": "52.36", "pos_long": "13.50" },
          "alternate": { "icao_code": "EDDP", "plan_rwy": "26R" },
          "fuel": { "plan_takeoff": "20000", "plan_landing": "10000" },
          "weights": { "est_zfw": "140000" },
          "times": { "sched_on": "1761654000", "est_time_enroute": "3600" },
          "navlog": {
            "fix": { "ident": "EDDB", "type": "apt", "pos_lat": "52.36", "pos_long": "13.50", "distance": "280", "time_total": "3600", "fuel_plan_onboard": "10000", "altitude_feet": "200" }
          }
        }
        """;

    [Fact]
    public void SimBrief_OFP_wird_gelesen_und_in_kg_umgerechnet()
    {
        var ofp = OfpParser.Parse(OfpJson);

        Assert.Equal("lbs", ofp.SourceUnits);
        Assert.Equal("DLH4AB", ofp.Callsign);
        Assert.Equal("DLH4AB", ofp.FlightNumber);
        Assert.Equal("23L", ofp.Origin.PlanRunway);
        Assert.Equal("EDDP", ofp.Alternate?.Icao);
        Assert.Equal(36000, ofp.InitialAltitudeFt);
        Assert.Equal(9072, ofp.Fuel.PlanTakeoffKg);
        Assert.Equal(63503, ofp.Weights.EstZfwKg);

        // Einzelner Fix kommt als Objekt statt Array
        var fix = Assert.Single(ofp.Fixes);
        Assert.Equal("EDDB", fix.Ident);
        Assert.Equal(280, fix.CumulativeDistanceNm);
        Assert.Equal(4536, fix.FuelPlanOnboardKg);
        Assert.Equal(2, ofp.RoutePoints().Count);
    }

    [Fact]
    public void SimBrief_Fehlermeldung_wird_erkannt()
    {
        var ex = Assert.Throws<SimBriefException>(() => OfpParser.Parse("""{ "fetch": { "status": "Error: Unknown UserID" } }"""));
        Assert.Contains("Unknown UserID", ex.Message);
    }

    [Fact]
    public void Position_wird_dem_richtigen_Routenabschnitt_zugeordnet()
    {
        RoutePoint[] points = [new("A", 0, 0, -1), new("B", 0, 1, 0), new("C", 0, 2, 1)];

        // 3 nm nördlich der Strecke, halb zwischen B und C (Kurs Ost → nördlich = links)
        var progress = RouteTracker.Locate(points, 0.05, 1.5);

        Assert.NotNull(progress);
        Assert.Equal(1, progress.LegIndex);
        Assert.InRange(progress.CrossTrackNm, -3.2, -2.8);
        Assert.InRange(progress.DistanceToNextNm, 29, 31);
        Assert.InRange(progress.DistanceToGoNm, 29, 31);
        Assert.InRange(progress.DistanceFlownNm, 89, 91);
        Assert.InRange(progress.LegFraction, 0.45, 0.55);
    }

    [Fact]
    public void Entfernungen_auf_dem_Grosskreis()
    {
        // 1° Länge am Äquator ≈ 60 nm
        Assert.InRange(RouteMath.DistanceNm(0, 0, 0, 1), 59.9, 60.2);
        Assert.InRange(RouteMath.BearingDeg(0, 0, 0, 1), 89.9, 90.1);
        Assert.InRange(RouteMath.BearingDeg(0, 0, 1, 0), -0.1, 0.1);
    }

    // ------------------------------------------------------------------ Alert-Bus

    [Fact]
    public void Alert_Bus_fuehrt_aktive_Zustaende_und_frische_Ereignisse()
    {
        var bus = new AlertBus();
        var now = DateTimeOffset.UtcNow;
        bus.Publish([
            new AlertEvent(0, now, Rules.GearSpeed, AlertSeverity.Warning, AlertKind.Raise, "Gear speed", "x"),
            new AlertEvent(0, now, Rules.ApDisconnect, AlertSeverity.Caution, AlertKind.Event, "Autopilot off", "y"),
        ]);

        Assert.Equal(2, bus.LastId);
        Assert.Equal(2, bus.Active(now).Count);
        Assert.Equal(Rules.GearSpeed, bus.Active(now)[0].Rule);

        // Ereignisse verschwinden nach 15 s, Zustände erst mit Clear
        Assert.Single(bus.Active(now.AddSeconds(20)));
        bus.Publish([new AlertEvent(0, now, Rules.GearSpeed, AlertSeverity.Warning, AlertKind.Clear, null, "z")]);
        Assert.Empty(bus.Active(now.AddSeconds(20)));
        Assert.Equal(2, bus.Since(1).Count);
    }

    [Fact]
    public async Task Warten_endet_bei_neuer_Meldung()
    {
        var bus = new AlertBus();
        var wait = bus.WaitAsync(bus.LastId, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        bus.Publish([new AlertEvent(0, DateTimeOffset.UtcNow, Rules.SimPaused, AlertSeverity.Info, AlertKind.Raise, "Sim paused", "p")]);
        var events = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Sim paused", Assert.Single(events).Callout);
    }

    [Fact]
    public async Task Warten_liefert_nach_Timeout_nichts()
    {
        var bus = new AlertBus();
        var events = await bus.WaitAsync(0, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Empty(events);
    }

    // ------------------------------------------------------------------ Profile

    private static ProfileStore RepoProfiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Duopilo", "profiles")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var options = Options.Create(new DuopiloOptions { ProfilesDirectory = Path.Combine(dir.FullName, "src", "Duopilo", "profiles") });
        return new ProfileStore(options, NullLogger<ProfileStore>.Instance);
    }

    [Fact]
    public void Headwind_A330_hat_eigenes_Profil()
    {
        var store = RepoProfiles();
        const string path = @"c:\users\flo21\appdata\local\packages\microsoft.limitless_8wekyb3d8bbwe\localcache\packages\community\headwindsim-aircraft-a330-900\simobjects\airplanes\headwind_a330neo\aircraft.cfg";

        var profile = store.Match("Airbus A330-900neo Condor Sea - Danke Tecke", path);

        Assert.NotNull(profile);
        Assert.Equal("Headwind A330-900neo", profile.Name);
        Assert.Equal("L:A32NX_AUTOPILOT_1_ACTIVE", profile.Bindings[BindingKeys.Ap1].Name);
        Assert.Equal("AUTOPILOT ALTITUDE LOCK VAR:3", profile.Bindings[BindingKeys.SelectedAltitude].Name);
        Assert.Equal("arinc429", profile.Bindings[BindingKeys.ApuN].Type);
        Assert.False(profile.Bindings.ContainsKey(BindingKeys.AthrMode)); // schreibt Headwind nur bei A.FLOOR – lieber leer als falsch
        Assert.Equal("L:A32NX_FCU_ATS_FMA_DISCRETE_WORD", profile.Bindings[BindingKeys.AthrFmaWord].Name); // A/THR-Modus wie das PFD
        Assert.Equal("arinc429", profile.Bindings[BindingKeys.FmgcDiscreteWord1].Type); // ALT CRZ
        Assert.Equal("knots", profile.Bindings[BindingKeys.SpeedTarget].Unit);
        Assert.Equal(180, profile.Limits.VfeFor(null, "FULL"));
        Assert.Contains(profile.Actions, a => a.Id == "apu.start");
        Assert.Contains(profile.Actions, a => a.Id == "signs.seatbelts");

        // Der A32NX bleibt beim A32NX
        Assert.Equal("FlyByWire A32NX", store.Match("FlyByWire A320neo", @"...\flybywire-aircraft-a320-neo\SimObjects\AirPlanes\FlyByWire_A320_NEO\aircraft.cfg")!.Name);
    }

    [Fact]
    public void Mitgelieferte_Profile_sind_gueltig()
    {
        var store = RepoProfiles();

        Assert.Empty(store.Errors);
        Assert.Contains(store.All, p => p.Name == "FlyByWire A32NX");
        Assert.All(store.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.All(p.Bindings, b => Assert.False(string.IsNullOrWhiteSpace(b.Value.Name)));
        });
    }

    [Theory]
    [InlineData("FlyByWire A320neo", @"SimObjects\AirPlanes\FlyByWire_A320_NEO\aircraft.cfg", "FlyByWire A32NX")]
    [InlineData("A380X Lufthansa", @"SimObjects\AirPlanes\FlyByWire_A380X\aircraft.cfg", "FlyByWire A380X")]
    [InlineData("Airbus A321LR Condor", @"SimObjects\Airplanes\inibuilds-aircraft-a321lr\aircraft.cfg", "iniBuilds A321LR")]
    [InlineData("Airbus A321LR Condor", null, "iniBuilds A321LR")]
    [InlineData("Cessna 172", @"SimObjects\Airplanes\Asobo_C172\aircraft.cfg", null)]
    public void Profil_wird_ueber_Pfad_und_Titel_erkannt(string title, string? path, string? expected)
    {
        var store = RepoProfiles();
        Assert.Equal(expected, store.Match(title, path)?.Name);
    }

    [Fact]
    public void FBW_Profil_liefert_AP_ECAM_und_Fuel_Flow()
    {
        var a32nx = RepoProfiles().Match("FlyByWire A320neo", null);
        Assert.NotNull(a32nx);
        Assert.Equal("L:A32NX_AUTOPILOT_1_ACTIVE", a32nx.Bindings[BindingKeys.Ap1].Name);
        Assert.True(a32nx.Bindings.ContainsKey("AP1")); // Groß-/Kleinschreibung egal
        Assert.Equal("L:A32NX_ENGINE_FF:{i}", a32nx.Bindings[BindingKeys.FuelFlowKgh].Name);
        Assert.NotNull(a32nx.Ecam);
        Assert.Equal(7, a32nx.Ecam.Lines);
        Assert.Equal(230, a32nx.Limits.VfeFor(1));
        Assert.Null(a32nx.Limits.VfeFor(0));
    }

    [Fact]
    public void Bindings_uebersetzen_Enums_und_Bitmasken()
    {
        var enumBinding = new ProfileBinding { Type = "enum", Values = new Dictionary<string, string> { ["10"] = "ALT", ["14"] = "V/S" } };
        Assert.Equal("V/S", enumBinding.Translate(14));
        Assert.Null(enumBinding.Translate(99));

        var bits = new ProfileBinding { Type = "bitmask", Values = new Dictionary<string, string> { ["0"] = "ALT", ["2"] = "CLB", ["4"] = "G/S" } };
        Assert.Equal(new[] { "ALT", "G/S" }, bits.DecodeBits(17));
    }
}
