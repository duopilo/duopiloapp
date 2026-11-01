namespace Duopilo.Traffic;

/// <summary>
/// Route vom Parkplatz zum Haltepunkt einer Startbahn mit den Wegen für Pushback und Rollen (<see cref="TaxiKinematics"/>).
/// </summary>
/// <param name="Nodes">Knoten der Route im Netz, von der Parkposition bis zum Haltepunkt.</param>
/// <param name="Taxiways">Rollwege entlang der Route ("A, A7, B").</param>
/// <param name="PushbackPath">Weg des Pushbacks (vorwärts gerechnet), null = vorwärts aus der Parkposition.</param>
public sealed record TaxiRoute(
    TaxiNode Parking,
    TaxiHold Hold,
    IReadOnlyList<int> Nodes,
    IReadOnlyList<string> Taxiways,
    double LengthM,
    TaxiPath? PushbackPath,
    TaxiPath TaxiPath)
{
    /// <summary>Zeigt die Nase so weit vom Rollweg weg (Grad), schiebt ein Schlepper das Flugzeug zurück.</summary>
    public const double PushbackAboveDeg = 90;

    /// <summary>Der Rollweg hinter dem Pushback-Punkt zählt, wenn er höchstens so weit (Grad) von der Gegenrichtung abweicht.</summary>
    private const double PushbackLineDeg = 45;

    private const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Langsamer als das rollt das Flugzeug auch in engen Kurven nicht (m/s, gut 4 kt).</summary>
    private const double MinTurnMps = 2.2;

    public bool Pushback => PushbackPath is not null;

    /// <summary>
    /// Route von <paramref name="parking"/> zum ersten erreichbaren Haltepunkt vor dem Start von <paramref name="end"/>
    /// (<see cref="TaxiGraph.DepartureHolds"/>), sonst null.
    /// </summary>
    public static TaxiRoute? Plan(TaxiGraph graph, RunwayEnd end, TaxiNode parking, TaxiProfile profile)
    {
        foreach (var hold in graph.DepartureHolds(end))
        {
            if (graph.Route(parking.Id, hold.Node.Id) is { Count: >= 2 } nodes)
            {
                return Build(graph, parking, hold, nodes, profile);
            }
        }

        return null;
    }

    /// <summary>
    /// Parkpositionen, die zu Kategorie und Wirbelschleppen-Kategorie passen, in zufälliger Reihenfolge: Verkehrsflugzeuge an Gates und
    /// Frachtpositionen, Allgemeine Luftfahrt auf GA-Vorfeldern, Militär auf Militärpositionen – jeweils groß genug (Radius) –, danach
    /// jede andere Parkposition, die groß genug ist (ohne Tankstellen, Fahrzeuge und Docks). Belegte Positionen
    /// (<see cref="TaxiGraph.IsOccupied(TaxiNode)"/>) fallen weg.
    /// </summary>
    public static IReadOnlyList<TaxiNode> CandidateParkings(TaxiGraph graph, TrafficCategory category, TrafficWake wake, Random random)
    {
        var minRadius = wake switch
        {
            TrafficWake.Light => 5,
            TrafficWake.Medium => 17,
            TrafficWake.Heavy => 26,
            _ => 38,
        };
        int[] preferred = category switch
        {
            TrafficCategory.GeneralAviation => [1, 2, 3, 4, 14],
            TrafficCategory.Military => [6, 7],
            _ => [8, 9, 10, 15, 5],
        };
        var fitting = graph.Nodes.Where(n => n.Parking is { } p && p.RadiusM >= minRadius && p.Type is not (11 or 12 or 13 or 0) && !graph.IsOccupied(n)).ToList();
        var first = fitting.Where(n => preferred.Contains(n.Parking!.Type)).OrderBy(_ => random.Next()).ToList();
        var rest = fitting.Except(first).OrderBy(_ => random.Next());
        return [.. first, .. rest];
    }

    /// <summary>Wege aus der Route: Pushback, wenn die Nase vom Rollweg wegzeigt, dann vorwärts bis kurz vor den Haltepunkt.</summary>
    public static TaxiRoute Build(TaxiGraph graph, TaxiNode parking, TaxiHold hold, IReadOnlyList<int> nodes, TaxiProfile profile)
    {
        var points = nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)).ToList();
        var straight = profile.TaxiKt * MetersPerSecondPerKnot;
        TaxiPath? pushback = null;

        var heading = parking.Parking?.HeadingDeg ?? 0;
        var leadIn = TaxiPath.Heading(points[1].EastM - points[0].EastM, points[1].NorthM - points[0].NorthM);
        if (parking.IsParking && Math.Abs(TrafficMath.Normalize(leadIn - heading + 180) - 180) > PushbackAboveDeg && points.Count >= 2)
        {
            // Rückwärts zum ersten Rollweg-Punkt T und im Bogen auf den Rollweg dahinter (Gegenrichtung der Weiterfahrt) bis E
            var t = graph.Nodes[nodes[1]];
            var ahead = points.Count >= 3 ? Unit(points[2].EastM - t.EastM, points[2].NorthM - t.NorthM) : Unit(t.EastM - parking.EastM, t.NorthM - parking.NorthM);
            var behind = (East: -ahead.East, North: -ahead.North);
            var (endEast, endNorth) = (t.EastM + (behind.East * profile.PushbackLengthM * 0.5), t.NorthM + (behind.North * profile.PushbackLengthM * 0.5));
            var bestDot = Math.Cos(PushbackLineDeg * Math.PI / 180);
            foreach (var edge in graph.EdgesOf(t.Id))
            {
                var other = graph.Nodes[edge.Other(t.Id)];
                if (other.IsParking || (nodes.Count >= 3 && other.Id == nodes[2]) || graph.IsBlocked(edge))
                {
                    continue;
                }

                var dir = Unit(other.EastM - t.EastM, other.NorthM - t.NorthM);
                var dot = (dir.East * behind.East) + (dir.North * behind.North);
                if (dot >= bestDot)
                {
                    bestDot = dot;
                    var length = Math.Min(profile.PushbackLengthM, edge.LengthM);
                    (endEast, endNorth) = (t.EastM + (dir.East * length), t.NorthM + (dir.North * length));
                }
            }

            pushback = TaxiPath.Build([points[0], (t.EastM, t.NorthM), (endEast, endNorth)], profile.PushbackRadiusM, TaxiKinematics.PushbackKt * MetersPerSecondPerKnot, profile.LateralMps2, 0.3);
            points = [(endEast, endNorth), .. points.Skip(1)];
        }

        // Kurz vor dem Haltepunkt stehen bleiben: die Nase steht vor dem Bezugspunkt
        if (points.Count >= 2)
        {
            var (lastEast, lastNorth) = points[^1];
            var (prevEast, prevNorth) = points[^2];
            var segment = Math.Sqrt(((lastEast - prevEast) * (lastEast - prevEast)) + ((lastNorth - prevNorth) * (lastNorth - prevNorth)));
            if (segment > profile.StopShortM + 1)
            {
                var f = (segment - profile.StopShortM) / segment;
                points[^1] = (prevEast + ((lastEast - prevEast) * f), prevNorth + ((lastNorth - prevNorth) * f));
            }
        }

        var taxi = TaxiPath.Build(points, profile.TurnRadiusM, straight, profile.LateralMps2, Math.Min(straight, MinTurnMps));
        return new TaxiRoute(parking, hold, nodes, graph.TaxiwayNames(nodes), Math.Round(graph.LengthM(nodes)), pushback, taxi);
    }

    private static (double East, double North) Unit(double east, double north)
    {
        var length = Math.Sqrt((east * east) + (north * north));
        return length > 0 ? (east / length, north / length) : (0, 1);
    }
}
