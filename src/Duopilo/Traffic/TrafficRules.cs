using System.Globalization;
using System.Text.Json;
using Duopilo.Localization;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Was ein Flugzeug auf einer Bahn tut – danach richten sich die Bahnregeln.</summary>
public enum TrafficOperation
{
    Arrival,
    Departure,
}

/// <summary>Wirbelschleppen-Kategorie nach ICAO (L, M, H, J).</summary>
public enum TrafficWake
{
    Light,
    Medium,
    Heavy,
    Super,
}

/// <summary>Art eines Flugplatzes laut OurAirports.</summary>
public enum TrafficAirportKind
{
    Large,
    Medium,
    Small,
    Heliport,
    Seaplane,
    Balloon,
    Closed,
}

/// <summary>Einordnung eines Flugplatzes (data\ourairports\airports.csv).</summary>
/// <param name="Scheduled">Linienverkehr laut OurAirports.</param>
/// <param name="Military">Militärflugplatz nach dem Namen ("Air Base", "AFB", "RAF" …), nie bei Linienverkehr.</param>
public sealed record TrafficAirportInfo(TrafficAirportKind Kind, bool Scheduled, bool Military);

/// <summary>
/// Alle Flugplätze mit Art, Linienverkehr und Militär – erzeugt von tools/import_airports.py aus OurAirports (gemeinfrei).
/// Unbekannte Plätze (in MSFS, nicht in OurAirports) haben keine Einordnung und bekommen allen Verkehr.
/// </summary>
public sealed class TrafficAirportDirectory
{
    public const string Folder = "ourairports";

    public const string FileName = "airports.csv";

    private readonly Dictionary<string, TrafficAirportInfo> _airports;

    public TrafficAirportDirectory(IReadOnlyDictionary<string, TrafficAirportInfo>? airports = null) =>
        _airports = new(airports ?? new Dictionary<string, TrafficAirportInfo>(), StringComparer.OrdinalIgnoreCase);

    public int Count => _airports.Count;

    public TrafficAirportInfo? Find(string icao) => _airports.GetValueOrDefault(icao.Trim());

    /// <summary>Zeilen "ident,kind,scheduled,military"; Kommentare (#) und Kopfzeile werden übersprungen, kaputte Zeilen auch.</summary>
    public static TrafficAirportDirectory Parse(TextReader reader)
    {
        var airports = new Dictionary<string, TrafficAirportInfo>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("ident,", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 4 || parts[0].Length == 0 || parts[1].Length != 1 || KindOf(parts[1][0]) is not { } kind)
            {
                continue;
            }

            airports[parts[0]] = new TrafficAirportInfo(kind, parts[2] == "1", parts[3] == "1");
        }

        return new TrafficAirportDirectory(airports);
    }

    /// <summary>Erste airports.csv in den Datenordnern (eigener vor dem Programmordner); fehlt sie, bleibt das Verzeichnis leer.</summary>
    public static TrafficAirportDirectory Load(IReadOnlyList<string> roots, List<string> messages)
    {
        var path = roots.Select(r => Path.Combine(r, Folder, FileName)).FirstOrDefault(File.Exists);
        if (path is null)
        {
            messages.Add(Loc.T("traffic.rules.directoryMissing", Path.Combine(Folder, FileName)));
            return new TrafficAirportDirectory();
        }

        try
        {
            using var reader = new StreamReader(path);
            return Parse(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {ex.Message}"));
            return new TrafficAirportDirectory();
        }
    }

    private static TrafficAirportKind? KindOf(char code) => code switch
    {
        'L' => TrafficAirportKind.Large,
        'M' => TrafficAirportKind.Medium,
        'S' => TrafficAirportKind.Small,
        'H' => TrafficAirportKind.Heliport,
        'W' => TrafficAirportKind.Seaplane,
        'B' => TrafficAirportKind.Balloon,
        'C' => TrafficAirportKind.Closed,
        _ => null,
    };
}

/// <summary>Eine Regel für eine oder mehrere Bahnen eines Flughafens.</summary>
/// <param name="Runways">Bahnbezeichnungen, für die die Regel gilt, z. B. ["07L", "25R"].</param>
/// <param name="Operations">Erlaubte Betriebsarten ("arrival", "departure"); null = alle, leer = gesperrt.</param>
/// <param name="Categories">Erlaubte Kategorien ("airliner", "generalaviation"); null = alle.</param>
/// <param name="ExcludeTypes">Gesperrte ICAO-Typen, "*" am Ende als Präfix (z. B. "B74*").</param>
/// <param name="MaxWake">Schwerste erlaubte Wirbelschleppen-Kategorie ("light", "medium", "heavy", "super"); null = alle.</param>
/// <param name="Note">Begründung oder Quelle, erscheint in der Meldung.</param>
public sealed record TrafficRunwayRule(
    IReadOnlyList<string> Runways,
    IReadOnlyList<string>? Operations = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? ExcludeTypes = null,
    string? MaxWake = null,
    string? Note = null);

/// <summary>Ein Teil des Verkehrsmix eines Flughafens: diese Typen mit diesem Gewicht.</summary>
/// <param name="Types">ICAO-Typen, "*" am Ende als Präfix, "*" allein = jeder Typ.</param>
/// <param name="Weight">Anteil relativ zu den anderen Teilen, z. B. 8 zu 2.</param>
public sealed record TrafficMixEntry(IReadOnlyList<string> Types, double Weight = 1, string? Note = null);

/// <summary>Regeln eines Flughafens.</summary>
/// <param name="Categories">Erlaubte Kategorien am Platz; null = aus der Einordnung (OurAirports), leer = kein eigener Verkehr.</param>
/// <param name="Military">Militärflugplatz ja/nein statt der Erkennung am Namen.</param>
/// <param name="Mix">Verkehrsmix: nur diese Typen, ausgewählt nach Gewicht; null = alle passenden Typen.</param>
public sealed record TrafficAirportRule(
    IReadOnlyList<TrafficRunwayRule>? Runways = null,
    IReadOnlyList<string>? Categories = null,
    bool? Military = null,
    IReadOnlyList<TrafficMixEntry>? Mix = null,
    string? Note = null);

/// <summary>Inhalt von airport-rules.json: Regeln je ICAO-Code.</summary>
public sealed record TrafficRulesFile(IReadOnlyDictionary<string, TrafficAirportRule> Airports);

/// <summary>Ein Grund, warum ein Flugzeug an einem Platz oder auf einer Bahn nicht verkehren darf.</summary>
/// <param name="Code">airport, airport_category, mix, operation, category, type, wake, too_short oder too_narrow.</param>
/// <param name="Text">Für den Piloten (Sprache der Oberfläche).</param>
public sealed record TrafficRuleViolation(string Code, string Text);

/// <summary>
/// Welche Flugzeuge wo verkehren dürfen, für alle Flughäfen:
/// <list type="bullet">
/// <item>Einordnung aus OurAirports (<see cref="TrafficAirportDirectory"/>): Militärflugplätze, Heliports, Wasser- und Ballonplätze
/// und geschlossene Plätze bekommen keinen eigenen Verkehr, kleine Plätze und Plätze ohne Linienverkehr nur Allgemeine Luftfahrt.</item>
/// <item>Regeln je Flughafen aus <c>data\traffic\airport-rules.json</c> (mitgeliefert im Programmordner, eigene im Datenordner ersetzen
/// die Regeln eines Flughafens): Kategorien und Militär statt der Einordnung, Verkehrsmix (EDHI: überwiegend Beluga), Bahnregeln
/// (EDDF 07L/25R nur Landungen ohne B747, A380, MD-11, An-124, L-1011; 18 nur Starts).</item>
/// <item>Länge und Breite der Bahn gegen den Bedarf des Typs.</item>
/// </list>
/// </summary>
public sealed class TrafficRules
{
    public const string Folder = "traffic";

    public const string FileName = "airport-rules.json";

    /// <summary>Landestrecke ≈ Faktor × Vapp² (m, kt): A320 135 kt → rund 1370 m, 737-800 145 kt → 1580 m, C172 65 kt → 320 m.</summary>
    public const double LandingFactor = 0.075;

    /// <summary>Startstrecke als Vielfaches der Landestrecke: A320 rund 1910 m, MD-11 rund 2520 m.</summary>
    public const double TakeoffFactor = 1.4;

    /// <summary>Mindestbreite Heavy und Super (ICAO-Code E/F).</summary>
    public const double WideBodyWidthM = 45;

    /// <summary>Mindestbreite übrige Verkehrsflugzeuge (ICAO-Code C).</summary>
    public const double AirlinerWidthM = 30;

    private static readonly TrafficCategory[] AllCategories = [TrafficCategory.Airliner, TrafficCategory.GeneralAviation];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>ICAO-Typen der Kategorie Super.</summary>
    private static readonly HashSet<string> SuperTypes = new(StringComparer.OrdinalIgnoreCase) { "A388", "A225" };

    /// <summary>ICAO-Typen der Kategorie Heavy (Präfix mit "*").</summary>
    private static readonly string[] HeavyTypes =
    [
        "A30B", "A306", "A310", "A33*", "A34*", "A35*", "A3ST", "A124", "B74*", "B76*", "B77*", "B78*", "MD11", "DC10", "L101",
        "IL96", "C17", "C5*", "A400",
    ];

    private readonly Dictionary<string, TrafficAirportRule> _airports;

    public TrafficRules(IReadOnlyDictionary<string, TrafficAirportRule>? airports = null, TrafficAirportDirectory? directory = null)
    {
        _airports = new(airports ?? new Dictionary<string, TrafficAirportRule>(), StringComparer.OrdinalIgnoreCase);
        Directory = directory ?? new TrafficAirportDirectory();
    }

    /// <summary>Einordnung aller Flugplätze.</summary>
    public TrafficAirportDirectory Directory { get; }

    /// <summary>Flughäfen mit eigenen Regeln.</summary>
    public IReadOnlyCollection<string> Airports => _airports.Keys;

    /// <summary>Regeln aus JSON; wirft <see cref="JsonException"/> bei kaputter Datei.</summary>
    public static TrafficRules Parse(string json, TrafficAirportDirectory? directory = null) =>
        new(JsonSerializer.Deserialize<TrafficRulesFile>(json, Json)?.Airports, directory);

    /// <summary>
    /// Lädt airport-rules.json aus den Datenordnern (<paramref name="roots"/> wie <c>AirspaceLoader.Roots</c>: zuerst der eigene, dann
    /// der Programmordner). Regeln eines Flughafens aus dem eigenen Ordner ersetzen die mitgelieferten. Fehler landen in
    /// <paramref name="messages"/>.
    /// </summary>
    public static TrafficRules Load(IReadOnlyList<string> roots, List<string> messages, TrafficAirportDirectory? directory = null)
    {
        var airports = new Dictionary<string, TrafficAirportRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Reverse())
        {
            var path = Path.Combine(root, Folder, FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                foreach (var (icao, rules) in Parse(File.ReadAllText(path))._airports)
                {
                    airports[icao] = rules;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                messages.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {ex.Message}"));
            }
        }

        return new TrafficRules(airports, directory);
    }

    /// <summary>Regeln des Flughafens aus der Datei, sonst null.</summary>
    public TrafficAirportRule? RuleFor(string airport) => _airports.GetValueOrDefault(airport.Trim());

    /// <summary>Verkehrsmix des Flughafens, sonst null.</summary>
    public IReadOnlyList<TrafficMixEntry>? MixFor(string airport) => RuleFor(airport)?.Mix is { Count: > 0 } mix ? mix : null;

    /// <summary>
    /// Welche Kategorien am Platz verkehren dürfen, und warum es weniger als alle sind (Schlüssel eines Grundtexts, null = alle).
    /// </summary>
    public (IReadOnlyList<TrafficCategory> Categories, string? Reason) AirportTraffic(string airport)
    {
        var rule = RuleFor(airport);
        if (rule?.Categories is { } names)
        {
            var categories = names.Select(ParseCategory).OfType<TrafficCategory>().Distinct().ToList();
            return (categories, categories.Count == AllCategories.Length ? null : "traffic.rules.reason.rules");
        }

        var info = Directory.Find(airport);
        if (rule?.Military ?? info?.Military ?? false)
        {
            return ([], "traffic.rules.reason.military");
        }

        return info?.Kind switch
        {
            null => (AllCategories, null),
            TrafficAirportKind.Heliport => ([], "traffic.rules.reason.heliport"),
            TrafficAirportKind.Seaplane => ([], "traffic.rules.reason.seaplane"),
            TrafficAirportKind.Balloon => ([], "traffic.rules.reason.balloon"),
            TrafficAirportKind.Closed => ([], "traffic.rules.reason.closed"),
            TrafficAirportKind.Small => ([TrafficCategory.GeneralAviation], "traffic.rules.reason.small"),
            _ when !info.Scheduled => ([TrafficCategory.GeneralAviation], "traffic.rules.reason.noScheduled"),
            _ => (AllCategories, null),
        };
    }

    /// <summary>Wirbelschleppen-Kategorie eines Typs: Super und Heavy aus der Liste, Allgemeine Luftfahrt Light, sonst Medium.</summary>
    public static TrafficWake WakeOf(string? icaoType, TrafficCategory category)
    {
        if (icaoType is { Length: > 0 } type)
        {
            if (SuperTypes.Contains(type))
            {
                return TrafficWake.Super;
            }

            if (HeavyTypes.Any(h => Matches(h, type)))
            {
                return TrafficWake.Heavy;
            }
        }

        return category == TrafficCategory.GeneralAviation ? TrafficWake.Light : TrafficWake.Medium;
    }

    /// <summary>Nötige Landestrecke in m aus der Anfluggeschwindigkeit.</summary>
    public static double RequiredLandingM(double approachKt) => LandingFactor * approachKt * approachKt;

    /// <summary>Nötige Startstrecke in m aus der Anfluggeschwindigkeit.</summary>
    public static double RequiredTakeoffM(double approachKt) => RequiredLandingM(approachKt) * TakeoffFactor;

    /// <summary>Nötige Bahnbreite in m, 0 = keine Prüfung (Allgemeine Luftfahrt).</summary>
    public static double RequiredWidthM(TrafficWake wake, TrafficCategory category) => wake switch
    {
        TrafficWake.Super or TrafficWake.Heavy => WideBodyWidthM,
        _ when category == TrafficCategory.Airliner => AirlinerWidthM,
        _ => 0,
    };

    /// <summary>Gehört der Typ zum Mix? Ohne Mix immer; ein unbekannter Typ nur bei einem Teil mit "*".</summary>
    public static bool InMix(IReadOnlyList<TrafficMixEntry>? mix, string? icaoType) =>
        mix is null || mix.Any(e => MatchesEntry(e, icaoType));

    /// <summary>Passt der Typ zu einem Teil des Mix?</summary>
    public static bool MatchesEntry(TrafficMixEntry entry, string? icaoType) =>
        (entry.Types ?? []).Any(t => t.Trim() == "*" || (icaoType is { Length: > 0 } type && Matches(t, type)));

    /// <summary>
    /// Gründe, warum das Flugzeug (<paramref name="icaoType"/> darf null sein) an diesem Platz und auf dieser Bahn nicht für
    /// <paramref name="operation"/> verkehren darf; leer = erlaubt.
    /// </summary>
    public IReadOnlyList<TrafficRuleViolation> Check(
        RunwayEnd runway,
        TrafficOperation operation,
        string? icaoType,
        TrafficCategory category,
        double approachKt)
    {
        var violations = new List<TrafficRuleViolation>();
        var wake = WakeOf(icaoType, category);
        var label = icaoType is { Length: > 0 } ? icaoType.ToUpperInvariant() : CategoryLabel(category);
        var airport = RuleFor(runway.Airport);
        var airportNote = string.IsNullOrWhiteSpace(airport?.Note) ? "" : $" ({airport.Note.Trim()})";

        // Platz: Militär, Heliport, geschlossen, nur Allgemeine Luftfahrt …
        var (categories, reason) = AirportTraffic(runway.Airport);
        var reasonText = reason is null ? "" : Reason(reason);
        if (categories.Count == 0)
        {
            violations.Add(new("airport", Loc.T("traffic.rules.airportNone", runway.Airport, reasonText, airportNote)));
        }
        else if (!categories.Contains(category))
        {
            violations.Add(new("airport_category", Loc.T("traffic.rules.airportCategory", runway.Airport, string.Join(", ", categories.Select(CategoryLabel)), reasonText, airportNote)));
        }

        if (MixFor(runway.Airport) is { } mix && !InMix(mix, icaoType))
        {
            var types = string.Join(", ", mix.SelectMany(e => e.Types ?? []).Distinct(StringComparer.OrdinalIgnoreCase));
            violations.Add(new("mix", Loc.T("traffic.rules.mix", runway.Airport, label, types, airportNote)));
        }

        // Bahn
        var designation = AirportRunway.Normalize(runway.Designation);
        foreach (var rule in (airport?.Runways ?? []).Where(r => (r.Runways ?? []).Any(d => AirportRunway.Normalize(d) == designation)))
        {
            var note = string.IsNullOrWhiteSpace(rule.Note) ? "" : $" ({rule.Note.Trim()})";
            if (rule.Operations is { } operations && !operations.Any(o => string.Equals(o, OperationName(operation), StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new("operation", operations.Count == 0
                    ? Loc.T("traffic.rules.closed", runway.Designation, note)
                    : Loc.T("traffic.rules.operation", runway.Designation, string.Join(", ", operations.Select(OperationLabel)), note)));
            }

            if (rule.Categories is { } runwayCategories && !runwayCategories.Any(c => ParseCategory(c) == category))
            {
                violations.Add(new("category", Loc.T("traffic.rules.category", runway.Designation, CategoryLabel(category), note)));
            }

            if (icaoType is { Length: > 0 } type && rule.ExcludeTypes?.FirstOrDefault(t => Matches(t, type)) is not null)
            {
                violations.Add(new("type", Loc.T("traffic.rules.type", runway.Designation, label, note)));
            }

            if (ParseWake(rule.MaxWake) is { } maxWake && wake > maxWake)
            {
                violations.Add(new("wake", Loc.T("traffic.rules.wake", runway.Designation, label, wake.ToString(), maxWake.ToString(), note)));
            }
        }

        var needM = operation == TrafficOperation.Departure ? RequiredTakeoffM(approachKt) : RequiredLandingM(approachKt);
        if (runway.LengthM < needM)
        {
            violations.Add(new("too_short", Loc.T("traffic.rules.tooShort", runway.Designation, runway.LengthM, label, needM)));
        }

        var widthM = RequiredWidthM(wake, category);
        if (runway.WidthM > 1 && widthM > 0 && runway.WidthM < widthM - 0.5)
        {
            violations.Add(new("too_narrow", Loc.T("traffic.rules.tooNarrow", runway.Designation, runway.WidthM, label, widthM)));
        }

        return violations;
    }

    /// <summary>
    /// Modell für einen Test ohne Titel: unter den installierten, freigegebenen Modellen die, die an Platz und Bahn erlaubt sind. Mit
    /// Verkehrsmix wählt <paramref name="random"/> einen Teil nach Gewicht (nur Teile mit passenden Modellen), darin ein Modell mit dem
    /// besten Status. Ohne Mix: <paramref name="preferred"/>, wenn erlaubt, sonst das erste mit dem besten Status. Keins = null.
    /// </summary>
    public AiModel? PickModel(
        IReadOnlyList<AiModel> models,
        RunwayEnd runway,
        TrafficOperation operation,
        Random random,
        AiModel? preferred = null)
    {
        var allowed = models
            .Where(m => !TrafficModels.IsExcluded(m.Title, m.Livery))
            .Select(m => (Model: m, Type: TrafficModels.TypeOf(m.Title, m.Livery)))
            .Where(m => m.Type is not null && Check(runway, operation, m.Type.IcaoType, m.Type.Category, m.Type.ApproachKt).Count == 0)
            .Select(m => (m.Model, Type: m.Type!))
            .ToList();
        if (allowed.Count == 0)
        {
            return null;
        }

        if (MixFor(runway.Airport) is { } mix)
        {
            var entries = mix
                .Where(e => e.Weight > 0)
                .Select(e => (Entry: e, Models: allowed.Where(m => MatchesEntry(e, m.Type.IcaoType)).ToList()))
                .Where(e => e.Models.Count > 0)
                .ToList();
            if (entries.Count == 0)
            {
                return null;
            }

            var roll = random.NextDouble() * entries.Sum(e => e.Entry.Weight);
            var chosen = entries[^1];
            foreach (var entry in entries)
            {
                if ((roll -= entry.Entry.Weight) < 0)
                {
                    chosen = entry;
                    break;
                }
            }

            var best = chosen.Models.Max(m => m.Type.Status);
            var candidates = chosen.Models.Where(m => m.Type.Status == best).ToList();
            return candidates[random.Next(candidates.Count)].Model;
        }

        if (preferred is not null && allowed.Any(m => m.Model == preferred))
        {
            return preferred;
        }

        return allowed.OrderByDescending(m => m.Type.Status).First().Model;
    }

    public static string OperationName(TrafficOperation operation) => operation == TrafficOperation.Departure ? "departure" : "arrival";

    public static string CategoryName(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? "generalaviation" : "airliner";

    private static TrafficCategory? ParseCategory(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "airliner" => TrafficCategory.Airliner,
        "generalaviation" or "general_aviation" or "ga" => TrafficCategory.GeneralAviation,
        _ => null,
    };

    private static string Reason(string key) => key switch
    {
        "traffic.rules.reason.military" => Loc.T("traffic.rules.reason.military"),
        "traffic.rules.reason.heliport" => Loc.T("traffic.rules.reason.heliport"),
        "traffic.rules.reason.seaplane" => Loc.T("traffic.rules.reason.seaplane"),
        "traffic.rules.reason.balloon" => Loc.T("traffic.rules.reason.balloon"),
        "traffic.rules.reason.closed" => Loc.T("traffic.rules.reason.closed"),
        "traffic.rules.reason.small" => Loc.T("traffic.rules.reason.small"),
        "traffic.rules.reason.noScheduled" => Loc.T("traffic.rules.reason.noScheduled"),
        _ => Loc.T("traffic.rules.reason.rules"),
    };

    private static string OperationLabel(string operation) => operation.Trim().ToLowerInvariant() switch
    {
        "arrival" => Loc.T("traffic.rules.arrivals"),
        "departure" => Loc.T("traffic.rules.departures"),
        _ => operation,
    };

    private static string CategoryLabel(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? Loc.T("traffic.rules.generalAviation") : Loc.T("traffic.rules.airliners");

    private static TrafficWake? ParseWake(string? value) =>
        Enum.TryParse<TrafficWake>(value?.Trim(), ignoreCase: true, out var wake) ? wake : null;

    /// <summary>Typ passt zum Muster: gleich oder Präfix bei "*" am Ende ("*" allein passt immer).</summary>
    private static bool Matches(string pattern, string type)
    {
        var p = pattern.Trim();
        return p.EndsWith('*')
            ? type.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(p, type, StringComparison.OrdinalIgnoreCase);
    }
}
