using Duopilo.Aircraft;
using Duopilo.Input;
using Duopilo.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

/// <summary>
/// Sicherheitssperren (v0.9.1): Sperrliste, Plausibilitätssperren je Phase, Rückfragen mit Warnhinweis,
/// fehlende Grenzwerte und das Nachlesen von Fahrwerk und Klappen mit Callout. Jede Sperre: erlaubt / gesperrt / Rückfrage.
/// </summary>
public sealed partial class InputTests
{
    private const string Allowed = "erlaubt";
    private const string Locked = "gesperrt";
    private const string Ask = "Rückfrage";

    /// <summary>A339 in der Luft: 200 kt, 8000 ft (Druckhöhe gleich), Klappen eingefahren.</summary>
    private static FlightSnapshot Flight(double ias = 200, double alt = 8000, string flaps = "0") => Air(ias) with
    {
        AltitudeFt = alt, PressureAltitudeFt = alt, FlapsLabel = flaps, FlapsIndex = flaps == "0" ? 0 : 3,
    };

    private static FlightSnapshot OnGround() => Ground() with { FlapsLabel = "0", FlapsIndex = 0, PressureAltitudeFt = 300, AltitudeFt = 300 };

    /// <summary>Planen (Profil-Sperren) und Code-Sperren auswerten wie das Eingabe-Modul – ohne Sim.</summary>
    private static (string Outcome, string? Reason, IReadOnlyList<SafetyCaution> Cautions) Judge(
        AircraftProfile profile,
        string id,
        string? value,
        FlightSnapshot s,
        Dictionary<string, bool?>? states = null,
        int? cleared = null,
        MsaReading? msa = null,
        AircraftLimits? limits = null)
    {
        limits ??= profile.Limits;
        var action = profile.Actions.Single(a => a.Id == id);
        var planned = ActionPlanner.Plan(action, value, limits, s);
        if (planned.Plan is not { } plan)
        {
            return (Locked, planned.Refusal, []);
        }

        var verdict = SafetyRules.Evaluate(plan, new SafetyContext
        {
            Snapshot = s,
            Limits = limits,
            States = states ?? new Dictionary<string, bool?>(),
            ClearedAltitudeFt = cleared,
            Msa = msa,
        });
        if (verdict.Refusal is not null)
        {
            return (Locked, verdict.Refusal, []);
        }

        var cautions = plan.Cautions.Concat(verdict.Cautions).ToList();
        return (cautions.Count > 0 ? Ask : Allowed, null, cautions);
    }

    private static AircraftLimits A339Limits(Action<AircraftLimits> change)
    {
        var limits = A339().Limits;
        var copy = new AircraftLimits
        {
            VmoKt = limits.VmoKt, VleKt = limits.VleKt, VloKt = limits.VloKt, VfeKt = new(limits.VfeKt), LandingFlaps = [.. limits.LandingFlaps],
            MaxGearAltitudeFt = limits.MaxGearAltitudeFt, SpeedbrakeInhibitFlaps = limits.SpeedbrakeInhibitFlaps?.ToList(), MsaSource = limits.MsaSource,
        };
        change(copy);
        return copy;
    }

    // ------------------------------------------------------------------ 2. Nie über Duopilo: Triebwerk, Treibstoff, Feuer, Hydraulik, IDG

    [Theory]
    [InlineData("L:A32NX_OVHD_HYD_ENG_1_PUMP_PB_IS_AUTO", false)]
    [InlineData("L:A32NX_OVHD_HYD_PTU_PB_IS_AUTO", false)]
    [InlineData("L:A32NX_OVHD_HYD_EPUMPB_PB_IS_AUTO", false)]
    [InlineData("L:A32NX_OVHD_ELEC_IDG_1_PB_IS_RELEASED", false)]
    [InlineData("L:A32NX_RAT_MANUAL_DEPLOY", false)]
    [InlineData("L:A32NX_PTU_ON", false)]
    [InlineData("L:A32NX_OVHD_FUEL_XFEED_PB_IS_OPEN", false)]
    [InlineData("L:A32NX_FIRE_BUTTON_APU", false)]
    [InlineData("L:A32NX_ENGINE_MASTER_1", false)]
    [InlineData("L:A32NX_OVHD_ELEC_GEN_1_PB_IS_ON", true)]
    [InlineData("L:A32NX_KNOB_OVHD_AIRCOND_PACKFLOW_Position", true)]
    [InlineData("L:A32NX_CONFIGURATION_TEST", true)]
    [InlineData("L:XMLVAR_SWITCH_OVHD_INTLT_SEATBELT_Position", true)]
    public void Hydraulik_IDG_und_Treibstoff_sind_gesperrt(string name, bool allowed) =>
        Assert.Equal(allowed, ActionSafety.VariableProblem(name) is null);

    [Theory]
    [InlineData("A32NX.HYD_PTU_TOGGLE")]
    [InlineData("A32NX.RAT_MANUAL_DEPLOY")]
    [InlineData("FUELSYSTEM_PUMP_OFF")]
    [InlineData("TOGGLE_ENGINE1_FAILURE")]
    public void Gesperrte_Events_bleiben_gesperrt(string name) => Assert.NotNull(ActionSafety.EventProblem(name, 0));

    [Fact]
    public void Gesperrte_Bereiche_und_IDs_werden_beim_Laden_entfernt()
    {
        static ProfileAction Harmless(string id, string area) => new()
        {
            Id = id, Area = area, Label = id, Readback = id,
            Steps = [new ActionStep { Set = "L:A32NX_TEST_SWITCH", Value = J("1") }],
        };

        var profile = new AircraftProfile
        {
            Name = "Test",
            Actions =
            [
                Harmless("eng.master1", "Engine"),
                Harmless("fuel.xfeed", "Fuel"),
                Harmless("fire.eng1", "Fire"),
                Harmless("hyd.ptu", "Hydraulic"),
                Harmless("elec.idg1", "Elec"),
                Harmless("x.y", "Hyd"),
                Harmless("antiice.engines", "AntiIce"),
                Harmless("lights.test", "Lights"),
            ],
        };
        var errors = new List<string>();

        ProfileStore.ValidateActions(profile, errors);

        Assert.Equal(new[] { "antiice.engines", "lights.test" }, profile.Actions.Select(a => a.Id));
        Assert.Equal(6, errors.Count);
        Assert.All(errors, e => Assert.Contains("blocked", e));
    }

    [Fact]
    public void Sicherheitsrelevante_Schritte_nur_unter_ihrer_Standard_ID()
    {
        var profile = new AircraftProfile
        {
            Name = "Test",
            Limits = Limits,
            Actions =
            [
                // Beide Packs in einer Aktion käme an der Sperre "beide Packs aus nur am Boden" vorbei
                new ProfileAction
                {
                    Id = "air.packs", Readback = "Packs {value}",
                    Parameter = new ActionParameter { Type = "choice", Options = new() { ["on"] = 1, ["off"] = 0 } },
                    Steps =
                    [
                        new ActionStep { Set = "L:A32NX_OVHD_COND_PACK_1_PB_IS_ON", Value = J("\"{value}\"") },
                        new ActionStep { Set = "L:A32NX_OVHD_COND_PACK_2_PB_IS_ON", Value = J("\"{value}\"") },
                    ],
                },
                new ProfileAction { Id = "config.gear", Readback = "Gear", Steps = [new ActionStep { Event = "GEAR_DOWN" }], Interlocks = [Interlocks.GearSpeed] },
                new ProfileAction { Id = "xpdr.standby", Readback = "Standby", Steps = [new ActionStep { Set = "L:A32NX_TRANSPONDER_MODE", Value = J("0") }] },
                new ProfileAction { Id = "adirs.ir1", Readback = "IR 1 off", Steps = [new ActionStep { Set = "L:A32NX_OVHD_ADIRS_IR_1_MODE_SELECTOR_KNOB", Value = J("0") }] },
                GearDown(),
            ],
        };
        var errors = new List<string>();

        ProfileStore.ValidateActions(profile, errors);

        Assert.Equal(new[] { "adirs.ir1", "gear.down" }, profile.Actions.Select(a => a.Id));
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("air.pack1"));
    }

    [Fact]
    public void Mitgelieferte_Profile_haben_alle_Grenzwerte()
    {
        var store = RepoStore();
        Assert.Empty(store.Warnings);
        foreach (var name in new[] { "FlyByWire A32NX", "Headwind A330-900neo" })
        {
            var limits = store.All.Single(p => p.Name == name).Limits;
            Assert.Equal(25000, limits.MaxGearAltitudeFt);
            Assert.Equal(new[] { "FULL" }, limits.SpeedbrakeInhibitFlaps);
            Assert.Equal("ofp", limits.MsaSource);
        }
    }

    [Fact]
    public void Fehlende_Grenzwerte_stehen_als_Warnung_im_Profil()
    {
        var profile = new AircraftProfile
        {
            Name = "Ohne",
            Limits = new AircraftLimits { VfeKt = new() { ["1"] = 230 } },
            Actions = [GearDown(), Flaps(), new ProfileAction { Id = "speedbrake", Readback = "Speedbrake", Steps = [new ActionStep { Event = "SPOILERS_SET" }] }],
        };
        var errors = new List<string>();
        var warnings = new List<string>();

        ProfileStore.ValidateActions(profile, errors, warnings);

        Assert.Empty(errors);
        Assert.Equal(3, profile.Actions.Count); // nichts entfernt – nur mit Rückfrage
        Assert.Contains(warnings, w => w.Contains("vloKt"));
        Assert.Contains(warnings, w => w.Contains("maxGearAltitudeFt"));
        Assert.Contains(warnings, w => w.Contains("vfeKt") && w.Contains("FULL"));
        Assert.Contains(warnings, w => w.Contains("speedbrakeInhibitFlaps"));
    }

    // ------------------------------------------------------------------ 3. Plausibilitätssperren je Phase

    [Fact]
    public void Gear_down_nur_unter_VLO_und_unter_25000_ft()
    {
        var p = A339();
        Assert.Equal(Allowed, Judge(p, "gear.down", null, Flight(ias: 240, alt: 8000)).Outcome);
        Assert.Equal(Allowed, Judge(p, "gear.down", null, OnGround()).Outcome);

        var fast = Judge(p, "gear.down", null, Flight(ias: 260, alt: 8000));
        Assert.Equal(Locked, fast.Outcome);
        Assert.Contains("VLO 250", fast.Reason);

        var high = Judge(p, "gear.down", null, Flight(ias: 240, alt: 26000));
        Assert.Equal(Locked, high.Outcome);
        Assert.Contains("25000", high.Reason);

        // Druckhöhe zählt, nicht die angezeigte Höhe
        Assert.Equal(Locked, Judge(p, "gear.down", null, Flight(ias: 240, alt: 24000) with { PressureAltitudeFt = 25100 }).Outcome);

        // Fehlt die Höchsthöhe im Profil: nur mit Rückfrage
        var noAlt = Judge(p, "gear.down", null, Flight(ias: 240, alt: 8000), limits: A339Limits(l => l.MaxGearAltitudeFt = null));
        Assert.Equal(Ask, noAlt.Outcome);
        Assert.True(Assert.Single(noAlt.Cautions).MissingLimit);
    }

    [Fact]
    public void Fehlende_VLO_und_VFE_nur_mit_Rueckfrage()
    {
        var p = A339();
        var noVlo = Judge(p, "gear.down", null, Flight(ias: 240), limits: A339Limits(l => { l.VloKt = null; l.VleKt = null; }));
        Assert.Equal(Ask, noVlo.Outcome);
        Assert.Contains(noVlo.Cautions, c => c.Code == "LIMIT_MISSING_VLO");

        Assert.Equal(Allowed, Judge(p, "flaps", "2", Flight(ias: 190)).Outcome);
        Assert.Equal(Locked, Judge(p, "flaps", "3", Flight(ias: 190)).Outcome);
        var noVfe = Judge(p, "flaps", "3", Flight(ias: 190), limits: A339Limits(l => l.VfeKt.Remove("3")));
        Assert.Equal(Ask, noVfe.Outcome);
        Assert.Contains(noVfe.Cautions, c => c.Code == "LIMIT_MISSING_VFE" && c.MissingLimit);
    }

    [Fact]
    public void Speedbrake_nicht_in_gesperrten_Klappenstellungen()
    {
        var p = A339();
        Assert.Equal(Allowed, Judge(p, "speedbrake", "half", Flight()).Outcome);
        Assert.Equal(Allowed, Judge(p, "speedbrake", "full", Flight(flaps: "2")).Outcome);

        var full = Judge(p, "speedbrake", "half", Flight(ias: 160, flaps: "FULL"));
        Assert.Equal(Locked, full.Outcome);
        Assert.Contains("FULL", full.Reason);

        // Einfahren geht immer
        Assert.Equal(Allowed, Judge(p, "speedbrake", "retract", Flight(ias: 160, flaps: "FULL")).Outcome);

        // Ohne Liste im Profil: mit ausgefahrenen Klappen nur mit Rückfrage, eingefahren erlaubt
        var unknownLimit = A339Limits(l => l.SpeedbrakeInhibitFlaps = null);
        Assert.Equal(Ask, Judge(p, "speedbrake", "half", Flight(flaps: "2"), limits: unknownLimit).Outcome);
        Assert.Equal(Allowed, Judge(p, "speedbrake", "half", Flight(), limits: unknownLimit).Outcome);

        // Klappenstellung unbekannt: Rückfrage
        Assert.Equal(Ask, Judge(p, "speedbrake", "half", Flight() with { FlapsLabel = null, FlapsIndex = null }).Outcome);
    }

    [Fact]
    public void Batterien_aus_nur_am_Boden()
    {
        var p = A339();
        Assert.Equal(Allowed, Judge(p, "elec.battery", "off", OnGround()).Outcome);
        Assert.Equal(Allowed, Judge(p, "elec.battery", "on", Flight()).Outcome);

        var air = Judge(p, "elec.battery", "off", Flight());
        Assert.Equal(Locked, air.Outcome);
        Assert.Contains("on the ground", air.Reason);
        Assert.Equal(Locked, Judge(p, "elec.battery", "off", Flight() with { OnGround = null }).Outcome);
    }

    [Fact]
    public void Beide_Packs_aus_nur_am_Boden()
    {
        var p = A339();
        Dictionary<string, bool?> OtherPack(bool? on) => new() { [SafetyRules.OtherPackOn] = on };

        Assert.Equal(Allowed, Judge(p, "air.pack1", "off", Flight(), OtherPack(true)).Outcome);
        Assert.Equal(Allowed, Judge(p, "air.pack2", "off", OnGround(), OtherPack(false)).Outcome);
        Assert.Equal(Allowed, Judge(p, "air.pack1", "on", Flight(), OtherPack(false)).Outcome);

        var both = Judge(p, "air.pack2", "off", Flight(), OtherPack(false));
        Assert.Equal(Locked, both.Outcome);
        Assert.Contains("Pack 1", both.Reason);

        Assert.Equal(Ask, Judge(p, "air.pack1", "off", Flight(), OtherPack(null)).Outcome);

        // Gelesen wird der Schalter des anderen Packs
        var plan = ActionPlanner.Plan(p.Actions.Single(a => a.Id == "air.pack1"), "off", p.Limits, Flight()).Plan!;
        var read = Assert.Single(SafetyRules.Reads(plan, p));
        Assert.Equal(SafetyRules.OtherPackOn, read.Key);
        Assert.Equal("L:A32NX_OVHD_COND_PACK_2_PB_IS_ON", read.Condition.Variable);
        Assert.Equal(1, read.Condition.Value);
    }

    [Fact]
    public void APU_Master_aus_nicht_bei_APU_Bleed_an()
    {
        var p = A339();
        Dictionary<string, bool?> Bleed(bool? on) => new() { [SafetyRules.ApuBleedOn] = on };

        Assert.Equal(Allowed, Judge(p, "apu.master", "off", OnGround(), Bleed(false)).Outcome);
        Assert.Equal(Allowed, Judge(p, "apu.master", "on", OnGround(), Bleed(true)).Outcome);

        var bleed = Judge(p, "apu.master", "off", OnGround(), Bleed(true));
        Assert.Equal(Locked, bleed.Outcome);
        Assert.Contains("APU bleed", bleed.Reason);

        Assert.Equal(Ask, Judge(p, "apu.master", "off", OnGround(), Bleed(null)).Outcome);

        // APU Bleed kommt aus der Bindung des Profils
        var plan = ActionPlanner.Plan(p.Actions.Single(a => a.Id == "apu.master"), "off", p.Limits, OnGround()).Plan!;
        Assert.Equal("L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON", Assert.Single(SafetyRules.Reads(plan, p)).Condition.Variable);
    }

    // ------------------------------------------------------------------ 4. Rückfrage mit Warnhinweis

    [Theory]
    [InlineData("xpdr.mode", "stby", "XPDR_STBY_IN_FLIGHT")]
    [InlineData("xpdr.altReporting", "off", "ALT_RPTG_OFF_IN_FLIGHT")]
    [InlineData("tcas.mode", "stby", "TCAS_STBY_IN_FLIGHT")]
    [InlineData("fcu.ap1", "off", "AP1_OFF")]
    [InlineData("fcu.ap2", "off", "AP2_OFF")]
    [InlineData("fcu.athr", "off", "ATHR_OFF")]
    public void Im_Flug_nur_mit_Rueckfrage_am_Boden_erlaubt(string id, string value, string code)
    {
        var p = A339();
        var air = Judge(p, id, value, Flight());
        Assert.Equal(Ask, air.Outcome);
        Assert.Equal(code, Assert.Single(air.Cautions).Code);
        Assert.False(air.Cautions[0].MissingLimit);

        Assert.Equal(Allowed, Judge(p, id, value, OnGround()).Outcome);

        // Unklar, ob am Boden: wie im Flug
        Assert.Equal(Ask, Judge(p, id, value, Flight() with { OnGround = null }).Outcome);
    }

    [Theory]
    [InlineData("xpdr.mode", "auto")]
    [InlineData("xpdr.mode", "on")]
    [InlineData("xpdr.altReporting", "on")]
    [InlineData("tcas.mode", "tara")]
    [InlineData("fcu.ap1", "on")]
    [InlineData("fcu.athr", "on")]
    public void Einschalten_braucht_keine_Rueckfrage(string id, string value) =>
        Assert.Equal(Allowed, Judge(A339(), id, value, Flight()).Outcome);

    [Theory]
    [InlineData("7500", "hijack")]
    [InlineData("7600", "radio failure")]
    [InlineData("7700", "emergency")]
    public void Notfall_Squawks_nur_mit_Rueckfrage(string code, string spoken)
    {
        var p = A339();
        foreach (var state in new[] { Flight(), OnGround() })
        {
            var result = Judge(p, "xpdr.code", code, state);
            Assert.Equal(Ask, result.Outcome);
            Assert.Contains(spoken, Assert.Single(result.Cautions).Spoken);
        }

        Assert.Equal(Allowed, Judge(p, "xpdr.code", "7000", Flight()).Outcome);
        Assert.Equal(Allowed, Judge(p, "xpdr.code", "2340", Flight()).Outcome);
    }

    [Fact]
    public void ADIRS_aus_im_Flug_gesperrt_am_Boden_mit_Rueckfrage()
    {
        var profile = new AircraftProfile
        {
            Name = "Test",
            Limits = Limits,
            Actions =
            [
                new ProfileAction
                {
                    Id = "adirs.ir1", Readback = "IR one {value}",
                    Parameter = new ActionParameter { Type = "choice", Options = new() { ["off"] = 0, ["nav"] = 1, ["att"] = 2 } },
                    Steps = [new ActionStep { Set = "L:A32NX_OVHD_ADIRS_IR_1_MODE_SELECTOR_KNOB", Value = J("\"{value}\"") }],
                },
            ],
        };
        ProfileStore.ValidateActions(profile, []);
        Assert.Single(profile.Actions);

        // Im Flug irreversibel (kein Ausrichten in der Luft): fest gesperrt, auch wenn unklar ist, ob am Boden
        var air = Judge(profile, "adirs.ir1", "off", Air());
        Assert.Equal(Locked, air.Outcome);
        Assert.Contains("realignment", air.Reason);
        Assert.Equal(Locked, Judge(profile, "adirs.ir1", "off", Air() with { OnGround = null }).Outcome);

        // Am Boden nur mit Rückfrage
        var ground = Judge(profile, "adirs.ir1", "off", Ground());
        Assert.Equal(Ask, ground.Outcome);
        Assert.Equal("ADIRS_OFF", Assert.Single(ground.Cautions).Code);

        Assert.Equal(Allowed, Judge(profile, "adirs.ir1", "nav", Air()).Outcome);
    }

    [Fact]
    public void FCU_Hoehe_unter_MSA_nur_mit_Rueckfrage()
    {
        var p = A339();
        var msa = new MsaReading(6000, "MORA ABC–DEF", null);

        Assert.Equal(Allowed, Judge(p, "fcu.altitude", "FL100", Flight(alt: 12000), msa: msa).Outcome);
        var below = Judge(p, "fcu.altitude", "4000", Flight(alt: 8000), msa: msa);
        Assert.Equal(Ask, below.Outcome);
        Assert.Equal("BELOW_MSA", Assert.Single(below.Cautions).Code);
        Assert.Contains("6000", below.Cautions[0].Spoken);

        // Freigegeben ist freigegeben (Radar-Vectoring unter MSA)
        Assert.Equal(Allowed, Judge(p, "fcu.altitude", "4000", Flight(alt: 8000), cleared: 4000, msa: msa).Outcome);

        // MSA unbekannt oder keine Quelle im Profil: Rückfrage, als fehlender Grenzwert vermerkt
        var unknown = Judge(p, "fcu.altitude", "FL100", Flight(alt: 12000), msa: new MsaReading(null, "ofp", "kein SimBrief-Plan geladen"));
        Assert.Equal("MSA_UNKNOWN", Assert.Single(unknown.Cautions).Code);
        Assert.True(unknown.Cautions[0].MissingLimit);
        var noSource = Judge(p, "fcu.altitude", "FL100", Flight(alt: 12000), msa: msa, limits: A339Limits(l => l.MsaSource = null));
        Assert.Equal("LIMIT_MISSING_MSA_SOURCE", Assert.Single(noSource.Cautions).Code);
    }

    [Theory]
    [InlineData(30000, 10000, "FL200", Allowed)] // Sinkflug FL300 → FL100: Zwischenstufe
    [InlineData(30000, 10000, "FL100", Allowed)]
    [InlineData(30000, 10000, "FL080", Ask)] // unter die Freigabe
    [InlineData(30000, 10000, "FL320", Ask)] // nach oben, ohne Freigabe
    [InlineData(32000, 32000, "FL340", Ask)] // Step Climb ohne Freigabe (Testlog 02:10Z)
    [InlineData(32000, 34000, "FL340", Allowed)]
    [InlineData(5000, 7000, "6000", Allowed)]
    public void FCU_Hoehe_ausserhalb_der_Freigabe_nur_mit_Rueckfrage(double current, int cleared, string target, string expected)
    {
        var msa = new MsaReading(3000, "MORA", null);
        Assert.Equal(expected, Judge(A339(), "fcu.altitude", target, Flight(alt: current), cleared: cleared, msa: msa).Outcome);
    }

    [Fact]
    public void Ohne_ATC_Sitzung_keine_Freigabepruefung()
    {
        var msa = new MsaReading(3000, "MORA", null);
        Assert.Equal(Allowed, Judge(A339(), "fcu.altitude", "FL380", Flight(alt: 36000), cleared: null, msa: msa).Outcome);
    }

    [Fact]
    public void Rueckfrage_klingt_wie_eine_Gegenbestaetigung()
    {
        var plan = ActionPlanner.Plan(A339Action("xpdr.mode"), "stby", A339().Limits, Flight()).Plan! with
        {
            Cautions = [new SafetyCaution("XPDR_STBY_IN_FLIGHT", "transponder standby in flight", "x")],
        };
        Assert.Equal("Caution, transponder standby in flight. Confirm transponder standby?", plan.CautionPrompt);
        Assert.True(plan.NeedsCrossConfirmation);
    }

    // ------------------------------------------------------------------ Ablauf: zweite Bestätigung mit Warnhinweis

    private sealed class FixedContext(int? cleared, MsaReading msa) : IInputContext
    {
        public int? ClearedAltitudeFt() => cleared;

        public MsaReading Msa(string? source, FlightSnapshot snapshot) => msa;
    }

    [Fact]
    public async Task Kritische_Aktion_braucht_die_zweite_Bestaetigung()
    {
        var (input, backend, time) = CreateA339();
        backend.Current = Flight();
        backend.State["L:A32NX_TRANSPONDER_MODE"] = 1;

        var prepared = await input.PrepareAsync("xpdr.mode", "stby", default);
        Assert.Equal(InputResult.AwaitingConfirmation, prepared.Status);
        Assert.Equal("Transponder standby", prepared.Readback);
        Assert.True(prepared.CautionFollows);

        time.Now += TimeSpan.FromSeconds(2);
        var caution = await input.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.Equal(InputResult.AwaitingCautionConfirmation, caution.Status);
        Assert.Equal("Caution, transponder standby in flight. Confirm transponder standby?", caution.Caution);
        Assert.Equal(prepared.Token, caution.Token);
        Assert.Empty(backend.Sent);
        Assert.True(input.Pending!.CautionStage);
        Assert.Contains(input.History, e => e.Outcome == "caution");

        // Die Rückfrage muss erst gehört werden
        Assert.Equal(InputResult.Failed, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
        Assert.Empty(backend.Sent);

        time.Now += TimeSpan.FromSeconds(2);
        var done = await input.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.Equal(InputResult.Executed, done.Status);
        Assert.Equal(new[] { "L:A32NX_TRANSPONDER_MODE=0" }, backend.Sent);
        Assert.Null(input.Pending);
    }

    [Fact]
    public async Task Negative_bei_der_Rueckfrage_verwirft()
    {
        var (input, backend, time) = CreateA339();
        backend.Current = Flight();
        backend.State["L:A32NX_FCU_ATHR_LIGHT_ON"] = 1;

        var prepared = await input.PrepareAsync("fcu.athr", "off", default);
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(InputResult.AwaitingCautionConfirmation, (await input.ConfirmAsync(prepared.Token!, "confirm", default)).Status);
        time.Now += TimeSpan.FromSeconds(2);

        Assert.Equal(InputResult.Cancelled, (await input.ConfirmAsync(prepared.Token!, "negative", default)).Status);
        Assert.Empty(backend.Sent);
        Assert.Null(input.Pending);
    }

    [Fact]
    public async Task Keine_Rueckfrage_wenn_der_Schalter_schon_so_steht()
    {
        var (input, backend, time) = CreateA339();
        backend.Current = Flight();
        backend.State["L:A32NX_FCU_ATHR_LIGHT_ON"] = 0; // Autothrust ist schon aus

        var prepared = await input.PrepareAsync("fcu.athr", "off", default);
        Assert.False(prepared.CautionFollows);
        time.Now += TimeSpan.FromSeconds(2);

        var done = await input.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.Equal(InputResult.Executed, done.Status);
        Assert.True(done.AlreadySet);
    }

    [Fact]
    public async Task Rueckfrage_auch_wenn_sich_der_Zustand_erst_nach_dem_Readback_aendert()
    {
        var (input, backend, time) = CreateA339(onGround: true);
        backend.Current = OnGround();
        backend.State["L:A32NX_TRANSPONDER_MODE"] = 1;

        var prepared = await input.PrepareAsync("xpdr.mode", "stby", default);
        Assert.False(prepared.CautionFollows); // am Boden kein Warnhinweis

        backend.Current = Flight(); // inzwischen abgehoben
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);

        Assert.Equal(InputResult.AwaitingCautionConfirmation, result.Status);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Sperren_gelten_auch_im_Ablauf()
    {
        var (input, backend, _) = CreateA339(onGround: true);
        backend.Current = OnGround();
        backend.State["L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON"] = 1;
        backend.State["L:A32NX_OVHD_APU_MASTER_SW_PB_IS_ON"] = 1;

        var refused = await input.PrepareAsync("apu.master", "off", default);
        Assert.Equal(InputResult.Refused, refused.Status);
        Assert.Contains("APU bleed", refused.Reason);

        backend.State["L:A32NX_OVHD_PNEU_APU_BLEED_PB_IS_ON"] = 0;
        var ok = await input.PrepareAsync("apu.master", "off", default);
        Assert.Equal(InputResult.AwaitingConfirmation, ok.Status);
        Assert.False(ok.CautionFollows);
    }

    [Fact]
    public async Task Fehlender_Grenzwert_wird_im_Protokoll_vermerkt()
    {
        var (input, backend, time) = CreateA339();
        backend.Profile!.Limits.MaxGearAltitudeFt = null;
        backend.Current = Flight(ias: 200);

        var prepared = await input.PrepareAsync("gear.down", null, default);
        Assert.True(prepared.CautionFollows);
        Assert.Contains(input.History, e => e.Outcome == "limit missing" && e.Detail!.Contains("maxGearAltitudeFt"));
    }

    [Fact]
    public async Task FCU_Hoehe_nutzt_Freigabe_und_MSA_der_Umgebung()
    {
        var time = new ManualTime();
        var backend = new FakeBackend(time) { Profile = A339(), Current = Flight(alt: 30000) };
        var options = Options.Create(new DuopiloOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N")),
            Input = new InputOptions { Enabled = true },
        });
        var context = new FixedContext(10000, new MsaReading(5000, "MORA", null));
        var input = new InputService(options, backend, NullLogger<InputService>.Instance, time, context);

        Assert.False((await input.PrepareAsync("fcu.altitude", "FL100", default)).CautionFollows);
        Assert.True((await input.PrepareAsync("fcu.altitude", "FL080", default)).CautionFollows);
    }

    // ------------------------------------------------------------------ 5. Nachlesen mit Callout

    private static (InputService Input, FakeBackend Backend, ManualTime Time, AlertBus Bus) CreateWithVerifier(AircraftProfile profile, FlightSnapshot state)
    {
        var time = new ManualTime();
        var backend = new FakeBackend(time) { Profile = profile, Current = state };
        backend.State["SIM ON GROUND"] = state.OnGround == true ? 1 : 0;
        var options = Options.Create(new DuopiloOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N")),
            Input = new InputOptions { Enabled = true },
        });
        var bus = new AlertBus();
        var verifier = new ConfigVerifier(backend, NullLogger<ConfigVerifier>.Instance, bus, new ConfigCalloutClaims(), time);
        var input = new InputService(options, backend, NullLogger<InputService>.Instance, time, null, verifier);
        return (input, backend, time, bus);
    }

    private static async Task<InputResult> Execute(InputService input, ManualTime time, string id, string? value)
    {
        var prepared = await input.PrepareAsync(id, value, default);
        Assert.Equal(InputResult.AwaitingConfirmation, prepared.Status);
        time.Now += TimeSpan.FromSeconds(2);
        var result = await input.ConfirmAsync(prepared.Token!, "confirm", default);
        Assert.Equal(InputResult.Executed, result.Status);
        return result;
    }

    private static void SetGear(FakeBackend backend, double nose, double left, double right)
    {
        backend.State["GEAR CENTER POSITION"] = nose;
        backend.State["GEAR LEFT POSITION"] = left;
        backend.State["GEAR RIGHT POSITION"] = right;
    }

    [Fact]
    public async Task Gear_down_three_green()
    {
        var (input, backend, time, bus) = CreateWithVerifier(A339(), Flight(ias: 200));
        SetGear(backend, 0, 0, 0);
        backend.OnEvent = (name, _) =>
        {
            if (name == "GEAR_DOWN")
            {
                backend.State["L:A32NX_GEAR_HANDLE_POSITION"] = 1;
                SetGear(backend, 100, 100, 100);
            }
        };

        var result = await Execute(input, time, "gear.down", null);
        Assert.Contains("three green", result.FollowUp);
        await input.LastVerification!;

        var callout = Assert.Single(bus.Since(0));
        Assert.Equal(Rules.PmGear, callout.Rule);
        Assert.Equal("Gear down, three green", callout.Callout);
        Assert.Equal(AlertSeverity.Info, callout.Severity);
        Assert.Contains(input.History, e => e.Outcome == "checked");
    }

    [Fact]
    public async Task Gear_disagree_wenn_ein_Bein_nicht_verriegelt()
    {
        var (input, backend, time, bus) = CreateWithVerifier(A339(), Flight(ias: 200));
        SetGear(backend, 0, 0, 0);
        backend.OnEvent = (name, _) =>
        {
            if (name == "GEAR_DOWN")
            {
                backend.State["L:A32NX_GEAR_HANDLE_POSITION"] = 1;
                SetGear(backend, 100, 100, 60); // rechtes Hauptfahrwerk hängt
            }
        };

        await Execute(input, time, "gear.down", null);
        await input.LastVerification!;

        var callout = Assert.Single(bus.Since(0));
        Assert.Equal("Gear disagree", callout.Callout);
        Assert.Equal(AlertSeverity.Caution, callout.Severity);
        Assert.Contains("right 60", callout.Message);
        Assert.Contains(input.History, e => e.Outcome == "disagree");
    }

    [Fact]
    public async Task Gear_up_erst_wenn_alle_eingefahren()
    {
        var (input, backend, time, bus) = CreateWithVerifier(A339(), Flight(ias: 180, alt: 1500));
        SetGear(backend, 100, 100, 100);
        backend.OnEvent = (name, _) =>
        {
            if (name == "GEAR_UP")
            {
                backend.State["L:A32NX_GEAR_HANDLE_POSITION"] = 0;
                SetGear(backend, 0, 0, 0);
            }
        };

        await Execute(input, time, "gear.up", null);
        await input.LastVerification!;

        Assert.Equal("Gear up", Assert.Single(bus.Since(0)).Callout);
    }

    [Fact]
    public async Task Flaps_two_erst_bei_erreichter_Stellung()
    {
        var (input, backend, time, bus) = CreateWithVerifier(A339(), Flight(ias: 190));
        backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 1;
        backend.State["TRAILING EDGE FLAPS LEFT PERCENT"] = 0;
        backend.State["LEADING EDGE FLAPS LEFT PERCENT"] = 70;
        backend.OnEvent = (name, _) =>
        {
            if (name == "FLAPS_2")
            {
                backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 2;
                backend.State["TRAILING EDGE FLAPS LEFT PERCENT"] = 10; // fährt noch …
                backend.RevertAfterRead["TRAILING EDGE FLAPS LEFT PERCENT"] = 43; // … und steht dann
            }
        };

        var result = await Execute(input, time, "flaps", "2");
        Assert.Contains("Flaps two", result.FollowUp);
        await input.LastVerification!;

        var callout = Assert.Single(bus.Since(0));
        Assert.Equal(Rules.PmFlaps, callout.Rule);
        Assert.Equal("Flaps two", callout.Callout);
    }

    [Fact]
    public async Task Flaps_zero_wenn_alles_eingefahren()
    {
        var (input, backend, time, bus) = CreateWithVerifier(A339(), Flight(ias: 220, flaps: "1"));
        backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 1;
        backend.State["TRAILING EDGE FLAPS LEFT PERCENT"] = 0;
        backend.State["LEADING EDGE FLAPS LEFT PERCENT"] = 0;
        backend.OnEvent = (name, _) => backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 0;

        await Execute(input, time, "flaps", "0");
        await input.LastVerification!;

        Assert.Equal("Flaps zero", Assert.Single(bus.Since(0)).Callout);
    }

    [Fact]
    public async Task Flaps_disagree_ohne_Bewegung()
    {
        var profile = A339();
        profile.Verification = new VerificationConfig { Flaps = FlapsVerification.Default() };
        profile.Verification.Flaps!.TimeoutSec = 6;
        var (input, backend, time, bus) = CreateWithVerifier(profile, Flight(ias: 190));
        backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 1;
        backend.State["TRAILING EDGE FLAPS LEFT PERCENT"] = 0;
        backend.State["LEADING EDGE FLAPS LEFT PERCENT"] = 70;
        backend.OnEvent = (name, _) => backend.State["L:A32NX_FLAPS_HANDLE_INDEX"] = 2; // Hebel ja, Klappen nein

        await Execute(input, time, "flaps", "2");
        await input.LastVerification!;

        var callout = Assert.Single(bus.Since(0));
        Assert.Equal("Flaps disagree", callout.Callout);
        Assert.Contains("no movement", callout.Message);
    }

    [Fact]
    public async Task Nicht_lesbares_Fahrwerk_wird_nicht_angesagt()
    {
        var profile = A339();
        profile.Verification = new VerificationConfig { Gear = GearVerification.Default() };
        profile.Verification.Gear!.TimeoutSec = 5;
        var (input, backend, time, bus) = CreateWithVerifier(profile, Flight(ias: 200));
        backend.OnEvent = (name, _) => backend.State["L:A32NX_GEAR_HANDLE_POSITION"] = 1; // Fahrwerksbeine liest das Flugzeug nicht

        await Execute(input, time, "gear.down", null);
        await input.LastVerification!;

        var e = Assert.Single(bus.Since(0));
        Assert.Null(e.Callout);
        Assert.Contains("not readable", e.Message);
    }

    [Fact]
    public void Monitoring_sagt_den_Hebel_nicht_doppelt_an()
    {
        var t0 = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        FlightSnapshot At(double sec, bool gearDown) => Air(160) with
        {
            TimeUtc = t0.AddSeconds(sec), AircraftKey = "test", AltitudeFt = 2500, PressureAltitudeFt = 2500, AglFt = 2400,
            GearHandleDown = gearDown, GearExtendedPct = gearDown ? 100 : 0, FlapsLabel = "0", FlapsIndex = 0, ApEngaged = false,
        };

        var claims = new ConfigCalloutClaims();
        var engine = new MonitorEngine(new MonitorSettings { Claims = claims });
        engine.Evaluate(At(0, false));
        engine.Evaluate(At(11, false));

        claims.Claim(ConfigCalloutClaims.Gear, t0.AddSeconds(60));
        var claimed = new[] { At(12, true), At(12.5, true), At(13.2, true) }.SelectMany(engine.Evaluate).ToList();
        Assert.DoesNotContain(claimed, e => e.Rule == Rules.PmGear);

        // Ohne Eintrag sagt das Monitoring den Hebel wie bisher an
        var free = new MonitorEngine();
        free.Evaluate(At(0, false));
        free.Evaluate(At(11, false));
        var events = new[] { At(12, true), At(12.5, true), At(13.2, true) }.SelectMany(free.Evaluate).ToList();
        Assert.Contains(events, e => e.Rule == Rules.PmGear && e.Callout == "Gear down");
    }

    [Fact]
    public void MSA_aus_der_MORA_des_aktuellen_Abschnitts()
    {
        var ofp = new Duopilo.FlightPlan.Ofp
        {
            Origin = new Duopilo.FlightPlan.OfpAirport("EDDF", null, null, 50.03, 8.57, 364, 5000, null, null),
            Destination = new Duopilo.FlightPlan.OfpAirport("EDDS", null, null, 48.69, 9.22, 1276, null, 7000, null),
            Fixes =
            [
                new Duopilo.FlightPlan.OfpFix { Ident = "AAA", Lat = 49.8, Lon = 8.7, MoraFt = 4300 },
                new Duopilo.FlightPlan.OfpFix { Ident = "BBB", Lat = 49.4, Lon = 8.9, MoraFt = 5900 },
                new Duopilo.FlightPlan.OfpFix { Ident = "EDDS", Lat = 48.69, Lon = 9.22, MoraFt = 4100 },
            ],
        };
        var points = ofp.RoutePoints();
        var route = Duopilo.FlightPlan.RouteTracker.Locate(points, 49.6, 8.8);

        var msa = MsaCalculator.FromOfp(ofp, points, route);
        Assert.Equal(5900, msa.Ft);
        Assert.Contains("AAA", msa.Source);

        Assert.Null(MsaCalculator.FromOfp(ofp, points, null).Ft);
        Assert.Equal(5900, Duopilo.FlightPlan.OfpParser.Mora(59));
        Assert.Equal(5900, Duopilo.FlightPlan.OfpParser.Mora(5900));
        Assert.Null(Duopilo.FlightPlan.OfpParser.Mora(0));
    }
}
