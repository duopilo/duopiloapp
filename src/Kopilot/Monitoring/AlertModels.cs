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
/// <param name="Message">Erklärung auf Deutsch mit den Werten.</param>
public sealed record AlertEvent(
    long Id,
    DateTimeOffset TimeUtc,
    string Rule,
    AlertSeverity Severity,
    AlertKind Kind,
    string? Callout,
    string Message);

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

    public static readonly IReadOnlyList<string> All =
    [
        SimPaused, GearSpeed, FlapSpeed, GearDownHigh, SpeedLimitFl100, SpeedDeviation, SinkRate, BankAngle,
        ApDisconnect, MasterWarning, MasterCaution, EcamMessage, Alt1000ToGo, TopOfDescent, LandingCheck, RunwayAssigned, Stall, Overspeed,
    ];
}
