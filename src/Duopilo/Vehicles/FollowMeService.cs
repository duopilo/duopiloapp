using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Traffic;
using Microsoft.Extensions.Options;

namespace Duopilo.Vehicles;

/// <summary>Stand des Follow-me für follow_me und das Log.</summary>
/// <param name="State">idle, planning, approach, in_position, leading, at_stand, driving_off, leaving, finished, unavailable.</param>
/// <param name="Source">atc (Anfrage im Funk) oder tool (follow_me).</param>
/// <param name="GapM">Abstand Fahrzeug – Flugzeug entlang der Route, sobald es gewendet hat.</param>
public sealed record FollowMeInfo(
    string State,
    string? Source,
    long RequestId,
    string? Airport,
    string? Gate,
    string? GateType,
    IReadOnlyList<string> Taxiways,
    double? RouteM,
    double? GapM,
    double? TargetGapM,
    double? SpeedKt,
    uint? ObjectId,
    string Model,
    string? Error);

/// <summary>
/// Follow-me nach der Landung – nur auf Anfrage: Fordert der Pilot es beim Ground an (<see cref="AtcSession.FollowMe"/>), plant der
/// Dienst die Route vom Flugzeug zum Gate (Rollweg-Netz aus MSFS, Gate wie bei ATC oder ein freies passendes), erzeugt das Fahrzeug
/// auf der Route vor dem Flugzeug und bewegt es selbst (<see cref="FollowMeVehicle"/>): entgegen, wenden, vorweg mit Abstand, am Gate
/// vorbei und weg. Seinen Stand bekommt ATC über <see cref="AtcStatus"/> ("follow the follow-me …" oder "not available").
/// Läuft nur in der Tray-Instanz (in der stdio-Instanz ist es aus), damit nie zwei Fahrzeuge kommen.
/// </summary>
public sealed class FollowMeService : BackgroundService
{
    /// <summary>Anfragen über follow_me (ohne ATC) tragen diese Nummer.</summary>
    public const long ToolRequestId = -1;

    private const double MetersPerSecondPerKnot = 0.514444;
    private const double ObstacleMarginM = 4000;
    private const double MaxGroundCorrectionFt = 60;
    private const int MaxParkingTries = 10;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AtcInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRun = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan KeepFailed = TimeSpan.FromSeconds(60);

    private readonly DuopiloOptions _options;
    private readonly SimConnectClient _sim;
    private readonly TrafficConnection _traffic;
    private readonly SimBriefService _simBrief;
    private readonly IServiceProvider _services;
    private readonly ILogger<FollowMeService> _log;
    private readonly object _lock = new();

    private Run? _run;
    private FollowMeInfo? _last;
    private volatile AtcFollowMeStatus _atcStatus = new(0, AtcFollowMeVehicle.None);

    public FollowMeService(IOptions<DuopiloOptions> options, SimConnectClient sim, TrafficConnection traffic, SimBriefService simBrief, IServiceProvider services, ILogger<FollowMeService> log)
    {
        _options = options.Value;
        _sim = sim;
        _traffic = traffic;
        _simBrief = simBrief;
        _services = services;
        _log = log;
    }

    /// <summary>Follow-me gibt es in dieser Instanz.</summary>
    public bool Available => _options.FollowMe.Enabled;

    /// <summary>Stand für ATC (ohne Sperre, auch aus dem Lock des AtcService lesbar), null = in dieser Instanz kein Follow-me.</summary>
    public AtcFollowMeStatus? AtcStatus => Available ? _atcStatus : null;

    /// <summary>Aktueller Stand (follow_me status).</summary>
    public FollowMeInfo Status()
    {
        lock (_lock)
        {
            return _run is { } run ? Info(run) : _last ?? Idle();
        }
    }

    /// <summary>Follow-me ohne Funk anfordern (follow_me request) – zum Testen an jedem Flughafen.</summary>
    public FollowMeInfo Request(string? airport, string? gate)
    {
        if (!Available)
        {
            throw new InvalidOperationException(Loc.T("tools.followMe.disabled"));
        }

        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        lock (_lock)
        {
            if (_run is { Failed: false })
            {
                throw new InvalidOperationException(Loc.T("tools.followMe.busy"));
            }

            var icao = string.IsNullOrWhiteSpace(airport) ? NearestPlanAirport() : airport.Trim().ToUpperInvariant();
            if (icao is null)
            {
                throw new InvalidOperationException(Loc.T("tools.followMe.noAirport"));
            }

            var run = Start(ToolRequestId, "tool", icao, string.IsNullOrWhiteSpace(gate) ? null : gate.Trim(), _simBrief.Current?.AircraftIcao);
            return Info(run);
        }
    }

    /// <summary>Abbestellen (follow_me cancel): das Fahrzeug fährt davon.</summary>
    public FollowMeInfo Cancel()
    {
        lock (_lock)
        {
            if (_run is not { } run)
            {
                throw new InvalidOperationException(Loc.T("tools.followMe.nothing"));
            }

            LeaveLocked(run);
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

                    if (Available && DateTimeOffset.UtcNow >= nextAtc)
                    {
                        nextAtc = DateTimeOffset.UtcNow + AtcInterval;
                        FollowAtc();
                    }

                    await UpdateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, Loc.Log("log.followMe.updateFailed"), ex.Message);
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
            await RemoveAsync(last, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ ATC

    /// <summary>Anfrage im Funk übernehmen und – wenn ATC das Follow-me nicht mehr will (abbestellt, Gate erreicht) – wegschicken.</summary>
    private void FollowAtc()
    {
        if (_services.GetService<AtcService>() is not { } atc)
        {
            return;
        }

        var order = atc.WithSession<AtcOrder?>((session, _) => session is null
            ? null
            : new AtcOrder(session.FollowMe, session.FollowMeRequestId, session.Phase, session.KnownGate, session.Plan.Ofp.Destination.Icao, session.Plan.Ofp.AircraftIcao));

        lock (_lock)
        {
            var run = _run;
            if (order is { FollowMe: AtcFollowMe.Requested, Phase: AtcPhase.GroundArrival } && (run is null || run.RequestId != order.RequestId))
            {
                if (run is not null && !run.Failed)
                {
                    // Ein anderes Follow-me (Test oder ältere Anfrage) fährt weg; das neue kommt, sobald es fort ist
                    LeaveLocked(run);
                    return;
                }

                if (order.Airport is not { Length: > 0 } airport)
                {
                    _atcStatus = new AtcFollowMeStatus(order.RequestId, AtcFollowMeVehicle.Unavailable);
                    return;
                }

                Start(order.RequestId, "atc", airport, order.Gate, order.AircraftIcao);
                return;
            }

            if (run is { Source: "atc", Leaving: false, Failed: false }
                && (order is null || order.RequestId != run.RequestId || order.FollowMe is not (AtcFollowMe.Requested or AtcFollowMe.Following)
                    || order.Phase != AtcPhase.GroundArrival))
            {
                LeaveLocked(run);
            }
        }
    }

    private sealed record AtcOrder(AtcFollowMe FollowMe, long RequestId, AtcPhase Phase, string? Gate, string? Airport, string? AircraftIcao);

    // ------------------------------------------------------------------ Ablauf

    private Run Start(long requestId, string source, string airport, string? gate, string? aircraftIcao)
    {
        var run = new Run(requestId, source, airport, gate, aircraftIcao, DateTimeOffset.UtcNow);
        _run = run;
        _last = null;
        SetAtc(run, AtcFollowMeVehicle.OnTheWay);
        _log.LogInformation(Loc.Log("log.followMe.requested"), source, requestId, airport, gate ?? "-");
        _ = Task.Run(() => PrepareAsync(run, run.Cts.Token));
        return run;
    }

    /// <summary>Route planen, Fahrzeug erzeugen und bewegen. Geht etwas nicht, ist das Follow-me „nicht verfügbar“.</summary>
    private async Task PrepareAsync(Run run, CancellationToken ct)
    {
        uint? created = null;
        try
        {
            if (_sim.Bool("onGround") != true)
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.notOnGround"));
            }

            if (_sim.Num("lat") is not { } lat || _sim.Num("lon") is not { } lon || _sim.Num("hdgTrue") is not { } heading)
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.noPosition"));
            }

            var runways = await _sim.RequestAirportRunwaysAsync(run.Airport, ct);
            var data = await _sim.RequestAirportTaxiwaysAsync(run.Airport, ct);
            if (data.Points.Count == 0)
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.noTaxiways", run.Airport));
            }

            var graph = TaxiGraph.Build(data, runways);
            var (east, north) = graph.ToLocal(lat, lon);
            if (FollowMePlan.StartNode(graph, east, north, heading) is null)
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.notOnTaxiway", run.Airport, FollowMeProfile.MaxOffTaxiwayM));
            }

            var wake = TrafficRules.WakeOf(run.AircraftIcao, TrafficCategory.Airliner);
            var category = wake == TrafficWake.Light ? TrafficCategory.GeneralAviation : TrafficCategory.Airliner;
            await SetObstaclesAsync(graph, category, ct);
            var plan = ChoosePlan(run, graph, east, north, heading, category, wake);

            // Boden: unter dem eigenen Flugzeug gemessen, sonst die Höhe des Flughafens
            var groundFt = _sim.Num("altTrue") is { } alt && _sim.Num("altAgl") is { } agl && Math.Abs(alt - agl - graph.ElevationFt) < MaxGroundCorrectionFt
                ? alt - agl
                : graph.ElevationFt;
            var height = _options.FollowMe.HeightFt;
            var spawn = plan.Approach.At(0);
            var (spawnLat, spawnLon) = graph.ToLatLon(spawn.East, spawn.North);
            _log.LogInformation(
                Loc.Log("log.followMe.planned"),
                plan.Parking.Parking?.Label ?? "?",
                string.Join(" ", plan.Taxiways),
                plan.StandEntryS,
                plan.MeetS,
                plan.Approach.LengthM);

            uint objectId;
            try
            {
                objectId = await _sim.CreateSimulatedObjectAsync(_options.FollowMe.Model, null, new AiInitPosition(spawnLat, spawnLon, groundFt + height, spawn.HeadingDeg, true, 0), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.spawnFailed", _options.FollowMe.Model, ex.Message));
            }

            created = objectId;
            var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, groundFt, height);
            lock (_lock)
            {
                if (!ReferenceEquals(_run, run) || ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                run.Graph = graph;
                run.Plan = plan;
                run.Vehicle = vehicle;
                run.ObjectId = objectId;
                run.GroundFt = groundFt;
                if (run.Leaving)
                {
                    vehicle.Leave();
                }
            }

            _sim.StartAiPose(objectId, dt =>
            {
                vehicle.Advance(dt);
                return vehicle.Phase == FollowMePhase.Gone ? null : vehicle.Pose();
            }, releaseAi: false);
            _sim.StartAiProbe(objectId, sample => OnProbe(run, vehicle, sample));
            _log.LogInformation(Loc.Log("log.followMe.spawned"), _options.FollowMe.Model, objectId);
            _ = SwitchLightsAsync(objectId, ct);
        }
        catch (OperationCanceledException)
        {
            if (created is { } id)
            {
                await RemoveObjectAsync(id);
            }
        }
        catch (Exception ex)
        {
            if (created is { } id)
            {
                await RemoveObjectAsync(id);
            }

            var reason = ex is FollowMeUnavailableException ? ex.Message : Loc.T("tools.followMe.failed", ex.Message);
            lock (_lock)
            {
                run.Error = reason;
                run.FailedUtc = DateTimeOffset.UtcNow;
                if (ReferenceEquals(_run, run))
                {
                    SetAtc(run, AtcFollowMeVehicle.Unavailable);
                }
            }

            _log.LogWarning(Loc.Log("log.followMe.unavailable"), Loc.ToEnglish(reason) ?? reason);
        }
    }

    /// <summary>
    /// Gate wie angefragt (bzw. wie ATC es nennt), sonst eins, das zu Größe und Art passt und frei ist – das nächste unter den ersten
    /// passenden mit Route (Ground vergibt meist ein nahes freies).
    /// </summary>
    private FollowMePlan ChoosePlan(Run run, TaxiGraph graph, double east, double north, double heading, TrafficCategory category, TrafficWake wake)
    {
        var gap = FollowMeProfile.GapM(wake);
        if (run.Gate is { } label)
        {
            if (graph.FindParking(label) is { } wanted)
            {
                return FollowMePlan.Build(graph, east, north, heading, wanted, gap)
                    ?? throw new FollowMeUnavailableException(Loc.T("tools.followMe.noRouteToGate", wanted.Parking?.Label ?? label));
            }

            if (run.Source == "tool")
            {
                throw new FollowMeUnavailableException(Loc.T("tools.followMe.gateUnknown", label, run.Airport));
            }
        }

        var random = new Random(unchecked((int)(run.RequestId * 7919) ^ run.Airport.GetHashCode(StringComparison.Ordinal)));
        var plans = TaxiRoute.CandidateParkings(graph, category, wake, random)
            .Take(MaxParkingTries)
            .Select(node => FollowMePlan.Build(graph, east, north, heading, node, gap))
            .OfType<FollowMePlan>()
            .ToList();
        return plans.MinBy(p => p.StandEntryS)
            ?? throw new FollowMeUnavailableException(Loc.T("tools.followMe.noRoute", run.Airport));
    }

    /// <summary>Andere Flugzeuge am Boden: ihre Parkpositionen sind belegt, an ihnen führt die Route nicht zu nah vorbei.</summary>
    private async Task SetObstaclesAsync(TaxiGraph graph, TrafficCategory category, CancellationToken ct)
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
            graph.SetObstacles(parked, TaxiProfile.For(category).ClearanceM);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, Loc.Log("log.followMe.obstaclesFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Rundumlicht und Fahrlicht an, 2, 3 und 5 s nach dem Spawn: Solange das Objekt lädt, verwirft der Sim die Werte (DATA_ERROR),
    /// deshalb mehrmals. Nur <c>L:1:</c> und die Events – die SimVars <c>LIGHT …</c> lehnt der Sim an Bodenfahrzeugen ab (wie bei der
    /// Feuerwehr, Tests EDDL/EDDK 04./05.11.).
    /// </summary>
    private async Task SwitchLightsAsync(uint objectId, CancellationToken ct)
    {
        foreach (var delay in new[] { 2, 1, 2 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                await _sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_RUNDUMLICHT", "Number", 1, ct);
                await _sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_FAHRLICHT", "Number", 1, ct);
                await _sim.SetAiLightsAsync(objectId, new AiLights(Beacon: true, Landing: true), ct, simVars: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, Loc.Log("log.followMe.lightsFailed"), ex.Message);
            }
        }
    }

    /// <summary>Boden unter dem Fahrzeug aus der Abfrage (eingefroren: Höhe minus Höhe über Grund), nur in der Nähe des Erwarteten.</summary>
    private static void OnProbe(Run run, FollowMeVehicle vehicle, AiProbeSample sample)
    {
        var measured = sample.AltitudeFt - sample.AltitudeAglFt;
        if (Math.Abs(measured - run.GroundFt) < MaxGroundCorrectionFt)
        {
            vehicle.GroundTargetFt = measured;
        }
    }

    /// <summary>Takt: Lage des Flugzeugs an das Fahrzeug, Stand an ATC, am Ende entfernen.</summary>
    private async Task UpdateAsync(CancellationToken ct)
    {
        Run? done = null;
        lock (_lock)
        {
            if (_run is not { } run)
            {
                return;
            }

            if (run.Failed)
            {
                // Nicht verfügbar: ATC hat es gehört (oder fragt neu) – nach einer Weile vergessen
                if (DateTimeOffset.UtcNow - run.FailedUtc > KeepFailed)
                {
                    _last = Info(run);
                    _run = null;
                }

                return;
            }

            if (run.Vehicle is not { } vehicle || run.Plan is not { } plan || run.Graph is not { } graph)
            {
                return;
            }

            if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
            {
                var (east, north) = graph.ToLocal(lat, lon);
                var from = Math.Max(0, run.AircraftS - 30);
                var s = plan.Reference.NearestS(east, north, from, run.AircraftS + 150);
                run.AircraftS = s;
                vehicle.SetAircraft(s, (_sim.Num("gs") ?? 0) * MetersPerSecondPerKnot);
            }

            if (DateTimeOffset.UtcNow - run.StartedUtc > MaxRun)
            {
                LeaveLocked(run);
            }

            var phase = vehicle.Phase;
            if (phase != run.LoggedPhase)
            {
                run.LoggedPhase = phase;
                _log.LogInformation(Loc.Log("log.followMe.phase"), PhaseName(phase), double.IsNaN(vehicle.GapM) ? 0 : vehicle.GapM);
            }

            SetAtc(run, phase switch
            {
                FollowMePhase.Approach => AtcFollowMeVehicle.OnTheWay,
                FollowMePhase.InPosition => AtcFollowMeVehicle.InPosition,
                FollowMePhase.Leading or FollowMePhase.AtStand => AtcFollowMeVehicle.Leading,
                _ => AtcFollowMeVehicle.Finished,
            });

            if (phase == FollowMePhase.Gone)
            {
                done = run;
                _last = Info(run) with { State = "finished" };
                _run = null;
            }
        }

        if (done is not null)
        {
            await RemoveAsync(done, ct);
        }
    }

    /// <summary>Wegschicken: das Fahrzeug fährt davon; wird es gerade erst geplant, kommt es gar nicht.</summary>
    private void LeaveLocked(Run run)
    {
        if (run.Leaving)
        {
            return;
        }

        run.Leaving = true;
        if (run.Vehicle is { } vehicle)
        {
            vehicle.Leave();
            return;
        }

        // Noch in Planung oder nicht verfügbar: gar nicht erst kommen
        run.Cts.Cancel();
        if (ReferenceEquals(_run, run))
        {
            _last = Info(run) with { State = run.Failed ? "unavailable" : "finished" };
            _run = null;
            SetAtc(run, run.Failed ? AtcFollowMeVehicle.Unavailable : AtcFollowMeVehicle.Finished);
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
            _last = Info(run) with { State = "finished" };
            _run = null;
            _atcStatus = new AtcFollowMeStatus(0, AtcFollowMeVehicle.None);
        }
    }

    private async Task RemoveAsync(Run run, CancellationToken ct)
    {
        run.Cts.Cancel();
        if (run.ObjectId is not { } id)
        {
            return;
        }

        _sim.StopAiProbe(id);
        _sim.StopAiPose(id, unfreeze: false);
        await RemoveObjectAsync(id);
    }

    private async Task RemoveObjectAsync(uint objectId)
    {
        try
        {
            await _sim.RemoveAiObjectAsync(objectId, CancellationToken.None);
            _log.LogInformation(Loc.Log("log.followMe.removed"), objectId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, Loc.Log("log.followMe.removeFailed"), objectId, ex.Message);
        }
    }

    private void SetAtc(Run run, AtcFollowMeVehicle state) =>
        _atcStatus = new AtcFollowMeStatus(run.RequestId, state, run.Plan?.Parking.Parking?.Label ?? run.Gate);

    /// <summary>Ohne Flughafen: Ziel oder Start des SimBrief-Plans, je nachdem, welcher näher ist.</summary>
    private string? NearestPlanAirport()
    {
        if (_simBrief.Current is not { } ofp)
        {
            return null;
        }

        var candidates = new[] { ofp.Destination, ofp.Origin }.Where(a => a.Icao is { Length: > 0 }).ToList();
        if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
        {
            return candidates
                .OrderBy(a => a.Lat is { } alat && a.Lon is { } alon ? RouteMath.DistanceNm(lat, lon, alat, alon) : double.MaxValue)
                .FirstOrDefault()?.Icao;
        }

        return candidates.FirstOrDefault()?.Icao;
    }

    private FollowMeInfo Idle() => new("idle", null, 0, null, null, null, [], null, null, null, null, null, _options.FollowMe.Model, null);

    private FollowMeInfo Info(Run run)
    {
        var vehicle = run.Vehicle;
        var state = run.Failed ? "unavailable"
            : vehicle is null ? "planning"
            : PhaseName(vehicle.Phase);
        var gap = vehicle is not null && !double.IsNaN(vehicle.GapM) ? Math.Round(vehicle.GapM) : (double?)null;
        return new FollowMeInfo(
            state,
            run.Source,
            run.RequestId,
            run.Airport,
            run.Plan?.Parking.Parking?.Label ?? run.Gate,
            run.Plan?.Parking.Parking?.TypeName,
            run.Plan?.Taxiways ?? [],
            run.Plan is { } plan ? Math.Round(plan.StandEntryS) : null,
            gap,
            run.Plan?.GapM,
            vehicle is not null ? Math.Round(vehicle.SpeedMps / MetersPerSecondPerKnot, 1) : null,
            run.ObjectId,
            _options.FollowMe.Model,
            run.Error);
    }

    private static string PhaseName(FollowMePhase phase) => phase switch
    {
        FollowMePhase.Approach => "approach",
        FollowMePhase.InPosition => "in_position",
        FollowMePhase.Leading => "leading",
        FollowMePhase.AtStand => "at_stand",
        FollowMePhase.DrivingOff => "driving_off",
        FollowMePhase.Leaving => "leaving",
        _ => "finished",
    };

    private sealed class Run(long requestId, string source, string airport, string? gate, string? aircraftIcao, DateTimeOffset startedUtc)
    {
        public long RequestId { get; } = requestId;

        public string Source { get; } = source;

        public string Airport { get; } = airport;

        public string? Gate { get; } = gate;

        public string? AircraftIcao { get; } = aircraftIcao;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public CancellationTokenSource Cts { get; } = new();

        public TaxiGraph? Graph { get; set; }

        public FollowMePlan? Plan { get; set; }

        public FollowMeVehicle? Vehicle { get; set; }

        public uint? ObjectId { get; set; }

        public double GroundFt { get; set; }

        public double AircraftS { get; set; }

        public FollowMePhase? LoggedPhase { get; set; }

        public bool Leaving { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset FailedUtc { get; set; }

        public bool Failed => Error is not null;
    }

    private sealed class FollowMeUnavailableException(string message) : Exception(message);
}
