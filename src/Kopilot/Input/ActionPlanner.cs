using System.Globalization;
using System.Text.Json;
using Kopilot.Aircraft;
using Kopilot.Localization;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>
/// Prüft eine angeforderte Aktion (Wert, Raster, Sperren) und rechnet aus, was an MSFS geht.
/// Ohne Abhängigkeit vom Sim – damit testbar.
/// </summary>
public static class ActionPlanner
{
    public static PlanResult Plan(ProfileAction action, string? input, AircraftLimits limits, FlightSnapshot snapshot)
    {
        if (ActionSafety.Problem(action) is { } problem)
        {
            return PlanResult.Refuse($"{action.Id}: {problem}.");
        }

        if (action.Steps.Count == 0)
        {
            return PlanResult.Refuse(Loc.T("input.plan.noSteps", action.Id));
        }

        // ---------------- Wert
        double value = 0;
        string? valueText = null;
        string? option = null;
        if (action.Parameter is { } p)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return PlanResult.Refuse(Loc.T("input.plan.valueRequired", action.Id, Describe(p)));
            }

            var parsed = p.IsChoice ? ParseChoice(p, input) : ParseNumber(p, input);
            if (parsed.Error is not null)
            {
                return PlanResult.Refuse($"{action.Id}: {parsed.Error}");
            }

            (value, valueText, option) = (parsed.Value, parsed.Text, parsed.Option);
        }

        // ---------------- Sperren (fehlt ein Grenzwert: nur mit Rückfrage)
        var cautions = new List<SafetyCaution>();
        foreach (var name in action.Interlocks)
        {
            var outcome = Interlocks.Evaluate(name, snapshot, limits, option ?? valueText);
            if (outcome.Refusal is { } reason)
            {
                return PlanResult.Refuse(reason);
            }

            if (outcome.Caution is { } caution)
            {
                cautions.Add(caution);
            }
        }

        // ---------------- Schritte
        var steps = new List<ResolvedStep>();
        foreach (var step in action.Steps)
        {
            if (step.Only is { Count: > 0 } only && (option is null || !only.Contains(option, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Event) == string.IsNullOrWhiteSpace(step.Set))
            {
                return PlanResult.Refuse(Loc.T("input.plan.stepEventOrSet", action.Id));
            }

            if (Resolve(step.Value, value, option, fallback: 0) is not { } raw)
            {
                return PlanResult.Refuse(Loc.T("input.plan.noStepValue", action.Id, option, step.Event ?? step.Set));
            }

            var scaled = raw * step.Scale;
            ResolvedCondition? skip = null;
            if (step.SkipIf is { } c && !string.IsNullOrWhiteSpace(c.Variable))
            {
                if (Resolve(c.EqualTo, value, option, fallback: value) is not { } expected)
                {
                    return PlanResult.Refuse(Loc.T("input.plan.noSkipValue", action.Id, option));
                }

                skip = new ResolvedCondition(c.Variable.Trim(), Unit(c.Unit), expected, c.Tolerance);
            }

            if (!string.IsNullOrWhiteSpace(step.Event))
            {
                if (step.Encode?.Equals("bcd16", StringComparison.OrdinalIgnoreCase) == true)
                {
                    scaled = Bcd16((int)Math.Round(scaled));
                }

                var rounded = Math.Round(scaled);
                if (!double.IsFinite(rounded) || rounded < int.MinValue || rounded > uint.MaxValue)
                {
                    return PlanResult.Refuse(Loc.T("input.plan.valueTooLarge", action.Id, scaled));
                }

                var data = rounded < 0 ? unchecked((uint)(int)rounded) : (uint)rounded;
                if (ActionSafety.EventProblem(step.Event, data) is { } eventProblem)
                {
                    return PlanResult.Refuse($"{action.Id}: {eventProblem}.");
                }

                steps.Add(new ResolvedStep(step.Event.Trim(), data, null, "", 0, skip, Math.Clamp(step.DelayMs, 0, 2000)));
            }
            else
            {
                // Absolut setzen: Steht der Wert schon, wird nichts geschrieben (Antwort "alreadySet")
                var unit = Unit(step.Unit);
                skip ??= new ResolvedCondition(step.Set!.Trim(), unit, scaled, 0.01);
                steps.Add(new ResolvedStep(null, 0, step.Set!.Trim(), unit, scaled, skip, Math.Clamp(step.DelayMs, 0, 2000)));
            }
        }

        if (steps.Count == 0)
        {
            return PlanResult.Refuse(Loc.T("input.plan.noStepForOption", action.Id, option));
        }

        ResolvedCheck? check = null;
        if (action.Check is { } chk && !string.IsNullOrWhiteSpace(chk.Variable))
        {
            if (Resolve(chk.Expect, value, option, fallback: value) is not { } expect)
            {
                return PlanResult.Refuse(Loc.T("input.plan.noCheckForOption", action.Id, option));
            }

            var variable = chk.Variable.Trim();
            var independent = !steps.Any(s => s.Set is not null && SameVariable(s.Set, variable))
                              && !action.Steps.Any(s => s.Set is not null && SameVariable(s.Set, variable));
            check = new ResolvedCheck(
                new ResolvedCondition(variable, Unit(chk.Unit), expect, chk.Tolerance, Modulo: chk.Modulo),
                Math.Clamp(chk.TimeoutMs, 500, 15000),
                Math.Clamp(chk.HoldMs, 0, 5000),
                independent || chk.ConfirmsEffect);
        }

        var requires = new List<ResolvedCondition>();
        foreach (var r in action.Requires.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Variable)))
        {
            if (r.Only is { Count: > 0 } onlyFor && (option is null || !onlyFor.Contains(option, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (Resolve(r.EqualTo, value, option, fallback: value) is not { } required)
            {
                return PlanResult.Refuse(Loc.T("input.plan.noConditionForOption", action.Id, option));
            }

            requires.Add(new ResolvedCondition(r.Variable.Trim(), Unit(r.Unit), required, r.Tolerance, ConditionMessage(r)));
        }

        var spoken = option is not null && action.Parameter?.Spoken is { } say
            ? say.FirstOrDefault(kv => kv.Key.Equals(option, StringComparison.OrdinalIgnoreCase)).Value ?? valueText
            : valueText;
        var readback = string.IsNullOrWhiteSpace(action.Readback)
            ? (spoken is null ? action.Label : $"{action.Label} {spoken}")
            : action.Readback.Replace("{value}", spoken ?? "", StringComparison.OrdinalIgnoreCase);

        return PlanResult.Ok(new ActionPlan(action, valueText, readback.Trim(), steps, check, requires)
        {
            Value = action.Parameter is null ? null : value,
            Option = option,
            Cautions = cautions,
        });
    }

    /// <summary>
    /// Welche Auswahl passt zu einem gelesenen Prüfwert? Damit "stateAfter 0" bei einem Schalter, dessen Stellung 0 "on" heißt,
    /// nicht als "aus" gelesen wird. Mehrere Treffer (z. B. Landing lights off/retract = 0) werden mit "/" verbunden.
    /// Null bei Aktionen ohne Auswahl oder Prüfung und wenn keine Auswahl passt.
    /// </summary>
    public static string? StateName(ProfileAction action, double state)
    {
        if (action.Parameter is not { IsChoice: true, Options: { Count: > 0 } options } || action.Check is not { } check || string.IsNullOrWhiteSpace(check.Variable))
        {
            return null;
        }

        var names = new List<string>();
        foreach (var (key, optionValue) in options)
        {
            if (Resolve(check.Expect, optionValue, key, fallback: optionValue) is { } expected
                && new ResolvedCondition(check.Variable.Trim(), Unit(check.Unit), expected, check.Tolerance, Modulo: check.Modulo).Matches(state))
            {
                names.Add(key);
            }
        }

        return names.Count > 0 ? string.Join("/", names) : null;
    }

    /// <summary>
    /// Bedingung "das Flugzeug steht schon auf dieser Auswahl" – aus der Prüfung der Aktion, sonst aus dem ersten gesetzten Wert.
    /// Für Sperren, die einen anderen Schalter lesen (z. B. Pack 2 beim Abschalten von Pack 1). Null, wenn nicht bestimmbar.
    /// </summary>
    public static ResolvedCondition? StateFor(ProfileAction action, string option)
    {
        if (action.Parameter is not { IsChoice: true, Options: { } options })
        {
            return null;
        }

        var key = options.Keys.FirstOrDefault(k => k.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        var value = options[key];
        if (action.Check is { } chk && !string.IsNullOrWhiteSpace(chk.Variable) && Resolve(chk.Expect, value, key, fallback: value) is { } expect)
        {
            return new ResolvedCondition(chk.Variable.Trim(), Unit(chk.Unit), expect, chk.Tolerance, Modulo: chk.Modulo);
        }

        var set = action.Steps.FirstOrDefault(s => s is not null && !string.IsNullOrWhiteSpace(s.Set)
                                                  && (s.Only is not { Count: > 0 } only || only.Contains(key, StringComparer.OrdinalIgnoreCase)));
        return set is not null && Resolve(set.Value, value, key, fallback: 0) is { } raw
            ? new ResolvedCondition(set.Set!.Trim(), Unit(set.Unit), raw * set.Scale, 0.01)
            : null;
    }

    /// <summary>Kurzbeschreibung des erwarteten Werts, z. B. "100–399 kt" oder "on | off".</summary>
    public static string Describe(ActionParameter p)
    {
        if (p.IsChoice)
        {
            return string.Join(" | ", p.Options?.Keys ?? Enumerable.Empty<string>());
        }

        var range = p.Min is { } min && p.Max is { } max ? $"{Num(min)}–{Num(max)}" : Loc.T("input.value.number");
        var unit = string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit;
        return p.Format?.ToLowerInvariant() switch
        {
            "squawk" => Loc.T("input.value.squawk"),
            "heading" => "0–360°",
            "altitude" => Loc.T("input.value.altitude", range, unit),
            _ => range + unit,
        };
    }

    /// <summary>Transpondercode 7000 → 0x7000 (jede Dezimalziffer ein Nibble).</summary>
    public static int Bcd16(int code)
    {
        var result = 0;
        var shift = 0;
        code = Math.Abs(code);
        while (code > 0 && shift < 16)
        {
            result |= (code % 10) << shift;
            code /= 10;
            shift += 4;
        }

        return result;
    }

    // ------------------------------------------------------------------ Werte lesen

    private sealed record Parsed(double Value, string? Text, string? Option, string? Error)
    {
        public static Parsed Fail(string error) => new(0, null, null, error);
    }

    private static Parsed ParseChoice(ActionParameter p, string input)
    {
        var options = p.Options ?? [];
        var key = options.Keys.FirstOrDefault(k => k.Equals(input.Trim(), StringComparison.OrdinalIgnoreCase));
        return key is null
            ? Parsed.Fail(Loc.T("input.plan.unknownOption", input.Trim(), string.Join(", ", options.Keys)))
            : new Parsed(options[key], key, key, null);
    }

    private static Parsed ParseNumber(ActionParameter p, string input)
    {
        var text = input.Trim();
        var format = p.Format?.ToLowerInvariant();

        if (format == "squawk")
        {
            if (text.Length != 4 || text.Any(c => c is < '0' or > '7'))
            {
                return Parsed.Fail(Loc.T("input.plan.squawkInvalid", text));
            }

            return new Parsed(int.Parse(text, CultureInfo.InvariantCulture), text, null, null);
        }

        var flightLevel = false;
        if (format == "altitude" && text.StartsWith("FL", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..].Trim();
            flightLevel = true;
        }

        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return Parsed.Fail(Loc.T("input.plan.notANumber", input.Trim()));
        }

        if (flightLevel)
        {
            value *= 100;
        }

        var unit = string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit;
        if ((p.Min is { } min && value < min - 1e-9) || (p.Max is { } max && value > max + 1e-9))
        {
            return Parsed.Fail(Loc.T("input.plan.outOfRange", Num(value), unit, Describe(p)));
        }

        if (p.Step is { } step && step > 0)
        {
            var steps = value / step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
            {
                return Parsed.Fail(Loc.T("input.plan.offStep", Num(value), unit, Num(step)));
            }

            value = Math.Round(steps) * step;
        }

        if (format == "heading" && Math.Abs(value - 360) < 1e-9)
        {
            // 360 sagen Piloten, das FCU zeigt 000
            return new Parsed(0, "360", null, null);
        }

        var shown = format switch
        {
            "frequency" => value.ToString("0.000", CultureInfo.InvariantCulture),
            "heading" => value.ToString("000", CultureInfo.InvariantCulture),
            "altitude" when flightLevel => "FL" + (value / 100).ToString("000", CultureInfo.InvariantCulture),
            _ => Num(value),
        };

        return new Parsed(value, shown, null, null);
    }

    /// <summary>Zahl, "{value}" oder je Auswahl { "on": 1 }. Null = kein Wert für diese Auswahl.</summary>
    private static double? Resolve(JsonElement? element, double value, string? option, double fallback)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return fallback;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.GetDouble();
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String when e.GetString()?.Trim().Equals("{value}", StringComparison.OrdinalIgnoreCase) == true:
                return value;
            case JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                return d;
            case JsonValueKind.Object when option is not null:
                foreach (var prop in e.EnumerateObject())
                {
                    if (prop.Name.Equals(option, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        return prop.Value.GetDouble();
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Text, wenn eine Bedingung nicht erfüllt ist: "messageKey" aus den Sprachdateien, sonst "message" aus dem Profil, sonst ein Standardtext.</summary>
    private static string ConditionMessage(StepCondition r)
    {
        var key = r.MessageKey?.Trim();
        if (!string.IsNullOrEmpty(key) && Loc.Has(key))
        {
            return Loc.T(key);
        }

        return r.Message ?? Loc.T("input.plan.conditionNotMet", r.Variable);
    }

    private static string Unit(string? unit) => string.IsNullOrWhiteSpace(unit) ? "number" : unit.Trim();

    private static bool SameVariable(string a, string b) =>
        string.Equals(Sim.SimVarSpec.NormalizeName(a), Sim.SimVarSpec.NormalizeName(b), StringComparison.OrdinalIgnoreCase);

    private static string Num(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.###", CultureInfo.InvariantCulture);
}
