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

    /// <summary>Start (Stufe B): Spawn auf der Bahn, Startlauf, Abheben, Steigflug geradeaus auf 3000 ft über der Bahn, Fahrwerk rein.</summary>
    B1,

    /// <summary>Überflug in Reiseflughöhe: geradeaus über den Flughafen hinweg, von der Wegpunkt-KI geflogen.</summary>
    C1,

    /// <summary>Rollen: Spawn an einer Parkposition, Pushback, über die Rollwege zum Haltepunkt der Startbahn (Duopilo rollt selbst).</summary>
    D1,
}

/// <summary>
/// Wie der Test die Geschwindigkeit der KI regelt. Die Geschwindigkeit in den Wegpunkten beachtet sie nicht, und AI DESIRED SPEED
/// überschreibt sie (beta.14: immer 169 kt) – Throttle und Velocity sind Versuche.
/// </summary>
public enum TrafficSpeedMethod
{
    /// <summary>Nur die Geschwindigkeit in den Wegpunkten.</summary>
    Off,

    /// <summary>AI DESIRED SPEED auf die Geschwindigkeit des nächsten Wegpunkts (wirkte in beta.14 nicht).</summary>
    Desired,

    /// <summary>Wegpunkte in der Luft mit THROTTLE_REQUESTED und festem Schub in Prozent statt SPEED_REQUESTED.</summary>
    Throttle,

    /// <summary>VELOCITY BODY Z fünfmal pro Sekunde auf die Wunschgeschwindigkeit (als TAS), dazu AI DESIRED SPEED.</summary>
    Velocity,
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

    /// <summary>
    /// Überflug: Bezugspunkt in der Mitte der Bahn, Kurs <paramref name="trackDeg"/> (rechtweisend) oder der der Bahn. Point und
    /// Offsets rechnen dann entlang dieses Kurses ab dem Überflugpunkt.
    /// </summary>
    public RunwayEnd Overhead(double? trackDeg)
    {
        var (lat, lon) = Point(LengthM / 2);
        return this with
        {
            ThresholdLatitude = lat,
            ThresholdLongitude = lon,
            HeadingTrueDeg = trackDeg is { } t ? TrafficMath.Normalize(t) : HeadingTrueDeg,
        };
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
/// <param name="ApproachKt">Anfluggeschwindigkeit (Vapp), aus der die Geschwindigkeiten der Wegpunkte folgen.</param>
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
    double ApproachKt,
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

    /// <summary>
    /// Anfluggeschwindigkeit (Vapp), wenn weder Anfrage noch Freigabeliste eine nennen. Daraus: ab 6 NM Vapp + 25, darunter Vapp + 5,
    /// an der Schwelle Vapp, beim Aufsetzen Vapp − 5 (bei 135 kt die bisherigen 160/140/135/130).
    /// </summary>
    public const double DefaultApproachKt = 135;

    private const double StopRollM = 1300;

    private const double StopMarginM = 250;

    private const double BeyondStopM = 60;

    /// <summary>Luft-Wegpunkte liegen mindestens so weit vor dem ersten Boden-Wegpunkt.</summary>
    private const double AirBeforeGroundM = 100;

    /// <summary>B1: Spawn so weit hinter dem Bahnanfang, auf der Mittellinie in Startrichtung.</summary>
    public const double LineUpM = 60;

    /// <summary>B1: Startgeschwindigkeit (etwa V2) über Vapp des Typs – A320 135 + 10, C172 65 + 10 (etwa Vy).</summary>
    public const double TakeoffMarginKt = 10;

    /// <summary>B1: Ab dieser Höhe über der Bahn ist der Start gelungen und der Test endet.</summary>
    public const double ClimbEndFt = 2800;

    private static readonly double[] Gates = [12, 10, 8, 6, 4, 3, 2, 1, 0.5];

    /// <summary>Ob es ein Start ist (<see cref="TrafficTestVariant.B1"/>) statt eines Anflugs.</summary>
    public bool IsTakeoff => Variant == TrafficTestVariant.B1;

    /// <summary>Ob es ein Überflug ist (<see cref="TrafficTestVariant.C1"/>).</summary>
    public bool IsOverflight => Variant == TrafficTestVariant.C1;

    /// <summary>Ob es Rollen ist (<see cref="TrafficTestVariant.D1"/>).</summary>
    public bool IsTaxi => Variant == TrafficTestVariant.D1;

    /// <summary>Rollen: Route von der Parkposition zum Haltepunkt.</summary>
    public TaxiRoute? Taxi { get; init; }

    /// <summary>Überflug: Reiseflughöhe (MSL, Fuß; Flugfläche bei Standarddruck gleichgesetzt).</summary>
    public double CruiseAltitudeFt { get; init; }

    /// <summary>
    /// Überflug: Geschwindigkeit, die die KI bekommt (Wegpunkte, Wunschgeschwindigkeit, Spawn) – die wahre Reisegeschwindigkeit. Die KI
    /// nimmt sie als TAS: beta.18 in EDDF flog die A320 auf FL350 mit 265 kt gewünscht 265 kt über Grund statt 450.
    /// </summary>
    public double CruiseKt { get; init; }

    /// <summary>Überflug: die IAS dazu (nur zur Anzeige).</summary>
    public double CruiseIasKt { get; init; }

    /// <summary>Überflug: wahre Reisegeschwindigkeit.</summary>
    public double CruiseTasKt { get; init; }

    /// <summary>Überflug: Ab hier hinter dem Überflugpunkt ist der Test durch.</summary>
    public double ExitM { get; init; }

    /// <summary>Überflug, Verkehrsflugzeuge: Flugfläche und wahre Geschwindigkeit (etwa Mach 0,78).</summary>
    public const double AirlinerFlightLevel = 350;

    public const double AirlinerCruiseTasKt = 450;

    /// <summary>Überflug, Allgemeine Luftfahrt: Flugfläche; die TAS ist doppelte Vapp (C172 130 kt), höchstens 300 kt.</summary>
    public const double GeneralAviationFlightLevel = 85;

    /// <summary>Überflug: Spawn so weit vor dem Überflugpunkt (Verkehrsflugzeuge, GA kürzer – sonst dauert es länger als MaxMinutes).</summary>
    public const double AirlinerOverflightNm = 40;

    public const double GeneralAviationOverflightNm = 15;

    /// <summary>Überflug: höchstens so weit vor dem Überflugpunkt – der Sim erzeugt Objekte nur bis rund 80 NM um den Spieler.</summary>
    public const double MaxOverflightNm = 75;

    /// <summary>B1: Startgeschwindigkeit, etwa V2 (Vapp + <see cref="TakeoffMarginKt"/>).</summary>
    public double TakeoffKt => ApproachKt + TakeoffMarginKt;

    /// <summary>Geschwindigkeit am ersten Boden-Wegpunkt (Vapp − 5, mindestens 40 kt).</summary>
    public double TouchdownKt => Math.Max(40, ApproachKt - 5);

    /// <summary>Schub der Wegpunkte in der Luft bei <see cref="TrafficSpeedMethod.Throttle"/>, sonst null.</summary>
    public double? ThrottlePercent { get; init; }

    /// <summary>Standard-Schub für <see cref="TrafficSpeedMethod.Throttle"/>, wenn die Anfrage keinen nennt.</summary>
    public const double DefaultThrottlePercent = 30;

    /// <summary>
    /// Wunschgeschwindigkeit in der Luft an einer Position: die des nächsten Wegpunkts, hinter der Schwelle aber nicht unter
    /// <see cref="TouchdownKt"/> – sonst bekäme die KI im Abfangen schon die Ausrollgeschwindigkeit (60 kt) vorgegeben.
    /// </summary>
    public double AirborneTargetKt(double alongM)
    {
        var next = Next(alongM).Waypoint.SpeedKt;
        return IsOverflight ? CruiseKt : IsTakeoff || alongM < 0 ? next : Math.Max(next, TouchdownKt);
    }

    /// <summary>Wahre Fluggeschwindigkeit (TAS) zu einer angezeigten (IAS) in einer Höhe: Faustregel 2 % je 1000 ft.</summary>
    public static double TrueAirspeedKt(double indicatedKt, double altitudeFt) => indicatedKt * (1 + (0.02 * Math.Max(0, altitudeFt) / 1000));

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
        double? finalLowFt = null,
        double? approachKt = null,
        double? throttlePercent = null)
    {
        if (variant == TrafficTestVariant.B1)
        {
            return BuildTakeoff(runway, approachKt, throttlePercent: throttlePercent);
        }

        if (variant == TrafficTestVariant.C1)
        {
            return BuildOverflight(runway.Overhead(null), TrafficCategory.Airliner, approachKt);
        }

        if (variant == TrafficTestVariant.D1)
        {
            throw new ArgumentException("D1 (taxi) needs a taxi route, see BuildTaxi.", nameof(variant));
        }

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
        var vapp = Math.Clamp(approachKt ?? DefaultApproachKt, 50, 180);
        var intermediateKt = vapp + 25;
        var gateKt = vapp + 5;
        var touchdownKt = Math.Max(40, vapp - 5);

        // A2: Fahrwerk bei 3000 ft über der Bahn, spätestens am Punkt des Gleitpfads in dieser Höhe; liegt der Spawn schon tiefer, gleich
        var spawnHeight = GlidePathFt(runway, spawn) - runway.ElevationFt;
        var gearAtSpawn = variant == TrafficTestVariant.A1 || spawnHeight <= GearCommandFt;
        double? gearDownFt = gearAtSpawn ? null : GearCommandFt;
        double? gearDownNm = gearAtSpawn ? null : Math.Round(GlidePathDistanceNm(GearCommandFt), 1);

        var waypoints = new List<TrafficTestWaypoint>();
        var checkpoints = new List<TrafficTestCheckpoint>();
        var throttle = Throttle(throttlePercent);
        var air = AirFlags(throttle);
        foreach (var gate in Gates.Where(g => g < spawn - 0.5).Append(0))
        {
            var along = -gate * RunwayEnd.MetersPerNm;
            var speed = gate == 0 ? vapp : gate >= 6 ? intermediateKt : gateKt;
            var name = gate == 0 ? "threshold" : gate.ToString("0.#", CultureInfo.InvariantCulture) + "nm";
            var path = GlidePathFt(runway, gate);
            checkpoints.Add(new TrafficTestCheckpoint(name, along, path, speed));
            if (along > touchdown - AirBeforeGroundM)
            {
                continue;
            }

            var (lat, lon) = runway.Point(along);
            var drop = low * FinalLowShare(gate);
            waypoints.Add(new(name, along, new AiWaypoint(lat, lon, path - drop, speed, air, throttle ?? 0), drop > 0 ? path : null));
        }

        var ground = AiWaypointFlags.SpeedRequested | AiWaypointFlags.OnGround;
        Add(waypoints, runway, "touchdown", touchdown, runway.ElevationFt, touchdownKt, ground);
        Add(waypoints, runway, "rollout1", touchdown + ((stop - touchdown) * 0.45), runway.ElevationFt, Math.Min(60, touchdownKt - 5), ground);
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
            AirspeedKt: (uint)(spawn >= 6 ? intermediateKt : gateKt));

        return new TrafficTestPlan(variant, runway, initial, spawn, gearAtSpawn, gearDownFt, gearDownNm, touchdown, stop, low, vapp, waypoints, checkpoints, warnings)
        {
            ThrottlePercent = throttle,
        };
    }

    /// <summary>
    /// Start (B1): Spawn <see cref="LineUpM"/> hinter dem Bahnanfang am Boden, Fahrwerk unten; dann Wegpunkte in der Luft geradeaus:
    /// 1 NM hinter dem Bahnende 1000 ft, 4 NM weiter 3000 ft über der Bahn, dann geradeaus weiter. Geschwindigkeiten aus der
    /// Startgeschwindigkeit (etwa V2 = Vapp + 10): + 10, + 40, + 60 kt. Mit <paramref name="rollM"/> liegt vorher ein Boden-Wegpunkt
    /// so weit voraus, mit Startgeschwindigkeit – falls die KI ohne ihn nicht losrollt oder nicht beschleunigt.
    /// </summary>
    public static TrafficTestPlan BuildTakeoff(RunwayEnd runway, double? approachKt = null, double? rollM = null, double? throttlePercent = null)
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

        var vapp = Math.Clamp(approachKt ?? DefaultApproachKt, 50, 180);
        var v2 = vapp + TakeoffMarginKt;
        var waypoints = new List<TrafficTestWaypoint>();
        var roll = Math.Clamp(rollM ?? 0, 0, runway.LengthM * 0.6);
        if (roll > 0)
        {
            Add(waypoints, runway, "roll", LineUpM + roll, runway.ElevationFt, v2, AiWaypointFlags.SpeedRequested | AiWaypointFlags.OnGround);
        }

        var throttle = Throttle(throttlePercent);
        var air = AirFlags(throttle);
        var dep1 = runway.LengthM + RunwayEnd.MetersPerNm;
        Add(waypoints, runway, "dep1", dep1, runway.ElevationFt + 1000, v2 + 10, air, throttle);
        Add(waypoints, runway, "dep2", dep1 + (4 * RunwayEnd.MetersPerNm), runway.ElevationFt + 3000, Math.Min(v2 + 40, 250), air, throttle);
        Add(waypoints, runway, "dep3", dep1 + (10 * RunwayEnd.MetersPerNm), runway.ElevationFt + 3000, Math.Min(v2 + 60, 250), air, throttle);
        Add(waypoints, runway, "dep4", dep1 + (20 * RunwayEnd.MetersPerNm), runway.ElevationFt + 3000, Math.Min(v2 + 60, 250), air, throttle);

        var (lat, lon) = runway.Point(LineUpM);
        var spawn = new AiInitPosition(lat, lon, runway.ElevationFt, runway.HeadingTrueDeg, OnGround: true, AirspeedKt: 0);
        return new TrafficTestPlan(TrafficTestVariant.B1, runway, spawn, 0, true, null, null, 0, 0, 0, vapp, waypoints, [], warnings)
        {
            ThrottlePercent = throttle,
        };
    }

    /// <summary>
    /// Überflug (C1): Spawn <paramref name="spawnNm"/> vor dem Überflugpunkt auf Reiseflughöhe in Kursrichtung, Fahrwerk oben; Wegpunkte
    /// über dem Punkt, gleich weit dahinter (Ende des Tests) und 30 NM weiter, alle auf Reiseflughöhe mit der Reisegeschwindigkeit als
    /// IAS. <paramref name="overhead"/> ist der Überflugpunkt mit Kurs (<see cref="RunwayEnd.Overhead"/>).
    /// </summary>
    public static TrafficTestPlan BuildOverflight(
        RunwayEnd overhead,
        TrafficCategory category,
        double? approachKt = null,
        double? flightLevel = null,
        double? cruiseTasKt = null,
        double? spawnNm = null)
    {
        var ga = category == TrafficCategory.GeneralAviation;
        var vapp = Math.Clamp(approachKt ?? DefaultApproachKt, 50, 180);
        var level = Math.Clamp(flightLevel ?? (ga ? GeneralAviationFlightLevel : AirlinerFlightLevel), 30, 450);
        var altitude = Math.Max(level * 100, overhead.ElevationFt + 1500);
        var tas = Math.Clamp(cruiseTasKt ?? (ga ? Math.Min(vapp * 2, 300) : AirlinerCruiseTasKt), 80, 600);
        var ias = Math.Round(tas / (1 + (0.02 * altitude / 1000)));
        var distance = Math.Clamp(spawnNm ?? (ga ? GeneralAviationOverflightNm : AirlinerOverflightNm), 5, MaxOverflightNm);
        var exit = distance * RunwayEnd.MetersPerNm;

        var waypoints = new List<TrafficTestWaypoint>();
        var flags = AiWaypointFlags.SpeedRequested;
        Add(waypoints, overhead, "overhead", 0, altitude, tas, flags);
        Add(waypoints, overhead, "exit", exit, altitude, tas, flags);
        Add(waypoints, overhead, "beyond", exit + (30 * RunwayEnd.MetersPerNm), altitude, tas, flags);

        var (lat, lon) = overhead.Point(-exit);
        var spawn = new AiInitPosition(lat, lon, altitude, overhead.HeadingTrueDeg, OnGround: false, AirspeedKt: (uint)Math.Round(tas));
        return new TrafficTestPlan(TrafficTestVariant.C1, overhead, spawn, distance, false, null, null, 0, 0, 0, vapp, waypoints, [], [])
        {
            CruiseAltitudeFt = altitude,
            CruiseKt = tas,
            CruiseIasKt = ias,
            CruiseTasKt = tas,
            ExitM = exit,
        };
    }

    /// <summary>
    /// Rollen (D1): Spawn am Anfang des Wegs (Parkposition, Kurs wie beim Losrollen bzw. Pushback), Fahrwerk unten. Wegpunkte bekommt
    /// die KI keine – Duopilo rollt selbst; der eine Eintrag am Haltepunkt dient Status und Auswertung.
    /// </summary>
    public static TrafficTestPlan BuildTaxi(RunwayEnd runway, double? approachKt, TaxiRoute route, TaxiGraph graph)
    {
        var start = route.PushbackPath is { } push ? push.At(0) : route.TaxiPath.At(0);
        var heading = route.PushbackPath is not null ? TrafficMath.Normalize(start.HeadingDeg + 180) : start.HeadingDeg;
        var (lat, lon) = graph.ToLatLon(start.East, start.North);
        var spawn = new AiInitPosition(lat, lon, runway.ElevationFt, heading, OnGround: true, AirspeedKt: 0);
        var hold = route.Hold.Node;
        var (holdAlong, _) = runway.Offsets(hold.Latitude, hold.Longitude);
        var waypoints = new List<TrafficTestWaypoint>
        {
            new("hold", holdAlong, new AiWaypoint(hold.Latitude, hold.Longitude, runway.ElevationFt, 0, AiWaypointFlags.OnGround)),
        };
        var vapp = Math.Clamp(approachKt ?? DefaultApproachKt, 50, 180);
        return new TrafficTestPlan(TrafficTestVariant.D1, runway, spawn, 0, true, null, null, 0, 0, 0, vapp, waypoints, [], [])
        {
            Taxi = route,
        };
    }

    /// <summary>Der nächste Wegpunkt vor einer Position (entlang der Mittellinie), am Ende der letzte.</summary>
    public TrafficTestWaypoint Next(double alongM) =>
        Waypoints.FirstOrDefault(w => w.AlongM > alongM + 1) ?? Waypoints[^1];

    private static void Add(List<TrafficTestWaypoint> list, RunwayEnd runway, string name, double alongM, double altitudeFt, double speedKt, AiWaypointFlags flags, double? throttlePercent = null)
    {
        var (lat, lon) = runway.Point(alongM);
        list.Add(new TrafficTestWaypoint(name, alongM, new AiWaypoint(lat, lon, altitudeFt, speedKt, flags, throttlePercent ?? 0)));
    }

    /// <summary>Schub 0–100 %, null bleibt null (dann gilt die Geschwindigkeit der Wegpunkte).</summary>
    private static double? Throttle(double? percent) => percent is { } p ? Math.Clamp(p, 0, 100) : null;

    /// <summary>Wegpunkte in der Luft: Geschwindigkeit oder (Versuch) fester Schub, Sinkrate jeweils berechnet.</summary>
    private static AiWaypointFlags AirFlags(double? throttle) =>
        (throttle is null ? AiWaypointFlags.SpeedRequested : AiWaypointFlags.ThrottleRequested) | AiWaypointFlags.ComputeVerticalSpeed;
}
