using System.Globalization;
using System.Text.Json;
using Duopilo.Atc.Airspace;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Sim;
using Microsoft.Extensions.Options;

namespace Duopilo.Traffic;

/// <summary>Anfrage für einen Testanflug.</summary>
/// <param name="Title">Modelltitel; leer = automatisch (A320, FSLTL bevorzugt).</param>
/// <param name="SpawnNm">Abstand des Spawns zur Schwelle; leer = Standard der Variante (A1 5 NM, A2 15 NM).</param>
/// <param name="Gear">Wie das Fahrwerk ausgefahren wird (Event GEAR_DOWN, GEAR_SET, Hebel-SimVar oder alles).</param>
/// <param name="FinalLowFt">Wie weit die letzte Meile unter dem Gleitpfad liegt; leer = <see cref="TrafficTestPlan.DefaultFinalLowFt"/>.</param>
/// <param name="TouchdownM">Erster Boden-Wegpunkt hinter der Schwelle (davor negativ); leer = <see cref="TrafficTestPlan.DefaultTouchdownM"/>.</param>
/// <param name="ApproachKt">Anfluggeschwindigkeit (Vapp); leer = aus der Freigabeliste, sonst <see cref="TrafficTestPlan.DefaultApproachKt"/>.</param>
/// <param name="SpeedMethod">Wie die Geschwindigkeit geregelt wird (<see cref="TrafficSpeedMethod"/>); die Wegpunkt-Geschwindigkeit beachtet die KI nicht.</param>
/// <param name="ThrottlePercent">Schub der Wegpunkte in der Luft bei <see cref="TrafficSpeedMethod.Throttle"/>; leer = <see cref="TrafficTestPlan.DefaultThrottlePercent"/>.</param>
/// <param name="GoAround">Durchstarten, wenn die KI zu hoch oder zu schnell über die Schwelle kommt oder zu lange nicht aufsetzt (<see cref="TrafficGoAround"/>).</param>
/// <param name="RollM">B1: Boden-Wegpunkt so weit vor dem Spawn, mit Startgeschwindigkeit; leer oder 0 = keiner (<see cref="TrafficTestPlan.BuildTakeoff"/>).</param>
/// <param name="KinematicNm">Ab dieser Entfernung zur Schwelle bewegt Duopilo das Flugzeug selbst (<see cref="TrafficKinematics"/>); leer = Wegpunkt-KI bis zum Halt.</param>
/// <param name="TrackDeg">C1: Kurs des Überflugs (rechtweisend); leer = Richtung der Bahn.</param>
/// <param name="FlightLevel">C1: Flugfläche; leer = 350 (GA 85).</param>
/// <param name="CruiseKt">C1: wahre Reisegeschwindigkeit; leer = 450 kt (GA doppelte Vapp).</param>
/// <param name="Parking">D1: Parkposition, z. B. "A12"; leer = passend zum Typ zufällig.</param>
/// <param name="KinematicTakeoff">B1: Duopilo fliegt den Start selbst (Startlauf, Rotieren, Steigflug, <see cref="TrafficKinematics.Takeoff"/>); false = Wegpunkt-KI wie bis beta.20.</param>
public sealed record TrafficTestRequest(
    string Airport,
    string Runway,
    TrafficTestVariant Variant,
    string? Title = null,
    string? Livery = null,
    double? SpawnNm = null,
    AiGearMethod Gear = AiGearMethod.All,
    double? FinalLowFt = null,
    double? TouchdownM = null,
    double? ApproachKt = null,
    TrafficSpeedMethod SpeedMethod = TrafficSpeedMethod.Desired,
    bool GoAround = true,
    double? RollM = null,
    double? ThrottlePercent = null,
    double? KinematicNm = null,
    double? TrackDeg = null,
    double? FlightLevel = null,
    double? CruiseKt = null,
    bool IgnoreRunwayRules = false,
    bool KinematicTakeoff = true,
    string? Parking = null);

/// <summary>Zeitpunkt und Text eines Ereignisses der Testlinie.</summary>
public sealed record TrafficTestEvent(double Seconds, string Text);

/// <summary>Letzter Messpunkt eines laufenden Tests.</summary>
/// <param name="PitchDeg">Längsneigung, Nase hoch positiv.</param>
public sealed record TrafficTestLast(
    double Seconds,
    double AltitudeFt,
    double AltitudeAglFt,
    double IndicatedKt,
    double TargetKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    double PitchDeg,
    double BankDeg,
    bool OnGround,
    double GearExtendedPercent,
    double? DistanceToThresholdNm,
    double AlongM,
    double CrossM,
    string? AiState,
    string Lights,
    double DesiredKt);

/// <summary>Zustand eines Testanflugs für Tool und Browser.</summary>
/// <param name="State">running oder ended.</param>
/// <param name="Tail">Kennzeichen des Testflugzeugs (DUO01 bis DUO04).</param>
/// <param name="Type">ICAO-Typ laut Freigabeliste, sonst null.</param>
/// <param name="ModelStatus">verified, candidate, untested, excluded oder unknown (<see cref="TrafficModels"/>).</param>
public sealed record TrafficTestStatus(
    string State,
    string Tail,
    string Variant,
    string Airport,
    string Runway,
    string Title,
    string? Livery,
    string? Type,
    string ModelStatus,
    string Gear,
    double FinalLowFt,
    double ApproachKt,
    string SpeedMethod,
    double? ThrottlePercent,
    bool GoAround,
    uint ObjectId,
    DateTimeOffset StartedUtc,
    double ElapsedSeconds,
    string? EndReason,
    TrafficTestLast? Last,
    TrafficTestSummary Summary,
    IReadOnlyList<TrafficTestEvent> Events,
    IReadOnlyList<TrafficTestWaypoint> Waypoints,
    string? CsvFile,
    string? SummaryFile,
    string? Note,
    double? KinematicNm = null,
    TrafficTestKinematics? Kinematics = null,
    TrafficTestTaxi? Taxi = null);

/// <summary>Rollweg-Netz eines Flughafens zum Prüfen (traffic_taxi_info).</summary>
/// <param name="Mapping">Zuordnung von BIAS_X/BIAS_Z zu Ost/Nord; <paramref name="MappingErrorM"/> ist der mittlere Abstand der Bahn-Abschnitte zur Mittellinie damit.</param>
/// <param name="BrokenPaths">Abschnitte mit Index außerhalb der Listen (verworfen).</param>
public sealed record TrafficTaxiInfo(
    string Airport,
    double ReferenceLatitude,
    double ReferenceLongitude,
    double ElevationFt,
    int TaxiPoints,
    int Parkings,
    int Paths,
    int RunwayPaths,
    int BrokenPaths,
    string Mapping,
    double? MappingErrorM,
    IReadOnlyList<string> Taxiways,
    IReadOnlyList<TrafficTaxiInfoParking> ParkingSpots,
    IReadOnlyList<TrafficTaxiInfoHolds> Holds,
    TrafficTaxiInfoRoute? Route,
    string? RouteError,
    int Obstacles = 0,
    int BlockedPaths = 0,
    int OccupiedParkings = 0,
    string? ObstacleError = null);

/// <param name="Occupied">Ein anderes Flugzeug steht darauf (innerhalb des Radius, mindestens 10 m).</param>
public sealed record TrafficTaxiInfoParking(string Label, string Type, double RadiusM, double HeadingDeg, bool Occupied = false);

/// <summary>Haltepunkte einer Bahn: Anzahl und die Rollwege daran.</summary>
public sealed record TrafficTaxiInfoHolds(string Runway, int Count, IReadOnlyList<string> Taxiways);

/// <summary>Route von der Parkposition zum Haltepunkt mit den Punkten (zum Nachsehen auf der Karte).</summary>
public sealed record TrafficTaxiInfoRoute(
    string Parking,
    string ParkingType,
    string HoldRunway,
    string HoldTaxiway,
    IReadOnlyList<string> Taxiways,
    double RouteM,
    bool Pushback,
    double PushbackM,
    double TaxiPathM,
    int Turns,
    double? MinTurnRadiusM,
    IReadOnlyList<TrafficTaxiInfoPoint> Points);

public sealed record TrafficTaxiInfoPoint(double Latitude, double Longitude, string Kind);

/// <summary>Stand des Rollens (D1, <see cref="TaxiKinematics"/>).</summary>
/// <param name="Phase">parked, pushback, pushback_done, taxi, holding_short (vor einer gekreuzten Bahn), holding; B2 line_up, lined_up; A3 landing bis zum Abrollpunkt.</param>
/// <param name="Crossings">Gekreuzte Bahnen mit dem Halt davor; <paramref name="CrossingsDone"/> davon schon gekreuzt.</param>
/// <param name="Taxiways">Rollwege der Route.</param>
/// <param name="RouteM">Länge der Route im Netz.</param>
/// <param name="DistanceM">Bisher gerollt (Pushback und Rollen).</param>
/// <param name="Mapping">Wie BIAS_X/BIAS_Z auf Ost/Nord liegen (<see cref="TaxiGraph.Mapping"/>), mit dem mittleren Abstand der Bahn-Abschnitte zur Mittellinie.</param>
public sealed record TrafficTestTaxi(
    string Phase,
    string Parking,
    string ParkingType,
    string HoldRunway,
    string HoldTaxiway,
    IReadOnlyList<string> Taxiways,
    double RouteM,
    bool Pushback,
    double? PushbackM,
    double TaxiPathM,
    int Turns,
    double? MinTurnRadiusM,
    double DistanceM,
    double SpeedKt,
    double MaxSpeedKt,
    string Mapping,
    double? MappingErrorM,
    double? LineUpAlongM = null,
    double? ExitAlongM = null,
    double? ExitKt = null,
    IReadOnlyList<string>? Crossings = null,
    int CrossingsDone = 0,
    string? WaitingFor = null);

/// <summary>Stand der Kinematik eines Tests (<see cref="TrafficKinematics"/>).</summary>
/// <param name="Phase">approach, flare, rollout, stopped; Start: lineup, takeoff_roll, rotation, climb.</param>
/// <param name="StartAlongM">Wo Duopilo übernommen hat (vor der Schwelle negativ; Start: ab dem Bahnanfang).</param>
/// <param name="StartHeightFt">Höhe der Räder über der Bahn bei der Übernahme.</param>
/// <param name="FlareAlongM">Wo das Abfangen begann.</param>
/// <param name="RotateKt">Start: Rotationsgeschwindigkeit (Vr).</param>
/// <param name="ClimbKt">Start: Steiggeschwindigkeit bis zur Beschleunigungshöhe.</param>
/// <param name="TakeoffAccelMps2">Start: Beschleunigung im Startlauf.</param>
/// <param name="RotateAlongM">Start: wo das Rotieren begann (ab dem Bahnanfang).</param>
/// <param name="LiftoffAlongM">Start: Abhebepunkt (ab dem Bahnanfang).</param>
public sealed record TrafficTestKinematics(
    string Phase,
    double StartAlongM,
    double StartHeightFt,
    double StartSpeedKt,
    double? FlareAlongM,
    double? TouchdownAlongM,
    double? TouchdownKt,
    double? TouchdownSinkFpm,
    double GroundFt,
    double CgHeightFt,
    double GroundPitchDeg = 0,
    double? RotateKt = null,
    double? ClimbKt = null,
    double? TakeoffAccelMps2 = null,
    double? RotateAlongM = null,
    double? LiftoffAlongM = null,
    double? LiftoffKt = null,
    double? LiftoffPitchDeg = null);

/// <summary>
/// Testwerkzeug für eigenen KI-Verkehr, Stufe A der Testlinie (docs/research/eigener-verkehr.md): NonATC-Flugzeuge fliegen einen
/// Endanflug über Wegpunkte, landen und halten auf der Bahn. A1 mit Fahrwerk ab dem Spawn, A2 mit Fahrwerk erst bei 3000 ft über der
/// Bahn, damit es bei 2500 ft draußen ist.
///
/// Bis zu <see cref="MaxConcurrent"/> Tests gleichzeitig, je Bahn einer (z. B. Parallelanflug auf 05L und 05R); ein neuer Test auf
/// derselben Bahn ersetzt den alten. Messpunkte (1 Hz) gehen je Test als CSV nach traffic-tests\ im Datenordner, die Auswertung als
/// JSON daneben. Die Flugzeuge werden beim Stoppen, nach <see cref="TrafficTestOptions.MaxMinutes"/> und beim Beenden von Duopilo
/// entfernt. Standardmäßig aus (Duopilo:TrafficTest:Enabled), weil es Objekte im Sim erzeugt.
/// </summary>
public sealed class TrafficTestService : IHostedService, IDisposable
{
    /// <summary>Kennzeichen (ATC ID) des ersten Testflugzeugs, die weiteren DUO02 bis DUO04 – so tauchen sie auch in get_traffic auf.</summary>
    public const string TailNumber = "DUO01";

    /// <summary>Gleichzeitige Tests, je Bahn einer.</summary>
    public const int MaxConcurrent = 4;

    /// <summary>Weiter weg lehnt der Sim neue Objekte ohnehin ab (Bereich um den Spieler).</summary>
    public const double MaxDistanceNm = 80;

    /// <summary>Beendete Tests, die der Status noch zeigt (die jüngsten).</summary>
    private const int KeepEnded = 8;

    private static readonly TimeSpan NoDataAfter = TimeSpan.FromSeconds(30);

    /// <summary>Velocity: so oft setzt der Test VELOCITY BODY Z (die KI rechnet ihre Geschwindigkeit jeden Frame nach).</summary>
    private static readonly TimeSpan VelocityPeriod = TimeSpan.FromMilliseconds(200);

    private const double FeetPerSecondPerKnot = 1.68781;

    /// <summary>Kinematik: Übernahme frühestens/spätestens so weit vor der Schwelle.</summary>
    private const double MinKinematicNm = 1;

    private const double MaxKinematicNm = 12;

    /// <summary>Falls der Sim STATIC CG TO GROUND nicht liefert: Bezugspunkt so hoch über den Rädern (A320 im Test: 9 ft).</summary>
    private const double DefaultCgHeightFt = 9;

    /// <summary>Volle Modelle: Lichter in diesem Abstand (s) noch einmal senden, bis zum Aufsetzen.</summary>
    private const double FullModelLightResendSeconds = 15;

    /// <summary>Rollen: So lange steht das Flugzeug am Haltepunkt, dann endet der Test (holding).</summary>
    private const double HoldingEndSeconds = 60;

    /// <summary>Rollen: Flugzeuge so weit um den Bezugspunkt des Flughafens zählen als Hindernisse (und so viel Rand bekommt die Abfrage).</summary>
    private const double TaxiObstacleMarginM = 6000;

    /// <summary>Rollen: Verkehr so weit um den Flughafen abfragen, bevor eine Bahn gekreuzt wird (Endanflug bis 4 NM plus Bahnlänge).</summary>
    private const double RunwayScanRadiusM = 15000;

    /// <summary>Rollen: Liefert die Verkehrsabfrage nichts, nach so vielen Sekunden am Halt trotzdem weiter.</summary>
    private const double RunwayCheckGiveUpSeconds = 30;

    /// <summary>A3: Aufsetzpunkt der Kinematik hinter der Schwelle (für den frühesten Abrollpunkt).</summary>
    private const double TaxiInTouchdownM = 450;

    /// <summary>A3: mit dieser Verzögerung rechnet die Wahl des Abrollpunkts (die Kinematik bremst bis 2,5 m/s²).</summary>
    private const double TaxiInBrakeMps2 = 2.0;

    /// <summary>Rollen: so viele Parkpositionen probiert der Test, bis eine eine Route zum Haltepunkt hat.</summary>
    private const int MaxParkingTries = 25;

    /// <summary>Start: Weicht der gemessene Boden mehr als das von der Bahnhöhe ab, gilt die Bahnhöhe aus den Bahndaten.</summary>
    private const double MaxGroundCorrectionFt = 100;

    /// <summary>STATIC PITCH darüber gilt als Unsinn (Spornradflugzeuge stehen mit rund 10–12° Nase hoch).</summary>
    private const double MaxStaticPitchDeg = 15;

    /// <summary>Start: Rollt das Flugzeug so lange nach dem Spawn nicht los, endet der Test (no_roll).</summary>
    private const double NoRollSeconds = 90;

    /// <summary>Start: Fahrwerk rein frühestens in dieser Höhe über der Bahn (positive rate).</summary>
    private const double TakeoffGearUpFt = 30;

    private readonly SimConnectClient _sim;
    private readonly TrafficConnection _traffic;
    private readonly DuopiloOptions _options;
    private readonly ILogger<TrafficTestService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly List<TrafficTestRun> _runs = [];
    private TrafficAirportDirectory? _airportDirectory;

    public TrafficTestService(SimConnectClient sim, TrafficConnection traffic, IOptions<DuopiloOptions> options, ILogger<TrafficTestService> log)
    {
        _sim = sim;
        _traffic = traffic;
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => _options.TrafficTest.Enabled;

    public string Directory => Path.Combine(_options.ResolveDataDirectory(), "traffic-tests");

    /// <summary>Kennzeichen des <paramref name="slot"/>-ten Testflugzeugs (0 = DUO01).</summary>
    public static string Tail(int slot) => string.Create(CultureInfo.InvariantCulture, $"DUO{slot + 1:00}");

    /// <summary>Installierte Flugzeugmodelle, auf Wunsch gefiltert (alle Wörter müssen vorkommen).</summary>
    public async Task<IReadOnlyList<AiModel>> ModelsAsync(string? filter, CancellationToken ct) =>
        Filter(await _sim.EnumerateAircraftModelsAsync(ct), filter);

    /// <summary>Modelle, deren Titel oder Livery alle Wörter enthält; doppelte raus, nach Titel und Livery sortiert.</summary>
    public static IReadOnlyList<AiModel> Filter(IReadOnlyList<AiModel> models, string? filter)
    {
        var words = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. models
            .Where(m => words.All(w => m.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Livery.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(m => (m.Title, m.Livery))
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Livery, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Startet einen Testanflug; ein laufender auf derselben Bahn wird vorher beendet. Wirft <see cref="InvalidOperationException"/>
    /// mit einem Text für den Piloten, wenn etwas fehlt (aus, kein Flug, Flughafen oder Bahn unbekannt, zu weit weg, kein Modell,
    /// schon <see cref="MaxConcurrent"/> Tests auf anderen Bahnen).
    /// </summary>
    public async Task<TrafficTestStatus> StartTestAsync(TrafficTestRequest request, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.disabled"));
        }

        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        if (_sim.Status.FlightRunning == false)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.noFlight"));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var airport = request.Airport.Trim().ToUpperInvariant();
            var runways = await _sim.RequestAirportRunwaysAsync(airport, ct);
            if (runways.Count == 0)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.airportUnknown", airport));
            }

            var runway = RunwayEnd.Find(airport, runways, request.Runway);
            if (runway is null)
            {
                var wanted = request.Runway.Trim().ToUpperInvariant();
                var available = string.Join(", ", runways.SelectMany(r => new[] { r.Primary, r.Secondary }).Where(d => d.Length > 0));
                throw new InvalidOperationException(Loc.T("tools.trafficTest.runwayUnknown", wanted, airport, available));
            }

            if (runway.LengthM < TrafficTestPlan.MinRunwayM)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.runwayTooShort", runway.Designation, runway.LengthM));
            }

            if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
            {
                var distance = RouteMath.DistanceNm(lat, lon, runway.ThresholdLatitude, runway.ThresholdLongitude);
                if (distance > MaxDistanceNm)
                {
                    throw new InvalidOperationException(Loc.T("tools.trafficTest.tooFar", distance, MaxDistanceNm));
                }
            }

            // Modell und Flughafenregeln vor dem Ersetzen: passt das Flugzeug nicht zu Platz oder Bahn, bleibt der laufende Test.
            // Ohne Titel wählen die Regeln das Modell (Verkehrsmix, Militärplatz, nur Allgemeine Luftfahrt …)
            var ruleMessages = new List<string>();
            var rules = request.Variant == TrafficTestVariant.C1 ? null : LoadRules(ruleMessages);
            var operation = request.Variant is TrafficTestVariant.B1 or TrafficTestVariant.D1 or TrafficTestVariant.B2 ? TrafficOperation.Departure : TrafficOperation.Arrival;
            var (title, livery) = await ResolveModelAsync(request, rules, runway, operation, ct);
            var modelType = TrafficModels.TypeOf(title, livery);
            var approachKt = request.ApproachKt ?? modelType?.ApproachKt;
            var category = modelType?.Category ?? TrafficCategory.Airliner;
            var violations = rules?.Check(runway, operation, modelType?.IcaoType, category, Math.Clamp(approachKt ?? TrafficTestPlan.DefaultApproachKt, 50, 180))
                ?? [];
            if (violations.Count > 0 && !request.IgnoreRunwayRules)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.runwayRules", runway.Designation, string.Join(" ", violations.Select(v => v.Text))));
            }

            // Rollen: Netz lesen, Parkposition und Route vor dem Ersetzen – fehlt etwas, bleibt der laufende Test
            TaxiGraph? taxiGraph = null;
            TaxiRoute? taxiRoute = null;
            var taxiEvents = new List<string>();
            if (request.Variant is TrafficTestVariant.D1 or TrafficTestVariant.B2)
            {
                taxiGraph = await LoadTaxiGraphAsync(airport, runways, ct);
                var (obstacles, scanError) = await SetTaxiObstaclesAsync(taxiGraph, category, ct);
                taxiEvents.Add(string.Create(CultureInfo.InvariantCulture, $"taxi_obstacles {obstacles} aircraft on the ground, {taxiGraph.BlockedEdges} paths blocked (clearance {taxiGraph.ClearanceM:0} m)"));
                if (scanError is not null)
                {
                    taxiEvents.Add("warning:taxi_obstacles_unknown " + scanError);
                }

                ReserveOwnParkings(taxiGraph);
                taxiRoute = PlanTaxi(taxiGraph, runway, request.Parking, category, TrafficRules.WakeOf(modelType?.IcaoType, category));
                if (taxiGraph.IsOccupied(taxiRoute.Parking))
                {
                    taxiEvents.Add("warning:parking_occupied " + taxiRoute.Parking.Parking?.Label);
                }

                // B2: vom Haltepunkt auf die Bahn und in Startrichtung aufreihen
                if (request.Variant == TrafficTestVariant.B2)
                {
                    taxiRoute = taxiRoute.WithLineUp(taxiGraph, runway, TaxiProfile.For(category));
                    if (taxiRoute.LineUpPath is null)
                    {
                        throw new InvalidOperationException(Loc.T("tools.trafficTest.noLineUp", runway.Designation, taxiRoute.Hold.Taxiway));
                    }

                    taxiEvents.Add(string.Create(CultureInfo.InvariantCulture, $"line_up_planned {taxiRoute.LineUpPath.LengthM:0} m from the hold to {taxiRoute.LineUpAlongM:0} m behind the start of the runway"));
                }
            }

            // A3: Abrollpunkt und Weg zu einer freien Parkposition, bevor der laufende Test ersetzt wird
            if (request.Variant == TrafficTestVariant.A3)
            {
                taxiGraph = await LoadTaxiGraphAsync(airport, runways, ct);
                var (obstacles, scanError) = await SetTaxiObstaclesAsync(taxiGraph, category, ct);
                taxiEvents.Add(string.Create(CultureInfo.InvariantCulture, $"taxi_obstacles {obstacles} aircraft on the ground, {taxiGraph.BlockedEdges} paths blocked (clearance {taxiGraph.ClearanceM:0} m)"));
                if (scanError is not null)
                {
                    taxiEvents.Add("warning:taxi_obstacles_unknown " + scanError);
                }

                ReserveOwnParkings(taxiGraph);
                taxiRoute = PlanTaxiIn(taxiGraph, runway, request.Parking, category, TrafficRules.WakeOf(modelType?.IcaoType, category), approachKt ?? TrafficTestPlan.DefaultApproachKt);
                taxiEvents.Add(string.Create(CultureInfo.InvariantCulture,
                    $"taxi_in_planned exit {taxiRoute.EntryAlongM:0} m ({taxiRoute.Hold.Taxiway}) at {taxiRoute.HandoverKt:0} kt, route {string.Join(' ', taxiRoute.Taxiways)} {taxiRoute.LengthM:0} m to parking {taxiRoute.Parking.Parking?.Label} ({taxiRoute.Parking.Parking?.TypeName})"));
            }

            // Rollen: gekreuzte Bahnen mit Halt davor (A3: ohne die Landebahn, auf der der Weg beginnt)
            if (taxiRoute is not null && taxiGraph is not null)
            {
                var landing = request.Variant == TrafficTestVariant.A3 ? RunwayOf(runways, runway.Designation) : null;
                taxiRoute = taxiRoute.WithCrossings(taxiGraph, TaxiProfile.For(category), landing);
                taxiEvents.Add(taxiRoute.Crossings.Count == 0
                    ? "taxi_crossings none"
                    : "taxi_crossings " + string.Join(", ", taxiRoute.Crossings.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Runway} hold on {c.Taxiway} at {c.StopS:0} m ({c.CenterlineM:0} m from the centerline)"))));
            }

            // Ein Test je Bahn: den alten auf dieser Bahn ersetzen, auf anderen Bahnen höchstens MaxConcurrent. Rollende Tests (D1, B2, A3)
            // ersetzen einander nicht – sie stellen sich an (Abstand beim Rollen, Reihenfolge am Haltepunkt)
            var rolling = request.Variant is TrafficTestVariant.D1 or TrafficTestVariant.B2 or TrafficTestVariant.A3;
            foreach (var old in Running().Where(r => r.SameRunway(runway) && !(rolling && (r.InitialPlan.IsTaxi || r.InitialPlan.Variant == TrafficTestVariant.A3))))
            {
                await StopCoreAsync(old, "replaced");
            }

            var running = Running();
            if (running.Count >= MaxConcurrent)
            {
                throw new InvalidOperationException(Loc.T("tools.trafficTest.tooMany", MaxConcurrent));
            }

            var slot = Enumerable.Range(0, MaxConcurrent).First(i => running.All(r => r.Slot != i));
            double? throttle = request.SpeedMethod == TrafficSpeedMethod.Throttle ? request.ThrottlePercent ?? TrafficTestPlan.DefaultThrottlePercent : null;
            var plan = request.Variant switch
            {
                TrafficTestVariant.B1 => TrafficTestPlan.BuildTakeoff(runway, approachKt, request.RollM, throttle),
                TrafficTestVariant.D1 => TrafficTestPlan.BuildTaxi(runway, approachKt, taxiRoute!, taxiGraph!),
                TrafficTestVariant.B2 => TrafficTestPlan.BuildTaxi(runway, approachKt, taxiRoute!, taxiGraph!, takeoff: true),
                TrafficTestVariant.C1 => TrafficTestPlan.BuildOverflight(runway.Overhead(request.TrackDeg), category, approachKt, request.FlightLevel, request.CruiseKt, request.SpawnNm),
                _ => TrafficTestPlan.Build(runway, request.Variant, request.SpawnNm, request.TouchdownM, request.FinalLowFt, approachKt, throttle),
            };
            if (request.Variant == TrafficTestVariant.A3)
            {
                plan = plan with { Taxi = taxiRoute };
            }
            var spawnUtc = DateTimeOffset.UtcNow;
            var csv = OpenCsv(plan, spawnUtc);

            uint objectId;
            try
            {
                objectId = await _sim.CreateNonAtcAircraftAsync(title, livery, Tail(slot), plan.Spawn, ct);
            }
            catch
            {
                csv?.Writer.Dispose();
                throw;
            }

            var run = new TrafficTestRun(plan, slot, title, livery, request.Gear, objectId, spawnUtc, csv?.Path, csv?.Writer, _options.TrafficTest.MaxMinutes)
            {
                SpeedMethod = request.SpeedMethod,
                GoAroundEnabled = request.GoAround,
                KinematicNm = plan.Variant == TrafficTestVariant.A3 ? Math.Clamp(plan.SpawnNm, MinKinematicNm, MaxKinematicNm)
                    : plan.IsTakeoff || plan.IsOverflight || request.KinematicNm is null ? null : Math.Clamp(request.KinematicNm.Value, MinKinematicNm, MaxKinematicNm),
                KinematicTakeoff = plan.IsTakeoff && request.KinematicTakeoff,
                Category = category,
                Lights = plan.IsOverflight ? TrafficLights.Cruise : plan.IsTaxi ? TrafficLights.Rollout : TrafficLights.Approach,
                TaxiGraph = taxiGraph,
                FullModel = !title.Contains("Passive", StringComparison.OrdinalIgnoreCase),
            };
            run.AddEvent(0, plan.IsTaxi && plan.Taxi is { } taxi && taxiGraph is not null
                ? string.Create(CultureInfo.InvariantCulture, $"spawn at parking {taxi.Parking.Parking?.Label} ({taxi.Parking.Parking?.TypeName}, {taxi.Parking.Parking?.RadiusM:0} m), route {string.Join(' ', taxi.Taxiways)} {taxi.LengthM:0} m to the hold on {taxi.Hold.Taxiway} ({taxi.Hold.Runway}), pushback {(taxi.Pushback ? "yes" : "no")}, net {taxiGraph.Nodes.Count} nodes, {taxiGraph.Edges.Count} paths, mapping {taxiGraph.Mapping} ({taxiGraph.MappingErrorM:0.0} m), object {objectId}, {run.Tail}")
                : plan.IsTakeoff
                ? string.Create(CultureInfo.InvariantCulture, $"spawn on the runway {TrafficTestPlan.LineUpM:0} m behind its start, takeoff speed {plan.TakeoffKt:0} kt, object {objectId}, {run.Tail}")
                : plan.IsOverflight
                ? string.Create(CultureInfo.InvariantCulture, $"spawn {plan.SpawnNm:0.#} NM before overhead, {plan.CruiseAltitudeFt:0} ft, track {plan.Runway.HeadingTrueDeg:000}, {plan.CruiseKt:0} kt tas (about {plan.CruiseIasKt:0} ias), object {objectId}, {run.Tail}")
                : string.Create(CultureInfo.InvariantCulture, $"spawn {plan.SpawnNm:0.#} NM, {plan.Spawn.AltitudeFt:0} ft, {plan.Spawn.AirspeedKt} kt, object {objectId}, {run.Tail}"));
            run.AddEvent(0, "speed_method " + SpeedMethodName(request.SpeedMethod)
                + (plan.ThrottlePercent is { } t ? string.Create(CultureInfo.InvariantCulture, $" {t:0} %") : ""));
            foreach (var warning in plan.Warnings)
            {
                run.AddEvent(0, "warning:" + warning);
            }

            foreach (var taxiEvent in taxiEvents)
            {
                run.AddEvent(0, taxiEvent);
            }

            foreach (var violation in violations)
            {
                run.AddEvent(0, "warning:runway_rules_ignored " + violation.Code + ": " + violation.Text);
            }

            foreach (var message in ruleMessages)
            {
                run.AddEvent(0, "warning:runway_rules_file " + message);
            }

            // Gesperrte (FBW, Headwind) und nicht freigegebene Modelle gehen im Test trotzdem, aber mit Hinweis
            if (TrafficModels.ExcludedBy(title, livery) is { } excludedBy)
            {
                run.AddEvent(0, "warning:excludedModel " + excludedBy);
            }
            else if (TrafficModels.TypeOf(title, livery) is null)
            {
                run.AddEvent(0, "warning:notApproved");
            }

            lock (_lock)
            {
                _runs.RemoveAll(r => r.EndReason is not null && r.SameRunway(runway));
                _runs.Add(run);
                var ended = _runs.Where(r => r.EndReason is not null).ToList();
                foreach (var stale in ended.Take(Math.Max(0, ended.Count - KeepEnded)))
                {
                    _runs.Remove(stale);
                }
            }

            try
            {
                if (plan.GearAtSpawn)
                {
                    await _sim.SetAiGearAsync(objectId, true, request.Gear, ct);
                    run.GearCommands++;
                    run.AddEvent(0, "gear_down_sent " + GearName(request.Gear));
                }

                await _sim.SetAiLightsAsync(objectId, run.Lights, ct);
                run.AddEvent(0, plan.IsOverflight ? "lights_sent cruise" : plan.IsTaxi ? "lights_sent taxi" : "lights_sent approach");

                // Rollen: keine Wegpunkte – Duopilo schiebt das Flugzeug ab dem ersten Messpunkt selbst
                if (!plan.IsTaxi)
                {
                    await _sim.SetAiWaypointsAsync(objectId, [.. plan.Waypoints.Select(w => w.Waypoint)], ct);
                    run.AddEvent(0, string.Create(CultureInfo.InvariantCulture, $"waypoints_sent {plan.Waypoints.Count}"));
                }
                _sim.StartAiProbe(objectId, OnSample);
                if (request.SpeedMethod == TrafficSpeedMethod.Velocity)
                {
                    run.VelocityTimer = new System.Threading.Timer(_ => VelocityTick(run), null, VelocityPeriod, VelocityPeriod);
                }
            }
            catch
            {
                // Halb eingerichtetes Flugzeug nicht stehen lassen
                await StopCoreAsync(run, "setup_failed");
                throw;
            }

            _log.LogInformation(Loc.Log("log.trafficTest.started"), plan.Variant, airport, runway.Designation, title, objectId);
            lock (_lock)
            {
                return Status(run);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Beendet laufende Tests: Abfrage stoppen, Flugzeug entfernen, Auswertung schreiben. Mit <paramref name="runway"/> nur die Tests
    /// auf dieser Bahn ("05L" oder "EDDL 05L"), mit <paramref name="tail"/> nur den mit dieser Kennung ("DUO02"), sonst alle. Gibt den
    /// Stand aller Tests zurück.
    /// </summary>
    public async Task<IReadOnlyList<TrafficTestStatus>> StopTestAsync(string reason, CancellationToken ct, string? runway = null, string? tail = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var run in Running().Where(r => r.Matches(runway) && r.HasTail(tail)))
            {
                await StopCoreAsync(run, reason);
            }

            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Beendet genau diesen Test (Ende aus der Auswertung: gestartet, am Haltepunkt, geparkt …). Andere Tests derselben Bahn – die
    /// Warteschlange – laufen weiter.
    /// </summary>
    private async Task StopRunAsync(TrafficTestRun run, string reason)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await StopCoreAsync(run, reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Alle Tests (laufende und die zuletzt beendeten), in Startreihenfolge; mit <paramref name="runway"/> nur die dieser Bahn.</summary>
    public IReadOnlyList<TrafficTestStatus> Status(string? runway = null)
    {
        lock (_lock)
        {
            return [.. _runs.Where(r => r.Matches(runway)).Select(Status)];
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StopTestAsync("shutdown", cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            _log.LogDebug(ex, Loc.Log("log.trafficTest.actionFailed"), "shutdown", ex.Message);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Name der Speed-Methode wie im Tool-Parameter (off, desired, throttle, velocity).</summary>
    public static string SpeedMethodName(TrafficSpeedMethod method) => method.ToString().ToLowerInvariant();

    /// <summary>Name der Fahrwerks-Methode wie im Tool-Parameter (all, down, set, handle).</summary>
    public static string GearName(AiGearMethod method) => method.ToString().ToLowerInvariant();

    /// <summary>
    /// Standardmodell: ein A320 aus dem FSLTL-Paket, sonst ein geprüftes Modell der Freigabeliste (in deren Reihenfolge), sonst
    /// irgendein A320 – nie ein gesperrtes (<see cref="TrafficModels"/>).
    /// </summary>
    public static AiModel? PickDefaultModel(IReadOnlyList<AiModel> models)
    {
        var usable = models.Where(m => !TrafficModels.IsExcluded(m.Title, m.Livery)).ToList();
        var verified = TrafficModels.Approved
            .Where(t => t.Status == TrafficModelStatus.Verified)
            .SelectMany(t => t.Titles)
            .Select(title => usable.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null);
        return usable.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase) && m.Title.Contains("FSLTL", StringComparison.OrdinalIgnoreCase))
            ?? verified
            ?? usable.FirstOrDefault(m => m.Title.Contains("A320", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ intern

    private List<TrafficTestRun> Running()
    {
        lock (_lock)
        {
            return [.. _runs.Where(r => r.EndReason is null)];
        }
    }

    private TrafficTestStatus Status(TrafficTestRun run)
    {
        var now = DateTimeOffset.UtcNow;
        string? note = null;
        if (run.EndReason is null && (now - (run.LastSampleUtc ?? run.SpawnUtc)) > NoDataAfter)
        {
            note = Loc.T("tools.trafficTest.noData", (int)NoDataAfter.TotalSeconds);
        }

        var plan = run.Analysis.Plan;
        return new TrafficTestStatus(
            run.EndReason is null ? "running" : "ended",
            run.Tail,
            run.InitialPlan.Variant.ToString(),
            plan.Runway.Airport,
            plan.Runway.Designation,
            run.Title,
            run.Livery.Length > 0 ? run.Livery : null,
            TrafficModels.TypeOf(run.Title, run.Livery)?.IcaoType,
            TrafficModels.StatusName(run.Title, run.Livery),
            GearName(run.GearMethod),
            plan.FinalLowFt,
            plan.ApproachKt,
            SpeedMethodName(run.SpeedMethod),
            plan.ThrottlePercent,
            run.GoAroundEnabled,
            run.ObjectId,
            run.SpawnUtc,
            Math.Round(((run.EndedUtc ?? now) - run.SpawnUtc).TotalSeconds),
            run.EndReason,
            run.Last,
            run.Analysis.Summary(),
            [.. run.Events],
            [.. plan.Waypoints, .. (run.Analysis.GoAroundPlan?.Waypoints ?? [])],
            run.CsvPath,
            run.SummaryPath,
            note,
            run.KinematicNm,
            run.Kinematics is { } k
                ? new TrafficTestKinematics(
                    JsonNamingPolicy.SnakeCaseLower.ConvertName(k.Phase.ToString()),
                    Math.Round(k.StartAlongM),
                    Math.Round(k.StartHeightFt),
                    Math.Round(k.StartSpeedKt),
                    k.FlareAlongM is { } flare ? Math.Round(flare) : null,
                    k.Touchdown is { } td ? Math.Round(td.AlongM) : null,
                    k.Touchdown is { } tdKt ? Math.Round(tdKt.SpeedKt) : null,
                    k.Touchdown is { } tdSink ? Math.Round(tdSink.SinkFpm) : null,
                    Math.Round(k.GroundFt, 1),
                    Math.Round(k.CgHeightFt, 1),
                    Math.Round(k.GroundPitchDeg, 1),
                    k.IsTakeoff || k.Liftoff is not null ? Math.Round(k.RotateKt) : null,
                    k.IsTakeoff || k.Liftoff is not null ? Math.Round(k.ClimbKt) : null,
                    k.IsTakeoff || k.Liftoff is not null ? Math.Round(k.TakeoffAccelMps2, 2) : null,
                    k.RotateAlongM is { } rotate ? Math.Round(rotate) : null,
                    k.Liftoff is { } lift ? Math.Round(lift.AlongM) : null,
                    k.Liftoff is { } liftKt ? Math.Round(liftKt.SpeedKt) : null,
                    k.Liftoff is { } liftPitch ? Math.Round(liftPitch.PitchDeg, 1) : null)
                : null,
            run.InitialPlan.Taxi is { } route
                ? new TrafficTestTaxi(
                    run.InitialPlan.Variant == TrafficTestVariant.A3 && !run.TaxiActive ? "landing"
                        : run.TaxiKinematics is { } t ? JsonNamingPolicy.SnakeCaseLower.ConvertName(t.Phase.ToString()) : "parked",
                    route.Parking.Parking?.Label ?? "",
                    route.Parking.Parking?.TypeName ?? "",
                    route.Hold.Runway,
                    route.Hold.Taxiway,
                    route.Taxiways,
                    route.LengthM,
                    route.Pushback,
                    route.PushbackPath is { } push ? Math.Round(push.LengthM) : null,
                    Math.Round(route.TaxiPath.LengthM),
                    route.TaxiPath.Turns,
                    route.TaxiPath.MinRadiusM is { } radius ? Math.Round(radius, 1) : null,
                    Math.Round(run.TaxiKinematics?.TotalM ?? 0),
                    Math.Round(run.TaxiKinematics?.SpeedKt ?? 0, 1),
                    Math.Round(run.TaxiKinematics?.MaxSpeedKt ?? 0, 1),
                    run.TaxiGraph?.Mapping ?? "",
                    run.TaxiGraph?.MappingErrorM,
                    run.InitialPlan.TakeoffAfterTaxi ? route.LineUpAlongM : null,
                    route.HandoverAlongM is not null ? route.EntryAlongM : null,
                    route.HandoverAlongM is not null ? route.HandoverKt : null,
                    [.. route.Crossings.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Runway} on {c.Taxiway} at {c.StopS:0} m"))],
                    run.TaxiKinematics?.CrossingsDone ?? 0,
                    run.BlockedBy is { } by ? _runs.FirstOrDefault(r => r.ObjectId == by)?.Tail : null)
                : null);
    }

    private async Task<(string Title, string Livery)> ResolveModelAsync(
        TrafficTestRequest request,
        TrafficRules? rules,
        RunwayEnd runway,
        TrafficOperation operation,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            return (request.Title.Trim(), request.Livery?.Trim() ?? "");
        }

        IReadOnlyList<AiModel> models;
        try
        {
            models = await _sim.EnumerateAircraftModelsAsync(ct);
        }
        catch (NotSupportedException)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.titleRequired"));
        }

        if (rules is null)
        {
            var pick = PickDefaultModel(models) ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.noModel"));
            return (pick.Title, pick.Livery);
        }

        var fitting = rules.PickModel(models, runway, operation, Random.Shared, PickDefaultModel(models))
            ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.noFittingModel", runway.Airport, runway.Designation));
        return (fitting.Title, fitting.Livery);
    }

    /// <summary>Messpunkt vom SimConnect-Thread: auswerten, ins CSV schreiben, Fahrwerk und Zeitlimit.</summary>
    private void OnSample(AiProbeSample sample)
    {
        TrafficTestRun run;
        TrafficTestPoint point;
        lock (_lock)
        {
            if (_runs.FirstOrDefault(r => r.EndReason is null && r.ObjectId == sample.ObjectId) is not { } current)
            {
                return;
            }

            run = current;

            // Kinematik: Übernahme ab KinematicNm vor der Schwelle; danach liefert sie Fahrt, Sinkrate, Neigung und "am Boden" –
            // eingefroren meldet der Sim dafür nichts Brauchbares
            StartKinematicsIfDue(run, sample);
            if (run.Kinematics is { } kin && !run.TaxiActive)
            {
                CorrectGround(run, kin, sample);
                sample = sample with
                {
                    OnGround = kin.OnGround,
                    IndicatedKt = kin.SpeedKt,
                    GroundSpeedKt = kin.OnGround ? kin.SpeedKt : TrafficTestPlan.TrueAirspeedKt(kin.SpeedKt, sample.AltitudeFt),
                    VerticalSpeedFpm = kin.VerticalSpeedFpm,
                    PitchDeg = kin.PitchDeg,
                };
            }
            else if (run.TaxiKinematics is { } taxi)
            {
                if (sample.AltitudeAglFt > 0 && sample.AltitudeAglFt < 300)
                {
                    taxi.GroundTargetFt = sample.AltitudeFt - sample.AltitudeAglFt;
                }

                sample = sample with
                {
                    OnGround = true,
                    IndicatedKt = taxi.SpeedKt,
                    GroundSpeedKt = taxi.SpeedKt,
                    VerticalSpeedFpm = 0,
                    PitchDeg = taxi.PitchDeg,
                };
            }

            point = run.Analysis.Add(sample);
            run.LastSampleUtc = sample.Utc;
            run.Last = new TrafficTestLast(
                point.Seconds,
                Math.Round(sample.AltitudeFt),
                Math.Round(sample.AltitudeAglFt),
                Math.Round(sample.IndicatedKt),
                point.TargetKt,
                Math.Round(sample.GroundSpeedKt),
                Math.Round(sample.VerticalSpeedFpm),
                Math.Round(sample.PitchDeg, 1),
                Math.Round(sample.BankDeg, 1),
                sample.OnGround,
                Math.Round(sample.GearExtendedPercent),
                point.DistanceToThresholdNm,
                Math.Round(point.AlongM),
                Math.Round(point.CrossM, 1),
                sample.AiState,
                AiLights.Describe(sample.LightStates),
                Math.Round(sample.DesiredKt));
            foreach (var e in point.Events)
            {
                run.AddEvent(point.Seconds, e);
            }

            WriteCsv(run, sample, point);
        }

        var plan = run.Analysis.Plan;
        var heightFt = sample.AltitudeFt - plan.Runway.ElevationFt;

        // Durchstarten: zu hoch oder zu schnell über der Schwelle, oder zu lange nicht aufgesetzt – neue Wegpunkte geradeaus,
        // Steigflug auf 3000 ft, schneller werden; das Fahrwerk kommt bei positiver Steigrate rein (nicht mit Kinematik: die fliegt den Pfad)
        if (run.GoAroundEnabled && !plan.IsTakeoff && !plan.IsOverflight && !plan.IsTaxi && run.Kinematics is null && run.Analysis.GoAroundPlan is null
            && TrafficGoAround.Check(plan, run.PreviousAlongM, point.AlongM, sample.AltitudeFt, sample.IndicatedKt, sample.OnGround, run.Analysis.TouchedDown) is { } reason)
        {
            var goAround = TrafficGoAroundPlan.Build(plan, point.AlongM);
            lock (_lock)
            {
                run.Analysis.MarkGoAround(point.Seconds, point.AlongM, Math.Max(0, heightFt), sample.IndicatedKt, reason, goAround);
            }

            run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"go_around {reason.Text} at {heightFt:0} ft, {point.AlongM:0} m"));
            _log.LogInformation(Loc.Log("log.trafficTest.goAround"), plan.Runway.Designation, reason.Text);
            Fire(_sim.SetAiWaypointsAsync(run.ObjectId, [.. goAround.Waypoints.Select(w => w.Waypoint)], CancellationToken.None), "go_around");
            if (run.SpeedMethod is TrafficSpeedMethod.Desired or TrafficSpeedMethod.Velocity)
            {
                var kt = goAround.TargetKt(point.AlongM);
                run.DesiredSentKt = kt;
                run.DesiredSentSeconds = point.Seconds;
                run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"speed_target {kt:0} kt (ias {sample.IndicatedKt:0})"));
                Fire(_sim.SetAiDesiredSpeedAsync(run.ObjectId, kt, CancellationToken.None), "speed");
            }
        }

        run.PreviousAlongM = point.AlongM;

        if (run.Analysis.GoAroundSeconds is { } goAroundSeconds && !run.Analysis.TouchedDown)
        {
            if (!run.GearUpSent && !sample.OnGround && sample.VerticalSpeedFpm >= TrafficGoAroundPlan.PositiveClimbFpm)
            {
                run.GearUpSent = true;
                run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"gear_up_sent {GearName(run.GearMethod)} at {heightFt:0} ft"));
                Fire(_sim.SetAiGearAsync(run.ObjectId, false, run.GearMethod, CancellationToken.None), "gear");
            }

            var reached = heightFt >= TrafficGoAroundPlan.EndFt;
            if ((reached || point.Seconds >= goAroundSeconds + TrafficGoAroundPlan.TimeoutSeconds) && !run.StopScheduled)
            {
                run.StopScheduled = true;
                _ = Task.Run(() => StopRunAsync(run, reached ? "go_around" : "go_around_timeout"));
            }
        }

        // Rollen (D1): eine Minute am Haltepunkt stehen, dann Ende
        if (plan.IsTaxi && !plan.TakeoffAfterTaxi && run.TaxiKinematics is { Phase: TaxiPhase.Holding })
        {
            run.HoldingSinceSeconds ??= point.Seconds;
            if (point.Seconds - run.HoldingSinceSeconds.Value >= HoldingEndSeconds && !run.StopScheduled)
            {
                run.StopScheduled = true;
                _ = Task.Run(() => StopRunAsync(run, "holding"));
            }
        }

        // A3: eine Minute an der Parkposition stehen, dann Ende
        if (run.InitialPlan.Variant == TrafficTestVariant.A3 && run.TaxiActive && run.TaxiKinematics is { Phase: TaxiPhase.Holding })
        {
            run.HoldingSinceSeconds ??= point.Seconds;
            if (point.Seconds - run.HoldingSinceSeconds.Value >= HoldingEndSeconds && !run.StopScheduled)
            {
                run.StopScheduled = true;
                _ = Task.Run(() => StopRunAsync(run, "parked"));
            }
        }

        // Rollen: vor einer gekreuzten Bahn und vor dem Aufreihen (B2) erst weiter, wenn die Bahn frei ist
        if (run.TaxiKinematics is { CheckRunway: true } held && (run.InitialPlan.Variant != TrafficTestVariant.A3 || run.TaxiActive))
        {
            UpdateTaxiSpacing(run, held, point.Seconds);
            CheckRunwayWhileHolding(run, held, point.Seconds);
        }

        // Überflug (C1): Ende hinter dem Ausflugpunkt
        if (plan.IsOverflight && point.AlongM >= plan.ExitM && !run.StopScheduled)
        {
            run.StopScheduled = true;
            _ = Task.Run(() => StopRunAsync(run, "passed"));
        }

        // Start (B1): Fahrwerk rein bei positiver Steigrate; Ende bei 2800 ft, am Boden über das Bahnende oder ohne Losrollen
        if (plan.IsTakeoff)
        {
            // Mit Kinematik zählt die Höhe der Räder (der Bezugspunkt liegt bei der A400M schon am Boden 19 ft hoch)
            var wheelsFt = run.Kinematics?.HeightFt ?? heightFt;
            if (run.Analysis.LiftedOff && !run.GearUpSent && !sample.OnGround && wheelsFt >= TakeoffGearUpFt
                && sample.VerticalSpeedFpm >= TrafficGoAroundPlan.PositiveClimbFpm)
            {
                run.GearUpSent = true;
                run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"gear_up_sent {GearName(run.GearMethod)} at {heightFt:0} ft"));
                Fire(_sim.SetAiGearAsync(run.ObjectId, false, run.GearMethod, CancellationToken.None), "gear");
            }

            var end = run.Analysis.LiftedOff && heightFt >= TrafficTestPlan.ClimbEndFt ? "climbed"
                : !run.Analysis.LiftedOff && sample.OnGround && point.AlongM > plan.Runway.LengthM ? "no_liftoff"
                : !run.Analysis.RollStarted && point.Seconds - run.TakeoffStartedSeconds >= NoRollSeconds ? "no_roll"
                : null;
            if (end is not null && !run.StopScheduled)
            {
                run.StopScheduled = true;
                _ = Task.Run(() => StopRunAsync(run, end));
            }
        }

        // A2: Fahrwerk erst im Anflug ausfahren – bei 3000 ft über der Bahn, spätestens am Punkt des Gleitpfads in dieser Höhe
        if (plan.GearDownFt is { } gearFt && run.GearCommands == 0 && !sample.OnGround && run.Analysis.GoAroundPlan is null
            && (heightFt <= gearFt || (plan.GearDownNm is { } gearNm && point.DistanceToThresholdNm is { } d && d <= gearNm)))
        {
            run.GearCommands++;
            run.GearSentSeconds = point.Seconds;
            var distance = point.DistanceToThresholdNm ?? 0;
            run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"gear_down_sent {GearName(run.GearMethod)} at {heightFt:0} ft, {distance:0.0} NM"));
            Fire(_sim.SetAiGearAsync(run.ObjectId, true, run.GearMethod, CancellationToken.None), "gear");
        }

        // Fährt das Fahrwerk nach dem Befehl nicht aus, bis zu dreimal im Abstand von 3 s nachschicken (nur in der Luft, nicht beim Durchstarten)
        if (run.GearCommands is > 0 and < 4 && !sample.OnGround && sample.GearExtendedPercent < 50 && run.Analysis.GoAroundPlan is null && !run.GearUpSent
            && point.Seconds >= run.GearSentSeconds + (3 * run.GearCommands))
        {
            run.GearCommands++;
            run.AddEvent(point.Seconds, "gear_down_resent " + GearName(run.GearMethod));
            Fire(_sim.SetAiGearAsync(run.ObjectId, true, run.GearMethod, CancellationToken.None), "gear");
        }

        // Geschwindigkeit: die KI beachtet die Wegpunkt-Geschwindigkeit nicht – Wunschgeschwindigkeit des nächsten Wegpunkts setzen,
        // bei Änderung sofort, sonst alle 5 s (in der Luft bis zum Aufsetzen; hinter der Schwelle nicht unter Vapp − 5; nach dem
        // Durchstarten die Wegpunkte des Durchstartens). Velocity setzt zusätzlich VELOCITY BODY Z (VelocityTick, 5 Hz).
        var targetKt = run.Analysis.GoAroundPlan?.TargetKt(point.AlongM) ?? plan.AirborneTargetKt(point.AlongM);
        var airborne = !sample.OnGround && !run.Analysis.TouchedDown && (!plan.IsTakeoff || run.Analysis.LiftedOff);
        if (run.SpeedMethod == TrafficSpeedMethod.Velocity && run.Kinematics is null)
        {
            var tasKt = TrafficTestPlan.TrueAirspeedKt(targetKt, sample.AltitudeFt);
            if (airborne && run.VelocityFtS is null)
            {
                run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"velocity_target {targetKt:0} kt (tas {tasKt:0}, ias {sample.IndicatedKt:0})"));
            }

            run.VelocityFtS = airborne ? tasKt * FeetPerSecondPerKnot : null;
        }

        if (run.SpeedMethod is (TrafficSpeedMethod.Desired or TrafficSpeedMethod.Velocity) && airborne && run.Kinematics is null
            && (run.DesiredSentKt != targetKt || point.Seconds >= run.DesiredSentSeconds + 5))
        {
            if (run.DesiredSentKt != targetKt)
            {
                run.AddEvent(point.Seconds, string.Create(CultureInfo.InvariantCulture, $"speed_target {targetKt:0} kt (ias {sample.IndicatedKt:0})"));
            }

            run.DesiredSentKt = targetKt;
            run.DesiredSentSeconds = point.Seconds;
            Fire(_sim.SetAiDesiredSpeedAsync(run.ObjectId, targetKt, CancellationToken.None), "speed");
        }

        // Lichter einmal nachschicken, falls das Modell sie beim Spawn noch nicht angenommen hat
        if (!run.LightsChecked && point.Seconds >= 5 && !sample.OnGround)
        {
            run.LightsChecked = true;
            if (!run.Lights.MatchedBy(sample.LightStates))
            {
                run.AddEvent(point.Seconds, "lights_resent " + (run.Analysis.Plan.IsOverflight ? "cruise" : "approach") + " (were: " + AiLights.Describe(sample.LightStates) + ")");
                Fire(_sim.SetAiLightsAsync(run.ObjectId, run.Lights, CancellationToken.None), "lights");
            }
        }

        // Volle Modelle (kein PassiveAircraft-Dummy): Der Sim meldet die Lichter gleich als an, sichtbar wurden sie beim A400M aber erst
        // spät (beta.18 nach einiger Zeit, beta.19 in Wunstorf trotz Nachschicken nach 10/20/30 s erst kurz vor der Bahn) – deshalb alle
        // 15 s noch einmal senden, bis zum Aufsetzen
        if (run.FullModel && !run.RolloutLightsSent && !run.Analysis.TouchedDown
            && point.Seconds >= FullModelLightResendSeconds * (run.FullModelLightResends + 1))
        {
            run.FullModelLightResends++;
            run.AddEvent(point.Seconds, "lights_resent full_model");
            Fire(_sim.SetAiLightsAsync(run.ObjectId, run.Lights, CancellationToken.None), "lights");
        }

        // Nach dem Aufsetzen beim Abbremsen: Landelicht und Strobes aus, Taxilicht an
        if (!run.RolloutLightsSent && run.Analysis.TouchedDown && sample.OnGround && sample.GroundSpeedKt < TrafficLights.RolloutBelowKt)
        {
            run.RolloutLightsSent = true;
            run.RolloutLightsSeconds = point.Seconds;
            run.AddEvent(point.Seconds, "lights_sent rollout");
            Fire(_sim.SetAiLightsAsync(run.ObjectId, TrafficLights.Rollout, CancellationToken.None), "lights");
        }

        // Im Schlafzustand nach der Landung schaltet die KI alle Lichter aus (beta.14) – bis zu dreimal im Abstand von 5 s nachschicken,
        // nach dem Wechsel in den Schlafzustand noch einmal dreimal. Nur Nav und Beacon zählen: Ein Taxilicht haben die PassiveAircraft nicht
        if (sample.AiState != run.LastAiState)
        {
            if (sample.AiState?.Contains("SLEEP", StringComparison.OrdinalIgnoreCase) == true)
            {
                run.RolloutLightResends = 0;
            }

            run.LastAiState = sample.AiState;
        }

        if (run.RolloutLightsSent && run.RolloutLightResends < 3 && point.Seconds >= run.RolloutLightsSeconds + 5
            && !TrafficLights.RolloutRequired.MatchedBy(sample.LightStates))
        {
            run.RolloutLightResends++;
            run.RolloutLightsSeconds = point.Seconds;
            run.AddEvent(point.Seconds, "lights_resent rollout (were: " + AiLights.Describe(sample.LightStates) + ", " + (sample.AiState ?? "") + ")");
            Fire(_sim.SetAiLightsAsync(run.ObjectId, TrafficLights.Rollout, CancellationToken.None), "lights");
        }

        if (point.Seconds > run.MaxMinutes * 60 && !run.StopScheduled)
        {
            run.StopScheduled = true;
            _ = Task.Run(() => StopRunAsync(run, "timeout"));
        }
    }

    /// <summary>
    /// Übernimmt das Flugzeug, sobald es <see cref="TrafficTestRun.KinematicNm"/> vor der Schwelle ist (in der Luft, nicht
    /// durchgestartet): Kinematik aus dem Messpunkt starten, dann setzt der Client die Lage jeden Frame.
    /// </summary>
    private void StartKinematicsIfDue(TrafficTestRun run, AiProbeSample sample)
    {
        if (run.InitialPlan.IsTaxi)
        {
            if (run.TaxiKinematics is null)
            {
                StartTaxiKinematics(run, sample);
            }
            else if (run.InitialPlan.TakeoffAfterTaxi && run.Kinematics is null && run.TaxiKinematics.Phase == TaxiPhase.LinedUp)
            {
                StartTakeoffAfterTaxi(run, sample);
            }

            return;
        }

        if (run.KinematicTakeoff)
        {
            if (run.Kinematics is null)
            {
                StartTakeoffKinematics(run, sample);
            }

            return;
        }

        if (run.KinematicNm is not { } nm || run.Kinematics is not null || sample.OnGround || run.Analysis.GoAroundPlan is not null
            || run.Analysis.TouchedDown)
        {
            return;
        }

        var plan = run.Analysis.Plan;
        var (along, cross) = plan.Runway.Offsets(sample.Latitude, sample.Longitude);
        if (along < -nm * RunwayEnd.MetersPerNm)
        {
            return;
        }

        var cg = sample.CgHeightFt > 0.5 ? sample.CgHeightFt : DefaultCgHeightFt;
        var ground = plan.Runway.ElevationFt;
        // A3 übernimmt gleich am Spawn – meldet der Sim dort noch keine Fahrt, mit der Spawn-Geschwindigkeit (Vapp + 5) weiter
        var speedKt = sample.IndicatedKt >= plan.ApproachKt * 0.7 ? sample.IndicatedKt : plan.ApproachKt + 5;
        var kin = new TrafficKinematics(plan, KinematicProfile.For(run.Category), along, cross, sample.AltitudeFt - ground - cg, speedKt, sample.PitchDeg, ground, cg)
        {
            GroundPitchDeg = GroundPitch(sample.StaticPitchDeg),
        };
        run.Kinematics = kin;

        // A3: am Abrollpunkt mit Rollgeschwindigkeit, dort übernimmt das Rollen (im selben Frame, ohne Halt)
        TaxiKinematics? after = null;
        if (run.InitialPlan.Variant == TrafficTestVariant.A3 && plan.Taxi is { HandoverAlongM: { } handover } route && run.TaxiGraph is { } graph)
        {
            kin.ExitAlongM = handover;
            kin.ExitKt = route.HandoverKt;
            after = new TaxiKinematics(null, route.TaxiPath, graph.ToLatLon, ground, cg, kin.GroundPitchDeg, rolling: route.HandoverKt, crossingStops: [.. route.Crossings.Select(c => c.StopS)])
            {
                CheckRunway = true,
            };
            run.TaxiKinematics = after;
        }

        _sim.StartAiPose(run.ObjectId, dt =>
        {
            if (after is not null && run.TaxiActive)
            {
                after.Advance(dt);
                return after.Pose();
            }

            kin.Advance(dt);
            if (after is not null && kin.Phase == KinematicPhase.Exited)
            {
                after.GroundFt = kin.GroundFt;
                run.TaxiActive = true;
                return after.Pose();
            }

            return kin.Pose();
        });
        var seconds = Math.Max(0, (sample.Utc - run.SpawnUtc).TotalSeconds);
        run.AddEvent(Math.Round(seconds, 1), string.Create(CultureInfo.InvariantCulture,
            $"kinematic_start at {-along / RunwayEnd.MetersPerNm:0.0} NM, {kin.StartHeightFt:0} ft, {sample.IndicatedKt:0} kt (cg {cg:0.0} ft, static pitch {sample.StaticPitchDeg:0.0}°)"));
    }

    /// <summary>
    /// Rollen (D1): Duopilo übernimmt mit dem ersten Messpunkt und schiebt das Flugzeug über die Wege der Route (Pushback, Rollen).
    /// Boden gemessen unter dem Flugzeug, sonst die Bahnhöhe.
    /// </summary>
    private void StartTaxiKinematics(TrafficTestRun run, AiProbeSample sample)
    {
        var plan = run.InitialPlan;
        if (plan.Taxi is not { } route || run.TaxiGraph is not { } graph)
        {
            return;
        }

        var cg = sample.CgHeightFt > 0.5 ? sample.CgHeightFt : DefaultCgHeightFt;
        var measured = sample.AltitudeFt - sample.AltitudeAglFt;
        var ground = sample.AltitudeAglFt > 0 && Math.Abs(measured - plan.Runway.ElevationFt) < MaxGroundCorrectionFt ? measured : plan.Runway.ElevationFt;
        var taxi = new TaxiKinematics(route.PushbackPath, route.TaxiPath, graph.ToLatLon, ground, cg, GroundPitch(sample.StaticPitchDeg), plan.TakeoffAfterTaxi ? route.LineUpPath : null, crossingStops: [.. route.Crossings.Select(c => c.StopS)])
        {
            CheckRunway = true,
        };
        run.TaxiKinematics = taxi;
        _sim.StartAiPose(run.ObjectId, dt =>
        {
            taxi.Advance(dt);
            return taxi.Pose();
        });
        var seconds = Math.Max(0, (sample.Utc - run.SpawnUtc).TotalSeconds);
        run.AddEvent(Math.Round(seconds, 1), string.Create(CultureInfo.InvariantCulture,
            $"kinematic_taxi pushback {route.PushbackPath?.LengthM ?? 0:0} m, taxi {route.TaxiPath.LengthM:0} m, {route.TaxiPath.Turns} turns (cg {cg:0.0} ft, static pitch {sample.StaticPitchDeg:0.0}°)"));
    }

    /// <summary>
    /// Abflug vom Gate (B2): Das Flugzeug steht aufgereiht auf der Bahn – ab hier fliegt die Start-Kinematik (<see cref="TrafficKinematics.Takeoff"/>)
    /// von dieser Stelle aus, die Auswertung wechselt auf die eines Starts (B1), die Lichter auf Start (Landelicht, Strobes).
    /// </summary>
    private void StartTakeoffAfterTaxi(TrafficTestRun run, AiProbeSample sample)
    {
        var plan = run.InitialPlan;
        if (run.TaxiKinematics is not { } taxi || run.TaxiGraph is not { } graph)
        {
            return;
        }

        var takeoffPlan = TrafficTestPlan.BuildTakeoff(plan.Runway, plan.ApproachKt);
        var (lat, lon) = graph.ToLatLon(taxi.EastM, taxi.NorthM);
        var (along, cross) = plan.Runway.Offsets(lat, lon);
        var kin = TrafficKinematics.Takeoff(takeoffPlan, KinematicProfile.For(run.Category), Math.Max(0, along), cross, 0, taxi.GroundFt, taxi.CgHeightFt, taxi.PitchDeg);
        var seconds = Math.Round(Math.Max(0, (sample.Utc - run.SpawnUtc).TotalSeconds), 1);
        run.Kinematics = kin;
        run.Analysis = new TrafficTestAnalysis(takeoffPlan, run.SpawnUtc);
        run.TakeoffStartedSeconds = seconds;
        run.Lights = TrafficLights.Approach;
        _sim.StartAiPose(run.ObjectId, dt =>
        {
            kin.Advance(dt);
            return kin.Pose();
        });
        Fire(_sim.SetAiLightsAsync(run.ObjectId, run.Lights, CancellationToken.None), "lights");
        run.AddEvent(seconds, string.Create(CultureInfo.InvariantCulture,
            $"kinematic_takeoff after line-up at {kin.AlongM:0} m ({plan.Runway.LengthM - kin.AlongM:0} m runway ahead, {cross:0.0} m off the centerline), vr {kin.RotateKt:0} kt, {kin.TakeoffAccelMps2:0.0} m/s²"));
        run.AddEvent(seconds, "lights_sent takeoff");
    }

    /// <summary>
    /// Rollen (D1): Rollwege lesen, Netz bauen, Parkposition wählen (genannte oder passend zum Typ zufällig) und die Route zum ersten
    /// erreichbaren Haltepunkt vor der Startbahn. Wirft mit einem Text für den Piloten, wenn etwas fehlt.
    /// </summary>
    private static TaxiRoute PlanTaxi(TaxiGraph graph, RunwayEnd runway, string? parking, TrafficCategory category, TrafficWake wake)
    {
        var profile = TaxiProfile.For(category);
        if (!string.IsNullOrWhiteSpace(parking))
        {
            var node = graph.FindParking(parking)
                ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.parkingUnknown", parking.Trim(), graph.Icao, string.Join(", ", graph.Parkings.Select(p => p.Label).Take(20))));
            return TaxiRoute.Plan(graph, runway, node, profile)
                ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.noTaxiRoute", node.Parking?.Label ?? "", runway.Designation));
        }

        if (TaxiRoute.RunwayEntries(graph, runway).Count == 0)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.noHold", runway.Designation, graph.Icao));
        }

        foreach (var node in TaxiRoute.CandidateParkings(graph, category, wake, Random.Shared).Take(MaxParkingTries))
        {
            if (TaxiRoute.Plan(graph, runway, node, profile) is { } route)
            {
                return route;
            }
        }

        throw new InvalidOperationException(Loc.T("tools.trafficTest.noTaxiRoute", "–", runway.Designation));
    }

    /// <summary>
    /// Andere Flugzeuge am Boden rund um den Flughafen (KI-Verkehr an den Gates, das eigene Flugzeug; ohne die eigenen Testflugzeuge)
    /// als Hindernisse ins Netz: Routen führen mit <see cref="TaxiProfile.ClearanceM"/> Abstand an ihnen vorbei, belegte Parkpositionen
    /// fallen weg. Der Umkreis der Abfrage liegt um das eigene Flugzeug, deshalb reicht er bis über den Flughafen hinaus.
    /// </summary>
    private async Task<(int Count, string? Error)> SetTaxiObstaclesAsync(TaxiGraph graph, TrafficCategory category, CancellationToken ct)
    {
        var radius = TaxiObstacleMarginM;
        if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
        {
            radius += RouteMath.DistanceNm(lat, lon, graph.Latitude, graph.Longitude) * RunwayEnd.MetersPerNm;
        }

        var scan = await _traffic.ScanAsync((uint)Math.Min(radius, TrafficConnection.MaxScanRadiusMeters), includeHelicopters: true, withText: false, ct);
        var own = Running().Select(r => r.ObjectId).ToHashSet();
        var parked = scan.Samples
            .Where(s => s.OnGround && !own.Contains(s.ObjectId))
            .Where(s =>
            {
                var (east, north) = graph.ToLocal(s.Latitude, s.Longitude);
                return Math.Abs(east) < TaxiObstacleMarginM && Math.Abs(north) < TaxiObstacleMarginM;
            })
            .Select(s => (s.Latitude, s.Longitude))
            .ToList();
        graph.SetObstacles(parked, TaxiProfile.For(category).ClearanceM);
        return (parked.Count, scan.Error);
    }

    /// <summary>Höhe, bis zu der ein Abflug die Bahn für den nächsten belegt.</summary>
    private const double DepartureClearFt = 400;

    /// <summary>Ein B2-Test reiht sich gerade auf oder startet und ist noch unter <see cref="DepartureClearFt"/>.</summary>
    private static bool IsDeparting(TrafficTestRun run) =>
        run.TaxiKinematics is { Phase: TaxiPhase.LineUp or TaxiPhase.LinedUp }
        && (run.Kinematics is null || run.Kinematics.Phase != KinematicPhase.Climb || run.Kinematics.HeightFt < DepartureClearFt);

    /// <summary>
    /// Abstand beim Rollen: Halt hinter dem nächsten anderen Testflugzeug auf dem eigenen Weg (<see cref="TaxiTraffic"/>). Warten zwei
    /// aufeinander (Kreuzung), fährt das mit dem kleineren Platz (DUO01 vor DUO02).
    /// </summary>
    private void UpdateTaxiSpacing(TrafficTestRun run, TaxiKinematics taxi, double seconds)
    {
        if (run.TaxiGraph is not { } graph || taxi.Phase != TaxiPhase.Taxi)
        {
            return;
        }

        var others = new List<(double East, double North)>();
        var owners = new List<TrafficTestRun>();
        foreach (var other in Running())
        {
            if (other == run || other.TaxiKinematics is not { } ot || other.TaxiGraph is not { } og || og.Icao != graph.Icao
                || (other.InitialPlan.Variant == TrafficTestVariant.A3 && !other.TaxiActive)
                || (other.InitialPlan.TakeoffAfterTaxi && other.Kinematics is not null)
                || (other.BlockedBy == run.ObjectId && run.Slot < other.Slot))
            {
                continue;
            }

            var (lat, lon) = og.ToLatLon(ot.EastM, ot.NorthM);
            others.Add(graph.ToLocal(lat, lon));
            owners.Add(other);
        }

        var stop = TaxiTraffic.StopBehind(taxi.Taxi, taxi.DistanceM, others, TaxiTraffic.SpacingFor(run.Category));
        taxi.TrafficStopS = stop?.StopS ?? double.NaN;
        var blocker = stop is { } st ? owners[st.Index] : null;
        if (blocker?.ObjectId != run.BlockedBy)
        {
            run.BlockedBy = blocker?.ObjectId;
            run.AddEvent(seconds, blocker is null ? "taxi_spacing_clear" : string.Create(CultureInfo.InvariantCulture, $"taxi_spacing behind {blocker.Tail}, stop at {stop!.Value.StopS:0} m"));
        }
    }

    /// <summary>Parkpositionen der laufenden Rolltests (Start bzw. Ziel) für neue Tests sperren.</summary>
    private void ReserveOwnParkings(TaxiGraph graph)
    {
        graph.Reserve(Running()
            .Where(r => r.InitialPlan.Taxi is not null && r.TaxiGraph?.Icao == graph.Icao)
            .Select(r => (r.InitialPlan.Taxi!.Parking.Latitude, r.InitialPlan.Taxi.Parking.Longitude)));
    }

    /// <summary>Die Bahn mit dem Ende <paramref name="designation"/>, sonst null.</summary>
    private static AirportRunway? RunwayOf(IReadOnlyList<AirportRunway> runways, string designation)
    {
        var wanted = AirportRunway.Normalize(designation);
        return runways.FirstOrDefault(r => AirportRunway.Normalize(r.Primary) == wanted || AirportRunway.Normalize(r.Secondary) == wanted);
    }

    /// <summary>
    /// Rollen: Steht das Flugzeug vor einer gekreuzten Bahn (oder B2 vor dem Aufreihen) und ist die Mindestzeit fast um, fragt der Test
    /// den Verkehr ab (höchstens eine Abfrage zugleich) und gibt die Bahn frei, wenn nichts auf ihr oder im Endanflug ist
    /// (<see cref="RunwayOccupancy"/>). Ohne Antwort aus der Abfrage nach <see cref="RunwayCheckGiveUpSeconds"/> trotzdem.
    /// </summary>
    private void CheckRunwayWhileHolding(TrafficTestRun run, TaxiKinematics held, double seconds)
    {
        if (run.TaxiGraph is not { } graph || run.InitialPlan.Taxi is not { } route || held.RunwayClear || run.RunwayScanBusy)
        {
            return;
        }

        string? label = null;
        AirportRunway? runway = null;
        if (held.Phase == TaxiPhase.HoldingShort && held.CrossingsDone < route.Crossings.Count)
        {
            var crossing = route.Crossings[held.CrossingsDone];
            label = crossing.Runway;
            runway = RunwayOf(graph.Runways, crossing.Runway.Split('/')[0]);
            if (run.HoldShortAnnounced != held.CrossingsDone)
            {
                run.HoldShortAnnounced = held.CrossingsDone;
                run.AddEvent(seconds, "holding_short " + crossing.Runway + " on " + crossing.Taxiway);
            }

            if (held.PhaseSeconds < TaxiKinematics.CrossingHoldSeconds - 1)
            {
                return;
            }
        }
        else if (held.Phase == TaxiPhase.Holding && held.LineUpPath is not null)
        {
            label = run.InitialPlan.Runway.Designation;
            runway = RunwayOf(graph.Runways, label);
            run.LineUpQueuedUtc ??= DateTimeOffset.UtcNow;
            if (held.PhaseSeconds < TaxiKinematics.HoldBeforeLineUpSeconds - 1)
            {
                return;
            }

            // Reihenfolge: zuerst, wer am längsten am Haltepunkt wartet; auf der Bahn höchstens ein Abflug zugleich (bis 400 ft)
            var others = Running().Where(r => r != run && r.SameRunway(run.InitialPlan.Runway) && r.InitialPlan.TakeoffAfterTaxi).ToList();
            var ahead = others.Where(r => r.TaxiKinematics is { Phase: TaxiPhase.Holding } && r.LineUpQueuedUtc < run.LineUpQueuedUtc).ToList();
            var departing = others.FirstOrDefault(IsDeparting);
            var reason = ahead.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"line_up_queue position {ahead.Count + 1} behind {string.Join(' ', ahead.OrderBy(r => r.LineUpQueuedUtc).Select(r => r.Tail))}")
                : departing is not null ? "line_up_wait " + departing.Tail + " departing" : null;
            if (reason is not null)
            {
                if (reason != run.LastQueueEvent)
                {
                    run.LastQueueEvent = reason;
                    run.AddEvent(seconds, reason);
                }

                return;
            }
        }

        if (runway is null || label is null)
        {
            return;
        }

        run.RunwayScanBusy = true;
        var since = held.PhaseSeconds;
        _ = Task.Run(async () =>
        {
            try
            {
                var radius = RunwayScanRadiusM;
                if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
                {
                    radius += RouteMath.DistanceNm(lat, lon, graph.Latitude, graph.Longitude) * RunwayEnd.MetersPerNm;
                }

                var scan = await _traffic.ScanAsync((uint)Math.Min(radius, TrafficConnection.MaxScanRadiusMeters), includeHelicopters: true, withText: true, CancellationToken.None);
                if (scan.Error is not null)
                {
                    if (since >= RunwayCheckGiveUpSeconds)
                    {
                        held.RunwayClear = true;
                        run.AddEvent(seconds, "warning:runway_unknown " + label + " (" + scan.Error + "), going on");
                    }

                    return;
                }

                var blocker = RunwayOccupancy.Blocker(graph.Icao, runway, scan.Samples, run.ObjectId);
                if (blocker is null)
                {
                    held.RunwayClear = true;
                    run.LastRunwayBlocker = null;
                    run.AddEvent(seconds, string.Create(CultureInfo.InvariantCulture, $"runway_clear {label} after {since:0} s"));
                }
                else if (RunwayOccupancy.Key(blocker) != run.LastRunwayBlocker)
                {
                    // Nur bei einem neuen Grund (anderes Flugzeug, anderer Ort) – nicht bei jeder neuen Entfernung
                    run.LastRunwayBlocker = RunwayOccupancy.Key(blocker);
                    run.AddEvent(seconds, "runway_busy " + label + ": " + blocker);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException or ObjectDisposedException)
            {
                _log.LogDebug(ex, "Runway check failed");
            }
            finally
            {
                run.RunwayScanBusy = false;
            }
        });
    }

    /// <summary>
    /// A3: Abrollpunkt und Weg zur Parkposition (genannte oder passend zum Typ zufällig, frei). Abrollen frühestens dort, wo das
    /// Flugzeug nach dem Aufsetzen (um 450 m) mit 2 m/s² auf Rollgeschwindigkeit gebremst hat.
    /// </summary>
    private static TaxiRoute PlanTaxiIn(TaxiGraph graph, RunwayEnd runway, string? parking, TrafficCategory category, TrafficWake wake, double approachKt)
    {
        var profile = TaxiProfile.For(category);
        var touchdownMps = Math.Max(20, approachKt - 5) * 0.514444;
        var taxiMps = profile.TaxiKt * 0.514444;
        var minExit = TaxiInTouchdownM + (touchdownMps * 2) + (((touchdownMps * touchdownMps) - (taxiMps * taxiMps)) / (2 * TaxiInBrakeMps2)) + TaxiRoute.ExitLeadM;
        if (!string.IsNullOrWhiteSpace(parking))
        {
            var node = graph.FindParking(parking)
                ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.parkingUnknown", parking.Trim(), graph.Icao, string.Join(", ", graph.Parkings.Select(p => p.Label).Take(20))));
            return TaxiRoute.PlanArrival(graph, runway, node, profile, minExit)
                ?? throw new InvalidOperationException(Loc.T("tools.trafficTest.noTaxiRoute", node.Parking?.Label ?? "", runway.Designation));
        }

        foreach (var node in TaxiRoute.CandidateParkings(graph, category, wake, Random.Shared).Take(MaxParkingTries))
        {
            if (TaxiRoute.PlanArrival(graph, runway, node, profile, minExit) is { } route)
            {
                return route;
            }
        }

        throw new InvalidOperationException(Loc.T("tools.trafficTest.noTaxiRoute", "–", runway.Designation));
    }

    private async Task<TaxiGraph> LoadTaxiGraphAsync(string airport, IReadOnlyList<AirportRunway> runways, CancellationToken ct)
    {
        var data = await _sim.RequestAirportTaxiwaysAsync(airport, ct);
        if (data.Points.Count == 0)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.noTaxiways", airport));
        }

        return TaxiGraph.Build(data, runways);
    }

    /// <summary>
    /// Rollweg-Netz eines Flughafens zum Prüfen (traffic_taxi_info): Umfang, Achsen-Zuordnung, Parkpositionen, Haltepunkte und – mit
    /// <paramref name="runway"/> – die Route von <paramref name="parking"/> (oder einer passenden) zum Haltepunkt.
    /// </summary>
    public async Task<TrafficTaxiInfo> TaxiInfoAsync(string airport, string? runway, string? parking, string? category, int limit, CancellationToken ct)
    {
        if (!_sim.Connection.IsConnected)
        {
            throw new InvalidOperationException(Loc.T("sim.notConnected"));
        }

        var icao = airport.Trim().ToUpperInvariant();
        var runways = await _sim.RequestAirportRunwaysAsync(icao, ct);
        if (runways.Count == 0)
        {
            throw new InvalidOperationException(Loc.T("tools.trafficTest.airportUnknown", icao));
        }

        var graph = await LoadTaxiGraphAsync(icao, runways, ct);
        var cat = TrafficRules.ParseCategory(category) ?? TrafficCategory.Airliner;
        var (obstacles, obstacleError) = await SetTaxiObstaclesAsync(graph, cat, ct);
        var holds = graph.Holds();
        TrafficTaxiInfoRoute? route = null;
        string? routeError = null;
        if (!string.IsNullOrWhiteSpace(runway))
        {
            var end = RunwayEnd.Find(icao, runways, runway);
            if (end is null)
            {
                routeError = Loc.T("tools.trafficTest.runwayUnknown", runway.Trim().ToUpperInvariant(), icao, string.Join(", ", runways.SelectMany(r => new[] { r.Primary, r.Secondary }).Where(d => d.Length > 0)));
            }
            else
            {
                try
                {
                    var planned = PlanTaxi(graph, end, parking, cat, cat == TrafficCategory.GeneralAviation ? TrafficWake.Light : TrafficWake.Medium);
                    route = new TrafficTaxiInfoRoute(
                        planned.Parking.Parking?.Label ?? "",
                        planned.Parking.Parking?.TypeName ?? "",
                        planned.Hold.Runway,
                        planned.Hold.Taxiway,
                        planned.Taxiways,
                        planned.LengthM,
                        planned.Pushback,
                        Math.Round(planned.PushbackPath?.LengthM ?? 0),
                        Math.Round(planned.TaxiPath.LengthM),
                        planned.TaxiPath.Turns,
                        planned.TaxiPath.MinRadiusM is { } r ? Math.Round(r, 1) : null,
                        [.. planned.Nodes.Select(n => graph.Nodes[n]).Select(n => new TrafficTaxiInfoPoint(Math.Round(n.Latitude, 6), Math.Round(n.Longitude, 6), n.IsParking ? "parking" : n.IsHoldShort ? "hold" : "taxi"))]);
                }
                catch (InvalidOperationException ex)
                {
                    routeError = ex.Message;
                }
            }
        }

        var take = Math.Clamp(limit, 1, 500);
        return new TrafficTaxiInfo(
            icao,
            Math.Round(graph.Latitude, 6),
            Math.Round(graph.Longitude, 6),
            Math.Round(graph.ElevationFt),
            graph.Nodes.Count(n => !n.IsParking),
            graph.Nodes.Count(n => n.IsParking),
            graph.Edges.Count,
            graph.Edges.Count(e => e.Type == 2),
            graph.BrokenPaths,
            graph.Mapping,
            graph.MappingErrorM,
            [.. graph.Edges.Select(e => e.Name).Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            [.. graph.Nodes.Where(n => n.Parking is not null).OrderBy(n => n.Parking!.Label, StringComparer.OrdinalIgnoreCase).Take(take).Select(n => new TrafficTaxiInfoParking(n.Parking!.Label, n.Parking.TypeName, Math.Round(n.Parking.RadiusM, 1), Math.Round(n.Parking.HeadingDeg), graph.IsOccupied(n)))],
            [.. holds.GroupBy(h => h.Runway).OrderBy(g => g.Key).Select(g => new TrafficTaxiInfoHolds(g.Key, g.Count(), [.. g.Select(h => h.Taxiway).Where(t => t.Length > 0).Distinct().OrderBy(t => t)]))],
            route,
            routeError,
            obstacles,
            graph.BlockedEdges,
            graph.Nodes.Count(n => n.Parking is not null && graph.IsOccupied(n)),
            obstacleError);
    }

    /// <summary>
    /// Start (B1): Duopilo übernimmt gleich mit dem ersten Messpunkt – die Wegpunkt-KI hob ohne zu rotieren ab (beta.16/17). Bahnhöhe
    /// gemessen unter dem Flugzeug, sonst aus den Bahndaten; Lage am Boden aus STATIC PITCH.
    /// </summary>
    private void StartTakeoffKinematics(TrafficTestRun run, AiProbeSample sample)
    {
        var plan = run.Analysis.Plan;
        var (along, cross) = plan.Runway.Offsets(sample.Latitude, sample.Longitude);
        var cg = sample.CgHeightFt > 0.5 ? sample.CgHeightFt : DefaultCgHeightFt;
        var measured = sample.AltitudeFt - sample.AltitudeAglFt;
        var ground = sample.AltitudeAglFt > 0 && Math.Abs(measured - plan.Runway.ElevationFt) < MaxGroundCorrectionFt ? measured : plan.Runway.ElevationFt;
        var speed = sample.OnGround ? Math.Max(0, sample.GroundSpeedKt) : 0;
        var kin = TrafficKinematics.Takeoff(plan, KinematicProfile.For(run.Category), Math.Clamp(along, 0, plan.Runway.LengthM / 2), cross, speed, ground, cg, GroundPitch(sample.StaticPitchDeg));
        run.Kinematics = kin;
        _sim.StartAiPose(run.ObjectId, dt =>
        {
            kin.Advance(dt);
            return kin.Pose();
        });
        var seconds = Math.Max(0, (sample.Utc - run.SpawnUtc).TotalSeconds);
        run.AddEvent(Math.Round(seconds, 1), string.Create(CultureInfo.InvariantCulture,
            $"kinematic_takeoff at {kin.AlongM:0} m, {speed:0} kt, vr {kin.RotateKt:0} kt, climb {kin.ClimbKt:0} kt, {kin.TakeoffAccelMps2:0.0} m/s² (cg {cg:0.0} ft, static pitch {sample.StaticPitchDeg:0.0}°)"));
    }

    /// <summary>
    /// Flughafenregeln für den Test: airport-rules.json bei jedem Start frisch (eigene Änderungen gelten sofort), die Einordnung aller
    /// Flugplätze (OurAirports, rund 47 000 Kennungen) nur beim ersten Mal.
    /// </summary>
    private TrafficRules LoadRules(List<string> messages)
    {
        var roots = AirspaceLoader.Roots(_options.ResolveDataDirectory());
        _airportDirectory ??= TrafficAirportDirectory.Load(roots, messages);
        var rules = TrafficRules.Load(roots, messages, _airportDirectory);
        foreach (var message in messages)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.runwayRulesFile"), message);
        }

        return rules;
    }

    /// <summary>Lage am Boden aus STATIC PITCH (Nase hoch positiv wie static_pitch in flight_model.cfg); Unsinn ergibt 0.</summary>
    public static double GroundPitch(double staticPitchDeg) =>
        double.IsFinite(staticPitchDeg) && Math.Abs(staticPitchDeg) <= MaxStaticPitchDeg ? staticPitchDeg : 0;

    /// <summary>
    /// Bahnhöhe nachführen: Über der Bahn misst der Sim die Höhe über Grund; die Kinematik setzt sonst auf die Höhe aus den
    /// Bahndaten auf (EDDF 07C: 343 ft, gemessen bis 363 ft). Die Kinematik läuft je Frame mit
    /// <see cref="TrafficKinematics.GroundSlewFtPerSec"/> darauf zu, damit das Flugzeug nicht springt.
    /// </summary>
    private static void CorrectGround(TrafficTestRun run, TrafficKinematics kin, AiProbeSample sample)
    {
        var plan = run.Analysis.Plan;
        var (along, _) = plan.Runway.Offsets(sample.Latitude, sample.Longitude);
        if (along < 0 || along > plan.Runway.LengthM || sample.AltitudeAglFt <= 0 || sample.AltitudeAglFt > 500)
        {
            return;
        }

        kin.GroundTargetFt = sample.AltitudeFt - sample.AltitudeAglFt;
    }

    /// <summary>Velocity: Längsgeschwindigkeit setzen, solange der Test läuft und die KI in der Luft ist (Timer-Thread).</summary>
    private void VelocityTick(TrafficTestRun run)
    {
        if (run.EndReason is null && run.VelocityFtS is { } feetPerSecond && _sim.Connection.IsConnected)
        {
            Fire(_sim.SetAiVelocityAsync(run.ObjectId, feetPerSecond, CancellationToken.None), "velocity");
        }
    }

    private async Task StopCoreAsync(TrafficTestRun run, string reason)
    {
        lock (_lock)
        {
            if (run.EndReason is not null)
            {
                return;
            }

            run.EndReason = reason;
            run.EndedUtc = DateTimeOffset.UtcNow;
        }

        _sim.StopAiProbe(run.ObjectId);
        _sim.StopAiPose(run.ObjectId, unfreeze: false);
        run.VelocityTimer?.Dispose();
        run.VelocityTimer = null;
        if (_sim.Connection.IsConnected)
        {
            try
            {
                await _sim.RemoveAiObjectAsync(run.ObjectId, CancellationToken.None);
                run.AddEvent(Math.Round((run.EndedUtc.Value - run.SpawnUtc).TotalSeconds, 1), "removed");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                _log.LogWarning(Loc.Log("log.trafficTest.actionFailed"), "remove", ex.Message);
            }
        }

        TrafficTestSummary summary;
        lock (_lock)
        {
            summary = run.Analysis.Summary();
            run.CsvWriter?.Dispose();
            run.CsvWriter = null;
            run.SummaryPath = WriteSummary(run, summary);
        }

        _log.LogInformation(Loc.Log("log.trafficTest.ended"), run.Analysis.Plan.Runway.Designation, reason, summary.Verdict, summary.Samples);
    }

    private (string Path, StreamWriter Writer)? OpenCsv(TrafficTestPlan plan, DateTimeOffset spawnUtc)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = string.Create(CultureInfo.InvariantCulture, $"{spawnUtc:yyyyMMdd-HHmmss}-{plan.Runway.Airport}{plan.Runway.Designation}-{plan.Variant}.csv");
            var path = Path.Combine(Directory, name);
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine("utc,t_s,lat,lon,alt_ft,agl_ft,ias_kt,target_kt,gs_kt,vs_fpm,hdg_true,pitch_deg,bank_deg,on_ground,gear_handle,gear_total_pct,gear_center_pct,gear_left_pct,gear_right_pct,lights,desired_kt,ai_state,kin_phase,along_m,cross_m,dist_thr_nm,events");
            return (path, writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            return null;
        }
    }

    private void WriteCsv(TrafficTestRun run, AiProbeSample s, TrafficTestPoint p)
    {
        if (run.CsvWriter is not { } writer)
        {
            return;
        }

        try
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{s.Utc:O},{p.Seconds:0.0},{s.Latitude:0.000000},{s.Longitude:0.000000},{s.AltitudeFt:0},{s.AltitudeAglFt:0},{s.IndicatedKt:0},{p.TargetKt:0},{s.GroundSpeedKt:0},{s.VerticalSpeedFpm:0},{s.HeadingTrueDeg:0},{s.PitchDeg:0.0},{s.BankDeg:0.0},{(s.OnGround ? 1 : 0)},{(s.GearHandleDown ? 1 : 0)},{s.GearTotalPercent:0.0},{s.GearCenterPercent:0.0},{s.GearLeftPercent:0.0},{s.GearRightPercent:0.0},{Csv(AiLights.Describe(s.LightStates))},{s.DesiredKt:0},{Csv(s.AiState)},{(run.TaxiActive ? run.TaxiKinematics?.Phase.ToString().ToLowerInvariant() : run.Kinematics?.Phase.ToString().ToLowerInvariant() ?? run.TaxiKinematics?.Phase.ToString().ToLowerInvariant())},{p.AlongM:0},{p.CrossM:0.0},{p.DistanceToThresholdNm:0.00},{Csv(string.Join(' ', p.Events))}"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            run.CsvWriter = null;
        }
    }

    private string? WriteSummary(TrafficTestRun run, TrafficTestSummary summary)
    {
        if (run.CsvPath is not { } csv)
        {
            return null;
        }

        try
        {
            var path = Path.ChangeExtension(csv, ".json");
            var plan = run.Analysis.Plan;
            var json = JsonSerializer.Serialize(
                new
                {
                    variant = run.InitialPlan.Variant.ToString(),
                    tail = run.Tail,
                    airport = plan.Runway.Airport,
                    runway = plan.Runway.Designation,
                    runwayHeadingTrue = Math.Round(plan.Runway.HeadingTrueDeg, 1),
                    runwayLengthM = Math.Round(plan.Runway.LengthM),
                    runwayElevationFt = Math.Round(plan.Runway.ElevationFt),
                    title = run.Title,
                    type = TrafficModels.TypeOf(run.Title, run.Livery)?.IcaoType,
                    modelStatus = TrafficModels.StatusName(run.Title, run.Livery),
                    livery = run.Livery,
                    gear = GearName(run.GearMethod),
                    spawnUtc = run.SpawnUtc,
                    endReason = run.EndReason,
                    plan = new { plan.SpawnNm, plan.GearAtSpawn, plan.GearDownFt, plan.GearDownNm, plan.TouchdownM, plan.StopM, plan.FinalLowFt, plan.ApproachKt, takeoffKt = plan.IsTakeoff ? plan.TakeoffKt : (double?)null, speedMethod = SpeedMethodName(run.SpeedMethod), plan.ThrottlePercent, goAround = run.GoAroundEnabled, waypoints = plan.Waypoints, goAroundWaypoints = run.Analysis.GoAroundPlan?.Waypoints },
                    summary,
                    events = run.Events,
                },
                SummaryJson);
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(Loc.Log("log.trafficTest.csvFailed"), ex.Message);
            return null;
        }
    }

    private static readonly JsonSerializerOptions SummaryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string Csv(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private void Fire(Task task, string action) =>
        task.ContinueWith(
            t => _log.LogWarning(Loc.Log("log.trafficTest.actionFailed"), action, t.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private sealed class TrafficTestRun(
        TrafficTestPlan plan,
        int slot,
        string title,
        string livery,
        AiGearMethod gearMethod,
        uint objectId,
        DateTimeOffset spawnUtc,
        string? csvPath,
        StreamWriter? csvWriter,
        int maxMinutes)
    {
        private readonly List<TrafficTestEvent> _events = [];

        /// <summary>Auswertung; beim Abflug vom Gate (B2) wechselt sie nach dem Aufreihen auf die eines Starts.</summary>
        public TrafficTestAnalysis Analysis { get; set; } = new(plan, spawnUtc);

        /// <summary>Der Plan beim Start des Tests (Variante, Rollroute) – <see cref="Analysis"/> kann danach einen anderen haben.</summary>
        public TrafficTestPlan InitialPlan { get; } = plan;

        /// <summary>B2: Sekunde, ab der gestartet wird (für no_roll); sonst 0.</summary>
        public double TakeoffStartedSeconds { get; set; }

        public int Slot { get; } = slot;

        public string Tail { get; } = TrafficTestService.Tail(slot);

        public string Title { get; } = title;

        public string Livery { get; } = livery;

        public AiGearMethod GearMethod { get; } = gearMethod;

        /// <summary>Sekunde des ersten Fahrwerksbefehls (A1 beim Spawn = 0, A2 bei 10 NM).</summary>
        public double GearSentSeconds { get; set; }

        public uint ObjectId { get; } = objectId;

        public DateTimeOffset SpawnUtc { get; } = spawnUtc;

        public string? CsvPath { get; } = csvPath;

        public StreamWriter? CsvWriter { get; set; } = csvWriter;

        public int MaxMinutes { get; } = Math.Clamp(maxMinutes, 1, 120);

        public string? SummaryPath { get; set; }

        public string? EndReason { get; set; }

        public DateTimeOffset? EndedUtc { get; set; }

        public DateTimeOffset? LastSampleUtc { get; set; }

        public TrafficTestLast? Last { get; set; }

        public int GearCommands { get; set; }

        public bool StopScheduled { get; set; }

        /// <summary>Ob nach dem Spawn geprüft wurde, dass die Anfluglichter an sind.</summary>
        public bool LightsChecked { get; set; }

        /// <summary>Volles Modell statt PassiveAircraft-Dummy – Lichter kommen erst, wenn sein Bordnetz läuft.</summary>
        public bool FullModel { get; init; }

        /// <summary>Wie oft die Lichter des vollen Modells schon nachgeschickt wurden.</summary>
        public int FullModelLightResends { get; set; }

        public bool RolloutLightsSent { get; set; }

        /// <summary>Wie die Geschwindigkeit geregelt wird.</summary>
        public TrafficSpeedMethod SpeedMethod { get; init; }

        /// <summary>Velocity: Längsgeschwindigkeit (TAS, ft/s), die <see cref="VelocityTick"/> setzt; null = nicht setzen.</summary>
        public double? VelocityFtS { get; set; }

        public System.Threading.Timer? VelocityTimer { get; set; }

        public double RolloutLightsSeconds { get; set; }

        public int RolloutLightResends { get; set; }

        public string? LastAiState { get; set; }

        /// <summary>Ab dieser Entfernung zur Schwelle Kinematik statt KI, sonst null.</summary>
        public double? KinematicNm { get; init; }

        /// <summary>D1: Rollweg-Netz des Flughafens (Umrechnung der Wege in Breite/Länge).</summary>
        public TaxiGraph? TaxiGraph { get; init; }

        /// <summary>D1: Kinematik am Boden, ab dem ersten Messpunkt.</summary>
        public TaxiKinematics? TaxiKinematics { get; set; }

        private volatile bool _taxiActive;

        /// <summary>A3: Das Rollen hat von der Landung übernommen (wird im Frame gesetzt, daher volatile).</summary>
        public bool TaxiActive
        {
            get => _taxiActive;
            set => _taxiActive = value;
        }

        private volatile bool _runwayScanBusy;

        /// <summary>Rollen: Eine Abfrage, ob die Bahn frei ist, läuft gerade.</summary>
        public bool RunwayScanBusy
        {
            get => _runwayScanBusy;
            set => _runwayScanBusy = value;
        }

        /// <summary>Rollen: wegen dieses Testflugzeugs steht es gerade (Abstand), sonst null.</summary>
        public uint? BlockedBy { get; set; }

        /// <summary>B2: seit wann es am Haltepunkt auf das Aufreihen wartet (Reihenfolge).</summary>
        public DateTimeOffset? LineUpQueuedUtc { get; set; }

        /// <summary>B2: zuletzt gemeldeter Grund für das Warten am Haltepunkt.</summary>
        public string? LastQueueEvent { get; set; }

        /// <summary>Rollen: was die Bahn zuletzt belegt hat (für das Ereignis nur bei einer Änderung).</summary>
        public string? LastRunwayBlocker { get; set; }

        /// <summary>Rollen: für diesen Halt vor einer Bahn ist das Ereignis schon geschrieben (Index der Kreuzung).</summary>
        public int HoldShortAnnounced { get; set; } = -1;

        /// <summary>D1: seit wann das Flugzeug am Haltepunkt steht.</summary>
        public double? HoldingSinceSeconds { get; set; }

        /// <summary>B1: Duopilo fliegt den Start selbst (<see cref="TrafficKinematics.Takeoff"/>) statt der Wegpunkt-KI.</summary>
        public bool KinematicTakeoff { get; init; }

        /// <summary>Kategorie des Typs (Kennwerte der Kinematik).</summary>
        public TrafficCategory Category { get; init; }

        /// <summary>Lichter beim Spawn (Anflug oder Reiseflug), auch beim Nachschicken.</summary>
        public AiLights Lights { get; set; } = TrafficLights.Approach;

        /// <summary>Kinematik ab der Übernahme, sonst null (nur auf dem SimConnect-Thread verändert).</summary>
        public TrafficKinematics? Kinematics { get; set; }

        public double? DesiredSentKt { get; set; }

        public double DesiredSentSeconds { get; set; }

        /// <summary>Ob die KI durchstarten soll, wenn der Anflug nicht passt (<see cref="TrafficGoAround"/>).</summary>
        public bool GoAroundEnabled { get; init; }

        /// <summary>Lage des vorigen Messpunkts entlang der Mittellinie (für das Überfliegen der Schwelle).</summary>
        public double? PreviousAlongM { get; set; }

        public bool GearUpSent { get; set; }

        public IReadOnlyList<TrafficTestEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public bool SameRunway(RunwayEnd runway) =>
            string.Equals(Analysis.Plan.Runway.Airport, runway.Airport, StringComparison.OrdinalIgnoreCase)
            && AirportRunway.Normalize(Analysis.Plan.Runway.Designation) == AirportRunway.Normalize(runway.Designation);

        /// <summary>Passt zu "05L", "5L" oder "EDDL 05L"; leer passt immer.</summary>
        public bool Matches(string? runway) => TrafficTestService.Matches(Analysis.Plan.Runway, runway);

        /// <summary>Kennung ist <paramref name="tail"/> ("DUO02", Groß/klein egal); leer passt immer.</summary>
        public bool HasTail(string? tail) => string.IsNullOrWhiteSpace(tail) || string.Equals(Tail, tail.Trim(), StringComparison.OrdinalIgnoreCase);

        public void AddEvent(double seconds, string text)
        {
            lock (_events)
            {
                _events.Add(new TrafficTestEvent(seconds, text));
            }
        }
    }

    /// <summary>
    /// Ob <paramref name="filter"/> die Bahn <paramref name="runway"/> meint: "05L"/"5L" (Bahn an jedem Flughafen), "EDDL 05L" oder
    /// "EDDL" (alle Bahnen dort); leer passt immer.
    /// </summary>
    public static bool Matches(RunwayEnd runway, string? filter)
    {
        var text = (filter ?? "").Trim();
        if (text.Length == 0)
        {
            return true;
        }

        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        if (first.Length == 4 && char.IsLetter(first[0]) && first.All(char.IsLetterOrDigit))
        {
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            return string.Equals(first, runway.Airport, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == 0 || AirportRunway.Normalize(rest) == AirportRunway.Normalize(runway.Designation));
        }

        return AirportRunway.Normalize(text) == AirportRunway.Normalize(runway.Designation);
    }
}
