using Kopilot.Monitoring;
using Kopilot.Sim;
using Microsoft.Extensions.Options;

namespace Kopilot.Traffic;

/// <summary>Ergebnis einer Verkehrsabfrage für get_traffic.</summary>
/// <param name="RadiusNm">Tatsächlich abgefragter Radius (höchstens 200 km).</param>
/// <param name="Complete">MSFS hat die Abfrage vollständig beantwortet.</param>
public sealed record TrafficScanResult(TrafficPicture Picture, double RadiusNm, bool Complete, string? Error);

/// <summary>
/// Verkehr um das eigene Flugzeug über SimConnect (KI-Verkehr und Multiplayer).
/// Fragt auf Anfrage ab (get_traffic) und – solange die Regel TRAFFIC_PROXIMITY an ist und das Flugzeug fliegt –
/// alle paar Sekunden im Hintergrund für das Monitoring.
/// </summary>
public sealed class TrafficService : BackgroundService
{
    public const double DefaultRadiusNm = 40;

    public const double MetersPerNm = 1852;

    /// <summary>200 km – mehr nimmt SimConnect nicht an.</summary>
    public const double MaxRadiusNm = SimConnectClient.MaxScanRadiusMeters / MetersPerNm;

    private readonly SimConnectClient _sim;
    private readonly TrafficMonitoringOptions _options;
    private readonly bool _proximityEnabled;
    private readonly ILogger<TrafficService> _log;

    private readonly VerticalSpeedTracker _verticalSpeeds = new();

    private volatile TrafficPicture? _monitoringPicture;
    private DateTimeOffset _lastErrorLogUtc;

    public TrafficService(SimConnectClient sim, IOptions<KopilotOptions> options, ILogger<TrafficService> log)
    {
        _sim = sim;
        _log = log;
        var monitoring = options.Value.Monitoring;
        _options = monitoring.Traffic;
        _proximityEnabled = monitoring.Enabled
            && !monitoring.DisabledRules.Contains(Rules.TrafficProximity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Verkehrslage der letzten Hintergrundabfrage für das Monitoring, null wenn keine aktuelle vorliegt.</summary>
    public TrafficPicture? MonitoringPicture => _monitoringPicture;

    /// <summary>Radius der Hintergrundabfrage: großzügig um den Warnradius, damit Annäherungen früh in den Daten sind.</summary>
    public double MonitoringRadiusNm => Math.Clamp(Math.Max(5, _options.ProximityRangeNm * 2), 1, MaxRadiusNm);

    /// <summary>Begrenzt einen gewünschten Radius auf 0,1 NM bis 200 km.</summary>
    public static double ClampRadius(double radiusNm) =>
        double.IsFinite(radiusNm) ? Math.Clamp(radiusNm, 0.1, MaxRadiusNm) : DefaultRadiusNm;

    public async Task<TrafficScanResult> ScanAsync(double radiusNm, CancellationToken ct)
    {
        var radius = ClampRadius(radiusNm);
        var scan = await _sim.ScanAircraftAsync((uint)Math.Ceiling(radius * MetersPerNm), includeHelicopters: true, ct);
        var now = DateTimeOffset.UtcNow;
        if (scan.Error is not null)
        {
            return new TrafficScanResult(TrafficPicture.Empty(now), radius, false, scan.Error);
        }

        var derived = _verticalSpeeds.Update(scan.Samples, now);
        var picture = TrafficPictureBuilder.Build(now, scan.Samples, FallbackOwn(), radius, derived);
        return new TrafficScanResult(picture, radius, scan.Complete, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_proximityEnabled)
        {
            _log.LogInformation("Verkehrswarnung (TRAFFIC_PROXIMITY) ist aus – keine Verkehrsabfragen im Hintergrund.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.ScanIntervalSec, 1, 30)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var status = _sim.Status;
        if (!_sim.Connection.IsConnected || status.FlightRunning == false || _sim.Bool("onGround") != false)
        {
            _monitoringPicture = null;
            return;
        }

        if (status.Paused)
        {
            // Monitoring ruht in der Pause; das Alter der letzten Lage verhindert danach veraltete Warnungen
            return;
        }

        try
        {
            var result = await ScanAsync(MonitoringRadiusNm, ct);
            _monitoringPicture = result.Error is null && result.Picture.Own is not null ? result.Picture : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _monitoringPicture = null;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastErrorLogUtc > TimeSpan.FromMinutes(1))
            {
                _lastErrorLogUtc = now;
                _log.LogWarning(ex, "Verkehrsabfrage fehlgeschlagen");
            }
        }
    }

    /// <summary>Eigene Werte aus den abonnierten SimVars – falls das eigene Flugzeug nicht in der Abfrage steckt.</summary>
    private OwnAircraft? FallbackOwn() =>
        _sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon && _sim.Num("altTrue") is { } alt && _sim.Num("hdgTrue") is { } hdg
            ? new OwnAircraft(lat, lon, alt, hdg, _sim.Bool("onGround") == true)
            : null;
}
