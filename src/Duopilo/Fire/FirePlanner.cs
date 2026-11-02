using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Weg eines Fahrzeugs von der Wache in den Bereitstellungsraum.</summary>
/// <param name="Hold">Haltepunkt an der Bahn, null bei einem Notfall am Boden (Ziel ist das Flugzeug).</param>
/// <param name="AlongM">Lage des Haltepunkts entlang der Landebahn ab der Schwelle (Meter), sonst 0.</param>
public sealed record FireVehiclePlan(FireUnit Unit, TaxiNode? Hold, double AlongM, TaxiPath Route);

/// <summary>Plan einer Alarmierung: Wache (freie Parkposition), Landebahn und die Wege der Fahrzeuge.</summary>
/// <param name="Missing">Fahrzeuge ohne Weg (kein Haltepunkt erreichbar) – sie rücken nicht aus.</param>
public sealed record FireDispatchPlan(TaxiNode Spawn, RunwayEnd? Runway, IReadOnlyList<FireVehiclePlan> Vehicles, IReadOnlyList<FireUnit> Missing);

/// <summary>
/// Planung ohne Sim: Die Fahrzeuge spawnen auf einer freien Parkposition nahe der Bahnmitte (am liebsten eine Fahrzeug-Parkposition,
/// Gates zuletzt, außer Sicht des eigenen Flugzeugs) und fahren über das Rollweg-Netz zu Haltepunkten der Landebahn: Aufsetzzone,
/// Mitte, Stoppstelle, Bahnende. Teilen sich Fahrzeuge einen Haltepunkt, hält jedes weitere etwas früher.
/// </summary>
public static class FirePlanner
{
    /// <summary>So weit soll die Wache mindestens vom eigenen Flugzeug weg sein (sonst sieht man das Spawnen).</summary>
    public const double OutOfSightM = 500;

    /// <summary>So viele Parkpositionen probiert die Planung als Wache, bis eine Route zur Bahn findet.</summary>
    public const int MaxSpawnTries = 8;

    /// <summary>Bereitstellung an Haltepunkten der Landebahn.</summary>
    public static FireDispatchPlan? Plan(TaxiGraph graph, RunwayEnd runway, IReadOnlyList<FireUnit> units, TrafficWake wake, (double East, double North)? avoid)
    {
        var designation = AirportRunway.Normalize(runway.Designation);
        var holds = graph.Holds()
            .Where(h => h.Runway.Split('/').Any(d => AirportRunway.Normalize(d) == designation))
            .Select(h =>
            {
                var (along, cross) = runway.Offsets(h.Node.Latitude, h.Node.Longitude);
                return (Hold: h, Along: along, Cross: cross);
            })
            .Where(h => h.Along > -100 && h.Along < runway.LengthM + 100)
            .ToList();
        if (holds.Count == 0)
        {
            return null;
        }

        var (midLat, midLon) = runway.Point(runway.LengthM / 2);
        var (midEast, midNorth) = graph.ToLocal(midLat, midLon);
        foreach (var spawn in SpawnCandidates(graph, midEast, midNorth, avoid).Take(MaxSpawnTries))
        {
            var (_, spawnCross) = runway.Offsets(spawn.Latitude, spawn.Longitude);
            var vehicles = new List<FireVehiclePlan>();
            var missing = new List<FireUnit>();
            var queue = new Dictionary<int, int>();
            foreach (var unit in units)
            {
                var target = TargetAlongM(unit.Role, runway.LengthM, wake);

                // Bester Haltepunkt: nah am Ziel, möglichst auf der Seite der Wache
                var ranked = holds
                    .OrderBy(h => Math.Abs(h.Along - target) + (Math.Sign(h.Cross) == Math.Sign(spawnCross) ? 0 : 300))
                    .ToList();
                FireVehiclePlan? planned = null;
                foreach (var candidate in ranked)
                {
                    if (graph.Route(spawn.Id, candidate.Hold.Node.Id) is not { Count: >= 2 } nodes)
                    {
                        continue;
                    }

                    var waiting = queue.GetValueOrDefault(candidate.Hold.Node.Id);
                    var points = Truncate(nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)).ToList(), waiting * FireProfile.QueueSpacingM);
                    if (points.Count < 2)
                    {
                        continue;
                    }

                    queue[candidate.Hold.Node.Id] = waiting + 1;
                    planned = new FireVehiclePlan(unit, candidate.Hold.Node, Math.Round(candidate.Along), BuildPath(points, FireProfile.MaxMps));
                    break;
                }

                if (planned is null)
                {
                    missing.Add(unit);
                }
                else
                {
                    vehicles.Add(planned);
                }
            }

            if (vehicles.Count > 0)
            {
                return new FireDispatchPlan(spawn, runway, vehicles, missing);
            }
        }

        return null;
    }

    /// <summary>Notfall am Boden: von der Wache über die Rollwege bis kurz vor das Flugzeug, die Fahrzeuge hintereinander.</summary>
    public static FireDispatchPlan? PlanToAircraft(TaxiGraph graph, IReadOnlyList<FireUnit> units, double aircraftEast, double aircraftNorth, double postUnitM)
    {
        foreach (var spawn in SpawnCandidates(graph, aircraftEast, aircraftNorth, (aircraftEast, aircraftNorth)).Take(MaxSpawnTries))
        {
            if (RouteBetween(graph, spawn.EastM, spawn.NorthM, aircraftEast, aircraftNorth, fromParking: spawn) is not { } route)
            {
                continue;
            }

            var vehicles = new List<FireVehiclePlan>();
            for (var i = 0; i < units.Count; i++)
            {
                var points = Truncate(route, (postUnitM * 1.5) + (i * FireProfile.QueueSpacingM));
                if (points.Count >= 2)
                {
                    vehicles.Add(new FireVehiclePlan(units[i], null, 0, BuildPath(points, FireProfile.MaxMps)));
                }
            }

            if (vehicles.Count > 0)
            {
                return new FireDispatchPlan(spawn, null, vehicles, units.Where(u => vehicles.All(v => v.Unit != u)).ToList());
            }
        }

        return null;
    }

    /// <summary>
    /// Freie Parkpositionen als Wache, die beste zuerst: nah an <paramref name="refEast"/>/<paramref name="refNorth"/>, Fahrzeug-Parkposition
    /// vor Vorfeld und Fracht, Tankstelle und Gates zuletzt, nicht in Sichtweite von <paramref name="avoid"/>.
    /// </summary>
    public static IEnumerable<TaxiNode> SpawnCandidates(TaxiGraph graph, double refEast, double refNorth, (double East, double North)? avoid) =>
        graph.Nodes
            .Where(n => n is { IsParking: true, Parking: not null } && !graph.IsOccupied(n))
            .Select(n =>
            {
                var score = Distance(n.EastM, n.NorthM, refEast, refNorth) + TypePenalty(n.Parking!.Type);
                if (avoid is { } a && Distance(n.EastM, n.NorthM, a.East, a.North) < OutOfSightM)
                {
                    score += 5000;
                }

                return (Node: n, Score: score);
            })
            .OrderBy(c => c.Score)
            .Select(c => c.Node);

    /// <summary>Strafmeter je Art der Parkposition: Fahrzeuge 0, Vorfeld/Fracht/Militär 150, Tankstelle 300, Gates 400.</summary>
    public static double TypePenalty(int parkingType) => parkingType switch
    {
        13 => 0,
        >= 1 and <= 7 or 11 or 14 => 150,
        12 => 300,
        _ => 400,
    };

    /// <summary>Wo der Bereitstellungsraum entlang der Bahn liegen soll (Meter ab der Schwelle).</summary>
    public static double TargetAlongM(FireStandbyRole role, double runwayLengthM, TrafficWake wake) => role switch
    {
        FireStandbyRole.Touchdown => Math.Min(450, runwayLengthM * 0.25),
        FireStandbyRole.Mid => runwayLengthM / 2,
        FireStandbyRole.Rollout => Math.Clamp(FireCategory.ExpectedStopM(wake) + 150, Math.Min(600, runwayLengthM * 0.4), Math.Max(runwayLengthM - 200, runwayLengthM * 0.6)),
        FireStandbyRole.End => Math.Max(runwayLengthM - 250, runwayLengthM * 0.75),
        _ => runwayLengthM / 2,
    };

    /// <summary>
    /// Weg zwischen zwei Punkten über das Rollweg-Netz: vom Punkt zum nächsten Rollpunkt, die Route, vom letzten Rollpunkt zum Ziel.
    /// <paramref name="fromParking"/>: am Anfang steht das Fahrzeug auf dieser Parkposition (Route ab ihr). Null = keine Route.
    /// </summary>
    public static List<(double East, double North)>? RouteBetween(TaxiGraph graph, double fromEast, double fromNorth, double toEast, double toNorth, TaxiNode? fromParking = null)
    {
        var start = fromParking ?? NearestNode(graph, fromEast, fromNorth);
        var end = NearestNode(graph, toEast, toNorth);
        if (start is null || end is null)
        {
            return null;
        }

        var points = new List<(double East, double North)> { (fromEast, fromNorth) };
        if (start.Id != end.Id)
        {
            if (graph.Route(start.Id, end.Id) is not { Count: >= 2 } nodes)
            {
                return null;
            }

            points.AddRange(nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
        }

        points.Add((toEast, toNorth));
        return points;
    }

    /// <summary>Nächster Rollpunkt (keine Parkposition) zu einem Punkt.</summary>
    public static TaxiNode? NearestNode(TaxiGraph graph, double east, double north) =>
        graph.Nodes.Where(n => !n.IsParking).MinBy(n => Distance(n.EastM, n.NorthM, east, north));

    /// <summary>
    /// Weg durch Punkte mit den Kurven der Feuerwehrfahrzeuge. Vorher fallen Punkte weg, die kaum vom geraden Weg abweichen
    /// (<see cref="SimplifyM"/>): Jeder Knick bekommt in <see cref="TaxiPath"/> einen Bogen mit Kurvengeschwindigkeit, und die vielen
    /// leichten Knicke im Rollweg-Netz hielten die Fahrzeuge sonst fast überall bei Kurventempo (Test EDDL, 02.11.: 12–25 kt).
    /// </summary>
    public static TaxiPath BuildPath(IReadOnlyList<(double East, double North)> points, double maxMps) =>
        TaxiPath.Build(Simplify(points, SimplifyM), FireProfile.TurnRadiusM, maxMps, FireProfile.LateralMps2, FireProfile.MinTurnMps);

    /// <summary>So weit darf der vereinfachte Weg höchstens vom Rollweg abweichen.</summary>
    public const double SimplifyM = 4;

    /// <summary>Douglas-Peucker: Linienzug ohne die Punkte, die höchstens <paramref name="toleranceM"/> neben dem Rest liegen.</summary>
    public static List<(double East, double North)> Simplify(IReadOnlyList<(double East, double North)> points, double toleranceM)
    {
        if (points.Count < 3)
        {
            return [.. points];
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            var (worst, index) = (0.0, -1);
            for (var i = from + 1; i < to; i++)
            {
                var d = TaxiGraph.SegmentDistance(points[i].East, points[i].North, points[from].East, points[from].North, points[to].East, points[to].North);
                if (d > worst)
                {
                    (worst, index) = (d, i);
                }
            }

            if (index >= 0 && worst > toleranceM)
            {
                keep[index] = true;
                stack.Push((from, index));
                stack.Push((index, to));
            }
        }

        return [.. points.Where((_, i) => keep[i])];
    }

    /// <summary>Die letzten <paramref name="meters"/> eines Linienzugs abschneiden (das Ende rückt entlang des Wegs nach vorn).</summary>
    public static List<(double East, double North)> Truncate(List<(double East, double North)> points, double meters)
    {
        if (meters <= 0 || points.Count < 2)
        {
            return points;
        }

        var result = new List<(double East, double North)>(points);
        var left = meters;
        while (result.Count >= 2 && left > 0)
        {
            var (a, b) = (result[^2], result[^1]);
            var length = Distance(a.East, a.North, b.East, b.North);
            if (length > left)
            {
                var f = (length - left) / length;
                result[^1] = (a.East + ((b.East - a.East) * f), a.North + ((b.North - a.North) * f));
                return result;
            }

            left -= length;
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    public static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
}
