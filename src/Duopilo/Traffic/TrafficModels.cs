using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Wie weit ein Flugzeugmodell für den eigenen Verkehr geprüft ist.</summary>
public enum TrafficModelStatus
{
    /// <summary>Im Test gelandet, laut Messwerten in Ordnung, im Sim aber noch nicht angesehen.</summary>
    Candidate,

    /// <summary>Im Sim angesehen: Fahrwerk sichtbar draußen, Landung in Ordnung.</summary>
    Verified,
}

/// <summary>Ein freigegebener Flugzeugtyp mit den Modelltiteln, die ihn im Sim darstellen.</summary>
/// <param name="IcaoType">ICAO-Typbezeichner, z. B. "A20N" – darüber ordnet der spätere Fahrplan-Verkehr Flüge Modellen zu.</param>
/// <param name="Titles">Exakte Modelltitel (ohne Groß-/Kleinschreibung), wie sie traffic_test_models liefert.</param>
public sealed record TrafficModelType(string IcaoType, string Name, IReadOnlyList<string> Titles, TrafficModelStatus Status, string Note);

/// <summary>
/// Freigabe- und Sperrliste der Flugzeugmodelle für den eigenen Verkehr (docs/research/eigener-verkehr.md).
///
/// Gesperrt sind die Modelle von FlyByWire (A32NX, A380X) und Headwind (A330-900neo): Sie steuern Fahrwerk und Triebwerke über eigene
/// Systeme, die für KI-Objekte nicht laufen – im Test (EDDL, 1.11.2026) kam das Fahrwerk mit jedem Befehl nur halb heraus, obwohl alle
/// Fahrwerkswerte 100 % meldeten, und die Triebwerke drehten nicht. Die Titel nennen den Hersteller nicht immer (Headwind: „Airbus
/// A330-900neo Condor Beach“), deshalb Titelmuster.
///
/// Freigegeben sind Typen, deren Modelle im Test gelandet sind; „Verified“ erst, wenn Fahrwerk und Landung im Sim angesehen wurden.
/// Freigaben gelten über exakte Titel, damit Varianten (VIP, Frachter) nicht ungeprüft mitrutschen.
/// </summary>
public static class TrafficModels
{
    /// <summary>Teilstücke von Titel oder Livery (ohne Groß-/Kleinschreibung), bei denen ein Modell gesperrt ist.</summary>
    public static readonly IReadOnlyList<string> ExcludedPatterns = ["FlyByWire", "A32NX", "A380X", "Headwind", "A330-900neo"];

    /// <summary>Freigegebene Typen, geprüfte zuerst.</summary>
    public static readonly IReadOnlyList<TrafficModelType> Approved =
    [
        new("A20N", "Airbus A320neo", ["A320neo V2"], TrafficModelStatus.Verified,
            "EDDL 1.11.2026: gear down by 2720 ft and visible, landed on both runways (696 m / 1204 m)."),
        new("A333", "Airbus A330-300", ["A330-300 (RR)", "A330-300 (GE)"], TrafficModelStatus.Verified,
            "EDDL 1.11.2026: gear visible (slightly in the ground, no physics), touchdown 330 m."),
        new("A321", "Airbus A321", ["A321"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed, late touchdown."),
        new("A310", "Airbus A310", ["A310"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed."),
        new("A337", "Airbus A330 BelugaXL", ["A330-BelugaXL"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed, touchdown 897 m."),
        new("B748", "Boeing 747-8", ["747-8i"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed."),
        new("B738", "Boeing 737-800", ["Asobo PassiveAircraft B737-800"], TrafficModelStatus.Candidate,
            "EDDL 1.11.2026: landed, touchdown 1144 m."),
    ];

    /// <summary>Das Muster, an dem ein Modell gesperrt ist, sonst null.</summary>
    public static string? ExcludedBy(string title, string? livery = null) =>
        ExcludedPatterns.FirstOrDefault(p =>
            title.Contains(p, StringComparison.OrdinalIgnoreCase)
            || (livery?.Contains(p, StringComparison.OrdinalIgnoreCase) ?? false));

    public static bool IsExcluded(string title, string? livery = null) => ExcludedBy(title, livery) is not null;

    /// <summary>Der freigegebene Typ zu einem Modelltitel, sonst null (auch für gesperrte Modelle).</summary>
    public static TrafficModelType? TypeOf(string title, string? livery = null) =>
        IsExcluded(title, livery)
            ? null
            : Approved.FirstOrDefault(t => t.Titles.Any(x => string.Equals(x, title.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>Status als Text für Tools und Log: verified, candidate, excluded oder unknown.</summary>
    public static string StatusName(string title, string? livery = null) =>
        IsExcluded(title, livery) ? "excluded" : TypeOf(title, livery)?.Status.ToString().ToLowerInvariant() ?? "unknown";
}

/// <summary>Lichter der Testflugzeuge (NonATC-Flugzeuge schaltet der Sim nicht selbst).</summary>
public static class TrafficLights
{
    /// <summary>Im Anflug: Nav, Beacon, Strobes, Landelicht, Bug-/Taxilicht und Logo an.</summary>
    public static readonly AiLights Approach = new(Nav: true, Beacon: true, Strobe: true, Landing: true, Taxi: true, Logo: true);

    /// <summary>Nach dem Aufsetzen beim Abbremsen: Landelicht und Strobes aus, Taxilicht bleibt an.</summary>
    public static readonly AiLights Rollout = new(Strobe: false, Landing: false, Taxi: true);

    /// <summary>Unter dieser Geschwindigkeit am Boden schaltet der Test auf <see cref="Rollout"/>.</summary>
    public const double RolloutBelowKt = 60;
}
