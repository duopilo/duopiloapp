using Duopilo.FlightPlan;

namespace Duopilo.Atc;

/// <summary>
/// Verkehr im Duopilo-ATC, wie ihn echte Lotsen erwähnen: Departure, Center und Approach melden Verkehr von sich aus, wenn er
/// nahe kommt ("traffic 2 o'clock, 6 miles, crossing left to right, A320, 1000 feet above, climbing"), beantworten Rückfragen
/// des Piloten ("request traffic information", "confirm traffic 12 o'clock") und der Tower nennt die Reihenfolge im Endanflug
/// ("number two, follow the A320 on 5 miles final"). Die Lage kommt mit <see cref="AtcInput.Traffic"/>; ohne sie meldet ATC
/// keinen Verkehr. Der Verkehr in MSFS wird nicht wirklich gestaffelt – gemeldet wird, was die Abfrage zeigt.
/// </summary>
public sealed partial class AtcSession
{
    /// <summary>So weit voraus wird eine Annäherung angesagt.</summary>
    private const double TrafficInfoLeadSeconds = 180;

    /// <summary>Rückfragen: Umkreis und Höhenband (mit "above"/"below" oder Uhrzeit wie der TCAS-Bereich darüber/darunter).</summary>
    private const double TrafficQueryRangeNm = 20;

    private const double TrafficQueryAltitudeFt = 3000;

    private const double TrafficQueryAltitudeWideFt = 9900;

    /// <summary>Endanflug: Vorausfliegende höchstens so weit vom Platz, Kurs und Peilung höchstens so weit abweichend.</summary>
    private const double FinalMaxNm = 15;

    private const double FinalAlignDeg = 40;

    /// <summary>Folgt der Pilot jemandem, kommt die Landefreigabe spätestens so nah am Platz (statt <see cref="AtcRules.LandingClearanceFallbackNm"/>).</summary>
    private const double SequencedLandingFallbackNm = 2;

    /// <summary>Zwischen zwei Traffic-Infos mindestens so viel Zeit …</summary>
    private static readonly TimeSpan TrafficInfoMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>… und so lange Funkstille davor (ATC redet nicht in einen laufenden Austausch).</summary>
    private static readonly TimeSpan TrafficInfoQuiet = TimeSpan.FromSeconds(15);

    /// <summary>Dasselbe Flugzeug wird frühestens nach dieser Zeit erneut gemeldet.</summary>
    private static readonly TimeSpan TrafficInfoRepeat = TimeSpan.FromMinutes(10);

    private readonly Dictionary<uint, DateTimeOffset> _trafficInformed = [];
    private DateTimeOffset? _lastTrafficInfoAt;
    private DateTimeOffset? _lastRadioAt;
    private uint? _sequenceTarget;

    /// <summary>Flugzeug, dem der Pilot im Endanflug folgt (Objekt-ID der Verkehrsabfrage), null = keins.</summary>
    public uint? SequenceTarget => _sequenceTarget;

    /// <summary>
    /// Traffic-Info von sich aus: nur bei Departure, Center und Approach nach dem Erstanruf, ohne offenes Readback, nach etwas
    /// Funkstille, höchstens einmal pro Minute und je Flugzeug einmal. Gemeldet wird das Flugzeug, das in den nächsten drei
    /// Minuten am nächsten herankommt (Höhenband und Abstand aus den Regeln).
    /// </summary>
    private void TickTrafficInfo(AtcInput input, List<AtcTransmission> output)
    {
        if (!Rules.TrafficInfo || output.Count > 0 || input.Traffic is not { } picture || input.OnGround != false
            || Phase is not (AtcPhase.Departure or AtcPhase.Center or AtcPhase.Approach)
            || Step is not (AtcStep.DepartureClimb or AtcStep.CenterCruise or AtcStep.ApproachVectoring or AtcStep.ApproachEstablished)
            || PendingReadback is not null || _oceanicReadback is not null || _heldAddress is not null)
        {
            return;
        }

        var now = input.TimeUtc;
        if ((_lastRadioAt is { } radio && now - radio < TrafficInfoQuiet)
            || (_lastTrafficInfoAt is { } last && now - last < TrafficInfoMinInterval))
        {
            return;
        }

        PruneTrafficInformed(now);
        AtcTrafficTarget? best = null;
        var bestTime = double.MaxValue;
        foreach (var target in picture.Targets)
        {
            if (target.OnGround || _trafficInformed.ContainsKey(target.ObjectId) || target.DistanceNm > Rules.TrafficInfoRangeNm
                || Math.Abs(target.AltitudeDiffFt) > Rules.TrafficInfoAltitudeFt)
            {
                continue;
            }

            if (AtcTrafficMath.ClosestApproach(picture, target, input.GroundSpeedKt) is not { Closing: true } closest
                || closest.TimeSec > TrafficInfoLeadSeconds || closest.MissNm > Rules.TrafficInfoMissNm)
            {
                continue;
            }

            if (closest.TimeSec < bestTime)
            {
                best = target;
                bestTime = closest.TimeSec;
            }
        }

        if (best is not null)
        {
            SayTrafficInfo(output, best, picture, now);
        }
    }

    /// <summary>
    /// Rückfrage des Piloten beantworten: das passende Flugzeug (Uhrzeit, "above"/"below", sonst das nächste in der Luft) als
    /// Traffic-Info, sonst "no reported traffic". Am Boden gibt es keine Traffic-Info.
    /// </summary>
    private void AnswerTrafficQuery(AtcTrafficQuery query, AtcInput input, List<AtcTransmission> output)
    {
        if (input.OnGround == false && input.Traffic is { } picture)
        {
            var altitude = query.Clock is not null || query.Vertical != 0 ? TrafficQueryAltitudeWideFt : TrafficQueryAltitudeFt;
            if (AtcTrafficMath.Match(picture, query, TrafficQueryRangeNm, altitude) is { } target
                && SayTrafficInfo(output, target, picture, input.TimeUtc) is not null)
            {
                return;
            }
        }

        Say(output, AtcMessageIds.CommonNoTraffic, null);
    }

    private AtcRendered? SayTrafficInfo(List<AtcTransmission> output, AtcTrafficTarget target, AtcTrafficPicture picture, DateTimeOffset now)
    {
        var rendered = Say(output, AtcMessageIds.CommonTrafficInfo, TrafficLocals(target, picture));
        if (rendered is not null)
        {
            _trafficInformed[target.ObjectId] = now;
            _lastTrafficInfoAt = now;
        }

        return rendered;
    }

    private static Dictionary<string, AtcValue> TrafficLocals(AtcTrafficTarget target, AtcTrafficPicture picture)
    {
        var locals = new Dictionary<string, AtcValue>
        {
            ["trafficClock"] = AtcTrafficMath.ClockValue(target.Clock),
            ["trafficDistance"] = AtcTrafficMath.DistanceValue(target.DistanceNm),
            ["trafficDirection"] = AtcTrafficMath.DirectionValue(picture.OwnHeadingTrueDeg, target.HeadingTrueDeg),
            ["trafficLevel"] = AtcTrafficMath.LevelValue(target.AltitudeDiffFt),
        };
        if (AtcTrafficMath.TypeValue(target.Type) is { } type)
        {
            locals["trafficType"] = type;
        }

        if (AtcTrafficMath.TrendValue(target.VerticalTrend) is { } trend)
        {
            locals["trafficTrend"] = trend;
        }

        return locals;
    }

    private void PruneTrafficInformed(DateTimeOffset now)
    {
        foreach (var id in _trafficInformed.Where(kv => now - kv.Value > TrafficInfoRepeat).Select(kv => kv.Key).ToList())
        {
            _trafficInformed.Remove(id);
        }
    }

    // ------------------------------------------------------------------ Endanflug

    /// <summary>
    /// Erstanruf beim Tower mit Verkehr vor einem im Endanflug: Reihenfolge nennen ("number two, follow the A320 on 5 miles
    /// final") statt sofort freizugeben. Rückgabe: true, wenn so beantwortet.
    /// </summary>
    private bool TrySequence(AtcInput input, List<AtcTransmission> output, DateTimeOffset now)
    {
        if (!Rules.TrafficSequencing || input.OnGround != false || input.Traffic is not { } picture)
        {
            return false;
        }

        var ahead = PrecedingOnFinal(input, picture);
        if (ahead.Count == 0)
        {
            return false;
        }

        var (follow, toDest) = ahead[0];
        var locals = new Dictionary<string, AtcValue>
        {
            ["sequence"] = AtcTrafficMath.SequenceValue(ahead.Count + 1),
            ["trafficFinal"] = AtcTrafficMath.FinalValue(toDest),
        };
        if (AtcTrafficMath.TypeValue(follow.Type) is { } type)
        {
            locals["trafficType"] = type;
        }

        if (Say(output, AtcMessageIds.TowerSequence, locals) is null)
        {
            return false;
        }

        _sequenceTarget = follow.ObjectId;
        _trafficInformed[follow.ObjectId] = now;
        SetStep(AtcStep.LandingSequenced, now);
        return true;
    }

    /// <summary>Vorausfliegender gelandet (am Boden oder aus der Abfrage verschwunden) oder nicht mehr vor einem im Anflug?</summary>
    private bool PrecedingLanded(AtcInput input)
    {
        if (_sequenceTarget is not { } id)
        {
            return true;
        }

        if (input.Traffic is not { } picture)
        {
            return false;
        }

        var target = picture.Targets.FirstOrDefault(t => t.ObjectId == id);
        if (target is null || target.OnGround)
        {
            return true;
        }

        // Durchgestartet oder abgedreht
        return !PrecedingOnFinal(input, picture).Any(a => a.Target.ObjectId == id);
    }

    /// <summary>
    /// Flugzeuge vor dem eigenen im Endanflug, das nächste (dem man folgt) zuerst: in der Luft, näher am Zielplatz, in derselben
    /// Richtung unterwegs, voraus, nicht im Steigflug (Abflüge) und nicht deutlich höher. Bis 2 nm vor dem Platz muss das Ziel
    /// außerdem auf den Platz zu fliegen.
    /// </summary>
    private List<(AtcTrafficTarget Target, double ToDestNm)> PrecedingOnFinal(AtcInput input, AtcTrafficPicture picture)
    {
        var result = new List<(AtcTrafficTarget Target, double ToDestNm)>();
        if (DistanceToDest(input) is not { } own || Plan.Ofp.Destination.Lat is not { } dlat || Plan.Ofp.Destination.Lon is not { } dlon)
        {
            return result;
        }

        foreach (var t in picture.Targets)
        {
            if (t.OnGround || t.VerticalTrend == "climbing" || t.AltitudeDiffFt > 1000)
            {
                continue;
            }

            var toDest = RouteMath.DistanceNm(t.Lat, t.Lon, dlat, dlon);
            if (toDest >= own - 0.5 || toDest > FinalMaxNm
                || AtcTrafficMath.AngleDiff(t.HeadingTrueDeg, picture.OwnHeadingTrueDeg) > FinalAlignDeg
                || AtcTrafficMath.AngleDiff(t.RelativeBearingDeg, 0) > FinalAlignDeg)
            {
                continue;
            }

            if (toDest >= 2 && AtcTrafficMath.AngleDiff(RouteMath.BearingDeg(t.Lat, t.Lon, dlat, dlon), t.HeadingTrueDeg) > FinalAlignDeg)
            {
                continue;
            }

            result.Add((t, toDest));
        }

        result.Sort((a, b) => b.ToDestNm.CompareTo(a.ToDestNm));
        return result;
    }

    /// <summary>Erstanruf bei einer neuen Station (nach einer Übergabe): wird zuerst normal beantwortet.</summary>
    private static bool IsContactStep(AtcStep step) =>
        step is AtcStep.DepartureContact or AtcStep.CenterContact or AtcStep.ApproachContact or AtcStep.LandingContact or AtcStep.ArrivalContact;
}
