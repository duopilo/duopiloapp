namespace Duopilo.Traffic;

/// <summary>Wo ein neuer Anflug in die Reihe kommt (<see cref="ArrivalSequence.Plan"/>).</summary>
/// <param name="SpawnNm">Spawn so weit vor der Schwelle.</param>
/// <param name="Behind">Hinter diesem Anflug (Kennung), null = keiner vor ihm oder weit genug weg.</param>
/// <param name="SpacingNm">Abstand zum vorderen Anflug, 0 = keiner.</param>
/// <param name="Reason">Woher der Abstand kommt: wake (Wirbelschleppen bzw. Radarstaffelung), departure_gap (Lücke für einen Abflug),
/// departing (ein Abflug rollt gerade auf die Bahn oder startet), sonst free.</param>
public sealed record ArrivalSlot(double SpawnNm, string? Behind, double SpacingNm, string Reason);

/// <summary>
/// Takt der Landungen auf einer Bahn und Mischbetrieb mit Starts (eine Bahn für beides wie meist in Düsseldorf). Anflüge kommen mit
/// Abstand nach Wirbelschleppen-Kategorie hintereinander (ICAO, vereinfacht; sonst <see cref="MinSpacingNm"/> Radarstaffelung). Wartet
/// ein Abflug derselben Bahn, bekommt er eine Lücke von <see cref="DepartureGapNm"/>. Ein Abflug reiht sich erst auf, wenn die nächste
/// Landung weiter als <see cref="DepartureReleaseNm"/> weg ist; ist die Bahn bei <see cref="GoAroundDecisionNm"/> noch belegt, startet
/// die Landung durch.
/// </summary>
public static class ArrivalSequence
{
    /// <summary>Radarstaffelung im Endanflug, wenn keine Wirbelschleppen-Staffelung mehr verlangt.</summary>
    public const double MinSpacingNm = 3;

    /// <summary>
    /// Lücke für einen Abflug zwischen zwei Landungen: Die vordere räumt die Bahn in gut einer Minute (die hintere fliegt dabei gut 2 NM),
    /// danach braucht der Abflug noch rund <see cref="DepartureReleaseNm"/> bis zur nächsten Landung.
    /// </summary>
    public const double DepartureGapNm = 8;

    /// <summary>Weiter draußen spawnt kein Anflug.</summary>
    public const double MaxSpawnNm = 30;

    /// <summary>Annahme: so schnell (über Grund) kommt eine Landung im Endanflug näher – A320 mit Vapp + 5 und etwas Höhe.</summary>
    public const double FinalGroundSpeedKt = 145;

    /// <summary>Ab dieser Entfernung vor der Schwelle prüft eine Landung, ob die Bahn frei ist.</summary>
    public const double LandingCheckNm = 2;

    /// <summary>Ist die Bahn hier (rund 180 ft über der Bahn) noch belegt, startet die Landung durch; danach landet sie.</summary>
    public const double GoAroundDecisionNm = 0.4;

    /// <summary>Abflug: vom Startlauf bis 500 ft über der Bahn (ab da zählt er nicht mehr als „auf der Bahn“).</summary>
    private const double ClimbTo500Seconds = 12;

    /// <summary>Abflug: Reserve für die Abfrage und das Losrollen.</summary>
    private const double ReleaseReserveSeconds = 20;

    /// <summary>Kürzester und weitester Abstand der nächsten Landung beim Aufreihen.</summary>
    private const double MinReleaseNm = 4;

    private const double MaxReleaseNm = 8;

    /// <summary>
    /// Abstand im Endanflug hinter <paramref name="leader"/> (ICAO Doc 4444, vereinfacht): hinter Super 4–8 NM, hinter Heavy 4–6 NM,
    /// Light hinter Medium 5 NM, sonst <see cref="MinSpacingNm"/>.
    /// </summary>
    public static double SpacingNm(TrafficWake leader, TrafficWake follower) => (leader, follower) switch
    {
        (TrafficWake.Super, TrafficWake.Super) => 4,
        (TrafficWake.Super, TrafficWake.Heavy) => 6,
        (TrafficWake.Super, TrafficWake.Medium) => 7,
        (TrafficWake.Super, TrafficWake.Light) => 8,
        (TrafficWake.Heavy, TrafficWake.Heavy) => 4,
        (TrafficWake.Heavy, TrafficWake.Medium) => 5,
        (TrafficWake.Heavy, TrafficWake.Light) => 6,
        (TrafficWake.Medium, TrafficWake.Light) => 5,
        _ => MinSpacingNm,
    };

    /// <summary>
    /// Abflug: So weit muss die nächste Landung beim Aufreihen noch weg sein. Aufreihen über <paramref name="lineUpM"/> (mit
    /// <see cref="TaxiKinematics.LineUpKt"/>, Kurven eingerechnet), kurz stehen, Startlauf bis <paramref name="rotateKt"/> mit
    /// <paramref name="accelMps2"/>, Steigen auf 500 ft und Reserve – die Landung fliegt solange mit <see cref="FinalGroundSpeedKt"/> und
    /// braucht die freie Bahn erst bei <see cref="GoAroundDecisionNm"/>.
    /// </summary>
    public static double DepartureReleaseNm(double lineUpM, double rotateKt, double accelMps2)
    {
        var lineUp = Math.Max(0, lineUpM) / (TaxiKinematics.LineUpKt * 0.514444) * 1.25;
        var roll = Math.Max(0, rotateKt) * 0.514444 / Math.Max(0.5, accelMps2);
        var seconds = lineUp + TrafficKinematics.LineupSeconds + roll + ClimbTo500Seconds + ReleaseReserveSeconds;
        return Math.Clamp(GoAroundDecisionNm + (seconds * FinalGroundSpeedKt / 3600), MinReleaseNm, MaxReleaseNm);
    }

    /// <summary>
    /// Platz für einen neuen Anflug: gewünscht <paramref name="wantedNm"/> vor der Schwelle, aber mit Abstand hinter dem letzten der
    /// <paramref name="arrivals"/> (Kennung, Entfernung, Kategorie – noch in der Luft vor der Schwelle) und – wartet ein Abflug
    /// (<paramref name="departureWaiting"/>) – mit der Lücke dafür. Rollt gerade ein Abflug auf die Bahn oder startet, nicht näher als
    /// <paramref name="departingReleaseNm"/>. Höchstens <see cref="MaxSpawnNm"/>.
    /// </summary>
    public static ArrivalSlot Plan(
        double wantedNm,
        IEnumerable<(string Tail, double DistanceNm, TrafficWake Wake)> arrivals,
        TrafficWake own,
        bool departureWaiting = false,
        double? departingReleaseNm = null)
    {
        var spawn = wantedNm;
        string? behind = null;
        var spacing = 0.0;
        var reason = "free";
        var last = arrivals.OrderByDescending(a => a.DistanceNm).FirstOrDefault();
        if (last.Tail is not null)
        {
            spacing = SpacingNm(last.Wake, own);
            var gap = departureWaiting && DepartureGapNm > spacing;
            if (gap)
            {
                spacing = DepartureGapNm;
            }

            if (last.DistanceNm + spacing > spawn)
            {
                spawn = last.DistanceNm + spacing;
                behind = last.Tail;
                reason = gap ? "departure_gap" : "wake";
            }
        }

        if (departingReleaseNm is { } release && release > spawn)
        {
            spawn = release;
            reason = "departing";
        }

        return new ArrivalSlot(Math.Round(Math.Min(spawn, MaxSpawnNm), 1), behind, spacing, reason);
    }
}
