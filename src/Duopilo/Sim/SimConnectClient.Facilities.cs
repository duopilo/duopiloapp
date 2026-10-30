using System.Runtime.InteropServices;
using Duopilo.Atc;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>
/// Flughafen-Frequenzen über die Facilities-API von SimConnect (AIRPORT → FREQUENCY). Anfragen laufen nacheinander:
/// SimConnect schickt je Frequenz eine FACILITY_DATA-Nachricht und zum Schluss FACILITY_DATA_END.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint FacilityDefinitionId = 950_000;
    private const uint FacilityRequestBase = 2_000_000;
    private static readonly TimeSpan FacilityTimeout = TimeSpan.FromSeconds(10);

    // Nur auf dem SimConnect-Thread benutzt
    private readonly Queue<FacilityRequest> _facilityQueue = new();
    private FacilityRequest? _facilityActive;
    private bool _facilityDefined;
    private uint _nextFacilityRequest;

    /// <summary>Alle Frequenzen eines Flughafens aus der MSFS-Datenbank (Ground, Tower, Clearance, Approach …).</summary>
    public async Task<IReadOnlyList<AirportFrequency>> RequestAirportFrequenciesAsync(string icao, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var request = new FacilityRequest(icao.Trim().ToUpperInvariant());
        Enqueue(sc =>
        {
            _facilityQueue.Enqueue(request);
            StartNextFacilityRequest(sc);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FacilityTimeout * 3);
        return await request.Completion.Task.WaitAsync(timeout.Token);
    }

    private void StartNextFacilityRequest(SimConnect sc)
    {
        if (_facilityActive is not null || !_facilityQueue.TryDequeue(out var next))
        {
            return;
        }

        try
        {
            if (!_facilityDefined)
            {
                var def = (SimDefinitionId)FacilityDefinitionId;
                sc.AddToFacilityDefinition(def, "OPEN AIRPORT");
                sc.AddToFacilityDefinition(def, "N_FREQUENCIES");
                sc.AddToFacilityDefinition(def, "OPEN FREQUENCY");
                sc.AddToFacilityDefinition(def, "TYPE");
                sc.AddToFacilityDefinition(def, "FREQUENCY");
                sc.AddToFacilityDefinition(def, "NAME");
                sc.AddToFacilityDefinition(def, "CLOSE FREQUENCY");
                sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");
                sc.RegisterFacilityDataDefineStruct<FacilityAirportData>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
                sc.RegisterFacilityDataDefineStruct<FacilityFrequencyData>(SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY);
                _facilityDefined = true;
            }

            next.StartedUtc = DateTimeOffset.UtcNow;
            _facilityActive = next;
            sc.RequestFacilityData((SimDefinitionId)FacilityDefinitionId, (SimRequestId)(FacilityRequestBase + ++_nextFacilityRequest), next.Icao, "");
        }
        catch (Exception ex) when (ex is not COMException)
        {
            _facilityActive = null;
            next.Completion.TrySetException(ex);
            StartNextFacilityRequest(sc);
        }
    }

    private void OnFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (_facilityActive is not { } active || data.Data is not { Length: > 0 })
        {
            return;
        }

        if (data.Data[0] is FacilityFrequencyData f && f.FrequencyHz > 0)
        {
            var name = f.Name?.TrimEnd('\0', ' ');
            active.Frequencies.Add(new AirportFrequency(f.Type, f.FrequencyHz / 1_000_000.0, string.IsNullOrWhiteSpace(name) ? null : name));
        }
    }

    private void OnFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (_facilityActive is { } active)
        {
            _facilityActive = null;
            _log.LogInformation(Loc.Log("log.sim.facilityFrequencies"), active.Frequencies.Count, active.Icao);
            active.Completion.TrySetResult(active.Frequencies);
        }

        StartNextFacilityRequest(sender);
    }

    /// <summary>Kommt keine Antwort (z. B. unbekannter ICAO-Code), nach einer Weile aufgeben und die nächste Anfrage starten.</summary>
    private void ExpireFacilityRequest()
    {
        if (_facilityActive is { } active && DateTimeOffset.UtcNow - active.StartedUtc > FacilityTimeout)
        {
            _facilityActive = null;
            active.Completion.TrySetException(new TimeoutException(Loc.T("sim.facilities.timeout", active.Icao)));
            if (_sc is { } sc)
            {
                StartNextFacilityRequest(sc);
            }
        }
    }

    private void ResetFacilities()
    {
        _facilityDefined = false;
        _facilityActive?.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        _facilityActive = null;
        while (_facilityQueue.TryDequeue(out var queued))
        {
            queued.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        }
    }

    private sealed class FacilityRequest(string icao)
    {
        public string Icao { get; } = icao;

        public DateTimeOffset StartedUtc { get; set; }

        public List<AirportFrequency> Frequencies { get; } = [];

        public TaskCompletionSource<IReadOnlyList<AirportFrequency>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>AIRPORT-Teil der Facility-Definition (nur die Anzahl, damit der Datensatz nicht leer ist).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityAirportData
{
    public int FrequencyCount;
}

/// <summary>FREQUENCY-Teil: TYPE (INT32), FREQUENCY (INT32, Hz), NAME (CHAR[64]) – Reihenfolge wie in der Definition.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityFrequencyData
{
    public int Type;

    public int FrequencyHz;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string Name;
}
