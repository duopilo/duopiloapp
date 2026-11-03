using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Weg eines Fahrzeugs von seiner Wache in den Bereitstellungsraum.</summary>
/// <param name="Hold">Haltepunkt an der Bahn, null bei einem Notfall am Boden (Ziel ist das Flugzeug).</param>
/// <param name="AlongM">Lage des Haltepunkts entlang der Landebahn ab der Schwelle (Meter), sonst 0.</param>
public sealed record FireVehiclePlan(FireUnit Unit, FireStation Station, TaxiNode? Hold, double AlongM, TaxiPath Route);

/// <summary>Plan einer Alarmierung: Wachen, Landebahn und die Wege der Fahrzeuge.</summary>
/// <param name="Missing">Fahrzeuge ohne Weg (kein Haltepunkt erreichbar) – sie rücken nicht aus.</param>
public sealed record FireDispatchPlan(IReadOnlyList<FireStation> Stations, RunwayEnd? Runway, IReadOnlyList<FireVehiclePlan> Vehicles, IReadOnlyList<FireUnit> Missing)
{
    /// <summary>Die erste Wache (die des ersten Fahrzeugs).</summary>
    public FireStation Spawn => Vehicles.Count > 0 ? Vehicles[0].Station : Stations[0];

    /// <summary>Echte Wachen aus OpenStreetMap (sonst freie Parkposition).</summary>
    public bool FromOsm => Stations.Any(s => s.Source == "osm");

    /// <summary>
    /// Einsatz an einer Parkposition (Gate, Vorfeld): Vor der Nase steht das Terminal, links die Fluggastbrücke. Die Fahrzeuge kommen
    /// über die Zufahrt der Position von hinten und stellen sich nur hinter und rechts neben das Flugzeug (<see cref="FireOperation.StandPost"/>).
    /// </summary>
    public TaxiNode? Stand { get; init; }
}

/// <summary>
/// Planung ohne Sim. Wachen: echte Feuerwachen aus OpenStreetMap, sonst eine freie Parkposition nahe der Bahnmitte (am liebsten eine
/// Fahrzeug-Parkposition, Gates zuletzt, außer Sicht des eigenen Flugzeugs). Bei Local Standby und Notfall am Boden kommt alles von
/// einer Wache (der nächsten), bei Full Emergency nimmt jedes Fahrzeug die Wache mit dem kürzesten Weg. Ziele sind Haltepunkte der
/// Landebahn: Aufsetzzone, Mitte, Stoppstelle, Bahnende. Teilen sich Fahrzeuge einen Haltepunkt, hält jedes weitere etwas früher.
/// </summary>
public static class FirePlanner
{
    /// <summary>So weit soll eine Parkposition als Wache mindestens vom eigenen Flugzeug weg sein (sonst sieht man das Spawnen).</summary>
    public const double OutOfSightM = 500;

    /// <summary>So viele Parkpositionen probiert die Planung als Wache, bis eine Route zur Bahn findet.</summary>
    public const int MaxSpawnTries = 8;

    /// <summary>Weiter als das vom nächsten Rollpunkt weg taugt eine OSM-Wache nicht als Start.</summary>
    public const double MaxStationOffNetworkM = 400;

    /// <summary>
    /// Bereitstellung an Haltepunkten der Landebahn. <paramref name="stations"/>: Wachen aus OSM (leer = freie Parkposition);
    /// <paramref name="allStations"/>: jedes Fahrzeug von der Wache mit dem kürzesten Weg (Full Emergency), sonst alle von einer.
    /// </summary>
    public static FireDispatchPlan? Plan(
        TaxiGraph graph,
        RunwayEnd runway,
        IReadOnlyList<FireUnit> units,
        TrafficWake wake,
        (double East, double North)? avoid,
        IReadOnlyList<FireStation>? stations = null,
        bool allStations = false)
    {
        var designation = AirportRunway.Normalize(runway.Designation);
        var holds = graph.Holds()
            .Where(h => h.Runway.Split('/').Any(d => AirportRunway.Normalize(d) == designation))
            .Select(h =>
            {
                var (along, cross) = runway.Offsets(h.Node.Latitude, h.Node.Longitude);
                return new HoldInfo(h.Node, along, cross);
            })
            .Where(h => h.Along > -100 && h.Along < runway.LengthM + 100)
            .ToList();
        if (holds.Count == 0)
        {
            return null;
        }

        var (midLat, midLon) = runway.Point(runway.LengthM / 2);
        var (midEast, midNorth) = graph.ToLocal(midLat, midLon);
        var osm = (stations ?? []).Where(s => NearestNode(graph, s.EastM, s.NorthM) is { } n && Distance(n.EastM, n.NorthM, s.EastM, s.NorthM) <= MaxStationOffNetworkM).ToList();
        if (osm.Count > 0)
        {
            var plan = allStations
                ? PlanUnits(graph, runway, units, wake, holds, osm)
                : osm.OrderBy(s => Distance(s.EastM, s.NorthM, midEast, midNorth))
                    .Select(s => PlanUnits(graph, runway, units, wake, holds, [s]))
                    .FirstOrDefault(p => p is not null);
            if (plan is not null)
            {
                return plan;
            }
        }

        // Rückfall: freie Parkposition
        foreach (var spawn in SpawnCandidates(graph, midEast, midNorth, avoid).Take(MaxSpawnTries))
        {
            if (PlanUnits(graph, runway, units, wake, holds, [FireStation.FromParking(spawn)]) is { } plan)
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>
    /// Notfall am Boden: von der nächsten Wache (OSM, sonst freie Parkposition außer Sicht) über die Rollwege bis kurz vor das
    /// Flugzeug, die Fahrzeuge hintereinander.
    /// </summary>
    /// <param name="stand">Steht das Flugzeug (oder der Einsatzort) auf einer Parkposition: über deren Zufahrt anfahren, nie geradeaus –
    /// der nächste Rollpunkt kann hinter dem Terminal liegen (Test EDDL B9, 03.11.: ein FLF fuhr durch den Abflug in die Fluggastbrücke).</param>
    public static FireDispatchPlan? PlanToAircraft(TaxiGraph graph, IReadOnlyList<FireUnit> units, double aircraftEast, double aircraftNorth, double postUnitM, IReadOnlyList<FireStation>? stations = null, TaxiNode? stand = null)
    {
        var candidates = (stations ?? [])
            .Where(s => NearestNode(graph, s.EastM, s.NorthM) is { } n && Distance(n.EastM, n.NorthM, s.EastM, s.NorthM) <= MaxStationOffNetworkM)
            .OrderBy(s => Distance(s.EastM, s.NorthM, aircraftEast, aircraftNorth))
            .Concat(SpawnCandidates(graph, aircraftEast, aircraftNorth, (aircraftEast, aircraftNorth)).Take(MaxSpawnTries).Select(FireStation.FromParking));
        foreach (var station in candidates)
        {
            // Je Fahrzeug ein eigener Platz an der Wache (nebeneinander), also ein eigener Weg
            var vehicles = new List<FireVehiclePlan>();
            for (var i = 0; i < units.Count; i++)
            {
                var route = stand is not null
                    ? RouteFrom(graph, station, stand, i)
                    : FromStation(station, i, (sx, sy) => RouteBetween(graph, sx, sy, aircraftEast, aircraftNorth, fromParking: station.Parking));
                if (route is null)
                {
                    if (i == 0)
                    {
                        break;
                    }

                    continue;
                }

                var points = Truncate(route, (postUnitM * 1.5) + (i * FireProfile.QueueSpacingM));
                if (points.Count >= 2)
                {
                    vehicles.Add(new FireVehiclePlan(units[i], station, null, 0, BuildPath(points, FireProfile.MaxMps)));
                }
            }

            if (vehicles.Count > 0)
            {
                return new FireDispatchPlan([station], null, vehicles, units.Where(u => vehicles.All(v => v.Unit != u)).ToList()) { Stand = stand };
            }
        }

        return null;
    }

    /// <summary>
    /// Haltepunkt und Weg je Fahrzeug von den möglichen Wachen: je Wache der beste Haltepunkt (nah am Ziel, möglichst auf ihrer Seite der
    /// Bahn) mit Route; von mehreren Wachen die mit dem kürzesten Weg (plus dreifach die Abweichung vom Ziel). Null = kein Fahrzeug.
    /// </summary>
    private static FireDispatchPlan? PlanUnits(TaxiGraph graph, RunwayEnd runway, IReadOnlyList<FireUnit> units, TrafficWake wake, List<HoldInfo> holds, IReadOnlyList<FireStation> stations)
    {
        var vehicles = new List<FireVehiclePlan>();
        var missing = new List<FireUnit>();
        var queue = new Dictionary<int, int>();
        var used = new Dictionary<FireStation, int>();
        foreach (var unit in units)
        {
            var target = TargetAlongM(unit.Role, runway.LengthM, wake);
            (double Cost, FireStation Station, HoldInfo Hold, List<(double East, double North)> Points)? best = null;
            foreach (var station in stations)
            {
                var (_, stationCross) = runway.Offsets(station.Latitude, station.Longitude);
                var ranked = holds.OrderBy(h => Math.Abs(h.Along - target) + (Math.Sign(h.Cross) == Math.Sign(stationCross) ? 0 : 300));
                foreach (var hold in ranked)
                {
                    if (RouteFrom(graph, station, hold.Node, used.GetValueOrDefault(station)) is not { Count: >= 2 } points)
                    {
                        continue;
                    }

                    var cost = LengthM(points) + (3 * Math.Abs(hold.Along - target));
                    if (best is null || cost < best.Value.Cost)
                    {
                        best = (cost, station, hold, points);
                    }

                    break;
                }
            }

            if (best is not { } chosen)
            {
                missing.Add(unit);
                continue;
            }

            var waiting = queue.GetValueOrDefault(chosen.Hold.Node.Id);
            var truncated = Truncate(chosen.Points, waiting * FireProfile.QueueSpacingM);
            if (truncated.Count < 2)
            {
                missing.Add(unit);
                continue;
            }

            queue[chosen.Hold.Node.Id] = waiting + 1;
            used[chosen.Station] = used.GetValueOrDefault(chosen.Station) + 1;
            vehicles.Add(new FireVehiclePlan(unit, chosen.Station, chosen.Hold.Node, Math.Round(chosen.Hold.Along), BuildPath(truncated, FireProfile.MaxMps)));
        }

        return vehicles.Count == 0
            ? null
            : new FireDispatchPlan([.. vehicles.Select(v => v.Station).Distinct()], runway, vehicles, missing);
    }

    /// <summary>
    /// Weg von einer Wache zu einem Rollpunkt bzw. einer Parkposition für das Fahrzeug <paramref name="index"/>: von seinem Platz
    /// (<see cref="ExitStart"/>) zum nächsten Rollpunkt, zu dem die gerade Strecke nicht durch ein Gebäude führt, und weiter.
    /// </summary>
    private static List<(double East, double North)>? RouteFrom(TaxiGraph graph, FireStation station, TaxiNode target, int index = 0) =>
        FromStation(station, index, (sx, sy) =>
        {
            foreach (var start in ClearFirst(graph, sx, sy, StartNodes(graph, sx, sy, station.Parking)))
            {
                var points = new List<(double East, double North)> { (sx, sy) };
                if (start.Id == target.Id)
                {
                    points.Add((target.EastM, target.NorthM));
                    return points;
                }

                if (graph.Route(start.Id, target.Id) is { Count: >= 2 } nodes)
                {
                    points.AddRange(nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
                    return points;
                }
            }

            return null;
        });

    /// <summary>
    /// Anfang des Wegs an der Wache für das Fahrzeug <paramref name="index"/>: sein Platz (nebeneinander, <see cref="FireStation.Exits"/>),
    /// vor einem Tor dazu <see cref="FireExits.RollOutM"/> geradeaus aus der Halle. Ohne Plätze der Mittelpunkt.
    /// </summary>
    public static List<(double East, double North)> ExitStart(FireStation station, int index)
    {
        if (station.Exits.Count == 0)
        {
            return [(station.EastM, station.NorthM)];
        }

        var exit = station.Exits[index % station.Exits.Count];
        var points = new List<(double East, double North)> { (exit.EastM, exit.NorthM) };
        if (exit.RollOut)
        {
            var rad = exit.HeadingDeg * Math.PI / 180;
            points.Add((exit.EastM + (Math.Sin(rad) * FireExits.RollOutM), exit.NorthM + (Math.Cos(rad) * FireExits.RollOutM)));
        }

        return points;
    }

    /// <summary>
    /// Weg ab dem Platz eines Fahrzeugs: <paramref name="route"/> rechnet ab dem letzten Punkt von <see cref="ExitStart"/>. Neben einer
    /// Parkposition (nicht auf ihr) geht es gleich zum nächsten Punkt statt erst seitlich auf die Position.
    /// </summary>
    private static List<(double East, double North)>? FromStation(FireStation station, int index, Func<double, double, List<(double East, double North)>?> route)
    {
        var prefix = ExitStart(station, index);
        var (sx, sy) = prefix[^1];
        if (route(sx, sy) is not { Count: >= 2 } points)
        {
            return null;
        }

        IEnumerable<(double East, double North)> rest = points.Skip(1);
        if (station.Parking is { } parking && Distance(sx, sy, parking.EastM, parking.NorthM) > 0.5)
        {
            rest = rest.SkipWhile(p => Distance(p.East, p.North, parking.EastM, parking.NorthM) < 0.5);
        }

        var result = new List<(double East, double North)>(prefix);
        result.AddRange(rest);
        return result.Count >= 2 ? result : null;
    }

    /// <summary>Rollpunkte, zu denen die gerade Strecke ab (x, y) frei von Gebäuden ist, zuerst; danach die übrigen (lieber knapp als gar nicht).</summary>
    private static IEnumerable<TaxiNode> ClearFirst(TaxiGraph graph, double x, double y, IEnumerable<TaxiNode> nodes)
    {
        var list = nodes.ToList();
        return list.Where(n => graph.Buildings == 0 || n.IsParking || !graph.CrossesBuilding(x, y, n.EastM, n.NorthM))
            .Concat(list)
            .Distinct();
    }

    /// <summary>
    /// Wo ein Weg ins Rollweg-Netz beginnt: die Parkposition, sonst die nächsten Rollpunkte (bis <see cref="MaxEndNodeTries"/>, höchstens
    /// <see cref="MaxStationOffNetworkM"/> entfernt, mindestens der nächste). Der nächste kann abgeschnitten sein – an der Wache in EDDL
    /// ein Stück am Helipad (Test 02.11.: OSM-Wache bekannt, gespawnt wurde trotzdem auf V63).
    /// </summary>
    private static IEnumerable<TaxiNode> StartNodes(TaxiGraph graph, double east, double north, TaxiNode? parking)
    {
        if (parking is not null)
        {
            return [parking];
        }

        return graph.Nodes
            .Where(n => !n.IsParking)
            .Select(n => (Node: n, Distance: Distance(n.EastM, n.NorthM, east, north)))
            .OrderBy(x => x.Distance)
            .Take(MaxEndNodeTries)
            .Where((x, i) => i == 0 || x.Distance <= MaxStationOffNetworkM)
            .Select(x => x.Node);
    }

    private static double LengthM(List<(double East, double North)> points)
    {
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1].East, points[i - 1].North, points[i].East, points[i].North);
        }

        return length;
    }

    private sealed record HoldInfo(TaxiNode Node, double Along, double Cross);

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

    /// <summary>So viele nahe Rollpunkte probiert <see cref="RouteBetween"/> am Ziel, bis einer erreichbar ist.</summary>
    public const int MaxEndNodeTries = 12;

    /// <summary>
    /// Weg zwischen zwei Punkten über das Rollweg-Netz: vom Punkt zum nächsten Rollpunkt, die Route, vom letzten Rollpunkt zum Ziel.
    /// <paramref name="fromParking"/>: am Anfang steht das Fahrzeug auf dieser Parkposition (Route ab ihr). Ist der nächste Rollpunkt am
    /// Ziel nicht erreichbar (z. B. ein Helipad abseits des Netzes, Test EDDL 02.11.), probiert es die nächsten bis
    /// <see cref="MaxEndNodeTries"/>; das letzte Stück fährt das Fahrzeug geradeaus. Am Anfang ebenso (<see cref="StartNodes"/>).
    /// Null = keine Route.
    /// </summary>
    public static List<(double East, double North)>? RouteBetween(TaxiGraph graph, double fromEast, double fromNorth, double toEast, double toNorth, TaxiNode? fromParking = null)
    {
        // Das letzte Stück fährt geradeaus: erst Rollpunkte, von denen aus es nicht durch ein Gebäude führt
        var ends = graph.Nodes
            .Where(n => !n.IsParking)
            .OrderBy(n => Distance(n.EastM, n.NorthM, toEast, toNorth))
            .Take(MaxEndNodeTries)
            .ToList();
        ends = [.. ends.Where(n => graph.Buildings == 0 || !graph.CrossesBuilding(n.EastM, n.NorthM, toEast, toNorth)).Concat(ends).Distinct()];
        foreach (var start in ClearFirst(graph, fromEast, fromNorth, StartNodes(graph, fromEast, fromNorth, fromParking)))
        {
            foreach (var end in ends)
            {
                var points = new List<(double East, double North)> { (fromEast, fromNorth) };
                if (start.Id != end.Id)
                {
                    if (graph.Route(start.Id, end.Id) is not { Count: >= 2 } nodes)
                    {
                        continue;
                    }

                    points.AddRange(nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
                }

                points.Add((toEast, toNorth));
                return points;
            }
        }

        return null;
    }

    /// <summary>
    /// Weg von einem Punkt zu einem Knoten (auch einer Parkposition): über den nächsten erreichbaren Rollpunkt (bis
    /// <see cref="MaxEndNodeTries"/> probiert). Null = keine Route.
    /// </summary>
    public static List<(double East, double North)>? RouteToNode(TaxiGraph graph, double fromEast, double fromNorth, TaxiNode target)
    {
        var starts = graph.Nodes
            .Where(n => !n.IsParking)
            .OrderBy(n => Distance(n.EastM, n.NorthM, fromEast, fromNorth))
            .Take(MaxEndNodeTries);
        foreach (var start in starts)
        {
            if (start.Id == target.Id)
            {
                return [(fromEast, fromNorth), (target.EastM, target.NorthM)];
            }

            if (graph.Route(start.Id, target.Id) is { Count: >= 2 } nodes)
            {
                var points = new List<(double East, double North)> { (fromEast, fromNorth) };
                points.AddRange(nodes.Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
                return points;
            }
        }

        return null;
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
        TaxiPath.Build(Simplify(points, SimplifyM), FireProfile.TurnRadiusM, maxMps, FireProfile.LateralMps2, FireProfile.MinTurnMps, CornerRadiusM);

    /// <summary>So weit darf ein Bogen höchstens innen an der Ecke vorbeischneiden (Rollwege sind gut 20 m breit).</summary>
    public const double MaxCornerCutM = 4;

    /// <summary>
    /// Kurvenradius je Knick: so groß, dass der Bogen höchstens <see cref="MaxCornerCutM"/> an der Ecke vorbeischneidet – flache Knicke
    /// fahren sich fast mit voller Geschwindigkeit, rechte Winkel eng und langsam (Test EDDL 02.11.: an jedem Knick 17 kt).
    /// </summary>
    public static double CornerRadiusM(double turnRad)
    {
        var excess = (1 / Math.Cos(Math.Min(turnRad, 3.0) / 2)) - 1;
        return excess <= 1e-6 ? 500 : Math.Clamp(MaxCornerCutM / excess, 8, 500);
    }

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

    /// <summary>Linienzug nach <paramref name="meters"/> Metern enden lassen (das Ende rückt auf den Weg); mindestens der Anfang bleibt.</summary>
    public static List<(double East, double North)> CutAt(IReadOnlyList<(double East, double North)> points, double meters)
    {
        var result = new List<(double East, double North)> { points[0] };
        var left = Math.Max(0, meters);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var length = Distance(a.East, a.North, b.East, b.North);
            if (length >= left)
            {
                var f = length < 1e-9 ? 0 : left / length;
                result.Add((a.East + ((b.East - a.East) * f), a.North + ((b.North - a.North) * f)));
                return result;
            }

            result.Add(b);
            left -= length;
        }

        return result;
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
