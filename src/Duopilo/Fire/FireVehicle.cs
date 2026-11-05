using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Abschnitt der Fahrt eines Feuerwehrfahrzeugs.</summary>
public enum FireVehiclePhase
{
    /// <summary>Fährt von der Wache in den Bereitstellungsraum.</summary>
    Responding,

    /// <summary>Steht am Haltepunkt (bzw. vor dem Flugzeug) und wartet.</summary>
    Standby,

    /// <summary>Fährt auf die Spur hinter dem Flugzeug.</summary>
    Joining,

    /// <summary>Folgt der Spur des Flugzeugs mit Abstand.</summary>
    Following,

    /// <summary>Fährt auf seinen Platz am stehenden Flugzeug.</summary>
    Positioning,

    /// <summary>Steht am Flugzeug.</summary>
    AtAircraft,

    /// <summary>Fährt zurück zur Wache (ohne Blaulicht).</summary>
    Returning,

    /// <summary>Zurück – das Objekt kann entfernt werden.</summary>
    Gone,
}

/// <summary>
/// Kinematik eines Feuerwehrfahrzeugs: auf einem <see cref="TaxiPath"/> (zur Bahn, auf die Spur, an das Flugzeug, zurück) oder auf der
/// <see cref="FireTrail"/> hinter dem Flugzeug her. Beim Folgen regelt es die Geschwindigkeit nach dem Abstand entlang der Spur und
/// überholt das Flugzeug nie. Befehle (<see cref="Drive"/>, <see cref="Follow"/>, <see cref="SetAircraft"/>) kommen aus dem Takt des
/// Dienstes, <see cref="Advance"/> läuft auf dem SimConnect-Thread – alles unter einer Sperre.
/// </summary>
public sealed class FireVehicle
{
    private readonly object _lock = new();
    private readonly Func<double, double, (double Latitude, double Longitude)> _toLatLon;
    private readonly FireTrail _trail;

    private TaxiPath? _path;
    private double _pathMaxMps;
    private FireVehiclePhase _arrive;
    private double? _arriveHeading;
    private double _joinS;
    private double _gapM;
    private double _aircraftS = double.NaN;
    private double _aircraftMps;
    private double _ageS;

    public FireVehicle(FireUnit unit, string model, TaxiPath route, FireTrail trail, Func<double, double, (double Latitude, double Longitude)> toLatLon, double groundFt, double heightFt = 0)
    {
        Unit = unit;
        Model = model;
        _trail = trail;
        _toLatLon = toLatLon;
        GroundFt = groundFt;
        HeightFt = heightFt;
        var (east, north, heading) = route.At(0);
        (EastM, NorthM, HeadingDeg) = (east, north, heading);
        (HomeEastM, HomeNorthM) = (east, north);
        Phase = FireVehiclePhase.Responding;
        SetPath(route, FireProfile.MaxMps, FireVehiclePhase.Standby, null);
    }

    public FireUnit Unit { get; }

    public string Model { get; }

    public FireVehiclePhase Phase { get; private set; }

    /// <summary>Die eigene Wache (Anfang des ersten Wegs): dorthin fährt das Fahrzeug zurück.</summary>
    public double HomeEastM { get; }

    public double HomeNorthM { get; }

    public double EastM { get; private set; }

    public double NorthM { get; private set; }

    public double HeadingDeg { get; private set; }

    public double SpeedMps { get; private set; }

    /// <summary>Gefahrene Strecke seit dem Spawn (Räder drehen, <c>L:1:DUOPILO_WEG</c>).</summary>
    public double OdometerM { get; private set; }

    /// <summary>Krümmung der Fahrspur in 1/m, rechts positiv (Lenkung, <c>L:1:DUOPILO_KURVE</c>); bleibt im Stand stehen.</summary>
    public double CurvaturePerM { get; private set; }

    /// <summary>Engster Kurvenradius, den die Lenkung zeigt (darunter wären es Sprünge der Spur, kein Lenken).</summary>
    public const double MaxCurvaturePerM = 1 / 6.0;

    /// <summary>Glättung der Krümmung: Zeitkonstante in Sekunden (die Spur hat alle 3 m einen Knick).</summary>
    public const double CurvatureSmoothingS = 0.4;

    /// <summary>Ab dieser Verzögerung leuchtet das Bremslicht (m/s²), danach noch <see cref="BrakeHoldS"/> Sekunden.</summary>
    public const double BrakeDecelMps2 = 0.3;

    public const double BrakeHoldS = 0.6;

    private double _brakeHoldS;
    private double? _yieldMps;

    /// <summary>Martinshorn erlaubt (Duopilo:Fire:Siren).</summary>
    public bool SirenAllowed { get; init; } = true;

    /// <summary>Art des Horns für <c>L:1:DUOPILO_HORN</c>: 0 Folgetonhorn, 1 Wail, 2 Yelp, 3 Hi-Lo (später je Region).</summary>
    public int Horn { get; init; }

    /// <summary>Warnlichtfarbe für <c>L:1:DUOPILO_LICHTFARBE</c>: 0 blau, 1 rot/weiß, 2 rot (später je Region).</summary>
    public int LightColour { get; init; }

    /// <summary>Bremslicht: das Fahrzeug wird gerade langsamer (oder hat eben gehalten).</summary>
    public bool Braking
    {
        get
        {
            lock (_lock)
            {
                return _brakeHoldS > 0;
            }
        }
    }

    /// <summary>Martinshorn: nur auf der Anfahrt mit Blaulicht und in Fahrt, nicht beim Folgen, Aufstellen oder Einrücken.</summary>
    public bool Siren
    {
        get
        {
            lock (_lock)
            {
                return SirenAllowed && Phase == FireVehiclePhase.Responding && SpeedMps > 0.5;
            }
        }
    }

    /// <summary>Strecke auf dem aktuellen Weg (nicht beim Folgen).</summary>
    public double DistanceM { get; private set; }

    /// <summary>Stelle auf der Spur beim Folgen, sonst NaN.</summary>
    public double TrailS { get; private set; } = double.NaN;

    /// <summary>Abstand zum Flugzeug entlang der Spur beim Folgen, sonst NaN.</summary>
    public double GapM
    {
        get
        {
            lock (_lock)
            {
                return Phase == FireVehiclePhase.Following ? _aircraftS - TrailS : double.NaN;
            }
        }
    }

    /// <summary>Soll-Abstand beim Folgen.</summary>
    public double TargetGapM
    {
        get
        {
            lock (_lock)
            {
                return _gapM;
            }
        }
    }

    /// <summary>Blaulicht: an, außer auf der Rückfahrt.</summary>
    public bool BlueLight => Phase is not (FireVehiclePhase.Returning or FireVehiclePhase.Gone);

    /// <summary>Restweg des aktuellen Wegs (Meter), 0 beim Folgen oder Stehen.</summary>
    public double RemainingM
    {
        get
        {
            lock (_lock)
            {
                return _path is { } path && Phase is FireVehiclePhase.Responding or FireVehiclePhase.Joining or FireVehiclePhase.Positioning or FireVehiclePhase.Returning
                    ? Math.Max(0, path.LengthM - DistanceM)
                    : 0;
            }
        }
    }

    /// <summary>Höhe des Bodens (MSL), läuft auf <see cref="GroundTargetFt"/>.</summary>
    public double GroundFt { get; private set; }

    /// <summary>Gemessener Boden unter dem Fahrzeug (aus einem anderen Thread), null = keine Messung.</summary>
    public double? GroundTargetFt { get; set; }

    /// <summary>
    /// So lange nach dem Spawn springt das Fahrzeug gleich auf den gemessenen Boden, statt langsam hinzugleiten (Test EDDK 05.11.: 30 ft
    /// zu tief gespawnt, mit 6 ft/s sah man die Fahrzeuge an der Wache aus dem Boden kommen). Danach gleitet es wie bisher.
    /// </summary>
    public const double GroundSnapS = 3;

    /// <summary>
    /// So lange nach dem Spawn gehen keine Z-Variablen hinaus: Solange das Objekt lädt, verwirft der Sim sie mit DATA_ERROR (sieben je
    /// Fahrzeug, Test EDDK 05.11.), und unveränderte Werte würden danach nie mehr gesendet.
    /// </summary>
    public const double VarsDelayS = 2;

    /// <summary>Sekunden seit dem Spawn (aus <see cref="Advance"/>).</summary>
    public double AgeS
    {
        get
        {
            lock (_lock)
            {
                return _ageS;
            }
        }
    }

    /// <summary>
    /// Boden unter dem Fahrzeug gemessen (aus der Abfrage): in den ersten <see cref="GroundSnapS"/> Sekunden sofort übernommen, danach
    /// als Ziel, auf das die Höhe gleitet.
    /// </summary>
    public void MeasureGround(double groundFt)
    {
        lock (_lock)
        {
            if (_ageS < GroundSnapS)
            {
                GroundFt = groundFt;
            }

            GroundTargetFt = groundFt;
        }
    }

    /// <summary>Bezugspunkt des Modells über dem Boden.</summary>
    public double HeightFt { get; }

    /// <summary>
    /// GROUND VELOCITY des Objekts laut Sim (Abfrage einmal pro Sekunde), null = keine Abfrage. Zeigt, ob der Sim für die
    /// eingefrorenen, von Duopilo bewegten Fahrzeuge eine Geschwindigkeit rechnet – dann kann das Modell die Räder danach drehen.
    /// </summary>
    public double? SimGroundSpeedKt { get; set; }

    /// <summary>
    /// Höchstgeschwindigkeit, um Abstand zu einem Fahrzeug davor zu halten (null = frei); setzt <see cref="FireOperation"/> in jedem
    /// Takt. Bei 0 steht es (kein Anrollen mit Schrittgeschwindigkeit).
    /// </summary>
    public double? YieldMps
    {
        get
        {
            lock (_lock)
            {
                return _yieldMps;
            }
        }

        set
        {
            lock (_lock)
            {
                _yieldMps = value is { } mps ? Math.Max(0, mps) : null;
            }
        }
    }

    /// <summary>Ob es gerade einen Weg fährt (Anfahrt, auf die Spur, Aufstellen, Rückfahrt).</summary>
    public bool OnPath => Phase is FireVehiclePhase.Responding or FireVehiclePhase.Joining or FireVehiclePhase.Positioning or FireVehiclePhase.Returning;

    /// <summary>
    /// Wie weit voraus auf dem eigenen Weg (bis <paramref name="lookAheadM"/>) ein Punkt höchstens <paramref name="halfWidthM"/> neben
    /// dem Weg liegt; nur vor dem Fahrzeug, null = nicht.
    /// </summary>
    public double? AheadOnPath(double east, double north, double lookAheadM, double halfWidthM)
    {
        lock (_lock)
        {
            if (_path is not { } path || !OnPath)
            {
                return null;
            }

            var rad = HeadingDeg * Math.PI / 180;
            if (((east - EastM) * Math.Sin(rad)) + ((north - NorthM) * Math.Cos(rad)) <= 0)
            {
                return null;
            }

            for (var s = 0.0; s <= lookAheadM && DistanceM + s <= path.LengthM; s += 2)
            {
                var (e, n, _) = path.At(DistanceM + s);
                if (Math.Sqrt(((e - east) * (e - east)) + ((n - north) * (n - north))) <= halfWidthM)
                {
                    return s;
                }
            }

            return null;
        }
    }

    /// <summary>Neue Lage des Flugzeugs auf der Spur und seine Geschwindigkeit über Grund (m/s).</summary>
    public void SetAircraft(double trailS, double groundSpeedMps)
    {
        lock (_lock)
        {
            _aircraftS = trailS;
            _aircraftMps = Math.Max(0, groundSpeedMps);
        }
    }

    /// <summary>
    /// Einen Weg fahren (<paramref name="phase"/>: Joining, Positioning oder Returning); am Ende <paramref name="arrive"/>, mit
    /// <paramref name="finalHeadingDeg"/> als Blickrichtung (z. B. zum Flugzeug). Beim Joining folgt es danach der Spur ab
    /// <paramref name="joinS"/> mit <paramref name="gapM"/> Abstand.
    /// </summary>
    public void Drive(TaxiPath path, FireVehiclePhase phase, FireVehiclePhase arrive, double maxMps, double? finalHeadingDeg = null, double joinS = 0, double gapM = 0)
    {
        lock (_lock)
        {
            if (Phase == FireVehiclePhase.Gone)
            {
                return;
            }

            Phase = phase;
            TrailS = double.NaN;
            _joinS = joinS;
            _yieldMps = null;
            if (gapM > 0)
            {
                _gapM = gapM;
            }

            SetPath(path, maxMps, arrive, finalHeadingDeg);
        }
    }

    /// <summary>Gleich der Spur folgen (steht schon darauf), ab <paramref name="trailS"/> mit <paramref name="gapM"/> Abstand.</summary>
    public void Follow(double trailS, double gapM)
    {
        lock (_lock)
        {
            if (Phase == FireVehiclePhase.Gone)
            {
                return;
            }

            Phase = FireVehiclePhase.Following;
            TrailS = trailS;
            _gapM = gapM;
            _path = null;
        }
    }

    /// <summary>Soll-Abstand ändern (Bahn → Rollweg).</summary>
    public void SetGap(double gapM)
    {
        lock (_lock)
        {
            _gapM = gapM;
        }
    }

    /// <summary>Sofort weg (Verbindung, Abbruch ohne Rückfahrt).</summary>
    public void Remove()
    {
        lock (_lock)
        {
            Phase = FireVehiclePhase.Gone;
            SpeedMps = 0;
        }
    }

    /// <summary>Rechnet <paramref name="dt"/> Sekunden weiter.</summary>
    public void Advance(double dt)
    {
        lock (_lock)
        {
            if (dt <= 0 || Phase == FireVehiclePhase.Gone)
            {
                return;
            }

            _ageS += dt;
            if (GroundTargetFt is { } ground)
            {
                GroundFt = GroundFt < ground
                    ? Math.Min(ground, GroundFt + (TrafficKinematics.GroundSlewFtPerSec * dt))
                    : Math.Max(ground, GroundFt - (TrafficKinematics.GroundSlewFtPerSec * dt));
            }

            if (!double.IsNaN(_aircraftS))
            {
                // Zwischen zwei Meldungen mit der Geschwindigkeit des Flugzeugs weiter, aber nicht über das Ende der Spur hinaus
                _aircraftS = Math.Min(_trail.LengthM + 5, _aircraftS + (_aircraftMps * dt));
            }

            var (east, north, heading, speed) = (EastM, NorthM, HeadingDeg, SpeedMps);
            switch (Phase)
            {
                case FireVehiclePhase.Responding:
                case FireVehiclePhase.Joining:
                case FireVehiclePhase.Positioning:
                case FireVehiclePhase.Returning:
                    MoveOnPath(dt);
                    break;
                case FireVehiclePhase.Following:
                    MoveOnTrail(dt);
                    break;
                default:
                    SpeedMps = 0;
                    break;
            }

            Roll(east, north, heading, dt);
            _brakeHoldS = speed - SpeedMps > BrakeDecelMps2 * dt ? BrakeHoldS : Math.Max(0, _brakeHoldS - dt);
        }
    }

    /// <summary>
    /// Strecke und Krümmung aus der Bewegung dieses Takts. Sprünge (neuer Weg, Drehen im Stand) zählen nicht: über 30 m in einem Takt
    /// oder weniger als 2 cm Weg.
    /// </summary>
    private void Roll(double fromEast, double fromNorth, double fromHeading, double dt)
    {
        var moved = Math.Sqrt(Math.Pow(EastM - fromEast, 2) + Math.Pow(NorthM - fromNorth, 2));
        if (moved < 0.02 || moved > 30)
        {
            return;
        }

        OdometerM += moved;
        var turn = ((HeadingDeg - fromHeading + 540) % 360) - 180;
        var raw = Math.Clamp(turn * Math.PI / 180 / moved, -MaxCurvaturePerM, MaxCurvaturePerM);
        CurvaturePerM += (raw - CurvaturePerM) * (dt / (CurvatureSmoothingS + dt));
    }

    /// <summary>Lage für den Sim: Bezugspunkt <see cref="HeightFt"/> über dem Boden, waagerecht.</summary>
    public AiPose Pose()
    {
        lock (_lock)
        {
            var (lat, lon) = _toLatLon(EastM, NorthM);
            return new AiPose(lat, lon, GroundFt + HeightFt, 0, 0, HeadingDeg, Vars());
        }
    }

    /// <summary>
    /// Z-Variablen für das Modell (docs/research/feuerwehr-fahrzeuge.md): Strecke für die Räder, Krümmung für die Lenkung, Tempo für das
    /// Motorgeräusch, Bremslicht und Martinshorn.
    /// </summary>
    public AiObjectVar[] Vars()
    {
        lock (_lock)
        {
            if (_ageS < VarsDelayS)
            {
                return [];
            }

            return
            [
                new("L:1:DUOPILO_WEG", OdometerM, 0.001),
                new("L:1:DUOPILO_KURVE", CurvaturePerM, 0.0002),
                new("L:1:DUOPILO_TEMPO", SpeedMps, 0.05),
                new("L:1:DUOPILO_BREMSE", _brakeHoldS > 0 ? 1 : 0, 0.5),
                new("L:1:DUOPILO_SIRENE", SirenAllowed && Phase == FireVehiclePhase.Responding && SpeedMps > 0.5 ? 1 : 0, 0.5),
                new("L:1:DUOPILO_HORN", Horn, 0.5),
                new("L:1:DUOPILO_LICHTFARBE", LightColour, 0.5),
            ];
        }
    }

    /// <summary>Wunschgeschwindigkeit auf der Spur: Flugzeug plus <see cref="FireProfile.GapGain"/> je Meter zu viel Abstand, nie zu nah.</summary>
    public static double TrailTargetMps(double gapM, double targetGapM, double aircraftMps)
    {
        var excess = gapM - targetGapM;

        // Steht das Flugzeug, bleibt auch das Fahrzeug stehen, sobald es nah genug ist (kein Kriechen um die letzten Meter)
        if (aircraftMps < 0.5 && excess < 5)
        {
            return 0;
        }

        return Math.Clamp(aircraftMps + (FireProfile.GapGain * excess), 0, FireProfile.MaxMps);
    }

    private void SetPath(TaxiPath path, double maxMps, FireVehiclePhase arrive, double? finalHeadingDeg)
    {
        _path = path;
        _pathMaxMps = maxMps;
        _arrive = arrive;
        _arriveHeading = finalHeadingDeg;
        DistanceM = 0;
    }

    private void MoveOnPath(double dt)
    {
        if (_path is not { } path || path.LengthM < 0.1)
        {
            Arrive();
            return;
        }

        var allowed = Math.Min(_pathMaxMps, path.AllowedMps(DistanceM, FireProfile.DecelMps2));
        if (_yieldMps is { } yieldMps)
        {
            allowed = Math.Min(allowed, yieldMps);
        }

        Accelerate(allowed, dt);
        if (SpeedMps <= 0 && path.LengthM - DistanceM > 0.05 && _yieldMps is not < 0.05)
        {
            SpeedMps = Math.Min(0.3, path.LengthM - DistanceM);
        }

        DistanceM = Math.Min(path.LengthM, DistanceM + (SpeedMps * dt));
        var (east, north, heading) = path.At(DistanceM);
        (EastM, NorthM, HeadingDeg) = (east, north, heading);
        if (DistanceM >= path.LengthM - 0.05)
        {
            Arrive();
        }
    }

    private void Arrive()
    {
        SpeedMps = 0;
        if (_arriveHeading is { } heading)
        {
            HeadingDeg = heading;
        }

        if (_arrive == FireVehiclePhase.Following)
        {
            Phase = FireVehiclePhase.Following;
            TrailS = _joinS;
            _path = null;
            return;
        }

        Phase = _arrive;
        _path = null;
    }

    private void MoveOnTrail(double dt)
    {
        if (double.IsNaN(_aircraftS) || double.IsNaN(TrailS))
        {
            SpeedMps = 0;
            return;
        }

        var gap = _aircraftS - TrailS;
        var target = TrailTargetMps(gap, _gapM, _aircraftMps);

        // Kurven auf der Spur: sqrt(Querbeschleunigung · Radius), Radius grob aus der Kursänderung auf den nächsten 25 m
        var turn = _trail.TurnAheadDeg(TrailS, 25);
        if (turn > 15)
        {
            var radius = 25 / (turn * Math.PI / 180);
            target = Math.Min(target, Math.Max(FireProfile.MinTurnMps, Math.Sqrt(FireProfile.LateralMps2 * radius)));
        }

        Accelerate(target, dt);

        // Nie näher als der halbe Soll-Abstand heran
        var limit = Math.Max(TrailS, _aircraftS - (_gapM * 0.5));
        TrailS = Math.Min(limit, TrailS + (SpeedMps * dt));
        if (TrailS >= limit)
        {
            SpeedMps = Math.Min(SpeedMps, _aircraftMps);
        }

        var (east, north, heading) = _trail.At(TrailS);
        (EastM, NorthM) = (east, north);
        if (SpeedMps > 0.2)
        {
            HeadingDeg = heading;
        }
    }

    private void Accelerate(double targetMps, double dt) =>
        SpeedMps = SpeedMps < targetMps
            ? Math.Min(targetMps, SpeedMps + (FireProfile.AccelMps2 * dt))
            : Math.Max(targetMps, SpeedMps - (FireProfile.DecelMps2 * dt));
}
