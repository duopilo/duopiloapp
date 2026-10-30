using Duopilo.Aircraft;
using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Sim;

namespace Duopilo.Input;

/// <summary>Umgebung für die Plausibilitätssperren: ATC-Freigabe und MSA – austauschbar für Tests.</summary>
public interface IInputContext
{
    /// <summary>Freigegebene Höhe der laufenden ATC-Sitzung (ft). Null = keine Sitzung oder keine Freigabe.</summary>
    int? ClearedAltitudeFt();

    /// <summary>MSA aus der Quelle des Profils (<see cref="AircraftLimits.MsaSource"/>).</summary>
    MsaReading Msa(string? source, FlightSnapshot snapshot);
}

/// <summary>Freigabe aus dem ATC-Modul, MSA aus dem SimBrief-Navlog.</summary>
public sealed class InputContext(AtcService atc, SimBriefService simBrief, SimConnectClient sim) : IInputContext
{
    public int? ClearedAltitudeFt() => atc.WithSession((session, _) => session?.ClearedAltitudeFt);

    public MsaReading Msa(string? source, FlightSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new MsaReading(null, null, Loc.T("input.msa.noSource"));
        }

        if (!source.Trim().Equals("ofp", StringComparison.OrdinalIgnoreCase))
        {
            return new MsaReading(null, source, Loc.T("input.msa.unknownSource", source));
        }

        if (simBrief.Current is not { } ofp)
        {
            return new MsaReading(null, "ofp", Loc.T("input.msa.noOfp"));
        }

        if (sim.Num("lat") is not { } lat || sim.Num("lon") is not { } lon)
        {
            return new MsaReading(null, "ofp", Loc.T("input.msa.positionUnknown"));
        }

        var points = ofp.RoutePoints();
        return MsaCalculator.FromOfp(ofp, points, simBrief.Locate(ofp, points, lat, lon));
    }
}

/// <summary>MSA aus der SimBrief-MORA: höchster Wert der beiden Fixes des aktuellen Abschnitts.</summary>
public static class MsaCalculator
{
    /// <summary>Weiter weg von der Route gilt die MORA der Route nicht mehr (nm).</summary>
    public const double MaxCrossTrackNm = 10;

    public static MsaReading FromOfp(Ofp ofp, IReadOnlyList<RoutePoint> points, RouteProgress? route)
    {
        if (route is null || route.LegIndex < 0 || route.LegIndex + 1 >= points.Count)
        {
            return new MsaReading(null, "ofp", Loc.T("input.msa.notOnRoute"));
        }

        if (Math.Abs(route.CrossTrackNm) > MaxCrossTrackNm)
        {
            return new MsaReading(null, "ofp", Loc.T("input.msa.offRoute", Math.Abs(route.CrossTrackNm)));
        }

        double? msa = null;
        foreach (var point in new[] { points[route.LegIndex], points[route.LegIndex + 1] })
        {
            if (point.FixIndex >= 0 && point.FixIndex < ofp.Fixes.Count && ofp.Fixes[point.FixIndex].MoraFt is { } mora)
            {
                msa = Math.Max(msa ?? 0, mora);
            }
        }

        return msa is { } ft
            ? new MsaReading(ft, $"MORA {points[route.LegIndex].Ident}–{points[route.LegIndex + 1].Ident}", null)
            : new MsaReading(null, "ofp", Loc.T("input.msa.noMora"));
    }
}
