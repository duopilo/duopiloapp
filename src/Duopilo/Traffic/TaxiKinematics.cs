using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Abschnitt des Rollens.</summary>
public enum TaxiPhase
{
    /// <summary>Steht an der Parkposition (Triebwerke an, Freigabe).</summary>
    Parked,

    /// <summary>Pushback: rückwärts aus der Parkposition, im Bogen auf den Rollweg.</summary>
    Pushback,

    /// <summary>Nach dem Pushback: Schlepper abkoppeln.</summary>
    PushbackDone,

    /// <summary>Rollt vorwärts über die Rollwege.</summary>
    Taxi,

    /// <summary>Steht am Haltepunkt (Ende der Route; mit Aufreihen bis zur Freigabe).</summary>
    Holding,

    /// <summary>Rollt vom Haltepunkt auf die Bahn und reiht sich auf der Mittellinie in Startrichtung auf.</summary>
    LineUp,

    /// <summary>Steht aufgereiht auf der Bahn – bereit zum Start.</summary>
    LinedUp,
}

/// <summary>Kennwerte des Rollens je Kategorie.</summary>
/// <param name="TaxiKt">Rollgeschwindigkeit geradeaus.</param>
/// <param name="TurnRadiusM">Kurvenradius an Kreuzungen.</param>
/// <param name="LateralMps2">Höchste Querbeschleunigung in Kurven (daraus die Kurvengeschwindigkeit).</param>
/// <param name="PushbackRadiusM">Kurvenradius beim Pushback.</param>
/// <param name="PushbackLengthM">So weit hinter den Rollweg-Punkt schiebt der Schlepper das Flugzeug.</param>
/// <param name="StopShortM">So weit vor dem Haltepunkt bleibt der Bezugspunkt stehen (die Nase steht davor).</param>
/// <param name="ClearanceM">So weit führt die Route mindestens an anderen Flugzeugen am Boden vorbei (zu deren Bezugspunkt).</param>
public sealed record TaxiProfile(
    double TaxiKt,
    double TurnRadiusM,
    double LateralMps2,
    double PushbackRadiusM,
    double PushbackLengthM,
    double StopShortM,
    double ClearanceM)
{
    /// <summary>Verkehrsflugzeuge: 15 kt geradeaus, in 30-m-Kurven rund 9 kt, 25 m Abstand zu anderen Flugzeugen.</summary>
    public static readonly TaxiProfile Airliner = new(15, 30, 0.8, 20, 40, 12, 25);

    /// <summary>Allgemeine Luftfahrt: 10 kt, enge Kurven, 12 m Abstand.</summary>
    public static readonly TaxiProfile GeneralAviation = new(10, 10, 0.8, 8, 15, 4, 12);

    public static TaxiProfile For(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? GeneralAviation : Airliner;
}

/// <summary>
/// Kinematik am Boden: Duopilo schiebt das Flugzeug selbst über die Rollwege (wie <see cref="TrafficKinematics"/> in der Luft). Erst
/// <see cref="ParkedSeconds"/> an der Parkposition, dann – wenn die Nase zum Gebäude zeigt – Pushback rückwärts mit
/// <see cref="PushbackKt"/> im Bogen auf den Rollweg und <see cref="PushbackDoneSeconds"/> warten, dann vorwärts zum Haltepunkt:
/// geradeaus mit der Rollgeschwindigkeit, vor Kurven und dem Halt rechtzeitig langsamer. Lage in Metern vom Bezugspunkt des
/// Flughafens, <see cref="Pose"/> rechnet sie in Breite/Länge um.
/// </summary>
public sealed class TaxiKinematics
{
    /// <summary>So lange steht das Flugzeug vor dem Pushback bzw. Losrollen an der Parkposition.</summary>
    public const double ParkedSeconds = 5;

    /// <summary>Pushback-Geschwindigkeit (Schrittgeschwindigkeit).</summary>
    public const double PushbackKt = 2.5;

    /// <summary>Nach dem Pushback so lange stehen (Schlepper ab, Triebwerke).</summary>
    public const double PushbackDoneSeconds = 10;

    /// <summary>Mit Aufreihen: so lange am Haltepunkt warten (Freigabe „line up“).</summary>
    public const double HoldBeforeLineUpSeconds = 15;

    /// <summary>Aufreihen höchstens so schnell.</summary>
    public const double LineUpKt = 10;

    private const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Anfahren (m/s²).</summary>
    private const double AccelMps2 = 0.5;

    /// <summary>Bremsen vor Kurven und am Halt (m/s²).</summary>
    public const double DecelMps2 = 0.6;

    /// <summary>Anfahren und Bremsen beim Pushback (m/s²).</summary>
    private const double PushbackAccelMps2 = 0.2;

    private readonly Func<double, double, (double Latitude, double Longitude)> _toLatLon;
    private double _phaseSeconds;

    /// <param name="pushback">Weg des Pushbacks (vorwärts gerechnet, das Flugzeug fährt ihn rückwärts); null = vorwärts los.</param>
    /// <param name="taxi">Weg zum Haltepunkt.</param>
    /// <param name="toLatLon">Meter östlich/nördlich → Breite/Länge (<see cref="TaxiGraph.ToLatLon"/>).</param>
    /// <param name="lineUp">Weg vom Haltepunkt auf die Bahn (Aufreihen); null = am Haltepunkt bleiben.</param>
    /// <param name="rolling">Rollen nach der Landung: gleich in <see cref="TaxiPhase.Taxi"/> mit dieser Geschwindigkeit (kt), ohne an der Parkposition zu stehen; null = erst stehen.</param>
    public TaxiKinematics(TaxiPath? pushback, TaxiPath taxi, Func<double, double, (double Latitude, double Longitude)> toLatLon, double groundFt, double cgHeightFt, double groundPitchDeg, TaxiPath? lineUp = null, double? rolling = null)
    {
        Pushback = pushback is { LengthM: > 0.5 } ? pushback : null;
        Taxi = taxi;
        LineUpPath = lineUp is { LengthM: > 0.5 } ? lineUp : null;
        _toLatLon = toLatLon;
        GroundFt = groundFt;
        CgHeightFt = Math.Max(0, cgHeightFt);
        PitchDeg = groundPitchDeg;
        var start = Pushback is { } p ? p.At(0) : taxi.At(0);
        (EastM, NorthM) = (start.East, start.North);
        HeadingDeg = Pushback is not null ? TrafficMath.Normalize(start.HeadingDeg + 180) : start.HeadingDeg;
        if (rolling is { } kt)
        {
            Phase = TaxiPhase.Taxi;
            SpeedKt = Math.Max(0, kt);
        }
    }

    public TaxiPath? Pushback { get; }

    public TaxiPath Taxi { get; }

    /// <summary>Weg vom Haltepunkt auf die Bahn, null = kein Aufreihen.</summary>
    public TaxiPath? LineUpPath { get; }

    public TaxiPhase Phase { get; private set; } = TaxiPhase.Parked;

    public double EastM { get; private set; }

    public double NorthM { get; private set; }

    public double HeadingDeg { get; private set; }

    public double PitchDeg { get; }

    /// <summary>Geschwindigkeit über Grund (beim Pushback rückwärts, positiv).</summary>
    public double SpeedKt { get; private set; }

    /// <summary>Höchste Geschwindigkeit beim Rollen.</summary>
    public double MaxSpeedKt { get; private set; }

    /// <summary>Strecke auf dem aktuellen Weg (Pushback bzw. Rollen).</summary>
    public double DistanceM { get; private set; }

    /// <summary>Bisher gerollt (Pushback und Rollen).</summary>
    public double TotalM { get; private set; }

    /// <summary>Höhe der Bahn/Rollwege (MSL), läuft auf <see cref="GroundTargetFt"/>.</summary>
    public double GroundFt { get; set; }

    /// <summary>Gemessener Boden unter dem Flugzeug, null = keine Messung.</summary>
    public double? GroundTargetFt { get; set; }

    public double CgHeightFt { get; }

    public bool Moving => SpeedKt > 0.5;

    /// <summary>Rechnet <paramref name="dt"/> Sekunden weiter.</summary>
    public void Advance(double dt)
    {
        if (dt <= 0)
        {
            return;
        }

        if (GroundTargetFt is { } ground)
        {
            GroundFt = GroundFt < ground
                ? Math.Min(ground, GroundFt + (TrafficKinematics.GroundSlewFtPerSec * dt))
                : Math.Max(ground, GroundFt - (TrafficKinematics.GroundSlewFtPerSec * dt));
        }

        _phaseSeconds += dt;
        switch (Phase)
        {
            case TaxiPhase.Parked when _phaseSeconds >= ParkedSeconds:
                Next(Pushback is not null ? TaxiPhase.Pushback : TaxiPhase.Taxi);
                break;
            case TaxiPhase.Pushback:
                Move(Pushback!, PushbackKt * MetersPerSecondPerKnot, PushbackAccelMps2, PushbackAccelMps2, reverse: true, dt);
                if (DistanceM >= Pushback!.LengthM - 0.05)
                {
                    SpeedKt = 0;
                    Next(TaxiPhase.PushbackDone);
                }

                break;
            case TaxiPhase.PushbackDone when _phaseSeconds >= PushbackDoneSeconds:
                Next(TaxiPhase.Taxi);
                break;
            case TaxiPhase.Taxi:
                Move(Taxi, double.MaxValue, AccelMps2, DecelMps2, reverse: false, dt);
                if (DistanceM >= Taxi.LengthM - 0.05 || (SpeedKt < 0.1 && Taxi.LengthM - DistanceM < 0.5))
                {
                    SpeedKt = 0;
                    Next(TaxiPhase.Holding);
                }

                break;
            case TaxiPhase.Holding when LineUpPath is not null && _phaseSeconds >= HoldBeforeLineUpSeconds:
                Next(TaxiPhase.LineUp);
                break;
            case TaxiPhase.LineUp:
                Move(LineUpPath!, LineUpKt * MetersPerSecondPerKnot, AccelMps2, DecelMps2, reverse: false, dt);
                if (DistanceM >= LineUpPath!.LengthM - 0.05 || (SpeedKt < 0.1 && LineUpPath.LengthM - DistanceM < 0.5))
                {
                    SpeedKt = 0;
                    Next(TaxiPhase.LinedUp);
                }

                break;
        }
    }

    /// <summary>Lage für den Sim: auf dem Weg, Bezugspunkt über dem Boden, Nase auf der Lage am Boden.</summary>
    public AiPose Pose()
    {
        var (lat, lon) = _toLatLon(EastM, NorthM);
        return new AiPose(lat, lon, GroundFt + CgHeightFt, PitchDeg, 0, HeadingDeg);
    }

    private void Next(TaxiPhase phase)
    {
        Phase = phase;
        _phaseSeconds = 0;
        DistanceM = 0;
    }

    private void Move(TaxiPath path, double maxMps, double accel, double decel, bool reverse, double dt)
    {
        var speed = SpeedKt * MetersPerSecondPerKnot;
        var allowed = Math.Min(maxMps, path.AllowedMps(DistanceM, decel));
        speed = Math.Min(speed + (accel * dt), allowed);

        // Am Anfang eines Wegs mit Grenze 0 nicht stehen bleiben: kriechen, bis die Bremsgrenze greift
        if (speed <= 0 && path.LengthM - DistanceM > 0.05)
        {
            speed = Math.Min(0.2, path.LengthM - DistanceM);
        }

        var step = Math.Min(speed * dt, Math.Max(0, path.LengthM - DistanceM));
        DistanceM += step;
        TotalM += step;
        SpeedKt = speed / MetersPerSecondPerKnot;
        if (!reverse)
        {
            MaxSpeedKt = Math.Max(MaxSpeedKt, SpeedKt);
        }

        var (east, north, heading) = path.At(DistanceM);
        (EastM, NorthM) = (east, north);
        HeadingDeg = reverse ? TrafficMath.Normalize(heading + 180) : heading;
    }
}
