using Kopilot.Input;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>Sprachschleife (v0.9): Zwei-Schritt-Ablauf mit Readback und Bestätigung, Abfragen, Phraseologie.</summary>
public sealed class VoiceDialogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeGateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public InputResult NextPrepare { get; set; } = new(InputResult.AwaitingConfirmation, Token: "t1", Action: "flaps", Value: "1", Readback: "Flaps 1");

        public InputResult NextConfirm { get; set; } = new(InputResult.Executed, Action: "flaps", Value: "1", Confirmed: true);

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(NextPrepare);
        }

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct)
        {
            Calls.Add($"confirm {token} {pilotWords}");
            return Task.FromResult(NextConfirm);
        }

        public InputResult Cancel(string token, string reason)
        {
            Calls.Add($"cancel {token}");
            return new InputResult(InputResult.Cancelled);
        }
    }

    private sealed class FixedState(VoiceFlightState state) : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => state;
    }

    private static readonly VoiceFlightState Cruise = new()
    {
        Connected = true, OnGround = false, IasKt = 250.4, GroundSpeedKt = 410, AltitudeFt = 4980, PressureAltitudeFt = 5100, BaroHpa = 1008,
        HeadingMagDeg = 89.6, FlapsLabel = "0", GearExtendedPct = 0, SpeedbrakeHandlePct = 0, SpoilersArmed = false, SeatbeltsOn = true,
        ApuNPct = 0, FuelKg = 12437, Engines = [new EngineReading(true, 82), new EngineReading(true, 82.5)],
    };

    private static (VoiceDialog Dialog, FakeGateway Gateway, ManualTime Time) Create(VoiceFlightState? state = null)
    {
        var time = new ManualTime(T0);
        var gateway = new FakeGateway();
        return (new VoiceDialog(gateway, new FixedState(state ?? Cruise), time), gateway, time);
    }

    /// <summary>Ein Satz des Piloten. Ein Readback gilt sofort als gesprochen (wie nach der Sprachausgabe), außer <paramref name="hear"/> ist false.</summary>
    private static async Task<VoiceReply?> Say(VoiceDialog dialog, string raw, bool hear = true, DateTimeOffset? startUtc = null)
    {
        var normalized = VoiceText.Normalize(raw);
        var addressed = VoiceText.Words(normalized) is { Length: > 0 } w && w[0] == "kopilot";
        var reply = await dialog.HandleAsync(VoiceIntents.Recognize(normalized, ["kopilot"]), normalized, addressed, startUtc, CancellationToken.None);
        if (hear && reply?.ReadbackToken is not null)
        {
            dialog.Spoken(reply, dialog.Pending?.ExpiresUtc - dialog.ConfirmTimeout ?? T0);
        }

        return reply;
    }

    // ------------------------------------------------------------------ Zwei Schritte

    [Fact]
    public async Task Befehl_Readback_Confirm_fuehrt_aus()
    {
        var (dialog, gateway, _) = Create();

        var readback = await Say(dialog, "Flaps one");
        Assert.Equal("Flaps 1", readback?.Text);
        Assert.Equal("Flaps one", readback?.Spoken);
        Assert.NotNull(dialog.Pending);

        var done = await Say(dialog, "Confirm");
        Assert.Null(done); // ausgeführt – nichts zu sagen
        Assert.Null(dialog.Pending);
        Assert.Equal(new[] { "prepare flaps 1", "confirm t1 confirm" }, gateway.Calls);
    }

    [Theory]
    [InlineData("Correct")]
    [InlineData("Affirm")]
    [InlineData("Ja, korrekt")]
    public async Task Auch_Correct_und_Affirm_bestaetigen(string words)
    {
        var (dialog, gateway, _) = Create();
        await Say(dialog, "Klappen eins");

        await Say(dialog, words);

        Assert.StartsWith("confirm t1", gateway.Calls[^1]);
    }

    // ------------------------------------------------------------------ Nur bestätigen, was gehört wurde

    [Fact]
    public async Task Confirm_zaehlt_erst_nach_dem_gesprochenen_Readback()
    {
        var (dialog, gateway, time) = Create();
        var readback = await Say(dialog, "Flaps one", hear: false);
        Assert.Equal("t1", readback?.ReadbackToken);

        // Readback wartet noch in der Warteschlange (der Pilot hat gleich weitergesprochen) – "Confirm" zählt nicht
        Assert.Null(await Say(dialog, "Confirm"));
        Assert.NotNull(dialog.Pending);
        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("confirm", StringComparison.Ordinal));

        // Readback gesprochen bis T0+3 s; ein "Confirm", das schon vorher begann, zählt auch nicht
        dialog.Spoken(readback!, T0.AddSeconds(3));
        time.Now = T0.AddSeconds(4);
        Assert.Null(await Say(dialog, "Confirm", startUtc: T0.AddSeconds(2)));
        Assert.NotNull(dialog.Pending);

        Assert.Null(await Say(dialog, "Confirm", startUtc: T0.AddSeconds(4)));
        Assert.Null(dialog.Pending);
        Assert.Equal("confirm t1 confirm", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Nicht_gesprochener_Readback_wird_verworfen()
    {
        var (dialog, gateway, _) = Create();
        var readback = await Say(dialog, "Gear up", hear: false);

        dialog.NotSpoken(readback!);

        Assert.Null(dialog.Pending);
        Assert.Equal("cancel t1", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Say_again_spricht_den_Readback_noch_einmal_und_macht_ihn_bestaetigbar()
    {
        var (dialog, gateway, time) = Create();
        await Say(dialog, "Gear up", hear: false);

        time.Now = T0.AddSeconds(8);
        var again = await Say(dialog, "Say again", hear: false);
        Assert.Equal("t1", again?.ReadbackToken);
        dialog.Spoken(again!, T0.AddSeconds(9));

        time.Now = T0.AddSeconds(10);
        await Say(dialog, "Correct");
        Assert.StartsWith("confirm t1", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Zeitfenster_beginnt_mit_dem_Ende_des_Readbacks()
    {
        var (dialog, gateway, time) = Create();
        var readback = await Say(dialog, "Gear up", hear: false);
        dialog.Spoken(readback!, T0.AddSeconds(10));

        time.Now = T0.AddSeconds(65);
        dialog.Expire();
        Assert.NotNull(dialog.Pending);

        time.Now = T0.AddSeconds(71);
        dialog.Expire();
        Assert.Null(dialog.Pending);
        Assert.Equal("cancel t1", gateway.Calls[^1]);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("Ja")]
    [InlineData("OK")]
    [InlineData("Okay")]
    [InlineData("Richtig")]
    [InlineData("Roger")]
    public async Task Ja_und_OK_bestaetigen_nichts(string words)
    {
        var (dialog, gateway, _) = Create();

        // Ohne offene Aktion: still
        Assert.Null(await Say(dialog, words));

        // Nach dem Readback: nachfragen, die Aktion bleibt offen
        await Say(dialog, "Flaps one");
        Assert.Equal(VoiceDialog.SayAgain, (await Say(dialog, words))?.Text);
        Assert.NotNull(dialog.Pending);
        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("confirm", StringComparison.Ordinal));

        // "Say again" wiederholt danach den Readback, nicht das "Say again"
        Assert.Equal("Flaps 1", (await Say(dialog, "Say again"))?.Text);
    }

    [Fact]
    public async Task Neuer_Befehl_verwirft_die_offene_Aktion_auch_wenn_er_abgelehnt_wird()
    {
        var (dialog, gateway, _) = Create();
        await Say(dialog, "Gear up");

        gateway.NextPrepare = new InputResult(InputResult.Refused, "Klappen nur bis VFE.");
        Assert.Equal(VoiceDialog.Unable, (await Say(dialog, "Flaps full"))?.Text);

        Assert.Null(dialog.Pending);
        Assert.Equal(new[] { "prepare gear.up ", "cancel t1", "prepare flaps FULL" }, gateway.Calls);
    }

    [Theory]
    [InlineData("Negative")]
    [InlineData("Cancel")]
    [InlineData("Nein")]
    public async Task Negative_bricht_ab(string words)
    {
        var (dialog, gateway, _) = Create();
        await Say(dialog, "Gear up");

        var reply = await Say(dialog, words);

        Assert.Equal(VoiceDialog.Cancelled, reply?.Text);
        Assert.Null(dialog.Pending);
        Assert.Equal("cancel t1", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Ohne_Bestaetigung_verfaellt_die_Aktion_nach_60_Sekunden()
    {
        var (dialog, gateway, time) = Create();
        await Say(dialog, "Gear up");

        time.Now = T0.AddSeconds(59);
        dialog.Expire();
        Assert.NotNull(dialog.Pending);

        time.Now = T0.AddSeconds(61);
        dialog.Expire();
        Assert.Null(dialog.Pending);
        Assert.Equal("cancel t1", gateway.Calls[^1]);

        // Ein spätes "Confirm" führt nichts mehr aus
        Assert.Null(await Say(dialog, "Confirm"));
        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("confirm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_ohne_offene_Aktion_wird_ignoriert()
    {
        var (dialog, gateway, _) = Create();

        Assert.Null(await Say(dialog, "Confirm"));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Abgelehnt_gesperrt_oder_nicht_uebernommen()
    {
        var (dialog, gateway, _) = Create();

        gateway.NextPrepare = new InputResult(InputResult.Disabled, "Eingaben sind gesperrt.");
        Assert.Equal(VoiceDialog.InputDisabled, (await Say(dialog, "Gear up"))?.Text);
        Assert.Null(dialog.Pending);

        gateway.NextPrepare = new InputResult(InputResult.Refused, "Klappen nur bis VFE.");
        Assert.Equal(VoiceDialog.Unable, (await Say(dialog, "Flaps full"))?.Text);

        gateway.NextPrepare = new InputResult(InputResult.AwaitingConfirmation, Token: "t2", Readback: "Gear up");
        gateway.NextConfirm = new InputResult(InputResult.Executed, Confirmed: false);
        await Say(dialog, "Gear up");
        Assert.Equal(VoiceDialog.NotConfirmed, (await Say(dialog, "Confirm"))?.Text);
    }

    [Fact]
    public async Task Readback_nutzt_die_Phraseologie()
    {
        var (dialog, gateway, _) = Create();
        gateway.NextPrepare = new InputResult(InputResult.AwaitingConfirmation, Token: "t3", Readback: "QNH 1013 set");

        var reply = await Say(dialog, "QNH one zero one three");

        Assert.Equal("prepare efis.baro 1013", gateway.Calls[^1]);
        Assert.Equal("QNH one zero one three set", reply?.Spoken);
    }

    // ------------------------------------------------------------------ Abfragen, Unbekanntes, Stumm

    [Theory]
    [InlineData("Speed check", "Speed 250")]
    [InlineData("Altitude check", "Altitude 5000")]
    [InlineData("Fuel check", "Fuel 12.4 tons")]
    [InlineData("Engine check", "Engines normal")]
    [InlineData("Speedbrake check", "Speedbrake retracted")]
    [InlineData("Gear check", "Gear up")]
    [InlineData("Flaps check", "Flaps 0")]
    [InlineData("Spoilers check", "Spoilers disarmed")]
    [InlineData("Baro check", "QNH 1008")]
    [InlineData("Heading check", "Heading 090")]
    public async Task Abfragen_nur_mit_dem_reinen_Callout(string question, string answer)
    {
        var (dialog, gateway, _) = Create();

        var reply = await Say(dialog, question);

        Assert.Equal(answer, reply?.Text);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Unbekannter_Satz_bekommt_Say_again_langes_Gespraech_nicht()
    {
        var (dialog, _, _) = Create();

        Assert.Equal(VoiceDialog.SayAgain, (await Say(dialog, "Flaps banana"))?.Text);
        Assert.Null(await Say(dialog, "Und dann hat er gesagt, dass wir morgen früh wieder nach Düsseldorf zurückfliegen"));
    }

    [Fact]
    public async Task Say_again_wiederholt_die_letzte_Antwort()
    {
        var (dialog, _, _) = Create();
        await Say(dialog, "Speed check");

        Assert.Equal("Speed 250", (await Say(dialog, "Say again"))?.Text);
    }

    [Fact]
    public async Task Kopilot_Pause_und_weiter()
    {
        var (dialog, gateway, _) = Create();

        Assert.Equal(VoiceDialog.Paused, (await Say(dialog, "Kopilot Pause"))?.Text);
        Assert.True(dialog.Muted);
        Assert.Null(await Say(dialog, "Gear up"));
        Assert.Null(await Say(dialog, "Speed check"));
        Assert.Empty(gateway.Calls);

        Assert.Equal(VoiceDialog.Resumed, (await Say(dialog, "Kopilot weiter"))?.Text);
        Assert.False(dialog.Muted);
        Assert.Equal("Flaps 1", (await Say(dialog, "Flaps 1"))?.Text);
    }

    [Fact]
    public async Task Stumm_schalten_verwirft_die_offene_Aktion()
    {
        var (dialog, gateway, _) = Create();
        await Say(dialog, "Gear up");

        dialog.SetMuted(true);

        Assert.Null(dialog.Pending);
        Assert.Equal("cancel t1", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Mit_Pflicht_zur_Anrede()
    {
        var (dialog, gateway, _) = Create();
        dialog.RequireWakeWord = true;

        Assert.Null(await Say(dialog, "Flaps one"));
        Assert.Null(await Say(dialog, "Banana"));
        Assert.Empty(gateway.Calls);
        Assert.Equal("Flaps 1", (await Say(dialog, "Kopilot, flaps one"))?.Text);
    }

    // ------------------------------------------------------------------ Abfragen im Einzelnen

    [Fact]
    public void Geschwindigkeit_immer_in_Knoten()
    {
        var high = Cruise with { IasKt = 268, PressureAltitudeFt = 35000, BaroHpa = 1013.25 };
        Assert.Equal("Speed 268", VoiceQueries.Answer(QueryKind.Speed, high));
        Assert.Equal("FL350", VoiceQueries.Answer(QueryKind.Altitude, high));
        Assert.Equal("Standard", VoiceQueries.Answer(QueryKind.Baro, high));

        var taxi = Cruise with { OnGround = true, IasKt = 0, GroundSpeedKt = 12 };
        Assert.Equal("Ground speed 12", VoiceQueries.Answer(QueryKind.Speed, taxi));
    }

    [Fact]
    public void Triebwerke_Klappen_Speedbrake()
    {
        Assert.Equal("Engine 2 not running", VoiceQueries.Answer(QueryKind.Engines, Cruise with { Engines = [new(true, 20), new(false, 0)] }));
        Assert.Equal("Engines shut down", VoiceQueries.Answer(QueryKind.Engines, Cruise with { Engines = [new(false, 0), new(false, 0)] }));
        Assert.Equal("N1 split", VoiceQueries.Answer(QueryKind.Engines, Cruise with { Engines = [new(true, 80), new(true, 70)] }));
        Assert.Equal("Flaps 1", VoiceQueries.Answer(QueryKind.Flaps, Cruise with { FlapsLabel = "1+F" }));
        Assert.Equal("Flaps full", VoiceQueries.Answer(QueryKind.Flaps, Cruise with { FlapsLabel = "FULL" }));
        Assert.Equal("Speedbrake half", VoiceQueries.Answer(QueryKind.Speedbrake, Cruise with { SpeedbrakeHandlePct = 50 }));
        Assert.Equal("Speedbrake full", VoiceQueries.Answer(QueryKind.Speedbrake, Cruise with { SpeedbrakeHandlePct = 100 }));
        Assert.Equal("Gear in transit", VoiceQueries.Answer(QueryKind.Gear, Cruise with { GearExtendedPct = 40 }));
        Assert.Equal("Fuel 800 kilos", VoiceQueries.Answer(QueryKind.Fuel, Cruise with { FuelKg = 812 }));
        Assert.Equal("Unable", VoiceQueries.Answer(QueryKind.Speed, new VoiceFlightState()));
    }

    // ------------------------------------------------------------------ Phraseologie

    [Theory]
    [InlineData("Speed 250 selected", "Speed two five zero selected")]
    [InlineData("Speed 250", "Speed two five zero")]
    [InlineData("Altitude 5000 set", "Altitude five thousand set")]
    [InlineData("Altitude 10000", "Altitude one zero thousand")]
    [InlineData("Altitude 12500", "Altitude one two thousand five hundred")]
    [InlineData("Altitude FL120 set", "Altitude flight level one two zero set")]
    [InlineData("FL350", "flight level three five zero")]
    [InlineData("QNH 1013 set", "QNH one zero one three set")]
    [InlineData("Heading 090 selected", "Heading zero niner zero selected")]
    [InlineData("Squawk 7000", "Squawk seven zero zero zero")]
    [InlineData("Vertical speed -1000", "Vertical speed minus one thousand")]
    [InlineData("COM one standby 121.800", "COM one standby one two one decimal eight")]
    [InlineData("Fuel 12.4 tons", "Fuel twelve point four tons")]
    [InlineData("Flaps 1", "Flaps one")]
    [InlineData("Engine 2 not running", "Engine two not running")]
    [InlineData("ND range 40", "ND range forty")]
    [InlineData("Speedbrake retracted", "Speedbrake retracted")]
    public void Zahlen_wie_im_Funk(string text, string spoken)
    {
        Assert.Equal(spoken, VoicePhraseology.ForSpeech(text));
    }

    [Theory]
    [InlineData("4AB", "four alpha bravo")]
    [InlineData("D-AIAB", "delta alpha india alpha bravo")]
    [InlineData("EDDL", "echo delta delta lima")]
    public void Buchstabieren_nach_NATO_Alphabet(string text, string spoken)
    {
        Assert.Equal(spoken, VoicePhraseology.Spell(text));
    }

    // ------------------------------------------------------------------ Kritische Aktion: Rückfrage mit Warnhinweis (v0.9.1)

    [Fact]
    public async Task Kritische_Aktion_Rueckfrage_dann_zweites_Confirm()
    {
        var (dialog, gateway, _) = Create();
        const string caution = "Caution, transponder standby in flight. Confirm transponder standby?";
        gateway.NextPrepare = new InputResult(InputResult.AwaitingConfirmation, Token: "t1", Action: "xpdr.mode", Value: "stby", Readback: "Transponder standby")
        {
            CautionFollows = true,
        };
        gateway.NextConfirm = new InputResult(InputResult.AwaitingCautionConfirmation, Token: "t1", Action: "xpdr.mode", Value: "stby", Readback: caution)
        {
            Caution = caution,
        };

        await Say(dialog, "Flaps one");
        var question = await Say(dialog, "Confirm");

        // Erstes Confirm: nicht ausgeführt, sondern die Rückfrage mit Warnhinweis
        Assert.Equal(caution, question?.Text);
        Assert.Equal("t1", question?.ReadbackToken);
        Assert.NotNull(dialog.Pending);

        gateway.NextConfirm = new InputResult(InputResult.Executed, Action: "xpdr.mode", Value: "stby", Confirmed: true);
        var done = await Say(dialog, "Confirm");

        Assert.Null(done);
        Assert.Null(dialog.Pending);
        Assert.Equal(new[] { "prepare flaps 1", "confirm t1 confirm", "confirm t1 confirm" }, gateway.Calls);
    }

    [Fact]
    public async Task Kritische_Aktion_Negative_bei_der_Rueckfrage()
    {
        var (dialog, gateway, _) = Create();
        gateway.NextConfirm = new InputResult(InputResult.AwaitingCautionConfirmation, Token: "t1", Action: "fcu.athr", Value: "off")
        {
            Caution = "Caution, autothrust off. Confirm autothrust off?",
        };

        await Say(dialog, "Flaps one");
        await Say(dialog, "Confirm");
        Assert.Equal("Cancelled", (await Say(dialog, "Negative"))?.Text);
        Assert.Equal("cancel t1", gateway.Calls[^1]);
    }
}
