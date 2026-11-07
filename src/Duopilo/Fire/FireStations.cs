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

    /// <summary>Militärische Wache (in einer militärischen Fläche oder mit militärischem Betreiber), siehe <see cref="OsmFireStation.Military"/>.</summary>
    public bool Military { get; init; }

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

    /// <summary>Betreiber (operator=*), wenn eingetragen.</summary>
    public string? Operator { get; init; }

    /// <summary>
    /// Militärische Wache: liegt in einer militärischen Fläche (landuse=military, military=airfield/barracks/base …), trägt selbst
    /// military=* oder operator:type=military, oder der Betreiber ist das Militär (Bundeswehr, Air Force …). Test EDDK 05.11.: die Wache
    /// der Bundeswehr im militärischen Teil heißt in OSM nur „Flughafenfeuerwehr“.
    /// </summary>
    public bool Military { get; init; }
}

/// <summary>Was Duopilo aus OSM zu einem Flughafen braucht: Wachen, Gebäude und Betriebsstraßen.</summary>
public sealed record OsmAirportData(IReadOnlyList<OsmFireStation> Stations, IReadOnlyList<OsmShape> Buildings)
{
    public static readonly OsmAirportData Empty = new([], []);

    /// <summary>Betriebsstraßen (highway=service ohne Parkplatzgassen, Tunnel und Flächen) als Linien.</summary>
    public IReadOnlyList<OsmShape> Roads { get; init; } = [];

    /// <summary>Schranken und Tore auf diesen Straßen (barrier=gate, lift_gate …): dort endet die Luftseite meist.</summary>
    public IReadOnlyList<OsmPoint> Barriers { get; init; } = [];

    /// <summary>Zäune und Mauern im Flughafen (barrier=fence, wall): trennen Luft- und Landseite.</summary>
    public IReadOnlyList<OsmShape> Fences { get; init; } = [];

    /// <summary>Umriss des Flughafens (aeroway=aerodrome), Außenringe.</summary>
    public IReadOnlyList<OsmShape> Boundary { get; init; } = [];
}

/// <summary>
/// Feuerwachen und Gebäude eines Flughafens aus OpenStreetMap über die Overpass API: alles mit <c>amenity=fire_station</c> oder
/// <c>building=fire_station</c> innerhalb des Flughafen-Umrisses (<c>aeroway=aerodrome</c> mit dem ICAO-Code) samt Umriss, Tore
/// (<c>entrance</c>, <c>door</c>) bis 60 m darum, dazu alle Gebäude (außer Dächern/Carports) und Fluggastbrücken im Flughafen als
/// Hindernis für gerade Fahrten und die Betriebsstraßen (<c>highway=service</c>) mit ihren Schranken für Szenerien ohne Fahrwege.
/// Das Ergebnis liegt 30 Tage im Datenordner (cache\fire-stations), ein Fehlschlag 15 Minuten. Die
/// Abfrage läuft unabhängig vom Einsatz bis zum Ende (Overpass braucht zu Stoßzeiten über 20 s), damit der Zwischenspeicher beim
/// nächsten Mal gefüllt ist; mehrere Server (Komma-getrennt) werden der Reihe nach gefragt. Daten: © OpenStreetMap-Mitwirkende,
/// ODbL 1.0 (THIRD-PARTY-NOTICES.md); Duopilo liefert keine Daten mit, es fragt zur Laufzeit.
/// </summary>
public sealed class FireStationLookup
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    /// <summary>Fehlen im Zwischenspeicher neuere Teile (Gebäude, Straßen), so lange auf die neue Abfrage warten, sonst das Alte nehmen.</summary>
    private static readonly TimeSpan UpgradeWait = TimeSpan.FromSeconds(6);

    /// <summary>Militärische Flächen (military=*), in denen eine Wache als militärisch gilt; Übungsplätze und Sperrgebiete nicht (zu groß).</summary>
    private static readonly HashSet<string> MilitaryAreaValues = new(StringComparer.Ordinal) { "airfield", "barracks", "base", "naval_base", "office" };

    /// <summary>Wörter im Betreiber, die eine militärische Wache kennzeichnen (Groß-/Kleinschreibung egal).</summary>
    private static readonly string[] MilitaryOperators =
    [
        "bundeswehr", "luftwaffe", "air force", "airforce", "military", "militär", "militaer", "armee", "armée", "army", "navy",
        " raf ", "usaf", "ministry of defence", "bundesheer", "luchtmacht", "fuerza aérea", "aeronautica militare",
    ];

    /// <summary>Straßenarten (service=*), die keine Betriebsstraßen sind.</summary>
    private static readonly HashSet<string> SkippedServices = new(StringComparer.Ordinal) { "parking_aisle", "drive-through" };

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
            if (c.Ok && (c.Buildings is null || c.Roads is null || c.MilitaryChecked != true))
            {
                // Zwischenspeicher von vor den Gebäuden oder Straßen: kurz auf die neue Abfrage warten, sonst die Wachen gleich nehmen
                // (die Abfrage läuft weiter und füllt den Zwischenspeicher fürs nächste Mal)
                return Upgrade(Fetch(key, file, cached), FromCache(c));
            }

            return Task.FromResult(FromCache(c));
        }

        return Fetch(key, file, cached);
    }

    private static async Task<OsmAirportData> Upgrade(Task<OsmAirportData> fetch, OsmAirportData fallback)
    {
        await Task.WhenAny(fetch, Task.Delay(UpgradeWait));
        return fetch.IsCompletedSuccessfully ? fetch.Result : fallback;
    }

    private static OsmAirportData FromCache(CacheFile cache) =>
        new(cache.Stations, cache.Buildings ?? [])
        {
            Roads = cache.Roads ?? [],
            Barriers = cache.Barriers ?? [],
            Fences = cache.Fences ?? [],
            Boundary = cache.Boundary ?? [],
        };

    private static CacheFile ToCache(bool ok, OsmAirportData data, bool complete, bool militaryChecked) =>
        complete
            ? new(DateTimeOffset.UtcNow, ok, data.Stations, data.Buildings, data.Roads, data.Barriers, data.Fences, data.Boundary, militaryChecked ? true : null)
            : new(DateTimeOffset.UtcNow, ok, data.Stations);

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
                WriteCache(file, ToCache(true, data, complete: true, militaryChecked: true));
                _log.LogInformation(Loc.Log("log.fire.osmFetched"), icao, data.Stations.Count, data.Roads.Count, url);
                return data;
            }
            catch (Exception ex)
            {
                _log.LogWarning(Loc.Log("log.fire.osmFailed"), icao, url, ex.Message);
            }
        }

        // Alte Daten weiter nutzen; den Fehlschlag merken, damit nicht jeder Einsatz neu fragt
        var old = cached is { Ok: true } ok ? FromCache(ok) : OsmAirportData.Empty;
        WriteCache(file, ToCache(cached is { Ok: true }, old, complete: cached is { Ok: true, Buildings: not null, Roads: not null }, militaryChecked: cached?.MilitaryChecked == true));
        return old;
    }

    /// <summary>
    /// Overpass-Abfrage: Wachen mit Umriss (out geom), Tore und Türen bis 60 m darum, Gebäude (ohne Dächer/Carports/Vordächer) und
    /// Fluggastbrücken im Flughafen-Umriss, die Betriebsstraßen (highway=service ohne Flächen, Tunnel und überdachte Stücke) mit ihren
    /// Schranken, Zäune und Mauern und der Umriss des Flughafens selbst.
    /// </summary>
    public static string Query(string icao)
    {
        var code = icao.ToUpperInvariant().Replace("\"", "", StringComparison.Ordinal);
        return "[out:json][timeout:60];area[\"aeroway\"=\"aerodrome\"][\"icao\"=\"" + code + "\"]->.platz;" +
            "(nwr[\"amenity\"=\"fire_station\"](area.platz);nwr[\"building\"=\"fire_station\"](area.platz);)->.wachen;.wachen out geom;" +
            "(node(around.wachen:60)[\"entrance\"];node(around.wachen:60)[\"door\"];);out;" +
            "(way[\"building\"][\"building\"!~\"^(roof|carport|canopy)$\"](area.platz);way[\"aeroway\"=\"jet_bridge\"](area.platz););out geom;" +
            "way[\"highway\"=\"service\"][\"area\"!=\"yes\"][\"tunnel\"!~\".\"][\"covered\"!=\"yes\"](area.platz)->.strassen;.strassen out geom;" +
            "node(w.strassen)[\"barrier\"~\"^(gate|lift_gate|swing_gate|sliding_gate|bollard|block|chain|border_control|toll_booth)$\"];out;" +
            "way[\"barrier\"~\"^(fence|wall)$\"](area.platz);out geom;" +
            "(nwr[\"landuse\"=\"military\"](area.platz);nwr[\"military\"~\"^(airfield|barracks|base|naval_base|office)$\"](area.platz););out geom;" +
            "nwr[\"aeroway\"=\"aerodrome\"][\"icao\"=\"" + code + "\"];out geom;";
    }

    /// <summary>Nur die Wachen aus einer Antwort (siehe <see cref="ParseAll"/>).</summary>
    public static IReadOnlyList<OsmFireStation> Parse(string json) => ParseAll(json).Stations;

    /// <summary>
    /// Antwort der Overpass API lesen: Wachen (Knoten mit lat/lon, Flächen mit Umriss oder center), Tore, Gebäude und Brücken,
    /// Betriebsstraßen (ohne Parkplatzgassen und Drive-in), Schranken, Zäune und den Flughafen-Umriss.
    /// Gebäude und Einrichtung derselben Wache (näher als 80 m) zählen einmal, mit Namen und Umriss, wenn eins davon einen hat; die Tore
    /// gehören zur nächsten Wache bis 60 m.
    /// </summary>
    public static OsmAirportData ParseAll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stations = new List<OsmFireStation>();
        var doors = new List<OsmDoor>();
        var buildings = new List<OsmShape>();
        var roads = new List<OsmShape>();
        var barriers = new List<OsmPoint>();
        var fences = new List<OsmShape>();
        var boundary = new List<OsmShape>();
        var military = new List<OsmShape>();
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

                var stationOperator = Tag("operator");
                AddStation(stations, new OsmFireStation(Tag("name"), lat, lon)
                {
                    Outline = outline,
                    Operator = stationOperator,
                    Military = Tag("military") is { } militaryTag && militaryTag != "no" || Tag("operator:type") == "military" || IsMilitaryOperator(stationOperator),
                });
                buildings.AddRange(shapes.Where(s => s.Points.Count >= 2));
                continue;
            }

            if (Tag("highway") == "service")
            {
                if (Tag("area") != "yes" && (Tag("tunnel") is null or "no") && Tag("covered") != "yes" && !SkippedServices.Contains(Tag("service") ?? ""))
                {
                    roads.AddRange(shapes);
                }

                continue;
            }

            if (Tag("barrier") is { } barrier)
            {
                if (barrier is "fence" or "wall")
                {
                    fences.AddRange(shapes);
                }
                else if (element.TryGetProperty("lat", out var bLat) && element.TryGetProperty("lon", out var bLon))
                {
                    barriers.Add(new OsmPoint(bLat.GetDouble(), bLon.GetDouble()));
                }

                continue;
            }

            if (Tag("landuse") == "military" || MilitaryAreaValues.Contains(Tag("military") ?? ""))
            {
                military.AddRange(shapes);
                if (Tag("building") is null)
                {
                    continue;
                }
            }

            if (Tag("aeroway") == "aerodrome")
            {
                boundary.AddRange(shapes);
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

        // Wachen in einer militärischen Fläche
        var militaryRings = Rings(military);
        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[i];
            if (!station.Military && militaryRings.Any(r => Contains(r.Points, station.Latitude, station.Longitude)))
            {
                stations[i] = station with { Military = true };
            }
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

        return new OsmAirportData(stations, buildings) { Roads = roads, Barriers = barriers, Fences = fences, Boundary = Rings(boundary) };
    }

    /// <summary>Teilstücke eines Umrisses (Relation aus mehreren Wegen) zu geschlossenen Ringen zusammensetzen; offene Reste fallen weg.</summary>
    public static IReadOnlyList<OsmShape> Rings(IReadOnlyList<OsmShape> parts)
    {
        static bool Same(OsmPoint a, OsmPoint b) => DistanceM(a.Lat, a.Lon, b.Lat, b.Lon) < 0.5;
        var rings = parts.Where(p => p.Closed).ToList();
        var open = parts.Where(p => !p.Closed && p.Points.Count >= 2).Select(p => p.Points.ToList()).ToList();
        while (open.Count > 0)
        {
            var ring = open[0];
            open.RemoveAt(0);
            var grown = true;
            while (grown && !Same(ring[0], ring[^1]))
            {
                grown = false;
                for (var i = 0; i < open.Count; i++)
                {
                    var part = open[i];
                    if (Same(ring[^1], part[0]))
                    {
                        ring.AddRange(part.Skip(1));
                    }
                    else if (Same(ring[^1], part[^1]))
                    {
                        ring.AddRange(Enumerable.Reverse(part).Skip(1));
                    }
                    else if (Same(ring[0], part[^1]))
                    {
                        ring.InsertRange(0, part.Take(part.Count - 1));
                    }
                    else if (Same(ring[0], part[0]))
                    {
                        ring.InsertRange(0, Enumerable.Reverse(part).Take(part.Count - 1));
                    }
                    else
                    {
                        continue;
                    }

                    open.RemoveAt(i);
                    grown = true;
                    break;
                }
            }

            if (ring.Count >= 4 && Same(ring[0], ring[^1]))
            {
                rings.Add(new OsmShape(ring, true));
            }
        }

        return rings;
    }

    /// <summary>Linien in Metern im Rollweg-Netz (Straßen, Zäune, Umriss).</summary>
    public static IReadOnlyList<IReadOnlyList<(double East, double North)>> ToLines(IReadOnlyList<OsmShape> shapes, TaxiGraph graph) =>
        shapes.Select(s => (IReadOnlyList<(double East, double North)>)ToPoints(s.Points, graph)).ToList();

    /// <summary>Punkte in Metern im Rollweg-Netz (Schranken).</summary>
    public static IReadOnlyList<(double East, double North)> ToPoints(IReadOnlyList<OsmPoint> points, TaxiGraph graph) =>
        points.Select(p => Local(graph, p)).ToList();

    private static (double East, double North) Local(TaxiGraph graph, OsmPoint point)
    {
        var (east, north) = graph.ToLocal(point.Lat, point.Lon);
        return (east, north);
    }

    /// <summary>
    /// Wachen aus OSM als <see cref="FireStation"/> im Rollweg-Netz, mit Plätzen; nur bis 6 km vom Bezugspunkt, Name sonst "Wache n".
    /// Ist die Wache nur ein Punkt (amenity=fire_station ohne building=fire_station, z. B. die Flughafenfeuerwehr Düsseldorf), gilt das
    /// Gebäude aus <paramref name="buildings"/> als ihr Umriss, in dem der Punkt liegt. Ist ihre Fläche das Gelände (<see cref="HallInSite"/>),
    /// gilt das größte Gebäude darin als Halle, und die Plätze liegen auf der Seite zum Hof (Quelle „hall“).
    /// </summary>
    public static IReadOnlyList<FireStation> ToStations(IReadOnlyList<OsmFireStation> osm, TaxiGraph graph, IReadOnlyList<OsmShape>? buildings = null) =>
        [.. osm
            .Select((s, i) =>
            {
                var (east, north) = graph.ToLocal(s.Latitude, s.Longitude);
                var name = string.IsNullOrWhiteSpace(s.Name) ? "Wache " + (i + 1).ToString(CultureInfo.InvariantCulture) : s.Name.Trim();
                var hall = s.Outline is { } site ? HallInSite(site, buildings) : null;
                var outline = (hall ?? s.Outline ?? ContainingBuilding(buildings, s.Latitude, s.Longitude))?.Select(p => graph.ToLocal(p.Lat, p.Lon)).ToList();
                var doors = s.Doors.Select(d =>
                {
                    var (de, dn) = graph.ToLocal(d.Lat, d.Lon);
                    return (de, dn, d.Garage);
                }).ToList();

                // Auf einem Gelände zeigen die Tore zum Hof: zur Mitte des Geländes statt zum nächsten Rollpunkt
                (double East, double North)? facing = hall is not null && s.Outline is { } yard
                    ? Centroid([.. yard.Select(p => graph.ToLocal(p.Lat, p.Lon))])
                    : null;
                var (exits, source) = FireExits.ForOsm(east, north, outline, doors, graph, facing);
                if (hall is not null && source == "outline")
                {
                    source = "hall";
                }

                return new FireStation(name, east, north, s.Latitude, s.Longitude, "osm") { Exits = exits, ExitSource = source, Military = s.Military };
            })
            .Where(s => Math.Abs(s.EastM) < 6000 && Math.Abs(s.NorthM) < 6000)];

    /// <summary>Hat ein Gebäude in der Fläche einer Wache höchstens diesen Anteil an ihr, ist die Fläche das Gelände der Wache.</summary>
    public const double SiteBuildingShareMax = 0.8;

    /// <summary>Kleinere Gebäude auf dem Gelände zählen nicht als Halle (Schuppen, Trafostation).</summary>
    public const double MinHallM2 = 150;

    /// <summary>
    /// Ist die Fläche der Wache ihr Gelände statt ihr Gebäude, das größte Gebäude darauf als Halle, sonst null. Test EDDK 05.11.:
    /// amenity=fire_station liegt auf dem ganzen Grundstück (Hof und Zufahrten), Duopilo stellte die Fahrzeuge 4 m vor den
    /// Grundstücksrand aufs Vorfeld statt vor die Halle. Ein Gebäude zählt, wenn seine Mitte auf dem Gelände liegt.
    /// </summary>
    public static IReadOnlyList<OsmPoint>? HallInSite(IReadOnlyList<OsmPoint> outline, IReadOnlyList<OsmShape>? buildings)
    {
        if (buildings is null || outline.Count < 4)
        {
            return null;
        }

        var site = AreaM2(outline);
        return buildings
            .Where(b => b.Closed && b.Points.Count >= 4 && !SameShape(b.Points, outline))
            .Select(b => (b.Points, Area: AreaM2(b.Points)))
            .Where(b => b.Area >= MinHallM2 && b.Area <= site * SiteBuildingShareMax && Contains(outline, b.Points.Average(p => p.Lat), b.Points.Average(p => p.Lon)))
            .OrderByDescending(b => b.Area)
            .Select(b => b.Points)
            .FirstOrDefault();
    }

    /// <summary>Gebäude ohne die Gelände der Wachen: über den Hof fahren die Fahrzeuge, er ist kein Hindernis.</summary>
    public static IReadOnlyList<OsmShape> WithoutSites(IReadOnlyList<OsmShape> buildings, IReadOnlyList<OsmFireStation> stations)
    {
        var sites = stations.Where(s => s.Outline is { } o && HallInSite(o, buildings) is not null).Select(s => s.Outline!).ToList();
        return sites.Count == 0 ? buildings : [.. buildings.Where(b => !sites.Any(site => SameShape(b.Points, site)))];
    }

    private static bool SameShape(IReadOnlyList<OsmPoint> a, IReadOnlyList<OsmPoint> b) =>
        a.Count == b.Count && a.Zip(b).All(x => Math.Abs(x.First.Lat - x.Second.Lat) < 1e-8 && Math.Abs(x.First.Lon - x.Second.Lon) < 1e-8);

    /// <summary>Fläche eines Umrisses in Quadratmetern (eben genähert um den ersten Punkt).</summary>
    private static double AreaM2(IReadOnlyList<OsmPoint> points)
    {
        var lat0 = points[0].Lat;
        var cos = Math.Cos(lat0 * Math.PI / 180);
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            sum += (((a.Lon - points[0].Lon) * cos) * (b.Lat - lat0)) - (((b.Lon - points[0].Lon) * cos) * (a.Lat - lat0));
        }

        return Math.Abs(sum) / 2 * 111_320 * 111_320;
    }

    /// <summary>Flächenschwerpunkt eines Umrisses in Metern (bei entarteter Fläche der Mittelwert der Punkte).</summary>
    private static (double East, double North) Centroid(IReadOnlyList<(double East, double North)> points)
    {
        var (area, cx, cy) = (0.0, 0.0, 0.0);
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            var cross = (a.East * b.North) - (b.East * a.North);
            area += cross;
            cx += (a.East + b.East) * cross;
            cy += (a.North + b.North) * cross;
        }

        return Math.Abs(area) < 1e-6
            ? (points.Average(p => p.East), points.Average(p => p.North))
            : (cx / (3 * area), cy / (3 * area));
    }

    /// <summary>Ob der Betreiber das Militär ist (Bundeswehr, Air Force …).</summary>
    public static bool IsMilitaryOperator(string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return false;
        }

        var text = " " + operatorName.Trim().ToLowerInvariant() + " ";
        return MilitaryOperators.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Für zivile Einsätze ohne militärische Wachen, solange es eine andere gibt (auf einem reinen Militärflugplatz bleiben sie).
    /// <paramref name="dropped"/>: die weggelassenen.
    /// </summary>
    public static IReadOnlyList<FireStation> WithoutMilitary(IReadOnlyList<FireStation> stations, out IReadOnlyList<FireStation> dropped)
    {
        if (stations.All(s => s.Military) || stations.All(s => !s.Military))
        {
            dropped = [];
            return stations;
        }

        dropped = [.. stations.Where(s => s.Military)];
        return [.. stations.Where(s => !s.Military)];
    }

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

        if (merged.Operator is null && station.Operator is not null)
        {
            merged = merged with { Operator = station.Operator };
        }

        if (station.Military && !merged.Military)
        {
            merged = merged with { Military = true };
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

    /// <summary>
    /// Zwischenspeicher je Flughafen; <paramref name="Buildings"/> bzw. <paramref name="Roads"/> null = Datei von vor den Gebäuden bzw.
    /// Straßen (neu abfragen).
    /// </summary>
    private sealed record CacheFile(
        DateTimeOffset FetchedUtc,
        bool Ok,
        IReadOnlyList<OsmFireStation> Stations,
        IReadOnlyList<OsmShape>? Buildings = null,
        IReadOnlyList<OsmShape>? Roads = null,
        IReadOnlyList<OsmPoint>? Barriers = null,
        IReadOnlyList<OsmShape>? Fences = null,
        IReadOnlyList<OsmShape>? Boundary = null,
        bool? MilitaryChecked = null);
}
