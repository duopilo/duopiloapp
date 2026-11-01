using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Wie weit ein Flugzeugmodell für den eigenen Verkehr geprüft ist.</summary>
public enum TrafficModelStatus
{
    /// <summary>Vorgesehen, aber noch nicht getestet (z. B. die GA-Flugzeuge aus MSFS 2024).</summary>
    Untested,

    /// <summary>Im Test gelandet, laut Messwerten in Ordnung, im Sim aber noch nicht angesehen.</summary>
    Candidate,

    /// <summary>Im Sim angesehen: Fahrwerk sichtbar draußen, Landung in Ordnung.</summary>
    Verified,
}

/// <summary>Art des Verkehrs – der spätere Verkehr setzt GA an kleinen Plätzen und in der Platzrunde ein, Airliner nach Fahrplan.</summary>
public enum TrafficCategory
{
    Airliner,

    /// <summary>Allgemeine Luftfahrt: Kolben, Turboprop, Business-Jets.</summary>
    GeneralAviation,
}

/// <summary>Ein freigegebener Flugzeugtyp mit den Modelltiteln, die ihn im Sim darstellen.</summary>
/// <param name="IcaoType">ICAO-Typbezeichner, z. B. "A20N" – darüber ordnet der spätere Fahrplan-Verkehr Flüge Modellen zu.</param>
/// <param name="Titles">Exakte Modelltitel (ohne Groß-/Kleinschreibung), wie sie traffic_test_models liefert.</param>
public sealed record TrafficModelType(
    string IcaoType,
    string Name,
    TrafficCategory Category,
    IReadOnlyList<string> Titles,
    TrafficModelStatus Status,
    string Note)
{
    /// <summary>
    /// Titelwörter für Typen, deren genaue Titel noch nicht feststehen (ungetestete GA): Ein Muster passt, wenn alle seine Wörter im
    /// Titel vorkommen. Exakte Titel aller Typen gehen vor, damit ein Muster kein geprüftes Modell übernimmt.
    /// </summary>
    public IReadOnlyList<string[]> TitleWords { get; init; } = [];

    public bool MatchesExactly(string title) => Titles.Any(x => string.Equals(x, title.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool MatchesWords(string title) =>
        TitleWords.Any(words => words.All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)));
}

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
        new("A20N", "Airbus A320neo", TrafficCategory.Airliner, ["A320neo V2"], TrafficModelStatus.Verified,
            "EDDL 1.11.2026: gear down by 2720 ft and visible, landed on both runways (696 m / 1204 m)."),
        new("A333", "Airbus A330-300", TrafficCategory.Airliner, ["A330-300 (RR)", "A330-300 (GE)"], TrafficModelStatus.Verified,
            "EDDL 1.11.2026: gear visible (slightly in the ground, no physics), touchdown 330 m."),
        new("A321", "Airbus A321", TrafficCategory.Airliner, ["A321"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed, late touchdown."),
        new("A310", "Airbus A310", TrafficCategory.Airliner, ["A310"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed."),
        new("A337", "Airbus A330 BelugaXL", TrafficCategory.Airliner, ["A330-BelugaXL"], TrafficModelStatus.Candidate,
            "EDDL 31.10.2026: landed, touchdown 897 m."),
        new("B748", "Boeing 747-8", TrafficCategory.Airliner, ["747-8i"], TrafficModelStatus.Candidate, "EDDL 31.10.2026: landed."),
        new("B738", "Boeing 737-800", TrafficCategory.Airliner, ["Asobo PassiveAircraft B737-800"], TrafficModelStatus.Candidate,
            "EDDL 1.11.2026: landed, touchdown 1144 m."),

        // Allgemeine Luftfahrt aus MSFS 2024 (Standard, Deluxe, Premium Deluxe) – noch ungetestet, Titel über Wörter
        Ga("C152", "Cessna 152", [["152"]]),
        Ga("C172", "Cessna 172 Skyhawk", [["172"]]),
        Ga("C208", "Cessna 208B Grand Caravan EX", [["Caravan"]]),
        Ga("COL4", "Cessna 400 Corvalis TT", [["Corvalis"]]),
        Ga("C404", "Cessna 404 Titan", [["Titan"]]),
        Ga("C408", "Cessna 408 SkyCourier", [["SkyCourier"]]),
        Ga("C25C", "Cessna Citation CJ4", [["CJ4"]]),
        Ga("C700", "Cessna Citation Longitude", [["Longitude"]]),
        Ga("BE36", "Beechcraft Bonanza G36", [["Bonanza"]]),
        Ga("BE58", "Beechcraft Baron G58", [["Baron"]]),
        Ga("BE9L", "Beechcraft King Air C90 GTX", [["King Air", "C90"]]),
        Ga("B350", "Beechcraft King Air 350i", [["King Air", "350"]]),
        Ga("SR22", "Cirrus SR22", [["SR22"]]),
        Ga("SF50", "Cirrus Vision SF50", [["SF50"], ["Vision Jet"]]),
        Ga("TBM9", "Daher TBM 930", [["TBM"]]),
        Ga("DA40", "Diamond DA40", [["DA40"]]),
        Ga("DA62", "Diamond DA62", [["DA62"]]),
        Ga("DV20", "Diamond DV20", [["DV20"]]),
        Ga("PC6T", "Pilatus PC-6 B2", [["PC-6"], ["PC6"]]),
        Ga("PC12", "Pilatus PC-12 NGX", [["PC-12"], ["PC12"]]),
        Ga("PC24", "Pilatus PC-24", [["PC-24"], ["PC24"]]),
        Ga("DR40", "Robin DR400-100 Cadet", [["DR400"]]),
        Ga("DHC2", "De Havilland Canada DHC-2 Beaver", [["Beaver"]]),
        Ga("DHC6", "De Havilland Canada DHC-6 Twin Otter", [["Twin Otter"]]),
    ];

    /// <summary>Ungetesteter GA-Typ, erkannt an Titelwörtern.</summary>
    private static TrafficModelType Ga(string icaoType, string name, IReadOnlyList<string[]> titleWords) =>
        new(icaoType, name, TrafficCategory.GeneralAviation, [], TrafficModelStatus.Untested, "MSFS 2024 aircraft list, not tested yet.")
        {
            TitleWords = titleWords,
        };

    /// <summary>Das Muster, an dem ein Modell gesperrt ist, sonst null.</summary>
    public static string? ExcludedBy(string title, string? livery = null) =>
        ExcludedPatterns.FirstOrDefault(p =>
            title.Contains(p, StringComparison.OrdinalIgnoreCase)
            || (livery?.Contains(p, StringComparison.OrdinalIgnoreCase) ?? false));

    public static bool IsExcluded(string title, string? livery = null) => ExcludedBy(title, livery) is not null;

    /// <summary>Der freigegebene Typ zu einem Modelltitel (exakte Titel vor Titelwörtern), sonst null – auch für gesperrte Modelle.</summary>
    public static TrafficModelType? TypeOf(string title, string? livery = null) =>
        IsExcluded(title, livery)
            ? null
            : Approved.FirstOrDefault(t => t.MatchesExactly(title)) ?? Approved.FirstOrDefault(t => t.MatchesWords(title));

    /// <summary>Status als Text für Tools und Log: verified, candidate, untested, excluded oder unknown.</summary>
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
