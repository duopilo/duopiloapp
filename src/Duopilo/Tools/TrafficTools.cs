using System.ComponentModel;
using System.Globalization;
using Kopilot.Localization;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Traffic;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>Verkehr um das eigene Flugzeug (KI-Verkehr und Multiplayer) über SimConnect.</summary>
[McpServerToolType]
public static class TrafficTools
{
    public const int DefaultLimit = 10;

    public const int MaxLimit = 50;

    [McpServerTool(Name = "get_traffic", ReadOnly = true)]
    [Description(
        "Other aircraft around the pilot's aircraft in MSFS 2024 (AI traffic and multiplayer, if enabled in the sim; helicopters included), " +
        "sorted by distance. Per target: callsign/ATC ID, aircraft type (ATC MODEL), horizontal distance in NM, relative bearing as clock position " +
        "(e.g. '2 o'clock') and true bearing, altitude and altitude difference in ft (positive = above), vertical trend (climbing/descending/level, " +
        "±500 ft/min), ground speed in knots, heading, on ground. " +
        "Altitude difference: the target's altitude as SimConnect reports it (for MSFS live traffic its barometric altitude) minus the own " +
        "pressure altitude, like TCAS and flight levels ('ownAircraft.altitudeReference': 'pressure'; 'true' = own true altitude, if no pressure " +
        "altitude is available or the setting says so). " +
        "Ground speed: 'groundSpeedSource' 'position' = derived from the target's position change between traffic queries (reliable); " +
        "'simvar' = the sim's GROUND VELOCITY, which is far too low for MSFS live traffic (roughly a factor of 3) – don't state it as fact. " +
        "Calling get_traffic again after about 10–20 s gives position-based values. The nearest target has 'closest': true, " +
        "and 'callout' is a short traffic call for it, e.g. 'Traffic, 2 o'clock, 5 miles, 1000 feet above, descending'. " +
        "If 'status' is 'no_traffic', no other aircraft are within the radius (or traffic is disabled in the sim). " +
        "'messageCheck' is a diagnostic of the raw SimConnect replies (crash analysis); mention it only when asked.")]
    public static async Task<string> GetTraffic(
        SimConnectClient sim,
        TrafficService traffic,
        AlertBus alerts,
        [Description("Search radius in nautical miles. Default 40, maximum 108 (200 km, the SimConnect limit).")] double radiusNm = TrafficService.DefaultRadiusNm,
        [Description("Maximum number of targets returned, nearest first (1–50). Default 10.")] int limit = DefaultLimit,
        [Description("Only airborne targets (skip aircraft on the ground, e.g. parked at the gate). Default false.")] bool airborneOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!sim.Connection.IsConnected)
        {
            return KopilotTools.NotConnected(alerts);
        }

        var status = sim.Status;
        var maxTargets = Math.Clamp(limit, 1, MaxLimit);
        var radius = TrafficService.ClampRadius(radiusNm);
        var radiusNote = double.IsFinite(radiusNm) && radiusNm > TrafficService.MaxRadiusNm
            ? Loc.T("tools.traffic.radiusLimited", Nm(TrafficService.MaxRadiusNm))
            : null;

        if (status.FlightRunning == false)
        {
            return Json.Serialize(new
            {
                connected = true,
                status = "no_flight",
                radiusNm = Fmt.Round(radius, 1),
                totalInRadius = 0,
                targets = Array.Empty<object>(),
                callout = (string?)null,
                message = Loc.T("tools.traffic.noFlight"),
                activeAlerts = AlertView.Active(alerts),
            });
        }

        var result = await traffic.ScanAsync(radius, cancellationToken);
        var picture = result.Picture;
        var all = airborneOnly ? picture.Targets.Where(t => !t.OnGround).ToList() : picture.Targets.ToList();
        var shown = all.Take(maxTargets).ToList();

        var (state, message) = Describe(result, all.Count, airborneOnly);
        return Json.Serialize(new
        {
            connected = true,
            paused = status.Paused,
            pauseKind = status.PauseKind,
            status = state,
            radiusNm = Fmt.Round(result.RadiusNm, 1),
            limit = maxTargets,
            airborneOnly,
            totalInRadius = all.Count,
            targets = shown.Select((t, i) => View(t, closest: i == 0)),
            callout = shown.FirstOrDefault()?.Callout,
            ownAircraft = picture.Own is { } own
                ? new
                {
                    altitudeFt = Fmt.Int(own.ReferenceAltitudeFt),
                    altitudeReference = own.AltitudeReference,
                    pressureAltitudeFt = Fmt.Int(own.PressureAltitudeFt),
                    trueAltitudeFt = Fmt.Int(own.AltitudeFt),
                    headingTrueDeg = Fmt.Heading(own.HeadingTrueDeg),
                    onGround = own.OnGround,
                }
                : null,
            message = Join(message, radiusNote),
            messageCheck = MessageCheck(traffic.Diagnostics, traffic.MessageLogPath),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    /// <summary>
    /// Prüfung der SimConnect-Antworten (Absturzanalyse Testlog 0.9): Größe der leeren Antworten und kurze Datenblöcke.
    /// </summary>
    public static object MessageCheck(TrafficMessageStatsSnapshot d, string? logPath) => new
    {
        messages = d.Messages,
        emptyReply = d.EmptyReplyVerdict,
        shortBlock = d.ShortBlockVerdict,
        sizeMismatches = d.SizeMismatches,
        log = logPath,
    };

    /// <summary>Status und Hinweis zum Ergebnis.</summary>
    public static (string Status, string? Message) Describe(TrafficScanResult result, int targetCount, bool airborneOnly)
    {
        if (result.Error is { } error)
        {
            return ("error", Loc.T("tools.traffic.queryFailed", error));
        }

        var picture = result.Picture;
        if (picture.Own is null)
        {
            return result.Complete
                ? ("no_position", Loc.T("tools.traffic.noOwnPosition"))
                : ("no_response", Loc.T("tools.traffic.noResponse"));
        }

        if (targetCount > 0)
        {
            return ("ok", result.Complete ? null : Loc.T("tools.traffic.incomplete"));
        }

        var radius = Nm(result.RadiusNm);
        if (picture.OthersReceived == 0)
        {
            return ("no_traffic", Loc.T("tools.traffic.noneMaybeDisabled", radius));
        }

        return ("no_traffic", airborneOnly
            ? Loc.T("tools.traffic.noneAirborne", radius)
            : Loc.T("tools.traffic.none", radius));
    }

    public static object View(TrafficTarget t, bool closest) => new
    {
        ident = t.Ident,
        callsign = t.Callsign,
        atcId = t.AtcId,
        type = t.Type,
        category = t.Category,
        distanceNm = Fmt.Round(t.DistanceNm, 1),
        clock = t.ClockText,
        relativeBearingDeg = Fmt.Heading(t.RelativeBearingDeg),
        bearingTrueDeg = Fmt.Heading(t.BearingTrueDeg),
        altitudeFt = Fmt.Int(t.AltitudeFt),
        altitudeDiffFt = Fmt.Int(t.AltitudeDiffFt),
        verticalTrend = t.VerticalTrend,
        verticalSpeedFpm = Fmt.Int(t.VerticalSpeedFpm),
        verticalSpeedSource = t.VerticalSpeedDerived ? "derived" : null,
        groundSpeedKt = Fmt.Int(t.GroundSpeedKt),
        groundSpeedSource = t.GroundSpeedSource,
        groundSpeedBaselineSec = Fmt.Int(t.GroundSpeedBaselineSec),
        groundSpeedSimVarKt = t.GroundSpeedSource == TrafficTarget.GroundSpeedFromPosition ? Fmt.Int(t.GroundSpeedSimVarKt) : null,
        headingTrueDeg = Fmt.Heading(t.HeadingTrueDeg),
        headingMagDeg = Fmt.Heading(t.HeadingMagDeg),
        onGround = t.OnGround,
        closest,
        callout = t.Callout,
        objectId = t.ObjectId,
    };

    private static string Nm(double nm) => Math.Round(nm, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;
}
