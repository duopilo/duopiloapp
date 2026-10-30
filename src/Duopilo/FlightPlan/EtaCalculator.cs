namespace Duopilo.FlightPlan;

/// <summary>Restzeiten in Sekunden ab jetzt (null = nicht berechenbar).</summary>
public sealed record EtaPrediction
{
    /// <summary><see cref="EtaCalculator.SimBrief"/> oder <see cref="EtaCalculator.GroundSpeed"/>.</summary>
    public required string Source { get; init; }

    public double? NextSec { get; init; }

    public double? DestinationSec { get; init; }

    public double? TopOfClimbSec { get; init; }

    public double? TopOfDescentSec { get; init; }
}

/// <summary>
/// ETA nach SimBrief-Navlog: geplante Zeiten der restlichen Fixes (mit den geplanten Winden, Steig- und Sinkflug),
/// korrigiert um die Abweichung zum Plan am letzten überflogenen Fix. Ohne verwertbare Navlog-Zeiten: Distanz durch Groundspeed.
/// </summary>
public static class EtaCalculator
{
    public const string SimBrief = "simbrief";

    public const string GroundSpeed = "groundspeed";

    public static EtaPrediction Predict(Ofp ofp, IReadOnlyList<RoutePoint> points, FlightProgress progress, double? groundSpeedKt, FixPassage? lastPassage = null) =>
        FromPlan(ofp, points, progress, lastPassage) ?? FromGroundSpeed(progress, groundSpeedKt);

    private static EtaPrediction? FromPlan(Ofp ofp, IReadOnlyList<RoutePoint> points, FlightProgress progress, FixPassage? lastPassage)
    {
        var next = progress.NextIndex;
        var destination = points.Count - 1;
        if (next < 1 || next > destination
            || PlanTime(ofp, points[next - 1]) is not { } prevTime
            || PlanTime(ofp, points[next]) is not { } nextTime
            || PlanTime(ofp, points[destination]) is not { } destinationTime
            || nextTime < prevTime || destinationTime < nextTime)
        {
            return null;
        }

        // Bis zum nächsten Fix im geplanten Tempo des Abschnitts, der dort endet (passt auch nach DIR TO)
        var legNm = RouteMath.DistanceNm(points[next - 1].Lat, points[next - 1].Lon, points[next].Lat, points[next].Lon);
        var legTime = nextTime - prevTime;
        var toNext = legNm < 0.05 ? 0 : progress.DistanceToNextNm * legTime / legNm;

        // Abweichung am letzten überflogenen Fix: Überflugzeit dort + Planzeit ab dort (wie im Flugdurchführungsplan).
        // Ist man auf dem laufenden Abschnitt schon langsamer als geplant, gilt die Planzeit ab der aktuellen Position –
        // die Restzeit wird nie kürzer als der Plan ab hier.
        var notYetCredited = lastPassage is { } p && p.PointIndex == next - 1 ? Math.Max(0, legTime - p.ElapsedSec - toNext) : 0;

        double? Remaining(int index) =>
            index >= next && index <= destination && PlanTime(ofp, points[index]) is { } t && t >= nextTime
                ? toNext + notYetCredited + (t - nextTime)
                : null;

        double? toc = null, tod = null;
        if ((progress.TopOfClimb is { } c && (toc = Remaining(c.PointIndex)) is null)
            || (progress.TopOfDescent is { } d && (tod = Remaining(d.PointIndex)) is null))
        {
            return null;
        }

        return new EtaPrediction
        {
            Source = SimBrief,
            NextSec = toNext + notYetCredited,
            DestinationSec = Remaining(destination),
            TopOfClimbSec = toc,
            TopOfDescentSec = tod,
        };
    }

    private static EtaPrediction FromGroundSpeed(FlightProgress progress, double? groundSpeedKt)
    {
        double? Time(double? nm) => groundSpeedKt is > 50 && nm is { } d ? d / groundSpeedKt.Value * 3600 : null;

        return new EtaPrediction
        {
            Source = GroundSpeed,
            NextSec = Time(progress.DistanceToNextNm),
            DestinationSec = Time(progress.DistanceToGoNm),
            TopOfClimbSec = Time(progress.TopOfClimb?.DistanceNm),
            TopOfDescentSec = Time(progress.TopOfDescent?.DistanceNm),
        };
    }

    /// <summary>Geplante Zeit ab Abheben (Navlog time_total); der Startflughafen ist 0.</summary>
    private static double? PlanTime(Ofp ofp, RoutePoint point) =>
        point.FixIndex < 0 ? 0 : point.FixIndex < ofp.Fixes.Count ? ofp.Fixes[point.FixIndex].TimeTotalSec : null;
}
