using Duopilo.Aircraft;
using Duopilo.Ecam;
using Duopilo.Input;

namespace Duopilo.Tests;

/// <summary>
/// Testflug Headwind A330-900neo (29.10.2026): Batterien, Autobrake, TCAS, Schalterstellungen, NAV &amp; LOGO, Lesedaten,
/// tolerantere Bestätigung bei mark_action_verified und Tokens, die immer zur offenen Aktion gehören.
/// </summary>
public sealed partial class InputTests
{
    private static AircraftProfile A339() => RepoStore().All.Single(p => p.Name == "Headwind A330-900neo");

    private static ProfileAction A339Action(string id) => A339().Actions.Single(a => a.Id == id);

    private static (InputService Service, FakeBackend Backend, ManualTime Time) CreateA339(bool onGround = false)
    {
        var (input, backend, time) = Create();
        backend.Profile = A339();
        backend.Current = onGround ? Ground() : Air(250);
        backend.State["SIM ON GROUND"] = onGround ? 1 : 0;
        return (input, backend, time);
    }

    // ------------------------------------------------------------------ 1. Batterien

    [Fact]
    public void A339_Batterien_erst_BAT_1_dann_BAT_2()
    {
        var profile = A339();
        var battery = profile.Actions.Single(a => a.Id == "elec.battery");

        var on = ActionPlanner.Plan(battery, "on", profile.Limits, Air(150)).Plan!;
        Assert.Equal("Batteries on", on.Readback);
        Assert.Equal(new[] { "L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO", "L:A32NX_OVHD_ELEC_BAT_2_PB_IS_AUTO" }, on.Steps.Select(s => s.Set));
        Assert.All(on.Steps, s => Assert.Equal(1, s.SetValue));
        Assert.True(on.Steps[0].DelayMs >= 500);
        Assert.Empty(on.Requires);

        // Ausschalten nur am Boden
        var off = ActionPlanner.Plan(battery, "off", profile.Limits, Ground()).Plan!;
        Assert.All(off.Steps, s => Assert.Equal(0, s.SetValue));
        Assert.Equal("SIM ON GROUND", Assert.Single(off.Requires).Variable);

        // APU master und APU start fragen genau die Variable ab, die elec.battery für BAT 1 schreibt
        Assert.Contains(profile.Actions.Single(a => a.Id == "apu.master").Requires, r => r.Variable == on.Steps[0].Set);
        Assert.Contains(profile.Actions.Single(a => a.Id == "apu.start").Requires, r => r.Variable == on.Steps[0].Set);
    }

    [Fact]
    public async Task A339_Batterien_einschalten_gibt_APU_master_frei()
    {
        var (input, backend, time) = CreateA339(onGround: true);
        backend.State["L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO"] = 0;
        backend.State["L:A32NX_OVHD_ELEC_BAT_2_PB_IS_AUTO"] = 0;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 0;

        Assert.Contains("BAT 1", (await input.PrepareAsync("apu.master", "on", default)).Reason);

        var battery = await input.PrepareAsync("elec.battery", "on", default);
        Assert.Equal(InputResult.AwaitingConfirmation, battery.Status);
        time.Now += TimeSpan.FromSeconds(2);
        var done = await input.ConfirmAsync(battery.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, done.Status);
        Assert.True(done.Confirmed);
        Assert.Equal("on", done.StateAfterText);
        Assert.Equal(new[] { "L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO=1", "L:A32NX_OVHD_ELEC_BAT_2_PB_IS_AUTO=1" }, backend.Sent);

        Assert.Equal(InputResult.AwaitingConfirmation, (await input.PrepareAsync("apu.master", "on", default)).Status);
    }

    // ------------------------------------------------------------------ 2. Autobrake

    /// <summary>
    /// Auch beim A32NX: A32NX.AUTOBRAKE_SET übernimmt den Parameter 1:1 (0 aus … 3 MAX, a320_systems_wasm/src/autobrakes.rs),
    /// nicht 1–4 wie in a320-events.md – "off" hätte LO gesetzt.
    /// </summary>
    [Theory]
    [InlineData("Headwind A330-900neo", "off", "A32NX.AUTOBRAKE_SET_DISARM", 0)]
    [InlineData("Headwind A330-900neo", "lo", "A32NX.AUTOBRAKE_SET_LO", 1)]
    [InlineData("Headwind A330-900neo", "med", "A32NX.AUTOBRAKE_SET_MED", 2)]
    [InlineData("Headwind A330-900neo", "max", "A32NX.AUTOBRAKE_SET_MAX", 3)]
    [InlineData("FlyByWire A32NX", "off", "A32NX.AUTOBRAKE_SET_DISARM", 0)]
    [InlineData("FlyByWire A32NX", "lo", "A32NX.AUTOBRAKE_SET_LO", 1)]
    [InlineData("FlyByWire A32NX", "med", "A32NX.AUTOBRAKE_SET_MED", 2)]
    [InlineData("FlyByWire A32NX", "max", "A32NX.AUTOBRAKE_SET_MAX", 3)]
    public void Autobrake_eigenes_Event_je_Stellung(string aircraft, string option, string expectedEvent, double armedMode)
    {
        var profile = RepoStore().All.Single(p => p.Name == aircraft);
        var plan = ActionPlanner.Plan(profile.Actions.Single(a => a.Id == "brakes.autobrake"), option, profile.Limits, Ground()).Plan!;

        var step = Assert.Single(plan.Steps);
        Assert.Equal(expectedEvent, step.Event);
        Assert.Equal(0u, step.EventData);
        Assert.Equal("L:A32NX_AUTOBRAKES_ARMED_MODE", plan.Check!.Condition.Variable);
        Assert.Equal(armedMode, plan.Check.Condition.Value);
        Assert.True(plan.Check.Independent);
        Assert.Equal(option == "max", plan.Requires.Any(r => r.Variable == "SIM ON GROUND"));
    }

    [Fact]
    public async Task A339_Autobrake_MAX_am_Boden_uebernommen_in_der_Luft_abgelehnt()
    {
        var (input, backend, time) = CreateA339(onGround: true);
        backend.State["L:A32NX_AUTOBRAKES_ARMED_MODE"] = 0;
        backend.OnEvent = (name, _) =>
        {
            if (name == "A32NX.AUTOBRAKE_SET_MAX")
            {
                backend.State["L:A32NX_AUTOBRAKES_ARMED_MODE"] = 3;
            }
        };

        var prepared = await input.PrepareAsync("brakes.autobrake", "max", default);
        Assert.Equal("Autobrake max", prepared.Readback);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.True(result.Confirmed);
        Assert.Equal(3, result.StateAfter);
        Assert.Equal("max", result.StateAfterText);
        Assert.Equal(new[] { "A32NX.AUTOBRAKE_SET_MAX:0" }, backend.Sent);

        // In der Luft nimmt das Flugzeug MAX nicht an – Duopilo sagt das vorher, statt "stateAfter 0" zu melden
        backend.Current = Air(250);
        backend.State["SIM ON GROUND"] = 0;
        var air = await input.PrepareAsync("brakes.autobrake", "max", default);
        Assert.Equal(InputResult.Refused, air.Status);
        Assert.Contains("on the ground", air.Reason);
        Assert.Equal(InputResult.AwaitingConfirmation, (await input.PrepareAsync("brakes.autobrake", "lo", default)).Status);
    }

    [Fact]
    public void A339_Autobrake_schickt_keine_Werte_ueber_3()
    {
        // Regression: A32NX.AUTOBRAKE_SET mit 1–4 – MAX = 4 hat das WASM als "aus" gelesen
        var profile = A339();
        var action = profile.Actions.Single(a => a.Id == "brakes.autobrake");
        foreach (var option in action.Parameter!.Options!.Keys)
        {
            var plan = ActionPlanner.Plan(action, option, profile.Limits, Ground()).Plan!;
            Assert.DoesNotContain(plan.Steps, s => s.Event == "A32NX.AUTOBRAKE_SET" && s.EventData > 3);
        }
    }

    // ------------------------------------------------------------------ 3. TCAS

    [Fact]
    public void A339_TCAS_prueft_den_Modus_des_TCAS_Rechners()
    {
        var profile = A339();
        var tcas = profile.Actions.Single(a => a.Id == "tcas.mode");
        var tara = ActionPlanner.Plan(tcas, "tara", profile.Limits, Air(150)).Plan!;

        var set = Assert.Single(tara.Steps);
        Assert.Equal("L:A32NX_SWITCH_TCAS_Position", set.Set);
        Assert.Equal(2, set.SetValue);
        Assert.Equal("Enum", set.Unit);
        Assert.Equal("TCAS TA RA", tara.Readback);
        Assert.Equal("L:A32NX_TCAS_MODE", tara.Check!.Condition.Variable);
        Assert.Equal(2, tara.Check.Condition.Value);
        Assert.True(tara.Check.Independent);

        // Transponder ON (3), ALT (4) und am Boden Mode S (5) reichen – STBY (1), OFF (0) und TEST (2) nicht
        var xpdr = tara.Requires.Single(r => r.Variable == "TRANSPONDER STATE:1");
        Assert.All(new double[] { 3, 4, 5 }, s => Assert.True(xpdr.Matches(s), s.ToString()));
        Assert.All(new double[] { 0, 1, 2 }, s => Assert.False(xpdr.Matches(s), s.ToString()));
        Assert.Contains(tara.Requires, r => r.Variable == "L:A32NX_SWITCH_ATC_ALT" && r.Value == 1);

        Assert.Empty(ActionPlanner.Plan(tcas, "stby", profile.Limits, Air(150)).Plan!.Requires);
    }

    [Fact]
    public async Task A339_TCAS_TA_RA_nur_mit_aktivem_Transponder()
    {
        var (input, backend, time) = CreateA339();
        backend.State["TRANSPONDER STATE:1"] = 1; // STBY
        backend.State["L:A32NX_SWITCH_ATC_ALT"] = 1;
        backend.State["L:A32NX_SWITCH_TCAS_Position"] = 0;
        backend.State["L:A32NX_TCAS_MODE"] = 0;
        backend.OnSet = (name, value) =>
        {
            if (name == "L:A32NX_SWITCH_TCAS_Position")
            {
                backend.State["L:A32NX_TCAS_MODE"] = value; // TCAS-Rechner übernimmt die Stellung
            }
        };

        Assert.Contains("STBY", (await input.PrepareAsync("tcas.mode", "tara", default)).Reason);

        backend.State["TRANSPONDER STATE:1"] = 4; // ALT
        var prepared = await input.PrepareAsync("tcas.mode", "tara", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.True(result.Confirmed);
        Assert.True(result.CheckIndependent);
        Assert.Equal("tara", result.StateAfterText);
        Assert.Equal(new[] { "L:A32NX_SWITCH_TCAS_Position=2" }, backend.Sent);
        Assert.Contains("L:A32NX_SWITCH_TCAS_Position|Enum", backend.Reads);
        Assert.Contains("L:A32NX_TCAS_MODE|Enum", backend.Reads);
    }

    // ------------------------------------------------------------------ 4./5. Schalterstellungen: Rohwert und Name

    [Theory]
    [InlineData("signs.seatbelts", 0, "on")]
    [InlineData("signs.seatbelts", 1, "auto")]
    [InlineData("signs.seatbelts", 2, "off")]
    [InlineData("signs.noSmoking", 1, "auto")]
    [InlineData("lights.nose", 0, "to")]
    [InlineData("lights.nose", 1, "taxi")]
    [InlineData("lights.nose", 2, "off")]
    [InlineData("lights.landing", 0, "off/retract")]
    [InlineData("tcas.mode", 2, "tara")]
    [InlineData("brakes.autobrake", 0, "off")]
    public void A339_Schalterstellung_wird_benannt(string id, double state, string expected) =>
        Assert.Equal(expected, ActionPlanner.StateName(A339Action(id), state));

    [Fact]
    public void Zahlenwerte_und_unbekannte_Stellungen_haben_keinen_Namen()
    {
        Assert.Null(ActionPlanner.StateName(A339Action("fcu.speed"), 250));
        Assert.Null(ActionPlanner.StateName(A339Action("signs.seatbelts"), 7));
        Assert.Null(ActionPlanner.StateName(A339Action("gear.down"), 1));
    }

    [Fact]
    public void A339_Zuordnung_der_Wahlschalter_wie_im_Cockpitmodell()
    {
        // A330_NEO_INTERIOR.xml: Tooltip 0 = ON / T.O, 1 = AUTO / TAXI, 2 = OFF – im Reiseflug gelesen: Seatbelts OFF = 2, Nose OFF = 2
        Assert.Equal(new Dictionary<string, double> { ["on"] = 0, ["auto"] = 1, ["off"] = 2 }, A339Action("signs.seatbelts").Parameter!.Options);
        Assert.Equal(new Dictionary<string, double> { ["on"] = 0, ["auto"] = 1, ["off"] = 2 }, A339Action("signs.noSmoking").Parameter!.Options);
        Assert.Equal(new Dictionary<string, double> { ["to"] = 0, ["taxi"] = 1, ["off"] = 2 }, A339Action("lights.nose").Parameter!.Options);
    }

    [Fact]
    public async Task A339_Seatbelts_on_meldet_Stellung_0_als_on()
    {
        var (input, backend, time) = CreateA339();
        backend.State["L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position"] = 2;
        backend.State["CABIN SEATBELTS ALERT SWITCH"] = 0;
        backend.OnEvent = (name, _) =>
        {
            if (name == "CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE")
            {
                backend.State["CABIN SEATBELTS ALERT SWITCH"] = 1 - backend.State["CABIN SEATBELTS ALERT SWITCH"];
            }
        };

        var prepared = await input.PrepareAsync("signs.seatbelts", "on", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.True(result.Confirmed);
        Assert.Equal(0, result.StateAfter);
        Assert.Equal("on", result.StateAfterText);
        Assert.Equal(1, backend.State["CABIN SEATBELTS ALERT SWITCH"]);
        Assert.Equal(new[] { "L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position=0", "CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE:0" }, backend.Sent);

        // Der Pilot sieht es im Cockpit: "Sind on" reicht zum Merken
        Assert.Equal(InputResult.Executed, input.MarkVerified("signs.seatbelts", "Sind on").Status);
        Assert.Equal("pilot", input.VerifiedBy(backend.Profile!.Actions.Single(a => a.Id == "signs.seatbelts")));
    }

    [Fact]
    public async Task Fehlgeschlagene_Pruefung_nennt_Stellungen_beim_Namen()
    {
        var (input, backend, time) = CreateA339();
        backend.State["L:LIGHTING_LANDING_1"] = 2;
        backend.State["CIRCUIT SWITCH ON:17"] = 0;
        backend.State["CIRCUIT SWITCH ON:20"] = 0;
        backend.RevertAfterRead["L:LIGHTING_LANDING_1"] = 2; // Flugzeug setzt den Schalter zurück

        var prepared = await input.PrepareAsync("lights.nose", "to", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.False(result.Confirmed);
        Assert.Equal("off", result.StateAfterText);
        Assert.Contains("(off)", input.History[0].Detail);
    }

    // ------------------------------------------------------------------ 6. mark_action_verified

    [Theory]
    [InlineData("signs.noSmoking", "Confirm, no smoking, Auto", true)]
    [InlineData("signs.seatbelts", "Sind on", true)]
    [InlineData("signs.seatbelts", "Seatbelts", true)]
    [InlineData("apu.start", "APU Avail", true)]
    [InlineData("apu.start", "APU läuft", true)]
    [InlineData("apu.bleed", "check", true)]
    [InlineData("tcas.mode", "TCAS TA RA", true)]
    [InlineData("elec.battery", "Batteries on", true)]
    [InlineData("signs.noSmoking", "No, no smoking is still off", false)]
    [InlineData("signs.seatbelts", "seatbelts did not come on", false)]
    [InlineData("apu.start", "APU not avail", false)]
    [InlineData("apu.start", "negative", false)]
    [InlineData("apu.start", "hm", false)]
    [InlineData("apu.start", "", false)]
    [InlineData("lights.nose", "nein, Licht ist aus", false)]
    [InlineData("lights.nose", "immer noch aus", false)]
    public void Bestaetigung_fuer_mark_action_verified(string id, string words, bool expected) =>
        Assert.Equal(expected, InputService.PilotConfirmsWorked(A339Action(id), words));

    [Fact]
    public void Ausgefuehrter_Wert_gilt_als_Bestaetigung()
    {
        var action = A339Action("brakes.autobrake");
        Assert.True(InputService.PilotConfirmsWorked(action, "MAX", "max"));
        Assert.False(InputService.PilotConfirmsWorked(action, "MAX", null));
    }

    [Fact]
    public async Task Ausfuehren_bleibt_streng()
    {
        // confirm_action akzeptiert weiterhin nur ein klares "confirm" – Zustandswörter gelten nur fürs Merken
        Assert.True(InputService.PilotConfirmsWorked(A339Action("lights.nav"), "on"));
        var (input, backend, time) = CreateA339();
        var prepared = await input.PrepareAsync("lights.nav", "on", default);
        time.Now += TimeSpan.FromSeconds(2);

        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "on", default)).Status);
        Assert.NotNull(input.Pending);
        Assert.Empty(backend.Sent);
    }

    // ------------------------------------------------------------------ 7. Token gehört immer zur offenen Aktion

    [Fact]
    public async Task Langsames_prepare_ueberschreibt_kein_neueres()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = 1;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Gates["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = gate;

        // Erster Aufruf hängt beim Lesen (beim Client als fehlgeschlagen gemeldet), der zweite kommt durch
        var slow = input.PrepareAsync("apu.master", "on", default);
        var fresh = await input.PrepareAsync("gear.up", null, default);
        Assert.Equal(InputResult.AwaitingConfirmation, fresh.Status);

        gate.SetResult();
        var late = await slow;

        Assert.Equal(InputResult.Failed, late.Status);
        Assert.Null(late.Token);
        Assert.Equal(fresh.Token, input.Pending!.Token);
        Assert.Equal("gear.up", input.Pending.Plan.Action.Id);

        time.Now += TimeSpan.FromSeconds(3);
        var done = await input.ConfirmAsync(fresh.Token!, "confirm", default);
        Assert.Equal(InputResult.Executed, done.Status);
        Assert.Equal(new[] { "GEAR_UP:0" }, backend.Sent);
    }

    [Fact]
    public async Task Alter_Token_nennt_was_passiert_ist()
    {
        var (input, backend, time) = Create();
        var first = await input.PrepareAsync("gear.up", null, default);
        var second = await input.PrepareAsync("efis.ls", "on", default);
        time.Now += TimeSpan.FromSeconds(3);

        var result = await input.ConfirmAsync(first.Token!, "confirm", default);

        Assert.Equal(InputResult.Failed, result.Status);
        Assert.Contains("replaced", result.Reason);
        Assert.Contains("efis.ls", result.Reason);
        Assert.DoesNotContain(second.Token!, result.Reason);
        Assert.Empty(backend.Sent);
        Assert.Equal(second.Token, input.Pending!.Token);
    }

    [Fact]
    public async Task Fehlgeschlagenes_prepare_verwirft_die_offene_Aktion()
    {
        var (input, backend, time) = Create();
        var open = await input.PrepareAsync("gear.up", null, default);
        backend.Current = Air(190);

        var refused = await input.PrepareAsync("flaps", "3", default);

        Assert.Equal(InputResult.Refused, refused.Status);
        Assert.Null(refused.Token);
        Assert.Null(input.Pending);
        time.Now += TimeSpan.FromSeconds(3);
        var result = await input.ConfirmAsync(open.Token!, "confirm", default);
        Assert.Equal(InputResult.Failed, result.Status);
        Assert.Contains("replaced", result.Reason);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Abgebrochener_Aufruf_legt_keine_Aktion_an()
    {
        var (input, _, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await input.PrepareAsync("gear.up", null, cts.Token);

        Assert.Equal(InputResult.Failed, result.Status);
        Assert.Null(result.Token);
        Assert.Null(input.Pending);
    }

    [Fact]
    public async Task Geteilte_Anzeige_kennt_den_Token_nur_als_Hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N"));
        var (input, _, _) = Create(dataDir: dir);

        var prepared = await input.PrepareAsync("gear.up", null, default);

        var shared = new InputSettingsStore(dir).LoadPending()!;
        Assert.Equal("gear.up", shared.Action);
        Assert.Matches("^[0-9a-f]{16}$", shared.TokenHash);
        Assert.DoesNotContain(prepared.Token!, shared.TokenHash);
    }

    // ------------------------------------------------------------------ 8./9. Lesedaten, NAV & LOGO

    [Fact]
    public void A339_Lesedaten_ECAM_APU_Elektrik_Treibstoff()
    {
        var profile = A339();

        Assert.Equal("hdw-a339x", profile.Ecam!.Format);
        Assert.True(EcamCatalog.IsKnownFormat(profile.Ecam.Format));
        Assert.Equal("L:A32NX_EWD_LOWER_LEFT_LINE_{n}", profile.Ecam.LeftLines);
        Assert.Equal("L:A32NX_EWD_LOWER_RIGHT_LINE_{n}", profile.Ecam.RightLines);

        Assert.Equal("L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON", profile.Bindings[BindingKeys.ApuBleed].Name);
        Assert.Equal(A339Action("apu.bleed").Check!.Variable, profile.Bindings[BindingKeys.ApuBleed].Name);
        Assert.Contains(profile.Electrical, e => e.Key == "apuGenOn" && e.Name == "APU GENERATOR SWITCH:1");
        Assert.Contains(profile.Electrical, e => e.Key == "apuGenVolt");

        Assert.Equal(
            new[] { "FUELSYSTEM PUMP SWITCH:2", "FUELSYSTEM PUMP SWITCH:5", "FUELSYSTEM PUMP SWITCH:3", "FUELSYSTEM PUMP SWITCH:6" },
            profile.Fuel.Where(f => f.Key is "pumpL1" or "pumpL2" or "pumpR1" or "pumpR2").Select(f => f.Name));
        Assert.Equal("ON", profile.Fuel.Single(f => f.Key == "pumpL1").Translate(1.0));

        // GEN-Pushbutton aus dem Modell statt der L:-Variable, die im Flug immer 0 las
        Assert.DoesNotContain(profile.Variables, v => v.Name == "L:A32NX_OVHD_ELEC_ENG_GEN_1_PB_IS_ON");

        // Andere Profile unverändert
        var a32nx = RepoStore().All.Single(p => p.Name == "FlyByWire A32NX");
        Assert.Empty(a32nx.Fuel);
        Assert.Equal("fbw-a32nx", a32nx.Ecam!.Format);
        Assert.DoesNotContain(a32nx.Actions, a => a.Id is "elec.battery" or "lights.nav" or "lights.logo");
    }

    [Fact]
    public void Headwind_ECAM_Tabelle_wird_gelesen()
    {
        var entries = EcamCatalog.Parse("""
            const EwdGroups: Record<string, string> = {
              'ELEC$3': '\x1b<4m\x1b4mELEC\x1bm',
            };
            export const EcamMemos: { [n: string]: string } = {
              ['000007001', { text: '\x1b<3mIGNITION' }],
              ['000019002', { text: '\x1b<4mELEC EXT PWR' }],
            };
            """);

        Assert.Equal("IGNITION", entries["000007001"].Text);
        Assert.Equal("ELEC EXT PWR", entries["000019002"].Text);
    }

    [Fact]
    public void A339_Nav_und_Logo()
    {
        var profile = A339();

        var nav = ActionPlanner.Plan(profile.Actions.Single(a => a.Id == "lights.nav"), "on", profile.Limits, Air(150)).Plan!;
        Assert.Equal("Nav and logo lights on", nav.Readback);
        Assert.Equal(new[] { "LOGO_LIGHTS_SET", "NAV_LIGHTS_SET" }, nav.Steps.Select(s => s.Event));
        Assert.All(nav.Steps, s => Assert.Equal(1u, s.EventData));
        Assert.Equal("LIGHT NAV", nav.Check!.Condition.Variable);
        Assert.True(nav.Check.Independent);

        var logo = ActionPlanner.Plan(profile.Actions.Single(a => a.Id == "lights.logo"), "off", profile.Limits, Air(150)).Plan!;
        var step = Assert.Single(logo.Steps);
        Assert.Equal("LOGO_LIGHTS_SET", step.Event);
        Assert.Equal(0u, step.EventData);
        Assert.Equal("LIGHT LOGO", logo.Check!.Condition.Variable);
        Assert.Equal(0, logo.Check.Condition.Value);
    }
}
