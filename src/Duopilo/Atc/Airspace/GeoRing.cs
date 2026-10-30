namespace Duopilo.Atc.Airspace;

/// <summary>
/// Geschlossener Ring aus Längen/Breiten (Grad) für Punkt-in-Polygon-Abfragen. Ein Ring über die Datumsgrenze
/// (Sprung von mehr als 180° zwischen zwei Punkten) wird intern auf 0–360° Länge verschoben.
/// </summary>
public sealed class GeoRing
{
    private readonly double[] _lon;
    private readonly double[] _lat;

    /// <param name="lonLat">Punkte abwechselnd Länge, Breite. Der Ring muss nicht geschlossen sein.</param>
    public GeoRing(IReadOnlyList<double> lonLat)
    {
        var count = lonLat.Count / 2;
        if (count > 1 && lonLat[0] == lonLat[(count - 1) * 2] && lonLat[1] == lonLat[(count - 1) * 2 + 1])
        {
            count--; // Schlusspunkt = Anfangspunkt
        }

        _lon = new double[count];
        _lat = new double[count];
        for (var i = 0; i < count; i++)
        {
            _lon[i] = lonLat[i * 2];
            _lat[i] = lonLat[i * 2 + 1];
        }

        for (var i = 1; i < count && !Wraps; i++)
        {
            Wraps = Math.Abs(_lon[i] - _lon[i - 1]) > 180;
        }

        if (Wraps)
        {
            for (var i = 0; i < count; i++)
            {
                if (_lon[i] < 0)
                {
                    _lon[i] += 360;
                }
            }
        }

        MinLon = count > 0 ? _lon.Min() : 0;
        MaxLon = count > 0 ? _lon.Max() : 0;
        MinLat = count > 0 ? _lat.Min() : 0;
        MaxLat = count > 0 ? _lat.Max() : 0;

        // Fläche grob in Grad² mit Breitenkorrektur – nur zum Vergleichen ("kleinster Sektor gewinnt")
        var meanCos = Math.Cos((MinLat + MaxLat) / 2 * Math.PI / 180);
        var sum = 0.0;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            sum += (_lon[j] * meanCos * _lat[i]) - (_lon[i] * meanCos * _lat[j]);
        }

        Area = Math.Abs(sum) / 2;
    }

    public int Count => _lon.Length;

    public bool Wraps { get; }

    public double MinLon { get; }

    public double MaxLon { get; }

    public double MinLat { get; }

    public double MaxLat { get; }

    public double Area { get; }

    public bool Contains(double lat, double lon)
    {
        if (_lon.Length < 3)
        {
            return false;
        }

        if (Wraps && lon < 0)
        {
            lon += 360;
        }

        if (lat < MinLat || lat > MaxLat || lon < MinLon || lon > MaxLon)
        {
            return false;
        }

        // Strahl nach Osten (Even-Odd)
        var inside = false;
        for (int i = 0, j = _lon.Length - 1; i < _lon.Length; j = i++)
        {
            var yi = _lat[i];
            var yj = _lat[j];
            if ((yi > lat) != (yj > lat))
            {
                var x = ((_lon[j] - _lon[i]) * (lat - yi) / (yj - yi)) + _lon[i];
                if (lon < x)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }
}

/// <summary>Polygon mit Außenring und optionalen Löchern.</summary>
public sealed class GeoPolygon(GeoRing outer, IReadOnlyList<GeoRing>? holes = null)
{
    public GeoRing Outer { get; } = outer;

    public IReadOnlyList<GeoRing> Holes { get; } = holes ?? [];

    public double Area => Math.Max(0, Outer.Area - Holes.Sum(h => h.Area));

    public bool Contains(double lat, double lon) => Outer.Contains(lat, lon) && !Holes.Any(h => h.Contains(lat, lon));
}
