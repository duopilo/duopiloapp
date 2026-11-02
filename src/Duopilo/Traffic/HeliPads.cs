using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Ziel eines Heli-Anflugs (H1): ein Helipad aus der MSFS-Datenbank oder ein beliebiger Punkt.</summary>
/// <param name="Airport">Flughafen oder Heliport, bei Koordinaten "POS".</param>
/// <param name="Label">Bezeichnung: H1, H2 … (aus den Startpunkten der Helipads), bei Koordinaten HP1–HP4 nach der Kennung.</param>
/// <param name="ElevationFt">Höhe der Pad-Mitte (MSL); bei <paramref name="Measured"/> nur geschätzt.</param>
/// <param name="HeadingDeg">Ausrichtung des Pads (rechtweisend), auf die der Heli im Schweben dreht; null = egal.</param>
/// <param name="Type">none, h, square, circle, medical; bei Koordinaten "point".</param>
/// <param name="Measured">Der Boden kommt aus der Messung unter dem Heli (Koordinaten), nicht aus der Datenbank.</param>
public sealed record HeliPad(
    string Airport,
    string Label,
    double Latitude,
    double Longitude,
    double ElevationFt,
    double? HeadingDeg,
    double LengthM,
    double WidthM,
    string Type,
    bool Measured = false);

/// <summary>Helipads aus der MSFS-Datenbank lesbar machen: Bezeichnungen und Suche nach "H02", "h2" oder "2".</summary>
public static class HeliPads
{
    /// <summary>So nah muss ein Startpunkt an der Mitte eines Helipads liegen, damit seine Nummer dessen Bezeichnung wird.</summary>
    public const double StartMatchM = 40;

    /// <summary>
    /// Helipads mit Bezeichnung: Liegt ein Startpunkt auf einem Pad (TYPE 3, höchstens <see cref="StartMatchM"/> entfernt), heißt
    /// es nach dessen Nummer (H1, H2 …), die übrigen bekommen der Reihe nach die kleinsten freien Nummern. Sortiert nach Nummer.
    /// </summary>
    public static IReadOnlyList<HeliPad> From(AirportHelipads data)
    {
        var numbers = new int?[data.Helipads.Count];
        var used = new HashSet<int>();
        foreach (var start in data.Starts.Where(s => s.Number > 0))
        {
            var best = -1;
            var bestM = StartMatchM;
            for (var i = 0; i < data.Helipads.Count; i++)
            {
                var pad = data.Helipads[i];
                var m = RouteMath.DistanceNm(start.Latitude, start.Longitude, pad.Latitude, pad.Longitude) * RunwayEnd.MetersPerNm;
                if (numbers[i] is null && m <= bestM)
                {
                    best = i;
                    bestM = m;
                }
            }

            if (best >= 0 && used.Add(start.Number))
            {
                numbers[best] = start.Number;
            }
        }

        var next = 1;
        for (var i = 0; i < numbers.Length; i++)
        {
            if (numbers[i] is not null)
            {
                continue;
            }

            while (used.Contains(next))
            {
                next++;
            }

            numbers[i] = next;
            used.Add(next);
        }

        return [.. data.Helipads
            .Select((pad, i) => new HeliPad(
                data.Icao,
                "H" + numbers[i]!.Value.ToString(CultureInfo.InvariantCulture),
                pad.Latitude,
                pad.Longitude,
                // Ohne eigene Höhe (0) die des Flughafens
                (pad.AltitudeM != 0 ? pad.AltitudeM : data.AltitudeM) * RunwayEnd.FeetPerMeter,
                double.IsFinite(pad.HeadingDeg) ? TrafficMath.Normalize(pad.HeadingDeg) : null,
                pad.LengthM,
                pad.WidthM,
                pad.TypeName))
            .OrderBy(p => Number(p.Label) ?? int.MaxValue)];
    }

    /// <summary>"H02", "h2", "2" → "H2", "hp02" → "HP2"; anderes nur in Großbuchstaben, leer bleibt leer.</summary>
    public static string NormalizeLabel(string? label)
    {
        var text = (label ?? "").Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        var prefix = text.StartsWith("HP", StringComparison.Ordinal) ? "HP" : text.StartsWith('H') ? "H" : "";
        var digits = text[prefix.Length..];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return text;
        }

        var number = digits.TrimStart('0');
        return (prefix == "HP" ? "HP" : "H") + (number.Length == 0 ? "0" : number);
    }

    /// <summary>Das Pad mit dieser Bezeichnung, ohne Bezeichnung das erste; sonst null.</summary>
    public static HeliPad? Find(IReadOnlyList<HeliPad> pads, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return pads.Count > 0 ? pads[0] : null;
        }

        var wanted = NormalizeLabel(label);
        return pads.FirstOrDefault(p => NormalizeLabel(p.Label) == wanted);
    }

    private static int? Number(string label) =>
        int.TryParse(NormalizeLabel(label).TrimStart('H', 'P'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
