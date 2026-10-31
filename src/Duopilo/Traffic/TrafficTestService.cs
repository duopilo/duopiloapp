using System.Globalization;
using System.Text.Json;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Sim;
using Microsoft.Extensions.Options;

namespace Duopilo.Traffic;

/// <summary>Anfrage für einen Testanflug.</summary>
/// <param name="Title">Modelltitel; leer = automatisch (A320, FSLTL bevorzugt).</param>
/// <param name="SpawnNm">Abstand des Spawns zur Schwelle; leer = Standard der Variante (A1 5 NM, A2 15 NM).</param>
/// <param name="Gear">Wie das Fahrwerk ausgefahren wird (Event GEAR_DOWN, GEAR_SET, Hebel-SimVar oder alles).</param>
public sealed record TrafficTestRequest(
    string Airport,
    string Runway,
    TrafficTestVariant Variant,
    string? Title = null,
    string? Livery = null,
    double? SpawnNm = null,
    AiGearMethod Gear = AiGearMethod.All);

/// <summary>Zeitpunkt und Text eines Ereignisses der Testlinie.</summary>
public sealed record TrafficTestEvent(double Seconds, string Text);

/// <summary>Letzter Messpunkt eines laufenden Tests.</summary>
public sealed record TrafficTestLast(
    double Seconds,
    double AltitudeFt,
    double AltitudeAglFt,
    double IndicatedKt,
    double TargetKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    bool OnGround,
    double GearExtendedPercent,
    double? DistanceToThresholdNm,
    double AlongM,
    double CrossM,
    string? AiState);

/// <summary>Zustand des Testwerkzeugs für Tool und Browser.</summary>
/// <param name="State">idle, running oder ended.</param>
public sealed record TrafficTestStatus(
    string State,
    string? Variant,
    string? Airport,
    string? Runway,
    string? Title,
    string? Livery,
    string? Gear,
    uint? ObjectId,
    DateTimeOffset? StartedUtc,
    double? ElapsedSeconds,
    string? EndReason,
    TrafficTestLast? Last,
    TrafficTestSummary? Summary,
    IReadOnlyList<TrafficTestEvent> Events,
    IReadOnlyList<TrafficTestWaypoint>? Waypoints,
    string? CsvFile,
    string? SummaryFile,
    string? Note)
{
    public static readonly TrafficTestStatus Idle = new("idle", null, null, null, null, null, null, null, null, null, null, null, null, [], null, null, null, null);
}

/// <summary>
/// Testwerkzeug für eigenen KI-Verkehr, Stufe A der Testlinie (docs/research/eigener-verkehr.md): ein NonATC-Flugzeug fliegt einen
/// Endanflug über Wegpunkte, landet und hält auf der Bahn. A1 mit Fahrwerk ab dem Spawn, A2 mit Fahrwerk erst bei 10 NM.
///
/// Ein Test zur Zeit. Messpunkte (1 Hz) gehen als CSV nach traffic-tests\ im Datenordner, die Auswertung als JSON daneben.
/// Das Flugzeug wird beim Stoppen, nach <see cref="TrafficTestOptions.MaxMinutes"/> und beim Beenden von Duopilo entfernt.
/// Standardmäßig aus (Duopilo:TrafficTest:Enabled), weil es Objekte im Sim erzeugt.
/// </summary>
public sealed class TrafficTestService : IHostedService, IDisposable
{
    /// <summary>Kennzeichen (ATC ID) der Testflugzeuge – so tauchen sie auch in get_traffic auf.</summary>
    public const string TailNumber = "DUO01";

    /// <summary>Weiter weg lehnt der Sim neue Objekte ohnehin ab (Bereich um den Spieler).</summary>
    public const double MaxDistanceNm = 80;

    private static readonly TimeSpan NoDataAfter = TimeSpan.FromSeconds(30);

    private readonly SimConnectClient _sim;
    private readonly DuopiloOptions _options;
    private readonly ILogger<TrafficTestService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private TrafficTestRun? _run;

    public TrafficTestService(SimConnectClient sim, IOptions<DuopiloOptions> options, ILogger<TrafficTestService> log)
    {
        _sim = sim;
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => _options.TrafficTest.Enabled;

    public string Directory => Path.Combine(_options.ResolveDataDirectory(), "traffic-tests");

    /// <summary>Installierte Flugzeugmodelle, auf Wunsch gefiltert (alle Wörter müssen vorkommen).</summary>
    public async Task<IReadOnlyList<AiModel>> ModelsAsync(string? filter, CancellationToken ct) =>
        Filter(await _sim.EnumerateAircraftModelsAsync(ct), filter);

    /// <summary>Modelle, deren Titel oder Livery alle Wörter enthält; doppelte raus, nach Titel und Livery sortiert.</summary>
    public static IReadOnlyList<AiModel> Filter(IReadOnlyList<AiModel> models, string? filter)
    {
        var words = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. models
            .Where(m => words.All(w => m.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Livery.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(m => (m.Title, m.Livery))
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Livery, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Startet einen Testanflug (ein laufender wird vorher beendet). Wirft <see cref="InvalidOperationException"/> mit einem Text
    /// für den Piloten, wenn etwas fehlt (aus, kein Flug, Flughafen oder Bahn unbekannt, zu weit weg, kein Modell).
    /// </summary>
    public async Task<TrafficTestStatus> StartTestAsync(TrafficTestRequest request, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.disabled"));
        }

        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        if (_sim.Status.FlightRunning == false)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.noFlight"));
        }

        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync("replaced");

            var airport = request.Airport.Trim().ToUpperInvariant();
            var runways = await _sim.RequestAirportRunwaysAsync(airport, ct);
            if (runways.Count == 0)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.airportUnknown", airport));
            }

            var runway = RunwayEnd.Find(airport, runways, request.Runway);
            if (runway is null)
            {
                var wanted = request.Runway.Trim().ToUpperInvariant();
                var available = string.Join(", ", runways.SelectMany(r => new[] { r.Primary, r.Secondary }).Where(d => d.Length > 0));
                throw new InvalidOperationException(Loc.T("tools.trafficTest.runwayUnknown", wanted, airport, available));
            }

            if (runway.LengthM < TrafficTestPlan.MinRunwayM)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.runwayTooShort", runway.Designation, runway.LengthM));
            }

            if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
            {
                var distance = RouteMath.DistanceNm(lat, lon, runway.ThresholdLatitude, runway.ThresholdLongitude);
                if (distance > MaxDistanceNm)
                {
                    throw new InvalidOperationException(Loc.T("tools.trafficTest.tooFar", distance, MaxDistanceNm));
                }
            }

            var (title, livery) = await ResolveModelAsync(request, ct);
            var plan = TrafficTestPlan.Build(runway, request.Variant, request.SpawnNm);
            var spawnUtc = DateTimeOffset.UtcNow;
            var csv = OpenCsv(plan, spawnUtc);

            uint objectId;
            try
            {
                objectId = await _sim.CreateNonAtcAircraftAsync(title, livery, TailNumber, plan.Spawn, ct);
            }
            catch
            {
                csv?.Writer.Dispose();
                throw;
            }

            var run = new TrafficTestRun(plan, title, livery, request.Gear, objectId, spawnUtc, csv?.Path, csv?.Writer, _options.TrafficTest.MaxMinutes);
            run.AddEvent(0, string.Create(CultureInfo.InvariantCulture, $"spawn {plan.SpawnNm:0.#} NM, {plan.Spawn.AltitudeFt:0} ft, {plan.Spawn.AirspeedKt} kt, object {objectId}"));
            foreach (var warning in plan.Warnings)
            {
                run.AddEvent(0, "warning:" + warning);
            }

            lock (_lock)
            {
                _run = run;
            }

            try
            {
                if (plan.GearAtSpawn)
                {
                    await _sim.SetAiGearAsync(objectId, true, request.Gear, ct);
                    run.GearCommands++;
                    run.AddEvent(0, "gear_down_sent " + GearName(request.Gear));
                }

                await _sim.SetAiWaypointsAsync(objectId, [.. plan.Waypoints.Select(w => w.Waypoint)], ct);
                run.AddEvent(0, string.Create(CultureInfo.InvariantCulture, $"waypoints_sent {plan.Waypoints.Count}"));
                _sim.StartAiProbe(objectId, OnSample);
            }
            catch
            {
                // Halb eingerichtetes Flugzeug nicht stehen lassen
                await StopCoreAsync("setup_failed");
                throw;
            }

            _log.LogInformation(Loc.Log("log.trafficTest.started"), plan.Variant, airport, runway.Designation, title, objectId);
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Beendet den laufenden Test: Abfrage stoppen, Flugzeug entfernen, Auswertung schreiben.</summary>
    public async Task<TrafficTestStatus> StopTestAsync(string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync(reason);
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    public TrafficTestStatus Status()
    {
        lock (_lock)
        {
            if (_run is not { } run)
            {
                return TrafficTestStatus.Idle;
            }

            var now = DateTimeOffset.UtcNow;
            string? note = null;
            if (run.EndReason is null && (now - (run.LastSampleUtc ?? run.SpawnUtc)) > NoDataAfter)
            {
                note = Loc.T("tools.trafficTest.noData", (int)NoDataAfter.TotalSeconds);
            }

            var plan = run.Analysis.Plan;
            return new TrafficTestStatus(
                run.EndReason is null ? "running" : "ended",
                plan.Variant.ToString(),
                plan.Runway.Airport,
                plan.Runway.Designation,
                run.Title,
                run.Livery.Length > 0 ? run.Livery : null,
                GearName(run.GearMethod),
                run.ObjectId,
                run.SpawnUtc,
                Math.Round(((run.EndedUtc ?? now) - run.SpawnUtc).TotalSeconds),
                run.EndReason,
                run.Last,
                run.Analysis.Summary(),
                [.. run.Events],
                plan.Waypoints,
                run.CsvPath,
                run.SummaryPath,
                note);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StopTestAsync("shutdown", cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            _log.LogDebug(ex, Loc.Log("log.trafficTest.actionFailed"), "shutdown", ex.Message);
        }
    }

    public void Dispose() => _gate.Dispose();

    // ------------------------------------------------------------------ intern

    private async Task<(string Title, string Livery)> ResolveModelAsync(TrafficTestRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            return (request.Title.Trim(), request.Livery?.Trim() ?? "");
        }

        IReadOnlyList<AiModel> models;
        try
        {
            models = await _sim.EnumerateAircraftModelsAsync(ct);
        }
        catch (NotSupportedException)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.titleRequired"));
        }

        var pick = PickDefaultModel(models) ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.noModel"));
        return (pick.Title, pick.Livery);
    }

    /// <summary>Standardmodell: ein A320 aus dem FSLTL-Paket, sonst irgendein A320.</summary>
    public static AiModel? PickDefaultModel(IReadOnlyList<AiModel> models) =>
        models.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase) && m.Title.Contains("FSLTL", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase));

    /// <summary>Messpunkt vom SimConnect-Thread: auswerten, ins CSV schreiben, Fahrwerk und Zeitlimit.</summary>
    private void OnSample(AiProbeSample sample)
    {
        TrafficTestRun run;
        TrafficTestPoint point;
        lock (_lock)
        {
            if (_run is not { EndReason: null } current || current.ObjectId != sample.ObjectId)
            {
                return;
            }

            run = current;
            point = run.Analysis.Add(sample);
            run.LastSampleUtc = sample.Utc;
            run.Last = new TrafficTestLast(
                point.Seconds,
                Math.Round(sample.AltitudeFt),
                Math.Round(sample.AltitudeAglFt),
                Math.Round(sample.IndicatedKt),
                point.TargetKt,
                Math.Round(sample.GroundSpeedKt),
                Math.Round(sample.VerticalSpeedFpm),
                sample.OnGround,
                Math.Round(sample.GearExtendedPercent),
                point.DistanceToThresholdNm,
                Math.Round(point.AlongM),
                Math.Round(point.CrossM, 1),
                sample.AiState);
            foreach (var e in point.Events)
            {
                run.AddEvent(point.Seconds, e);
            }

            WriteCsv(run, sample, point);
        }

        var plan = run.Analysis.Plan;

        // A2: Fahrwerk erst im Anflug ausfahren
        if (plan.GearDownNm is { } gearNm && run.GearCommands == 0 && point.DistanceToThresholdNm is { } distance && distance <= gearNm)
        {
            run.GearCommands++;
            run.GearSentSeconds = point.Seconds;
            run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"gear_down_sent {GearName(run.GearMethod)} at {distance:0.0} NM"));
            Fire(_sim.SetAiGearAsync(run.ObjectId, true, run.GearMethod, CancellationToken.None), "gear");
        }

        // Fährt das Fahrwerk nach dem Befehl nicht aus, bis zu dreimal im Abstand von 3 s nachschicken (nur in der Luft)
        if (run.GearCommands is > 0 and < 4 && !sample.OnGround && sample.GearExtendedPercent < 50
            && point.Seconds >= run.GearSentSeconds + (3 * run.GearCommands))
        {
            run.GearCommands++;
            run.AddEvent(point.Seconds, "gear_down_resent " + GearName(run.GearMethod));
            Fire(_sim.SetAiGearAsync(run.ObjectId, true, run.GearMethod, CancellationToken.None), "gear");
        }

        if (point.Seconds > run.MaxMinutes * 60 && !run.StopScheduled)
        {
            run.StopScheduled = true;
            _ = Task.Run(() => StopTestAsync("timeout", CancellationToken.None));
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        TrafficTestRun? run;
        lock (_lock)
        {
            run = _run;
            if (run is null || run.EndReason is not null)
            {
                return;
            }

            run.EndReason = reason;
            run.EndedUtc = DateTimeOffset.UtcNow;
        }

        _sim.StopAiProbe(run.ObjectId);
        if (_sim.Connection.IsConnected)
        {
            try
            {
                await _sim.RemoveAiObjectAsync(run.ObjectId, CancellationToken.None);
                run.AddEvent(Math.Round((run.EndedUtc.Value - run.SpawnUtc).TotalSeconds, 1), "removed");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                _log.LogWarning(Loc.Log("log.trafficTest.actionFailed"), "remove", ex.Message);
            }
        }

        TrafficTestSummary summary;
        lock (_lock)
        {
            summary = run.Analysis.Summary();
            run.CsvWriter?.Dispose();
            run.CsvWriter = null;
            run.SummaryPath = WriteSummary(run, summary);
        }

        _log.LogInformation(Loc.Log("log.trafficTest.ended"), reason, summary.Verdict, summary.Samples);
    }

    private (string Path, StreamWriter Writer)? OpenCsv(TrafficTestPlan plan, DateTimeOffset spawnUtc)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = string.Create(CultureInfo.InvariantCulture, $"{spawnUtc:yyyyMMdd-HHmmss}-{plan.Runway.Airport}{plan.Runway.Designation}-{plan.Variant}.csv");
            var path = Path.Combine(Directory, name);
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine("utc,t_s,lat,lon,alt_ft,agl_ft,ias_kt,target_kt,gs_kt,vs_fpm,hdg_true,on_ground,gear_handle,gear_total_pct,gear_center_pct,gear_left_pct,gear_right_pct,ai_state,along_m,cross_m,dist_thr_nm,events");
            return (path, writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            return null;
        }
    }

    private void WriteCsv(TrafficTestRun run, AiProbeSample s, TrafficTestPoint p)
    {
        if (run.CsvWriter is not { } writer)
        {
            return;
        }

        try
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{s.Utc:O},{p.Seconds:0.0},{s.Latitude:0.000000},{s.Longitude:0.000000},{s.AltitudeFt:0},{s.AltitudeAglFt:0},{s.IndicatedKt:0},{p.TargetKt:0},{s.GroundSpeedKt:0},{s.VerticalSpeedFpm:0},{s.HeadingTrueDeg:0},{(s.OnGround ? 1 : 0)},{(s.GearHandleDown ? 1 : 0)},{s.GearTotalPercent:0.0},{s.GearCenterPercent:0.0},{s.GearLeftPercent:0.0},{s.GearRightPercent:0.0},{Csv(s.AiState)},{p.AlongM:0},{p.CrossM:0.0},{p.DistanceToThresholdNm:0.00},{Csv(string.Join(' ', p.Events))}"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            run.CsvWriter = null;
        }
    }

    private string? WriteSummary(TrafficTestRun run, TrafficTestSummary summary)
    {
        if (run.CsvPath is not { } csv)
        {
            return null;
        }

        try
        {
            var path = Path.ChangeExtension(csv, ".json");
            var plan = run.Analysis.Plan;
            var json = JsonSerializer.Serialize(
                new
                {
                    variant = plan.Variant.ToString(),
                    airport = plan.Runway.Airport,
                    runway = plan.Runway.Designation,
                    runwayHeadingTrue = Math.Round(plan.Runway.HeadingTrueDeg, 1),
                    runwayLengthM = Math.Round(plan.Runway.LengthM),
                    runwayElevationFt = Math.Round(plan.Runway.ElevationFt),
                    title = run.Title,
                    livery = run.Livery,
                    gear = GearName(run.GearMethod),
                    spawnUtc = run.SpawnUtc,
                    endReason = run.EndReason,
                    plan = new { plan.SpawnNm, plan.GearAtSpawn, plan.GearDownNm, plan.TouchdownM, plan.StopM, waypoints = plan.Waypoints },
                    summary,
                    events = run.Events,
                },
                SummaryJson);
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            return null;
        }
    }

    private static readonly JsonSerializerOptions SummaryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Name der Fahrwerks-Methode wie im Tool-Parameter (all, down, set, handle).</summary>
    public static string GearName(AiGearMethod method) => method.ToString().ToLowerInvariant();

    private static string Csv(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private void Fire(Task task, string action) =>
        task.ContinueWith(
            t => _log.LogWarning(Loc.Log("log.trafficTest.actionFailed"), action, t.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private sealed class TrafficTestRun(
        TrafficTestPlan plan,
        string title,
        string livery,
        AiGearMethod gearMethod,
        uint objectId,
        DateTimeOffset spawnUtc,
        string? csvPath,
        StreamWriter? csvWriter,
        int maxMinutes)
    {
        private readonly List<TrafficTestEvent> _events = [];

        public TrafficTestAnalysis Analysis { get; } = new(plan, spawnUtc);

        public string Title { get; } = title;

        public string Livery { get; } = livery;

        public AiGearMethod GearMethod { get; } = gearMethod;

        /// <summary>Sekunde des ersten Fahrwerksbefehls (A1 beim Spawn = 0, A2 bei 10 NM).</summary>
        public double GearSentSeconds { get; set; }

        public uint ObjectId { get; } = objectId;

        public DateTimeOffset SpawnUtc { get; } = spawnUtc;

        public string? CsvPath { get; } = csvPath;

        public StreamWriter? CsvWriter { get; set; } = csvWriter;

        public int MaxMinutes { get; } = Math.Clamp(maxMinutes, 1, 120);

        public string? SummaryPath { get; set; }

        public string? EndReason { get; set; }

        public DateTimeOffset? EndedUtc { get; set; }

        public DateTimeOffset? LastSampleUtc { get; set; }

        public TrafficTestLast? Last { get; set; }

        public int GearCommands { get; set; }

        public bool StopScheduled { get; set; }

        public IReadOnlyList<TrafficTestEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void AddEvent(double seconds, string text)
        {
            lock (_events)
            {
                _events.Add(new TrafficTestEvent(seconds, text));
            }
        }
    }
}
