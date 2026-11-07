using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>
/// Abstellposition nach einem Notfall: Ein Flugzeug mit Feuerwehreinsatz parkt aus Sicherheitsgründen frei auf dem Vorfeld, nicht am
/// Gebäude (Florian, 05.11.). Gesucht wird eine Position ohne Fluggastbrücke, lieber Vorfeld als Gate, mit Abstand zu den Gebäuden aus
/// OSM und groß genug für das Flugzeug; unter gleich guten die nächste zur Landebahn. Nur für Feuerwehreinsätze – sonst wählt der
/// Verkehrstest wie bisher (<see cref="TaxiRoute.CandidateParkings"/>).
/// </summary>
public static class FireStands
{
    /// <summary>So weit soll die Position vom nächsten Gebäude weg sein (Mitte der Position, Umrisse aus OSM).</summary>
    public const double BuildingClearM = 50;

    /// <summary>So viele Positionen gehen an den Verkehrstest (er nimmt die erste, die vom Abrollpunkt aus erreichbar und frei ist).</summary>
    public const int MaxStands = 12;

    /// <summary>Mindestradius der Position je Wirbelschleppenklasse (wie <see cref="TaxiRoute.CandidateParkings"/>).</summary>
    public static double MinRadiusM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 5,
        TrafficWake.Medium => 17,
        TrafficWake.Heavy => 26,
        _ => 38,
    };

    /// <summary>Gate (klein, mittel, groß, extra) statt Vorfeld.</summary>
    public static bool IsGate(int type) => type is 8 or 9 or 10 or 15;

    /// <summary>
    /// Freie Positionen, die beste zuerst: ohne Fluggastbrücke (<paramref name="jetways"/>: <see cref="TaxiParking.Index"/> der
    /// Positionen mit Brücke; null = unbekannt), Vorfeld vor Gate, dann mit <see cref="BuildingClearM"/> Abstand zu Gebäuden vor ohne,
    /// dann die nächste zu <paramref name="near"/> (Mitte der Landebahn). Keine Docks, Tankstellen und Fahrzeug-Plätze.
    /// </summary>
    public static IReadOnlyList<TaxiNode> Remote(TaxiGraph graph, TrafficWake wake, IReadOnlySet<int>? jetways, (double East, double North) near)
    {
        var minRadius = MinRadiusM(wake);
        return [.. graph.Nodes
            .Where(n => n.Parking is { } p && p.RadiusM >= minRadius && p.Type is not (0 or 11 or 12 or 13)
                && !(jetways?.Contains(p.Index) ?? false) && !graph.IsOccupied(n))
            .Select(n => (Node: n, Tier: (IsGate(n.Parking!.Type) ? 2 : 0)
                + (graph.DistanceToBuildingM(n.EastM, n.NorthM, BuildingClearM) < BuildingClearM ? 1 : 0)))
            .OrderBy(c => c.Tier)
            .ThenBy(c => FirePlanner.Distance(c.Node.EastM, c.Node.NorthM, near.East, near.North))
            .Take(MaxStands)
            .Select(c => c.Node)];
    }
}
