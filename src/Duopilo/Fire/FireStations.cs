using System.Collections.Concurrent;
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
    /// <summary>
    /// Wo die Fahrzeuge nebeneinander erscheinen (je Fahrzeug ein Platz, der Reihe nach): vor den Toren, auf dem Fahrzeugstellplatz
    /// davor oder an der Hallenseite zur Straße (<see cref="FireExits"/>). Leer = alle am Mittelpunkt.
    /// </summary>
    public IReadOnlyList<FireExit> Exits { get; init; } = [];

    /// <summary>Woher die Plätze kommen: door, vehicle_parking, outline, centre oder parking.</summary>
    public string ExitSource { get; init; } = "centre";

    /// <summary>Wache aus einer freien Parkposition: die Fahrzeuge nebeneinander quer zur Ausrichtung der Position.</summary>
    public static FireStation FromParking(TaxiNode node) =>
        new(node.Parking?.Label ?? "?", node.EastM, node.NorthM, node.Latitude, node.Longitude, "parking", node)
        {
            Exits = FireExits.ForParking(node),
            ExitSource = "parking",
        };
}

/// <summary>Punkt aus OpenStreetMap.</summary>
public sealed record OsmPoint(double Lat, double Lon);

/// <summary>Tor oder Tür an einer Wache (entrance=*, door=*); <paramref name="Garage"/>: Hallentor (door=overhead, entrance=garage …).</summary>
public sealed record OsmDoor(double Lat, double Lon, bool Garage);

/// <summary>Gebäude (geschlossener Umriss) oder Fluggastbrücke (Linie) aus OpenStreetMap – Hindernis für gerade Fahrten.</summary>
public sealed record OsmShape(IReadOnlyList<OsmPoint> Points, bool Closed);

/// <summary>Feuerwache aus OpenStreetMap (amenity=fire_station oder building=fire_station), mit Umriss und Toren, wenn bekannt.</summary>
public sealed record OsmFireStation(string? Name, double Latitude, double Longitude)
{
    /// <summary>Gebäudeumriss (geschlossen), wenn die Wache in OSM eine Fläche ist.</summary>
    public IReadOnlyList<OsmPoint>? Outline { get; init; }

    /// <summary>Tore und Türen bis 60 m um die Wache.</summary>
    public IReadOnlyList<OsmDoor> Doors { get; init; } = [];
}

/// <summary>Was Duopilo aus OSM zu einem Flughafen braucht: Wachen und Gebäude.</summary>
public sealed record OsmAirportData(IReadOnlyList<OsmFireStation> Stations, IReadOnlyList<OsmShape> Buildings)
{
    public static readonly OsmAirportData Empty = new([], []);
}

/// <summary>
/// Feuerwachen und Gebäude eines Flughafens aus OpenStreetMap über die Overpass API: alles mit <c>amenity=fire_station</c> oder
/// <c>building=fire_station</c> innerhalb des Flughafen-Umrisses (<c>aeroway=aerodrome</c> mit dem ICAO-Code) samt Umriss, Tore
/// (<c>entrance</c>, <c>door</c>) bis 60 m darum, dazu alle Gebäude (außer Dächern/Carports) und Fluggastbrücken im Flughafen als
/// Hindernis für gerade Fahrten. Das Ergebnis liegt 30 Tage im Datenordner (cache\fire-stations), ein Fehlschlag 15 Minuten. Die
/// Abfrage läuft unabhängig vom Einsatz bis zum Ende (Overpass braucht zu Stoßzeiten über 20 s), damit der Zwischenspeicher beim
/// nächsten Mal gefüllt ist; mehrere Server (Komma-getrennt) werden der Reihe nach gefragt. Daten: © OpenStreetMap-Mitwirkende,
/// ODbL 1.0 (THIRD-PARTY-NOTICES.md); Duopilo liefert keine Daten mit, es fragt zur Laufzeit.
/// </summary>
public sealed class FireStationLookup
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    /// <summary>Tore so weit um die Wache gehören zu ihr.</summary>
    private const double DoorRadiusM = 60;

    private readonly HttpClient _http;
    private readonly string[] _urls;
    private readonly string _cacheDirectory;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Task<OsmAirportData>> _running = new(StringComparer.OrdinalIgnoreCase);

    public FireStationLookup(string overpassUrls, string dataDirectory, ILogger log)
    {
        _urls = overpassUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "fire-stations");
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppNames.UserAgent);
    }

    /// <summary>
    /// Wachen und Gebäude des Flughafens: aus dem Zwischenspeicher, sonst die laufende oder eine neue Abfrage. Leer, wenn OSM keine
    /// kennt oder nichts geht. Wer nicht warten will, nimmt die Aufgabe mit einer Frist (die Abfrage läuft trotzdem weiter).
    /// </summary>
    public Task<OsmAirportData> GetAsync(string icao)
    {
        var key = icao.Trim().ToUpperInvariant();
        var file = Path.Combine(_cacheDirectory, key + ".json");
        var cached = ReadCache(file);

        if (cached is { } c && DateTimeOffset.UtcNow - c.FetchedUtc < (c.Ok ? Fresh : RetryAfterFailure))
        {
            if (c.Ok && c.Buildings is null)
            {
                // Zwischenspeicher von vor den Gebäuden: die Wachen gleich nehmen, Umrisse und Gebäude im Hintergrund nachholen
                _ = Fetch(key, file, cached);
            }

            return Task.FromResult(new OsmAirportData(c.Stations, c.Buildings ?? []));
        }

        return Fetch(key, file, cached);
    }

    /// <summary>Die laufende Abfrage für den Flughafen oder eine neue (eine je Flughafen zugleich).</summary>
    private Task<OsmAirportData> Fetch(string key, string file, CacheFile? cached)
    {
        var task = _running.GetOrAdd(key, _ => Task.Run(() => FetchAsync(key, file, cached)));
        _ = task.ContinueWith(_ => _running.TryRemove(new KeyValuePair<string, Task<OsmAirportData>>(key, task)), TaskScheduler.Default);
        return task;
    }

    private async Task<OsmAirportData> FetchAsync(string icao, string file, CacheFile? cached)
    {
        foreach (var url in _urls)
        {
            try
            {
                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Query(icao))]);
                using var response = await _http.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                var data = ParseAll(await response.Content.ReadAsStringAsync());
                WriteCache(file, new CacheFile(DateTimeOffset.UtcNow, true, data.Stations, data.Buildings));
                _log.LogInformation(Loc.Log("log.fire.osmFetched"), icao, data.Stations.Count, url);
                return data;
            }
            catch (Exception ex)
            {
                _log.LogWarning(Loc.Log("log.fire.osmFailed"), icao, url, ex.Message);
            }
        }

        // Alte Daten weiter nutzen; den Fehlschlag merken, damit nicht jeder Einsatz neu fragt
        var old = cached is { Ok: true } ok ? new OsmAirportData(ok.Stations, ok.Buildings ?? []) : OsmAirportData.Empty;
        WriteCache(file, new CacheFile(DateTimeOffset.UtcNow, cached is { Ok: true }, old.Stations, cached is { Ok: true } ? old.Buildings : null));
        return old;
    }

    /// <summary>
    /// Overpass-Abfrage: Wachen mit Umriss (out geom), Tore und Türen bis 60 m darum, Gebäude (ohne Dächer/Carports/Vordächer) und
    /// Fluggastbrücken im Flughafen-Umriss.
    /// </summary>
    public static string Query(string icao) =>
        "[out:json][timeout:60];area[\"aeroway\"=\"aerodrome\"][\"icao\"=\"" + icao.ToUpperInvariant().Replace("\"", "", StringComparison.Ordinal) + "\"]->.platz;" +
        "(nwr[\"amenity\"=\"fire_station\"](area.platz);nwr[\"building\"=\"fire_station\"](area.platz);)->.wachen;.wachen out geom;" +
        "(node(around.wachen:60)[\"entrance\"];node(around.wachen:60)[\"door\"];);out;" +
        "(way[\"building\"][\"building\"!~\"^(roof|carport|canopy)$\"](area.platz);way[\"aeroway\"=\"jet_bridge\"](area.platz););out geom;";

    /// <summary>Nur die Wachen aus einer Antwort (siehe <see cref="ParseAll"/>).</summary>
    public static IReadOnlyList<OsmFireStation> Parse(string json) => ParseAll(json).Stations;

    /// <summary>
    /// Antwort der Overpass API lesen: Wachen (Knoten mit lat/lon, Flächen mit Umriss oder center), Tore, Gebäude und Brücken.
    /// Gebäude und Einrichtung derselben Wache (näher als 80 m) zählen einmal, mit Namen und Umriss, wenn eins davon einen hat; die Tore
    /// gehören zur nächsten Wache bis 60 m.
    /// </summary>
    public static OsmAirportData ParseAll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stations = new List<OsmFireStation>();
        var doors = new List<OsmDoor>();
        var buildings = new List<OsmShape>();
        if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return OsmAirportData.Empty;
        }

        foreach (var element in elements.EnumerateArray())
        {
            var tags = element.TryGetProperty("tags", out var t) ? t : default;
            string? Tag(string key) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(key, out var v) ? v.GetString() : null;
            var shapes = Shapes(element);

            if (Tag("amenity") == "fire_station" || Tag("building") == "fire_station")
            {
                var outline = shapes.Where(s => s.Closed).MaxBy(s => s.Points.Count)?.Points;
                var (lat, lon) = Centre(element, outline ?? shapes.FirstOrDefault()?.Points);
                if (double.IsNaN(lat))
                {
                    continue;
                }

                AddStation(stations, new OsmFireStation(Tag("name"), lat, lon) { Outline = outline });
                buildings.AddRange(shapes.Where(s => s.Points.Count >= 2));
                continue;
            }

            var entrance = Tag("entrance");
            var door = Tag("door");
            if ((entrance is not null || door is not null) && element.TryGetProperty("lat", out var dLat) && element.TryGetProperty("lon", out var dLon))
            {
                var garage = door is "overhead" or "garage" or "roller" or "sliding" or "folding" || entrance is "garage" or "service";
                doors.Add(new OsmDoor(dLat.GetDouble(), dLon.GetDouble(), garage));
                continue;
            }

            buildings.AddRange(shapes.Where(s => s.Points.Count >= 2));
        }

        // Tore der nächsten Wache zuordnen
        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[i];
            var mine = doors
                .Where(d => DistanceM(station.Latitude, station.Longitude, d.Lat, d.Lon) <= DoorRadiusM)
                .Where(d => stations.MinBy(s => DistanceM(s.Latitude, s.Longitude, d.Lat, d.Lon)) == station)
                .ToList();
            if (mine.Count > 0)
            {
                stations[i] = station with { Doors = mine };
            }
        }

        return new OsmAirportData(stations, buildings);
    }

    /// <summary>
    /// Wachen aus OSM als <see cref="FireStation"/> im Rollweg-Netz, mit Plätzen; nur bis 6 km vom Bezugspunkt, Name sonst "Wache n".
    /// Ist die Wache nur ein Punkt (amenity=fire_station ohne building=fire_station, z. B. die Flughafenfeuerwehr Düsseldorf), gilt das
    /// Gebäude aus <paramref name="buildings"/> als ihr Umriss, in dem der Punkt liegt.
    /// </summary>
    public static IReadOnlyList<FireStation> ToStations(IReadOnlyList<OsmFireStation> osm, TaxiGraph graph, IReadOnlyList<OsmShape>? buildings = null) =>
        [.. osm
            .Select((s, i) =>
            {
                var (east, north) = graph.ToLocal(s.Latitude, s.Longitude);
                var name = string.IsNullOrWhiteSpace(s.Name) ? "Wache " + (i + 1).ToString(CultureInfo.InvariantCulture) : s.Name.Trim();
                var outline = (s.Outline ?? ContainingBuilding(buildings, s.Latitude, s.Longitude))?.Select(p => graph.ToLocal(p.Lat, p.Lon)).ToList();
                var doors = s.Doors.Select(d =>
                {
                    var (de, dn) = graph.ToLocal(d.Lat, d.Lon);
                    return (de, dn, d.Garage);
                }).ToList();
                var (exits, source) = FireExits.ForOsm(east, north, outline, doors, graph);
                return new FireStation(name, east, north, s.Latitude, s.Longitude, "osm") { Exits = exits, ExitSource = source };
            })
            .Where(s => Math.Abs(s.EastM) < 6000 && Math.Abs(s.NorthM) < 6000)];

    /// <summary>Das kleinste geschlossene Gebäude, in dem der Punkt liegt.</summary>
    private static IReadOnlyList<OsmPoint>? ContainingBuilding(IReadOnlyList<OsmShape>? buildings, double lat, double lon) =>
        buildings?
            .Where(b => b.Closed && Contains(b.Points, lat, lon))
            .MinBy(b => (b.Points.Max(p => p.Lat) - b.Points.Min(p => p.Lat)) * (b.Points.Max(p => p.Lon) - b.Points.Min(p => p.Lon)))?
            .Points;

    private static bool Contains(IReadOnlyList<OsmPoint> polygon, double lat, double lon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Lat > lat) != (b.Lat > lat) && lon < ((b.Lon - a.Lon) * (lat - a.Lat) / (b.Lat - a.Lat)) + a.Lon)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Gebäude und Brücken als Umrisse in Metern (Brücken als 4 m breite Bänder), nur bis 6 km vom Bezugspunkt.</summary>
    public static IReadOnlyList<IReadOnlyList<(double East, double North)>> ToBuildings(IReadOnlyList<OsmShape> shapes, TaxiGraph graph)
    {
        var result = new List<IReadOnlyList<(double East, double North)>>();
        foreach (var shape in shapes)
        {
            var points = shape.Points.Select(p => graph.ToLocal(p.Lat, p.Lon)).ToList();
            if (points.Count < 2 || points.All(p => Math.Abs(p.EastM) > 6000 || Math.Abs(p.NorthM) > 6000))
            {
                continue;
            }

            if (shape.Closed && points.Count >= 4)
            {
                result.Add(points);
                continue;
            }

            for (var i = 0; i + 1 < points.Count; i++)
            {
                result.Add(Band(points[i], points[i + 1], 2));
            }
        }

        return result;
    }

    /// <summary>Rechteck um eine Strecke, halbe Breite <paramref name="halfWidthM"/>.</summary>
    private static List<(double East, double North)> Band((double East, double North) a, (double East, double North) b, double halfWidthM)
    {
        var (dx, dy) = (b.East - a.East, b.North - a.North);
        var length = Math.Max(0.01, Math.Sqrt((dx * dx) + (dy * dy)));
        var (nx, ny) = (-dy / length * halfWidthM, dx / length * halfWidthM);
        return [(a.East + nx, a.North + ny), (b.East + nx, b.North + ny), (b.East - nx, b.North - ny), (a.East - nx, a.North - ny), (a.East + nx, a.North + ny)];
    }

    /// <summary>Linien eines Elements: Weg (geometry) oder Relation (Mitglieder mit geometry); geschlossen, wenn Anfang = Ende.</summary>
    private static List<OsmShape> Shapes(JsonElement element)
    {
        var shapes = new List<OsmShape>();
        void Add(JsonElement geometry)
        {
            var points = geometry.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("lat", out _) && p.TryGetProperty("lon", out _))
                .Select(p => new OsmPoint(p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()))
                .ToList();
            if (points.Count >= 2)
            {
                var closed = points.Count >= 4 && DistanceM(points[0].Lat, points[0].Lon, points[^1].Lat, points[^1].Lon) < 0.5;
                shapes.Add(new OsmShape(points, closed));
            }
        }

        if (element.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Array)
        {
            Add(geometry);
        }

        if (element.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                var role = member.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role is "outer" or "" or null && member.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Array)
                {
                    Add(g);
                }
            }
        }

        return shapes;
    }

    /// <summary>Lage eines Elements: lat/lon, center, sonst die Mitte des Umrisses.</summary>
    private static (double Lat, double Lon) Centre(JsonElement element, IReadOnlyList<OsmPoint>? outline)
    {
        var at = element.TryGetProperty("center", out var center) ? center : element;
        if (at.TryGetProperty("lat", out var lat) && at.TryGetProperty("lon", out var lon))
        {
            return (lat.GetDouble(), lon.GetDouble());
        }

        if (outline is { Count: > 0 })
        {
            var points = outline.Count > 1 && outline[0] == outline[^1] ? outline.Take(outline.Count - 1).ToList() : [.. outline];
            return (points.Average(p => p.Lat), points.Average(p => p.Lon));
        }

        return (double.NaN, double.NaN);
    }

    private static void AddStation(List<OsmFireStation> found, OsmFireStation station)
    {
        var same = found.FindIndex(f => DistanceM(f.Latitude, f.Longitude, station.Latitude, station.Longitude) < 80);
        if (same < 0)
        {
            found.Add(station);
            return;
        }

        var merged = found[same];
        if (merged.Name is null && station.Name is not null)
        {
            merged = merged with { Name = station.Name };
        }

        if (merged.Outline is null && station.Outline is not null)
        {
            // Der Umriss des Gebäudes ist die bessere Lage als der Punkt der Einrichtung
            var outline = station.Outline;
            merged = merged with
            {
                Outline = outline,
                Latitude = outline.Take(outline.Count - 1).Average(p => p.Lat),
                Longitude = outline.Take(outline.Count - 1).Average(p => p.Lon),
            };
        }

        found[same] = merged;
    }

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

    /// <summary>Zwischenspeicher je Flughafen; <paramref name="Buildings"/> null = Datei von vor den Gebäuden (neu abfragen).</summary>
    private sealed record CacheFile(DateTimeOffset FetchedUtc, bool Ok, IReadOnlyList<OsmFireStation> Stations, IReadOnlyList<OsmShape>? Buildings = null);
}
