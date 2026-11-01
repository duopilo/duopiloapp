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

    public class SIMCONNECT_RECV_EVENT_FRAME : SIMCONNECT_RECV_EVENT
    {
        public float fFrameRate;
        public float fSimSpeed;
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

    public enum SIMCONNECT_FACILITY_DATA_TYPE
    {
        AIRPORT,
        RUNWAY,
        START,
        FREQUENCY,
        HELIPAD,
        APPROACH,
        APPROACH_TRANSITION,
        APPROACH_LEG,
        FINAL_APPROACH_LEG,
        MISSED_APPROACH_LEG,
        DEPARTURE,
        ARRIVAL,
        RUNWAY_TRANSITION,
        ENROUTE_TRANSITION,
        TAXI_POINT,
        TAXI_PARKING,
        TAXI_PATH,
        TAXI_NAME,
        JETWAY,
        VOR,
        NDB,
        WAYPOINT,
        ROUTE,
        PAVEMENT,
        APPROACH_LIGHTS,
        VASI,
    }

    public class SIMCONNECT_RECV_FACILITY_DATA : SIMCONNECT_RECV
    {
        public uint UserRequestId;
        public uint UniqueRequestId;
        public uint ParentUniqueRequestId;
        public uint Type;
        public uint IsListItem;
        public uint ItemIndex;
        public uint ListSize;
        public object[] Data;
    }

    public class SIMCONNECT_RECV_FACILITY_DATA_END : SIMCONNECT_RECV
    {
        public uint RequestId;
    }

    public enum SIMCONNECT_INPUT_EVENT_TYPE : uint
    {
        DOUBLE,
        STRING,
    }

    public class SIMCONNECT_RECV_LIST_TEMPLATE : SIMCONNECT_RECV
    {
        public uint dwRequestID;
        public uint dwArraySize;
        public uint dwEntryNumber;
        public uint dwOutOf;
    }

    public struct SIMCONNECT_INPUT_EVENT_DESCRIPTOR
    {
        public string Name;
        public ulong Hash;
        public SIMCONNECT_INPUT_EVENT_TYPE eType;
    }

    public class SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS : SIMCONNECT_RECV_LIST_TEMPLATE
    {
        public object[] rgData;
    }

    public class SIMCONNECT_RECV_GET_INPUT_EVENT : SIMCONNECT_RECV
    {
        public uint dwRequestID;
        public SIMCONNECT_INPUT_EVENT_TYPE eType;
        public object[] Value;
    }

    public class SIMCONNECT_RECV_ENUMERATE_INPUT_EVENT_PARAMS : SIMCONNECT_RECV
    {
        public ulong Hash;
        public string Value;
    }

    public struct SIMCONNECT_DATA_INITPOSITION
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
        public uint OnGround;
        public uint Airspeed;
    }

    public struct SIMCONNECT_DATA_WAYPOINT
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public uint Flags;
        public double ktsSpeed;
        public double percentThrottle;
    }

    public class SIMCONNECT_RECV_ASSIGNED_OBJECT_ID : SIMCONNECT_RECV
    {
        public uint dwRequestID;
        public uint dwObjectID;
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

        public delegate void RecvEventFrameEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT_FRAME data);

        public delegate void RecvSystemStateEventHandler(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data);

        public delegate void RecvFacilityDataEventHandler(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data);

        public delegate void RecvFacilityDataEndEventHandler(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data);

        public delegate void RecvEnumerateInputEventsEventHandler(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS data);

        public delegate void RecvGetInputEventEventHandler(SimConnect sender, SIMCONNECT_RECV_GET_INPUT_EVENT data);

        public delegate void RecvEnumerateInputEventParamsEventHandler(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENT_PARAMS data);

        public delegate void RecvAssignedObjectIdEventHandler(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data);

        public SimConnect(string szName, IntPtr hWnd, uint UserEventWin32, WaitHandle hEventHandle, uint ConfigIndex)
        {
            throw new NotSupportedException(
                "Duopilo wurde gegen den SimConnect-Stub gebaut und kann sich nicht mit MSFS verbinden. " +
                "Bitte das MSFS-2024-SDK installieren oder die SimConnect-DLLs nach lib\\ kopieren und neu bauen (siehe README).");
        }

        public event RecvOpenEventHandler OnRecvOpen;

        public event RecvQuitEventHandler OnRecvQuit;

        public event RecvExceptionEventHandler OnRecvException;

        public event RecvSimobjectDataEventHandler OnRecvSimobjectData;

        public event RecvSimobjectDataBytypeEventHandler OnRecvSimobjectDataBytype;

        public event RecvEventEventHandler OnRecvEvent;

        public event RecvEventFilenameEventHandler OnRecvEventFilename;

        public event RecvEventFrameEventHandler OnRecvEventFrame;

        public event RecvSystemStateEventHandler OnRecvSystemState;

        public event RecvFacilityDataEventHandler OnRecvFacilityData;

        public event RecvFacilityDataEndEventHandler OnRecvFacilityDataEnd;

        public event RecvEnumerateInputEventsEventHandler OnRecvEnumerateInputEvents;

        public event RecvGetInputEventEventHandler OnRecvGetInputEvent;

        public event RecvEnumerateInputEventParamsEventHandler OnRecvEnumerateInputEventParams;

        public event RecvAssignedObjectIdEventHandler OnRecvAssignedObjectId;

        public void AICreateNonATCAircraft(string szContainerTitle, string szTailNumber, SIMCONNECT_DATA_INITPOSITION InitPos, Enum RequestID)
        {
        }

        public void AICreateNonATCAircraft_EX1(string szContainerTitle, string szLivery, string szTailNumber, SIMCONNECT_DATA_INITPOSITION InitPos, Enum RequestID)
        {
        }

        public void AIRemoveObject(uint ObjectID, Enum RequestID)
        {
        }

        public void AIReleaseControl(uint ObjectID, Enum RequestID)
        {
        }

        public void RegisterStruct<RECV, T>(Enum dwID)
            where RECV : SIMCONNECT_RECV
        {
        }

        public void EnumerateInputEvents(Enum RequestID)
        {
        }

        public void GetInputEvent(Enum RequestID, ulong Hash)
        {
        }

        public void SetInputEvent(ulong Hash, object Value)
        {
        }

        public void EnumerateInputEventParams(ulong Hash)
        {
        }

        public void SubscribeToSystemEvent(Enum EventID, string SystemEventName)
        {
        }

        public void UnsubscribeFromSystemEvent(Enum EventID)
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

        public void TransmitClientEvent_EX1(uint ObjectID, Enum EventID, Enum GroupID, SIMCONNECT_EVENT_FLAG Flags, uint dwData0, uint dwData1, uint dwData2, uint dwData3, uint dwData4)
        {
        }

        public void SetDataOnSimObject(Enum DefineID, uint ObjectID, SIMCONNECT_DATA_SET_FLAG Flags, object pDataSet)
        {
        }

        public void AddToFacilityDefinition(Enum DefineID, string FieldName)
        {
        }

        public void RegisterFacilityDataDefineStruct<T>(SIMCONNECT_FACILITY_DATA_TYPE dwType)
        {
        }

        public void RequestFacilityData(Enum DefineID, Enum RequestID, string ICAO, string Region)
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
