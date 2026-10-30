using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kopilot.Aircraft;
using Kopilot.Input;
using Microsoft.Extensions.Options;
using Microsoft.FlightSimulator.SimConnect;

namespace Kopilot.Sim;

/// <summary>
/// Hält die Verbindung zu MSFS 2024 über SimConnect.
///
/// Alle SimConnect-Aufrufe laufen auf genau einem eigenen Thread (die Bibliothek ist nicht threadsicher).
/// Andere Threads (MCP-Tools) lesen nur die zwischengespeicherten Werte oder stellen Befehle in eine Warteschlange.
///
/// Jede Variable bekommt eine eigene Data Definition. Ist ein Name unbekannt, fehlt nur dieser Wert.
/// </summary>
public sealed partial class SimConnectClient : BackgroundService
{
    private const uint AdHocRequestBase = 1_000_000;
    private const uint ClientEventBase = 10_000;
    private const uint StateRequestAircraft = 900_001;
    private const uint StateRequestSim = 900_002;
    private const string TitleKey = "title";

    private readonly ILogger<SimConnectClient> _log;
    private readonly KopilotOptions _options;
    private readonly ProfileStore _profiles;

    private readonly AutoResetEvent _messageEvent = new(false);
    private readonly AutoResetEvent _commandEvent = new(false);
    private readonly ConcurrentQueue<Action<SimConnect>> _commands = new();

    // Werte für andere Threads
    private readonly ConcurrentDictionary<string, SimValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, PendingRead> _pending = new();

    // Nur auf dem SimConnect-Thread benutzt
    private SimConnect? _sc;
    private uint _nextDefId;
    private uint _nextAdHocRequest;
    private readonly Dictionary<uint, string> _valueKeyByDefId = [];
    private readonly HashSet<string> _registeredValueKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _adHocDefIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _writeDefIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private uint _nextEventId;
    private string? _lastTitle;
    private string? _lastPath;
    private string? _lastConnectError;
    private bool _quitRequested;

    // Simulator-Zustand (Pause, Flug läuft, Flugzeugpfad) – von anderen Threads gelesen
    private volatile SimStatus _simStatus = new(false, 0, null, null);

    private volatile ConnectionInfo _connection = ConnectionInfo.Disconnected;
    private volatile AircraftProfile? _activeProfile;

    public SimConnectClient(IOptions<KopilotOptions> options, ProfileStore profiles, ILogger<SimConnectClient> log)
    {
        _options = options.Value;
        _profiles = profiles;
        _log = log;
    }

    public ConnectionInfo Connection => _connection;

    public AircraftProfile? ActiveProfile => _activeProfile;

    public int ReceivingCount => _values.Count;

    public SimStatus Status => _simStatus;

    /// <summary>Schlüssel, unter dem der Wert einer beliebigen Variable abgelegt wird.</summary>
    public static string VarKey(string name, string unit) => $"var:{name.Trim()}|{unit.Trim().ToLowerInvariant()}";

    /// <summary>Letzter Fehler beim Verbindungsaufbau (z. B. fehlende SimConnect.dll), sonst null.</summary>
    public string? LastError => _lastConnectError;

    // ------------------------------------------------------------------ Lesen (beliebiger Thread)

    public SimValue? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public double? Num(string key) => Get(key)?.Value as double?;

    public bool? Bool(string key) => Num(key) is { } d ? d != 0 : null;

    public string? Str(string key) => Get(key)?.Value is string s && s.Length > 0 ? s : null;

    /// <summary>Alter des jüngsten Werts eines Schlüssels in Sekunden.</summary>
    public double? AgeSeconds(string key) =>
        Get(key) is { } v ? (DateTimeOffset.UtcNow - v.UpdatedUtc).TotalSeconds : null;

    /// <summary>
    /// Liest eine beliebige Variable einmalig (A: oder L:), z. B. zum Erkunden von L:-Variablen.
    /// </summary>
    public async Task<ReadResult> ReadOnceAsync(string name, string? unit, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            return ReadResult.Fail("Keine Verbindung zu MSFS.");
        }

        var normalized = SimVarSpec.NormalizeName(name);
        if (normalized.Length == 0)
        {
            return ReadResult.Fail("Kein Variablenname angegeben.");
        }

        var kind = SimVarSpec.KindFromUnit(unit);

        // Bekannte Text-SimVars auch ohne unit "string" als Text lesen
        if (kind == SimVarKind.Number && (string.IsNullOrWhiteSpace(unit) || unit.Trim().Equals("number", StringComparison.OrdinalIgnoreCase))
            && SimVarSpec.IsTextVariable(normalized))
        {
            kind = SimVarKind.String;
        }

        var effectiveUnit = kind == SimVarKind.String ? "" : (string.IsNullOrWhiteSpace(unit) ? "number" : unit.Trim());
        var spec = new SimVarSpec($"adhoc:{normalized}|{effectiveUnit}", normalized, effectiveUnit, kind);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(sc =>
        {
            if (!_adHocDefIds.TryGetValue(spec.Key, out var defId))
            {
                defId = DefineVariable(sc, spec);
                _adHocDefIds[spec.Key] = defId;
            }

            var requestId = AdHocRequestBase + ++_nextAdHocRequest;
            _pending[requestId] = new PendingRead(tcs, DateTimeOffset.UtcNow.AddMilliseconds(_options.ReadTimeoutMs * 2));
            sc.RequestDataOnSimObject(
                (SimRequestId)requestId,
                (SimDefinitionId)defId,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.ONCE,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ReadTimeoutMs);
        try
        {
            var value = await tcs.Task.WaitAsync(timeout.Token);
            return ReadResult.Success(value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ReadResult.Fail("Keine Antwort von MSFS – Variablenname oder Einheit vermutlich ungültig.");
        }
        catch (InvalidOperationException ex)
        {
            return ReadResult.Fail(ex.Message);
        }
    }

    // ------------------------------------------------------------------ Schreiben (nur für das Eingabe-Modul)

    /// <summary>
    /// Schickt ein Event an das eigene Flugzeug: Standard-Event ("GEAR_DOWN") oder Custom-Event ("A32NX.FCU_SPD_SET").
    /// Negative Werte werden als Zweierkomplement übergeben. Gesperrte Namen (Triebwerk, Feuer, Treibstoff, MCDU) werfen.
    /// </summary>
    public Task TransmitEventAsync(string name, uint data, CancellationToken ct)
    {
        var eventName = name.Trim();
        if (eventName.StartsWith("K:", StringComparison.OrdinalIgnoreCase))
        {
            eventName = eventName[2..].Trim();
        }

        ActionSafety.EnsureEventAllowed(eventName, data);
        return RunOnSimThreadAsync(sc =>
        {
            if (!_eventIds.TryGetValue(eventName, out var id))
            {
                id = ClientEventBase + ++_nextEventId;
                sc.MapClientEventToSimEvent((SimClientEventId)id, eventName);
                _eventIds[eventName] = id;
            }

            sc.TransmitClientEvent(
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                (SimClientEventId)id,
                data,
                SimGroupId.PriorityHighest,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }, ct);
    }

    /// <summary>Schreibt eine Variable (L:-Variable oder schreibbare SimVar). Gesperrte Namen werfen.</summary>
    public Task SetVariableAsync(string name, string unit, double value, CancellationToken ct)
    {
        var normalized = SimVarSpec.NormalizeName(name);
        ActionSafety.EnsureVariableAllowed(normalized);
        var effectiveUnit = string.IsNullOrWhiteSpace(unit) ? "number" : unit.Trim();
        var key = $"set:{normalized}|{effectiveUnit.ToLowerInvariant()}";
        return RunOnSimThreadAsync(sc =>
        {
            if (!_writeDefIds.TryGetValue(key, out var defId))
            {
                defId = DefineVariable(sc, new SimVarSpec(key, normalized, effectiveUnit, SimVarKind.Number));
                _writeDefIds[key] = defId;
            }

            sc.SetDataOnSimObject((SimDefinitionId)defId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
        }, ct);
    }

    /// <summary>
    /// Führt einen Befehl auf dem SimConnect-Thread aus. Läuft die Zeit ab oder wird abgebrochen, bevor der Befehl dran war,
    /// wird er verworfen – er darf nicht später noch ausgeführt werden (sonst schaltet ein Taster bei Wiederholung doppelt).
    /// </summary>
    private async Task RunOnSimThreadAsync(Action<SimConnect> action, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException("Keine Verbindung zu MSFS.");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(sc =>
        {
            if (tcs.Task.IsCompleted)
            {
                return;
            }

            try
            {
                action(sc);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _options.ReadTimeoutMs)));
        using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("MSFS hat den Befehl nicht rechtzeitig angenommen."))))
        {
            await tcs.Task;
        }
    }

    /// <summary>Profil nach dem Neuladen der Profildateien erneut zuordnen.</summary>
    public void RefreshProfile() => Enqueue(sc =>
    {
        _activeProfile = null;
        ApplyProfile(sc, force: true);
    });

    // ------------------------------------------------------------------ SimConnect-Thread

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => RunGuarded(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>
    /// Fängt Ladefehler der SimConnect-Bibliothek ab (sie treten schon beim JIT von <see cref="Run"/> auf),
    /// damit der MCP-Server trotzdem läuft und den Fehler über kopilot_status melden kann.
    /// </summary>
    private void RunGuarded(CancellationToken ct)
    {
        try
        {
            Run(ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or DllNotFoundException)
        {
            _lastConnectError = ex.Message;
            _log.LogCritical(ex, "SimConnect-Bibliothek konnte nicht geladen werden. Siehe README, Abschnitt 'SimConnect-SDK'.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Run(CancellationToken ct)
    {
        _log.LogInformation("Kopilot wartet auf Microsoft Flight Simulator …");
        var handles = new WaitHandle[] { _messageEvent, _commandEvent, ct.WaitHandle };

        while (!ct.IsCancellationRequested)
        {
            if (_sc is null && !TryConnect())
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectSeconds)));
                continue;
            }

            var signaled = WaitHandle.WaitAny(handles, 500);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (signaled == 0)
                {
                    // Für das Absturz-Journal: Ein harter Absturz beim Auspacken einer Nachricht (AccessViolation im
                    // SimConnect-Marshaling) lässt diesen Zustand stehen – beim nächsten Start sichtbar.
                    Kopilot.Hosting.CrashJournal.State("SimConnect.ReceiveMessage");
                    _sc!.ReceiveMessage();
                    Kopilot.Hosting.CrashJournal.State(null);
                }

                if (_quitRequested)
                {
                    Disconnect();
                    continue;
                }

                DrainCommands();
            }
            catch (COMException ex)
            {
                _log.LogWarning("SimConnect-Verbindung abgebrochen: {Message}", ex.Message);
                Disconnect();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unerwarteter Fehler im SimConnect-Thread – Verbindung wird neu aufgebaut.");
                Disconnect();
            }

            ExpirePendingReads();
            ExpireFacilityRequest();
        }

        Disconnect();
    }

    private bool TryConnect()
    {
        try
        {
            var sc = new SimConnect(_options.ClientName, IntPtr.Zero, 0, _messageEvent, 0);
            sc.OnRecvOpen += OnOpen;
            sc.OnRecvQuit += OnQuit;
            sc.OnRecvException += OnException;
            sc.OnRecvSimobjectData += OnData;
            sc.OnRecvEvent += OnEvent;
            sc.OnRecvEventFilename += OnEventFilename;
            sc.OnRecvSystemState += OnSystemState;
            sc.OnRecvFacilityData += OnFacilityData;
            sc.OnRecvFacilityDataEnd += OnFacilityDataEnd;
            _sc = sc;
            _lastConnectError = null;
            return true;
        }
        catch (COMException)
        {
            // MSFS läuft (noch) nicht – ganz normal, leise weiter warten.
            return false;
        }
        catch (Exception ex)
        {
            // z. B. SimConnect.dll fehlt oder Stub-Build. Nur einmal loggen, nicht alle 5 Sekunden.
            if (_lastConnectError != ex.Message)
            {
                _lastConnectError = ex.Message;
                _log.LogError(ex, "SimConnect kann nicht gestartet werden: {Message}", ex.Message);
            }

            return false;
        }
    }

    private void Disconnect()
    {
        if (_sc is not null)
        {
            try
            {
                _sc.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Fehler beim Schließen der SimConnect-Verbindung");
            }

            _sc = null;
        }

        if (_connection.IsConnected)
        {
            _log.LogInformation("Verbindung zu MSFS getrennt.");
        }

        _quitRequested = false;
        _connection = ConnectionInfo.Disconnected;
        _activeProfile = null;
        _lastTitle = null;
        _lastPath = null;
        _simStatus = new SimStatus(false, 0, null, null);
        _nextDefId = 0;
        _valueKeyByDefId.Clear();
        _registeredValueKeys.Clear();
        _adHocDefIds.Clear();
        _writeDefIds.Clear();
        _eventIds.Clear();
        _nextEventId = 0;
        _values.Clear();

        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetException(new InvalidOperationException("Verbindung zu MSFS getrennt."));
            }
        }

        while (_commands.TryDequeue(out _))
        {
            // Befehle für die alte Verbindung verwerfen
        }

        ResetFacilities();
    }

    private void Enqueue(Action<SimConnect> command)
    {
        _commands.Enqueue(command);
        _commandEvent.Set();
    }

    private void DrainCommands()
    {
        while (_sc is not null && _commands.TryDequeue(out var command))
        {
            try
            {
                command(_sc);
            }
            catch (COMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SimConnect-Befehl fehlgeschlagen");
            }
        }
    }

    private void ExpirePendingReads()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (id, pending) in _pending)
        {
            if (pending.ExpiresUtc < now && _pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetCanceled();
            }
        }
    }

    // ------------------------------------------------------------------ Registrierung

    private uint DefineVariable(SimConnect sc, SimVarSpec spec)
    {
        var defId = ++_nextDefId;
        if (spec.Kind == SimVarKind.String)
        {
            sc.AddToDataDefinition((SimDefinitionId)defId, spec.Name, null, SIMCONNECT_DATATYPE.STRING256, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<SimString256>((SimDefinitionId)defId);
        }
        else
        {
            sc.AddToDataDefinition((SimDefinitionId)defId, spec.Name, spec.Unit, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<double>((SimDefinitionId)defId);
        }

        return defId;
    }

    private void Subscribe(SimConnect sc, SimVarSpec spec)
    {
        if (!_registeredValueKeys.Add(spec.Key))
        {
            return;
        }

        var defId = DefineVariable(sc, spec);
        _valueKeyByDefId[defId] = spec.Key;

        // Request-ID = Definition-ID. Schnelle Werte jedes n-te Sim-Frame, alle anderen 1x pro Sekunde.
        sc.RequestDataOnSimObject(
            (SimRequestId)defId,
            (SimDefinitionId)defId,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            spec.Fast ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            spec.Fast ? (uint)Math.Clamp(_options.FastIntervalFrames, 0, 60) : 0,
            0);
    }

    /// <summary>Profil anhand von Titel und aircraft.cfg-Pfad wählen und seine Variablen abonnieren.</summary>
    private void ApplyProfile(SimConnect sc, bool force = false)
    {
        var title = _lastTitle;
        var path = _lastPath;
        var profile = _profiles.Match(title, path);
        if (!force && ReferenceEquals(profile, _activeProfile))
        {
            return;
        }

        _activeProfile = profile;
        if (profile is null)
        {
            _log.LogInformation("Flugzeug: {Title} ({Path}) – kein Profil", title ?? "unbekannt", path ?? "Pfad unbekannt");
            return;
        }

        foreach (var (key, binding) in profile.Bindings)
        {
            var unit = binding.EffectiveUnit;
            var fast = binding.Fast ?? BindingKeys.FastByDefault.Contains(key);

            // "{i}" = Triebwerksindex → je Triebwerk eine Variable
            var names = binding.Name.Contains("{i}", StringComparison.Ordinal)
                ? Enumerable.Range(1, SimVarCatalog.MaxEngines).Select(i => binding.Name.Replace("{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : [binding.Name];

            foreach (var name in names)
            {
                Subscribe(sc, new SimVarSpec(VarKey(name, unit), name.Trim(), unit, SimVarSpec.KindFromUnit(unit)) { Fast = fast });
            }
        }

        if (profile.Ecam is { } ecam)
        {
            for (var n = 1; n <= Math.Clamp(ecam.Lines, 0, 20); n++)
            {
                foreach (var pattern in new[] { ecam.LeftLines, ecam.RightLines })
                {
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        var name = pattern.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Subscribe(sc, new SimVarSpec(VarKey(name, "number"), name, "number", SimVarKind.Number));
                    }
                }
            }
        }

        foreach (var variable in profile.Variables.Concat(profile.Electrical).Concat(profile.Fuel))
        {
            var unit = string.IsNullOrWhiteSpace(variable.Unit) ? "number" : variable.Unit;
            Subscribe(sc, new SimVarSpec(VarKey(variable.Name, unit), variable.Name.Trim(), unit, SimVarSpec.KindFromUnit(unit)));
        }

        _log.LogInformation(
            "Flugzeug: {Title} ({Path}) → Profil {Profile} ({Bindings} Bindings, {Vars} Variablen)",
            title ?? "unbekannt",
            path ?? "Pfad unbekannt",
            profile.Name,
            profile.Bindings.Count,
            profile.Variables.Count);
    }

    // ------------------------------------------------------------------ SimConnect-Ereignisse

    private void OnOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        var version = $"{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}.{data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}";
        _connection = new ConnectionInfo(true, data.szApplicationName, version, DateTimeOffset.UtcNow);
        _log.LogInformation("Verbunden mit {App} {Version}", data.szApplicationName, version);

        foreach (var spec in SimVarCatalog.All)
        {
            Subscribe(sender, spec);
        }

        // Systemereignisse: Pause, Flug läuft, Flugzeugwechsel
        sender.SubscribeToSystemEvent(SimEventId.Pause, "Pause_EX1");
        sender.SubscribeToSystemEvent(SimEventId.Sim, "Sim");
        sender.SubscribeToSystemEvent(SimEventId.AircraftLoaded, "AircraftLoaded");
        sender.RequestSystemState((SimRequestId)StateRequestAircraft, "AircraftLoaded");
        sender.RequestSystemState((SimRequestId)StateRequestSim, "Sim");

        _log.LogInformation("{Count} SimVars abonniert.", SimVarCatalog.All.Count);
    }

    private void OnQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        // Nicht direkt hier trennen – wir stecken noch in ReceiveMessage(). Der Run-Loop räumt auf.
        _log.LogInformation("MSFS wurde beendet.");
        _quitRequested = true;
    }

    private void OnException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        _log.LogWarning(
            "SimConnect meldet {Exception} (SendID {SendId}, Index {Index})",
            (SIMCONNECT_EXCEPTION)data.dwException,
            data.dwSendID,
            data.dwIndex);
    }

    private void OnEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        var status = _simStatus;
        switch ((SimEventId)data.uEventID)
        {
            case SimEventId.Pause:
                var flags = data.dwData & ~2u; // Bit 2 (Legacy-Pause mit Ton) wird nicht mehr benutzt
                if (flags != status.PauseFlags && flags == 0)
                {
                    _log.LogInformation("Sim läuft weiter.");
                }
                else if (flags != status.PauseFlags)
                {
                    _log.LogInformation("Sim pausiert (Flags {Flags}).", flags);
                }

                _simStatus = status with { Paused = flags != 0, PauseFlags = flags };
                break;
            case SimEventId.Sim:
                _simStatus = status with { FlightRunning = data.dwData == 1 };
                break;
        }
    }

    private void OnEventFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        if ((SimEventId)data.uEventID == SimEventId.AircraftLoaded)
        {
            SetAircraftPath(sender, data.szFileName);
        }
    }

    private void OnSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        switch (data.dwRequestID)
        {
            case StateRequestAircraft:
                SetAircraftPath(sender, data.szString);
                break;
            case StateRequestSim:
                _simStatus = _simStatus with { FlightRunning = data.dwInteger == 1 };
                break;
        }
    }

    private void SetAircraftPath(SimConnect sc, string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastPath = path;
        _simStatus = _simStatus with { AircraftPath = path };
        ApplyProfile(sc);
    }

    private void OnData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        var raw = data.dwData is { Length: > 0 } ? data.dwData[0] : null;
        object? value = raw switch
        {
            double d => d,
            SimString256 s => s.Value?.TrimEnd('\0', ' ') ?? "",
            _ => raw,
        };

        if (data.dwRequestID >= AdHocRequestBase)
        {
            if (_pending.TryRemove(data.dwRequestID, out var pending))
            {
                pending.Completion.TrySetResult(value);
            }

            return;
        }

        if (!_valueKeyByDefId.TryGetValue(data.dwDefineID, out var key))
        {
            return;
        }

        _values[key] = new SimValue(value, DateTimeOffset.UtcNow);

        if (key == TitleKey && !string.Equals(value as string, _lastTitle, StringComparison.Ordinal))
        {
            _lastTitle = value as string;
            ApplyProfile(sender);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _messageEvent.Dispose();
        _commandEvent.Dispose();
    }

    // ------------------------------------------------------------------ Hilfstypen

    private sealed record PendingRead(TaskCompletionSource<object?> Completion, DateTimeOffset ExpiresUtc);
}

public sealed record SimValue(object? Value, DateTimeOffset UpdatedUtc);

public sealed record ConnectionInfo(bool IsConnected, string? Application, string? Version, DateTimeOffset? SinceUtc)
{
    public static readonly ConnectionInfo Disconnected = new(false, null, null, null);
}

public sealed record ReadResult(bool Ok, object? Value, string? Error)
{
    public static ReadResult Success(object? value) => new(true, value, null);

    public static ReadResult Fail(string error) => new(false, null, error);
}
