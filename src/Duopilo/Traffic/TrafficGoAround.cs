using System.Globalization;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Grund für ein Durchstarten des Testflugzeugs.</summary>
/// <param name="Code">high_at_threshold, fast_at_threshold oder no_touchdown_by.</param>
/// <param name="Value">Wie weit drüber: ft über dem Gleitpfad, kt über Vapp oder die Grenze in m hinter der Schwelle.</param>
/// <param name="Text">Für Ereignis und Log, z. B. "high_at_threshold +180 ft".</param>
public sealed record TrafficGoAroundReason(string Code, double Value, string Text);

/// <summary>
/// Durchstarten: geradeaus in Bahnrichtung, Steigflug auf 1500 ft und dann 3000 ft über der Bahn. Die Wegpunkte ersetzen die des
/// Anflugs; ob die KI dafür ihren Landemodus (STATE_SIMPLE_LANDING) verlässt, zeigt erst der Test im Sim.
/// </summary>
/// <param name="FromAlongM">Wo durchgestartet wurde, ab der Schwelle (davor negativ).</param>
public sealed record TrafficGoAroundPlan(double FromAlongM, IReadOnlyList<TrafficTestWaypoint> Waypoints)
{
    /// <summary>Erster Wegpunkt: so hoch über der Bahn, 3 NM hinter dem Durchstartpunkt.</summary>
    public const double FirstFt = 1500;

    /// <summary>Zielhöhe über der Bahn ab 6 NM hinter dem Durchstartpunkt.</summary>
    public const double AltitudeFt = 3000;

    /// <summary>Ab dieser Höhe über der Bahn ist das Durchstarten gelungen und der Test endet.</summary>
    public const double EndFt = 2800;

    /// <summary>Kommt die KI so lange nach dem Durchstarten nicht auf <see cref="EndFt"/>, endet der Test trotzdem.</summary>
    public const double TimeoutSeconds = 300;

    /// <summary>Fahrwerk rein, sobald die KI so schnell steigt (positive rate).</summary>
    public const double PositiveClimbFpm = 300;

    public static TrafficGoAroundPlan Build(TrafficTestPlan plan, double fromAlongM)
    {
        var start = Math.Max(fromAlongM, 0);
        var runway = plan.Runway;
        var vapp = plan.ApproachKt;
        var flags = AiWaypointFlags.SpeedRequested | AiWaypointFlags.ComputeVerticalSpeed;
        var waypoints = new List<TrafficTestWaypoint>();
        foreach (var (name, nm, heightFt, speedKt) in new[]
        {
            ("ga1", 3.0, FirstFt, vapp + 15),
            ("ga2", 6.0, AltitudeFt, vapp + 35),
            ("ga3", 12.0, AltitudeFt, vapp + 50),
        })
        {
            var along = start + (nm * RunwayEnd.MetersPerNm);
            var (lat, lon) = runway.Point(along);
            waypoints.Add(new TrafficTestWaypoint(name, along, new AiWaypoint(lat, lon, runway.ElevationFt + heightFt, Math.Min(speedKt, 250), flags)));
        }

        return new TrafficGoAroundPlan(fromAlongM, waypoints);
    }

    /// <summary>Geschwindigkeit des nächsten Wegpunkts vor einer Position, am Ende die des letzten.</summary>
    public double TargetKt(double alongM) =>
        (Waypoints.FirstOrDefault(w => w.AlongM > alongM + 1) ?? Waypoints[^1]).Waypoint.SpeedKt;
}

/// <summary>
/// Wann das Testflugzeug durchstartet, statt zu landen: an der Schwelle mehr als <see cref="HighFt"/> über dem Gleitpfad oder mehr als
/// <see cref="FastKt"/> über Vapp, oder bis <see cref="LongLandingM"/> hinter der Schwelle noch nicht aufgesetzt. Hintergrund: Die KI
/// kreuzte die Schwelle in den ersten Tests bis 190 ft zu hoch (ATR 72 weit drüber) und schwebte teils weit über die Bahn.
/// </summary>
public static class TrafficGoAround
{
    public const double HighFt = 150;

    public const double FastKt = 20;

    /// <summary>Ende der Aufsetzzone auf langen Bahnen.</summary>
    public const double MaxLongLandingM = 900;

    /// <summary>Mindestens so weit hinter dem ersten Boden-Wegpunkt, falls der weit hinten liegt (touchdownM).</summary>
    public const double BeyondTouchdownM = 300;

    /// <summary>Spätester Aufsetzpunkt: 900 m, auf kurzen Bahnen ein Drittel der Länge, mindestens 300 m hinter dem Boden-Wegpunkt.</summary>
    public static double LongLandingM(TrafficTestPlan plan) =>
        Math.Max(Math.Min(MaxLongLandingM, plan.Runway.LengthM / 3), plan.TouchdownM + BeyondTouchdownM);

    /// <summary>
    /// Grund zum Durchstarten für diesen Messpunkt, sonst null. Schwelle: am ersten Messpunkt in der Luft hinter ihr
    /// (<paramref name="previousAlongM"/> davor), gemessen wie das Gate "threshold" der Auswertung.
    /// </summary>
    public static TrafficGoAroundReason? Check(
        TrafficTestPlan plan,
        double? previousAlongM,
        double alongM,
        double altitudeFt,
        double indicatedKt,
        bool onGround,
        bool touchedDown)
    {
        if (onGround || touchedDown)
        {
            return null;
        }

        if (previousAlongM is < 0 && alongM >= 0)
        {
            var high = altitudeFt - TrafficTestPlan.GlidePathFt(plan.Runway, 0);
            if (high > HighFt)
            {
                return new("high_at_threshold", Math.Round(high), string.Create(CultureInfo.InvariantCulture, $"high_at_threshold +{high:0} ft"));
            }

            var fast = indicatedKt - plan.ApproachKt;
            if (fast > FastKt)
            {
                return new("fast_at_threshold", Math.Round(fast), string.Create(CultureInfo.InvariantCulture, $"fast_at_threshold +{fast:0} kt"));
            }
        }

        var limit = LongLandingM(plan);
        if (alongM > limit)
        {
            return new("no_touchdown_by", Math.Round(limit), string.Create(CultureInfo.InvariantCulture, $"no_touchdown_by {limit:0} m"));
        }

        return null;
    }
}
