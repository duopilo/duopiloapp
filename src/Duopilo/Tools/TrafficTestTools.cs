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
        "lined up 60 m behind the start of the runway, takeoff roll, liftoff, climb straight ahead to 3000 ft above the runway, gear up at a " +
        "positive climb rate; the test ends at 2800 ft (climbed), on the ground past the runway end (no_liftoff) or without rolling within " +
        "90 s (no_roll). Up to 4 tests at the same time, one per runway (e.g. a parallel approach: one call for 05L, one for 05R); starting a " +
        "test on a runway that already has one replaces it. Pick runways the pilot is not using right now. Samples (1 per second) go to one CSV " +
        "per test in the data folder (traffic-tests). Follow with traffic_test_status, end with traffic_test_stop (the aircraft is removed; " +
        "after MaxMinutes, default 15, also without stop). Lights: nav, beacon, strobes, landing, taxi and logo on at spawn; after touchdown " +
        "below 60 kt landing lights and strobes off. Go-around (goAround, default on): more than 150 ft above the glide path or more than " +
        "Vapp + 20 kt at the threshold, or not on the ground 900 m behind it (short runways: a third of the length) – then new waypoints " +
        "straight ahead, climb to 3000 ft above the runway, gear up at a positive climb rate, landing light stays on; the test ends at " +
        "2800 ft (reason go_around) or 5 minutes after the go-around (go_around_timeout). " +
        "Models outside the approval list (traffic_test_models: status) get a warning.")]
    public static async Task<string> Start(
        TrafficTestService tests,
        [Description("ICAO code of the airport, e.g. EDDL. Must be within 80 NM of the pilot's aircraft.")] string airport,
        [Description("Runway end to land on or take off from, e.g. 23L (5L and 05L are the same).")] string runway,
        [Description("A1 (gear down from spawn, 5 NM), A2 (spawn 15 NM with the gear up, gear down at 3000 ft above the runway) or B1 (takeoff from the runway). Default A1.")] string variant = "A1",
        [Description("Aircraft title as listed by traffic_test_models. Empty = automatic (an FSLTL A320 if installed, else a verified model of the approval list – a PassiveAircraft model –, else any A320; FlyByWire and Headwind models are skipped).")] string? title = null,
        [Description("Livery name for the title (MSFS 2024 modular aircraft). Empty = default livery.")] string? livery = null,
        [Description("Spawn distance before the threshold in NM (2–30). Empty = default of the variant.")] double? spawnNm = null,
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
        [Description(
            "Go around when the approach doesn't fit (default true): too high or too fast at the threshold, or no touchdown by 900 m. " +
            "false = always land, as before.")] bool goAround = true,
        [Description(
            "B1 only: an extra ground waypoint this many m ahead of the spawn with the takeoff speed, in case the AI doesn't start rolling " +
            "or accelerating towards a first waypoint in the air (0–60 % of the runway). Empty or 0 = none.")] double? rollM = null,
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

        try
        {
            var status = await tests.StartTestAsync(new TrafficTestRequest(airport, runway, parsed, title, livery, spawnNm, gearMethod, finalLowFt, touchdownM, approachKt, speed, goAround, rollM, throttlePercent, kinematicNm), cancellationToken);
            return Json.Serialize(new { ok = true, status });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or ArgumentException)
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
        "go_around_ignored = went around but touched down anyway; takeoff B1: climbed, climbing, rolling, no_roll, no_liftoff), go-around " +
        "(reason, where, height, lowest and highest point after it, seconds until the gear was up, whether 2800 ft were reached), takeoff " +
        "(roll start, liftoff point, speed and pitch, roll distance, height over the runway end, climb rate, pitch and bank up to 1500 ft, " +
        "gear up, lights at liftoff), kinematics (phase, where Duopilo took over, flare, touchdown, ground height), speed deviation, height " +
        "above the runway when the gear was down and whether that was by 2500 ft, values " +
        "at the approach gates (altitude deviation from the glide path, pitch), approach pitch, touchdown point, speed, sink rate, pitch and " +
        "flare (pitch gain before touchdown), max bank on final, lights on final and after landing, float seconds, stop point. Paths of " +
        "the CSV and summary files.")]
    public static string Status(
        TrafficTestService tests,
        [Description("Only the test on this runway, e.g. 05L or 'EDDL 05L'. Empty = all tests.")] string? runway = null)
    {
        var all = tests.Status(runway);
        return Json.Serialize(new { ok = true, enabled = tests.Enabled, running = all.Count(t => t.State == "running"), tests = all });
    }

    [McpServerTool(Name = "traffic_test_stop", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Ends traffic tests: removes the test aircraft from the sim and writes the evaluation next to the CSV. Without runway all tests " +
        "end. Returns the final state of all tests.")]
    public static async Task<string> Stop(
        TrafficTestService tests,
        [Description("Only the test on this runway, e.g. 05L or 'EDDL 05L'. Empty = all tests.")] string? runway = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await tests.StopTestAsync("stopped", cancellationToken, runway);
            return Json.Serialize(new { ok = true, running = all.Count(t => t.State == "running"), tests = all });
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
        "Each model has 'type' (ICAO type from Duopilo's approval list), 'category' (airliner, generalaviation) and 'status': 'verified' " +
        "(gear and landing checked in the sim), 'candidate' (landed in a test, not looked at yet), 'untested' (on the list, e.g. the MSFS " +
        "2024 GA aircraft, not tested yet), 'excluded' (FlyByWire, Headwind: as AI their gear only comes half out and the engines don't " +
        "spin) or 'unknown' (not on the list). 'approved' lists the approval list itself.")]
    public static async Task<string> Models(
        TrafficTestService tests,
        [Description("Words that must all appear in title or livery, case-insensitive. Empty = all.")] string? filter = null,
        [Description("Maximum number of models in the answer. Default 50.")] int limit = DefaultModelLimit,
        [Description("Only models of the approval list (verified, candidate and untested).")] bool approvedOnly = false,
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
                approved = TrafficModels.Approved.Select(t => new
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
