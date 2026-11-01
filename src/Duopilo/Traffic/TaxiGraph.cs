using System.Globalization;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Knoten des Rollweg-Netzes: Rollpunkt oder Parkposition, Lage in Metern östlich/nördlich des Bezugspunkts.</summary>
/// <param name="PointType">TAXI_POINT TYPE (1 normal, 2 Haltepunkt, 4 ILS-Haltepunkt, 5/6 ohne Markierung); bei Parkpositionen 0.</param>
public sealed record TaxiNode(int Id, bool IsParking, double EastM, double NorthM, double Latitude, double Longitude, int PointType, TaxiParking? Parking = null)
{
    /// <summary>Haltepunkt vor einer Bahn (auch ILS und ohne Markierung).</summary>
    public bool IsHoldShort => !IsParking && PointType is 2 or 4 or 5 or 6;
}

/// <summary>Parkposition mit lesbarer Bezeichnung (z. B. "A12", "P5"), Art, Kurs und Radius.</summary>
public sealed record TaxiParking(int Index, string Label, int Type, double HeadingDeg, double RadiusM)
{
    /// <summary>Art der Parkposition als Wort (gate_heavy, ramp_ga, cargo …).</summary>
    public string TypeName => TaxiGraph.ParkingTypeName(Type);
}

/// <summary>Rollweg-Abschnitt (in beide Richtungen befahrbar).</summary>
/// <param name="Type">TAXI_PATH TYPE: 1 Rollweg, 2 Bahn, 3 Parkposition, 4 Pfad.</param>
/// <param name="Runway">Bei Bahn-Abschnitten die Bahn, z. B. "05R".</param>
public sealed record TaxiEdge(int From, int To, double LengthM, int Type, string Name, double WidthM, string? Runway)
{
    public int Other(int node) => node == From ? To : From;
}

/// <summary>Haltepunkt mit der Bahn, zu der er gehört.</summary>
/// <param name="Runway">Bahn, z. B. "05R/23L".</param>
/// <param name="CenterlineM">Abstand zur Mittellinie.</param>
public sealed record TaxiHold(TaxiNode Node, string Runway, double CenterlineM, string Taxiway);

/// <summary>
/// Rollweg-Netz eines Flughafens aus der MSFS-Datenbank (<see cref="AirportTaxiData"/>): Rollpunkte und Parkpositionen als Knoten,
/// Rollweg-, Bahn- und Parkpositions-Abschnitte als Kanten (gesperrte, Fahrzeug-, Straßen- und Markierungs-Abschnitte nicht).
///
/// Die Lage kommt als BIAS_X/BIAS_Z in Metern vom Bezugspunkt des Flughafens. Welche Achse nach Osten und welche nach Norden zeigt,
/// sagt die Doku nicht eindeutig – das Netz probiert die Möglichkeiten und nimmt die, bei der die Bahn-Abschnitte auf den
/// Mittellinien der Bahnen liegen (<see cref="Mapping"/>, <see cref="MappingErrorM"/>).
/// </summary>
public sealed class TaxiGraph
{
    /// <summary>Rollen auf Bahnen kostet so viel mehr – Routen nehmen die Rollwege und kreuzen Bahnen nur.</summary>
    public const double RunwayCostFactor = 8;

    /// <summary>Haltepunkte gehören zur nächsten Bahn, wenn sie höchstens so weit von deren Mittellinie liegen.</summary>
    public const double MaxHoldCenterlineM = 400;

    private static readonly (string Name, Func<double, double, (double East, double North)> Map)[] Mappings =
    [
        ("x_east_z_north", (x, z) => (x, z)),
        ("z_east_x_north", (x, z) => (z, x)),
        ("x_east_z_south", (x, z) => (x, -z)),
        ("x_west_z_north", (x, z) => (-x, z)),
    ];

    private readonly List<TaxiNode> _nodes = [];
    private readonly List<TaxiEdge> _edges = [];
    private readonly List<List<TaxiEdge>> _adjacency = [];
    private readonly List<AirportRunway> _runways;
    private readonly HashSet<TaxiEdge> _blocked = [];
    private List<(double EastM, double NorthM)> _obstacles = [];

    private TaxiGraph(string icao, double latitude, double longitude, double elevationFt, IReadOnlyList<AirportRunway> runways)
    {
        Icao = icao;
        Latitude = latitude;
        Longitude = longitude;
        ElevationFt = elevationFt;
        _runways = [.. runways];
    }

    public string Icao { get; }

    /// <summary>Bezugspunkt des Flughafens.</summary>
    public double Latitude { get; }

    public double Longitude { get; }

    public double ElevationFt { get; }

    public IReadOnlyList<TaxiNode> Nodes => _nodes;

    public IReadOnlyList<TaxiEdge> Edges => _edges;

    public IEnumerable<TaxiParking> Parkings => _nodes.Where(n => n.Parking is not null).Select(n => n.Parking!);

    /// <summary>Gewählte Zuordnung von BIAS_X/BIAS_Z zu Ost/Nord.</summary>
    public string Mapping { get; private set; } = Mappings[0].Name;

    /// <summary>Mittlerer Abstand der Bahn-Abschnitte zu den Mittellinien mit der gewählten Zuordnung; null = keine Bahn-Abschnitte.</summary>
    public double? MappingErrorM { get; private set; }

    /// <summary>Abschnitte, die verworfen wurden (Index außerhalb der Listen).</summary>
    public int BrokenPaths { get; private set; }

    public IReadOnlyList<TaxiEdge> EdgesOf(int node) => _adjacency[node];

    /// <summary>Andere Flugzeuge am Boden (Meter östlich/nördlich), <see cref="SetObstacles"/>.</summary>
    public IReadOnlyList<(double EastM, double NorthM)> Obstacles => _obstacles;

    /// <summary>Abschnitte, die zu nah an einem anderen Flugzeug vorbeiführen.</summary>
    public int BlockedEdges => _blocked.Count;

    /// <summary>Mindestabstand der Abschnitte zu anderen Flugzeugen (<see cref="SetObstacles"/>).</summary>
    public double ClearanceM { get; private set; }

    /// <summary>
    /// Andere Flugzeuge am Boden (KI-Verkehr an den Gates, das eigene Flugzeug): Abschnitte, die näher als <paramref name="clearanceM"/>
    /// an einem vorbeiführen, sind für Routen gesperrt; besetzte Parkpositionen zeigt <see cref="IsOccupied"/>. Der Abstand gilt zum
    /// Bezugspunkt des anderen Flugzeugs – die Flügel reichen bei Verkehrsflugzeugen 15–30 m weit.
    /// </summary>
    public void SetObstacles(IEnumerable<(double Latitude, double Longitude)> positions, double clearanceM)
    {
        _obstacles = [.. positions.Select(p => ToLocal(p.Latitude, p.Longitude))];
        ClearanceM = clearanceM;
        _blocked.Clear();
        if (_obstacles.Count == 0)
        {
            return;
        }

        foreach (var edge in _edges)
        {
            var (a, b) = (_nodes[edge.From], _nodes[edge.To]);
            if (_obstacles.Any(o => SegmentDistance(o.EastM, o.NorthM, a.EastM, a.NorthM, b.EastM, b.NorthM) < clearanceM))
            {
                _blocked.Add(edge);
            }
        }
    }

    /// <summary>Ob ein Abschnitt zu nah an einem anderen Flugzeug vorbeiführt.</summary>
    public bool IsBlocked(TaxiEdge edge) => _blocked.Contains(edge);

    /// <summary>Ob ein anderes Flugzeug näher als <paramref name="radiusM"/> an einem Punkt steht.</summary>
    public bool IsOccupied(double eastM, double northM, double radiusM) =>
        _obstacles.Any(o => Math.Sqrt(((o.EastM - eastM) * (o.EastM - eastM)) + ((o.NorthM - northM) * (o.NorthM - northM))) < radiusM);

    /// <summary>Ob eine Parkposition belegt ist: ein anderes Flugzeug innerhalb ihres Radius (mindestens 10 m).</summary>
    public bool IsOccupied(TaxiNode parking) => IsOccupied(parking.EastM, parking.NorthM, Math.Max(10, parking.Parking?.RadiusM ?? 10));

    /// <summary>Kleinster Abstand eines Punkts zur Strecke A–B.</summary>
    public static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared > 0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0, 1) : 0;
        var (cx, cy) = (ax + (t * dx) - px, ay + (t * dy) - py);
        return Math.Sqrt((cx * cx) + (cy * cy));
    }

    /// <summary>Netz aus den Daten des Sims; <paramref name="runways"/> dienen der Zuordnung der Achsen und der Haltepunkte.</summary>
    public static TaxiGraph Build(AirportTaxiData data, IReadOnlyList<AirportRunway> runways)
    {
        var graph = new TaxiGraph(data.Icao, data.Latitude, data.Longitude, data.AltitudeM * RunwayEnd.FeetPerMeter, runways);
        var (mapping, error) = graph.ChooseMapping(data);
        graph.Mapping = mapping.Name;
        graph.MappingErrorM = error;
        graph.Fill(data, mapping.Map);
        return graph;
    }

    /// <summary>Meter östlich/nördlich des Bezugspunkts als Breite/Länge (WGS84-Gradlängen an der Breite des Flughafens).</summary>
    public (double Latitude, double Longitude) ToLatLon(double eastM, double northM)
    {
        var (meterPerDegLat, meterPerDegLon) = DegreeLengths(Latitude);
        return (Latitude + (northM / meterPerDegLat), Longitude + (eastM / meterPerDegLon));
    }

    /// <summary>Breite/Länge als Meter östlich/nördlich des Bezugspunkts.</summary>
    public (double EastM, double NorthM) ToLocal(double latitude, double longitude)
    {
        var (meterPerDegLat, meterPerDegLon) = DegreeLengths(Latitude);
        return ((longitude - Longitude) * meterPerDegLon, (latitude - Latitude) * meterPerDegLat);
    }

    /// <summary>Parkposition nach Bezeichnung ("A12", "a 12", "P5"), sonst null.</summary>
    public TaxiNode? FindParking(string label)
    {
        var wanted = Compact(label);
        return _nodes.FirstOrDefault(n => n.Parking is { } p && Compact(p.Label) == wanted);
    }

    /// <summary>Haltepunkte aller Bahnen mit Bahn, Abstand zur Mittellinie und dem Rollweg daran.</summary>
    public IReadOnlyList<TaxiHold> Holds()
    {
        var holds = new List<TaxiHold>();
        foreach (var node in _nodes.Where(n => n.IsHoldShort))
        {
            if (NearestRunway(node.EastM, node.NorthM) is { } nearest && nearest.CrossM <= MaxHoldCenterlineM)
            {
                holds.Add(new TaxiHold(node, nearest.Runway.Primary + "/" + nearest.Runway.Secondary, Math.Round(nearest.CrossM), TaxiwayAt(node.Id)));
            }
        }

        return holds;
    }

    /// <summary>
    /// Haltepunkte vor dem Start von <paramref name="end"/>: die der Bahn, nach der Entfernung zum Startpunkt sortiert (zuerst die nahe
    /// am Bahnanfang – ein Start über die volle Länge).
    /// </summary>
    public IReadOnlyList<TaxiHold> DepartureHolds(RunwayEnd end)
    {
        var designation = AirportRunway.Normalize(end.Designation);
        return [.. Holds()
            .Where(h => h.Runway.Split('/').Any(d => AirportRunway.Normalize(d) == designation))
            .Select(h => (Hold: h, Along: end.Offsets(h.Node.Latitude, h.Node.Longitude).AlongM))
            .Where(h => h.Along > -600 && h.Along < end.LengthM * 0.5)
            .OrderBy(h => Math.Abs(h.Along))
            .Select(h => h.Hold)];
    }

    /// <summary>
    /// Kürzeste Route (Dijkstra) von <paramref name="from"/> nach <paramref name="to"/>: Bahn-Abschnitte kosten
    /// <see cref="RunwayCostFactor"/>-mal mehr, durch Parkpositionen geht es nicht hindurch (nur am Anfang und am Ende), an anderen
    /// Flugzeugen nicht zu nah vorbei (<see cref="SetObstacles"/>). Null = keine.
    /// </summary>
    public IReadOnlyList<int>? Route(int from, int to)
    {
        if (from == to)
        {
            return [from];
        }

        var cost = new double[_nodes.Count];
        var previous = new int[_nodes.Count];
        Array.Fill(cost, double.PositiveInfinity);
        Array.Fill(previous, -1);
        cost[from] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(from, 0);
        while (queue.TryDequeue(out var node, out var c))
        {
            if (c > cost[node])
            {
                continue;
            }

            if (node == to)
            {
                break;
            }

            if (_nodes[node].IsParking && node != from)
            {
                continue;
            }

            foreach (var edge in _adjacency[node])
            {
                if (_blocked.Contains(edge))
                {
                    continue;
                }

                var next = edge.Other(node);
                if (_nodes[next].IsParking && next != to)
                {
                    continue;
                }

                var step = edge.LengthM * (edge.Type == 2 ? RunwayCostFactor : 1);
                if (cost[node] + step < cost[next])
                {
                    cost[next] = cost[node] + step;
                    previous[next] = node;
                    queue.Enqueue(next, cost[next]);
                }
            }
        }

        if (previous[to] < 0)
        {
            return null;
        }

        var route = new List<int> { to };
        for (var n = previous[to]; n >= 0; n = previous[n])
        {
            route.Add(n);
        }

        route.Reverse();
        return route;
    }

    /// <summary>Rollwege entlang einer Route, ohne Wiederholungen und leere Namen ("A, A7, B").</summary>
    public IReadOnlyList<string> TaxiwayNames(IReadOnlyList<int> route)
    {
        var names = new List<string>();
        for (var i = 1; i < route.Count; i++)
        {
            var edge = Edge(route[i - 1], route[i]);
            var name = edge?.Type == 2 ? "RWY " + edge.Runway : edge?.Name;
            if (!string.IsNullOrWhiteSpace(name) && (names.Count == 0 || names[^1] != name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Abschnitt zwischen zwei Knoten, sonst null.</summary>
    public TaxiEdge? Edge(int a, int b) => _adjacency[a].FirstOrDefault(e => e.Other(a) == b);

    /// <summary>Kosten einer Route wie bei <see cref="Route"/>: Meter, Bahn-Abschnitte <see cref="RunwayCostFactor"/>-fach.</summary>
    public double CostM(IReadOnlyList<int> route)
    {
        var cost = 0.0;
        for (var i = 1; i < route.Count; i++)
        {
            var edge = Edge(route[i - 1], route[i]);
            var length = Distance(_nodes[route[i - 1]], _nodes[route[i]]);
            cost += edge?.Type == 2 ? length * RunwayCostFactor : length;
        }

        return cost;
    }

    /// <summary>Länge einer Route in Metern.</summary>
    public double LengthM(IReadOnlyList<int> route)
    {
        var length = 0.0;
        for (var i = 1; i < route.Count; i++)
        {
            length += Distance(_nodes[route[i - 1]], _nodes[route[i]]);
        }

        return length;
    }

    /// <summary>Art der Parkposition (TAXI_PARKING TYPE) als Wort.</summary>
    public static string ParkingTypeName(int type) => type switch
    {
        1 => "ramp_ga",
        2 => "ramp_ga_small",
        3 => "ramp_ga_medium",
        4 => "ramp_ga_large",
        5 => "ramp_cargo",
        6 => "ramp_mil_cargo",
        7 => "ramp_mil_combat",
        8 => "gate_small",
        9 => "gate_medium",
        10 => "gate_heavy",
        11 => "dock_ga",
        12 => "fuel",
        13 => "vehicle",
        14 => "ramp_ga_extra",
        15 => "gate_extra",
        _ => "none",
    };

    /// <summary>
    /// Bezeichnung einer Parkposition aus NAME, NUMBER und SUFFIX: GATE_A + 12 → "A12", GATE + 3 → "G3", PARKING + 5 → "P5",
    /// N_PARKING + 2 → "NP2", DOCK + 1 → "D1"; ein Buchstaben-Suffix kommt hinten dran ("A12B").
    /// </summary>
    public static string ParkingLabel(int name, uint number, int suffix)
    {
        var prefix = name switch
        {
            1 => "P",
            >= 2 and <= 9 => new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[name - 2] + "P",
            10 => "G",
            11 => "D",
            >= 12 and <= 37 => ((char)('A' + (name - 12))).ToString(),
            _ => "",
        };
        var tail = suffix is >= 12 and <= 37 ? ((char)('A' + (suffix - 12))).ToString() : "";
        return prefix + number.ToString(CultureInfo.InvariantCulture) + tail;
    }

    /// <summary>Die Bahnen des Flughafens.</summary>
    public IReadOnlyList<AirportRunway> Runways => _runways;

    /// <summary>Bahn, auf der ein Punkt liegt (höchstens halbe Breite plus 3 m neben der Mittellinie, innerhalb der Länge plus 20 m), sonst null.</summary>
    public AirportRunway? RunwayAt(double eastM, double northM)
    {
        foreach (var runway in _runways)
        {
            var (along, cross) = RunwayOffsets(runway, eastM, northM);
            if (Math.Abs(along) <= (runway.LengthM / 2) + 20 && Math.Abs(cross) <= (Math.Max(runway.WidthM, 20) / 2) + 3)
            {
                return runway;
            }
        }

        return null;
    }

    /// <summary>Nächste Bahn zu einem Punkt mit Abstand zur Mittellinie (innerhalb der Länge plus 300 m), sonst null.</summary>
    public (AirportRunway Runway, double CrossM)? NearestRunway(double eastM, double northM)
    {
        (AirportRunway, double)? best = null;
        foreach (var runway in _runways)
        {
            var (along, cross) = RunwayOffsets(runway, eastM, northM);
            if (Math.Abs(along) > (runway.LengthM / 2) + 300)
            {
                continue;
            }

            if (best is null || Math.Abs(cross) < best.Value.Item2)
            {
                best = (runway, Math.Abs(cross));
            }
        }

        return best;
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static double Distance(TaxiNode a, TaxiNode b) => Math.Sqrt(((a.EastM - b.EastM) * (a.EastM - b.EastM)) + ((a.NorthM - b.NorthM) * (a.NorthM - b.NorthM)));

    /// <summary>Meter je Grad Breite und Länge (WGS84) an einer Breite.</summary>
    private static (double Lat, double Lon) DegreeLengths(double latitudeDeg)
    {
        var phi = latitudeDeg * Math.PI / 180;
        var lat = 111_132.92 - (559.82 * Math.Cos(2 * phi)) + (1.175 * Math.Cos(4 * phi));
        var lon = (111_412.84 * Math.Cos(phi)) - (93.5 * Math.Cos(3 * phi));
        return (lat, Math.Max(1, lon));
    }

    /// <summary>Lage zur Mittellinie einer Bahn: entlang ab der Bahnmitte in Richtung des primären Endes, quer.</summary>
    private (double AlongM, double CrossM) RunwayOffsets(AirportRunway runway, double eastM, double northM)
    {
        var (centerEast, centerNorth) = ToLocal(runway.Latitude, runway.Longitude);
        var heading = runway.HeadingDeg * Math.PI / 180;
        var (dirEast, dirNorth) = (Math.Sin(heading), Math.Cos(heading));
        var (dx, dy) = (eastM - centerEast, northM - centerNorth);
        return ((dx * dirEast) + (dy * dirNorth), (dx * dirNorth) - (dy * dirEast));
    }

    /// <summary>Die Zuordnung, bei der die Bahn-Abschnitte am besten auf den Mittellinien liegen.</summary>
    private ((string Name, Func<double, double, (double East, double North)> Map) Mapping, double? ErrorM) ChooseMapping(AirportTaxiData data)
    {
        var onRunways = data.Paths
            .Where(p => p.Type == 2)
            .SelectMany(p => new[] { p.Start, p.End })
            .Where(i => i >= 0 && i < data.Points.Count)
            .Distinct()
            .Select(i => data.Points[i])
            .ToList();
        if (onRunways.Count == 0 || _runways.Count == 0)
        {
            return (Mappings[0], null);
        }

        var scored = Mappings
            .Select(m => (Mapping: m, Error: onRunways.Average(p =>
            {
                var (east, north) = m.Map(p.BiasX, p.BiasZ);
                return NearestRunway(east, north)?.CrossM ?? 1000;
            })))
            .OrderBy(s => s.Error)
            .ToList();
        return (scored[0].Mapping, Math.Round(scored[0].Error, 1));
    }

    private void Fill(AirportTaxiData data, Func<double, double, (double East, double North)> map)
    {
        foreach (var point in data.Points)
        {
            AddNode(false, map(point.BiasX, point.BiasZ), point.Type, null);
        }

        for (var i = 0; i < data.Parkings.Count; i++)
        {
            var p = data.Parkings[i];
            var parking = new TaxiParking(i, ParkingLabel(p.Name, p.Number, p.Suffix), p.Type, p.HeadingDeg, p.RadiusM);
            AddNode(true, map(p.BiasX, p.BiasZ), 0, parking);
        }

        var points = data.Points.Count;
        foreach (var path in data.Paths)
        {
            if (path.Type is not (1 or 2 or 3 or 4))
            {
                continue;
            }

            var from = path.Start;
            var to = path.Type == 3 ? points + path.End : path.End;
            if (from < 0 || from >= points || to < 0 || to >= _nodes.Count || from == to)
            {
                BrokenPaths++;
                continue;
            }

            var name = path.NameIndex < data.Names.Count ? data.Names[(int)path.NameIndex] : "";
            var runway = path.Type == 2 && path.RunwayNumber > 0 ? AirportRunway.Designation(path.RunwayNumber, path.RunwayDesignator) : null;
            var edge = new TaxiEdge(from, to, Distance(_nodes[from], _nodes[to]), path.Type, name.Trim(), path.WidthM, runway);
            _edges.Add(edge);
            _adjacency[from].Add(edge);
            _adjacency[to].Add(edge);
        }
    }

    private void AddNode(bool isParking, (double East, double North) local, int pointType, TaxiParking? parking)
    {
        var (lat, lon) = ToLatLon(local.East, local.North);
        _nodes.Add(new TaxiNode(_nodes.Count, isParking, local.East, local.North, lat, lon, pointType, parking));
        _adjacency.Add([]);
    }

    /// <summary>Name des Rollwegs an einem Knoten (der erste Name eines Rollweg-Abschnitts daran).</summary>
    private string TaxiwayAt(int node) =>
        _adjacency[node].Where(e => e.Type is 1 or 4).Select(e => e.Name).FirstOrDefault(n => n.Length > 0) ?? "";
}
