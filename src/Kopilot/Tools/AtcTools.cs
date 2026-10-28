using System.ComponentModel;
using Kopilot.Atc;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Sim;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>Was das Ingame-ATC von MSFS 2024 über SimVars preisgibt.</summary>
[McpServerToolType]
public static class AtcTools
{
    [McpServerTool(Name = "get_atc", ReadOnly = true)]
    [Description(
        "MSFS in-game ATC state: callsign, clearance flags (IFR, taxi, takeoff, landing), and the runway assigned by ATC with number, " +
        "L/C/R designator (taken from the SimBrief plan when the number matches), airport and the spoken form for callouts " +
        "(e.g. 'runway two three left'). Assigned altitude, heading and squawk are NOT exposed by MSFS as SimVars; 'experimental' lists " +
        "related ATC variables whose meaning still has to be confirmed in flight. Third-party ATC (BeyondATC, SayIntentions) is not visible here.")]
    public static string GetAtc(SimConnectClient sim, SimBriefService simBrief, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return KopilotTools.NotConnected(alerts);
        }

        var runway = AtcReader.AssignedRunway(sim, simBrief.Current);
        var airline = sim.Str("atcAirline");
        var flightNumber = sim.Str("atcFlightNumber");
        return Json.Serialize(new
        {
            connected = true,
            callsign = airline is not null || flightNumber is not null ? $"{airline} {flightNumber}".Trim() : null,
            atcId = sim.Str("atcId"),
            clearances = new
            {
                ifr = sim.Bool("atcClearedIfr"),
                taxi = sim.Bool("atcClearedTaxi"),
                takeoff = sim.Bool("atcClearedTakeoff"),
                landing = sim.Bool("atcClearedLanding"),
                note = "Nicht jede Freigabe setzt die Variable – im Testflug blieben IFR und LANDING auf 0.",
            },
            runwaySelected = sim.Bool("atcRunwaySelected"),
            assignedRunway = runway is null
                ? null
                : new
                {
                    ident = runway.Ident,
                    number = runway.Number,
                    designator = runway.Designator,
                    designatorSource = runway.DesignatorSource,
                    airport = runway.Airport,
                    headingMagDeg = Fmt.Heading(runway.HeadingMagDeg),
                    lengthFt = Fmt.Int(sim.Num("atcRunwayLengthFt")),
                    spoken = "runway " + runway.Spoken,
                    callout = $"Runway {runway.Spoken} assigned",
                    note = runway.Designator is null ? "L/C/R ist über SimConnect nicht verfügbar und passt zu keiner Bahn im SimBrief-Plan." : null,
                },
            experimental = new
            {
                currentWaypointAltitudeFt = Fmt.Int(sim.Num("atcCurrentWpAltFt")),
                previousWaypointAltitudeFt = Fmt.Int(sim.Num("atcPreviousWpAltFt")),
                flightPlanDiffAltitudeFt = Fmt.Int(sim.Num("atcFpDiffAltFt")),
                flightPlanDiffHeadingDeg = Fmt.Int(sim.Num("atcFpDiffHdgDeg")),
                flightPlanDiffDistanceNm = Fmt.Round(sim.Num("atcFpDiffDistM") / 1852, 1),
                airportTowered = sim.Bool("atcAirportTowered"),
                onParkingSpot = sim.Bool("atcOnParkingSpot"),
            },
            transponder = Fmt.Squawk(sim.Num("xpdrCode")),
            notAvailable = new[] { "assigned altitude", "assigned heading", "assigned squawk" },
            activeAlerts = AlertView.Active(alerts),
        });
    }
}
