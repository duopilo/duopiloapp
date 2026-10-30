using Kopilot.Localization;
using Microsoft.Extensions.Options;

namespace Kopilot.FlightPlan;

/// <summary>Lädt das letzte OFP aus SimBrief (öffentliche API, keine Anmeldung) und hält es zwischengespeichert.</summary>
public sealed class SimBriefService
{
    private const string FetchUrl = "https://www.simbrief.com/api/xml.fetcher.php";

    private readonly SimBriefOptions _options;
    private readonly ILogger<SimBriefService> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FixPassageTracker _passages = new();

    private volatile Ofp? _current;
    private DateTimeOffset _fetchedUtc;
    private int? _legHint;

    public SimBriefService(IOptions<KopilotOptions> options, ILogger<SimBriefService> log)
    {
        _options = options.Value.SimBrief;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kopilot/1.0 (+https://github.com/Shin-Jin-Jin/Kopilot)");
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>Zuletzt geladenes OFP ohne Netzwerkzugriff (für Monitoring und ATC).</summary>
    public Ofp? Current => _current;

    public DateTimeOffset? FetchedUtc => _current is null ? null : _fetchedUtc;

    public string? LastError { get; private set; }

    /// <summary>Gesetzter Username (leer = keiner).</summary>
    public string Username => _options.Username;

    /// <summary>Gesetzte Pilot-ID (leer = keine).</summary>
    public string PilotId => _options.PilotId;

    /// <summary>
    /// Konto aus den Dashboard-Einstellungen übernehmen – gilt sofort, ohne Neustart. Hat sich das Konto geändert, wird der
    /// geladene Plan verworfen (er gehört zum alten Konto); der nächste <see cref="GetAsync"/> holt den neuen.
    /// </summary>
    /// <returns>true, wenn sich das Konto geändert hat.</returns>
    public bool ChangeAccount(string username, string pilotId)
    {
        username = username.Trim();
        pilotId = pilotId.Trim();
        if (string.Equals(username, _options.Username.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(pilotId, _options.PilotId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        _options.Username = username;
        _options.PilotId = pilotId;
        _current = null;
        _legHint = null;
        _fetchedUtc = default;
        LastError = null;
        _log.LogInformation(
            Loc.Log("log.flightplan.accountChanged"),
            pilotId.Length > 0 ? Loc.T("flightplan.account.pilotId", pilotId) : username.Length > 0 ? username : Loc.T("flightplan.account.none"));
        return true;
    }

    /// <summary>OFP holen – aus dem Cache, solange er jünger als CacheMinutes ist, sonst von SimBrief.</summary>
    public async Task<Ofp?> GetAsync(bool refresh, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            LastError = Loc.T("flightplan.simbrief.notConfigured");
            return null;
        }

        if (!refresh && _current is { } cached && DateTimeOffset.UtcNow - _fetchedUtc < TimeSpan.FromMinutes(Math.Max(1, _options.CacheMinutes)))
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var query = !string.IsNullOrWhiteSpace(_options.PilotId)
                ? $"userid={Uri.EscapeDataString(_options.PilotId.Trim())}"
                : $"username={Uri.EscapeDataString(_options.Username.Trim())}";

            using var response = await _http.GetAsync($"{FetchUrl}?{query}&json=1", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // SimBrief antwortet bei unbekanntem Nutzer mit 400 und einer Fehlermeldung im JSON
                LastError = Loc.T("flightplan.simbrief.httpError", (int)response.StatusCode, ExtractStatus(body) ?? response.ReasonPhrase);
                return _current;
            }

            var ofp = OfpParser.Parse(body);
            if (_current?.RequestId != ofp.RequestId)
            {
                _legHint = null;
            }

            _current = ofp;
            _fetchedUtc = DateTimeOffset.UtcNow;
            LastError = null;
            _log.LogInformation(Loc.Log("log.flightplan.ofpLoaded"), ofp.Origin.Icao, ofp.Destination.Icao, ofp.Fixes.Count);
            return ofp;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or SimBriefException)
        {
            LastError = Loc.T("flightplan.simbrief.fetchFailed", ex.Message);
            _log.LogWarning("SimBrief: {Message}", ex.Message);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Position auf der Route bestimmen und den Abschnitt für die nächste Abfrage merken.</summary>
    public RouteProgress? Locate(Ofp ofp, IReadOnlyList<RoutePoint> points, double lat, double lon)
    {
        var progress = RouteTracker.Locate(points, lat, lon, ReferenceEquals(ofp, _current) ? _legHint : null);
        if (progress is not null && ReferenceEquals(ofp, _current))
        {
            _legHint = progress.LegIndex;
        }

        return progress;
    }

    /// <summary>Überflug der Fixes mitschreiben und den letzten überflogenen Fix liefern (für die ETA nach Plan).</summary>
    /// <param name="zuluSec">Sim-Uhrzeit in Sekunden seit Mitternacht.</param>
    public FixPassage? ObservePassage(Ofp ofp, IReadOnlyList<RoutePoint> points, RouteProgress progress, double? zuluSec, double? groundSpeedKt) =>
        ReferenceEquals(ofp, _current) ? _passages.Observe(ofp, points, progress, zuluSec, groundSpeedKt) : null;

    private static string? ExtractStatus(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("fetch", out var f) && f.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
