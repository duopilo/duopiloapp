using System.Runtime.InteropServices;

namespace Kopilot.Sim;

/// <summary>
/// Direkter Zugriff auf die native SimConnect.dll (MSFS-SDK, x64) – nur für die Verkehrsabfrage, siehe
/// <see cref="TrafficConnection"/>. Signaturen nach SimConnect.h; Rückgabe ist ein HRESULT (negativ = Fehler).
/// </summary>
internal static class NativeSimConnect
{
    private const string Dll = "SimConnect.dll";

    /// <summary>SIMCONNECT_UNUSED</summary>
    public const uint Unused = 0xFFFFFFFF;

    /// <summary>SIMCONNECT_DATATYPE_FLOAT64</summary>
    public const int DataTypeFloat64 = 4;

    /// <summary>SIMCONNECT_DATATYPE_STRING64</summary>
    public const int DataTypeString64 = 7;

    /// <summary>SIMCONNECT_SIMOBJECT_TYPE_AIRCRAFT</summary>
    public const int ObjectTypeAircraft = 2;

    /// <summary>SIMCONNECT_SIMOBJECT_TYPE_HELICOPTER</summary>
    public const int ObjectTypeHelicopter = 3;

    [DllImport(Dll, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern int SimConnect_Open(out IntPtr phSimConnect, string szName, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_Close(IntPtr hSimConnect);

    [DllImport(Dll, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern int SimConnect_AddToDataDefinition(
        IntPtr hSimConnect,
        uint defineId,
        string datumName,
        string? unitsName,
        int datumType,
        float epsilon,
        uint datumId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_RequestDataOnSimObjectType(IntPtr hSimConnect, uint requestId, uint defineId, uint radiusMeters, int type);

    /// <summary>Nächste Nachricht: Zeiger und Größe gelten nur bis zum nächsten Aufruf. Fehler (E_FAIL), wenn keine da ist.</summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetNextDispatch(IntPtr hSimConnect, out IntPtr ppData, out uint pcbData);
}
