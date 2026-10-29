using System.Runtime.InteropServices;

namespace Kopilot.Sim;

// Die verwaltete SimConnect-API erwartet IDs als Enum. Wir vergeben sie dynamisch per Cast aus uint.

public enum SimDefinitionId : uint
{
    None = 0,
}

public enum SimRequestId : uint
{
    None = 0,
}

/// <summary>IDs für eigene Client-Events (Eingaben). Werden je Verbindung dynamisch vergeben.</summary>
public enum SimClientEventId : uint
{
    None = 0,
}

/// <summary>Gruppe beim Senden von Events: mit GROUPID_IS_PRIORITY ist das die Priorität (1 = höchste).</summary>
public enum SimGroupId : uint
{
    PriorityHighest = 1,
}

/// <summary>Container für String-SimVars (SIMCONNECT_DATATYPE.STRING256).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct SimString256
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Value;
}

/// <summary>
/// Ein Flugzeug aus RequestDataOnSimObjectType (Verkehrsabfrage). Reihenfolge und Typen müssen exakt zur
/// Data Definition in <see cref="SimConnectClient"/> passen (<see cref="TrafficVariables"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct TrafficDatum
{
    public double Latitude;
    public double Longitude;
    public double AltitudeFt;
    public double HeadingTrueDeg;
    public double HeadingMagDeg;
    public double GroundSpeedKt;
    public double VerticalSpeedFpm;
    public double OnGround;
    public double IsUser;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcAirline;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcFlightNumber;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcModel;
}

/// <summary>SimVars der Verkehrsabfrage in der Reihenfolge von <see cref="TrafficDatum"/>. Unit null = Text (STRING64).</summary>
public static class TrafficVariables
{
    public static readonly IReadOnlyList<(string Name, string? Unit)> All =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("PLANE HEADING DEGREES MAGNETIC", "degrees"),
        ("GROUND VELOCITY", "knots"),
        ("VERTICAL SPEED", "feet per minute"),
        ("SIM ON GROUND", "Bool"),
        ("IS USER SIM", "Bool"),
        ("ATC ID", null),
        ("ATC AIRLINE", null),
        ("ATC FLIGHT NUMBER", null),
        ("ATC MODEL", null),
    ];
}

/// <summary>Rohdaten eines Flugzeugs aus der Verkehrsabfrage (eigenes Flugzeug eingeschlossen, siehe <see cref="IsUser"/>).</summary>
/// <param name="ObjectId">SimConnect-Objekt-ID.</param>
/// <param name="Category">"aircraft" oder "helicopter".</param>
/// <param name="AltitudeFt">Wahre Höhe über MSL (PLANE ALTITUDE).</param>
public sealed record AircraftSample(
    uint ObjectId,
    string Category,
    string? AtcId,
    string? AtcAirline,
    string? AtcFlightNumber,
    string? AtcModel,
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double HeadingTrueDeg,
    double HeadingMagDeg,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    bool OnGround,
    bool IsUser);

/// <summary>Ergebnis einer Verkehrsabfrage.</summary>
/// <param name="Complete">Alle Antworten von MSFS sind eingetroffen.</param>
/// <param name="Error">Fehlertext, wenn die Abfrage nicht ausgeführt werden konnte.</param>
public sealed record AircraftScan(IReadOnlyList<AircraftSample> Samples, bool Complete, string? Error)
{
    public static AircraftScan Fail(string error) => new([], false, error);
}

/// <summary>IDs für abonnierte SimConnect-Systemereignisse.</summary>
public enum SimEventId : uint
{
    None = 0,
    Pause = 1,
    Sim = 2,
    AircraftLoaded = 3,
}

/// <summary>Zustand des Simulators (nicht des Flugzeugs).</summary>
/// <param name="Paused">Pausiert (normale Pause, Active Pause oder Sim-Pause).</param>
/// <param name="PauseFlags">Rohwert aus Pause_EX1: 1 = Pause, 4 = Active Pause, 8 = Sim-Pause.</param>
/// <param name="FlightRunning">true = im Flug, false = Menü/Ladebildschirm, null = unbekannt.</param>
/// <param name="AircraftPath">Pfad zur aircraft.cfg des geladenen Flugzeugs.</param>
public sealed record SimStatus(bool Paused, uint PauseFlags, bool? FlightRunning, string? AircraftPath)
{
    public string? PauseKind => PauseFlags switch
    {
        0 => null,
        _ when (PauseFlags & 4) != 0 => "active",
        _ when (PauseFlags & 8) != 0 => "sim",
        _ => "full",
    };
}
