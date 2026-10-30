using System.Text.Json;
using Kopilot.Aircraft;
using Kopilot.Input;
using Kopilot.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kopilot.Tests;

/// <summary>Eingabe-Modul (v0.7): Planung, Sperren, Sperrliste und der zweistufige Ablauf mit Bestätigung.</summary>
public sealed partial class InputTests
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
        Steps =
        [
            new ActionStep { Event = "FLAPS_UP", Only = ["0"] },
            new ActionStep { Event = "FLAPS_1", Only = ["1"] },
            new ActionStep { Event = "FLAPS_2", Only = ["2"] },
            new ActionStep { Event = "FLAPS_3", Only = ["3"] },
            new ActionStep { Event = "FLAPS_DOWN", Only = ["FULL"] },
        ],
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

    private static ProfileAction ApuMaster() => new()
    {
        Id = "apu.master", Area = "APU", Label = "APU master", Readback = "APU master {value}",
        Parameter = new ActionParameter { Type = "choice", Options = new() { ["on"] = 1, ["off"] = 0 } },
        Steps = [new ActionStep { Set = "L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON", Value = J("\"{value}\"") }],
        Requires = [new StepCondition { Variable = "L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED", EqualTo = J("1"), Only = ["on"], Message = "Kein Strom" }],
        Check = new ActionCheck { Variable = "L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON", Expect = J("\"{value}\"") },
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
        Assert.Equal(250, plan.Check!.Condition.Value);
        Assert.True(plan.Check.Independent);
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
        Assert.Equal("Locked on the ground.", ActionPlanner.Plan(GearUp(), null, Limits, Ground()).Refusal);
        Assert.NotNull(ActionPlanner.Plan(GearUp(), null, Limits, Air()).Plan);
        Assert.NotNull(ActionPlanner.Plan(GearUp(), null, Limits, Air() with { OnGround = null }).Refusal);
    }

    [Fact]
    public void Fahrwerk_ausfahren_nur_bis_VLE()
    {
        Assert.Contains("VLE", ActionPlanner.Plan(GearDown(), null, Limits, Air(300)).Refusal);
        Assert.NotNull(ActionPlanner.Plan(GearDown(), null, Limits, Air(250)).Plan);

        var withVlo = new AircraftLimits { VleKt = 280, VloKt = 250 };
        Assert.Contains("VLO 250", ActionPlanner.Plan(GearDown(), null, withVlo, Air(260)).Refusal);
        Assert.NotNull(ActionPlanner.Plan(GearDown(), null, withVlo, Air(240)).Plan);
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
    public void Nur_am_Boden()
    {
        var gpu = new ProfileAction
        {
            Id = "elec.extPwr", Readback = "External power {value}",
            Parameter = new ActionParameter { Type = "choice", Options = new() { ["on"] = 1, ["off"] = 0 } },
            Steps = [new ActionStep { Set = "L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON", Value = J("\"{value}\"") }],
            Interlocks = [Interlocks.OnGround],
        };

        Assert.NotNull(ActionPlanner.Plan(gpu, "on", Limits, Ground()).Plan);
        Assert.Equal("Only possible on the ground.", ActionPlanner.Plan(gpu, "on", Limits, Air()).Refusal);
        Assert.Contains("Not clear", ActionPlanner.Plan(gpu, "off", Limits, Air() with { OnGround = null }).Refusal);
    }

    [Fact]
    public void Unbekannte_Sperre_sperrt()
    {
        var action = Speed();
        action.Interlocks = ["irgendwas"];
        Assert.Contains("Unknown interlock", ActionPlanner.Plan(action, "250", Limits, Air()).Refusal);
    }

    [Theory]
    [InlineData("FUELSYSTEM_PUMP_TOGGLE", 0u, false)]
    [InlineData("ENGINE_MASTER_1", 0u, false)]
    [InlineData("A32NX.MCDU_L_BUTTON", 0u, false)]
    [InlineData("THROTTLE_SET", 0u, false)]
    [InlineData("RELOAD_USER_AIRCRAFT", 0u, false)]
    [InlineData("CROSS_FEED_TOGGLE", 0u, false)]
    [InlineData("GEAR_TOGGLE", 0u, false)]
    [InlineData("PAUSE_ON", 0u, false)]
    [InlineData("ELECTRICAL_CIRCUIT_TOGGLE", 5u, false)]
    [InlineData("ELECTRICAL_CIRCUIT_TOGGLE", 21u, true)]
    [InlineData("A32NX.FCU_SPD_SET", 250u, true)]
    [InlineData("K:GEAR_DOWN", 0u, true)]
    [InlineData("XPNDR_SET", 0x7000u, true)]
    [InlineData("NAV_LIGHTS_SET", 1u, true)]
    [InlineData("LOGO_LIGHTS_SET", 0u, true)]
    [InlineData("A32NX.AUTOBRAKE_SET_MAX", 0u, true)]
    [InlineData("A32NX.AUTOBRAKE_SET_DISARM", 0u, true)]
    public void Erlaubte_Events(string name, uint data, bool allowed) => Assert.Equal(allowed, ActionSafety.EventProblem(name, data) is null);

    [Theory]
    [InlineData("GENERAL ENG COMBUSTION:1", false)]
    [InlineData("PLANE ALTITUDE", false)]
    [InlineData("L:A32NX_ENG_1_MASTER", false)]
    [InlineData("L:A32NX_FIRE_BUTTON_ENG1", false)]
    [InlineData("L:A32NX_ENGINE_MODE_SELECTOR", false)]
    [InlineData("L:A32NX_OVHD_FUEL_PUMP_1_PB_IS_AUTO", false)]
    [InlineData("L:A32NX_GEAR_LEVER_POSITION_REQUEST", false)]
    [InlineData("L:A32NX_FLAPS_HANDLE_INDEX", false)]
    [InlineData("L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON", true)]
    [InlineData("L:A32NX_OVHD_COND_PACK_1_PB_IS_ON", true)]
    [InlineData("L:A32NX_BUTTON_OVHD_ANTI_ICE_ENG_1_POSITION", true)]
    [InlineData("L:A32NX_TRANSPONDER_MODE", true)]
    [InlineData("L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO", true)]
    [InlineData("L:A32NX_SWITCH_TCAS_Position", true)]
    public void Erlaubte_Variablen(string name, bool allowed) => Assert.Equal(allowed, ActionSafety.VariableProblem(name) is null);

    [Fact]
    public void Fehlende_Grenzwerte_nur_mit_Rueckfrage()
    {
        // v0.9.1: Fehlt ein Grenzwert im Profil, ist die Aktion nicht mehr gesperrt, sondern geht nur mit Rückfrage (im Log vermerkt)
        var none = new AircraftLimits();
        var gear = ActionPlanner.Plan(GearDown(), null, none, Air(150)).Plan!;
        var gearCaution = Assert.Single(gear.Cautions);
        Assert.Equal("LIMIT_MISSING_VLO", gearCaution.Code);
        Assert.True(gearCaution.MissingLimit);
        Assert.Contains("VLO/VLE", gearCaution.Reason);
        Assert.Contains("VFE", Assert.Single(ActionPlanner.Plan(Flaps(), "2", none, Air(150)).Plan!.Cautions).Reason);
        Assert.Empty(ActionPlanner.Plan(Flaps(), "0", none, Air(150)).Plan!.Cautions);

        // Fahrt unbekannt bleibt gesperrt
        Assert.Contains("Airspeed unknown", ActionPlanner.Plan(GearDown(), null, none, Air(150) with { IasKt = null }).Refusal);

        // Groß-/Kleinschreibung der Stellung egal
        var mixed = new AircraftLimits { VfeKt = new() { ["Full"] = 177, ["2"] = 200, ["1"] = 230, ["3"] = 185 } };
        Assert.Contains("VFE 177", ActionPlanner.Plan(Flaps(), "FULL", mixed, Air(190)).Refusal);
    }

    [Theory]
    [InlineData(0, 360, true)]
    [InlineData(0, 359.8, true)]
    [InlineData(359, 1, false)]
    [InlineData(90, 90.4, true)]
    [InlineData(90, 450, true)]
    public void Kurse_werden_zyklisch_verglichen(double expected, double state, bool matches)
    {
        Assert.Equal(matches, new ResolvedCondition("x", "number", expected, 0.5, Modulo: 360).Matches(state));
        Assert.Equal(Math.Abs(state - expected) <= 0.5, new ResolvedCondition("x", "number", expected, 0.5).Matches(state));
    }

    [Fact]
    public async Task Kurs_360_wird_als_360_bestaetigt()
    {
        var (input, backend, time) = Create();
        backend.Profile!.Actions.Add(new ProfileAction
        {
            Id = "fcu.heading", Area = "FCU", Label = "FCU heading", Readback = "Heading {value} selected",
            Parameter = new ActionParameter { Min = 0, Max = 360, Step = 1, Format = "heading" },
            Steps = [new ActionStep { Event = "A32NX.FCU_HDG_SET", Value = J("\"{value}\"") }],
            Check = new ActionCheck { Variable = "L:A32NX_FCU_AFS_DISPLAY_HDG_TRK_VALUE", Expect = J("\"{value}\""), Modulo = 360 },
        });
        backend.OnEvent = (name, data) => backend.State["L:A32NX_FCU_AFS_DISPLAY_HDG_TRK_VALUE"] = data;

        var prepared = await input.PrepareAsync("fcu.heading", "360", default);
        Assert.Equal("Heading 360 selected", prepared.Readback);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.True(result.Confirmed);
        Assert.Equal(360, result.StateAfter);
        Assert.Equal(new[] { "A32NX.FCU_HDG_SET:0" }, backend.Sent);
    }

    [Fact]
    public void read_simvar_dekodiert_ARINC_429()
    {
        var raw = Kopilot.Sim.Arinc429Word.Encode(Kopilot.Sim.Arinc429Ssm.NormalOperation, 99.5f);
        var view = System.Text.Json.JsonSerializer.SerializeToElement(Kopilot.Tools.KopilotTools.ArincView("L:A32NX_APU_N", raw, null));
        Assert.Equal(99.5, view.GetProperty("value").GetDouble());
        Assert.True(view.GetProperty("valid").GetBoolean());

        // Normale Zahlen bleiben, wie sie sind
        Assert.Null(Kopilot.Tools.KopilotTools.ArincView("L:A32NX_FMA_LATERAL_MODE", 20, null));

        // Im Profil als arinc429 bekannt: auch ungültige Wörter (SSM 0) werden dekodiert
        var profile = new AircraftProfile { Bindings = new() { ["apuN"] = new ProfileBinding { Name = "L:A32NX_APU_N", Type = "arinc429" } } };
        var failed = System.Text.Json.JsonSerializer.SerializeToElement(Kopilot.Tools.KopilotTools.ArincView("L:A32NX_APU_N", 0, profile));
        Assert.False(failed.GetProperty("valid").GetBoolean());
        Assert.Equal("FailureWarning", failed.GetProperty("ssm").GetString());
    }

    [Fact]
    public void Steuerkurs_360_ist_000()
    {
        var hdg = new ProfileAction
        {
            Id = "fcu.heading", Readback = "Heading {value} selected",
            Parameter = new ActionParameter { Min = 0, Max = 360, Step = 1, Format = "heading" },
            Steps = [new ActionStep { Event = "A32NX.FCU_HDG_SET", Value = J("\"{value}\"") }],
        };

        var north = ActionPlanner.Plan(hdg, "360", Limits, Air()).Plan!;
        Assert.Equal(0u, north.Steps[0].EventData);
        Assert.Equal("Heading 360 selected", north.Readback);
        Assert.Equal("Heading 090 selected", ActionPlanner.Plan(hdg, "90", Limits, Air()).Plan!.Readback);
    }

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
        Assert.Throws<InvalidOperationException>(() => ActionSafety.EnsureEventAllowed("ENGINE_MASTER_1", 0));
        Assert.Throws<InvalidOperationException>(() => ActionSafety.EnsureVariableAllowed("GENERAL ENG COMBUSTION:1"));
    }

    private static ProfileStore RepoStore()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Kopilot", "profiles")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var options = Options.Create(new KopilotOptions { ProfilesDirectory = Path.Combine(dir.FullName, "src", "Kopilot", "profiles") });
        var store = new ProfileStore(options, NullLogger<ProfileStore>.Instance);
        Assert.Empty(store.Errors);
        return store;
    }

    /// <summary>Jede Aktion mit jedem Wert planen (onGround-Aktionen am Boden, alles andere bei 150 kt in der Luft).</summary>
    private static void AssertAllPlannable(AircraftProfile profile)
    {
        foreach (var action in profile.Actions)
        {
            string?[] values = action.Parameter switch
            {
                null => [null],
                { IsChoice: true } p => p.Options!.Keys.ToArray<string?>(),
                { Format: "squawk" } => ["7000"],
                { Format: "frequency" } => ["121.800"],
                { } p => [(p.Min ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            };

            foreach (var value in values)
            {
                var state = action.Interlocks.Contains(Interlocks.OnGround) ? Ground() : Air(150);
                var result = ActionPlanner.Plan(action, value, profile.Limits, state);
                Assert.True(result.Plan is not null, $"{profile.Name}: {action.Id} = {value}: {result.Refusal}");
                Assert.DoesNotContain("{", result.Plan!.Readback);
                Assert.NotEmpty(result.Plan.Steps);
            }
        }
    }

    [Fact]
    public void A32NX_Aktionen_lassen_sich_alle_planen()
    {
        var store = RepoStore();
        var profile = store.All.Single(p => p.Name == "FlyByWire A32NX");
        Assert.True(profile.Actions.Count >= 40);
        Assert.Contains(profile.Actions, a => a.Id == "gear.up" && a.Interlocks.Contains(Interlocks.NotOnGround));
        Assert.Contains(profile.Actions, a => a.Id == "flaps" && a.Interlocks.Contains(Interlocks.FlapsSpeed));

        AssertAllPlannable(profile);

        // APU-Werte für get_systems: dieselben Variablen wie die Eingaben, N aus dem ECB (ARINC 429)
        Assert.Equal("arinc429", profile.Bindings[BindingKeys.ApuN].Type);
        Assert.Equal("L:A32NX_APU_N", profile.Bindings[BindingKeys.ApuN].Name);
        var masterAction = profile.Actions.Single(a => a.Id == "apu.master");
        Assert.Equal(profile.Bindings[BindingKeys.ApuMaster].Name, masterAction.Steps[0].Set);
        Assert.Equal(profile.Bindings[BindingKeys.ApuMaster].Name, masterAction.Check!.Variable);
        Assert.Contains(profile.Actions.Single(a => a.Id == "apu.start").Requires, r => r.Variable == profile.Bindings[BindingKeys.ApuMaster].Name);
        Assert.Contains(profile.Electrical, e => e.Key == "apuGenHz");

        // Engine Anti-Ice einzeln: prüft den echten Zustand ENG ANTI ICE:n (FBW schickt dafür K:ANTI_ICE_SET_ENGn)
        var eng2 = ActionPlanner.Plan(profile.Actions.Single(a => a.Id == "antiice.eng2"), "on", profile.Limits, Air(150)).Plan!;
        Assert.Equal("L:A32NX_BUTTON_OVHD_ANTI_ICE_ENG_2_POSITION", Assert.Single(eng2.Steps).Set);
        Assert.Equal("Engine two anti ice on", eng2.Readback);
        Assert.True(eng2.Check!.Independent);
        Assert.Contains(profile.Actions, a => a.Id == "antiice.engines");

        // External Power: nur am Boden, Einschalten nur mit GPU
        var extPwr = profile.Actions.Single(a => a.Id == "elec.extPwr");
        Assert.Equal("Only possible on the ground.", ActionPlanner.Plan(extPwr, "on", profile.Limits, Air(150)).Refusal);
        var onGround = ActionPlanner.Plan(extPwr, "on", profile.Limits, Ground()).Plan!;
        Assert.Equal("L:A32NX_EXT_PWR_AVAIL:1", Assert.Single(onGround.Requires).Variable);
        Assert.Empty(ActionPlanner.Plan(extPwr, "off", profile.Limits, Ground()).Plan!.Requires);
        Assert.True(onGround.Check!.Independent);

        // Landescheinwerfer: Stellung, Ausfahren und Stromkreis je Seite
        var landing = ActionPlanner.Plan(profile.Actions.Single(a => a.Id == "lights.landing"), "retract", profile.Limits, Air(150)).Plan!;
        Assert.Equal("Landing lights retracted", landing.Readback);
        Assert.Equal(2, landing.Steps.Count(s => s.Event == "ELECTRICAL_CIRCUIT_TOGGLE"));
        Assert.All(landing.Steps.Where(s => s.Event is not null), s => Assert.Equal(0, s.SkipIf!.Value));
    }

    [Fact]
    public void A339_hat_alle_A32NX_Aktionen_mit_eigenen_Anpassungen()
    {
        var store = RepoStore();
        var a32nx = store.All.Single(p => p.Name == "FlyByWire A32NX");
        var profile = store.All.Single(p => p.Name == "Headwind A330-900neo");

        // Alle A32NX-Aktionen in derselben Reihenfolge, dazu nur die A330-eigenen, keine doppelt
        string[] a339Only = ["lights.nav", "lights.logo", "elec.battery"];
        Assert.Equal(a32nx.Actions.Select(a => a.Id), profile.Actions.Select(a => a.Id).Where(id => !a339Only.Contains(id)));
        Assert.Equal(a339Only.Order(), profile.Actions.Select(a => a.Id).Except(a32nx.Actions.Select(a => a.Id)).Order());
        Assert.Equal(profile.Actions.Count, profile.Actions.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        AssertAllPlannable(profile);

        ProfileAction Get(string id) => profile.Actions.Single(a => a.Id == id);
        ActionPlan Plan(string id, string? value) => ActionPlanner.Plan(Get(id), value, profile.Limits, Air(150)).Plan!;

        // Sperren wie beim A32NX
        Assert.Contains(Interlocks.NotOnGround, Get("gear.up").Interlocks);
        Assert.Contains(Interlocks.GearSpeed, Get("gear.down").Interlocks);
        Assert.Contains(Interlocks.FlapsSpeed, Get("flaps").Interlocks);
        Assert.Contains("VFE 180", ActionPlanner.Plan(Get("flaps"), "FULL", profile.Limits, Air(190)).Refusal);
        Assert.Contains("VLO 250", ActionPlanner.Plan(Get("gear.down"), null, profile.Limits, Air(260)).Refusal);

        // Im Sim getestet: FCU, Lights, Anti-Ice, External Power, APU – angepasste und neue Aktionen ungeprüft
        Assert.True(Get("fcu.heading").Verified);
        Assert.True(Get("lights.beacon").Verified);
        Assert.True(Get("antiice.engines").Verified);
        Assert.True(Get("apu.start").Verified);
        Assert.All(new[] { "fcu.headingManaged", "lights.landing", "signs.seatbelts", "signs.noSmoking", "flaps", "gear.up", "apu.bleed", "xpdr.code" },
            id => Assert.False(Get(id).Verified, id));

        // External Power und APU: die A330-Fassungen bleiben (Schalter statt Schütz 3XG, BAT 1 statt DC BAT BUS)
        Assert.Equal("L:A32NX_OVHD_ELEC_EXT_PWR_PB_IS_ON", Get("elec.extPwr").Check!.Variable);
        Assert.Contains(Get("apu.master").Requires, r => r.Variable == "L:A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO");

        // Seatbelts: Schalter mit drei Stellungen, Zeichen bei ON/OFF selbst mitschalten, AUTO macht das Flugzeug
        var auto = Plan("signs.seatbelts", "auto");
        var set = Assert.Single(auto.Steps);
        Assert.Equal("L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position", set.Set);
        Assert.Equal(1, set.SetValue);
        Assert.Equal("Enum", set.SkipIf!.Unit);
        Assert.Equal("Enum", auto.Check!.Condition.Unit);
        var on = Plan("signs.seatbelts", "on");
        Assert.Equal(0, on.Steps[0].SetValue);
        Assert.Equal("CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE", on.Steps[1].Event);
        Assert.Equal(1, on.Steps[1].SkipIf!.Value);
        var off = Plan("signs.seatbelts", "off");
        Assert.Equal(2, off.Steps[0].SetValue);
        Assert.Equal(0, off.Steps[1].SkipIf!.Value);

        // No smoking: Stellung als Enum lesen (vor dem Schreiben und in der Prüfung)
        var noSmoking = Plan("signs.noSmoking", "on");
        Assert.Equal("Enum", Assert.Single(noSmoking.Steps).SkipIf!.Unit);
        Assert.Equal("Enum", noSmoking.Check!.Condition.Unit);

        // Landescheinwerfer: geprüft wird das Licht selbst
        var landing = Plan("lights.landing", "on");
        Assert.Equal("LIGHT LANDING:2", landing.Check!.Condition.Variable);
        Assert.Equal(1, landing.Check.Condition.Value);
        Assert.True(landing.Check.Independent);
        Assert.Equal(0, Plan("lights.landing", "off").Check!.Condition.Value);

        // Managed NAV: NAV braucht ein paar Sekunden
        Assert.True(Plan("fcu.headingManaged", null).Check!.TimeoutMs >= 8000);
    }

    [Fact]
    public void Kaputte_Profile_verhindern_den_Start_nicht()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "test.json"), """
            {
              "name": "Kaputt",
              "match": [ { "title": [ "Kaputt" ] } ],
              "limits": null,
              "actions": [
                null,
                { "id": "a", "steps": null },
                { "id": "b", "steps": [ null ] },
                { "id": "c", "steps": [ { "event": "GEAR_DOWN" } ], "interlocks": [ null ] },
                { "id": "d", "steps": [ { "event": "FLAPS_DOWN" } ] },
                { "id": "e", "steps": [ { "set": "PLANE ALTITUDE", "value": 0 } ] },
                { "id": "ok", "steps": [ { "event": "A32NX.FCU_ALT_PULL" } ] }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(dir, "nicht-json.json"), "{ das ist kein JSON");

        var store = new ProfileStore(Options.Create(new KopilotOptions { ProfilesDirectory = dir }), NullLogger<ProfileStore>.Instance);

        var profile = Assert.Single(store.All);
        Assert.Equal(new[] { "ok" }, profile.Actions.Select(a => a.Id));
        Assert.Equal(6, store.Errors.Count);
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
            Actions = [Speed(), Flaps(), GearUp(), Ls(), ApuMaster()],
        };

        /// <summary>Was ein Event am Zustand ändert (simuliertes Flugzeug).</summary>
        public Action<string, uint>? OnEvent { get; set; }

        /// <summary>Simuliertes Flugzeug, das bestimmte geschriebene Variablen nach dem ersten Lesen zurücksetzt.</summary>
        public Dictionary<string, double> RevertAfterRead { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lesen dieser Variablen wartet, bis der Test sie freigibt (langsame SimConnect-Antwort).</summary>
        public Dictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gelesene Variablen mit Einheit, in Reihenfolge.</summary>
        public List<string> Reads { get; } = [];

        public FlightSnapshot Snapshot() => Current;

        public async Task<double?> ReadAsync(string name, string unit, CancellationToken ct)
        {
            if (Gates.TryGetValue(name, out var gate))
            {
                await gate.Task.WaitAsync(ct);
            }

            time.Now += TimeSpan.FromSeconds(1);
            Reads.Add($"{name}|{unit}");
            var result = State.TryGetValue(name, out var v) ? v : (double?)null;
            if (RevertAfterRead.TryGetValue(name, out var revert))
            {
                State[name] = revert;
            }

            return result;
        }

        public Task SendEventAsync(string name, uint data, CancellationToken ct)
        {
            Sent.Add($"{name}:{data}");
            OnEvent?.Invoke(name, data);
            return Task.CompletedTask;
        }

        /// <summary>Was das Schreiben einer Variable sonst noch am Zustand ändert (z. B. TCAS-Rechner übernimmt die Schalterstellung).</summary>
        public Action<string, double>? OnSet { get; set; }

        public Task SetAsync(string name, string unit, double value, CancellationToken ct)
        {
            Sent.Add($"{name}={value}");
            State[name] = value;
            OnSet?.Invoke(name, value);
            return Task.CompletedTask;
        }
    }

    private static (InputService Service, FakeBackend Backend, ManualTime Time) Create(bool enabled = true, string? dataDir = null)
    {
        var time = new ManualTime();
        var backend = new FakeBackend(time);
        var dir = dataDir ?? Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new KopilotOptions { DataDirectory = dir, Input = new InputOptions { Enabled = enabled } });
        return (new InputService(options, backend, NullLogger<InputService>.Instance, time), backend, time);
    }

    [Fact]
    public async Task Gesperrt_ist_der_Standard()
    {
        Assert.False(new InputOptions().Enabled);
        var (input, backend, _) = Create(enabled: false);

        Assert.Equal(InputResult.Disabled, (await input.PrepareAsync("fcu.speed", "250", default)).Status);
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

        var prepared = await input.PrepareAsync("fcu.speed", "250", default);
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
        Assert.Equal("executed", input.History[0].Outcome);

        // Token ist verbraucht
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
    }

    [Fact]
    public async Task Nein_verwirft_die_Aktion()
    {
        var (input, backend, time) = Create();
        var prepared = await input.PrepareAsync("gear.up", null, default);
        time.Now += TimeSpan.FromSeconds(3);

        var result = await input.ConfirmAsync(prepared.Token!, "negative, gear down", default);

        Assert.Equal(InputResult.Cancelled, result.Status);
        Assert.Empty(backend.Sent);
        Assert.Null(input.Pending);
    }

    [Theory]
    [InlineData("Confirm.\nNo, wait")]
    [InlineData("„nein“")]
    [InlineData("(no)")]
    [InlineData("negative")]
    public async Task Nein_in_jeder_Schreibweise(string answer)
    {
        var (input, backend, time) = Create();
        var prepared = await input.PrepareAsync("gear.up", null, default);
        time.Now += TimeSpan.FromSeconds(3);

        Assert.Equal(InputResult.Cancelled, (await input.ConfirmAsync(prepared.Token!, answer, default)).Status);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Abbrechen_im_Dashboard_gilt_fuer_alle_Instanzen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        var (claudeDesktop, backend, time) = Create(dataDir: dir);
        var (tray, _, _) = Create(dataDir: dir);

        var prepared = await claudeDesktop.PrepareAsync("gear.up", null, default);
        tray.CancelEverywhere("Abgebrochen (Dashboard)");
        time.Now += TimeSpan.FromSeconds(3);

        var result = await claudeDesktop.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.Equal(InputResult.Refused, result.Status);
        Assert.Contains("dashboard", result.Reason);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Sperren_werden_beim_Bestaetigen_neu_geprueft()
    {
        var (input, backend, time) = Create();
        backend.Current = Air(190);
        Assert.Equal(InputResult.Refused, (await input.PrepareAsync("flaps", "3", default)).Status);

        var prepared = await input.PrepareAsync("flaps", "2", default);
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
        var prepared = await input.PrepareAsync("gear.up", null, default);

        time.Now += TimeSpan.FromSeconds(61);

        Assert.Null(input.Pending);
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
        Assert.Empty(backend.Sent);
        Assert.Contains(input.History, e => e.Outcome == "expired");
    }

    [Fact]
    public async Task Umschalttaster_nur_wenn_noetig()
    {
        var (input, backend, time) = Create();
        backend.State["L:BTN_LS_1_FILTER_ACTIVE"] = 1;

        var prepared = await input.PrepareAsync("efis.ls", "on", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "yes", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(backend.Sent);

        backend.OnEvent = (name, _) => backend.State["L:BTN_LS_1_FILTER_ACTIVE"] = 0;
        prepared = await input.PrepareAsync("efis.ls", "off", default);
        time.Now += TimeSpan.FromSeconds(2);
        result = await input.ConfirmAsync(prepared.Token!, "bestätigt", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.True(result.Confirmed);
        Assert.Equal(new[] { "A32NX.FCU_EFIS_L_LS_PUSH:0" }, backend.Sent);
    }

    [Fact]
    public async Task Absolut_gesetzter_Wert_der_schon_steht_wird_nicht_geschrieben()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = 1;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 1;

        var prepared = await input.PrepareAsync("apu.master", "on", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.True(result.AlreadySet);
        Assert.True(result.Confirmed);
        Assert.False(result.CheckIndependent);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Zurueckgesetzter_Wert_gilt_nicht_als_uebernommen()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = 1;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 0;
        backend.RevertAfterRead["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 0; // das System setzt den Schalter sofort zurück

        var prepared = await input.PrepareAsync("apu.master", "on", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.False(result.Confirmed);
        Assert.Contains("reset", result.Reason);
        Assert.Equal(new[] { "L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON=1" }, backend.Sent);
    }

    [Fact]
    public async Task APU_Start_ohne_Wirkung_wird_als_fehlgeschlagen_gemeldet()
    {
        var (input, backend, time) = Create();
        backend.Profile!.Actions.Add(new ProfileAction
        {
            Id = "apu.start", Area = "APU", Label = "APU start", Readback = "APU start",
            Steps = [new ActionStep { Set = "L:A32NX_OVHD_APU_START_PB_IS_ON", Value = J("1") }],
            Check = new ActionCheck { Variable = "L:A32NX_OVHD_APU_START_PB_IS_ON", Expect = J("1"), HoldMs = 2500, ConfirmsEffect = true },
        });
        backend.State["L:A32NX_OVHD_APU_START_PB_IS_ON"] = 0;
        backend.RevertAfterRead["L:A32NX_OVHD_APU_START_PB_IS_ON"] = 0; // APU-Elektronik ohne Strom schaltet das ON-Licht aus

        var prepared = await input.PrepareAsync("apu.start", null, default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.False(result.Confirmed);
        Assert.True(result.CheckIndependent);

        // Läuft die APU an, bleibt das Licht an
        backend.RevertAfterRead.Clear();
        prepared = await input.PrepareAsync("apu.start", null, default);
        time.Now += TimeSpan.FromSeconds(2);
        result = await input.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.True(result.Confirmed);
    }

    private sealed class ThrowingBackend : IInputBackend
    {
        public bool Connected => true;

        public AircraftProfile? Profile => throw new InvalidCastException("kaputt");

        public FlightSnapshot Snapshot() => Air();

        public Task<double?> ReadAsync(string name, string unit, CancellationToken ct) => Task.FromResult<double?>(null);

        public Task SendEventAsync(string name, uint data, CancellationToken ct) => Task.CompletedTask;

        public Task SetAsync(string name, string unit, double value, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Ausnahmen_werden_zu_einer_strukturierten_Antwort()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new KopilotOptions { DataDirectory = dir, Input = new InputOptions { Enabled = true } });
        var input = new InputService(options, new ThrowingBackend(), NullLogger<InputService>.Instance);

        var result = await input.PrepareAsync("apu.start", null, default);

        Assert.Equal(InputResult.Error, result.Status);
        Assert.Contains("kaputt", result.Reason);

        // Fehlende Parameter: strukturierte Antwort statt MCP-Fehler
        var (ok, _, _) = Create();
        Assert.Equal(InputResult.Failed, (await ok.ConfirmAsync(null, null, default)).Status);
    }

    [Fact]
    public async Task Echt_bestaetigte_Aktion_wird_als_geprueft_gespeichert()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        var (input, backend, time) = Create(dataDir: dir);
        backend.OnEvent = (name, data) =>
        {
            if (name == "A32NX.FCU_SPD_SET")
            {
                backend.State["L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE"] = data;
            }
        };
        var speed = backend.Profile!.Actions.Single(a => a.Id == "fcu.speed");
        Assert.Null(input.VerifiedBy(speed));

        var prepared = await input.PrepareAsync("fcu.speed", "250", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.True(result.Confirmed);
        Assert.True(result.Verified);
        Assert.Equal("sim", input.VerifiedBy(speed));

        // Gilt auch für andere Kopilot-Instanzen (gleicher Datenordner) …
        var (other, otherBackend, _) = Create(dataDir: dir);
        Assert.Equal("sim", other.VerifiedBy(otherBackend.Profile!.Actions.Single(a => a.Id == "fcu.speed")));

        // … aber nicht mehr, wenn sich die Definition im Profil ändert
        var changed = Speed();
        changed.Check!.Tolerance = 2;
        Assert.Null(input.VerifiedBy(changed));
    }

    [Fact]
    public async Task Nur_Schalterstellung_braucht_die_Bestaetigung_des_Piloten()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = 1;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 0;
        var master = backend.Profile!.Actions.Single(a => a.Id == "apu.master");

        Assert.Equal(InputResult.Refused, input.MarkVerified("apu.master", "confirm").Status); // noch nicht ausgeführt

        var prepared = await input.PrepareAsync("apu.master", "on", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.True(result.Confirmed);
        Assert.False(result.CheckIndependent);
        Assert.Null(input.VerifiedBy(master)); // nicht automatisch

        Assert.Equal(InputResult.Refused, input.MarkVerified("apu.master", "no, it did not work").Status);
        Assert.Equal(InputResult.Executed, input.MarkVerified("apu.master", "confirm, APU master is on").Status);
        Assert.Equal("pilot", input.VerifiedBy(master));
    }

    [Fact]
    public void Im_Profil_markierte_Aktion_gilt_als_geprueft()
    {
        var (input, backend, _) = Create();
        var gear = backend.Profile!.Actions.Single(a => a.Id == "gear.up");
        gear.Verified = true;
        Assert.Equal("profile", input.VerifiedBy(gear));
    }

    [Fact]
    public async Task Bedingung_gilt_nur_fuer_die_genannte_Auswahl()
    {
        var (input, backend, _) = Create();
        backend.State["L:A32NX_ELEC_DC_BAT_BUS_IS_POWERED"] = 0;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 1;

        Assert.Equal("Kein Strom", (await input.PrepareAsync("apu.master", "on", default)).Reason);
        Assert.Equal(InputResult.AwaitingConfirmation, (await input.PrepareAsync("apu.master", "off", default)).Status);
    }

    [Fact]
    public async Task Flugzeug_uebernimmt_den_Wert_nicht()
    {
        var (input, backend, time) = Create();
        backend.State["L:A32NX_FCU_AFS_DISPLAY_SPD_MACH_VALUE"] = 180;

        var prepared = await input.PrepareAsync("fcu.speed", "250", default);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.Executed, result.Status);
        Assert.False(result.Confirmed);
        Assert.Equal(180, result.StateAfter);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Ohne_Verbindung_oder_Profil_keine_Eingabe()
    {
        var (input, backend, _) = Create();
        backend.Connected = false;
        Assert.Equal(InputResult.Refused, (await input.PrepareAsync("fcu.speed", "250", default)).Status);

        backend.Connected = true;
        backend.Profile = null;
        Assert.Equal(InputResult.Refused, (await input.PrepareAsync("fcu.speed", "250", default)).Status);

        backend.Profile = new AircraftProfile { Name = "Leer" };
        Assert.Contains("doesn't exist", (await input.PrepareAsync("fcu.speed", "250", default)).Reason);
    }

    [Fact]
    public async Task Dashboard_Schalter_hat_Vorrang()
    {
        var (input, _, _) = Create(enabled: false);
        Assert.False(input.Enabled);

        input.SetEnabled(true);
        Assert.True(input.Enabled);
        Assert.Equal(InputResult.AwaitingConfirmation, (await input.PrepareAsync("gear.up", null, default)).Status);

        input.SetEnabled(false);
        Assert.False(input.Enabled);
        Assert.Null(input.Pending);
    }
}
