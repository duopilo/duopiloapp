using System.Reflection;
using System.Runtime.InteropServices;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>
/// Eigene Bodenfahrzeuge (Follow-me, später Feuerwehr): als SimObject erzeugen (AICreateSimulatedObject, in MSFS 2024 mit Livery) und
/// Werte am einzelnen Fahrzeug setzen – etwa die Z-Variablen <c>L:1:DUOPILO_RUNDUMLICHT</c>, die nur für dieses eine Objekt gelten.
/// Bewegt werden die Fahrzeuge wie die KI-Flugzeuge mit <see cref="StartAiPose"/> (ohne <c>releaseAi</c>, sie haben keinen KI-Piloten).
/// </summary>
public sealed partial class SimConnectClient
{
    // Nur auf dem SimConnect-Thread benutzt. Je Objekt eigene Data Definitions (Schlüssel: Objekt, Variable|Einheit): Eine Definition, die
    // erst an einem Fahrzeug und dann an einem anderen Modell benutzt wurde, traf bei der Feuerwehr die falsche Z-Variable (Test EDDK
    // 05.11.: die Treppe des RTF lief mit DUOPILO_WEG). Vermutung: MSFS legt L:1:-Variablen je Modell in der Reihenfolge an, in der der
    // Code sie zuerst benutzt, und bindet eine Definition beim ersten Setzen an diese Stelle.
    private readonly Dictionary<(uint ObjectId, string Key), uint> _objectVarDefIds = [];

    /// <summary>
    /// Erzeugt ein SimObject (Bodenfahrzeug, Gegenstand) aus dem Titel seiner sim.cfg und liefert die Objekt-ID. Kommt keine ID
    /// (Titel nicht installiert, außerhalb des Bereichs um den Spieler), wirft es mit der zuletzt gemeldeten SimConnect-Ausnahme im Text.
    /// </summary>
    public async Task<uint> CreateSimulatedObjectAsync(string title, string? livery, AiInitPosition position, CancellationToken ct)
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
                InvokeCreateSimulated(sc, title, livery ?? "", ToInitPosition(position), requestId);
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

    /// <summary>
    /// Setzt eine Zahl an einem eigenen Objekt (SetDataOnSimObject), z. B. <c>L:1:DUOPILO_RUNDUMLICHT</c> mit "Number" oder
    /// <c>LIGHT BEACON</c> mit "Bool". Während das Objekt lädt, verwirft der Sim den Wert – nach dem Erzeugen also wiederholen.
    /// </summary>
    public Task SetObjectNumberAsync(uint objectId, string simVar, string unit, double value, CancellationToken ct) =>
        RunOnSimThreadAsync(sc =>
        {
            var defId = ObjectVarDefId(sc, objectId, simVar, unit);
            sc.SetDataOnSimObject((SimDefinitionId)defId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
        }, ct);

    /// <summary>Data Definition für eine Zahl an genau diesem Objekt (beim ersten Mal angelegt), nur auf dem SimConnect-Thread.</summary>
    private uint ObjectVarDefId(SimConnect sc, uint objectId, string simVar, string unit)
    {
        var key = (objectId, simVar + "|" + unit);
        if (!_objectVarDefIds.TryGetValue(key, out var defId))
        {
            defId = ++_nextDefId;
            sc.AddToDataDefinition((SimDefinitionId)defId, simVar, unit, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<double>((SimDefinitionId)defId);
            _objectVarDefIds[key] = defId;
        }

        return defId;
    }

    /// <summary>Die Data Definitions eines Objekts freigeben (es ist weg), nur auf dem SimConnect-Thread.</summary>
    private void ClearObjectVarDefs(SimConnect sc, uint objectId)
    {
        foreach (var (key, defId) in _objectVarDefIds.Where(e => e.Key.ObjectId == objectId).ToList())
        {
            sc.ClearDataDefinition((SimDefinitionId)defId);
            _objectVarDefIds.Remove(key);
        }
    }

    /// <summary>AICreateSimulatedObject_EX1 (Titel, Livery, Position, Request), sonst AICreateSimulatedObject ohne Livery.</summary>
    private static void InvokeCreateSimulated(SimConnect sc, string title, string livery, SIMCONNECT_DATA_INITPOSITION position, uint requestId)
    {
        var type = sc.GetType();
        var request = (SimRequestId)requestId;
        var ex1 = FindMethod(type, "AICreateSimulatedObject_EX1", 4);
        if (ex1 is not null)
        {
            ex1.Invoke(sc, [title, livery, position, request]);
            return;
        }

        var legacy = FindMethod(type, "AICreateSimulatedObject", 3)
            ?? throw new NotSupportedException(Loc.T("sim.ai.createUnsupported"));
        legacy.Invoke(sc, [title, position, request]);
    }

    private void ResetVehicles() => _objectVarDefIds.Clear();
}
