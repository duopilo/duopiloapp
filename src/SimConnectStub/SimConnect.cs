// Stub der verwalteten SimConnect-API (nur Signaturen, keine Funktion).
// Namen und Signaturen folgen Microsoft.FlightSimulator.SimConnect aus dem MSFS-SDK.

using System;
using System.Threading;

namespace Microsoft.FlightSimulator.SimConnect
{
    public enum SIMCONNECT_DATATYPE
    {
        INVALID,
        INT32,
        INT64,
        FLOAT32,
        FLOAT64,
        STRING8,
        STRING32,
        STRING64,
        STRING128,
        STRING256,
        STRING260,
        STRINGV,
        INITPOSITION,
        MARKERSTATE,
        WAYPOINT,
        LATLONALT,
        XYZ,
        MAX,
    }

    public enum SIMCONNECT_PERIOD
    {
        NEVER,
        ONCE,
        VISUAL_FRAME,
        SIM_FRAME,
        SECOND,
    }

    public enum SIMCONNECT_SIMOBJECT_TYPE
    {
        USER,
        ALL,
        AIRCRAFT,
        HELICOPTER,
        BOAT,
        GROUND,
    }

    [Flags]
    public enum SIMCONNECT_DATA_REQUEST_FLAG
    {
        DEFAULT = 0,
        CHANGED = 1,
        TAGGED = 2,
    }

    [Flags]
    public enum SIMCONNECT_EVENT_FLAG
    {
        DEFAULT = 0,
        FAST_REPEAT_TIMER = 1,
        SLOW_REPEAT_TIMER = 2,
        GROUPID_IS_PRIORITY = 16,
    }

    [Flags]
    public enum SIMCONNECT_DATA_SET_FLAG
    {
        DEFAULT = 0,
        TAGGED = 1,
    }

    public enum SIMCONNECT_EXCEPTION
    {
        NONE,
        ERROR,
        SIZE_MISMATCH,
        UNRECOGNIZED_ID,
        UNOPENED,
        VERSION_MISMATCH,
        TOO_MANY_GROUPS,
        NAME_UNRECOGNIZED,
        TOO_MANY_EVENT_NAMES,
        EVENT_ID_DUPLICATE,
        TOO_MANY_MAPS,
        TOO_MANY_OBJECTS,
        TOO_MANY_REQUESTS,
    }

    public class SIMCONNECT_RECV
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
    }

    public class SIMCONNECT_RECV_OPEN : SIMCONNECT_RECV
    {
        public string szApplicationName;
        public uint dwApplicationVersionMajor;
        public uint dwApplicationVersionMinor;
        public uint dwApplicationBuildMajor;
        public uint dwApplicationBuildMinor;
        public uint dwSimConnectVersionMajor;
        public uint dwSimConnectVersionMinor;
        public uint dwSimConnectBuildMajor;
        public uint dwSimConnectBuildMinor;
    }

    public class SIMCONNECT_RECV_EXCEPTION : SIMCONNECT_RECV
    {
        public uint dwException;
        public uint dwSendID;
        public uint dwIndex;
    }

    public class SIMCONNECT_RECV_SIMOBJECT_DATA : SIMCONNECT_RECV
    {
        public uint dwRequestID;
        public uint dwObjectID;
        public uint dwDefineID;
        public uint dwFlags;
        public uint dwentrynumber;
        public uint dwoutof;
        public uint dwDefineCount;
        public object[] dwData;
    }

    public class SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE : SIMCONNECT_RECV_SIMOBJECT_DATA
    {
    }

    public class SIMCONNECT_RECV_EVENT : SIMCONNECT_RECV
    {
        public uint uGroupID;
        public uint uEventID;
        public uint dwData;
    }

    public class SIMCONNECT_RECV_EVENT_FILENAME : SIMCONNECT_RECV_EVENT
    {
        public string szFileName;
        public uint dwFlags;
    }

    public class SIMCONNECT_RECV_SYSTEM_STATE : SIMCONNECT_RECV
    {
        public uint dwRequestID;
        public uint dwInteger;
        public float fFloat;
        public string szString;
    }

    public class SimConnect : IDisposable
    {
        public const uint SIMCONNECT_OBJECT_ID_USER = 0;
        public const uint SIMCONNECT_UNUSED = uint.MaxValue;
        public const uint SIMCONNECT_GROUP_PRIORITY_HIGHEST = 1;

        public delegate void RecvOpenEventHandler(SimConnect sender, SIMCONNECT_RECV_OPEN data);

        public delegate void RecvQuitEventHandler(SimConnect sender, SIMCONNECT_RECV data);

        public delegate void RecvExceptionEventHandler(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data);

        public delegate void RecvSimobjectDataEventHandler(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data);

        public delegate void RecvSimobjectDataBytypeEventHandler(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data);

        public delegate void RecvEventEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT data);

        public delegate void RecvEventFilenameEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data);

        public delegate void RecvSystemStateEventHandler(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data);

        public SimConnect(string szName, IntPtr hWnd, uint UserEventWin32, WaitHandle hEventHandle, uint ConfigIndex)
        {
            throw new NotSupportedException(
                "Kopilot wurde gegen den SimConnect-Stub gebaut und kann sich nicht mit MSFS verbinden. " +
                "Bitte das MSFS-2024-SDK installieren oder die SimConnect-DLLs nach lib\\ kopieren und neu bauen (siehe README).");
        }

        public event RecvOpenEventHandler OnRecvOpen;

        public event RecvQuitEventHandler OnRecvQuit;

        public event RecvExceptionEventHandler OnRecvException;

        public event RecvSimobjectDataEventHandler OnRecvSimobjectData;

        public event RecvSimobjectDataBytypeEventHandler OnRecvSimobjectDataBytype;

        public event RecvEventEventHandler OnRecvEvent;

        public event RecvEventFilenameEventHandler OnRecvEventFilename;

        public event RecvSystemStateEventHandler OnRecvSystemState;

        public void SubscribeToSystemEvent(Enum EventID, string SystemEventName)
        {
        }

        public void RequestSystemState(Enum RequestID, string szState)
        {
        }

        public void AddToDataDefinition(Enum DefineID, string DatumName, string UnitsName, SIMCONNECT_DATATYPE DatumType, float fEpsilon, uint DatumID)
        {
        }

        public void RegisterDataDefineStruct<T>(Enum dwID)
        {
        }

        public void RequestDataOnSimObject(Enum RequestID, Enum DefineID, uint ObjectID, SIMCONNECT_PERIOD Period, SIMCONNECT_DATA_REQUEST_FLAG Flags, uint origin, uint interval, uint limit)
        {
        }

        public void RequestDataOnSimObjectType(Enum RequestID, Enum DefineID, uint dwRadiusMeters, SIMCONNECT_SIMOBJECT_TYPE type)
        {
        }

        public void MapClientEventToSimEvent(Enum EventID, string EventName)
        {
        }

        public void TransmitClientEvent(uint ObjectID, Enum EventID, uint dwData, Enum GroupID, SIMCONNECT_EVENT_FLAG Flags)
        {
        }

        public void SetDataOnSimObject(Enum DefineID, uint ObjectID, SIMCONNECT_DATA_SET_FLAG Flags, object pDataSet)
        {
        }

        public void ReceiveMessage()
        {
        }

        public void Dispose()
        {
        }
    }
}
