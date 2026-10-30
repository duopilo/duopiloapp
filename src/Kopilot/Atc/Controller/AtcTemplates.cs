using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kopilot.Atc.Airspace;

namespace Kopilot.Atc;

/// <summary>
/// Inhalt von atc-templates.json: alle Funksprüche als Textvorlagen mit {Variablen}, dazu Stimmen, Rufzeichen,
/// Stationsnamen, Frequenz-Platzhalter und die Schwellwerte der Zustandsmaschine. Kein Text steht im Code.
/// </summary>
public sealed class AtcTemplates
{
    /// <summary>Aktuelles Format. Ältere Dateien hebt <see cref="AtcTemplateMigration"/> beim Laden an.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = 1;

    public AtcVoiceConfig Voices { get; set; } = new();

    /// <summary>
    /// ICAO-Airline-Kennung → Telephony-Rufzeichen ("DLH" → "Lufthansa"). Seit Version 2 nur noch Ausnahmen und Korrekturen –
    /// alle anderen Airlines kommen aus der OpenFlights-Datenbank (<see cref="Airlines"/>).
    /// </summary>
    public Dictionary<string, string> Telephony { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zusatz für FIRs ohne eigenen Eintrag, deren Namen Kopilot aus VAT-Spy nimmt ("Minneapolis" → "Minneapolis Center"):
    /// ICAO-Präfix → Stationsart, der längste passende Präfix gewinnt, "" für alle übrigen.
    /// </summary>
    public Dictionary<string, string> FirSuffixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Airline-Datenbank (OpenFlights), von <see cref="AtcService"/> gesetzt – nicht Teil der Datei.</summary>
    [JsonIgnore]
    public AirlineDirectory Airlines { get; set; } = AirlineDirectory.Empty;

    /// <summary>Rufzeichen für den Funk: zuerst <see cref="Telephony"/>, dann <see cref="Airlines"/>.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> CallsignTelephony => new TelephonyView(Telephony, Airlines.Telephony);

    /// <summary>Stationsart ("delivery", "ground" …) → Anzeigename und Platzhalter-Frequenz.</summary>
    public Dictionary<string, AtcStationTypeConfig> StationTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flughafen (ICAO) → Rufname, Steigflughöhe der Freigabe, abweichende Stationsnamen und Frequenzen.</summary>
    public Dictionary<string, AtcAirportConfig> Airports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>FIR (ICAO) → Name der Kontrollstelle, Frequenz, Oceanic ja/nein.</summary>
    public Dictionary<string, AtcFirConfig> Firs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AtcRules Rules { get; set; } = new();

    /// <summary>Funksprüche nach ID ("delivery.clearance"). Mehrere Varianten: die erste, deren Variablen alle bekannt sind, gewinnt.</summary>
    public Dictionary<string, AtcMessageTemplate> Messages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Korrektur je Readback-Punkt für "negative, …" ("squawk" → "squawk {squawk}").</summary>
    public Dictionary<string, string> Corrections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Einzelne Wörter, die die Zustandsmaschine einsetzt ("climb", "descend", "maintain").</summary>
    public Dictionary<string, string> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AtcMessageTemplate? Message(string id) => Messages.TryGetValue(id, out var m) ? m : null;

    public string Word(string key, string fallback) =>
        Words.TryGetValue(key, out var w) && !string.IsNullOrWhiteSpace(w) ? w.Trim() : fallback;

    public AtcStationTypeConfig StationType(AtcStationType type) =>
        StationTypes.TryGetValue(AtcStations.TypeKey(type), out var c) ? c : new AtcStationTypeConfig();

    public AtcAirportConfig? Airport(string? icao) =>
        icao is not null && Airports.TryGetValue(icao, out var a) ? a : null;

    public AtcFirConfig? Fir(string? code) =>
        code is not null && Firs.TryGetValue(code, out var f) ? f : null;

    /// <summary>Wörterbücher ohne Groß-/Kleinschreibung (System.Text.Json legt sie sonst ohne Comparer an) und ohne null-Einträge.</summary>
    public AtcTemplates Normalized()
    {
        Telephony = Ci(Telephony);
        FirSuffixes = Ci(FirSuffixes);
        StationTypes = Ci(StationTypes);
        Airports = Ci(Airports);
        Firs = Ci(Firs);
        Messages = Ci(Messages);
        Corrections = Ci(Corrections);
        Words = Ci(Words);
        Voices ??= new AtcVoiceConfig();
        Voices.Pool ??= [];
        Voices.Stations = Ci(Voices.Stations);
        Rules ??= new AtcRules();
        foreach (var airport in Airports.Values)
        {
            airport.Stations = Ci(airport.Stations);
            airport.Frequencies = Ci(airport.Frequencies);
        }

        foreach (var message in Messages.Values)
        {
            message.Variants ??= [];
            message.Readback ??= [];
        }

        foreach (var fir in Firs.Values)
        {
            fir.Members ??= [];
        }

        return this;
    }

    private static Dictionary<string, T> Ci<T>(Dictionary<string, T>? source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            if (value is not null && !key.StartsWith('_'))
            {
                result[key] = value;
            }
        }

        return result;
    }
}

public sealed class AtcVoiceConfig
{
    /// <summary>Kokoro-Stimmen, aus denen jede Station pro Flug zufällig eine bekommt.</summary>
    public List<string> Pool { get; set; } = [];

    /// <summary>Feste Stimme je Station: Schlüssel "EDDF/tower", "FIR/EDGG" oder nur die Art ("delivery").</summary>
    public Dictionary<string, string> Stations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Die Stimme der Kopilot-Callouts nicht für ATC verwenden.</summary>
    public bool AvoidCalloutVoice { get; set; } = true;
}

public sealed class AtcStationTypeConfig
{
    /// <summary>Name der Stationsart im Funk ("Delivery", "Radar").</summary>
    public string Name { get; set; } = "";

    /// <summary>Frequenz, wenn weder die Facilities-API noch die Vorlagen eine liefern.</summary>
    public string PlaceholderFrequency { get; set; } = "";
}

public sealed class AtcAirportConfig
{
    /// <summary>Rufname des Flughafens ("Frankfurt"). Leer = Name aus dem SimBrief-Plan.</summary>
    public string? Name { get; set; }

    /// <summary>Höhe der ersten Steigflugfreigabe ({initialAlt}).</summary>
    public int? InitialClimbFt { get; set; }

    /// <summary>Stationsart → vollständiger Name, z. B. "departure": "Langen Radar".</summary>
    public Dictionary<string, string> Stations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stationsart → Frequenz (MHz), hat Vorrang vor der Facilities-API.</summary>
    public Dictionary<string, string> Frequencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AtcFirConfig
{
    /// <summary>Name der Kontrollstelle ("Langen Radar", "Shanwick Oceanic").</summary>
    public string? Name { get; set; }

    /// <summary>Frequenz der Stelle, wenn die Sektordaten für diese FIR (oder an dieser Stelle) keine liefern.</summary>
    public string? Frequency { get; set; }

    public bool Oceanic { get; set; }

    /// <summary>
    /// FIR mit eigener Oceanic-Stelle: deren Kennung (bei Gander "CZQO"). Oceanic-Fixe, die das OFP unter dieser FIR führt,
    /// gehören dann zur Oceanic-Stelle, der Rest zur Domestic-Stelle (Gander Center).
    /// </summary>
    public string? OceanicFir { get; set; }

    /// <summary>Stellen, deren Sektoren in dieser FIR mitzählen (z. B. für eine UIR wie EDVV: "EDYY", "EDWW").</summary>
    public List<string> Members { get; set; } = [];
}

/// <summary>Schwellwerte der Zustandsmaschine (Höhen in ft, Strecken in nm, Zeiten in s/min).</summary>
public sealed class AtcRules
{
    public int InitialClimbFt { get; set; } = 5000;

    public int DepartureClimbFt { get; set; } = 15000;

    /// <summary>Übergabe Departure → Center, sobald die freigegebene Höhe bis auf diesen Abstand erreicht ist.</summary>
    public int DepartureHandoffBelowFt { get; set; } = 2000;

    public bool OfferDirect { get; set; } = true;

    public int DirectMinAltitudeFt { get; set; } = 7000;

    public int TowerHandoffAglFt { get; set; } = 1500;

    public int CenterDescentFt { get; set; } = 11000;

    public int ApproachDescentFt { get; set; } = 5000;

    public double TopOfDescentLeadNm { get; set; } = 5;

    public double ApproachHandoffNm { get; set; } = 40;

    public double ApproachClearanceNm { get; set; } = 20;

    public double TowerHandoffNm { get; set; } = 12;

    public double LandingClearanceFallbackNm { get; set; } = 4;

    public int VacateSpeedKt { get; set; } = 40;

    public int OceanicRequestMinutes { get; set; } = 30;

    public int HoldShortSeconds { get; set; } = 8;

    public int TakeoffClearanceMaxSeconds { get; set; } = 60;

    public string DefaultApproach { get; set; } = "ILS";

    /// <summary>Sektoren, die die Route kürzer als diese Strecke berührt, bekommen keine eigene Übergabe (gegen Hin und Her).</summary>
    public double SectorMinNm { get; set; } = 20;
}

public sealed class AtcMessageTemplate
{
    /// <summary>"atc" oder "pilot" (Vorlage für den erwarteten Pilotenspruch).</summary>
    public string From { get; set; } = "atc";

    public List<string> Variants { get; set; } = [];

    /// <summary>Variablen, die der Pilot zurücklesen muss (z. B. "sid", "initialAlt", "squawk").</summary>
    public List<string> Readback { get; set; } = [];
}

/// <summary>
/// Lädt und prüft atc-templates.json. Die mitgelieferte Datei neben der Kopilot.exe ist die Grundlage; eine eigene Kopie
/// (z. B. %LOCALAPPDATA%\Kopilot\atc-templates.json) wird darübergelegt – sie braucht nur noch die Abweichungen. Ältere
/// Formate hebt <see cref="AtcTemplateMigration"/> vorher an.
/// </summary>
public static class AtcTemplateLoader
{
    public const string FileName = "atc-templates.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Eine Datei (Format wird bei Bedarf angehoben).</summary>
    public static AtcTemplates Parse(string json) => ParseLayered(json, null, []);

    /// <summary>Grundlage und eigene Kopie zusammenführen: Objekte werden je Schlüssel gemischt, die Kopie gewinnt; Listen ersetzt sie ganz.</summary>
    public static AtcTemplates ParseLayered(string? baseJson, string? userJson, List<string> notes)
    {
        var merged = baseJson is null ? new JsonObject() : AtcTemplateMigration.Migrate(ParseObject(baseJson), notes, "Grundlage");
        if (userJson is not null)
        {
            Merge(merged, AtcTemplateMigration.Migrate(ParseObject(userJson), notes, "eigene Kopie"));
        }

        return (merged.Deserialize<AtcTemplates>(Options) ?? throw new JsonException("Leere Vorlagendatei.")).Normalized();
    }

    /// <summary>Datei lesen. Fehler beim Lesen oder im JSON landen in <paramref name="errors"/>.</summary>
    public static AtcTemplates? Load(string path, List<string> errors) => Load(null, path, errors, []);

    /// <summary>
    /// Grundlage <paramref name="basePath"/> und eigene Kopie <paramref name="userPath"/> laden (beide optional). Ist die
    /// Kopie fehlerhaft, gilt nur die Grundlage – der Fehler steht in <paramref name="errors"/>.
    /// </summary>
    public static AtcTemplates? Load(string? basePath, string? userPath, List<string> errors, List<string> notes)
    {
        string? Read(string? path)
        {
            if (path is null)
            {
                return null;
            }

            try
            {
                var text = File.ReadAllText(path);
                ParseObject(text);
                return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                errors.Add($"{path}: {ex.Message}");
                return null;
            }
        }

        var baseJson = Read(basePath);
        var userJson = Read(userPath);
        if (baseJson is null && userJson is null)
        {
            return null;
        }

        try
        {
            var templates = ParseLayered(baseJson, userJson, notes);
            errors.AddRange(Validate(templates));
            return templates;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            errors.Add($"{Path.GetFileName(userPath ?? basePath)}: {ex.Message}");
            return null;
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject ?? throw new JsonException("Die Vorlagendatei muss ein JSON-Objekt sein.");

    /// <summary>Tiefe Mischung: gleichnamige Objekte rekursiv, sonst ersetzt <paramref name="overlay"/> den Wert (Schlüssel ohne Groß-/Kleinschreibung).</summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject overlayObject && target[existingKey] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
            }
            else
            {
                target.Remove(existingKey);
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Prüft, ob alle Funksprüche der Zustandsmaschine vorhanden sind, nur bekannte Variablen verwenden,
    /// jeder Readback-Punkt prüfbar ist und eine Korrektur hat – und dass keine Geschwindigkeit in Mach vorkommt.
    /// </summary>
    public static IReadOnlyList<string> Validate(AtcTemplates templates)
    {
        var errors = new List<string>();
        foreach (var id in AtcMessageIds.All)
        {
            if (templates.Message(id) is not { } m || m.Variants.Count == 0 || m.Variants.All(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Funkspruch \"{id}\" fehlt.");
            }
        }

        foreach (var (id, message) in templates.Messages)
        {
            if (message.From is not ("atc" or "pilot"))
            {
                errors.Add($"{id}: \"from\" muss \"atc\" oder \"pilot\" sein.");
            }

            foreach (var variant in message.Variants)
            {
                CheckText(errors, id, variant);
            }

            foreach (var item in message.Readback)
            {
                if (!AtcVariables.Checkable.Contains(item))
                {
                    errors.Add($"{id}: Readback-Punkt \"{item}\" ist nicht prüfbar.");
                }
                else if (!templates.Corrections.TryGetValue(item, out var correction) || string.IsNullOrWhiteSpace(correction))
                {
                    errors.Add($"{id}: Für den Readback-Punkt \"{item}\" fehlt eine Korrektur unter \"corrections\".");
                }
            }
        }

        foreach (var (item, correction) in templates.Corrections)
        {
            CheckText(errors, "corrections." + item, correction);
        }

        if (templates.Voices.Pool.Count == 0)
        {
            errors.Add("voices.pool ist leer – jede Station braucht eine Stimme.");
        }

        return errors;
    }

    private static void CheckText(List<string> errors, string id, string text)
    {
        foreach (var name in AtcRenderer.Placeholders(text))
        {
            if (!AtcVariables.Known.Contains(name))
            {
                errors.Add($"{id}: unbekannte Variable {{{name}}}.");
            }
        }

        if (text.Contains("mach", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{id}: Geschwindigkeiten nur in Knoten, nicht in Mach.");
        }
    }
}

/// <summary>IDs der Funksprüche, die die Zustandsmaschine verwendet.</summary>
public static class AtcMessageIds
{
    public const string DeliveryRequest = "delivery.request";
    public const string DeliveryClearance = "delivery.clearance";
    public const string DeliveryReadbackCorrect = "delivery.readbackCorrect";

    public const string GroundPushRequest = "ground.pushRequest";
    public const string GroundPushApproved = "ground.pushApproved";
    public const string GroundTaxiRequest = "ground.taxiRequest";
    public const string GroundTaxi = "ground.taxi";
    public const string GroundHandoffTower = "ground.handoffTower";

    public const string TowerReadyRequest = "tower.readyRequest";
    public const string TowerLineUp = "tower.lineUp";
    public const string TowerTakeoff = "tower.takeoff";
    public const string TowerHandoffDeparture = "tower.handoffDeparture";

    public const string DepartureContact = "departure.contact";
    public const string DepartureIdentified = "departure.identified";
    public const string DepartureDirect = "departure.direct";
    public const string DepartureHandoff = "departure.handoff";

    public const string CenterContactClimbing = "center.contactClimbing";
    public const string CenterContactLevel = "center.contactLevel";
    public const string CenterContactDescending = "center.contactDescending";
    public const string CenterClimb = "center.climb";
    public const string CenterDescend = "center.descend";
    public const string CenterIdentified = "center.identified";
    public const string CenterHandoff = "center.handoff";
    public const string CenterHandoffApproach = "center.handoffApproach";

    public const string OceanicRequest = "oceanic.request";
    public const string OceanicClearance = "oceanic.clearance";
    public const string OceanicReadbackCorrect = "oceanic.readbackCorrect";
    public const string OceanicPosition = "oceanic.position";
    public const string OceanicRoger = "oceanic.roger";
    public const string OceanicHandoff = "oceanic.handoff";

    public const string ApproachContact = "approach.contact";
    public const string ApproachDescend = "approach.descend";
    public const string ApproachCleared = "approach.cleared";
    public const string ApproachHandoff = "approach.handoff";

    public const string TowerLandingContact = "tower.landingContact";
    public const string TowerLanding = "tower.landing";
    public const string TowerVacate = "tower.vacate";

    public const string GroundArrivalContact = "ground.arrivalContact";
    public const string GroundTaxiToGate = "ground.taxiToGate";

    public const string CommonNegative = "common.negative";
    public const string CommonRoger = "common.roger";
    public const string CommonSayAgain = "common.sayAgain";
    public const string CommonRadioCheck = "common.radioCheck";

    public static readonly IReadOnlyList<string> All =
    [
        DeliveryRequest, DeliveryClearance, DeliveryReadbackCorrect,
        GroundPushRequest, GroundPushApproved, GroundTaxiRequest, GroundTaxi, GroundHandoffTower,
        TowerReadyRequest, TowerLineUp, TowerTakeoff, TowerHandoffDeparture,
        DepartureContact, DepartureIdentified, DepartureDirect, DepartureHandoff,
        CenterContactClimbing, CenterContactLevel, CenterContactDescending, CenterClimb, CenterDescend, CenterIdentified, CenterHandoff, CenterHandoffApproach,
        OceanicRequest, OceanicClearance, OceanicReadbackCorrect, OceanicPosition, OceanicRoger, OceanicHandoff,
        ApproachContact, ApproachDescend, ApproachCleared, ApproachHandoff,
        TowerLandingContact, TowerLanding, TowerVacate,
        GroundArrivalContact, GroundTaxiToGate,
        CommonNegative, CommonRoger, CommonSayAgain, CommonRadioCheck,
    ];
}

/// <summary>Namen der Vorlagen-Variablen.</summary>
public static class AtcVariables
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "callsign", "station", "dest", "sid", "star", "approach", "runway", "initialAlt", "squawk", "freq", "taxiRoute",
        "holdingPoint", "gate", "level", "speedKt", "entryFix", "heading", "wind", "qnh", "fix", "eta", "nextFix",
        // zusätzlich in den Vorlagen verwendet
        "direction", "altitude", "nextCenter", "route", "time", "corrections", "levelVerb",
    };

    /// <summary>Variablen, die als Readback-Punkt geprüft werden können.</summary>
    public static readonly IReadOnlySet<string> Checkable = new HashSet<string>(StringComparer.Ordinal)
    {
        "sid", "star", "approach", "runway", "initialAlt", "squawk", "freq", "holdingPoint", "gate", "level", "speedKt",
        "entryFix", "heading", "qnh", "fix",
    };
}
