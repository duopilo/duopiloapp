using Kopilot.Input;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Checklisten als Challenge-Response nach der Airbus Normal Checklist (v0.9.2, Punkt 6): Kopilot liest die Challenge, wartet auf
/// die Antwort, gleicht – wo möglich – mit dem Sim ab und meldet Abweichungen. Start per Sprache.
/// </summary>
public sealed class ChecklistTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class State : IVoiceFlightStateSource
    {
        public VoiceFlightState Current { get; set; } = new() { Connected = true, OnGround = true };

        public VoiceFlightState Read() => Current;
    }

    private sealed class Gateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.AwaitingConfirmation, Token: "t1", Action: actionId, Value: value, Readback: "Readback"));
        }

        public Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"execute {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.Executed, Action: actionId, Value: value, Confirmed: true) { Callout = $"{actionId} {value} set" });
        }

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct) =>
            Task.FromResult(new InputResult(InputResult.Executed, Confirmed: true));

        public InputResult Cancel(string token, string reason) => new(InputResult.Cancelled);
    }

    private static (VoiceDialog Dialog, Gateway Gateway, State State, ManualTime Time) Create()
    {
        var gateway = new Gateway();
        var state = new State();
        var time = new ManualTime(T0);
        return (new VoiceDialog(gateway, state, time), gateway, state, time);
    }

    private static Task<VoiceReply?> Say(VoiceDialog dialog, string raw, DateTimeOffset? at = null)
    {
        var normalized = VoiceText.Normalize(raw);
        var addressed = normalized.StartsWith("kopilot ", StringComparison.Ordinal);
        return dialog.HandleAsync(VoiceIntents.Recognize(normalized, ["kopilot"]), normalized, addressed, at, 0.9, CancellationToken.None);
    }

    // ------------------------------------------------------------------ Erkennung

    [Theory]
    [InlineData("Before start checklist", "beforeStart")]
    [InlineData("Checkliste vor dem Anlassen", "beforeStart")]
    [InlineData("After start checklist", "afterStart")]
    [InlineData("Checkliste nach dem Anlassen", "afterStart")]
    [InlineData("Before takeoff checklist", "beforeTakeoff")]
    [InlineData("Before take off check list", "beforeTakeoff")]
    [InlineData("Checkliste vor dem Start", "beforeTakeoff")]
    [InlineData("Start approach checklist", "approach")]
    [InlineData("Lies die Anflug Checkliste", "approach")]
    [InlineData("Landing checklist", "landing")]
    [InlineData("Starte Checkliste Landung", "landing")]
    [InlineData("After landing checklist", "afterLanding")]
    [InlineData("Checkliste nach der Landung", "afterLanding")]
    [InlineData("Parking checklist", "parking")]
    [InlineData("Parken Checkliste", "parking")]
    [InlineData("Kopilot, before takeoff checklist", "beforeTakeoff")]
    public void Checkliste_per_Sprache_starten(string raw, string id)
    {
        var intent = VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]);

        Assert.Equal(IntentKind.Checklist, intent.Kind);
        Assert.Equal(id, intent.Value);
        Assert.NotNull(AirbusChecklists.Find(id));
    }

    [Theory]
    [InlineData("Stop checklist", "stop")]
    [InlineData("Checkliste abbrechen", "stop")]
    [InlineData("Skip", "skip")]
    [InlineData("Next item", "skip")]
    [InlineData("Punkt überspringen", "skip")]
    public void Steuerung_der_Checkliste(string raw, string value)
    {
        var intent = VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]);

        Assert.Equal(IntentKind.ChecklistControl, intent.Kind);
        Assert.Equal(value, intent.Value);
    }

    [Fact]
    public void Alle_sieben_Checklisten_der_Airbus_Normal_Checklist()
    {
        Assert.Equal(
            new[] { "beforeStart", "afterStart", "beforeTakeoff", "approach", "landing", "afterLanding", "parking" },
            AirbusChecklists.All.Select(c => c.Id));
        Assert.All(AirbusChecklists.All, c => Assert.NotEmpty(c.Items));
        Assert.Contains(AirbusChecklists.Find("landing")!.Items, i => i.Challenge == "ECAM memo" && i.Checked);
    }

    // ------------------------------------------------------------------ Ablauf

    [Fact]
    public async Task Before_start_mit_Abgleich_und_offenem_Punkt()
    {
        var (dialog, gateway, state, _) = Create();
        state.Current = state.Current with { SeatbeltsOn = true, FuelKg = 12_400, BaroHpa = 1013.25, BeaconOn = false };

        var start = await Say(dialog, "Before start checklist");
        Assert.Equal("Before start checklist, cockpit preparation", start?.Text);
        Assert.True(start?.Checklist);

        Assert.Equal("Gear pins and covers", (await Say(dialog, "Completed"))?.Text);
        Assert.Equal("Signs", (await Say(dialog, "Removed"))?.Text);
        Assert.Equal("ADIRS", (await Say(dialog, "On and auto"))?.Text);
        Assert.Equal("Fuel quantity", (await Say(dialog, "Nav"))?.Text);
        Assert.Equal("Takeoff data", (await Say(dialog, "twelve point four"))?.Text);
        Assert.Equal("Baro reference", (await Say(dialog, "Set"))?.Text);
        Assert.Equal("Windows and doors", (await Say(dialog, "Standard"))?.Text);
        Assert.Equal("Beacon", (await Say(dialog, "Closed"))?.Text);

        // Beacon im Sim aus: Abweichung melden, Punkt bleibt offen; beim zweiten Mal als offen vermerken und weiter
        Assert.Equal("Beacon off", (await Say(dialog, "On"))?.Text);
        Assert.Equal("Thrust levers", (await Say(dialog, "On"))?.Text);
        Assert.Equal("Parking brake", (await Say(dialog, "Idle"))?.Text);

        var done = await Say(dialog, "Set");
        Assert.Equal("Before start checklist complete, open: beacon", done?.Text);
        Assert.False(done?.Checklist);
        Assert.Null(dialog.Checklists.Active);

        // Antworten wie "Standard" oder "Set" sind in der Checkliste nie Befehle
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Abweichung_korrigiert_dann_weiter()
    {
        var (dialog, _, state, _) = Create();
        state.Current = state.Current with { SeatbeltsOn = false };
        await Say(dialog, "Approach checklist");
        await Say(dialog, "Confirmed");
        Assert.Equal("Seat belts", (await Say(dialog, "Checked"))?.Text);

        Assert.Equal("Seat belts off", (await Say(dialog, "On"))?.Text);
        state.Current = state.Current with { SeatbeltsOn = true };
        Assert.Equal("Baro reference", (await Say(dialog, "On"))?.Text);
        Assert.Empty(dialog.Checklists.Open);
    }

    [Theory]
    [InlineData(1013.25, "one zero one three", "Baro standard")]
    [InlineData(1008.0, "one zero one three", "Baro QNH 1008")]
    [InlineData(1008.0, "standard", "Baro QNH 1008")]
    [InlineData(1008.2, "one zero zero eight", null)]
    [InlineData(1013.25, "standard", null)]
    public void Baro_reference_gegen_den_Hoehenmesser(double hpa, string answer, string? deviation)
    {
        var item = AirbusChecklists.Find("approach")!.Items.Single(i => i.Challenge == "Baro reference");

        Assert.Equal(deviation, item.Check!(new ChecklistContext(new VoiceFlightState { BaroHpa = hpa }, VoiceText.Normalize(answer))));
    }

    [Theory]
    [InlineData("twelve point four", null)]
    [InlineData("twelve thousand two hundred", null)]
    [InlineData("ten point five", "Fuel on board 12.4")]
    [InlineData("checked", null)]
    public void Fuel_quantity_gegen_den_Sim(string answer, string? deviation)
    {
        var item = AirbusChecklists.Find("beforeStart")!.Items.Single(i => i.Challenge == "Fuel quantity");

        Assert.Equal(deviation, item.Check!(new ChecklistContext(new VoiceFlightState { FuelKg = 12_400 }, VoiceText.Normalize(answer))));
    }

    [Fact]
    public void Landing_checklist_gleicht_Autothrust_Autobrake_und_Landing_Memo_ab()
    {
        var landing = AirbusChecklists.Find("landing")!;
        var athr = landing.Items.Single(i => i.Challenge == "Autothrust");
        var autobrake = landing.Items.Single(i => i.Challenge == "Autobrake");
        var memo = landing.Items.Single(i => i.Challenge == "ECAM memo");

        Assert.Equal("Autothrust armed", athr.Check!(new(new VoiceFlightState { AutothrustStatus = "ARMED" }, "speed")));
        Assert.Equal("Autothrust off", athr.Check!(new(new VoiceFlightState { AutothrustStatus = "OFF" }, "speed")));
        Assert.Null(athr.Check!(new(new VoiceFlightState { AutothrustStatus = "ACTIVE" }, "speed")));
        Assert.Equal("Autobrake low", autobrake.Check!(new(new VoiceFlightState { AutobrakeMode = "LO" }, "medium")));
        Assert.Null(autobrake.Check!(new(new VoiceFlightState { AutobrakeMode = "MED" }, "medium")));

        var configured = new VoiceFlightState
        {
            GearExtendedPct = 100, SeatbeltsOn = true, SpoilersArmed = true, FlapsLabel = "FULL", LandingFlaps = ["3", "FULL"],
        };
        Assert.Null(memo.Check!(new(configured, "landing no blue")));
        Assert.Equal("Spoilers not armed", memo.Check!(new(configured with { SpoilersArmed = false }, "landing no blue")));
        Assert.Equal(
            "Gear not down, flaps not in landing configuration",
            memo.Check!(new(configured with { GearExtendedPct = 0, FlapsLabel = "2" }, "landing no blue")));
    }

    [Fact]
    public void Parking_meldet_laufende_Triebwerke_und_Schilder()
    {
        var parking = AirbusChecklists.Find("parking")!;
        var engines = parking.Items.Single(i => i.Challenge == "Engines");
        var belts = parking.Items.Single(i => i.Challenge == "Seat belts");

        Assert.Equal("Engine 2 running", engines.Check!(new(new VoiceFlightState { Engines = [new(false, 0), new(true, 20)] }, "off")));
        Assert.Equal("Engines running", engines.Check!(new(new VoiceFlightState { Engines = [new(true, 20), new(true, 20)] }, "off")));
        Assert.Null(engines.Check!(new(new VoiceFlightState { Engines = [new(false, 0), new(false, 0)] }, "off")));
        Assert.Equal("Seat belts on", belts.Check!(new(new VoiceFlightState { SeatbeltsOn = true }, "off")));
    }

    [Fact]
    public async Task Skip_Say_again_Standby_und_Stop()
    {
        var (dialog, _, _, _) = Create();
        await Say(dialog, "After start checklist");

        // Standby: keine Antwort, Punkt bleibt
        Assert.Null(await Say(dialog, "Standby"));
        Assert.Equal("Anti ice", dialog.Checklists.Current?.Challenge);

        Assert.Equal("After start checklist, anti ice", (await Say(dialog, "Say again"))?.Text);
        Assert.Equal("ECAM status", (await Say(dialog, "Skip"))?.Text);
        Assert.Equal(new[] { "Anti ice" }, dialog.Checklists.Open);

        var stop = await Say(dialog, "Stop checklist");
        Assert.Equal("After start checklist interrupted", stop?.Text);
        Assert.Null(dialog.Checklists.Active);

        // Ohne Checkliste ist "Skip" nichts
        Assert.Null(await Say(dialog, "Skip"));
    }

    [Fact]
    public async Task Befehle_nur_mit_Anrede_waehrend_der_Checkliste()
    {
        var (dialog, gateway, _, _) = Create();
        await Say(dialog, "Before start checklist");

        // "Seat belts on" ist hier die Antwort auf die Challenge – nicht ausführen
        Assert.Equal("Gear pins and covers", (await Say(dialog, "Seat belts on"))?.Text);
        Assert.Empty(gateway.Calls);

        // Mit Anrede: Befehl, die Checkliste bleibt beim selben Punkt
        Assert.Equal("fcu.speed 250 set", (await Say(dialog, "Kopilot, speed 250"))?.Text);
        Assert.Equal(new[] { "execute fcu.speed 250" }, gateway.Calls);
        Assert.Equal("Gear pins and covers", dialog.Checklists.Current?.Challenge);
    }

    [Fact]
    public async Task Ohne_Antwort_die_Challenge_einmal_wiederholen()
    {
        var (dialog, _, _, time) = Create();
        var start = await Say(dialog, "Landing checklist");
        dialog.Spoken(start!, T0 + TimeSpan.FromSeconds(3));

        time.Now = T0 + TimeSpan.FromSeconds(32);
        Assert.Null(dialog.Tick());

        time.Now = T0 + TimeSpan.FromSeconds(34);
        var repeat = dialog.Tick();
        Assert.Equal("Landing checklist, cabin crew", repeat?.Text);
        Assert.True(repeat?.Checklist);

        // Nicht endlos
        time.Now = T0 + TimeSpan.FromSeconds(120);
        Assert.Null(dialog.Tick());
        Assert.Equal("Cabin crew", dialog.Checklists.Current?.Challenge);
    }

    [Fact]
    public async Task Neue_Checkliste_ersetzt_die_laufende()
    {
        var (dialog, _, _, _) = Create();
        await Say(dialog, "Before start checklist");

        Assert.Equal("Approach checklist, briefing", (await Say(dialog, "Approach checklist"))?.Text);
        Assert.Equal("approach", dialog.Checklists.Active?.Id);
    }
}
