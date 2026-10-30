using Duopilo.Aircraft;
using Duopilo.Monitoring;
using Duopilo.Sim;

namespace Duopilo.Input;

/// <summary>Eingabe-Modul über die bestehende SimConnect-Verbindung.</summary>
public sealed class SimInputBackend(SimConnectClient sim, SnapshotFactory snapshots) : IInputBackend
{
    public bool Connected => sim.Connection.IsConnected;

    public AircraftProfile? Profile => sim.ActiveProfile;

    public FlightSnapshot Snapshot() => snapshots.Build(DateTimeOffset.UtcNow);

    public async Task<double?> ReadAsync(string name, string unit, CancellationToken ct)
    {
        var result = await sim.ReadOnceAsync(name, unit, ct);
        return result.Ok && result.Value is double d && double.IsFinite(d) ? d : null;
    }

    public Task SendEventAsync(string name, uint data, CancellationToken ct) => sim.TransmitEventAsync(name, data, ct);

    public Task SetAsync(string name, string unit, double value, CancellationToken ct) => sim.SetVariableAsync(name, unit, value, ct);
}
