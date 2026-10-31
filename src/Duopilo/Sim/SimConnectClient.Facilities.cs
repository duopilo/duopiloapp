using System.Runtime.InteropServices;
using Duopilo.Atc;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>
/// Flughafen-Frequenzen und Bahnen über die Facilities-API von SimConnect (AIRPORT → FREQUENCY bzw. AIRPORT → RUNWAY).
/// Anfragen laufen nacheinander: SimConnect schickt je Eintrag eine FACILITY_DATA-Nachricht und zum Schluss FACILITY_DATA_END.
/// Beide Definitionen haben denselben AIRPORT-Teil (N_FREQUENCIES), weil die Struktur je Datentyp nur einmal registriert wird.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint FacilityDefinitionId = 950_000;
    private const uint FacilityRunwayDefinitionId = 950_001;
    private const uint FacilityRequestBase = 2_000_000;
    private static readonly TimeSpan FacilityTimeout = TimeSpan.FromSeconds(10);

    // Nur auf dem SimConnect-Thread benutzt
    private readonly Queue<FacilityRequest> _facilityQueue = new();
    private FacilityRequest? _facilityActive;
    private bool _facilityDefined;
    private bool _facilityRunwaysDefined;
    private uint _nextFacilityRequest;

    /// <summary>Alle Frequenzen eines Flughafens aus der MSFS-Datenbank (Ground, Tower, Clearance, Approach …).</summary>
    public async Task<IReadOnlyList<AirportFrequency>> RequestAirportFrequenciesAsync(string icao, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var request = new FacilityRequest(icao.Trim().ToUpperInvariant(), FacilityKind.Frequencies);
        Enqueue(sc =>
        {
            _facilityQueue.Enqueue(request);
            StartNextFacilityRequest(sc);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FacilityTimeout * 3);
        await request.Completion.Task.WaitAsync(timeout.Token);
        return request.Frequencies;
    }

    /// <summary>Alle Bahnen eines Flughafens aus der MSFS-Datenbank (Mitte, Kurs, Länge, Bezeichnung beider Enden).</summary>
    public async Task<IReadOnlyList<AirportRunway>> RequestAirportRunwaysAsync(string icao, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var request = new FacilityRequest(icao.Trim().ToUpperInvariant(), FacilityKind.Runways);
        Enqueue(sc =>
        {
            _facilityQueue.Enqueue(request);
            StartNextFacilityRequest(sc);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FacilityTimeout * 3);
        await request.Completion.Task.WaitAsync(timeout.Token);
        return request.Runways;
    }

    private void StartNextFacilityRequest(SimConnect sc)
    {
        if (_facilityActive is not null || !_facilityQueue.TryDequeue(out var next))
        {
            return;
        }

        try
        {
            if (!_facilityDefined)
            {
                // AIRPORT-Struktur einmal für beide Definitionen; RUNWAY gleich mit, damit sie vor der ersten Antwort steht
                var def = (SimDefinitionId)FacilityDefinitionId;
                sc.AddToFacilityDefinition(def, "OPEN AIRPORT");
                sc.AddToFacilityDefinition(def, "N_FREQUENCIES");
                sc.AddToFacilityDefinition(def, "OPEN FREQUENCY");
                sc.AddToFacilityDefinition(def, "TYPE");
                sc.AddToFacilityDefinition(def, "FREQUENCY");
                sc.AddToFacilityDefinition(def, "NAME");
                sc.AddToFacilityDefinition(def, "CLOSE FREQUENCY");
                sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");
                sc.RegisterFacilityDataDefineStruct<FacilityAirportData>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
                sc.RegisterFacilityDataDefineStruct<FacilityFrequencyData>(SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY);
                _facilityDefined = true;
            }

            if (next.Kind == FacilityKind.Runways && !_facilityRunwaysDefined)
            {
                var def = (SimDefinitionId)FacilityRunwayDefinitionId;
                sc.AddToFacilityDefinition(def, "OPEN AIRPORT");
                sc.AddToFacilityDefinition(def, "N_FREQUENCIES");
                sc.AddToFacilityDefinition(def, "OPEN RUNWAY");
                sc.AddToFacilityDefinition(def, "LATITUDE");
                sc.AddToFacilityDefinition(def, "LONGITUDE");
                sc.AddToFacilityDefinition(def, "ALTITUDE");
                sc.AddToFacilityDefinition(def, "HEADING");
                sc.AddToFacilityDefinition(def, "LENGTH");
                sc.AddToFacilityDefinition(def, "WIDTH");
                sc.AddToFacilityDefinition(def, "PRIMARY_NUMBER");
                sc.AddToFacilityDefinition(def, "PRIMARY_DESIGNATOR");
                sc.AddToFacilityDefinition(def, "SECONDARY_NUMBER");
                sc.AddToFacilityDefinition(def, "SECONDARY_DESIGNATOR");
                sc.AddToFacilityDefinition(def, "CLOSE RUNWAY");
                sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");
                sc.RegisterFacilityDataDefineStruct<FacilityRunwayData>(SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
                _facilityRunwaysDefined = true;
            }

            next.StartedUtc = DateTimeOffset.UtcNow;
            _facilityActive = next;
            var definition = next.Kind == FacilityKind.Runways ? FacilityRunwayDefinitionId : FacilityDefinitionId;
            sc.RequestFacilityData((SimDefinitionId)definition, (SimRequestId)(FacilityRequestBase + ++_nextFacilityRequest), next.Icao, "");
        }
        catch (Exception ex) when (ex is not COMException)
        {
            _facilityActive = null;
            next.Completion.TrySetException(ex);
            StartNextFacilityRequest(sc);
        }
    }

    private void OnFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (_facilityActive is not { } active || data.Data is not { Length: > 0 })
        {
            return;
        }

        if (data.Data[0] is FacilityFrequencyData f && f.FrequencyHz > 0)
        {
            var name = f.Name?.TrimEnd('\0', ' ');
            active.Frequencies.Add(new AirportFrequency(f.Type, f.FrequencyHz / 1_000_000.0, string.IsNullOrWhiteSpace(name) ? null : name));
        }
        else if (data.Data[0] is FacilityRunwayData r && r.Length > 0)
        {
            active.Runways.Add(new AirportRunway(
                r.Latitude,
                r.Longitude,
                r.Altitude,
                r.Heading,
                r.Length,
                r.Width,
                AirportRunway.Designation(r.PrimaryNumber, r.PrimaryDesignator),
                AirportRunway.Designation(r.SecondaryNumber, r.SecondaryDesignator)));
        }
    }

    private void OnFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (_facilityActive is { } active)
        {
            _facilityActive = null;
            if (active.Kind == FacilityKind.Runways)
            {
                _log.LogInformation(Loc.Log("log.sim.facilityRunways"), active.Runways.Count, active.Icao);
            }
            else
            {
                _log.LogInformation(Loc.Log("log.sim.facilityFrequencies"), active.Frequencies.Count, active.Icao);
            }

            active.Completion.TrySetResult();
        }

        StartNextFacilityRequest(sender);
    }

    /// <summary>Kommt keine Antwort (z. B. unbekannter ICAO-Code), nach einer Weile aufgeben und die nächste Anfrage starten.</summary>
    private void ExpireFacilityRequest()
    {
        if (_facilityActive is { } active && DateTimeOffset.UtcNow - active.StartedUtc > FacilityTimeout)
        {
            _facilityActive = null;
            active.Completion.TrySetException(new TimeoutException(Loc.T("sim.facilities.timeout", active.Icao)));
            if (_sc is { } sc)
            {
                StartNextFacilityRequest(sc);
            }
        }
    }

    private void ResetFacilities()
    {
        _facilityDefined = false;
        _facilityRunwaysDefined = false;
        _facilityActive?.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        _facilityActive = null;
        while (_facilityQueue.TryDequeue(out var queued))
        {
            queued.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        }
    }

    private enum FacilityKind
    {
        Frequencies,
        Runways,
    }

    private sealed class FacilityRequest(string icao, FacilityKind kind)
    {
        public string Icao { get; } = icao;

        public FacilityKind Kind { get; } = kind;

        public DateTimeOffset StartedUtc { get; set; }

        public List<AirportFrequency> Frequencies { get; } = [];

        public List<AirportRunway> Runways { get; } = [];

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>AIRPORT-Teil der Facility-Definition (nur die Anzahl, damit der Datensatz nicht leer ist).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityAirportData
{
    public int FrequencyCount;
}

/// <summary>FREQUENCY-Teil: TYPE (INT32), FREQUENCY (INT32, Hz), NAME (CHAR[64]) – Reihenfolge wie in der Definition.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityFrequencyData
{
    public int Type;

    public int FrequencyHz;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string Name;
}

/// <summary>
/// RUNWAY-Teil: LATITUDE, LONGITUDE, ALTITUDE (FLOAT64, Grad und Meter), HEADING, LENGTH, WIDTH (FLOAT32, Grad und Meter),
/// PRIMARY_NUMBER, PRIMARY_DESIGNATOR, SECONDARY_NUMBER, SECONDARY_DESIGNATOR (INT32) – Reihenfolge wie in der Definition.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityRunwayData
{
    public double Latitude;

    public double Longitude;

    public double Altitude;

    public float Heading;

    public float Length;

    public float Width;

    public int PrimaryNumber;

    public int PrimaryDesignator;

    public int SecondaryNumber;

    public int SecondaryDesignator;
}

/// <summary>Bahn aus der MSFS-Datenbank. Kurs in Richtung des primären Endes (von dessen Schwelle zum anderen Ende).</summary>
/// <param name="AltitudeM">Höhe der Bahnmitte in Metern.</param>
/// <param name="HeadingDeg">Kurs des primären Endes laut MSFS (rechtweisend).</param>
/// <param name="Primary">Bezeichnung des primären Endes, z. B. "23L" (leer, wenn MSFS keine Nummer hat).</param>
public sealed record AirportRunway(
    double Latitude,
    double Longitude,
    double AltitudeM,
    double HeadingDeg,
    double LengthM,
    double WidthM,
    string Primary,
    string Secondary)
{
    /// <summary>Bezeichnung aus Nummer (1–36, 37–44 = N…NW) und Zusatz (1 L, 2 R, 3 C, 4 W, 5 A, 6 B), z. B. 5 + 2 → "05R".</summary>
    public static string Designation(int number, int designator)
    {
        var num = number switch
        {
            >= 1 and <= 36 => number.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            37 => "N",
            38 => "NE",
            39 => "E",
            40 => "SE",
            41 => "S",
            42 => "SW",
            43 => "W",
            44 => "NW",
            _ => "",
        };
        var suffix = designator switch
        {
            1 => "L",
            2 => "R",
            3 => "C",
            4 => "W",
            5 => "A",
            6 => "B",
            _ => "",
        };
        return num.Length == 0 ? "" : num + suffix;
    }

    /// <summary>Vergleicht Bahnbezeichnungen ohne führende Null und Groß-/Kleinschreibung ("5L" = "05L", "rwy 23r" = "23R").</summary>
    public static string Normalize(string? designation)
    {
        var text = (designation ?? "").Trim().ToUpperInvariant();
        if (text.StartsWith("RWY", StringComparison.Ordinal))
        {
            text = text[3..].Trim();
        }

        return text.TrimStart('0');
    }
}
