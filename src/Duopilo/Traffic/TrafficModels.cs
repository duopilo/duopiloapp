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
    /// <summary>Anfluggeschwindigkeit (Vapp) in Knoten – daraus macht der Testanflug die Geschwindigkeiten der Wegpunkte.</summary>
    public double ApproachKt { get; init; } = TrafficTestPlan.DefaultApproachKt;

    /// <summary>Ein KI-Verkehrsmodell von Asobo/Microsoft (PassiveAircraft) – Lichter und Fahrwerk folgen den normalen Befehlen.</summary>
    public bool Passive => Titles.Any(t => t.Contains("PassiveAircraft", StringComparison.OrdinalIgnoreCase));

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
/// Erste Wahl sind die KI-Verkehrsmodelle von MSFS 2024 („Asobo PassiveAircraft …“, „Microsoft PassiveAircraft …“, rund 250 Typen
/// mit Airline-Lackierungen): Bei ihnen folgen Lichter und Fahrwerk den normalen Befehlen. Volle, flugfähige Modelle zeigen als KI
/// keine Lichter (A320neo V2, EDDL 1.11.2026 – die LIGHT-Werte stehen auf an, zu sehen ist nichts), weil ihre Systeme nicht laufen.
///
/// Gesperrt sind die Modelle von FlyByWire (A32NX, A380X) und Headwind (A330-900neo): Fahrwerk nur halb draußen, Triebwerke drehen
/// nicht. Die Titel nennen den Hersteller nicht immer (Headwind: „Airbus A330-900neo Condor Beach“), deshalb Titelmuster.
///
/// Freigaben gelten über exakte Titel, damit Varianten nicht ungeprüft mitrutschen. „Verified“ erst, wenn Fahrwerk, Lichter und
/// Landung im Sim angesehen wurden.
/// </summary>
public static class TrafficModels
{
    private const string Asobo = "Asobo PassiveAircraft ";

    private const string Microsoft = "Microsoft PassiveAircraft ";

    /// <summary>Teilstücke von Titel oder Livery (ohne Groß-/Kleinschreibung), bei denen ein Modell gesperrt ist.</summary>
    public static readonly IReadOnlyList<string> ExcludedPatterns = ["FlyByWire", "A32NX", "A380X", "Headwind", "A330-900neo"];

    /// <summary>Freigegebene Typen: geprüfte KI-Verkehrsmodelle zuerst, dann die übrigen, dann volle Modelle als Notlösung.</summary>
    public static readonly IReadOnlyList<TrafficModelType> Approved =
    [
        // Geprüft (EDDL 1.11.2026, A2, beide Bahnen)
        Passive("A20N", "Airbus A320neo", 135, TrafficModelStatus.Verified, "EDDL 1.11.2026: lights and gear visible, touchdown at the right spot.", "A320 NEO"),
        Passive("B738", "Boeing 737-800", 145, TrafficModelStatus.Verified, "EDDL 1.11.2026: lights and gear visible, touchdown 719 m.", "B737-800", "B737-800F"),

        // Airbus
        Passive("BCS1", "Airbus A220-100", 125, "A220-100"),
        Passive("BCS3", "Airbus A220-300", 130, "A220-300"),
        Passive("A318", "Airbus A318", 125, "A318"),
        Passive("A319", "Airbus A319", 130, "A319 CEO"),
        Passive("A320", "Airbus A320", 135, "A320 CEO"),
        Passive("A321", "Airbus A321", 140, "A321 CEO"),
        Passive("A21N", "Airbus A321neo", 140, "A321 NEO"),
        Passive("A310", "Airbus A310", 140, "A310-300", "A310-300F"),
        Passive("A332", "Airbus A330-200", 140, "A330-200", "A330-200F"),
        Passive("A333", "Airbus A330-300", 140, "A330-300", "A330-300F"),
        Passive("A339", "Airbus A330-900neo", 140, "A330-900"),
        Passive("A343", "Airbus A340-300", 145, "A340-300"),
        Passive("A346", "Airbus A340-600", 150, "A340-600"),
        Passive("A359", "Airbus A350-900", 140, "A350-900", "A350-900ULR"),
        Passive("A35K", "Airbus A350-1000", 145, "A350-1000"),
        Passive("A388", "Airbus A380-800", 145, "A380-800"),

        // Boeing und McDonnell Douglas
        Passive("B712", "Boeing 717-200", 135, "B717-200"),
        Passive("B733", "Boeing 737-300", 140, "B737-300", "B737-300F"),
        Passive("B734", "Boeing 737-400", 140, "B737-400"),
        Passive("B735", "Boeing 737-500", 135, "B737-500"),
        Passive("B736", "Boeing 737-600", 135, "B737-600"),
        Passive("B737", "Boeing 737-700", 140, "B737-700", "B737-700F"),
        Passive("B739", "Boeing 737-900", 150, "B737-900", "B737-900ER"),
        Passive("B38M", "Boeing 737 MAX 8", 145, "B737-Max8"),
        Passive("B39M", "Boeing 737 MAX 9", 150, "B737-Max9"),
        Passive("B744", "Boeing 747-400", 150, "B747-400", "B747-400BDSF", "B747-400ERF", "B747-400F"),
        Passive("B748", "Boeing 747-8", 155, "B747-8i", "B747-8F"),
        Passive("B752", "Boeing 757-200", 135, "B757-200", "B757-200SF"),
        Passive("B753", "Boeing 757-300", 140, "B757-300"),
        Passive("B762", "Boeing 767-200", 135, "B767-200ER"),
        Passive("B763", "Boeing 767-300", 140, "B767-300", "B767-300BCF", "B767-300ER", "B767-300ERF"),
        Passive("B764", "Boeing 767-400", 145, "B767-400ER"),
        Passive("B772", "Boeing 777-200", 140, "B777-200", "B777-200ER"),
        Passive("B77L", "Boeing 777-200LR / 777F", 145, "B777-200LR", "B777-F"),
        Passive("B773", "Boeing 777-300", 150, "B777-300"),
        Passive("B77W", "Boeing 777-300ER", 150, "B777-300ER"),
        Passive("B788", "Boeing 787-8", 140, "B787-08"),
        Passive("B789", "Boeing 787-9", 145, "B787-09"),
        Passive("B78X", "Boeing 787-10", 150, "B787-10"),
        Passive("MD11", "McDonnell Douglas MD-11", 155, "MD11F"),
        Passive("DC10", "McDonnell Douglas MD-10", 150, "MD10-10F", "MD10-30F"),
        Passive("MD88", "McDonnell Douglas MD-88", 135, "MD88"),
        Passive("MD90", "McDonnell Douglas MD-90", 135, "MD90_30"),

        // Regional
        Passive("AT43", "ATR 42-300", 105, "ATR42-300", "ATR42-300F", "ATR42-320"),
        Passive("AT45", "ATR 42-500", 110, "ATR42-500"),
        Passive("AT46", "ATR 42-600", 110, "ATR42-600"),
        Passive("AT72", "ATR 72-200", 115, "ATR72-200F", "ATR72-300F"),
        Passive("AT75", "ATR 72-500", 115, "ATR72-500", "ATR72-500F"),
        Passive("AT76", "ATR 72-600", 115, "ATR72-600", "ATR72-600F"),
        Passive("DH8A", "Dash 8-100", 105, "DHC-8 100"),
        Passive("DH8B", "Dash 8-200", 105, "DHC-8 200"),
        Passive("DH8C", "Dash 8-300", 110, "DHC-8 300"),
        Passive("DH8D", "Dash 8-400", 120, "DHC-8 400"),
        Passive("E135", "Embraer ERJ-135", 125, "E135"),
        Passive("E145", "Embraer ERJ-145", 130, "E145"),
        Passive("E170", "Embraer E170", 125, "E170"),
        Passive("E75L", "Embraer E175", 125, "E175"),
        Passive("E190", "Embraer E190", 130, "E190"),
        Passive("E290", "Embraer E190-E2", 130, "E190-E2"),
        Passive("E195", "Embraer E195", 130, "E195"),
        Passive("E295", "Embraer E195-E2", 130, "E195-E2"),
        Passive("SB20", "Saab 2000", 120, "S2000"),
        Passive("SF34", "Saab 340", 115, Microsoft + "S340B Passenger", Microsoft + "S340B Cargo"),
        Passive("B190", "Beechcraft 1900", 110, "Beechcraft 1900"),

        // Business
        Passive("C525", "Citation CJ / CJ1", 110, "Citation CJ", "Citation CJ1", "Citation CJ1 Plus"),
        Passive("C25A", "Citation CJ2", 115, "Citation CJ2", "Citation CJ2 Plus"),
        Passive("C25B", "Citation CJ3", 115, "Citation CJ3", "Citation CJ3 Plus"),
        Passive("C25C", "Citation CJ4", 115, "Citation CJ4"),
        Passive("C25M", "Citation M2", 110, "Citation M2"),
        Passive("C510", "Citation Mustang", 100, "Citation Mustang"),
        Passive("C500", "Citation I", 105, "Citation I"),
        Passive("C550", "Citation II", 110, "Citation II"),
        Passive("C560", "Citation V", 115, "Citation V"),
        Passive("C56X", "Citation XLS", 115, "Citation XLS", "Citation XLS Plus"),
        Passive("C650", "Citation III / VI / VII", 120, "Citation III", "Citation VI", "Citation VII"),
        Passive("C680", "Citation Sovereign", 115, "Citation Sovereign", "Citation Sovereign Plus"),
        Passive("C68A", "Citation Latitude", 115, "Citation Latitude"),
        Passive("C700", "Citation Longitude", 120, "Citation Longitude"),
        Passive("C750", "Citation X", 130, "Citation X", "Citation X Plus"),
        Passive("E50P", "Phenom 100", 105, "Phenom 100"),
        Passive("E55P", "Phenom 300", 115, "Phenom 300"),
        Passive("E545", "Praetor 500 / Legacy 500", 120, "Praetor 500", "Legacy 500"),
        Passive("E550", "Praetor 600", 120, "Praetor 600"),
        Passive("SF50", "Cirrus Vision SF50", 90, "SF50"),
        Passive("PC24", "Pilatus PC-24", 110, "PC24"),

        // Turboprops
        Passive("BE20", "King Air 200 / 260", 105, "King Air 200", "King Air 260"),
        Passive("B350", "King Air 350i / 360", 110, "King Air 350i", "King Air 360"),
        Passive("BE9L", "King Air C90 GTX", 100, Microsoft + "C90GTX Passenger", Microsoft + "C90GTX Medic"),
        Passive("PC12", "Pilatus PC-12", 90, "PC12"),
        Passive("PC6T", "Pilatus PC-6", 65, "PC6"),
        Passive("TBM9", "Daher TBM 930", 90, "TBM930"),
        Passive("C208", "Cessna 208 Caravan", 80, "C208", "C208B", "C208B Cargo"),
        Passive("KODI", "Kodiak 100", 75, "Kodiak"),
        Passive("C425", "Cessna 425 Conquest I", 105, "C425"),
        Passive("C441", "Cessna 441 Conquest II", 110, "C441"),

        // Allgemeine Luftfahrt (Kolben)
        Passive("C150", "Cessna 150", 60, "C150"),
        Passive("C152", "Cessna 152", 60, "C152", "C152 Aerobat"),
        Passive("C162", "Cessna 162 Skycatcher", 55, "C162"),
        Passive("C170", "Cessna 170", 60, "C170", "C170B"),
        Passive("C172", "Cessna 172", 65, "C172"),
        Passive("C72R", "Cessna 172RG", 70, "C172RG"),
        Passive("C175", "Cessna 175", 65, "C175"),
        Passive("C177", "Cessna 177 Cardinal", 70, "C177"),
        Passive("C77R", "Cessna 177RG", 75, "C177RG"),
        Passive("C182", "Cessna 182", 70, "C182", "T182"),
        Passive("C82R", "Cessna R182", 75, "R182", "TR182"),
        Passive("C185", "Cessna 185", 70, Microsoft + "C185 Passenger"),
        Passive("C206", "Cessna 206", 75, "C206 StationAir", "C206 Super Skywagon", "C206 Turbo StationAir"),
        Passive("C207", "Cessna 207", 75, Microsoft + "C207 Passenger", Microsoft + "C207 Cargo"),
        Passive("C210", "Cessna 210 Centurion", 80, "C210 Centurion", "T210 Turbo Centurion"),
        Passive("C310", "Cessna 310", 95, "C310", "C310R"),
        Passive("C337", "Cessna 337 Skymaster", 85, "C337"),
        Passive("C340", "Cessna 340", 95, "C340"),
        Passive("C402", "Cessna 402", 95, "C402"),
        Passive("C404", "Cessna 404 Titan", 95, Microsoft + "C404 Passengers", Microsoft + "C404 Cargo"),
        Passive("C408", "Cessna 408 SkyCourier", 100, Microsoft + "C408 Passenger", Microsoft + "C408 Cargo"),
        Passive("C414", "Cessna 414", 95, "C414"),
        Passive("C421", "Cessna 421", 100, "C421"),
        Passive("COL4", "Cessna 400 Corvalis", 85, Microsoft + "C400 Corvalis"),
        Passive("BE19", "Beechcraft 19 Sport", 65, "Beechcraft 19"),
        Passive("BE23", "Beechcraft 23 Musketeer", 70, "Beechcraft 23 Musketeer", "Beechcraft 23-24 Musketeer Super III"),
        Passive("BE24", "Beechcraft 24 Sierra", 75, "Beechcraft 24 Sierra"),
        Passive("BE33", "Beechcraft Bonanza 33", 75, "Bonanza 33", "Bonanza Debonair"),
        Passive("BE35", "Beechcraft Bonanza 35", 75, "Bonanza 35"),
        Passive("BE36", "Beechcraft Bonanza 36", 80, "Bonanza A36", "Bonanza G36"),
        Passive("BE55", "Beechcraft Baron 55", 90, "Baron 55"),
        Passive("BE56", "Beechcraft Baron 56", 95, "Baron 56TC"),
        Passive("BE58", "Beechcraft Baron 58", 95, "Baron 58", "Baron G58"),
        Passive("DV20", "Diamond DA20", 60, "DA20"),
        Passive("DA40", "Diamond DA40", 70, "DA40 NG", "DA40 TDI"),
        Passive("DA42", "Diamond DA42", 80, "DA42"),
        Passive("DA62", "Diamond DA62", 85, "DA62"),
        Passive("SR20", "Cirrus SR20", 75, "SR20"),
        Passive("SR22", "Cirrus SR22", 80, "SR22", "SR22T"),
        Passive("AA1", "Grumman AA-1", 65, "AA1"),
        Passive("AA5", "Grumman AA-5", 70, "AA5"),
        Passive("HUSK", "Aviat Husky", 55, "Husky"),

        // Volle Modelle – Notlösung: Fahrwerk und Landung gehen, Lichter sind als KI nicht zu sehen
        Full("A20N", "Airbus A320neo (iniBuilds, full)", 135, "A320neo V2",
            "EDDL 1.11.2026: gear visible, but no lights as AI – prefer the PassiveAircraft model."),
        Full("A333", "Airbus A330-300 (full)", 140, ["A330-300 (RR)", "A330-300 (GE)"],
            "EDDL 1.11.2026: gear visible (slightly in the ground), touchdown 330 m; lights not checked."),
        Full("A321", "Airbus A321 (full)", 140, "A321", "EDDL 31.10.2026: landed, late touchdown."),
        Full("A310", "Airbus A310 (full)", 140, "A310", "EDDL 31.10.2026: landed."),
        Full("A337", "Airbus A330 BelugaXL (full)", 140, "A330-BelugaXL", "EDDL 31.10.2026: landed, touchdown 897 m."),
        Full("B748", "Boeing 747-8 (full)", 155, "747-8i", "EDDL 31.10.2026: landed."),

        // Volle GA-Modelle aus MSFS 2024 – ungetestet, erkannt an Titelwörtern (die Passive-Titel oben gehen vor)
        Ga("C152", "Cessna 152", 60, [["152"]]),
        Ga("C172", "Cessna 172 Skyhawk", 65, [["172"]]),
        Ga("C208", "Cessna 208B Grand Caravan EX", 80, [["Caravan"]]),
        Ga("COL4", "Cessna 400 Corvalis TT", 85, [["Corvalis"]]),
        Ga("C404", "Cessna 404 Titan", 95, [["Titan"]]),
        Ga("C408", "Cessna 408 SkyCourier", 100, [["SkyCourier"]]),
        Ga("C25C", "Cessna Citation CJ4", 115, [["CJ4"]]),
        Ga("C700", "Cessna Citation Longitude", 120, [["Longitude"]]),
        Ga("BE36", "Beechcraft Bonanza G36", 80, [["Bonanza"]]),
        Ga("BE58", "Beechcraft Baron G58", 95, [["Baron"]]),
        Ga("BE9L", "Beechcraft King Air C90 GTX", 100, [["King Air", "C90"]]),
        Ga("B350", "Beechcraft King Air 350i", 110, [["King Air", "350"]]),
        Ga("SR22", "Cirrus SR22", 80, [["SR22"]]),
        Ga("SF50", "Cirrus Vision SF50", 90, [["SF50"], ["Vision Jet"]]),
        Ga("TBM9", "Daher TBM 930", 90, [["TBM"]]),
        Ga("DA40", "Diamond DA40", 70, [["DA40"]]),
        Ga("DA62", "Diamond DA62", 85, [["DA62"]]),
        Ga("DV20", "Diamond DV20", 60, [["DV20"]]),
        Ga("PC6T", "Pilatus PC-6 B2", 65, [["PC-6"], ["PC6"]]),
        Ga("PC12", "Pilatus PC-12 NGX", 90, [["PC-12"], ["PC12"]]),
        Ga("PC24", "Pilatus PC-24", 110, [["PC-24"], ["PC24"]]),
        Ga("DR40", "Robin DR400-100 Cadet", 65, [["DR400"]]),
        Ga("DHC2", "De Havilland Canada DHC-2 Beaver", 60, [["Beaver"]]),
        Ga("DHC6", "De Havilland Canada DHC-6 Twin Otter", 80, [["Twin Otter"]]),
    ];

    /// <summary>KI-Verkehrsmodell; Titel ohne Präfix bekommen „Asobo PassiveAircraft “ davor.</summary>
    private static TrafficModelType Passive(string icaoType, string name, double approachKt, params string[] titles) =>
        Passive(icaoType, name, approachKt, TrafficModelStatus.Untested, "PassiveAircraft model, not tested yet.", titles);

    private static TrafficModelType Passive(string icaoType, string name, double approachKt, TrafficModelStatus status, string note, params string[] titles) =>
        new(icaoType, name, CategoryOf(icaoType, approachKt), [.. titles.Select(t => t.Contains("PassiveAircraft", StringComparison.OrdinalIgnoreCase) ? t : Asobo + t)], status, note)
        {
            ApproachKt = approachKt,
        };

    private static TrafficModelType Full(string icaoType, string name, double approachKt, string title, string note) =>
        Full(icaoType, name, approachKt, [title], note);

    private static TrafficModelType Full(string icaoType, string name, double approachKt, string[] titles, string note) =>
        new(icaoType, name, TrafficCategory.Airliner, titles, TrafficModelStatus.Candidate, note) { ApproachKt = approachKt };

    /// <summary>Ungetesteter voller GA-Typ, erkannt an Titelwörtern.</summary>
    private static TrafficModelType Ga(string icaoType, string name, double approachKt, IReadOnlyList<string[]> titleWords) =>
        new(icaoType, name, TrafficCategory.GeneralAviation, [], TrafficModelStatus.Untested, "MSFS 2024 aircraft list, not tested yet.")
        {
            TitleWords = titleWords,
            ApproachKt = approachKt,
        };

    /// <summary>
    /// Airliner: Regionalflugzeuge (Linienverkehr, auch wenn sie langsamer als 120 kt anfliegen) und alles über 120 kt; sonst Allgemeine
    /// Luftfahrt (auch Business-Jets). Als Muster statt als statisches Feld, weil <see cref="Approved"/> davor initialisiert wird.
    /// </summary>
    private static TrafficCategory CategoryOf(string icaoType, double approachKt) =>
        icaoType is "AT43" or "AT45" or "AT46" or "AT72" or "AT75" or "AT76" or "DH8A" or "DH8B" or "DH8C" or "DH8D" or "SB20" or "SF34" or "B190"
        || approachKt > 120
            ? TrafficCategory.Airliner
            : TrafficCategory.GeneralAviation;

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

    /// <summary>
    /// Nach dem Aufsetzen beim Abbremsen: Landelicht und Strobes aus, Taxilicht bleibt an. Nav, Beacon und Logo stehen mit drin, weil
    /// die KI im Schlafzustand nach der Landung alle Lichter ausschaltet (beta.14) und der Test sie dann nachschickt.
    /// </summary>
    public static readonly AiLights Rollout = new(Nav: true, Beacon: true, Strobe: false, Landing: false, Taxi: true, Logo: true);

    /// <summary>Was nach der Landung an sein muss, sonst schickt der Test <see cref="Rollout"/> nach (Taxilicht haben die PassiveAircraft nicht).</summary>
    public static readonly AiLights RolloutRequired = new(Nav: true, Beacon: true);

    /// <summary>Unter dieser Geschwindigkeit am Boden schaltet der Test auf <see cref="Rollout"/>.</summary>
    public const double RolloutBelowKt = 60;
}
