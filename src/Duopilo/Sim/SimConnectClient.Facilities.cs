using System.Runtime.InteropServices;
using Duopilo.Atc;
using Duopilo.Localization;
using Microsoft.FlightSimulator.SimConnect;

namespace Duopilo.Sim;

/// <summary>
/// Flughafen-Frequenzen, Bahnen und Rollwege über die Facilities-API von SimConnect (AIRPORT → FREQUENCY, AIRPORT → RUNWAY bzw.
/// AIRPORT → TAXI_POINT/TAXI_PARKING/TAXI_PATH/TAXI_NAME). Anfragen laufen nacheinander: SimConnect schickt je Eintrag eine
/// FACILITY_DATA-Nachricht und zum Schluss FACILITY_DATA_END. Alle Definitionen haben denselben AIRPORT-Teil (Bezugspunkt und
/// N_FREQUENCIES), weil die Struktur je Datentyp nur einmal registriert wird.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint FacilityDefinitionId = 950_000;
    private const uint FacilityRunwayDefinitionId = 950_001;
    private const uint FacilityTaxiDefinitionId = 950_002;
    private const uint FacilityRequestBase = 2_000_000;
    private static readonly TimeSpan FacilityTimeout = TimeSpan.FromSeconds(10);

    // Nur auf dem SimConnect-Thread benutzt
    private readonly Queue<FacilityRequest> _facilityQueue = new();
    private FacilityRequest? _facilityActive;
    private bool _facilityDefined;
    private bool _facilityRunwaysDefined;
    private bool _facilityTaxiDefined;
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

    /// <summary>
    /// Rollwege eines Flughafens aus der MSFS-Datenbank: Bezugspunkt, Rollpunkte (mit Haltepunkten), Parkpositionen, Rollweg-Abschnitte
    /// und deren Namen. Große Flughäfen haben tausende Einträge – die Anfrage läuft, solange Daten kommen.
    /// </summary>
    public async Task<AirportTaxiData> RequestAirportTaxiwaysAsync(string icao, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var request = new FacilityRequest(icao.Trim().ToUpperInvariant(), FacilityKind.Taxiways);
        Enqueue(sc =>
        {
            _facilityQueue.Enqueue(request);
            StartNextFacilityRequest(sc);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FacilityTimeout * 6);
        await request.Completion.Task.WaitAsync(timeout.Token);
        return new AirportTaxiData(
            request.Icao,
            request.Latitude,
            request.Longitude,
            request.AltitudeM,
            [.. request.TaxiPoints.OrderBy(p => p.Index).Select(p => p.Item)],
            [.. request.TaxiParkings.OrderBy(p => p.Index).Select(p => p.Item)],
            [.. request.TaxiPaths.OrderBy(p => p.Index).Select(p => p.Item)],
            [.. request.TaxiNames.OrderBy(p => p.Index).Select(p => p.Item)]);
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
                AddAirportPart(sc, def);
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
                AddAirportPart(sc, def);
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

            if (next.Kind == FacilityKind.Taxiways && !_facilityTaxiDefined)
            {
                var def = (SimDefinitionId)FacilityTaxiDefinitionId;
                AddAirportPart(sc, def);
                sc.AddToFacilityDefinition(def, "OPEN TAXI_POINT");
                sc.AddToFacilityDefinition(def, "TYPE");
                sc.AddToFacilityDefinition(def, "ORIENTATION");
                sc.AddToFacilityDefinition(def, "BIAS_X");
                sc.AddToFacilityDefinition(def, "BIAS_Z");
                sc.AddToFacilityDefinition(def, "CLOSE TAXI_POINT");
                sc.AddToFacilityDefinition(def, "OPEN TAXI_PARKING");
                sc.AddToFacilityDefinition(def, "TYPE");
                sc.AddToFacilityDefinition(def, "TAXI_POINT_TYPE");
                sc.AddToFacilityDefinition(def, "NAME");
                sc.AddToFacilityDefinition(def, "SUFFIX");
                sc.AddToFacilityDefinition(def, "NUMBER");
                sc.AddToFacilityDefinition(def, "ORIENTATION");
                sc.AddToFacilityDefinition(def, "HEADING");
                sc.AddToFacilityDefinition(def, "RADIUS");
                sc.AddToFacilityDefinition(def, "BIAS_X");
                sc.AddToFacilityDefinition(def, "BIAS_Z");
                sc.AddToFacilityDefinition(def, "CLOSE TAXI_PARKING");
                sc.AddToFacilityDefinition(def, "OPEN TAXI_PATH");
                sc.AddToFacilityDefinition(def, "TYPE");
                sc.AddToFacilityDefinition(def, "WIDTH");
                sc.AddToFacilityDefinition(def, "RUNWAY_NUMBER");
                sc.AddToFacilityDefinition(def, "RUNWAY_DESIGNATOR");
                sc.AddToFacilityDefinition(def, "START");
                sc.AddToFacilityDefinition(def, "END");
                sc.AddToFacilityDefinition(def, "NAME_INDEX");
                sc.AddToFacilityDefinition(def, "CLOSE TAXI_PATH");
                sc.AddToFacilityDefinition(def, "OPEN TAXI_NAME");
                sc.AddToFacilityDefinition(def, "NAME");
                sc.AddToFacilityDefinition(def, "CLOSE TAXI_NAME");
                sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");
                sc.RegisterFacilityDataDefineStruct<FacilityTaxiPointData>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_POINT);
                sc.RegisterFacilityDataDefineStruct<FacilityTaxiParkingData>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING);
                sc.RegisterFacilityDataDefineStruct<FacilityTaxiPathData>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PATH);
                sc.RegisterFacilityDataDefineStruct<FacilityTaxiNameData>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_NAME);
                _facilityTaxiDefined = true;
            }

            next.StartedUtc = DateTimeOffset.UtcNow;
            _facilityActive = next;
            var definition = next.Kind switch
            {
                FacilityKind.Runways => FacilityRunwayDefinitionId,
                FacilityKind.Taxiways => FacilityTaxiDefinitionId,
                _ => FacilityDefinitionId,
            };
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

        // Große Flughäfen liefern tausende Einträge: Solange Daten kommen, läuft die Anfrage nicht ab
        active.StartedUtc = DateTimeOffset.UtcNow;
        var index = (int)data.ItemIndex;
        if (data.Data[0] is FacilityAirportData airport)
        {
            active.Latitude = airport.Latitude;
            active.Longitude = airport.Longitude;
            active.AltitudeM = airport.Altitude;
        }
        else if (data.Data[0] is FacilityTaxiPointData point)
        {
            active.TaxiPoints.Add((index, new TaxiPointData(point.Type, point.Orientation, point.BiasX, point.BiasZ)));
        }
        else if (data.Data[0] is FacilityTaxiParkingData parking)
        {
            active.TaxiParkings.Add((index, new TaxiParkingData(
                parking.Type, parking.TaxiPointType, parking.Name, parking.Suffix, parking.Number, parking.Orientation, parking.Heading, parking.Radius, parking.BiasX, parking.BiasZ)));
        }
        else if (data.Data[0] is FacilityTaxiPathData path)
        {
            active.TaxiPaths.Add((index, new TaxiPathData(path.Type, path.Width, path.RunwayNumber, path.RunwayDesignator, path.Start, path.End, path.NameIndex)));
        }
        else if (data.Data[0] is FacilityTaxiNameData taxiName)
        {
            active.TaxiNames.Add((index, taxiName.Name?.TrimEnd('\0', ' ') ?? ""));
        }
        else if (data.Data[0] is FacilityFrequencyData f && f.FrequencyHz > 0)
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
            else if (active.Kind == FacilityKind.Taxiways)
            {
                _log.LogInformation(Loc.Log("log.sim.facilityTaxiways"), active.TaxiPoints.Count, active.TaxiParkings.Count, active.TaxiPaths.Count, active.Icao);
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
        _facilityTaxiDefined = false;
        _facilityActive?.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        _facilityActive = null;
        while (_facilityQueue.TryDequeue(out var queued))
        {
            queued.Completion.TrySetException(new InvalidOperationException(Loc.T("sim.disconnected")));
        }
    }

    /// <summary>AIRPORT-Teil, gleich für alle Definitionen (die Struktur gibt es je Datentyp nur einmal): Bezugspunkt und N_FREQUENCIES.</summary>
    private static void AddAirportPart(SimConnect sc, SimDefinitionId def)
    {
        sc.AddToFacilityDefinition(def, "OPEN AIRPORT");
        sc.AddToFacilityDefinition(def, "LATITUDE");
        sc.AddToFacilityDefinition(def, "LONGITUDE");
        sc.AddToFacilityDefinition(def, "ALTITUDE");
        sc.AddToFacilityDefinition(def, "N_FREQUENCIES");
    }

    private enum FacilityKind
    {
        Frequencies,
        Runways,
        Taxiways,
    }

    private sealed class FacilityRequest(string icao, FacilityKind kind)
    {
        public string Icao { get; } = icao;

        public FacilityKind Kind { get; } = kind;

        public DateTimeOffset StartedUtc { get; set; }

        public List<AirportFrequency> Frequencies { get; } = [];

        public List<AirportRunway> Runways { get; } = [];

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double AltitudeM { get; set; }

        public List<(int Index, TaxiPointData Item)> TaxiPoints { get; } = [];

        public List<(int Index, TaxiParkingData Item)> TaxiParkings { get; } = [];

        public List<(int Index, TaxiPathData Item)> TaxiPaths { get; } = [];

        public List<(int Index, string Item)> TaxiNames { get; } = [];

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>AIRPORT-Teil der Facility-Definition: LATITUDE, LONGITUDE, ALTITUDE (FLOAT64, Grad und Meter), N_FREQUENCIES (INT32).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityAirportData
{
    public double Latitude;

    public double Longitude;

    public double Altitude;

    public int FrequencyCount;
}

/// <summary>TAXI_POINT: TYPE, ORIENTATION (INT32), BIAS_X, BIAS_Z (FLOAT32, Meter vom Bezugspunkt des Flughafens).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityTaxiPointData
{
    public int Type;

    public int Orientation;

    public float BiasX;

    public float BiasZ;
}

/// <summary>
/// TAXI_PARKING: TYPE, TAXI_POINT_TYPE, NAME, SUFFIX (INT32), NUMBER (UINT32), ORIENTATION (INT32), HEADING, RADIUS, BIAS_X, BIAS_Z
/// (FLOAT32) – Reihenfolge wie in der Definition.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityTaxiParkingData
{
    public int Type;

    public int TaxiPointType;

    public int Name;

    public int Suffix;

    public uint Number;

    public int Orientation;

    public float Heading;

    public float Radius;

    public float BiasX;

    public float BiasZ;
}

/// <summary>TAXI_PATH: TYPE (INT32), WIDTH (FLOAT32), RUNWAY_NUMBER, RUNWAY_DESIGNATOR, START, END (INT32), NAME_INDEX (UINT32).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FacilityTaxiPathData
{
    public int Type;

    public float Width;

    public int RunwayNumber;

    public int RunwayDesignator;

    public int Start;

    public int End;

    public uint NameIndex;
}

/// <summary>TAXI_NAME: NAME (STRING32).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct FacilityTaxiNameData
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string Name;
}

/// <summary>Rollwege eines Flughafens aus der MSFS-Datenbank (<see cref="SimConnectClient.RequestAirportTaxiwaysAsync"/>).</summary>
/// <param name="Latitude">Bezugspunkt des Flughafens; BIAS_X/BIAS_Z der Punkte sind Meter davon.</param>
/// <param name="AltitudeM">Höhe des Bezugspunkts in Metern.</param>
public sealed record AirportTaxiData(
    string Icao,
    double Latitude,
    double Longitude,
    double AltitudeM,
    IReadOnlyList<TaxiPointData> Points,
    IReadOnlyList<TaxiParkingData> Parkings,
    IReadOnlyList<TaxiPathData> Paths,
    IReadOnlyList<string> Names);

/// <summary>Rollpunkt: TYPE 1 normal, 2 Haltepunkt, 4 ILS-Haltepunkt, 5/6 Haltepunkt ohne Markierung; Lage in Metern vom Bezugspunkt.</summary>
public sealed record TaxiPointData(int Type, int Orientation, double BiasX, double BiasZ);

/// <summary>Parkposition: TYPE (1–4 GA-Vorfeld, 5 Fracht, 6/7 Militär, 8–10 Gate klein/mittel/schwer, 11 Dock, 12 Tanken, 13 Fahrzeuge, 14 GA extra, 15 Gate extra), NAME/SUFFIX (10 GATE, 12–37 GATE_A–GATE_Z …), Nummer, Kurs, Radius (m).</summary>
public sealed record TaxiParkingData(int Type, int TaxiPointType, int Name, int Suffix, uint Number, int Orientation, double HeadingDeg, double RadiusM, double BiasX, double BiasZ);

/// <summary>Rollweg-Abschnitt: TYPE (1 Rollweg, 2 Bahn, 3 Parkposition – END ist dann der Index der Parkposition –, 4 Pfad, 5 gesperrt, 6 Fahrzeuge, 7 Straße, 8 Markierung), Breite, Bahn, Anfang und Ende (Index der Rollpunkte), Name.</summary>
public sealed record TaxiPathData(int Type, double WidthM, int RunwayNumber, int RunwayDesignator, int Start, int End, uint NameIndex);

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
