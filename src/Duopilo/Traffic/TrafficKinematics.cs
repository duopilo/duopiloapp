using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Abschnitt der Kinematik.</summary>
public enum KinematicPhase
{
    /// <summary>Endanflug auf dem 3°-Pfad, Geschwindigkeit auf Vapp.</summary>
    Approach,

    /// <summary>Abfangen: Sinkrate nimmt mit der Höhe ab, Nase kommt hoch.</summary>
    Flare,

    /// <summary>Ausrollen: Nase runter, bremsen bis zum Haltepunkt.</summary>
    Rollout,

    /// <summary>Steht.</summary>
    Stopped,

    /// <summary>Hat die Bahn am Abrollpunkt mit Rollgeschwindigkeit erreicht – ab hier rollt <see cref="TaxiKinematics"/> weiter.</summary>
    Exited,

    /// <summary>Start: steht aufgereiht auf der Bahn, Triebwerke laufen hoch.</summary>
    Lineup,

    /// <summary>Start: Startlauf bis zur Rotationsgeschwindigkeit (Vr).</summary>
    TakeoffRoll,

    /// <summary>Start: rotieren – Nase hoch bis zum Abheben, Räder noch am Boden.</summary>
    Rotation,

    /// <summary>Start: Steigflug geradeaus, ab der Beschleunigungshöhe flacher und schneller, Abfangen auf 3000 ft über der Bahn.</summary>
    Climb,
}

/// <summary>Kennwerte der Kinematik je Kategorie.</summary>
/// <param name="ApproachPitchDeg">Längsneigung auf dem Gleitpfad, Nase hoch positiv.</param>
/// <param name="FlareHeightFt">Ab dieser Höhe der Räder über der Bahn fängt das Flugzeug ab.</param>
/// <param name="FlarePitchDeg">Längsneigung beim Aufsetzen.</param>
/// <param name="TouchdownSinkFpm">Sinkrate beim Aufsetzen.</param>
/// <param name="DecelKtPerSec">Wie schnell das Flugzeug im Anflug langsamer wird.</param>
/// <param name="MaxBrakeMps2">Stärkste Verzögerung beim Ausrollen (m/s²).</param>
public sealed record KinematicProfile(
    double ApproachPitchDeg,
    double FlareHeightFt,
    double FlarePitchDeg,
    double TouchdownSinkFpm,
    double DecelKtPerSec,
    double MaxBrakeMps2)
{
    /// <summary>Start: Beschleunigung im Startlauf (m/s²); auf kurzen Bahnen mehr, damit Vr bis 60 % der Bahn erreicht ist.</summary>
    public double TakeoffAccelMps2 { get; init; } = 2.0;

    /// <summary>Start: Rotationsgeschwindigkeit Vr über Vapp (A320: 135 + 5 = 140 kt; C172: 65 − 10 = 55 kt).</summary>
    public double RotateAboveVappKt { get; init; } = 5;

    /// <summary>Start: so schnell kommt die Nase beim Rotieren hoch (Airbus etwa 3°/s).</summary>
    public double RotateDegPerSec { get; init; } = 3;

    /// <summary>Start: bei dieser Längsneigung heben die Räder ab (A320 um 8°, Tailstrike erst ab rund 11,7°).</summary>
    public double LiftoffPitchDeg { get; init; } = 8;

    /// <summary>Start: Steigrate bis zur Beschleunigungshöhe.</summary>
    public double ClimbFpm { get; init; } = 2500;

    /// <summary>Start: Anstellwinkel im Steigflug bei Steiggeschwindigkeit – Längsneigung = Bahnneigung + Anstellwinkel (A320 um 15°).</summary>
    public double ClimbAoaDeg { get; init; } = 6;

    /// <summary>Start: Steiggeschwindigkeit über V2 (Verkehrsflugzeuge V2 + 10).</summary>
    public double ClimbAboveV2Kt { get; init; } = 10;

    /// <summary>Start: ab dieser Höhe über der Bahn flacher steigen und beschleunigen; null = gleichmäßig weiter (Allgemeine Luftfahrt).</summary>
    public double? AccelHeightFt { get; init; } = 1500;

    /// <summary>Start: Steigrate ab der Beschleunigungshöhe.</summary>
    public double AccelClimbFpm { get; init; } = 1500;

    /// <summary>Start: mehr Nase hoch nicht.</summary>
    public double MaxClimbPitchDeg { get; init; } = 18;

    /// <summary>Verkehrsflugzeuge: Airbus-typisch etwa 2,5° auf dem Pfad, Abfangen ab 30 ft, mit rund 5,5° aufsetzen (rund 450 m hinter der Schwelle).</summary>
    public static readonly KinematicProfile Airliner = new(2.5, 30, 5.5, 150, 1.5, 2.5);

    /// <summary>
    /// Allgemeine Luftfahrt: flacher Anflug mit mehr Nase hoch, Abfangen ab 15 ft. Start wie eine C172: Vr = Vapp − 10 (55 kt),
    /// abheben bei 6°, mit V2 = Vapp + 10 (75 kt, etwa Vy) und 700 ft/min steigen.
    /// </summary>
    public static readonly KinematicProfile GeneralAviation = new(3.0, 15, 6.0, 120, 1.5, 2.0)
    {
        TakeoffAccelMps2 = 1.4,
        RotateAboveVappKt = -10,
        LiftoffPitchDeg = 6,
        ClimbFpm = 700,
        ClimbAoaDeg = 4,
        ClimbAboveV2Kt = 0,
        AccelHeightFt = null,
        MaxClimbPitchDeg = 12,
    };

    public static KinematicProfile For(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? GeneralAviation : Airliner;
}

/// <summary>
/// Kinematik für den Endanflug: Ab der Übernahme (z. B. 4 NM) bewegt Duopilo das Flugzeug selbst statt der Wegpunkt-KI – die fliegt
/// in der Luft nicht unter rund 169 kt und setzt im Landemodus fest 133 kt (EDDL/EDDF, beta.14–16). Rein rechnerisch, ohne
/// SimConnect: <see cref="Advance"/> rechnet die Lage für die vergangene Zeit, <see cref="Pose"/> liefert sie für den Sim.
///
/// Ablauf: Geschwindigkeit mit <see cref="KinematicProfile.DecelKtPerSec"/> auf Vapp + 5, ab 0,5 NM auf Vapp; Höhe läuft weich auf
/// den 3°-Pfad (50 ft über der Schwelle), seitlich auf die Mittellinie. Ab <see cref="KinematicProfile.FlareHeightFt"/> nimmt die
/// Sinkrate mit der Höhe ab bis <see cref="KinematicProfile.TouchdownSinkFpm"/>, die Nase kommt hoch. Nach dem Aufsetzen geht die
/// Nase runter auf die Lage am Boden (<see cref="GroundPitchDeg"/>, STATIC PITCH des Modells), dann bremst das Flugzeug so, dass es
/// am Haltepunkt des Plans steht. Höhen sind die der Räder über der Bahn; gesetzt wird der Bezugspunkt (<see cref="CgHeightFt"/>
/// darüber). Die Bahnhöhe läuft gleitend auf den gemessenen Wert (<see cref="GroundTargetFt"/>).
///
/// Start (<see cref="Takeoff"/>, ab beta.21): Die Wegpunkt-KI hob in beta.16/17 ohne zu rotieren ab (A320 0,3°). Aufgereiht kurz
/// stehen, dann Startlauf mit fester Beschleunigung bis Vr, rotieren mit <see cref="KinematicProfile.RotateDegPerSec"/> bis
/// <see cref="KinematicProfile.LiftoffPitchDeg"/>, abheben und steigen: Die Nase läuft auf Bahnneigung + Anstellwinkel für
/// <see cref="KinematicProfile.ClimbFpm"/>, die Steigrate folgt aus Nase minus Anstellwinkel (der beim Abheben noch die ganze
/// Längsneigung ist und dann abklingt) – so steigt das Flugzeug weich an, statt nach dem Abheben zu springen. Ab der
/// Beschleunigungshöhe flacher und schneller, auf 3000 ft über der Bahn waagerecht.
/// </summary>
public sealed class TrafficKinematics
{
    private const double FeetPerMeter = RunwayEnd.FeetPerMeter;

    private const double MetersPerSecondPerKnot = 0.514444;

    private const double FeetPerSecondPerKnot = 1.68781;

    /// <summary>Start: so lange steht das Flugzeug aufgereiht, bevor es losrollt (Triebwerke hoch).</summary>
    public const double LineupSeconds = 3;

    /// <summary>Start: Vr spätestens nach diesem Anteil der Bahn – sonst beschleunigt das Flugzeug stärker.</summary>
    public const double RotateByRunwayShare = 0.6;

    /// <summary>Start: stärker beschleunigt es auch auf kurzen Bahnen nicht (m/s²).</summary>
    private const double MaxTakeoffAccelMps2 = 4;

    /// <summary>Start: In dieser Höhe über der Bahn fängt das Flugzeug ab und fliegt waagerecht (der Test endet bei 2800 ft).</summary>
    public const double LevelOffFt = 3000;

    /// <summary>Start: Zeitkonstante, mit der der Anstellwinkel nach dem Abheben vom Abhebewinkel auf den des Steigflugs fällt.</summary>
    private const double AoaSettleSeconds = 2.5;

    /// <summary>Start: Fahrtzunahme im Steigflug (kt/s).</summary>
    private const double ClimbAccelKtPerSec = 1.5;

    /// <summary>Start: höchstens so schnell nach der Beschleunigungshöhe (unter 10 000 ft).</summary>
    private const double MaxClimbKt = 250;

    /// <summary>Start: so schnell ändert sich die Nase nach dem Rotieren (°/s).</summary>
    private const double ClimbPitchDegPerSec = 1.5;

    /// <summary>Start: unter <see cref="LevelOffFt"/> höchstens so viel ft/min je ft Abstand (300 ft darunter 1500 ft/min).</summary>
    private const double LevelOffFpmPerFt = 5;

    /// <summary>Zeitkonstante, mit der Höhe und Querablage auf Pfad und Mittellinie laufen.</summary>
    private const double BlendSeconds = 8;

    /// <summary>Zeitkonstante, mit der die Längsneigung dem Soll folgt.</summary>
    private const double PitchSeconds = 2;

    /// <summary>Nach dem Aufsetzen so lange rollen, bevor gebremst wird (Bugrad runter, Spoiler).</summary>
    private const double BrakeDelaySeconds = 2;

    /// <summary>Nase runter nach dem Aufsetzen.</summary>
    private const double DerotateDegPerSec = 1.5;

    /// <summary>So schnell folgt die Bahnhöhe dem gemessenen Wert – je Frame, damit das Flugzeug nicht springt.</summary>
    public const double GroundSlewFtPerSec = 6;

    private readonly TrafficTestPlan _plan;
    private readonly KinematicProfile _profile;
    private double _glideSinkFps;
    private double _flareStartSinkFps;
    private double _brakeMps2;
    private double _rolloutSeconds;
    private double _lineupSeconds;
    private double _takeoffAccelMps2;
    private double _aoaDeg;
    private bool _climbPitchReached;

    public TrafficKinematics(TrafficTestPlan plan, KinematicProfile profile, double alongM, double crossM, double heightFt, double speedKt, double pitchDeg, double groundFt, double cgHeightFt)
    {
        _plan = plan;
        _profile = profile;
        AlongM = alongM;
        CrossM = crossM;
        HeightFt = Math.Max(0, heightFt);
        SpeedKt = speedKt;
        PitchDeg = pitchDeg;
        GroundFt = groundFt;
        CgHeightFt = Math.Max(0, cgHeightFt);
        StartAlongM = alongM;
        StartHeightFt = HeightFt;
        StartSpeedKt = speedKt;
    }

    /// <summary>
    /// Start von der Bahn: Flugzeug steht (oder rollt schon langsam) bei <paramref name="alongM"/> hinter dem Bahnanfang des Plans
    /// (<see cref="TrafficTestPlan.BuildTakeoff"/>), Nase auf der Lage am Boden. Vr und Steiggeschwindigkeit kommen aus Vapp des Plans
    /// und dem Profil; die Beschleunigung reicht für Vr bis <see cref="RotateByRunwayShare"/> der Bahn.
    /// </summary>
    public static TrafficKinematics Takeoff(TrafficTestPlan plan, KinematicProfile profile, double alongM, double crossM, double speedKt, double groundFt, double cgHeightFt, double groundPitchDeg)
    {
        var speed = Math.Max(0, speedKt);
        var kin = new TrafficKinematics(plan, profile, alongM, crossM, 0, speed, groundPitchDeg, groundFt, cgHeightFt)
        {
            GroundPitchDeg = groundPitchDeg,
        };
        kin.Phase = speed > 5 ? KinematicPhase.TakeoffRoll : KinematicPhase.Lineup;
        kin.RotateKt = Math.Max(40, plan.ApproachKt + profile.RotateAboveVappKt);
        kin.ClimbKt = Math.Max(kin.RotateKt + 5, plan.TakeoffKt + profile.ClimbAboveV2Kt);

        var rotateMps = TrafficTestPlan.TrueAirspeedKt(kin.RotateKt, groundFt) * MetersPerSecondPerKnot;
        var startMps = TrafficTestPlan.TrueAirspeedKt(speed, groundFt) * MetersPerSecondPerKnot;
        var available = Math.Max(300, (plan.Runway.LengthM * RotateByRunwayShare) - alongM);
        var needed = Math.Max(0, (rotateMps * rotateMps) - (startMps * startMps)) / (2 * available);
        kin._takeoffAccelMps2 = Math.Clamp(needed, profile.TakeoffAccelMps2, Math.Max(profile.TakeoffAccelMps2, MaxTakeoffAccelMps2));
        return kin;
    }

    public KinematicPhase Phase { get; private set; } = KinematicPhase.Approach;

    /// <summary>Ob das ein Start ist (<see cref="Takeoff"/>) statt eines Endanflugs.</summary>
    public bool IsTakeoff => Phase is KinematicPhase.Lineup or KinematicPhase.TakeoffRoll or KinematicPhase.Rotation or KinematicPhase.Climb;

    /// <summary>Start: Rotationsgeschwindigkeit (Vr), sonst 0.</summary>
    public double RotateKt { get; private set; }

    /// <summary>Start: Steiggeschwindigkeit bis zur Beschleunigungshöhe, sonst 0.</summary>
    public double ClimbKt { get; private set; }

    /// <summary>Start: Beschleunigung im Startlauf (m/s²).</summary>
    public double TakeoffAccelMps2 => _takeoffAccelMps2;

    /// <summary>Start: wo das Rotieren begann (ab dem Bahnanfang), sonst null.</summary>
    public double? RotateAlongM { get; private set; }

    /// <summary>Start: Abhebepunkt, Fahrt und Längsneigung, sonst null.</summary>
    public (double AlongM, double SpeedKt, double PitchDeg)? Liftoff { get; private set; }

    public double AlongM { get; private set; }

    public double CrossM { get; private set; }

    /// <summary>Höhe der Räder über der Bahn.</summary>
    public double HeightFt { get; private set; }

    /// <summary>Angezeigte Fahrt (ohne Wind gleich der Groundspeed bis auf die Höhenkorrektur).</summary>
    public double SpeedKt { get; private set; }

    public double PitchDeg { get; private set; }

    /// <summary>Steig- bzw. Sinkrate des letzten Schritts (negativ = sinkend).</summary>
    public double VerticalSpeedFpm { get; private set; }

    /// <summary>Höhe der Bahn (MSL), auf die die Räder aufsetzen – läuft auf <see cref="GroundTargetFt"/>.</summary>
    public double GroundFt { get; set; }

    /// <summary>Gemessene Bahnhöhe unter dem Flugzeug (vom Dienst), null = keine Messung; <see cref="GroundFt"/> folgt ihr gleitend.</summary>
    public double? GroundTargetFt { get; set; }

    /// <summary>Längsneigung, mit der das Modell am Boden steht (STATIC PITCH), Nase hoch positiv – das Ziel beim Ausrollen.</summary>
    public double GroundPitchDeg { get; set; }

    /// <summary>Höhe des Bezugspunkts über den Rädern (STATIC CG TO GROUND).</summary>
    public double CgHeightFt { get; set; }

    public double StartAlongM { get; }

    public double StartHeightFt { get; }

    public double StartSpeedKt { get; }

    /// <summary>
    /// Rollen nach der Landung: Statt am Haltepunkt zu stehen, bremst das Flugzeug bis hierhin (entlang ab der Schwelle) auf
    /// <see cref="ExitKt"/> und geht in <see cref="KinematicPhase.Exited"/> – dort übernimmt <see cref="TaxiKinematics"/>. Null = halten.
    /// </summary>
    public double? ExitAlongM { get; set; }

    /// <summary>Rollgeschwindigkeit am Abrollpunkt (<see cref="ExitAlongM"/>).</summary>
    public double ExitKt { get; set; }

    /// <summary>Wo das Abfangen begann, sonst null.</summary>
    public double? FlareAlongM { get; private set; }

    /// <summary>Aufsetzpunkt, Fahrt und Sinkrate, sonst null.</summary>
    public (double AlongM, double SpeedKt, double SinkFpm, double PitchDeg)? Touchdown { get; private set; }

    public bool OnGround => Phase is KinematicPhase.Rollout or KinematicPhase.Stopped or KinematicPhase.Exited or KinematicPhase.Lineup or KinematicPhase.TakeoffRoll or KinematicPhase.Rotation;

    /// <summary>Höhe des 3°-Pfads über der Bahn an einer Stelle (Räder), hinter der Schwelle weiter fallend.</summary>
    public static double GlidePathHeightFt(double alongM) =>
        TrafficTestPlan.ThresholdCrossingFt - (alongM * Math.Tan(TrafficTestPlan.GlideSlopeDeg * Math.PI / 180) * FeetPerMeter);

    /// <summary>Wunschfahrt im Anflug: bis 0,5 NM Vapp + 5, dann Vapp, im Abfangen Vapp − 5.</summary>
    public double TargetKt => Phase == KinematicPhase.Flare ? _plan.TouchdownKt
        : AlongM < -0.5 * RunwayEnd.MetersPerNm ? _plan.ApproachKt + 5
        : _plan.ApproachKt;

    /// <summary>Rechnet <paramref name="dt"/> Sekunden weiter.</summary>
    public void Advance(double dt)
    {
        if (dt <= 0)
        {
            return;
        }

        if (GroundTargetFt is { } ground)
        {
            GroundFt = Toward(GroundFt, ground, GroundSlewFtPerSec * dt);
        }

        if (Phase is KinematicPhase.Stopped or KinematicPhase.Exited)
        {
            VerticalSpeedFpm = 0;
            PitchDeg = Toward(PitchDeg, GroundPitchDeg, DerotateDegPerSec * dt);
            return;
        }

        switch (Phase)
        {
            case KinematicPhase.Approach:
                AdvanceApproach(dt);
                break;
            case KinematicPhase.Flare:
                AdvanceFlare(dt);
                break;
            case KinematicPhase.Rollout:
                AdvanceRollout(dt);
                break;
            case KinematicPhase.Lineup:
                AdvanceLineup(dt);
                break;
            case KinematicPhase.TakeoffRoll:
                AdvanceTakeoffRoll(dt);
                break;
            case KinematicPhase.Rotation:
                AdvanceRotation(dt);
                break;
            case KinematicPhase.Climb:
                AdvanceClimb(dt);
                break;
        }
    }

    /// <summary>Lage für den Sim: Punkt auf der Mittellinie mit Querablage, Bezugspunkt über den Rädern, Bahnkurs.</summary>
    public AiPose Pose()
    {
        var runway = _plan.Runway;
        var (lat, lon) = runway.Point(AlongM);
        if (Math.Abs(CrossM) > 0.01)
        {
            (lat, lon) = RouteMath.Destination(lat, lon, TrafficMath.Normalize(runway.HeadingTrueDeg + (CrossM > 0 ? 90 : -90)), Math.Abs(CrossM) / RunwayEnd.MetersPerNm);
        }

        return new AiPose(lat, lon, GroundFt + CgHeightFt + HeightFt, PitchDeg, 0, runway.HeadingTrueDeg);
    }

    /// <summary>Wahre Fahrt über Grund (ohne Wind) aus der angezeigten.</summary>
    private double GroundSpeedMps(double heightFt) =>
        TrafficTestPlan.TrueAirspeedKt(SpeedKt, GroundFt + heightFt) * MetersPerSecondPerKnot;

    private void AdvanceApproach(double dt)
    {
        SpeedKt = Toward(SpeedKt, TargetKt, _profile.DecelKtPerSec * dt);
        var before = HeightFt;
        var offset = before - GlidePathHeightFt(AlongM);
        AlongM += GroundSpeedMps(HeightFt) * dt;
        CrossM *= Math.Exp(-dt / BlendSeconds);

        // Abweichung vom Pfad klingt ab, der Pfad selbst sinkt mit
        HeightFt = Math.Max(0, GlidePathHeightFt(AlongM) + (offset * Math.Exp(-dt / BlendSeconds)));
        VerticalSpeedFpm = (HeightFt - before) / dt * 60;
        PitchDeg = Toward(PitchDeg, _profile.ApproachPitchDeg, Math.Abs(PitchDeg - _profile.ApproachPitchDeg) * (1 - Math.Exp(-dt / PitchSeconds)) + (0.2 * dt));

        if (HeightFt <= _profile.FlareHeightFt)
        {
            Phase = KinematicPhase.Flare;
            FlareAlongM = AlongM;
            _glideSinkFps = Math.Max(_profile.TouchdownSinkFpm / 60, -VerticalSpeedFpm / 60);
            _flareStartSinkFps = _glideSinkFps;
        }
    }

    private void AdvanceFlare(double dt)
    {
        // Sinkrate fällt linear mit der Höhe: von der des Pfads bei Flare-Beginn bis zur Aufsetz-Sinkrate
        var touchdownFps = _profile.TouchdownSinkFpm / 60;
        var share = Math.Clamp(HeightFt / _profile.FlareHeightFt, 0, 1);
        var sink = touchdownFps + ((_flareStartSinkFps - touchdownFps) * share);
        SpeedKt = Toward(SpeedKt, TargetKt, _profile.DecelKtPerSec * dt);
        AlongM += GroundSpeedMps(HeightFt) * dt;
        CrossM *= Math.Exp(-dt / BlendSeconds);
        HeightFt -= sink * dt;
        VerticalSpeedFpm = -sink * 60;
        var pitchTarget = _profile.ApproachPitchDeg + ((_profile.FlarePitchDeg - _profile.ApproachPitchDeg) * (1 - share));
        PitchDeg = Toward(PitchDeg, pitchTarget, 3 * dt);

        if (HeightFt <= 0)
        {
            HeightFt = 0;
            Touchdown = (AlongM, SpeedKt, sink * 60, PitchDeg);
            Phase = KinematicPhase.Rollout;
            _rolloutSeconds = 0;

            // So bremsen, dass das Flugzeug am Haltepunkt steht – oder am Abrollpunkt die Rollgeschwindigkeit hat; nicht stärker als
            // das Profil zulässt
            var target = ExitAlongM ?? _plan.StopM;
            var distance = Math.Max(1, target - AlongM - (GroundSpeedMps(0) * BrakeDelaySeconds));
            var speed = SpeedKt * MetersPerSecondPerKnot;
            var exitSpeed = ExitAlongM is null ? 0 : ExitKt * MetersPerSecondPerKnot;
            _brakeMps2 = Math.Clamp(Math.Max(0, (speed * speed) - (exitSpeed * exitSpeed)) / (2 * distance), 0.8, _profile.MaxBrakeMps2);
        }
    }

    private void AdvanceRollout(double dt)
    {
        _rolloutSeconds += dt;
        VerticalSpeedFpm = 0;
        HeightFt = 0;
        PitchDeg = Toward(PitchDeg, GroundPitchDeg, DerotateDegPerSec * dt);
        CrossM *= Math.Exp(-dt / BlendSeconds);
        if (_rolloutSeconds > BrakeDelaySeconds)
        {
            var floor = ExitAlongM is null ? 0 : ExitKt;
            SpeedKt = Math.Max(floor, SpeedKt - (_brakeMps2 / MetersPerSecondPerKnot * dt));
        }

        AlongM += SpeedKt * MetersPerSecondPerKnot * dt;
        if (ExitAlongM is { } exit && AlongM >= exit)
        {
            AlongM = exit;
            Phase = KinematicPhase.Exited;
            return;
        }

        if (SpeedKt <= 0)
        {
            SpeedKt = 0;
            Phase = KinematicPhase.Stopped;
        }
    }

    /// <summary>Wahre Fahrt je angezeigter in einer Höhe der Räder über der Bahn.</summary>
    private double TasFactor(double heightFt) => TrafficTestPlan.TrueAirspeedKt(1, GroundFt + heightFt);

    private void AdvanceLineup(double dt)
    {
        _lineupSeconds += dt;
        VerticalSpeedFpm = 0;
        HeightFt = 0;
        PitchDeg = Toward(PitchDeg, GroundPitchDeg, DerotateDegPerSec * dt);
        if (_lineupSeconds >= LineupSeconds)
        {
            Phase = KinematicPhase.TakeoffRoll;
        }
    }

    private void AdvanceTakeoffRoll(double dt)
    {
        VerticalSpeedFpm = 0;
        HeightFt = 0;
        PitchDeg = Toward(PitchDeg, GroundPitchDeg, DerotateDegPerSec * dt);
        CrossM *= Math.Exp(-dt / BlendSeconds);
        Accelerate(_takeoffAccelMps2, dt);
        if (SpeedKt >= RotateKt)
        {
            Phase = KinematicPhase.Rotation;
            RotateAlongM = AlongM;
        }
    }

    private void AdvanceRotation(double dt)
    {
        VerticalSpeedFpm = 0;
        HeightFt = 0;
        CrossM *= Math.Exp(-dt / BlendSeconds);

        // Beim Rotieren beschleunigt das Flugzeug etwas weniger (mehr Widerstand)
        Accelerate(_takeoffAccelMps2 * 0.8, dt);
        PitchDeg += _profile.RotateDegPerSec * dt;
        var liftoffPitch = Math.Max(_profile.LiftoffPitchDeg, GroundPitchDeg + 3);
        if (PitchDeg >= liftoffPitch)
        {
            Liftoff = (AlongM, SpeedKt, PitchDeg);

            // Beim Abheben ist die Bahnneigung 0: die ganze Längsneigung ist Anstellwinkel
            _aoaDeg = PitchDeg;
            Phase = KinematicPhase.Climb;
        }
    }

    private void AdvanceClimb(double dt)
    {
        CrossM *= Math.Exp(-dt / BlendSeconds);
        var accelerating = _profile.AccelHeightFt is { } accelFt && HeightFt >= accelFt;
        var targetKt = accelerating ? Math.Max(ClimbKt, Math.Min(_plan.TakeoffKt + 60, MaxClimbKt)) : ClimbKt;
        SpeedKt = Toward(SpeedKt, targetKt, ClimbAccelKtPerSec * dt);

        // Gewünschte Steigrate: bis zur Beschleunigungshöhe ClimbFpm, danach AccelClimbFpm, unter LevelOffFt weniger bis waagerecht
        var wantedFpm = Math.Min(accelerating ? _profile.AccelClimbFpm : _profile.ClimbFpm, Math.Max(0, (LevelOffFt - HeightFt) * LevelOffFpmPerFt));
        var tasFps = SpeedKt * TasFactor(HeightFt) * FeetPerSecondPerKnot;
        var wantedPathDeg = Math.Asin(Math.Clamp(wantedFpm / 60 / Math.Max(tasFps, 1), 0, 1)) * 180 / Math.PI;

        // Anstellwinkel des Steigflugs, mit der Fahrt quadratisch kleiner; der tatsächliche klingt vom Abhebewinkel darauf ab
        var aoa = Math.Clamp(_profile.ClimbAoaDeg * Math.Pow(ClimbKt / Math.Max(SpeedKt, 1), 2), 1, _profile.ClimbAoaDeg * 1.5);
        _aoaDeg += (aoa - _aoaDeg) * (1 - Math.Exp(-dt / AoaSettleSeconds));

        // Nase: erst mit der Rotationsrate weiter hoch, danach ruhiger
        var pitchTarget = Math.Min(wantedPathDeg + aoa, _profile.MaxClimbPitchDeg);
        if (PitchDeg >= pitchTarget)
        {
            _climbPitchReached = true;
        }

        PitchDeg = Toward(PitchDeg, pitchTarget, (_climbPitchReached ? ClimbPitchDegPerSec : _profile.RotateDegPerSec) * dt);

        // Bahnneigung = Nase minus Anstellwinkel – nie sinkend
        var path = Math.Max(0, PitchDeg - _aoaDeg) * Math.PI / 180;
        VerticalSpeedFpm = tasFps * Math.Sin(path) * 60;
        HeightFt += tasFps * Math.Sin(path) * dt;
        AlongM += tasFps / FeetPerMeter * Math.Cos(path) * dt;
    }

    /// <summary>Am Boden beschleunigen (wahre Fahrt um <paramref name="mps2"/>), entlang mit der mittleren Fahrt des Schritts.</summary>
    private void Accelerate(double mps2, double dt)
    {
        var before = GroundSpeedMps(0);
        SpeedKt += mps2 / MetersPerSecondPerKnot / TasFactor(0) * dt;
        AlongM += (before + GroundSpeedMps(0)) / 2 * dt;
    }

    private static double Toward(double value, double target, double maxStep) =>
        value < target ? Math.Min(target, value + maxStep) : Math.Max(target, value - maxStep);
}
