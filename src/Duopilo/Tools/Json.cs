using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Duopilo.Tools;

internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>Kleine Helfer zum Runden und Formatieren der Rohwerte.</summary>
internal static class Fmt
{
    public static int? Int(double? v) => v is { } d && double.IsFinite(d) ? (int)Math.Round(d) : null;

    public static double? Round(double? v, int digits) => v is { } d && double.IsFinite(d) ? Math.Round(d, digits) : null;

    /// <summary>Kurs 0–359 (360 → 0).</summary>
    public static int? Heading(double? v) => Int(v) is { } h ? ((h % 360) + 360) % 360 : null;

    public static double? Negate(double? v) => v is { } d ? -d : null;

    /// <summary>Sekunden seit Mitternacht → "HH:mm".</summary>
    public static string? ClockTime(double? secondsOfDay)
    {
        if (secondsOfDay is not { } s || !double.IsFinite(s))
        {
            return null;
        }

        var t = TimeSpan.FromSeconds(((s % 86400) + 86400) % 86400);
        return t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Dauer in Sekunden → "h:mm".</summary>
    public static string? Duration(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s) || s < 0)
        {
            return null;
        }

        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalHours}:{t.Minutes:00}";
    }

    /// <summary>
    /// Transponder-Code. MSFS liefert ihn als BCO16 (0x2000 für "2000"). Falls die Variable doch dezimal kommt,
    /// enthält die Hex-Darstellung Ziffern über 7 – dann wird dezimal formatiert.
    /// </summary>
    public static string? Squawk(double? raw)
    {
        if (Int(raw) is not { } code || code < 0)
        {
            return null;
        }

        var hex = code.ToString("X4", CultureInfo.InvariantCulture);
        return hex.All(c => c is >= '0' and <= '7') ? hex : code.ToString("D4", CultureInfo.InvariantCulture);
    }

    public static string? TransponderMode(double? raw) => Int(raw) switch
    {
        0 => "OFF",
        1 => "STBY",
        2 => "TEST",
        3 => "ON",
        4 => "ALT",
        null => null,
        var other => other.ToString(),
    };
}
