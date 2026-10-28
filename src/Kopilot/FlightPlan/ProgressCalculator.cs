namespace Kopilot.FlightPlan;

/// <summary>Markante Punkte der Route (TOC/TOD) vor dem Flugzeug.</summary>
public sealed record RouteMarker(string Ident, double DistanceNm);

/// <summary>Was das FMS über den aktiven Wegpunkt verrät (FBW: Distanz und Peilung, aber kein Name).</summary>
/// <param name="MagVariationDeg">Missweisung Ost positiv (true − magnetic), um eine missweisende Peilung umzurechnen.</param>
public sealed record FmsWaypointInput(double AircraftLat, double AircraftLon, double DistanceNm, double BearingDeg, double MagVariationDeg);

/// <summary>Fortschritt gegen den SimBrief-Plan an der aktuellen Position.</summary>
public sealed record FlightProgress
{
    public required RouteProgress Route { get; init; }

    public required RoutePoint Previous { get; init; }

    public required RoutePoint Next { get; init; }

    public OfpFix? NextFix { get; init; }

    /// <summary>Distanz zum nächsten Wegpunkt – vom FMS, wenn zugeordnet, sonst aus dem Plan.</summary>
    public double DistanceToNextNm { get; init; }

    /// <summary>Restliche Streckenlänge bis zum Ziel.</summary>
    public double DistanceToGoNm { get; init; }

    /// <summary>"fms" = nächster Wegpunkt aus dem FMS (z. B. nach DIR TO), "plan" = aus der SimBrief-Route.</summary>
    public string NextSource { get; init; } = "plan";

    /// <summary>Das FMS fliegt einen Punkt an, der nicht im SimBrief-Plan steht.</summary>
    public bool FmsWaypointNotInPlan { get; init; }

    public IReadOnlyList<string> Following { get; init; } = [];

    public double TotalDistanceNm { get; init; }

    public RouteMarker? TopOfClimb { get; init; }

    public RouteMarker? TopOfDescent { get; init; }

    public double? PlannedFuelHereKg { get; init; }

    public double? ActualFuelKg { get; init; }

    public double? FuelDifferenceKg { get; init; }

    public double? PredictedLandingFuelKg { get; init; }

    /// <summary>SID/STAR oder Flughafennähe – dort weicht die Flugbahn oft vom Plan ab (Vektoren).</summary>
    public bool InTerminalArea { get; init; }

    public bool OnRoute => NextSource == "fms" || Math.Abs(Route.CrossTrackNm) <= (InTerminalArea ? 10 : 3);
}

public static class ProgressCalculator
{
    /// <summary>So nah muss der vom FMS angeflogene Punkt an einem Plan-Fix liegen.</summary>
    public const double FmsMatchToleranceNm = 3;

    public static FlightProgress Compute(
        Ofp ofp,
        IReadOnlyList<RoutePoint> points,
        RouteProgress progress,
        double? actualFuelKg,
        FmsWaypointInput? fms = null)
    {
        var nextIndex = progress.NextPointIndex;
        var nextDistance = progress.DistanceToNextNm;
        var source = "plan";
        var notInPlan = false;

        if (fms is not null)
        {
            if (MatchFmsWaypoint(points, progress, fms) is { } matched)
            {
                nextIndex = matched;
                nextDistance = fms.DistanceNm;
                source = "fms";
            }
            else
            {
                notInPlan = true;
            }
        }

        var next = points[nextIndex];
        var prev = points[Math.Max(0, nextIndex - 1)];
        var nextFix = next.FixIndex >= 0 ? ofp.Fixes[next.FixIndex] : null;
        var toGo = nextDistance + LegsFrom(points, nextIndex);

        // Treibstoff: Planwert an der aktuellen Position, linear zwischen den Fixes der Plan-Geometrie
        var planPrev = points[progress.LegIndex];
        var planNext = points[progress.NextPointIndex];
        var fuelPrev = planPrev.FixIndex >= 0 ? ofp.Fixes[planPrev.FixIndex].FuelPlanOnboardKg : ofp.Fuel.PlanTakeoffKg;
        var fuelNext = planNext.FixIndex >= 0 ? ofp.Fixes[planNext.FixIndex].FuelPlanOnboardKg : null;
        double? plannedHere = fuelPrev is { } fp && fuelNext is { } fn ? fp + (fn - fp) * progress.LegFraction : fuelNext ?? fuelPrev;
        double? diff = actualFuelKg is { } a && plannedHere is { } p ? a - p : null;
        double? predictedLanding = ofp.Fuel.PlanLandingKg is { } landing && diff is { } d ? landing + d : null;

        return new FlightProgress
        {
            Route = progress,
            Previous = prev,
            Next = next,
            NextFix = nextFix,
            DistanceToNextNm = nextDistance,
            DistanceToGoNm = toGo,
            NextSource = source,
            FmsWaypointNotInPlan = notInPlan,
            Following = points.Skip(nextIndex + 1).Take(3).Select(pt => pt.Ident).ToList(),
            TotalDistanceNm = progress.DistanceFlownNm + toGo,
            TopOfClimb = Marker(points, nextIndex, nextDistance, "TOC"),
            TopOfDescent = Marker(points, nextIndex, nextDistance, "TOD"),
            PlannedFuelHereKg = plannedHere,
            ActualFuelKg = actualFuelKg,
            FuelDifferenceKg = diff,
            PredictedLandingFuelKg = predictedLanding,
            InTerminalArea = nextFix?.IsSidStar == true || progress.LegIndex == 0 || nextIndex == points.Count - 1,
        };
    }

    /// <summary>
    /// Ordnet den vom FMS angeflogenen Punkt (Position + Peilung + Distanz) einem Fix der Route zu.
    /// Die FBW-Peilung kann rechtweisend oder missweisend sein – beides wird probiert, der nähere Treffer gewinnt.
    /// </summary>
    public static int? MatchFmsWaypoint(IReadOnlyList<RoutePoint> points, RouteProgress progress, FmsWaypointInput fms)
    {
        if (fms.DistanceNm <= 0 || points.Count < 2)
        {
            return null;
        }

        int? best = null;
        var bestDistance = FmsMatchToleranceNm;
        foreach (var bearing in new[] { fms.BearingDeg, fms.BearingDeg + fms.MagVariationDeg })
        {
            var (lat, lon) = RouteMath.Destination(fms.AircraftLat, fms.AircraftLon, bearing, fms.DistanceNm);

            // Ab dem aktuellen Abschnitt suchen – DIR TO springt nach vorn, selten zurück
            for (var i = Math.Max(1, progress.LegIndex); i < points.Count; i++)
            {
                var distance = RouteMath.DistanceNm(lat, lon, points[i].Lat, points[i].Lon);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        return best;
    }

    private static double LegsFrom(IReadOnlyList<RoutePoint> points, int index)
    {
        var sum = 0.0;
        for (var i = index; i + 1 < points.Count; i++)
        {
            sum += RouteMath.DistanceNm(points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon);
        }

        return sum;
    }

    /// <summary>Streckenlänge bis zum nächsten Punkt mit dieser Kennung vor dem Flugzeug, sonst null.</summary>
    private static RouteMarker? Marker(IReadOnlyList<RoutePoint> points, int nextIndex, double distanceToNext, string ident)
    {
        var distance = distanceToNext;
        for (var i = nextIndex; i < points.Count; i++)
        {
            if (points[i].Ident.Equals(ident, StringComparison.OrdinalIgnoreCase))
            {
                return new RouteMarker(ident, distance);
            }

            if (i + 1 < points.Count)
            {
                distance += RouteMath.DistanceNm(points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon);
            }
        }

        return null;
    }
}
