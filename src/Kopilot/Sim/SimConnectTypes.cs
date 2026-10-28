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
