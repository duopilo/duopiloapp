using System.Globalization;
using System.Text.Json.Serialization;
using Kopilot.FlightPlan;
using Kopilot.Localization;
using Kopilot.Speech;

namespace Kopilot.Monitoring;

/// <summary>Grenzen des Fuel Checks (Kopilot:Monitoring:FuelCheck).</summary>
public sealed class FuelCheckSettings
{
    /// <summary>Im Reiseflug spätestens nach so vielen Minuten (Sim-Zeit) ohne Check einen Intervall-Check machen. 0 = aus.</summary>
    public double IntervalMinutes { get; set; } = 30;

    /// <summary>Warnung, wenn die Abweichung gegen den Plan seit dem letzten Check um mehr als so viele kg schlechter wird. 0 = aus.</summary>
    public double DeviationIncreaseKg { get; set; } = 300;

    /// <summary>
    /// Warnung, wenn die prognostizierte Landemenge unter Minimum + Puffer fällt. Der Puffer ist höchstens die Hälfte
    /// dessen, was der Plan über dem Minimum landet – sonst würde ein knapp geplanter Flug schon beim ersten Check warnen.
    /// </summary>
    public double LandingBufferKg { get; set; } = 500;

    /// <summary>Wegpunkt-Checks frühestens so viele Minuten nach dem letzten Check (dichte Wegpunkte über Europa). 0 = jeder Wegpunkt.</summary>
    public double MinSpacingMinutes { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FuelCheckKind>))]
public enum FuelCheckKind
{
    /// <summary>Überflug eines OFP-Wegpunkts im Reiseflug.</summary>
    Waypoint,

    /// <summary>Lange Strecke ohne Wegpunkt.</summary>
    Interval,

    /// <summary>Zwischen zwei Checks: Abweichung über die Schwelle gewachsen.</summary>
    Deviation,

    /// <summary>Zwischen zwei Checks: Landemenge unter Minimum + Puffer gefallen.</summary>
    Landing,

    /// <summary>Treibstoff ist gestiegen (Nachtanken über Toolbar/EFB) – kein Fehler, Vergleich beginnt neu.</summary>
    Refuel,

    /// <summary>Treibstoff ist schneller gesunken, als ein Flugzeug verbrennen kann – manuelle Änderung, Vergleich beginnt neu.</summary>
    Defuel,
}

/// <summary>Ein Eintrag im Verlauf des Fuel Checks (Dashboard-Trend und get_progress).</summary>
public sealed record FuelCheckEntry
{
    public long Id { get; init; }

    public DateTimeOffset TimeUtc { get; init; }

    /// <summary>Sim-Uhrzeit (ZULU TIME) in Sekunden seit Mitternacht.</summary>
    public double? ZuluSec { get; init; }

    public FuelCheckKind Kind { get; init; }

    /// <summary>Wegpunkt beim Wegpunkt-Check.</summary>
    public string? Ident { get; init; }

    public double ActualKg { get; init; }

    public double? PlannedKg { get; init; }

    /// <summary>Ist minus Plan.</summary>
    public double? DiffKg { get; init; }

    /// <summary>Veränderung der Abweichung seit dem letzten Check (negativ = schlechter geworden).</summary>
    public double? ChangeKg { get; init; }

    public double? PredictedLandingKg { get; init; }

    public double? MinimumLandingKg { get; init; }

    /// <summary>Minimum + (gedeckelter) Puffer.</summary>
    public double? LandingThresholdKg { get; init; }

    /// <summary>Menge vor dem Tanken bzw. vor dem Sprung.</summary>
    public double? FromKg { get; init; }

    /// <summary>Strecke ab Start entlang der Route – x-Achse des Trends im Dashboard.</summary>
    public double? DistanceFlownNm { get; init; }

    /// <summary>"deviation", "landing_buffer", "landing_minimum".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Callout { get; init; }

    public required string Message { get; init; }

    public bool IsWarning => Warnings.Count > 0;

    public bool IsCheck => Kind is FuelCheckKind.Waypoint or FuelCheckKind.Interval or FuelCheckKind.Deviation or FuelCheckKind.Landing;
}

/// <summary>
/// Verlauf des Fuel Checks eines Flugplans. Threadsicher: geschrieben vom MonitoringService, gelesen von Dashboard und Tools.
/// Ein neuer SimBrief-Plan beginnt einen neuen Verlauf.
/// </summary>
public sealed class FuelCheckLog(int capacity = 300)
{
    private readonly object _lock = new();
    private readonly LinkedList<FuelCheckEntry> _entries = new();
    private string? _planId;
    private long _lastId;

    public string? PlanId
    {
        get
        {
            lock (_lock)
            {
                return _planId;
            }
        }
    }

    /// <summary>Neuer Plan: Verlauf leeren. Derselbe Plan (z. B. nach einem Neustart des Monitorings) behält ihn.</summary>
    public void BeginPlan(string planId)
    {
        lock (_lock)
        {
            if (planId == _planId)
            {
                return;
            }

            _planId = planId;
            _entries.Clear();
        }
    }

    public FuelCheckEntry Add(FuelCheckEntry entry)
    {
        lock (_lock)
        {
            var stored = entry with { Id = ++_lastId };
            _entries.AddLast(stored);
            while (_entries.Count > capacity)
            {
                _entries.RemoveFirst();
            }

            return stored;
        }
    }

    public IReadOnlyList<FuelCheckEntry> Last(int count)
    {
        lock (_lock)
        {
            return _entries.TakeLast(Math.Max(0, count)).ToList();
        }
    }

    /// <summary>Letzter echter Check (Wegpunkt, Intervall oder Warnung), null wenn es noch keinen gab.</summary>
    public FuelCheckEntry? LastCheck
    {
        get
        {
            lock (_lock)
            {
                return _entries.LastOrDefault(e => e.IsCheck);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _planId = null;
        }
    }
}

/// <summary>
/// Ansagen und Meldungen des Fuel Checks. Ansage englisch und kurz wie im Cockpit ("HENDY, fuel 34.1, 2.2 below plan"),
/// Mengen in Tonnen mit einer Nachkommastelle; die Meldung deutsch mit kg.
/// </summary>
public static class FuelCheckText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Darunter gilt die Abweichung als "on plan" (eine Nachkommastelle in Tonnen).</summary>
    public const double OnPlanKg = 50;

    /// <summary>Menge für die Ansage: ab 1 t in Tonnen ("34.1"), darunter in kg ("850 kilos").</summary>
    public static string Amount(double kg) =>
        Math.Abs(kg) >= 1000
            ? Math.Round(Math.Abs(kg) / 1000, 1).ToString("0.0", Inv)
            : ((long)Math.Round(Math.Abs(kg) / 10) * 10).ToString(Inv) + " kilos";

    /// <summary>Abweichung für die Ansage: "on plan", "2.2 below plan", "0.4 above plan".</summary>
    public static string Deviation(double diffKg, double actualKg) =>
        Math.Abs(diffKg) < OnPlanKg ? "on plan" : $"{Difference(diffKg, actualKg)} {(diffKg < 0 ? "below" : "above")} plan";

    /// <summary>Differenz für die Ansage, ohne Vorzeichen: bei Airlinern in Tonnen ("0.3", mindestens 0.1), bei sehr kleinen Mengen in kg.</summary>
    public static string Difference(double kg, double actualKg) =>
        actualKg >= 1000 ? Math.Max(0.1, Math.Round(Math.Abs(kg) / 1000, 1)).ToString("0.0", Inv) : Amount(kg);

    /// <summary>
    /// Wie der Wegpunkt angesagt wird – mit der gemeinsamen Aussprache (<see cref="Pronunciation.Waypoint"/>): fünf
    /// Buchstaben als Wort ("Hendy"), Funkfeuer mit Namen ("Wabush" statt YWK), sonst im NATO-Alphabet, Koordinatenpunkte
    /// als "fifty three north fifty west". Null = ohne Ort ansagen.
    /// </summary>
    public static string? Place(FuelCheckFix? fix) =>
        fix is null || string.IsNullOrWhiteSpace(fix.Ident) ? null : Pronunciation.Waypoint(fix.Ident, fix.Name, fix.Lat, fix.Lon);

    /// <summary>"HENDY, fuel 34.1, 2.2 below plan" plus Zusätze ("deviation increased by 0.6").</summary>
    public static string Callout(string? place, double actualKg, double diffKg, IEnumerable<string>? extras = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(place))
        {
            parts.Add(place);
        }

        parts.Add("fuel " + Amount(actualKg));
        parts.Add(Deviation(diffKg, actualKg));
        if (extras is not null)
        {
            parts.AddRange(extras.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Deutsche Mengenangabe, auf 10 kg gerundet: "34.120 kg".</summary>
    public static string Kg(double kg)
    {
        var rounded = (long)Math.Round(Math.Abs(kg) / 10) * 10;
        return (kg < 0 && rounded > 0 ? "−" : "") + rounded.ToString("#,0", Inv).Replace(",", Loc.T("monitor.fuel.groupSeparator"), StringComparison.Ordinal) + " kg";
    }

    /// <summary>Mit Vorzeichen: "+1.200 kg" / "−2.180 kg".</summary>
    public static string SignedKg(double kg) => (kg >= 0 ? "+" : "") + Kg(kg);
}
