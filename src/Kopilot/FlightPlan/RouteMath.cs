namespace Kopilot.FlightPlan;

/// <summary>Großkreis-Rechnungen in nautischen Meilen.</summary>
public static class RouteMath
{
    public const double EarthRadiusNm = 3440.065;

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Rechtweisender Kurs von 1 nach 2 (0–360).</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var φ1 = Rad(lat1);
        var φ2 = Rad(lat2);
        var Δλ = Rad(lon2 - lon1);
        var y = Math.Sin(Δλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        return (Deg(Math.Atan2(y, x)) + 360) % 360;
    }

    /// <summary>Zielpunkt bei Start, rechtweisendem Kurs und Entfernung.</summary>
    public static (double Lat, double Lon) Destination(double lat, double lon, double bearingDeg, double distanceNm)
    {
        var δ = distanceNm / EarthRadiusNm;
        var θ = Rad(bearingDeg);
        var φ1 = Rad(lat);
        var λ1 = Rad(lon);
        var φ2 = Math.Asin(Math.Clamp(Math.Sin(φ1) * Math.Cos(δ) + Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(θ), -1, 1));
        var λ2 = λ1 + Math.Atan2(Math.Sin(θ) * Math.Sin(δ) * Math.Cos(φ1), Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));
        return (Deg(φ2), ((Deg(λ2) + 540) % 360) - 180);
    }

    /// <summary>
    /// Lage von P relativ zur Strecke A→B: Querablage (rechts positiv) und Strecke entlang A→B ab A (vor A negativ).
    /// </summary>
    public static (double CrossTrackNm, double AlongTrackNm) TrackOffsets(
        double latA, double lonA, double latB, double lonB, double latP, double lonP)
    {
        var d13 = DistanceNm(latA, lonA, latP, lonP) / EarthRadiusNm;
        if (d13 < 1e-9)
        {
            return (0, 0);
        }

        var θ13 = Rad(BearingDeg(latA, lonA, latP, lonP));
        var θ12 = Rad(BearingDeg(latA, lonA, latB, lonB));
        var xt = Math.Asin(Math.Clamp(Math.Sin(d13) * Math.Sin(θ13 - θ12), -1, 1));
        var cosXt = Math.Cos(xt);
        var at = cosXt < 1e-12 ? 0 : Math.Acos(Math.Clamp(Math.Cos(d13) / cosXt, -1, 1));
        if (Math.Cos(θ13 - θ12) < 0)
        {
            at = -at;
        }

        return (xt * EarthRadiusNm, at * EarthRadiusNm);
    }
}

/// <summary>Position des Flugzeugs auf der geplanten Route.</summary>
/// <param name="LegIndex">Index des aktuellen Abschnitts (Punkt LegIndex → LegIndex+1) in der Punktliste.</param>
/// <param name="CrossTrackNm">Querablage, rechts positiv.</param>
/// <param name="AlongLegNm">Bereits zurückgelegte Strecke im aktuellen Abschnitt.</param>
/// <param name="LegLengthNm">Länge des aktuellen Abschnitts.</param>
/// <param name="DistanceToNextNm">Direkte Entfernung zum nächsten Punkt.</param>
/// <param name="DistanceToGoNm">Restliche Streckenlänge bis zum letzten Punkt.</param>
/// <param name="DistanceFlownNm">Streckenlänge vom ersten Punkt bis zur Position.</param>
public sealed record RouteProgress(
    int LegIndex,
    double CrossTrackNm,
    double AlongLegNm,
    double LegLengthNm,
    double DistanceToNextNm,
    double DistanceToGoNm,
    double DistanceFlownNm)
{
    public int NextPointIndex => LegIndex + 1;

    /// <summary>Anteil des aktuellen Abschnitts (0–1).</summary>
    public double LegFraction => LegLengthNm < 0.01 ? 1 : Math.Clamp(AlongLegNm / LegLengthNm, 0, 1);
}

public static class RouteTracker
{
    /// <summary>
    /// Ordnet die Position einem Routenabschnitt zu. Mit <paramref name="hint"/> (letzter Abschnitt) wird bevorzugt
    /// vorwärts gesucht, damit sich kreuzende Routenteile nicht verwechselt werden.
    /// </summary>
    public static RouteProgress? Locate(IReadOnlyList<RoutePoint> points, double lat, double lon, int? hint = null)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var legLengths = new double[points.Count - 1];
        for (var i = 0; i < legLengths.Length; i++)
        {
            legLengths[i] = RouteMath.DistanceNm(points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon);
        }

        var from = 0;
        var to = legLengths.Length - 1;
        if (hint is { } h && h >= 0 && h < legLengths.Length)
        {
            from = Math.Max(0, h - 1);
        }

        var best = FindBest(points, legLengths, lat, lon, from, to);
        if (best is null || Math.Abs(best.Value.Xt) > 30)
        {
            // Weit weg vom erwarteten Abschnitt: komplette Route durchsuchen
            best = FindBest(points, legLengths, lat, lon, 0, to) ?? best;
        }

        if (best is not { } b)
        {
            return null;
        }

        var along = Math.Clamp(b.At, 0, legLengths[b.Leg]);
        var next = points[b.Leg + 1];
        var toNext = RouteMath.DistanceNm(lat, lon, next.Lat, next.Lon);
        var remainingAfterNext = legLengths.Skip(b.Leg + 1).Sum();
        var flown = legLengths.Take(b.Leg).Sum() + along;

        return new RouteProgress(b.Leg, b.Xt, along, legLengths[b.Leg], toNext, toNext + remainingAfterNext, flown);
    }

    private static (int Leg, double Xt, double At)? FindBest(
        IReadOnlyList<RoutePoint> points, double[] legLengths, double lat, double lon, int from, int to)
    {
        (int Leg, double Xt, double At, double Score)? best = null;
        for (var i = from; i <= to; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            double xt, at;
            if (legLengths[i] < 0.05)
            {
                xt = RouteMath.DistanceNm(a.Lat, a.Lon, lat, lon);
                at = 0;
            }
            else
            {
                (xt, at) = RouteMath.TrackOffsets(a.Lat, a.Lon, b.Lat, b.Lon, lat, lon);
            }

            // Vor dem Abschnitt oder dahinter: Entfernung zum nächsten Endpunkt zählt
            var outside = at < 0 ? -at : at > legLengths[i] ? at - legLengths[i] : 0;
            var score = Math.Sqrt(xt * xt + outside * outside);

            // Im Zweifel den späteren Abschnitt nehmen (Position genau auf einem Wegpunkt)
            if (best is null || score < best.Value.Score - 0.01 || (Math.Abs(score - best.Value.Score) <= 0.01 && outside == 0))
            {
                best = (i, xt, at, score);
            }
        }

        return best is { } r ? (r.Leg, r.Xt, r.At) : null;
    }
}
