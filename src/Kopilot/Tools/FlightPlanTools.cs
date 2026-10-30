using System.ComponentModel;
using System.Globalization;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Sim;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>Flugplan und Fortschritt aus dem SimBrief-OFP (die Airliner-FMS geben ihre Route nicht über SimConnect heraus).</summary>
[McpServerToolType]
public static class FlightPlanTools
{
    [McpServerTool(Name = "get_flight_plan", ReadOnly = true)]
    [Description(
        "The pilot's latest SimBrief flight plan (OFP): callsign, origin/destination/alternate with planned runways, route, initial altitude, cost index, " +
        "fuel plan and weights in kg, scheduled times (UTC) and the navlog fixes with airway, planned altitude, cumulative distance, planned time and fuel on board. " +
        "This is the plan, not the FMS state. Use refresh=true after the pilot generated a new OFP.")]
    public static async Task<string> GetFlightPlan(
        SimBriefService simBrief,
        AlertBus alerts,
        [Description("Load the OFP again from SimBrief instead of the cache.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var ofp = await simBrief.GetAsync(refresh, cancellationToken);
        if (ofp is null)
        {
            return Json.Serialize(new { loaded = false, message = simBrief.LastError, activeAlerts = AlertView.Active(alerts) });
        }

        return Json.Serialize(new
        {
            loaded = true,
            fetchedUtc = simBrief.FetchedUtc,
            generatedUtc = ofp.GeneratedUtc,
            warning = simBrief.LastError,
            callsign = ofp.Callsign,
            flightNumber = ofp.FlightNumber,
            aircraft = ofp.AircraftIcao,
            registration = ofp.Registration,
            origin = Airport(ofp.Origin),
            destination = Airport(ofp.Destination),
            alternate = ofp.Alternate is { } alt ? Airport(alt) : null,
            route = ofp.Route,
            initialAltitudeFt = ofp.InitialAltitudeFt,
            costIndex = ofp.CostIndex,
            airDistanceNm = ofp.AirDistanceNm,
            avgWindComponentKt = ofp.AvgWindComponentKt,
            fuelKg = ofp.Fuel,
            weightsKg = ofp.Weights,
            times = new
            {
                scheduledOutUtc = Clock(ofp.Times.ScheduledOutUtc),
                scheduledOffUtc = Clock(ofp.Times.ScheduledOffUtc),
                scheduledOnUtc = Clock(ofp.Times.ScheduledOnUtc),
                scheduledInUtc = Clock(ofp.Times.ScheduledInUtc),
                estTimeEnroute = Fmt.Duration(ofp.Times.EstTimeEnrouteSec),
            },
            fixes = ofp.Fixes.Select(f => new
            {
                ident = f.Ident,
                airway = f.Airway,
                stage = f.Stage,
                sidStar = f.IsSidStar,
                altitudeFt = f.AltitudeFt,
                distanceNm = Fmt.Int(f.CumulativeDistanceNm),
                planTime = Fmt.Duration(f.TimeTotalSec),
                fuelOnboardKg = Fmt.Int(f.FuelPlanOnboardKg),
            }),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_progress", ReadOnly = true)]
    [Description(
        "Flight progress against the SimBrief plan, computed from the aircraft position: next fix with distance and ETA (taken from the FMS active waypoint when the " +
        "aircraft profile provides it, e.g. after DIR TO – 'source': 'fms'), distance and time to destination, " +
        "distance to top of descent, cross-track deviation from the planned route, planned altitude at the next fix, and fuel on board versus plan " +
        "with predicted landing fuel (kg). Times are UTC. 'etaSource': 'simbrief' = ETE/ETA from the planned navlog times (planned winds, climb and descent), " +
        "shifted by the deviation from plan at the last fix passed; 'groundspeed' = remaining distance at current ground speed (navlog without times). " +
        "'fuelCheck': Kopilot's automatic cruise fuel checks (at every OFP waypoint and at least every 'intervalMinutes'): the last check and the recent " +
        "trend (diffKg = actual minus plan, changeKg = change since the previous check, negative = worse), plus refuelling it detected.")]
    public static async Task<string> GetProgress(
        SimConnectClient sim,
        SimBriefService simBrief,
        AlertBus alerts,
        MonitoringService monitoring,
        CancellationToken cancellationToken = default)
    {
        if (!sim.Connection.IsConnected)
        {
            return KopilotTools.NotConnected(alerts);
        }

        var ofp = await simBrief.GetAsync(false, cancellationToken);
        if (ofp is null)
        {
            return Json.Serialize(new { connected = true, loaded = false, message = simBrief.LastError, activeAlerts = AlertView.Active(alerts) });
        }

        if (sim.Num("lat") is not { } lat || sim.Num("lon") is not { } lon)
        {
            return Json.Serialize(new { connected = true, loaded = true, message = "Keine Position vom Sim.", activeAlerts = AlertView.Active(alerts) });
        }

        var points = ofp.RoutePoints();
        var progress = simBrief.Locate(ofp, points, lat, lon);
        if (progress is null)
        {
            return Json.Serialize(new { connected = true, loaded = true, message = "Der SimBrief-Plan enthält keine verwertbare Route.", activeAlerts = AlertView.Active(alerts) });
        }

        var gs = sim.Num("gs");
        var zulu = sim.Num("zuluTimeSec");
        var signals = new AircraftSignals(sim);
        var passage = simBrief.ObservePassage(ofp, points, progress, zulu, gs);
        var fp = ProgressCalculator.Compute(ofp, points, progress, sim.Num("fuelTotalKg"), signals.FmsWaypoint(lat, lon));
        var eta = EtaCalculator.Predict(ofp, points, fp, gs, passage);
        var destDist = fp.DistanceToGoNm;
        var xt = progress.CrossTrackNm;

        return Json.Serialize(new
        {
            connected = true,
            loaded = true,
            route = $"{ofp.Origin.Icao} → {ofp.Destination.Icao}",
            groundSpeedKt = Fmt.Int(gs),
            etaSource = eta.Source,
            previousFix = fp.Previous.Ident,
            nextFix = new
            {
                ident = fp.Next.Ident,
                source = fp.NextSource,
                airway = fp.NextFix?.Airway,
                distanceNm = Math.Round(fp.DistanceToNextNm, 1),
                bearingTrueDeg = Fmt.Heading(RouteMath.BearingDeg(lat, lon, fp.Next.Lat, fp.Next.Lon)),
                ete = Fmt.Duration(eta.NextSec),
                etaUtc = Eta(zulu, eta.NextSec),
                plannedAltitudeFt = fp.NextFix?.AltitudeFt,
                plannedStage = fp.NextFix?.Stage,
            },
            fmsWaypointNotInPlan = fp.FmsWaypointNotInPlan ? true : (bool?)null,
            following = fp.Following,
            topOfClimb = Marker(fp.TopOfClimb, eta.TopOfClimbSec, zulu),
            topOfDescent = Marker(fp.TopOfDescent, eta.TopOfDescentSec, zulu),
            topOfDescentSource = fp.TopOfDescent is null ? null : "simbrief",
            destination = new
            {
                icao = ofp.Destination.Icao,
                runway = ofp.Destination.PlanRunway,
                distanceNm = Math.Round(destDist),
                ete = Fmt.Duration(eta.DestinationSec),
                etaUtc = Eta(zulu, eta.DestinationSec),
                scheduledOnUtc = Clock(ofp.Times.ScheduledOnUtc),
                versusSchedule = VersusSchedule(ofp, zulu, eta.DestinationSec),
            },
            crossTrackNm = Math.Round(xt, 1),
            crossTrackSide = Math.Abs(xt) < 0.1 ? null : xt > 0 ? "right" : "left",
            onRoute = fp.OnRoute,
            fuel = new
            {
                actualKg = Fmt.Int(fp.ActualFuelKg),
                plannedHereKg = Fmt.Int(fp.PlannedFuelHereKg),
                differenceKg = Fmt.Int(fp.FuelDifferenceKg),
                plannedLandingKg = Fmt.Int(ofp.Fuel.PlanLandingKg),
                predictedLandingKg = Fmt.Int(fp.PredictedLandingFuelKg),
                minimumAtDestinationKg = Fmt.Int(ofp.Fixes.Count > 0 ? ofp.Fixes[^1].FuelMinOnboardKg : null),
            },
            fuelCheck = FuelCheckView(monitoring, FuelPlanSample.PlanIdOf(ofp)),
            note = fp.FmsWaypointNotInPlan
                ? "Das FMS fliegt einen Punkt an, der nicht im SimBrief-Plan steht – nächster Fix und Distanzen kommen aus dem Plan."
                : fp.InTerminalArea ? "Im SID/STAR- oder Flughafenbereich weicht die Flugbahn oft vom Plan ab (Vektoren)." : null,
            activeAlerts = AlertView.Active(alerts),
        });
    }

    /// <summary>Fuel Check für get_progress: letzter Check, Verlauf (kompakt) und Grenzen. Null, wenn der Verlauf zu einem anderen Plan gehört.</summary>
    internal static object? FuelCheckView(MonitoringService monitoring, string planId, int last = 12)
    {
        var log = monitoring.FuelChecks;
        if (log.PlanId != planId)
        {
            return null;
        }

        var settings = monitoring.FuelCheckSettings;
        return new
        {
            last = log.LastCheck is { } c ? FuelCheckItem(c) : null,
            history = log.Last(last).Select(FuelCheckItem),
            settings = new
            {
                intervalMinutes = settings.IntervalMinutes,
                deviationIncreaseKg = settings.DeviationIncreaseKg,
                landingBufferKg = settings.LandingBufferKg,
                minSpacingMinutes = settings.MinSpacingMinutes,
            },
        };
    }

    private static object FuelCheckItem(FuelCheckEntry e) => new
    {
        kind = e.Kind,
        timeUtc = e.ZuluSec is { } z ? Fmt.ClockTime(z) + "Z" : e.TimeUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z",
        fix = e.Ident,
        actualKg = Fmt.Int(e.ActualKg),
        plannedKg = Fmt.Int(e.PlannedKg),
        diffKg = Fmt.Int(e.DiffKg),
        changeKg = Fmt.Int(e.ChangeKg),
        predictedLandingKg = Fmt.Int(e.PredictedLandingKg),
        minimumLandingKg = Fmt.Int(e.MinimumLandingKg),
        fromKg = Fmt.Int(e.FromKg),
        warnings = e.Warnings.Count > 0 ? e.Warnings : null,
        callout = e.Callout,
    };

    private static object? Marker(RouteMarker? marker, double? eteSec, double? zulu) =>
        marker is null ? null : new { distanceNm = Math.Round(marker.DistanceNm), ete = Fmt.Duration(eteSec), etaUtc = Eta(zulu, eteSec) };

    /// <summary>Ankunft gegen den Plan ("+12 min" / "-5 min"). Der Sim kennt nur die Uhrzeit, daher modulo 24 h.</summary>
    internal static string? VersusSchedule(Ofp ofp, double? zulu, double? destinationEteSec)
    {
        if (zulu is not { } z || destinationEteSec is not { } ete || ofp.Times.ScheduledOnUtc is not { } schedOn)
        {
            return null;
        }

        var etaSec = z + ete;
        var schedSec = schedOn.UtcDateTime.TimeOfDay.TotalSeconds;
        var deltaMin = ((etaSec - schedSec) % 86400 + 86400) % 86400 / 60;
        if (deltaMin > 720)
        {
            deltaMin -= 1440;
        }

        return deltaMin >= 0 ? $"+{deltaMin:0} min" : $"{deltaMin:0} min";
    }

    private static object Airport(OfpAirport a) => new
    {
        icao = a.Icao,
        name = a.Name,
        plannedRunway = a.PlanRunway,
        elevationFt = a.ElevationFt,
        transitionAltitudeFt = a.TransitionAltitudeFt,
        transitionLevelFt = a.TransitionLevelFt,
        metar = a.Metar,
    };

    internal static string? Clock(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z" : null;

    /// <summary>Uhrzeit (UTC) in <paramref name="eteSec"/> Sekunden ab der Sim-Uhrzeit.</summary>
    internal static string? Eta(double? zulu, double? eteSec) =>
        zulu is { } z && eteSec is { } e ? Fmt.ClockTime(z + e) + "Z" : null;
}
