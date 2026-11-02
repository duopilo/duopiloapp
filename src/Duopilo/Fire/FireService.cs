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
public sealed record FireVehicleInfo(string Unit, string Model, string Phase, uint? ObjectId, string? Hold, double? AlongM, double? SpeedKt, double? SimSpeedKt, double? RemainingM, double? GapM, double? TargetGapM);

/// <summary>Stand der Feuerwehr für fire_service und das Log.</summary>
/// <param name="State">idle, armed, planning, dispatching, responding, standby, following, at_aircraft, returning, finished, unavailable.</param>
/// <param name="Source">atc (Mayday/Pan Pan im Funk) oder tool (fire_service alert).</param>
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
    string? Error);

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
    private static readonly TimeSpan KeepFailed = TimeSpan.FromSeconds(60);

    private readonly DuopiloOptions _options;
    private readonly SimConnectClient _sim;
    private readonly TrafficConnection _traffic;
    private readonly SimBriefService _simBrief;
    private readonly IServiceProvider _services;
    private readonly ILogger<FireService> _log;
    private readonly object _lock = new();

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
    public FireInfo Alert(string? level, string? airport, string? runway)
    {
        if (!Available)
        {
            throw new InvalidOperationException(Loc.T("tools.fire.disabled"));
        }

        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var alertLevel = ParseLevel(level) ?? throw new InvalidOperationException(Loc.T("tools.fire.levelUnknown", level ?? ""));
        lock (_lock)
        {
            if (_run is { Failed: false })
            {
                throw new InvalidOperationException(Loc.T("tools.fire.busy"));
            }

            var icao = string.IsNullOrWhiteSpace(airport) ? NearestPlanAirport() : airport.Trim().ToUpperInvariant();
            if (icao is null)
            {
                throw new InvalidOperationException(Loc.T("tools.fire.noAirport"));
            }

            var run = new Run("tool", alertLevel, icao, string.IsNullOrWhiteSpace(runway) ? null : runway.Trim(), _simBrief.Current?.AircraftIcao, DateTimeOffset.UtcNow, null);
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
                var runway = airport is null || string.Equals(airport, order.Destination, StringComparison.OrdinalIgnoreCase) ? order.Runway : null;
                var next = new Run("atc", level, airport, runway, order.AircraftIcao, DateTimeOffset.UtcNow, order.SinceUtc);
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
        if (dispatchNow)
        {
            Dispatch(run);
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

            var graph = TaxiGraph.Build(data, runways);
            await SetObstaclesAsync(graph, ct);
            var wake = TrafficRules.WakeOf(run.AircraftIcao, TrafficCategory.Airliner);
            var category = FireCategory.Of(run.AircraftIcao, wake);
            var units = FireCategory.Lineup(run.Level, category);
            var own = OwnLocal(graph);
            var onGround = _sim.Bool("onGround") == true;

            FireDispatchPlan? plan;
            if (run.Level == FireAlertLevel.GroundIncident)
            {
                if (!onGround || own is null)
                {
                    throw new FireUnavailableException(Loc.T("tools.fire.notOnGround"));
                }

                plan = FirePlanner.PlanToAircraft(graph, units, own.Value.East, own.Value.North, FireCategory.PostUnitM(wake))
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.noRouteToAircraft", airport));
            }
            else
            {
                var end = ResolveRunway(airport, runways, run.Runway)
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.runwayUnknown", run.Runway ?? "-", airport));
                plan = FirePlanner.Plan(graph, end, units, wake, onGround ? own : null)
                    ?? throw new FireUnavailableException(Loc.T("tools.fire.noRouteToRunway", airport, end.Designation));
            }

            var operation = new FireOperation(graph, run.Level, plan, wake, category);
            lock (_lock)
            {
                if (!ReferenceEquals(_run, run) || ct.IsCancellationRequested)
                {
                    return;
                }

                run.Graph = graph;
                run.Operation = operation;
                run.Category = category;
                run.Runway = plan.Runway?.Designation ?? run.Runway;
            }

            _log.LogInformation(
                Loc.Log("log.fire.planned"),
                plan.Spawn.Parking?.Label ?? "?",
                plan.Runway?.Designation ?? "-",
                category,
                string.Join(", ", plan.Vehicles.Select(v => v.Unit.Name + "@" + (v.Hold is null ? "aircraft" : v.AlongM.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))),
                plan.Missing.Count);

            // Boden: unter dem eigenen Flugzeug gemessen (am Boden), sonst die Höhe des Flughafens; die Abfrage je Fahrzeug korrigiert
            var groundFt = onGround && _sim.Num("altTrue") is { } alt && _sim.Num("altAgl") is { } agl && Math.Abs(alt - agl - graph.ElevationFt) < MaxGroundCorrectionFt
                ? alt - agl
                : graph.ElevationFt;
            var height = _options.Fire.HeightFt;
            var (spawnLat, spawnLon) = graph.ToLatLon(plan.Spawn.EastM, plan.Spawn.NorthM);
            var errors = new List<string>();
            for (var i = 0; i < plan.Vehicles.Count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(SpawnInterval, ct);
                }

                var vp = plan.Vehicles[i];
                var model = ModelFor(vp.Unit);
                uint objectId;
                try
                {
                    var heading = vp.Route.At(0).HeadingDeg;
                    objectId = await _sim.CreateSimulatedObjectAsync(model, null, new AiInitPosition(spawnLat, spawnLon, groundFt + height, heading, true, 0), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(vp.Unit.Name + ": " + ex.Message);
                    _log.LogWarning(Loc.Log("log.fire.spawnFailed"), vp.Unit.Name, model, Loc.ToEnglish(ex.Message) ?? ex.Message);
                    continue;
                }

                var vehicle = new FireVehicle(vp.Unit, model, vp.Route, operation.Trail, graph.ToLatLon, groundFt, height);
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

    /// <summary>Blaulicht und Fahrlicht an – während das Objekt lädt, verwirft der Sim die Werte, deshalb mehrmals.</summary>
    private async Task SwitchLightsAsync(uint objectId, CancellationToken ct)
    {
        foreach (var delay in new[] { 1, 2, 5 })
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
        await _sim.SetAiLightsAsync(objectId, new AiLights(Beacon: blue, Landing: true), ct);
    }

    /// <summary>Boden unter dem Fahrzeug aus der Abfrage (eingefroren: Höhe minus Höhe über Grund), nur in der Nähe des Erwarteten.</summary>
    private static void OnProbe(double expectedGroundFt, FireVehicle vehicle, AiProbeSample sample)
    {
        vehicle.SimGroundSpeedKt = sample.GroundSpeedKt;
        var measured = sample.AltitudeFt - sample.AltitudeAglFt;
        if (Math.Abs(measured - expectedGroundFt) < MaxGroundCorrectionFt)
        {
            vehicle.GroundTargetFt = measured;
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

            if (OwnState(graph, now) is { } aircraft)
            {
                operation.Update(aircraft, dt);
            }

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

    private (double East, double North)? OwnLocal(TaxiGraph graph) =>
        _sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon ? graph.ToLocal(lat, lon) : null;

    /// <summary>Titel des Fahrzeugs aus den Einstellungen; FLF abwechselnd im Panther- und Z8-Stil.</summary>
    private string ModelFor(FireUnit unit)
    {
        var fire = _options.Fire;
        switch (unit.Kind)
        {
            case FireVehicleKind.Flf:
                var models = fire.FlfModels.Split(new[] { ',', ';' },StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return models.Length == 0 ? "Duopilo_FLF_Panther_Licht" : models[(unit.Number - 1) % models.Length];
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
                    vehicle.Phase == FireVehiclePhase.Following ? Math.Round(vehicle.TargetGapM) : null);
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
            operation?.Plan.Spawn.Parking?.Label,
            vehicles,
            missing,
            run.Error);
    }

    private sealed class Run(string source, FireAlertLevel level, string? airport, string? runway, string? aircraftIcao, DateTimeOffset startedUtc, DateTimeOffset? emergencySince)
    {
        public string Source { get; } = source;

        public FireAlertLevel Level { get; } = level;

        public string? Airport { get; set; } = airport;

        public string? Runway { get; set; } = runway;

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

        public List<string> SpawnErrors { get; set; } = [];

        public string? Error { get; set; }

        public DateTimeOffset FailedUtc { get; set; }

        public bool Failed => Error is not null;
    }

    private sealed class FireUnavailableException(string message) : Exception(message);
}
