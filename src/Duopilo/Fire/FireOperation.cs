using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Lage des eigenen Flugzeugs für den Einsatz (Meter östlich/nördlich wie im Rollweg-Netz).</summary>
public sealed record FireAircraftState(double EastM, double NorthM, double HeadingDeg, double GroundSpeedMps, bool OnGround);

/// <summary>
/// Ein Einsatz ohne Sim: Spur des Flugzeugs ab dem Aufsetzen, und was jedes Fahrzeug als Nächstes tut.
/// <list type="bullet">
/// <item>Am Haltepunkt: Ist das Flugzeug vorbei und langsamer als 40 kt, fährt das Fahrzeug auf die Spur dahinter und folgt.</item>
/// <item>Bleibt das Flugzeug stehen, bevor es ein Fahrzeug passiert hat, fährt das Fahrzeug ihm entgegen.</item>
/// <item>Steht das Flugzeug, stellen sich die Fahrzeuge auf ihre Plätze rundherum (nie seitlich in Achsrichtung der Räder).</item>
/// <item>Rollt es weiter, folgt nur FLF 1 bis zum Stand, die anderen fahren zurück. Steht es dort eine Minute, rücken alle ein.</item>
/// </list>
/// <see cref="Update"/> läuft im Takt des Dienstes; die Fahrzeuge rechnen ihre Bewegung selbst auf dem SimConnect-Thread.
/// </summary>
public sealed class FireOperation
{
    /// <summary>Langsamer als das (40 kt) fahren die Fahrzeuge auf die Bahn hinter das Flugzeug.</summary>
    public const double JoinBelowMps = 20.6;

    /// <summary>So lange muss das Flugzeug auf der Bahn stehen, bis die Fahrzeuge es umstellen.</summary>
    public const double StopOnRunwaySeconds = 6;

    /// <summary>Abseits der Bahn so lange (vor der ersten Kontrolle) – kurze Halte an Kreuzungen lösen nichts aus.</summary>
    public const double StopOffRunwaySeconds = 20;

    /// <summary>Steht das Flugzeug nach der Kontrolle so lange abseits der Bahn (am Stand), rücken alle ein.</summary>
    public const double FinishedSeconds = 60;

    /// <summary>Notfall am Boden: so lange bleiben die Fahrzeuge am stehenden Flugzeug.</summary>
    public const double GroundIncidentSeconds = 180;

    /// <summary>So weit vom Flugzeug weg fährt ein Fahrzeug, das noch zur Bahn unterwegs ist, ihm direkt entgegen.</summary>
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

    /// <summary>So weit vor einem Gebäude hält ein Fahrzeug, wenn sein gerader Weg zum Platz hineinführen würde.</summary>
    public const double BuildingClearM = 5;

    public FireOperation(TaxiGraph graph, FireAlertLevel level, FireDispatchPlan plan, TrafficWake wake, int category)
    {
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

    /// <summary>Gespawntes Fahrzeug übernehmen.</summary>
    public void Add(FireVehicle vehicle)
    {
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

        if (Returning || !_landed)
        {
            return;
        }

        var onRunway = Graph.RunwayAt(aircraft.EastM, aircraft.NorthM) is not null;
        UpdateGaps(vehicles, onRunway);
        var escort = Escort(vehicles);
        var stopped = _stoppedSeconds >= (onRunway ? StopOnRunwaySeconds : StopOffRunwaySeconds);
        var moving = aircraft.GroundSpeedMps > 2;

        foreach (var vehicle in vehicles)
        {
            switch (vehicle.Phase)
            {
                case FireVehiclePhase.Responding:
                case FireVehiclePhase.Standby:
                    UpdateWaiting(vehicle, aircraft, aircraftS, stopped);
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
        // Flugzeug nie vorbeikam – Test EDDL 02.11.), rückt gleich ein
        if (_offRunwaySeconds > 10 && !_stoppedOnRunway)
        {
            _released = true;
        }

        if (_released)
        {
            foreach (var vehicle in vehicles.Where(v => v.Phase == FireVehiclePhase.Standby && !ReferenceEquals(v, escort)))
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
    }

    /// <summary>
    /// Platz eines Fahrzeugs am Flugzeug im Flugzeug-System (x nach vorn, y nach rechts, Meter), aus dem Grundmaß <paramref name="u"/>:
    /// FLF vor links und hinter rechts bzw. links am Hauptfahrwerk, ELW schräg vor der Nase, die anderen weiter weg.
    /// </summary>
    public static (double X, double Y) Post(FireUnit unit, double u) => unit.Kind switch
    {
        FireVehicleKind.Flf => unit.Number switch
        {
            1 => (u, -u),
            2 => (-u, u),
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
            _ => (-1.4 * u, -0.6 * u),
        },
        FireVehicleKind.Elw => (-2.1 * u, 0),
        FireVehicleKind.Hlf => (-0.9 * u, 1.6 * u),
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
    private void UpdateWaiting(FireVehicle vehicle, FireAircraftState aircraft, double aircraftS, bool stopped)
    {
        if (Trail.Count < 2 && Level != FireAlertLevel.GroundIncident)
        {
            return;
        }

        // Vorbei = das Fahrzeug liegt hinter dem Flugzeug (auch nach einer Landung auf einer anderen Bahn)
        var distanceToAircraft = FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, aircraft.EastM, aircraft.NorthM);
        var passed = ToAircraft(aircraft, vehicle.EastM, vehicle.NorthM).X < -20;

        if (stopped && !_inspected && !passed && (vehicle.Phase == FireVehiclePhase.Standby || distanceToAircraft < DirectRangeM))
        {
            Position(vehicle, aircraft);
            return;
        }

        if (vehicle.Phase == FireVehiclePhase.Standby && passed && aircraft.GroundSpeedMps < JoinBelowMps)
        {
            if (_released && !ReferenceEquals(vehicle, Escort(Vehicles)))
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
            points = FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, je, jn);
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
        var local = DetourPoints(from, post, u, atStand).Select(p => ToLocal(aircraft, p.X, p.Y)).ToList();
        // Geradeaus (auch über Gras, wie echte Löschfahrzeuge), nur von weit weg und nicht von derselben Bahn über die Rollwege
        var sameRunway = Graph.RunwayAt(vehicle.EastM, vehicle.NorthM) is { } on && ReferenceEquals(on, Graph.RunwayAt(aircraft.EastM, aircraft.NorthM));
        var far = !sameRunway && FirePlanner.Distance(vehicle.EastM, vehicle.NorthM, aircraft.EastM, aircraft.NorthM) > PositionRouteM;
        var offNetwork = 0;
        if (far && FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, local[^1].East, local[^1].North) is { } route)
        {
            // Von weit weg (z. B. vom Haltepunkt am Bahnende) über die Rollwege, die letzten Meter wie geplant
            route.RemoveAt(route.Count - 1);
            local = [.. route, .. local.Skip(Math.Max(1, local.Count - 2))];
            offNetwork = route.Count - 1;
        }

        // Führt das gerade Stück abseits des Netzes durch ein Gebäude (Terminal, Halle, Fluggastbrücke), hält das Fahrzeug davor
        var straight = local.Skip(offNetwork).ToList();
        if (Graph.FirstBuildingHitM(straight) is { } hit)
        {
            var before = FirePlanner.CutAt(straight, Math.Max(0, hit - BuildingClearM));
            local = [.. local.Take(offNetwork), .. before];
            if (local.Count < 2 || FirePlanner.Distance(local[0].East, local[0].North, local[^1].East, local[^1].North) < 1)
            {
                // Schon am Gebäude: stehen bleiben, nur zum Flugzeug drehen
                var (dx, dy) = (aircraft.EastM - vehicle.EastM, aircraft.NorthM - vehicle.NorthM);
                var length = Math.Max(0.1, Math.Sqrt((dx * dx) + (dy * dy)));
                local = [(vehicle.EastM, vehicle.NorthM), (vehicle.EastM + (dx / length * 0.5), vehicle.NorthM + (dy / length * 0.5))];
            }
        }

        var (pe, pn) = local[^1];
        var heading = TaxiPath.Heading(aircraft.EastM - pe, aircraft.NorthM - pn);
        var path = FirePlanner.BuildPath(local, FireProfile.MaxMps);
        vehicle.Drive(path, FireVehiclePhase.Positioning, FireVehiclePhase.AtAircraft, path.LengthM > 200 ? FireProfile.MaxMps : FireProfile.PositionMps, heading);
    }

    /// <summary>
    /// Zurück zur eigenen Wache über die Rollwege (ohne Blaulicht), dort weg: zur Parkposition selbst bzw. zur OSM-Wache. Ohne Route
    /// verschwindet das Fahrzeug an Ort und Stelle – geradeaus weiterzufahren hieße am Haltepunkt: auf die Bahn (Test EDDL 02.11.).
    /// </summary>
    private void SendBack(FireVehicle vehicle)
    {
        var station = Plan.Vehicles.FirstOrDefault(v => v.Unit == vehicle.Unit)?.Station;
        var points = station?.Parking is { } parking
            ? FirePlanner.RouteToNode(Graph, vehicle.EastM, vehicle.NorthM, parking)
            : null;
        points ??= FirePlanner.RouteBetween(Graph, vehicle.EastM, vehicle.NorthM, vehicle.HomeEastM, vehicle.HomeNorthM);
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

    /// <summary>Platz in der Kolonne: Reihenfolge der Alarmierung unter den folgenden Fahrzeugen.</summary>
    private int ColumnIndex(FireVehicle vehicle)
    {
        var column = Vehicles.Where(v => ReferenceEquals(v, vehicle) || v.Phase is FireVehiclePhase.Following or FireVehiclePhase.Joining).ToList();
        return Math.Max(0, column.IndexOf(vehicle));
    }

    /// <summary>Das Fahrzeug, das bis zum Stand folgt: FLF 1, sonst das erste.</summary>
    private static FireVehicle? Escort(IReadOnlyList<FireVehicle> vehicles) =>
        vehicles.FirstOrDefault(v => v.Unit is { Kind: FireVehicleKind.Flf, Number: 1 }) ?? vehicles.FirstOrDefault();

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by) =>
        TaxiGraph.SegmentDistance(px, py, ax, ay, bx, by);
}
