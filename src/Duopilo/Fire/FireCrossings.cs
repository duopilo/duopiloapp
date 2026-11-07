using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>
/// Eine Bahn auf dem Weg eines Feuerwehrfahrzeugs: Es hält bei <paramref name="StopS"/> (etwa an der Haltelinie) und fährt erst mit
/// Freigabe weiter; ab <paramref name="EntryS"/> ist es auf der Bahn.
/// </summary>
public sealed record FireCrossing(AirportRunway Runway, double StopS, double EntryS)
{
    /// <summary>Bezeichnung für Log und Status, z. B. "06/24".</summary>
    public string Designation => Runway.Secondary.Length > 0 ? Runway.Primary + "/" + Runway.Secondary : Runway.Primary;
}

/// <summary>
/// Wo ein Weg auf eine Bahn führt. Test EDDK 05.11.: Die Fahrzeuge kreuzten Bahnen ohne Halt; der eigene KI-Verkehr hält davor und
/// kreuzt erst, wenn nichts auf der Bahn oder im Endanflug ist.
/// </summary>
public static class FireCrossings
{
    /// <summary>Schrittweite beim Absuchen des Wegs.</summary>
    public const double StepM = 2;

    /// <summary>So weit vor dem Bahnrand (halbe Breite) hält ein Fahrzeug, etwa an der Haltelinie.</summary>
    public const double HoldClearM = 40;

    /// <summary>
    /// Die Bahnen, auf die <paramref name="path"/> von außerhalb führt, in der Reihenfolge des Wegs. Wer schon auf einer Bahn steht
    /// (am Flugzeug auf der Landebahn), hält vor der nächsten nicht, solange er von Bahn zu Bahn fährt.
    /// </summary>
    public static IReadOnlyList<FireCrossing> Find(TaxiGraph graph, TaxiPath path)
    {
        var crossings = new List<FireCrossing>();
        if (graph.Runways.Count == 0 || path.LengthM < StepM)
        {
            return crossings;
        }

        var (startEast, startNorth, _) = path.At(0);
        var previous = graph.RunwayAt(startEast, startNorth);
        var offSince = 0.0;
        for (var s = StepM; s < path.LengthM + StepM; s += StepM)
        {
            var at = Math.Min(s, path.LengthM);
            var (east, north, _) = path.At(at);
            var on = graph.RunwayAt(east, north);
            if (on is not null && previous is null)
            {
                crossings.Add(new FireCrossing(on, StopBefore(graph, path, on, offSince, at), at));
            }

            if (on is null && previous is not null)
            {
                offSince = at;
            }

            previous = on;
            if (at >= path.LengthM)
            {
                break;
            }
        }

        return crossings;
    }

    /// <summary>Letzte Stelle vor <paramref name="entryS"/>, die mindestens halbe Breite plus <see cref="HoldClearM"/> neben der Bahn liegt.</summary>
    private static double StopBefore(TaxiGraph graph, TaxiPath path, AirportRunway runway, double fromS, double entryS)
    {
        var clear = (Math.Max(runway.WidthM, 20) / 2) + HoldClearM;
        for (var s = entryS - StepM; s > fromS; s -= StepM)
        {
            var (east, north, _) = path.At(s);
            if (Math.Abs(graph.OffsetsTo(runway, east, north).CrossM) >= clear)
            {
                return s;
            }
        }

        return fromS;
    }
}
