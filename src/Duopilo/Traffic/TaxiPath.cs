namespace Duopilo.Traffic;

/// <summary>
/// Weg zum Rollen: Polylinie (Meter östlich/nördlich) mit runden Ecken – an jedem Knick ein Kreisbogen mit dem Kurvenradius, kürzer,
/// wenn die Abschnitte dafür nicht reichen. Jeder Teil hat eine Höchstgeschwindigkeit: geradeaus die Rollgeschwindigkeit, im Bogen so
/// schnell, dass die Querbeschleunigung nicht über <c>lateralMps2</c> geht.
/// </summary>
public sealed class TaxiPath
{
    private readonly List<Piece> _pieces;

    private TaxiPath(List<Piece> pieces)
    {
        _pieces = pieces;
        LengthM = pieces.Count == 0 ? 0 : pieces[^1].StartS + pieces[^1].Length;
    }

    public double LengthM { get; }

    /// <summary>Anzahl der Kurven.</summary>
    public int Turns => _pieces.Count(p => p.IsArc);

    /// <summary>Kleinster Kurvenradius, sonst null.</summary>
    public double? MinRadiusM => _pieces.Where(p => p.IsArc).Select(p => (double?)p.Radius).Min();

    /// <summary>
    /// Weg durch <paramref name="points"/>; Ecken mit <paramref name="turnRadiusM"/>, Höchstgeschwindigkeit geradeaus
    /// <paramref name="straightMps"/>, in Bögen sqrt(<paramref name="lateralMps2"/> · r), aber nicht unter <paramref name="minMps"/>.
    /// </summary>
    public static TaxiPath Build(IReadOnlyList<(double East, double North)> points, double turnRadiusM, double straightMps, double lateralMps2, double minMps)
    {
        var p = new List<(double X, double Y)>();
        foreach (var point in points)
        {
            if (p.Count == 0 || Math.Abs(point.East - p[^1].X) + Math.Abs(point.North - p[^1].Y) > 0.5)
            {
                p.Add((point.East, point.North));
            }
        }

        var pieces = new List<Piece>();
        if (p.Count < 2)
        {
            return new TaxiPath(pieces);
        }

        var s = 0.0;
        var current = p[0];
        for (var i = 1; i < p.Count - 1; i++)
        {
            var (ux, uy, lenIn) = Direction(p[i - 1], p[i]);
            var (vx, vy, lenOut) = Direction(p[i], p[i + 1]);
            var turn = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
            var half = Math.Tan(Math.Abs(turn) / 2);
            if (Math.Abs(turn) < 1 * Math.PI / 180 || half <= 0)
            {
                continue;
            }

            // Tangentenlänge: Ein Abschnitt zwischen zwei Ecken reicht für beide je zur Hälfte, der erste und letzte ganz
            var maxIn = lenIn * (i - 1 == 0 ? 1 : 0.5);
            var maxOut = lenOut * (i + 1 == p.Count - 1 ? 1 : 0.5);
            var t = Math.Min(turnRadiusM * half, Math.Min(maxIn, maxOut));
            var radius = t / half;
            var a = (X: p[i].X - (ux * t), Y: p[i].Y - (uy * t));
            AddLine(pieces, ref s, current, a, straightMps);

            var left = turn > 0;
            var (nx, ny) = left ? (-uy, ux) : (uy, -ux);
            var center = (X: a.X + (nx * radius), Y: a.Y + (ny * radius));
            var length = radius * Math.Abs(turn);
            if (length > 0.01)
            {
                var limit = Math.Min(straightMps, Math.Max(minMps, Math.Sqrt(lateralMps2 * radius)));
                pieces.Add(new Piece(s, length, true, a.X, a.Y, 0, 0, center.X, center.Y, radius, Math.Atan2(a.Y - center.Y, a.X - center.X), left ? 1 : -1, limit));
                s += length;
            }

            current = (p[i].X + (vx * t), p[i].Y + (vy * t));
        }

        AddLine(pieces, ref s, current, p[^1], straightMps);
        return new TaxiPath(pieces);
    }

    /// <summary>Punkt und Kurs (rechtweisend, Richtung der Bewegung) bei <paramref name="s"/> Metern ab dem Anfang.</summary>
    public (double East, double North, double HeadingDeg) At(double s)
    {
        if (_pieces.Count == 0)
        {
            return (0, 0, 0);
        }

        var piece = PieceAt(s);
        var a = Math.Clamp(s - piece.StartS, 0, piece.Length);
        if (!piece.IsArc)
        {
            var f = piece.Length > 0 ? a / piece.Length : 0;
            var (dx, dy) = (piece.Bx - piece.Ax, piece.By - piece.Ay);
            return (piece.Ax + (dx * f), piece.Ay + (dy * f), Heading(dx, dy));
        }

        var phi = piece.StartAngle + (piece.Sense * a / piece.Radius);
        var (cos, sin) = (Math.Cos(phi), Math.Sin(phi));
        var (tx, ty) = piece.Sense > 0 ? (-sin, cos) : (sin, -cos);
        return (piece.Cx + (piece.Radius * cos), piece.Cy + (piece.Radius * sin), Heading(tx, ty));
    }

    /// <summary>
    /// Höchste Geschwindigkeit bei <paramref name="s"/>, mit der das Flugzeug mit <paramref name="decelMps2"/> jede folgende Grenze und
    /// das Ende (Halt) noch einhält.
    /// </summary>
    public double AllowedMps(double s, double decelMps2)
    {
        if (_pieces.Count == 0)
        {
            return 0;
        }

        var allowed = Math.Sqrt(2 * decelMps2 * Math.Max(0, LengthM - s));
        foreach (var piece in _pieces)
        {
            if (piece.StartS + piece.Length < s)
            {
                continue;
            }

            var distance = Math.Max(0, piece.StartS - s);
            allowed = Math.Min(allowed, Math.Sqrt((piece.Limit * piece.Limit) + (2 * decelMps2 * distance)));
        }

        return allowed;
    }

    /// <summary>Stelle des Wegs (Meter ab dem Anfang), die einem Punkt am nächsten liegt, gesucht ab <paramref name="fromS"/> (bis <paramref name="toS"/>).</summary>
    public double NearestS(double east, double north, double fromS = 0, double toS = double.MaxValue)
    {
        double Distance2(double s)
        {
            var (x, y, _) = At(s);
            return ((x - east) * (x - east)) + ((y - north) * (y - north));
        }

        var start = Math.Clamp(fromS, 0, LengthM);
        var end = Math.Clamp(toS, start, LengthM);
        var (best, bestD) = (start, Distance2(start));
        for (var s = start; s <= end; s += 1)
        {
            if (Distance2(s) is var d && d < bestD)
            {
                (best, bestD) = (s, d);
            }
        }

        for (var s = Math.Max(start, best - 1); s <= Math.Min(end, best + 1); s += 0.05)
        {
            if (Distance2(s) is var d && d < bestD)
            {
                (best, bestD) = (s, d);
            }
        }

        return best;
    }

    /// <summary>Kompasskurs einer Richtung (Ost, Nord).</summary>
    public static double Heading(double east, double north)
    {
        var deg = Math.Atan2(east, north) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    private static (double X, double Y, double Length) Direction((double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return length > 0 ? (dx / length, dy / length, length) : (0, 1, 0);
    }

    private static void AddLine(List<Piece> pieces, ref double s, (double X, double Y) a, (double X, double Y) b, double limit)
    {
        var length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        if (length <= 0.01)
        {
            return;
        }

        pieces.Add(new Piece(s, length, false, a.X, a.Y, b.X, b.Y, 0, 0, 0, 0, 0, limit));
        s += length;
    }

    private Piece PieceAt(double s)
    {
        foreach (var piece in _pieces)
        {
            if (s < piece.StartS + piece.Length)
            {
                return piece;
            }
        }

        return _pieces[^1];
    }

    /// <summary>Gerade (A → B) oder Bogen (Mittelpunkt C, Radius, Startwinkel, Sinn +1 links/−1 rechts).</summary>
    private sealed record Piece(
        double StartS,
        double Length,
        bool IsArc,
        double Ax,
        double Ay,
        double Bx,
        double By,
        double Cx,
        double Cy,
        double Radius,
        double StartAngle,
        int Sense,
        double Limit);
}
