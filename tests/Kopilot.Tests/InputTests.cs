using System.Text.Json;
using Kopilot.Aircraft;
using Kopilot.Input;
using Kopilot.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kopilot.Tests;

/// <summary>Eingabe-Modul (v0.7): Planung, Sperren, Sperrliste und der zweistufige Ablauf mit Bestätigung.</summary>
public sealed class InputTests
{
    private static readonly AircraftLimits Limits = new()
    {
        VleKt = 280,
        VfeKt = new Dictionary<string, double> { ["1"] = 230, ["1+F"] = 215, ["2"] = 200, ["3"] = 185, ["FULL"] = 177 },
    };

    private static FlightSnapshot Air(double ias = 250) => new()
    {
        Connected = true, FlightRunning = true, DataAgeSec = 0.1, OnGround = false, IasKt = ias, AltitudeFt = 8000,
    };

    private static FlightSnapshot Ground() => Air(0) with { OnGround = true };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ProfileAction Speed() => new()
    {
        Id = "fcu.speed", Area = "FCU", Label = "FCU speed", Readback = "Speed {value} selected",
        Parameter = new ActionParameter { Min = 100, Max = 399, Step = 1, Unit = "kt" },
        Steps =
        [
            new ActionStep { Event = "A32NX.FCU_SPD_SET", Value = J("\"{value}\"") },
            new ActionStep { Event = "A32NX.FCU_SPD_PULL", DelayMs = 0 },
        ],
        Check = new ActionCheck { Variable = "L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE", Expect = J("\"{value}\""), Tolerance = 0.5 },
    };

    private static ProfileAction Flaps() => new()
    {
        Id = "flaps", Area = "Config", Label = "Flaps", Readback = "Flaps {value}",
        Parameter = new ActionParameter { Type = "choice", Options = new() { ["0"] = 0, ["1"] = 1, ["2"] = 2, ["3"] = 3, ["FULL"] = 4 } },
        Steps = [new ActionStep { Set = "L:A32NX_FLAPS_HANDLE_INDEX", Value = J("\"{value}\"") }],
        Interlocks = [Interlocks.FlapsSpeed],
    };

    private static ProfileAction GearUp() => new()
    {
        Id = "gear.up", Area = "Config", Label = "Gear up", Readback = "Gear up",
        Steps = [new ActionStep { Event = "GEAR_UP" }],
        Interlocks = [Interlocks.NotOnGround],
    };

    private static ProfileAction GearDown() => new()
    {
        Id = "gear.down", Area = "Config", Label = "Gear down", Readback = "Gear down",
        Steps = [new ActionStep { Event = "GEAR_DOWN" }],
        Interlocks = [Interlocks.GearSpeed],
    };

    private static ProfileAction Ls() => new()
    {
        Id = "efis.ls", Area = "EFIS", Label = "LS", Readback = "LS {value}",
        Parameter = new ActionParameter { Type = "choice", Options = new() { ["on"] = 1, ["off"] = 0 } },
        Steps = [new ActionStep { Event = "A32NX.FCU_EFIS_L_LS_PUSH", SkipIf = new StepCondition { Variable = "L:BTN_LS_1_FILTER_ACTIVE", EqualTo = J("\"{value}\"") } }],
        Check = new ActionCheck { Variable = "L:BTN_LS_1_FILTER_ACTIVE", Expect = J("\"{value}\"") },
    };

    // ------------------------------------------------------------------ Planung

    [Fact]
    public void Speed_wird_gesetzt_und_gezogen()
    {
        var plan = ActionPlanner.Plan(Speed(), "250", Limits, Air()).Plan!;

        Assert.Equal("Speed 250 selected", plan.Readback);
        Assert.Equal(new[] { "A32NX.FCU_SPD_SET", "A32NX.FCU_SPD_PULL" }, plan.Steps.Select(s => s.Event));
        Assert.Equal(250u, plan.Steps[0].EventData);
        Assert.Equal(250, plan.Check!.Value);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("400")]
    [InlineData("250.5")]
    [InlineData("schnell")]
    [InlineData("")]
    public void Ungueltige_Werte_werden_abgelehnt(string value)
    {
        var result = ActionPlanner.Plan(Speed(), value, Limits, Air());
        Assert.Null(result.Plan);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void Hoehe_als_Flugflaeche_und_negative_VS()
    {
        var alt = new ProfileAction
        {
            Id = "fcu.altitude", Readback = "Altitude {value}",
            Parameter = new ActionParameter { Min = 100, Max = 49000, Step = 100, Unit = "ft", Format = "altitude" },
            Steps = [new ActionStep { Event = "A32NX.FCU_ALT_SET", Value = J("\"{value}\"") }],
        };
        var plan = ActionPlanner.Plan(alt, "FL120", Limits, Air()).Plan!;
        Assert.Equal(12000u, plan.Steps[0].EventData);
        Assert.Equal("Altitude FL120", plan.Readback);

        var vs = new ProfileAction
        {
            Id = "fcu.vs",
            Parameter = new ActionParameter { Min = -6000, Max = 6000, Step = 100 },
            Steps = [new ActionStep { Event = "A32NX.FCU_VS_SET", Value = J("\"{value}\"") }],
        };
        Assert.Equal(-1000, unchecked((int)ActionPlanner.Plan(vs, "-1000", Limits, Air()).Plan!.Steps[0].EventData));
    }

    [Fact]
    public void Squawk_als_BCD_und_Frequenz_in_Hertz()
    {
        var squawk = new ProfileAction
        {
            Id = "xpdr.code", Readback = "Squawk {value}",
            Parameter = new ActionParameter { Format = "squawk" },
            Steps = [new ActionStep { Event = "XPNDR_SET", Value = J("\"{value}\""), Encode = "bcd16" }],
        };
        Assert.Equal(0x7000u, ActionPlanner.Plan(squawk, "7000", Limits, Air()).Plan!.Steps[0].EventData);
        Assert.Equal(0x2471u, ActionPlanner.Plan(squawk, "2471", Limits, Air()).Plan!.Steps[0].EventData);
        Assert.Null(ActionPlanner.Plan(squawk, "7800", Limits, Air()).Plan);
        Assert.Null(ActionPlanner.Plan(squawk, "700", Limits, Air()).Plan);

        var com = new ProfileAction
        {
            Id = "com1.standby", Readback = "COM 1 standby {value}",
            Parameter = new ActionParameter { Min = 118, Max = 136.99, Step = 0.005, Unit = "MHz", Format = "frequency" },
            Steps = [new ActionStep { Event = "COM_STBY_RADIO_SET_HZ", Value = J("\"{value}\""), Scale = 1_000_000 }],
        };
        var plan = ActionPlanner.Plan(com, "121,8", Limits, Air()).Plan!;
        Assert.Equal(121_800_000u, plan.Steps[0].EventData);
        Assert.Equal("COM 1 standby 121.800", plan.Readback);
        Assert.Null(ActionPlanner.Plan(com, "121.803", Limits, Air()).Plan);
        Assert.Null(ActionPlanner.Plan(com, "140.0", Limits, Air()).Plan);
    }

    [Fact]
    public void Auswahl_mit_Schritten_je_Option()
    {
        var baro = new ProfileAction
        {
            Id = "efis.baroMode", Readback = "{value}",
            Parameter = new ActionParameter { Type = "choice", Options = new() { ["std"] = 1, ["qnh"] = 0 } },
            Steps =
            [
                new ActionStep { Event = "A32NX.FCU_EFIS_L_BARO_PULL", Only = ["std"] },
                new ActionStep { Event = "A32NX.FCU_EFIS_L_BARO_PUSH", Only = ["qnh"] },
                new ActionStep { Set = "L:TEST_MODE", Value = J("""{ "std": 7, "qnh": 3 }""") },
            ],
        };

        var std = ActionPlanner.Plan(baro, "STD", Limits, Air()).Plan!;
        Assert.Equal("std", std.ValueText);
        Assert.Equal("A32NX.FCU_EFIS_L_BARO_PULL", std.Steps[0].Event);
        Assert.Equal(7, std.Steps[1].SetValue);

        var qnh = ActionPlanner.Plan(baro, "qnh", Limits, Air()).Plan!;
        Assert.Equal("A32NX.FCU_EFIS_L_BARO_PUSH", qnh.Steps[0].Event);
        Assert.Equal(3, qnh.Steps[1].SetValue);

        Assert.Contains("std, qnh", ActionPlanner.Plan(baro, "auto", Limits, Air()).Refusal);
    }

    // ------------------------------------------------------------------ Sperren

    [Fact]
    public void Fahrwerk_einfahren_am_Boden_gesperrt()
    {
        Assert.Equal("Am Boden gesperrt.", ActionPlanner.Plan(GearUp(), null, Limits, Ground()).Refusal);
        Assert.NotNull(ActionPlanner.Plan(GearUp(), null, Limits, Air()).Plan);
        Assert.NotNull(ActionPlanner.Plan(GearUp(), null, Limits, Air() with { OnGround = null }).Refusal);
    }

    [Fact]
    public void Fahrwerk_ausfahren_nur_bis_VLE()
    {
        Assert.Contains("VLE", ActionPlanner.Plan(GearDown(), null, Limits, Air(300)).Refusal);
        Assert.NotNull(ActionPlanner.Plan(GearDown(), null, Limits, Air(250)).Plan);
    }

    [Fact]
    public void Klappen_nur_bis_VFE()
    {
        Assert.Contains("VFE 185", ActionPlanner.Plan(Flaps(), "3", Limits, Air(195)).Refusal);
        Assert.NotNull(ActionPlanner.Plan(Flaps(), "2", Limits, Air(195)).Plan);
        Assert.NotNull(ActionPlanner.Plan(Flaps(), "0", Limits, Air(300)).Plan);
        Assert.NotNull(ActionPlanner.Plan(Flaps(), "1", Limits, Ground()).Plan);
        Assert.NotNull(ActionPlanner.Plan(Flaps(), "FULL", Limits, Air(170)).Plan);
    }

    [Fact]
    public void Unbekannte_Sperre_sperrt()
    {
        var action = GearUp();
        action.Interlocks = ["irgendwas"];
        Assert.Contains("Unbekannte Sperre", ActionPlanner.Plan(action, null, Limits, Air()).Refusal);
    }

    [Theory]
    [InlineData("FUELSYSTEM_PUMP_TOGGLE", true)]
    [InlineData("ENGINE_MASTER_1", true)]
    [InlineData("L:A32NX_ENG_1_MASTER", true)]
    [InlineData("L:A32NX_FIRE_BUTTON_ENG1", true)]
    [InlineData("A32NX.MCDU_L_BUTTON", true)]
    [InlineData("THROTTLE_SET", true)]
    [InlineData("L:A32NX_ENGINE_MODE_SELECTOR", true)]
    [InlineData("A32NX.FCU_SPD_SET", false)]
    [InlineData("L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON", false)]
    [InlineData("L:A32NX_OVHD_COND_PACK_1_PB_IS_ON", false)]
    [InlineData("GEAR_DOWN", false)]
    [InlineData("XPNDR_SET", false)]
    public void Sperrliste(string name, bool forbidden) => Assert.Equal(forbidden, ActionSafety.IsForbidden(name));

    [Fact]
    public void Gesperrte_Aktionen_werden_beim_Laden_entfernt()
    {
        var profile = new AircraftProfile
        {
            Name = "Test",
            Actions =
            [
                Speed(),
                new ProfileAction { Id = "fuel.pump", Steps = [new ActionStep { Event = "FUELSYSTEM_PUMP_ON" }] },
                new ProfileAction { Id = "x", Steps = [new ActionStep { Event = "GEAR_UP" }], Interlocks = ["gibtsnicht"] },
                Speed(),
            ],
        };
        var errors = new List<string>();

        ProfileStore.ValidateActions(profile, errors);

        Assert.Equal(new[] { "fcu.speed" }, profile.Actions.Select(a => a.Id));
        Assert.Equal(3, errors.Count);
        Assert.Throws<InvalidOperationException>(() => ActionSafety.EnsureAllowed("ENGINE_MASTER_1"));
    }

    // ------------------------------------------------------------------ Ablauf mit Bestätigung

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeBackend(ManualTime time) : IInputBackend
    {
        public List<string> Sent { get; } = [];

        public Dictionary<string, double> State { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FlightSnapshot Current { get; set; } = Air();

        public bool Connected { get; set; } = true;

        public AircraftProfile? Profile { get; set; } = new()
        {
            Name = "Test",
            Limits = Limits,
            Actions = [Speed(), Flaps(), GearUp(), Ls()],
        };

        /// <summary>Was ein Event am Zustand ändert (simuliertes Flugzeug).</summary>
        public Action<string, uint>? OnEvent { get; set; }

        public FlightSnapshot Snapshot() => Current;

        public Task<double?> ReadAsync(string name, string unit, CancellationToken ct)
        {
            time.Now += TimeSpan.FromSeconds(1);
            return Task.FromResult(State.TryGetValue(name, out var v) ? v : (double?)null);
        }

        public Task SendEventAsync(string name, uint data, CancellationToken ct)
        {
            Sent.Add($"{name}:{data}");
            OnEvent?.Invoke(name, data);
            return Task.CompletedTask;
        }

        public Task SetAsync(string name, string unit, double value, CancellationToken ct)
        {
            Sent.Add($"{name}={value}");
            State[name] = value;
            return Task.CompletedTask;
        }
    }

    private static (InputService Service, FakeBackend Backend, ManualTime Time) Create(bool enabled = true)
    {
        var time = new ManualTime();
        var backend = new FakeBackend(time);
        var dir = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new KopilotOptions { DataDirectory = dir, Input = new InputOptions { Enabled = enabled } });
        return (new InputService(options, backend, NullLogger<InputService>.Instance, time), backend, time);
    }

    [Fact]
    public void Gesperrt_ist_der_Standard()
    {
        Assert.False(new InputOptions().Enabled);
        var (input, backend, _) = Create(enabled: false);

        Assert.Equal(InputResult.Disabled, input.Prepare("fcu.speed", "250").Status);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Vorbereiten_bestaetigen_ausfuehren_pruefen()
    {
        var (input, backend, time) = Create();
        backend.OnEvent = (name, data) =>
        {
            if (name == "A32NX.FCU_SPD_SET")
            {
                backend.State["L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE"] = data;
            }
        };

        var prepared = input.Prepare("fcu.speed", "250");
        Assert.Equal(InputResult.AwaitingConfirmation, prepared.Status);
        Assert.Equal("Speed 250 selected", prepared.Readback);
        Assert.Empty(backend.Sent);

        // Sofort bestätigen geht nicht – erst Readback, dann Antwort des Piloten
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);

        time.Now += TimeSpan.FromSeconds(4);
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync("falsch", "confirm", default)).Status);
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "hm", default)).Status);

        var done = await input.ConfirmAsync(prepared.Token!, "Confirm.", default);

        Assert.Equal(InputResult.Executed, done.Status);
        Assert.True(done.Confirmed);
        Assert.Equal(new[] { "A32NX.FCU_SPD_SET:250", "A32NX.FCU_SPD_PULL:0" }, backend.Sent);
        Assert.Null(input.Pending);
        Assert.Equal("ausgeführt", input.History[0].Outcome);

        // Token ist verbraucht
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
    }

    [Fact]
    public async Task Nein_verwirft_die_Aktion()
    {
        var (input, backend, time) = Create();
        var prepared = input.Prepare("gear.up", null);
        time.Now += TimeSpan.FromSeconds(3);

        var result = await input.ConfirmAsync(prepared.Token!, "negative, gear down", default);

        Assert.Equal(InputResult.Cancelled, result.Status);
        Assert.Empty(backend.Sent);
        Assert.Null(input.Pending);
    }

    [Fact]
    public async Task Sperren_werden_beim_Bestaetigen_neu_geprueft()
    {
        var (input, backend, time) = Create();
        backend.Current = Air(190);
        Assert.Equal(InputResult.Refused, input.Prepare("flaps", "3").Status);

        var prepared = input.Prepare("flaps", "2");
        Assert.Equal(InputResult.AwaitingConfirmation, prepared.Status);

        backend.Current = Air(230); // inzwischen schneller als VFE 200
        time.Now += TimeSpan.FromSeconds(3);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Refused, result.Status);
        Assert.Contains("VFE 200", result.Reason);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Abgelaufen_nach_dem_Zeitfenster()
    {
        var (input, backend, time) = Create();
        var prepared = input.Prepare("gear.up", null);

        time.Now += TimeSpan.FromSeconds(61);

        Assert.Null(input.Pending);
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
        Assert.Empty(backend.Sent);
        Assert.Contains(input.History, e => e.Outcome == "abgelaufen");
    }

    [Fact]
    public async Task Umschalttaster_nur_wenn_noetig()
    {
        var (input, backend, time) = Create();
        backend.State["L:BTN_LS_1_FILTER_ACTIVE"] = 1;

        var prepared = input.Prepare("efis.ls", "on");
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "yes", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(backend.Sent);

        backend.OnEvent = (name, _) => backend.State["L:BTN_LS_1_FILTER_ACTIVE"] = 0;
        prepared = input.Prepare("efis.ls", "off");
        time.Now += TimeSpan.FromSeconds(2);
        result = await input.ConfirmAsync(prepared.Token!, "bestätigt", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.True(result.Confirmed);
        Assert.Equal(new[] { "A32NX.FCU_EFIS_L_LS_PUSH:0" }, backend.Sent);
    }

    [Fact]
    public async Task Flugzeug_uebernimmt_den_Wert_nicht()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE"] = 180;

        var prepared = input.Prepare("fcu.speed", "250");
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.False(result.Confirmed);
        Assert.Equal(180, result.StateAfter);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Ohne_Verbindung_oder_Profil_keine_Eingabe()
    {
        var (input, backend, _) = Create();
        backend.Connected = false;
        Assert.Equal(InputResult.Refused, input.Prepare("fcu.speed", "250").Status);

        backend.Connected = true;
        backend.Profile = null;
        Assert.Equal(InputResult.Refused, input.Prepare("fcu.speed", "250").Status);

        backend.Profile = new AircraftProfile { Name = "Leer" };
        Assert.Contains("gibt es", input.Prepare("fcu.speed", "250").Reason);
    }

    [Fact]
    public void Dashboard_Schalter_hat_Vorrang()
    {
        var (input, _, _) = Create(enabled: false);
        Assert.False(input.Enabled);

        input.SetEnabled(true);
        Assert.True(input.Enabled);
        Assert.Equal(InputResult.AwaitingConfirmation, input.Prepare("gear.up", null).Status);

        input.SetEnabled(false);
        Assert.False(input.Enabled);
        Assert.Null(input.Pending);
    }
}
