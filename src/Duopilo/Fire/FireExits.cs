using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>
/// Platz, an dem ein Fahrzeug an seiner Wache erscheint (Meter wie im Rollweg-Netz), mit Blickrichtung. <paramref name="RollOut"/>: es
/// steht vor einem Tor und rollt erst <see cref="FireExits.RollOutM"/> geradeaus aus der Halle, bevor es zum Rollweg-Netz abbiegt.
/// </summary>
public sealed record FireExit(double EastM, double NorthM, double HeadingDeg, bool RollOut);

/// <summary>
/// Plätze an der Wache, damit die Fahrzeuge nebeneinander statt ineinander erscheinen und nicht mitten im Gebäude. Der Reihe nach:
/// <list type="number">
/// <item>Tore aus OSM (<c>door=overhead</c>, <c>entrance=garage</c> … zuerst, dann andere Türen): je Fahrzeug ein Tor, 4 m davor.</item>
/// <item>Fahrzeugstellplatz der Szenerie (Parkposition „vehicle“) bis 80 m um die Wache: nebeneinander quer zu seiner Ausrichtung.</item>
/// <item>Gebäudeumriss: die Hallenseite, die zum nächsten Rollpunkt bzw. zur Betriebsstraße zeigt, die Fahrzeuge 4 m davor
/// nebeneinander, Front nach außen.</item>
/// <item>Nur der Mittelpunkt: nebeneinander quer zur Richtung zum nächsten Rollpunkt.</item>
/// </list>
/// </summary>
public static class FireExits
{
    /// <summary>So viele Plätze je Wache (mehr Fahrzeuge fangen wieder vorn an).</summary>
    public const int Slots = 8;

    /// <summary>Abstand nebeneinander (ein FLF ist rund 3 m breit, Hallentore etwa 4–5 m).</summary>
    public const double SpacingM = 6;

    /// <summary>So weit vor dem Tor bzw. der Hallenwand erscheinen die Fahrzeuge.</summary>
    public const double DoorOffsetM = 4;

    /// <summary>So weit rollen sie geradeaus aus der Halle, bevor sie abbiegen.</summary>
    public const double RollOutM = 12;

    /// <summary>Fahrzeugstellplätze der Szenerie so weit um die Wache gehören zu ihr.</summary>
    public const double VehicleParkingRadiusM = 80;

    /// <summary>MSFS-Parkposition für Bodenfahrzeuge (TAXI_PARKING TYPE).</summary>
    private const int VehicleParkingType = 13;

    /// <summary>Plätze an einer OSM-Wache (siehe Klasse) und woher sie kommen: door, vehicle_parking, outline oder centre.</summary>
    public static (IReadOnlyList<FireExit> Exits, string Source) ForOsm(
        double eastM,
        double northM,
        IReadOnlyList<(double East, double North)>? outline,
        IReadOnlyList<(double East, double North, bool Garage)> doors,
        TaxiGraph graph)
    {
        if (FirePlanner.NearestNode(graph, eastM, northM) is not { } nearest)
        {
            return ([], "centre");
        }

        var edges = outline is { Count: >= 4 } ? OutwardEdges(outline) : [];

        // 1. Tore: Hallentore zuerst; Blick weg vom Gebäude (Normale der nächsten Wand, sonst weg vom Mittelpunkt)
        if (doors.Count > 0)
        {
            var exits = new List<FireExit>();
            var ordered = doors.OrderByDescending(d => d.Garage).ThenBy(d => FirePlanner.Distance(d.East, d.North, nearest.EastM, nearest.NorthM)).ToList();
            for (var i = 0; i < Slots; i++)
            {
                var door = ordered[i % ordered.Count];
                var (nx, ny) = edges.Count > 0
                    ? edges.MinBy(e => TaxiGraph.SegmentDistance(door.East, door.North, e.Ax, e.Ay, e.Bx, e.By))!.Normal
                    : Unit(door.East - eastM, door.North - northM, nearest.EastM - eastM, nearest.NorthM - northM);
                var row = i / ordered.Count;
                var (sx, sy) = (-ny, nx);
                var offset = row == 0 ? 0 : (row % 2 == 1 ? 1 : -1) * ((row + 1) / 2) * SpacingM;
                var (ex, ey) = (door.East + (nx * DoorOffsetM) + (sx * offset), door.North + (ny * DoorOffsetM) + (sy * offset));
                exits.Add(new FireExit(ex, ey, TaxiPath.Heading(nx, ny), true));
            }

            return (exits, "door");
        }

        // 2. Fahrzeugstellplatz der Szenerie vor der Wache
        var parking = graph.Nodes
            .Where(n => n.Parking is { Type: VehicleParkingType } && FirePlanner.Distance(n.EastM, n.NorthM, eastM, northM) <= VehicleParkingRadiusM)
            .MinBy(n => FirePlanner.Distance(n.EastM, n.NorthM, eastM, northM));
        if (parking is not null)
        {
            return (ForParking(parking), "vehicle_parking");
        }

        // 3. Hallenseite zur Straße: die Wand, deren Außenseite am ehesten zum nächsten Rollpunkt zeigt (lange Wände bevorzugt)
        var side = edges
            .Where(e => e.Length >= 4)
            .Select(e =>
            {
                var (tx, ty) = (nearest.EastM - ((e.Ax + e.Bx) / 2), nearest.NorthM - ((e.Ay + e.By) / 2));
                var distance = Math.Max(0.1, Math.Sqrt((tx * tx) + (ty * ty)));
                return (Edge: e, Score: (((e.Normal.X * tx) + (e.Normal.Y * ty)) / distance) * Math.Min(e.Length, 25));
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Edge)
            .FirstOrDefault();
        if (side is not null)
        {
            var (dx, dy) = ((side.Bx - side.Ax) / side.Length, (side.By - side.Ay) / side.Length);
            var (mx, my) = ((side.Ax + side.Bx) / 2, (side.Ay + side.By) / 2);
            var perRow = Math.Max(1, (int)((side.Length - 2) / SpacingM) + 1);
            var heading = TaxiPath.Heading(side.Normal.X, side.Normal.Y);
            var exits = new List<FireExit>();
            for (var i = 0; i < Slots; i++)
            {
                var along = Lateral(i % perRow) * SpacingM;
                var away = DoorOffsetM + ((i / perRow) * 9);
                exits.Add(new FireExit(mx + (dx * along) + (side.Normal.X * away), my + (dy * along) + (side.Normal.Y * away), heading, true));
            }

            return (exits, "outline");
        }

        // 4. Nur der Mittelpunkt: nebeneinander quer zur Richtung zum nächsten Rollpunkt
        var (fx, fy) = Unit(nearest.EastM - eastM, nearest.NorthM - northM, 0, 1);
        return (Row(eastM, northM, fx, fy, TaxiPath.Heading(fx, fy)), "centre");
    }

    /// <summary>Plätze an einer Parkposition: der erste auf ihr, die weiteren daneben, quer zu ihrer Ausrichtung.</summary>
    public static IReadOnlyList<FireExit> ForParking(TaxiNode node)
    {
        var heading = node.Parking?.HeadingDeg ?? 0;
        var rad = heading * Math.PI / 180;
        return Row(node.EastM, node.NorthM, Math.Sin(rad), Math.Cos(rad), heading);
    }

    /// <summary>Reihe nebeneinander um einen Punkt, quer zur Richtung (fx, fy): 0, rechts, links, weiter rechts …</summary>
    private static List<FireExit> Row(double eastM, double northM, double fx, double fy, double heading)
    {
        var (rx, ry) = (fy, -fx);
        return [.. Enumerable.Range(0, Slots).Select(i =>
        {
            var offset = Lateral(i) * SpacingM;
            return new FireExit(eastM + (rx * offset), northM + (ry * offset), heading, false);
        })];
    }

    /// <summary>0, 1, −1, 2, −2 …: Plätze von der Mitte nach außen.</summary>
    private static int Lateral(int i) => i == 0 ? 0 : (i % 2 == 1 ? 1 : -1) * ((i + 1) / 2);

    private static (double X, double Y) Unit(double x, double y, double fallbackX, double fallbackY)
    {
        var length = Math.Sqrt((x * x) + (y * y));
        if (length > 0.01)
        {
            return (x / length, y / length);
        }

        var fallback = Math.Sqrt((fallbackX * fallbackX) + (fallbackY * fallbackY));
        return fallback > 0.01 ? (fallbackX / fallback, fallbackY / fallback) : (0, 1);
    }

    /// <summary>Wände des Umrisses mit Normale nach außen.</summary>
    private static List<Wall> OutwardEdges(IReadOnlyList<(double East, double North)> outline)
    {
        // Vorzeichen der Fläche: positiv = gegen den Uhrzeigersinn (Ost/Nord), dann zeigt die rechte Normale nach außen
        var area = 0.0;
        for (var i = 0; i + 1 < outline.Count; i++)
        {
            area += (outline[i].East * outline[i + 1].North) - (outline[i + 1].East * outline[i].North);
        }

        var sign = area >= 0 ? 1 : -1;
        var walls = new List<Wall>();
        for (var i = 0; i + 1 < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[i + 1]);
            var length = FirePlanner.Distance(a.East, a.North, b.East, b.North);
            if (length < 0.5)
            {
                continue;
            }

            var normal = (sign * (b.North - a.North) / length, sign * -(b.East - a.East) / length);
            walls.Add(new Wall(a.East, a.North, b.East, b.North, length, normal));
        }

        return walls;
    }

    private sealed record Wall(double Ax, double Ay, double Bx, double By, double Length, (double X, double Y) Normal);
}
