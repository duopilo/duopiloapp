using System.Globalization;
using System.Text.Json;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>
/// Wache, von der Fahrzeuge ausrücken und zu der sie zurückfahren: eine echte Feuerwache aus OpenStreetMap (<c>osm</c>) oder – als
/// Rückfall – eine freie Parkposition (<c>parking</c>, dann mit <paramref name="Parking"/>). Lage in Metern wie im Rollweg-Netz.
/// </summary>
public sealed record FireStation(string Name, double EastM, double NorthM, double Latitude, double Longitude, string Source, TaxiNode? Parking = null)
{
    /// <summary>Wache aus einer freien Parkposition.</summary>
    public static FireStation FromParking(TaxiNode node) =>
        new(node.Parking?.Label ?? "?", node.EastM, node.NorthM, node.Latitude, node.Longitude, "parking", node);
}

/// <summary>Feuerwache aus OpenStreetMap (amenity=fire_station oder building=fire_station).</summary>
public sealed record OsmFireStation(string? Name, double Latitude, double Longitude);

/// <summary>
/// Feuerwachen eines Flughafens aus OpenStreetMap über die Overpass API: alles mit <c>amenity=fire_station</c> oder
/// <c>building=fire_station</c> innerhalb des Flughafen-Umrisses (<c>aeroway=aerodrome</c> mit dem ICAO-Code). Das Ergebnis liegt
/// 30 Tage im Datenordner (cache\fire-stations), ein Fehlschlag einen Tag, damit nicht jeder Einsatz neu fragt. Daten:
/// © OpenStreetMap-Mitwirkende, ODbL 1.0 (THIRD-PARTY-NOTICES.md); Duopilo liefert keine Daten mit, es fragt zur Laufzeit.
/// </summary>
public sealed class FireStationLookup
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _cacheDirectory;
    private readonly ILogger _log;

    public FireStationLookup(string overpassUrl, string dataDirectory, ILogger log)
    {
        _url = overpassUrl;
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "fire-stations");
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppNames.UserAgent);
    }

    /// <summary>Feuerwachen des Flughafens (aus dem Zwischenspeicher oder neu abgefragt); leer, wenn OSM keine kennt oder nichts geht.</summary>
    public async Task<IReadOnlyList<OsmFireStation>> GetAsync(string icao, CancellationToken ct)
    {
        var file = Path.Combine(_cacheDirectory, icao.ToUpperInvariant() + ".json");
        var cached = ReadCache(file);
        if (cached is { } c && DateTimeOffset.UtcNow - c.FetchedUtc < (c.Ok ? Fresh : RetryAfterFailure))
        {
            return c.Stations;
        }

        try
        {
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Query(icao))]);
            using var response = await _http.PostAsync(_url, content, ct);
            response.EnsureSuccessStatusCode();
            var stations = Parse(await response.Content.ReadAsStringAsync(ct));
            WriteCache(file, new CacheFile(DateTimeOffset.UtcNow, true, stations));
            _log.LogInformation(Loc.Log("log.fire.osmFetched"), icao, stations.Count);
            return stations;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(Loc.Log("log.fire.osmFailed"), icao, ex.Message);

            // Alte Daten weiter nutzen; den Fehlschlag merken, damit nicht jeder Einsatz neu fragt
            var stations = cached is { Ok: true } old ? old.Stations : [];
            WriteCache(file, new CacheFile(DateTimeOffset.UtcNow, cached is { Ok: true }, stations));
            return stations;
        }
    }

    /// <summary>Overpass-Abfrage: Feuerwachen innerhalb des Flughafen-Umrisses, mit Mittelpunkt bei Flächen.</summary>
    public static string Query(string icao) =>
        "[out:json][timeout:25];area[\"aeroway\"=\"aerodrome\"][\"icao\"=\"" + icao.ToUpperInvariant().Replace("\"", "", StringComparison.Ordinal) + "\"]->.platz;" +
        "(nwr[\"amenity\"=\"fire_station\"](area.platz);nwr[\"building\"=\"fire_station\"](area.platz););out center tags;";

    /// <summary>
    /// Antwort der Overpass API lesen: Knoten mit lat/lon, Flächen mit center. Gebäude und Einrichtung derselben Wache (näher als 80 m)
    /// zählen einmal, mit Namen, wenn eins davon einen hat.
    /// </summary>
    public static IReadOnlyList<OsmFireStation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var found = new List<OsmFireStation>();
        if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (var element in elements.EnumerateArray())
        {
            var at = element.TryGetProperty("center", out var center) ? center : element;
            if (!at.TryGetProperty("lat", out var latValue) || !at.TryGetProperty("lon", out var lonValue))
            {
                continue;
            }

            string? name = null;
            if (element.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var nameValue))
            {
                name = nameValue.GetString();
            }

            var station = new OsmFireStation(name, latValue.GetDouble(), lonValue.GetDouble());
            var same = found.FindIndex(f => DistanceM(f.Latitude, f.Longitude, station.Latitude, station.Longitude) < 80);
            if (same < 0)
            {
                found.Add(station);
            }
            else if (found[same].Name is null && name is not null)
            {
                found[same] = found[same] with { Name = name };
            }
        }

        return found;
    }

    /// <summary>Wachen aus OSM als <see cref="FireStation"/> im Rollweg-Netz; nur bis 6 km vom Bezugspunkt, Name sonst "Wache n".</summary>
    public static IReadOnlyList<FireStation> ToStations(IReadOnlyList<OsmFireStation> osm, TaxiGraph graph) =>
        [.. osm
            .Select((s, i) =>
            {
                var (east, north) = graph.ToLocal(s.Latitude, s.Longitude);
                var name = string.IsNullOrWhiteSpace(s.Name) ? "Wache " + (i + 1).ToString(CultureInfo.InvariantCulture) : s.Name.Trim();
                return new FireStation(name, east, north, s.Latitude, s.Longitude, "osm");
            })
            .Where(s => Math.Abs(s.EastM) < 6000 && Math.Abs(s.NorthM) < 6000)];

    private static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * 111_320;
        var dLon = (lon2 - lon1) * 111_320 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt((dLat * dLat) + (dLon * dLon));
    }

    private CacheFile? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, Loc.Log("log.fire.osmCacheFailed"), file, ex.Message);
            return null;
        }
    }

    private void WriteCache(string file, CacheFile cache)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(file, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, Loc.Log("log.fire.osmCacheFailed"), file, ex.Message);
        }
    }

    private sealed record CacheFile(DateTimeOffset FetchedUtc, bool Ok, IReadOnlyList<OsmFireStation> Stations);
}
