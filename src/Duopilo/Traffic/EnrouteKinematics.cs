using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>
/// Seitlicher Weg eines Anflugs aus dem Reiseflug (A4) in Bahnkoordinaten: entlang ab der Schwelle in Landerichtung (davor negativ),
/// quer rechts positiv; Kurse relativ zur Bahn (0 = Landerichtung, rechts herum positiv). Gerade bis an den Kurvenkreis, dann im Bogen
/// auf die verlängerte Mittellinie – am Eindrehpunkt (<see cref="JoinAlongM"/>, quer 0) genau im Landekurs. Der Kreis liegt auf der
/// Seite des Starts: von rechts kommend eine Rechtskurve, von links eine Linkskurve; von hinter dem Flughafen wird daraus ein Bogen
/// von rund 180°.
/// </summary>
/// <param name="LineHeadingDeg">Kurs der Geraden bis zur Kurve.</param>
/// <param name="StraightM">Länge der Geraden.</param>
/// <param name="SweepDeg">Wie weit die Kurve dreht (0 bis unter 360°).</param>
public sealed record ArrivalPath(
    double StartAlongM,
    double StartCrossM,
    double LineHeadingDeg,
    double StraightM,
    double CenterAlongM,
    double CenterCrossM,
    double RadiusM,
    double SweepDeg,
    bool RightTurn,
    double JoinAlongM)
{
    /// <summary>Länge des Bogens.</summary>
    public double ArcM => RadiusM * SweepDeg * Math.PI / 180;

    /// <summary>Länge vom Start bis zum Eindrehpunkt.</summary>
    public double LengthM => StraightM + ArcM;

    /// <summary>
    /// Weg vom Start (<paramref name="startAlongM"/>, <paramref name="startCrossM"/>) zum Eindrehpunkt <paramref name="joinAlongM"/> auf der
    /// verlängerten Mittellinie mit dem Kurvenradius <paramref name="radiusM"/>: Tangente vom Start an den Kreis, der die Mittellinie am
    /// Eindrehpunkt berührt. Wirft, wenn der Start zu nah am Kreis liegt.
    /// </summary>
    public static ArrivalPath Build(double startAlongM, double startCrossM, double joinAlongM, double radiusM)
    {
        var radius = Math.Max(50, radiusM);
        var right = startCrossM >= 0;
        var centerCross = right ? radius : -radius;
        var dAlong = joinAlongM - startAlongM;
        var dCross = centerCross - startCrossM;
        var distance = Math.Sqrt((dAlong * dAlong) + (dCross * dCross));
        if (distance <= radius * 1.05)
        {
            throw new ArgumentException("The start lies too close to the turn onto the final.", nameof(startAlongM));
        }

        // Der Kreis liegt rechts (Rechtskurve) bzw. links der Geraden: um den halben Öffnungswinkel neben der Richtung zur Mitte
        var toCenter = Math.Atan2(dCross, dAlong) * 180 / Math.PI;
        var alpha = Math.Asin(radius / distance) * 180 / Math.PI;
        var line = TrafficMath.Normalize(right ? toCenter - alpha : toCenter + alpha);
        var sweep = right ? TrafficMath.Normalize(-line) : line;
        if (sweep > 359.5)
        {
            sweep = 0;
        }

        return new ArrivalPath(
            startAlongM,
            startCrossM,
            line,
            Math.Sqrt((distance * distance) - (radius * radius)),
            joinAlongM,
            centerCross,
            radius,
            sweep,
            right,
            joinAlongM);
    }

    /// <summary>
    /// Lage nach <paramref name="distanceM"/> Metern Weg: entlang, quer, Kurs relativ zur Bahn und Krümmung (1/m, Rechtskurve positiv).
    /// Hinter dem Eindrehpunkt geht es auf der Mittellinie weiter.
    /// </summary>
    public (double AlongM, double CrossM, double HeadingDeg, double CurvaturePerM) At(double distanceM)
    {
        if (distanceM <= StraightM)
        {
            var line = LineHeadingDeg * Math.PI / 180;
            var d = Math.Max(0, distanceM);
            return (StartAlongM + (d * Math.Cos(line)), StartCrossM + (d * Math.Sin(line)), LineHeadingDeg, 0);
        }

        var arc = distanceM - StraightM;
        if (arc >= ArcM)
        {
            return (JoinAlongM + (arc - ArcM), 0, 0, 0);
        }

        var turned = arc / RadiusM * 180 / Math.PI;
        var heading = LineHeadingDeg + (RightTurn ? turned : -turned);
        var h = heading * Math.PI / 180;

        // Die Mitte liegt quer zur Flugrichtung: rechts davon (Rechtskurve) bzw. links
        var (along, cross) = RightTurn
            ? (CenterAlongM + (RadiusM * Math.Sin(h)), CenterCrossM - (RadiusM * Math.Cos(h)))
            : (CenterAlongM - (RadiusM * Math.Sin(h)), CenterCrossM + (RadiusM * Math.Cos(h)));
        return (along, cross, TrafficMath.Normalize(heading), (RightTurn ? 1 : -1) / RadiusM);
    }
}

/// <summary>Kennwerte des Streckenflugs (A4, B3) je Kategorie, Geschwindigkeiten als IAS.</summary>
/// <param name="HighKt">Sinkflug über <see cref="SpeedLimitFt"/>.</param>
/// <param name="LowKt">Sinkflug darunter.</param>
/// <param name="InterceptKt">Vor und in der Kurve auf den Final.</param>
/// <param name="ClimbLowKt">Steigflug unter <see cref="SpeedLimitFt"/>.</param>
/// <param name="ClimbHighKt">Steigflug darüber (höchstens so viel, wie die Reise-TAS hergibt).</param>
/// <param name="BankDeg">Querneigung in Kurven.</param>
/// <param name="ClimbLowFpm">Steigrate unter <see cref="SpeedLimitFt"/>.</param>
/// <param name="ClimbMidFpm">Steigrate bis FL240.</param>
/// <param name="ClimbHighFpm">Steigrate darüber.</param>
/// <param name="JoinNm">Eindrehpunkt auf den Final, so weit vor der Schwelle.</param>
public sealed record EnrouteProfile(
    double HighKt,
    double LowKt,
    double InterceptKt,
    double ClimbLowKt,
    double ClimbHighKt,
    double BankDeg,
    double ClimbLowFpm,
    double ClimbMidFpm,
    double ClimbHighFpm,
    double JoinNm)
{
    /// <summary>Unter FL100 höchstens 250 kt.</summary>
    public const double SpeedLimitFt = 10_000;

    /// <summary>Ab hier zählt die mittlere Steigrate nicht mehr.</summary>
    public const double HighClimbFt = 24_000;

    /// <summary>
    /// Verkehrsflugzeuge (auch Militär): Sinkflug mit 300 kt, unter FL100 250, vor der Kurve auf den Final 210; Steigflug mit 250, über
    /// FL100 300 kt, 2500/2000/1200 ft/min, Kurven mit 25°, Eindrehen 10 NM vor der Schwelle. Allgemeine Luftfahrt (C172 mit Vapp 65):
    /// rund 1,7 Vapp (110 kt), vor der Kurve Vapp + 25, Steigflug mit Vapp + 20 und 700 ft/min, Kurven mit 20°, Eindrehen bei 5 NM.
    /// </summary>
    public static EnrouteProfile For(TrafficCategory category, double approachKt)
    {
        if (category != TrafficCategory.GeneralAviation)
        {
            return new(300, 250, 210, 250, 300, 25, 2500, 2000, 1200, 10);
        }

        var cruise = Math.Round(approachKt * 1.7);
        return new(cruise, cruise, approachKt + 25, approachKt + 20, approachKt + 20, 20, 700, 700, 500, 5);
    }

    /// <summary>Kurvenradius bei <paramref name="trueAirspeedKt"/> mit <see cref="BankDeg"/> (m).</summary>
    public double TurnRadiusM(double trueAirspeedKt)
    {
        var v = trueAirspeedKt * 0.514444;
        return v * v / (EnrouteKinematics.Gravity * Math.Tan(BankDeg * Math.PI / 180));
    }
}

/// <summary>
/// Streckenflug, den Duopilo selbst rechnet (wie die Kinematik im Endanflug, ohne SimConnect): Anflug aus dem Reiseflug (A4) auf einem
/// <see cref="ArrivalPath"/> bis zum Eindrehpunkt – dort übernimmt <see cref="TrafficKinematics"/> den Endanflug – und Abflug (B3) nach
/// dem Start bis in die Reiseflughöhe auf einem Kurs. Höhen sind die der Räder über der Bahn wie bei <see cref="TrafficKinematics"/>.
///
/// Anflug: Höhe nach dem verbleibenden Weg bis zur Schwelle auf 3° (wie der Gleitpfad, oben höchstens die Reiseflughöhe), die
/// Sinkrate läuft weich darauf; Fahrt nach <see cref="EnrouteProfile"/>, rechtzeitig vor der Kurve auf <see cref="EnrouteProfile.InterceptKt"/>.
/// Abflug: auf den Kurs drehen (Kurvenrate aus der Querneigung, höchstens 3°/s), steigen mit der Rate des Profils, kurz vor der
/// Reiseflughöhe abfangen, dort mit der Reise-TAS. Die Querneigung folgt der Kurve mit 5°/s (Anflug etwas vorausschauend), die Nase
/// ist Bahnneigung plus Anstellwinkel, der mit der Fahrt quadratisch kleiner wird.
/// </summary>
public sealed class EnrouteKinematics
{
    public const double Gravity = 9.80665;

    private const double MetersPerSecondPerKnot = 0.514444;

    private const double FeetPerSecondPerKnot = 1.68781;

    /// <summary>So schnell wird das Flugzeug langsamer bzw. schneller (kt/s).</summary>
    public const double DecelKtPerSec = 1.0;

    public const double AccelKtPerSec = 1.5;

    /// <summary>Anflug: so weit vor der Kurve auf den Final schon mit <see cref="EnrouteProfile.InterceptKt"/>.</summary>
    public const double InterceptLeadNm = 4;

    /// <summary>So schnell ändert sich die Steig- bzw. Sinkrate (ft/min je Sekunde).</summary>
    private const double VerticalChangeFpmPerSec = 300;

    /// <summary>Anflug: Zeitkonstante, mit der die Höhe auf das Profil läuft.</summary>
    private const double PathSeconds = 10;

    private const double RollDegPerSec = 5;

    private const double PitchDegPerSec = 1;

    /// <summary>Anflug: Querneigung schon so weit vor dem Bogen (halb).</summary>
    private const double BankLeadSeconds = 3;

    /// <summary>Abflug: Zeitkonstante, mit der der Kurs auf den gewünschten läuft (Kurvenrate = Abweichung / Zeit).</summary>
    private const double HeadingSeconds = 8;

    private const double MaxTurnDegPerSec = 3;

    /// <summary>Kurz vor der Zielhöhe höchstens so viel ft/min je ft Abstand (300 ft darunter 1500 ft/min).</summary>
    private const double LevelOffFpmPerFt = 5;

    private const double BankLimitDeg = 30;

    private readonly RunwayEnd _runway;
    private readonly EnrouteProfile _profile;
    private readonly ArrivalPath? _path;
    private readonly double _targetHeadingDeg;
    private readonly double _cruiseFt;
    private readonly double _cruiseTasKt;
    private readonly double _aoaRefDeg;
    private readonly double _aoaRefKt;
    private double _distanceM;

    private EnrouteKinematics(
        RunwayEnd runway,
        EnrouteProfile profile,
        ArrivalPath? path,
        double targetHeadingDeg,
        double cruiseFt,
        double cruiseTasKt,
        double aoaRefDeg,
        double aoaRefKt,
        double groundFt,
        double cgHeightFt)
    {
        _runway = runway;
        _profile = profile;
        _path = path;
        _targetHeadingDeg = targetHeadingDeg;
        _cruiseFt = cruiseFt;
        _cruiseTasKt = Math.Max(80, cruiseTasKt);
        _aoaRefDeg = Math.Max(1, aoaRefDeg);
        _aoaRefKt = Math.Max(40, aoaRefKt);
        GroundFt = groundFt;
        CgHeightFt = Math.Max(0, cgHeightFt);
    }

    /// <summary>
    /// Anflug ab dem Start des Wegs: <paramref name="heightFt"/> Räder über der Bahn, <paramref name="speedKt"/> IAS. Reiseflughöhe
    /// <paramref name="cruiseFt"/> (MSL) begrenzt das Profil oben, <paramref name="cruiseTasKt"/> die Fahrt dort. Anstellwinkel
    /// <paramref name="aoaRefDeg"/> bei <paramref name="aoaRefKt"/> (im Endanflug Nase minus Pfad bei Vapp + 5).
    /// </summary>
    public static EnrouteKinematics Arrival(
        RunwayEnd runway,
        ArrivalPath path,
        EnrouteProfile profile,
        double cruiseFt,
        double cruiseTasKt,
        double heightFt,
        double speedKt,
        double groundFt,
        double cgHeightFt,
        double aoaRefDeg,
        double aoaRefKt)
    {
        var kin = new EnrouteKinematics(runway, profile, path, 0, cruiseFt, cruiseTasKt, aoaRefDeg, aoaRefKt, groundFt, cgHeightFt);
        var (along, cross, heading, _) = path.At(0);
        kin.AlongM = along;
        kin.CrossM = cross;
        kin.HeadingDeg = heading;
        kin.HeightFt = Math.Max(0, heightFt);
        kin.SpeedKt = speedKt;
        kin.StartHeightFt = kin.HeightFt;
        kin.VerticalSpeedFpm = kin.TargetVerticalSpeedFpm();
        kin.PitchDeg = kin.PitchTargetDeg();
        return kin;
    }

    /// <summary>
    /// Abflug ab der Übergabe aus dem Steigflug nach dem Start: Lage, Fahrt, Steigrate und Nase von dort, Kurs der Bahn. Dreht auf
    /// <paramref name="trackDeg"/> (rechtweisend, leer = geradeaus) und steigt auf <paramref name="cruiseFt"/> (MSL).
    /// </summary>
    public static EnrouteKinematics Departure(
        RunwayEnd runway,
        EnrouteProfile profile,
        double? trackDeg,
        double cruiseFt,
        double cruiseTasKt,
        double alongM,
        double crossM,
        double heightFt,
        double speedKt,
        double verticalSpeedFpm,
        double pitchDeg,
        double groundFt,
        double cgHeightFt,
        double aoaRefDeg,
        double aoaRefKt)
    {
        var target = trackDeg is { } t ? TrafficMath.Normalize(t - runway.HeadingTrueDeg) : 0;
        return new EnrouteKinematics(runway, profile, null, target, cruiseFt, cruiseTasKt, aoaRefDeg, aoaRefKt, groundFt, cgHeightFt)
        {
            AlongM = alongM,
            CrossM = crossM,
            HeightFt = Math.Max(0, heightFt),
            SpeedKt = speedKt,
            VerticalSpeedFpm = verticalSpeedFpm,
            PitchDeg = pitchDeg,
            StartHeightFt = Math.Max(0, heightFt),
        };
    }

    /// <summary>Anflug (A4) statt Abflug (B3).</summary>
    public bool IsArrival => _path is not null;

    /// <summary>Anflug: der Weg bis zum Eindrehpunkt, sonst null.</summary>
    public ArrivalPath? Path => _path;

    public double AlongM { get; private set; }

    public double CrossM { get; private set; }

    /// <summary>Kurs relativ zur Bahn (0 = Landerichtung, rechts herum positiv).</summary>
    public double HeadingDeg { get; private set; }

    /// <summary>Höhe der Räder über der Bahn.</summary>
    public double HeightFt { get; private set; }

    /// <summary>Angezeigte Fahrt.</summary>
    public double SpeedKt { get; private set; }

    public double VerticalSpeedFpm { get; private set; }

    public double PitchDeg { get; private set; }

    /// <summary>Querneigung, rechts positiv (der Sim zählt links positiv, siehe <see cref="Pose"/>).</summary>
    public double BankRightDeg { get; private set; }

    /// <summary>Größte Querneigung bisher (Betrag).</summary>
    public double MaxBankDeg { get; private set; }

    public double GroundFt { get; }

    public double CgHeightFt { get; }

    public double StartHeightFt { get; private init; }

    /// <summary>Anflug: am Eindrehpunkt angekommen – ab hier fliegt <see cref="TrafficKinematics"/>.</summary>
    public bool Joined => _path is { } p && _distanceM >= p.LengthM;

    /// <summary>Anflug: im Bogen auf den Final.</summary>
    public bool Turning => _path is { } p && _distanceM > p.StraightM && _distanceM < p.LengthM;

    /// <summary>Höhe der Räder (MSL).</summary>
    public double AltitudeFt => GroundFt + HeightFt;

    public double TrueAirspeedKt => TrafficTestPlan.TrueAirspeedKt(SpeedKt, AltitudeFt);

    public double HeadingTrueDeg => TrafficMath.Normalize(_runway.HeadingTrueDeg + HeadingDeg);

    /// <summary>Anflug: verbleibender Weg bis zur Schwelle (NM); Abflug: 0.</summary>
    public double RemainingNm => _path is { } p ? (Math.Max(0, p.LengthM - _distanceM) - p.JoinAlongM) / RunwayEnd.MetersPerNm : 0;

    /// <summary>Anflug: verbleibender Weg bis zum Beginn der Kurve auf den Final (NM), 0 = schon drin.</summary>
    public double ToTurnNm => _path is { } p ? Math.Max(0, p.StraightM - _distanceM) / RunwayEnd.MetersPerNm : 0;

    /// <summary>Entfernung zur Schwelle der Bahn (Luftlinie, NM).</summary>
    public double DistanceFromRunwayNm => Math.Sqrt((AlongM * AlongM) + (CrossM * CrossM)) / RunwayEnd.MetersPerNm;

    /// <summary>Abflug: gewünschter Kurs relativ zur Bahn.</summary>
    public double TargetHeadingDeg => _targetHeadingDeg;

    /// <summary>Zielhöhe der Räder über der Bahn: Anflug das Profil an dieser Stelle, Abflug die Reiseflughöhe.</summary>
    public double TargetHeightFt => _path is not null ? ProfileHeightFt(RemainingM) : _cruiseFt - GroundFt;

    /// <summary>Gewünschte Fahrt (IAS) an dieser Stelle.</summary>
    public double TargetKt => _path is not null ? ArrivalTargetKt() : DepartureTargetKt();

    /// <summary>Abschnitt für Status und CSV: descent, level, turn, joined (Anflug); climb, turn, cruise (Abflug).</summary>
    public string Phase => _path is not null
        ? (Joined ? "joined" : Turning ? "turn" : VerticalSpeedFpm < -300 ? "descent" : "level")
        : Math.Abs(HeadingError()) > 2 ? "turn" : VerticalSpeedFpm > 300 ? "climb" : "cruise";

    private double RemainingM => _path is { } p ? Math.Max(0, p.LengthM - _distanceM) - p.JoinAlongM : 0;

    /// <summary>Rechnet <paramref name="dt"/> Sekunden weiter (am Eindrehpunkt bleibt der Anflug stehen).</summary>
    public void Advance(double dt)
    {
        if (dt <= 0 || Joined)
        {
            return;
        }

        var target = TargetKt;
        SpeedKt = Toward(SpeedKt, target, (target < SpeedKt ? DecelKtPerSec : AccelKtPerSec) * dt);
        var tasMps = TrueAirspeedKt * MetersPerSecondPerKnot;

        VerticalSpeedFpm = Toward(VerticalSpeedFpm, TargetVerticalSpeedFpm(), VerticalChangeFpmPerSec * dt);
        HeightFt = Math.Max(0, HeightFt + (VerticalSpeedFpm / 60 * dt));
        var path = PathAngleRad();
        var groundMps = tasMps * Math.Cos(path);

        if (_path is { } p)
        {
            _distanceM = Math.Min(p.LengthM, _distanceM + (groundMps * dt));
            var (along, cross, heading, curvature) = p.At(_distanceM);
            AlongM = along;
            CrossM = cross;
            HeadingDeg = heading;

            // Querneigung aus der Krümmung hier und ein paar Sekunden voraus – rollt so schon kurz vor dem Bogen ein
            var ahead = p.At(_distanceM + (groundMps * BankLeadSeconds)).CurvaturePerM;
            var bank = Math.Atan(groundMps * groundMps * ((curvature + ahead) / 2) / Gravity) * 180 / Math.PI;
            BankRightDeg = Toward(BankRightDeg, Math.Clamp(bank, -BankLimitDeg, BankLimitDeg), RollDegPerSec * dt);
        }
        else
        {
            // Kurvenrate aus der Abweichung, begrenzt durch Querneigung und Standardrate; der Kurs dreht mit der tatsächlichen Querneigung
            var maxRate = Math.Min(MaxTurnDegPerSec, Gravity * Math.Tan(_profile.BankDeg * Math.PI / 180) / Math.Max(tasMps, 1) * 180 / Math.PI);
            var wantedRate = Math.Clamp(HeadingError() / HeadingSeconds, -maxRate, maxRate);
            var wantedBank = Math.Atan(tasMps * wantedRate * Math.PI / 180 / Gravity) * 180 / Math.PI;
            BankRightDeg = Toward(BankRightDeg, wantedBank, RollDegPerSec * dt);
            var rate = Gravity * Math.Tan(BankRightDeg * Math.PI / 180) / Math.Max(tasMps, 1) * 180 / Math.PI;
            HeadingDeg = TrafficMath.Normalize(HeadingDeg + (rate * dt));
            var heading = HeadingDeg * Math.PI / 180;
            AlongM += groundMps * Math.Cos(heading) * dt;
            CrossM += groundMps * Math.Sin(heading) * dt;
        }

        MaxBankDeg = Math.Max(MaxBankDeg, Math.Abs(BankRightDeg));
        PitchDeg = Toward(PitchDeg, PitchTargetDeg(), PitchDegPerSec * dt);
    }

    /// <summary>Lage für den Sim: Punkt in Bahnkoordinaten, Bezugspunkt über den Rädern; MSFS zählt die Querneigung links positiv.</summary>
    public AiPose Pose()
    {
        var (lat, lon) = _runway.Point(AlongM);
        if (Math.Abs(CrossM) > 0.01)
        {
            (lat, lon) = RouteMath.Destination(lat, lon, TrafficMath.Normalize(_runway.HeadingTrueDeg + (CrossM > 0 ? 90 : -90)), Math.Abs(CrossM) / RunwayEnd.MetersPerNm);
        }

        return new AiPose(lat, lon, GroundFt + CgHeightFt + HeightFt, PitchDeg, -BankRightDeg, HeadingTrueDeg);
    }

    /// <summary>Anflug: Höhe der Räder über der Bahn auf 3° nach dem Weg bis zur Schwelle, höchstens die Reiseflughöhe.</summary>
    public double ProfileHeightFt(double remainingM) =>
        Math.Min(_cruiseFt - GroundFt, TrafficKinematics.GlidePathHeightFt(-Math.Max(0, remainingM)));

    private double HeadingError()
    {
        var error = TrafficMath.Normalize(_targetHeadingDeg - HeadingDeg);
        return error > 180 ? error - 360 : error;
    }

    /// <summary>Höchstens so viel IAS, wie die Reise-TAS in dieser Höhe ergibt (oben wie Mach konstant).</summary>
    private double CruiseIasKt() => _cruiseTasKt / TrafficTestPlan.TrueAirspeedKt(1, AltitudeFt);

    private double ArrivalTargetKt()
    {
        if (ToTurnNm <= InterceptLeadNm)
        {
            return _profile.InterceptKt;
        }

        // Unter FL100 250 kt: schon etwas darüber langsamer werden (bei 2000 ft/min und 1 kt/s rund 1700 ft)
        var limit = AltitudeFt < EnrouteProfile.SpeedLimitFt + 1500 ? _profile.LowKt : _profile.HighKt;
        return Math.Min(limit, CruiseIasKt());
    }

    private double DepartureTargetKt()
    {
        if (AltitudeFt >= _cruiseFt - 100)
        {
            return CruiseIasKt();
        }

        return AltitudeFt < EnrouteProfile.SpeedLimitFt ? _profile.ClimbLowKt : Math.Min(_profile.ClimbHighKt, CruiseIasKt());
    }

    private double TargetVerticalSpeedFpm()
    {
        var tasMps = TrueAirspeedKt * MetersPerSecondPerKnot;
        if (_path is not null)
        {
            // Profil sinkt mit dem Weg (3°, unterhalb der Reiseflughöhe), Abweichung klingt mit PathSeconds ab
            var remaining = RemainingM;
            var target = ProfileHeightFt(remaining);
            var descending = TrafficKinematics.GlidePathHeightFt(-remaining) < _cruiseFt - GroundFt;
            var slope = descending ? Math.Tan(TrafficTestPlan.GlideSlopeDeg * Math.PI / 180) * RunwayEnd.FeetPerMeter : 0;
            var wanted = (-slope * tasMps * 60) + ((target - HeightFt) / PathSeconds * 60);
            return Math.Clamp(wanted, -4000, 1000);
        }

        var toGo = _cruiseFt - AltitudeFt;
        var rate = AltitudeFt < EnrouteProfile.SpeedLimitFt ? _profile.ClimbLowFpm
            : AltitudeFt < EnrouteProfile.HighClimbFt ? _profile.ClimbMidFpm
            : _profile.ClimbHighFpm;
        return toGo >= 0 ? Math.Min(rate, toGo * LevelOffFpmPerFt) : Math.Max(-1500, toGo * LevelOffFpmPerFt);
    }

    /// <summary>Bahnneigung aus Steigrate und wahrer Fahrt.</summary>
    private double PathAngleRad()
    {
        var tasFps = Math.Max(1, TrueAirspeedKt * FeetPerSecondPerKnot);
        return Math.Asin(Math.Clamp(VerticalSpeedFpm / 60 / tasFps, -0.5, 0.5));
    }

    /// <summary>Nase = Bahnneigung + Anstellwinkel; der fällt mit der Fahrt quadratisch.</summary>
    private double PitchTargetDeg()
    {
        var aoa = Math.Clamp(_aoaRefDeg * Math.Pow(_aoaRefKt / Math.Max(SpeedKt, 1), 2), 0.5, 10);
        return (PathAngleRad() * 180 / Math.PI) + aoa;
    }

    private static double Toward(double value, double target, double maxStep) =>
        value < target ? Math.Min(target, value + maxStep) : Math.Max(target, value - maxStep);
}
