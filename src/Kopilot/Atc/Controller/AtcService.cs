using System.Threading.Channels;
using Kopilot.Atc.Airspace;
using Kopilot.FlightPlan;
using Kopilot.Sim;
using Microsoft.Extensions.Options;

namespace Kopilot.Atc;

/// <summary>Spricht ATC-Funksprüche mit der Stimme der Station (in Kopilot: die Sprachausgabe der Tray-Instanz).</summary>
public interface IAtcRadio
{
    Task TransmitAsync(AtcTransmission transmission, CancellationToken ct);
}

/// <summary>Antwort auf einen Pilotenspruch.</summary>
public sealed record AtcTransmitResult(bool Ok, string? Error, IReadOnlyList<AtcTransmission> Responses);

/// <summary>
/// Hält die ATC-Sitzung des aktuellen Flugs: lädt atc-templates.json, baut den Plan aus dem SimBrief-OFP, taktet die
/// Zustandsmaschine mit den SimConnect-Werten, holt Frequenzen über die Facilities-API und gibt die Funksprüche an die
/// Sprachausgabe. Eine Sitzung beginnt mit dem ersten Pilotenspruch (oder atc_control start).
/// Die Sitzung wird laufend gesichert (atc-session.json) und nach einem Neustart oder Absturz von Kopilot automatisch
/// fortgesetzt, sobald MSFS verbunden ist und derselbe SimBrief-Plan geladen ist.
/// </summary>
public sealed class AtcService : BackgroundService
{
    private readonly KopilotOptions _options;
    private readonly SimConnectClient _sim;
    private readonly SimBriefService _simBrief;
    private readonly IServiceProvider _services;
    private readonly ILogger<AtcService> _log;
    private readonly object _lock = new();
    private readonly AtcFrequencyBook _frequencies = new();
    private readonly Dictionary<string, DateTimeOffset> _frequencyAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<AtcTransmission> _radioQueue = Channel.CreateUnbounded<AtcTransmission>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _airspaceLock = new();

    private AtcTemplates? _templates;
    private string? _templatesPath;
    private string? _templatesBasePath;
    private IReadOnlyList<string> _templateErrors = [];
    private IReadOnlyList<string> _templateNotes = [];
    private AirspaceDatabase? _airspace;
    private IReadOnlyList<string> _airspaceMessages = [];
    private readonly AtcSessionStore _store;

    /// <summary>Spätestens so oft sichern, auch ohne Zustandswechsel (Streckenposition, Funkverlauf).</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);

    private AtcSession? _session;
    private string? _sessionOfpId;
    private string? _lastError;
    private string? _savedFingerprint;
    private DateTimeOffset _savedUtc;
    private bool _autoRestoreDone;
    private DateTimeOffset? _restoredFromUtc;

    public AtcService(IOptions<KopilotOptions> options, SimConnectClient sim, SimBriefService simBrief, IServiceProvider services, ILogger<AtcService> log)
    {
        _options = options.Value;
        _sim = sim;
        _simBrief = simBrief;
        _services = services;
        _log = log;
        _store = AtcSessionStore.From(_options);
        ReloadTemplates();
    }

    public bool Enabled => _options.Atc.Enabled;

    /// <summary>Eigene Kopie der Vorlagen, sonst die mitgelieferte Datei.</summary>
    public string? TemplatesPath => _templatesPath;

    /// <summary>Mitgelieferte Grundlage, über die die eigene Kopie gelegt wird.</summary>
    public string? TemplatesBasePath => _templatesBasePath;

    public IReadOnlyList<string> TemplateErrors => _templateErrors;

    /// <summary>Was beim Laden angepasst wurde (Formatwechsel der eigenen Kopie).</summary>
    public IReadOnlyList<string> TemplateNotes => _templateNotes;

    /// <summary>Herkunft der Luftraumdaten und Rufzeichen (für den Status).</summary>
    public IReadOnlyList<string> AirspaceSources
    {
        get
        {
            var airspace = _airspace;
            var airlines = _templates?.Airlines;
            var sources = new List<string>(airspace?.Sources ?? []);
            if (airlines is { Count: > 0 })
            {
                sources.Add($"Airlines: {airlines.Source} ({airlines.Count} Rufzeichen, OpenFlights)");
            }

            sources.AddRange(_airspaceMessages);
            return sources;
        }
    }

    public AtcFrequencyBook Frequencies => _frequencies;

    /// <summary>Datei der Sitzungssicherung (null = aus).</summary>
    public string? SessionFile => _store.FilePath;

    /// <summary>Zuletzt gesichert.</summary>
    public DateTimeOffset? SessionSavedUtc => _store.LastSavedUtc;

    /// <summary>Fehler beim Sichern oder Laden der Sitzung.</summary>
    public string? SessionStoreError => _store.LastError;

    /// <summary>Die laufende Sitzung wurde aus einer Sicherung von diesem Zeitpunkt fortgesetzt (null = neu begonnen).</summary>
    public DateTimeOffset? RestoredFromUtc => _restoredFromUtc;

    /// <summary>Kann ATC sprechen (Tray-Instanz mit Sprachausgabe)?</summary>
    public bool HasRadio => _services.GetService<IAtcRadio>() is not null && _options.Atc.Speak;

    /// <summary>Zugriff auf die laufende Sitzung unter der Sperre (für Status und Tools).</summary>
    public T WithSession<T>(Func<AtcSession?, AtcInput, T> read)
    {
        lock (_lock)
        {
            return read(_session, BuildInput());
        }
    }

    /// <summary>
    /// Für die Sprachschleife: Stationsnamen und eigenes Rufzeichen (Code und Sprechform) des laufenden bzw. geplanten Flugs,
    /// damit sie Funksprüche an ATC vom Kopilot-Befehl unterscheiden kann.
    /// </summary>
    public AtcRoutingInfo RoutingInfo()
    {
        var simCallsign = SimCallsign();
        lock (_lock)
        {
            return AtcRoutingInfo.From(_templates, _session, _simBrief.Current, simCallsign);
        }
    }

    /// <summary>
    /// Vorlagen, Airline-Datenbank und Luftraumdaten neu laden. Die mitgelieferte atc-templates.json neben der Kopilot.exe
    /// ist die Grundlage, eine eigene Kopie in DataDirectory wird darübergelegt. Die Luftraumdaten lädt Kopilot im
    /// Hintergrund nach (beim ersten Bedarf spätestens).
    /// </summary>
    public IReadOnlyList<string> ReloadTemplates()
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var (basePath, userPath) = ResolveTemplatesPaths();
        AtcTemplates? templates = null;
        if (basePath is null && userPath is null)
        {
            errors.Add($"{AtcTemplateLoader.FileName} nicht gefunden.");
        }
        else
        {
            templates = AtcTemplateLoader.Load(basePath, userPath, errors, notes);
        }

        var airlineMessages = new List<string>();
        if (templates is not null)
        {
            templates.Airlines = AirspaceLoader.LoadAirlines(AirspaceLoader.Roots(_options.ResolveDataDirectory()), airlineMessages);
        }

        lock (_lock)
        {
            _templatesPath = userPath ?? basePath;
            _templatesBasePath = basePath;
            _templateErrors = errors;
            _templateNotes = notes;
            _templates = templates;
            _session = null;
            _sessionOfpId = null;
        }

        lock (_airspaceLock)
        {
            _airspace = null;
            _airspaceMessages = airlineMessages;
        }

        foreach (var note in notes)
        {
            _log.LogInformation("ATC-Vorlagen: {Note}", note);
        }

        if (errors.Count > 0)
        {
            _log.LogWarning("ATC-Vorlagen ({Path}): {Errors}", userPath ?? basePath ?? AtcTemplateLoader.FileName, string.Join(" | ", errors));
        }
        else
        {
            _log.LogInformation(
                "ATC-Vorlagen geladen: {Path}{Base} ({Count} Funksprüche, {Airlines} Airlines aus OpenFlights)",
                userPath ?? basePath,
                userPath is not null && basePath is not null ? $" über {basePath}" : "",
                templates?.Messages.Count ?? 0,
                templates?.Airlines.Count ?? 0);
        }

        return errors;
    }

    /// <summary>FIR-Grenzen und Sektoren – beim ersten Aufruf geladen (ein bis zwei Sekunden), danach aus dem Speicher.</summary>
    public AirspaceDatabase Airspace()
    {
        lock (_airspaceLock)
        {
            if (_airspace is { } loaded)
            {
                return loaded;
            }

            var messages = new List<string>();
            var started = DateTimeOffset.UtcNow;
            var airspace = AirspaceLoader.Load(AirspaceLoader.Roots(_options.ResolveDataDirectory()), messages);
            _airspace = airspace;
            _airspaceMessages = _airspaceMessages.Concat(messages).ToList();
            foreach (var message in messages)
            {
                _log.LogWarning("Luftraumdaten: {Message}", message);
            }

            _log.LogInformation(
                "Luftraumdaten geladen in {Ms} ms: {Sources}",
                (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                string.Join(" | ", airspace.Sources));
            return airspace;
        }
    }

    /// <summary>Funkspruch des Piloten. Startet bei Bedarf die Sitzung.</summary>
    public async Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AtcTransmitResult(false, "Kein Funkspruch übergeben.", []);
        }

        if (await EnsureSessionAsync(ct) is { } error)
        {
            return new AtcTransmitResult(false, error, []);
        }

        IReadOnlyList<AtcTransmission> responses;
        lock (_lock)
        {
            if (_session is null)
            {
                return new AtcTransmitResult(false, _lastError ?? "ATC-Sitzung nicht verfügbar.", []);
            }

            responses = _session.PilotTransmission(text, BuildInput());
        }

        Broadcast(responses);
        SaveSession(force: true);
        return new AtcTransmitResult(true, null, responses);
    }

    /// <summary>start, stop, restart, reload oder say_again.</summary>
    public async Task<string> ControlAsync(string action, CancellationToken ct)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                return await EnsureSessionAsync(ct) ?? "ATC-Sitzung läuft.";
            case "restart":
                lock (_lock)
                {
                    _session = null;
                    _sessionOfpId = null;
                }

                // Neu heißt neu: nicht aus der Sicherung fortsetzen
                _store.Delete();
                return await EnsureSessionAsync(ct) ?? "ATC-Sitzung neu gestartet.";
            case "stop":
                lock (_lock)
                {
                    _session = null;
                    _sessionOfpId = null;
                    _restoredFromUtc = null;
                }

                _store.Delete();
                return "ATC-Sitzung beendet.";
            case "reload":
                var errors = ReloadTemplates();
                return errors.Count == 0 ? $"Vorlagen neu geladen: {_templatesPath}" : "Vorlagen mit Fehlern: " + string.Join(" | ", errors);
            case "say_again":
                var result = await TransmitAsync("say again", ct);
                return result.Ok ? "Letzter Funkspruch wiederholt." : result.Error ?? "Nichts zu wiederholen.";
            default:
                return "Unbekannte Aktion. Erlaubt: start, stop, restart, reload, say_again.";
        }
    }

    public string SetValue(string name, string? value)
    {
        var key = AtcSession.OverridableValues.FirstOrDefault(v => v.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return "Unbekannter Wert. Erlaubt: " + string.Join(", ", AtcSession.OverridableValues);
        }

        lock (_lock)
        {
            if (_session is null)
            {
                return "Keine ATC-Sitzung – zuerst einen Funkspruch senden oder atc_control start.";
            }

            _session.SetOverride(key, value);
        }

        SaveSession(force: true);
        return string.IsNullOrWhiteSpace(value) ? $"{key} zurückgesetzt." : $"{key} = {value.Trim()}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _log.LogInformation("ATC ist ausgeschaltet (Kopilot:Atc:Enabled).");
            return;
        }

        var radio = RadioLoopAsync(stoppingToken);
        _ = Task.Run(() =>
        {
            try
            {
                Airspace();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Luftraumdaten nicht geladen");
            }
        }, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await AutoRestoreAsync(stoppingToken);
                Tick();
                SaveSession(force: false);
                _ = PrefetchFrequenciesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }

        _radioQueue.Writer.TryComplete();
        await radio.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Tick()
    {
        if (!_sim.Connection.IsConnected || _sim.Status.Paused)
        {
            return;
        }

        IReadOnlyList<AtcTransmission> output = [];
        lock (_lock)
        {
            if (_session is null)
            {
                return;
            }

            // Neuer SimBrief-Plan = neuer Flug: alte Sitzung endet
            if (_simBrief.Current is { } ofp && ofp.RequestId is not null && _sessionOfpId is not null && ofp.RequestId != _sessionOfpId)
            {
                _log.LogInformation("Neuer SimBrief-Plan – ATC-Sitzung des alten Flugs beendet.");
                _session = null;
                _sessionOfpId = null;
                return;
            }

            try
            {
                output = _session.Tick(BuildInput());
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Fehler in der ATC-Zustandsmaschine");
            }
        }

        Broadcast(output);
    }

    /// <summary>
    /// Nach dem Start von Kopilot einmal: Gibt es eine gesicherte Sitzung, die nicht zu alt ist, wird sie fortgesetzt, sobald
    /// MSFS verbunden ist – ohne dass der Pilot erst funken muss. Eine Sicherung eines anderen Flugs bleibt liegen.
    /// </summary>
    private async Task AutoRestoreAsync(CancellationToken ct)
    {
        if (_autoRestoreDone || !_sim.Connection.IsConnected)
        {
            return;
        }

        _autoRestoreDone = true;
        lock (_lock)
        {
            if (_session is not null)
            {
                return;
            }
        }

        if (RestorableSnapshot(null) is null)
        {
            return;
        }

        try
        {
            if (await EnsureSessionAsync(ct, restoreOnly: true) is { } error)
            {
                _log.LogInformation("ATC-Sitzung nicht fortgesetzt: {Reason}", error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("ATC-Sitzung nicht fortgesetzt: {Message}", ex.Message);
        }
    }

    /// <summary>Sicherung, die zu diesem Flug passt und nicht zu alt ist (flightKey null = nur Alter und Version prüfen).</summary>
    private AtcSessionSnapshot? RestorableSnapshot(string? flightKey)
    {
        if (_store.Load() is not { } snapshot)
        {
            if (_store.LastError is { } error)
            {
                _log.LogWarning("ATC-Sitzungssicherung: {Error}", error);
            }

            return null;
        }

        if (snapshot.Phase is AtcPhase.Inactive or AtcPhase.Done)
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - snapshot.SavedUtc;
        if (age > TimeSpan.FromHours(Math.Max(0.1, _options.Atc.SessionMaxAgeHours)))
        {
            _log.LogInformation("ATC-Sitzungssicherung von {Saved:u} ist zu alt ({Hours:0.0} h) – neue Sitzung.", snapshot.SavedUtc, age.TotalHours);
            return null;
        }

        if (flightKey is not null && !string.Equals(snapshot.FlightKey, flightKey, StringComparison.Ordinal))
        {
            _log.LogInformation("ATC-Sitzungssicherung gehört zu einem anderen Flug ({Saved}) – neue Sitzung.", snapshot.FlightKey);
            return null;
        }

        return snapshot;
    }

    /// <summary>
    /// Sitzung sichern, wenn sich etwas geändert hat (<paramref name="force"/>: nach jedem Funkspruch) oder spätestens alle 60 s.
    /// </summary>
    private void SaveSession(bool force)
    {
        if (_store.FilePath is null)
        {
            return;
        }

        AtcSessionSnapshot snapshot;
        string fingerprint;
        lock (_lock)
        {
            if (_session is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            fingerprint = _session.StateFingerprint();
            if (!force && fingerprint == _savedFingerprint && now - _savedUtc < SaveInterval)
            {
                return;
            }

            snapshot = _session.Snapshot(now);
            _savedFingerprint = fingerprint;
            _savedUtc = now;
        }

        if (!_store.Save(snapshot) && _store.LastError is { } error)
        {
            _log.LogWarning("ATC-Sitzung: {Error}", error);
        }
    }

    /// <summary>Sitzung anlegen, falls keine läuft. Rückgabe: Fehlermeldung oder null.</summary>
    /// <param name="ct">Abbruch.</param>
    /// <param name="restoreOnly">Nur aus einer passenden Sicherung fortsetzen, sonst keine Sitzung anlegen (Autostart).</param>
    private async Task<string?> EnsureSessionAsync(CancellationToken ct, bool restoreOnly = false)
    {
        if (!Enabled)
        {
            return "ATC ist ausgeschaltet (Kopilot:Atc:Enabled).";
        }

        lock (_lock)
        {
            if (_session is not null)
            {
                return null;
            }

            if (_templates is null)
            {
                return "ATC-Vorlagen fehlen oder sind ungültig: " + string.Join(" | ", _templateErrors);
            }
        }

        var ofp = _simBrief.Current ?? await _simBrief.GetAsync(refresh: false, ct);
        if (ofp is null)
        {
            _lastError = _simBrief.LastError ?? "Kein SimBrief-Plan geladen.";
            return "ATC braucht den SimBrief-Plan: " + _lastError;
        }

        var airspace = await Task.Run(() => Airspace(), ct);

        lock (_lock)
        {
            if (_session is not null)
            {
                return null;
            }

            var templates = _templates!;
            var plan = AtcPlan.Build(ofp, SimCallsign(), templates, airspace);
            var snapshot = RestorableSnapshot(plan.FlightKey);
            if (snapshot is null && restoreOnly)
            {
                return "keine passende Sicherung für diesen Flug.";
            }

            var context = new AtcSessionContext(
                _frequencies.Get,
                _services.GetService<ITaxiRouteProvider>() ?? new NoTaxiRouteProvider(),
                CalloutVoice(),
                InstalledVoices());
            var session = new AtcSession(templates, plan, context);
            if (snapshot is not null)
            {
                var note = session.Restore(snapshot, BuildInput());
                _restoredFromUtc = snapshot.SavedUtc;
                _log.LogInformation(
                    "ATC-Sitzung fortgesetzt (Sicherung von {Saved:HH:mm:ss}Z): {Callsign} {Orig} → {Dest}, {Station}, Phase {Phase}/{Step}, " +
                    "freigegeben {Cleared} ft, Squawk {Squawk}, {Lines} Funksprüche{Note}",
                    snapshot.SavedUtc,
                    plan.Callsign,
                    ofp.Origin.Icao,
                    ofp.Destination.Icao,
                    session.Station.Name,
                    session.Phase,
                    session.Step,
                    session.ClearedAltitudeFt,
                    session.Squawk,
                    session.Transcript.Count,
                    note is null ? "" : $" – {note}");
            }
            else
            {
                session.Start(BuildInput());
                _restoredFromUtc = null;
                _log.LogInformation(
                    "ATC-Sitzung: {Callsign} {Orig} → {Dest}, Phase {Phase}, Squawk {Squawk}{Oceanic}, Stationen: {Stations}",
                    plan.Callsign,
                    ofp.Origin.Icao,
                    ofp.Destination.Icao,
                    session.Phase,
                    session.Squawk,
                    plan.Oceanic is null ? "" : ", mit Oceanic-Teil",
                    string.Join(" → ", plan.Firs.Select(f => FormattableString.Invariant($"{session.StationName(f)} {f.FrequencyMhz:0.000} ab {f.StartNm:0} nm"))));
            }

            _session = session;
            _sessionOfpId = ofp.RequestId;
            _lastError = null;
            _savedFingerprint = null;
        }

        SaveSession(force: true);
        _ = PrefetchFrequenciesAsync(ct);
        return null;
    }

    private void Broadcast(IReadOnlyList<AtcTransmission> transmissions)
    {
        foreach (var t in transmissions)
        {
            _log.LogInformation("ATC {Station} ({Voice}): {Text}", t.StationName, t.Voice, t.Text);
            if (t.From == "atc" && HasRadio)
            {
                _radioQueue.Writer.TryWrite(t);
            }
        }
    }

    private async Task RadioLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var t in _radioQueue.Reader.ReadAllAsync(ct))
            {
                if (_services.GetService<IAtcRadio>() is not { } radio)
                {
                    continue;
                }

                try
                {
                    await radio.TransmitAsync(t, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("ATC-Funkspruch nicht gesprochen: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Beenden
        }
    }

    /// <summary>Frequenzen von Start und Ziel über die Facilities-API holen (je Flughafen einmal, nach Fehlern frühestens nach 2 Minuten erneut).</summary>
    private async Task PrefetchFrequenciesAsync(CancellationToken ct)
    {
        if (!_sim.Connection.IsConnected || _simBrief.Current is not { } ofp)
        {
            return;
        }

        foreach (var icao in new[] { ofp.Origin.Icao, ofp.Destination.Icao })
        {
            if (string.IsNullOrWhiteSpace(icao) || _frequencies.Has(icao))
            {
                continue;
            }

            lock (_frequencyAttempts)
            {
                if (_frequencyAttempts.TryGetValue(icao, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2))
                {
                    continue;
                }

                _frequencyAttempts[icao] = DateTimeOffset.UtcNow;
            }

            try
            {
                var list = await _sim.RequestAirportFrequenciesAsync(icao, ct);
                if (list.Count > 0)
                {
                    _frequencies.Set(icao, list);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("Facilities für {Icao} nicht verfügbar: {Message}", icao, ex.Message);
            }
        }
    }

    private AtcInput BuildInput() => new()
    {
        TimeUtc = DateTimeOffset.UtcNow,
        OnGround = _sim.Bool("onGround"),
        AltitudeFt = _sim.Num("altIndicated"),
        AglFt = _sim.Num("altAgl"),
        GroundSpeedKt = _sim.Num("gs"),
        IasKt = _sim.Num("ias"),
        VerticalSpeedFpm = _sim.Num("vs"),
        Lat = _sim.Num("lat"),
        Lon = _sim.Num("lon"),
        HeadingMagDeg = _sim.Num("hdgMag"),
        WindDirectionDeg = _sim.Num("windDir"),
        WindSpeedKt = _sim.Num("windKt"),
        QnhHpa = _sim.Num("qnhHpa"),
        ZuluTimeSec = _sim.Num("zuluTimeSec"),
        ParkingBrake = new AircraftSignals(_sim).ParkingBrake,
        AssignedRunway = _sim.Connection.IsConnected ? AtcReader.AssignedRunway(_sim, _simBrief.Current)?.Ident : null,
    };

    private string? SimCallsign()
    {
        var airline = _sim.Str("atcAirline");
        var number = _sim.Str("atcFlightNumber");
        return airline is not null && number is not null ? airline + number : _sim.Str("atcId");
    }

    private string? CalloutVoice() =>
        _services.GetService<Speech.ISpeechOutput>()?.Status.Settings.KokoroVoice ?? _options.Speech.KokoroVoice;

    private IReadOnlyCollection<string>? InstalledVoices()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "voices");
        var voices = Speech.KokoroSpeechEngine.ListVoices(directory);
        return voices.Count > 0 ? voices.ToList() : null;
    }

    /// <summary>
    /// Grundlage: TemplatesFile neben der Kopilot.exe. Eigene Kopie: TemplatesFile in DataDirectory (oder ein absoluter Pfad).
    /// Zeigen beide auf dieselbe Datei, gibt es nur die Grundlage.
    /// </summary>
    private (string? BasePath, string? UserPath) ResolveTemplatesPaths()
    {
        var configured = _options.Atc.TemplatesFile;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = AtcTemplateLoader.FileName;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, Path.IsPathRooted(configured) ? AtcTemplateLoader.FileName : configured);
        var basePath = File.Exists(bundled) ? bundled : null;
        var own = Path.IsPathRooted(configured) ? configured : Path.Combine(_options.ResolveDataDirectory(), configured);
        var userPath = File.Exists(own) && (basePath is null || !Path.GetFullPath(own).Equals(Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
            ? own
            : null;
        return (basePath, userPath);
    }
}
