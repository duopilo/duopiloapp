using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>Eine Regel für eine oder mehrere Bahnen eines Flughafens (aus runway-rules.json).</summary>
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

/// <summary>Regeln eines Flughafens.</summary>
public sealed record TrafficAirportRules(IReadOnlyList<TrafficRunwayRule> Runways, string? Note = null);

/// <summary>Inhalt von runway-rules.json: Regeln je ICAO-Code.</summary>
public sealed record TrafficRunwayRulesFile(IReadOnlyDictionary<string, TrafficAirportRules> Airports);

/// <summary>Ein Grund, warum ein Flugzeug eine Bahn nicht nutzen darf.</summary>
/// <param name="Code">operation, category, type, wake, too_short oder too_narrow.</param>
/// <param name="Text">Für den Piloten (Sprache der Oberfläche).</param>
public sealed record TrafficRunwayViolation(string Code, string Text);

/// <summary>
/// Welche Flugzeuge welche Bahn nutzen dürfen: zum einen nach den Bahndaten (Länge und Breite gegen den Bedarf des Typs), zum
/// anderen nach Regeln je Flughafen aus <c>data\traffic\runway-rules.json</c> – mitgeliefert im Programmordner, eigene im
/// Datenordner (die ersetzen die mitgelieferten Regeln des Flughafens). Beispiel EDDF: 07L/25R (Landebahn Nordwest) nur Landungen,
/// keine B747, A380, MD-11, An-124, L-1011; 18 (Startbahn West) nur Starts.
/// </summary>
public sealed class TrafficRunwayRules
{
    public const string Folder = "traffic";

    public const string FileName = "runway-rules.json";

    /// <summary>Landestrecke ≈ Faktor × Vapp² (m, kt): A320 135 kt → rund 1370 m, 737-800 145 kt → 1580 m, C172 65 kt → 320 m.</summary>
    public const double LandingFactor = 0.075;

    /// <summary>Startstrecke als Vielfaches der Landestrecke: A320 rund 1910 m, MD-11 rund 2520 m.</summary>
    public const double TakeoffFactor = 1.4;

    /// <summary>Mindestbreite Heavy und Super (ICAO-Code E/F).</summary>
    public const double WideBodyWidthM = 45;

    /// <summary>Mindestbreite übrige Verkehrsflugzeuge (ICAO-Code C).</summary>
    public const double AirlinerWidthM = 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>ICAO-Typen der Kategorie Super.</summary>
    private static readonly HashSet<string> SuperTypes = new(StringComparer.OrdinalIgnoreCase) { "A388", "A225" };

    /// <summary>ICAO-Typen der Kategorie Heavy (Präfix mit "*").</summary>
    private static readonly string[] HeavyTypes =
    [
        "A30B", "A306", "A310", "A33*", "A34*", "A35*", "A124", "B74*", "B76*", "B77*", "B78*", "MD11", "DC10", "L101",
        "IL96", "C17", "C5*", "A400",
    ];

    private readonly Dictionary<string, TrafficAirportRules> _airports;

    public TrafficRunwayRules(IReadOnlyDictionary<string, TrafficAirportRules>? airports = null) =>
        _airports = new(airports ?? new Dictionary<string, TrafficAirportRules>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Flughäfen mit eigenen Regeln.</summary>
    public IReadOnlyCollection<string> Airports => _airports.Keys;

    /// <summary>Regeln aus JSON; wirft <see cref="JsonException"/> bei kaputter Datei.</summary>
    public static TrafficRunwayRules Parse(string json) =>
        new(JsonSerializer.Deserialize<TrafficRunwayRulesFile>(json, Json)?.Airports);

    /// <summary>
    /// Lädt runway-rules.json aus den Datenordnern (<paramref name="roots"/> wie <c>AirspaceLoader.Roots</c>: zuerst der eigene, dann
    /// der Programmordner). Regeln eines Flughafens aus dem eigenen Ordner ersetzen die mitgelieferten. Fehler landen in
    /// <paramref name="messages"/>.
    /// </summary>
    public static TrafficRunwayRules Load(IReadOnlyList<string> roots, List<string> messages)
    {
        var airports = new Dictionary<string, TrafficAirportRules>(StringComparer.OrdinalIgnoreCase);
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

        return new TrafficRunwayRules(airports);
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

    /// <summary>
    /// Gründe, warum das Flugzeug (<paramref name="icaoType"/> darf null sein) die Bahn für <paramref name="operation"/> nicht nutzen
    /// darf; leer = erlaubt.
    /// </summary>
    public IReadOnlyList<TrafficRunwayViolation> Check(
        RunwayEnd runway,
        TrafficOperation operation,
        string? icaoType,
        TrafficCategory category,
        double approachKt)
    {
        var violations = new List<TrafficRunwayViolation>();
        var wake = WakeOf(icaoType, category);
        var label = icaoType is { Length: > 0 } ? icaoType.ToUpperInvariant() : CategoryName(category);
        var designation = AirportRunway.Normalize(runway.Designation);

        if (_airports.TryGetValue(runway.Airport, out var airport))
        {
            foreach (var rule in (airport.Runways ?? []).Where(r => (r.Runways ?? []).Any(d => AirportRunway.Normalize(d) == designation)))
            {
                var note = string.IsNullOrWhiteSpace(rule.Note) ? "" : $" ({rule.Note.Trim()})";
                if (rule.Operations is { } operations && !operations.Any(o => string.Equals(o, OperationName(operation), StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(new("operation", operations.Count == 0
                        ? Loc.T("traffic.rules.closed", runway.Designation, note)
                        : Loc.T("traffic.rules.operation", runway.Designation, string.Join(", ", operations.Select(OperationLabel)), note)));
                }

                if (rule.Categories is { } categories && !categories.Any(c => string.Equals(c, CategoryName(category), StringComparison.OrdinalIgnoreCase)))
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

    public static string OperationName(TrafficOperation operation) => operation == TrafficOperation.Departure ? "departure" : "arrival";

    public static string CategoryName(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? "generalaviation" : "airliner";

    public static string WakeName(TrafficWake wake) => wake.ToString().ToLowerInvariant();

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

    /// <summary>Typ passt zum Muster: gleich oder Präfix bei "*" am Ende.</summary>
    private static bool Matches(string pattern, string type)
    {
        var p = pattern.Trim();
        return p.EndsWith('*')
            ? type.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(p, type, StringComparison.OrdinalIgnoreCase);
    }
}
