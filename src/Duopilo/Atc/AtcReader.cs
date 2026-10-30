using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Atc;

/// <summary>Liest, was das Ingame-ATC von MSFS über SimVars preisgibt.</summary>
public static class AtcReader
{
    /// <summary>Zugewiesene Bahn, falls ATC eine gewählt hat. L/C/R kommt aus dem SimBrief-Plan, sofern die Nummer passt.</summary>
    public static AssignedRunway? AssignedRunway(SimConnectClient sim, Ofp? ofp)
    {
        if (sim.Bool("atcRunwaySelected") != true)
        {
            return null;
        }

        var airport = sim.Str("atcRunwayAirport");
        return RunwayMath.FromAtc(
            sim.Num("atcRunwayHeadingTrue"),
            sim.Num("hdgTrue"),
            sim.Num("hdgMag"),
            airport,
            PlannedRunways(airport, ofp, sim.Bool("onGround") == true));
    }

    /// <summary>Geplante Bahnen in sinnvoller Reihenfolge: passender Flughafen zuerst, sonst am Boden der Start-, in der Luft der Zielflughafen.</summary>
    public static IEnumerable<string?> PlannedRunways(string? atcAirport, Ofp? ofp, bool onGround)
    {
        if (ofp is null)
        {
            return [];
        }

        var airports = new List<OfpAirport> { ofp.Origin, ofp.Destination };
        if (ofp.Alternate is { } alt)
        {
            airports.Add(alt);
        }

        if (!string.IsNullOrWhiteSpace(atcAirport))
        {
            var match = airports.FirstOrDefault(a =>
                (a.Icao is not null && atcAirport.Contains(a.Icao, StringComparison.OrdinalIgnoreCase))
                || (a.Name is not null && (a.Name.Contains(atcAirport, StringComparison.OrdinalIgnoreCase) || atcAirport.Contains(a.Name, StringComparison.OrdinalIgnoreCase))));
            if (match is not null)
            {
                return [match.PlanRunway];
            }
        }

        return onGround ? [ofp.Origin.PlanRunway, ofp.Destination.PlanRunway] : [ofp.Destination.PlanRunway, ofp.Origin.PlanRunway];
    }
}
