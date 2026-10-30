namespace Duopilo.FlightPlan;

/// <summary>Ein Fix der Route, wie ihn der Fuel Check braucht (Name für die Ansage, Phase laut SimBrief).</summary>
/// <param name="Stage">SimBrief-Phase des Abschnitts, der an diesem Fix endet: CLB, CRZ oder DSC (null = unbekannt).</param>
public sealed record FuelCheckFix(string Ident, string? Name, string? Type, string? Stage, double Lat, double Lon);

/// <summary>
/// Treibstoff gegen den SimBrief-Plan an der aktuellen Position – Grundlage für den Fuel Check im Monitoring.
/// Wird vom SnapshotFactory alle paar Sekunden neu gerechnet (dieselbe Rechnung wie get_progress und das Dashboard).
/// </summary>
public sealed record FuelPlanSample
{
    /// <summary>Kennung des Plans (SimBrief request_id) – ein neuer Plan beginnt den Fuel Check von vorn.</summary>
    public required string PlanId { get; init; }

    /// <summary>Wann die Stichprobe gerechnet wurde.</summary>
    public DateTimeOffset TimeUtc { get; init; }

    /// <summary>Aktueller Abschnitt (Punkt LegIndex → LegIndex+1) in der Punktliste der Route.</summary>
    public int LegIndex { get; init; }

    /// <summary>Der zuletzt erreichte Punkt der Route (Anfang des aktuellen Abschnitts), null am Startflughafen.</summary>
    public FuelCheckFix? LastFix { get; init; }

    /// <summary>Der Punkt, auf den das Flugzeug zufliegt (Ende des aktuellen Abschnitts).</summary>
    public FuelCheckFix? NextFix { get; init; }

    public double CrossTrackNm { get; init; }

    public double DistanceFlownNm { get; init; }

    /// <summary>SID/STAR oder Flughafennähe.</summary>
    public bool InTerminalArea { get; init; }

    /// <summary>Treibstoff an Bord, als die Stichprobe gerechnet wurde.</summary>
    public double ActualKg { get; init; }

    /// <summary>Planwert an der Position (linear zwischen den Fixes).</summary>
    public double PlannedKg { get; init; }

    /// <summary>Ist minus Plan (negativ = weniger als geplant).</summary>
    public double DiffKg => ActualKg - PlannedKg;

    /// <summary>Geplante Landemenge laut OFP.</summary>
    public double? PlannedLandingKg { get; init; }

    /// <summary>Prognose: geplante Landemenge plus heutige Abweichung.</summary>
    public double? PredictedLandingKg => PlannedLandingKg is { } p ? p + DiffKg : null;

    /// <summary>Mindestmenge am Ziel (SimBrief fuel_min_onboard am letzten Fix, sonst Ausweich- plus Endreserve).</summary>
    public double? MinimumLandingKg { get; init; }

    /// <summary>Baut die Stichprobe aus dem Fortschritt. Null ohne Ist- oder Planwert.</summary>
    public static FuelPlanSample? From(Ofp ofp, IReadOnlyList<RoutePoint> points, RouteProgress route, FlightProgress progress, DateTimeOffset timeUtc)
    {
        if (progress.ActualFuelKg is not { } actual || progress.PlannedFuelHereKg is not { } planned || points.Count < 2)
        {
            return null;
        }

        return new FuelPlanSample
        {
            PlanId = PlanIdOf(ofp),
            TimeUtc = timeUtc,
            LegIndex = route.LegIndex,
            LastFix = Fix(ofp, points[route.LegIndex]),
            NextFix = Fix(ofp, points[route.NextPointIndex]),
            CrossTrackNm = route.CrossTrackNm,
            DistanceFlownNm = route.DistanceFlownNm,
            InTerminalArea = progress.InTerminalArea,
            ActualKg = actual,
            PlannedKg = planned,
            PlannedLandingKg = ofp.Fuel.PlanLandingKg,
            MinimumLandingKg = MinimumLanding(ofp),
        };
    }

    public static string PlanIdOf(Ofp ofp) =>
        ofp.RequestId ?? $"{ofp.Origin.Icao}-{ofp.Destination.Icao}-{ofp.GeneratedUtc?.ToUnixTimeSeconds()}-{ofp.Fixes.Count}";

    /// <summary>Mindestmenge bei der Landung: Navlog am Ziel, sonst Ausweich- plus Endreserve, sonst nur die Endreserve.</summary>
    public static double? MinimumLanding(Ofp ofp)
    {
        if (ofp.Fixes.Count > 0 && ofp.Fixes[^1].FuelMinOnboardKg is { } min && min > 0)
        {
            return min;
        }

        return ofp.Fuel.ReserveKg is { } reserve ? reserve + (ofp.Fuel.AlternateBurnKg ?? 0) : null;
    }

    private static FuelCheckFix? Fix(Ofp ofp, RoutePoint point)
    {
        if (point.FixIndex < 0)
        {
            return null;
        }

        var f = ofp.Fixes[point.FixIndex];
        return new FuelCheckFix(point.Ident, f.Name, f.Type, f.Stage?.ToUpperInvariant(), point.Lat, point.Lon);
    }
}
