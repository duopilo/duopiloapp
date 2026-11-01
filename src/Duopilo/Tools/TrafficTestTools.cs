using System.ComponentModel;
using Duopilo.Localization;
using Duopilo.Traffic;
using ModelContextProtocol.Server;

namespace Duopilo.Tools;

/// <summary>
/// Testwerkzeug für eigenen KI-Verkehr (Stufe A der Testlinie, docs/research/eigener-verkehr.md). Erzeugt Objekte im Sim, deshalb
/// standardmäßig aus (Duopilo:TrafficTest:Enabled).
/// </summary>
[McpServerToolType]
public static class TrafficTestTools
{
    public const int DefaultModelLimit = 50;

    [McpServerTool(Name = "traffic_test_start", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Developer test tool (off by default, Duopilo:TrafficTest:Enabled): spawns an AI aircraft (tail DUO01–DUO04, no ATC control) that " +
        "flies a final approach to a runway by waypoints, lands and stops on the runway – to find out whether Duopilo can run its own traffic. " +
        "Variant A1: spawn 5 NM out on the extended centerline with the gear already down. Variant A2: spawn 15 NM out with the gear up, gear " +
        "command at 3000 ft above the runway so it is down by 2500 ft (the evaluation checks that). Variant B1 is a takeoff instead: spawn " +
        "lined up 60 m behind the start of the runway, takeoff roll, rotation, liftoff, climb straight ahead to 3000 ft above the runway " +
        "(flown by Duopilo itself, kinematicTakeoff), gear up at a " +
        "positive climb rate; the test ends at 2800 ft (climbed), on the ground past the runway end (no_liftoff) or without rolling within " +
        "90 s (no_roll). Variant C1 is an overflight at cruise altitude flown by the waypoint AI: spawn spawnNm (default 40, general " +
        "aviation 15) before the middle of the runway at FL350 (general aviation FL85), straight over it on the runway's direction or " +
        "trackDeg, nav/beacon/strobes/logo on, landing light off; the test ends the same distance behind it (passed). Variant C2 is the " +
        "same overflight away from airports (no airport or runway needed): over latitude/longitude or – without them – right over the " +
        "pilot's aircraft, track trackDeg or across the pilot's heading, offsetNm beside it; without flightLevel and less than 3 NM " +
        "beside the pilot it stays at least 2000 ft above the pilot (event overflight_separated); status runway OVF1–OVF4 by tail, " +
        "airport OWN or POS. Variant D1 is taxiing, " +
        "flown by Duopilo itself: spawn at a parking spot (parking, else one that fits the type at random – airliners at gates and cargo " +
        "stands, general aviation on GA ramps, military on military stands, big enough by radius), the route over the taxiways (MSFS " +
        "taxiway network, runways only crossed) to the first reachable holding point of the runway, pushback when the nose points away " +
        "from the taxiway (2.5 kt, in a curve onto the taxiway), 10 s, then taxi at 15 kt (general aviation 10), slower in turns, stop " +
        "just before the holding point; nav, beacon, taxi and logo lights; the test ends after a minute at the holding point (holding). " +
        "Runways the route crosses (D1, B2, A3): hold short 12 m before their holding line (general aviation 4 m) for at least 5 s and " +
        "cross only when no aircraft is on that runway or on final to it within 4 NM (airborne; traffic scan; after 30 s without an answer anyway). " +
        "B2 also lines up only when its own runway is free. " +
        "Taxi tests (D1, B2, A3) on the same runway don't replace each other but queue: each keeps 48 m (general aviation 22 m) behind " +
        "another test aircraft ahead on its route; oncoming test aircraft on the same stretch of taxiway: the one already in it or " +
        "reaching it first goes, the other holds before it, 40 m clear of the other's route (event taxi_give_way); B2 lines up in the " +
        "order of arrival at the holding points, one departure at a time " +
        "(the next lines up once the previous is above 400 ft); new tests skip the parking spots of running ones. " +
        "Landings (A3) on the same runway queue too: a new one spawns behind the last arrival still on final with wake spacing (3 NM, " +
        "behind heavy 4–6, behind super 4–8, light behind medium 5); when a departure (B2) of that runway waits, the landing leaves a " +
        "gap that fits it (still its release distance away when the departure is ready at the holding point and the landing ahead has " +
        "vacated, 60 s after touchdown, plus 20 s) if that puts it at most 8 NM farther out, else it lands first; spawnNm is the " +
        "earliest point, up to 30 NM. Mixed mode on one runway: B2 lines up only when the next landing is " +
        "farther than its release distance (line-up, takeoff roll and 500 ft at 145 kt, 4–8 NM; event departure_release); a kinematic " +
        "landing checks from 2 NM whether someone is on the runway and goes around at 0.4 NM if it is still occupied (go_around " +
        "runway_occupied, climb straight ahead to 3000 ft, the test ends at 2800 ft). " +
        "traffic_taxi_info shows the network and the route without spawning. Variant B2 is a departure from the gate: like D1 to the " +
        "holding point, 15 s there, then line up (over the taxi points onto the centerline, in a curve into the takeoff direction, " +
        "40 m on) and take off like B1 with kinematicTakeoff from there – landing light and strobes on; the evaluation switches to the " +
        "takeoff's, the test ends at 2800 ft (climbed). Variant A3 is a landing with taxi-in: like A1, flown by Duopilo from the spawn " +
        "(kinematic), braking on the runway to taxi speed instead of to a stop, leaving the runway while rolling at the first exit " +
        "behind the braking distance (not angled backwards) and taxiing to a free parking spot (parking, else one that fits the type) – " +
        "the test ends after a minute there (parked); the status has taxi with exitAlongM and exitKt. Up to 4 tests at the same time, one per runway (e.g. a parallel approach: one call for 05L, one for 05R); starting a " +
        "test on a runway that already has one replaces it. Pick runways the pilot is not using right now. Samples (1 per second) go to one CSV " +
        "per test in the data folder (traffic-tests). Follow with traffic_test_status, end with traffic_test_stop (the aircraft is removed; " +
        "after MaxMinutes, default 15 – taxi tests D1, B2, A3 at least 30 – also without stop). Lights: nav, beacon, strobes, landing, taxi and logo on at spawn; after touchdown " +
        "below 60 kt landing lights and strobes off. Go-around (goAround, default on): more than 150 ft above the glide path or more than " +
        "Vapp + 20 kt at the threshold, or not on the ground 900 m behind it (short runways: a third of the length) – then new waypoints " +
        "straight ahead, climb to 3000 ft above the runway, gear up at a positive climb rate, landing light stays on; the test ends at " +
        "2800 ft (reason go_around) or 5 minutes after the go-around (go_around_timeout). " +
        "Models outside the approval list (traffic_test_models: status) get a warning. Airport rules for every airport: the category " +
        "must fit the airport (OurAirports classification: military airfields only military aircraft, heliports, seaplane bases and " +
        "closed fields nothing, small fields and fields without scheduled service general aviation only), the type must be in the " +
        "airport's traffic mix if it has one (EDHI mostly Belugas), the runway must allow landing (A1/A2) or takeoff (B1), category, " +
        "type and wake category (data\\traffic\\airport-rules.json, e.g. EDDF 07L/25R landings only without B747, A380, MD-11; 18 " +
        "takeoffs only), and it must be long enough (landing about 0.075 × Vapp² m, takeoff 1.4 times that) and wide enough (heavy and " +
        "super 45 m, other airliners 30 m, heavy military 27 m); otherwise the start is refused with the reasons (ignoreRunwayRules to test anyway). Without " +
        "title the model is picked by these rules – by weight from the traffic mix.")]
    public static async Task<string> Start(
        TrafficTestService tests,
        [Description("ICAO code of the airport, e.g. EDDL. Must be within 80 NM of the pilot's aircraft. Not needed for C2.")] string? airport = null,
        [Description("Runway end to land on or take off from, e.g. 23L (5L and 05L are the same). Not needed for C2.")] string? runway = null,
        [Description("A1 (gear down from spawn, 5 NM), A2 (spawn 15 NM with the gear up, gear down at 3000 ft above the runway), B1 (takeoff from the runway), C1 (overflight at cruise altitude over an airport), C2 (overflight away from airports: over latitude/longitude or the pilot's aircraft), D1 (taxi from a parking spot to the holding point of the runway), B2 (departure from the gate: taxi, line up, take off) or A3 (landing with taxi-in to a parking spot). Default A1.")] string variant = "A1",
        [Description("Aircraft title as listed by traffic_test_models. Empty = automatic (an FSLTL A320 if installed, else a verified model of the approval list – a PassiveAircraft model –, else any A320; FlyByWire and Headwind models are skipped).")] string? title = null,
        [Description("Livery name for the title (MSFS 2024 modular aircraft). Empty = default livery.")] string? livery = null,
        [Description("Spawn distance before the threshold in NM (2–30; C1: before the overflight point, 5–75). Empty = default of the variant.")] double? spawnNm = null,
        [Description(
            "How the gear is lowered: 'all' (default: event GEAR_DOWN, event GEAR_SET 1 and the simvar GEAR HANDLE POSITION together), " +
            "'down' (only GEAR_DOWN), 'set' (only GEAR_SET 1) or 'handle' (only GEAR HANDLE POSITION) – to find out which one the model obeys.")] string gear = "all",
        [Description(
            "How far the waypoints of the last mile lie below the 3° glide path, in ft (0–300): the threshold waypoint by the full value, " +
            "0.5 NM by 60 %, 1 NM by a quarter. The AI levels off on short final and crossed the threshold 170–190 ft high in the first " +
            "tests; aiming lower should bring it down on the glide path. The evaluation still measures against the glide path. " +
            "0 = exactly on the glide path, empty = 100.")] double? finalLowFt = null,
        [Description(
            "Where the first ground waypoint lies, in m behind the threshold (−500 to 600, negative = before the threshold). The AI " +
            "switches to its landing mode about 1 km before it and touched down 100–700 m behind it in the tests. Empty = 150.")] double? touchdownM = null,
        [Description(
            "Approach speed (Vapp) in kt, e.g. 115 for an ATR 72 or 65 for a Cessna 172. The waypoints get Vapp + 25 from 6 NM, Vapp + 5 " +
            "below, Vapp at the threshold. B1: takeoff speed about V2 = Vapp + 10, climb waypoints + 10, + 40 and + 60 kt on top. Empty = " +
            "from the approval list (traffic_test_models: approachKt), else 135.")] double? approachKt = null,
        [Description(
            "How the speed is controlled – the AI ignores the speed in the waypoints (every model flew 160–169 kt) and overwrote AI DESIRED " +
            "SPEED in beta.14 (stayed at 169 kt). 'desired' (default): AI DESIRED SPEED to the speed of the next waypoint. 'throttle' " +
            "(experiment): waypoints in the air with THROTTLE_REQUESTED and a fixed throttle (throttlePercent) instead of the speed. " +
            "'velocity' (experiment): VELOCITY BODY Z five times a second to the wanted speed as TAS, plus AI DESIRED SPEED. 'off': waypoint " +
            "speeds only.")] string speedMethod = "desired",
        [Description("Throttle in % (0–100) of the waypoints in the air for speedMethod 'throttle'. Empty = 30.")] double? throttlePercent = null,
        [Description(
            "A1/A2: distance before the threshold in NM (1–12) from which Duopilo moves the aircraft itself (kinematic): the AI is " +
            "released, the aircraft frozen and its position, height and attitude set every frame – speed down to Vapp + 5 and Vapp from " +
            "0.5 NM, onto the 3° path, flare from 30 ft (general aviation 15 ft) to about 150 ft/min, touchdown around 450 m behind the " +
            "threshold, then braking to the stop point. Needed because the waypoint AI flies no slower than about 169 kt and lands at a " +
            "fixed 133 kt. Empty = waypoint AI to the stop, as before.")] double? kinematicNm = null,
        [Description("C1, C2: track of the overflight in degrees true. Empty = C1 the direction of the runway, C2 across the pilot's heading (from the left).")] double? trackDeg = null,
        [Description("C1, C2: flight level, e.g. 350. Empty = 350 (general aviation 85); C2 over the pilot's aircraft at least 2000 ft above it.")] double? flightLevel = null,
        [Description("C1, C2: true cruise speed in kt. Empty = 450 (general aviation twice the approach speed, at most 300); the AI takes it as true airspeed.")] double? cruiseKt = null,
        [Description("Start even if the aircraft doesn't fit the runway (runway rules, length, width) – the reasons go into the events. Default false.")] bool ignoreRunwayRules = false,
        [Description(
            "Go around when the approach doesn't fit (default true): too high or too fast at the threshold, or no touchdown by 900 m. " +
            "false = always land, as before.")] bool goAround = true,
        [Description(
            "B1 only: an extra ground waypoint this many m ahead of the spawn with the takeoff speed, in case the AI doesn't start rolling " +
            "or accelerating towards a first waypoint in the air (0–60 % of the runway). Empty or 0 = none. Only without kinematicTakeoff.")] double? rollM = null,
        [Description(
            "B1 only (default true): Duopilo flies the takeoff itself (kinematic, like the final with kinematicNm) – the waypoint AI lifted " +
            "off without rotating. 3 s lined up, takeoff roll at 2 m/s² (general aviation 1.4; more on short runways so Vr comes by 60 % " +
            "of the runway), rotation at Vr = Vapp + 5 (general aviation Vapp − 10) with 3°/s, liftoff at 8° (general aviation 6°), climb " +
            "at V2 + 10 (general aviation V2) with 2500 ft/min (general aviation 700), nose up to about 15° (path plus angle of attack), " +
            "from 1500 ft above the runway 1500 ft/min and faster, level at 3000 ft. false = waypoint AI as before.")] bool kinematicTakeoff = true,
        [Description("D1 and B2: parking spot to start from; A3: parking spot to taxi to – e.g. 'A12', 'P5' or 'G3' (traffic_taxi_info lists them). Empty = a free one that fits the type, at random.")] string? parking = null,
        [Description("C2: latitude of the overflight point. Empty (with longitude) = the pilot's aircraft.")] double? latitude = null,
        [Description("C2: longitude of the overflight point.")] double? longitude = null,
        [Description("C2: pass this many NM beside the point (right of the track positive), e.g. 2 to fly past the pilot. Empty = right over it.")] double? offsetNm = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<TrafficTestVariant>(variant?.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.variantUnknown", variant ?? "") });
        }

        if (!Enum.TryParse<Duopilo.Sim.AiGearMethod>(gear?.Trim(), ignoreCase: true, out var gearMethod) || !Enum.IsDefined(gearMethod))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.gearUnknown", gear ?? "") });
        }

        if (!Enum.TryParse<TrafficSpeedMethod>(speedMethod?.Trim(), ignoreCase: true, out var speed) || !Enum.IsDefined(speed))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.speedMethodUnknown", speedMethod ?? "") });
        }

        if (parsed != TrafficTestVariant.C2 && (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(runway)))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.airportRequired") });
        }

        if (parsed == TrafficTestVariant.C2 && (latitude is null) != (longitude is null))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.positionIncomplete") });
        }

        try
        {
            var status = await tests.StartTestAsync(new TrafficTestRequest(airport ?? "", runway ?? "", parsed, title, livery, spawnNm, gearMethod, finalLowFt, touchdownM, approachKt, speed, goAround, rollM, throttlePercent, kinematicNm, trackDeg, flightLevel, cruiseKt, ignoreRunwayRules, kinematicTakeoff, parking, latitude, longitude, offsetNm), cancellationToken);
            return Json.Serialize(new { ok = true, status = Brief(status) });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or ArgumentException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "traffic_taxi_info", ReadOnly = true)]
    [Description(
        "Developer tool for taxiing (traffic_test_start D1): reads the taxiway network of an airport from MSFS without spawning anything – " +
        "number of taxi points, parking spots and paths, how the offsets of the points lie (mapping and its mean distance of the runway " +
        "paths to the runway centerlines – a few metres means the positions fit), taxiway names, parking spots (label, type, radius, " +
        "heading), holding points per runway with their taxiways and, with runway, the route from parking (or one that fits the category) " +
        "to the holding point: taxiways, length, pushback, turns and the points with latitude/longitude.")]
    public static async Task<string> TaxiInfo(
        TrafficTestService tests,
        [Description("ICAO code of the airport, e.g. EDDL.")] string airport,
        [Description("Runway to taxi to, e.g. 23L. Empty = no route.")] string? runway = null,
        [Description("Parking spot to start from, e.g. 'A12'. Empty = one that fits the category, at random.")] string? parking = null,
        [Description("Category for the parking spot: airliner (default), generalaviation or military.")] string? category = null,
        [Description("Maximum number of parking spots in the answer. Default 60.")] int limit = 60,
        CancellationToken cancellationToken = default)
    {
        if (!tests.Enabled)
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.disabled") });
        }

        try
        {
            return Json.Serialize(new { ok = true, info = await tests.TaxiInfoAsync(airport, runway, parking, category, limit, cancellationToken) });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or ArgumentException or OperationCanceledException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "traffic_test_status", ReadOnly = true)]
    [Description(
        "State of the traffic tests (traffic_test_start), one entry per test in 'tests' (running ones and the last ended ones): running/ended, " +
        "tail, ICAO type and model status (verified, candidate, excluded, unknown), last sample (altitude, IAS against the requested speed, " +
        "ground speed, vertical speed, pitch – nose up positive –, bank, gear, lights, " +
        "distance to the threshold, position relative to the centerline, AI state), events (spawn, gear, touchdown, stopped …) and the " +
        "evaluation: verdict (stopped, landed, floated = hovered above the runway without touching down, airborne, go_around, " +
        "go_around_ignored = went around but touched down anyway; takeoff B1: climbed, climbing, rolling, no_roll, no_liftoff; overflight C1: " +
        "passed, cruising; taxi D1: parked, taxiing, stopped), overflight (when overhead, distance off track, altitude and speed there, max track and altitude deviation, " +
        "mean IAS and ground speed), go-around " +
        "(reason, where, height, lowest and highest point after it, seconds until the gear was up, whether 2800 ft were reached), takeoff " +
        "(roll start, liftoff point, speed and pitch, roll distance, height over the runway end, climb rate, pitch and bank up to 1500 ft, " +
        "gear up, lights at liftoff), kinematics (phase, where Duopilo took over, flare, touchdown, ground height; takeoff: Vr, climb speed, acceleration, where the rotation began, liftoff point, speed and pitch), speed deviation, height " +
        "above the runway when the gear was down and whether that was by 2500 ft, values " +
        "at the approach gates (altitude deviation from the glide path, pitch), approach pitch, touchdown point, speed, sink rate, pitch and " +
        "flare (pitch gain before touchdown), max bank on final, lights on final and after landing, float seconds, stop point; taxi D1: " +
        "'taxi' with phase (parked, pushback, pushback_done, taxi, holding_short before a crossed runway, holding; B2 also line_up, " +
        "lined_up; A3 landing until the exit, then taxi and holding at the parking spot), parking spot, holding point (A3: the exit), " +
        "taxiways, route length, pushback, turns, distance (with the pushback; crossing stops and taxi_spacing count on the taxi path without it), speed, crossings (runway, taxiway, stop) and crossingsDone, A3 exitAlongM and " +
        "exitKt, waitingFor (the test aircraft it keeps its distance to); events holding_short, runway_busy (what blocks it), runway_clear, " +
        "taxi_spacing, taxi_give_way (oncoming), line_up_queue, line_up_wait; arrivals arrival_sequence (spawn and spacing), landing_clear, landing_runway_busy, " +
        "go_around runway_occupied; B2 departure_release. Paths of " +
        "the CSV and summary files. The waypoints only with details: true.")]
    public static string Status(
        TrafficTestService tests,
        [Description("Only the test on this runway, e.g. 05L or 'EDDL 05L' (C2: OVF1–OVF4, OWN or POS). Empty = all tests.")] string? runway = null,
        [Description("Include the waypoints of each test (long). Default false.")] bool details = false)
    {
        var all = tests.Status(runway);
        return Json.Serialize(new
        {
            ok = true,
            enabled = tests.Enabled,
            running = all.Count(t => t.State == "running"),
            tests = details ? all : all.Select(Brief).ToList(),
        });
    }

    /// <summary>Status ohne Wegpunkte – die stehen schon in der Antwort auf den Start nicht mehr, sonst wird jede Abfrage sehr lang.</summary>
    private static TrafficTestStatus Brief(TrafficTestStatus status) => status with { Waypoints = [] };

    [McpServerTool(Name = "traffic_test_stop", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Ends traffic tests: removes the test aircraft from the sim and writes the evaluation next to the CSV. Without runway and tail all " +
        "tests end. A test that finishes by itself (climbed, holding, parked …) ends alone; the others on its runway keep running. Returns " +
        "the final state of the ended tests (without waypoints) and how many are still running.")]
    public static async Task<string> Stop(
        TrafficTestService tests,
        [Description("Only the tests on this runway, e.g. 05L or 'EDDL 05L' (C2: OVF1–OVF4, OWN or POS). Empty = all runways.")] string? runway = null,
        [Description("Only the test with this tail, e.g. DUO02 (one aircraft of a queue). Empty = all on the runway.")] string? tail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await tests.StopTestAsync("stopped", cancellationToken, runway, tail);
            var ended = string.IsNullOrWhiteSpace(runway) && string.IsNullOrWhiteSpace(tail)
                ? all
                : [.. tests.Status(runway).Where(t => string.IsNullOrWhiteSpace(tail) || string.Equals(t.Tail, tail.Trim(), StringComparison.OrdinalIgnoreCase))];
            return Json.Serialize(new { ok = true, running = all.Count(t => t.State == "running"), tests = ended.Select(Brief).ToList() });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "traffic_test_models", ReadOnly = true)]
    [Description(
        "Aircraft models installed in MSFS 2024 that can be spawned (title and livery, from EnumerateSimObjectsAndLiveries), for " +
        "traffic_test_start. Filter by words, e.g. 'A320 FSLTL' or 'Lufthansa'. 'suggested' is the model traffic_test_start takes without a title. " +
        "Each model has 'type' (ICAO type from Duopilo's approval list), 'category' (airliner, generalaviation, military) and 'status': 'verified' " +
        "(gear and landing checked in the sim), 'candidate' (landed in a test, not looked at yet), 'untested' (on the list, e.g. the MSFS " +
        "2024 GA aircraft, not tested yet), 'excluded' (FlyByWire, Headwind: as AI their gear only comes half out and the engines don't " +
        "spin) or 'unknown' (not on the list). 'approved' lists the approval list itself, only with approvalList: true (long).")]
    public static async Task<string> Models(
        TrafficTestService tests,
        [Description("Words that must all appear in title or livery, case-insensitive. Empty = all.")] string? filter = null,
        [Description("Maximum number of models in the answer. Default 50.")] int limit = DefaultModelLimit,
        [Description("Only models of the approval list (verified, candidate and untested).")] bool approvedOnly = false,
        [Description("Include the approval list itself (about 160 types, long). Default false.")] bool approvalList = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await tests.ModelsAsync(null, cancellationToken);
            var matched = TrafficTestService.Filter(all, filter)
                .Where(m => !approvedOnly || TrafficModels.TypeOf(m.Title, m.Livery) is not null)
                .ToList();
            var max = Math.Clamp(limit, 1, 500);
            var suggested = TrafficTestService.PickDefaultModel(all);
            return Json.Serialize(new
            {
                ok = true,
                total = all.Count,
                matched = matched.Count,
                truncated = matched.Count > max,
                suggested = suggested is null ? null : new { title = suggested.Title, livery = suggested.Livery },
                approved = !approvalList ? null : TrafficModels.Approved.Select(t => new
                {
                    type = t.IcaoType,
                    name = t.Name,
                    category = t.Category.ToString().ToLowerInvariant(),
                    approachKt = t.ApproachKt,
                    passive = t.Passive,
                    titles = t.Titles,
                    titleWords = t.TitleWords,
                    status = t.Status.ToString().ToLowerInvariant(),
                    note = t.Note,
                }),
                models = matched.Take(max).Select(m => new
                {
                    title = m.Title,
                    livery = m.Livery,
                    type = TrafficModels.TypeOf(m.Title, m.Livery)?.IcaoType,
                    category = TrafficModels.TypeOf(m.Title, m.Livery)?.Category.ToString().ToLowerInvariant(),
                    approachKt = TrafficModels.TypeOf(m.Title, m.Livery)?.ApproachKt,
                    status = TrafficModels.StatusName(m.Title, m.Livery),
                    excluded = TrafficModels.IsExcluded(m.Title, m.Livery),
                }),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
