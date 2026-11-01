using System.Runtime.InteropServices;

namespace Duopilo.Sim;

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
/// SimVars der Verkehrsabfrage, in dieser Reihenfolge in der Data Definition (siehe <see cref="TrafficConnection"/>).
/// Die Zahlen kommen als FLOAT64 (je 8 Byte), die Texte als STRING64 (je 64 Byte) dahinter.
/// </summary>
public static class TrafficVariables
{
    public static readonly IReadOnlyList<(string Name, string Unit)> Numeric =
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
    ];

    /// <summary>Nur bei get_traffic – der Hintergrundscan der Verkehrswarnung fragt keine Texte ab.</summary>
    public static readonly IReadOnlyList<string> Text = ["ATC ID", "ATC AIRLINE", "ATC FLIGHT NUMBER", "ATC MODEL"];

    public const int NumericFieldBytes = 8;

    public const int TextFieldBytes = 64;

    public static int NumericBytes => Numeric.Count * NumericFieldBytes;

    public static int PayloadBytes(bool withText) => NumericBytes + (withText ? Text.Count * TextFieldBytes : 0);
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

    /// <summary>Jeder Frame – nur abonniert, solange Duopilo KI-Flugzeuge selbst bewegt.</summary>
    Frame = 4,
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
