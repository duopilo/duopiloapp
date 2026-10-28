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
        "Flight progress against the SimBrief plan, computed from the aircraft position: next fix with distance and ETA, distance and time to destination, " +
        "distance to top of descent, cross-track deviation from the planned route, planned altitude at the next fix, and fuel on board versus plan " +
        "with predicted landing fuel (kg). Times are UTC.")]
    public static async Task<string> GetProgress(
        SimConnectClient sim,
        SimBriefService simBrief,
        AlertBus alerts,
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
        var next = points[progress.NextPointIndex];
        var nextFix = next.FixIndex >= 0 ? ofp.Fixes[next.FixIndex] : null;
        var prev = points[progress.LegIndex];

        // Restliche Strecke bis zu einem Punkt der Route
        double DistanceTo(int pointIndex)
        {
            var d = progress.DistanceToNextNm;
            for (var i = progress.NextPointIndex; i < pointIndex; i++)
            {
                d += RouteMath.DistanceNm(points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon);
            }

            return d;
        }

        object? Marker(string ident)
        {
            var index = -1;
            for (var i = progress.NextPointIndex; i < points.Count; i++)
            {
                if (points[i].Ident.Equals(ident, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return null;
            }

            var dist = DistanceTo(index);
            return new { distanceNm = Math.Round(dist), ete = Ete(dist, gs), etaUtc = Eta(zulu, dist, gs) };
        }

        // Treibstoff: Planwert an der aktuellen Position, linear zwischen den Fixes
        var fuelPrev = prev.FixIndex >= 0 ? ofp.Fixes[prev.FixIndex].FuelPlanOnboardKg : ofp.Fuel.PlanTakeoffKg;
        var fuelNext = nextFix?.FuelPlanOnboardKg;
        double? plannedHere = fuelPrev is { } fp && fuelNext is { } fn ? fp + (fn - fp) * progress.LegFraction : fuelNext ?? fuelPrev;
        var actual = sim.Num("fuelTotalKg");
        double? diff = actual is { } a && plannedHere is { } p ? a - p : null;
        double? predictedLanding = ofp.Fuel.PlanLandingKg is { } landing && diff is { } dd ? landing + dd : null;

        var destDist = progress.DistanceToGoNm;
        string? delay = null;
        if (zulu is { } z && gs is > 50 && ofp.Times.ScheduledOnUtc is { } schedOn)
        {
            var etaSec = z + destDist / gs.Value * 3600;
            var schedSec = schedOn.UtcDateTime.TimeOfDay.TotalSeconds;
            var deltaMin = ((etaSec - schedSec) % 86400 + 86400) % 86400 / 60;
            if (deltaMin > 720)
            {
                deltaMin -= 1440;
            }

            delay = deltaMin >= 0 ? $"+{deltaMin:0} min" : $"{deltaMin:0} min";
        }

        var xt = progress.CrossTrackNm;
        var inTerminal = nextFix?.IsSidStar == true || progress.LegIndex == 0 || progress.NextPointIndex == points.Count - 1;
        return Json.Serialize(new
        {
            connected = true,
            loaded = true,
            route = $"{ofp.Origin.Icao} → {ofp.Destination.Icao}",
            groundSpeedKt = Fmt.Int(gs),
            previousFix = prev.Ident,
            nextFix = new
            {
                ident = next.Ident,
                airway = nextFix?.Airway,
                distanceNm = Math.Round(progress.DistanceToNextNm, 1),
                bearingTrueDeg = Fmt.Heading(RouteMath.BearingDeg(lat, lon, next.Lat, next.Lon)),
                ete = Ete(progress.DistanceToNextNm, gs),
                etaUtc = Eta(zulu, progress.DistanceToNextNm, gs),
                plannedAltitudeFt = nextFix?.AltitudeFt,
                plannedStage = nextFix?.Stage,
            },
            following = points.Skip(progress.NextPointIndex + 1).Take(3).Select(pt => pt.Ident),
            topOfClimb = Marker("TOC"),
            topOfDescent = Marker("TOD"),
            destination = new
            {
                icao = ofp.Destination.Icao,
                runway = ofp.Destination.PlanRunway,
                distanceNm = Math.Round(destDist),
                ete = Ete(destDist, gs),
                etaUtc = Eta(zulu, destDist, gs),
                scheduledOnUtc = Clock(ofp.Times.ScheduledOnUtc),
                versusSchedule = delay,
            },
            crossTrackNm = Math.Round(xt, 1),
            crossTrackSide = Math.Abs(xt) < 0.1 ? null : xt > 0 ? "right" : "left",
            onRoute = Math.Abs(xt) <= (inTerminal ? 10 : 3),
            fuel = new
            {
                actualKg = Fmt.Int(actual),
                plannedHereKg = Fmt.Int(plannedHere),
                differenceKg = Fmt.Int(diff),
                plannedLandingKg = Fmt.Int(ofp.Fuel.PlanLandingKg),
                predictedLandingKg = Fmt.Int(predictedLanding),
                minimumAtDestinationKg = Fmt.Int(ofp.Fixes.Count > 0 ? ofp.Fixes[^1].FuelMinOnboardKg : null),
            },
            note = inTerminal ? "Im SID/STAR- oder Flughafenbereich weicht die Flugbahn oft vom Plan ab (Vektoren)." : null,
            activeAlerts = AlertView.Active(alerts),
        });
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

    private static string? Clock(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z" : null;

    private static string? Ete(double distanceNm, double? gs) => gs is > 50 ? Fmt.Duration(distanceNm / gs.Value * 3600) : null;

    private static string? Eta(double? zulu, double distanceNm, double? gs) =>
        zulu is { } z && gs is > 50 ? Fmt.ClockTime(z + distanceNm / gs.Value * 3600) + "Z" : null;
}
