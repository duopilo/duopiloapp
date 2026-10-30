using Duopilo.Input;
using Duopilo.Voice;

namespace Duopilo.Tests;

/// <summary>
/// Modus "ausführen + Callout" (v0.9.2, Punkt 3): sicher erkannte Befehle sofort ausführen und nach der Prüfung ansagen;
/// Readback mit "Confirm" nur bei unsicherer Erkennung und bei Aktionen mit Rückfrage. Gesperrtes bleibt gesperrt.
/// </summary>
public sealed class VoiceExecuteModeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public InputResult NextExecute { get; set; } = new(InputResult.Executed, Action: "fcu.speed", Value: "250", Readback: "Speed 250 selected", Confirmed: true)
        {
            Callout = "Speed 250 set",
        };

        public InputResult NextPrepare { get; set; } = new(InputResult.AwaitingConfirmation, Token: "t1", Action: "fcu.speed", Value: "250", Readback: "Speed 250 selected");

        public InputResult NextConfirm { get; set; } = new(InputResult.Executed, Confirmed: true);

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(NextPrepare);
        }

        public Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"execute {actionId} {value}");
            return Task.FromResult(NextExecute);
        }

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct)
        {
            Calls.Add($"confirm {token}");
            return Task.FromResult(NextConfirm);
        }

        public InputResult Cancel(string token, string reason)
        {
            Calls.Add($"cancel {token}");
            return new InputResult(InputResult.Cancelled);
        }
    }

    private sealed class NoState : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => new() { Connected = true };
    }

    private static (VoiceDialog Dialog, Gateway Gateway) Create()
    {
        var gateway = new Gateway();
        return (new VoiceDialog(gateway, new NoState(), new ManualTime(T0)), gateway);
    }

    private static Task<VoiceReply?> Say(VoiceDialog dialog, string raw, double? confidence = 0.9)
    {
        var normalized = VoiceText.Normalize(raw);
        return dialog.HandleAsync(VoiceIntents.Recognize(normalized, ["kopilot"]), normalized, false, T0, confidence, CancellationToken.None);
    }

    [Fact]
    public void Standard_ist_ausfuehren_und_Callout()
    {
        Assert.True(new VoiceSettings().Normalized().Commands.ExecuteWithCallout);
        Assert.Equal(0.6, new VoiceSettings().Normalized().Commands.MinConfidence);
        Assert.True(Create().Dialog.ExecuteWithCallout);
    }

    [Fact]
    public async Task Sicher_erkannt_sofort_ausfuehren_und_nach_der_Pruefung_ansagen()
    {
        var (dialog, gateway) = Create();

        var reply = await Say(dialog, "Speed two five zero");

        Assert.Equal("Speed 250 set", reply?.Text);
        Assert.Equal("Speed two five zero set", reply?.Spoken);
        Assert.Null(reply?.ReadbackToken);
        Assert.Null(dialog.Pending);
        Assert.True(dialog.LastExecutedDirectly);
        Assert.Equal(new[] { "execute fcu.speed 250" }, gateway.Calls);
    }

    [Fact]
    public async Task Klappen_mit_Callout_aus_dem_Readback()
    {
        var (dialog, gateway) = Create();
        gateway.NextExecute = new InputResult(InputResult.Executed, Action: "flaps", Value: "2", Readback: "Flaps two", Confirmed: true) { Callout = "Flaps two" };

        Assert.Equal("Flaps two", (await Say(dialog, "Flaps two"))?.Text);
    }

    [Fact]
    public async Task Fahrwerk_sagt_das_Nachlesen_an()
    {
        var (dialog, gateway) = Create();
        gateway.NextExecute = new InputResult(InputResult.Executed, Action: "gear.down", Readback: "Gear down", Confirmed: true)
        {
            Callout = "Gear down",
            FollowUp = "gear down and locked",
        };

        // "Gear down, three green" kommt vom Nachlesen, sobald alle Beine verriegelt sind – hier nichts doppelt
        Assert.Null(await Say(dialog, "Gear down"));
        Assert.Equal("execute gear.down ", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Nicht_uebernommen_heisst_Not_confirmed()
    {
        var (dialog, gateway) = Create();
        gateway.NextExecute = new InputResult(InputResult.Executed, Action: "fcu.speed", Value: "250", Confirmed: false) { Callout = "Speed 250 set" };

        Assert.Equal(VoiceDialog.NotConfirmed, (await Say(dialog, "Speed 250"))?.Text);
    }

    [Fact]
    public async Task Unsicher_erkannt_Readback_mit_Confirm()
    {
        var (dialog, gateway) = Create();

        var reply = await Say(dialog, "Speed two five zero", confidence: 0.41);

        Assert.Equal("Speed 250 selected", reply?.Text);
        Assert.Equal("t1", reply?.ReadbackToken);
        Assert.Equal(new[] { "prepare fcu.speed 250" }, gateway.Calls);
        Assert.NotNull(dialog.Pending);
    }

    [Fact]
    public async Task Schwelle_ist_einstellbar()
    {
        var (dialog, gateway) = Create();
        dialog.MinConfidence = 0.95;

        await Say(dialog, "Speed 250", confidence: 0.9);

        Assert.Equal("prepare fcu.speed 250", gateway.Calls[^1]);
    }

    [Fact]
    public async Task Aktion_mit_Rueckfrage_geht_den_zweistufigen_Weg()
    {
        var (dialog, gateway) = Create();
        gateway.NextExecute = new InputResult(InputResult.AwaitingConfirmation, Token: "t9", Action: "xpdr.mode", Value: "stby", Readback: "Transponder standby")
        {
            CautionFollows = true,
        };

        var reply = await Say(dialog, "Transponder standby");

        // Kein sofortiges Ausführen: Readback, auf den "Confirm" folgt (danach die Rückfrage mit Warnhinweis)
        Assert.Equal("Transponder standby", reply?.Text);
        Assert.Equal("t9", reply?.ReadbackToken);
        Assert.Equal("t9", dialog.Pending?.Token);
        Assert.False(dialog.LastExecutedDirectly);
    }

    [Fact]
    public async Task Gesperrtes_bleibt_gesperrt()
    {
        var (dialog, gateway) = Create();
        gateway.NextExecute = new InputResult(InputResult.Refused, "Batterien aus nur am Boden.");

        Assert.Equal(VoiceDialog.Unable, (await Say(dialog, "Batteries off"))?.Text);
        Assert.Null(dialog.Pending);

        gateway.NextExecute = new InputResult(InputResult.Disabled, "gesperrt");
        Assert.Equal(VoiceDialog.InputDisabled, (await Say(dialog, "Speed 250"))?.Text);
    }

    [Fact]
    public async Task Modus_aus_jeder_Befehl_mit_Readback()
    {
        var (dialog, gateway) = Create();
        dialog.ExecuteWithCallout = false;

        await Say(dialog, "Speed 250", confidence: 0.99);

        Assert.Equal(new[] { "prepare fcu.speed 250" }, gateway.Calls);
    }

    [Fact]
    public void Konfidenz_wird_aus_den_Einstellungen_gelesen()
    {
        var s = VoiceSettingsStore.Parse("""{ "commands": { "executeWithCallout": false, "minConfidence": 1.7 } }""");

        Assert.False(s.Commands.ExecuteWithCallout);
        Assert.Equal(1.0, s.Commands.MinConfidence);
    }
}
