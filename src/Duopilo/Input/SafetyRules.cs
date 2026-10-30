using System.Globalization;
using Kopilot.Aircraft;
using Kopilot.Localization;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>MSA laut Profil-Quelle. <see cref="Ft"/> null = unbekannt, <see cref="Problem"/> sagt warum.</summary>
public sealed record MsaReading(double? Ft, string? Source, string? Problem);

/// <summary>Zustand, den eine Sperre live lesen muss (z. B. "anderes Pack an?").</summary>
public sealed record SafetyRead(string Key, ResolvedCondition Condition);

/// <summary>Alles, was die Plausibilitätssperren über den Moment wissen müssen.</summary>
public sealed record SafetyContext
{
    public required FlightSnapshot Snapshot { get; init; }

    public AircraftLimits Limits { get; init; } = new();

    /// <summary>Live gelesene Zustände je <see cref="SafetyRead.Key"/>: true/false, null = nicht lesbar. Fehlt der Schlüssel = nicht gelesen.</summary>
    public IReadOnlyDictionary<string, bool?> States { get; init; } = new Dictionary<string, bool?>();

    /// <summary>Freigegebene Höhe der laufenden ATC-Sitzung (ft). Null = keine Sitzung oder keine Freigabe.</summary>
    public int? ClearedAltitudeFt { get; init; }

    /// <summary>MSA laut <see cref="AircraftLimits.MsaSource"/>. Null = nicht ermittelt.</summary>
    public MsaReading? Msa { get; init; }
}

/// <summary>Ergebnis der Plausibilitätssperren: abgelehnt oder erlaubt, ggf. mit Rückfragen.</summary>
public sealed record SafetyVerdict(string? Refusal, IReadOnlyList<SafetyCaution> Cautions)
{
    public static readonly SafetyVerdict Allowed = new(null, []);
}

/// <summary>
/// Plausibilitätssperren je Phase und Rückfragen mit Warnhinweis – im Code festgelegt, hängen an den Standard-IDs
/// (<see cref="ActionSafety.CanonicalIdProblem"/> sorgt dafür, dass die Schritte nur unter diesen IDs stehen).
/// Rein rechnend: Live-Zustände liest das Eingabe-Modul vorher (<see cref="Reads"/>).
/// <list type="bullet">
/// <item>Sperren: gear.down nur unter der Höchsthöhe (A330: 25 000 ft; VLO prüft <see cref="Interlocks.GearSpeed"/>),
/// Speedbrake nicht in den gesperrten Klappenstellungen, Batterien aus und beide Packs aus nur am Boden,
/// APU Master aus nicht bei APU Bleed an, ADIRS aus nur am Boden (im Flug irreversibel: kein Ausrichten in der Luft).</item>
/// <item>Rückfrage: Transponder STBY, ALT RPTG aus und TCAS STBY im Flug, Squawk 7500/7600/7700, ADIRS aus am Boden, AP/ATHR aus im Flug,
/// FCU-Höhe unter MSA oder außerhalb der ATC-Freigabe.</item>
/// <item>Fehlt ein Grenzwert oder eine Quelle im Profil (Höchsthöhe Fahrwerk, Speedbrake-Klappen, MSA-Quelle): nur mit Rückfrage,
/// vermerkt im Log.</item>
/// </list>
/// </summary>
public static class SafetyRules
{
    /// <summary>Live-Zustand: das jeweils andere Pack ist an.</summary>
    public const string OtherPackOn = "otherPackOn";

    /// <summary>Live-Zustand: APU Bleed ist an.</summary>
    public const string ApuBleedOn = "apuBleedOn";

    /// <summary>Toleranz zur freigegebenen Höhe (ft).</summary>
    private const double ClearanceToleranceFt = 50;

    /// <summary>Toleranz zur aktuellen Höhe beim Freigabe-Korridor (ft) – QNH/STD und Anzeige schwanken.</summary>
    private const double CurrentAltitudeToleranceFt = 300;

    private static readonly HashSet<string> OffWords = new(StringComparer.OrdinalIgnoreCase) { "off", "stby", "standby", "0" };

    /// <summary>Notfall-Codes: Warnhinweis zum Vorlesen (englisch) und Schlüssel der Erklärung in den Sprachdateien.</summary>
    private static readonly Dictionary<string, (string Spoken, string ReasonKey)> EmergencySquawks = new()
    {
        ["7500"] = ("squawk seven five zero zero, hijack code", "input.caution.squawk7500"),
        ["7600"] = ("squawk seven six zero zero, radio failure code", "input.caution.squawk7600"),
        ["7700"] = ("squawk seven seven zero zero, emergency code", "input.caution.squawk7700"),
    };

    /// <summary>Kurzbeschreibung der Code-Sperren einer Aktion (für list_actions), null ohne.</summary>
    public static string? Describe(string id) => id.ToLowerInvariant() switch
    {
        "gear.down" => Loc.T("input.rule.gearDown"),
        "gear.up" => Loc.T("input.rule.gearUp"),
        "flaps" => Loc.T("input.rule.flaps"),
        "speedbrake" => Loc.T("input.rule.speedbrake"),
        "elec.battery" or "elec.bat1" or "elec.bat2" => Loc.T("input.rule.batteries"),
        "air.pack1" or "air.pack2" => Loc.T("input.rule.packs"),
        "apu.master" => Loc.T("input.rule.apuMaster"),
        "xpdr.mode" => Loc.T("input.rule.xpdrMode"),
        "xpdr.altreporting" => Loc.T("input.rule.altReporting"),
        "tcas.mode" => Loc.T("input.rule.tcasMode"),
        "xpdr.code" => Loc.T("input.rule.squawk"),
        "fcu.ap1" or "fcu.ap2" or "fcu.athr" => Loc.T("input.rule.autoflight"),
        "fcu.altitude" => Loc.T("input.rule.fcuAltitude"),
        _ when id.StartsWith("adirs.", StringComparison.OrdinalIgnoreCase) => Loc.T("input.rule.adirs"),
        _ => null,
    };

    /// <summary>Welche Zustände vor der Auswertung live gelesen werden müssen.</summary>
    public static IReadOnlyList<SafetyRead> Reads(ActionPlan plan, AircraftProfile profile)
    {
        var reads = new List<SafetyRead>();
        var id = plan.Action.Id.Trim().ToLowerInvariant();
        if (id is "air.pack1" or "air.pack2" && IsOff(plan.Option))
        {
            var other = id == "air.pack1" ? "air.pack2" : "air.pack1";
            if (Find(profile, other) is { } otherAction && ActionPlanner.StateFor(otherAction, "on") is { } on)
            {
                reads.Add(new SafetyRead(OtherPackOn, on));
            }
        }

        if (id == "apu.master" && IsOff(plan.Option))
        {
            if (profile.Bindings.TryGetValue(BindingKeys.ApuBleed, out var binding) && !string.IsNullOrWhiteSpace(binding.Name))
            {
                reads.Add(new SafetyRead(ApuBleedOn, new ResolvedCondition(binding.Name.Trim(), binding.EffectiveUnit, 1, 0.5)));
            }
            else if (Find(profile, "apu.bleed") is { } bleed && ActionPlanner.StateFor(bleed, "on") is { } bleedOn)
            {
                reads.Add(new SafetyRead(ApuBleedOn, bleedOn));
            }
        }

        return reads;
    }

    /// <summary>Plausibilitätssperren und Rückfragen für einen fertig geplanten Schritt.</summary>
    public static SafetyVerdict Evaluate(ActionPlan plan, SafetyContext ctx)
    {
        var s = ctx.Snapshot;
        var id = plan.Action.Id.Trim().ToLowerInvariant();
        var option = plan.Option;
        var inFlight = s.OnGround != true; // unbekannt zählt als Flug
        var cautions = new List<SafetyCaution>();

        // ---------------- Fahrwerk ausfahren: unter der Höchsthöhe
        if (plan.Steps.Any(st => IsEvent(st, "GEAR_DOWN")) && s.OnGround != true)
        {
            var altitude = s.PressureAltitudeFt ?? s.AltitudeFt;
            if (ctx.Limits.MaxGearAltitudeFt is not { } maxAlt)
            {
                cautions.Add(new SafetyCaution("LIMIT_MISSING_GEAR_ALTITUDE", "gear altitude limit unknown",
                    Loc.T("input.caution.gearAltitudeMissing"), MissingLimit: true));
            }
            else if (altitude is not { } alt)
            {
                return Refuse(Loc.T("input.safety.gearAltitudeUnknown"));
            }
            else if (alt >= maxAlt)
            {
                return Refuse(Loc.T("input.safety.gearTooHigh", alt, maxAlt));
            }
        }

        // ---------------- Speedbrake: nicht in den gesperrten Klappenstellungen
        if (id == "speedbrake" && plan.Value is > 0.001)
        {
            var flaps = FlapsText(s);
            var retracted = flaps is "0" || (s.FlapsLabel is null && s.FlapsIndex is < 0.5);
            if (flaps is null)
            {
                cautions.Add(new SafetyCaution("FLAPS_UNKNOWN", "flap position unknown", Loc.T("input.caution.flapsUnknown")));
            }
            else if (!retracted)
            {
                if (ctx.Limits.SpeedbrakeInhibitFlaps is not { } inhibit)
                {
                    cautions.Add(new SafetyCaution("LIMIT_MISSING_SPEEDBRAKE_FLAPS", "speedbrake with flaps extended",
                        Loc.T("input.caution.speedbrakeFlapsMissing", flaps), MissingLimit: true));
                }
                else if (inhibit.Contains(flaps, StringComparer.OrdinalIgnoreCase))
                {
                    return Refuse(Loc.T("input.safety.speedbrakeFlaps", flaps));
                }
            }
        }

        // ---------------- Batterien aus: nur am Boden
        if (id is "elec.battery" or "elec.bat1" or "elec.bat2" && IsOff(option))
        {
            if (s.OnGround != true)
            {
                return Refuse(s.OnGround is null
                    ? Loc.T("input.safety.batteriesGroundUnknown")
                    : Loc.T("input.safety.batteriesInFlight"));
            }
        }

        // ---------------- Packs: beide aus nur am Boden
        if (id is "air.pack1" or "air.pack2" && IsOff(option) && inFlight)
        {
            var other = id == "air.pack1" ? "Pack 2" : "Pack 1";
            switch (State(ctx, OtherPackOn))
            {
                case false:
                    return Refuse(Loc.T("input.safety.otherPackOff", other));
                case null:
                    cautions.Add(new SafetyCaution("OTHER_PACK_UNKNOWN", "other pack state unknown",
                        Loc.T("input.caution.otherPackUnknown", other)));
                    break;
            }
        }

        // ---------------- APU Master aus: nicht bei APU Bleed an
        if (id == "apu.master" && IsOff(option))
        {
            switch (State(ctx, ApuBleedOn))
            {
                case true:
                    return Refuse(Loc.T("input.safety.apuBleedOn"));
                case null:
                    cautions.Add(new SafetyCaution("APU_BLEED_UNKNOWN", "APU bleed state unknown",
                        Loc.T("input.caution.apuBleedUnknown")));
                    break;
            }
        }

        // ---------------- Rückfragen: Transponder, TCAS, Squawk
        if (id == "xpdr.mode" && IsOff(option) && inFlight)
        {
            cautions.Add(new SafetyCaution("XPDR_STBY_IN_FLIGHT", "transponder standby in flight",
                Loc.T("input.caution.xpdrStandby")));
        }

        if (id == "xpdr.altreporting" && IsOff(option) && inFlight)
        {
            cautions.Add(new SafetyCaution("ALT_RPTG_OFF_IN_FLIGHT", "altitude reporting off in flight",
                Loc.T("input.caution.altReportingOff")));
        }

        if (id == "tcas.mode" && IsOff(option) && inFlight)
        {
            cautions.Add(new SafetyCaution("TCAS_STBY_IN_FLIGHT", "TCAS standby in flight",
                Loc.T("input.caution.tcasStandby")));
        }

        if (id == "xpdr.code" && plan.ValueText is { } code && EmergencySquawks.TryGetValue(code, out var squawk))
        {
            cautions.Add(new SafetyCaution($"SQUAWK_{code}", squawk.Spoken, Loc.T(squawk.ReasonKey)));
        }

        // ---------------- ADIRS aus: im Flug gesperrt (irreversibel, kein Ausrichten in der Luft), am Boden nur mit Rückfrage
        if (id.StartsWith("adirs.", StringComparison.Ordinal) && IsOff(option))
        {
            if (inFlight)
            {
                return Refuse(s.OnGround is null
                    ? Loc.T("input.safety.adirsGroundUnknown")
                    : Loc.T("input.safety.adirsInFlight"));
            }

            cautions.Add(new SafetyCaution("ADIRS_OFF", "ADIRS off", Loc.T("input.caution.adirsOff")));
        }

        // ---------------- Rückfragen: Autopilot, Autothrust

        if (id is "fcu.ap1" or "fcu.ap2" && IsOff(option) && inFlight)
        {
            var which = id == "fcu.ap1" ? "one" : "two";
            cautions.Add(new SafetyCaution(id == "fcu.ap1" ? "AP1_OFF" : "AP2_OFF", $"autopilot {which} off",
                Loc.T("input.caution.apOff", id == "fcu.ap1" ? 1 : 2)));
        }

        if (id == "fcu.athr" && IsOff(option) && inFlight)
        {
            cautions.Add(new SafetyCaution("ATHR_OFF", "autothrust off", Loc.T("input.caution.athrOff")));
        }

        // ---------------- FCU-Höhe: MSA und Freigabe
        if (id == "fcu.altitude" && plan.Value is { } target)
        {
            AltitudeCautions(target, ctx, cautions);
        }

        return new SafetyVerdict(null, cautions);
    }

    private static void AltitudeCautions(double target, SafetyContext ctx, List<SafetyCaution> cautions)
    {
        var inv = CultureInfo.InvariantCulture;
        var cleared = ctx.ClearedAltitudeFt;
        var matchesClearance = cleared is { } c && Math.Abs(target - c) <= ClearanceToleranceFt;

        // MSA: ATC-Freigaben (Radar-Vectoring-Höhen) dürfen darunter liegen – stimmt das Ziel mit der Freigabe, keine Rückfrage
        if (!matchesClearance)
        {
            if (ctx.Limits.MsaSource is null)
            {
                cautions.Add(new SafetyCaution("LIMIT_MISSING_MSA_SOURCE", "minimum safe altitude unknown",
                    Loc.T("input.caution.msaSourceMissing"), MissingLimit: true));
            }
            else if (ctx.Msa?.Ft is not { } msa)
            {
                cautions.Add(new SafetyCaution("MSA_UNKNOWN", "minimum safe altitude unknown",
                    Loc.T("input.caution.msaUnknown", ctx.Msa?.Problem ?? Loc.T("input.msa.noData")), MissingLimit: true));
            }
            else if (target < msa)
            {
                cautions.Add(new SafetyCaution("BELOW_MSA", $"below minimum safe altitude {SpokenAltitude(msa)}",
                    Loc.T("input.caution.belowMsa", target.ToString("0", inv), msa.ToString("0", inv), ctx.Msa?.Source)));
            }
        }

        // Freigabe: Ziel muss zwischen aktueller und freigegebener Höhe liegen
        if (cleared is { } clearedFt && !matchesClearance)
        {
            var current = ctx.Snapshot.AltitudeFt;
            double lo, hi;
            if (current is not { } cur)
            {
                lo = clearedFt - ClearanceToleranceFt;
                hi = clearedFt + ClearanceToleranceFt;
            }
            else if (clearedFt <= cur)
            {
                lo = clearedFt - ClearanceToleranceFt;
                hi = cur + CurrentAltitudeToleranceFt;
            }
            else
            {
                lo = cur - CurrentAltitudeToleranceFt;
                hi = clearedFt + ClearanceToleranceFt;
            }

            if (target < lo || target > hi)
            {
                cautions.Add(new SafetyCaution("ALTITUDE_NOT_CLEARED", $"altitude not cleared, cleared {SpokenAltitude(clearedFt)}",
                    Loc.T("input.caution.notCleared", target.ToString("0", inv), clearedFt.ToString("0", inv))));
            }
        }
    }

    /// <summary>"flight level three four zero" bzw. "5000 feet" – Ziffern spricht die Sprachausgabe.</summary>
    private static string SpokenAltitude(double ft)
    {
        var rounded = Math.Round(ft / 100) * 100;
        return rounded >= 18000
            ? "flight level " + (rounded / 100).ToString("000", CultureInfo.InvariantCulture)
            : rounded.ToString("0", CultureInfo.InvariantCulture) + " feet";
    }

    /// <summary>Klappenstellung im Klartext des Profils, sonst der Hebel-Index. Null = unbekannt.</summary>
    private static string? FlapsText(FlightSnapshot s) =>
        s.FlapsLabel ?? (s.FlapsIndex is { } fi ? Math.Round(fi).ToString(CultureInfo.InvariantCulture) : null);

    private static bool IsOff(string? option) => option is not null && OffWords.Contains(option.Trim());

    private static bool? State(SafetyContext ctx, string key) => ctx.States.TryGetValue(key, out var v) ? v : null;

    private static bool IsEvent(ResolvedStep step, string name)
    {
        if (step.Event is not { } e)
        {
            return false;
        }

        var n = e.Trim();
        if (n.StartsWith("K:", StringComparison.OrdinalIgnoreCase))
        {
            n = n[2..].Trim();
        }

        return n.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static ProfileAction? Find(AircraftProfile profile, string id) =>
        profile.Actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static SafetyVerdict Refuse(string reason) => new(reason, []);
}
