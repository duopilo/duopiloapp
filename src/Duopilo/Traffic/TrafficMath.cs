using System.Globalization;
using System.Text.RegularExpressions;

namespace Duopilo.Traffic;

/// <summary>Rechnungen und Texte rund um Verkehr: Peilung, Uhrzeit-Angabe, Vertikaltrend und Callout.</summary>
public static partial class TrafficMath
{
    /// <summary>Ab dieser Vertikalgeschwindigkeit gilt ein Ziel als steigend oder sinkend (wie der Pfeil beim TCAS).</summary>
    public const double TrendThresholdFpm = 500;

    /// <summary>Winkel auf 0 bis unter 360° bringen.</summary>
    public static double Normalize(double deg)
    {
        var d = deg % 360;
        if (d < 0)
        {
            d += 360;
        }

        // -1e-15 % 360 + 360 ergibt genau 360
        return d >= 360 ? 0 : d;
    }

    /// <summary>Relative Peilung: Winkel vom eigenen Steuerkurs (rechtweisend) zum Ziel, im Uhrzeigersinn, 0 bis unter 360°.</summary>
    public static double RelativeBearingDeg(double bearingTrueDeg, double ownHeadingTrueDeg) =>
        Normalize(bearingTrueDeg - ownHeadingTrueDeg);

    /// <summary>
    /// Uhrzeit-Angabe 1–12 für eine relative Peilung. Jede Stunde deckt 30° ab, mittig um ihre Stunde:
    /// 12 Uhr = 345° bis unter 15°, 1 Uhr = 15° bis unter 45° usw.
    /// </summary>
    public static int ClockPosition(double relativeBearingDeg)
    {
        var hour = (int)Math.Floor((Normalize(relativeBearingDeg) + 15) / 30) % 12;
        return hour == 0 ? 12 : hour;
    }

    public static string ClockText(int clock) => clock.ToString(CultureInfo.InvariantCulture) + " o'clock";

    /// <summary>"climbing", "descending" oder "level". Am Boden immer "level".</summary>
    public static string VerticalTrend(double? verticalSpeedFpm, bool onGround = false)
    {
        if (onGround || verticalSpeedFpm is not { } vs || !double.IsFinite(vs))
        {
            return "level";
        }

        return vs >= TrendThresholdFpm ? "climbing" : vs <= -TrendThresholdFpm ? "descending" : "level";
    }

    /// <summary>Entfernung für den Callout: "less than 1 mile", "1 mile", "5 miles".</summary>
    public static string DistanceText(double distanceNm)
    {
        if (distanceNm < 0.95)
        {
            return "less than 1 mile";
        }

        var miles = (int)Math.Round(distanceNm, MidpointRounding.AwayFromZero);
        return miles == 1 ? "1 mile" : miles.ToString(CultureInfo.InvariantCulture) + " miles";
    }

    /// <summary>
    /// Höhe des Ziels minus eigene Höhe, positiv = darüber. Das Ziel geht mit dem Wert ein, den SimConnect liefert (PLANE ALTITUDE);
    /// bei MSFS-Live-Traffic ist das die barometrische Höhe aus ADS-B. Verglichen wird deshalb – wie beim TCAS – mit der eigenen
    /// Druckhöhe, nicht mit der wahren Höhe. Testflug über Labrador: eigene wahre Höhe 30.630 ft auf FL320 (−47 °C), ein Ziel
    /// auf FL330 erschien so 2.400 ft statt 1.000 ft darüber. Ohne Druckhöhe bleibt es bei der wahren Höhe.
    /// </summary>
    public static double AltitudeDifference(double targetAltitudeFt, OwnAircraft own) => targetAltitudeFt - own.ReferenceAltitudeFt;

    /// <summary>Höhendifferenz für den Callout, auf 100 ft gerundet: "1000 feet above", "500 feet below", "same altitude".</summary>
    public static string AltitudeText(double altitudeDiffFt)
    {
        var rounded = (int)(Math.Round(altitudeDiffFt / 100, MidpointRounding.AwayFromZero) * 100);
        if (rounded == 0)
        {
            return "same altitude";
        }

        return Math.Abs(rounded).ToString(CultureInfo.InvariantCulture) + (rounded > 0 ? " feet above" : " feet below");
    }

    /// <summary>
    /// Kurze Verkehrsmeldung, z. B. "Traffic, 2 o'clock, 5 miles, 1000 feet above, descending".
    /// Ziele am Boden: "Traffic, 11 o'clock, 2 miles, on ground". Horizontaler Flug bekommt keinen Zusatz.
    /// </summary>
    public static string Callout(int clock, double distanceNm, double altitudeDiffFt, string verticalTrend, bool targetOnGround)
    {
        var parts = new List<string> { "Traffic", ClockText(clock), DistanceText(distanceNm) };
        if (targetOnGround)
        {
            parts.Add("on ground");
        }
        else
        {
            parts.Add(AltitudeText(altitudeDiffFt));
            if (verticalTrend is "climbing" or "descending")
            {
                parts.Add(verticalTrend);
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Rufzeichen eines Ziels: Airline + Flugnummer nur, wenn beide gesetzt sind ("Lufthansa 123"), sonst die ATC ID
    /// (Registrierung). Live-Traffic-GA liefert als ATC FLIGHT NUMBER oft eine interne Nummer ohne Airline ("1432126") –
    /// die ist kein Rufzeichen (Testlog 0.9, 01:22Z: C172 N605SD).
    /// </summary>
    public static string? Callsign(string? atcAirline, string? atcFlightNumber, string? atcId)
    {
        var airline = CleanAtcText(atcAirline);
        var number = string.IsNullOrWhiteSpace(atcFlightNumber) ? null : atcFlightNumber.Trim();
        if (airline is not null && number is not null)
        {
            return $"{airline} {number}";
        }

        return string.IsNullOrWhiteSpace(atcId) ? null : atcId.Trim();
    }

    /// <summary>
    /// Text-SimVars wie ATC MODEL kommen bei manchen Flugzeugen als Lokalisierungsschlüssel
    /// ("TT:ATCCOM.AC_MODEL_A320.0.text", "ATCCOM.AC_MODEL B738.0.text", bei Live-Traffic "ATCCOM.AC MODEL C441.0.tts"
    /// oder "ATCCOM.ATC NAME CITATION.0.tts"). Liefert den lesbaren Teil oder null.
    /// </summary>
    public static string? CleanAtcText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var m = AtcKeyRegex().Match(text);
        if (m.Success)
        {
            text = m.Groups["value"].Value;
        }
        else if (text.StartsWith("TT:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("$$:", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        text = text.Replace('_', ' ').Trim();
        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex(@"^(?:TT:)?ATCCOM\.(?:(?:AC|ATC)[ _](?:MODEL|NAME|TYPE)|AIRLINE)?[ _]?(?<value>.+?)(?:\.\d+)?\.(?:text|tts)$", RegexOptions.IgnoreCase)]
    private static partial Regex AtcKeyRegex();
}
