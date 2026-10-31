using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Varianten der Stufe A der Testlinie (docs/research/eigener-verkehr.md).</summary>
public enum TrafficTestVariant
{
    /// <summary>Spawn 5 NM vor der Bahn mit Fahrwerk unten (wie mrlm-net/simconnect #289).</summary>
    A1,

    /// <summary>Spawn 15 NM vor der Bahn mit Fahrwerk oben, Fahrwerk bei 10 NM ausfahren.</summary>
    A2,
}

/// <summary>Landerichtung einer Bahn: Schwelle, Landekurs, Höhe, Länge.</summary>
/// <param name="Designation">Bezeichnung des Endes, auf dem gelandet wird, z. B. "23L".</param>
/// <param name="ElevationFt">Höhe der Bahn (Mitte laut MSFS) in Fuß.</param>
/// <param name="HeadingTrueDeg">Landekurs, rechtweisend.</param>
public sealed record RunwayEnd(
    string Airport,
    string Designation,
    double ThresholdLatitude,
    double ThresholdLongitude,
    double ElevationFt,
    double HeadingTrueDeg,
    double LengthM,
    double WidthM)
{
    public const double FeetPerMeter = 3.28084;

    public const double MetersPerNm = 1852.0;

    /// <summary>Das Ende mit der Bezeichnung <paramref name="designation"/> ("23L", "5L" = "05L"), sonst null.</summary>
    public static RunwayEnd? Find(string airport, IReadOnlyList<AirportRunway> runways, string designation)
    {
        var wanted = AirportRunway.Normalize(designation);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var runway in runways)
        {
            if (AirportRunway.Normalize(runway.Primary) == wanted)
            {
                return From(airport, runway, primary: true);
            }

            if (AirportRunway.Normalize(runway.Secondary) == wanted)
            {
                return From(airport, runway, primary: false);
            }
        }

        return null;
    }

    /// <summary>Landerichtung über das primäre oder sekundäre Ende. MSFS liefert die Mitte und den Kurs des primären Endes.</summary>
    public static RunwayEnd From(string airport, AirportRunway runway, bool primary)
    {
        var heading = TrafficMath.Normalize(primary ? runway.HeadingDeg : runway.HeadingDeg + 180);
        var (lat, lon) = RouteMath.Destination(runway.Latitude, runway.Longitude, TrafficMath.Normalize(heading + 180), runway.LengthM / 2 / MetersPerNm);
        return new RunwayEnd(
            airport,
            primary ? runway.Primary : runway.Secondary,
            lat,
            lon,
            runway.AltitudeM * FeetPerMeter,
            heading,
            runway.LengthM,
            runway.WidthM);
    }

    /// <summary>Punkt auf der verlängerten Mittellinie: positiv in Landerichtung hinter der Schwelle, negativ davor (Meter).</summary>
    public (double Latitude, double Longitude) Point(double alongM)
    {
        var bearing = alongM >= 0 ? HeadingTrueDeg : TrafficMath.Normalize(HeadingTrueDeg + 180);
        return RouteMath.Destination(ThresholdLatitude, ThresholdLongitude, bearing, Math.Abs(alongM) / MetersPerNm);
    }

    /// <summary>Lage zur Mittellinie in Metern: entlang ab der Schwelle (davor negativ), quer (rechts positiv).</summary>
    public (double AlongM, double CrossM) Offsets(double latitude, double longitude)
    {
        var (farLat, farLon) = Point(10_000);
        var (cross, along) = RouteMath.TrackOffsets(ThresholdLatitude, ThresholdLongitude, farLat, farLon, latitude, longitude);
        return (along * MetersPerNm, cross * MetersPerNm);
    }
}

/// <summary>Wegpunkt der Testlinie mit Name und Lage auf der Mittellinie (für Log und Auswertung).</summary>
/// <param name="AlongM">Entfernung zur Schwelle in Metern, davor negativ.</param>
public sealed record TrafficTestWaypoint(string Name, double AlongM, AiWaypoint Waypoint);

/// <summary>
/// Plan eines Testanflugs: Spawn auf der verlängerten Mittellinie, 3°-Gleitpfad bis 50 ft über der Schwelle, Aufsetzen rund 300 m
/// dahinter, Ausrollen mit fallender Geschwindigkeit bis zum Halt auf der Bahn. Ein Wegpunkt liegt 60 m hinter dem Haltepunkt, weil
/// die KI laut mrlm-net/simconnect #289 etwa 46 m vor dem letzten Rollwegpunkt stehen bleibt.
/// </summary>
public sealed record TrafficTestPlan(
    TrafficTestVariant Variant,
    RunwayEnd Runway,
    AiInitPosition Spawn,
    double SpawnNm,
    bool GearAtSpawn,
    double? GearDownNm,
    double TouchdownM,
    double StopM,
    IReadOnlyList<TrafficTestWaypoint> Waypoints,
    IReadOnlyList<string> Warnings)
{
    public const double GlideSlopeDeg = 3.0;

    public const double ThresholdCrossingFt = 50;

    public const double DefaultTouchdownM = 300;

    public const double ShortRunwayM = 1800;

    /// <summary>Bahnen kürzer als das gehen nicht: Aufsetzen plus Ausrollen bis zum Halt braucht Platz.</summary>
    public const double MinRunwayM = 1200;

    private const double ApproachKt = 140;

    private const double IntermediateKt = 160;

    private const double ThresholdKt = 135;

    private const double TouchdownKt = 130;

    private const double StopRollM = 1300;

    private const double StopMarginM = 250;

    private const double BeyondStopM = 60;

    private static readonly double[] Gates = [12, 10, 8, 6, 4, 3, 2, 1, 0.5];

    public static double DefaultSpawnNm(TrafficTestVariant variant) => variant == TrafficTestVariant.A2 ? 15 : 5;

    /// <summary>Höhe auf dem Gleitpfad <paramref name="distanceNm"/> vor der Schwelle (MSL, Fuß).</summary>
    public static double GlidePathFt(RunwayEnd runway, double distanceNm) =>
        runway.ElevationFt + ThresholdCrossingFt
        + (distanceNm * RunwayEnd.MetersPerNm * Math.Tan(GlideSlopeDeg * Math.PI / 180) * RunwayEnd.FeetPerMeter);

    public static TrafficTestPlan Build(RunwayEnd runway, TrafficTestVariant variant, double? spawnNm = null, double touchdownM = DefaultTouchdownM)
    {
        if (runway.LengthM < MinRunwayM)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Runway {runway.Designation} is {runway.LengthM:0} m long, at least {MinRunwayM:0} m are needed."), nameof(runway));
        }

        var warnings = new List<string>();
        if (runway.LengthM < ShortRunwayM)
        {
            warnings.Add("shortRunway");
        }

        var spawn = Math.Clamp(spawnNm ?? DefaultSpawnNm(variant), 2, 30);
        var gearDownNm = variant == TrafficTestVariant.A2 ? Math.Min(10, spawn - 1) : (double?)null;
        var touchdown = Math.Clamp(touchdownM, 150, runway.LengthM / 3);
        var stop = Math.Max(touchdown + 400, Math.Min(touchdown + StopRollM, runway.LengthM - StopMarginM));
        var beyond = Math.Min(stop + BeyondStopM, runway.LengthM - 100);

        var waypoints = new List<TrafficTestWaypoint>();
        var air = AiWaypointFlags.SpeedRequested | AiWaypointFlags.ComputeVerticalSpeed;
        foreach (var gate in Gates.Where(g => g < spawn - 0.5))
        {
            var (lat, lon) = runway.Point(-gate * RunwayEnd.MetersPerNm);
            var speed = gate >= 6 ? IntermediateKt : ApproachKt;
            var name = gate.ToString("0.#", CultureInfo.InvariantCulture) + "nm";
            waypoints.Add(new(name, -gate * RunwayEnd.MetersPerNm, new AiWaypoint(lat, lon, GlidePathFt(runway, gate), speed, air)));
        }

        var ground = AiWaypointFlags.SpeedRequested | AiWaypointFlags.OnGround;
        Add(waypoints, runway, "threshold", 0, runway.ElevationFt + ThresholdCrossingFt, ThresholdKt, air);
        Add(waypoints, runway, "touchdown", touchdown, runway.ElevationFt, TouchdownKt, ground);
        Add(waypoints, runway, "rollout1", touchdown + ((stop - touchdown) * 0.45), runway.ElevationFt, 60, ground);
        Add(waypoints, runway, "rollout2", touchdown + ((stop - touchdown) * 0.85), runway.ElevationFt, 25, ground);
        Add(waypoints, runway, "stop", stop, runway.ElevationFt, 10, ground);
        Add(waypoints, runway, "beyondStop", beyond, runway.ElevationFt, 5, ground);

        var (spawnLat, spawnLon) = runway.Point(-spawn * RunwayEnd.MetersPerNm);
        var initial = new AiInitPosition(
            spawnLat,
            spawnLon,
            GlidePathFt(runway, spawn),
            runway.HeadingTrueDeg,
            OnGround: false,
            AirspeedKt: (uint)(spawn >= 6 ? IntermediateKt : ApproachKt));

        return new TrafficTestPlan(variant, runway, initial, spawn, variant == TrafficTestVariant.A1, gearDownNm, touchdown, stop, waypoints, warnings);
    }

    /// <summary>Der nächste Wegpunkt vor einer Position (entlang der Mittellinie), am Ende der letzte.</summary>
    public TrafficTestWaypoint Next(double alongM) =>
        Waypoints.FirstOrDefault(w => w.AlongM > alongM + 1) ?? Waypoints[^1];

    private static void Add(List<TrafficTestWaypoint> list, RunwayEnd runway, string name, double alongM, double altitudeFt, double speedKt, AiWaypointFlags flags)
    {
        var (lat, lon) = runway.Point(alongM);
        list.Add(new TrafficTestWaypoint(name, alongM, new AiWaypoint(lat, lon, altitudeFt, speedKt, flags)));
    }
}
