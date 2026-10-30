using Kopilot.Atc.Airspace;
using Kopilot.FlightPlan;

namespace Kopilot.Atc;

/// <summary>Grenzen und Sektoren aus den Luftraumdaten (VAT-Spy, VATSIM-Sektoren).</summary>
public sealed partial class AtcPlan
{
    // Abtastung der Route: FIR-Grenzen grob alle 1 nm, danach auf ~0,01 nm genau; Sektoren alle 2 nm
    private const double CrossingStepNm = 1;
    private const double SectorStepNm = 2;

    // Liegt eine Grenze so nah an einem Fix, gilt sie am Fix (Meldung am Grenzpunkt geht an die neue Stelle)
    private const double SnapToFixNm = 2;

    // Lücke zwischen zwei FIRs, ab der eine dritte FIR ohne eigenen Fix eingefügt wird
    private const double ForeignFirMinNm = 30;

    /// <summary>
    /// Wo verlässt die Route zwischen Fix A (FIR <paramref name="a"/> laut OFP) und Fix B (FIR <paramref name="b"/>) die eine
    /// und erreicht die andere FIR? Das OFP gibt die Reihenfolge vor, VAT-Spy die Lage. VAT-Spy-Grenzen überlappen sich
    /// manchmal (z. B. reicht CZQX bis hinter Wabush, CZUL bis Churchill Falls) – dann gilt: Liegt der ganze Abschnitt in A,
    /// zählt der Eintritt in B; liegt er ganz in B, der Austritt aus A; sonst die Mitte der Überlappung. Liegt zwischen
    /// beiden eine Lücke von mindestens 30 nm in einer dritten FIR, kommt diese dazwischen.
    /// </summary>
    /// <returns>Übergänge in Flugrichtung (der letzte ist B) oder null, wenn die Daten nichts hergeben.</returns>
    internal static IReadOnlyList<(string Fir, double StartNm)>? Crossing(
        AirspaceDatabase airspace, string a, string b, RoutePoint from, RoutePoint to, double fromNm, double toNm)
    {
        var hasA = airspace.HasBoundary(a);
        var hasB = airspace.HasBoundary(b);
        var length = toNm - fromNm;
        if ((!hasA && !hasB) || length < 0.5)
        {
            return null;
        }

        var bearing = RouteMath.BearingDeg(from.Lat, from.Lon, to.Lat, to.Lon);
        var legNm = RouteMath.DistanceNm(from.Lat, from.Lon, to.Lat, to.Lon);
        (double Lat, double Lon) At(double fraction) => RouteMath.Destination(from.Lat, from.Lon, bearing, legNm * fraction);
        bool InA(double fraction)
        {
            var (lat, lon) = At(fraction);
            return airspace.InFir(a, lat, lon);
        }

        bool InB(double fraction)
        {
            var (lat, lon) = At(fraction);
            return airspace.InFir(b, lat, lon);
        }

        var n = Math.Max(2, (int)Math.Ceiling(length / CrossingStepNm));
        var inA = new bool[n + 1];
        var inB = new bool[n + 1];
        for (var i = 0; i <= n; i++)
        {
            inA[i] = hasA && InA((double)i / n);
            inB[i] = hasB && InB((double)i / n);
        }

        // Ende der A-Strecke ab dem Anfang des Abschnitts, Beginn der B-Strecke bis zum Ende
        var exitA = 0;
        while (hasA && exitA <= n && inA[exitA])
        {
            exitA++;
        }

        var enterB = n;
        while (hasB && enterB >= 0 && inB[enterB])
        {
            enterB--;
        }

        enterB++;

        double Bisect(int lo, int hi, Func<double, bool> inside)
        {
            var fLo = (double)lo / n;
            var fHi = (double)hi / n;
            var stateLo = inside(fLo);
            for (var k = 0; k < 14; k++)
            {
                var mid = (fLo + fHi) / 2;
                if (inside(mid) == stateLo)
                {
                    fLo = mid;
                }
                else
                {
                    fHi = mid;
                }
            }

            return fromNm + ((fLo + fHi) / 2 * length);
        }

        double? tA = hasA && exitA >= 1 && exitA <= n ? Bisect(exitA - 1, exitA, InA) : null;
        double? tB = hasB && enterB >= 1 && enterB <= n ? Bisect(enterB - 1, enterB, InB) : null;

        double Snap(double t) => t - fromNm < SnapToFixNm ? fromNm : toNm - t < SnapToFixNm ? toNm : t;

        if (tA is { } leaveA && tB is { } reachB)
        {
            if (reachB - leaveA >= ForeignFirMinNm)
            {
                var gap = At(((leaveA + reachB) / 2 - fromNm) / length);
                var foreign = airspace.SmallestFirAt(gap.Lat, gap.Lon, f =>
                    !f.Id.Equals(a, StringComparison.OrdinalIgnoreCase) && !f.Id.Equals(b, StringComparison.OrdinalIgnoreCase)
                    && !f.Contains(from.Lat, from.Lon) && !f.Contains(to.Lat, to.Lon));
                if (foreign is not null)
                {
                    return [(foreign.Id, Snap(leaveA)), (b, Snap(reachB))];
                }
            }

            return [(b, Snap((leaveA + reachB) / 2))];
        }

        if (tB is { } entry)
        {
            return [(b, Snap(entry))];
        }

        if (tA is { } exit)
        {
            return [(b, Snap(exit))];
        }

        // Fix B liegt genau auf der Grenze (z. B. 57N030W zwischen Shanwick und Gander): A reicht bis dorthin, B beginnt dort
        var aCovers = hasA && exitA == n + 1;
        var bCovers = hasB && enterB == 0;
        if (aCovers && !bCovers)
        {
            return [(b, toNm)];
        }

        if (bCovers && !aCovers)
        {
            return [(b, fromNm)];
        }

        return null;
    }

    /// <summary>
    /// Teilt die FIR-Abschnitte (außer Oceanic) nach den Sektoren entlang der Route. Je Punkt (alle 2 nm, geplante Höhe aus
    /// dem OFP) zählt der kleinste passende Sektor: zuerst die eigene FIR, dann die in den Vorlagen unter "members"
    /// genannten Stellen, dann Stellen ohne eigene FIR-Grenze (oberer Luftraum wie Maastricht oder Rhein); hat die FIR selbst
    /// keine Grenze (UIR wie EDVV), jeder Sektor. Lücken übernehmen den Sektor davor (am Anfang den danach), Sektoren unter
    /// <see cref="AtcRules.SectorMinNm"/> gehen im Nachbarn auf – so pendelt die Übergabe nicht an Grenzen entlang der Route.
    /// </summary>
    private static List<AtcFirSegment> SplitSectors(
        Ofp ofp, List<AtcFirSegment> segments, IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances, int cruiseFt,
        AtcTemplates templates, AirspaceDatabase airspace)
    {
        if (airspace.Sectors.Count == 0 || points.Count < 2 || segments.Count == 0)
        {
            return segments;
        }

        var total = distances[^1];
        var minNm = Math.Max(0, templates.Rules.SectorMinNm);
        var altitude = AltitudeProfile(ofp, points, distances, cruiseFt);
        var result = new List<AtcFirSegment>();
        for (var s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            var end = s + 1 < segments.Count ? segments[s + 1].StartNm : total;
            if (segment.Oceanic || end - segment.StartNm < 0.5)
            {
                result.Add(segment);
                continue;
            }

            // Sektor je Abtastpunkt
            var samples = new List<(double Nm, AirspaceSector? Sector)>();
            for (var nm = segment.StartNm; nm < end; nm += SectorStepNm)
            {
                var (lat, lon) = PositionAt(points, distances, nm);
                samples.Add((nm, BestSector(airspace, templates, segment.Fir, lat, lon, altitude(nm) / 100.0)));
            }

            if (samples.All(x => x.Sector is null))
            {
                result.Add(segment);
                continue;
            }

            // Lücken füllen: mit dem Sektor davor, am Anfang mit dem ersten gefundenen
            var firstFound = samples.First(x => x.Sector is not null).Sector;
            AirspaceSector? last = firstFound;
            var runs = new List<(double Start, AirspaceSector Sector)>();
            foreach (var (nm, sector) in samples)
            {
                var use = sector ?? last!;
                last = use;
                if (runs.Count == 0 || runs[^1].Sector.StationKey != use.StationKey)
                {
                    runs.Add((runs.Count == 0 ? segment.StartNm : nm, use));
                }
            }

            // Kurze Sektoren im Nachbarn aufgehen lassen (kürzester zuerst)
            while (runs.Count > 1)
            {
                var shortest = -1;
                var shortestLength = double.MaxValue;
                for (var i = 0; i < runs.Count; i++)
                {
                    var length = (i + 1 < runs.Count ? runs[i + 1].Start : end) - runs[i].Start;
                    if (length < minNm && length < shortestLength)
                    {
                        shortest = i;
                        shortestLength = length;
                    }
                }

                if (shortest < 0)
                {
                    break;
                }

                if (shortest == 0)
                {
                    runs[1] = (runs[0].Start, runs[1].Sector);
                }

                runs.RemoveAt(shortest);
                for (var i = runs.Count - 1; i > 0; i--)
                {
                    if (runs[i].Sector.StationKey == runs[i - 1].Sector.StationKey)
                    {
                        runs.RemoveAt(i);
                    }
                }
            }

            foreach (var (start, sector) in runs)
            {
                var stationFir = sector.Fir.Length > 0 && !sector.Fir.Equals(segment.Fir, StringComparison.OrdinalIgnoreCase)
                    && (!airspace.HasBoundary(segment.Fir) || !airspace.HasBoundary(sector.Fir) || IsMember(templates, segment.Fir, sector.Fir))
                        ? sector.Fir
                        : segment.Fir;
                result.Add(new AtcFirSegment(stationFir, start, false)
                {
                    Sector = sector.Position,
                    SectorName = sector.Name,
                    FrequencyMhz = sector.FrequencyMhz,
                });
            }
        }

        return result;
    }

    private static bool IsMember(AtcTemplates templates, string fir, string other) =>
        templates.Fir(fir)?.Members is { } members && members.Any(m => m.Equals(other, StringComparison.OrdinalIgnoreCase));

    private static AirspaceSector? BestSector(AirspaceDatabase airspace, AtcTemplates templates, string fir, double lat, double lon, double flightLevel)
    {
        var firHasBoundary = airspace.HasBoundary(fir);
        AirspaceSector? best = null;
        var bestRank = int.MaxValue;
        var bestArea = double.MaxValue;
        foreach (var sector in airspace.SectorsAt(lat, lon, flightLevel))
        {
            int rank;
            if (sector.Fir.Equals(fir, StringComparison.OrdinalIgnoreCase))
            {
                rank = 0;
            }
            else if (IsMember(templates, fir, sector.Fir))
            {
                rank = 1;
            }
            else if (sector.Fir.Length > 0 && !airspace.HasBoundary(sector.Fir))
            {
                rank = 2; // Stelle ohne eigene FIR, z. B. Maastricht (EDYY) über Norddeutschland
            }
            else if (!firHasBoundary)
            {
                rank = 3; // FIR ohne Grenze (UIR wie EDVV, unbekannte FIR): Sektor entscheidet
            }
            else
            {
                continue; // Sektor einer anderen FIR (z. B. VATSIM-Gander über Wabush, das real Montreal ist)
            }

            if (rank < bestRank || (rank == bestRank && sector.Ring.Area < bestArea))
            {
                best = sector;
                bestRank = rank;
                bestArea = sector.Ring.Area;
            }
        }

        return best;
    }

    /// <summary>Position auf der Route (Großkreis zwischen den Punkten).</summary>
    private static (double Lat, double Lon) PositionAt(IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances, double nm)
    {
        for (var i = 1; i < points.Count; i++)
        {
            if (distances[i] >= nm || i == points.Count - 1)
            {
                var a = points[i - 1];
                var b = points[i];
                var bearing = RouteMath.BearingDeg(a.Lat, a.Lon, b.Lat, b.Lon);
                return RouteMath.Destination(a.Lat, a.Lon, bearing, Math.Clamp(nm - distances[i - 1], 0, distances[i] - distances[i - 1]));
            }
        }

        return (points[0].Lat, points[0].Lon);
    }

    /// <summary>Geplante Höhe (ft) entlang der Route aus den Höhen der OFP-Fixe; ohne Angaben die Reiseflughöhe.</summary>
    private static Func<double, double> AltitudeProfile(Ofp ofp, IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances, int cruiseFt)
    {
        var known = new List<(double Nm, double Ft)>();
        for (var i = 0; i < points.Count; i++)
        {
            var fix = points[i].FixIndex;
            if (fix >= 0 && fix < ofp.Fixes.Count && ofp.Fixes[fix].AltitudeFt is { } ft && ft > 0)
            {
                known.Add((distances[i], ft));
            }
        }

        if (known.Count == 0)
        {
            return _ => cruiseFt;
        }

        return nm =>
        {
            if (nm <= known[0].Nm)
            {
                return known[0].Ft;
            }

            for (var i = 1; i < known.Count; i++)
            {
                if (known[i].Nm >= nm)
                {
                    var (n0, f0) = known[i - 1];
                    var (n1, f1) = known[i];
                    return n1 - n0 < 0.01 ? f1 : f0 + ((f1 - f0) * (nm - n0) / (n1 - n0));
                }
            }

            return known[^1].Ft;
        };
    }
}
