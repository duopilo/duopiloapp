namespace Duopilo.Fire;

/// <summary>
/// Spur des Flugzeugs ab dem Aufsetzen (Meter östlich/nördlich wie im Rollweg-Netz): Punkte alle <see cref="SpacingM"/> Meter mit der
/// Strecke ab dem ersten Punkt. Die Fahrzeuge folgen dieser Spur statt einer geplanten Route – wo das Flugzeug abrollt, dreht oder
/// stehen bleibt, ist damit egal. Das Flugzeug steht immer am Ende der Spur. Geschrieben vom Takt des Dienstes, gelesen vom
/// SimConnect-Thread (Fahrzeuge), deshalb unter einer Sperre.
/// </summary>
public sealed class FireTrail
{
    /// <summary>Neuer Punkt erst nach so viel Bewegung.</summary>
    public const double SpacingM = 3;

    private readonly object _lock = new();
    private readonly List<(double East, double North, double S)> _points = [];

    /// <summary>Länge der Spur = Stelle des Flugzeugs.</summary>
    public double LengthM
    {
        get
        {
            lock (_lock)
            {
                return _points.Count == 0 ? 0 : _points[^1].S;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>Lage des Flugzeugs übernehmen: neuer Punkt, wenn es sich seit dem letzten mindestens <see cref="SpacingM"/> bewegt hat.</summary>
    public void Add(double east, double north)
    {
        lock (_lock)
        {
            if (_points.Count == 0)
            {
                _points.Add((east, north, 0));
                return;
            }

            var last = _points[^1];
            var d = Math.Sqrt(((east - last.East) * (east - last.East)) + ((north - last.North) * (north - last.North)));
            if (d >= SpacingM)
            {
                _points.Add((east, north, last.S + d));
            }
        }
    }

    /// <summary>Spur verwerfen (Durchstarten).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
        }
    }

    /// <summary>Punkt und Kurs (Richtung der Spur) bei <paramref name="s"/> Metern; außerhalb am Anfang bzw. Ende.</summary>
    public (double East, double North, double HeadingDeg) At(double s)
    {
        lock (_lock)
        {
            if (_points.Count == 0)
            {
                return (0, 0, 0);
            }

            if (_points.Count == 1)
            {
                return (_points[0].East, _points[0].North, 0);
            }

            var i = Segment(s);
            var (a, b) = (_points[i], _points[i + 1]);
            var length = b.S - a.S;
            var f = length > 0 ? Math.Clamp((s - a.S) / length, 0, 1) : 0;
            return (a.East + ((b.East - a.East) * f), a.North + ((b.North - a.North) * f), Heading(b.East - a.East, b.North - a.North));
        }
    }

    /// <summary>Stelle der Spur, die einem Punkt am nächsten liegt (zwischen <paramref name="fromS"/> und <paramref name="toS"/>).</summary>
    public double NearestS(double east, double north, double fromS = 0, double toS = double.MaxValue)
    {
        lock (_lock)
        {
            if (_points.Count < 2)
            {
                return 0;
            }

            var (best, bestD) = (Math.Max(0, fromS), double.MaxValue);
            for (var i = 0; i < _points.Count - 1; i++)
            {
                var (a, b) = (_points[i], _points[i + 1]);
                if (b.S < fromS || a.S > toS)
                {
                    continue;
                }

                var (dx, dy) = (b.East - a.East, b.North - a.North);
                var length2 = (dx * dx) + (dy * dy);
                var t = length2 > 0 ? Math.Clamp((((east - a.East) * dx) + ((north - a.North) * dy)) / length2, 0, 1) : 0;
                var (px, py) = (a.East + (dx * t), a.North + (dy * t));
                var d = ((east - px) * (east - px)) + ((north - py) * (north - py));
                if (d < bestD)
                {
                    var s = a.S + ((b.S - a.S) * t);
                    (best, bestD) = (Math.Clamp(s, fromS, toS), d);
                }
            }

            return best;
        }
    }

    /// <summary>Abstand eines Punkts zur Spur (Meter), unendlich ohne Spur.</summary>
    public double DistanceTo(double east, double north)
    {
        if (Count < 2)
        {
            return double.PositiveInfinity;
        }

        var (x, y, _) = At(NearestS(east, north));
        return Math.Sqrt(((x - east) * (x - east)) + ((y - north) * (y - north)));
    }

    /// <summary>Größte Kursänderung (Grad) zwischen <paramref name="s"/> und <paramref name="s"/> + <paramref name="aheadM"/>.</summary>
    public double TurnAheadDeg(double s, double aheadM)
    {
        var (_, _, start) = At(s);
        var max = 0.0;
        for (var d = 5.0; d <= aheadM; d += 5)
        {
            var (_, _, h) = At(s + d);
            var diff = Math.Abs(((h - start + 540) % 360) - 180);
            max = Math.Max(max, diff);
        }

        return max;
    }

    private int Segment(double s)
    {
        // Binäre Suche nach dem Abschnitt [i, i+1] mit S_i <= s
        var (lo, hi) = (0, _points.Count - 2);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_points[mid].S <= s)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static double Heading(double east, double north)
    {
        var deg = Math.Atan2(east, north) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }
}
