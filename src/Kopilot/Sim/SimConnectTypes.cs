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

/// <summary>Container für String-SimVars (SIMCONNECT_DATATYPE.STRING256).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct SimString256
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Value;
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
