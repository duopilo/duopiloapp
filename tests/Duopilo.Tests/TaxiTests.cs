using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>
/// Rollen (D1): Netz aus den Facility-Daten, Haltepunkte, Route, Weg mit runden Kurven und die Kinematik vom Gate bis zum Haltepunkt.
/// Ob die Lage der Punkte im Sim stimmt, zeigt erst traffic_taxi_info.
/// </summary>
public sealed class TaxiTests
{
    // Bahn 09/27 2000 m lang durch den Bezugspunkt, Rollweg A 200 m nördlich parallel, Zubringer A1 (Westende) und A9 (Ostende) mit
    // Haltepunkt 90 m vor der Mittellinie, Gate A5 nördlich am Rollweg (Nase nach Norden zum Gebäude), GA-Platz P3 (Nase zum Rollweg)
    private static readonly AirportRunway Runway = new(51.0, 7.0, 50, 90, 2000, 45, "09", "27");

    private const int H1 = 11;
    private const int H2 = 14;

    private static AirportTaxiData Data(bool swapAxes = false)
    {
        var points = new List<(double East, double North, int Type)>();
        for (var i = 0; i <= 10; i++)
        {
            points.Add((-1000 + (200 * i), 200, 1));             // 0–10: Rollweg A
        }

        points.Add((-800, 90, 2));                                 // 11: Haltepunkt an A1
        points.Add((-800, 0, 1));                                  // 12: Bahn bei A1
        points.Add((-1000, 0, 1));                                 // 13: Bahnanfang West
        points.Add((800, 90, 2));                                  // 14: Haltepunkt an A9
        points.Add((800, 0, 1));                                   // 15: Bahn bei A9
        points.Add((1000, 0, 1));                                  // 16: Bahnende Ost

        var paths = new List<TaxiPathData>();
        for (var i = 0; i < 10; i++)
        {
            paths.Add(new TaxiPathData(1, 23, 0, 0, i, i + 1, 0));
        }

        paths.Add(new TaxiPathData(1, 23, 0, 0, 1, H1, 1));
        paths.Add(new TaxiPathData(1, 23, 0, 0, H1, 12, 1));
        paths.Add(new TaxiPathData(1, 23, 0, 0, 9, H2, 2));
        paths.Add(new TaxiPathData(1, 23, 0, 0, H2, 15, 2));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 13, 12, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 12, 15, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 15, 16, 0));
        paths.Add(new TaxiPathData(3, 20, 0, 0, 5, 0, 0));        // Gate A5 an Punkt 5
        paths.Add(new TaxiPathData(3, 10, 0, 0, 7, 1, 0));        // GA-Platz P3 an Punkt 7
        paths.Add(new TaxiPathData(6, 5, 0, 0, 0, 13, 0));        // Fahrzeugweg – zählt nicht

        TaxiPointData Point(double east, double north, int type) =>
            swapAxes ? new TaxiPointData(type, 0, north, east) : new TaxiPointData(type, 0, east, north);
        TaxiParkingData Parking(int type, int name, uint number, double heading, double radius, double east, double north) =>
            swapAxes
                ? new TaxiParkingData(type, 0, name, 0, number, 0, heading, radius, north, east)
                : new TaxiParkingData(type, 0, name, 0, number, 0, heading, radius, east, north);

        return new AirportTaxiData(
            "EDXT",
            51.0,
            7.0,
            50,
            [.. points.Select(p => Point(p.East, p.North, p.Type))],
            [Parking(9, 12, 5, 0, 20, 0, 350), Parking(1, 1, 3, 180, 8, 400, 260)],
            paths,
            ["A", "A1", "A9"]);
    }

    private static TaxiGraph Graph(bool swapAxes = false) => TaxiGraph.Build(Data(swapAxes), [Runway]);

    private static RunwayEnd End(string designation) => RunwayEnd.Find("EDXT", [Runway], designation)!;

    [Fact]
    public void Netz_aus_Punkten_Parkpositionen_und_Abschnitten()
    {
        var graph = Graph();

        Assert.Equal(19, graph.Nodes.Count);
        Assert.Equal(19, graph.Edges.Count);
        Assert.Equal(0, graph.BrokenPaths);
        Assert.Equal("x_east_z_north", graph.Mapping);
        Assert.InRange(graph.MappingErrorM!.Value, 0, 1);
        Assert.Equal(["A5", "P3"], graph.Parkings.Select(p => p.Label));
        Assert.Equal("gate_medium", graph.FindParking("a 5")!.Parking!.TypeName);

        // Breite/Länge und zurück
        var node = graph.Nodes[0];
        var (east, north) = graph.ToLocal(node.Latitude, node.Longitude);
        Assert.Equal(-1000, east, 3);
        Assert.Equal(200, north, 3);
        Assert.True(node.Longitude < 7.0 && node.Latitude > 51.0);
    }

    [Fact]
    public void Netz_erkennt_vertauschte_Achsen_an_den_Bahn_Abschnitten()
    {
        var graph = Graph(swapAxes: true);

        Assert.Equal("z_east_x_north", graph.Mapping);
        Assert.InRange(graph.MappingErrorM!.Value, 0, 1);
        Assert.Equal(-1000, graph.Nodes[0].EastM, 3);
        Assert.Equal(200, graph.Nodes[0].NorthM, 3);
    }

    [Fact]
    public void Haltepunkte_gehoeren_zur_Bahn_und_zum_Rollweg()
    {
        var graph = Graph();
        var holds = graph.Holds();

        Assert.Equal(2, holds.Count);
        Assert.All(holds, h => Assert.Equal("09/27", h.Runway));
        Assert.All(holds, h => Assert.Equal(90, h.CenterlineM, 0));
        Assert.Equal(["A1", "A9"], holds.Select(h => h.Taxiway).OrderBy(t => t));

        // Abflug von der 09: der Haltepunkt am Westende; von der 27: der am Ostende
        Assert.Equal(H1, Assert.Single(graph.DepartureHolds(End("09"))).Node.Id);
        Assert.Equal(H2, Assert.Single(graph.DepartureHolds(End("27"))).Node.Id);
    }

    [Fact]
    public void Route_vom_Gate_ueber_die_Rollwege_zum_Haltepunkt()
    {
        var graph = Graph();
        var gate = graph.FindParking("A5")!;

        var route = graph.Route(gate.Id, H1)!;

        Assert.Equal([gate.Id, 5, 4, 3, 2, 1, H1], route);
        Assert.Equal(["A", "A1"], graph.TaxiwayNames(route));
        Assert.Equal(150 + 800 + 110, graph.LengthM(route), 1);
    }

    [Fact]
    public void Route_meidet_die_Bahn_und_geht_nicht_durch_Parkpositionen()
    {
        var graph = Graph();

        // Von A1 nach A9 ginge es über die Bahn (1600 m) kürzer als über A (1820 m) – die Bahn kostet aber das Achtfache
        var route = graph.Route(H1, H2)!;
        Assert.DoesNotContain(route, n => graph.Edge(route[Math.Max(0, route.IndexOf(n) - 1)], n)?.Type == 2);
        Assert.Contains(5, route);

        // Durch eine Parkposition hindurch geht es nicht
        Assert.DoesNotContain(route, n => graph.Nodes[n].IsParking);
    }

    [Fact]
    public void Gate_mit_Pushback_GA_Platz_ohne()
    {
        var graph = Graph();
        var gate = TaxiRoute.Plan(graph, End("09"), graph.FindParking("A5")!, TaxiProfile.Airliner)!;

        // Nase nach Norden zum Gebäude, Rollweg im Süden: rückwärts zu A, im Bogen nach Osten (Gegenrichtung der Weiterfahrt nach Westen)
        Assert.True(gate.Pushback);
        var (pushEast, pushNorth, _) = gate.PushbackPath!.At(gate.PushbackPath.LengthM);
        Assert.Equal(40, pushEast, 1);
        Assert.Equal(200, pushNorth, 1);
        Assert.Equal(1, gate.PushbackPath.Turns);
        Assert.Equal(TaxiProfile.Airliner.PushbackRadiusM, gate.PushbackPath.MinRadiusM!.Value, 1);

        // Rollen von dort nach Westen, an A1 nach Süden, 12 m vor dem Haltepunkt stehen
        var (startEast, startNorth, startHeading) = gate.TaxiPath.At(0);
        Assert.Equal(40, startEast, 1);
        Assert.Equal(270, startHeading, 1);
        var (endEast, endNorth, endHeading) = gate.TaxiPath.At(gate.TaxiPath.LengthM);
        Assert.Equal(-800, endEast, 1);
        Assert.Equal(90 + TaxiProfile.Airliner.StopShortM, endNorth, 1);
        Assert.Equal(180, endHeading, 1);
        Assert.Equal(200, startNorth, 1);

        var ga = TaxiRoute.Plan(graph, End("27"), graph.FindParking("P3")!, TaxiProfile.GeneralAviation)!;
        Assert.False(ga.Pushback);
        Assert.Equal(["A", "A9"], ga.Taxiways);
    }

    [Fact]
    public void Weg_mit_runden_Kurven_ohne_Spruenge()
    {
        var path = TaxiPath.Build([(0, 0), (0, 100), (100, 100), (100, 0)], 30, 7.7, 0.8, 2.2);

        Assert.Equal(2, path.Turns);
        Assert.Equal(30, path.MinRadiusM!.Value, 1);

        // Zwei 90°-Bögen mit 30 m statt der Ecken: 300 − 4·30 + 2·(π/2·30)
        Assert.Equal(300 - 120 + (Math.PI * 30), path.LengthM, 1);
        var previous = path.At(0);
        for (var s = 0.5; s <= path.LengthM; s += 0.5)
        {
            var current = path.At(s);
            var step = Math.Sqrt(Math.Pow(current.East - previous.East, 2) + Math.Pow(current.North - previous.North, 2));
            Assert.InRange(step, 0.45, 0.55);
            var turn = Math.Abs(TrafficMath.Normalize(current.HeadingDeg - previous.HeadingDeg + 180) - 180);
            Assert.True(turn < 1.5, $"Kurs springt bei {s} m um {turn}°");
            previous = current;
        }

        // Im Bogen höchstens sqrt(0,8 · 30) m/s, vor dem Ende bis zum Halt bremsen
        Assert.InRange(path.AllowedMps(70 + (Math.PI * 7.5), 0.6), 0, Math.Sqrt(0.8 * 30) + 0.01);
        Assert.Equal(7.7, path.AllowedMps(10, 0.6), 2);
        Assert.Equal(0, path.AllowedMps(path.LengthM, 0.6), 3);
    }

    [Fact]
    public void Kurze_Abschnitte_engere_Kurve()
    {
        // Nur 20 m bis zur nächsten Ecke: der Bogen wird enger statt über die Ecke hinaus
        var path = TaxiPath.Build([(0, 0), (0, 100), (20, 100), (20, 0)], 30, 7.7, 0.8, 2.2);

        Assert.Equal(2, path.Turns);
        Assert.Equal(10, path.MinRadiusM!.Value, 1);
        Assert.InRange(path.AllowedMps(95, 0.6), 2.2, 2.9);
    }

    [Fact]
    public void Kinematik_vom_Gate_mit_Pushback_zum_Haltepunkt()
    {
        var graph = Graph();
        var route = TaxiRoute.Plan(graph, End("09"), graph.FindParking("A5")!, TaxiProfile.Airliner)!;
        var kin = new TaxiKinematics(route.PushbackPath, route.TaxiPath, graph.ToLatLon, 164, 9, 0.3);

        // Am Anfang steht die Nase wie an der Parkposition (Norden)
        Assert.Equal(TaxiPhase.Parked, kin.Phase);
        Assert.Equal(0, kin.HeadingDeg, 1);
        var phases = new List<TaxiPhase>();
        double maxPushKt = 0, maxTaxiKt = 0, turnKt = double.MaxValue;
        for (var i = 0; i < 30 * 900 && kin.Phase != TaxiPhase.Holding; i++)
        {
            kin.Advance(1.0 / 30);
            if (phases.Count == 0 || phases[^1] != kin.Phase)
            {
                phases.Add(kin.Phase);
            }

            if (kin.Phase == TaxiPhase.Pushback)
            {
                maxPushKt = Math.Max(maxPushKt, kin.SpeedKt);
                // Nase von Norden über Nordwest nach Westen: das Heck schwenkt nach Osten
                Assert.True(kin.HeadingDeg is >= 269 or <= 1, $"Pushback-Kurs {kin.HeadingDeg}");
            }
            else if (kin.Phase == TaxiPhase.Taxi)
            {
                maxTaxiKt = Math.Max(maxTaxiKt, kin.SpeedKt);
                if (Math.Abs(kin.EastM + 800) < 2 && kin.NorthM > 150)
                {
                    turnKt = Math.Min(turnKt, kin.SpeedKt);
                }
            }
        }

        Assert.Equal([TaxiPhase.Parked, TaxiPhase.Pushback, TaxiPhase.PushbackDone, TaxiPhase.Taxi, TaxiPhase.Holding], phases);
        Assert.InRange(maxPushKt, 2, TaxiKinematics.PushbackKt + 0.01);
        Assert.InRange(maxTaxiKt, 14.5, 15.01);
        Assert.InRange(turnKt, 0, 10);

        // Steht kurz vor dem Haltepunkt, Nase nach Süden zur Bahn, auf dem Boden
        Assert.Equal(-800, kin.EastM, 1);
        Assert.Equal(102, kin.NorthM, 1);
        Assert.Equal(180, kin.HeadingDeg, 1);
        Assert.Equal(0, kin.SpeedKt);
        var pose = kin.Pose();
        Assert.Equal(164 + 9, pose.AltitudeFt, 3);
        Assert.Equal(0.3, pose.PitchUpDeg, 3);
        Assert.InRange(kin.TotalM, route.PushbackPath!.LengthM + route.TaxiPath.LengthM - 0.5, route.PushbackPath.LengthM + route.TaxiPath.LengthM + 0.5);
    }

    [Theory]
    [InlineData(16, 5u, 0, "E5")]
    [InlineData(12, 12u, 0, "A12")]
    [InlineData(1, 3u, 0, "P3")]
    [InlineData(10, 7u, 0, "G7")]
    [InlineData(13, 12u, 14, "B12C")]
    [InlineData(3, 2u, 0, "NEP2")]
    [InlineData(0, 4u, 0, "4")]
    public void Bezeichnung_der_Parkposition(int name, uint number, int suffix, string expected) =>
        Assert.Equal(expected, TaxiGraph.ParkingLabel(name, number, suffix));

    [Fact]
    public void Parkpositionen_passend_zu_Kategorie_und_Groesse()
    {
        var graph = Graph();

        var airliner = TaxiRoute.CandidateParkings(graph, TrafficCategory.Airliner, TrafficWake.Medium, new Random(1));
        Assert.Equal(["A5"], airliner.Select(n => n.Parking!.Label));
        Assert.Empty(TaxiRoute.CandidateParkings(graph, TrafficCategory.Airliner, TrafficWake.Heavy, new Random(1)));

        var ga = TaxiRoute.CandidateParkings(graph, TrafficCategory.GeneralAviation, TrafficWake.Light, new Random(1));
        Assert.Equal(["P3", "A5"], ga.Select(n => n.Parking!.Label));
    }
}
