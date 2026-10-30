using System.Globalization;
using System.Text.Json;

namespace Kopilot.Atc.Airspace;

/// <summary>
/// Lädt die Luftraumdaten aus dem Ordner "data": VAT-Spy (FIR-Grenzen und -Namen, Originalformat), die Streckensektoren
/// (atc-sectors.json aus tools/import_sectors.py) und die Airline-Datenbank von OpenFlights. Jede Quelle wird zuerst unter
/// %LOCALAPPDATA%\Kopilot\data gesucht (dort überlebt ein eigener, neuerer Stand jedes Update), dann neben der Kopilot.exe.
/// </summary>
public static class AirspaceLoader
{
    public const string DataFolder = "data";
    public const string VatSpyFolder = "vatspy";
    public const string VatSpyDat = "VATSpy.dat";
    public const string VatSpyBoundaries = "Boundaries.geojson";
    public const string SectorsFolder = "sectors";
    public const string SectorsFile = "atc-sectors.json";
    public const string OpenFlightsFolder = "openflights";
    public const string AirlinesFile = "airlines.dat";

    /// <summary>Datenordner in der Reihenfolge, in der gesucht wird.</summary>
    public static IReadOnlyList<string> Roots(string? dataDirectory) =>
        new[] { dataDirectory is null ? null : Path.Combine(dataDirectory, DataFolder), Path.Combine(AppContext.BaseDirectory, DataFolder) }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>FIR-Grenzen und Sektoren laden. Fehlt eine Quelle, bleibt sie leer – Meldungen landen in <paramref name="messages"/>.</summary>
    public static AirspaceDatabase Load(IReadOnlyList<string> roots, List<string> messages)
    {
        var sources = new List<string>();
        var firs = new List<AirspaceFir>();
        var vatspy = roots.Select(r => Path.Combine(r, VatSpyFolder))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, VatSpyDat)) && File.Exists(Path.Combine(d, VatSpyBoundaries)));
        if (vatspy is null)
        {
            messages.Add($"VAT-Spy-Daten ({VatSpyDat}, {VatSpyBoundaries}) nicht gefunden – FIR-Grenzen nur aus dem OFP.");
        }
        else
        {
            try
            {
                using var geo = File.OpenRead(Path.Combine(vatspy, VatSpyBoundaries));
                firs = ParseVatSpy(File.ReadAllText(Path.Combine(vatspy, VatSpyDat)), geo);
                sources.Add($"VAT-Spy: {vatspy} ({firs.Count(f => f.HasBoundary)} FIRs mit Grenzen)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                messages.Add($"VAT-Spy ({vatspy}): {ex.Message}");
            }
        }

        var sectors = new List<AirspaceSector>();
        var sectorFile = roots.Select(r => Path.Combine(r, SectorsFolder, SectorsFile)).FirstOrDefault(File.Exists);
        if (sectorFile is null)
        {
            messages.Add($"{SectorsFile} nicht gefunden – keine Sektoren, eine Frequenz je FIR.");
        }
        else
        {
            try
            {
                using var stream = File.OpenRead(sectorFile);
                var (parsed, info) = ParseSectors(stream, messages);
                sectors = parsed;
                sources.Add($"Sektoren: {sectorFile} ({sectors.Count}{(info is null ? "" : ", " + info)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                messages.Add($"{SectorsFile} ({sectorFile}): {ex.Message}");
            }
        }

        return new AirspaceDatabase(firs, sectors, sources);
    }

    /// <summary>Airline-Datenbank (OpenFlights). Fehlt sie, kommen Rufzeichen nur aus atc-templates.json.</summary>
    public static AirlineDirectory LoadAirlines(IReadOnlyList<string> roots, List<string> messages)
    {
        var file = roots.Select(r => Path.Combine(r, OpenFlightsFolder, AirlinesFile)).FirstOrDefault(File.Exists);
        if (file is null)
        {
            messages.Add($"{AirlinesFile} (OpenFlights) nicht gefunden – Rufzeichen nur aus atc-templates.json.");
            return AirlineDirectory.Empty;
        }

        try
        {
            using var reader = new StreamReader(file);
            return AirlineDirectory.Parse(reader, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add($"{AirlinesFile} ({file}): {ex.Message}");
            return AirlineDirectory.Empty;
        }
    }

    /// <summary>
    /// VAT-Spy im Originalformat: Namen aus dem Abschnitt [FIRs] von VATSpy.dat ("ICAO|NAME|PRÄFIX|GRENZE"), Grenzen aus
    /// Boundaries.geojson. Unterteilungen wie "EDGG-N" oder "CZQO-GOTA" (Präfix ist selbst eine FIR) bleiben außen vor –
    /// Kopilot nimmt dafür die Sektoren.
    /// </summary>
    public static List<AirspaceFir> ParseVatSpy(string datText, Stream boundaries)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var raw in datText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }

            if (section != "[FIRs]")
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 2 || parts[0].Trim().Length == 0)
            {
                continue;
            }

            var icao = parts[0].Trim();
            var boundary = parts.Length >= 4 && parts[3].Trim().Length > 0 ? parts[3].Trim() : icao;
            var name = parts[1].Trim();
            if (name.Length > 0)
            {
                names.TryAdd(boundary, name);
                names.TryAdd(icao, name);
            }
        }

        var polygons = new Dictionary<string, List<GeoPolygon>>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(boundaries))
        {
            foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("id", out var idElement)
                    || idElement.GetString() is not { Length: > 0 } id || !feature.TryGetProperty("geometry", out var geometry)
                    || geometry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!polygons.TryGetValue(id, out var list))
                {
                    list = [];
                    polygons[id] = list;
                }

                var coordinates = geometry.GetProperty("coordinates");
                switch (geometry.GetProperty("type").GetString())
                {
                    case "Polygon":
                        list.Add(GeoJsonPolygon(coordinates));
                        break;
                    case "MultiPolygon":
                        foreach (var polygon in coordinates.EnumerateArray())
                        {
                            list.Add(GeoJsonPolygon(polygon));
                        }

                        break;
                }
            }
        }

        bool IsSubdivision(string id) => id.IndexOf('-') is > 0 and var dash && polygons.ContainsKey(id[..dash]);

        var firs = new List<AirspaceFir>();
        foreach (var (id, list) in polygons)
        {
            if (!IsSubdivision(id))
            {
                firs.Add(new AirspaceFir(id.ToUpperInvariant(), names.GetValueOrDefault(id), list));
            }
        }

        // FIRs, die VAT-Spy nur mit Namen kennt
        foreach (var (id, name) in names)
        {
            if (!polygons.ContainsKey(id) && !id.Contains('-'))
            {
                firs.Add(new AirspaceFir(id.ToUpperInvariant(), name, []));
            }
        }

        return firs;
    }

    private static GeoPolygon GeoJsonPolygon(JsonElement rings)
    {
        GeoRing? outer = null;
        var holes = new List<GeoRing>();
        foreach (var ring in rings.EnumerateArray())
        {
            var lonLat = new List<double>();
            foreach (var point in ring.EnumerateArray())
            {
                lonLat.Add(point[0].GetDouble());
                lonLat.Add(point[1].GetDouble());
            }

            var geoRing = new GeoRing(lonLat);
            if (outer is null)
            {
                outer = geoRing;
            }
            else
            {
                holes.Add(geoRing);
            }
        }

        return new GeoPolygon(outer ?? new GeoRing([]), holes);
    }

    /// <summary>atc-sectors.json lesen. Rückgabe: Sektoren und eine kurze Herkunftsangabe (Quelle, Stand).</summary>
    public static (List<AirspaceSector> Sectors, string? Info) ParseSectors(Stream json, List<string> messages)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var sectors = new List<AirspaceSector>();
        var skipped = 0;
        foreach (var s in root.GetProperty("sectors").EnumerateArray())
        {
            try
            {
                var points = new List<double>();
                foreach (var v in s.GetProperty("points").EnumerateArray())
                {
                    points.Add(v.GetDouble());
                }

                var frequency = s.GetProperty("frequency").ValueKind == JsonValueKind.Number
                    ? s.GetProperty("frequency").GetDouble()
                    : double.Parse(s.GetProperty("frequency").GetString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture);
                if (points.Count < 6 || frequency is < 108.0 or >= 137.0)
                {
                    skipped++;
                    continue;
                }

                sectors.Add(new AirspaceSector(
                    (Str(s, "fir") ?? "").ToUpperInvariant(),
                    Str(s, "id") ?? "",
                    Str(s, "position") ?? Str(s, "id") ?? "",
                    Str(s, "name") ?? "",
                    frequency,
                    s.TryGetProperty("minFl", out var min) ? min.GetInt32() : 0,
                    s.TryGetProperty("maxFl", out var max) ? max.GetInt32() : 999,
                    new GeoRing(points)));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            messages.Add($"{SectorsFile}: {skipped} Sektoren unvollständig, übersprungen.");
        }

        var source = Str(root, "source");
        var commit = Str(root, "sourceCommit");
        var generated = Str(root, "generated");
        var info = string.Join(", ", new[]
        {
            source is null ? null : source.Split(',')[0].Trim(),
            commit is { Length: >= 7 } ? commit[..7] : null,
            generated,
        }.OfType<string>());
        return (sectors, info.Length == 0 ? null : info);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
