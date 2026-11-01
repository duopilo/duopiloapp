using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>
/// Eigene KI-Flugzeuge über SimConnect: erzeugen (NonATC, MSFS 2024 mit Livery), Wegpunkte setzen, Fahrwerk, Werte abfragen und
/// wieder entfernen. Bisher nur für das Testwerkzeug (<see cref="Duopilo.Traffic.TrafficTestService"/>).
///
/// Die Funktionen aus MSFS 2024 (AICreateNonATCAircraft_EX1, EnumerateSimObjectsAndLiveries) werden per Reflection gesucht: Fehlen sie
/// in der geladenen SimConnect-Bibliothek, fällt das Erzeugen auf AICreateNonATCAircraft zurück, und die Modellliste meldet „nicht
/// verfügbar“ – die Verbindung steht trotzdem.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint AiCreateRequestBase = 4_000_000;
    private const uint AiRemoveRequestBase = 4_100_000;
    private const uint AiModelsRequest = 4_200_000;
    private const uint AiReleaseRequestBase = 4_300_000;

    /// <summary>Abfragen auf KI-Objekten. Unter <see cref="AdHocRequestBase"/>, damit <see cref="OnData"/> sie nicht als Einzelabfrage nimmt.</summary>
    private const uint AiProbeRequestBase = 970_000;
    private const uint AiProbeRequestRange = 1_000;

    private static readonly TimeSpan AiCreateTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AiModelsTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AiModelsQuiet = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<uint>> _aiCreateRequests = new();
    private readonly ConcurrentDictionary<uint, AiProbe> _aiProbes = new();
    private readonly ConcurrentDictionary<uint, Func<double, AiPose?>> _aiPoseSources = new();
    private readonly object _aiModelsGate = new();
    private AiModelEnumeration? _aiModels;
    private uint _nextAiCreateRequest;
    private uint _nextAiRemoveRequest;
    private uint _nextAiProbeRequest;
    private volatile string? _lastException;
    private DateTimeOffset _lastExceptionUtc;

    // Nur auf dem SimConnect-Thread benutzt
    private uint _aiWaypointDefId;
    private uint _aiProbeDefId;
    private uint _aiGearDefId;
    private uint _aiSpeedDefId;
    private uint _aiVelocityDefId;
    private uint _aiPoseDefId;
    private uint _nextAiReleaseRequest;
    private bool _frameSubscribed;
    private long _lastFrameTicks;
    private readonly Dictionary<string, uint> _aiLightDefIds = new(StringComparer.Ordinal);
    private bool _aiModelsAvailable;

    /// <summary>Letzte SimConnect-Ausnahme der letzten <paramref name="window"/> (z. B. CREATE_OBJECT_FAILED), sonst null.</summary>
    public string? RecentException(TimeSpan window) =>
        _lastException is { } name && DateTimeOffset.UtcNow - _lastExceptionUtc <= window ? name : null;

    /// <summary>
    /// Installierte Flugzeugmodelle mit Livery (EnumerateSimObjectsAndLiveries, MSFS 2024). MSFS schickt die Liste in mehreren
    /// Paketen; fertig ist sie, wenn alle Pakete da sind oder eine Weile nichts mehr kommt.
    /// </summary>
    public async Task<IReadOnlyList<AiModel>> EnumerateAircraftModelsAsync(CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var enumeration = new AiModelEnumeration();
        lock (_aiModelsGate)
        {
            _aiModels?.Completion.TrySetCanceled();
            _aiModels = enumeration;
        }

        Enqueue(sc =>
        {
            try
            {
                if (!_aiModelsAvailable || !InvokeEnumerateLiveries(sc))
                {
                    enumeration.Completion.TrySetException(new NotSupportedException(Loc.T("sim.ai.modelsUnsupported")));
                }
            }
            catch (Exception ex) when (ex is not COMException)
            {
                enumeration.Completion.TrySetException(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
            }
        });

        try
        {
            var deadline = DateTimeOffset.UtcNow + AiModelsTimeout;
            while (true)
            {
                var done = await Task.WhenAny(enumeration.Completion.Task, Task.Delay(250, ct));
                if (done == enumeration.Completion.Task)
                {
                    return await enumeration.Completion.Task;
                }

                lock (_aiModelsGate)
                {
                    var quiet = enumeration.LastPacketUtc is { } last && DateTimeOffset.UtcNow - last > AiModelsQuiet;
                    if (quiet || DateTimeOffset.UtcNow > deadline)
                    {
                        return [.. enumeration.Models];
                    }
                }
            }
        }
        finally
        {
            lock (_aiModelsGate)
            {
                if (ReferenceEquals(_aiModels, enumeration))
                {
                    _aiModels = null;
                }
            }
        }
    }

    /// <summary>
    /// Erzeugt ein KI-Flugzeug ohne ATC-Kontrolle und liefert seine Objekt-ID. Kommt keine ID (unbekannter Titel, außerhalb des
    /// Bereichs um den Spieler), wirft es mit der zuletzt gemeldeten SimConnect-Ausnahme im Text.
    /// </summary>
    public async Task<uint> CreateNonAtcAircraftAsync(string title, string? livery, string tailNumber, AiInitPosition position, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var requestId = AiCreateRequestBase + (Interlocked.Increment(ref _nextAiCreateRequest) % 90_000);
        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiCreateRequests[requestId] = tcs;
        Enqueue(sc =>
        {
            try
            {
                InvokeCreateNonAtc(sc, title, livery ?? "", tailNumber, ToInitPosition(position), requestId);
            }
            catch (Exception ex) when (ex is not COMException)
            {
                tcs.TrySetException(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
            }
        });

        try
        {
            return await tcs.Task.WaitAsync(AiCreateTimeout, ct);
        }
        catch (TimeoutException)
        {
            var exception = RecentException(AiCreateTimeout + TimeSpan.FromSeconds(1));
            throw new InvalidOperationException(exception is null
                ? Loc.T("sim.ai.createNoAnswer")
                : Loc.T("sim.ai.createFailed", exception));
        }
        finally
        {
            _aiCreateRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>Ersetzt die Wegpunktliste eines eigenen KI-Flugzeugs (AI WAYPOINT LIST).</summary>
    public Task SetAiWaypointsAsync(uint objectId, IReadOnlyList<AiWaypoint> waypoints, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            if (_aiWaypointDefId == 0)
            {
                _aiWaypointDefId = ++_nextDefId;
                sc.AddToDataDefinition((SimDefinitionId)_aiWaypointDefId, "AI WAYPOINT LIST", "number", SIMCONNECT_DATATYPE.WAYPOINT, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<SIMCONNECT_DATA_WAYPOINT>((SimDefinitionId)_aiWaypointDefId);
            }

            var data = waypoints.Select(w => (object)new SIMCONNECT_DATA_WAYPOINT
            {
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                Altitude = w.AltitudeFt,
                Flags = (uint)w.Flags,
                ktsSpeed = w.SpeedKt,
                percentThrottle = w.ThrottlePercent,
            }).ToArray();
            sc.SetDataOnSimObject((SimDefinitionId)_aiWaypointDefId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        }, ct);

    /// <summary>
    /// Fahrwerk eines eigenen KI-Flugzeugs. Welcher Weg bei welchem Modell wirkt, ist offen – deshalb wählbar:
    /// Event GEAR_DOWN/GEAR_UP, Event GEAR_SET (1/0), die SimVar GEAR HANDLE POSITION oder alles zusammen.
    /// </summary>
    public Task SetAiGearAsync(uint objectId, bool down, AiGearMethod method, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            if (method is AiGearMethod.Down or AiGearMethod.All)
            {
                TransmitToObject(sc, objectId, down ? "GEAR_DOWN" : "GEAR_UP", 0);
            }

            if (method is AiGearMethod.Set or AiGearMethod.All)
            {
                TransmitToObject(sc, objectId, "GEAR_SET", down ? 1u : 0u);
            }

            if (method is AiGearMethod.Handle or AiGearMethod.All)
            {
                if (_aiGearDefId == 0)
                {
                    _aiGearDefId = ++_nextDefId;
                    sc.AddToDataDefinition((SimDefinitionId)_aiGearDefId, "GEAR HANDLE POSITION", "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    sc.RegisterDataDefineStruct<double>((SimDefinitionId)_aiGearDefId);
                }

                sc.SetDataOnSimObject((SimDefinitionId)_aiGearDefId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, down ? 1.0 : 0.0);
            }
        }, ct);

    /// <summary>
    /// Lichter eines eigenen KI-Flugzeugs (NonATC-Flugzeuge schaltet der Sim nicht selbst). Beide Wege zugleich, weil offen ist, was
    /// welches Modell annimmt: das Event …_SET mit 1/0 und die SimVar LIGHT …. Lichter mit null bleiben unverändert.
    /// </summary>
    public Task SetAiLightsAsync(uint objectId, AiLights lights, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            foreach (var (light, on) in lights.Changes())
            {
                var (eventName, simVar) = AiLights.Names(light);
                TransmitToObject(sc, objectId, eventName, on ? 1u : 0u);

                if (!_aiLightDefIds.TryGetValue(simVar, out var defId))
                {
                    defId = ++_nextDefId;
                    sc.AddToDataDefinition((SimDefinitionId)defId, simVar, "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    sc.RegisterDataDefineStruct<double>((SimDefinitionId)defId);
                    _aiLightDefIds[simVar] = defId;
                }

                sc.SetDataOnSimObject((SimDefinitionId)defId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, on ? 1.0 : 0.0);
            }
        }, ct);

    /// <summary>
    /// Wunschgeschwindigkeit eines eigenen KI-Flugzeugs über die SimVar AI DESIRED SPEED (Knoten). Die Geschwindigkeit in den
    /// Wegpunkten beachtet die KI nicht (alle flogen im Test 160–169 kt, auch die ATR).
    /// </summary>
    public Task SetAiDesiredSpeedAsync(uint objectId, double knots, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            if (_aiSpeedDefId == 0)
            {
                _aiSpeedDefId = ++_nextDefId;
                sc.AddToDataDefinition((SimDefinitionId)_aiSpeedDefId, "AI DESIRED SPEED", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<double>((SimDefinitionId)_aiSpeedDefId);
            }

            sc.SetDataOnSimObject((SimDefinitionId)_aiSpeedDefId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, knots);
        }, ct);

    /// <summary>
    /// Versuch: Längsgeschwindigkeit eines eigenen KI-Flugzeugs direkt setzen (VELOCITY BODY Z, Fuß pro Sekunde) – laut Entwicklerforum
    /// reagieren Flugzeuge darauf. Die KI rechnet ihre eigene Geschwindigkeit jeden Frame nach, deshalb ruft der Test das oft auf.
    /// </summary>
    public Task SetAiVelocityAsync(uint objectId, double feetPerSecond, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            if (_aiVelocityDefId == 0)
            {
                _aiVelocityDefId = ++_nextDefId;
                sc.AddToDataDefinition((SimDefinitionId)_aiVelocityDefId, "VELOCITY BODY Z", "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<double>((SimDefinitionId)_aiVelocityDefId);
            }

            sc.SetDataOnSimObject((SimDefinitionId)_aiVelocityDefId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, feetPerSecond);
        }, ct);

    /// <summary>
    /// Kinematik: Duopilo übernimmt ein eigenes KI-Flugzeug ganz – die KI gibt es ab (AIReleaseControl), Position, Höhe und Lage
    /// werden eingefroren und jeden Frame aus <paramref name="pose"/> gesetzt (so wie vPilot Flugzeuge bewegt). <paramref name="pose"/>
    /// bekommt die vergangene Sim-Zeit in Sekunden (0 in der Pause) und läuft auf dem SimConnect-Thread; null = diesen Frame nichts setzen.
    /// </summary>
    public void StartAiPose(uint objectId, Func<double, AiPose?> pose)
    {
        _aiPoseSources[objectId] = pose;
        Enqueue(sc =>
        {
            if (!_aiPoseSources.ContainsKey(objectId))
            {
                return;
            }

            sc.AIReleaseControl(objectId, (SimRequestId)(AiReleaseRequestBase + (++_nextAiReleaseRequest % 90_000)));
            Freeze(sc, objectId, true);
            if (!_frameSubscribed)
            {
                sc.SubscribeToSystemEvent(SimEventId.Frame, "Frame");
                _frameSubscribed = true;
                _lastFrameTicks = 0;
            }
        });
    }

    /// <summary>Beendet die Kinematik; mit <paramref name="unfreeze"/> steht das Flugzeug danach wieder frei (ohne KI-Pilot).</summary>
    public void StopAiPose(uint objectId, bool unfreeze)
    {
        if (!_aiPoseSources.TryRemove(objectId, out _))
        {
            return;
        }

        Enqueue(sc =>
        {
            if (unfreeze)
            {
                Freeze(sc, objectId, false);
            }

            if (_aiPoseSources.IsEmpty && _frameSubscribed)
            {
                sc.UnsubscribeFromSystemEvent(SimEventId.Frame);
                _frameSubscribed = false;
            }
        });
    }

    /// <summary>Ob Duopilo dieses KI-Flugzeug gerade selbst bewegt.</summary>
    public bool IsAiPoseActive(uint objectId) => _aiPoseSources.ContainsKey(objectId);

    private void Freeze(SimConnect sc, uint objectId, bool on)
    {
        TransmitToObject(sc, objectId, "FREEZE_LATITUDE_LONGITUDE_SET", on ? 1u : 0u);
        TransmitToObject(sc, objectId, "FREEZE_ALTITUDE_SET", on ? 1u : 0u);
        TransmitToObject(sc, objectId, "FREEZE_ATTITUDE_SET", on ? 1u : 0u);
    }

    /// <summary>Jeder Frame: Lage aller übernommenen KI-Flugzeuge setzen (SimConnect-Thread).</summary>
    private void OnAiFrame(SimConnect sender, SIMCONNECT_RECV_EVENT_FRAME data)
    {
        if (_aiPoseSources.IsEmpty)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var dt = _lastFrameTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFrameTicks, now).TotalSeconds;
        _lastFrameTicks = now;
        dt = _simStatus.Paused ? 0 : Math.Clamp(dt, 0, 0.25) * Math.Clamp(data.fSimSpeed, 0, 16);

        if (_aiPoseDefId == 0)
        {
            _aiPoseDefId = ++_nextDefId;
            var def = (SimDefinitionId)_aiPoseDefId;
            foreach (var (name, unit) in AiPoseVariables.All)
            {
                sender.AddToDataDefinition(def, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            }

            sender.RegisterDataDefineStruct<AiPoseData>(def);
        }

        foreach (var (objectId, source) in _aiPoseSources)
        {
            AiPose? pose;
            try
            {
                pose = source(dt);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, Loc.Log("log.sim.aiProbeFailed"), objectId);
                continue;
            }

            if (pose is { } p)
            {
                sender.SetDataOnSimObject((SimDefinitionId)_aiPoseDefId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new AiPoseData
                {
                    Latitude = p.Latitude,
                    Longitude = p.Longitude,
                    AltitudeFt = p.AltitudeFt,
                    // Der Sim zählt Nase hoch negativ
                    PitchDeg = -p.PitchUpDeg,
                    BankDeg = p.BankDeg,
                    HeadingTrueDeg = p.HeadingTrueDeg,
                });
            }
        }
    }

    /// <summary>Event an ein eigenes KI-Objekt (nicht an das eigene Flugzeug, daher ohne die Sperrliste des Eingabe-Moduls).</summary>
    private void TransmitToObject(SimConnect sc, uint objectId, string eventName, uint data)
    {
        if (!_eventIds.TryGetValue(eventName, out var id))
        {
            id = ClientEventBase + ++_nextEventId;
            sc.MapClientEventToSimEvent((SimClientEventId)id, eventName);
            _eventIds[eventName] = id;
        }

        sc.TransmitClientEvent(objectId, (SimClientEventId)id, data, SimGroupId.PriorityHighest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>Entfernt ein eigenes KI-Flugzeug. Objekte anderer Clients oder des Sims lehnt SimConnect ab.</summary>
    public Task RemoveAiObjectAsync(uint objectId, CancellationToken ct)
    {
        StopAiProbe(objectId);
        return RunOnSimThreadAsync(sc =>
            sc.AIRemoveObject(objectId, (SimRequestId)(AiRemoveRequestBase + (++_nextAiRemoveRequest % 90_000))), ct);
    }

    /// <summary>Fragt die Werte eines KI-Objekts einmal pro Sekunde ab, bis <see cref="StopAiProbe"/> kommt.</summary>
    public void StartAiProbe(uint objectId, Action<AiProbeSample> onSample)
    {
        var requestId = AiProbeRequestBase + (Interlocked.Increment(ref _nextAiProbeRequest) % AiProbeRequestRange);
        var probe = new AiProbe(objectId, requestId, onSample);
        _aiProbes[objectId] = probe;
        Enqueue(sc =>
        {
            if (!_aiProbes.TryGetValue(objectId, out var current) || !ReferenceEquals(current, probe))
            {
                return;
            }

            EnsureAiProbeDefinition(sc);
            sc.RequestDataOnSimObject((SimRequestId)requestId, (SimDefinitionId)_aiProbeDefId, objectId, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        });
    }

    public void StopAiProbe(uint objectId)
    {
        if (!_aiProbes.TryRemove(objectId, out var probe))
        {
            return;
        }

        Enqueue(sc =>
        {
            if (_aiProbeDefId != 0)
            {
                sc.RequestDataOnSimObject((SimRequestId)probe.RequestId, (SimDefinitionId)_aiProbeDefId, objectId, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            }
        });
    }

    // ------------------------------------------------------------------ SimConnect-Thread

    private void EnsureAiProbeDefinition(SimConnect sc)
    {
        if (_aiProbeDefId != 0)
        {
            return;
        }

        _aiProbeDefId = ++_nextDefId;
        var def = (SimDefinitionId)_aiProbeDefId;
        foreach (var (name, unit) in AiProbeVariables.Numeric)
        {
            sc.AddToDataDefinition(def, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        }

        sc.AddToDataDefinition(def, AiProbeVariables.State, null, SIMCONNECT_DATATYPE.STRING64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<AiProbeData>(def);
    }

    /// <summary>Für <see cref="OnData"/>: true, wenn die Nachricht zu einer KI-Abfrage gehört.</summary>
    private bool TryHandleAiProbeData(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID < AiProbeRequestBase || data.dwRequestID >= AiProbeRequestBase + AiProbeRequestRange)
        {
            return false;
        }

        if (_aiProbes.TryGetValue(data.dwObjectID, out var probe)
            && probe.RequestId == data.dwRequestID
            && data.dwData is { Length: > 0 }
            && data.dwData[0] is AiProbeData d)
        {
            var state = d.State?.TrimEnd('\0', ' ');
            var sample = new AiProbeSample(
                data.dwObjectID,
                DateTimeOffset.UtcNow,
                d.Latitude,
                d.Longitude,
                d.AltitudeFt,
                d.AltitudeAglFt,
                d.IndicatedKt,
                d.GroundSpeedKt,
                d.VerticalSpeedFpm,
                d.HeadingTrueDeg,
                d.OnGround != 0,
                d.GearHandle != 0,
                d.GearTotalPercent,
                d.GearCenterPercent,
                d.GearLeftPercent,
                d.GearRightPercent,
                string.IsNullOrWhiteSpace(state) ? null : state)
            {
                // Der Sim zählt Nase hoch negativ
                PitchDeg = -d.PitchDeg,
                BankDeg = d.BankDeg,
                LightStates = (int)d.LightStates,
                DesiredKt = d.DesiredKt,
                CgHeightFt = d.CgHeightFt,
            };
            try
            {
                probe.OnSample(sample);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, Loc.Log("log.sim.aiProbeFailed"), data.dwObjectID);
            }
        }

        return true;
    }

    /// <summary>
    /// Handler eintragen. Eigene Methode wie bei den Input Events: Fehlt etwas in der geladenen Bibliothek, schlägt nur das hier fehl.
    /// </summary>
    private void TryRegisterAiHandlers(SimConnect sc)
    {
        try
        {
            RegisterAiHandlers(sc);
        }
        catch (Exception ex) when (ex is MissingMethodException or MissingFieldException or TypeLoadException)
        {
            _log.LogWarning(Loc.Log("log.sim.aiUnavailable"), ex.Message);
        }

        _aiModelsAvailable = TrySubscribeLiveryList(sc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RegisterAiHandlers(SimConnect sc)
    {
        sc.OnRecvAssignedObjectId += OnAssignedObjectId;
        sc.OnRecvEventFrame += OnAiFrame;
    }

    private void OnAssignedObjectId(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (_aiCreateRequests.TryGetValue(data.dwRequestID, out var tcs))
        {
            tcs.TrySetResult(data.dwObjectID);
        }
    }

    /// <summary>SIMCONNECT_DATA_INITPOSITION aus der eigenen Beschreibung.</summary>
    private static SIMCONNECT_DATA_INITPOSITION ToInitPosition(AiInitPosition p) => new()
    {
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Altitude = p.AltitudeFt,
        Pitch = p.PitchDeg,
        Bank = p.BankDeg,
        Heading = p.HeadingTrueDeg,
        OnGround = p.OnGround ? 1u : 0u,
        Airspeed = p.AirspeedKt,
    };

    /// <summary>AICreateNonATCAircraft_EX1 (Titel, Livery, Kennzeichen, Position, Request), sonst AICreateNonATCAircraft ohne Livery.</summary>
    private static void InvokeCreateNonAtc(SimConnect sc, string title, string livery, string tailNumber, SIMCONNECT_DATA_INITPOSITION position, uint requestId)
    {
        var type = sc.GetType();
        var request = (SimRequestId)requestId;
        var ex1 = FindMethod(type, "AICreateNonATCAircraft_EX1", 5);
        if (ex1 is not null)
        {
            ex1.Invoke(sc, [title, livery, tailNumber, position, request]);
            return;
        }

        var legacy = FindMethod(type, "AICreateNonATCAircraft", 4)
            ?? throw new NotSupportedException(Loc.T("sim.ai.createUnsupported"));
        legacy.Invoke(sc, [title, tailNumber, position, request]);
    }

    private bool InvokeEnumerateLiveries(SimConnect sc)
    {
        var method = FindMethod(sc.GetType(), "EnumerateSimObjectsAndLiveries", 2);
        if (method is null)
        {
            return false;
        }

        method.Invoke(sc, [(SimRequestId)AiModelsRequest, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT]);
        return true;
    }

    private static MethodInfo? FindMethod(Type type, string name, int parameterCount) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);

    /// <summary>
    /// Handler für die Modellliste per Reflection eintragen (Ereignis mit „Livery“ im Namen). Ein Handler mit SIMCONNECT_RECV als
    /// Parameter passt zu jedem Delegat (SimConnect, SIMCONNECT_RECV_…); die Felder liest <see cref="OnLiveryList"/> per Reflection.
    /// </summary>
    private bool TrySubscribeLiveryList(SimConnect sc)
    {
        try
        {
            var ev = sc.GetType().GetEvents(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(e => e.Name.Contains("Livery", StringComparison.OrdinalIgnoreCase));
            if (ev?.EventHandlerType is not { } handlerType)
            {
                return false;
            }

            var target = typeof(SimConnectClient).GetMethod(nameof(OnLiveryList), BindingFlags.NonPublic | BindingFlags.Instance)!;
            var handler = Delegate.CreateDelegate(handlerType, this, target, throwOnBindFailure: false);
            if (handler is null)
            {
                return false;
            }

            ev.AddEventHandler(sc, handler);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or MemberAccessException or TargetInvocationException)
        {
            _log.LogWarning(Loc.Log("log.sim.aiModelsUnavailable"), ex.Message);
            return false;
        }
    }

    private void OnLiveryList(SimConnect sender, SIMCONNECT_RECV data)
    {
        lock (_aiModelsGate)
        {
            if (_aiModels is not { } enumeration || ReadUInt(data, "dwRequestID") != AiModelsRequest)
            {
                return;
            }

            enumeration.LastPacketUtc = DateTimeOffset.UtcNow;
            if (ReadMember(data, "rgData") is Array items)
            {
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var title = ReadText(item, "Title");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        enumeration.Models.Add(new AiModel(title, ReadText(item, "Livery") ?? ""));
                    }
                }
            }

            var entry = ReadUInt(data, "dwEntryNumber");
            var outOf = ReadUInt(data, "dwOutOf");
            if (outOf is 0 || (entry is { } e && outOf is { } o && e + 1 >= o))
            {
                enumeration.Completion.TrySetResult([.. enumeration.Models]);
            }
        }
    }

    private static object? ReadMember(object source, string name)
    {
        var type = source.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
    }

    private static uint? ReadUInt(object source, string name) => ReadMember(source, name) switch
    {
        uint u => u,
        int i when i >= 0 => (uint)i,
        _ => null,
    };

    /// <summary>Erstes Text-Feld, dessen Name <paramref name="part"/> enthält (AircraftTitle, LiveryName …).</summary>
    private static string? ReadText(object source, string part)
    {
        var type = source.GetType();
        var value = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(string) && f.Name.Contains(part, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.GetValue(source) as string)
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.Name.Contains(part, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.GetValue(source) as string))
            .FirstOrDefault(v => v is not null);
        return value?.TrimEnd('\0', ' ');
    }

    private void RememberException(SIMCONNECT_EXCEPTION exception)
    {
        _lastException = exception.ToString();
        _lastExceptionUtc = DateTimeOffset.UtcNow;
    }

    private void ResetAiObjects()
    {
        _aiWaypointDefId = 0;
        _aiProbeDefId = 0;
        _aiGearDefId = 0;
        _aiSpeedDefId = 0;
        _aiVelocityDefId = 0;
        _aiPoseDefId = 0;
        _frameSubscribed = false;
        _aiPoseSources.Clear();
        _aiLightDefIds.Clear();
        _aiModelsAvailable = false;
        _aiProbes.Clear();
        foreach (var (_, tcs) in _aiCreateRequests)
        {
            tcs.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        }

        lock (_aiModelsGate)
        {
            _aiModels?.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
            _aiModels = null;
        }
    }

    private sealed record AiProbe(uint ObjectId, uint RequestId, Action<AiProbeSample> OnSample);

    private sealed class AiModelEnumeration
    {
        public List<AiModel> Models { get; } = [];

        public DateTimeOffset? LastPacketUtc { get; set; }

        public TaskCompletionSource<IReadOnlyList<AiModel>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Installiertes Flugzeugmodell (Titel aus aircraft.cfg bzw. Preset) mit Livery.</summary>
public sealed record AiModel(string Title, string Livery);

/// <summary>Startposition eines KI-Flugzeugs (SIMCONNECT_DATA_INITPOSITION).</summary>
/// <param name="AltitudeFt">Höhe über MSL in Fuß.</param>
public sealed record AiInitPosition(
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double HeadingTrueDeg,
    bool OnGround,
    uint AirspeedKt,
    double PitchDeg = 0,
    double BankDeg = 0);

/// <summary>Wegpunkt für AI WAYPOINT LIST (SIMCONNECT_DATA_WAYPOINT).</summary>
/// <param name="AltitudeFt">Höhe über MSL in Fuß (mit <see cref="AiWaypointFlags.AltitudeIsAgl"/> über Grund).</param>
public sealed record AiWaypoint(
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double SpeedKt,
    AiWaypointFlags Flags,
    double ThrottlePercent = 0);

/// <summary>SIMCONNECT_WAYPOINT_FLAGS (Werte aus SimConnect.h).</summary>
[Flags]
public enum AiWaypointFlags : uint
{
    None = 0,
    SpeedRequested = 0x04,
    ThrottleRequested = 0x08,
    ComputeVerticalSpeed = 0x10,
    AltitudeIsAgl = 0x20,
    OnGround = 0x0010_0000,
    Reverse = 0x0020_0000,
    WrapToFirst = 0x0040_0000,
}

/// <summary>Werte eines KI-Flugzeugs aus der Abfrage (einmal pro Sekunde).</summary>
public sealed record AiProbeSample(
    uint ObjectId,
    DateTimeOffset Utc,
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double AltitudeAglFt,
    double IndicatedKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    double HeadingTrueDeg,
    bool OnGround,
    bool GearHandleDown,
    double GearTotalPercent,
    double GearCenterPercent,
    double GearLeftPercent,
    double GearRightPercent,
    string? AiState)
{
    /// <summary>
    /// Am weitesten ausgefahrenes Fahrwerk (0–100). Mehrere Werte, weil nicht jedes Modell als KI alle davon pflegt – beim vollen
    /// A320neo blieb GEAR TOTAL PCT EXTENDED im Test bei etwa 1 %, obwohl das Fahrwerk auf der Bahn sichtbar draußen war.
    /// </summary>
    public double GearExtendedPercent => Math.Max(Math.Max(GearTotalPercent, GearCenterPercent), Math.Max(GearLeftPercent, GearRightPercent));

    /// <summary>Längsneigung in Grad, Nase hoch positiv (PLANE PITCH DEGREES mit umgedrehtem Vorzeichen) – zeigt, ob die KI abfängt.</summary>
    public double PitchDeg { get; init; }

    /// <summary>Querneigung in Grad (PLANE BANK DEGREES, Vorzeichen wie im Sim).</summary>
    public double BankDeg { get; init; }

    /// <summary>Eingeschaltete Lichter (LIGHT STATES, Bits siehe <see cref="AiLights"/>).</summary>
    public int LightStates { get; init; }

    /// <summary>Wunschgeschwindigkeit der KI (AI DESIRED SPEED) in Knoten.</summary>
    public double DesiredKt { get; init; }

    /// <summary>Höhe des Bezugspunkts über dem Boden, wenn das Flugzeug steht (STATIC CG TO GROUND) – die Kinematik setzt es damit auf.</summary>
    public double CgHeightFt { get; init; }
}

/// <summary>Lage eines von Duopilo bewegten KI-Flugzeugs (<see cref="SimConnectClient.StartAiPose"/>).</summary>
/// <param name="AltitudeFt">Höhe des Bezugspunkts über MSL.</param>
/// <param name="PitchUpDeg">Längsneigung, Nase hoch positiv.</param>
public readonly record struct AiPose(double Latitude, double Longitude, double AltitudeFt, double PitchUpDeg, double BankDeg, double HeadingTrueDeg);

/// <summary>SimVars, die die Kinematik jeden Frame setzt, in dieser Reihenfolge (siehe <see cref="AiPoseData"/>).</summary>
public static class AiPoseVariables
{
    public static readonly IReadOnlyList<(string Name, string Unit)> All =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE PITCH DEGREES", "degrees"),
        ("PLANE BANK DEGREES", "degrees"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
    ];
}

/// <summary>Struktur zu <see cref="AiPoseVariables"/>: sechs FLOAT64.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AiPoseData
{
    public double Latitude;

    public double Longitude;

    public double AltitudeFt;

    public double PitchDeg;

    public double BankDeg;

    public double HeadingTrueDeg;
}

/// <summary>SimVars der KI-Abfrage, in dieser Reihenfolge in der Data Definition (siehe <see cref="AiProbeData"/>).</summary>
public static class AiProbeVariables
{
    public static readonly IReadOnlyList<(string Name, string Unit)> Numeric =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE ALT ABOVE GROUND", "feet"),
        ("AIRSPEED INDICATED", "knots"),
        ("GROUND VELOCITY", "knots"),
        ("VERTICAL SPEED", "feet per minute"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("SIM ON GROUND", "Bool"),
        ("GEAR HANDLE POSITION", "Bool"),
        ("GEAR TOTAL PCT EXTENDED", "percent"),
        ("GEAR CENTER POSITION", "percent"),
        ("GEAR LEFT POSITION", "percent"),
        ("GEAR RIGHT POSITION", "percent"),
        ("PLANE PITCH DEGREES", "degrees"),
        ("PLANE BANK DEGREES", "degrees"),
        ("LIGHT STATES", "mask"),
        ("AI DESIRED SPEED", "knots"),
        ("STATIC CG TO GROUND", "feet"),
    ];

    /// <summary>Zustand der KI (z. B. „SIMPLE_LANDING“), als STRING64 hinter den Zahlen.</summary>
    public const string State = "AI TRAFFIC STATE";
}

/// <summary>Struktur zu <see cref="AiProbeVariables"/>: neunzehn FLOAT64, dann STRING64.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct AiProbeData
{
    public double Latitude;

    public double Longitude;

    public double AltitudeFt;

    public double AltitudeAglFt;

    public double IndicatedKt;

    public double GroundSpeedKt;

    public double VerticalSpeedFpm;

    public double HeadingTrueDeg;

    public double OnGround;

    public double GearHandle;

    public double GearTotalPercent;

    public double GearCenterPercent;

    public double GearLeftPercent;

    public double GearRightPercent;

    public double PitchDeg;

    public double BankDeg;

    public double LightStates;

    public double DesiredKt;

    public double CgHeightFt;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string State;
}

/// <summary>Ein Licht eines KI-Flugzeugs.</summary>
public enum AiLight
{
    Nav,
    Beacon,
    Strobe,
    Landing,
    Taxi,
    Logo,
}

/// <summary>Gewünschte Lichter eines KI-Flugzeugs; null = unverändert lassen.</summary>
public sealed record AiLights(bool? Nav = null, bool? Beacon = null, bool? Strobe = null, bool? Landing = null, bool? Taxi = null, bool? Logo = null)
{
    // Bits von LIGHT STATES laut SDK
    private static readonly (AiLight Light, int Bit, string Short)[] Bits =
    [
        (AiLight.Nav, 0x0001, "nav"),
        (AiLight.Beacon, 0x0002, "beacon"),
        (AiLight.Landing, 0x0004, "landing"),
        (AiLight.Taxi, 0x0008, "taxi"),
        (AiLight.Strobe, 0x0010, "strobe"),
        (AiLight.Logo, 0x0100, "logo"),
    ];

    /// <summary>Die Lichter, die geschaltet werden sollen, mit Soll-Zustand.</summary>
    public IEnumerable<(AiLight Light, bool On)> Changes()
    {
        (AiLight Light, bool? On)[] all = [(AiLight.Nav, Nav), (AiLight.Beacon, Beacon), (AiLight.Strobe, Strobe), (AiLight.Landing, Landing), (AiLight.Taxi, Taxi), (AiLight.Logo, Logo)];
        return all.Where(l => l.On is not null).Select(l => (l.Light, l.On!.Value));
    }

    /// <summary>Event (…_SET mit 1/0) und SimVar eines Lichts.</summary>
    public static (string EventName, string SimVar) Names(AiLight light) => light switch
    {
        AiLight.Nav => ("NAV_LIGHTS_SET", "LIGHT NAV"),
        AiLight.Beacon => ("BEACON_LIGHTS_SET", "LIGHT BEACON"),
        AiLight.Strobe => ("STROBES_SET", "LIGHT STROBE"),
        AiLight.Landing => ("LANDING_LIGHTS_SET", "LIGHT LANDING"),
        AiLight.Taxi => ("TAXI_LIGHTS_SET", "LIGHT TAXI"),
        AiLight.Logo => ("LOGO_LIGHTS_SET", "LIGHT LOGO"),
        _ => throw new ArgumentOutOfRangeException(nameof(light)),
    };

    /// <summary>Ob <paramref name="light"/> laut LIGHT STATES an ist.</summary>
    public static bool IsOn(int lightStates, AiLight light) => (lightStates & Bits.Single(b => b.Light == light).Bit) != 0;

    /// <summary>Eingeschaltete Lichter als Text, z. B. "nav beacon landing strobe logo"; "" wenn keins an ist.</summary>
    public static string Describe(int lightStates) =>
        string.Join(' ', Bits.Where(b => (lightStates & b.Bit) != 0).Select(b => b.Short));

    /// <summary>Ob alle gewünschten Lichter laut LIGHT STATES so stehen wie verlangt.</summary>
    public bool MatchedBy(int lightStates) => Changes().All(c => IsOn(lightStates, c.Light) == c.On);
}

/// <summary>Wie Duopilo das Fahrwerk eines KI-Flugzeugs bedient (traffic_test_start, Parameter gear).</summary>
public enum AiGearMethod
{
    /// <summary>GEAR_DOWN/GEAR_UP, GEAR_SET und GEAR HANDLE POSITION zusammen.</summary>
    All,

    /// <summary>Event GEAR_DOWN bzw. GEAR_UP.</summary>
    Down,

    /// <summary>Event GEAR_SET mit 1 (unten) bzw. 0 (oben).</summary>
    Set,

    /// <summary>SimVar GEAR HANDLE POSITION schreiben.</summary>
    Handle,
}
