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

    [Fact]
    public void Boden_gleich_nach_dem_Spawn_sofort_spaeter_gleitend()
    {
        // In den ersten 3 s springt das Fahrzeug auf den gemessenen Boden (sonst kam es sichtbar aus dem Boden), danach 6 ft/s
        var (graph, plan) = Planned();
        var vehicle = new FollowMeVehicle(plan, graph.ToLatLon, 50) { GroundTargetFt = 80 };
        vehicle.Advance(0.1);
        Assert.Equal(80, vehicle.GroundFt, 3);

        Run(vehicle, 3);
        vehicle.GroundTargetFt = 70;
        vehicle.Advance(0.5);
        Assert.Equal(80 - (TrafficKinematics.GroundSlewFtPerSec * 0.5), vehicle.GroundFt, 3);
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
