namespace Duopilo.Traffic;

/// <summary>Wo ein neuer Anflug in die Reihe kommt (<see cref="ArrivalSequence.Plan"/>).</summary>
/// <param name="SpawnNm">Spawn so weit vor der Schwelle.</param>
/// <param name="Behind">Hinter diesem Anflug (Kennung), null = keiner vor ihm oder weit genug weg.</param>
/// <param name="SpacingNm">Abstand zum vorderen Anflug, 0 = keiner.</param>
/// <param name="Reason">Woher der Abstand kommt: wake (Wirbelschleppen bzw. Radarstaffelung), departure_gap (Lücke für einen Abflug),
/// departing (ein Abflug rollt gerade auf die Bahn oder startet), sonst free.</param>
/// <param name="GapFor">departure_gap: für diesen Abflug (Kennung).</param>
public sealed record ArrivalSlot(double SpawnNm, string? Behind, double SpacingNm, string Reason, string? GapFor = null);

/// <summary>Ein Abflug, der auf eine Lücke zwischen zwei Landungen wartet.</summary>
/// <param name="Tail">Kennung.</param>
/// <param name="ReadySeconds">In so vielen Sekunden steht er am Haltepunkt bereit (0 = schon da).</param>
/// <param name="ReleaseNm">So weit muss die nächste Landung beim Aufreihen weg sein (<see cref="ArrivalSequence.DepartureReleaseNm"/>).</param>
public readonly record struct WaitingDeparture(string Tail, double ReadySeconds, double ReleaseNm);

/// <summary>
/// Takt der Landungen auf einer Bahn und Mischbetrieb mit Starts (eine Bahn für beides wie meist in Düsseldorf). Anflüge kommen mit
/// Abstand nach Wirbelschleppen-Kategorie hintereinander (ICAO, vereinfacht; sonst <see cref="MinSpacingNm"/> Radarstaffelung). Wartet
/// ein Abflug derselben Bahn, bekommt er eine Lücke, die zu seiner Ankunft am Haltepunkt passt. Ein Abflug reiht sich erst auf, wenn die nächste
/// Landung weiter als <see cref="DepartureReleaseNm"/> weg ist; ist die Bahn bei <see cref="GoAroundDecisionNm"/> noch belegt, startet
/// die Landung durch.
/// </summary>
public static class ArrivalSequence
{
    /// <summary>Radarstaffelung im Endanflug, wenn keine Wirbelschleppen-Staffelung mehr verlangt.</summary>
    public const double MinSpacingNm = 3;

    /// <summary>Lücke für einen Abflug: so lange nach dem Aufsetzen hat die Landung davor die Bahn geräumt (EDDL: gut 40 s bis zum Abrollweg).</summary>
    public const double LeaderRunwaySeconds = 60;

    /// <summary>Lücke für einen Abflug: Reserve für die Abfrage und das Losrollen.</summary>
    public const double GapReserveSeconds = 20;

    /// <summary>Für eine Lücke kommt eine Landung höchstens so viel weiter draußen in die Reihe – sonst landet sie zuerst.</summary>
    public const double MaxGapExtraNm = 8;

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
    /// <paramref name="arrivals"/> (Kennung, Entfernung, Kategorie – noch in der Luft vor der Schwelle). Wartet ein Abflug
    /// (<paramref name="departure"/>), bekommt er eine Lücke, die zu ihm passt: Die Landung ist noch <see cref="WaitingDeparture.ReleaseNm"/>
    /// weg, wenn der Abflug bereit ist und die Landung davor die Bahn geräumt hat (<see cref="LeaderRunwaySeconds"/> nach dem Aufsetzen),
    /// plus <see cref="GapReserveSeconds"/> – aber nur, wenn sie dafür höchstens <see cref="MaxGapExtraNm"/> weiter draußen kommt;
    /// sonst landet sie zuerst. Rollt gerade ein Abflug auf die Bahn oder startet, nicht näher als <paramref name="departingReleaseNm"/>.
    /// Höchstens <see cref="MaxSpawnNm"/>.
    /// </summary>
    public static ArrivalSlot Plan(
        double wantedNm,
        IEnumerable<(string Tail, double DistanceNm, TrafficWake Wake)> arrivals,
        TrafficWake own,
        WaitingDeparture? departure = null,
        double? departingReleaseNm = null)
    {
        var spawn = wantedNm;
        string? behind = null;
        string? gapFor = null;
        var spacing = 0.0;
        var reason = "free";
        var last = arrivals.OrderByDescending(a => a.DistanceNm).FirstOrDefault();
        if (last.Tail is not null)
        {
            spacing = SpacingNm(last.Wake, own);
            if (last.DistanceNm + spacing > spawn)
            {
                spawn = last.DistanceNm + spacing;
                behind = last.Tail;
                reason = "wake";
            }
        }

        if (departure is { } d)
        {
            var leaderClear = last.Tail is null ? 0 : (last.DistanceNm / FinalGroundSpeedKt * 3600) + LeaderRunwaySeconds;
            var gapNm = d.ReleaseNm + ((Math.Max(d.ReadySeconds, leaderClear) + GapReserveSeconds) * FinalGroundSpeedKt / 3600);
            if (gapNm > spawn && gapNm - spawn <= MaxGapExtraNm)
            {
                spawn = gapNm;
                behind = last.Tail;
                spacing = last.Tail is null ? 0 : gapNm - last.DistanceNm;
                reason = "departure_gap";
                gapFor = d.Tail;
            }
        }

        if (departingReleaseNm is { } release && release > spawn)
        {
            spawn = release;
            reason = "departing";
        }

        return new ArrivalSlot(Math.Round(Math.Min(spawn, MaxSpawnNm), 1), behind, Math.Round(spacing, 1), reason, gapFor);
    }
}
