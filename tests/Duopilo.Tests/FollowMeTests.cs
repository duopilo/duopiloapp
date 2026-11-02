using Duopilo.Sim;
using Duopilo.Traffic;
using Duopilo.Vehicles;

namespace Duopilo.Tests;

/// <summary>
/// Follow-me: Route vom Flugzeug zum Gate, Wendeschleife vor dem Flugzeug, Abstand beim Vorwegfahren, am Gate vorbei und weg.
/// Ob das Fahrzeug im Sim so fährt, zeigt erst der Test im Sim (docs/research/follow-me.md).
/// </summary>
public sealed class FollowMeTests
{
    // Bahn 09/27 durch den Bezugspunkt, Rollweg A 200 m nördlich parallel (Punkte alle 200 m von -1000 bis 1000), Zubringer A1
    // (Westende) und A9 (Ostende) mit Haltepunkt 90 m vor der Mittellinie, Gate A5 an Punkt 5 (0/200), Nase nach Norden
    private static readonly AirportRunway Runway = new(51.0, 7.0, 50, 90, 2000, 45, "09", "27");

    private static TaxiGraph Graph()
    {
        var points = new List<TaxiPointData>();
        for (var i = 0; i <= 10; i++)
        {
            points.Add(new TaxiPointData(1, 0, -1000 + (200 * i), 200));    // 0–10: Rollweg A
        }

        points.Add(new TaxiPointData(2, 0, -800, 90));                       // 11: Haltepunkt an A1
        points.Add(new TaxiPointData(1, 0, -800, 0));                        // 12: Bahn bei A1
        points.Add(new TaxiPointData(1, 0, -1000, 0));                       // 13: Bahnanfang West
        points.Add(new TaxiPointData(2, 0, 800, 90));                        // 14: Haltepunkt an A9
        points.Add(new TaxiPointData(1, 0, 800, 0));                         // 15: Bahn bei A9
        points.Add(new TaxiPointData(1, 0, 1000, 0));                        // 16: Bahnende Ost

        var paths = new List<TaxiPathData>();
        for (var i = 0; i < 10; i++)
        {
            paths.Add(new TaxiPathData(1, 23, 0, 0, i, i + 1, 0));
        }

        paths.Add(new TaxiPathData(1, 23, 0, 0, 1, 11, 1));
        paths.Add(new TaxiPathData(1, 23, 0, 0, 11, 12, 1));
        paths.Add(new TaxiPathData(1, 23, 0, 0, 9, 14, 2));
        paths.Add(new TaxiPathData(1, 23, 0, 0, 14, 15, 2));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 13, 12, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 12, 15, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, 15, 16, 0));
        paths.Add(new TaxiPathData(3, 20, 0, 0, 5, 0, 0));                    // Gate A5 an Punkt 5

        var data = new AirportTaxiData(
            "EDXT",
            51.0,
            7.0,
            50,
            points,
            [new TaxiParkingData(9, 0, 12, 0, 5, 0, 0, 20, 0, 350)],
            paths,
            ["A", "A1", "A9"]);
        return TaxiGraph.Build(data, [Runway]);
    }

    /// <summary>Abgerollt über A9, steht auf dem Zubringer 100 m nördlich der Mittellinie, Nase nach Norden.</summary>
    private static (TaxiGraph Graph, FollowMePlan Plan) Planned()
    {
        var graph = Graph();
        var plan = FollowMePlan.Build(graph, 800, 100, 0, graph.FindParking("A5")!, FollowMeProfile.GapM(TrafficWake.Medium));
        Assert.NotNull(plan);
        return (graph, plan!);
    }

    private static double Distance((double East, double North) a, (double East, double North) b) =>
        Math.Sqrt(((a.East - b.East) * (a.East - b.East)) + ((a.North - b.North) * (a.North - b.North)));

    [Fact]
    public void Route_vom_Zubringer_zum_Gate()
    {
        var (_, plan) = Planned();

        Assert.Equal("A5", plan.Parking.Parking!.Label);
        Assert.Contains("A", plan.Taxiways);
        Assert.Equal(60, plan.MeetS);
        Assert.Equal(60, plan.GapM);

        // 100 m Zubringer, 800 m Rollweg A bis Punkt 5 (die Kurve bei A9 kürzt etwas ab)
        Assert.InRange(plan.StandEntryS, 885, 900);

        // Halt 40 m hinter der Einfahrt geradeaus auf A, weg nach Westen
        var stop = plan.Lead.At(plan.Lead.LengthM);
        Assert.InRange(Distance((stop.East, stop.North), (-40, 200)), 0, 1);
        var off = plan.DriveOff.At(plan.DriveOff.LengthM);
        Assert.InRange(Distance((off.East, off.North), (-200, 200)), 0, 1);
        Assert.InRange(plan.DriveOff.LengthM, 155, 165);
    }

    [Fact]
    public void Wendeschleife_vor_dem_Flugzeug()
    {
        var (_, plan) = Planned();

        // Startet 600 m vor dem Treffpunkt auf Rollweg A und fährt nach Osten dem Flugzeug entgegen
        var start = plan.Approach.At(0);
        Assert.InRange(start.North, 195, 205);
        Assert.InRange(start.HeadingDeg, 80, 100);

        // Wartet gewendet rechts neben dem Treffpunkt (800/160) und 2 m weiter, Nase nach Norden – Richtung Gate
        var wait = plan.Approach.At(plan.Approach.LengthM);
        Assert.InRange(Distance((wait.East, wait.North), (800 + FollowMeProfile.TurnRadiusM, 162)), 0, 0.5);
        Assert.True(wait.HeadingDeg < 5 || wait.HeadingDeg > 355, $"Kurs {wait.HeadingDeg:0}");

        // Beim Wenden bleibt es vor dem Flugzeug: nie näher als Abstand minus Radius
        var closest = double.MaxValue;
        for (var s = 0.0; s <= plan.Approach.LengthM; s += 0.5)
        {
            var p = plan.Approach.At(s);
            closest = Math.Min(closest, Distance((p.East, p.North), (800, 100)));
        }

        Assert.InRange(closest, plan.GapM - FollowMeProfile.TurnRadiusM - 1, plan.GapM);
        Assert.Equal(plan.Approach.At(plan.Approach.LengthM).East, plan.Lead.At(0).East, 1);
    }

    [Fact]
    public void Kein_Follow_me_wenn_das_Gate_zu_nah_ist_oder_kein_Rollweg_da()
    {
        var graph = Graph();
        var gate = graph.FindParking("A5")!;

        Assert.Null(FollowMePlan.Build(graph, 150, 200, 270, gate, 60));
        Assert.Null(FollowMePlan.Build(graph, 0, -600, 0, gate, 60));
        Assert.Null(FollowMePlan.StartNode(graph, 0, -600, 0));
    }

    [Fact]
    public void Startpunkt_in_Blickrichtung()
    {
        var graph = Graph();

        // Auf dem Zubringer A9 nach Norden: Punkt 9; nach Süden: der Haltepunkt
        Assert.Equal(9, FollowMePlan.StartNode(graph, 800, 120, 0)!.Value.Node);
        Assert.Equal(14, FollowMePlan.StartNode(graph, 800, 120, 180)!.Value.Node);

        // Auf A nach Westen zwischen 6 und 7: Punkt 6
        Assert.Equal(6, FollowMePlan.StartNode(graph, 310, 205, 270)!.Value.Node);
    }

    [Fact]
    public void Faehrt_entgegen_wendet_und_wartet()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);

        var seconds = Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);
        Assert.Equal(FollowMePhase.InPosition, vehicle.Phase);
        Assert.InRange(seconds, 60, 160);
        Assert.InRange(vehicle.GapM, plan.GapM - 2, plan.GapM + 1);

        // Das Flugzeug steht: das Fahrzeug auch (kein Kriechen)
        Run(vehicle, 30);
        Assert.Equal(FollowMePhase.InPosition, vehicle.Phase);
        Assert.InRange(vehicle.SpeedMps, 0, 0.01);

        var pose = vehicle.Pose();
        Assert.Equal(50, pose.AltitudeFt, 3);
        Assert.InRange(pose.HeadingTrueDeg, 0, 360);
    }

    [Fact]
    public void Haelt_beim_Vorwegfahren_den_Abstand_und_wartet_am_Gate()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);
        Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);

        // Das Flugzeug beschleunigt mit 0,5 m/s² auf 7 m/s (knapp 14 kt), rollt bis kurz vor die Einfahrt und hält dort
        var aircraftS = 0.0;
        var speed = 0.0;
        var minGap = double.MaxValue;
        var maxGapSteady = 0.0;
        var t = 0.0;
        var step = 0;
        while (t < 400 && vehicle.Phase != FollowMePhase.Gone)
        {
            speed = aircraftS < plan.StandEntryS - 30 ? Math.Min(7, speed + 0.05) : Math.Max(0, speed - 0.1);
            aircraftS = Math.Min(plan.StandEntryS, aircraftS + (speed * 0.1));
            if (step++ % 5 == 0)
            {
                vehicle.SetAircraft(aircraftS, speed);
            }

            vehicle.Advance(0.1);
            t += 0.1;
            if (vehicle.Phase is FollowMePhase.Leading or FollowMePhase.InPosition && !double.IsNaN(vehicle.GapM))
            {
                minGap = Math.Min(minGap, vehicle.GapM);
                if (t > 40 && aircraftS < plan.StandEntryS - 150)
                {
                    maxGapSteady = Math.Max(maxGapSteady, vehicle.GapM);
                }
            }
        }

        Assert.Equal(FollowMePhase.Gone, vehicle.Phase);
        Assert.InRange(minGap, 40, plan.GapM);
        Assert.InRange(maxGapSteady, plan.GapM - 15, plan.GapM + 10);
    }

    [Fact]
    public void Wartet_wenn_das_Flugzeug_stehen_bleibt()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);
        Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);

        // 200 m rollen (beschleunigen auf 7 m/s), dann stehen
        var (s, speed) = (0.0, 0.0);
        while (s < 200)
        {
            speed = Math.Min(7, speed + 0.05);
            s += speed * 0.1;
            vehicle.SetAircraft(s, speed);
            vehicle.Advance(0.1);
        }

        vehicle.SetAircraft(s, 0);
        Run(vehicle, 30);

        Assert.Equal(FollowMePhase.Leading, vehicle.Phase);
        Assert.InRange(vehicle.SpeedMps, 0, 0.01);
        Assert.InRange(vehicle.GapM, plan.GapM * 0.8, plan.GapM + 20);
    }

    [Fact]
    public void Abbestellt_faehrt_es_davon()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);
        Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);

        vehicle.Leave();
        Run(vehicle, 1);
        Assert.Equal(FollowMePhase.Leaving, vehicle.Phase);
        var seconds = Run(vehicle, 120, () => vehicle.Phase == FollowMePhase.Gone);
        Assert.Equal(FollowMePhase.Gone, vehicle.Phase);
        Assert.InRange(seconds, 10, 40);
    }

    [Fact]
    public void Abbestellt_auf_dem_Weg_und_noch_weit_weg_verschwindet_es_gleich()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        Run(vehicle, 5);
        Assert.Equal(FollowMePhase.Approach, vehicle.Phase);

        vehicle.Leave();
        Run(vehicle, 0.1);
        Assert.Equal(FollowMePhase.Gone, vehicle.Phase);
    }

    [Fact]
    public void Boden_aus_der_Messung()
    {
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50, heightFt: 1.5) { GroundTargetFt = 53 };

        Run(vehicle, 2);

        Assert.Equal(53 + 1.5, vehicle.Pose().AltitudeFt, 3);
    }

    // ------------------------------------------------------------------ KI-Verkehr (A3, A4 mit followMe)

    /// <summary>Landung auf der 27, Abrollen über A1 im Westen, dann Rollweg A nach Osten zu Gate A5.</summary>
    private static (TaxiGraph Graph, TaxiRoute Route, RunwayEnd Runway, FollowMePlan Plan) Arrival()
    {
        var graph = Graph();
        var end = RunwayEnd.Find("EDXT", [Runway], "27")!;
        var route = TaxiRoute.PlanArrival(graph, end, graph.FindParking("A5")!, TaxiProfile.Airliner, 1000);
        Assert.NotNull(route);
        var plan = FollowMePlan.FromArrival(graph, route!, end, FollowMeProfile.GapM(TrafficWake.Medium));
        Assert.NotNull(plan);
        return (graph, route!, end, plan!);
    }

    [Fact]
    public void KI_Follow_me_wartet_hinter_der_Stelle_an_der_die_Bahn_verlassen_ist()
    {
        var (graph, route, end, plan) = Arrival();

        // Abrollweg A1 (x = -800): die Bahn ist ab 90 m neben der Mittellinie verlassen, das Fahrzeug wendet 60 m weiter
        Assert.NotNull(plan.ClearS);
        var clear = route.TaxiPath.At(plan.ClearS!.Value);
        var (clearLat, clearLon) = graph.ToLatLon(clear.East, clear.North);
        Assert.InRange(Math.Abs(end.Offsets(clearLat, clearLon).CrossM), FollowMePlan.ClearOfRunwayM, FollowMePlan.ClearOfRunwayM + 3);
        Assert.Equal(plan.ClearS!.Value + plan.GapM, plan.MeetS, 3);
        Assert.Empty(plan.Crossings);
        Assert.Empty(plan.ApproachCrossings);

        // Der Weg des Fahrzeugs ist der Rollweg des Flugzeugs: dieselben Meter
        foreach (var s in new[] { 100.0, 400.0, 800.0 })
        {
            var p = route.TaxiPath.At(s);
            Assert.InRange(plan.Reference.NearestS(p.East, p.North) - s, -3, 3);
        }

        // Halt 40 m hinter der Einfahrt (Punkt 5) weiter nach Osten, weg Richtung Punkt 6
        var stop = plan.Lead.At(plan.Lead.LengthM);
        Assert.InRange(Distance((stop.East, stop.North), (40, 200)), 0, 1);
        // Die Einfahrt: wo das Flugzeug zum Gate abbiegt (das Fahrzeug fährt dort geradeaus, ein paar Meter länger)
        Assert.InRange(plan.StandEntryS, route.TaxiPath.NearestS(0, 200) - 2, route.TaxiPath.NearestS(0, 200) + 10);
    }

    [Fact]
    public void KI_Flugzeug_wartet_aufs_Fahrzeug_und_folgt_mit_Abstand()
    {
        var (graph, route, _, plan) = Arrival();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        var taxi = new TaxiKinematics(null, route.TaxiPath, graph.ToLatLon, 50, 8, 0, rolling: route.HandoverKt);

        // Das Fahrzeug ist knapp unterwegs, das Flugzeug rollt gleich von der Bahn ab
        var t = 0.0;
        var minGap = double.MaxValue;
        var maxBeforeInPosition = 0.0;
        while (t < 900 && !(taxi.Phase == TaxiPhase.Holding && vehicle.Phase == FollowMePhase.Gone))
        {
            vehicle.SetAircraft(taxi.Phase == TaxiPhase.Holding ? taxi.Taxi.LengthM : taxi.DistanceM, taxi.SpeedKt * 0.514444);
            vehicle.Advance(0.1);
            taxi.TrafficStopS = TrafficTestService.FollowMeStop(vehicle);
            if (t >= 20)
            {
                taxi.Advance(0.1);
            }

            t += 0.1;
            if (vehicle.Phase == FollowMePhase.Approach && t >= 20)
            {
                maxBeforeInPosition = Math.Max(maxBeforeInPosition, taxi.DistanceM);
            }

            if (vehicle.Phase is FollowMePhase.InPosition or FollowMePhase.Leading && taxi.Phase == TaxiPhase.Taxi)
            {
                minGap = Math.Min(minGap, vehicle.ReferenceS - taxi.DistanceM);
            }
        }

        // Vor dem Wenden kommt das Flugzeug nicht über die Stelle hinaus, an der es die Bahn verlassen hat
        Assert.InRange(maxBeforeInPosition, 0, plan.ClearS!.Value + 0.5);

        // Danach nie näher als 60 % des Abstands; am Ende steht es an der Parkposition und das Fahrzeug ist weg
        Assert.InRange(minGap, (plan.GapM * TrafficTestService.FollowMeMinGapFactor) - 1, plan.GapM + 5);
        Assert.Equal(TaxiPhase.Holding, taxi.Phase);
        Assert.Equal(FollowMePhase.Gone, vehicle.Phase);
    }

    [Fact]
    public void Faehrt_vor_gekreuzter_Bahn_erst_wenn_sie_frei_ist()
    {
        var (graph, basePlan) = Planned();
        var plan = basePlan with { Crossings = [new FollowMeCrossing("36/18", 300, 400)] };
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);
        Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);

        // Das Flugzeug rollt mit 5 m/s hinterher, bleibt aber hinter dem Fahrzeug (Halt mit 60 % des Abstands)
        var aircraftS = 0.0;
        for (var i = 0; i < 1200 && !vehicle.HoldingShort; i++)
        {
            aircraftS = Math.Min(aircraftS + 0.5, vehicle.ReferenceS - (plan.GapM * TrafficTestService.FollowMeMinGapFactor));
            vehicle.SetAircraft(aircraftS, 5);
            vehicle.Advance(0.1);
        }

        Assert.True(vehicle.HoldingShort);
        Assert.InRange(vehicle.ReferenceS, 299, 300.5);

        // Ohne Freigabe bleibt es stehen
        vehicle.SetAircraft(aircraftS, 0);
        Run(vehicle, 20);
        Assert.True(vehicle.HoldingShort);
        Assert.Equal(0, vehicle.CrossingsDone);

        // Frei: es kreuzt – und bleibt erst stehen (das Flugzeug wartet), wenn die Bahn auch für das Flugzeug hinter ihm frei wäre
        Assert.Equal("36/18", vehicle.HoldingAt!.Runway);
        vehicle.CrossingClear = true;
        vehicle.SetAircraft(vehicle.ReferenceS - 30, 0);
        Run(vehicle, 60);
        Assert.False(vehicle.HoldingShort);
        Assert.Equal(1, vehicle.CrossingsDone);
        var clear = 400 + (plan.GapM * FollowMeProfile.MinGapFactor) + FollowMeVehicle.ClearMarginM;
        Assert.InRange(vehicle.ReferenceS, clear - 0.5, clear + 15);
        Assert.InRange(vehicle.SpeedMps, 0, 0.01);
    }

    // ------------------------------------------------------------------ KI mit gekreuzter Bahn

    // Landung auf der 27, Abrollen bei Ost 200 nach Norden (C), bei Nord b (300) nach Westen (B) über die Querbahn 36/18 (Mitte Ost 0 /
    // Nord 700, 1000 m lang: Nord 200 bis 1200) mit Haltepunkten 90 m beiderseits, Parkposition A1 bei (-600/b+50), B geht weiter nach Westen
    private static readonly AirportRunway CrossRunway = new(51.0 + (700 / 111_250.0), 7.0, 50, 0, 1000, 45, "36", "18");

    private static TaxiGraph CrossingGraph(double b = 300)
    {
        TaxiPointData P(double east, double north, int type = 1) => new(type, 0, east, north);
        var data = new AirportTaxiData(
            "EDXT",
            51.0,
            7.0,
            50,
            [
                P(-1000, 0),       // 0 Bahnanfang West
                P(200, 0),         // 1 Abrollpunkt
                P(1000, 0),        // 2 Bahnende Ost
                P(200, 90, 2),     // 3 Haltepunkt an C
                P(200, b),         // 4
                P(90, b, 2),       // 5 Haltepunkt vor 36/18
                P(0, b),           // 6 auf 36/18
                P(-90, b, 2),      // 7 Haltepunkt der Gegenrichtung
                P(-600, b),        // 8 Einfahrt
                P(-1000, b),       // 9 B weiter nach Westen
                P(0, 200),         // 10 Ende 18
                P(0, 1200),        // 11 Ende 36
            ],
            [new TaxiParkingData(9, 0, 12, 0, 1, 0, 0, 20, -600, b + 50)],
            [
                new TaxiPathData(2, 45, 9, 0, 0, 1, 0),
                new TaxiPathData(2, 45, 9, 0, 1, 2, 0),
                new TaxiPathData(1, 20, 0, 0, 1, 3, 1),
                new TaxiPathData(1, 20, 0, 0, 3, 4, 1),
                new TaxiPathData(1, 20, 0, 0, 4, 5, 0),
                new TaxiPathData(1, 20, 0, 0, 5, 6, 0),
                new TaxiPathData(1, 20, 0, 0, 6, 7, 0),
                new TaxiPathData(1, 20, 0, 0, 7, 8, 0),
                new TaxiPathData(1, 20, 0, 0, 8, 9, 0),
                new TaxiPathData(2, 45, 36, 0, 10, 6, 0),
                new TaxiPathData(2, 45, 36, 0, 6, 11, 0),
                new TaxiPathData(3, 20, 0, 0, 8, 0, 0),
            ],
            ["B", "C"]);
        return TaxiGraph.Build(data, [Runway, CrossRunway]);
    }

    private static (TaxiGraph Graph, TaxiRoute Route, FollowMePlan Plan) ArrivalWithCrossing(double b = 300)
    {
        var graph = CrossingGraph(b);
        var end = RunwayEnd.Find("EDXT", [Runway, CrossRunway], "27")!;
        var route = TaxiRoute.PlanArrival(graph, end, graph.FindParking("A1")!, TaxiProfile.Airliner, 500)!.WithCrossings(graph, TaxiProfile.Airliner, Runway);
        var plan = FollowMePlan.FromArrival(graph, route, end, FollowMeProfile.GapM(TrafficWake.Medium));
        Assert.NotNull(plan);
        return (graph, route, plan!);
    }

    [Fact]
    public void KI_Follow_me_kreuzt_auf_dem_Weg_entgegen_erst_wenn_die_Bahn_frei_ist()
    {
        var (graph, route, plan) = ArrivalWithCrossing();

        // Vorweg: derselbe Halt wie der des Flugzeugs (12 m vor dem Haltepunkt bei Ost 90), frei gegenüber (Ost -102)
        var crossing = Assert.Single(route.Crossings);
        var lead = Assert.Single(plan.Crossings);
        Assert.Equal("36/18", lead.Runway);
        Assert.Equal(crossing.StopS, lead.StopM, 1);
        Assert.InRange(plan.Reference.At(lead.ClearM).East, -106, -98);

        // Entgegen: es startet hinter der Querbahn und hält auf der anderen Seite (Ost -102, nach Osten), frei ab dem Halt des Flugzeugs
        Assert.True(plan.Approach.At(0).East < -150, $"Start bei Ost {plan.Approach.At(0).East:0}");
        var approach = Assert.Single(plan.ApproachCrossings);
        var hold = plan.Approach.At(approach.StopM);
        Assert.InRange(hold.East, -106, -98);
        Assert.InRange(hold.North, 298, 302);
        Assert.InRange(hold.HeadingDeg, 85, 95);
        Assert.InRange(plan.Approach.At(approach.ClearM).East, 98, 106);

        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        vehicle.SetAircraft(0, 0);
        Run(vehicle, 200, () => vehicle.HoldingShort);
        Assert.Equal(FollowMePhase.Approach, vehicle.Phase);
        Assert.Same(approach, vehicle.HoldingAt);
        Assert.InRange(vehicle.DistanceM, approach.StopM - 0.5, approach.StopM + 0.1);

        // Ohne Freigabe bleibt es stehen, danach fährt es weiter, wendet und wartet
        Run(vehicle, 30);
        Assert.True(vehicle.HoldingShort);
        vehicle.CrossingClear = true;
        Run(vehicle, 200, () => vehicle.Phase == FollowMePhase.InPosition);
        Assert.Equal(FollowMePhase.InPosition, vehicle.Phase);
        Assert.Equal(1, vehicle.ApproachCrossingsDone);
        Assert.Equal(0, vehicle.CrossingsDone);
    }

    [Fact]
    public void KI_Follow_me_startet_nicht_auf_einer_gekreuzten_Bahn()
    {
        // B bei Nord 600: 600 m hinter dem Treffpunkt läge der Start auf der Querbahn – er wird vor sie gelegt, kein Kreuzen entgegen
        var (_, _, plan) = ArrivalWithCrossing(600);
        var crossing = Assert.Single(plan.Crossings);
        Assert.True(plan.MeetS + FollowMeProfile.SpawnAheadM < crossing.ClearM, $"Treffpunkt {plan.MeetS:0}, frei ab {crossing.ClearM:0}");
        Assert.Empty(plan.ApproachCrossings);
        var start = plan.Approach.At(0);
        Assert.InRange(start.East, 102, 108);
        Assert.InRange(start.North, 598, 602);
    }

    [Fact]
    public void Halt_hinter_dem_Fahrzeug()
    {
        Assert.Equal(5, TrafficTestService.CombineStops(5, double.NaN));
        Assert.Equal(3, TrafficTestService.CombineStops(5, 3));
        Assert.True(double.IsNaN(TrafficTestService.CombineStops(double.NaN, double.NaN)));

        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50);
        Assert.Equal(plan.MeetS - plan.GapM, TrafficTestService.FollowMeStop(vehicle));
        vehicle.Leave();
        vehicle.Advance(0.1);
        Assert.True(double.IsNaN(TrafficTestService.FollowMeStop(vehicle)));
    }

    /// <summary>Rechnet in Schritten von 0,1 s, höchstens <paramref name="maxSeconds"/> oder bis <paramref name="until"/>; Rückgabe: vergangene Sekunden.</summary>
    private static double Run(FollowMeVehicle vehicle, double maxSeconds, Func<bool>? until = null)
    {
        var t = 0.0;
        while (t < maxSeconds - 1e-9 && !(until?.Invoke() ?? false))
        {
            vehicle.Advance(0.1);
            t += 0.1;
        }

        return t;
    }
}
