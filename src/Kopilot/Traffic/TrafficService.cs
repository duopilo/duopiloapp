using Kopilot.Monitoring;
using Kopilot.Sim;
using Microsoft.Extensions.Options;

namespace Kopilot.Traffic;

/// <summary>Ergebnis einer Verkehrsabfrage für get_traffic.</summary>
/// <param name="RadiusNm">Tatsächlich abgefragter Radius (höchstens 200 km).</param>
/// <param name="Complete">MSFS hat die Abfrage vollständig beantwortet.</param>
public sealed record TrafficScanResult(TrafficPicture Picture, double RadiusNm, bool Complete, string? Error);

/// <summary>
/// Verkehr um das eigene Flugzeug über SimConnect (KI-Verkehr, Live-Traffic und Multiplayer), abgefragt über die eigene
/// Verkehrsverbindung (<see cref="TrafficConnection"/>). Fragt auf Anfrage ab (get_traffic, mit Texten) und – solange die
/// Regel TRAFFIC_PROXIMITY an ist und das Flugzeug fliegt – alle paar Sekunden im Hintergrund für das Monitoring
/// (nur Zahlen; Callsign und Typ kommen aus der letzten get_traffic-Abfrage, siehe <see cref="TrafficIdentCache"/>).
/// </summary>
public sealed class TrafficService : BackgroundService
{
    public const double DefaultRadiusNm = 40;

    public const double MetersPerNm = 1852;

    /// <summary>200 km – mehr nimmt SimConnect nicht an.</summary>
    public const double MaxRadiusNm = TrafficConnection.MaxScanRadiusMeters / MetersPerNm;

    private readonly SimConnectClient _sim;
    private readonly TrafficConnection _connection;
    private readonly TrafficMonitoringOptions _options;
    private readonly bool _proximityEnabled;
    private readonly ILogger<TrafficService> _log;

    private readonly VerticalSpeedTracker _verticalSpeeds = new();
    private readonly GroundSpeedTracker _groundSpeeds = new();
    private readonly TrafficIdentCache _idents = new();

    private volatile TrafficPicture? _monitoringPicture;
    private DateTimeOffset _lastErrorLogUtc;

    public TrafficService(SimConnectClient sim, TrafficConnection connection, IOptions<KopilotOptions> options, ILogger<TrafficService> log)
    {
        _sim = sim;
        _connection = connection;
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

    /// <summary>Zähler der Verkehrsverbindung mit Urteil zu den Absturz-Hypothesen (für get_traffic).</summary>
    public TrafficMessageStatsSnapshot Diagnostics => _connection.Diagnostics;

    /// <summary>Pfad von traffic-messages.csv, null wenn aus.</summary>
    public string? MessageLogPath => _connection.MessageLogPath;

    /// <summary>Abfrage für get_traffic – mit Callsign, Airline, Flugnummer und Typ.</summary>
    public Task<TrafficScanResult> ScanAsync(double radiusNm, CancellationToken ct) => ScanAsync(radiusNm, withText: true, ct);

    private async Task<TrafficScanResult> ScanAsync(double radiusNm, bool withText, CancellationToken ct)
    {
        var radius = ClampRadius(radiusNm);
        var scan = await _connection.ScanAsync((uint)Math.Ceiling(radius * MetersPerNm), includeHelicopters: true, withText, ct);
        var now = DateTimeOffset.UtcNow;
        if (scan.Error is not null)
        {
            return new TrafficScanResult(TrafficPicture.Empty(now), radius, false, scan.Error);
        }

        IReadOnlyList<AircraftSample> samples = scan.Samples;
        if (withText)
        {
            _idents.Remember(samples, now);
        }
        else
        {
            samples = _idents.Fill(samples, now);
        }

        var verticalSpeeds = _verticalSpeeds.Update(samples, now);
        var groundSpeeds = _groundSpeeds.Update(samples, now);
        var picture = TrafficPictureBuilder.Build(now, samples, FallbackOwn(), radius, verticalSpeeds, groundSpeeds, OwnPressureAltitude());
        return new TrafficScanResult(picture, radius, scan.Complete, null);
    }

    /// <summary>Eigene Druckhöhe als Bezug für die Höhendifferenz – null, wenn auf die wahre Höhe eingestellt oder (noch) kein Wert da ist.</summary>
    private double? OwnPressureAltitude() =>
        _options.AltitudeReference == TrafficAltitudeReference.Pressure && _sim.Num("altPressure") is { } pa && double.IsFinite(pa) ? pa : null;

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
            var result = await ScanAsync(MonitoringRadiusNm, withText: false, ct);
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
