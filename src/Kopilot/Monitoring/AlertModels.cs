using System.Text.Json.Serialization;

namespace Kopilot.Monitoring;

[JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
public enum AlertSeverity
{
    Info,
    Caution,
    Warning,
}

[JsonConverter(typeof(JsonStringEnumConverter<AlertKind>))]
public enum AlertKind
{
    /// <summary>Zustand beginnt (bleibt aktiv bis "Clear").</summary>
    Raise,

    /// <summary>Einmaliges Ereignis (z. B. Autopilot aus, Runway zugewiesen).</summary>
    Event,

    /// <summary>Zustand beendet.</summary>
    Clear,
}

/// <summary>Meldung des Monitorings.</summary>
/// <param name="Id">Fortlaufende Nummer (für get_alerts/wait_for_alerts "sinceId").</param>
/// <param name="Rule">Regel-ID, z. B. "GEAR_SPEED".</param>
/// <param name="Callout">Kurzer englischer Callout zum Vorlesen, null bei Clear oder wenn stumm.</param>
/// <param name="Message">Erklärung in der gewählten Sprache mit den Werten.</param>
public sealed record AlertEvent(
    long Id,
    DateTimeOffset TimeUtc,
    string Rule,
    AlertSeverity Severity,
    AlertKind Kind,
    string? Callout,
    string Message)
{
    /// <summary>
    /// Kurze englische Erinnerung, falls die Meldung quittiert werden muss und keine eigene Ansage hat – z. B. "Check squawk 2340",
    /// wenn ATC die Abweichung schon angesprochen hat (Bestätigungspflicht der Sprachschleife). null = <see cref="Callout"/>.
    /// </summary>
    public string? Reminder { get; init; }
}

/// <summary>Alle Regel-IDs.</summary>
public static class Rules
{
    public const string SimPaused = "SIM_PAUSED";
    public const string GearSpeed = "GEAR_SPEED";
    public const string FlapSpeed = "FLAP_SPEED";
    public const string GearDownHigh = "GEAR_DOWN_HIGH";
    public const string SpeedLimitFl100 = "SPEED_LIMIT_FL100";
    public const string SpeedDeviation = "SPEED_DEVIATION";
    public const string SinkRate = "SINK_RATE";
    public const string BankAngle = "BANK_ANGLE";
    public const string ApDisconnect = "AP_DISCONNECT";
    public const string MasterWarning = "MASTER_WARNING";
    public const string MasterCaution = "MASTER_CAUTION";
    public const string Alt1000ToGo = "ALT_1000_TO_GO";
    public const string RunwayAssigned = "RUNWAY_ASSIGNED";
    public const string Stall = "STALL";
    public const string Overspeed = "OVERSPEED";
    public const string EcamMessage = "ECAM_MESSAGE";
    public const string TopOfDescent = "TOP_OF_DESCENT";
    public const string LandingCheck = "LANDING_CHECK";
    public const string TrafficProximity = "TRAFFIC_PROXIMITY";

    // Pilot-Monitoring-Callouts (Startlauf, Landung, Höhenmarken, Konfiguration)
    public const string Pm100Knots = "PM_100_KNOTS";
    public const string PmV1 = "PM_V1";
    public const string PmRotate = "PM_ROTATE";
    public const string PmPositiveClimb = "PM_POSITIVE_CLIMB";
    public const string PmSpoilers = "PM_SPOILERS";
    public const string PmReverse = "PM_REVERSE";
    public const string PmDecel = "PM_DECEL";
    public const string Pm70Knots = "PM_70_KNOTS";
    public const string PmTenThousand = "PM_TEN_THOUSAND";
    public const string PmTransition = "PM_TRANSITION";
    public const string PmFlaps = "PM_FLAPS";
    public const string PmGear = "PM_GEAR";

    // Am Boden: zu schnell gerollt (v0.9, Sprachschleife)
    public const string TaxiSpeed = "TAXI_SPEED";

    // Fuel Check im Reiseflug: Ansage an Wegpunkten/im Intervall, Warnung bei wachsender Abweichung oder knapper Landemenge
    public const string FuelCheck = "FUEL_CHECK";
    public const string FuelWarning = "FUEL_WARNING";

    // Autoflug und Anflug (v0.9.2): TCAS RA, A/THR aus, FMA-Wechsel, LOC/GS-Abweichung, Approach-Phase nicht aktiviert
    public const string TcasRa = "TCAS_RA";
    public const string AutothrustOff = "ATHR_OFF";
    public const string FmaChange = "FMA_CHANGE";
    public const string LocDeviation = "LOC_DEVIATION";
    public const string GsDeviation = "GS_DEVIATION";
    public const string ApproachPhase = "APPR_NOT_ACTIVE";

    // Ansagen, die viele Airliner selbst machen (FWC) – nur mit Monitoring:AircraftAutoCallouts
    public const string AutoRadioAltitude = "AUTO_RA";
    public const string AutoMinimum = "AUTO_MINIMUM";
    public const string AutoRetard = "AUTO_RETARD";

    public static bool IsAircraftAutoCallout(string rule) => rule.StartsWith("AUTO_", StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> All =
    [
        SimPaused, GearSpeed, FlapSpeed, GearDownHigh, SpeedLimitFl100, SpeedDeviation, SinkRate, BankAngle,
        ApDisconnect, MasterWarning, MasterCaution, EcamMessage, Alt1000ToGo, TopOfDescent, LandingCheck, RunwayAssigned, Stall, Overspeed,
        TrafficProximity,
        Pm100Knots, PmV1, PmRotate, PmPositiveClimb, PmSpoilers, PmReverse, PmDecel, Pm70Knots, PmTenThousand, PmTransition, PmFlaps, PmGear,
        TaxiSpeed,
        FuelCheck, FuelWarning,
        TcasRa, AutothrustOff, FmaChange, LocDeviation, GsDeviation, ApproachPhase,
        AutoRadioAltitude, AutoMinimum, AutoRetard,
    ];
}
