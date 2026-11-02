using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Abschnitt eines Heli-Anflugs oder -Abflugs (<see cref="HeliKinematics"/>).</summary>
public enum HeliPhase
{
    /// <summary>Geradeaus auf Reisehöhe und mit Reisefahrt (Anflug: zum Pad; Abflug: auf Kurs, Ende des Steigflugs).</summary>
    Cruise,

    /// <summary>Anflug: auf dem Anflugwinkel sinken und so langsamer werden, dass es über dem Pad steht.</summary>
    Approach,

    /// <summary>Schweben über dem Pad, auf den Kurs des Pads drehen.</summary>
    Hover,

    /// <summary>Senkrecht absetzen.</summary>
    Descent,

    /// <summary>Am Boden.</summary>
    Landed,

    /// <summary>Abflug: am Boden, Rotor läuft an.</summary>
    Startup,

    /// <summary>Abflug: senkrecht auf die Schwebehöhe.</summary>
    Liftoff,

    /// <summary>Abflug: aus dem Schweben vorwärts Fahrt aufnehmen, flach steigen.</summary>
    Transition,

    /// <summary>Abflug: mit Fahrt auf die Reisehöhe steigen.</summary>
    Climb,
}

/// <summary>
/// Versuche, den Rotor des Test-Helis zum Drehen zu bringen (beta.39: Passive-B205 mit stehendem Rotor). ROTOR RPM und ROTOR RPM PCT
/// sind laut SDK nicht schreibbar – deshalb Triebwerk und Regler.
/// </summary>
public enum HeliRotorMethod
{
    /// <summary>Nichts versuchen.</summary>
    Off,

    /// <summary>Event ENGINE_AUTO_START (beim Spawn und nach 5 s noch einmal) – für volle Modelle mit Triebwerk.</summary>
    Event,

    /// <summary>SimVars alle <see cref="HeliRotor.ResendSeconds"/> s: <see cref="HeliRotor.SimVars"/>.</summary>
    SimVars,

    /// <summary>Event und SimVars.</summary>
    All,
}

/// <summary>Was <see cref="HeliRotorMethod"/> schickt.</summary>
public static class HeliRotor
{
    public const string StartEvent = "ENGINE_AUTO_START";

    public const string StopEvent = "ENGINE_AUTO_SHUTDOWN";

    public const double ResendSeconds = 3;

    /// <summary>So lange nach dem Aufsetzen läuft der Rotor noch, dann abstellen (Event, SimVars auf aus, kein Nachschicken mehr).</summary>
    public const double StopAfterLandingSeconds = 15;

    /// <summary>So lange nach dem Aufsetzen sind alle Lichter aus (der Rotor läuft bis dahin aus).</summary>
    public const double LightsOffAfterLandingSeconds = 45;

    /// <summary>So lange nach dem Aufsetzen endet der Test (landed).</summary>
    public const double EndAfterLandingSeconds = 75;

    /// <summary>Triebwerk an, N1 und Drehzahl, Reglerziel 100 % – ob ein Modell davon den Rotor dreht, zeigt der Test.</summary>
    public static readonly IReadOnlyList<(string Name, string Unit, double Value)> SimVars =
    [
        ("GENERAL ENG COMBUSTION:1", "bool", 1),
        ("TURB ENG N1:1", "percent", 90),
        ("GENERAL ENG RPM:1", "rpm", 6600),
        ("ROTOR GOV TARGET PCT:1", "percent over 100", 1),
    ];

    /// <summary>Zum Abstellen: dieselben SimVars auf aus.</summary>
    public static readonly IReadOnlyList<(string Name, string Unit, double Value)> SimVarsOff =
    [
        ("GENERAL ENG COMBUSTION:1", "bool", 0),
        ("TURB ENG N1:1", "percent", 0),
        ("GENERAL ENG RPM:1", "rpm", 0),
        ("ROTOR GOV TARGET PCT:1", "percent over 100", 0),
    ];

    public static bool SendsEvent(HeliRotorMethod method) => method is HeliRotorMethod.Event or HeliRotorMethod.All;

    public static bool SendsSimVars(HeliRotorMethod method) => method is HeliRotorMethod.SimVars or HeliRotorMethod.All;
}

/// <summary>Kennwerte eines Heli-Anflugs.</summary>
/// <param name="CruiseKt">Reisefahrt (über Grund, ohne Wind).</param>
/// <param name="DecelMps2">Verzögerung im Anflug – damit steht der Heli genau über dem Pad.</param>
/// <param name="ApproachAngleDeg">Anflugwinkel bis zur Schwebehöhe (steiler als bei Flugzeugen).</param>
/// <param name="HoverFt">Schwebehöhe der Kufen über dem Pad.</param>
/// <param name="HoverSeconds">So lange schwebt er mindestens, bevor er absetzt.</param>
/// <param name="DescentFpm">Sinkrate beim senkrechten Absetzen.</param>
/// <param name="FinalDescentFpm">Sinkrate in den letzten <see cref="HeliKinematics.FinalDescentFt"/>.</param>
/// <param name="YawDegPerSec">Drehrate im Schweben.</param>
/// <param name="CruisePitchDeg">Längsneigung in Reisefahrt (Nase runter negativ).</param>
/// <param name="HoverPitchDeg">Längsneigung im Schweben.</param>
/// <param name="DecelPitchPerMps2">So viel Nase hoch je m/s² Verzögerung (beim Beschleunigen ebenso viel runter).</param>
/// <param name="StartupSeconds">Abflug: so lange läuft der Rotor am Boden, bevor er abhebt.</param>
/// <param name="TakeoffHoverFt">Abflug: Schwebehöhe nach dem Abheben.</param>
/// <param name="LiftoffFpm">Abflug: Steigrate beim Abheben und zu Beginn des Übergangs.</param>
/// <param name="AccelMps2">Abflug: Beschleunigung bis zur Reisefahrt.</param>
/// <param name="ClimbFpm">Abflug: Steigrate mit Fahrt (ab <see cref="HeliKinematics.ClimbFromKt"/>).</param>
public sealed record HeliProfile(
    double CruiseKt,
    double DecelMps2,
    double ApproachAngleDeg,
    double HoverFt,
    double HoverSeconds,
    double DescentFpm,
    double FinalDescentFpm,
    double YawDegPerSec,
    double CruisePitchDeg,
    double HoverPitchDeg,
    double DecelPitchPerMps2,
    double StartupSeconds = 30,
    double TakeoffHoverFt = 10,
    double LiftoffFpm = 300,
    double AccelMps2 = 1.0,
    double ClimbFpm = 1000)
{
    /// <summary>
    /// Mittlerer Heli (Bell 205, H135): 90 kt, 0,6 m/s² (90 kt bis zum Stand in knapp 1 NM), 6° (bei 90 kt gut 950 ft/min),
    /// 40 ft Schweben für 5 s, 300 ft/min absetzen, die letzten 10 ft 100.
    /// </summary>
    public static readonly HeliProfile Default = new(90, 0.6, 6, 40, 5, 300, 100, 10, -3, 2, 6);

    /// <summary>Mit anderer Reisefahrt (40–150 kt).</summary>
    public HeliProfile WithCruise(double? cruiseKt) => cruiseKt is { } kt ? this with { CruiseKt = Math.Clamp(kt, 40, 150) } : this;
}

/// <summary>
/// Heli-Anflug, den Duopilo selbst rechnet: Die Wegpunkt-KI fliegt einen Heli wie ein Flugzeug (Endanflug, Landemodus, durchstarten –
/// beta.39 mit dem Passive-B205). Geradeaus aus der Anflugrichtung zum Pad, auf Reisehöhe, bis der Anflugwinkel erreicht ist; dann
/// sinken und mit fester Verzögerung so langsamer werden (v = √(2·a·d)), dass der Heli über dem Pad steht; schweben, auf den Kurs des
/// Pads drehen und senkrecht absetzen. Längsneigung: in Fahrt Nase runter, beim Verzögern hoch, im Schweben leicht hoch.
/// Rahmen: <see cref="RunwayEnd"/> mit dem Pad als Schwelle und dem Anflugkurs als Kurs – entlang = −Entfernung zum Pad.
/// </summary>
public sealed class HeliKinematics
{
    private const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Die letzten Fuß setzt er langsamer auf.</summary>
    public const double FinalDescentFt = 10;

    /// <summary>Zeitkonstante, mit der die Höhe dem Anflugprofil folgt.</summary>
    private const double HeightSeconds = 3;

    private const double PitchDegPerSec = 2;

    /// <summary>So nah am Pad gilt der Heli als angekommen (m).</summary>
    private const double ArrivedM = 0.3;

    /// <summary>Höhe folgt dem gemessenen Boden höchstens so schnell (ft/s).</summary>
    private const double GroundSlewFtPerSec = 20;

    /// <summary>Abflug: ab dieser Fahrt steigt er mit <see cref="HeliProfile.ClimbFpm"/> (darunter anteilig ab <see cref="HeliProfile.LiftoffFpm"/>).</summary>
    public const double ClimbFromKt = 40;

    /// <summary>Abflug: Zeitkonstante, mit der er auf der Reisehöhe abfängt.</summary>
    private const double LevelOffSeconds = 6;

    private readonly RunwayEnd _frame;
    private readonly HeliProfile _profile;
    private readonly double _startHeightFt;
    private readonly double? _padHeadingDeg;

    public HeliKinematics(RunwayEnd frame, HeliProfile profile, double startDistanceM, double startHeightFt, double startKt, double? padHeadingDeg, double groundFt, double cgHeightFt)
    {
        _frame = frame;
        _profile = profile;
        _startHeightFt = Math.Max(profile.HoverFt, startHeightFt);
        _padHeadingDeg = padHeadingDeg;
        DistanceM = Math.Max(0, startDistanceM);
        HeightFt = _startHeightFt;
        SpeedKt = Math.Clamp(startKt, 0, profile.CruiseKt);
        PitchDeg = profile.CruisePitchDeg;
        HeadingTrueDeg = frame.HeadingTrueDeg;
        GroundFt = groundFt;
        CgHeightFt = Math.Max(0, cgHeightFt);
        StartDistanceM = DistanceM;
    }

    public HeliPhase Phase { get; private set; } = HeliPhase.Cruise;

    /// <summary>
    /// Abflug (H2): Der Heli steht auf dem Pad (Mitte von <paramref name="frame"/>), Rotor läuft <see cref="HeliProfile.StartupSeconds"/>
    /// an, dann senkrecht auf <see cref="HeliProfile.TakeoffHoverFt"/>, im Schweben von <paramref name="startHeadingDeg"/> auf den
    /// Kurs des Rahmens drehen, vorwärts mit <see cref="HeliProfile.AccelMps2"/> auf Reisefahrt beschleunigen und dabei auf
    /// <paramref name="cruiseHeightFt"/> über dem Pad steigen (erst flach, ab <see cref="ClimbFromKt"/> mit <see cref="HeliProfile.ClimbFpm"/>).
    /// <see cref="DistanceM"/> ist dann der Weg ab dem Pad.
    /// </summary>
    public static HeliKinematics Departure(RunwayEnd frame, HeliProfile profile, double cruiseHeightFt, double startHeadingDeg, double groundFt, double cgHeightFt, double groundPitchDeg)
    {
        var heli = new HeliKinematics(frame, profile, 0, cruiseHeightFt, 0, frame.HeadingTrueDeg, groundFt, cgHeightFt)
        {
            GroundPitchDeg = groundPitchDeg,
        };
        heli.IsDeparture = true;
        heli.Phase = HeliPhase.Startup;
        heli.HeightFt = 0;
        heli.SpeedKt = 0;
        heli.PitchDeg = groundPitchDeg;
        heli.HeadingTrueDeg = TrafficMath.Normalize(startHeadingDeg);
        return heli;
    }

    /// <summary>Abflug (<see cref="Departure"/>) statt Anflug.</summary>
    public bool IsDeparture { get; private set; }

    /// <summary>Abflug: so lange läuft der Rotor schon am Boden.</summary>
    public double StartupElapsedSeconds { get; private set; }

    public HeliProfile Profile => _profile;

    /// <summary>Höhe über dem Pad beim Start (Reisehöhe).</summary>
    public double StartHeightFt => _startHeightFt;

    /// <summary>Ausrichtung des Pads, auf die er im Schweben dreht; null = er bleibt auf dem Anflugkurs.</summary>
    public double? PadHeadingDeg => _padHeadingDeg;

    /// <summary>Höhe über dem Pad, die er gerade anstrebt: im Anflug das Profil, im Schweben die Schwebehöhe, danach 0.</summary>
    public double TargetHeightFt => IsDeparture
        ? Phase switch
        {
            HeliPhase.Startup => 0,
            HeliPhase.Liftoff or HeliPhase.Hover => _profile.TakeoffHoverFt,
            _ => _startHeightFt,
        }
        : Phase switch
        {
            HeliPhase.Cruise or HeliPhase.Approach => ProfileHeightFt(DistanceM),
            HeliPhase.Hover => _profile.HoverFt,
            _ => 0,
        };

    /// <summary>Fahrt, die er gerade anstrebt (im Anflug die höchste, mit der er noch über dem Pad zum Stehen kommt).</summary>
    public double TargetKt => IsDeparture
        ? (Phase is HeliPhase.Transition or HeliPhase.Climb or HeliPhase.Cruise ? _profile.CruiseKt : 0)
        : Phase is HeliPhase.Cruise or HeliPhase.Approach ? AllowedKt(DistanceM) : 0;

    /// <summary>Restweg zum Pad (m).</summary>
    public double DistanceM { get; private set; }

    public double StartDistanceM { get; }

    /// <summary>Höhe der Kufen über dem Pad.</summary>
    public double HeightFt { get; private set; }

    /// <summary>Fahrt über Grund (ohne Wind gleich der angezeigten).</summary>
    public double SpeedKt { get; private set; }

    public double VerticalSpeedFpm { get; private set; }

    public double PitchDeg { get; private set; }

    public double HeadingTrueDeg { get; private set; }

    /// <summary>Höhe des Pads (MSL), läuft gleitend auf <see cref="GroundTargetFt"/>.</summary>
    public double GroundFt { get; set; }

    /// <summary>Gemessener Boden unter dem Heli (vom Dienst), null = keine Messung.</summary>
    public double? GroundTargetFt { get; set; }

    /// <summary>Höhe des Bezugspunkts über den Kufen.</summary>
    public double CgHeightFt { get; set; }

    /// <summary>Längsneigung am Boden (STATIC PITCH).</summary>
    public double GroundPitchDeg { get; set; }

    /// <summary>So lange schwebt er schon.</summary>
    public double HoverSeconds { get; private set; }

    /// <summary>Sinkrate beim Aufsetzen, sonst null.</summary>
    public double? TouchdownFpm { get; private set; }

    /// <summary>Wo das Verzögern begann (Restweg in m), sonst null.</summary>
    public double? DecelFromM { get; private set; }

    public bool OnGround => Phase is HeliPhase.Landed or HeliPhase.Startup;

    /// <summary>Höhe der Kufen über dem Pad im Anflug an dieser Stelle: Anflugwinkel bis zur Schwebehöhe, oben die Starthöhe.</summary>
    public double ProfileHeightFt(double distanceM) =>
        Math.Min(_startHeightFt, _profile.HoverFt + (Math.Max(0, distanceM) * Math.Tan(_profile.ApproachAngleDeg * Math.PI / 180) * RunwayEnd.FeetPerMeter));

    /// <summary>Höchste Fahrt an dieser Stelle, mit der er mit der Verzögerung des Profils noch über dem Pad steht.</summary>
    public double AllowedKt(double distanceM) =>
        Math.Min(_profile.CruiseKt, Math.Sqrt(2 * _profile.DecelMps2 * Math.Max(0, distanceM)) / MetersPerSecondPerKnot);

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

        var before = HeightFt;
        if (IsDeparture)
        {
            AdvanceDeparture(dt);
            VerticalSpeedFpm = OnGround ? 0 : (HeightFt - before) / dt * 60;
            return;
        }

        switch (Phase)
        {
            case HeliPhase.Cruise:
            case HeliPhase.Approach:
                AdvanceApproach(dt);
                break;
            case HeliPhase.Hover:
                AdvanceHover(dt);
                break;
            case HeliPhase.Descent:
                AdvanceDescent(dt);
                break;
            default:
                PitchDeg = Toward(PitchDeg, GroundPitchDeg, PitchDegPerSec * dt);
                break;
        }

        VerticalSpeedFpm = Phase == HeliPhase.Landed ? 0 : (HeightFt - before) / dt * 60;
    }

    /// <summary>Lage für den Sim: auf der Linie zum Pad (Abflug: ab dem Pad), Bezugspunkt über den Kufen, ohne Querneigung.</summary>
    public AiPose Pose()
    {
        var (lat, lon) = _frame.Point(IsDeparture ? DistanceM : -DistanceM);
        return new AiPose(lat, lon, GroundFt + CgHeightFt + HeightFt, PitchDeg, 0, HeadingTrueDeg);
    }

    private void AdvanceApproach(double dt)
    {
        // Fahrt: bis Reisefahrt, aber nie mehr, als zum Stehen über dem Pad passt
        var allowed = AllowedKt(DistanceM);
        var decelerating = allowed < _profile.CruiseKt - 0.5;
        if (decelerating && DecelFromM is null)
        {
            DecelFromM = DistanceM;
        }

        var before = SpeedKt;
        SpeedKt = Math.Min(allowed, SpeedKt + (_profile.DecelMps2 / MetersPerSecondPerKnot * dt));
        DistanceM = Math.Max(0, DistanceM - ((before + SpeedKt) / 2 * MetersPerSecondPerKnot * dt));
        var target = ProfileHeightFt(DistanceM);
        HeightFt += (target - HeightFt) * (1 - Math.Exp(-dt / HeightSeconds));
        if (Phase == HeliPhase.Cruise && (target < _startHeightFt - 1 || decelerating))
        {
            Phase = HeliPhase.Approach;
        }

        // Nase: in Fahrt runter, im Stand leicht hoch, beim Verzögern zusätzlich hoch (bis kurz vor dem Stehen)
        var share = Math.Clamp(SpeedKt / _profile.CruiseKt, 0, 1);
        var pitch = (_profile.CruisePitchDeg * share) + (_profile.HoverPitchDeg * (1 - share));
        if (decelerating)
        {
            pitch += _profile.DecelPitchPerMps2 * _profile.DecelMps2 * Math.Clamp(SpeedKt / 10, 0, 1);
        }

        PitchDeg = Toward(PitchDeg, pitch, PitchDegPerSec * dt);
        if (DistanceM <= ArrivedM)
        {
            DistanceM = 0;
            SpeedKt = 0;
            Phase = HeliPhase.Hover;
        }
    }

    private void AdvanceHover(double dt)
    {
        HoverSeconds += dt;
        HeightFt += (_profile.HoverFt - HeightFt) * (1 - Math.Exp(-dt / HeightSeconds));
        PitchDeg = Toward(PitchDeg, _profile.HoverPitchDeg, PitchDegPerSec * dt);
        var aligned = YawTo(_padHeadingDeg, dt);
        if (HoverSeconds >= _profile.HoverSeconds && aligned && Math.Abs(HeightFt - _profile.HoverFt) < 2)
        {
            Phase = HeliPhase.Descent;
        }
    }

    /// <summary>
    /// Mit der Drehrate des Profils auf <paramref name="headingDeg"/> drehen; true, wenn die Drehung ganz durch ist (der letzte Schritt
    /// landet genau auf dem Kurs) oder es keinen Zielkurs gibt.
    /// </summary>
    private bool YawTo(double? headingDeg, double dt)
    {
        if (headingDeg is not { } target)
        {
            return true;
        }

        var error = TrafficMath.Normalize(target - HeadingTrueDeg);
        error = error > 180 ? error - 360 : error;
        var step = Math.Clamp(error, -_profile.YawDegPerSec * dt, _profile.YawDegPerSec * dt);
        HeadingTrueDeg = TrafficMath.Normalize(HeadingTrueDeg + step);
        return Math.Abs(error - step) < 0.01;
    }

    /// <summary>Abflug: Anlassen, Abheben, Schweben mit Drehung auf den Kurs, Übergang, Steigflug, Reiseflug.</summary>
    private void AdvanceDeparture(double dt)
    {
        switch (Phase)
        {
            case HeliPhase.Startup:
                StartupElapsedSeconds += dt;
                PitchDeg = Toward(PitchDeg, GroundPitchDeg, PitchDegPerSec * dt);
                if (StartupElapsedSeconds >= _profile.StartupSeconds)
                {
                    Phase = HeliPhase.Liftoff;
                }

                break;
            case HeliPhase.Liftoff:
                HeightFt = Math.Min(_profile.TakeoffHoverFt, HeightFt + (_profile.LiftoffFpm / 60 * dt));
                PitchDeg = Toward(PitchDeg, _profile.HoverPitchDeg, PitchDegPerSec * dt);
                if (HeightFt >= _profile.TakeoffHoverFt)
                {
                    Phase = HeliPhase.Hover;
                }

                break;
            case HeliPhase.Hover:
                HoverSeconds += dt;
                PitchDeg = Toward(PitchDeg, _profile.HoverPitchDeg, PitchDegPerSec * dt);
                if (YawTo(_frame.HeadingTrueDeg, dt) && HoverSeconds >= _profile.HoverSeconds)
                {
                    Phase = HeliPhase.Transition;
                }

                break;
            default:
                AdvanceClimbOut(dt);
                break;
        }
    }

    private void AdvanceClimbOut(double dt)
    {
        var before = SpeedKt;
        SpeedKt = Math.Min(_profile.CruiseKt, SpeedKt + (_profile.AccelMps2 / MetersPerSecondPerKnot * dt));
        DistanceM += (before + SpeedKt) / 2 * MetersPerSecondPerKnot * dt;

        // Steigen: erst flach, mit der Fahrt bis ClimbFromKt auf die volle Rate, an der Reisehöhe weich abfangen
        var share = Math.Clamp(SpeedKt / ClimbFromKt, 0, 1);
        var fpm = _profile.LiftoffFpm + ((_profile.ClimbFpm - _profile.LiftoffFpm) * share);
        var rate = Math.Min(fpm, Math.Max(0, _startHeightFt - HeightFt) * 60 / LevelOffSeconds);
        HeightFt = Math.Min(_startHeightFt, HeightFt + (rate / 60 * dt));

        // Nase: beim Beschleunigen runter (bis kurz vor der Reisefahrt), in Fahrt wie im Reiseflug
        var cruiseShare = Math.Clamp(SpeedKt / _profile.CruiseKt, 0, 1);
        var pitch = (_profile.CruisePitchDeg * cruiseShare) + (_profile.HoverPitchDeg * (1 - cruiseShare));
        pitch -= _profile.DecelPitchPerMps2 * _profile.AccelMps2 * Math.Clamp((_profile.CruiseKt - SpeedKt) / 10, 0, 1);
        PitchDeg = Toward(PitchDeg, pitch, PitchDegPerSec * dt);

        Phase = SpeedKt < ClimbFromKt ? HeliPhase.Transition
            : HeightFt < _startHeightFt - 1 ? HeliPhase.Climb
            : HeliPhase.Cruise;
    }

    private void AdvanceDescent(double dt)
    {
        var rate = HeightFt > FinalDescentFt ? _profile.DescentFpm : _profile.FinalDescentFpm;
        HeightFt = Math.Max(0, HeightFt - (rate / 60 * dt));
        PitchDeg = Toward(PitchDeg, _profile.HoverPitchDeg, PitchDegPerSec * dt);
        if (HeightFt <= 0)
        {
            TouchdownFpm = rate;
            Phase = HeliPhase.Landed;
        }
    }

    private static double Toward(double value, double target, double maxStep) =>
        value < target ? Math.Min(target, value + maxStep) : Math.Max(target, value - maxStep);
}
