using Kopilot.Atc;
using Kopilot.FlightPlan;
using Kopilot.Input;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Sprachschleife ↔ ATC-Modul (v0.9): Router mit den echten Stationsnamen und Rufzeichen aus atc-templates.json und
/// dem SimBrief-Plan, und der ganze Weg Abschrift → Router → AtcSession → Antwort der Station.
/// </summary>
public sealed class VoiceAtcTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const double FraLat = 50.0333;
    private const double FraLon = 8.5706;
    private const double BerLat = 52.3667;
    private const double BerLon = 13.5033;

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

    /// <summary>EDDF → EDDB, Rufzeichen DLH4AB, FIR Langen → Bremen.</summary>
    private static Ofp ShortHaul()
    {
        static OfpFix At(string ident, double f, string? type = "wpt", bool sidStar = false, string? fir = "EDGG") => new()
        {
            Ident = ident,
            Lat = FraLat + ((BerLat - FraLat) * f),
            Lon = FraLon + ((BerLon - FraLon) * f),
            Type = type,
            IsSidStar = sidStar,
            Fir = fir,
        };

        return new Ofp
        {
            RequestId = "fra-ber-voice",
            Callsign = "DLH4AB",
            Origin = new OfpAirport("EDDF", "Frankfurt/Main International", "25C", FraLat, FraLon, 364, 5000, 6000, null),
            Destination = new OfpAirport("EDDB", "Berlin Brandenburg", "25R", BerLat, BerLon, 157, 5000, 6000, null),
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

    private static readonly AtcInput AtGate = new()
    {
        TimeUtc = T0,
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

    private static AtcSession Session(AtcTemplates templates)
    {
        var session = new AtcSession(templates, AtcPlan.Build(ShortHaul(), null, templates), new AtcSessionContext(_ => null, new NoTaxiRouteProvider(), "bm_george"));
        session.Start(AtGate);
        return session;
    }

    /// <summary>ATC-Modul wie im AtcService, aber ohne Sim: echte AtcSession, Antworten werden mitgeschrieben.</summary>
    private sealed class SessionLink(AtcTemplates templates, AtcSession session) : IAtcLink
    {
        public bool Enabled { get; set; } = true;

        public string? Fail { get; set; }

        public List<string> Sent { get; } = [];

        public List<AtcTransmission> Responses { get; } = [];

        public AtcRoutingInfo RoutingInfo() => AtcRoutingInfo.From(templates, session, null, null);

        public Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            if (Fail is not null)
            {
                return Task.FromResult(new AtcTransmitResult(false, Fail, []));
            }

            var responses = session.PilotTransmission(text, AtGate);
            Responses.AddRange(responses);
            return Task.FromResult(new AtcTransmitResult(true, null, responses));
        }
    }

    private sealed class RecordingGateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.AwaitingConfirmation, Token: "t1", Action: actionId, Value: value, Readback: $"Squawk {value}"));
        }

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct)
        {
            Calls.Add($"confirm {token}");
            return Task.FromResult(new InputResult(InputResult.Executed, Confirmed: true));
        }

        public InputResult Cancel(string token, string reason)
        {
            Calls.Add($"cancel {token}");
            return new(InputResult.Cancelled);
        }
    }

    private sealed class NoState : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => new() { Connected = true, IasKt = 250 };
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (VoiceDispatcher Dispatcher, SessionLink Atc, RecordingGateway Gateway, AtcSession Session, VoiceDialog Dialog) Setup()
    {
        var templates = Templates();
        var session = Session(templates);
        var link = new SessionLink(templates, session);
        var gateway = new RecordingGateway();
        var dialog = new VoiceDialog(gateway, new NoState(), new ManualTime(T0.AddMinutes(5)));
        var dispatcher = new VoiceDispatcher(dialog, link, new VoiceRouter(["kopilot"]), ["kopilot"]);
        return (dispatcher, link, gateway, session, dialog);
    }

    private static VoiceRoute Route(string raw)
    {
        var templates = Templates();
        var info = AtcRoutingInfo.From(templates, Session(templates), null, null);
        return new VoiceRouter(["kopilot"]).Route(VoiceText.Normalize(raw), info.Callsigns, info.Stations);
    }

    // ------------------------------------------------------------------ Namen aus dem ATC-Modul

    [Fact]
    public void Stationen_und_Rufzeichen_kommen_aus_Plan_und_Vorlagen()
    {
        var templates = Templates();
        var info = AtcRoutingInfo.From(templates, Session(templates), null, null);

        Assert.Contains("Frankfurt Delivery", info.Stations);
        Assert.Contains("Frankfurt Ground", info.Stations);
        Assert.Contains("Frankfurt Tower", info.Stations);
        Assert.Contains("Berlin Approach", info.Stations);
        Assert.Contains("Langen Radar", info.Stations);
        Assert.Contains("Shanwick Oceanic", info.Stations);
        Assert.DoesNotContain("Oceanic", info.Stations);
        Assert.Equal(new[] { "DLH4AB", "Lufthansa four alpha bravo" }, info.Callsigns);

        // Vor der ersten Sitzung: aus dem SimBrief-Plan bzw. aus MSFS
        Assert.Equal(info.Callsigns, AtcRoutingInfo.From(templates, null, ShortHaul(), null).Callsigns);
        Assert.Equal(new[] { "BAW12", "Speedbird one two" }, AtcRoutingInfo.From(templates, null, null, "BAW12").Callsigns);
        Assert.Empty(AtcRoutingInfo.From(null, null, null, null).Callsigns);
    }

    // ------------------------------------------------------------------ Router mit den echten Namen

    [Theory]
    [InlineData("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", "frankfurt delivery")]
    [InlineData("Frankfurt Ground, Lufthansa 4 Alpha Bravo, request push and start", "frankfurt ground")]
    [InlineData("Langen Radar, Lufthansa four alpha bravo, passing flight level one two zero", "langen radar")]
    [InlineData("Berlin Approach, Lufthansa four alpha bravo, information Alpha", "berlin approach")]
    [InlineData("Shanwick Oceanic, Lufthansa four alpha bravo, request oceanic clearance", "shanwick oceanic")]
    [InlineData("Maastricht Radar, good day", "maastricht radar")]
    public void Anruf_mit_Station_geht_ans_ATC_Modul(string raw, string station)
    {
        var route = Route(raw);

        Assert.Equal(VoiceTarget.Atc, route.Target);
        Assert.Equal(station, route.Station);
    }

    [Theory]
    [InlineData("Cleared to Berlin, MARUN seven golf departure, runway two five center, climb five thousand feet, squawk two two zero five, Lufthansa four alpha bravo")]
    [InlineData("Cleared to Berlin, MARUN 7G departure, runway 25 Center, climb 5000 feet, squawk 2205, Lufthansa 4 Alpha Bravo.")]
    [InlineData("Squawk two two zero five, Lufthansa four alpha bravo")]
    [InlineData("Contact Ground one two one decimal eight, Lufthansa four alpha bravo")]
    [InlineData("Lufthansa four alpha bravo, request descent")]
    [InlineData("DLH4AB, wilco")]
    public void Readback_und_Meldung_mit_eigenem_Rufzeichen_gehen_ans_ATC_Modul(string raw)
    {
        var route = Route(raw);

        Assert.Equal(VoiceTarget.Atc, route.Target);
        Assert.NotNull(route.Callsign);
    }

    [Theory]
    [InlineData("Squawk two two zero five")]
    [InlineData("Kopilot, squawk two two zero five")]
    [InlineData("Climb five thousand")]
    [InlineData("Flaps one")]
    [InlineData("Speed check")]
    [InlineData("Approach on")]
    [InlineData("Ground spoilers arm")]
    public void Ohne_Station_und_Rufzeichen_bleibt_es_bei_Kopilot(string raw)
    {
        Assert.Equal(VoiceTarget.Kopilot, Route(raw).Target);
    }

    // ------------------------------------------------------------------ Ganzer Weg bis zur Station

    [Fact]
    public async Task Freigabe_und_Readback_ueber_die_Sprachschleife()
    {
        var (dispatcher, atc, gateway, session, _) = Setup();

        var request = await dispatcher.HandleAsync("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin.", CancellationToken.None);
        Assert.Equal(VoiceTarget.Atc, request.Route.Target);
        Assert.Null(request.Reply);
        var clearance = Assert.Single(atc.Responses);
        Assert.StartsWith("DLH4AB, cleared to EDDB, MARUN7G departure", clearance.Text);
        Assert.Equal(AtcStep.ClearanceReadback, session.Step);

        // Readback, wie Whisper ihn abschreibt (Ziffern) – Rufzeichen hinten
        var squawk = session.Squawk;
        var readback = await dispatcher.HandleAsync($"Cleared to Berlin, MARUN 7G departure, runway 25 Center, climb 5000 feet, squawk {squawk}, Lufthansa 4 Alpha Bravo.", CancellationToken.None);
        Assert.Equal(VoiceTarget.Atc, readback.Route.Target);
        Assert.Equal("DLH4AB, readback correct, contact Ground 121.8", Assert.Single(readback.Atc!.Responses).Text);
        Assert.Equal(AtcPhase.GroundDeparture, session.Phase);

        // Derselbe Squawk ohne Rufzeichen ist ein Kopilot-Befehl
        var command = await dispatcher.HandleAsync($"Squawk {squawk}", CancellationToken.None);
        Assert.Equal(VoiceTarget.Kopilot, command.Route.Target);
        Assert.Equal($"prepare xpdr.code {squawk}", Assert.Single(gateway.Calls));
        Assert.Equal(2, atc.Sent.Count);
    }

    [Fact]
    public async Task Say_again_geht_an_die_Seite_die_zuletzt_gesprochen_hat()
    {
        var (dispatcher, atc, _, _, dialog) = Setup();
        await dispatcher.HandleAsync("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", CancellationToken.None);
        var clearance = atc.Responses[^1].Text;

        // Kopilot hat noch nichts gesagt → ATC wiederholt die Freigabe
        var again = await dispatcher.HandleAsync("Say again", CancellationToken.None);
        Assert.Equal(VoiceTarget.Atc, again.Route.Target);
        Assert.Equal(clearance, Assert.Single(again.Atc!.Responses).Text);

        // Danach antwortet Kopilot (später als ATC) → "Say again" wiederholt Kopilot
        await dispatcher.HandleAsync("Speed check", CancellationToken.None);
        var kopilot = await dispatcher.HandleAsync("Say again", CancellationToken.None);
        Assert.Equal(VoiceTarget.Kopilot, kopilot.Route.Target);
        Assert.Equal("Speed 250", kopilot.Reply?.Text);
        Assert.NotNull(dialog.LastReplyUtc);
    }

    [Fact]
    public async Task Spricht_ATC_nach_dem_Readback_verfaellt_die_Aktion_und_Affirm_gilt_ATC()
    {
        var templates = Templates();
        var link = new SessionLink(templates, Session(templates));
        var gateway = new RecordingGateway();
        var t = T0.AddMinutes(5);
        var dialog = new VoiceDialog(gateway, new NoState(), new ManualTime(t));
        DateTimeOffset? atcHeard = null;
        var dispatcher = new VoiceDispatcher(dialog, link, new VoiceRouter(["kopilot"]), ["kopilot"]) { AtcHeardUtc = () => atcHeard };

        var readback = await dispatcher.HandleAsync("Squawk one zero zero zero", t, CancellationToken.None);
        dialog.Spoken(readback.Reply!, t.AddSeconds(2));

        // Danach funkt ATC (z. B. ein Frequenzwechsel), dann sagt der Pilot "Affirm"
        atcHeard = t.AddSeconds(4);
        var affirm = await dispatcher.HandleAsync("Affirm", t.AddSeconds(6), CancellationToken.None);

        Assert.Equal(VoiceTarget.Atc, affirm.Route.Target);
        Assert.Equal("Affirm", Assert.Single(link.Sent));
        Assert.Null(dialog.Pending);
        Assert.Equal(new[] { "prepare xpdr.code 1000", "cancel t1" }, gateway.Calls);
    }

    [Fact]
    public async Task Readback_nach_ATC_wird_bei_Kopilot_bestaetigt()
    {
        var templates = Templates();
        var link = new SessionLink(templates, Session(templates));
        var gateway = new RecordingGateway();
        var t = T0.AddMinutes(5);
        var dialog = new VoiceDialog(gateway, new NoState(), new ManualTime(t));
        var dispatcher = new VoiceDispatcher(dialog, link, new VoiceRouter(["kopilot"]), ["kopilot"]) { AtcHeardUtc = () => t.AddSeconds(-10) };
        await dispatcher.HandleAsync("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", t.AddSeconds(-20), CancellationToken.None);

        var readback = await dispatcher.HandleAsync("Squawk one zero zero zero", t, CancellationToken.None);
        dialog.Spoken(readback.Reply!, t.AddSeconds(2));
        var confirm = await dispatcher.HandleAsync("Confirm", t.AddSeconds(3), CancellationToken.None);

        Assert.Equal(VoiceTarget.Kopilot, confirm.Route.Target);
        Assert.Equal("confirm t1", gateway.Calls[^1]);
        Assert.Single(link.Sent);
    }

    [Fact]
    public async Task ATC_nicht_verfuegbar()
    {
        var (dispatcher, atc, _, _, _) = Setup();

        atc.Fail = "ATC braucht den SimBrief-Plan.";
        var failed = await dispatcher.HandleAsync("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance", CancellationToken.None);
        Assert.Equal(VoiceDispatcher.AtcUnavailable, failed.Reply?.Text);
        Assert.False(failed.Atc!.Ok);

        atc.Fail = null;
        atc.Enabled = false;
        var off = await dispatcher.HandleAsync("Frankfurt Tower, Lufthansa four alpha bravo, ready for departure", CancellationToken.None);
        Assert.Equal(VoiceDispatcher.AtcUnavailable, off.Reply?.Text);
        Assert.Single(atc.Sent);
    }

    [Fact]
    public async Task Stumm_geht_auch_nichts_an_ATC()
    {
        var (dispatcher, atc, _, _, dialog) = Setup();
        dialog.SetMuted(true);

        var outcome = await dispatcher.HandleAsync("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance", CancellationToken.None);

        Assert.Null(outcome.Reply);
        Assert.Empty(atc.Sent);
    }
}
