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
/// <param name="Type">TAXI_PATH TYPE: 1 Rollweg, 2 Bahn, 3 Parkposition, 4 Pfad, 6/7 Betriebsstraße.</param>
/// <param name="Runway">Bei Bahn-Abschnitten die Bahn, z. B. "05R".</param>
/// <param name="RunwayShare">Anteil des Abschnitts auf einer Bahn (Betriebsstraßen, die eine Bahn kreuzen): kostet dort wie eine Bahn.</param>
public sealed record TaxiEdge(int From, int To, double LengthM, int Type, string Name, double WidthM, string? Runway, double RunwayShare = 0)
{
    public int Other(int node) => node == From ? To : From;
}

/// <summary>Ergebnis von <see cref="TaxiGraph.AddRoads"/>.</summary>
/// <param name="Added">Ob die Straßen ins Netz kamen (nicht, wenn zu viele auf Parkpositionen liegen).</param>
/// <param name="Sections">Hinzugefügte Straßenabschnitte.</param>
/// <param name="LengthM">Ihre Länge zusammen.</param>
/// <param name="Links">Verbindungen zu Rollpunkten.</param>
/// <param name="Dropped">Abschnitte ohne Verbindung zum Netz (Landseite, Parkplätze), weggelassen.</param>
/// <param name="Cut">Abschnitte außerhalb des Flughafens, über einen Zaun oder an einer Schranke, weggelassen.</param>
/// <param name="ParkingShare">Anteil der Straßenmeter mitten auf Parkpositionen.</param>
public sealed record RoadMerge(bool Added, int Sections, double LengthM, int Links, int Dropped, int Cut, double ParkingShare);

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

    /// <summary>
    /// Kosten der Betriebsstraßen (TAXI_PATH VEHICLE/ROAD) im Verhältnis zu Rollwegen: unter 1 bevorzugt sie (Feuerwehr: Fahrzeuge
    /// fahren über die Straßen, wo es eine gibt, und nur dort über Rollwege, wo es kürzer ist). Gilt nur in Netzen mit Fahrwegen.
    /// </summary>
    public double RoadCostFactor { get; set; } = 1;

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
    private readonly List<(double EastM, double NorthM)> _reserved = [];

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

    /// <summary>Geladene Fahrweg-Abschnitte der Bodenfahrzeuge (nur mit <c>vehicleRoads</c>).</summary>
    public int VehicleRoads { get; private set; }

    /// <summary>
    /// Abschnitte je TAXI_PATH-Typ in den Daten der Szenerie, geladen oder nicht (1 Rollweg, 2 Bahn, 3 Parkposition, 4 Weg,
    /// 5 gesperrt, 6 Fahrzeug, 7 Straße, 8 Markierung) – zeigt, ob eine Szenerie überhaupt Fahrwege für Bodenfahrzeuge hat.
    /// </summary>
    public IReadOnlyDictionary<int, int> PathTypes { get; private set; } = new Dictionary<int, int>();

    /// <summary>Längstes Fahrweg-Stück, das als echt gilt.</summary>
    public const double MaxVehicleRoadSegmentM = 400;

    /// <summary>Betriebsstraßen-Abschnitte aus einer anderen Quelle (<see cref="AddRoads"/>, OpenStreetMap).</summary>
    public int AddedRoads { get; private set; }

    /// <summary>Eine hinzugefügte Straße hängt am Netz, wo einer ihrer Punkte so nah an einem Rollpunkt liegt.</summary>
    public const double RoadLinkM = 20;

    /// <summary>Wie <see cref="RoadLinkM"/> für Straßenenden (sie enden oft am Rand des Vorfelds).</summary>
    public const double RoadEndLinkM = 50;

    /// <summary>Längeres Straßenstück aus einer anderen Quelle gilt als Fehler.</summary>
    private const double MaxAddedRoadSegmentM = 3000;

    /// <summary>Parkpositionen, auf denen keine Straße liegen sollte: der innere Teil des Radius, mindestens 5 m.</summary>
    private const double ParkingCoreShare = 0.35;

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
        RefreshBlocked();
    }

    /// <summary>Gesperrte Abschnitte neu bestimmen (nach neuen Hindernissen oder neuen Abschnitten).</summary>
    private void RefreshBlocked()
    {
        _blocked.Clear();
        if (_obstacles.Count == 0)
        {
            return;
        }

        foreach (var edge in _edges)
        {
            var (a, b) = (_nodes[edge.From], _nodes[edge.To]);
            if (_obstacles.Any(o => SegmentDistance(o.EastM, o.NorthM, a.EastM, a.NorthM, b.EastM, b.NorthM) < ClearanceM))
            {
                _blocked.Add(edge);
            }
        }
    }

    /// <summary>
    /// Hindernisse näher als <paramref name="radiusM"/> an einem Punkt entfernen, etwa am Einsatzort der Feuerwehr: Das Flugzeug dort
    /// (oder die Nachbarn an der Parkposition) ist das Ziel, kein Hindernis. Ohne Radius: alle.
    /// </summary>
    public void ClearObstacles(double eastM = 0, double northM = 0, double radiusM = double.PositiveInfinity)
    {
        var keep = _obstacles
            .Where(o => Math.Sqrt(Math.Pow(o.EastM - eastM, 2) + Math.Pow(o.NorthM - northM, 2)) > radiusM)
            .Select(o => ToLatLon(o.EastM, o.NorthM))
            .ToList();
        SetObstacles(keep, ClearanceM);
    }

    /// <summary>Ob ein Abschnitt zu nah an einem anderen Flugzeug vorbeiführt.</summary>
    public bool IsBlocked(TaxiEdge edge) => _blocked.Contains(edge);

    // Gebäude (Umrisse in Metern, mit Rahmen zum schnellen Aussortieren), siehe SetBuildings
    private List<(double MinE, double MinN, double MaxE, double MaxN, (double East, double North)[] Points)> _buildings = [];

    /// <summary>Anzahl der Gebäude (<see cref="SetBuildings"/>).</summary>
    public int Buildings => _buildings.Count;

    /// <summary>
    /// Gebäude als Hindernis für gerade Fahrten außerhalb des Netzes (aus OSM, Feuerwehr): Umrisse in Metern. Die Abschnitte des Netzes
    /// selbst bleiben frei – Betriebsstraßen führen unter Fluggastbrücken durch, und Szenerie und OSM passen nicht überall genau.
    /// </summary>
    public void SetBuildings(IEnumerable<IReadOnlyList<(double East, double North)>> polygons) =>
        _buildings = [.. polygons
            .Where(p => p.Count >= 3)
            .Select(p => (p.Min(q => q.East), p.Min(q => q.North), p.Max(q => q.East), p.Max(q => q.North), p.ToArray()))];

    /// <summary>
    /// Wie gut die Gebäude zur Szenerie passen: Anteil der Parkpositionen und der Rollweg-Meter (Rollwege, Bahnen, Wege – ohne
    /// Betriebsstraßen), die in einem Gebäude liegen. In der passenden Szenerie ist beides fast null; liegt ein Gebäude über den
    /// Rollwegen, passt OSM nicht zu dieser Szenerie (andere Szenerie, anderer Ausbaustand).
    /// </summary>
    public (double ParkingShare, double TaxiShare) BuildingOverlap()
    {
        if (_buildings.Count == 0)
        {
            return (0, 0);
        }

        var parkings = _nodes.Where(n => n.IsParking).ToList();
        var parkingShare = parkings.Count == 0 ? 0 : parkings.Count(n => InsideAnyBuilding(n.EastM, n.NorthM)) / (double)parkings.Count;
        var (samples, inside) = (0, 0);
        foreach (var edge in _edges.Where(e => e.Type is 1 or 2 or 4))
        {
            var (a, b) = (_nodes[edge.From], _nodes[edge.To]);
            var steps = Math.Max(1, (int)(edge.LengthM / 10));
            for (var i = 0; i < steps; i++)
            {
                var f = (i + 0.5) / steps;
                samples++;
                if (InsideAnyBuilding(a.EastM + ((b.EastM - a.EastM) * f), a.NorthM + ((b.NorthM - a.NorthM) * f)))
                {
                    inside++;
                }
            }
        }

        return (parkingShare, samples == 0 ? 0 : inside / (double)samples);
    }

    private bool InsideAnyBuilding(double x, double y) =>
        _buildings.Any(b => x >= b.MinE && x <= b.MaxE && y >= b.MinN && y <= b.MaxN && Inside(b.Points, x, y));

    /// <summary>Ob die gerade Strecke von a nach b durch ein Gebäude führt (Kante geschnitten oder ein Ende darin).</summary>
    public bool CrossesBuilding(double ax, double ay, double bx, double by) => BuildingHit(ax, ay, bx, by) is not null;

    /// <summary>Strecke entlang des Linienzugs bis zum ersten Gebäude (Meter), null = frei.</summary>
    public double? FirstBuildingHitM(IReadOnlyList<(double East, double North)> path)
    {
        var travelled = 0.0;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (a, b) = (path[i], path[i + 1]);
            var length = Math.Sqrt(((b.East - a.East) * (b.East - a.East)) + ((b.North - a.North) * (b.North - a.North)));
            if (BuildingHit(a.East, a.North, b.East, b.North) is { } t)
            {
                return travelled + (t * length);
            }

            travelled += length;
        }

        return null;
    }

    /// <summary>Kleinster Anteil t (0–1) entlang a→b, an dem die Strecke ein Gebäude trifft; null = frei.</summary>
    private double? BuildingHit(double ax, double ay, double bx, double by)
    {
        double? first = null;
        var (minE, maxE, minN, maxN) = (Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));
        foreach (var (bMinE, bMinN, bMaxE, bMaxN, points) in _buildings)
        {
            if (bMaxE < minE || bMinE > maxE || bMaxN < minN || bMinN > maxN)
            {
                continue;
            }

            if (Inside(points, ax, ay))
            {
                return 0;
            }

            for (var i = 0; i + 1 < points.Length; i++)
            {
                if (SegmentIntersection(ax, ay, bx, by, points[i].East, points[i].North, points[i + 1].East, points[i + 1].North) is { } t && (first is null || t < first))
                {
                    first = t;
                }
            }

            // Nicht geschlossene Umrisse: auch die letzte Kante
            var (l, f) = (points[^1], points[0]);
            if ((l.East != f.East || l.North != f.North) && SegmentIntersection(ax, ay, bx, by, l.East, l.North, f.East, f.North) is { } u && (first is null || u < first))
            {
                first = u;
            }
        }

        return first;
    }

    /// <summary>Punkt im Umriss (Strahlverfahren).</summary>
    private static bool Inside((double East, double North)[] polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Schnitt der Strecken p→q und r→s: Anteil t entlang p→q, sonst null.</summary>
    private static double? SegmentIntersection(double px, double py, double qx, double qy, double rx, double ry, double sx, double sy)
    {
        var (dx, dy, ex, ey) = (qx - px, qy - py, sx - rx, sy - ry);
        var denominator = (dx * ey) - (dy * ex);
        if (Math.Abs(denominator) < 1e-12)
        {
            return null;
        }

        var t = (((rx - px) * ey) - ((ry - py) * ex)) / denominator;
        var u = (((rx - px) * dy) - ((ry - py) * dx)) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? t : null;
    }

    /// <summary>Ob ein anderes Flugzeug näher als <paramref name="radiusM"/> an einem Punkt steht.</summary>
    /// <summary>
    /// Parkpositionen anderer eigener Testflugzeuge (Start oder Ziel) als belegt merken – ohne Rollwege zu sperren; sie rollen ja selbst
    /// und halten Abstand (<see cref="TaxiTraffic"/>).
    /// </summary>
    public void Reserve(IEnumerable<(double Latitude, double Longitude)> positions)
    {
        _reserved.AddRange(positions.Select(p => ToLocal(p.Latitude, p.Longitude)));
    }

    public bool IsOccupied(double eastM, double northM, double radiusM) =>
        _reserved.Any(p => Math.Sqrt(((p.EastM - eastM) * (p.EastM - eastM)) + ((p.NorthM - northM) * (p.NorthM - northM))) < radiusM) ||
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

    /// <summary>
    /// Netz aus den Daten des Sims; <paramref name="runways"/> dienen der Zuordnung der Achsen und der Haltepunkte.
    /// <paramref name="vehicleRoads"/>: auch die Fahrwege der Bodenfahrzeuge (TAXI_PATH VEHICLE und ROAD) – nur für Duopilos eigene
    /// Fahrzeuge (Feuerwehr), nie für Flugzeuge.
    /// </summary>
    public static TaxiGraph Build(AirportTaxiData data, IReadOnlyList<AirportRunway> runways, bool vehicleRoads = false)
    {
        var graph = new TaxiGraph(data.Icao, data.Latitude, data.Longitude, data.AltitudeM * RunwayEnd.FeetPerMeter, runways);
        var (mapping, error) = graph.ChooseMapping(data);
        graph.Mapping = mapping.Name;
        graph.MappingErrorM = error;
        graph.Fill(data, mapping.Map, vehicleRoads);
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

    /// <summary>Parkposition nach Bezeichnung ("A12", "a 12", "P5"; "A01" findet A1), sonst null.</summary>
    public TaxiNode? FindParking(string label)
    {
        var wanted = Compact(label);
        return _nodes.FirstOrDefault(n => n.Parking is { } p && Compact(p.Label) == wanted)
            ?? _nodes.FirstOrDefault(n => n.Parking is { } p && WithoutLeadingZeros(Compact(p.Label)) == WithoutLeadingZeros(wanted));
    }

    /// <summary>Führende Nullen in Zahlen weg: "A01" → "A1", "V061A" → "V61A" (aber "A10" bleibt).</summary>
    private static string WithoutLeadingZeros(string label) =>
        System.Text.RegularExpressions.Regex.Replace(label, "(?<![0-9])0+(?=[0-9])", "");

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

                var step = edge.LengthM * Factor(edge);
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

    /// <summary>TAXI_PATH-Typ als Wort für Status und Log.</summary>
    public static string PathTypeName(int type) => type switch
    {
        1 => "taxi",
        2 => "runway",
        3 => "parking",
        4 => "path",
        5 => "closed",
        6 => "vehicle",
        7 => "road",
        8 => "painted_line",
        _ => "type" + type.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Kostenfaktor eines Abschnitts: Bahnen <see cref="RunwayCostFactor"/>, Betriebsstraßen <see cref="RoadCostFactor"/>, sonst 1;
    /// kreuzt eine Straße eine Bahn, kostet ihr Anteil auf der Bahn wie eine Bahn.
    /// </summary>
    private double Factor(TaxiEdge? edge)
    {
        if (edge is null)
        {
            return 1;
        }

        var factor = edge.Type switch
        {
            2 => RunwayCostFactor,
            6 or 7 => RoadCostFactor,
            _ => 1,
        };
        return edge.RunwayShare > 0 ? (factor * (1 - edge.RunwayShare)) + (RunwayCostFactor * edge.RunwayShare) : factor;
    }

    /// <summary>Kosten einer Route wie bei <see cref="Route"/>: Meter, Bahn-Abschnitte <see cref="RunwayCostFactor"/>-fach.</summary>
    public double CostM(IReadOnlyList<int> route)
    {
        var cost = 0.0;
        for (var i = 1; i < route.Count; i++)
        {
            var edge = Edge(route[i - 1], route[i]);
            var length = Distance(_nodes[route[i - 1]], _nodes[route[i]]);
            cost += length * Factor(edge);
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

    private void Fill(AirportTaxiData data, Func<double, double, (double East, double North)> map, bool vehicleRoads)
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
        PathTypes = data.Paths.GroupBy(p => p.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
        foreach (var path in data.Paths)
        {
            var road = path.Type is 6 or 7;
            if (path.Type is not (1 or 2 or 3 or 4) && !(vehicleRoads && road))
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

            // Ein Fahrweg-Stück über Hunderte Meter wäre ein falsch gelesener Index (Ende auf einer Parkposition statt einem Rollpunkt)
            if (road && edge.LengthM > MaxVehicleRoadSegmentM)
            {
                BrokenPaths++;
                continue;
            }

            if (road)
            {
                VehicleRoads++;
                edge = edge with { RunwayShare = RunwayShareOf(_nodes[from], _nodes[to]) };
            }

            AddEdge(edge);
        }
    }

    private void AddEdge(TaxiEdge edge)
    {
        _edges.Add(edge);
        _adjacency[edge.From].Add(edge);
        _adjacency[edge.To].Add(edge);
    }

    /// <summary>
    /// Betriebsstraßen aus einer anderen Quelle ins Netz (OpenStreetMap, für die Feuerwehr, wenn die Szenerie keine Fahrwege hat):
    /// Linienzüge in Metern; gleiche Punkte verschiedener Linien werden Kreuzungen. An <paramref name="cuts"/> (Schranken und Tore)
    /// endet eine Straße – dort geht es meist von der Luft- auf die Landseite. Eine Straße hängt am Netz, wo einer ihrer Punkte höchstens
    /// <see cref="RoadLinkM"/> (Straßenende <see cref="RoadEndLinkM"/>) neben einem Rollpunkt liegt, der nicht nur an einer Bahn hängt,
    /// und die Verbindung durch kein Gebäude und keinen Zaun führt. Straßenteile ohne eine solche Verbindung fallen weg (Landseite,
    /// Parkplätze). Die Landseite trennen außerdem: <paramref name="fences"/> (Zäune und Mauern: Abschnitte darüber fallen weg) und
    /// <paramref name="boundary"/> (Flughafen-Umriss: Abschnitte, deren Mitte draußen liegt, fallen weg). Liegen mehr als
    /// <paramref name="maxParkingShare"/> der Straßenmeter mitten auf Parkpositionen, passt die Quelle nicht zur Szenerie (anderer
    /// Ausbaustand, verschoben): dann kommt nichts dazu. Abschnitte und Verbindungen sind Straßen (Typ 7).
    /// </summary>
    public RoadMerge AddRoads(
        IEnumerable<IReadOnlyList<(double East, double North)>> lines,
        IEnumerable<(double East, double North)> cuts,
        double maxParkingShare,
        IEnumerable<IReadOnlyList<(double East, double North)>>? fences = null,
        IEnumerable<IReadOnlyList<(double East, double North)>>? boundary = null)
    {
        var walls = Walls(fences ?? []);
        var areas = (boundary ?? []).Where(b => b.Count >= 4).Select(b => b.ToArray()).ToList();
        bool Outside(double x, double y) => areas.Count > 0 && !areas.Any(a => Inside(a, x, y));
        var cut = 0;

        // Punkte und Abschnitte; an Schranken, Zäunen und am Flughafen-Umriss getrennt
        var cutKeys = cuts.Select(c => VertexKey(c.East, c.North)).ToHashSet();
        var vertices = new List<(double East, double North)>();
        var index = new Dictionary<(long, long), int>();
        var segments = new HashSet<(int A, int B)>();
        int Vertex((double East, double North) p)
        {
            var key = VertexKey(p.East, p.North);
            if (!index.TryGetValue(key, out var i))
            {
                i = vertices.Count;
                vertices.Add(p);
                index[key] = i;
            }

            return i;
        }

        static bool Far((double East, double North) p) => Math.Abs(p.East) > 6000 || Math.Abs(p.North) > 6000;

        foreach (var line in lines)
        {
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var (a, b) = (line[i], line[i + 1]);
                var length = Math.Sqrt(((b.East - a.East) * (b.East - a.East)) + ((b.North - a.North) * (b.North - a.North)));
                if (Far(a) || Far(b) || length < 0.1 || length > MaxAddedRoadSegmentM)
                {
                    continue;
                }

                if (cutKeys.Contains(VertexKey(a.East, a.North)) || cutKeys.Contains(VertexKey(b.East, b.North)))
                {
                    cut++;
                    continue;
                }

                if (Outside((a.East + b.East) / 2, (a.North + b.North) / 2) || CrossesWall(walls, a.East, a.North, b.East, b.North))
                {
                    cut++;
                    continue;
                }

                var (va, vb) = (Vertex(a), Vertex(b));
                segments.Add(va < vb ? (va, vb) : (vb, va));
            }
        }

        // Verbindungen zum Netz
        var degree = new int[vertices.Count];
        foreach (var (a, b) in segments)
        {
            degree[a]++;
            degree[b]++;
        }

        var anchors = _nodes.Where(n => !n.IsParking && _adjacency[n.Id].Any(e => e.Type != 2)).ToList();
        var links = new List<(int Vertex, int Node, double LengthM)>();
        for (var v = 0; v < vertices.Count && anchors.Count > 0; v++)
        {
            var (e, n) = vertices[v];
            var nearest = anchors.MinBy(a => ((a.EastM - e) * (a.EastM - e)) + ((a.NorthM - n) * (a.NorthM - n)))!;
            var distance = Math.Sqrt(((nearest.EastM - e) * (nearest.EastM - e)) + ((nearest.NorthM - n) * (nearest.NorthM - n)));
            if ((distance <= RoadLinkM || (degree[v] == 1 && distance <= RoadEndLinkM)) && !CrossesBuilding(e, n, nearest.EastM, nearest.NorthM) &&
                !CrossesWall(walls, e, n, nearest.EastM, nearest.NorthM))
            {
                links.Add((v, nearest.Id, distance));
            }
        }

        // Nur Straßenteile mit Verbindung
        var parent = Enumerable.Range(0, vertices.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        foreach (var (a, b) in segments)
        {
            parent[Find(a)] = Find(b);
        }

        var linked = links.Select(l => Find(l.Vertex)).ToHashSet();
        var kept = segments.Where(s => linked.Contains(Find(s.A))).ToList();
        var dropped = segments.Count - kept.Count;

        // Passt es zur Szenerie? Straßen mitten über Parkpositionen (ohne Fahrzeugstellplätze) sprechen dagegen
        var cores = _nodes
            .Where(p => p.Parking is { Type: not 13 })
            .Select(p => (p.EastM, p.NorthM, R: Math.Max(5, p.Parking!.RadiusM * ParkingCoreShare)))
            .ToList();
        var (samples, onParking) = (0, 0);
        foreach (var (a, b) in kept)
        {
            var (pa, pb) = (vertices[a], vertices[b]);
            var steps = Math.Max(1, (int)(Math.Sqrt(((pb.East - pa.East) * (pb.East - pa.East)) + ((pb.North - pa.North) * (pb.North - pa.North))) / 10));
            for (var i = 0; i < steps; i++)
            {
                var f = (i + 0.5) / steps;
                var (x, y) = (pa.East + ((pb.East - pa.East) * f), pa.North + ((pb.North - pa.North) * f));
                samples++;
                if (cores.Any(c => ((c.EastM - x) * (c.EastM - x)) + ((c.NorthM - y) * (c.NorthM - y)) < c.R * c.R))
                {
                    onParking++;
                }
            }
        }

        var parkingShare = samples == 0 ? 0 : onParking / (double)samples;
        if (parkingShare > maxParkingShare)
        {
            return new RoadMerge(false, 0, 0, 0, dropped, cut, parkingShare);
        }

        // Ins Netz
        var nodeOf = new Dictionary<int, int>();
        int NodeOf(int v)
        {
            if (!nodeOf.TryGetValue(v, out var id))
            {
                id = _nodes.Count;
                AddNode(false, vertices[v], 1, null);
                nodeOf[v] = id;
            }

            return id;
        }

        var total = 0.0;
        foreach (var (a, b) in kept)
        {
            var (na, nb) = (NodeOf(a), NodeOf(b));
            var edge = new TaxiEdge(na, nb, Distance(_nodes[na], _nodes[nb]), 7, "", 6, null, RunwayShareOf(_nodes[na], _nodes[nb]));
            AddEdge(edge);
            total += edge.LengthM;
        }

        foreach (var (v, node, length) in links)
        {
            AddEdge(new TaxiEdge(NodeOf(v), node, length, 7, "", 6, null));
        }

        AddedRoads += kept.Count;
        RefreshBlocked();
        return new RoadMerge(true, kept.Count, total, links.Count, dropped, cut, parkingShare);
    }

    /// <summary>Zaun-Strecken mit Rahmen (schnelles Aussortieren).</summary>
    private static List<(double MinE, double MinN, double MaxE, double MaxN, double Ax, double Ay, double Bx, double By)> Walls(
        IEnumerable<IReadOnlyList<(double East, double North)>> fences)
    {
        var walls = new List<(double, double, double, double, double, double, double, double)>();
        foreach (var fence in fences)
        {
            for (var i = 0; i + 1 < fence.Count; i++)
            {
                var (a, b) = (fence[i], fence[i + 1]);
                walls.Add((Math.Min(a.East, b.East), Math.Min(a.North, b.North), Math.Max(a.East, b.East), Math.Max(a.North, b.North), a.East, a.North, b.East, b.North));
            }
        }

        return walls;
    }

    /// <summary>Ob die Strecke a→b einen Zaun kreuzt.</summary>
    private static bool CrossesWall(
        List<(double MinE, double MinN, double MaxE, double MaxN, double Ax, double Ay, double Bx, double By)> walls,
        double ax,
        double ay,
        double bx,
        double by)
    {
        var (minE, maxE, minN, maxN) = (Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));
        foreach (var w in walls)
        {
            if (w.MaxE < minE || w.MinE > maxE || w.MaxN < minN || w.MinN > maxN)
            {
                continue;
            }

            if (SegmentIntersection(ax, ay, bx, by, w.Ax, w.Ay, w.Bx, w.By) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Schlüssel eines Straßenpunkts (auf 10 cm): gleiche Punkte verschiedener Linien sind eine Kreuzung.</summary>
    private static (long, long) VertexKey(double east, double north) => ((long)Math.Round(east * 10), (long)Math.Round(north * 10));

    /// <summary>Anteil eines Abschnitts auf einer Bahn (Proben alle 5 m).</summary>
    private double RunwayShareOf(TaxiNode a, TaxiNode b)
    {
        if (_runways.Count == 0)
        {
            return 0;
        }

        var steps = Math.Max(1, (int)Math.Ceiling(Distance(a, b) / 5));
        var on = 0;
        for (var i = 0; i < steps; i++)
        {
            var f = (i + 0.5) / steps;
            if (RunwayAt(a.EastM + ((b.EastM - a.EastM) * f), a.NorthM + ((b.NorthM - a.NorthM) * f)) is not null)
            {
                on++;
            }
        }

        return on / (double)steps;
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
