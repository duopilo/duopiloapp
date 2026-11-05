using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Traffic;
using Microsoft.Extensions.Options;

namespace Duopilo.Fire;

/// <summary>Ein Fahrzeug im Status von fire_service.</summary>
/// <param name="RemainingM">Restweg bis zum Ziel des aktuellen Wegs (Haltepunkt, Platz am Flugzeug, Wache), beim Folgen und Stehen null.</param>
/// <param name="SimSpeedKt">GROUND VELOCITY des Objekts laut Sim (für die Raddrehung im Modell), null = noch keine Abfrage.</param>
/// <param name="HoldingShort">Steht vor dieser Bahn und wartet auf die Freigabe (z. B. "06/24"), sonst null.</param>
public sealed record FireVehicleInfo(string Unit, string Model, string Phase, uint? ObjectId, string? Hold, double? AlongM, double? SpeedKt, double? SimSpeedKt, double? RemainingM, double? GapM, double? TargetGapM, double Latitude, double Longitude, string? HoldingShort = null);

/// <summary>Stand der Feuerwehr für fire_service und das Log.</summary>
/// <param name="State">idle, armed, planning, dispatching, responding, standby, following, at_aircraft, returning, finished, unavailable.</param>
/// <param name="Source">atc (Mayday/Pan Pan im Funk) oder tool (fire_service alert).</param>
/// <param name="Spawn">Wache(n): Namen der OSM-Feuerwachen bzw. der Parkposition.</param>
/// <param name="Target">Wem die Feuerwehr folgt: own (eigenes Flugzeug) oder das Kennzeichen eines Verkehrstests (DUO01 …).</param>
public sealed record FireInfo(
    string State,
    string? Source,
    string? Level,
    string? Airport,
    string? Runway,
    int? Category,
    string? Spawn,
    IReadOnlyList<FireVehicleInfo> Vehicles,
    IReadOnlyList<string> Missing,
    string? Error,
    string? Target = null);

/// <summary>
/// Flughafenfeuerwehr (docs/research/feuerwehr-einsaetze.md): Bei Mayday oder Pan Pan im ATC-Modul (oder zum Testen über
/// fire_service) rückt sie aus, sobald das Flugzeug in der Nähe des Landeflughafens ist. Die Fahrzeuge (Fahrzeugpaket von Duopilo im
/// Community-Ordner) spawnen auf einer freien Parkposition nahe der Bahnmitte, fahren mit Blaulicht zu den Haltepunkten der Landebahn,
/// folgen nach dem Aufsetzen der Spur des Flugzeugs, stellen sich um das stehende Flugzeug und rücken danach wieder ein. Läuft nur in
/// der Tray-Instanz (in der stdio-Instanz ist es aus).
/// </summary>
public sealed class FireService : BackgroundService
{
    private const double MetersPerSecondPerKnot = 0.514444;
    private const double ObstacleMarginM = 4000;
    private const double MaxGroundCorrectionFt = 60;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AtcInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpawnInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxRun = TimeSpan.FromMinutes(40);
    private static readonly TimeSpan MaxArmed = TimeSpan.FromHours(3);

    /// <summary>So lange wartet die Planung auf die Wachen aus OSM; danach eine freie Parkposition, die Abfrage läuft weiter.</summary>
    private static readonly TimeSpan OsmWait = TimeSpan.FromSeconds(8);

    /// <summary>Höchstens so viele Parkpositionen bzw. Rollweg-Meter dürfen in OSM-Gebäuden liegen, sonst passt OSM nicht zur Szenerie.</summary>
    private const double MaxParkingInBuildings = 0.08;

    private const double MaxTaxiInBuildings = 0.02;

    /// <summary>Hat die Szenerie weniger Fahrweg-Abschnitte, kommen die Betriebsstraßen aus OSM dazu.</summary>
    private const int MinSceneryRoads = 20;

    /// <summary>Höchstens so viel der OSM-Straßenmeter darf mitten auf Parkpositionen liegen, sonst passen sie nicht zur Szenerie.</summary>
    private const double MaxRoadsOnParking = 0.08;

    /// <summary>Im Umkreis des Einsatzorts sind geparkte Flugzeuge kein Hindernis (Meter).</summary>
    private const double SceneClearM = 60;
    private static readonly TimeSpan KeepFailed = TimeSpan.FromSeconds(60);

    private readonly DuopiloOptions _options;
    private readonly SimConnectClient _sim;
    private readonly TrafficConnection _traffic;
    private readonly SimBriefService _simBrief;
    private readonly IServiceProvider _services;
    private readonly ILogger<FireService> _log;
    private readonly object _lock = new();

    private readonly Lazy<FireStationLookup> _stations;

    private Run? _run;
    private FireInfo? _last;
    private DateTimeOffset? _handledEmergency;
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;
    private (double Lat, double Lon, DateTimeOffset Utc)? _fix;

    public FireService(IOptions<DuopiloOptions> options, SimConnectClient sim, TrafficConnection traffic, SimBriefService simBrief, IServiceProvider services, ILogger<FireService> log)
    {
        _options = options.Value;
        _sim = sim;
        _traffic = traffic;
        _simBrief = simBrief;
        _services = services;
        _log = log;
        _stations = new Lazy<FireStationLookup>(() => new FireStationLookup(_options.Fire.OverpassUrl, _options.ResolveDataDirectory(), log));
    }

    /// <summary>Feuerwehr gibt es in dieser Instanz.</summary>
    public bool Available => _options.Fire.Enabled;

    /// <summary>Aktueller Stand (fire_service status).</summary>
    public FireInfo Status()
    {
        lock (_lock)
        {
            return _run is { } run ? Info(run) : _last ?? Idle();
        }
    }

    /// <summary>Alarm ohne Funk (fire_service alert) – zum Testen an jedem Flughafen. Ohne Flughafen: der nähere des SimBrief-Plans.</summary>
    /// <param name="tail">Statt des eigenen Flugzeugs einem KI-Flugzeug eines Verkehrstests folgen (DUO01 …); Flughafen und Bahn kommen
    /// dann vom Test, wenn nicht angegeben.</param>
    /// <param name="parking">Notfall am Boden an dieser Parkposition statt an einem Flugzeug (z. B. V61A).</param>
    public FireInfo Alert(string? level, string? airport, string? runway, string? tail = null, string? parking = null)
    {
        if (!Available)
        {
            throw new InvalidOperationException(Loc.T("tools.fire.disabled"));
        }

        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var place = string.IsNullOrWhiteSpace(parking) ? null : parking.Trim().ToUpperInvariant();
        if (place is not null && !string.IsNullOrWhiteSpace(tail))
        {
            throw new InvalidOperationException(Loc.T("tools.fire.parkingAndTail"));
        }

        // An einer Parkposition gibt es keine Landung: immer ein Notfall am Boden
        var alertLevel = place is not null
            ? FireAlertLevel.GroundIncident
            : ParseLevel(level) ?? throw new InvalidOperationException(Loc.T("tools.fire.levelUnknown", level ?? ""));
        lock (_lock)
        {
            if (_run is { Failed: false })
            {
                throw new InvalidOperationException(Loc.T("tools.fire.busy"));
            }

            TrafficTestStatus? test = null;
            if (!string.IsNullOrWhiteSpace(tail))
            {
                test = _services.GetService<TrafficTestService>()?.Status()
                    .LastOrDefault(t => t.State == "running" && string.Equals(t.Tail, tail.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(Loc.T("tools.fire.tailUnknown", tail.Trim()));
            }

            var icao = !string.IsNullOrWhiteSpace(airport) ? airport.Trim().ToUpperInvariant() : test?.Airport ?? NearestPlanAirport();
            if (icao is null)
            {
                throw new InvalidOperationException(Loc.T("tools.fire.noAirport"));
            }

            var runwayName = !string.IsNullOrWhiteSpace(runway) ? runway.Trim() : test?.Runway;
            var run = new Run("tool", alertLevel, icao, runwayName, test?.Type ?? _simBrief.Current?.AircraftIcao, DateTimeOffset.UtcNow, null)
            {
                TargetTail = test?.Tail,
                TargetParking = place,
            };
            StartLocked(run, dispatchNow: true);
            return Info(run);
        }
    }

    /// <summary>Abbrechen (fire_service cancel): die Fahrzeuge rücken ein.</summary>
    public FireInfo Cancel()
    {
        lock (_lock)
        {
            if (_run is not { } run)
            {
                throw new InvalidOperationException(Loc.T("tools.fire.nothing"));
            }

            StandDownLocked(run, "cancelled");
            return _run is { } current ? Info(current) : _last ?? Idle();
        }
    }

    /// <summary>
    /// Sofort aufräumen (fire_service reset): alle Fahrzeuge verschwinden ohne Rückfahrt, auch in der Pause – zum Neustarten eines
    /// Tests. Entfernt werden sie im nächsten Takt.
    /// </summary>
    public FireInfo Reset()
    {
        lock (_lock)
        {
            if (_run is not { } run)
            {
                throw new InvalidOperationException(Loc.T("tools.fire.nothing"));
            }

            _log.LogInformation(Loc.Log("log.fire.standDown"), "reset");
            run.Cts.Cancel();
            run.SpawnDone = true;
            run.Operation?.ReturnAll();
            foreach (var vehicle in run.Objects.Keys)
            {
                vehicle.Remove();
            }

            if (run.Objects.Count == 0)
            {
                _last = Info(run) with { State = "finished" };
                _run = null;
                return _last;
            }

            return Info(run);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextAtc = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (!_sim.Connection.IsConnected)
                    {
                        Drop();
                        continue;
                    }

                    if (Available && _options.Fire.OnAtcEmergency && DateTimeOffset.UtcNow >= nextAtc)
                    {
                        nextAtc = DateTimeOffset.UtcNow + AtcInterval;
                        FollowAtc();
                    }

                    await UpdateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, Loc.Log("log.fire.updateFailed"), ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }

        Run? last;
        lock (_lock)
        {
            last = _run;
            _run = null;
        }

        if (last is not null)
        {
            last.Cts.Cancel();
            foreach (var (_, id) in last.Objects)
            {
                await RemoveObjectAsync(id, CancellationToken.None);
            }
        }
    }

    // ------------------------------------------------------------------ ATC

    /// <summary>Mayday oder Pan Pan im ATC-Modul: Feuerwehr vormerken; abgebrochen ("cancel Mayday"): einrücken.</summary>
    private void FollowAtc()
    {
        if (_services.GetService<AtcService>() is not { } atc)
        {
            return;
        }

        var order = atc.WithSession<AtcFireOrder?>((session, input) =>
        {
            if (session?.Emergency is not { } emergency)
            {
                return null;
            }

            var ofp = session.Plan.Ofp;
            var runway = session.Overrides.GetValueOrDefault("arrivalRunway") ?? session.Plan.ArrivalRunway ?? input.AssignedRunway;
            return new AtcFireOrder(emergency.Kind, emergency.SinceUtc, emergency.Target, runway, ofp.Destination.Icao, ofp.AircraftIcao);
        });

        lock (_lock)
        {
            var run = _run;

            // Alter Notfall (z. B. aus einer wiederhergestellten ATC-Sitzung von gestern): nicht mehr ausrücken
            if (order is not null && order.SinceUtc != _handledEmergency && DateTimeOffset.UtcNow - order.SinceUtc > MaxArmed)
            {
                _handledEmergency = order.SinceUtc;
                _log.LogInformation(Loc.Log("log.fire.staleEmergency"), order.Kind, order.SinceUtc, MaxArmed.TotalHours);
                return;
            }

            if (order is not null && order.SinceUtc != _handledEmergency && (run is null || run.Failed))
            {
                _handledEmergency = order.SinceUtc;
                var level = order.Kind == AtcEmergencyKind.Mayday ? FireAlertLevel.FullEmergency : FireAlertLevel.LocalStandby;
                if (_sim.Bool("onGround") == true)
                {
                    level = FireAlertLevel.GroundIncident;
                }

                // Ziel des Notfalls, wenn es ein Flughafen des Plans ist; sonst entscheidet die Nähe beim Ausrücken
                var airport = PlanAirports().FirstOrDefault(a => string.Equals(a.Icao, order.Target, StringComparison.OrdinalIgnoreCase))?.Icao;
                // Die Bahn aus dem Plan gilt nur am Zielflughafen; am Boden braucht es keine
                var runway = level != FireAlertLevel.GroundIncident && (airport is null || string.Equals(airport, order.Destination, StringComparison.OrdinalIgnoreCase))
                    ? order.Runway
                    : null;
                var next = new Run("atc", level, airport, runway, order.AircraftIcao, DateTimeOffset.UtcNow, order.SinceUtc) { RunwayAirport = runway is null ? null : order.Destination };
                StartLocked(next, dispatchNow: false);
                return;
            }

            if (run is { Source: "atc", Failed: false } && (order is null || order.SinceUtc != run.EmergencySince))
            {
                StandDownLocked(run, "atc_cancelled");
            }
        }
    }

    private sealed record AtcFireOrder(AtcEmergencyKind Kind, DateTimeOffset SinceUtc, string? Target, string? Runway, string? Destination, string? AircraftIcao);

    // ------------------------------------------------------------------ Ablauf

    private void StartLocked(Run run, bool dispatchNow)
    {
        _run = run;
        _last = null;
        _log.LogInformation(Loc.Log("log.fire.alerted"), run.Source, LevelName(run.Level), run.Airport ?? "-", run.Runway ?? "-");
        Prefetch(run);
        if (dispatchNow)
        {
            Dispatch(run);
        }
    }

    /// <summary>Wachen aus OSM schon beim Alarm abfragen (Flughafen des Einsatzes, sonst alle des Plans), damit sie beim Ausrücken da sind.</summary>
    private void Prefetch(Run run)
    {
        if (!_options.Fire.OsmStations)
        {
            return;
        }

        List<string> airports = run.Airport is { } icao ? [icao] : [.. PlanAirports().Select(a => a.Icao)];
        if (run.Airport is null && NearestPlanAirport() is { } nearest && !airports.Contains(nearest, StringComparer.OrdinalIgnoreCase))
        {
            airports.Insert(0, nearest);
        }

        foreach (var airport in airports)
        {
            _ = _stations.Value.GetAsync(airport);
        }
    }

    private void Dispatch(Run run)
    {
        run.Dispatched = true;
        _ = Task.Run(() => PrepareAsync(run, run.Cts.Token));
    }

    /// <summary>Planen und die Fahrzeuge nacheinander spawnen. Geht es nicht, ist die Feuerwehr „nicht verfügbar“.</summary>
    private async Task PrepareAsync(Run run, CancellationToken ct)
    {
        try
        {
            var airport = run.Airport ?? throw new FireUnavailableException(Loc.T("tools.fire.noAirport"));
            var runways = await _sim.RequestAirportRunwaysAsync(airport, ct);
            var data = await _sim.RequestAirportTaxiwaysAsync(airport, ct);
            if (data.Points.Count == 0)
            {
                throw new FireUnavailableException(Loc.T("tools.fire.noTaxiways", airport));
            }

            // Mit den Fahrwegen der Bodenfahrzeuge: von der Wache über die Betriebsstraßen auf die Rollwege
            var graph = TaxiGraph.Build(data, runways, vehicleRoads: true);
            graph.RoadCostFactor = FireProfile.RoadCostFactor;
            _log.LogInformation(
                Loc.Log("log.fire.network"),
                airport,
                graph.Nodes.Count,
                graph.VehicleRoads,
                string.Join(", ", graph.PathTypes.Select(t => TaxiGraph.PathTypeName(t.Key) + " " + t.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            await SetObstaclesAsync(graph, ct);
            await LogGroundVehiclesAsync(graph, airport, ct);
            var wake = TrafficRules.WakeOf(run.AircraftIcao, TrafficCategory.Airliner);
            var category = FireCategory.Of(run.AircraftIcao, wake);
            var units = FireCategory.Lineup(run.Level, category);
            var own = OwnLocal(graph);
            var onGround = _sim.Bool("onGround") == true;

            // Echte Wachen aus OpenStreetMap; ohne (oder ohne Weg von ihnen) eine freie Parkposition
            IReadOnlyList<FireStation> stations = [];
            if (_options.Fire.OsmStations)
            {
                var fetch = _stations.Value.GetAsync(airport);
                if (await Task.WhenAny(fetch, Task.Delay(OsmWait, ct)) == fetch)
                {
                    var osm = await fetch;
                    UseBuildings(graph, airport, osm.Buildings);
                    UseRoads(graph, airport, osm);
                    stations = FireStationLookup.ToStations(osm.Stations, graph, osm.Buildings);

                    // Militärische Wachen fahren keine zivilen Einsätze (EDDK: Bundeswehr im militärischen Teil), auf einem reinen
                    // Militärflugplatz bleiben sie
                    stations = FireStationLookup.WithoutMilitary(stations, out var military);
                    if (military.Count > 0)
                    {
                        _log.LogInformation(Loc.Log("log.fire.militaryStations"), airport, string.Join(", ", military.Select(st => st.Name)));
                    }
                    _log.LogInformation(
                        Loc.Log("log.fire.stations"),
                        airport,
                        stations.Count == 0 ? "-" : string.Join(", ", stations.Select(st => st.Name + " (" + st.ExitSource + ")")),
                        graph.Buildings);
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    _log.LogInformation(Loc.Log("log.fire.osmPending"), airport, OsmWait.TotalSeconds);
                }
            }

            FireDispatchPlan? plan;
            FireAircraftState? place = null;
            if (run.Level == FireAlertLevel.GroundIncident)
            {
                (double East, double North) at;
                TaxiNode? stand = null;
                if (run.TargetParking is { } label)
                {
                    // Einsatzort Parkposition: die Fahrzeuge stellen sich auf, als stünde dort ein Flugzeug (Nase in Richtung der Position)
                    var node = graph.FindParking(label) ?? throw new FireUnavailableException(Loc.T("tools.fire.parkingUnknown", label, airport));
                    place = new FireAircraftState(node.EastM, node.NorthM, node.Parking?.HeadingDeg ?? 0, 0, true);
                    at = (node.EastM, node.NorthM);
                    stand = node;
                }
                else if (!onGround || own is null)
                {
                    throw new FireUnavailableException(Loc.T("tools.fire.notOnGround"));
                }
                else
                {
                    at = own.Value;

                    // Steht das eigene Flugzeug auf einer Parkposition (Gate), dieselbe Anfahrt und Aufstellung wie dort
                    stand = graph.Nodes
                        .Where(n => n.Parking is { } p && FirePlanner.Distance(n.EastM, n.NorthM, at.East, at.North) <= Math.Max(8, p.RadiusM * 0.6))
                        .MinBy(n => FirePlanner.Distance(n.EastM, n.NorthM, at.East, at.North));
                }

                // Am Einsatzort ist das Flugzeug (und was daneben parkt) das Ziel, kein Hindernis (EDDL V81 zwischen V80 und V82)
                graph.ClearObstacles(at.East, at.North, SceneClearM);
                var target = at;
                plan = PlanAvoidingObstacles(graph, airport, () => FirePlanner.PlanToAircraft(graph, units, target.East, target.North, FireCategory.PostUnitM(wake), stations, stand))
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.noRouteToAircraft", airport));
            }
            else
            {
                var end = ResolveRunway(airport, runways, run.Runway)
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.runwayUnknown", run.Runway ?? "-", airport));
                plan = PlanAvoidingObstacles(graph, airport, () => FirePlanner.Plan(graph, end, units, wake, onGround ? own : null, stations, allStations: run.Level == FireAlertLevel.FullEmergency))
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.noRouteToRunway", airport, end.Designation));
            }

            var operation = new FireOperation(graph, run.Level, plan, wake, category, _log);
            if (place is { } scene)
            {
                var (sceneLat, sceneLon) = graph.ToLatLon(scene.EastM, scene.NorthM);
                _log.LogInformation(Loc.Log("log.fire.scene"), run.TargetParking, Math.Round(sceneLat, 6), Math.Round(sceneLon, 6), Math.Round(scene.HeadingDeg));
            }
            lock (_lock)
            {
                if (!ReferenceEquals(_run, run) || ct.IsCancellationRequested)
                {
                    return;
                }

                run.Graph = graph;
                run.Operation = operation;
                run.TargetPlace = place;
                run.Category = category;
                run.Runway = plan.Runway?.Designation ?? run.Runway;
            }

            _log.LogInformation(
                Loc.Log("log.fire.planned"),
                string.Join(", ", plan.Stations.Select(st => st.Name + " (" + st.Source + ")")),
                plan.Runway?.Designation ?? "-",
                category,
                string.Join(", ", plan.Vehicles.Select(v => v.Unit.Name + "@" + (v.Hold is null ? "aircraft" : v.AlongM.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))),
                plan.Missing.Count);

            // Boden: unter dem eigenen Flugzeug gemessen (am Boden, nur in der Nähe der Wache), sonst die Höhe des Flughafens; die Abfrage
            // je Fahrzeug korrigiert
            double? ownGroundFt = onGround && _sim.Num("altTrue") is { } alt && _sim.Num("altAgl") is { } agl ? alt - agl : null;
            var height = _options.Fire.HeightFt;
            var errors = new List<string>();
            for (var i = 0; i < plan.Vehicles.Count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(SpawnInterval, ct);
                }

                var vp = plan.Vehicles[i];
                var model = ModelFor(vp.Unit);
                uint objectId = 0;
                var (startEast, startNorth, heading) = vp.Route.At(0);
                var (spawnLat, spawnLon) = graph.ToLatLon(startEast, startNorth);
                var groundFt = SpawnGroundFt(
                    graph.ElevationFt,
                    ownGroundFt,
                    own is { } ownPos ? FirePlanner.Distance(ownPos.East, ownPos.North, startEast, startNorth) : null);
                Exception? failure = null;

                // Neue, neutrale Titel zuerst; fehlt das umbenannte Paket noch, der alte Titel (und für den Rest der Sitzung nur noch er)
                foreach (var title in Titles(model))
                {
                    try
                    {
                        objectId = await _sim.CreateSimulatedObjectAsync(title, null, new AiInitPosition(spawnLat, spawnLon, groundFt + height, heading, true, 0), ct);
                        if (LegacyTitles.ContainsKey(title))
                        {
                            _missingTitles.Clear();
                        }

                        model = title;
                        failure = null;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure = ex;
                        _log.LogWarning(Loc.Log("log.fire.spawnFailed"), vp.Unit.Name, title, Loc.ToEnglish(ex.Message) ?? ex.Message);
                        if (LegacyTitles.ContainsKey(title))
                        {
                            // Fehlt ein neuer Titel, ist es das alte Paket: alle neuen überspringen (jeder Versuch wartet 10 s)
                            _missingTitles.UnionWith(LegacyTitles.Keys);
                        }
                    }
                }

                if (failure is not null || objectId == 0)
                {
                    errors.Add(vp.Unit.Name + ": " + (failure?.Message ?? "-"));
                    continue;
                }

                var vehicle = new FireVehicle(vp.Unit, model, vp.Route, operation.Trail, graph.ToLatLon, groundFt, height)
                {
                    SirenAllowed = _options.Fire.Siren,
                };
                bool keep;
                lock (_lock)
                {
                    keep = ReferenceEquals(_run, run) && !ct.IsCancellationRequested;
                    if (keep)
                    {
                        run.Objects[vehicle] = objectId;
                        run.Plans[vehicle] = vp;
                        run.BlueSent[vehicle] = true;
                    }
                }

                if (!keep)
                {
                    await RemoveObjectAsync(objectId, CancellationToken.None);
                    return;
                }

                operation.Add(vehicle);
                _sim.StartAiPose(objectId, dt =>
                {
                    vehicle.Advance(dt);
                    return vehicle.Phase == FireVehiclePhase.Gone ? null : vehicle.Pose();
                }, releaseAi: false);
                _sim.StartAiProbe(objectId, sample => OnProbe(groundFt, vehicle, sample));
                _log.LogInformation(Loc.Log("log.fire.spawned"), vp.Unit.Name, model, objectId);
                _ = SwitchLightsAsync(objectId, ct);
            }

            if (errors.Count == plan.Vehicles.Count)
            {
                throw new FireUnavailableException(Loc.T("tools.fire.spawnFailed", string.Join(" | ", errors)));
            }

            lock (_lock)
            {
                run.SpawnDone = true;
                run.SpawnErrors = errors;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                run.SpawnDone = true;
            }
        }
        catch (Exception ex)
        {
            var reason = ex is FireUnavailableException ? ex.Message : Loc.T("tools.fire.failed", ex.Message);
            lock (_lock)
            {
                run.SpawnDone = true;
                run.Error = reason;
                run.FailedUtc = DateTimeOffset.UtcNow;
                run.Operation?.ReturnAll();
            }

            _log.LogWarning(Loc.Log("log.fire.unavailable"), Loc.ToEnglish(reason) ?? reason);
        }
    }

    /// <summary>
    /// Landebahn: die genannte bzw. von ATC zugewiesene; ohne Angabe die, auf deren Anflug das Flugzeug am ehesten zufliegt (Kurs zur
    /// Schwelle am nächsten am Bahnkurs), am Boden die nächste.
    /// </summary>
    private RunwayEnd? ResolveRunway(string airport, IReadOnlyList<AirportRunway> runways, string? designation)
    {
        if (designation is { Length: > 0 } && RunwayEnd.Find(airport, runways, designation) is { } named)
        {
            return named;
        }

        var ends = runways.SelectMany(r => new[] { RunwayEnd.From(airport, r, primary: true), RunwayEnd.From(airport, r, primary: false) })
            .Where(e => e.Designation.Length > 0)
            .ToList();
        if (ends.Count == 0)
        {
            return null;
        }

        if (_sim.Num("lat") is not { } lat || _sim.Num("lon") is not { } lon)
        {
            return ends[0];
        }

        return ends.MinBy(e =>
        {
            var bearing = RouteMath.BearingDeg(lat, lon, e.ThresholdLatitude, e.ThresholdLongitude);
            return Math.Abs(((bearing - e.HeadingTrueDeg + 540) % 360) - 180);
        });
    }

    /// <summary>Andere Flugzeuge am Boden: ihre Parkpositionen sind belegt, dort spawnt keine Wache.</summary>
    /// <summary>
    /// Gebäude aus OSM als Hindernis – nur, wenn sie zur Szenerie dieses Nutzers passen. OSM zeigt die echte Welt, die Szenerie
    /// (Standard oder Zusatz, Ausbaustand) kann anders sein: Liegen mehr als 8 % der Parkpositionen oder 2 % der Rollweg-Meter in einem
    /// Gebäude, bleiben die Gebäude für diesen Einsatz aus (Brücken zählen dabei nicht, sie hängen über den Positionen).
    /// </summary>
    private void UseBuildings(TaxiGraph graph, string airport, IReadOnlyList<OsmShape> shapes)
    {
        graph.SetBuildings(FireStationLookup.ToBuildings([.. shapes.Where(s => s.Closed)], graph));
        var (parkingShare, taxiShare) = graph.BuildingOverlap();
        if (parkingShare > MaxParkingInBuildings || taxiShare > MaxTaxiInBuildings)
        {
            _log.LogInformation(Loc.Log("log.fire.buildingsMismatch"), airport, Math.Round(parkingShare * 100, 1), Math.Round(taxiShare * 100, 1));
            graph.SetBuildings([]);
            return;
        }

        graph.SetBuildings(FireStationLookup.ToBuildings(shapes, graph));
    }

    /// <summary>
    /// Betriebsstraßen aus OSM, wenn die Szenerie (fast) keine Fahrwege mitbringt (Aerosoft EDDL: keine). Nur die Luftseite: Straßen
    /// außerhalb des Flughafen-Umrisses, über Zäune, hinter Schranken und ohne Verbindung zu einem Rollweg fallen weg
    /// (<see cref="TaxiGraph.AddRoads"/>); liegen mehr als 8 % der Straßenmeter mitten auf Parkpositionen, passen sie nicht zur Szenerie.
    /// </summary>
    private void UseRoads(TaxiGraph graph, string airport, OsmAirportData osm)
    {
        if (graph.VehicleRoads >= MinSceneryRoads || osm.Roads.Count == 0)
        {
            return;
        }

        var merge = graph.AddRoads(
            FireStationLookup.ToLines(osm.Roads, graph),
            FireStationLookup.ToPoints(osm.Barriers, graph),
            MaxRoadsOnParking,
            FireStationLookup.ToLines(osm.Fences, graph),
            FireStationLookup.ToLines(osm.Boundary, graph));
        if (!merge.Added)
        {
            _log.LogInformation(Loc.Log("log.fire.osmRoadsMismatch"), airport, Math.Round(merge.ParkingShare * 100, 1));
            return;
        }

        _log.LogInformation(
            Loc.Log("log.fire.osmRoads"),
            airport,
            merge.Sections,
            Math.Round(merge.LengthM / 1000, 1),
            merge.Links,
            merge.Crossings,
            merge.Cut,
            merge.Dropped);
    }

    /// <summary>
    /// Planen mit den geparkten Flugzeugen als Hindernis; geht so kein Weg, ohne sie – lieber knapp an einem Flugzeug vorbei als gar
    /// nicht ausrücken.
    /// </summary>
    private FireDispatchPlan? PlanAvoidingObstacles(TaxiGraph graph, string airport, Func<FireDispatchPlan?> plan)
    {
        if (plan() is { } avoiding)
        {
            return avoiding;
        }

        if (graph.BlockedEdges == 0)
        {
            return null;
        }

        _log.LogInformation(Loc.Log("log.fire.ignoringObstacles"), airport, graph.BlockedEdges);
        graph.ClearObstacles();
        return plan();
    }

    /// <summary>
    /// Bodenfahrzeuge, die SimConnect im Flughafen meldet (Bodendienste, als SimObject platzierte Fahrzeuge), ins Log: Art (ATC MODEL),
    /// Lage im Netz und Tempo, die nächsten 40 zum Bezugspunkt. Erst einmal nur zum Ansehen – danach sollen die stehenden wie geparkte
    /// Flugzeuge als Hindernis zählen. Statische Modelle der Szenerie sieht SimConnect nicht.
    /// </summary>
    private async Task LogGroundVehiclesAsync(TaxiGraph graph, string airport, CancellationToken ct)
    {
        try
        {
            var scan = await _traffic.ScanGroundAsync((uint)Math.Min(ObstacleMarginM, TrafficConnection.MaxScanRadiusMeters), ct);
            if (scan.Error is { } error)
            {
                _log.LogInformation(Loc.Log("log.fire.groundVehiclesFailed"), airport, error);
                return;
            }

            var own = new HashSet<uint>();
            lock (_lock)
            {
                if (_run is { } current)
                {
                    own.UnionWith(current.Objects.Values);
                }
            }

            var vehicles = scan.Samples
                .Where(s => !own.Contains(s.ObjectId))
                .Select(s => (Sample: s, Local: graph.ToLocal(s.Latitude, s.Longitude)))
                .Where(v => Math.Abs(v.Local.EastM) < ObstacleMarginM && Math.Abs(v.Local.NorthM) < ObstacleMarginM)
                .OrderBy(v => Math.Sqrt((v.Local.EastM * v.Local.EastM) + (v.Local.NorthM * v.Local.NorthM)))
                .ToList();
            var parked = vehicles.Count(v => v.Sample.GroundSpeedKt < 0.5);
            var list = string.Join("; ", vehicles.Take(40).Select(v => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{(string.IsNullOrWhiteSpace(v.Sample.AtcModel) ? "?" : v.Sample.AtcModel.Trim())} {v.Local.EastM:0}/{v.Local.NorthM:0} m {v.Sample.GroundSpeedKt:0} kt")));
            _log.LogInformation(Loc.Log("log.fire.groundVehicles"), airport, vehicles.Count, parked, scan.Complete, list.Length == 0 ? "-" : list);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(Loc.Log("log.fire.groundVehiclesFailed"), airport, ex.Message);
        }
    }

    private async Task SetObstaclesAsync(TaxiGraph graph, CancellationToken ct)
    {
        try
        {
            var scan = await _traffic.ScanAsync((uint)Math.Min(ObstacleMarginM, TrafficConnection.MaxScanRadiusMeters), includeHelicopters: true, withText: false, ct);
            var parked = scan.Samples
                .Where(s => s.OnGround)
                .Where(s =>
                {
                    var (e, n) = graph.ToLocal(s.Latitude, s.Longitude);
                    return Math.Abs(e) < ObstacleMarginM && Math.Abs(n) < ObstacleMarginM;
                })
                .Select(s => (s.Latitude, s.Longitude))
                .ToList();
            graph.SetObstacles(parked, TaxiProfile.Airliner.ClearanceM);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, Loc.Log("log.fire.obstaclesFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Blaulicht und Fahrlicht an – während das Objekt lädt, verwirft der Sim die Werte (mit DATA_ERROR), deshalb erst nach
    /// <see cref="FireVehicle.VarsDelayS"/> und mehrmals.
    /// </summary>
    private async Task SwitchLightsAsync(uint objectId, CancellationToken ct)
    {
        foreach (var delay in new[] { 2, 1, 2 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                await SetLightsAsync(objectId, blue: true, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, Loc.Log("log.fire.lightsFailed"), ex.Message);
            }
        }
    }

    private async Task SetLightsAsync(uint objectId, bool blue, CancellationToken ct)
    {
        await _sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_BLAULICHT", "Number", blue ? 1 : 0, ct);
        await _sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_FAHRLICHT", "Number", 1, ct);
        // Geschaltet wird über die L:1-Variablen; die SimVars LIGHT … lehnt der Sim an Bodenfahrzeugen ab (DATA_ERROR), nur die Events
        await _sim.SetAiLightsAsync(objectId, new AiLights(Beacon: blue, Landing: true), ct, simVars: false);
    }

    /// <summary>Mindesthalt vor einer Bahn mit Blaulicht (Anfahrt, auf die Spur, Aufstellen).</summary>
    public const double CrossingHoldBlueS = 2;

    /// <summary>Mindesthalt vor einer Bahn ohne Blaulicht (Rückfahrt), wie beim eigenen KI-Verkehr.</summary>
    public const double CrossingHoldS = 5;

    /// <summary>Mit Blaulicht zählt der Endanflug nur bis 2 NM (rund 45 s); auf der Rückfahrt wie beim KI-Verkehr bis 4 NM.</summary>
    public const double CrossingFinalBlueNm = 2;

    /// <summary>Ohne Antwort aus der Verkehrsabfrage kreuzt ein Fahrzeug nach so vielen Sekunden Halt trotzdem.</summary>
    public const double CrossingGiveUpS = 20;

    /// <summary>So alt darf die Verkehrsabfrage für eine Freigabe höchstens sein.</summary>
    private static readonly TimeSpan CrossingScanMaxAge = TimeSpan.FromSeconds(3);

    /// <summary>So weit um den Flughafen fragt Duopilo den Verkehr ab (dazu der Abstand des eigenen Flugzeugs zum Flughafen).</summary>
    private const double CrossingScanRadiusM = 15000;

    /// <summary>
    /// Halt vor Bahnen (Test EDDK 05.11.: die Fahrzeuge kreuzten ohne Halt). Bahnen ohne Freigabepflicht (<see cref="FireOperation.NeedsClearance"/>)
    /// gibt der Einsatz selbst frei, dort hält niemand. Sonst hält das Fahrzeug an der Haltelinie, nach dem Mindesthalt fragt Duopilo
    /// den Verkehr ab (eine Abfrage zugleich) und gibt die Bahn frei, wenn nichts auf ihr oder im Endanflug ist (<see cref="RunwayOccupancy"/>).
    /// Ohne Antwort aus der Abfrage nach <see cref="CrossingGiveUpS"/> trotzdem.
    /// </summary>
    private void CheckCrossingsLocked(Run run, FireOperation operation, TaxiGraph graph, DateTimeOffset now)
    {
        var needScan = false;
        foreach (var vehicle in run.Objects.Keys)
        {
            if (vehicle.NextCrossing is not { } crossing)
            {
                run.CrossingLogged.Remove(vehicle);
                run.CrossingBlocker.Remove(vehicle);
                continue;
            }

            // Ohne Freigabepflicht gibt der Einsatz selbst frei (FireOperation.Update)
            if (!operation.NeedsClearance(vehicle, crossing))
            {
                continue;
            }

            if (!vehicle.HoldingShort)
            {
                continue;
            }

            var key = crossing.Designation + "@" + crossing.EntryS.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            if (run.CrossingLogged.GetValueOrDefault(vehicle) != key)
            {
                run.CrossingLogged[vehicle] = key;
                run.CrossingBlocker.Remove(vehicle);
                _log.LogInformation(Loc.Log("log.fire.holdingShort"), vehicle.Unit.Name, crossing.Designation);
            }

            needScan = true;
            var held = vehicle.CrossingHeldS;
            if (held < (vehicle.BlueLight ? CrossingHoldBlueS : CrossingHoldS) || run.CrossingScan is not { } scan || now - run.CrossingScanUtc > CrossingScanMaxAge)
            {
                continue;
            }

            if (scan.Error is not null)
            {
                if (held >= CrossingGiveUpS)
                {
                    vehicle.ClearCrossing();
                    _log.LogWarning(Loc.Log("log.fire.crossingUnchecked"), vehicle.Unit.Name, crossing.Designation, Loc.ToEnglish(scan.Error) ?? scan.Error);
                }

                continue;
            }

            // Das Flugzeug des Notfalls zählt nach der Landung nicht: um das stehende oder rollende Flugzeug fährt die Feuerwehr ohnehin
            // (sonst wartete etwa ein FLF auf der Rückfahrt vor der Landebahn, solange das Flugzeug darauf steht)
            var samples = operation.Landed ? scan.Samples.Where(sample => !IsEmergencyAircraft(run, sample)) : scan.Samples;
            var blocker = RunwayOccupancy.Blocker(graph.Icao, crossing.Runway, samples, uint.MaxValue, vehicle.BlueLight ? CrossingFinalBlueNm : RunwayOccupancy.FinalNm);
            if (blocker is null)
            {
                vehicle.ClearCrossing();
                _log.LogInformation(Loc.Log("log.fire.crossing"), vehicle.Unit.Name, crossing.Designation, Math.Round(held));
            }
            else if (run.CrossingBlocker.GetValueOrDefault(vehicle) != RunwayOccupancy.Key(blocker))
            {
                run.CrossingBlocker[vehicle] = RunwayOccupancy.Key(blocker);
                _log.LogInformation(Loc.Log("log.fire.crossingBusy"), vehicle.Unit.Name, crossing.Designation, blocker);
            }
        }

        if (!needScan || run.CrossingScanBusy || now - run.CrossingScanUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        run.CrossingScanBusy = true;
        var radius = CrossingScanRadiusM;
        if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
        {
            radius += RouteMath.DistanceNm(lat, lon, graph.Latitude, graph.Longitude) * RunwayEnd.MetersPerNm;
        }

        _ = Task.Run(async () =>
        {
            AircraftScan scan;
            try
            {
                scan = await _traffic.ScanAsync((uint)Math.Min(radius, TrafficConnection.MaxScanRadiusMeters), includeHelicopters: true, withText: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Jeder Fehler zählt als Fehlschlag der Abfrage, sonst bliebe sie für immer „busy“ und niemand käme über die Bahn
                scan = AircraftScan.Fail(ex.Message);
            }

            lock (_lock)
            {
                run.CrossingScan = scan;
                run.CrossingScanUtc = DateTimeOffset.UtcNow;
                run.CrossingScanBusy = false;
            }
        });
    }

    /// <summary>Das Flugzeug des Einsatzes in der Verkehrsabfrage: das KI-Flugzeug des Verkehrstests (Kennung) bzw. das eigene.</summary>
    private static bool IsEmergencyAircraft(Run run, AircraftSample sample) =>
        run.TargetTail is { } tail
            ? string.Equals(sample.AtcId?.Trim(), tail, StringComparison.OrdinalIgnoreCase)
            : run.TargetPlace is null && sample.IsUser;

    /// <summary>So nah muss das eigene Flugzeug am Spawnpunkt stehen, damit die Höhe des Bodens unter ihm auch für das Fahrzeug gilt.</summary>
    public const double OwnGroundRangeM = 300;

    /// <summary>
    /// Boden für den Spawn: unter dem eigenen Flugzeug gemessen, wenn es am Boden höchstens <see cref="OwnGroundRangeM"/> vom Spawnpunkt
    /// steht und die Messung zur Platzhöhe passt, sonst die Platzhöhe. Test EDDK 05.11.: Der Jetson stand 1,3 km entfernt 30 ft tiefer
    /// als die Wache, alle Fahrzeuge kamen aus dem Boden.
    /// </summary>
    public static double SpawnGroundFt(double elevationFt, double? ownGroundFt, double? ownDistanceM) =>
        ownGroundFt is { } own && ownDistanceM is { } distance && distance <= OwnGroundRangeM && Math.Abs(own - elevationFt) < MaxGroundCorrectionFt
            ? own
            : elevationFt;

    /// <summary>Boden unter dem Fahrzeug aus der Abfrage (eingefroren: Höhe minus Höhe über Grund), nur in der Nähe des Erwarteten.</summary>
    private static void OnProbe(double expectedGroundFt, FireVehicle vehicle, AiProbeSample sample)
    {
        vehicle.SimGroundSpeedKt = sample.GroundSpeedKt;
        var measured = sample.AltitudeFt - sample.AltitudeAglFt;
        if (Math.Abs(measured - expectedGroundFt) < MaxGroundCorrectionFt)
        {
            vehicle.MeasureGround(measured);
        }
    }

    /// <summary>Takt: vorgemerkten Alarm auslösen, Lage des Flugzeugs an den Einsatz, Licht, entfernen, was zurück ist.</summary>
    private async Task UpdateAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 2);
        _lastTick = now;
        var remove = new List<(FireVehicle Vehicle, uint ObjectId)>();
        var lights = new List<(uint ObjectId, bool Blue)>();
        Run? finished = null;

        lock (_lock)
        {
            if (_run is not { } run)
            {
                return;
            }

            if (run.Failed)
            {
                if (now - run.FailedUtc > KeepFailed && run.Objects.Count == 0)
                {
                    _last = Info(run);
                    _run = null;
                    return;
                }
            }

            if (!run.Dispatched)
            {
                ArmedLocked(run, now);
                return;
            }

            if (run.Operation is not { } operation || run.Graph is not { } graph)
            {
                return;
            }

            var aircraft = run.TargetPlace ?? (run.TargetTail is { } targetTail ? TestState(run, targetTail, graph, now) : OwnState(graph, now));
            if (aircraft is not null)
            {
                operation.Update(aircraft, dt);
            }

            CheckCrossingsLocked(run, operation, graph, now);

            if (now - run.StartedUtc > MaxRun && !operation.Returning)
            {
                StandDownLocked(run, "timeout");
            }

            foreach (var (vehicle, id) in run.Objects)
            {
                if (vehicle.Phase == FireVehiclePhase.Gone)
                {
                    remove.Add((vehicle, id));
                    continue;
                }

                if (run.BlueSent.GetValueOrDefault(vehicle) != vehicle.BlueLight)
                {
                    run.BlueSent[vehicle] = vehicle.BlueLight;
                    lights.Add((id, vehicle.BlueLight));
                }

                var phase = vehicle.Phase;
                if (!run.LoggedPhase.TryGetValue(vehicle, out var logged) || logged != phase)
                {
                    run.LoggedPhase[vehicle] = phase;
                    _log.LogInformation(Loc.Log("log.fire.phase"), vehicle.Unit.Name, PhaseName(phase), double.IsNaN(vehicle.GapM) ? 0 : vehicle.GapM);
                }
            }

            foreach (var (vehicle, _) in remove)
            {
                run.Objects.Remove(vehicle);
                operation.Forget(vehicle);
            }

            if (run.SpawnDone && run.Objects.Count == 0 && (operation.Returning || run.Failed))
            {
                finished = run;
                _last = Info(run) with { State = run.Failed ? "unavailable" : "finished" };
                _run = null;
            }
        }

        foreach (var (id, blue) in lights)
        {
            try
            {
                await SetLightsAsync(id, blue, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, Loc.Log("log.fire.lightsFailed"), ex.Message);
            }
        }

        foreach (var (vehicle, id) in remove)
        {
            _sim.StopAiProbe(id);
            _sim.StopAiPose(id, unfreeze: false);
            await RemoveObjectAsync(id, ct);
            _log.LogInformation(Loc.Log("log.fire.removed"), vehicle.Unit.Name, id);
        }

        if (finished is not null)
        {
            finished.Cts.Cancel();
            _log.LogInformation(Loc.Log("log.fire.finished"), finished.Airport ?? "-");
        }
    }

    /// <summary>Vorgemerkt (ATC): ausrücken, sobald das Flugzeug nahe genug am Landeflughafen ist.</summary>
    private void ArmedLocked(Run run, DateTimeOffset now)
    {
        if (now - run.StartedUtc > MaxArmed)
        {
            _last = Info(run) with { State = "finished" };
            _run = null;
            return;
        }

        if (_sim.Num("lat") is not { } lat || _sim.Num("lon") is not { } lon)
        {
            return;
        }

        // Ohne Ziel: der nächste Flughafen des Plans
        var airports = PlanAirports().ToList();
        var target = run.Airport is { } icao
            ? airports.FirstOrDefault(a => string.Equals(a.Icao, icao, StringComparison.OrdinalIgnoreCase))
            : airports.MinBy(a => RouteMath.DistanceNm(lat, lon, a.Lat!.Value, a.Lon!.Value));
        if (target is null)
        {
            return;
        }

        if (RouteMath.DistanceNm(lat, lon, target.Lat!.Value, target.Lon!.Value) > _options.Fire.DispatchRangeNm)
        {
            return;
        }

        run.Airport = target.Icao;
        if (run.RunwayAirport is { } runwayAirport && !string.Equals(runwayAirport, target.Icao, StringComparison.OrdinalIgnoreCase))
        {
            // Bahn des Zielflughafens, gelandet wird aber am nächsten: dort die Bahn aus dem Anflug
            run.Runway = null;
        }

        _log.LogInformation(Loc.Log("log.fire.dispatched"), run.Airport ?? "-", run.Runway ?? "-");
        Dispatch(run);
    }

    /// <summary>Einsatz beenden: vorgemerkt oder in Planung = gar nicht ausrücken, sonst alle zurück.</summary>
    private void StandDownLocked(Run run, string reason)
    {
        _log.LogInformation(Loc.Log("log.fire.standDown"), reason);
        if (run.Operation is { } operation)
        {
            operation.ReturnAll();
            return;
        }

        run.Cts.Cancel();
        if (ReferenceEquals(_run, run) && run.Objects.Count == 0)
        {
            _last = Info(run) with { State = "finished" };
            _run = null;
        }
    }

    /// <summary>Verbindung weg: mit ihr sind auch die Objekte fort.</summary>
    private void Drop()
    {
        lock (_lock)
        {
            if (_run is not { } run)
            {
                return;
            }

            run.Cts.Cancel();
            foreach (var vehicle in run.Objects.Keys)
            {
                vehicle.Remove();
            }

            _last = Info(run) with { State = "finished" };
            _run = null;
            _fix = null;
        }
    }

    private async Task RemoveObjectAsync(uint objectId, CancellationToken ct)
    {
        try
        {
            await _sim.RemoveAiObjectAsync(objectId, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, Loc.Log("log.fire.removeFailed"), objectId, ex.Message);
        }
    }

    /// <summary>
    /// Lage des eigenen Flugzeugs im Rollweg-Netz. Die Position kommt nur einmal pro Sekunde, Kurs und Fahrt jedes Frame – dazwischen
    /// rechnet der Dienst mit beiden weiter, damit die Spur nicht in Sekundenschritten springt.
    /// </summary>
    private FireAircraftState? OwnState(TaxiGraph graph, DateTimeOffset now)
    {
        if (_sim.Num("lat") is not { } lat || _sim.Num("lon") is not { } lon)
        {
            return null;
        }

        if (_fix is not { } fix || fix.Lat != lat || fix.Lon != lon)
        {
            _fix = (lat, lon, now);
            fix = _fix.Value;
        }

        var heading = _sim.Num("hdgTrue") ?? 0;
        var mps = Math.Max(0, (_sim.Num("gs") ?? 0) * MetersPerSecondPerKnot);
        var (east, north) = graph.ToLocal(fix.Lat, fix.Lon);
        var ahead = mps * Math.Min(1.5, (now - fix.Utc).TotalSeconds);
        var rad = heading * Math.PI / 180;
        return new FireAircraftState(east + (ahead * Math.Sin(rad)), north + (ahead * Math.Cos(rad)), heading, mps, _sim.Bool("onGround") == true);
    }

    /// <summary>
    /// Lage eines KI-Flugzeugs aus einem Verkehrstest (Messpunkt einmal pro Sekunde, dazwischen mit Kurs und Fahrt weitergerechnet).
    /// Ist der Test vorbei oder fehlt er 30 s lang, rückt die Feuerwehr ein.
    /// </summary>
    private FireAircraftState? TestState(Run run, string tail, TaxiGraph graph, DateTimeOffset now)
    {
        if (now - run.TestReadUtc >= TimeSpan.FromSeconds(1))
        {
            run.TestReadUtc = now;
            var test = _services.GetService<TrafficTestService>()?.Status()
                .LastOrDefault(t => string.Equals(t.Tail, tail, StringComparison.OrdinalIgnoreCase));
            if (test is { State: "running", Last: { Latitude: { } lat, Longitude: { } lon } last })
            {
                run.TestMissingSince = null;
                if (run.TestFix is not { } fix || fix.Seconds != last.Seconds)
                {
                    run.TestFix = (last.Seconds, lat, lon, last.HeadingTrueDeg ?? 0, last.GroundSpeedKt, last.OnGround, now);
                }
            }
            else
            {
                run.TestMissingSince ??= now;
                if (now - run.TestMissingSince > TimeSpan.FromSeconds(30) && run.Operation is { Returning: false })
                {
                    StandDownLocked(run, "traffic_test_ended");
                }
            }
        }

        if (run.TestFix is not { } current)
        {
            return null;
        }

        var mps = Math.Max(0, current.GroundSpeedKt * MetersPerSecondPerKnot);
        var (east, north) = graph.ToLocal(current.Latitude, current.Longitude);
        var ahead = mps * Math.Min(1.5, (now - current.Utc).TotalSeconds);
        var rad = current.HeadingDeg * Math.PI / 180;
        return new FireAircraftState(east + (ahead * Math.Sin(rad)), north + (ahead * Math.Cos(rad)), current.HeadingDeg, mps, current.OnGround);
    }

    private (double East, double North)? OwnLocal(TaxiGraph graph) =>
        _sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon ? graph.ToLocal(lat, lon) : null;

    /// <summary>Titel des Fahrzeugs aus den Einstellungen; FLF abwechselnd im Panther- und Z8-Stil.</summary>
    /// <summary>
    /// Alte Titel der Modelle mit Herstellernamen (vor der Umbenennung für die Veröffentlichung, docs/research/feuerwehr-regionen.md):
    /// Solange im Community-Ordner noch das alte Paket liegt, spawnt Duopilo damit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Duopilo_FLF_Rund_Licht"] = "Duopilo_FLF_Panther_Licht",
        ["Duopilo_FLF_Eckig_Licht"] = "Duopilo_FLF_Z8_Licht",
    };

    // Neue Titel, die es mit dem installierten Paket nicht gab; gilt bis MSFS neu verbunden ist (danach kann das Paket neu sein).
    // Nur PrepareAsync liest und schreibt, ein Einsatz nach dem anderen.
    private readonly HashSet<string> _missingTitles = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _missingTitlesFor;

    /// <summary>
    /// Titel zum Spawnen in dieser Reihenfolge: der eingestellte (außer er fehlte schon), dann der alte, und fehlte der eingestellte,
    /// zuletzt doch noch er. Nach einem Neustart von MSFS (neu gebautes Paket) zählt nichts mehr als fehlend (Test 03.11.: nach dem
    /// Umbenennen fehlten die alten Titel, die neuen wurden aus der Zeit davor übersprungen).
    /// </summary>
    private IEnumerable<string> Titles(string model)
    {
        if (_missingTitlesFor != _sim.Connection.SinceUtc)
        {
            _missingTitles.Clear();
            _missingTitlesFor = _sim.Connection.SinceUtc;
        }

        var missing = _missingTitles.Contains(model);
        if (!missing)
        {
            yield return model;
        }

        if (LegacyTitles.TryGetValue(model, out var legacy))
        {
            yield return legacy;
        }

        if (missing)
        {
            yield return model;
        }
    }

    private string ModelFor(FireUnit unit)
    {
        var fire = _options.Fire;
        switch (unit.Kind)
        {
            case FireVehicleKind.Flf:
                var models = fire.FlfModels.Split(new[] { ',', ';' },StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return models.Length == 0 ? "Duopilo_FLF_Rund_Licht" : models[(unit.Number - 1) % models.Length];
            case FireVehicleKind.Elw:
                return fire.ElwModel;
            case FireVehicleKind.Hlf:
                return fire.HlfModel;
            case FireVehicleKind.Rtf:
                return fire.RtfModel;
            default:
                return fire.RtwModel;
        }
    }

    /// <summary>Flughäfen des SimBrief-Plans mit Lage (Start, Ziel, Ausweichflughafen).</summary>
    private IEnumerable<OfpAirport> PlanAirports()
    {
        if (_simBrief.Current is not { } ofp)
        {
            return [];
        }

        return new[] { ofp.Destination, ofp.Origin, ofp.Alternate }
            .Where(a => a is { Icao.Length: > 0, Lat: not null, Lon: not null })
            .Select(a => a!);
    }

    /// <summary>Ohne Flughafen: der nächste Flughafen des SimBrief-Plans.</summary>
    private string? NearestPlanAirport()
    {
        var airports = PlanAirports().ToList();
        if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
        {
            return airports.MinBy(a => RouteMath.DistanceNm(lat, lon, a.Lat!.Value, a.Lon!.Value))?.Icao;
        }

        return airports.FirstOrDefault()?.Icao;
    }

    public static FireAlertLevel? ParseLevel(string? level) => (level ?? "local").Trim().ToLowerInvariant() switch
    {
        "" or "local" or "local_standby" or "panpan" or "pan_pan" => FireAlertLevel.LocalStandby,
        "full" or "full_emergency" or "mayday" => FireAlertLevel.FullEmergency,
        "ground" or "ground_incident" => FireAlertLevel.GroundIncident,
        _ => null,
    };

    public static string LevelName(FireAlertLevel level) => level switch
    {
        FireAlertLevel.FullEmergency => "full_emergency",
        FireAlertLevel.GroundIncident => "ground_incident",
        _ => "local_standby",
    };

    public static string PhaseName(FireVehiclePhase phase) => phase switch
    {
        FireVehiclePhase.Responding => "responding",
        FireVehiclePhase.Standby => "standby",
        FireVehiclePhase.Joining => "joining",
        FireVehiclePhase.Following => "following",
        FireVehiclePhase.Positioning => "positioning",
        FireVehiclePhase.AtAircraft => "at_aircraft",
        FireVehiclePhase.Returning => "returning",
        _ => "gone",
    };

    private FireInfo Idle() => new("idle", null, null, null, null, null, null, [], [], null);

    private FireInfo Info(Run run)
    {
        var operation = run.Operation;
        var state = run.Failed ? "unavailable"
            : !run.Dispatched ? "armed"
            : operation is null ? "planning"
            : operation.State;
        var vehicles = run.Objects
            .Select(pair =>
            {
                var (vehicle, id) = (pair.Key, pair.Value);
                var plan = run.Plans.GetValueOrDefault(vehicle);
                var pose = vehicle.Pose();
                return new FireVehicleInfo(
                    vehicle.Unit.Name,
                    vehicle.Model,
                    PhaseName(vehicle.Phase),
                    id,
                    plan?.Hold?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    plan?.Hold is null ? null : plan.AlongM,
                    Math.Round(vehicle.SpeedMps / MetersPerSecondPerKnot, 1),
                    vehicle.SimGroundSpeedKt is { } simKt ? Math.Round(simKt, 1) : null,
                    vehicle.RemainingM > 0 ? Math.Round(vehicle.RemainingM) : null,
                    double.IsNaN(vehicle.GapM) ? null : Math.Round(vehicle.GapM),
                    vehicle.Phase == FireVehiclePhase.Following ? Math.Round(vehicle.TargetGapM) : null,
                    Math.Round(pose.Latitude, 6),
                    Math.Round(pose.Longitude, 6),
                    vehicle.HoldingShort ? vehicle.NextCrossing?.Designation : null);
            })
            .ToList();
        var missing = operation?.Plan.Missing.Select(u => u.Name).ToList() ?? [];
        missing.AddRange(run.SpawnErrors);
        return new FireInfo(
            state,
            run.Source,
            LevelName(run.Level),
            run.Airport,
            run.Runway,
            run.Category,
            operation is null ? null : string.Join(", ", operation.Plan.Stations.Select(st => st.Name)),
            vehicles,
            missing,
            run.Error,
            run.TargetTail ?? (run.TargetParking is { } targetParking ? "parking " + targetParking : "own"));
    }

    private sealed class Run(string source, FireAlertLevel level, string? airport, string? runway, string? aircraftIcao, DateTimeOffset startedUtc, DateTimeOffset? emergencySince)
    {
        public string Source { get; } = source;

        public FireAlertLevel Level { get; } = level;

        public string? Airport { get; set; } = airport;

        public string? Runway { get; set; } = runway;

        /// <summary>Flughafen, zu dem <see cref="Runway"/> gehört (ATC: das Ziel des Plans); null = der des Einsatzes.</summary>
        public string? RunwayAirport { get; init; }

        public string? AircraftIcao { get; } = aircraftIcao;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public DateTimeOffset? EmergencySince { get; } = emergencySince;

        public CancellationTokenSource Cts { get; } = new();

        public bool Dispatched { get; set; }

        public bool SpawnDone { get; set; }

        public TaxiGraph? Graph { get; set; }

        public FireOperation? Operation { get; set; }

        public int? Category { get; set; }

        public Dictionary<FireVehicle, uint> Objects { get; } = [];

        public Dictionary<FireVehicle, FireVehiclePlan> Plans { get; } = [];

        public Dictionary<FireVehicle, bool> BlueSent { get; } = [];

        public Dictionary<FireVehicle, FireVehiclePhase> LoggedPhase { get; } = [];

        /// <summary>Für welche Bahn (Bezeichnung und Stelle auf dem Weg) der Halt eines Fahrzeugs schon im Log steht.</summary>
        public Dictionary<FireVehicle, string> CrossingLogged { get; } = [];

        /// <summary>Was die Bahn zuletzt belegte, je wartendes Fahrzeug (nur neue Gründe ins Log).</summary>
        public Dictionary<FireVehicle, string> CrossingBlocker { get; } = [];

        /// <summary>Letzte Verkehrsabfrage für die Bahnen, die die Fahrzeuge kreuzen wollen, und wann sie kam.</summary>
        public AircraftScan? CrossingScan { get; set; }

        public DateTimeOffset CrossingScanUtc { get; set; }

        public bool CrossingScanBusy { get; set; }

        public List<string> SpawnErrors { get; set; } = [];

        /// <summary>Kennzeichen des Verkehrstests, dem die Feuerwehr folgt; null = eigenes Flugzeug.</summary>
        public string? TargetTail { get; init; }

        /// <summary>Einsatzort Parkposition (fire_service alert mit parking) statt eines Flugzeugs.</summary>
        public string? TargetParking { get; init; }

        /// <summary>Lage des Einsatzorts an der Parkposition, sobald geplant (steht still).</summary>
        public FireAircraftState? TargetPlace { get; set; }

        public DateTimeOffset TestReadUtc { get; set; }

        public DateTimeOffset? TestMissingSince { get; set; }

        public (double Seconds, double Latitude, double Longitude, double HeadingDeg, double GroundSpeedKt, bool OnGround, DateTimeOffset Utc)? TestFix { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset FailedUtc { get; set; }

        public bool Failed => Error is not null;
    }

    private sealed class FireUnavailableException(string message) : Exception(message);
}
