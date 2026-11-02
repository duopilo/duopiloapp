using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Traffic;
using Microsoft.Extensions.Options;

namespace Duopilo.Vehicles;

/// <summary>
/// Follow-me-Fahrzeuge eines Flughafens: höchstens <see cref="MaxPerAirport"/> zugleich – für den Piloten (<see cref="FollowMeService"/>)
/// und den eigenen KI-Verkehr (Verkehrstest A3/A4) zusammen. Wer zuerst kommt, bekommt eins; ist keins frei, rollt das Flugzeug allein.
/// </summary>
public sealed class FollowMeFleet
{
    public const int MaxPerAirport = 2;

    private readonly Dictionary<string, int> _inUse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ein Fahrzeug am Flughafen belegen; null = alle unterwegs. Freigeben mit <see cref="IDisposable.Dispose"/>.</summary>
    public IDisposable? TryReserve(string airport)
    {
        lock (_inUse)
        {
            var used = _inUse.GetValueOrDefault(airport);
            if (used >= MaxPerAirport)
            {
                return null;
            }

            _inUse[airport] = used + 1;
            return new Lease(this, airport);
        }
    }

    /// <summary>So viele Fahrzeuge sind am Flughafen unterwegs.</summary>
    public int InUse(string airport)
    {
        lock (_inUse)
        {
            return _inUse.GetValueOrDefault(airport);
        }
    }

    private void Release(string airport)
    {
        lock (_inUse)
        {
            var used = _inUse.GetValueOrDefault(airport) - 1;
            if (used <= 0)
            {
                _inUse.Remove(airport);
            }
            else
            {
                _inUse[airport] = used;
            }
        }
    }

    private sealed class Lease(FollowMeFleet fleet, string airport) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                fleet.Release(airport);
            }
        }
    }
}

/// <summary>
/// Erzeugt, bewegt und entfernt ein Follow-me-Fahrzeug im Sim – für den Piloten und den KI-Verkehr gleich: SimObject
/// (<see cref="SimConnectClient.CreateSimulatedObjectAsync"/>) am Anfang von <see cref="FollowMePlan.Approach"/>, jeden Frame die Lage aus
/// <see cref="FollowMeVehicle"/>, Boden aus der Abfrage des Fahrzeugs, Rundum- und Fahrlicht.
/// </summary>
public sealed class FollowMeSpawner(SimConnectClient sim, IOptions<DuopiloOptions> options, ILogger<FollowMeSpawner> log)
{
    private const double MaxGroundCorrectionFt = 60;

    private readonly DuopiloOptions _options = options.Value;

    /// <summary>Follow-me-Fahrzeuge gibt es in dieser Instanz (in der stdio-Instanz aus).</summary>
    public bool Enabled => _options.FollowMe.Enabled;

    /// <summary>Titel des Fahrzeugs (<see cref="FollowMeOptions.Model"/>).</summary>
    public string Model => _options.FollowMe.Model;

    /// <summary>
    /// Fahrzeug am Anfang der Anfahrt erzeugen und losfahren lassen. <paramref name="beforeFrame"/> läuft jeden Frame vor dem Weiterrechnen
    /// (Lage des Flugzeugs übergeben), <paramref name="afterFrame"/> danach (z. B. den Halt des Flugzeugs dahinter setzen) – beide auf dem
    /// SimConnect-Thread. Wirft mit einem lesbaren Grund, wenn das Modell fehlt.
    /// </summary>
    public async Task<FollowMeUnit> SpawnAsync(
        FollowMePlan plan,
        TaxiGraph graph,
        double groundFt,
        Action<FollowMeVehicle>? beforeFrame,
        Action<FollowMeVehicle>? afterFrame,
        CancellationToken ct)
    {
        var height = _options.FollowMe.HeightFt;
        var spawn = plan.Approach.At(0);
        var (lat, lon) = graph.ToLatLon(spawn.East, spawn.North);
        uint objectId;
        try
        {
            objectId = await sim.CreateSimulatedObjectAsync(Model, null, new AiInitPosition(lat, lon, groundFt + height, spawn.HeadingDeg, true, 0), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Loc.T("tools.followMe.spawnFailed", Model, ex.Message), ex);
        }

        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, groundFt, height);
        var unit = new FollowMeUnit(objectId, vehicle, groundFt);
        if (ct.IsCancellationRequested)
        {
            await RemoveAsync(unit);
            ct.ThrowIfCancellationRequested();
        }

        sim.StartAiPose(objectId, dt =>
        {
            beforeFrame?.Invoke(vehicle);
            vehicle.Advance(dt);
            afterFrame?.Invoke(vehicle);
            return vehicle.Phase == FollowMePhase.Gone ? null : vehicle.Pose();
        }, releaseAi: false);
        sim.StartAiProbe(objectId, sample =>
        {
            // Eingefroren: Höhe minus Höhe über Grund ist der Boden – nur in der Nähe des Erwarteten
            var measured = sample.AltitudeFt - sample.AltitudeAglFt;
            if (Math.Abs(measured - unit.GroundFt) < MaxGroundCorrectionFt)
            {
                vehicle.GroundTargetFt = measured;
            }
        });
        log.LogInformation(Loc.Log("log.followMe.spawned"), Model, objectId);
        _ = SwitchLightsAsync(objectId, unit.Lights.Token);
        return unit;
    }

    /// <summary>Fahrzeug anhalten und aus dem Sim nehmen.</summary>
    public async Task RemoveAsync(FollowMeUnit unit)
    {
        if (Interlocked.Exchange(ref unit.RemovedFlag, 1) == 1)
        {
            return;
        }

        unit.Lights.Cancel();
        sim.StopAiProbe(unit.ObjectId);
        sim.StopAiPose(unit.ObjectId, unfreeze: false);
        if (!sim.Connection.IsConnected)
        {
            return;
        }

        try
        {
            await sim.RemoveAiObjectAsync(unit.ObjectId, CancellationToken.None);
            log.LogInformation(Loc.Log("log.followMe.removed"), unit.ObjectId);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, Loc.Log("log.followMe.removeFailed"), unit.ObjectId, ex.Message);
        }
    }

    /// <summary>Rundumlicht und Fahrlicht an – während das Objekt lädt, verwirft der Sim die Werte, deshalb mehrmals.</summary>
    private async Task SwitchLightsAsync(uint objectId, CancellationToken ct)
    {
        foreach (var delay in new[] { 1, 2, 5 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                await sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_RUNDUMLICHT", "Number", 1, ct);
                await sim.SetObjectNumberAsync(objectId, "L:1:DUOPILO_FAHRLICHT", "Number", 1, ct);
                await sim.SetAiLightsAsync(objectId, new AiLights(Beacon: true, Landing: true), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, Loc.Log("log.followMe.lightsFailed"), ex.Message);
            }
        }
    }
}

/// <summary>Ein Follow-me-Fahrzeug im Sim.</summary>
public sealed class FollowMeUnit(uint objectId, FollowMeVehicle vehicle, double groundFt)
{
    public uint ObjectId { get; } = objectId;

    public FollowMeVehicle Vehicle { get; } = vehicle;

    /// <summary>Erwarteter Boden beim Erzeugen (Grenze für die Messung).</summary>
    public double GroundFt { get; } = groundFt;

    internal CancellationTokenSource Lights { get; } = new();

    internal int RemovedFlag;
}
