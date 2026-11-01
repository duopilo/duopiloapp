using System.Globalization;
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
/// <param name="AltitudeDeviationFt">Höhe minus Gleitpfad (positiv = zu hoch) – auch bei tiefer gelegten Wegpunkten gegen den Pfad.</param>
/// <param name="PitchDeg">Längsneigung, Nase hoch positiv.</param>
public sealed record TrafficTestGate(string Name, double IndicatedKt, double TargetKt, double AltitudeDeviationFt, double CrossM, double PitchDeg);

/// <summary>Erster Bodenkontakt nach dem Anflug.</summary>
/// <param name="AlongM">Hinter der Schwelle (erster Boden-Wegpunkt: <see cref="TrafficTestPlan.TouchdownM"/>).</param>
/// <param name="SinkRateFpm">Sinkrate des letzten Messpunkts in der Luft (positiv = sinkend).</param>
/// <param name="PitchDeg">Längsneigung des letzten Messpunkts in der Luft, Nase hoch positiv.</param>
/// <param name="FlarePitchGainDeg">
/// Wie weit die Nase in den letzten <see cref="TrafficTestAnalysis.FlareWindowSeconds"/> s vor dem Aufsetzen über die mittlere Neigung
/// zwischen 2 und 1 NM ging – ab etwa 2° hat die KI abgefangen, um 0 ist sie ohne Abfangen aufgesetzt. Null ohne Messpunkte dort.
/// </param>
public sealed record TrafficTestTouchdown(
    double Seconds,
    double AlongM,
    double CrossM,
    double IndicatedKt,
    double GroundSpeedKt,
    double SinkRateFpm,
    double PitchDeg,
    double? FlarePitchGainDeg);

/// <summary>Durchstarten des Testflugzeugs (<see cref="TrafficGoAround"/>).</summary>
/// <param name="Reason">high_at_threshold, fast_at_threshold oder no_touchdown_by.</param>
/// <param name="Value">ft über dem Gleitpfad, kt über Vapp oder die Grenze in m hinter der Schwelle.</param>
/// <param name="AlongM">Wo durchgestartet wurde, ab der Schwelle.</param>
/// <param name="HeightFt">Höhe über der Bahn beim Durchstarten.</param>
/// <param name="LowestFt">Tiefster Punkt danach (über der Bahn) – sinkt die KI erst weiter, liegt er unter <paramref name="HeightFt"/>.</param>
/// <param name="HighestFt">Höchster Punkt danach (über der Bahn).</param>
/// <param name="GearUpSeconds">Sekunden nach dem Durchstarten, bis das Fahrwerk ganz drin war; null, solange es draußen ist.</param>
/// <param name="Reached">Ob die KI <see cref="TrafficGoAroundPlan.EndFt"/> über der Bahn erreicht hat.</param>
public sealed record TrafficTestGoAround(
    string Reason,
    double Value,
    double Seconds,
    double AlongM,
    double HeightFt,
    double IndicatedKt,
    double LowestFt,
    double HighestFt,
    double? GearUpSeconds,
    bool Reached);

/// <summary>Start (Variante <see cref="TrafficTestVariant.B1"/>).</summary>
/// <param name="RollStartSeconds">Sekunde, ab der das Flugzeug rollte (über 5 kt Groundspeed).</param>
/// <param name="LiftoffAlongM">Abhebepunkt ab dem Bahnanfang.</param>
/// <param name="LiftoffPitchDeg">Längsneigung beim Abheben, Nase hoch positiv – um 0 heißt: nicht rotiert.</param>
/// <param name="RollDistanceM">Startstrecke vom Spawn bis zum Abheben.</param>
/// <param name="MaxRollCrossM">Größte Querablage zur Mittellinie im Startlauf.</param>
/// <param name="HeightOverRunwayEndFt">Höhe über der Bahn am Bahnende (0 = dort noch am Boden).</param>
/// <param name="ClimbFpm">Mittlere Steigrate vom Abheben bis 1500 ft über der Bahn.</param>
/// <param name="MaxClimbPitchDeg">Größte Längsneigung bis 1500 ft.</param>
/// <param name="MaxClimbBankDeg">Größte Querneigung (Betrag) bis 1500 ft.</param>
/// <param name="GearUpSeconds">Sekunden nach dem Abheben, bis das Fahrwerk ganz drin war; null, solange es draußen ist.</param>
/// <param name="GearUpFt">Höhe über der Bahn, als das Fahrwerk drin war.</param>
/// <param name="HighestFt">Höchster Punkt über der Bahn.</param>
/// <param name="LightsAtLiftoff">Eingeschaltete Lichter beim Abheben.</param>
public sealed record TrafficTestTakeoff(
    double? RollStartSeconds,
    double? LiftoffSeconds,
    double? LiftoffAlongM,
    double? LiftoffCrossM,
    double? LiftoffKt,
    double? LiftoffPitchDeg,
    double? RollDistanceM,
    double? MaxRollCrossM,
    double? HeightOverRunwayEndFt,
    double? ClimbFpm,
    double? MaxClimbPitchDeg,
    double? MaxClimbBankDeg,
    double? GearUpSeconds,
    double? GearUpFt,
    double HighestFt,
    string? LightsAtLiftoff);

/// <summary>Auswertung eines Testanflugs.</summary>
/// <param name="Verdict">
/// stopped (gelandet und angehalten), landed, floated (schwebt über der Bahn), airborne, go_around (durchgestartet, nicht gelandet),
/// go_around_ignored (durchgestartet, aber doch aufgesetzt), no_data. Beim Start (B1): climbed (auf
/// <see cref="TrafficTestPlan.ClimbEndFt"/> gestiegen), climbing, rolling, no_roll (nicht losgerollt), no_liftoff (am Boden über das
/// Bahnende).
/// </param>
/// <param name="MeanSpeedDeviationKt">Mittlere Abweichung IAS minus Wunsch im Anflug (positiv = zu schnell).</param>
/// <param name="GearExtendedAtNm">Entfernung zur Schwelle, als das Fahrwerk ganz draußen war (0 = erst hinter der Schwelle).</param>
/// <param name="GearExtendedAtFt">Höhe über der Bahn, als das Fahrwerk ganz draußen war (0 = erst am Boden).</param>
/// <param name="GearDownInTime">
/// Ob das Fahrwerk bis <see cref="TrafficTestPlan.GearDownByFt"/> über der Bahn draußen war; null, solange es offen ist (noch nicht
/// draußen, aber auch noch nicht darunter).
/// </param>
/// <param name="FloatSeconds">Sekunden unter 30 ft über Grund in der Luft, weiter als 300 m hinter dem geplanten Aufsetzpunkt.</param>
/// <param name="StopShortOfLastWaypointM">Wie weit die KI vor dem letzten Rollwegpunkt angehalten hat.</param>
/// <param name="ApproachPitchDeg">Mittlere Längsneigung zwischen 2 und 1 NM (Nase hoch positiv).</param>
/// <param name="MaxFinalBankDeg">Größte Querneigung (Betrag) in der Luft auf den letzten 2 NM bis zum Aufsetzen.</param>
/// <param name="LightsOnFinal">Eingeschaltete Lichter des letzten Messpunkts in der Luft (z. B. "nav beacon landing taxi strobe logo").</param>
/// <param name="LightsAfterLanding">Eingeschaltete Lichter des letzten Messpunkts am Boden nach dem Aufsetzen.</param>
/// <param name="GoAround">Durchstarten, falls die KI durchgestartet ist.</param>
/// <param name="Takeoff">Start, nur bei Variante B1.</param>
public sealed record TrafficTestSummary(
    string Verdict,
    int Samples,
    double? MeanSpeedDeviationKt,
    double? MaxSpeedDeviationKt,
    IReadOnlyList<TrafficTestGate> Gates,
    double? GearExtendedAtNm,
    double? GearExtendedAtFt,
    bool? GearDownInTime,
    TrafficTestTouchdown? Touchdown,
    int FloatSeconds,
    bool AirborneBeyondRunwayEnd,
    bool OverranOnGround,
    double? MaxFinalCrossM,
    double? MaxRolloutCrossM,
    double? StopAlongM,
    double? StopShortOfLastWaypointM,
    double? ApproachPitchDeg,
    double? MaxFinalBankDeg,
    string? LightsOnFinal,
    string? LightsAfterLanding,
    IReadOnlyList<string> AiStates,
    TrafficTestGoAround? GoAround = null,
    TrafficTestTakeoff? Takeoff = null);

/// <summary>
/// Wertet die Messpunkte eines Testanflugs aus (rein rechnerisch, ohne SimConnect): Lage zur Mittellinie, Geschwindigkeit gegenüber
/// dem Wunsch, Fahrwerk, Aufsetzen, Schweben, Halt. Nicht threadsicher – der Dienst ruft sie unter seiner Sperre auf.
/// </summary>
public sealed class TrafficTestAnalysis(TrafficTestPlan plan, DateTimeOffset spawnUtc)
{
    /// <summary>Zeitfenster vor dem Aufsetzen, in dem die Auswertung das Abfangen sucht.</summary>
    public const double FlareWindowSeconds = 10;

    private const double FloatAglFt = 30;

    private const double FloatMarginM = 300;

    private const double StoppedKt = 1;

    /// <summary>Start: ab dieser Groundspeed rollt das Flugzeug.</summary>
    private const double RollStartKt = 5;

    /// <summary>Start: Abheben zählt erst ab dieser Fahrt (falls der Sim das Flugzeug beim Spawn kurz in der Luft meldet).</summary>
    private const double LiftoffMinKt = 30;

    /// <summary>Start: Steigrate, Längs- und Querneigung werden bis zu dieser Höhe über der Bahn ausgewertet.</summary>
    private const double InitialClimbFt = 1500;

    private readonly List<TrafficTestGate> _gates = [];
    private readonly List<string> _states = [];
    private readonly Queue<(double Seconds, double PitchDeg)> _recentPitch = new();
    private double _approachPitchSum;
    private int _approachPitchCount;
    private double? _maxFinalBank;
    private double _lastAirbornePitch;
    private int? _lightsOnFinal;
    private int? _lightsAfterLanding;
    private double _speedDeviationSum;
    private int _speedDeviationCount;
    private double? _maxSpeedDeviation;
    private double? _gearExtendedAtNm;
    private double? _gearExtendedAtFt;
    private bool _belowGearHeightWithoutGear;
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
    private TrafficGoAroundReason? _goAroundReason;
    private double _goAroundSeconds;
    private double _goAroundAlong;
    private double _goAroundHeight;
    private double _goAroundIas;
    private double _goAroundLowest;
    private double _goAroundHighest;
    private double? _gearUpSeconds;
    private double? _rollStartSeconds;
    private double? _liftoffSeconds;
    private double _liftoffAlong;
    private double _liftoffCross;
    private double _liftoffKt;
    private double _liftoffPitch;
    private int _lightsAtLiftoff;
    private double? _maxRollCross;
    private double? _heightOverEnd;
    private double _climbVsSum;
    private int _climbVsCount;
    private double? _maxClimbPitch;
    private double? _maxClimbBank;
    private double? _takeoffGearUpSeconds;
    private double? _takeoffGearUpFt;
    private double _highestFt;
    private bool _backOnGround;

    public int Samples { get; private set; }

    /// <summary>Ob das Flugzeug nach dem Anflug aufgesetzt hat.</summary>
    public bool TouchedDown => _touchdown is not null;

    public TrafficTestPlan Plan => plan;

    /// <summary>Start: ob das Flugzeug losgerollt ist.</summary>
    public bool RollStarted => _rollStartSeconds is not null;

    /// <summary>Start: ob das Flugzeug abgehoben hat.</summary>
    public bool LiftedOff => _liftoffSeconds is not null;

    /// <summary>Wegpunkte des Durchstartens, nachdem <see cref="MarkGoAround"/> es gemeldet hat; sonst null.</summary>
    public TrafficGoAroundPlan? GoAroundPlan { get; private set; }

    /// <summary>Sekunde des Durchstartens, sonst null.</summary>
    public double? GoAroundSeconds => GoAroundPlan is null ? null : _goAroundSeconds;

    /// <summary>
    /// Der Dienst hat das Durchstarten eingeleitet: Ab jetzt gelten die Geschwindigkeiten von <paramref name="goAround"/>, die Auswertung
    /// verfolgt Steigflug und Fahrwerk, und das Urteil heißt go_around.
    /// </summary>
    public void MarkGoAround(double seconds, double alongM, double heightFt, double indicatedKt, TrafficGoAroundReason reason, TrafficGoAroundPlan goAround)
    {
        if (GoAroundPlan is not null)
        {
            return;
        }

        GoAroundPlan = goAround;
        _goAroundReason = reason;
        _goAroundSeconds = seconds;
        _goAroundAlong = alongM;
        _goAroundHeight = heightFt;
        _goAroundIas = indicatedKt;
        _goAroundLowest = heightFt;
        _goAroundHighest = heightFt;
    }

    public TrafficTestPoint Add(AiProbeSample s)
    {
        Samples++;
        var seconds = Math.Max(0, (s.Utc - spawnUtc).TotalSeconds);
        var dt = _previousSeconds is { } p ? Math.Clamp(seconds - p, 0, 5) : 0;
        var (along, cross) = plan.Runway.Offsets(s.Latitude, s.Longitude);
        var next = plan.Next(along);
        var targetKt = GoAroundPlan?.TargetKt(along) ?? next.Waypoint.SpeedKt;
        var events = new List<string>();

        if (s.AiState is { } state && (_states.Count == 0 || _states[^1] != state))
        {
            _states.Add(state);
            events.Add("ai_state:" + state);
        }

        var heightFt = s.OnGround ? 0 : Math.Max(0, s.AltitudeFt - plan.Runway.ElevationFt);
        if (plan.IsTakeoff)
        {
            AddTakeoff(s, seconds, along, cross, heightFt, events);
            _previousAlong = along;
            _previousSeconds = seconds;
            return new TrafficTestPoint(Math.Round(seconds, 1), along, cross, null, targetKt, events);
        }

        if (_gearExtendedAtNm is null && s.GearExtendedPercent >= 99)
        {
            _gearExtendedAtNm = along >= 0 ? 0 : Math.Round(-along / RunwayEnd.MetersPerNm, 1);
            _gearExtendedAtFt = Math.Round(heightFt / 10) * 10;
            events.Add(string.Create(CultureInfo.InvariantCulture, $"gear_extended at {_gearExtendedAtFt:0} ft"));
        }
        else if (_gearExtendedAtNm is null && heightFt < TrafficTestPlan.GearDownByFt && !_belowGearHeightWithoutGear && Samples > 1)
        {
            _belowGearHeightWithoutGear = true;
            events.Add(string.Create(CultureInfo.InvariantCulture, $"gear_not_down_by {TrafficTestPlan.GearDownByFt:0} ft"));
        }

        if (GoAroundPlan is not null && seconds > _goAroundSeconds)
        {
            _goAroundLowest = Math.Min(_goAroundLowest, heightFt);
            _goAroundHighest = Math.Max(_goAroundHighest, heightFt);
            if (_gearUpSeconds is null && s.GearExtendedPercent <= 1 && !s.OnGround)
            {
                _gearUpSeconds = Math.Round(seconds - _goAroundSeconds, 1);
                events.Add(string.Create(CultureInfo.InvariantCulture, $"gear_retracted at {Math.Round(heightFt / 10) * 10:0} ft"));
            }
        }

        if (!s.OnGround)
        {
            _wasAirborne = true;
            _lastAirborneVs = s.VerticalSpeedFpm;
            _lastAirbornePitch = s.PitchDeg;
            if (_touchdown is null)
            {
                _lightsOnFinal = s.LightStates;
            }

            if (_touchdown is null)
            {
                _recentPitch.Enqueue((seconds, s.PitchDeg));
                while (_recentPitch.Count > 0 && _recentPitch.Peek().Seconds < seconds - FlareWindowSeconds)
                {
                    _recentPitch.Dequeue();
                }

                if (along >= -2 * RunwayEnd.MetersPerNm)
                {
                    _maxFinalBank = Math.Max(_maxFinalBank ?? 0, Math.Abs(s.BankDeg));
                }
            }

            if (along >= -2 * RunwayEnd.MetersPerNm && along <= -1 * RunwayEnd.MetersPerNm)
            {
                _approachPitchSum += s.PitchDeg;
                _approachPitchCount++;
            }

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

            if (s.AltitudeAglFt < FloatAglFt && along > plan.TouchdownM + FloatMarginM && GoAroundPlan is null)
            {
                _floatSeconds += dt;
            }

            if (along > plan.Runway.LengthM && !_airborneBeyondEnd && GoAroundPlan is null)
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
                double? flareGain = _approachPitchCount > 0 && _recentPitch.Count > 0
                    ? Math.Round(_recentPitch.Max(p => p.PitchDeg) - (_approachPitchSum / _approachPitchCount), 1)
                    : null;
                _touchdown = new TrafficTestTouchdown(
                    Math.Round(seconds, 1),
                    Math.Round(along),
                    Math.Round(cross, 1),
                    Math.Round(s.IndicatedKt),
                    Math.Round(s.GroundSpeedKt),
                    Math.Round(-_lastAirborneVs),
                    Math.Round(_lastAirbornePitch, 1),
                    flareGain);
                events.Add("touchdown");
            }

            if (_touchdown is not null)
            {
                _lightsAfterLanding = s.LightStates;
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
        return new TrafficTestPoint(Math.Round(seconds, 1), along, cross, distance, targetKt, events);
    }

    public TrafficTestSummary Summary()
    {
        var verdict = Samples == 0 ? "no_data"
            : plan.IsTakeoff ? (_liftoffSeconds is null
                ? (_overran ? "no_liftoff" : _rollStartSeconds is null ? "no_roll" : "rolling")
                : _highestFt >= TrafficTestPlan.ClimbEndFt ? "climbed" : "climbing")
            : GoAroundPlan is not null ? (_touchdown is null ? "go_around" : "go_around_ignored")
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
            _gearExtendedAtFt,
            _gearExtendedAtFt is { } gearFt ? gearFt >= TrafficTestPlan.GearDownByFt : _belowGearHeightWithoutGear ? false : null,
            _touchdown,
            (int)Math.Round(_floatSeconds),
            _airborneBeyondEnd,
            _overran,
            _maxFinalCross is { } f ? Math.Round(f, 1) : null,
            _maxRolloutCross is { } r ? Math.Round(r, 1) : null,
            _stopAlong is { } a ? Math.Round(a) : null,
            _stopAlong is { } stop ? Math.Round(lastWaypoint - stop) : null,
            _approachPitchCount > 0 ? Math.Round(_approachPitchSum / _approachPitchCount, 1) : null,
            _maxFinalBank is { } bank ? Math.Round(bank, 1) : null,
            _lightsOnFinal is { } final ? AiLights.Describe(final) : null,
            _lightsAfterLanding is { } after ? AiLights.Describe(after) : null,
            [.. _states],
            _goAroundReason is { } reason
                ? new TrafficTestGoAround(
                    reason.Code,
                    reason.Value,
                    Math.Round(_goAroundSeconds, 1),
                    Math.Round(_goAroundAlong),
                    Math.Round(_goAroundHeight),
                    Math.Round(_goAroundIas),
                    Math.Round(_goAroundLowest),
                    Math.Round(_goAroundHighest),
                    _gearUpSeconds,
                    _goAroundHighest >= TrafficGoAroundPlan.EndFt)
                : null,
            plan.IsTakeoff
                ? new TrafficTestTakeoff(
                    _rollStartSeconds,
                    _liftoffSeconds,
                    _liftoffSeconds is null ? null : Math.Round(_liftoffAlong),
                    _liftoffSeconds is null ? null : Math.Round(_liftoffCross, 1),
                    _liftoffSeconds is null ? null : Math.Round(_liftoffKt),
                    _liftoffSeconds is null ? null : Math.Round(_liftoffPitch, 1),
                    _liftoffSeconds is null ? null : Math.Round(_liftoffAlong - TrafficTestPlan.LineUpM),
                    _maxRollCross is { } rc ? Math.Round(rc, 1) : null,
                    _heightOverEnd,
                    _climbVsCount > 0 ? Math.Round(_climbVsSum / _climbVsCount) : null,
                    _maxClimbPitch is { } cp ? Math.Round(cp, 1) : null,
                    _maxClimbBank is { } cb ? Math.Round(cb, 1) : null,
                    _takeoffGearUpSeconds,
                    _takeoffGearUpFt,
                    Math.Round(_highestFt),
                    _liftoffSeconds is null ? null : AiLights.Describe(_lightsAtLiftoff))
                : null);
    }

    /// <summary>Start: Losrollen, Startlauf, Abheben, Bahnende, Steigflug bis 1500 ft, Fahrwerk rein.</summary>
    private void AddTakeoff(AiProbeSample s, double seconds, double along, double cross, double heightFt, List<string> events)
    {
        if (_rollStartSeconds is null && s.GroundSpeedKt > RollStartKt)
        {
            _rollStartSeconds = Math.Round(seconds, 1);
            events.Add("roll_start");
        }

        if (_heightOverEnd is null && along >= plan.Runway.LengthM)
        {
            _heightOverEnd = Math.Round(heightFt / 10) * 10;
            events.Add(string.Create(CultureInfo.InvariantCulture, $"runway_end at {_heightOverEnd:0} ft"));
        }

        _highestFt = Math.Max(_highestFt, heightFt);
        if (_liftoffSeconds is null)
        {
            if (s.OnGround)
            {
                _maxRollCross = Math.Max(_maxRollCross ?? 0, Math.Abs(cross));
                if (along > plan.Runway.LengthM && !_overran)
                {
                    _overran = true;
                    events.Add("overrun");
                }
            }
            else if (_rollStartSeconds is not null && s.IndicatedKt >= LiftoffMinKt)
            {
                _liftoffSeconds = Math.Round(seconds, 1);
                _liftoffAlong = along;
                _liftoffCross = cross;
                _liftoffKt = s.IndicatedKt;
                _liftoffPitch = s.PitchDeg;
                _lightsAtLiftoff = s.LightStates;
                events.Add(string.Create(CultureInfo.InvariantCulture, $"liftoff at {along:0} m, {s.IndicatedKt:0} kt"));
            }

            return;
        }

        if (s.OnGround)
        {
            if (!_backOnGround)
            {
                _backOnGround = true;
                events.Add("back_on_ground");
            }

            return;
        }

        if (heightFt < InitialClimbFt)
        {
            _climbVsSum += s.VerticalSpeedFpm;
            _climbVsCount++;
            _maxClimbPitch = Math.Max(_maxClimbPitch ?? s.PitchDeg, s.PitchDeg);
            _maxClimbBank = Math.Max(_maxClimbBank ?? 0, Math.Abs(s.BankDeg));
        }

        if (_takeoffGearUpSeconds is null && s.GearExtendedPercent <= 1)
        {
            _takeoffGearUpSeconds = Math.Round(seconds - _liftoffSeconds.Value, 1);
            _takeoffGearUpFt = Math.Round(heightFt / 10) * 10;
            events.Add(string.Create(CultureInfo.InvariantCulture, $"gear_retracted at {_takeoffGearUpFt:0} ft"));
        }
    }

    /// <summary>Überflogene Messpunkte des Anflugs (…nm und Schwelle) zwischen vorigem und aktuellem Messpunkt.</summary>
    private void RecordGates(AiProbeSample s, double along, double cross)
    {
        if (_previousAlong is not { } previous)
        {
            return;
        }

        foreach (var gate in plan.Checkpoints)
        {
            // Halber Meter Toleranz, damit ein Messpunkt genau auf dem Wegpunkt nicht an Rundung scheitert
            var at = gate.AlongM - 0.5;
            if (previous < at && along >= at)
            {
                _gates.Add(new TrafficTestGate(
                    gate.Name,
                    Math.Round(s.IndicatedKt),
                    gate.TargetKt,
                    Math.Round(s.AltitudeFt - gate.GlidePathFt),
                    Math.Round(cross, 1),
                    Math.Round(s.PitchDeg, 1)));
            }
        }
    }
}
