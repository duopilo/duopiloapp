using Duopilo.Traffic;

namespace Duopilo.Fire;

/// <summary>Alarmstufe wie bei der echten Flughafenfeuerwehr (docs/research/feuerwehr-einsaetze.md).</summary>
public enum FireAlertLevel
{
    /// <summary>Defekt bekannt oder vermutet, Landung ohne ernste Schwierigkeiten erwartet – meist Pan Pan.</summary>
    LocalStandby,

    /// <summary>Flugzeug in Schwierigkeiten, Unfallgefahr – meist Mayday.</summary>
    FullEmergency,

    /// <summary>Notfall am Boden (Brand, heiße Bremsen): direkt zum Flugzeug, keine Bereitstellung an der Bahn.</summary>
    GroundIncident,
}

/// <summary>Fahrzeugart der Flughafenfeuerwehr.</summary>
public enum FireVehicleKind
{
    /// <summary>Flugfeldlöschfahrzeug.</summary>
    Flf,

    /// <summary>Einsatzleitwagen.</summary>
    Elw,

    /// <summary>Hilfeleistungslöschfahrzeug.</summary>
    Hlf,

    /// <summary>Rettungswagen.</summary>
    Rtw,

    /// <summary>Rettungstreppe.</summary>
    Rtf,
}

/// <summary>Wo ein Fahrzeug an der Bahn bereitsteht.</summary>
public enum FireStandbyRole
{
    /// <summary>Haltepunkt an der Aufsetzzone (rund 300–600 m nach der Schwelle).</summary>
    Touchdown,

    /// <summary>Haltepunkt nahe der Bahnmitte (Einsatzleitung und Rettungsdienst).</summary>
    Mid,

    /// <summary>Haltepunkt kurz hinter der erwarteten Stoppstelle.</summary>
    Rollout,

    /// <summary>Haltepunkt vor dem Bahnende (Überrollen).</summary>
    End,

    /// <summary>Kein Haltepunkt: Notfall am Boden, direkt zum Flugzeug.</summary>
    Aircraft,
}

/// <summary>Ein Fahrzeug der Alarmierung: Art, laufende Nummer je Art (FLF 1, FLF 2 …) und Bereitstellungsraum.</summary>
public sealed record FireUnit(FireVehicleKind Kind, int Number, FireStandbyRole Role)
{
    /// <summary>Kurzname für Status und Log, z. B. "FLF 2".</summary>
    public string Name => Kind.ToString().ToUpperInvariant() + " " + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Feuerwehr-Kategorie (ICAO, 1–10) eines Flugzeugs und die Fahrzeuge je Alarmstufe. Die Kategorie richtet sich nach der Gesamtlänge
/// (Grenzen 9, 12, 18, 24, 28, 39, 49, 61, 76 m); ohne bekannten Typ dient die Wirbelschleppen-Klasse als Ersatz.
/// </summary>
public static class FireCategory
{
    // Gesamtlänge in Metern (gerundet) der häufigen Typen; A380 zählt wegen der Rumpfbreite zu Kategorie 10
    private static readonly Dictionary<string, double> LengthM = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C152"] = 7.3, ["C172"] = 8.3, ["P28A"] = 7.3, ["SR22"] = 7.9, ["DA40"] = 8.0, ["DA62"] = 9.2, ["BE58"] = 9.1,
        ["C208"] = 11.5, ["TBM9"] = 10.7, ["PC12"] = 14.4, ["BE20"] = 13.3, ["C25C"] = 16.3, ["C700"] = 22.3,
        ["SF34"] = 19.7, ["AT45"] = 22.7, ["AT46"] = 22.7, ["AT72"] = 27.2, ["AT76"] = 27.2, ["DH8A"] = 22.3, ["DH8D"] = 32.8,
        ["CRJ2"] = 26.8, ["CRJ7"] = 32.5, ["CRJ9"] = 36.4, ["CRJX"] = 39.1, ["E135"] = 26.3, ["E145"] = 29.9,
        ["E170"] = 29.9, ["E175"] = 31.7, ["E190"] = 36.2, ["E195"] = 38.7, ["E290"] = 36.3, ["E295"] = 41.5,
        ["BCS1"] = 35.0, ["BCS3"] = 38.7,
        ["A318"] = 31.4, ["A319"] = 33.8, ["A320"] = 37.6, ["A321"] = 44.5, ["A19N"] = 33.8, ["A20N"] = 37.6, ["A21N"] = 44.5,
        ["B736"] = 31.2, ["B737"] = 33.6, ["B738"] = 39.5, ["B739"] = 42.1, ["B37M"] = 35.6, ["B38M"] = 39.5, ["B39M"] = 42.2,
        ["B3XM"] = 43.8, ["B752"] = 47.3, ["B753"] = 54.4, ["MD82"] = 45.1, ["MD88"] = 45.1, ["A400"] = 45.1,
        ["A332"] = 58.8, ["A333"] = 63.7, ["A338"] = 58.8, ["A339"] = 63.7, ["A342"] = 59.4, ["A343"] = 63.7, ["A345"] = 67.9,
        ["A346"] = 75.4, ["A359"] = 66.8, ["A35K"] = 73.8, ["A3ST"] = 56.2, ["A337"] = 63.1,
        ["B762"] = 48.5, ["B763"] = 54.9, ["B764"] = 61.4, ["B772"] = 63.7, ["B77L"] = 63.7, ["B77W"] = 73.9,
        ["B788"] = 56.7, ["B789"] = 62.8, ["B78X"] = 68.3, ["B744"] = 70.7, ["B748"] = 76.3, ["BLCF"] = 71.7,
        ["MD11"] = 61.6, ["DC10"] = 55.5,
    };

    private static readonly double[] Limits = [9, 12, 18, 24, 28, 39, 49, 61, 76];

    /// <summary>Kategorie aus dem ICAO-Typ, sonst aus der Wirbelschleppen-Klasse (Light 4, Medium 7, Heavy 9, Super 10).</summary>
    public static int Of(string? icaoType, TrafficWake wake)
    {
        if (icaoType is { Length: > 0 } type)
        {
            if (string.Equals(type, "A388", StringComparison.OrdinalIgnoreCase))
            {
                return 10;
            }

            if (LengthM.TryGetValue(type, out var length))
            {
                return FromLength(length);
            }
        }

        return wake switch
        {
            TrafficWake.Light => 4,
            TrafficWake.Medium => 7,
            TrafficWake.Heavy => 9,
            _ => 10,
        };
    }

    /// <summary>Kategorie aus der Gesamtlänge (ICAO-Grenzen).</summary>
    public static int FromLength(double lengthM)
    {
        for (var i = 0; i < Limits.Length; i++)
        {
            if (lengthM < Limits[i])
            {
                return i + 1;
            }
        }

        return 10;
    }

    /// <summary>
    /// Fahrzeuge je Stufe und Kategorie, in der Reihenfolge der Alarmierung: bis 5 ein FLF, 6–7 zwei, ab 8 drei; bei Full Emergency
    /// dazu HLF, Rettungstreppe(n) und RTW. Ein Notfall am Boden bekommt dieselben Fahrzeuge wie ein Local Standby.
    /// </summary>
    public static IReadOnlyList<FireUnit> Lineup(FireAlertLevel level, int category)
    {
        var flf = category <= 5 ? 1 : category <= 7 ? 2 : 3;
        var aircraft = level == FireAlertLevel.GroundIncident;
        var units = new List<FireUnit>();
        FireStandbyRole Role(FireStandbyRole role) => aircraft ? FireStandbyRole.Aircraft : role;

        // Ein einzelnes FLF steht an der Stoppstelle, mehrere an Aufsetzzone, Stoppstelle und Bahnende
        var flfRoles = flf == 1
            ? new[] { FireStandbyRole.Rollout }
            : new[] { FireStandbyRole.Touchdown, FireStandbyRole.Rollout, FireStandbyRole.End };
        for (var i = 0; i < flf; i++)
        {
            units.Add(new FireUnit(FireVehicleKind.Flf, i + 1, Role(flfRoles[i])));
        }

        var full = level == FireAlertLevel.FullEmergency;
        if (category > 5 || full)
        {
            units.Add(new FireUnit(FireVehicleKind.Elw, 1, Role(FireStandbyRole.Mid)));
        }

        if (!full)
        {
            return units;
        }

        if (category > 5)
        {
            units.Add(new FireUnit(FireVehicleKind.Hlf, 1, FireStandbyRole.Mid));
            var stairs = category >= 8 ? 2 : 1;
            for (var i = 0; i < stairs; i++)
            {
                units.Add(new FireUnit(FireVehicleKind.Rtf, i + 1, FireStandbyRole.Mid));
            }
        }

        units.Add(new FireUnit(FireVehicleKind.Rtw, 1, FireStandbyRole.Mid));
        return units;
    }

    /// <summary>
    /// Grundmaß für die Plätze rund um das Flugzeug (Meter): gut die halbe Spannweite plus Abstand – Light 12, Medium 30, Heavy 42,
    /// Super 50.
    /// </summary>
    public static double PostUnitM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 12,
        TrafficWake.Medium => 30,
        TrafficWake.Heavy => 42,
        _ => 50,
    };

    /// <summary>Abstand hinter dem Bezugspunkt des Flugzeugs beim Folgen auf der Bahn (Abgasstrahl): Startwerte bis zum Test im Sim.</summary>
    public static double RunwayGapM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 35,
        TrafficWake.Medium => 80,
        TrafficWake.Heavy => 100,
        _ => 110,
    };

    /// <summary>Abstand beim Folgen auf den Rollwegen (Rollschub, langsamer).</summary>
    public static double TaxiGapM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 30,
        TrafficWake.Medium => 50,
        TrafficWake.Heavy => 65,
        _ => 75,
    };

    /// <summary>Erwartete Stoppstelle hinter der Schwelle (Meter) ohne Kenntnis der echten Landestrecke.</summary>
    public static double ExpectedStopM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 500,
        TrafficWake.Medium => 1500,
        TrafficWake.Heavy => 2000,
        _ => 2400,
    };
}

/// <summary>Kennwerte der Feuerwehrfahrzeuge im Einsatz (schneller als Follow-me, langsamer in Kurven).</summary>
public static class FireProfile
{
    /// <summary>
    /// Höchstens 90 km/h (rund 49 kt) – Einsatzfahrt über Rollwege und Bahn; echte FLF schaffen über 100 km/h, die Eintreffzeit
    /// zum Bahnende liegt bei 2–3 Minuten.
    /// </summary>
    public const double MaxMps = 25;

    /// <summary>Rückfahrt zur Wache ohne Blaulicht: 40 km/h.</summary>
    public const double ReturnMps = 11.1;

    /// <summary>Am Flugzeug und beim Umstellen: 20 km/h.</summary>
    public const double PositionMps = 5.5;

    /// <summary>Kurvenradius an echten Ecken (ein FLF ist lang und schwer): rund 17 kt in der Kurve.</summary>
    public const double TurnRadiusM = 25;

    public const double MinTurnMps = 2.5;

    /// <summary>Anfahren: 0 auf 80 km/h in gut 20 s.</summary>
    public const double AccelMps2 = 1.1;

    public const double DecelMps2 = 2.5;

    public const double LateralMps2 = 3.0;

    /// <summary>Abstandsregelung beim Folgen: so viel schneller (m/s) je Meter zu viel Abstand.</summary>
    public const double GapGain = 0.2;

    /// <summary>In der Kolonne: so viel Abstand zwischen den Fahrzeugen.</summary>
    public const double ColumnSpacingM = 25;

    /// <summary>Teilen sich Fahrzeuge einen Haltepunkt, hält jedes weitere so viel früher.</summary>
    public const double QueueSpacingM = 22;

    /// <summary>
    /// Betriebsstraßen kosten auf der Anfahrt 0,7 Meter Rollweg je Meter: Ein Umweg über die Straße bis zum 1,4-Fachen ist es wert,
    /// mehr nicht – im Einsatz zählt die Zeit, und zur Bahn geht es ohnehin über Rollwege.
    /// </summary>
    public const double RoadCostFactor = 0.7;

    /// <summary>Auf der Rückfahrt ohne Blaulicht bleiben die Fahrzeuge noch lieber auf den Straßen (Umweg bis zum Doppelten).</summary>
    public const double ReturnRoadCostFactor = 0.5;
}
