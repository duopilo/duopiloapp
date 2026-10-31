using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Ein ausgewerteter Messpunkt der Testlinie.</summary>
/// <param name="Seconds">Sekunden seit dem Spawn.</param>
/// <param name="AlongM">Entfernung zur Schwelle entlang der Mittellinie (davor negativ).</param>
/// <param name="CrossM">Querablage zur Mittellinie (rechts positiv).</param>
/// <param name="DistanceToThresholdNm">Entfernung zur Schwelle vor dem Aufsetzen, sonst null.</param>
/// <param name="TargetKt">Geschwindigkeit des nächsten Wegpunkts.</param>
/// <param name="Events">Ereignisse dieses Messpunkts (touchdown, gear_extended, stopped …).</param>
public sealed record TrafficTestPoint(
    double Seconds,
    double AlongM,
    double CrossM,
    double? DistanceToThresholdNm,
    double TargetKt,
    IReadOnlyList<string> Events);

/// <summary>Werte beim Überfliegen eines Wegpunkts im Anflug.</summary>
/// <param name="AltitudeDeviationFt">Höhe minus geplante Höhe (positiv = zu hoch).</param>
public sealed record TrafficTestGate(string Name, double IndicatedKt, double TargetKt, double AltitudeDeviationFt, double CrossM);

/// <summary>Erster Bodenkontakt nach dem Anflug.</summary>
/// <param name="AlongM">Hinter der Schwelle (geplant: <see cref="TrafficTestPlan.TouchdownM"/>).</param>
/// <param name="SinkRateFpm">Sinkrate des letzten Messpunkts in der Luft (positiv = sinkend).</param>
public sealed record TrafficTestTouchdown(double Seconds, double AlongM, double CrossM, double IndicatedKt, double GroundSpeedKt, double SinkRateFpm);

/// <summary>Auswertung eines Testanflugs.</summary>
/// <param name="Verdict">stopped (gelandet und angehalten), landed, floated (schwebt über der Bahn), airborne, no_data.</param>
/// <param name="MeanSpeedDeviationKt">Mittlere Abweichung IAS minus Wunsch im Anflug (positiv = zu schnell).</param>
/// <param name="GearExtendedAtNm">Entfernung zur Schwelle, als das Fahrwerk ganz draußen war (0 = erst hinter der Schwelle).</param>
/// <param name="FloatSeconds">Sekunden unter 30 ft über Grund in der Luft, weiter als 300 m hinter dem geplanten Aufsetzpunkt.</param>
/// <param name="StopShortOfLastWaypointM">Wie weit die KI vor dem letzten Rollwegpunkt angehalten hat.</param>
public sealed record TrafficTestSummary(
    string Verdict,
    int Samples,
    double? MeanSpeedDeviationKt,
    double? MaxSpeedDeviationKt,
    IReadOnlyList<TrafficTestGate> Gates,
    double? GearExtendedAtNm,
    TrafficTestTouchdown? Touchdown,
    int FloatSeconds,
    bool AirborneBeyondRunwayEnd,
    bool OverranOnGround,
    double? MaxFinalCrossM,
    double? MaxRolloutCrossM,
    double? StopAlongM,
    double? StopShortOfLastWaypointM,
    IReadOnlyList<string> AiStates);

/// <summary>
/// Wertet die Messpunkte eines Testanflugs aus (rein rechnerisch, ohne SimConnect): Lage zur Mittellinie, Geschwindigkeit gegenüber
/// dem Wunsch, Fahrwerk, Aufsetzen, Schweben, Halt. Nicht threadsicher – der Dienst ruft sie unter seiner Sperre auf.
/// </summary>
public sealed class TrafficTestAnalysis(TrafficTestPlan plan, DateTimeOffset spawnUtc)
{
    private const double FloatAglFt = 30;

    private const double FloatMarginM = 300;

    private const double StoppedKt = 1;

    private readonly List<TrafficTestGate> _gates = [];
    private readonly List<string> _states = [];
    private double _speedDeviationSum;
    private int _speedDeviationCount;
    private double? _maxSpeedDeviation;
    private double? _gearExtendedAtNm;
    private TrafficTestTouchdown? _touchdown;
    private double _floatSeconds;
    private bool _airborneBeyondEnd;
    private bool _overran;
    private double? _maxFinalCross;
    private double? _maxRolloutCross;
    private double? _stopAlong;
    private bool _wasAirborne;
    private double? _previousAlong;
    private double? _previousSeconds;
    private double _lastAirborneVs;

    public int Samples { get; private set; }

    public TrafficTestPlan Plan => plan;

    public TrafficTestPoint Add(AiProbeSample s)
    {
        Samples++;
        var seconds = Math.Max(0, (s.Utc - spawnUtc).TotalSeconds);
        var dt = _previousSeconds is { } p ? Math.Clamp(seconds - p, 0, 5) : 0;
        var (along, cross) = plan.Runway.Offsets(s.Latitude, s.Longitude);
        var next = plan.Next(along);
        var events = new List<string>();

        if (s.AiState is { } state && (_states.Count == 0 || _states[^1] != state))
        {
            _states.Add(state);
            events.Add("ai_state:" + state);
        }

        if (_gearExtendedAtNm is null && s.GearExtendedPercent >= 99)
        {
            _gearExtendedAtNm = along >= 0 ? 0 : Math.Round(-along / RunwayEnd.MetersPerNm, 1);
            events.Add("gear_extended");
        }

        if (!s.OnGround)
        {
            _wasAirborne = true;
            _lastAirborneVs = s.VerticalSpeedFpm;

            if (along < 0)
            {
                var deviation = s.IndicatedKt - next.Waypoint.SpeedKt;
                _speedDeviationSum += deviation;
                _speedDeviationCount++;
                if (_maxSpeedDeviation is null || Math.Abs(deviation) > Math.Abs(_maxSpeedDeviation.Value))
                {
                    _maxSpeedDeviation = deviation;
                }
            }

            if (along >= -2 * RunwayEnd.MetersPerNm && along <= 0)
            {
                _maxFinalCross = Math.Max(_maxFinalCross ?? 0, Math.Abs(cross));
            }

            if (s.AltitudeAglFt < FloatAglFt && along > plan.TouchdownM + FloatMarginM)
            {
                _floatSeconds += dt;
            }

            if (along > plan.Runway.LengthM && !_airborneBeyondEnd)
            {
                _airborneBeyondEnd = true;
                events.Add("airborne_beyond_runway_end");
            }

            RecordGates(s, along, cross);
        }
        else
        {
            if (_wasAirborne && _touchdown is null)
            {
                _touchdown = new TrafficTestTouchdown(
                    Math.Round(seconds, 1),
                    Math.Round(along),
                    Math.Round(cross, 1),
                    Math.Round(s.IndicatedKt),
                    Math.Round(s.GroundSpeedKt),
                    Math.Round(-_lastAirborneVs));
                events.Add("touchdown");
            }

            if (_touchdown is not null)
            {
                _maxRolloutCross = Math.Max(_maxRolloutCross ?? 0, Math.Abs(cross));
                if (along > plan.Runway.LengthM && !_overran)
                {
                    _overran = true;
                    events.Add("overrun");
                }

                if (_stopAlong is null && s.GroundSpeedKt < StoppedKt)
                {
                    _stopAlong = along;
                    events.Add("stopped");
                }
            }
        }

        _previousAlong = along;
        _previousSeconds = seconds;
        var distance = _touchdown is null && along < 0 ? Math.Round(-along / RunwayEnd.MetersPerNm, 2) : (double?)null;
        return new TrafficTestPoint(Math.Round(seconds, 1), along, cross, distance, next.Waypoint.SpeedKt, events);
    }

    public TrafficTestSummary Summary()
    {
        var verdict = Samples == 0 ? "no_data"
            : _touchdown is null ? (_floatSeconds >= 5 || _airborneBeyondEnd ? "floated" : "airborne")
            : _stopAlong is not null ? "stopped"
            : "landed";
        var lastWaypoint = plan.Waypoints[^1].AlongM;
        return new TrafficTestSummary(
            verdict,
            Samples,
            _speedDeviationCount > 0 ? Math.Round(_speedDeviationSum / _speedDeviationCount, 1) : null,
            _maxSpeedDeviation is { } m ? Math.Round(m, 1) : null,
            [.. _gates],
            _gearExtendedAtNm,
            _touchdown,
            (int)Math.Round(_floatSeconds),
            _airborneBeyondEnd,
            _overran,
            _maxFinalCross is { } f ? Math.Round(f, 1) : null,
            _maxRolloutCross is { } r ? Math.Round(r, 1) : null,
            _stopAlong is { } a ? Math.Round(a) : null,
            _stopAlong is { } stop ? Math.Round(lastWaypoint - stop) : null,
            [.. _states]);
    }

    /// <summary>Überflogene Wegpunkte des Anflugs (…nm und Schwelle) zwischen vorigem und aktuellem Messpunkt.</summary>
    private void RecordGates(AiProbeSample s, double along, double cross)
    {
        if (_previousAlong is not { } previous)
        {
            return;
        }

        foreach (var gate in plan.Waypoints)
        {
            if (gate.AlongM > 0 || gate.Waypoint.Flags.HasFlag(AiWaypointFlags.OnGround))
            {
                continue;
            }

            if (previous < gate.AlongM && along >= gate.AlongM)
            {
                _gates.Add(new TrafficTestGate(
                    gate.Name,
                    Math.Round(s.IndicatedKt),
                    gate.Waypoint.SpeedKt,
                    Math.Round(s.AltitudeFt - gate.Waypoint.AltitudeFt),
                    Math.Round(cross, 1)));
            }
        }
    }
}
