using System.Globalization;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>
/// Ist eine Bahn frei? Für das Rollen: vor dem Kreuzen einer Bahn und vor dem Aufreihen. Belegt ist sie durch ein Flugzeug auf der Bahn
/// (bis <see cref="OnRunwayMaxHeightFt"/> darüber, mit Rand an den Enden) oder im Endanflug auf eines ihrer Enden (in der Luft, beim
/// Kreuzen bis <see cref="FinalNm"/> davor, nahe der verlängerten Mittellinie, tief genug und in Richtung der Bahn unterwegs). Wer am Boden vor der
/// Schwelle steht oder rollt (die Warteschlange am Haltepunkt), ist nicht im Endanflug.
/// </summary>
public static class RunwayOccupancy
{
    /// <summary>Endanflug: so weit vor der Schwelle zählt ein Flugzeug (rund 90 s bei 160 kt – das Kreuzen dauert weniger).</summary>
    public const double FinalNm = 4;

    /// <summary>Endanflug: höchstens so weit neben der verlängerten Mittellinie.</summary>
    public const double FinalCrossM = 600;

    /// <summary>Endanflug: höchstens so hoch über der Bahn (der 3°-Pfad liegt bei 4 NM um 1300 ft).</summary>
    public const double FinalMaxHeightFt = 2000;

    /// <summary>Auf der Bahn: höchstens so hoch darüber (Aufsetzen, Abheben, Durchstarten).</summary>
    public const double OnRunwayMaxHeightFt = 500;

    /// <summary>Auf der Bahn: so viel Rand vor und hinter den Enden.</summary>
    public const double EndMarginM = 100;

    /// <summary>Endanflug: Kurs höchstens so weit (Grad) vom Landekurs.</summary>
    public const double FinalHeadingDeg = 45;

    /// <summary>Grund ohne Entfernung („DUO02 on final 23L“) – zum Vergleich, ob sich etwas geändert hat.</summary>
    public static string Key(string blocker)
    {
        var at = blocker.LastIndexOf(" NM", StringComparison.Ordinal);
        if (at < 0)
        {
            return blocker;
        }

        var space = blocker.LastIndexOf(' ', at - 1);
        return space > 0 ? blocker[..space] : blocker;
    }

    /// <summary>Endanflug: weiter draußen zählt kein Flugzeug, auch nicht vor dem Aufreihen.</summary>
    public const double MaxFinalNm = 12;

    /// <summary>Endanflug: so viel höher als der 3°-Pfad darf ein Flugzeug in größerer Entfernung noch sein.</summary>
    public const double AboveGlidePathFt = 1000;

    /// <summary>
    /// Was die Bahn <paramref name="runway"/> belegt („DUO02 on final 23L 2.4 NM“), null = frei. <paramref name="ownObjectId"/> (das
    /// wartende Flugzeug selbst) zählt nicht. Im Endanflug zählt, wer bis <paramref name="finalNm"/> vor einem der Enden ist (Kreuzen:
    /// <see cref="FinalNm"/>; Aufreihen zum Start: so weit, wie die nächste Landung beim Losrollen weg sein muss).
    /// </summary>
    public static string? Blocker(string airport, AirportRunway runway, IEnumerable<AircraftSample> samples, uint ownObjectId, double finalNm = FinalNm) =>
        Find(airport, runway, samples, ownObjectId, Math.Min(finalNm, MaxFinalNm));

    /// <summary>Für die Landung: wer auf der Bahn ist („DUO02 on the runway“) – den Endanflug dahinter (die nächsten Landungen) nicht.</summary>
    public static string? OnRunway(string airport, AirportRunway runway, IEnumerable<AircraftSample> samples, uint ownObjectId) =>
        Find(airport, runway, samples, ownObjectId, 0);

    private static string? Find(string airport, AirportRunway runway, IEnumerable<AircraftSample> samples, uint ownObjectId, double finalNm)
    {
        var first = RunwayEnd.From(airport, runway, primary: true);
        var ends = new List<RunwayEnd> { first };
        if (runway.Secondary.Length > 0)
        {
            ends.Add(RunwayEnd.From(airport, runway, primary: false));
        }

        var halfWidth = Math.Max(40, (runway.WidthM / 2) + 20);
        foreach (var s in samples)
        {
            if (s.ObjectId == ownObjectId)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(s.AtcId) ? "object " + s.ObjectId.ToString(CultureInfo.InvariantCulture) : s.AtcId.Trim();
            var height = s.AltitudeFt - first.ElevationFt;
            var (along, cross) = first.Offsets(s.Latitude, s.Longitude);
            if (Math.Abs(cross) <= halfWidth && along >= -EndMarginM && along <= first.LengthM + EndMarginM && height < OnRunwayMaxHeightFt)
            {
                return name + " on the runway";
            }

            if (s.OnGround || finalNm <= 0)
            {
                continue;
            }

            foreach (var end in ends)
            {
                var (a, c) = end.Offsets(s.Latitude, s.Longitude);
                var toward = Math.Abs(TrafficMath.Normalize(s.HeadingTrueDeg - end.HeadingTrueDeg + 180) - 180) <= FinalHeadingDeg;
                var maxHeight = Math.Max(FinalMaxHeightFt, KinematicGlidePathFt(-a) + AboveGlidePathFt);
                if (a < 0 && a >= -finalNm * RunwayEnd.MetersPerNm && Math.Abs(c) <= FinalCrossM && height < maxHeight && toward)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{name} on final {end.Designation} {-a / RunwayEnd.MetersPerNm:0.0} NM");
                }
            }
        }

        return null;
    }

    /// <summary>Höhe des 3°-Pfads <paramref name="distanceM"/> vor der Schwelle über der Bahn.</summary>
    private static double KinematicGlidePathFt(double distanceM) => TrafficKinematics.GlidePathHeightFt(-Math.Max(0, distanceM));
}
