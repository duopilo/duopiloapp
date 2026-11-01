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
/// <param name="FinalLowFt">Wie weit die letzte Meile unter dem Gleitpfad liegt; leer = <see cref="TrafficTestPlan.DefaultFinalLowFt"/>.</param>
/// <param name="TouchdownM">Erster Boden-Wegpunkt hinter der Schwelle (davor negativ); leer = <see cref="TrafficTestPlan.DefaultTouchdownM"/>.</param>
public sealed record TrafficTestRequest(
    string Airport,
    string Runway,
    TrafficTestVariant Variant,
    string? Title = null,
    string? Livery = null,
    double? SpawnNm = null,
    AiGearMethod Gear = AiGearMethod.All,
    double? FinalLowFt = null,
    double? TouchdownM = null);

/// <summary>Zeitpunkt und Text eines Ereignisses der Testlinie.</summary>
public sealed record TrafficTestEvent(double Seconds, string Text);

/// <summary>Letzter Messpunkt eines laufenden Tests.</summary>
/// <param name="PitchDeg">Längsneigung, Nase hoch positiv.</param>
public sealed record TrafficTestLast(
    double Seconds,
    double AltitudeFt,
    double AltitudeAglFt,
    double IndicatedKt,
    double TargetKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    double PitchDeg,
    double BankDeg,
    bool OnGround,
    double GearExtendedPercent,
    double? DistanceToThresholdNm,
    double AlongM,
    double CrossM,
    string? AiState,
    string Lights);

/// <summary>Zustand eines Testanflugs für Tool und Browser.</summary>
/// <param name="State">running oder ended.</param>
/// <param name="Tail">Kennzeichen des Testflugzeugs (DUO01 bis DUO04).</param>
/// <param name="Type">ICAO-Typ laut Freigabeliste, sonst null.</param>
/// <param name="ModelStatus">verified, candidate, excluded oder unknown (<see cref="TrafficModels"/>).</param>
public sealed record TrafficTestStatus(
    string State,
    string Tail,
    string Variant,
    string Airport,
    string Runway,
    string Title,
    string? Livery,
    string? Type,
    string ModelStatus,
    string Gear,
    double FinalLowFt,
    uint ObjectId,
    DateTimeOffset StartedUtc,
    double ElapsedSeconds,
    string? EndReason,
    TrafficTestLast? Last,
    TrafficTestSummary Summary,
    IReadOnlyList<TrafficTestEvent> Events,
    IReadOnlyList<TrafficTestWaypoint> Waypoints,
    string? CsvFile,
    string? SummaryFile,
    string? Note);

/// <summary>
/// Testwerkzeug für eigenen KI-Verkehr, Stufe A der Testlinie (docs/research/eigener-verkehr.md): NonATC-Flugzeuge fliegen einen
/// Endanflug über Wegpunkte, landen und halten auf der Bahn. A1 mit Fahrwerk ab dem Spawn, A2 mit Fahrwerk erst bei 3000 ft über der
/// Bahn, damit es bei 2500 ft draußen ist.
///
/// Bis zu <see cref="MaxConcurrent"/> Tests gleichzeitig, je Bahn einer (z. B. Parallelanflug auf 05L und 05R); ein neuer Test auf
/// derselben Bahn ersetzt den alten. Messpunkte (1 Hz) gehen je Test als CSV nach traffic-tests\ im Datenordner, die Auswertung als
/// JSON daneben. Die Flugzeuge werden beim Stoppen, nach <see cref="TrafficTestOptions.MaxMinutes"/> und beim Beenden von Duopilo
/// entfernt. Standardmäßig aus (Duopilo:TrafficTest:Enabled), weil es Objekte im Sim erzeugt.
/// </summary>
public sealed class TrafficTestService : IHostedService, IDisposable
{
    /// <summary>Kennzeichen (ATC ID) des ersten Testflugzeugs, die weiteren DUO02 bis DUO04 – so tauchen sie auch in get_traffic auf.</summary>
    public const string TailNumber = "DUO01";

    /// <summary>Gleichzeitige Tests, je Bahn einer.</summary>
    public const int MaxConcurrent = 4;

    /// <summary>Weiter weg lehnt der Sim neue Objekte ohnehin ab (Bereich um den Spieler).</summary>
    public const double MaxDistanceNm = 80;

    /// <summary>Beendete Tests, die der Status noch zeigt (die jüngsten).</summary>
    private const int KeepEnded = 8;

    private static readonly TimeSpan NoDataAfter = TimeSpan.FromSeconds(30);

    private readonly SimConnectClient _sim;
    private readonly DuopiloOptions _options;
    private readonly ILogger<TrafficTestService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly List<TrafficTestRun> _runs = [];

    public TrafficTestService(SimConnectClient sim, IOptions<DuopiloOptions> options, ILogger<TrafficTestService> log)
    {
        _sim = sim;
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => _options.TrafficTest.Enabled;

    public string Directory => Path.Combine(_options.ResolveDataDirectory(), "traffic-tests");

    /// <summary>Kennzeichen des <paramref name="slot"/>-ten Testflugzeugs (0 = DUO01).</summary>
    public static string Tail(int slot) => string.Create(CultureInfo.InvariantCulture, $"DUO{slot + 1:00}");

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
    /// Startet einen Testanflug; ein laufender auf derselben Bahn wird vorher beendet. Wirft <see cref="InvalidOperationException"/>
    /// mit einem Text für den Piloten, wenn etwas fehlt (aus, kein Flug, Flughafen oder Bahn unbekannt, zu weit weg, kein Modell,
    /// schon <see cref="MaxConcurrent"/> Tests auf anderen Bahnen).
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

            // Ein Test je Bahn: den alten auf dieser Bahn ersetzen, auf anderen Bahnen höchstens MaxConcurrent
            foreach (var old in Running().Where(r => r.SameRunway(runway)))
            {
                await StopCoreAsync(old, "replaced");
            }

            var running = Running();
            if (running.Count >= MaxConcurrent)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.tooMany", MaxConcurrent));
            }

            var slot = Enumerable.Range(0, MaxConcurrent).First(i => running.All(r => r.Slot != i));
            var (title, livery) = await ResolveModelAsync(request, ct);
            var plan = TrafficTestPlan.Build(runway, request.Variant, request.SpawnNm, request.TouchdownM, request.FinalLowFt);
            var spawnUtc = DateTimeOffset.UtcNow;
            var csv = OpenCsv(plan, spawnUtc);

            uint objectId;
            try
            {
                objectId = await _sim.CreateNonAtcAircraftAsync(title, livery, Tail(slot), plan.Spawn, ct);
            }
            catch
            {
                csv?.Writer.Dispose();
                throw;
            }

            var run = new TrafficTestRun(plan, slot, title, livery, request.Gear, objectId, spawnUtc, csv?.Path, csv?.Writer, _options.TrafficTest.MaxMinutes);
            run.AddEvent(0, string.Create(CultureInfo.InvariantCulture, $"spawn {plan.SpawnNm:0.#} NM, {plan.Spawn.AltitudeFt:0} ft, {plan.Spawn.AirspeedKt} kt, object {objectId}, {run.Tail}"));
            foreach (var warning in plan.Warnings)
            {
                run.AddEvent(0, "warning:" + warning);
            }

            // Gesperrte (FBW, Headwind) und nicht freigegebene Modelle gehen im Test trotzdem, aber mit Hinweis
            if (TrafficModels.ExcludedBy(title, livery) is { } excludedBy)
            {
                run.AddEvent(0, "warning:excludedModel " + excludedBy);
            }
            else if (TrafficModels.TypeOf(title, livery) is null)
            {
                run.AddEvent(0, "warning:notApproved");
            }

            lock (_lock)
            {
                _runs.RemoveAll(r => r.EndReason is not null && r.SameRunway(runway));
                _runs.Add(run);
                var ended = _runs.Where(r => r.EndReason is not null).ToList();
                foreach (var stale in ended.Take(Math.Max(0, ended.Count - KeepEnded)))
                {
                    _runs.Remove(stale);
                }
            }

            try
            {
                if (plan.GearAtSpawn)
                {
                    await _sim.SetAiGearAsync(objectId, true, request.Gear, ct);
                    run.GearCommands++;
                    run.AddEvent(0, "gear_down_sent " + GearName(request.Gear));
                }

                await _sim.SetAiLightsAsync(objectId, TrafficLights.Approach, ct);
                run.AddEvent(0, "lights_sent approach");

                await _sim.SetAiWaypointsAsync(objectId, [.. plan.Waypoints.Select(w => w.Waypoint)], ct);
                run.AddEvent(0, string.Create(CultureInfo.InvariantCulture, $"waypoints_sent {plan.Waypoints.Count}"));
                _sim.StartAiProbe(objectId, OnSample);
            }
            catch
            {
                // Halb eingerichtetes Flugzeug nicht stehen lassen
                await StopCoreAsync(run, "setup_failed");
                throw;
            }

            _log.LogInformation(Loc.Log("log.trafficTest.started"), plan.Variant, airport, runway.Designation, title, objectId);
            lock (_lock)
            {
                return Status(run);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Beendet laufende Tests: Abfrage stoppen, Flugzeug entfernen, Auswertung schreiben. Mit <paramref name="runway"/> nur den Test
    /// auf dieser Bahn ("05L" oder "EDDL 05L"), sonst alle. Gibt den Stand aller Tests zurück.
    /// </summary>
    public async Task<IReadOnlyList<TrafficTestStatus>> StopTestAsync(string reason, CancellationToken ct, string? runway = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var run in Running().Where(r => r.Matches(runway)))
            {
                await StopCoreAsync(run, reason);
            }

            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Alle Tests (laufende und die zuletzt beendeten), in Startreihenfolge; mit <paramref name="runway"/> nur die dieser Bahn.</summary>
    public IReadOnlyList<TrafficTestStatus> Status(string? runway = null)
    {
        lock (_lock)
        {
            return [.. _runs.Where(r => r.Matches(runway)).Select(Status)];
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

    /// <summary>Name der Fahrwerks-Methode wie im Tool-Parameter (all, down, set, handle).</summary>
    public static string GearName(AiGearMethod method) => method.ToString().ToLowerInvariant();

    /// <summary>
    /// Standardmodell: ein A320 aus dem FSLTL-Paket, sonst ein geprüftes Modell der Freigabeliste (in deren Reihenfolge), sonst
    /// irgendein A320 – nie ein gesperrtes (<see cref="TrafficModels"/>).
    /// </summary>
    public static AiModel? PickDefaultModel(IReadOnlyList<AiModel> models)
    {
        var usable = models.Where(m => !TrafficModels.IsExcluded(m.Title, m.Livery)).ToList();
        var verified = TrafficModels.Approved
            .Where(t => t.Status == TrafficModelStatus.Verified)
            .SelectMany(t => t.Titles)
            .Select(title => usable.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null);
        return usable.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase) && m.Title.Contains("FSLTL", StringComparison.OrdinalIgnoreCase))
            ?? verified
            ?? usable.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ intern

    private List<TrafficTestRun> Running()
    {
        lock (_lock)
        {
            return [.. _runs.Where(r => r.EndReason is null)];
        }
    }

    private TrafficTestStatus Status(TrafficTestRun run)
    {
        var now = DateTimeOffset.UtcNow;
        string? note = null;
        if (run.EndReason is null && (now - (run.LastSampleUtc ?? run.SpawnUtc)) > NoDataAfter)
        {
            note = Loc.T("tools.trafficTest.noData", (int)NoDataAfter.TotalSeconds);
        }

        var plan = run.Analysis.Plan;
        return new TrafficTestStatus(
            run.EndReason is null ? "running" : "ended",
            run.Tail,
            plan.Variant.ToString(),
            plan.Runway.Airport,
            plan.Runway.Designation,
            run.Title,
            run.Livery.Length > 0 ? run.Livery : null,
            TrafficModels.TypeOf(run.Title, run.Livery)?.IcaoType,
            TrafficModels.StatusName(run.Title, run.Livery),
            GearName(run.GearMethod),
            plan.FinalLowFt,
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

    /// <summary>Messpunkt vom SimConnect-Thread: auswerten, ins CSV schreiben, Fahrwerk und Zeitlimit.</summary>
    private void OnSample(AiProbeSample sample)
    {
        TrafficTestRun run;
        TrafficTestPoint point;
        lock (_lock)
        {
            if (_runs.FirstOrDefault(r => r.EndReason is null && r.ObjectId == sample.ObjectId) is not { } current)
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
                Math.Round(sample.PitchDeg, 1),
                Math.Round(sample.BankDeg, 1),
                sample.OnGround,
                Math.Round(sample.GearExtendedPercent),
                point.DistanceToThresholdNm,
                Math.Round(point.AlongM),
                Math.Round(point.CrossM, 1),
                sample.AiState,
                AiLights.Describe(sample.LightStates));
            foreach (var e in point.Events)
            {
                run.AddEvent(point.Seconds, e);
            }

            WriteCsv(run, sample, point);
        }

        var plan = run.Analysis.Plan;

        // A2: Fahrwerk erst im Anflug ausfahren – bei 3000 ft über der Bahn, spätestens am Punkt des Gleitpfads in dieser Höhe
        var heightFt = sample.AltitudeFt - plan.Runway.ElevationFt;
        if (plan.GearDownFt is { } gearFt && run.GearCommands == 0 && !sample.OnGround
            && (heightFt <= gearFt || (plan.GearDownNm is { } gearNm && point.DistanceToThresholdNm is { } d && d <= gearNm)))
        {
            run.GearCommands++;
            run.GearSentSeconds = point.Seconds;
            var distance = point.DistanceToThresholdNm ?? 0;
            run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"gear_down_sent {GearName(run.GearMethod)} at {heightFt:0} ft, {distance:0.0} NM"));
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

        // Lichter einmal nachschicken, falls das Modell sie beim Spawn noch nicht angenommen hat
        if (!run.LightsChecked && point.Seconds >= 5 && !sample.OnGround)
        {
            run.LightsChecked = true;
            if (!TrafficLights.Approach.MatchedBy(sample.LightStates))
            {
                run.AddEvent(point.Seconds, "lights_resent approach (were: " + AiLights.Describe(sample.LightStates) + ")");
                Fire(_sim.SetAiLightsAsync(run.ObjectId, TrafficLights.Approach, CancellationToken.None), "lights");
            }
        }

        // Nach dem Aufsetzen beim Abbremsen: Landelicht und Strobes aus, Taxilicht an
        if (!run.RolloutLightsSent && run.Analysis.TouchedDown && sample.OnGround && sample.GroundSpeedKt < TrafficLights.RolloutBelowKt)
        {
            run.RolloutLightsSent = true;
            run.AddEvent(point.Seconds, "lights_sent rollout");
            Fire(_sim.SetAiLightsAsync(run.ObjectId, TrafficLights.Rollout, CancellationToken.None), "lights");
        }

        if (point.Seconds > run.MaxMinutes * 60 && !run.StopScheduled)
        {
            run.StopScheduled = true;
            var designation = run.Analysis.Plan.Runway.Airport + " " + run.Analysis.Plan.Runway.Designation;
            _ = Task.Run(() => StopTestAsync("timeout", CancellationToken.None, designation));
        }
    }

    private async Task StopCoreAsync(TrafficTestRun run, string reason)
    {
        lock (_lock)
        {
            if (run.EndReason is not null)
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

        _log.LogInformation(Loc.Log("log.trafficTest.ended"), run.Analysis.Plan.Runway.Designation, reason, summary.Verdict, summary.Samples);
    }

    private (string Path, StreamWriter Writer)? OpenCsv(TrafficTestPlan plan, DateTimeOffset spawnUtc)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = string.Create(CultureInfo.InvariantCulture, $"{spawnUtc:yyyyMMdd-HHmmss}-{plan.Runway.Airport}{plan.Runway.Designation}-{plan.Variant}.csv");
            var path = Path.Combine(Directory, name);
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine("utc,t_s,lat,lon,alt_ft,agl_ft,ias_kt,target_kt,gs_kt,vs_fpm,hdg_true,pitch_deg,bank_deg,on_ground,gear_handle,gear_total_pct,gear_center_pct,gear_left_pct,gear_right_pct,lights,ai_state,along_m,cross_m,dist_thr_nm,events");
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
                $"{s.Utc:O},{p.Seconds:0.0},{s.Latitude:0.000000},{s.Longitude:0.000000},{s.AltitudeFt:0},{s.AltitudeAglFt:0},{s.IndicatedKt:0},{p.TargetKt:0},{s.GroundSpeedKt:0},{s.VerticalSpeedFpm:0},{s.HeadingTrueDeg:0},{s.PitchDeg:0.0},{s.BankDeg:0.0},{(s.OnGround ? 1 : 0)},{(s.GearHandleDown ? 1 : 0)},{s.GearTotalPercent:0.0},{s.GearCenterPercent:0.0},{s.GearLeftPercent:0.0},{s.GearRightPercent:0.0},{Csv(AiLights.Describe(s.LightStates))},{Csv(s.AiState)},{p.AlongM:0},{p.CrossM:0.0},{p.DistanceToThresholdNm:0.00},{Csv(string.Join(' ', p.Events))}"));
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
                    tail = run.Tail,
                    airport = plan.Runway.Airport,
                    runway = plan.Runway.Designation,
                    runwayHeadingTrue = Math.Round(plan.Runway.HeadingTrueDeg, 1),
                    runwayLengthM = Math.Round(plan.Runway.LengthM),
                    runwayElevationFt = Math.Round(plan.Runway.ElevationFt),
                    title = run.Title,
                    type = TrafficModels.TypeOf(run.Title, run.Livery)?.IcaoType,
                    modelStatus = TrafficModels.StatusName(run.Title, run.Livery),
                    livery = run.Livery,
                    gear = GearName(run.GearMethod),
                    spawnUtc = run.SpawnUtc,
                    endReason = run.EndReason,
                    plan = new { plan.SpawnNm, plan.GearAtSpawn, plan.GearDownFt, plan.GearDownNm, plan.TouchdownM, plan.StopM, plan.FinalLowFt, waypoints = plan.Waypoints },
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
        int slot,
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

        public int Slot { get; } = slot;

        public string Tail { get; } = TrafficTestService.Tail(slot);

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

        /// <summary>Ob nach dem Spawn geprüft wurde, dass die Anfluglichter an sind.</summary>
        public bool LightsChecked { get; set; }

        public bool RolloutLightsSent { get; set; }

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

        public bool SameRunway(RunwayEnd runway) =>
            string.Equals(Analysis.Plan.Runway.Airport, runway.Airport, StringComparison.OrdinalIgnoreCase)
            && AirportRunway.Normalize(Analysis.Plan.Runway.Designation) == AirportRunway.Normalize(runway.Designation);

        /// <summary>Passt zu "05L", "5L" oder "EDDL 05L"; leer passt immer.</summary>
        public bool Matches(string? runway) => TrafficTestService.Matches(Analysis.Plan.Runway, runway);

        public void AddEvent(double seconds, string text)
        {
            lock (_events)
            {
                _events.Add(new TrafficTestEvent(seconds, text));
            }
        }
    }

    /// <summary>
    /// Ob <paramref name="filter"/> die Bahn <paramref name="runway"/> meint: "05L"/"5L" (Bahn an jedem Flughafen), "EDDL 05L" oder
    /// "EDDL" (alle Bahnen dort); leer passt immer.
    /// </summary>
    public static bool Matches(RunwayEnd runway, string? filter)
    {
        var text = (filter ?? "").Trim();
        if (text.Length == 0)
        {
            return true;
        }

        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        if (first.Length == 4 && char.IsLetter(first[0]) && first.All(char.IsLetterOrDigit))
        {
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            return string.Equals(first, runway.Airport, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == 0 || AirportRunway.Normalize(rest) == AirportRunway.Normalize(runway.Designation));
        }

        return AirportRunway.Normalize(text) == AirportRunway.Normalize(runway.Designation);
    }
}
