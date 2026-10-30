using System.Globalization;
using System.Text.RegularExpressions;
using Kopilot.Atc.Airspace;
using Kopilot.FlightPlan;

namespace Kopilot.Atc;

/// <summary>
/// Abschnitt der Route bei einer Kontrollstelle. <paramref name="StartNm"/> = Streckenposition der Grenze ab dem Startflughafen.
/// <paramref name="Fir"/> ist die FIR bzw. Stelle der Station (OFP-Angabe, bei Gander Oceanic "CZQO", bei Sektoren einer
/// Stelle ohne eigene FIR-Grenze deren Kennung, z. B. "EDYY" für Maastricht). Mit Sektordaten zusätzlich der Sektor
/// (Kennung der Kontrollposition), sein Rufname laut Daten und die Frequenz.
/// </summary>
public sealed record AtcFirSegment(string Fir, double StartNm, bool Oceanic)
{
    /// <summary>Kontrollposition im Sektordatensatz ("MC"), null = eine Station für die ganze FIR.</summary>
    public string? Sector { get; init; }

    /// <summary>Rufname laut Sektordaten ("Montreal Centre") – die Vorlagen haben Vorrang.</summary>
    public string? SectorName { get; init; }

    public double? FrequencyMhz { get; init; }
}

/// <summary>Oceanic-Teil der Route: Eintrittspunkt, Meldepunkte (inkl. Ein- und Austritt) und Austrittspunkt (Indizes in <see cref="Ofp.Fixes"/>).</summary>
public sealed record AtcOceanicSegment(int EntryFix, int ExitFix, IReadOnlyList<int> ReportFixes, string EntryFir);

/// <summary>Stufensteigflug laut OFP: ab dem Fix <paramref name="FixIndex"/> (Streckenposition <paramref name="AlongNm"/>) die Höhe <paramref name="AltitudeFt"/>.</summary>
public sealed record AtcStepClimb(int FixIndex, string Fix, int AltitudeFt, double AlongNm);

/// <summary>
/// Was die ATC-Zustandsmaschine aus dem SimBrief-OFP braucht: Rufzeichen, SID/STAR, Bahnen, FIR-Abschnitte mit
/// Übergabepunkten und – auf Langstrecken – den Oceanic-Teil.
/// </summary>
public sealed partial class AtcPlan
{
    // Ohne FIR-Angaben im OFP: Nordatlantik westlich von 30°W ist Gander, östlich Shanwick.
    private const string ShanwickFir = "EGGX";
    private const string GanderFir = "CZQX";
    private const string GenericOceanicFir = AtcStations.GenericOceanicFir;

    public required Ofp Ofp { get; init; }

    /// <summary>ICAO-Rufzeichen, z. B. "DLH4AB".</summary>
    public required string Callsign { get; init; }

    public string? Sid { get; init; }

    public string? Star { get; init; }

    public string? DepartureRunway { get; init; }

    public string? ArrivalRunway { get; init; }

    public int CruiseFt { get; init; }

    public required IReadOnlyList<RoutePoint> Points { get; init; }

    /// <summary>Streckenposition jedes Punkts in <see cref="Points"/> (nm ab dem ersten Punkt).</summary>
    public required IReadOnlyList<double> PointDistanceNm { get; init; }

    public IReadOnlyList<AtcFirSegment> Firs { get; init; } = [];

    public AtcOceanicSegment? Oceanic { get; init; }

    /// <summary>Fix für "proceed direct" nach dem Start (erster Punkt nach der SID).</summary>
    public int? DirectFix { get; init; }

    /// <summary>Streckenposition des Top of Descent laut Plan.</summary>
    public double? TodNm { get; init; }

    /// <summary>Stufensteigflüge im Reiseflug laut OFP (Höhe der Fixe), in Flugrichtung.</summary>
    public IReadOnlyList<AtcStepClimb> StepClimbs { get; init; } = [];

    /// <summary>Kennung des Flugs für die Stimmen ("SimBrief-Request-ID", sonst Route).</summary>
    public required string FlightKey { get; init; }

    /// <summary>Luftraumdaten, mit denen der Plan gebaut wurde (FIR-Namen für die Stationen).</summary>
    public AirspaceDatabase Airspace { get; init; } = AirspaceDatabase.Empty;

    public double TotalNm => PointDistanceNm.Count > 0 ? PointDistanceNm[^1] : 0;

    /// <summary>Streckenposition eines Fixes, null ohne Koordinaten.</summary>
    public double? FixDistanceNm(int fixIndex)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            if (Points[i].FixIndex == fixIndex)
            {
                return PointDistanceNm[i];
            }
        }

        return null;
    }

    /// <summary>FIR-Abschnitt an einer Streckenposition.</summary>
    public AtcFirSegment? FirAt(double alongNm)
    {
        AtcFirSegment? current = null;
        foreach (var segment in Firs)
        {
            if (segment.StartNm <= alongNm + 0.01)
            {
                current = segment;
            }
        }

        return current ?? (Firs.Count > 0 ? Firs[0] : null);
    }

    /// <summary>Nächste FIR-Grenze nach <paramref name="alongNm"/>.</summary>
    public AtcFirSegment? NextFir(double alongNm) => Firs.FirstOrDefault(s => s.StartNm > alongNm + 0.01);

    /// <summary>
    /// Plan aus dem OFP ableiten. <paramref name="simCallsign"/> wird genommen, wenn das OFP kein Rufzeichen hat. Mit
    /// <paramref name="airspace"/> liegen die FIR-Grenzen dort, wo die Route die VAT-Spy-Grenze schneidet, und die FIRs sind
    /// in Sektoren mit eigenen Frequenzen geteilt; ohne Daten in der Mitte zwischen den Fixes der beiden FIRs.
    /// </summary>
    public static AtcPlan Build(Ofp ofp, string? simCallsign, AtcTemplates templates, AirspaceDatabase? airspace = null)
    {
        airspace ??= AirspaceDatabase.Empty;
        var points = ofp.RoutePoints();
        var distances = new List<double>(points.Count);
        var total = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                total += RouteMath.DistanceNm(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
            }

            distances.Add(total);
        }

        var callsign = CallsignCode(ofp, simCallsign) ?? "KOPILOT";
        var cruise = ofp.InitialAltitudeFt ?? ofp.Fixes.Select(f => f.AltitudeFt ?? 0).DefaultIfEmpty(0).Max();
        if (cruise <= 0)
        {
            cruise = 30000;
        }

        var firs = FirOfFixes(ofp.Fixes, templates, airspace);
        var oceanic = OceanicSegment(ofp, firs, templates);
        var segments = Segments(ofp, points, distances, firs, oceanic, templates, airspace);
        segments = SplitSectors(ofp, segments, points, distances, cruise, templates, airspace);

        return new AtcPlan
        {
            Ofp = ofp,
            Callsign = AtcSpeech.Compact(callsign),
            Sid = Procedure(ofp.SidIdent) ?? FirstProcedure(ofp.Fixes, fromStart: true),
            Star = Procedure(ofp.StarIdent) ?? FirstProcedure(ofp.Fixes, fromStart: false),
            DepartureRunway = ofp.Origin.PlanRunway,
            ArrivalRunway = ofp.Destination.PlanRunway,
            CruiseFt = cruise,
            Points = points,
            PointDistanceNm = distances,
            Firs = segments,
            Oceanic = oceanic,
            DirectFix = DirectCandidate(ofp.Fixes),
            TodNm = TodDistance(points, distances),
            StepClimbs = FindStepClimbs(ofp.Fixes, points, distances, cruise),
            FlightKey = ofp.RequestId ?? $"{ofp.Origin.Icao}-{ofp.Destination.Icao}-{ofp.GeneratedUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            Airspace = airspace,
        };
    }

    /// <summary>
    /// Rufzeichen für den Funk als Code ("CFG2205"). Die Flugnummer des OFP (ICAO-Airline + Nummer) hat Vorrang vor
    /// atc.callsign – dort kann der IATA-Code stehen ("DE2205"), aus dem kein Telephony-Rufzeichen wird. atc.callsign gilt
    /// nur, wenn es mit derselben ICAO-Kennung beginnt (alphanumerisches Rufzeichen wie "DLH4AB" zur Flugnummer DLH400)
    /// oder die Flugnummer keine ICAO-Kennung hat. Danach das Rufzeichen aus MSFS.
    /// </summary>
    public static string? CallsignCode(Ofp? ofp, string? simCallsign)
    {
        var callsign = ofp?.Callsign is { } c ? AtcSpeech.Compact(c) : "";
        var flightNumber = ofp?.FlightNumber is { } f ? AtcSpeech.Compact(f) : "";
        if (IcaoFlightRegex().IsMatch(flightNumber))
        {
            return callsign.Length > 3 && callsign.StartsWith(flightNumber[..3], StringComparison.Ordinal) ? callsign : flightNumber;
        }

        return FirstNonEmpty(callsign, flightNumber, simCallsign is null ? null : AtcSpeech.Compact(simCallsign));
    }

    /// <summary>Gültige SID/STAR-Kennung wie "MARUN7G", sonst null.</summary>
    public static string? Procedure(string? ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            return null;
        }

        var code = AtcSpeech.Compact(ident);
        return ProcedureRegex().IsMatch(code) ? code : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>SID: Airway der ersten SID/STAR-Fixe am Anfang; STAR: der letzten vor dem Ziel.</summary>
    private static string? FirstProcedure(IReadOnlyList<OfpFix> fixes, bool fromStart)
    {
        var ordered = fromStart ? fixes : fixes.Reverse();
        foreach (var fix in ordered)
        {
            if (fix.Type == "apt")
            {
                continue;
            }

            if (!fix.IsSidStar)
            {
                return null;
            }

            if (Procedure(fix.Airway) is { } p)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// FIR je Fix. Fehlen die Angaben im OFP, werden die Oceanic-Koordinaten zugeordnet (Nordatlantik: 30°W) und – mit
    /// Luftraumdaten – die übrigen Fixe nach den VAT-Spy-Grenzen. Danach bekommen Oceanic-Fixe einer FIR mit eigenem
    /// Oceanic-Teil dessen Kennung (Gander: das OFP schreibt CZQX, die Stelle über dem Atlantik ist CZQO).
    /// </summary>
    private static string[] FirOfFixes(IReadOnlyList<OfpFix> fixes, AtcTemplates templates, AirspaceDatabase airspace)
    {
        var result = new string[fixes.Count];
        if (fixes.Any(f => !string.IsNullOrWhiteSpace(f.Fir)))
        {
            string last = "";
            for (var i = 0; i < fixes.Count; i++)
            {
                last = string.IsNullOrWhiteSpace(fixes[i].Fir) ? last : fixes[i].Fir!.Trim().ToUpperInvariant();
                result[i] = last;
            }

            // Fixe vor der ersten Angabe gehören zur ersten FIR
            var first = result.FirstOrDefault(r => r.Length > 0) ?? "";
            for (var i = 0; i < result.Length && result[i].Length == 0; i++)
            {
                result[i] = first;
            }
        }
        else
        {
            for (var i = 0; i < fixes.Count; i++)
            {
                result[i] = IsOceanicCoordinate(fixes[i]) ? HeuristicOceanicFir(fixes[i]) : "";
            }

            // Ein- und Austrittspunkt (benannte Fixe direkt vor/nach den Koordinaten) gehören zum Oceanic-Teil
            var firstOceanic = Array.FindIndex(result, r => r.Length > 0);
            var lastOceanic = Array.FindLastIndex(result, r => r.Length > 0);
            if (firstOceanic > 0 && fixes[firstOceanic - 1].Type != "apt")
            {
                result[firstOceanic - 1] = result[firstOceanic];
            }

            if (lastOceanic >= 0 && lastOceanic + 1 < fixes.Count && fixes[lastOceanic + 1].Type != "apt")
            {
                result[lastOceanic + 1] = result[lastOceanic];
            }

            // Übrige Fixe nach den VAT-Spy-Grenzen – die FIR des Fixes davor bleibt, solange er drin liegt
            string? previous = null;
            for (var i = 0; i < fixes.Count; i++)
            {
                if (result[i].Length > 0 || fixes[i].Lat is not { } lat || fixes[i].Lon is not { } lon)
                {
                    previous = result[i].Length > 0 ? result[i] : previous;
                    continue;
                }

                // Bei Überlappungen zuerst FIRs mit eigenem Eintrag in den Vorlagen, dann die kleinste (große Überlagerungen wie NAT verlieren)
                var fir = previous is not null && airspace.InFir(previous, lat, lon)
                    ? previous
                    : airspace.FirsAt(lat, lon).OrderByDescending(f => templates.Fir(f.Id) is not null).ThenBy(f => f.Area).FirstOrDefault()?.Id;
                if (fir is not null)
                {
                    result[i] = fir;
                    previous = fir;
                }
            }
        }

        // Benannte Fixe zählen nur zwischen den Koordinaten zum Oceanic-Teil – ein Austrittspunkt wie HOIST bleibt Domestic
        var firstCoordinate = -1;
        var lastCoordinate = -1;
        for (var i = 0; i < fixes.Count; i++)
        {
            if (IsOceanicCoordinate(fixes[i]))
            {
                firstCoordinate = firstCoordinate < 0 ? i : firstCoordinate;
                lastCoordinate = i;
            }
        }

        for (var i = 0; i < fixes.Count; i++)
        {
            if (templates.Fir(result[i])?.OceanicFir is { Length: > 0 } oceanicFir
                && (IsOceanicCoordinate(fixes[i])
                    || (i > firstCoordinate && i < lastCoordinate && fixes[i].Lat is { } lat && fixes[i].Lon is { } lon
                        && airspace.InFir(oceanicFir, lat, lon))))
            {
                result[i] = oceanicFir.Trim().ToUpperInvariant();
            }
        }

        return result;
    }

    private static bool IsOceanicCoordinate(OfpFix fix) =>
        fix.Ident is not ("TOC" or "TOD") && AtcSpeech.IsLatLonIdent(fix.Ident);

    private static string HeuristicOceanicFir(OfpFix fix)
    {
        if (fix.Lat is { } lat && fix.Lon is { } lon && lat is >= 40.0 and <= 70.0 && lon is >= -70.0 and <= -5.0)
        {
            // 30°W selbst gehört schon zu Gander (Meldung an der Grenze geht an die nächste Stelle)
            return lon > -30.0 ? ShanwickFir : GanderFir;
        }

        return GenericOceanicFir;
    }

    internal static bool IsOceanicFir(string fir, AtcTemplates templates) =>
        fir == GenericOceanicFir || templates.Fir(fir)?.Oceanic == true;

    /// <summary>
    /// FIR-Abschnitte entlang der Route. Beim Oceanic-Teil liegt die Grenze kurz vor dem Eintrittspunkt bzw. am
    /// Austrittspunkt (die liegen auf der Grenze). Sonst schneidet Kopilot die Route mit den VAT-Spy-Grenzen beider FIRs
    /// (<see cref="Crossing"/>); ohne Grenzen liegt sie in der Mitte zwischen dem letzten Fix der alten und dem ersten der neuen FIR.
    /// </summary>
    private static List<AtcFirSegment> Segments(
        Ofp ofp, IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances, string[] firs, AtcOceanicSegment? oceanic,
        AtcTemplates templates, AirspaceDatabase airspace)
    {
        double? FixNm(int fix)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].FixIndex == fix)
                {
                    return distances[i];
                }
            }

            return null;
        }

        var entryNm = oceanic is null ? null : FixNm(oceanic.EntryFix);
        var exitNm = oceanic is null ? null : FixNm(oceanic.ExitFix);

        var segments = new List<AtcFirSegment>();
        string? current = null;
        double previousDistance = 0;
        var previousPoint = -1;
        for (var p = 0; p < points.Count; p++)
        {
            var fixIndex = points[p].FixIndex;
            if (fixIndex < 0 || fixIndex >= firs.Length)
            {
                continue;
            }

            // Der Zielflughafen wechselt keine FIR
            if (fixIndex == ofp.Fixes.Count - 1 && ofp.Fixes[fixIndex].Type == "apt")
            {
                break;
            }

            var fir = firs[fixIndex];
            if (current is null)
            {
                segments.Add(new AtcFirSegment(fir, 0, IsOceanicFir(fir, templates)));
                current = fir;
            }
            else if (fir != current)
            {
                var toOceanic = IsOceanicFir(fir, templates);
                var fromOceanic = IsOceanicFir(current, templates);
                var boundary = (previousDistance + distances[p]) / 2;
                var atOceanicEdge = false;
                if (toOceanic && !fromOceanic && entryNm is { } en && en <= distances[p] + 0.01)
                {
                    boundary = Math.Max(previousDistance, en - 1);
                    atOceanicEdge = true;
                }
                else if (fromOceanic && !toOceanic && exitNm is { } ex && ex >= previousDistance - 0.01)
                {
                    boundary = Math.Min(distances[p], ex + 1);
                    atOceanicEdge = true;
                }

                var crossing = atOceanicEdge || previousPoint < 0
                    ? null
                    : Crossing(airspace, current, fir, points[previousPoint], points[p], previousDistance, distances[p]);
                if (crossing is { Count: > 0 })
                {
                    segments.AddRange(crossing.Select(c => new AtcFirSegment(c.Fir, c.StartNm, IsOceanicFir(c.Fir, templates))));
                }
                else
                {
                    segments.Add(new AtcFirSegment(fir, boundary, toOceanic));
                }

                current = fir;
            }

            previousDistance = distances[p];
            previousPoint = p;
        }

        return segments;
    }

    private static AtcOceanicSegment? OceanicSegment(Ofp ofp, string[] firs, AtcTemplates templates)
    {
        var fixes = ofp.Fixes;
        var first = -1;
        var last = -1;
        for (var i = 0; i < fixes.Count; i++)
        {
            if (firs[i].Length > 0 && IsOceanicFir(firs[i], templates) && fixes[i].Type != "apt")
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0)
        {
            return null;
        }

        // Eintritt: benannter Grenzpunkt (z. B. RESNO) – liegt er noch in der Domestic-FIR, ist es der Fix davor
        var entry = first;
        if (IsOceanicCoordinate(fixes[first]) && first > 0 && fixes[first - 1].Type != "apt")
        {
            entry = first - 1;
        }

        var exit = last;
        if (IsOceanicCoordinate(fixes[last]) && last + 1 < fixes.Count && fixes[last + 1].Type != "apt")
        {
            exit = last + 1;
        }

        var reports = new List<int>();
        for (var i = entry; i <= exit; i++)
        {
            if (fixes[i].Lat is not null && fixes[i].Lon is not null && fixes[i].Ident is not ("TOC" or "TOD"))
            {
                reports.Add(i);
            }
        }

        return reports.Count == 0 ? null : new AtcOceanicSegment(entry, exit, reports, firs[first]);
    }

    /// <summary>Erster Fix nach der SID (für "proceed direct"), nur in der ersten Hälfte der Route.</summary>
    private static int? DirectCandidate(IReadOnlyList<OfpFix> fixes)
    {
        var lastSid = -1;
        for (var i = 0; i < fixes.Count && fixes[i].IsSidStar; i++)
        {
            lastSid = i;
        }

        if (lastSid < 0)
        {
            return null;
        }

        for (var i = lastSid + 1; i < fixes.Count / 2; i++)
        {
            if (fixes[i].Type is not ("ltlg" or "apt") && fixes[i].Ident is not ("TOC" or "TOD") && !fixes[i].IsSidStar
                && fixes[i].Lat is not null && !AtcSpeech.IsLatLonIdent(fixes[i].Ident))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Stufensteigflüge: Fixe im Reiseflug (SimBrief-Stage "CRZ", ohne Stages zwischen TOC und TOD), deren geplante Höhe
    /// mindestens 1000 ft über der bisherigen Reiseflughöhe liegt. Ein Stufensinkflug setzt die Basis nur herunter.
    /// </summary>
    private static List<AtcStepClimb> FindStepClimbs(IReadOnlyList<OfpFix> fixes, IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances, int cruise)
    {
        var result = new List<AtcStepClimb>();
        var withStages = fixes.Any(f => !string.IsNullOrWhiteSpace(f.Stage));
        var toc = -1;
        var tod = fixes.Count;
        for (var i = 0; i < fixes.Count; i++)
        {
            if (fixes[i].Ident.Equals("TOC", StringComparison.OrdinalIgnoreCase) && toc < 0)
            {
                toc = i;
            }
            else if (fixes[i].Ident.Equals("TOD", StringComparison.OrdinalIgnoreCase))
            {
                tod = i;
            }
        }

        var level = cruise;
        for (var i = 0; i < fixes.Count; i++)
        {
            var fix = fixes[i];
            var inCruise = withStages
                ? string.Equals(fix.Stage?.Trim(), "CRZ", StringComparison.OrdinalIgnoreCase)
                : toc >= 0 && i > toc && i < tod;
            if (!inCruise || fix.AltitudeFt is not { } altitude || fix.Ident is "TOC" or "TOD" || fix.Type == "apt")
            {
                continue;
            }

            if (altitude >= level + 1000)
            {
                var nm = Enumerable.Range(0, points.Count).Where(p => points[p].FixIndex == i).Select(p => (double?)distances[p]).FirstOrDefault();
                if (nm is { } along)
                {
                    result.Add(new AtcStepClimb(i, fix.Ident, altitude, along));
                }

                level = altitude;
            }
            else if (altitude <= level - 1000)
            {
                level = altitude;
            }
        }

        return result;
    }

    private static double? TodDistance(IReadOnlyList<RoutePoint> points, IReadOnlyList<double> distances)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Ident.Equals("TOD", StringComparison.OrdinalIgnoreCase))
            {
                return distances[i];
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Z]{2,6}\d[A-Z]?$")]
    private static partial Regex ProcedureRegex();

    // ICAO-Airline (3 Buchstaben) + Flugnummer, die mit einer Ziffer beginnt: "CFG2205", "DLH400"
    [GeneratedRegex(@"^[A-Z]{3}\d[A-Z0-9]*$")]
    private static partial Regex IcaoFlightRegex();
}
