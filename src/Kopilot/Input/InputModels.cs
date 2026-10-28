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

    /// <summary>Zustände, die vor dem Ausführen gelten müssen (live gelesen), z. B. FCU nicht im TRK/FPA-Modus.</summary>
    public List<StepCondition> Requires { get; set; } = [];

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

    /// <summary>Sonderformat: "squawk" (4 Ziffern 0–7), "frequency" (MHz mit 3 Nachkommastellen), "altitude" (auch "FL120"), "heading" (360 = 000).</summary>
    public string? Format { get; set; }

    /// <summary>Gesprochene Form einer Auswahl im Readback, z. B. { "tara": "TA RA", "lo": "low" }.</summary>
    public Dictionary<string, string>? Spoken { get; set; }

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

    /// <summary>Für "requires": Grund, wenn die Bedingung nicht erfüllt ist.</summary>
    public string? Message { get; set; }

    /// <summary>Für "requires": nur bei diesen Auswahlwerten prüfen (z. B. [ "on" ]). Leer = immer.</summary>
    public List<string>? Only { get; set; }
}

public sealed class ActionCheck
{
    public string Variable { get; set; } = "";

    public string Unit { get; set; } = "number";

    /// <summary>Zahl, "{value}" oder je Auswahl { "on": 1, "off": 0 }.</summary>
    public JsonElement? Expect { get; set; }

    public double Tolerance { get; set; } = 0.5;

    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// So lange muss der Wert stehen bleiben, bis er als übernommen gilt (ms). Fängt Systeme ab, die einen geschriebenen
    /// Wert sofort wieder zurücksetzen (z. B. APU START ohne Strom).
    /// </summary>
    public int HoldMs { get; set; } = 1000;

    /// <summary>
    /// true = die geprüfte Variable wird zwar auch geschrieben, aber vom Flugzeug aktiv zurückgesetzt, wenn die Aktion nicht wirkt
    /// (z. B. APU START: ohne Strom/Master schaltet die APU-Elektronik das ON-Licht sofort aus). Gilt dann als echter Nachweis.
    /// </summary>
    public bool ConfirmsEffect { get; set; }

    /// <summary>Zyklischer Wert, z. B. 360 für Kurse: 0 und 360 gelten als gleich.</summary>
    public double? Modulo { get; set; }
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

public sealed record ResolvedCondition(string Variable, string Unit, double Value, double Tolerance, string? Message = null, double? Modulo = null)
{
    public bool Matches(double state)
    {
        if (Modulo is not { } m || m <= 0)
        {
            return Math.Abs(state - Value) <= Tolerance;
        }

        var diff = (((state - Value) % m) + m) % m;
        return Math.Min(diff, m - diff) <= Tolerance;
    }
}

/// <summary>Prüfung nach dem Ausführen. Independent = liest eine Variable, die die Aktion nicht selbst schreibt (echter Nachweis).</summary>
public sealed record ResolvedCheck(ResolvedCondition Condition, int TimeoutMs, int HoldMs, bool Independent);

/// <summary>Ergebnis der Planung: entweder abgelehnt (Reason) oder bereit zur Bestätigung.</summary>
public sealed record ActionPlan(
    ProfileAction Action,
    string? ValueText,
    string Readback,
    IReadOnlyList<ResolvedStep> Steps,
    ResolvedCheck? Check,
    IReadOnlyList<ResolvedCondition> Requires)
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

/// <summary>Im Dashboard umschaltbar, gespeichert in input.json. Epoch zählt Sperren/Abbrüche – offene Aktionen älterer Epochen verfallen.</summary>
public sealed record InputSettings
{
    public bool Enabled { get; init; }

    public long Epoch { get; init; }
}

/// <summary>Offene Aktion für das Dashboard, auch wenn sie in einer anderen Kopilot-Instanz (stdio) vorbereitet wurde.</summary>
public sealed record SharedPending(string Action, string? Value, string Readback, DateTimeOffset ExpiresUtc, int ProcessId);

/// <summary>
/// Gemeinsamer Zustand aller Kopilot-Instanzen im Datenordner: input.json (Schalter, Epoch) und input-pending.json (offene Aktion).
/// Wird bei jedem Zugriff frisch gelesen – die Tray-Instanz und die stdio-Instanz von Claude Desktop sehen denselben Stand.
/// </summary>
public sealed class InputSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _lock = new();

    public string FilePath { get; } = Path.Combine(directory, "input.json");

    public string PendingPath { get; } = Path.Combine(directory, "input-pending.json");

    /// <summary>Gespeicherter Schalter, null = keine Datei (dann gilt appsettings).</summary>
    public InputSettings? Load() => Read<InputSettings>(FilePath);

    public void Save(InputSettings settings) => Write(FilePath, settings);

    public SharedPending? LoadPending() => Read<SharedPending>(PendingPath);

    public void SavePending(SharedPending? pending)
    {
        if (pending is not null)
        {
            Write(PendingPath, pending);
            return;
        }

        lock (_lock)
        {
            try
            {
                File.Delete(PendingPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nicht schlimm – das Dashboard prüft die Ablaufzeit
            }
        }
    }

    private T? Read<T>(string path)
        where T : class
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void Write<T>(string path, T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
    }
}
