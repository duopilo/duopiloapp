using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>
/// Route vom Parkplatz zum Haltepunkt einer Startbahn mit den Wegen für Pushback und Rollen (<see cref="TaxiKinematics"/>).
/// </summary>
/// <param name="Nodes">Knoten der Route im Netz, von der Parkposition bis zum ersten Knoten hinter dem Halt.</param>
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
    /// <summary>Aufreihen: so weit rollt das Flugzeug hinter dem Einmündungspunkt auf der Mittellinie in Startrichtung.</summary>
    public const double LineUpAheadM = 40;

    /// <summary>Zeigt die Nase so weit vom Rollweg weg (Grad), schiebt ein Schlepper das Flugzeug zurück.</summary>
    public const double PushbackAboveDeg = 90;

    /// <summary>Vom Halt höchstens so weit bis auf die Bahn – weiter sucht <see cref="Plan"/> keinen Haltepunkt.</summary>
    public const double MaxLineUpM = 300;

    /// <summary>So weit schiebt der Schlepper höchstens zurück, bis die Route abbiegt (lange Einfahrlinien).</summary>
    public const double MaxPushbackM = 250;

    /// <summary>Ohne Haltepunkt der Bahn in der Nähe: so weit von der Mittellinie halten.</summary>
    public const double DefaultHoldDistanceM = 90;

    /// <summary>Punkte gelten als auf der Bahn, wenn sie so nah an der Mittellinie liegen.</summary>
    private const double OnCenterlineM = 8;

    /// <summary>Wahl der Einmündung: jeder Meter hinter dem Bahnanfang zählt so viel wie diese Meter Rollweg (volle Bahnlänge bevorzugt).</summary>
    private const double EntryAlongWeight = 3;

    /// <summary>Der Rollweg hinter dem Pushback-Punkt zählt, wenn er höchstens so weit (Grad) von der Gegenrichtung abweicht.</summary>
    private const double PushbackLineDeg = 45;

    private const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Langsamer als das rollt das Flugzeug auch in engen Kurven nicht (m/s, gut 4 kt).</summary>
    private const double MinTurnMps = 2.2;

    public bool Pushback => PushbackPath is not null;

    /// <summary>Aufreihen: Weg vom Halt auf die Mittellinie und in Startrichtung, null = nicht geplant.</summary>
    public TaxiPath? LineUpPath { get; init; }

    /// <summary>Aufreihen: wo das Flugzeug danach steht, entlang ab dem Bahnanfang.</summary>
    public double? LineUpAlongM { get; init; }

    /// <summary>Rollpunkte hinter dem Halt bis zur Einmündung auf die Bahn (der Weg des Aufreihens).</summary>
    public IReadOnlyList<int> LineUpNodes { get; init; } = [];

    /// <summary>Einmündung auf die Mittellinie, entlang ab dem Bahnanfang; null = Route ohne Einmündung (<see cref="Build(TaxiGraph, TaxiNode, TaxiHold, IReadOnlyList{int}, TaxiProfile)"/>).</summary>
    public double? EntryAlongM { get; init; }

    /// <summary>
    /// Route von <paramref name="parking"/> auf die Bahn <paramref name="end"/> mit Halt davor, sonst null. Ziel ist eine Einmündung:
    /// ein Rollpunkt auf der Mittellinie in der ersten Bahnhälfte, den die Route über einen Rollweg erreicht (nicht über die Bahn) –
    /// die mit der kürzesten Route, wobei jeder Meter hinter dem Bahnanfang <see cref="EntryAlongWeight"/> Meter Rollweg zählt.
    /// Gehalten wird auf dem Weg dorthin (<see cref="FindStop"/>): am letzten Haltepunkt der Bahn (normale Haltelinie, nicht die
    /// ILS-Linie) oder – fehlt am Zubringer ein Haltepunkt im Netz – dort, wo die Route so weit von der Mittellinie entfernt ist wie der
    /// nächste Haltepunkt der Bahn. In EDDL hat der Zubringer von K1 auf die 23R keinen Haltepunkt im Netz; beta.24 hielt deshalb an der
    /// langen Linie am Rollweg geradeaus und rollte zurück.
    /// </summary>
    public static TaxiRoute? Plan(TaxiGraph graph, RunwayEnd end, TaxiNode parking, TaxiProfile profile)
    {
        var candidates = new List<(IReadOnlyList<int> Nodes, double Along, double Score)>();
        foreach (var (entry, along) in RunwayEntries(graph, end))
        {
            if (graph.Route(parking.Id, entry.Id) is not { Count: >= 3 } nodes || graph.Edge(nodes[^2], nodes[^1]) is not { Type: not 2 })
            {
                continue;
            }

            candidates.Add((nodes, along, graph.CostM(nodes) + (EntryAlongWeight * Math.Max(0, along))));
        }

        foreach (var (nodes, along, _) in candidates.OrderBy(c => c.Score))
        {
            if (FindStop(graph, end, nodes, profile) is { } stop)
            {
                return Build(graph, parking, nodes, stop, profile) with { EntryAlongM = Math.Round(along) };
            }
        }

        return null;
    }

    /// <summary>Rollpunkte auf der Mittellinie von <paramref name="end"/> (höchstens <see cref="OnCenterlineM"/> daneben) in der ersten Bahnhälfte.</summary>
    public static IReadOnlyList<(TaxiNode Node, double AlongM)> RunwayEntries(TaxiGraph graph, RunwayEnd end)
    {
        var entries = new List<(TaxiNode, double)>();
        foreach (var node in graph.Nodes)
        {
            if (node.IsParking || node.IsHoldShort)
            {
                continue;
            }

            var (along, cross) = end.Offsets(node.Latitude, node.Longitude);
            if (Math.Abs(cross) <= OnCenterlineM && along >= -200 && along <= end.LengthM * 0.5)
            {
                entries.Add((node, along));
            }
        }

        return entries;
    }

    /// <summary>
    /// Wo vor der Einmündung (letzter Knoten von <paramref name="nodes"/>) gehalten wird, rückwärts gesucht, höchstens
    /// <see cref="MaxLineUpM"/> weit und nur bis zu einem Bahn-Abschnitt (einer gekreuzten Bahn): am letzten normalen Haltepunkt der
    /// Bahn (TYPE 2 oder 5 – die ILS-Linie, TYPE 4/6, liegt davor und gilt bei gutem Wetter nicht) oder dort, wo die Route den Abstand
    /// des nächsten Haltepunkts der Bahn zur Mittellinie unterschreitet; jeweils <see cref="TaxiProfile.StopShortM"/> davor. Null =
    /// keiner (die Route liegt ganz nah an der Bahn).
    /// </summary>
    public static TaxiStop? FindStop(TaxiGraph graph, RunwayEnd end, IReadOnlyList<int> nodes, TaxiProfile profile)
    {
        var designation = AirportRunway.Normalize(end.Designation);
        var entryNode = graph.Nodes[nodes[^1]];
        var holds = graph.Holds().Where(h => h.Runway.Split('/').Any(d => AirportRunway.Normalize(d) == designation)).ToList();
        var normalHolds = holds.Where(h => h.Node.PointType is 2 or 5).ToList();
        var reference = normalHolds.OrderBy(h => Distance(h.Node, entryNode)).FirstOrDefault(h => Distance(h.Node, entryNode) < 600);
        var holdDistance = Math.Clamp(reference?.CenterlineM ?? DefaultHoldDistanceM, 40, 250);
        var normalIds = normalHolds.Select(h => h.Node.Id).ToHashSet();

        double Cross(TaxiNode n) => Math.Abs(end.Offsets(n.Latitude, n.Longitude).CrossM);

        var walked = 0.0;
        for (var i = nodes.Count - 1; i >= 1; i--)
        {
            var (a, b) = (graph.Nodes[nodes[i - 1]], graph.Nodes[nodes[i]]);
            if (i < nodes.Count - 1 && graph.Edge(a.Id, b.Id) is { Type: 2 })
            {
                break;
            }

            walked += Distance(a, b);
            if (walked > MaxLineUpM)
            {
                break;
            }

            (double East, double North)? at = null;
            if (normalIds.Contains(a.Id))
            {
                at = (a.EastM, a.NorthM);
            }
            else if (Cross(a) >= holdDistance && Cross(b) < holdDistance)
            {
                var t = (Cross(a) - holdDistance) / (Cross(a) - Cross(b));
                at = (a.EastM + ((b.EastM - a.EastM) * t), a.NorthM + ((b.NorthM - a.NorthM) * t));
            }

            if (at is { } point)
            {
                var (segment, east, north) = Before(graph, nodes, i - 1, point, profile.StopShortM);
                var taxiway = graph.Edge(a.Id, b.Id)?.Name ?? "";
                var hold = holds.FirstOrDefault(h => h.Node.Id == a.Id)
                    ?? new TaxiHold(a, end.Designation, Math.Round(holdDistance), taxiway);
                return new TaxiStop(segment, east, north, hold with { Taxiway = taxiway.Length > 0 ? taxiway : hold.Taxiway });
            }
        }

        return null;
    }

    /// <summary>
    /// Plant das Aufreihen dazu: vom Halt über die restlichen Rollpunkte auf die Einmündung, dort im Bogen in Startrichtung und
    /// <see cref="LineUpAheadM"/> weiter. Ohne Einmündung (Route aus <see cref="Build(TaxiGraph, TaxiNode, TaxiHold, IReadOnlyList{int}, TaxiProfile)"/>)
    /// bleibt es bei der Route.
    /// </summary>
    public TaxiRoute WithLineUp(TaxiGraph graph, RunwayEnd end, TaxiProfile profile)
    {
        if (LineUpNodes.Count == 0)
        {
            return this;
        }

        var entry = graph.Nodes[LineUpNodes[^1]];
        var (entryAlong, _) = end.Offsets(entry.Latitude, entry.Longitude);
        var aheadAlong = Math.Max(entryAlong, 0) + LineUpAheadM;
        var (aheadLat, aheadLon) = end.Point(aheadAlong);
        var (aheadEast, aheadNorth) = graph.ToLocal(aheadLat, aheadLon);
        var (centerLat, centerLon) = end.Point(entryAlong);
        var (centerEast, centerNorth) = graph.ToLocal(centerLat, centerLon);

        var start = TaxiPath.At(TaxiPath.LengthM);
        var points = new List<(double East, double North)> { (start.East, start.North) };
        points.AddRange(LineUpNodes.Take(LineUpNodes.Count - 1).Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
        points.Add((centerEast, centerNorth));
        points.Add((aheadEast, aheadNorth));
        var straight = Math.Min(profile.TaxiKt, TaxiKinematics.LineUpKt) * MetersPerSecondPerKnot;
        var path = TaxiPath.Build(points, profile.TurnRadiusM, straight, profile.LateralMps2, Math.Min(straight, MinTurnMps));
        return this with { LineUpPath = path, LineUpAlongM = Math.Round(aheadAlong) };
    }

    /// <summary>
    /// Punkt <paramref name="distanceM"/> vor <paramref name="point"/> (auf dem Abschnitt hinter Knoten <paramref name="segment"/>),
    /// rückwärts entlang der Route; mit dem Abschnitt, auf dem er liegt.
    /// </summary>
    private static (int Segment, double East, double North) Before(TaxiGraph graph, IReadOnlyList<int> nodes, int segment, (double East, double North) point, double distanceM)
    {
        var (east, north) = point;
        var left = distanceM;
        for (var k = segment; k >= 0; k--)
        {
            var a = graph.Nodes[nodes[k]];
            var length = Math.Sqrt(((east - a.EastM) * (east - a.EastM)) + ((north - a.NorthM) * (north - a.NorthM)));
            if (length >= left && length > 0)
            {
                var f = left / length;
                return (k, east + ((a.EastM - east) * f), north + ((a.NorthM - north) * f));
            }

            // Weiter auf dem Abschnitt davor: der Punkt ist jetzt Knoten k
            left -= length;
            (east, north) = (a.EastM, a.NorthM);
        }

        return (0, east, north);
    }

    private static double Distance(TaxiNode a, TaxiNode b) =>
        Math.Sqrt(((a.EastM - b.EastM) * (a.EastM - b.EastM)) + ((a.NorthM - b.NorthM) * (a.NorthM - b.NorthM)));

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

    /// <summary>
    /// Wege aus einer Route, die an einem Haltepunkt endet (letzter Knoten): Pushback, wenn die Nase vom Rollweg wegzeigt, dann vorwärts
    /// bis <see cref="TaxiProfile.StopShortM"/> vor den Haltepunkt. Ohne Einmündung – kein Aufreihen.
    /// </summary>
    public static TaxiRoute Build(TaxiGraph graph, TaxiNode parking, TaxiHold hold, IReadOnlyList<int> nodes, TaxiProfile profile)
    {
        var (segment, east, north) = Before(graph, nodes, nodes.Count - 2, (graph.Nodes[nodes[^1]].EastM, graph.Nodes[nodes[^1]].NorthM), profile.StopShortM);
        return Build(graph, parking, nodes, new TaxiStop(segment, east, north, hold), profile) with { LineUpNodes = [] };
    }

    /// <summary>
    /// Wege aus einer Route bis zur Einmündung mit dem Halt davor: Pushback, wenn die Nase vom Rollweg wegzeigt, dann vorwärts bis zum
    /// Halt; die Rollpunkte dahinter sind der Weg des Aufreihens (<see cref="WithLineUp"/>).
    /// </summary>
    public static TaxiRoute Build(TaxiGraph graph, TaxiNode parking, IReadOnlyList<int> nodes, TaxiStop stop, TaxiProfile profile)
    {
        var taxiNodes = nodes.Take(stop.Segment + 1).ToList();
        var points = taxiNodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)).ToList();
        points.Add((stop.East, stop.North));
        var straight = profile.TaxiKt * MetersPerSecondPerKnot;
        TaxiPath? pushback = null;

        var heading = parking.Parking?.HeadingDeg ?? 0;
        var leadIn = TaxiPath.Heading(points[1].EastM - points[0].EastM, points[1].NorthM - points[0].NorthM);
        if (parking.IsParking && Math.Abs(TrafficMath.Normalize(leadIn - heading + 180) - 180) > PushbackAboveDeg && points.Count >= 2)
        {
            // Rückwärts die Route entlang bis zu dem Punkt J, an dem sie abbiegt und hinter dem ein Rollweg in Gegenrichtung der
            // Weiterfahrt liegt; dorthin schwenkt das Heck (bis E), die Nase zeigt dann in Fahrtrichtung. Geht die Route nach der
            // Parkposition erst geradeaus weiter (lange Einfahrlinie wie auf den Frachtpositionen in EDDL), schiebt der Schlepper das
            // Flugzeug die ganze Linie zurück – sonst müsste es nach dem Pushback umdrehen.
            var routePoints = nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)).ToList();
            var (turnIndex, endEast, endNorth) = PushbackEnd(graph, parking, nodes, routePoints, profile);
            turnIndex = Math.Clamp(turnIndex, 1, Math.Max(1, points.Count - 2));
            var pushPoints = points.Take(turnIndex + 1).ToList();
            pushPoints.Add((endEast, endNorth));
            pushback = TaxiPath.Build(pushPoints, profile.PushbackRadiusM, TaxiKinematics.PushbackKt * MetersPerSecondPerKnot, profile.LateralMps2, 0.3);
            points = [(endEast, endNorth), .. points.Skip(turnIndex)];
        }

        var taxi = TaxiPath.Build(points, profile.TurnRadiusM, straight, profile.LateralMps2, Math.Min(straight, MinTurnMps));
        var routeNodes = nodes.Take(Math.Min(nodes.Count, stop.Segment + 2)).ToList();
        return new TaxiRoute(parking, stop.Hold, routeNodes, graph.TaxiwayNames(routeNodes), Math.Round(graph.LengthM(routeNodes)), pushback, taxi)
        {
            LineUpNodes = [.. nodes.Skip(stop.Segment + 1)],
        };
    }

    /// <summary>
    /// Wohin der Pushback führt: der erste Punkt J der Route (ab dem ersten Rollpunkt, höchstens <see cref="MaxPushbackM"/> weit), an
    /// dem ein freier Abschnitt höchstens <see cref="PushbackLineDeg"/> von der Gegenrichtung der Weiterfahrt abweicht und nicht der
    /// ist, über den das Flugzeug kam; dort bis E auf diesem Abschnitt (höchstens <see cref="TaxiProfile.PushbackLengthM"/>). Gibt es
    /// keinen, schwenkt das Heck am ersten Rollpunkt quer zur Einfahrt – dann rollt das Flugzeug mit einer Kurve los statt umzudrehen.
    /// </summary>
    private static (int Index, double East, double North) PushbackEnd(
        TaxiGraph graph,
        TaxiNode parking,
        IReadOnlyList<int> nodes,
        IReadOnlyList<(double EastM, double NorthM)> points,
        TaxiProfile profile)
    {
        var minDot = Math.Cos(PushbackLineDeg * Math.PI / 180);
        var distance = 0.0;
        for (var k = 1; k < nodes.Count - 1; k++)
        {
            distance += Length(points[k - 1], points[k]);
            if (distance > MaxPushbackM)
            {
                break;
            }

            var j = graph.Nodes[nodes[k]];
            var ahead = Unit(points[k + 1].EastM - j.EastM, points[k + 1].NorthM - j.NorthM);
            var behind = (East: -ahead.East, North: -ahead.North);
            (double Dot, double East, double North)? best = null;
            foreach (var edge in graph.EdgesOf(j.Id))
            {
                var other = graph.Nodes[edge.Other(j.Id)];
                if (other.IsParking || other.Id == nodes[k - 1] || other.Id == nodes[k + 1] || graph.IsBlocked(edge))
                {
                    continue;
                }

                var dir = Unit(other.EastM - j.EastM, other.NorthM - j.NorthM);
                var dot = (dir.East * behind.East) + (dir.North * behind.North);
                if (dot >= minDot && (best is null || dot > best.Value.Dot))
                {
                    var length = Math.Min(profile.PushbackLengthM, edge.LengthM);
                    best = (dot, j.EastM + (dir.East * length), j.NorthM + (dir.North * length));
                }
            }

            if (best is { } found)
            {
                return (k, found.East, found.North);
            }
        }

        // Kein passender Rollweg: am ersten Rollpunkt quer zur Einfahrt, auf die Seite gegen die nächste Kurve (sonst rechts)
        var t = graph.Nodes[nodes[1]];
        var inDir = Unit(t.EastM - parking.EastM, t.NorthM - parking.NorthM);
        var next = points.Count >= 3 ? Unit(points[2].EastM - t.EastM, points[2].NorthM - t.NorthM) : inDir;
        var cross = (inDir.East * next.North) - (inDir.North * next.East);
        // Biegt die Route links ab, schwenkt das Heck nach rechts (die Nase zeigt nach links), sonst umgekehrt; geradeaus: rechts
        var right = (East: inDir.North, North: -inDir.East);
        var side = cross < -0.2 ? (East: -right.East, North: -right.North) : right;
        var half = profile.PushbackLengthM * 0.5;
        return (1, t.EastM + (side.East * half), t.NorthM + (side.North * half));
    }

    private static double Length((double EastM, double NorthM) a, (double EastM, double NorthM) b) =>
        Math.Sqrt(((b.EastM - a.EastM) * (b.EastM - a.EastM)) + ((b.NorthM - a.NorthM) * (b.NorthM - a.NorthM)));

    private static (double East, double North) Unit(double east, double north)
    {
        var length = Math.Sqrt((east * east) + (north * north));
        return length > 0 ? (east / length, north / length) : (0, 1);
    }
}

/// <summary>Halt vor der Bahn: auf dem Abschnitt hinter Knoten <paramref name="Segment"/> der Route, an der Stelle (Ost, Nord).</summary>
/// <param name="Hold">Der Haltepunkt (oder der Knoten davor mit dem Abstand des nächsten Haltepunkts, wenn am Zubringer keiner im Netz ist).</param>
public sealed record TaxiStop(int Segment, double East, double North, TaxiHold Hold);
