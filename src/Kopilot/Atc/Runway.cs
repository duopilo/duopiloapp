using System.Globalization;

namespace Kopilot.Atc;

/// <summary>Vom Ingame-ATC zugewiesene Bahn.</summary>
/// <param name="Number">Bahnnummer 1–36.</param>
/// <param name="Designator">L, C, R oder null, falls unbekannt.</param>
/// <param name="DesignatorSource">"simbrief" wenn aus dem Flugplan übernommen, sonst null.</param>
public sealed record AssignedRunway(int Number, string? Designator, string? DesignatorSource, string? Airport, double HeadingMagDeg)
{
    public string Ident => Number.ToString("00", CultureInfo.InvariantCulture) + Designator;

    /// <summary>Sprechfunk-Form, z. B. "two three left".</summary>
    public string Spoken => Phraseology.Runway(Number, Designator);
}

public static class RunwayMath
{
    /// <summary>
    /// Bahnnummer aus dem rechtweisenden Bahnkurs. Die Missweisung wird aus dem Flugzeugkurs abgeleitet
    /// (true − magnetic), damit das Vorzeichen der MAGVAR-Variable keine Rolle spielt.
    /// </summary>
    public static AssignedRunway? FromAtc(
        double? runwayHeadingTrue,
        double? aircraftHeadingTrue,
        double? aircraftHeadingMag,
        string? airport,
        IEnumerable<string?> plannedRunways)
    {
        if (runwayHeadingTrue is not { } rwyTrue || !double.IsFinite(rwyTrue) || Math.Abs(rwyTrue) < 0.01)
        {
            return null;
        }

        var variation = 0.0;
        if (aircraftHeadingTrue is { } t && aircraftHeadingMag is { } m)
        {
            variation = Normalize180(t - m);
        }

        var mag = Normalize360(rwyTrue - variation);
        var number = (int)Math.Round(mag / 10.0, MidpointRounding.AwayFromZero);
        if (number <= 0)
        {
            number = 36;
        }
        else if (number > 36)
        {
            number -= 36;
        }

        string? designator = null;
        string? source = null;
        foreach (var planned in plannedRunways)
        {
            if (Parse(planned) is { } p && p.Number == number && p.Designator is not null)
            {
                designator = p.Designator;
                source = "simbrief";
                break;
            }
        }

        return new AssignedRunway(number, designator, source, string.IsNullOrWhiteSpace(airport) ? null : airport.Trim(), Math.Round(mag, 1));
    }

    /// <summary>"23L" → (23, "L"), "05" → (5, null).</summary>
    public static (int Number, string? Designator)? Parse(string? ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            return null;
        }

        var s = ident.Trim().ToUpperInvariant();
        if (s.StartsWith("RW", StringComparison.Ordinal))
        {
            s = s[2..].TrimStart('Y');
        }

        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length is 0 or > 2 || !int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 36)
        {
            return null;
        }

        var rest = s[digits.Length..].Trim();
        var designator = rest.Length > 0 && rest[0] is 'L' or 'C' or 'R' ? rest[0].ToString() : null;
        return (n, designator);
    }

    private static double Normalize360(double deg) => ((deg % 360) + 360) % 360;

    private static double Normalize180(double deg)
    {
        var d = Normalize360(deg);
        return d >= 180 ? d - 360 : d;
    }
}

/// <summary>Englische Sprechfunk-Phraseologie für Callouts.</summary>
public static class Phraseology
{
    private static readonly string[] Digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    /// <summary>Ziffernweise: 23 → "two three", 5 → "zero five" (bei <paramref name="minDigits"/> = 2).</summary>
    public static string DigitByDigit(int value, int minDigits = 1) =>
        string.Join(' ', Math.Abs(value).ToString(new string('0', Math.Max(1, minDigits)), CultureInfo.InvariantCulture).Select(c => Digits[c - '0']));

    public static string Runway(int number, string? designator)
    {
        var spoken = DigitByDigit(number, 2);
        return designator switch
        {
            "L" => spoken + " left",
            "R" => spoken + " right",
            "C" => spoken + " center",
            _ => spoken,
        };
    }
}
