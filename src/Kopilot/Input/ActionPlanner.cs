using System.Globalization;
using System.Text.Json;
using Kopilot.Aircraft;
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
        if (ActionSafety.FirstForbidden(action) is { } forbidden)
        {
            return PlanResult.Refuse($"{action.Id} nutzt {forbidden} – Triebwerk, Feuer, Treibstoff und MCDU sind gesperrt.");
        }

        if (action.Steps.Count == 0)
        {
            return PlanResult.Refuse($"{action.Id} hat im Profil keine Schritte.");
        }

        // ---------------- Wert
        double value = 0;
        string? valueText = null;
        string? option = null;
        if (action.Parameter is { } p)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return PlanResult.Refuse($"{action.Id} braucht einen Wert: {Describe(p)}.");
            }

            var parsed = p.IsChoice ? ParseChoice(p, input) : ParseNumber(p, input);
            if (parsed.Error is not null)
            {
                return PlanResult.Refuse($"{action.Id}: {parsed.Error}");
            }

            (value, valueText, option) = (parsed.Value, parsed.Text, parsed.Option);
        }

        // ---------------- Sperren
        foreach (var name in action.Interlocks)
        {
            if (Interlocks.Check(name, snapshot, limits, option ?? valueText) is { } reason)
            {
                return PlanResult.Refuse(reason);
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
                return PlanResult.Refuse($"{action.Id}: Jeder Schritt braucht genau eines von \"event\" oder \"set\".");
            }

            if (Resolve(step.Value, value, option, fallback: 0) is not { } raw)
            {
                return PlanResult.Refuse($"{action.Id}: Kein Wert für \"{option}\" im Schritt {step.Event ?? step.Set}.");
            }

            var scaled = raw * step.Scale;
            ResolvedCondition? skip = null;
            if (step.SkipIf is { } c && !string.IsNullOrWhiteSpace(c.Variable))
            {
                if (Resolve(c.EqualTo, value, option, fallback: value) is not { } expected)
                {
                    return PlanResult.Refuse($"{action.Id}: Kein Vergleichswert für \"{option}\".");
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
                    return PlanResult.Refuse($"{action.Id}: Wert {scaled} passt nicht in ein Event.");
                }

                var data = rounded < 0 ? unchecked((uint)(int)rounded) : (uint)rounded;
                steps.Add(new ResolvedStep(step.Event.Trim(), data, null, "", 0, skip, Math.Clamp(step.DelayMs, 0, 2000)));
            }
            else
            {
                steps.Add(new ResolvedStep(null, 0, step.Set!.Trim(), Unit(step.Unit), scaled, skip, Math.Clamp(step.DelayMs, 0, 2000)));
            }
        }

        if (steps.Count == 0)
        {
            return PlanResult.Refuse($"{action.Id}: Für \"{option}\" ist im Profil kein Schritt hinterlegt.");
        }

        ResolvedCondition? check = null;
        if (action.Check is { } chk && !string.IsNullOrWhiteSpace(chk.Variable)
            && Resolve(chk.Expect, value, option, fallback: value) is { } expect)
        {
            check = new ResolvedCondition(chk.Variable.Trim(), Unit(chk.Unit), expect, chk.Tolerance);
        }

        var readback = string.IsNullOrWhiteSpace(action.Readback)
            ? (valueText is null ? action.Label : $"{action.Label} {valueText}")
            : action.Readback.Replace("{value}", valueText ?? "", StringComparison.OrdinalIgnoreCase);

        return PlanResult.Ok(new ActionPlan(action, valueText, readback.Trim(), steps, check, Math.Clamp(action.Check?.TimeoutMs ?? 0, 0, 10000)));
    }

    /// <summary>Kurzbeschreibung des erwarteten Werts, z. B. "100–399 kt" oder "on | off".</summary>
    public static string Describe(ActionParameter p)
    {
        if (p.IsChoice)
        {
            return string.Join(" | ", p.Options?.Keys ?? Enumerable.Empty<string>());
        }

        var range = p.Min is { } min && p.Max is { } max ? $"{Num(min)}–{Num(max)}" : "Zahl";
        var unit = string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit;
        return p.Format?.ToLowerInvariant() switch
        {
            "squawk" => "4 Ziffern 0–7",
            "altitude" => $"{range}{unit} oder FLxxx",
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
            ? Parsed.Fail($"\"{input.Trim()}\" gibt es nicht. Möglich: {string.Join(", ", options.Keys)}.")
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
                return Parsed.Fail($"Squawk \"{text}\" ungültig – 4 Ziffern von 0 bis 7.");
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
            return Parsed.Fail($"\"{input.Trim()}\" ist keine Zahl.");
        }

        if (flightLevel)
        {
            value *= 100;
        }

        var unit = string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit;
        if ((p.Min is { } min && value < min - 1e-9) || (p.Max is { } max && value > max + 1e-9))
        {
            return Parsed.Fail($"{Num(value)}{unit} liegt außerhalb {Describe(p)}.");
        }

        if (p.Step is { } step && step > 0)
        {
            var steps = value / step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
            {
                return Parsed.Fail($"{Num(value)}{unit} passt nicht ins Raster von {Num(step)}{unit}.");
            }

            value = Math.Round(steps) * step;
        }

        var shown = format switch
        {
            "frequency" => value.ToString("0.000", CultureInfo.InvariantCulture),
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

    private static string Unit(string? unit) => string.IsNullOrWhiteSpace(unit) ? "number" : unit.Trim();

    private static string Num(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.###", CultureInfo.InvariantCulture);
}
