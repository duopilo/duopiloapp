using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Lage des eigenen Flugzeugs für den Einsatz (Meter östlich/nördlich wie im Rollweg-Netz).</summary>
public sealed record FireAircraftState(double EastM, double NorthM, double HeadingDeg, double GroundSpeedMps, bool OnGround);

/// <summary>
/// Ein Einsatz ohne Sim: Spur des Flugzeugs ab dem Aufsetzen, und was jedes Fahrzeug als Nächstes tut.
/// <list type="bullet">
/// <item>Am Haltepunkt: Ist das Flugzeug vorbei, fährt das Fahrzeug gleich auf die Spur dahinter und folgt (bis beta.44.feuerwehr.19
/// erst unter 40 kt – Nachtest EDDK 05.11.: FLF 1 stand an der Aufsetzzone, bis der A330 fast stand).</item>
/// <item>Das FLF am Bahnende fährt ab dem Aufsetzen über die Bahn entgegen und hält vor dem Flugzeug, solange es rollt.</item>
/// <item>Bleibt das Flugzeug stehen, bevor es ein Fahrzeug passiert hat, fährt das Fahrzeug ihm entgegen – auch von unterwegs.</item>
/// <item>Steht das Flugzeug, stellen sich die Fahrzeuge auf ihre Plätze rundherum (nie seitlich in Achsrichtung der Räder).</item>
/// <item>Rollt es weiter, folgt nur FLF 1 bis zum Stand, die anderen fahren zurück. Steht es dort eine Minute, rücken alle ein.</item>
/// </list>
/// <see cref="Update"/> läuft im Takt des Dienstes; die Fahrzeuge rechnen ihre Bewegung selbst auf dem SimConnect-Thread.
/// </summary>
public sealed class FireOperation
{
    /// <summary>
    /// Entgegen vom Bahnende, solange das Flugzeug rollt: Es bleibt vor der Stelle, an der das Flugzeug mit dieser Verzögerung (m/s²,
    /// vorsichtig gerechnet; ein Airliner bremst meist mit 1,5–2,5) zum Stehen käme, plus <see cref="ApproachClearM"/>.
    /// </summary>
    public const double ApproachDecelMps2 = 1.2;

    /// <summary>So viel Luft bleibt vor der vorausgerechneten Stoppstelle des Flugzeugs.</summary>
    public const double ApproachClearM = 150;

    /// <summary>
    /// Mit dieser Verzögerung (m/s²) rechnet das entgegenkommende FLF die Stelle, zu der es fährt (die Mittellinie dort, wo das Flugzeug
    /// wohl steht); die Grenze davor rechnet vorsichtiger mit <see cref="ApproachDecelMps2"/>.
    /// </summary>
    public const double ExpectedDecelMps2 = 2.0;

    /// <summary>Breite der Zone vor dem Flugzeug, in die das entgegenkommende FLF nicht fährt: halbe Bahnbreite plus so viel.</summary>
    public const double ApproachSideM = 25;

    /// <summary>So lange muss das Flugzeug auf der Bahn stehen, bis die Fahrzeuge es umstellen.</summary>
    public const double StopOnRunwaySeconds = 6;

    /// <summary>Abseits der Bahn so lange (vor der ersten Kontrolle) – kurze Halte an Kreuzungen lösen nichts aus.</summary>
    public const double StopOffRunwaySeconds = 20;

    /// <summary>Steht das Flugzeug nach der Kontrolle so lange abseits der Bahn (am Stand), rücken alle ein.</summary>
    public const double FinishedSeconds = 60;

    /// <summary>Notfall am Boden: so lange bleiben die Fahrzeuge am stehenden Flugzeug.</summary>
    public const double GroundIncidentSeconds = 180;

    /// <summary>
    /// Beim Notfall am Boden: so weit vom Flugzeug weg fährt ein Fahrzeug, das noch unterwegs ist, ihm direkt entgegen (sonst auf seinem
    /// geplanten Weg weiter). Nach einer Landung gilt keine Grenze: Steht das Flugzeug, fahren alle hin (Nachtest EDDK 05.11.: FLF 3 fuhr
    /// erst noch bis zum Bahnende und kam 68 s nach dem Stopp dort an).
    /// </summary>
    public const double DirectRangeM = 300;

    /// <summary>Weiter als das von der Spur weg (z. B. andere Bahn): über die Rollwege statt geradeaus auf die Spur.</summary>
    public const double RouteJoinM = 300;

    /// <summary>Weiter als das vom stehenden Flugzeug weg: über die Rollwege an das Flugzeug statt geradeaus.</summary>
    public const double PositionRouteM = 800;

    private readonly List<FireVehicle> _vehicles = [];
    private readonly object _lock = new();
    private bool _landed;
    private bool _seenAirborne;
    private double _airborneSeconds;
    private double _stoppedSeconds;
    private double _offRunwaySeconds;
    private bool _stoppedOnRunway;
    private bool _inspected;
    private bool _released;
    private FireVehicle? _escort;

    // Fahrzeuge vom Bahnende, die dem noch rollenden Flugzeug über die Bahn entgegenfahren (und wer es schon versucht hat)
    private readonly HashSet<FireVehicle> _approaching = [];
    private readonly HashSet<FireVehicle> _approachTried = [];

    /// <summary>
    /// So viel näher am Flugzeug muss ein anderes FLF sein, damit statt FLF 1 es bis zum Stand folgt (sonst bleibt es bei FLF 1).
    /// </summary>
    public const double EscortPreferFirstM = 200;

    /// <summary>So weit vor einem Gebäude hält ein Fahrzeug, wenn sein gerader Weg zum Platz hineinführen würde.</summary>
    public const double BuildingClearM = 5;

    /// <summary>So weit müssen die Plätze zweier Fahrzeuge am Flugzeug mindestens auseinander liegen.</summary>
    public const double MinPostSpacingM = 10;

    // Plätze der Fahrzeuge am Flugzeug (Ost/Nord), damit keins auf ein anderes fährt
    private readonly Dictionary<FireVehicle, (double East, double North)> _posts = [];

    /// <summary>So weit voraus auf dem eigenen Weg schaut ein Fahrzeug nach einem anderen.</summary>
    public const double LookAheadM = 40;

    /// <summary>Halbe Fahrspur: ein Fahrzeug so weit neben dem Weg steht im Weg.</summary>
    public const double LaneHalfWidthM = 3.5;

    /// <summary>Abstand Mitte zu Mitte, bei dem es hinter einem anderen hält (ein FLF ist rund 12 m lang).</summary>
    public const double StopGapM = 16;

    /// <summary>So lange wartet es hinter einem Fahrzeug, das nicht fährt (am Platz), dann fährt es weiter.</summary>
    public const double PatienceS = 6;

    private readonly Dictionary<FireVehicle, double> _waitedS = [];
    private readonly Dictionary<FireVehicle, double> _ignoreStandingUntil = [];
    private double _clock;

    private readonly ILogger? _log;

    public FireOperation(TaxiGraph graph, FireAlertLevel level, FireDispatchPlan plan, TrafficWake wake, int category, ILogger? log = null)
    {
        _log = log;
        Graph = graph;
        Level = level;
        Plan = plan;
        Wake = wake;
        Category = category;
    }

    public TaxiGraph Graph { get; }

    public FireAlertLevel Level { get; }

    public FireDispatchPlan Plan { get; }

    public TrafficWake Wake { get; }

    public int Category { get; }

    public FireTrail Trail { get; } = new();

    /// <summary>Das Flugzeug ist aufgesetzt (bzw. stand beim Notfall am Boden schon).</summary>
    public bool Landed => _landed;

    /// <summary>Alle Fahrzeuge fahren zurück (Einsatz zu Ende oder abgebrochen).</summary>
    public bool Returning { get; private set; }

    public IReadOnlyList<FireVehicle> Vehicles
    {
        get
        {
            lock (_lock)
            {
                return [.. _vehicles];
            }
        }
    }

    /// <summary>Stand für Status und Log.</summary>
    public string State
    {
        get
        {
            var vehicles = Vehicles;
            if (Returning)
            {
                return "returning";
            }

            if (vehicles.Count == 0)
            {
                return "dispatching";
            }

            if (vehicles.Any(v => v.Phase is FireVehiclePhase.AtAircraft or FireVehiclePhase.Positioning))
            {
                return "at_aircraft";
            }

            if (vehicles.Any(v => v.Phase is FireVehiclePhase.Following or FireVehiclePhase.Joining))
            {
                return "following";
            }

            return vehicles.Any(v => v.Phase == FireVehiclePhase.Standby) ? "standby" : "responding";
        }
    }

    /// <summary>Gespawntes Fahrzeug übernehmen; auf jedem seiner Wege hält es vor den Bahnen (<see cref="NeedsClearance"/>).</summary>
    public void Add(FireVehicle vehicle)
    {
        vehicle.UseCrossings(path => FireCrossings.Find(Graph, path));
        lock (_lock)
        {
            _vehicles.Add(vehicle);
        }

        if (Returning)
        {
            SendBack(vehicle);
        }
    }

    /// <summary>Fahrzeug, das weg ist (entfernt), vergessen.</summary>
    public void Forget(FireVehicle vehicle)
    {
        lock (_lock)
        {
            _vehicles.Remove(vehicle);
        }
    }

    /// <summary>
    /// Braucht das Fahrzeug eine Freigabe für diese Bahn (frei von Verkehr auf der Bahn und im Endanflug)? Auf der Rückfahrt vor jeder
    /// Bahn. Bei Mayday mit Blaulicht vor keiner: Die Feuerwehr hat Vorfahrt, und der Flughafen ist für den eigenen KI-Verkehr gesperrt
    /// (<see cref="Duopilo.Traffic.AirportClosures"/>; Nachtest EDDK 05.11.). Sonst nicht für die Bahn des Notfalls, sobald das Flugzeug
    /// gelandet ist – dort arbeitet die Feuerwehr –, vor der Landung (das Flugzeug kommt im Endanflug) vor jeder.
    /// </summary>
    public bool NeedsClearance(FireVehicle vehicle, FireCrossing crossing) =>
        vehicle.Phase == FireVehiclePhase.Returning || Returning
        || (Level != FireAlertLevel.FullEmergency && (!_landed || !IsEmergencyRunway(crossing.Runway)));

    /// <summary>Die Landebahn des Notfalls als Bahn im Netz (beide Richtungen), sonst null.</summary>
    public AirportRunway? EmergencyRunway => Graph.Runways.FirstOrDefault(IsEmergencyRunway);

    /// <summary>Die Landebahn des Notfalls (beide Richtungen).</summary>
    public bool IsEmergencyRunway(AirportRunway runway) =>
        Plan.Runway is { } end
        && (AirportRunway.Normalize(runway.Primary) == AirportRunway.Normalize(end.Designation)
            || (runway.Secondary.Length > 0 && AirportRunway.Normalize(runway.Secondary) == AirportRunway.Normalize(end.Designation)));

    /// <summary>Alle zurück zur Wache (Abbruch, Einsatz zu Ende).</summary>
    public void ReturnAll()
    {
        Returning = true;
        foreach (var vehicle in Vehicles)
        {
            if (vehicle.Phase is not (FireVehiclePhase.Returning or FireVehiclePhase.Gone))
            {
                SendBack(vehicle);
            }
        }
    }

    /// <summary>Takt: Spur fortschreiben, Lage an die Fahrzeuge, Entscheidungen je Fahrzeug.</summary>
    public void Update(FireAircraftState aircraft, double dt)
    {
        dt = Math.Max(0, dt);
        TrackAircraft(aircraft, dt);
        var aircraftS = Trail.LengthM;
        var vehicles = Vehicles;
        foreach (var vehicle in vehicles)
        {
            vehicle.SetAircraft(aircraftS, aircraft.GroundSpeedMps);
        }

        KeepDistance(vehicles, dt);

        if (Returning || !_landed)
        {
            return;
        }

        var runwayNow = Graph.RunwayAt(aircraft.EastM, aircraft.NorthM);
        var onRunway = runwayNow is not null;
        UpdateGaps(vehicles, onRunway);
        var escort = Escort(vehicles, aircraft);
        var stopped = _stoppedSeconds >= (onRunway ? StopOnRunwaySeconds : StopOffRunwaySeconds);
        var moving = aircraft.GroundSpeedMps > 2;
        var rollingOut = runwayNow is { } landingRunway && IsEmergencyRunway(landingRunway) && !stopped && !_released && !_inspected;

        foreach (var vehicle in vehicles)
        {
            switch (vehicle.Phase)
            {
                case FireVehiclePhase.Responding:
                case FireVehiclePhase.Standby:
                    // Vom Bahnende entgegen, solange das Flugzeug ausrollt; steht es, geht es weiter wie bei allen anderen
                    if (rollingOut && (_approaching.Contains(vehicle) || StartApproach(vehicle, aircraft)))
                    {
                        HoldBeforeAircraft(vehicle, aircraft);
                        break;
                    }

                    if (_approaching.Remove(vehicle))
                    {
                        vehicle.HoldAtS = null;
                    }

                    UpdateWaiting(vehicle, aircraft, aircraftS, stopped, escort);
                    break;

                case FireVehiclePhase.Joining:
                    // Nach dem Abrollen folgt nur noch der Begleiter; wer noch auf dem Weg auf die Spur ist, rückt ein
                    if (_released && !ReferenceEquals(vehicle, escort))
                    {
                        SendBack(vehicle);
                    }

                    break;

                case FireVehiclePhase.Following:
                    // Nach dem Abrollen (nur noch FLF 1 als Begleitung) bleibt es beim Halt hinter dem Flugzeug, statt es am Gate
                    // oder an einer Kreuzung zu umstellen
                    if (stopped && !_inspected && !_released)
                    {
                        Position(vehicle, aircraft);
                    }
                    else if (_released && !ReferenceEquals(vehicle, escort))
                    {
                        SendBack(vehicle);
                    }

                    break;

                case FireVehiclePhase.AtAircraft:
                    if (moving)
                    {
                        // Kontrolle vorbei: FLF 1 folgt bis zum Stand, die anderen rücken ein
                        _inspected = true;
                        _released = true;
                        if (ReferenceEquals(vehicle, escort))
                        {
                            Join(vehicle, aircraft, aircraftS, onRunway);
                        }
                        else
                        {
                            SendBack(vehicle);
                        }
                    }

                    break;
            }
        }

        // Ohne Halt auf der Bahn abgerollt: auch dann folgt nur noch FLF 1; wer noch wartet (etwa hinter dem Abrollweg, an dem das
        // Flugzeug nie vorbeikam – Test EDDL 02.11.), rückt gleich ein. Nicht beim Notfall am Boden: da steht das Flugzeug von Anfang an
        // abseits der Bahn, und alle sollen sich aufstellen (Test EDDL V81, 03.11.: FLF 2 und ELW kamen von hinten an und drehten um).
        if (Level != FireAlertLevel.GroundIncident && _offRunwaySeconds > 10 && !_stoppedOnRunway)
        {
            _released = true;
        }

        if (_released)
        {
            // Der Begleiter bleibt ab jetzt derselbe, auch wenn ein anderes Fahrzeug später näher kommt
            _escort ??= escort;
            foreach (var vehicle in vehicles.Where(v => v.Phase is FireVehiclePhase.Standby or FireVehiclePhase.Responding && !ReferenceEquals(v, escort)))
            {
                SendBack(vehicle);
            }
        }

        // Am Stand angekommen (nach der Kontrolle) bzw. Notfall am Boden erledigt: alle einrücken
        if (!onRunway && _stoppedSeconds >= (Level == FireAlertLevel.GroundIncident ? GroundIncidentSeconds : FinishedSeconds)
            && (_inspected || _released || Level == FireAlertLevel.GroundIncident)
            && vehicles.All(v => v.Phase is not (FireVehiclePhase.Responding or FireVehiclePhase.Joining or FireVehiclePhase.Positioning)))
        {
            ReturnAll();
        }

        // Die Bahn des Notfalls nach der Landung gleich freigeben (auch für die eben neu gesetzten Wege): dort hält niemand
        foreach (var vehicle in vehicles)
        {
            if (vehicle.NextCrossing is { } crossing && !NeedsClearance(vehicle, crossing))
            {
                vehicle.ClearCrossing();
            }
        }
    }

    /// <summary>
    /// Platz eines Fahrzeugs am Flugzeug im Flugzeug-System (x nach vorn, y nach rechts, Meter), aus dem Grundmaß <paramref name="u"/>:
    /// FLF vor links und hinter rechts bzw. links am Hauptfahrwerk, ein viertes vorn rechts, ELW schräg vor der Nase, die anderen weiter
    /// weg.
    /// </summary>
    public static (double X, double Y) Post(FireUnit unit, double u) => unit.Kind switch
    {
        FireVehicleKind.Flf => unit.Number switch
        {
            1 => (u, -u),
            2 => (-u, u),
            4 => (u, u),
            _ => (-u, -u),
        },
        FireVehicleKind.Elw => (1.3 * u, 0.6 * u),
        FireVehicleKind.Hlf => (-1.4 * u, 1.2 * u),
        FireVehicleKind.Rtf => unit.Number == 1 ? (0.45 * u, -1.35 * u) : (-0.9 * u, -1.4 * u),
        _ => (1.7 * u, -0.5 * u),
    };

    /// <summary>
    /// Platz an einem Flugzeug auf einer Parkposition: nur hinter und rechts neben ihm – vor der Nase steht das Terminal, links vorn die
    /// Fluggastbrücke. FLF rechts am Flügel und hinter dem Heck, ELW mit Überblick hinter dem Heck, Rettungstreppe an der rechten Tür.
    /// </summary>
    public static (double X, double Y) StandPost(FireUnit unit, double u) => unit.Kind switch
    {
        FireVehicleKind.Flf => unit.Number switch
        {
            1 => (0, 1.4 * u),
            2 => (-1.4 * u, 0.6 * u),
            4 => (-0.9 * u, 1.4 * u),
            _ => (-1.4 * u, -0.6 * u),
        },
        FireVehicleKind.Elw => (-2.1 * u, 0),
        FireVehicleKind.Hlf => (-1.9 * u, 1.5 * u),
        FireVehicleKind.Rtf => unit.Number == 1 ? (0.45 * u, 1.35 * u) : (-0.9 * u, 1.4 * u),
        _ => (-2.4 * u, 1.1 * u),
    };

    /// <summary>
    /// Weg vom Fahrzeug zu seinem Platz im Flugzeug-System, um das Flugzeug herum: Steht es auf der anderen Seite, zuerst hinter das Heck
    /// bzw. vor die Nase, dann auf die Seite des Platzes; führt die gerade Linie nah am Flugzeug vorbei, über einen Punkt seitlich.
    /// </summary>
    /// <param name="noseBlocked">An einer Parkposition: nie vor der Nase herum (Terminal), immer hinter dem Heck.</param>
    public static List<(double X, double Y)> DetourPoints((double X, double Y) from, (double X, double Y) post, double u, bool noseBlocked = false)
    {
        var points = new List<(double X, double Y)> { from };
        var side = post.Y != 0 ? Math.Sign(post.Y) : (from.Y != 0 ? Math.Sign(from.Y) : 1);
        var end = from.X < 0 || noseBlocked ? -1.6 * u : 1.6 * u;
        if (Math.Sign(from.Y) != side && Math.Abs(from.X) < 1.6 * u)
        {
            // Parallel zum Rumpf hinter das Heck (bzw. vor die Nase), dann auf die Seite des Platzes
            points.Add((end, from.Y));
            points.Add((end, side * 1.4 * u));
        }
        else if (SegmentDistance(0, 0, from.X, from.Y, post.X, post.Y) < 0.9 * u)
        {
            points.Add((Math.Clamp(from.X, -1.4 * u, noseBlocked ? 0 : 1.4 * u), side * 1.4 * u));
        }

        points.Add(post);
        return points;
    }

    /// <summary>Punkt im Flugzeug-System (x nach vorn, y nach rechts) als Ost/Nord.</summary>
    public static (double East, double North) ToLocal(FireAircraftState aircraft, double x, double y)
    {
        var rad = aircraft.HeadingDeg * Math.PI / 180;
        var (fx, fy) = (Math.Sin(rad), Math.Cos(rad));
        var (rx, ry) = (Math.Cos(rad), -Math.Sin(rad));
        return (aircraft.EastM + (x * fx) + (y * rx), aircraft.NorthM + (x * fy) + (y * ry));
    }

    /// <summary>Ost/Nord als Punkt im Flugzeug-System.</summary>
    public static (double X, double Y) ToAircraft(FireAircraftState aircraft, double east, double north)
    {
        var rad = aircraft.HeadingDeg * Math.PI / 180;
        var (dx, dy) = (east - aircraft.EastM, north - aircraft.NorthM);
        return ((dx * Math.Sin(rad)) + (dy * Math.Cos(rad)), (dx * Math.Cos(rad)) - (dy * Math.Sin(rad)));
    }

    private void TrackAircraft(FireAircraftState aircraft, double dt)
    {
        var onRunway = Graph.RunwayAt(aircraft.EastM, aircraft.NorthM) is not null;
        if (!aircraft.OnGround)
        {
            _seenAirborne = true;
        }

        // Aufgesetzt erst nach einem Flug (oder mit Landegeschwindigkeit auf einer Bahn). Steht das Flugzeug beim Alarm schon am Boden
        // (Test am Gate, Test EDDL 02.11.), warten die Fahrzeuge an den Haltepunkten, statt es für gelandet zu halten und einzurücken.
        var landing = aircraft.OnGround && (_landed || Level == FireAlertLevel.GroundIncident || _seenAirborne || (onRunway && aircraft.GroundSpeedMps > 15));
        if (landing)
        {
            _landed = true;
            _airborneSeconds = 0;
            Trail.Add(aircraft.EastM, aircraft.NorthM);
        }
        else if (_landed)
        {
            // Durchgestartet: Spur verwerfen, die Fahrzeuge bleiben in ihren Bereitstellungsräumen
            _airborneSeconds += dt;
            if (_airborneSeconds > 3)
            {
                _landed = false;
                Trail.Clear();
                _stoppedSeconds = 0;
                _offRunwaySeconds = 0;

                // Wer vom Bahnende entgegenkam, fährt von der Bahn zurück an seinen Haltepunkt
                foreach (var vehicle in _approaching)
                {
                    Retreat(vehicle);
                }

                _approaching.Clear();
                _approachTried.Clear();
            }
        }

        _stoppedSeconds = _landed && aircraft.GroundSpeedMps < 0.5 ? _stoppedSeconds + dt : 0;
        _offRunwaySeconds = _landed && !onRunway ? _offRunwaySeconds + dt : 0;
        if (_landed && onRunway && _stoppedSeconds >= StopOnRunwaySeconds)
        {
            _stoppedOnRunway = true;
        }
    }

    /// <summary>Am Haltepunkt oder auf dem Weg dorthin: auf die Spur, wenn das Flugzeug vorbei ist; entgegen, wenn es davor steht.</summary>
    private void UpdateWaiting(FireVehicle vehicle, FireAircraftState aircraft, double aircraftS, bool stopped, FireVehicle? escort)
    {
        if (Trail.Count < 2 && Level != FireAlertLevel.GroundIncident)
        {
            return;
        }

        // Vorbei = das Fahrzeug liegt hinter dem Flugzeug (auch nach einer Landung auf einer anderen Bahn)
        var distanceToAircraft = FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, aircraft.EastM, aircraft.NorthM);
        var passed = ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM).X < -20;

        // Noch unterwegs, aber das Flugzeug ist an Fahrzeug und Haltepunkt schon vorbei (später Alarm, Test EDDK 05.11.: FLF 1 fuhr erst
        // weiter zur Aufsetzzone und lag dann 2 km zurück): gleich auf die Spur bzw. an das stehende Flugzeug, nicht erst zum Haltepunkt.
        // Ohne Grenze für das Tempo des Flugzeugs (bis .19 erst unter 40 kt; Nachtest EDDK 05.11.: im Pan Pan lag FLF 1 so 1,8 km zurück)
        if (vehicle.Phase == FireVehiclePhase.Responding && Level != FireAlertLevel.GroundIncident && passed && HoldPassed(vehicle, aircraft)
            && (!_released || ReferenceEquals(vehicle, escort)))
        {
            if (stopped && !_inspected)
            {
                Position(vehicle, aircraft);
                return;
            }

            Join(vehicle, aircraft, aircraftS, Graph.RunwayAt(aircraft.EastM, aircraft.NorthM) is not null);
            return;
        }

        // Steht das Flugzeug, fahren alle hin – nach einer Landung auch von unterwegs, egal wie weit (Position wählt den Weg), und auch
        // wer hinter ihm unterwegs ist zu einem Haltepunkt vor ihm (Nachtest .20 EDDK 07.11.: FLF 3 bekam erst am Ende seines Wegs, 2 min
        // nach dem Stopp, seinen Platz)
        var behindOnTheWay = passed && vehicle.Phase == FireVehiclePhase.Responding && Level != FireAlertLevel.GroundIncident
            && !HoldPassed(vehicle, aircraft);
        if (stopped && !_inspected && (!passed || behindOnTheWay)
            && (vehicle.Phase == FireVehiclePhase.Standby || Level != FireAlertLevel.GroundIncident || distanceToAircraft < DirectRangeM))
        {
            Position(vehicle, aircraft);
            return;
        }

        // Am Haltepunkt und das Flugzeug ist vorbei: gleich auf die Spur, egal wie schnell es noch ist (Nachtest EDDK 05.11.: FLF 1 an der
        // Aufsetzzone fuhr erst unter 40 kt los, kurz vor dem Stopp des A330)
        if (vehicle.Phase == FireVehiclePhase.Standby && passed)
        {
            if (_released && !ReferenceEquals(vehicle, escort))
            {
                SendBack(vehicle);
                return;
            }

            if (stopped && !_inspected)
            {
                Position(vehicle, aircraft);
                return;
            }

            Join(vehicle, aircraft, aircraftS, Graph.RunwayAt(aircraft.EastM, aircraft.NorthM) is not null);
        }
    }

    /// <summary>Auf die Spur hinter dem Flugzeug: geradeaus schräg darauf, von weiter weg über die Rollwege.</summary>
    private void Join(FireVehicle vehicle, FireAircraftState aircraft, double aircraftS, bool onRunway)
    {
        var gap = BaseGap(onRunway) + (ColumnIndex(vehicle) * FireProfile.ColumnSpacingM);
        if (Trail.Count < 2)
        {
            return;
        }

        var nearest = Trail.NearestS(vehicle.EastM, vehicle.NorthM);
        var joinS = Math.Max(0, Math.Min(nearest + 15, aircraftS - gap));
        var (je, jn, _) = Trail.At(joinS);
        List<(double East, double North)>? points = null;
        var rel = ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM);
        if (rel.X > -10 && FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, aircraft.EastM, aircraft.NorthM) < RouteJoinM)
        {
            // Steht neben oder vor dem Flugzeug (nach dem Umstellen): seitlich vorbei nach hinten, nicht quer durch das Flugzeug
            var u = FireCategory.PostUnitM(Wake);
            var sideY = (rel.Y >= 0 ? 1 : -1) * Math.Max(Math.Abs(rel.Y), 1.4 * u);
            var target = ToAircraft(aircraft, je, jn);
            var aroundX = Math.Min(-1.6 * u, target.X + 20);
            points = new[] { rel, (rel.X, sideY), (aroundX, sideY), target }.Select(p => ToLocal(aircraft, p.Item1, p.Item2)).ToList();
        }
        else if (FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, je, jn) > RouteJoinM)
        {
            points = WithFreeRunway(() => FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, je, jn));
        }

        points ??= [(vehicle.EastM, vehicle.NorthM), (je, jn)];
        vehicle.Drive(FirePlanner.BuildPath(points, FireProfile.MaxMps), FireVehiclePhase.Joining, FireVehiclePhase.Following, FireProfile.MaxMps, null, joinS, gap);
    }

    /// <summary>Auf den Platz am stehenden Flugzeug, um das Flugzeug herum, Blick zum Flugzeug.</summary>
    private void Position(FireVehicle vehicle, FireAircraftState aircraft)
    {
        var u = FireCategory.PostUnitM(Wake);
        var atStand = Plan.Stand is not null;
        var post = atStand ? StandPost(vehicle.Unit, u) : Post(vehicle.Unit, u);
        var from = ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM);

        // Geradeaus (auch über Gras, wie echte Löschfahrzeuge), nur von weit weg und nicht von derselben Bahn über die Rollwege
        var sameRunway = Graph.RunwayAt(vehicle.EastM, vehicle.NorthM) is { } on && ReferenceEquals(on, Graph.RunwayAt(aircraft.EastM, aircraft.NorthM));
        var far = !sameRunway && FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, aircraft.EastM, aircraft.NorthM) > PositionRouteM;
        _posts.Remove(vehicle);

        // Der geplante Platz, sonst ein Ausweichplatz: keiner durch ein Gebäude, keiner auf einem anderen Fahrzeug (Test EDDL B9, 03.11.:
        // zwei FLF hielten vor demselben Gebäude und standen ineinander)
        List<(double East, double North)>? chosen = null;
        var how = "post";
        foreach (var (candidate, index) in PostCandidates(post, u, atStand).Select((c, i) => (c, i)))
        {
            var local = DetourPoints(from, candidate, u, atStand).Select(p => ToLocal(aircraft, p.X, p.Y)).ToList();
            var offNetwork = 0;
            if (far && WithFreeRunway(() => FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, local[^1].East, local[^1].North)) is { } route)
            {
                // Von weit weg (z. B. vom Haltepunkt am Bahnende) über das Netz – die Bahn des Notfalls wie ein Rollweg (Nachtest EDDK
                // 05.11.: FLF 3 fuhr 2,3 km über Rollwege und Straßen statt über die 14L) –, die letzten Meter wie geplant
                route.RemoveAt(route.Count - 1);
                local = [.. route, .. local.Skip(Math.Max(1, local.Count - 2))];
                offNetwork = route.Count - 1;
            }

            var end = local[^1];
            if (_posts.Values.Any(p => FirePlanner.Distance(p.East, p.North, end.East, end.North) < Math.Min(MinPostSpacingM, 0.35 * u))
                || Graph.FirstBuildingHitM([.. local.Skip(offNetwork)]) is not null)
            {
                continue;
            }

            chosen = local;
            how = index == 0 ? "post" : "alternative";
            post = candidate;
            break;
        }

        if (chosen is null)
        {
            // Kein freier Platz: stehen bleiben (am Ende der Anfahrt, in der Kolonne), nur zum Flugzeug drehen
            var (dx, dy) = (aircraft.EastM - vehicle.EastM, aircraft.NorthM - vehicle.NorthM);
            var length = Math.Max(0.1, Math.Sqrt((dx * dx) + (dy * dy)));
            chosen = [(vehicle.EastM, vehicle.NorthM), (vehicle.EastM + (dx / length * 0.5), vehicle.NorthM + (dy / length * 0.5))];
            how = "stay";
            post = ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM);
        }

        _posts[vehicle] = chosen[^1];
        _log?.LogInformation(Loc.Log("log.fire.post"), vehicle.Unit.Name, Math.Round(post.X), Math.Round(post.Y), how, atStand ? "stand" : "open");
        var (pe, pn) = chosen[^1];
        var heading = TaxiPath.Heading(aircraft.EastM - pe, aircraft.NorthM - pn);
        var path = FirePlanner.BuildPath(chosen, FireProfile.MaxMps);
        vehicle.Drive(path, FireVehiclePhase.Positioning, FireVehiclePhase.AtAircraft, path.LengthM > 200 ? FireProfile.MaxMps : FireProfile.PositionMps, heading);
    }

    /// <summary>
    /// Plätze zur Auswahl, der geplante zuerst: weiter hinten, weiter außen, noch weiter hinten, die andere Seite (am Gate nur hinter
    /// dem Flügel) und zuletzt schräg hinter dem Heck.
    /// </summary>
    public static IEnumerable<(double X, double Y)> PostCandidates((double X, double Y) post, double u, bool atStand)
    {
        yield return post;
        yield return (post.X - (0.5 * u), post.Y);
        yield return (post.X, post.Y * 1.35);
        yield return (post.X - u, post.Y);
        if (!atStand || post.X < 0)
        {
            yield return (post.X, -post.Y);
        }

        yield return (post.X - (1.5 * u), post.Y * 0.5);
        yield return (post.X - (1.5 * u), -post.Y * 0.5);
    }

    /// <summary>
    /// Zurück zur eigenen Wache über die Rollwege (ohne Blaulicht), dort weg: zur Parkposition selbst bzw. zur OSM-Wache. Ohne Route
    /// verschwindet das Fahrzeug an Ort und Stelle – geradeaus weiterzufahren hieße am Haltepunkt: auf die Bahn (Test EDDL 02.11.).
    /// </summary>
    private void SendBack(FireVehicle vehicle)
    {
        _posts.Remove(vehicle);
        var station = Plan.Vehicles.FirstOrDefault(v => v.Unit == vehicle.Unit)?.Station;

        // Ohne Blaulicht noch lieber über die Betriebsstraßen
        var roadCost = Graph.RoadCostFactor;
        Graph.RoadCostFactor = Math.Min(roadCost, FireProfile.ReturnRoadCostFactor);
        List<(double East, double North)>? points;
        try
        {
            points = station?.Parking is { } parking
                ? FirePlanner.RouteToNode(Graph, vehicle.EastM, vehicle.NorthM, parking)
                : null;
            points ??= FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, vehicle.HomeEastM, vehicle.HomeNorthM);
        }
        finally
        {
            Graph.RoadCostFactor = roadCost;
        }

        if (points is null)
        {
            vehicle.Remove();
            return;
        }

        vehicle.Drive(FirePlanner.BuildPath(points, FireProfile.ReturnMps), FireVehiclePhase.Returning, FireVehiclePhase.Gone, FireProfile.ReturnMps);
    }

    /// <summary>Soll-Abstände der Kolonne: auf der Bahn größer (Abgasstrahl), auf den Rollwegen kleiner.</summary>
    private void UpdateGaps(IReadOnlyList<FireVehicle> vehicles, bool onRunway)
    {
        foreach (var vehicle in vehicles.Where(v => v.Phase is FireVehiclePhase.Following or FireVehiclePhase.Joining))
        {
            vehicle.SetGap(BaseGap(onRunway) + (ColumnIndex(vehicle) * FireProfile.ColumnSpacingM));
        }
    }

    private double BaseGap(bool onRunway) => onRunway ? FireCategory.RunwayGapM(Wake) : FireCategory.TaxiGapM(Wake);

    /// <summary>
    /// Abstand zwischen den eigenen Fahrzeugen (Test EDDL 03.11.: auf der Rückfahrt fuhren sie gleichzeitig los und hingen ineinander).
    /// Wer einen Weg fährt, schaut <see cref="LookAheadM"/> auf seinem Weg voraus: Steht oder fährt dort ein anderes Fahrzeug
    /// (höchstens <see cref="LaneHalfWidthM"/> neben dem Weg), bremst es so, dass es <see cref="StopGapM"/> dahinter hält bzw. mit dessen
    /// Tempo folgt. Entgegenkommende zählen nicht (sie fahren aneinander vorbei). Sehen sich zwei gegenseitig oder stehen sie
    /// ineinander bzw. dicht nebeneinander (gleichzeitig losgefahren), fährt das mit Blaulicht zuerst, sonst das zuerst alarmierte.
    /// Hinter einem Fahrzeug, das nicht fährt (am Platz), wartet es <see cref="PatienceS"/>, dann fährt es weiter, damit nichts auf Dauer
    /// hängen bleibt.
    /// </summary>
    private void KeepDistance(IReadOnlyList<FireVehicle> vehicles, double dt)
    {
        _clock += dt;
        var order = vehicles.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        int Rank(FireVehicle v) => (v.BlueLight ? 0 : 1000) + order[v];
        double? Ahead(FireVehicle from, FireVehicle other)
        {
            if (other.Phase == FireVehiclePhase.Gone || ReferenceEquals(from, other))
            {
                return null;
            }

            var turn = Math.Abs(((other.HeadingDeg - from.HeadingDeg + 540) % 360) - 180);
            return other.SpeedMps > 0.5 && turn > 120 ? null : from.AheadOnPath(other.EastM, other.NorthM, LookAheadM, LaneHalfWidthM);
        }

        foreach (var vehicle in vehicles)
        {
            if (!vehicle.OnPath)
            {
                vehicle.YieldMps = null;
                _waitedS.Remove(vehicle);
                continue;
            }

            double? limit = null;
            var behindStanding = false;
            foreach (var other in vehicles)
            {
                var close = other.OnPath && !ReferenceEquals(other, vehicle) &&
                    FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, other.EastM, other.NorthM) < StopGapM / 2;
                var ahead = Ahead(vehicle, other) ?? (close && Ahead(other, vehicle) is null ? 0 : null);
                if (ahead is null || ((Ahead(other, vehicle) is not null || close) && Rank(other) > Rank(vehicle)))
                {
                    continue;
                }

                var standing = other.SpeedMps < 0.3 && !other.OnPath;
                if (standing && _ignoreStandingUntil.TryGetValue(vehicle, out var until) && _clock < until)
                {
                    continue;
                }

                var turn = Math.Abs(((other.HeadingDeg - vehicle.HeadingDeg + 540) % 360) - 180);
                // Bremsweg bis zum Halt hinter dem anderen; mit dessen Tempo erst ab halbem Abstand (sonst führen Fahrzeuge, die ineinander
                // stehen, gemeinsam los)
                var follow = turn < 60 ? other.SpeedMps * Math.Clamp((ahead.Value - (StopGapM / 2)) / (StopGapM / 2), 0, 1) : 0;
                var mps = Math.Sqrt(2 * FireProfile.DecelMps2 * Math.Max(0, ahead.Value - StopGapM)) + follow;
                limit = limit is { } l ? Math.Min(l, mps) : mps;
                behindStanding |= standing;
            }

            vehicle.YieldMps = limit;
            var waited = limit is < 0.1 && behindStanding && vehicle.SpeedMps < 0.1 ? _waitedS.GetValueOrDefault(vehicle) + dt : 0;
            if (waited > PatienceS)
            {
                _ignoreStandingUntil[vehicle] = _clock + 30;
                waited = 0;
            }

            _waitedS[vehicle] = waited;
        }
    }

    /// <summary>
    /// Platz in der Kolonne: die Folgenden nach ihrem Abstand zum Flugzeug, wer noch auf die Spur fährt, dahinter. Kommt FLF 1 später
    /// (Alarm kurz vor dem Aufsetzen), reiht es sich hinter den anderen ein, statt sie mit dem kleinsten Soll-Abstand zu überholen.
    /// </summary>
    private int ColumnIndex(FireVehicle vehicle)
    {
        var column = Vehicles
            .Where(v => ReferenceEquals(v, vehicle) || v.Phase is FireVehiclePhase.Following or FireVehiclePhase.Joining)
            .OrderBy(v => double.IsNaN(v.GapM) ? double.MaxValue : v.GapM)
            .ToList();
        return Math.Max(0, column.IndexOf(vehicle));
    }

    /// <summary>
    /// Das Fahrzeug, das bis zum Stand folgt: FLF 1 (sonst das erste), außer ein anderes FLF ist schon beim Flugzeug (folgt, stellt sich auf
    /// oder steht dort) und mehr als <see cref="EscortPreferFirstM"/> näher dran. Test EDDK 05.11.: Alarm 60 s vor dem Aufsetzen, FLF 2 und
    /// ELW folgten schon 500 m hinter dem A320 und rückten ein, FLF 1 musste von der Aufsetzzone 2 km aufholen. Nach dem Abrollen bleibt es
    /// beim einmal gewählten.
    /// </summary>
    private FireVehicle? Escort(IReadOnlyList<FireVehicle> vehicles, FireAircraftState aircraft)
    {
        if (_escort is { } chosen && vehicles.Contains(chosen) && chosen.Phase is not (FireVehiclePhase.Returning or FireVehiclePhase.Gone))
        {
            return chosen;
        }

        var first = vehicles.FirstOrDefault(v => v.Unit is { Kind: FireVehicleKind.Flf, Number: 1 }) ?? vehicles.FirstOrDefault();
        double DistanceOf(FireVehicle v) => FirePlanner.Distance(v.EastM, v.NorthM, aircraft.EastM, aircraft.NorthM);
        var nearest = vehicles
            .Where(v => v.Unit.Kind == FireVehicleKind.Flf && v.Phase is FireVehiclePhase.Following or FireVehiclePhase.Positioning or FireVehiclePhase.AtAircraft)
            .MinBy(DistanceOf);
        if (first is null || nearest is null || ReferenceEquals(first, nearest))
        {
            return first;
        }

        var firstWithAircraft = first.Phase is FireVehiclePhase.Following or FireVehiclePhase.Positioning or FireVehiclePhase.AtAircraft;
        return firstWithAircraft && DistanceOf(first) <= DistanceOf(nearest) + EscortPreferFirstM ? first : nearest;
    }

    /// <summary>
    /// FLF am Bahnende (Rolle <see cref="FireStandbyRole.End"/>): ab dem Aufsetzen über die Bahn dem Flugzeug entgegen, statt am
    /// Haltepunkt auf den Stopp zu warten (Nachtest EDDK 05.11., Florian: früher losfahren, wenn das Flugzeug landet, und über die Bahn
    /// statt über die Straße). Der Weg führt über das Netz – die Bahn des Notfalls wie ein Rollweg – auf die Mittellinie beim Flugzeug;
    /// wie weit es fahren darf, regelt <see cref="HoldBeforeAircraft"/>. Auch von unterwegs (war noch nicht am Bahnende).
    /// </summary>
    private bool StartApproach(FireVehicle vehicle, FireAircraftState aircraft)
    {
        if (Level == FireAlertLevel.GroundIncident || vehicle.Unit.Role != FireStandbyRole.End || Trail.Count < 2
            || ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM).X <= 0 || EmergencyRunway is not { } runway || !_approachTried.Add(vehicle))
        {
            return false;
        }

        // Ziel: die Mittellinie an der erwarteten Stoppstelle, nicht dort, wo das Flugzeug gerade ist (Nachtest .20 EDDK 07.11.: FLF 3 war
        // beim Aufsetzen noch unterwegs, bekam die Aufsetzstelle als Ziel und fuhr 2 km vom Flugzeug weg nach Nordwesten)
        var speed = Math.Max(0, aircraft.GroundSpeedMps);
        var rollout = speed * speed / (2 * ExpectedDecelMps2);
        var heading = aircraft.HeadingDeg * Math.PI / 180;
        var stopAlong = Graph.OffsetsTo(runway, aircraft.EastM + (rollout * Math.Sin(heading)), aircraft.NorthM + (rollout * Math.Cos(heading))).AlongM;
        var half = Math.Max(0, (runway.LengthM / 2) - 50);
        var (te, tn) = Graph.OnCenterline(runway, Math.Clamp(stopAlong, -half, half));
        if (WithFreeRunway(() => FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, te, tn)) is not { Count: >= 2 } points)
        {
            return false;
        }

        _approaching.Add(vehicle);
        vehicle.Drive(FirePlanner.BuildPath(points, FireProfile.MaxMps), FireVehiclePhase.Responding, FireVehiclePhase.Standby, FireProfile.MaxMps);
        var name = runway.Secondary.Length > 0 ? runway.Primary + "/" + runway.Secondary : runway.Primary;
        _log?.LogInformation(Loc.Log("log.fire.approach"), vehicle.Unit.Name, name);
        return true;
    }

    /// <summary>
    /// Grenze für das entgegenkommende FLF, solange das Flugzeug rollt: nicht in die Zone vor ihm – bis zu seiner vorausgerechneten
    /// Stoppstelle (<see cref="ApproachDecelMps2"/>) plus <see cref="ApproachClearM"/>, quer halbe Bahnbreite plus
    /// <see cref="ApproachSideM"/>. Mit dem Bremsen des Flugzeugs rückt die Grenze nach, steht es, fährt das FLF an seinen Platz.
    /// </summary>
    private void HoldBeforeAircraft(FireVehicle vehicle, FireAircraftState aircraft)
    {
        var speed = Math.Max(0, aircraft.GroundSpeedMps);
        var ahead = (speed * speed / (2 * ApproachDecelMps2)) + ApproachClearM;
        var side = (Math.Max(EmergencyRunway?.WidthM ?? 45, 20) / 2) + ApproachSideM;
        var limit = vehicle.FirstOnPath((east, north) =>
        {
            var (x, y) = ToAircraft(aircraft, east, north);
            return x > -20 && x < ahead && Math.Abs(y) < side;
        });
        vehicle.HoldAtS = limit is { } s ? Math.Max(0, s - 5) : null;
    }

    /// <summary>Durchgestartet: zurück an den Haltepunkt (von der Bahn herunter), dort wieder bereit.</summary>
    private void Retreat(FireVehicle vehicle)
    {
        if (Plan.Vehicles.FirstOrDefault(v => v.Unit == vehicle.Unit)?.Hold is { } hold
            && FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, hold.EastM, hold.NorthM) is { Count: >= 2 } points)
        {
            vehicle.Drive(FirePlanner.BuildPath(points, FireProfile.MaxMps), FireVehiclePhase.Responding, FireVehiclePhase.Standby, FireProfile.MaxMps);
        }
    }

    /// <summary>Wegsuche, bei der nach der Landung die Bahn des Notfalls wie ein Rollweg zählt (<see cref="TaxiGraph.FreeRunway"/>).</summary>
    private T WithFreeRunway<T>(Func<T> route)
    {
        var before = Graph.FreeRunway;
        Graph.FreeRunway = _landed && !Returning ? EmergencyRunway : null;
        try
        {
            return route();
        }
        finally
        {
            Graph.FreeRunway = before;
        }
    }

    /// <summary>Das Flugzeug ist am Haltepunkt des Fahrzeugs schon vorbei (ohne Haltepunkt: nein).</summary>
    private bool HoldPassed(FireVehicle vehicle, FireAircraftState aircraft) =>
        Plan.Vehicles.FirstOrDefault(v => v.Unit == vehicle.Unit)?.Hold is { } hold && ToAircraft(aircraft, hold.EastM, hold.NorthM).X < -20;

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by) =>
        TaxiGraph.SegmentDistance(px, py, ax, ay, bx, by);
}
