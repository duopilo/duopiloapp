using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>Ein Input Event des geladenen Flugzeugs (MSFS 2024: Bedienelemente laufen über Input Events und B-Variablen).</summary>
/// <param name="Type">"double" oder "string" (Asobo nutzt bisher nur double).</param>
public sealed record InputEventInfo(string Name, ulong Hash, string Type);

/// <summary>
/// Input Events über SimConnect: auflisten (EnumerateInputEvents), Werte lesen (GetInputEvent) und Parameter
/// (EnumerateInputEventParams). Damit lässt sich jedes Flugzeug im Sim auslesen, auch ohne Doku – z. B. der iniBuilds A321LR,
/// dessen FCU nur über Input Events geht. L:-Variablen kann SimConnect nicht auflisten.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint InputEventEnumRequest = 3_000_000;
    private const uint InputEventValueRequestBase = 3_000_001;
    private const int InputEventChunk = 100;
    private static readonly TimeSpan InputEventListTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InputEventChunkTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object?>> _inputValueRequests = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<string?>> _inputParamRequests = new();
    private readonly object _inputEnumGate = new();
    private InputEventEnumeration? _inputEnum;
    private uint _nextInputValueRequest;

    // Nur auf dem SimConnect-Thread benutzt
    private bool _inputStructsRegistered;
    private bool _inputEventsAvailable;

    /// <summary>Alle Input Events des geladenen Flugzeugs. Leer, wenn MSFS keine meldet (je nach Version nur im Entwicklermodus).</summary>
    public async Task<IReadOnlyList<InputEventInfo>> EnumerateInputEventsAsync(CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var enumeration = new InputEventEnumeration();
        lock (_inputEnumGate)
        {
            // Eine laufende Abfrage wird ersetzt – die Antworten tragen dieselbe Request-ID
            _inputEnum?.Completion.TrySetCanceled();
            _inputEnum = enumeration;
        }

        Enqueue(sc =>
        {
            if (!_inputEventsAvailable)
            {
                enumeration.Completion.TrySetException(new NotSupportedException(Loc.T("tools.inputEvents.unsupported")));
                return;
            }

            sc.EnumerateInputEvents((SimRequestId)InputEventEnumRequest);
        });

        try
        {
            return await enumeration.Completion.Task.WaitAsync(InputEventListTimeout, ct);
        }
        catch (TimeoutException)
        {
            // Keine Antwort – so verhält sich MSFS, wenn es keine Input Events herausgibt
            lock (_inputEnumGate)
            {
                return [.. enumeration.Events];
            }
        }
        finally
        {
            lock (_inputEnumGate)
            {
                if (ReferenceEquals(_inputEnum, enumeration))
                {
                    _inputEnum = null;
                }
            }
        }
    }

    /// <summary>Aktuelle Werte (double oder string, null = keine Antwort). In Paketen, damit SimConnect nicht überläuft.</summary>
    public async Task<IReadOnlyDictionary<ulong, object?>> ReadInputEventValuesAsync(IReadOnlyList<InputEventInfo> events, CancellationToken ct)
    {
        var result = new Dictionary<ulong, object?>();
        foreach (var chunk in events.Chunk(InputEventChunk))
        {
            if (!_connection.IsConnected)
            {
                throw new InvalidOperationException(Loc.T("sim.notConnected"));
            }

            var pending = chunk.Select(e =>
            {
                var id = InputEventValueRequestBase + (Interlocked.Increment(ref _nextInputValueRequest) % 900_000);
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inputValueRequests[id] = tcs;
                return (Event: e, Id: id, Completion: tcs);
            }).ToList();

            Enqueue(sc =>
            {
                EnsureInputEventStructs(sc);
                foreach (var p in pending)
                {
                    sc.GetInputEvent((SimRequestId)p.Id, p.Event.Hash);
                }
            });

            await WaitAll(pending.Select(p => (Task)p.Completion.Task), ct);
            foreach (var p in pending)
            {
                _inputValueRequests.TryRemove(p.Id, out _);
                result[p.Event.Hash] = p.Completion.Task.IsCompletedSuccessfully ? p.Completion.Task.Result : null;
            }
        }

        return result;
    }

    /// <summary>Parameter je Input Event (Beschreibung von MSFS, leer bei einfachen Tastern; null = keine Antwort).</summary>
    public async Task<IReadOnlyDictionary<ulong, string?>> ReadInputEventParamsAsync(IReadOnlyList<InputEventInfo> events, CancellationToken ct)
    {
        var result = new Dictionary<ulong, string?>();
        foreach (var chunk in events.Chunk(InputEventChunk))
        {
            if (!_connection.IsConnected)
            {
                throw new InvalidOperationException(Loc.T("sim.notConnected"));
            }

            var pending = chunk.Select(e =>
            {
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inputParamRequests[e.Hash] = tcs;
                return (Event: e, Completion: tcs);
            }).ToList();

            Enqueue(sc =>
            {
                foreach (var p in pending)
                {
                    sc.EnumerateInputEventParams(p.Event.Hash);
                }
            });

            await WaitAll(pending.Select(p => (Task)p.Completion.Task), ct);
            foreach (var p in pending)
            {
                _inputParamRequests.TryRemove(p.Event.Hash, out _);
                result[p.Event.Hash] = p.Completion.Task.IsCompletedSuccessfully ? p.Completion.Task.Result : null;
            }
        }

        return result;
    }

    private static async Task WaitAll(IEnumerable<Task> tasks, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(InputEventChunkTimeout, ct);
        }
        catch (TimeoutException)
        {
            // Was bis hierher nicht kam, bleibt leer
        }
    }

    /// <summary>
    /// Handler eintragen. Eigene Methode: Kennt die geladene SimConnect-Bibliothek die Input Events nicht (sehr altes SDK), schlägt
    /// nur diese Methode fehl – die Verbindung steht trotzdem.
    /// </summary>
    private void TryRegisterInputEventHandlers(SimConnect sc)
    {
        try
        {
            RegisterInputEventHandlers(sc);
            _inputEventsAvailable = true;
        }
        catch (Exception ex) when (ex is MissingMethodException or MissingFieldException or TypeLoadException)
        {
            // Der Fehler entsteht schon beim Übersetzen von RegisterInputEventHandlers – deshalb hier abfangen, nicht dort
            _inputEventsAvailable = false;
            _log.LogWarning(Loc.Log("log.sim.inputEventsUnavailable"), ex.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RegisterInputEventHandlers(SimConnect sc)
    {
        sc.OnRecvEnumerateInputEvents += OnEnumerateInputEvents;
        sc.OnRecvGetInputEvent += OnGetInputEvent;
        sc.OnRecvEnumerateInputEventParams += OnEnumerateInputEventParams;
    }

    /// <summary>Wie SimConnect den Wert eines Input Events decodiert, bestimmt die beim Typ eingetragene Struktur (double/string).</summary>
    private void EnsureInputEventStructs(SimConnect sc)
    {
        if (_inputStructsRegistered)
        {
            return;
        }

        _inputStructsRegistered = true;
        sc.RegisterStruct<SIMCONNECT_RECV_GET_INPUT_EVENT, InputEventDouble>(InputEventTypeId.Double);
        sc.RegisterStruct<SIMCONNECT_RECV_GET_INPUT_EVENT, InputEventString>(InputEventTypeId.String);
    }

    private void OnEnumerateInputEvents(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS data)
    {
        if (data.dwRequestID != InputEventEnumRequest)
        {
            return;
        }

        lock (_inputEnumGate)
        {
            if (_inputEnum is not { } enumeration)
            {
                return;
            }

            foreach (var item in data.rgData ?? [])
            {
                if (item is SIMCONNECT_INPUT_EVENT_DESCRIPTOR d && !string.IsNullOrWhiteSpace(d.Name))
                {
                    var type = Convert.ToInt32(d.eType, System.Globalization.CultureInfo.InvariantCulture) == (int)InputEventTypeId.String ? "string" : "double";
                    enumeration.Events.Add(new InputEventInfo(d.Name.TrimEnd('\0', ' '), d.Hash, type));
                }
            }

            if (data.dwOutOf == 0 || data.dwEntryNumber + 1 >= data.dwOutOf)
            {
                enumeration.Completion.TrySetResult([.. enumeration.Events]);
            }
        }
    }

    private void OnGetInputEvent(SimConnect sender, SIMCONNECT_RECV_GET_INPUT_EVENT data)
    {
        if (!_inputValueRequests.TryGetValue(data.dwRequestID, out var tcs))
        {
            return;
        }

        object? value = data.Value is { Length: > 0 } v ? v[0] switch
        {
            InputEventDouble d => d.Value,
            InputEventString s => s.Value?.TrimEnd('\0'),
            double d => d,
            float f => (double)f,
            string s => s,
            _ => null,
        } : null;
        tcs.TrySetResult(value);
    }

    private void OnEnumerateInputEventParams(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENT_PARAMS data)
    {
        if (_inputParamRequests.TryGetValue(data.Hash, out var tcs))
        {
            tcs.TrySetResult(data.Value?.TrimEnd('\0', ' '));
        }
    }

    private void ResetInputEvents()
    {
        _inputStructsRegistered = false;
        _inputEventsAvailable = false;
        lock (_inputEnumGate)
        {
            _inputEnum?.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
            _inputEnum = null;
        }

        foreach (var (_, tcs) in _inputValueRequests)
        {
            tcs.TrySetResult(null);
        }

        foreach (var (_, tcs) in _inputParamRequests)
        {
            tcs.TrySetResult(null);
        }
    }

    private sealed class InputEventEnumeration
    {
        public List<InputEventInfo> Events { get; } = [];

        public TaskCompletionSource<IReadOnlyList<InputEventInfo>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Typ eines Input Events wie SIMCONNECT_INPUT_EVENT_TYPE (0 double, 1 string) – als ID für RegisterStruct.</summary>
public enum InputEventTypeId : uint
{
    Double = 0,
    String = 1,
}

/// <summary>Wert eines Input Events vom Typ double (für RegisterStruct).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputEventDouble
{
    public double Value;
}

/// <summary>Wert eines Input Events vom Typ string (für RegisterStruct).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct InputEventString
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string Value;
}
