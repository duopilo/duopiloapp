using Duopilo.Traffic;

namespace Duopilo.Vehicles;

/// <summary>Kennwerte des Follow-me-Fahrzeugs (Transporter im Stil eines VW-Busses).</summary>
public static class FollowMeProfile
{
    /// <summary>Wenden vor dem Flugzeug: Radius der Wendeschleife (Wendekreis eines Transporters gut 12 m).</summary>
    public const double TurnRadiusM = 6.5;

    /// <summary>Kurven auf den Rollwegen.</summary>
    public const double LeadTurnRadiusM = 15;

    /// <summary>Höchstens 30 km/h – Vorfeld-Tempo, schneller als ein rollendes Verkehrsflugzeug.</summary>
    public const double MaxMps = 8.3;

    /// <summary>In engen Kurven und beim Wenden nicht langsamer als das.</summary>
    public const double MinTurnMps = 2.0;

    public const double AccelMps2 = 1.2;

    public const double DecelMps2 = 2.0;

    public const double LateralMps2 = 1.5;

    /// <summary>So weit kommt das Fahrzeug höchstens entgegen (es startet auf der Route vor dem Flugzeug, sonst an der Einfahrt zum Gate).</summary>
    public const double SpawnAheadM = 600;

    /// <summary>Spurwechsel vor und nach dem Wenden.</summary>
    public const double MergeM = 25;

    /// <summary>Kürzer als das bis zur Einfahrt zum Gate lohnt kein Follow-me.</summary>
    public const double MinRouteM = 200;

    /// <summary>Am Gate fährt das Fahrzeug so weit an der Einfahrt vorbei und hält dort, bis das Flugzeug eingebogen ist.</summary>
    public const double StandPassM = 40;

    /// <summary>Danach fährt es so weit weg und verschwindet.</summary>
    public const double DriveOffM = 150;

    /// <summary>Abstand Fahrzeug – Bezugspunkt des Flugzeugs entlang der Route (Nase plus Sicherheitsabstand), nach Wirbelschleppen-Kategorie.</summary>
    public static double GapM(TrafficWake wake) => wake switch
    {
        TrafficWake.Light => 35,
        TrafficWake.Medium => 60,
        TrafficWake.Heavy => 80,
        _ => 95,
    };

    /// <summary>So weit darf das Flugzeug höchstens neben einem Rollweg stehen, damit die Route von dort beginnt.</summary>
    public const double MaxOffTaxiwayM = 60;

    /// <summary>KI-Verkehr: So nah darf das Flugzeug dem Fahrzeug kommen (Anteil des Abstands) – darunter bremst es.</summary>
    public const double MinGapFactor = 0.6;
}

/// <summary>
/// Gekreuzte Bahn auf dem Weg des Fahrzeugs (Meter auf <see cref="FollowMePlan.Approach"/> bzw. <see cref="FollowMePlan.Reference"/>):
/// Halt davor bei <paramref name="StopM"/>, ab <paramref name="ClearM"/> liegt sie hinter ihm (auf der anderen Seite so weit von der
/// Mittellinie wie der Halt).
/// </summary>
/// <param name="Runway">Die gekreuzte Bahn ("05R/23L").</param>
public sealed record FollowMeCrossing(string Runway, double StopM, double ClearM);

/// <summary>
/// Weg des Follow-me (Meter östlich/nördlich wie im <see cref="TaxiGraph"/>):
/// <list type="bullet">
/// <item><see cref="Reference"/>: Route vom Flugzeug über die Rollwege bis zur Einfahrt zum Gate und daran vorbei bis zum Halt – daran
/// misst das Fahrzeug seinen Abstand zum Flugzeug.</item>
/// <item><see cref="Approach"/>: vom Startpunkt vor dem Flugzeug die Route entgegen bis zum Treffpunkt, dort eine Wendeschleife vor dem
/// Flugzeug – danach steht es in Fahrtrichtung rechts neben der Mittellinie.</item>
/// <item><see cref="Lead"/>: von dort zurück auf die Mittellinie und vor dem Flugzeug her bis zum Halt hinter der Einfahrt zum Gate.</item>
/// <item><see cref="DriveOff"/>: vom Halt weiter, bis es verschwindet.</item>
/// </list>
/// </summary>
/// <param name="MeetS">Treffpunkt (Mitte der Wendeschleife) auf <see cref="Reference"/>.</param>
/// <param name="LeadOffsetS">Stelle auf <see cref="Reference"/> = <paramref name="LeadOffsetS"/> + Meter auf <see cref="Lead"/>.</param>
/// <param name="StandEntryS">Einfahrt zum Gate auf <see cref="Reference"/>.</param>
/// <param name="GapM">Abstand, den das Fahrzeug vor dem Flugzeug hält.</param>
public sealed record FollowMePlan(
    TaxiNode Parking,
    IReadOnlyList<int> Nodes,
    IReadOnlyList<string> Taxiways,
    TaxiPath Reference,
    TaxiPath Approach,
    TaxiPath Lead,
    TaxiPath DriveOff,
    double MeetS,
    double LeadOffsetS,
    double StandEntryS,
    double GapM)
{
    /// <summary>KI-Verkehr: Ab so weit neben der Mittellinie hat das Flugzeug die Bahn verlassen (Abstand der Haltepunkte).</summary>
    public const double ClearOfRunwayM = TaxiRoute.DefaultHoldDistanceM;

    private const double SampleM = 8;

    /// <summary>Den Rollweg der KI so dicht abtasten, dass die Meter auf dem Bezugsweg mit denen des Flugzeugs übereinstimmen.</summary>
    private const double ArrivalSampleM = 4;

    /// <summary>So weit vor der Einfahrt biegt der Rollweg der KI zum Gate ab (Kurvenradius der Verkehrsflugzeuge) – das Fahrzeug fährt geradeaus weiter.</summary>
    private const double EntryLeadInM = 35;

    /// <summary>KI-Verkehr: Hier hat das Flugzeug die Bahn verlassen (auf <see cref="Reference"/>); bis hierhin rollt es, solange das Fahrzeug noch nicht steht.</summary>
    public double? ClearS { get; init; }

    /// <summary>Gekreuzte Bahnen beim Vorwegfahren, Meter auf <see cref="Reference"/> (KI-Verkehr: dieselben Halte wie die des Flugzeugs).</summary>
    public IReadOnlyList<FollowMeCrossing> Crossings { get; init; } = [];

    /// <summary>
    /// Gekreuzte Bahnen auf dem Weg entgegen, Meter auf <see cref="Approach"/>: Halt auf der Seite, von der das Fahrzeug kommt, frei auf
    /// der Seite des Flugzeugs.
    /// </summary>
    public IReadOnlyList<FollowMeCrossing> ApproachCrossings { get; init; } = [];

    /// <summary>Nach einer gekreuzten Bahn so weit dahinter starten, sonst (Start auf oder dicht hinter der Bahn) vor ihr.</summary>
    private const double SpawnClearM = 20;

    /// <summary>Nach der Wendeschleife so weit geradeaus bis zum Wartepunkt.</summary>
    private const double WaitAheadM = 2;

    /// <summary>Vom Wartepunkt so weit geradeaus, bevor es auf die Mittellinie zieht.</summary>
    private const double LeadStraightM = 5;

    /// <summary>
    /// Plant das Follow-me vom Flugzeug (Ost/Nord, rechtweisender Kurs) zu <paramref name="parking"/>. Null = keine Route: das Flugzeug
    /// steht nicht an einem Rollweg, es gibt keinen Weg dorthin, oder das Gate ist so nah, dass sich kein Follow-me lohnt.
    /// </summary>
    public static FollowMePlan? Build(TaxiGraph graph, double aircraftEast, double aircraftNorth, double headingTrueDeg, TaxiNode parking, double gapM)
    {
        if (!parking.IsParking || StartNode(graph, aircraftEast, aircraftNorth, headingTrueDeg) is not { } start)
        {
            return null;
        }

        if (graph.Route(start.Node, parking.Id) is not { Count: >= 2 } nodes)
        {
            return null;
        }

        // Einfahrt zum Gate: der letzte Rollpunkt vor der Parkposition
        var entry = graph.Nodes[nodes[^2]];
        if (entry.IsParking)
        {
            return null;
        }

        // Woher die Route an die Einfahrt kommt: vom Rollpunkt davor oder – liegt sie gleich voraus – vom Flugzeug
        var before = nodes.Count >= 3 ? graph.Nodes[nodes[^3]] : null;
        var cameFrom = before is not null ? (East: before.EastM, North: before.NorthM) : (East: start.East, North: start.North);
        var (beyond, driveOff) = PastEntry(graph, entry, cameFrom, before?.Id ?? -1, parking);

        var points = new List<(double East, double North)> { (start.East, start.North) };
        points.AddRange(nodes.Take(nodes.Count - 1).Select(n => (graph.Nodes[n].EastM, graph.Nodes[n].NorthM)));
        points.AddRange(beyond);
        var reference = TaxiPath.Build(points, FollowMeProfile.LeadTurnRadiusM, FollowMeProfile.MaxMps, FollowMeProfile.LateralMps2, FollowMeProfile.MinTurnMps);
        var entryS = reference.NearestS(entry.EastM, entry.NorthM);
        var meetS = Math.Max(gapM, FollowMeProfile.TurnRadiusM + 10);
        return Assemble(parking, nodes, graph.TaxiwayNames(nodes), reference, entryS, meetS, gapM, driveOff);
    }

    /// <summary>
    /// Plant das Follow-me für eine Landung mit Rollen des eigenen KI-Verkehrs (A3, A4): Weg des Fahrzeugs = Rollweg des Flugzeugs
    /// (<see cref="TaxiRoute.TaxiPath"/> ab der Übergabe auf der Bahn), Treffpunkt <paramref name="gapM"/> hinter der Stelle, an der das
    /// Flugzeug die Bahn verlassen hat (<see cref="ClearS"/>, <see cref="ClearOfRunwayM"/> von der Mittellinie), Halte vor gekreuzten Bahnen
    /// wie die des Flugzeugs (<see cref="Crossings"/>), auf dem Weg entgegen auf der anderen Seite (<see cref="ApproachCrossings"/>). Läge
    /// der Start auf einer gekreuzten Bahn oder dicht dahinter, startet das Fahrzeug vor ihr. Null = der Weg reicht nicht für ein Follow-me.
    /// </summary>
    public static FollowMePlan? FromArrival(TaxiGraph graph, TaxiRoute route, RunwayEnd runway, double gapM)
    {
        var nodes = route.Nodes;
        if (nodes.Count < 3 || !route.Parking.IsParking)
        {
            return null;
        }

        var entry = graph.Nodes[nodes[^2]];
        var before = graph.Nodes[nodes[^3]];
        if (entry.IsParking)
        {
            return null;
        }

        var path = route.TaxiPath;
        var pathEntryS = path.NearestS(entry.EastM, entry.NorthM);
        if (ClearOfRunway(graph, path, runway, pathEntryS) is not { } clearS)
        {
            return null;
        }

        // Bezugsweg: der Rollweg des Flugzeugs (dicht abgetastet, die Meter stimmen mit denen des Flugzeugs überein) bis vor die Kurve
        // zum Gate, dann geradeaus über die Einfahrt und an ihr vorbei wie beim Piloten
        var (beyond, driveOff) = PastEntry(graph, entry, (before.EastM, before.NorthM), before.Id, route.Parking);
        var points = new List<(double East, double North)>();
        for (var s = 0.0; s < pathEntryS - EntryLeadInM; s += ArrivalSampleM)
        {
            var p = path.At(s);
            points.Add((p.East, p.North));
        }

        points.Add((entry.EastM, entry.NorthM));
        points.AddRange(beyond);
        var reference = TaxiPath.Build(points, FollowMeProfile.LeadTurnRadiusM, FollowMeProfile.MaxMps, FollowMeProfile.LateralMps2, FollowMeProfile.MinTurnMps);
        var entryS = reference.NearestS(entry.EastM, entry.NorthM);
        var meetS = clearS + gapM;

        // Gekreuzte Bahnen hinter dem Treffpunkt: Halt wie das Flugzeug, frei auf der anderen Seite
        var crossings = route.Crossings
            .Where(c => c.StopS > meetS && c.StopS < entryS)
            .OrderBy(c => c.StopS)
            .Select(c => new FollowMeCrossing(c.Runway, c.StopS, FarSide(graph, reference, c)))
            .ToList();

        // Start nicht auf einer Bahn oder dicht dahinter: dann davor
        var spawnS = Math.Min(entryS, meetS + FollowMeProfile.SpawnAheadM);
        if (crossings.FirstOrDefault(c => c.StopM < spawnS && spawnS < c.ClearM + SpawnClearM) is { } onRunway)
        {
            spawnS = onRunway.StopM - 1;
        }

        if (Assemble(route.Parking, nodes, route.Taxiways, reference, entryS, meetS, gapM, driveOff, spawnS) is not { } plan)
        {
            return null;
        }

        // Auf dem Weg entgegen (die Route rückwärts ab spawnS): Halt auf der anderen Seite, frei am Halt des Flugzeugs
        var approach = new List<FollowMeCrossing>();
        foreach (var c in crossings.Where(c => c.ClearM + SpawnClearM <= spawnS))
        {
            approach.Add(new FollowMeCrossing(c.Runway, Math.Round(OnApproach(plan.Approach, reference, spawnS, c.ClearM), 1), Math.Round(OnApproach(plan.Approach, reference, spawnS, c.StopM), 1)));
        }

        return plan with { ClearS = clearS, Crossings = crossings, ApproachCrossings = [.. approach.OrderBy(c => c.StopM)] };
    }

    /// <summary>
    /// Hinter dem Halt vor <paramref name="crossing"/> die Stelle auf <paramref name="path"/>, die auf der anderen Seite der Bahn wieder so
    /// weit von der Mittellinie liegt; ohne Bahn im Netz oder ohne solche Stelle: so weit hinter dem Halt wie zweimal der Abstand zur Mittellinie.
    /// </summary>
    private static double FarSide(TaxiGraph graph, TaxiPath path, TaxiCrossing crossing)
    {
        var fallback = crossing.StopS + (2 * crossing.CenterlineM);
        if (RunwayEnd.Find(graph.Icao, graph.Runways, crossing.Runway.Split('/')[0]) is not { } end)
        {
            return fallback;
        }

        double Cross(double s)
        {
            var p = path.At(s);
            var (lat, lon) = graph.ToLatLon(p.East, p.North);
            return end.Offsets(lat, lon).CrossM;
        }

        var near = Cross(crossing.StopS);
        var hold = Math.Abs(near);
        for (var s = crossing.StopS + 2; s <= path.LengthM; s += 2)
        {
            var x = Cross(s);
            if (Math.Sign(x) != Math.Sign(near) && Math.Abs(x) >= hold)
            {
                return s;
            }
        }

        return fallback;
    }

    /// <summary>Stelle <paramref name="referenceS"/> auf dem Weg entgegen (der beginnt bei <paramref name="spawnS"/> und fährt die Route rückwärts).</summary>
    private static double OnApproach(TaxiPath approach, TaxiPath reference, double spawnS, double referenceS)
    {
        var p = reference.At(referenceS);
        var guess = spawnS - referenceS;
        return approach.NearestS(p.East, p.North, Math.Max(0, guess - 30), guess + 30);
    }

    /// <summary>Auf <paramref name="path"/> die erste Stelle, die <see cref="ClearOfRunwayM"/> (sonst 60 m) neben der Mittellinie liegt.</summary>
    private static double? ClearOfRunway(TaxiGraph graph, TaxiPath path, RunwayEnd runway, double untilS)
    {
        foreach (var clear in new[] { ClearOfRunwayM, 60.0 })
        {
            for (var s = 0.0; s <= untilS; s += 2)
            {
                var p = path.At(s);
                var (lat, lon) = graph.ToLatLon(p.East, p.North);
                if (Math.Abs(runway.Offsets(lat, lon).CrossM) >= clear)
                {
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>Wendeschleife, Weg vorweg und Wegfahren zu einem Bezugsweg; null = bis zur Einfahrt zu kurz.</summary>
    private static FollowMePlan? Assemble(
        TaxiNode parking,
        IReadOnlyList<int> nodes,
        IReadOnlyList<string> taxiways,
        TaxiPath reference,
        double entryS,
        double meetS,
        double gapM,
        List<(double East, double North)> driveOff,
        double? spawnAtS = null)
    {
        if (entryS < Math.Max(FollowMeProfile.MinRouteM, meetS + FollowMeProfile.MergeM + 20))
        {
            return null;
        }

        // Treffpunkt E mit Fahrtrichtung u; links/rechts bezogen auf die Fahrtrichtung zum Gate
        var (ex, ey, heading) = reference.At(meetS);
        var rad = heading * Math.PI / 180;
        var (ux, uy) = (Math.Sin(rad), Math.Cos(rad));
        var (lx, ly) = (-uy, ux);
        var r = FollowMeProfile.TurnRadiusM;

        // Entgegen: die Route rückwärts bis kurz vor den Treffpunkt, Spurwechsel nach links (in Fahrtrichtung des Fahrzeugs rechts),
        // dann die Wendeschleife um E herum zur Seite des Flugzeugs – danach steht es rechts der Mittellinie in Richtung Gate
        var spawnS = spawnAtS ?? Math.Min(entryS, meetS + FollowMeProfile.SpawnAheadM);
        var approach = new List<(double East, double North)>();
        for (var s = spawnS; s > meetS + FollowMeProfile.MergeM; s -= SampleM)
        {
            var p = reference.At(s);
            approach.Add((p.East, p.North));
        }

        var mergeIn = reference.At(meetS + FollowMeProfile.MergeM);
        approach.Add((mergeIn.East, mergeIn.North));
        for (var deg = 0; deg <= 180; deg += 15)
        {
            var phi = deg * Math.PI / 180;
            approach.Add((ex + (r * ((lx * Math.Cos(phi)) - (ux * Math.Sin(phi)))), ey + (r * ((ly * Math.Cos(phi)) - (uy * Math.Sin(phi))))));
        }

        // Nach dem Bogen ein kurzes Stück geradeaus: dann steht es genau in Richtung Gate
        var waitPoint = (East: ex - (r * lx) + (ux * WaitAheadM), North: ey - (r * ly) + (uy * WaitAheadM));
        approach.Add(waitPoint);
        var approachPath = TaxiPath.Build(approach, r, FollowMeProfile.MaxMps, FollowMeProfile.LateralMps2, FollowMeProfile.MinTurnMps);

        // Vorweg: vom Wartepunkt (rechts neben E) erst geradeaus, dann zurück auf die Mittellinie und die Route entlang bis zum Halt
        var straight = (East: waitPoint.East + (ux * LeadStraightM), North: waitPoint.North + (uy * LeadStraightM));
        var lead = new List<(double East, double North)> { waitPoint, straight };
        for (var s = meetS + FollowMeProfile.MergeM; s < reference.LengthM; s += SampleM)
        {
            var p = reference.At(s);
            lead.Add((p.East, p.North));
        }

        var end = reference.At(reference.LengthM);
        lead.Add((end.East, end.North));
        var leadPath = TaxiPath.Build(lead, FollowMeProfile.LeadTurnRadiusM, FollowMeProfile.MaxMps, FollowMeProfile.LateralMps2, FollowMeProfile.MinTurnMps);

        // Bis zum Einfädeln (meetS + MergeM auf der Route) ist es auf dem Weg vorweg so weit
        var merge = reference.At(meetS + FollowMeProfile.MergeM);
        var toMerge = LeadStraightM + Math.Sqrt(((merge.East - straight.East) * (merge.East - straight.East)) + ((merge.North - straight.North) * (merge.North - straight.North)));

        var off = new List<(double East, double North)> { (end.East, end.North) };
        off.AddRange(driveOff);
        var offPath = TaxiPath.Build(off, FollowMeProfile.LeadTurnRadiusM, FollowMeProfile.MaxMps, FollowMeProfile.LateralMps2, FollowMeProfile.MinTurnMps);

        return new FollowMePlan(
            parking,
            nodes,
            taxiways,
            reference,
            approachPath,
            leadPath,
            offPath,
            meetS,
            meetS + FollowMeProfile.MergeM - toMerge,
            entryS,
            gapM);
    }

    /// <summary>
    /// Wo die Route am Flugzeug beginnt: der Rollweg-Abschnitt, an dem es steht (höchstens <see cref="FollowMeProfile.MaxOffTaxiwayM"/>
    /// daneben, bevorzugt in seiner Richtung), der Punkt darauf neben dem Flugzeug und der Knoten am Ende in Blickrichtung.
    /// </summary>
    public static (int Node, double East, double North)? StartNode(TaxiGraph graph, double east, double north, double headingTrueDeg)
    {
        var rad = headingTrueDeg * Math.PI / 180;
        var (hx, hy) = (Math.Sin(rad), Math.Cos(rad));
        (double Score, int Node, double East, double North)? best = null;
        foreach (var edge in graph.Edges)
        {
            var (a, b) = (graph.Nodes[edge.From], graph.Nodes[edge.To]);
            if (a.IsParking || b.IsParking)
            {
                continue;
            }

            var (dx, dy) = (b.EastM - a.EastM, b.NorthM - a.NorthM);
            var length2 = (dx * dx) + (dy * dy);
            if (length2 < 0.01)
            {
                continue;
            }

            var t = Math.Clamp((((east - a.EastM) * dx) + ((north - a.NorthM) * dy)) / length2, 0, 1);
            var (px, py) = (a.EastM + (dx * t), a.NorthM + (dy * t));
            var distance = Math.Sqrt(((east - px) * (east - px)) + ((north - py) * (north - py)));
            if (distance > FollowMeProfile.MaxOffTaxiwayM)
            {
                continue;
            }

            // Richtung des Abschnitts zum Kurs (egal in welche Richtung er befahren wird): 0 = parallel, 1 = quer
            var length = Math.Sqrt(length2);
            var across = 1 - Math.Abs(((dx * hx) + (dy * hy)) / length);
            var score = distance + (across * 30) + (edge.Type == 2 ? 20 : 0);
            if (best is null || score < best.Value.Score)
            {
                // Knoten in Blickrichtung: der mit dem größeren Anteil voraus
                var aheadA = ((a.EastM - px) * hx) + ((a.NorthM - py) * hy);
                var aheadB = ((b.EastM - px) * hx) + ((b.NorthM - py) * hy);
                best = (score, aheadB >= aheadA ? b.Id : a.Id, px, py);
            }
        }

        return best is { } found ? (found.Node, found.East, found.North) : null;
    }

    /// <summary>
    /// Hinter der Einfahrt zum Gate: geradeaus weiter über die Rollwege (der Abschnitt, der am wenigsten abknickt, nicht zum Gate und
    /// nicht zurück) – <see cref="FollowMeProfile.StandPassM"/> bis zum Halt und <see cref="FollowMeProfile.DriveOffM"/> zum Wegfahren.
    /// Endet der Rollweg dort (Sackgasse), fährt es quer zur Seite, weg vom Gate.
    /// </summary>
    private static (List<(double East, double North)> Beyond, List<(double East, double North)> DriveOff) PastEntry(
        TaxiGraph graph,
        TaxiNode entry,
        (double East, double North) cameFrom,
        int previousNode,
        TaxiNode parking)
    {
        var walk = new List<(double East, double North)>();
        var (inX, inY) = Unit(entry.EastM - cameFrom.East, entry.NorthM - cameFrom.North);
        var (dirX, dirY) = (inX, inY);
        var current = entry;
        var previous = previousNode;
        var walked = 0.0;
        var visited = new HashSet<int> { entry.Id, parking.Id };
        while (walked < FollowMeProfile.StandPassM + FollowMeProfile.DriveOffM)
        {
            (double Dot, TaxiNode Node)? next = null;
            foreach (var edge in graph.EdgesOf(current.Id))
            {
                var other = graph.Nodes[edge.Other(current.Id)];
                if (other.IsParking || other.Id == previous || visited.Contains(other.Id) || edge.Type == 2)
                {
                    continue;
                }

                var (ox, oy) = Unit(other.EastM - current.EastM, other.NorthM - current.NorthM);
                var dot = (ox * dirX) + (oy * dirY);
                if (dot > 0.3 && (next is null || dot > next.Value.Dot))
                {
                    next = (dot, other);
                }
            }

            if (next is not { } step)
            {
                break;
            }

            var length = Math.Sqrt(((step.Node.EastM - current.EastM) * (step.Node.EastM - current.EastM)) + ((step.Node.NorthM - current.NorthM) * (step.Node.NorthM - current.NorthM)));
            (dirX, dirY) = Unit(step.Node.EastM - current.EastM, step.Node.NorthM - current.NorthM);
            walk.Add((step.Node.EastM, step.Node.NorthM));
            walked += length;
            visited.Add(step.Node.Id);
            previous = current.Id;
            current = step.Node;
        }

        if (walked < FollowMeProfile.StandPassM + FollowMeProfile.DriveOffM)
        {
            if (walked < FollowMeProfile.StandPassM)
            {
                // Sackgasse: von der Einfahrt quer zur Seite, weg vom Gate
                var (px, py) = (parking.EastM - entry.EastM, parking.NorthM - entry.NorthM);
                var (sideX, sideY) = (-inY, inX);
                if ((sideX * px) + (sideY * py) > 0)
                {
                    (sideX, sideY) = (-sideX, -sideY);
                }

                walk.Clear();
                walk.Add((entry.EastM + (sideX * FollowMeProfile.StandPassM), entry.NorthM + (sideY * FollowMeProfile.StandPassM)));
                (dirX, dirY) = (sideX, sideY);
                walked = FollowMeProfile.StandPassM;
            }

            var last = walk[^1];
            var rest = FollowMeProfile.StandPassM + FollowMeProfile.DriveOffM - walked;
            walk.Add((last.East + (dirX * rest), last.North + (dirY * rest)));
        }

        // Aufteilen: StandPassM hinter der Einfahrt der Halt (letzter Punkt von beyond), der Rest zum Wegfahren
        var beyond = new List<(double East, double North)>();
        var off = new List<(double East, double North)>();
        var at = (East: entry.EastM, North: entry.NorthM);
        var distance = 0.0;
        var stopped = false;
        foreach (var point in walk)
        {
            var segment = Math.Sqrt(((point.East - at.East) * (point.East - at.East)) + ((point.North - at.North) * (point.North - at.North)));
            if (stopped)
            {
                off.Add(point);
            }
            else if (distance + segment >= FollowMeProfile.StandPassM)
            {
                var f = segment > 0 ? (FollowMeProfile.StandPassM - distance) / segment : 1;
                beyond.Add((at.East + ((point.East - at.East) * f), at.North + ((point.North - at.North) * f)));
                stopped = true;
                if (f < 0.999)
                {
                    off.Add(point);
                }
            }
            else
            {
                beyond.Add(point);
            }

            distance += segment;
            at = point;
        }

        return (beyond, off);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt((x * x) + (y * y));
        return length > 0 ? (x / length, y / length) : (0, 1);
    }
}
