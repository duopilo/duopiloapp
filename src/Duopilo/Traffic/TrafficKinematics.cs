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
    /// <summary>Verkehrsflugzeuge: Airbus-typisch etwa 2,5° auf dem Pfad, Abfangen ab 30 ft, mit rund 5,5° aufsetzen (rund 450 m hinter der Schwelle).</summary>
    public static readonly KinematicProfile Airliner = new(2.5, 30, 5.5, 150, 1.5, 2.5);

    /// <summary>Allgemeine Luftfahrt: flacher Anflug mit mehr Nase hoch, Abfangen ab 15 ft.</summary>
    public static readonly KinematicProfile GeneralAviation = new(3.0, 15, 6.0, 120, 1.5, 2.0);

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
/// </summary>
public sealed class TrafficKinematics
{
    private const double FeetPerMeter = RunwayEnd.FeetPerMeter;

    private const double MetersPerSecondPerKnot = 0.514444;

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

    public KinematicPhase Phase { get; private set; } = KinematicPhase.Approach;

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

    /// <summary>Wo das Abfangen begann, sonst null.</summary>
    public double? FlareAlongM { get; private set; }

    /// <summary>Aufsetzpunkt, Fahrt und Sinkrate, sonst null.</summary>
    public (double AlongM, double SpeedKt, double SinkFpm, double PitchDeg)? Touchdown { get; private set; }

    public bool OnGround => Phase is KinematicPhase.Rollout or KinematicPhase.Stopped;

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

        if (Phase == KinematicPhase.Stopped)
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

            // So bremsen, dass das Flugzeug am Haltepunkt steht – nicht stärker als das Profil zulässt
            var distance = Math.Max(1, _plan.StopM - AlongM - (GroundSpeedMps(0) * BrakeDelaySeconds));
            var speed = SpeedKt * MetersPerSecondPerKnot;
            _brakeMps2 = Math.Clamp(speed * speed / (2 * distance), 0.8, _profile.MaxBrakeMps2);
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
            SpeedKt = Math.Max(0, SpeedKt - (_brakeMps2 / MetersPerSecondPerKnot * dt));
        }

        AlongM += SpeedKt * MetersPerSecondPerKnot * dt;
        if (SpeedKt <= 0)
        {
            SpeedKt = 0;
            Phase = KinematicPhase.Stopped;
        }
    }

    private static double Toward(double value, double target, double maxStep) =>
        value < target ? Math.Min(target, value + maxStep) : Math.Max(target, value - maxStep);
}
