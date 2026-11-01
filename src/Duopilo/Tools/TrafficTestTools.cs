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
        "command at 3000 ft above the runway so it is down by 2500 ft (the evaluation checks that). Up to 4 tests at the same time, one per runway (e.g. a parallel approach: one call for 05L, one for 05R); starting a " +
        "test on a runway that already has one replaces it. Pick runways the pilot is not using right now. Samples (1 per second) go to one CSV " +
        "per test in the data folder (traffic-tests). Follow with traffic_test_status, end with traffic_test_stop (the aircraft is removed; " +
        "after MaxMinutes, default 15, also without stop). Lights: nav, beacon, strobes, landing, taxi and logo on at spawn; after touchdown " +
        "below 60 kt landing lights and strobes off. Models outside the approval list (traffic_test_models: status) get a warning.")]
    public static async Task<string> Start(
        TrafficTestService tests,
        [Description("ICAO code of the airport, e.g. EDDL. Must be within 80 NM of the pilot's aircraft.")] string airport,
        [Description("Runway end to land on, e.g. 23L (5L and 05L are the same).")] string runway,
        [Description("A1 (gear down from spawn, 5 NM) or A2 (spawn 15 NM with the gear up, gear down at 3000 ft above the runway). Default A1.")] string variant = "A1",
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
            "below, Vapp at the threshold. Empty = from the approval list (traffic_test_models: approachKt), else 135.")] double? approachKt = null,
        [Description(
            "Control the speed on approach via the simvar AI DESIRED SPEED (default true) – the AI ignores the speed in the waypoints and " +
            "flew 160–169 kt with every model. false = waypoint speeds only, as before.")] bool speedControl = true,
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

        try
        {
            var status = await tests.StartTestAsync(new TrafficTestRequest(airport, runway, parsed, title, livery, spawnNm, gearMethod, finalLowFt, touchdownM, approachKt, speedControl), cancellationToken);
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
        "evaluation: verdict (stopped, landed, floated = hovered above the runway without touching down, airborne), speed deviation, height " +
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
