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

    /// <summary>An einer Hallenwand: so weit vor dem Ende der Wand liegt der äußerste Platz einer Reihe mindestens.</summary>
    public const double WallEndMarginM = 1.5;

    /// <summary>Abstand der Reihen vor einer Hallenwand.</summary>
    public const double RowSpacingM = 9;

    /// <summary>Höchstens so viele Reihen vor einer Hallenwand.</summary>
    public const int MaxRows = 4;

    /// <summary>Fahrzeugstellplätze der Szenerie so weit um die Wache gehören zu ihr.</summary>
    public const double VehicleParkingRadiusM = 80;

    /// <summary>MSFS-Parkposition für Bodenfahrzeuge (TAXI_PARKING TYPE).</summary>
    private const int VehicleParkingType = 13;

    /// <summary>
    /// Plätze an einer OSM-Wache (siehe Klasse) und woher sie kommen: door, vehicle_parking, outline oder centre. <paramref name="facing"/>:
    /// dorthin zeigt die Hallenseite (Hof des Geländes), sonst zum nächsten Rollpunkt.
    /// </summary>
    public static (IReadOnlyList<FireExit> Exits, string Source) ForOsm(
        double eastM,
        double northM,
        IReadOnlyList<(double East, double North)>? outline,
        IReadOnlyList<(double East, double North, bool Garage)> doors,
        TaxiGraph graph,
        (double East, double North)? facing = null)
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

        // 3. Hallenseite zur Straße: die Wand, deren Außenseite am ehesten zum nächsten Rollpunkt (bzw. zum Hof) zeigt (lange Wände bevorzugt)
        var (targetEast, targetNorth) = facing ?? (nearest.EastM, nearest.NorthM);
        var side = edges
            .Where(e => e.Length >= 4)
            .Select(e =>
            {
                var (tx, ty) = (targetEast - ((e.Ax + e.Bx) / 2), targetNorth - ((e.Ay + e.By) / 2));
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

            // Reihe von der Mitte nach außen, nie über die Wand hinaus (Nachtest EDDK 05.11.: an der 45-m-Wand lag Platz 8 24 m von der
            // Mitte, 1,5 m hinter dem Wandende im vorspringenden Flügel der Halle)
            var perRow = (2 * Math.Max(0, (int)(((side.Length / 2) - WallEndMarginM) / SpacingM))) + 1;
            var heading = TaxiPath.Heading(side.Normal.X, side.Normal.Y);
            var exits = new List<FireExit>();
            for (var row = 0; row < MaxRows && exits.Count < Slots; row++)
            {
                for (var i = 0; i < perRow && exits.Count < Slots; i++)
                {
                    var along = Lateral(i) * SpacingM;
                    var away = DoorOffsetM + (row * RowSpacingM);
                    var (ex, ey) = (mx + (dx * along) + (side.Normal.X * away), my + (dy * along) + (side.Normal.Y * away));

                    // Platz und Herausrollen frei vom Gebäude (sonst ein Platz weiter bzw. die nächste Reihe)
                    if (InsideOrAcross(outline!, ex, ey, ex + (side.Normal.X * RollOutM), ey + (side.Normal.Y * RollOutM)))
                    {
                        continue;
                    }

                    exits.Add(new FireExit(ex, ey, heading, true));
                }
            }

            if (exits.Count > 0)
            {
                return (exits, "outline");
            }
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

    /// <summary>Liegt der Punkt (ax/ay) im Umriss, oder schneidet die Strecke zu (bx/by) eine seiner Wände?</summary>
    public static bool InsideOrAcross(IReadOnlyList<(double East, double North)> outline, double ax, double ay, double bx, double by)
    {
        var inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            var (xi, yi) = outline[i];
            var (xj, yj) = outline[j];
            if ((yi > ay) != (yj > ay) && ax < ((xj - xi) * (ay - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }

            if (Crosses(ax, ay, bx, by, xi, yi, xj, yj))
            {
                return true;
            }
        }

        return inside;
    }

    private static bool Crosses(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        static double Cross(double ox, double oy, double px, double py, double qx, double qy) => ((px - ox) * (qy - oy)) - ((py - oy) * (qx - ox));
        var d1 = Cross(cx, cy, dx, dy, ax, ay);
        var d2 = Cross(cx, cy, dx, dy, bx, by);
        var d3 = Cross(ax, ay, bx, by, cx, cy);
        var d4 = Cross(ax, ay, bx, by, dx, dy);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
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
