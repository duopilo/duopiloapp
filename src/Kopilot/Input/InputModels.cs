using System.Text.Json;

namespace Kopilot.Input;

/// <summary>
/// Eine erlaubte Eingabe aus dem Flugzeugprofil ("actions"). Kopilot führt nur Aktionen aus dieser Liste aus –
/// es gibt keinen Weg, beliebige Events oder Variablen zu schreiben.
/// </summary>
public sealed class ProfileAction
{
    /// <summary>Eindeutiger Name, z. B. "fcu.speed" oder "gear.down".</summary>
    public string Id { get; set; } = "";

    /// <summary>Bereich für die Übersicht: FCU, EFIS, Config, Lights, Signs, AntiIce, Air, APU, Transponder, Radio, Brakes.</summary>
    public string Area { get; set; } = "";

    /// <summary>Kurzname auf Englisch, z. B. "FCU speed".</summary>
    public string Label { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Wert der Aktion (Zahl oder Auswahl). Null = Aktion ohne Wert (z. B. "gear.down").</summary>
    public ActionParameter? Parameter { get; set; }

    /// <summary>Was an MSFS geschickt wird, in dieser Reihenfolge.</summary>
    public List<ActionStep> Steps { get; set; } = [];

    /// <summary>Readback auf Englisch, "{value}" wird ersetzt, z. B. "Speed {value} selected".</summary>
    public string Readback { get; set; } = "";

    /// <summary>Prüfung nach dem Ausführen: Hat das Flugzeug den Wert übernommen?</summary>
    public ActionCheck? Check { get; set; }

    /// <summary>Sperren aus <see cref="Interlocks"/>, z. B. "notOnGround" oder "flapsSpeed".</summary>
    public List<string> Interlocks { get; set; } = [];

    /// <summary>false = Event/Variable stammt aus Doku/Quellcode und ist im Sim noch nicht geprüft.</summary>
    public bool Verified { get; set; }
}

public sealed class ActionParameter
{
    /// <summary>"number" oder "choice".</summary>
    public string Type { get; set; } = "number";

    public double? Min { get; set; }

    public double? Max { get; set; }

    /// <summary>Raster, z. B. 100 (ft) oder 0.005 (MHz). Werte außerhalb des Rasters werden abgelehnt.</summary>
    public double? Step { get; set; }

    /// <summary>Einheit für Anzeige und Hinweise, z. B. "kt", "ft", "hPa", "MHz".</summary>
    public string? Unit { get; set; }

    /// <summary>Auswahl: Name → Wert, z. B. { "on": 1, "off": 0 }. Reihenfolge = Anzeige.</summary>
    public Dictionary<string, double>? Options { get; set; }

    /// <summary>Sonderformat: "squawk" (4 Ziffern 0–7), "frequency" (MHz mit 3 Nachkommastellen), "altitude" (auch "FL120").</summary>
    public string? Format { get; set; }

    public bool IsChoice => Type.Equals("choice", StringComparison.OrdinalIgnoreCase);
}

public sealed class ActionStep
{
    /// <summary>Event-Name ohne "K:", z. B. "GEAR_DOWN" oder "A32NX.FCU_SPD_SET".</summary>
    public string? Event { get; set; }

    /// <summary>Variable, die geschrieben wird, z. B. "L:A32NX_TRANSPONDER_MODE".</summary>
    public string? Set { get; set; }

    public string Unit { get; set; } = "number";

    /// <summary>Zahl, "{value}" (Wert der Aktion) oder je Auswahl { "on": 1, "off": 0 }. Fehlt = 0.</summary>
    public JsonElement? Value { get; set; }

    /// <summary>Faktor auf den Wert (z. B. 16 für hPa beim BARO_SET, 1000000 für MHz → Hz).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>"bcd16" für den Transpondercode.</summary>
    public string? Encode { get; set; }

    /// <summary>Schritt nur für diese Auswahlwerte (z. B. [ "std" ]). Leer = immer.</summary>
    public List<string>? Only { get; set; }

    /// <summary>Schritt überspringen, wenn der Zustand schon passt (für Taster, die umschalten).</summary>
    public StepCondition? SkipIf { get; set; }

    /// <summary>Pause nach dem Schritt in ms (z. B. zwischen SET und PULL).</summary>
    public int DelayMs { get; set; }
}

public sealed class StepCondition
{
    public string Variable { get; set; } = "";

    public string Unit { get; set; } = "number";

    /// <summary>Zahl, "{value}" oder je Auswahl { "on": 1, "off": 0 }.</summary>
    public JsonElement? EqualTo { get; set; }

    public double Tolerance { get; set; } = 0.5;
}

public sealed class ActionCheck
{
    public string Variable { get; set; } = "";

    public string Unit { get; set; } = "number";

    /// <summary>Zahl, "{value}" oder je Auswahl { "on": 1, "off": 0 }.</summary>
    public JsonElement? Expect { get; set; }

    public double Tolerance { get; set; } = 0.5;

    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>Was Kopilot an MSFS schickt – fertig ausgerechnet.</summary>
public sealed record ResolvedStep(
    string? Event,
    uint EventData,
    string? Set,
    string Unit,
    double SetValue,
    ResolvedCondition? SkipIf,
    int DelayMs)
{
    public string Describe() => Event is not null
        ? $"Event {Event} ({unchecked((int)EventData)})"
        : $"Setze {Set} = {SetValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({Unit})";
}

public sealed record ResolvedCondition(string Variable, string Unit, double Value, double Tolerance);

/// <summary>Ergebnis der Planung: entweder abgelehnt (Reason) oder bereit zur Bestätigung.</summary>
public sealed record ActionPlan(
    ProfileAction Action,
    string? ValueText,
    string Readback,
    IReadOnlyList<ResolvedStep> Steps,
    ResolvedCondition? Check,
    int CheckTimeoutMs)
{
    public string Summary => ValueText is null ? Action.Id : $"{Action.Id} = {ValueText}";
}

public sealed record PlanResult(ActionPlan? Plan, string? Refusal)
{
    public static PlanResult Refuse(string reason) => new(null, reason);

    public static PlanResult Ok(ActionPlan plan) => new(plan, null);
}

/// <summary>Eine vorbereitete Aktion, die auf die Bestätigung des Piloten wartet.</summary>
public sealed record PendingAction(string Token, ActionPlan Plan, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

/// <summary>Eintrag im Eingabe-Protokoll.</summary>
public sealed record InputLogEntry(
    DateTimeOffset TimeUtc,
    string Action,
    string? Value,
    string Outcome,
    string? Detail,
    bool? Verified);

/// <summary>Im Dashboard umschaltbar, gespeichert in input.json.</summary>
public sealed record InputSettings
{
    public bool Enabled { get; init; }
}

/// <summary>Speichert den Schalter "Eingabe an/aus" in DataDirectory\input.json – gilt für alle Kopilot-Instanzen.</summary>
public sealed class InputSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _lock = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private InputSettings? _cached;

    public string FilePath { get; } = path;

    /// <summary>Gespeicherter Schalter, null = keine Datei (dann gilt appsettings). Liest nur neu, wenn sich die Datei geändert hat.</summary>
    public InputSettings? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    _cached = null;
                    _loadedStamp = DateTime.MinValue;
                    return null;
                }

                var stamp = File.GetLastWriteTimeUtc(FilePath);
                if (stamp != _loadedStamp)
                {
                    _cached = JsonSerializer.Deserialize<InputSettings>(File.ReadAllText(FilePath), Options);
                    _loadedStamp = stamp;
                }

                return _cached;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return _cached;
            }
        }
    }

    public void Save(InputSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            _cached = settings;
            _loadedStamp = File.GetLastWriteTimeUtc(FilePath);
        }
    }
}
