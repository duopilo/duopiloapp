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
    /// <summary>Aufreihen: so weit rollt das Flugzeug hinter dem Einmündungspunkt auf der Mittellinie in Startrichtung.</summary>
    public const double LineUpAheadM = 40;

    /// <summary>Punkte gelten als auf der Bahn, wenn sie so nah an der Mittellinie liegen.</summary>
    private const double OnCenterlineM = 8;

    /// <summary>Aufreihen: Weg vom Ende des Rollens (kurz vor dem Haltepunkt) auf die Mittellinie, null = nicht geplant.</summary>
    public TaxiPath? LineUpPath { get; init; }

    /// <summary>Aufreihen: wo das Flugzeug danach steht, entlang ab dem Bahnanfang.</summary>
    public double? LineUpAlongM { get; init; }

    /// <summary>Aufreihen: Rollpunkte vom Haltepunkt bis zur Einmündung auf die Bahn.</summary>
    public IReadOnlyList<int> LineUpNodes { get; init; } = [];

    /// <summary>
    /// Plant das Aufreihen dazu: vom Haltepunkt über die Rollpunkte zum nächsten Punkt auf der Mittellinie von <paramref name="end"/>
    /// (Bahn-Abschnitt oder höchstens <see cref="OnCenterlineM"/> daneben), dort im Bogen in Startrichtung und
    /// <see cref="LineUpAheadM"/> weiter. Ohne Weg auf die Bahn bleibt es bei der Route ohne Aufreihen.
    /// </summary>
    public TaxiRoute WithLineUp(TaxiGraph graph, RunwayEnd end, TaxiProfile profile)
    {
        var holdNode = Hold.Node.Id;
        IReadOnlyList<int>? best = null;
        var bestLength = double.MaxValue;
        foreach (var node in graph.Nodes)
        {
            if (node.IsParking || node.Id == holdNode)
            {
                continue;
            }

            var (along, cross) = end.Offsets(node.Latitude, node.Longitude);
            if (Math.Abs(cross) > OnCenterlineM || along < -200 || along > end.LengthM * 0.5)
            {
                continue;
            }

            if (graph.Route(holdNode, node.Id) is { Count: >= 2 } candidate && graph.LengthM(candidate) < Math.Min(bestLength, 600))
            {
                best = candidate;
                bestLength = graph.LengthM(candidate);
            }
        }

        if (best is null)
        {
            return this;
        }

        // Einmündung auf der Mittellinie, dann geradeaus in Startrichtung
        var entry = graph.Nodes[best[^1]];
        var (entryAlong, _) = end.Offsets(entry.Latitude, entry.Longitude);
        var aheadAlong = Math.Max(entryAlong, 0) + LineUpAheadM;
        var (aheadLat, aheadLon) = end.Point(aheadAlong);
        var (aheadEast, aheadNorth) = graph.ToLocal(aheadLat, aheadLon);
        var (centerLat, centerLon) = end.Point(entryAlong);
        var (centerEast, centerNorth) = graph.ToLocal(centerLat, centerLon);

        var start = TaxiPath.At(TaxiPath.LengthM);
        var points = new List<(double East, double North)> { (start.East, start.North) };
        points.AddRange(best.Take(best.Count - 1).Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
        points.Add((centerEast, centerNorth));
        points.Add((aheadEast, aheadNorth));
        var straight = Math.Min(profile.TaxiKt, TaxiKinematics.LineUpKt) * MetersPerSecondPerKnot;
        var path = TaxiPath.Build(points, profile.TurnRadiusM, straight, profile.LateralMps2, Math.Min(straight, MinTurnMps));
        return this with { LineUpPath = path, LineUpAlongM = Math.Round(aheadAlong), LineUpNodes = best };
    }

    /// <summary>Zeigt die Nase so weit vom Rollweg weg (Grad), schiebt ein Schlepper das Flugzeug zurück.</summary>
    public const double PushbackAboveDeg = 90;

    /// <summary>Der Rollweg hinter dem Pushback-Punkt zählt, wenn er höchstens so weit (Grad) von der Gegenrichtung abweicht.</summary>
    private const double PushbackLineDeg = 45;

    /// <summary>So weit schiebt der Schlepper höchstens zurück, bis die Route abbiegt (lange Einfahrlinien).</summary>
    public const double MaxPushbackM = 250;

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
            // Rückwärts die Route entlang bis zu dem Punkt J, an dem sie abbiegt und hinter dem ein Rollweg in Gegenrichtung der
            // Weiterfahrt liegt; dorthin schwenkt das Heck (bis E), die Nase zeigt dann in Fahrtrichtung. Geht die Route nach der
            // Parkposition erst geradeaus weiter (lange Einfahrlinie wie auf den Frachtpositionen in EDDL), schiebt der Schlepper das
            // Flugzeug die ganze Linie zurück – sonst müsste es nach dem Pushback umdrehen.
            var (turnIndex, endEast, endNorth) = PushbackEnd(graph, parking, nodes, points, profile);
            var pushPoints = points.Take(turnIndex + 1).ToList();
            pushPoints.Add((endEast, endNorth));
            pushback = TaxiPath.Build(pushPoints, profile.PushbackRadiusM, TaxiKinematics.PushbackKt * MetersPerSecondPerKnot, profile.LateralMps2, 0.3);
            points = [(endEast, endNorth), .. points.Skip(turnIndex)];
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
