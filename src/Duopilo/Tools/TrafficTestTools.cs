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
        "Developer test tool (off by default, Duopilo:TrafficTest:Enabled): spawns one AI aircraft (tail DUO01, no ATC control) that flies a " +
        "final approach to a runway by waypoints, lands and stops on the runway – to find out whether Duopilo can run its own traffic. " +
        "Variant A1: spawn 5 NM out on the extended centerline with the gear already down. Variant A2: spawn 15 NM out with the gear up, gear " +
        "down at 10 NM. Only one test at a time; starting a new one ends the previous. Pick a runway the pilot is not using right now. " +
        "Samples (1 per second) go to a CSV in the data folder (traffic-tests). Follow with traffic_test_status, end with traffic_test_stop " +
        "(the aircraft is removed; after MaxMinutes, default 15, also without stop).")]
    public static async Task<string> Start(
        TrafficTestService tests,
        [Description("ICAO code of the airport, e.g. EDDL. Must be within 80 NM of the pilot's aircraft.")] string airport,
        [Description("Runway end to land on, e.g. 23L (5L and 05L are the same).")] string runway,
        [Description("A1 (gear down from spawn, 5 NM) or A2 (gear up, gear down at 10 NM, spawn 15 NM). Default A1.")] string variant = "A1",
        [Description("Aircraft title as listed by traffic_test_models. Empty = automatic (an FSLTL A320 if installed, else any A320).")] string? title = null,
        [Description("Livery name for the title (MSFS 2024 modular aircraft). Empty = default livery.")] string? livery = null,
        [Description("Spawn distance before the threshold in NM (2–30). Empty = default of the variant.")] double? spawnNm = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<TrafficTestVariant>(variant?.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Json.Serialize(new { ok = false, error = Loc.T("tools.trafficTest.variantUnknown", variant ?? "") });
        }

        try
        {
            var status = await tests.StartTestAsync(new TrafficTestRequest(airport, runway, parsed, title, livery, spawnNm), cancellationToken);
            return Json.Serialize(new { ok = true, status });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or ArgumentException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "traffic_test_status", ReadOnly = true)]
    [Description(
        "State of the traffic test (traffic_test_start): running/ended, last sample (altitude, IAS against the requested speed, ground speed, " +
        "vertical speed, gear, distance to the threshold, position relative to the centerline, AI state), events (spawn, gear, touchdown, " +
        "stopped …) and the evaluation: verdict (stopped, landed, floated = hovered above the runway without touching down, airborne), " +
        "speed deviation, values at the approach gates, touchdown point and speed, float seconds, stop point. Paths of the CSV and summary files.")]
    public static string Status(TrafficTestService tests) =>
        Json.Serialize(new { ok = true, enabled = tests.Enabled, status = tests.Status() });

    [McpServerTool(Name = "traffic_test_stop", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Ends the traffic test: removes the test aircraft from the sim and writes the evaluation next to the CSV. Returns the final status.")]
    public static async Task<string> Stop(TrafficTestService tests, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await tests.StopTestAsync("stopped", cancellationToken);
            return Json.Serialize(new { ok = true, status });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "traffic_test_models", ReadOnly = true)]
    [Description(
        "Aircraft models installed in MSFS 2024 that can be spawned (title and livery, from EnumerateSimObjectsAndLiveries), for " +
        "traffic_test_start. Filter by words, e.g. 'A320 FSLTL' or 'Lufthansa'. 'suggested' is the model traffic_test_start takes without a title.")]
    public static async Task<string> Models(
        TrafficTestService tests,
        [Description("Words that must all appear in title or livery, case-insensitive. Empty = all.")] string? filter = null,
        [Description("Maximum number of models in the answer. Default 50.")] int limit = DefaultModelLimit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await tests.ModelsAsync(null, cancellationToken);
            var matched = TrafficTestService.Filter(all, filter);
            var max = Math.Clamp(limit, 1, 500);
            var suggested = TrafficTestService.PickDefaultModel(all);
            return Json.Serialize(new
            {
                ok = true,
                total = all.Count,
                matched = matched.Count,
                truncated = matched.Count > max,
                suggested = suggested is null ? null : new { title = suggested.Title, livery = suggested.Livery },
                models = matched.Take(max).Select(m => new { title = m.Title, livery = m.Livery }),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return Json.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
