namespace Kopilot.FlightPlan;

/// <summary>Markante Punkte der Route (TOC/TOD) vor dem Flugzeug.</summary>
public sealed record RouteMarker(string Ident, double DistanceNm);

/// <summary>Fortschritt gegen den SimBrief-Plan an der aktuellen Position.</summary>
public sealed record FlightProgress
{
    public required RouteProgress Route { get; init; }

    public required RoutePoint Previous { get; init; }

    public required RoutePoint Next { get; init; }

    public OfpFix? NextFix { get; init; }

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

    public bool OnRoute => Math.Abs(Route.CrossTrackNm) <= (InTerminalArea ? 10 : 3);
}

public static class ProgressCalculator
{
    public static FlightProgress Compute(Ofp ofp, IReadOnlyList<RoutePoint> points, RouteProgress progress, double? actualFuelKg)
    {
        var next = points[progress.NextPointIndex];
        var prev = points[progress.LegIndex];
        var nextFix = next.FixIndex >= 0 ? ofp.Fixes[next.FixIndex] : null;

        // Treibstoff: Planwert an der aktuellen Position, linear zwischen den Fixes
        var fuelPrev = prev.FixIndex >= 0 ? ofp.Fixes[prev.FixIndex].FuelPlanOnboardKg : ofp.Fuel.PlanTakeoffKg;
        var fuelNext = nextFix?.FuelPlanOnboardKg;
        double? plannedHere = fuelPrev is { } fp && fuelNext is { } fn ? fp + (fn - fp) * progress.LegFraction : fuelNext ?? fuelPrev;
        double? diff = actualFuelKg is { } a && plannedHere is { } p ? a - p : null;
        double? predictedLanding = ofp.Fuel.PlanLandingKg is { } landing && diff is { } d ? landing + d : null;

        return new FlightProgress
        {
            Route = progress,
            Previous = prev,
            Next = next,
            NextFix = nextFix,
            Following = points.Skip(progress.NextPointIndex + 1).Take(3).Select(pt => pt.Ident).ToList(),
            TotalDistanceNm = progress.DistanceFlownNm + progress.DistanceToGoNm,
            TopOfClimb = Marker(points, progress, "TOC"),
            TopOfDescent = Marker(points, progress, "TOD"),
            PlannedFuelHereKg = plannedHere,
            ActualFuelKg = actualFuelKg,
            FuelDifferenceKg = diff,
            PredictedLandingFuelKg = predictedLanding,
            InTerminalArea = nextFix?.IsSidStar == true || progress.LegIndex == 0 || progress.NextPointIndex == points.Count - 1,
        };
    }

    /// <summary>Streckenlänge bis zum nächsten Punkt mit dieser Kennung vor dem Flugzeug, sonst null.</summary>
    private static RouteMarker? Marker(IReadOnlyList<RoutePoint> points, RouteProgress progress, string ident)
    {
        var distance = progress.DistanceToNextNm;
        for (var i = progress.NextPointIndex; i < points.Count; i++)
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
