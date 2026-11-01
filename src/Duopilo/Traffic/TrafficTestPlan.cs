using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Varianten der Stufe A der Testlinie (docs/research/eigener-verkehr.md).</summary>
public enum TrafficTestVariant
{
    /// <summary>Spawn 5 NM vor der Bahn mit Fahrwerk unten (wie mrlm-net/simconnect #289).</summary>
    A1,

    /// <summary>Spawn 15 NM vor der Bahn mit Fahrwerk oben, Fahrwerk bei 3000 ft über der Bahn ausfahren (bei 2500 ft draußen).</summary>
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
/// <param name="PathAltitudeFt">Höhe auf dem 3°-Gleitpfad, wenn der Wegpunkt tiefer gelegt ist (<see cref="TrafficTestPlan.FinalLowFt"/>); sonst null.</param>
public sealed record TrafficTestWaypoint(string Name, double AlongM, AiWaypoint Waypoint, double? PathAltitudeFt = null)
{
    /// <summary>Höhe, an der die Auswertung den Anflug misst: der Gleitpfad, nicht der tiefer gelegte Wegpunkt.</summary>
    public double GlidePathAltitudeFt => PathAltitudeFt ?? Waypoint.AltitudeFt;
}

/// <summary>
/// Messpunkt im Anflug (…nm und Schwelle) für die Auswertung – auch dort, wo kein Wegpunkt liegt, z. B. an der Schwelle, wenn der erste
/// Boden-Wegpunkt davor oder knapp dahinter liegt.
/// </summary>
/// <param name="GlidePathFt">Höhe auf dem 3°-Gleitpfad (MSL).</param>
public sealed record TrafficTestCheckpoint(string Name, double AlongM, double GlidePathFt, double TargetKt);

/// <summary>
/// Plan eines Testanflugs: Spawn auf der verlängerten Mittellinie, 3°-Gleitpfad bis 50 ft über der Schwelle, erster Boden-Wegpunkt
/// (<see cref="TouchdownM"/>, Standard an der Schwelle), Ausrollen mit fallender Geschwindigkeit bis zum Halt auf der Bahn. Ein
/// Wegpunkt liegt 60 m hinter dem Haltepunkt, weil die KI laut mrlm-net/simconnect #289 etwa 46 m vor dem letzten Rollwegpunkt stehen
/// bleibt.
///
/// Erfahrungen aus EDDL 05L/05R (31.10./1.11.2026): Die KI geht rund 1 km vor dem ersten Boden-Wegpunkt in ihren Landemodus
/// (STATE_SIMPLE_LANDING), hebt die Nase, kreuzt die Schwelle 170–190 ft zu hoch und setzt 200–700 m hinter dem Boden-Wegpunkt auf.
/// Deshalb liegt er jetzt 150 m hinter der Schwelle statt 300 m (an der Schwelle setzte die A330-900 schon nach gut 100 m auf). Die Wegpunkte der letzten Meile liegen um <see cref="FinalLowFt"/>
/// tiefer als der Gleitpfad (1 NM ein Viertel, 0,5 NM 60 %, Schwelle voll); im Landemodus ignoriert die KI sie allerdings.
/// </summary>
/// <param name="GearDownFt">A2: Höhe über der Bahn, bei der der Fahrwerksbefehl rausgeht (damit es bei <see cref="GearDownByFt"/> draußen ist).</param>
/// <param name="GearDownNm">A2: Entfernung, ab der der Befehl spätestens rausgeht (die Höhe auf dem Gleitpfad), falls die KI zu hoch fliegt.</param>
/// <param name="TouchdownM">Erster Boden-Wegpunkt hinter der Schwelle (davor negativ).</param>
/// <param name="FinalLowFt">Wie weit der Schwellen-Wegpunkt unter dem Gleitpfad liegt (0 = genau auf dem Pfad).</param>
/// <param name="Checkpoints">Messpunkte der Auswertung im Anflug, mit Schwelle.</param>
public sealed record TrafficTestPlan(
    TrafficTestVariant Variant,
    RunwayEnd Runway,
    AiInitPosition Spawn,
    double SpawnNm,
    bool GearAtSpawn,
    double? GearDownFt,
    double? GearDownNm,
    double TouchdownM,
    double StopM,
    double FinalLowFt,
    IReadOnlyList<TrafficTestWaypoint> Waypoints,
    IReadOnlyList<TrafficTestCheckpoint> Checkpoints,
    IReadOnlyList<string> Warnings)
{
    public const double DefaultFinalLowFt = 100;

    public const double MaxFinalLowFt = 300;

    public const double GlideSlopeDeg = 3.0;

    public const double ThresholdCrossingFt = 50;

    /// <summary>
    /// Erster Boden-Wegpunkt 150 m hinter der Schwelle: Mit 0 setzten die A330 105–330 m dahinter auf (EDDL, 1.11.2026), mit 150 m
    /// sollte das in der Aufsetzzone (rund 300–450 m) landen.
    /// </summary>
    public const double DefaultTouchdownM = 150;

    public const double MinTouchdownM = -500;

    public const double MaxTouchdownM = 600;

    /// <summary>Bis zu dieser Höhe über der Bahn soll das Fahrwerk draußen sein (wie im echten Anflug).</summary>
    public const double GearDownByFt = 2500;

    /// <summary>A2 schickt den Fahrwerksbefehl hier: Das Ausfahren dauerte im Test 12–24 s, also rund 200–400 ft Sinkflug.</summary>
    public const double GearCommandFt = 3000;

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

    /// <summary>Luft-Wegpunkte liegen mindestens so weit vor dem ersten Boden-Wegpunkt.</summary>
    private const double AirBeforeGroundM = 100;

    private static readonly double[] Gates = [12, 10, 8, 6, 4, 3, 2, 1, 0.5];

    public static double DefaultSpawnNm(TrafficTestVariant variant) => variant == TrafficTestVariant.A2 ? 15 : 5;

    /// <summary>Höhe auf dem Gleitpfad <paramref name="distanceNm"/> vor der Schwelle (MSL, Fuß).</summary>
    public static double GlidePathFt(RunwayEnd runway, double distanceNm) =>
        runway.ElevationFt + ThresholdCrossingFt + (distanceNm * FeetPerNm);

    /// <summary>Entfernung vor der Schwelle, in der der Gleitpfad <paramref name="heightFt"/> über der Bahn liegt.</summary>
    public static double GlidePathDistanceNm(double heightFt) => Math.Max(0, (heightFt - ThresholdCrossingFt) / FeetPerNm);

    private static double FeetPerNm => RunwayEnd.MetersPerNm * Math.Tan(GlideSlopeDeg * Math.PI / 180) * RunwayEnd.FeetPerMeter;

    /// <summary>Anteil von <see cref="FinalLowFt"/>, um den ein Wegpunkt <paramref name="distanceNm"/> vor der Schwelle tiefer liegt.</summary>
    public static double FinalLowShare(double distanceNm) => distanceNm switch
    {
        <= 0 => 1.0,
        <= 0.5 => 0.6,
        <= 1 => 0.25,
        _ => 0,
    };

    public static TrafficTestPlan Build(
        RunwayEnd runway,
        TrafficTestVariant variant,
        double? spawnNm = null,
        double? touchdownM = null,
        double? finalLowFt = null)
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
        var touchdown = Math.Clamp(touchdownM ?? DefaultTouchdownM, MinTouchdownM, Math.Min(MaxTouchdownM, runway.LengthM / 3));
        var stop = Math.Max(touchdown + 400, Math.Min(Math.Max(touchdown, 0) + StopRollM, runway.LengthM - StopMarginM));
        var beyond = Math.Min(stop + BeyondStopM, runway.LengthM - 100);
        var low = Math.Clamp(finalLowFt ?? DefaultFinalLowFt, 0, MaxFinalLowFt);

        // A2: Fahrwerk bei 3000 ft über der Bahn, spätestens am Punkt des Gleitpfads in dieser Höhe; liegt der Spawn schon tiefer, gleich
        var spawnHeight = GlidePathFt(runway, spawn) - runway.ElevationFt;
        var gearAtSpawn = variant == TrafficTestVariant.A1 || spawnHeight <= GearCommandFt;
        double? gearDownFt = gearAtSpawn ? null : GearCommandFt;
        double? gearDownNm = gearAtSpawn ? null : Math.Round(GlidePathDistanceNm(GearCommandFt), 1);

        var waypoints = new List<TrafficTestWaypoint>();
        var checkpoints = new List<TrafficTestCheckpoint>();
        var air = AiWaypointFlags.SpeedRequested | AiWaypointFlags.ComputeVerticalSpeed;
        foreach (var gate in Gates.Where(g => g < spawn - 0.5).Append(0))
        {
            var along = -gate * RunwayEnd.MetersPerNm;
            var speed = gate == 0 ? ThresholdKt : gate >= 6 ? IntermediateKt : ApproachKt;
            var name = gate == 0 ? "threshold" : gate.ToString("0.#", CultureInfo.InvariantCulture) + "nm";
            var path = GlidePathFt(runway, gate);
            checkpoints.Add(new TrafficTestCheckpoint(name, along, path, speed));
            if (along > touchdown - AirBeforeGroundM)
            {
                continue;
            }

            var (lat, lon) = runway.Point(along);
            var drop = low * FinalLowShare(gate);
            waypoints.Add(new(name, along, new AiWaypoint(lat, lon, path - drop, speed, air), drop > 0 ? path : null));
        }

        var ground = AiWaypointFlags.SpeedRequested | AiWaypointFlags.OnGround;
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

        return new TrafficTestPlan(variant, runway, initial, spawn, gearAtSpawn, gearDownFt, gearDownNm, touchdown, stop, low, waypoints, checkpoints, warnings);
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
