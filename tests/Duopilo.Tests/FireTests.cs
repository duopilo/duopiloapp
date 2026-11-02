using Duopilo.Fire;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>
/// Flughafenfeuerwehr ohne Sim: Kategorie und Fahrzeuge, Wache auf freier Parkposition, Haltepunkte an der Landebahn, Spur des
/// Flugzeugs, Folgen mit Abstand und Umstellen am stehenden Flugzeug. Wie es im Sim aussieht, zeigt erst der Test dort.
/// </summary>
public sealed class FireTests
{
    // Bahn 09/27 durch den Bezugspunkt (x -1000 … 1000), Rollweg A 200 m nördlich parallel, Zubringer bei x = -800, -400, 0, 400, 800
    // mit Haltepunkt 90 m neben der Mittellinie. Parkpositionen: Fahrzeuge P1 nahe der Mitte (100/320), P2 am Ostende (900/320),
    // Gate A5 (-200/330).
    private static readonly AirportRunway Runway = new(51.0, 7.0, 50, 90, 2000, 45, "09", "27");

    private static readonly int[] ConnectorX = [-800, -400, 0, 400, 800];

    private static TaxiGraph Graph()
    {
        var points = new List<TaxiPointData>();
        for (var i = 0; i <= 10; i++)
        {
            points.Add(new TaxiPointData(1, 0, -1000 + (200 * i), 200));    // 0–10: Rollweg A
        }

        var paths = new List<TaxiPathData>();
        for (var i = 0; i < 10; i++)
        {
            paths.Add(new TaxiPathData(1, 23, 0, 0, i, i + 1, 0));
        }

        // Bahn: Anfang West, die Bahnpunkte der Zubringer, Ende Ost
        var runwayNodes = new List<(int X, int Index)>();
        points.Add(new TaxiPointData(1, 0, -1000, 0));
        runwayNodes.Add((-1000, points.Count - 1));
        foreach (var x in ConnectorX)
        {
            var a = (x + 1000) / 200;
            points.Add(new TaxiPointData(2, 0, x, 90));                       // Haltepunkt
            var hold = points.Count - 1;
            points.Add(new TaxiPointData(1, 0, x, 0));                        // Bahn
            var onRunway = points.Count - 1;
            runwayNodes.Add((x, onRunway));
            paths.Add(new TaxiPathData(1, 23, 0, 0, a, hold, 1));
            paths.Add(new TaxiPathData(1, 23, 0, 0, hold, onRunway, 1));
        }

        points.Add(new TaxiPointData(1, 0, 1000, 0));
        runwayNodes.Add((1000, points.Count - 1));
        for (var i = 0; i < runwayNodes.Count - 1; i++)
        {
            paths.Add(new TaxiPathData(2, 45, 9, 0, runwayNodes[i].Index, runwayNodes[i + 1].Index, 0));
        }

        paths.Add(new TaxiPathData(3, 10, 0, 0, 5, 0, 0));                    // P1 an A bei x = 0
        paths.Add(new TaxiPathData(3, 10, 0, 0, 10, 1, 0));                   // P2 an A bei x = 1000
        paths.Add(new TaxiPathData(3, 20, 0, 0, 4, 2, 0));                    // Gate A5 an A bei x = -200

        var data = new AirportTaxiData(
            "EDXF",
            51.0,
            7.0,
            50,
            points,
            [
                new TaxiParkingData(13, 0, 1, 0, 1, 0, 0, 10, 100, 320),
                new TaxiParkingData(13, 0, 1, 0, 2, 0, 0, 10, 900, 320),
                new TaxiParkingData(9, 0, 12, 0, 5, 0, 0, 20, -200, 330),
            ],
            paths,
            ["A", "A1"]);
        return TaxiGraph.Build(data, [Runway]);
    }

    private static RunwayEnd Runway09 => RunwayEnd.Find("EDXF", [Runway], "09")!;

    private static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    // ------------------------------------------------------------------ Kategorie und Fahrzeuge

    [Theory]
    [InlineData("A320", 6)]
    [InlineData("A20N", 6)]
    [InlineData("A321", 7)]
    [InlineData("B738", 7)]
    [InlineData("B752", 7)]
    [InlineData("B753", 8)]
    [InlineData("A333", 9)]
    [InlineData("B77W", 9)]
    [InlineData("B748", 10)]
    [InlineData("A388", 10)]
    [InlineData("C172", 1)]
    [InlineData("AT76", 5)]
    public void Kategorie_aus_der_Laenge(string type, int category) =>
        Assert.Equal(category, FireCategory.Of(type, TrafficWake.Medium));

    [Theory]
    [InlineData(TrafficWake.Light, 4)]
    [InlineData(TrafficWake.Medium, 7)]
    [InlineData(TrafficWake.Heavy, 9)]
    [InlineData(TrafficWake.Super, 10)]
    public void Kategorie_ohne_Typ_aus_der_Wirbelschleppen_Klasse(TrafficWake wake, int category) =>
        Assert.Equal(category, FireCategory.Of("XXXX", wake));

    [Fact]
    public void Fahrzeuge_je_Stufe_und_Kategorie()
    {
        Assert.Equal(["FLF 1"], FireCategory.Lineup(FireAlertLevel.LocalStandby, 3).Select(u => u.Name));
        Assert.Equal(["FLF 1", "ELW 1", "RTW 1"], FireCategory.Lineup(FireAlertLevel.FullEmergency, 3).Select(u => u.Name));
        Assert.Equal(["FLF 1", "FLF 2", "ELW 1"], FireCategory.Lineup(FireAlertLevel.LocalStandby, 7).Select(u => u.Name));
        Assert.Equal(["FLF 1", "FLF 2", "ELW 1", "HLF 1", "RTF 1", "RTW 1"], FireCategory.Lineup(FireAlertLevel.FullEmergency, 6).Select(u => u.Name));
        Assert.Equal(
            ["FLF 1", "FLF 2", "FLF 3", "ELW 1", "HLF 1", "RTF 1", "RTF 2", "RTW 1"],
            FireCategory.Lineup(FireAlertLevel.FullEmergency, 9).Select(u => u.Name));

        var local = FireCategory.Lineup(FireAlertLevel.LocalStandby, 7);
        Assert.Equal([FireStandbyRole.Touchdown, FireStandbyRole.Rollout, FireStandbyRole.Mid], local.Select(u => u.Role));
        Assert.All(FireCategory.Lineup(FireAlertLevel.GroundIncident, 7), u => Assert.Equal(FireStandbyRole.Aircraft, u.Role));
    }

    // ------------------------------------------------------------------ Planung

    [Fact]
    public void Wache_auf_der_Fahrzeug_Parkposition_nahe_der_Bahnmitte()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null);

        Assert.NotNull(plan);
        Assert.Equal("P1", plan!.Spawn.Name);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Belegte_oder_sichtbare_Wache_wird_uebersprungen()
    {
        var graph = Graph();
        var p1 = graph.FindParking("P1")!;

        // Ein Flugzeug steht auf P1
        graph.SetObstacles([(p1.Latitude, p1.Longitude)], 25);
        var occupied = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null);
        Assert.NotEqual("P1", occupied!.Spawn.Name);

        // Das eigene Flugzeug steht gleich daneben: Spawnen wäre zu sehen
        var visible = FirePlanner.Plan(Graph(), Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, (p1.EastM + 50, p1.NorthM));
        Assert.NotEqual("P1", visible!.Spawn.Name);
    }

    [Fact]
    public void Haltepunkte_an_Aufsetzzone_Mitte_und_Stoppstelle()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;

        // Schwelle 09 bei x = -1000: Haltepunkte 200, 600, 1000, 1400, 1800 m dahinter (ein paar Meter Spiel zwischen Kugel- und
        // WGS84-Rechnung)
        var along = plan.Vehicles.ToDictionary(v => v.Unit.Name, v => v.AlongM);
        Assert.InRange(along["FLF 1"], 595, 605);     // Aufsetzzone 450 m → nächster Haltepunkt
        Assert.InRange(along["ELW 1"], 995, 1005);    // Mitte
        Assert.InRange(along["FLF 2"], 1795, 1805);   // Stoppstelle 1500 + 150 m

        // Jede Route endet am Haltepunkt, also hinter der Haltelinie und nicht auf der Bahn
        foreach (var vehicle in plan.Vehicles)
        {
            var (e, n, _) = vehicle.Route.At(vehicle.Route.LengthM);
            Assert.InRange(Distance(e, n, vehicle.Hold!.EastM, vehicle.Hold.NorthM), 0, 1);
            Assert.Null(graph.RunwayAt(e, n));
        }
    }

    [Fact]
    public void Geteilter_Haltepunkt_jedes_weitere_Fahrzeug_haelt_frueher()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        var mid = plan.Vehicles.Where(v => Math.Abs(v.AlongM - 1000) < 5).Select(v => v.Route).ToList();

        // ELW, HLF, zwei Rettungstreppen und RTW an der Mitte: 22 m Abstand hintereinander
        Assert.Equal(5, mid.Count);
        var ends = mid.Select(r => r.At(r.LengthM)).Select(p => Distance(p.East, p.North, 0, 90)).ToList();
        for (var i = 0; i < ends.Count; i++)
        {
            Assert.InRange(ends[i], (i * FireProfile.QueueSpacingM) - 1, (i * FireProfile.QueueSpacingM) + 1);
        }
    }

    [Fact]
    public void Notfall_am_Boden_direkt_zum_Flugzeug()
    {
        var graph = Graph();
        var plan = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 7), 600, 200, FireCategory.PostUnitM(TrafficWake.Medium));

        Assert.NotNull(plan);
        Assert.All(plan!.Vehicles, v => Assert.Null(v.Hold));

        // Wache außer Sicht (über 500 m weg), die Fahrzeuge halten vor dem Flugzeug, hintereinander
        Assert.True(Distance(plan.Spawn.EastM, plan.Spawn.NorthM, 600, 200) >= FirePlanner.OutOfSightM);
        var stops = plan.Vehicles.Select(v => v.Route.At(v.Route.LengthM)).Select(p => Distance(p.East, p.North, 600, 200)).ToList();
        Assert.All(stops, d => Assert.InRange(d, 40, 120));
        Assert.True(stops.SequenceEqual(stops.OrderBy(d => d)));
    }

    // ------------------------------------------------------------------ Wachen aus OpenStreetMap

    private static FireStation Station(TaxiGraph graph, string name, double east, double north)
    {
        var (lat, lon) = graph.ToLatLon(east, north);
        return new FireStation(name, east, north, lat, lon, "osm");
    }

    [Fact]
    public void Overpass_Antwort_lesen_Gebaeude_und_Wache_zaehlen_einmal()
    {
        const string json = """
            {"elements":[
              {"type":"node","id":1,"lat":51.2800,"lon":6.7700,"tags":{"amenity":"fire_station","name":"Feuerwache Nord"}},
              {"type":"way","id":2,"center":{"lat":51.2801,"lon":6.7701},"tags":{"building":"fire_station"}},
              {"type":"way","id":3,"center":{"lat":51.2900,"lon":6.8000},"tags":{"amenity":"fire_station"}},
              {"type":"relation","id":4,"tags":{"amenity":"fire_station"}}
            ]}
            """;
        var stations = FireStationLookup.Parse(json);

        Assert.Equal(2, stations.Count);
        Assert.Equal("Feuerwache Nord", stations[0].Name);
        Assert.Null(stations[1].Name);
        Assert.Equal(51.29, stations[1].Latitude, 4);
        Assert.Contains("\"icao\"=\"EDDL\"", FireStationLookup.Query("eddl"));
    }

    [Fact]
    public void Wachen_aus_OSM_im_Rollweg_Netz_nur_in_der_Naehe()
    {
        var graph = Graph();
        var (nearLat, nearLon) = graph.ToLatLon(100, 260);
        var osm = new[] { new OsmFireStation(null, nearLat, nearLon), new OsmFireStation("Weit weg", 51.5, 7.5) };
        var stations = FireStationLookup.ToStations(osm, graph);

        var station = Assert.Single(stations);
        Assert.Equal("Wache 1", station.Name);
        Assert.InRange(Distance(station.EastM, station.NorthM, 100, 260), 0, 1);
    }

    [Fact]
    public void Wache_aus_OSM_vor_der_Parkposition()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null, [Station(graph, "Feuerwache", 100, 260)])!;

        Assert.True(plan.FromOsm);
        Assert.All(plan.Vehicles, v => Assert.Equal("Feuerwache", v.Station.Name));

        // Die Wege beginnen an der Wache, nicht auf einer Parkposition
        var (e, n, _) = plan.Vehicles[0].Route.At(0);
        Assert.InRange(Distance(e, n, 100, 260), 0, 1);
    }

    [Fact]
    public void Mehrere_Wachen_bei_Mayday_jedes_Fahrzeug_von_der_naechsten()
    {
        var graph = Graph();
        IReadOnlyList<FireStation> stations = [Station(graph, "West", -900, 260), Station(graph, "Ost", 900, 260)];
        var full = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null, stations, allStations: true)!;
        var byUnit = full.Vehicles.ToDictionary(v => v.Unit.Name, v => v.Station.Name);

        Assert.Equal("West", byUnit["FLF 1"]);   // Aufsetzzone bei x = -400
        Assert.Equal("Ost", byUnit["FLF 2"]);    // Stoppstelle bei x = 800
        Assert.Equal(2, full.Stations.Count);

        // Pan Pan: alles von einer Wache
        var local = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null, stations)!;
        Assert.Single(local.Vehicles.Select(v => v.Station.Name).Distinct());
    }

    [Fact]
    public void Wache_weit_weg_vom_Rollweg_Netz_Rueckfall_auf_die_Parkposition()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null, [Station(graph, "Abseits", 0, 900)])!;

        Assert.False(plan.FromOsm);
        Assert.Equal("P1", plan.Spawn.Name);
        Assert.Equal("parking", plan.Spawn.Source);
    }

    [Fact]
    public void Zurueck_zur_eigenen_Wache()
    {
        var graph = Graph();
        IReadOnlyList<FireStation> stations = [Station(graph, "West", -900, 260), Station(graph, "Ost", 900, 260)];
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null, stations, allStations: true)!;
        var operation = new FireOperation(graph, FireAlertLevel.FullEmergency, plan, TrafficWake.Heavy, 9);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        for (var t = 0; t < 40; t++)
        {
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        operation.ReturnAll();
        for (var t = 0; t < 4000 && operation.Vehicles.Any(v => v.Phase != FireVehiclePhase.Gone); t++)
        {
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        foreach (var vehicle in operation.Vehicles)
        {
            var home = plan.Vehicles.Single(v => v.Unit == vehicle.Unit).Station;
            Assert.Equal(FireVehiclePhase.Gone, vehicle.Phase);
            Assert.InRange(Distance(vehicle.EastM, vehicle.NorthM, home.EastM, home.NorthM), 0, 1);
        }
    }

    // ------------------------------------------------------------------ Spur

    [Fact]
    public void Spur_zeichnet_alle_drei_Meter_auf()
    {
        var trail = new FireTrail();
        for (var x = 0.0; x <= 100; x += 1)
        {
            trail.Add(x, 0);
        }

        Assert.InRange(trail.LengthM, 98, 100);
        Assert.InRange(trail.Count, 33, 35);
        var (e, n, h) = trail.At(50);
        Assert.InRange(e, 49.9, 50.1);
        Assert.Equal(0, n, 3);
        Assert.Equal(90, h, 3);
        Assert.InRange(trail.NearestS(40, 30), 39.9, 40.1);
        Assert.InRange(trail.DistanceTo(40, 30), 29.9, 30.1);
    }

    [Fact]
    public void Spur_erkennt_Kurven()
    {
        var trail = new FireTrail();
        for (var x = 0; x <= 60; x += 3)
        {
            trail.Add(x, 0);
        }

        for (var y = 3; y <= 60; y += 3)
        {
            trail.Add(60, y);
        }

        Assert.InRange(trail.TurnAheadDeg(10, 25), 0, 1);
        Assert.InRange(trail.TurnAheadDeg(45, 25), 89, 91);
    }

    // ------------------------------------------------------------------ Fahrzeug

    [Fact]
    public void Abstandsregelung_beim_Folgen()
    {
        // Zu weit weg: schneller als das Flugzeug; nah genug und das Flugzeug steht: stehen
        Assert.True(FireVehicle.TrailTargetMps(200, 80, 5) > 5);
        Assert.Equal(5, FireVehicle.TrailTargetMps(80, 80, 5), 3);
        Assert.Equal(0, FireVehicle.TrailTargetMps(82, 80, 0));
        Assert.Equal(FireProfile.MaxMps, FireVehicle.TrailTargetMps(1000, 80, 10));
    }

    [Fact]
    public void Faehrt_zum_Haltepunkt_und_wartet()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        var vp = plan.Vehicles[0];
        var vehicle = new FireVehicle(vp.Unit, "test", vp.Route, new FireTrail(), graph.ToLatLon, 164);

        for (var t = 0; t < 2400 && vehicle.Phase == FireVehiclePhase.Responding; t++)
        {
            vehicle.Advance(0.25);
            Assert.InRange(vehicle.SpeedMps, 0, FireProfile.MaxMps + 0.01);
        }

        Assert.Equal(FireVehiclePhase.Standby, vehicle.Phase);
        Assert.InRange(Distance(vehicle.EastM, vehicle.NorthM, vp.Hold!.EastM, vp.Hold.NorthM), 0, 1);
        Assert.True(vehicle.BlueLight);
    }

    [Fact]
    public void Folgt_der_Spur_mit_Abstand_und_ueberholt_nie()
    {
        var trail = new FireTrail();
        var aircraftS = 0.0;
        for (; aircraftS <= 300; aircraftS += 3)
        {
            trail.Add(aircraftS, 0);
        }

        var route = FirePlanner.BuildPath([(0, 50), (0, 40)], FireProfile.MaxMps);
        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Touchdown), "test", route, trail, (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 164);
        vehicle.Follow(0, 80);

        // Flugzeug rollt mit 10 m/s weiter, eine Minute
        var minGap = double.MaxValue;
        for (var t = 0; t < 240; t++)
        {
            aircraftS += 2.5;
            trail.Add(aircraftS, 0);
            vehicle.SetAircraft(trail.LengthM, 10);
            vehicle.Advance(0.25);
            minGap = Math.Min(minGap, vehicle.GapM);
        }

        Assert.Equal(FireVehiclePhase.Following, vehicle.Phase);
        Assert.InRange(vehicle.GapM, 65, 100);
        Assert.True(minGap >= 40);

        // Flugzeug steht: das Fahrzeug bleibt hinter ihm stehen
        for (var t = 0; t < 240; t++)
        {
            vehicle.SetAircraft(trail.LengthM, 0);
            vehicle.Advance(0.25);
        }

        Assert.Equal(0, vehicle.SpeedMps, 3);
        Assert.InRange(vehicle.GapM, 40, 90);
        Assert.InRange(vehicle.NorthM, -0.1, 0.1);
    }

    // ------------------------------------------------------------------ Einsatz

    [Fact]
    public void Plaetze_am_Flugzeug_nie_in_Achsrichtung_und_um_das_Flugzeug_herum()
    {
        var u = FireCategory.PostUnitM(TrafficWake.Medium);
        foreach (var unit in FireCategory.Lineup(FireAlertLevel.FullEmergency, 9))
        {
            var (x, y) = FireOperation.Post(unit, u);
            Assert.True(Math.Sqrt((x * x) + (y * y)) >= u);
            Assert.True(Math.Abs(x) >= 0.4 * u, unit.Name + " steht seitlich in Achsrichtung");
        }

        // Direkt hinter dem Flugzeug zu einem Platz vorn links: über einen Punkt seitlich, nie quer durch das Flugzeug
        var points = FireOperation.DetourPoints((-40, 0), (u, -u), u);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var d = TaxiGraph.SegmentDistance(0, 0, points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y);
            Assert.True(d >= 0.9 * u, "Abschnitt " + i + " führt " + d.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m am Flugzeug vorbei");
        }
    }

    [Fact]
    public void Flugzeug_System_hin_und_zurueck()
    {
        var aircraft = new FireAircraftState(100, 200, 90, 0, true);
        var (e, n) = FireOperation.ToLocal(aircraft, 30, -30);

        // Kurs Ost: vorn = Osten, links = Norden
        Assert.Equal(130, e, 6);
        Assert.Equal(230, n, 6);
        var (x, y) = FireOperation.ToAircraft(aircraft, e, n);
        Assert.Equal(30, x, 6);
        Assert.Equal(-30, y, 6);
    }

    [Fact]
    public void Landung_folgen_umstellen_weiterrollen_einruecken()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.LocalStandby, plan, TrafficWake.Medium, 7);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        var vehicles = operation.Vehicles;
        FireVehicle V(string name) => vehicles.Single(v => v.Unit.Name == name);
        const double dt = 0.25;
        void Step(FireAircraftState state)
        {
            operation.Update(state, dt);
            foreach (var vehicle in vehicles)
            {
                vehicle.Advance(dt);
            }
        }

        // Anflug: alle fahren in ihre Bereitstellungsräume
        var airborne = new FireAircraftState(-3000, 0, 90, 75, false);
        for (var t = 0; t < 1200 && vehicles.Any(v => v.Phase != FireVehiclePhase.Standby); t++)
        {
            Step(airborne);
        }

        Assert.All(vehicles, v => Assert.Equal(FireVehiclePhase.Standby, v.Phase));

        // Aufsetzen bei x = -700 mit 70 m/s, Bremsen mit 2,2 m/s² bis zum Stand bei gut x = 400
        var (x, speed) = (-700.0, 70.0);
        var sawFlf1Following = false;
        while (speed > 0)
        {
            speed = Math.Max(0, speed - (2.2 * dt));
            x += speed * dt;
            Step(new FireAircraftState(x, 0, 90, speed, true));
            sawFlf1Following |= V("FLF 1").Phase is FireVehiclePhase.Joining or FireVehiclePhase.Following;
        }

        Assert.True(sawFlf1Following, "FLF 1 ist nicht auf die Spur gefahren");
        Assert.Equal(FireVehiclePhase.Standby, V("FLF 2").Phase);   // steht noch vor dem Flugzeug

        // Stillstand auf der Bahn: alle umstellen das Flugzeug
        var stopped = new FireAircraftState(x, 0, 90, 0, true);
        for (var t = 0; t < 1200 && vehicles.Any(v => v.Phase != FireVehiclePhase.AtAircraft); t++)
        {
            Step(stopped);
        }

        var u = FireCategory.PostUnitM(TrafficWake.Medium);
        foreach (var vehicle in vehicles)
        {
            Assert.Equal(FireVehiclePhase.AtAircraft, vehicle.Phase);
            var (px, py) = FireOperation.Post(vehicle.Unit, u);
            var (pe, pn) = FireOperation.ToLocal(stopped, px, py);
            Assert.InRange(Distance(vehicle.EastM, vehicle.NorthM, pe, pn), 0, 1);
        }

        Assert.Equal("at_aircraft", operation.State);

        // Weiterrollen: FLF 1 folgt, die anderen rücken ein
        for (var t = 0; t < 120; t++)
        {
            x += 5 * dt;
            Step(new FireAircraftState(x, 0, 90, 5, true));
        }

        Assert.Contains(V("FLF 1").Phase, new[] { FireVehiclePhase.Joining, FireVehiclePhase.Following });
        Assert.Contains(V("FLF 2").Phase, new[] { FireVehiclePhase.Returning, FireVehiclePhase.Gone });
        Assert.Contains(V("ELW 1").Phase, new[] { FireVehiclePhase.Returning, FireVehiclePhase.Gone });
        Assert.False(V("FLF 2").BlueLight);

        // Abrollen über den Zubringer bei x = 800 nach Norden und dort stehen bleiben: nach einer Minute rücken alle ein
        while (x < 800)
        {
            x = Math.Min(800, x + (5 * dt));
            Step(new FireAircraftState(x, 0, 90, 5, true));
        }

        var north = 0.0;
        while (north < 150)
        {
            north += 4 * dt;
            Step(new FireAircraftState(800, north, 0, 4, true));
        }

        var parked = new FireAircraftState(800, north, 0, 0, true);
        for (var t = 0; t < 4 * 90; t++)
        {
            Step(parked);
        }

        Assert.True(operation.Returning);
        Assert.All(vehicles, v => Assert.Contains(v.Phase, new[] { FireVehiclePhase.Returning, FireVehiclePhase.Gone }));
    }

    [Fact]
    public void Alarm_am_Boden_die_Fahrzeuge_warten_an_der_Bahn()
    {
        // Test EDDL 02.11.: Alarm, während das Flugzeug am Vorfeld steht – die Fahrzeuge rückten nach einer Minute wieder ein
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.LocalStandby, plan, TrafficWake.Medium, 7);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        var parked = new FireAircraftState(-200, 330, 180, 0, true);
        for (var t = 0; t < 4 * 300; t++)
        {
            operation.Update(parked, 0.25);
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        Assert.False(operation.Returning);
        Assert.False(operation.Landed);
        Assert.All(operation.Vehicles, v => Assert.Equal(FireVehiclePhase.Standby, v.Phase));
    }

    [Fact]
    public void Abbruch_alle_fahren_zur_Wache_zurueck()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 7), TrafficWake.Medium, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.FullEmergency, plan, TrafficWake.Medium, 7);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        var airborne = new FireAircraftState(-6000, 0, 90, 80, false);
        for (var t = 0; t < 40; t++)
        {
            operation.Update(airborne, 0.25);
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        operation.ReturnAll();
        Assert.Equal("returning", operation.State);
        for (var t = 0; t < 2400 && operation.Vehicles.Any(v => v.Phase != FireVehiclePhase.Gone); t++)
        {
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        // Alle zurück an der Wache (P1)
        Assert.All(operation.Vehicles, v =>
        {
            Assert.Equal(FireVehiclePhase.Gone, v.Phase);
            Assert.InRange(Distance(v.EastM, v.NorthM, plan.Spawn.EastM, plan.Spawn.NorthM), 0, 1);
        });
    }

    [Theory]
    [InlineData("local", FireAlertLevel.LocalStandby)]
    [InlineData(null, FireAlertLevel.LocalStandby)]
    [InlineData("Mayday", FireAlertLevel.FullEmergency)]
    [InlineData("full", FireAlertLevel.FullEmergency)]
    [InlineData("ground", FireAlertLevel.GroundIncident)]
    public void Alarmstufe_aus_dem_Werkzeug(string? text, FireAlertLevel level) => Assert.Equal(level, FireService.ParseLevel(text));
}
