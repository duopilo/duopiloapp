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

    private static TaxiGraph Graph(bool stub = false, bool roads = false, bool runwayHold = false)
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

        // Hold-Short-Punkt auf der Bahn selbst (wie an einer Kreuzung, EDDK 14L Haltepunkt 744): bei x = 650, also 1650 m hinter der
        // Schwelle 09 – genau an der Stoppstelle eines Medium
        if (runwayHold)
        {
            points.Add(new TaxiPointData(5, 0, 650, 0));
            var onRunwayHold = points.Count - 1;
            var west = runwayNodes.First(r => r.X == 400).Index;
            var east = runwayNodes.First(r => r.X == 800).Index;
            paths.Add(new TaxiPathData(2, 45, 9, 0, west, onRunwayHold, 0));
            paths.Add(new TaxiPathData(2, 45, 9, 0, onRunwayHold, east, 0));
        }

        paths.Add(new TaxiPathData(3, 10, 0, 0, 5, 0, 0));                    // P1 an A bei x = 0
        paths.Add(new TaxiPathData(3, 10, 0, 0, 10, 1, 0));                   // P2 an A bei x = 1000
        paths.Add(new TaxiPathData(3, 20, 0, 0, 4, 2, 0));                    // Gate A5 an A bei x = -200

        // Abgeschnittenes Stück (wie am Helipad in EDDL): zwei Rollpunkte bei 300/330 und 320/345 ohne Verbindung zum Netz
        if (stub)
        {
            points.Add(new TaxiPointData(1, 0, 300, 330));
            points.Add(new TaxiPointData(1, 0, 320, 345));
            paths.Add(new TaxiPathData(1, 15, 0, 0, points.Count - 2, points.Count - 1, 0));

            // Fahrweg der Bodenfahrzeuge von A bei x = 200 zum Stück, dazu ein falsch gelesener über 1,3 km (wird verworfen)
            paths.Add(new TaxiPathData(6, 6, 0, 0, 6, points.Count - 2, 0));
            paths.Add(new TaxiPathData(7, 6, 0, 0, points.Count - 1, 0, 0));
        }

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
        return TaxiGraph.Build(data, [Runway], vehicleRoads: roads);
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
        // Nachtest EDDK 05.11.: bei Mayday ab Kategorie 8 vier FLF und nur eine Rettungstreppe; Local Standby bleibt bei der ICAO-Zahl
        var full = FireCategory.Lineup(FireAlertLevel.FullEmergency, 9);
        Assert.Equal(["FLF 1", "FLF 2", "FLF 3", "FLF 4", "ELW 1", "HLF 1", "RTF 1", "RTW 1"], full.Select(u => u.Name));
        Assert.Equal(
            [FireStandbyRole.Touchdown, FireStandbyRole.Rollout, FireStandbyRole.End, FireStandbyRole.Mid],
            full.Where(u => u.Kind == FireVehicleKind.Flf).Select(u => u.Role));
        Assert.Equal(["FLF 1", "FLF 2", "FLF 3", "ELW 1"], FireCategory.Lineup(FireAlertLevel.LocalStandby, 9).Select(u => u.Name));

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
    public void Haltepunkt_auf_der_Bahn_taugt_nicht_fuer_die_Bereitstellung()
    {
        // Test EDDK 05.11.: FLF 2 stand am Haltepunkt 744, 2 m neben der Mittellinie der 14L – auf der Landebahn
        var graph = Graph(runwayHold: true);
        Assert.Contains(graph.Holds(), h => Math.Abs(h.Node.EastM - 650) < 1 && Math.Abs(h.Node.NorthM) < 1);

        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        foreach (var vehicle in plan.Vehicles)
        {
            Assert.Null(graph.RunwayAt(vehicle.Hold!.EastM, vehicle.Hold.NorthM));
            Assert.InRange(Math.Abs(vehicle.Hold.NorthM), (45 / 2.0) + FirePlanner.MinHoldEdgeClearM, 1000);
        }

        var medium = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        Assert.InRange(medium.Vehicles.Single(v => v.Unit.Name == "FLF 2").AlongM, 1795, 1805);
    }

    [Fact]
    public void Geteilter_Haltepunkt_jedes_weitere_Fahrzeug_haelt_frueher()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        var mid = plan.Vehicles.Where(v => Math.Abs(v.AlongM - 1000) < 5).Select(v => v.Route).ToList();

        // FLF 4, ELW, HLF, Rettungstreppe und RTW an der Mitte: 22 m Abstand hintereinander
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
    public void Notfallflugzeug_ohne_Kennung_ueber_die_Objekt_ID()
    {
        // Nachtest EDDK 05.11.: Die Abfrage für die Bahnen läuft ohne Texte, der A330 des Tests kam ohne Kennung und hielt alle auf
        static AircraftSample Sample(uint id, string? atcId, bool user = false, double kt = 0) =>
            new(id, "aircraft", atcId, null, null, null, 50.87, 7.14, 260, 137, 135, kt, 0, true, user);

        Assert.True(FireService.IsEmergencyAircraft("DUO01", 217448451, place: false, Sample(217448451, null)));
        Assert.True(FireService.IsEmergencyAircraft("DUO01", null, place: false, Sample(5, " duo01 ")));
        Assert.False(FireService.IsEmergencyAircraft("DUO01", 217448451, place: false, Sample(5, null)));
        Assert.True(FireService.IsEmergencyAircraft(null, null, place: false, Sample(1, null, user: true)));
        Assert.False(FireService.IsEmergencyAircraft(null, null, place: true, Sample(1, null, user: true)));

        // Nur gelandet und stehend; rollt es (Pan Pan über 06/24), wartet das Fahrzeug wie vor jedem Flugzeug
        Assert.True(FireService.IgnoreForCrossing("DUO01", 217448451, place: false, landed: true, Sample(217448451, null)));
        Assert.False(FireService.IgnoreForCrossing("DUO01", 217448451, place: false, landed: true, Sample(217448451, null, kt: 10)));
        Assert.False(FireService.IgnoreForCrossing("DUO01", 217448451, place: false, landed: false, Sample(217448451, null)));
    }

    [Fact]
    public void Militaerische_Wachen_aus_Flaeche_oder_Betreiber()
    {
        // Test EDDK 05.11.: die Wache der Bundeswehr im militärischen Teil heißt in OSM nur „Flughafenfeuerwehr“
        const string json = """
            {"elements":[
              {"type":"node","id":1,"lat":50.8619,"lon":7.1216,"tags":{"amenity":"fire_station","name":"Flughafenfeuerwehr"}},
              {"type":"node","id":2,"lat":50.8678,"lon":7.1361,"tags":{"amenity":"fire_station","name":"Flughafenfeuerwehr Köln"}},
              {"type":"node","id":3,"lat":50.8700,"lon":7.1500,"tags":{"amenity":"fire_station","name":"Wache 3","operator":"Bundeswehr"}},
              {"type":"way","id":4,"geometry":[{"lat":50.860,"lon":7.120},{"lat":50.860,"lon":7.124},{"lat":50.864,"lon":7.124},{"lat":50.864,"lon":7.120},{"lat":50.860,"lon":7.120}],"tags":{"landuse":"military"}}
            ]}
            """;
        var stations = FireStationLookup.Parse(json);

        Assert.Equal(3, stations.Count);
        Assert.True(stations[0].Military);     // in der militärischen Fläche
        Assert.False(stations[1].Military);
        Assert.True(stations[2].Military);     // Betreiber Bundeswehr
        Assert.Equal("Bundeswehr", stations[2].Operator);
        Assert.Contains("landuse", FireStationLookup.Query("EDDK"));
        Assert.True(FireStationLookup.IsMilitaryOperator("Royal Air Force"));
        Assert.False(FireStationLookup.IsMilitaryOperator("Flughafen Köln/Bonn GmbH"));
    }

    [Fact]
    public void Zivile_Einsaetze_ohne_militaerische_Wachen_ausser_auf_dem_Militaerflugplatz()
    {
        var graph = Graph();
        var civil = Station(graph, "Hauptwache", 0, 260);
        var army = Station(graph, "Bundeswehr", -700, 260) with { Military = true };

        var kept = FireStationLookup.WithoutMilitary([civil, army], out var dropped);
        Assert.Equal(["Hauptwache"], kept.Select(s => s.Name));
        Assert.Equal(["Bundeswehr"], dropped.Select(s => s.Name));

        // Nur militärische Wachen (Militärflugplatz): alle bleiben
        Assert.Single(FireStationLookup.WithoutMilitary([army], out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Wache_in_der_Mitte_bevorzugt_Aussenwache_an_ihrem_Bahnende_bleibt()
    {
        // Test EDDK 05.11.: FLF 1 kam von der Gebäudebrandschutzwache am Rand statt von der Hauptwache. „Rand“ liegt näher an der
        // Aufsetzzone (x = -400) als „Mitte“, aber weit weg von der Bahnmitte; „Ost“ ist eine Außenwache am Bahnende (Haltepunkt x = 800)
        var graph = Graph();
        var centre = FirePlanner.RunwayCentre(graph);
        Assert.InRange(centre.East, -1, 1);
        Assert.InRange(centre.North, -1, 1);
        IReadOnlyList<FireStation> stations = [Station(graph, "Rand", -700, 260), Station(graph, "Mitte", 0, 260), Station(graph, "Ost", 900, 260)];

        var full = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null, stations, allStations: true)!;
        var byUnit = full.Vehicles.ToDictionary(v => v.Unit.Name, v => v.Station.Name);

        Assert.Equal("Mitte", byUnit["FLF 1"]);   // ohne Aufschlag „Rand“ (gut 100 m kürzer)
        Assert.Equal("Ost", byUnit["FLF 2"]);     // die Außenwache bleibt, ihr Weg ist viel kürzer
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
    public void Pan_Pan_alles_von_der_Wache_mit_dem_kuerzesten_Weg_nicht_der_naechsten_in_Luftlinie()
    {
        // Test EDDL 23R, 04.11.: Bei Pan Pan kam alles von der Wache Nord, die in Luftlinie näher an der Bahnmitte liegt, obwohl die
        // Hauptwache den kürzeren Weg hat (bei Mayday kamen sie von dort). Hier liegt "Nord" mittig, aber fast 400 m vom Rollweg weg,
        // "Ost" in Luftlinie weiter weg, dafür direkt an A: Wege gut 2,7 km gegen gut 2,2 km (Aufsetzzone x = -400, Mitte 0, Stoppstelle 800)
        var graph = Graph();
        IReadOnlyList<FireStation> stations = [Station(graph, "Nord", 0, 598), Station(graph, "Ost", 600, 230)];
        Assert.True(Distance(0, 598, 0, 0) < Distance(600, 230, 0, 0));

        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null, stations)!;

        Assert.Equal(["Ost"], plan.Vehicles.Select(v => v.Station.Name).Distinct());
        Assert.Equal(3, plan.Vehicles.Count);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Notfall_am_Boden_von_der_Wache_mit_dem_kuerzesten_Weg()
    {
        // Einsatzort A bei x = -600; "Weit" steht zuerst in der Liste, gewählt wird nach dem Weg (gut 200 m gegen 1,5 km), nicht nach der Reihenfolge
        var graph = Graph();
        IReadOnlyList<FireStation> stations = [Station(graph, "Weit", 900, 260), Station(graph, "Nah", -400, 260)];

        var plan = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 6), -600, 200, 30, stations)!;

        Assert.True(plan.FromOsm);
        Assert.Equal(["Nah"], plan.Vehicles.Select(v => v.Station.Name).Distinct());
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
    public void Spaeter_Alarm_FLF_1_faehrt_gleich_auf_die_Spur_und_reiht_sich_hinten_ein()
    {
        // Test EDDK 05.11. (Pan Pan 60 s vor dem Aufsetzen): FLF 1 fuhr erst weiter zur Aufsetzzone, obwohl das Flugzeug längst vorbei
        // war, und lag dann 2 km zurück. Jetzt fährt es gleich auf die Spur – hinter das ELW, das schon folgt, ohne es zu überholen.
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

        // Alarm kurz vor dem Aufsetzen: einmal in der Luft, dann gleich die Landung bei x = -700
        Step(new FireAircraftState(-3000, 0, 90, 75, false));
        var (x, speed) = (-700.0, 70.0);
        var flf1Standby = false;
        var flf1Joined = false;
        while (x < 400)
        {
            speed = Math.Max(10, speed - (2.2 * dt));
            x += speed * dt;
            Step(new FireAircraftState(x, 0, 90, speed, true));
            flf1Standby |= V("FLF 1").Phase == FireVehiclePhase.Standby;
            flf1Joined |= V("FLF 1").Phase is FireVehiclePhase.Joining or FireVehiclePhase.Following;
            if (V("FLF 1").Phase == FireVehiclePhase.Following && V("ELW 1").Phase == FireVehiclePhase.Following)
            {
                Assert.True(V("FLF 1").GapM >= V("ELW 1").GapM - 1, "FLF 1 überholt das ELW");
            }
        }

        Assert.False(flf1Standby, "FLF 1 ist erst zum Haltepunkt an der Aufsetzzone gefahren");
        Assert.True(flf1Joined, "FLF 1 ist nicht auf die Spur gefahren");

        // Abrollen über den Zubringer bei x = 400 ohne Halt: FLF 1 begleitet, ELW und FLF 2 rücken ein
        var north = 0.0;
        while (north < 200)
        {
            north += 5 * dt;
            Step(new FireAircraftState(400, north, 0, 5, true));
        }

        Assert.Contains(V("FLF 1").Phase, new[] { FireVehiclePhase.Joining, FireVehiclePhase.Following });
        Assert.Contains(V("ELW 1").Phase, new[] { FireVehiclePhase.Returning, FireVehiclePhase.Gone });
        Assert.Contains(V("FLF 2").Phase, new[] { FireVehiclePhase.Returning, FireVehiclePhase.Gone });
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

    [Fact]
    public async Task OSM_Fehlschlag_wird_gemerkt_und_alte_Wachen_bleiben()
    {
        var dir = Path.Combine(Path.GetTempPath(), "duopilo-fire-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Kein Server: leer, der Fehlschlag steht im Zwischenspeicher
            var lookup = new FireStationLookup("http://127.0.0.1:9/api/interpreter", dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Empty((await lookup.GetAsync("eddl")).Stations);
            var file = Path.Combine(dir, "cache", "fire-stations", "EDDL.json");
            Assert.Contains("\"Ok\":false", File.ReadAllText(file), StringComparison.Ordinal);

            // Abgelaufene, aber gute Daten: bleiben, auch wenn die Abfrage scheitert (beide Server)
            File.WriteAllText(
                Path.Combine(dir, "cache", "fire-stations", "EDDK.json"),
                "{\"FetchedUtc\":\"2020-01-01T00:00:00+00:00\",\"Ok\":true,\"Stations\":[{\"Name\":\"Wache 1\",\"Latitude\":50.87,\"Longitude\":7.14}]}");
            var two = new FireStationLookup("http://127.0.0.1:9/a, http://127.0.0.1:9/b", dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var old = await two.GetAsync("EDDK");
            Assert.Equal("Wache 1", Assert.Single(old.Stations).Name);
            Assert.Contains("\"Ok\":true", File.ReadAllText(Path.Combine(dir, "cache", "fire-stations", "EDDK.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wache_neben_einem_abgeschnittenen_Rollweg_faehrt_ueber_den_naechsten_erreichbaren()
    {
        // Der nächste Rollpunkt der Wache gehört zu einem Stück ohne Verbindung; früher fiel das auf eine Parkposition zurück
        var graph = Graph(stub: true);
        var station = Station(graph, "Flughafenfeuerwehr", 315, 360);

        var ground = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 6), -600, 200, 30, [station])!;
        Assert.True(ground.FromOsm);
        var (e, n, _) = ground.Vehicles[0].Route.At(0);
        Assert.InRange(Distance(e, n, 315, 360), 0, 1);

        var standby = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 6), TrafficWake.Medium, null, [station])!;
        Assert.True(standby.FromOsm);
    }

    [Fact]
    public void Fahrwege_der_Bodenfahrzeuge_nur_fuer_die_Feuerwehr()
    {
        // Ohne Fahrwege (Flugzeuge) bleibt das Stück abgeschnitten, mit ihnen führt ein Weg von dort auf Rollweg A
        var plain = Graph(stub: true);
        var roads = Graph(stub: true, roads: true);
        var stub = roads.Nodes.Count(n => !n.IsParking) - 2;
        Assert.Equal(0, plain.VehicleRoads);
        Assert.Equal(1, roads.VehicleRoads);
        Assert.Null(plain.Route(stub, 6));
        Assert.NotNull(roads.Route(stub, 6));

        // Der 1,3 km lange Abschnitt ist ein falsch gelesener Index und fehlt
        Assert.Equal(plain.BrokenPaths + 1, roads.BrokenPaths);
    }

    [Fact]
    public void Raeder_und_Lenkung_aus_der_Fahrt()
    {
        // 200 m nach Norden, Rechtskurve, 200 m nach Osten: die Strecke zählt mit, in der Kurve lenkt es rechts, danach wieder gerade
        var route = FirePlanner.BuildPath([(0, 0), (0, 200), (200, 200)], FireProfile.MaxMps);
        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Rollout), "test", route, new FireTrail(), (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 164);
        var maxRight = 0.0;
        for (var i = 0; i < 2000 && vehicle.Phase == FireVehiclePhase.Responding; i++)
        {
            vehicle.Advance(0.05);
            maxRight = Math.Max(maxRight, vehicle.CurvaturePerM);
        }

        Assert.Equal(FireVehiclePhase.Standby, vehicle.Phase);
        Assert.InRange(vehicle.OdometerM, route.LengthM - 1, route.LengthM + 1);
        Assert.InRange(maxRight, 0.5 / FirePlanner.CornerRadiusM(Math.PI / 2), FireVehicle.MaxCurvaturePerM);
        Assert.InRange(vehicle.CurvaturePerM, -0.005, 0.005);

        var vars = vehicle.Pose().Vars!.ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal(vehicle.OdometerM, vars["L:1:DUOPILO_WEG"]);
        Assert.Equal(vehicle.CurvaturePerM, vars["L:1:DUOPILO_KURVE"]);
        Assert.Equal(0, vars["L:1:DUOPILO_TEMPO"]);
        Assert.Equal(0, vars["L:1:DUOPILO_SIRENE"]);
    }

    [Fact]
    public void Bahn_auf_dem_Weg_Halt_an_der_Haltelinie_bis_zur_Freigabe()
    {
        // Test EDDK 05.11.: die Fahrzeuge kreuzten Bahnen ohne Halt. Weg von Norden über die 09/27 (45 m breit) nach Süden
        var graph = Graph();
        var path = FirePlanner.BuildPath([(0, 200), (0, -200)], FireProfile.MaxMps);
        var crossing = Assert.Single(FireCrossings.Find(graph, path));
        Assert.Equal("09/27", crossing.Designation);
        Assert.InRange(crossing.EntryS, 172, 178);                                        // Bahnrand 25,5 m neben der Mittellinie
        Assert.InRange(crossing.StopS, 200 - 62.5 - 3, 200 - 62.5);                     // halbe Breite plus 40 m davor

        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Rollout), "test", path, new FireTrail(), graph.ToLatLon, 164);
        vehicle.UseCrossings(p => FireCrossings.Find(graph, p));
        Assert.Equal(crossing, vehicle.NextCrossing);
        for (var i = 0; i < 400; i++)
        {
            vehicle.Advance(0.1);
        }

        Assert.True(vehicle.HoldingShort);
        Assert.InRange(vehicle.NorthM, 62.5, 66);
        Assert.InRange(vehicle.CrossingHeldS, 10, 40);
        Assert.Equal(FireVehiclePhase.Responding, vehicle.Phase);

        // Freigabe: über die Bahn bis ans Ende des Wegs
        vehicle.ClearCrossing();
        for (var i = 0; i < 600 && vehicle.Phase == FireVehiclePhase.Responding; i++)
        {
            vehicle.Advance(0.1);
        }

        Assert.Equal(FireVehiclePhase.Standby, vehicle.Phase);
        Assert.InRange(vehicle.NorthM, -201, -199);
        Assert.Null(vehicle.NextCrossing);
    }

    [Fact]
    public void Freigabe_nur_auf_der_Bahn_des_Notfalls_nach_der_Landung_nicht_noetig()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.LocalStandby, plan, TrafficWake.Medium, 7);
        var vehicle = new FireVehicle(plan.Vehicles[0].Unit, "test", plan.Vehicles[0].Route, operation.Trail, graph.ToLatLon, 164);
        operation.Add(vehicle);
        var emergency = new FireCrossing(Runway, 0, 10);
        var other = new FireCrossing(new AirportRunway(51.0, 7.0, 50, 0, 1500, 30, "18", "36"), 0, 10);
        Assert.True(operation.IsEmergencyRunway(Runway));
        Assert.False(operation.IsEmergencyRunway(other.Runway));

        // Vor der Landung (das Flugzeug kommt im Endanflug): vor jeder Bahn halten
        operation.Update(new FireAircraftState(-3000, 0, 90, 75, false), 0.25);
        Assert.True(operation.NeedsClearance(vehicle, emergency));

        // Gelandet: auf der Landebahn arbeitet die Feuerwehr, andere Bahnen nur mit Freigabe
        operation.Update(new FireAircraftState(-700, 0, 90, 70, true), 0.25);
        Assert.False(operation.NeedsClearance(vehicle, emergency));
        Assert.True(operation.NeedsClearance(vehicle, other));

        // Rückfahrt: wieder vor jeder Bahn
        operation.ReturnAll();
        Assert.True(operation.NeedsClearance(vehicle, emergency));
    }

    [Fact]
    public void Mayday_mit_Blaulicht_vor_keiner_Bahn_Freigabe_auf_der_Rueckfahrt_schon()
    {
        // Nachtest EDDK 05.11.: Die Feuerwehr hat Vorfahrt, bei Mayday ist der Flughafen für den eigenen KI-Verkehr gesperrt
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.FullEmergency, plan, TrafficWake.Heavy, 9);
        var vehicle = new FireVehicle(plan.Vehicles[0].Unit, "test", plan.Vehicles[0].Route, operation.Trail, graph.ToLatLon, 164);
        operation.Add(vehicle);
        var other = new FireCrossing(new AirportRunway(51.0, 7.0, 50, 0, 1500, 30, "18", "36"), 0, 10);

        operation.Update(new FireAircraftState(-3000, 0, 90, 75, false), 0.25);
        Assert.False(operation.NeedsClearance(vehicle, new FireCrossing(Runway, 0, 10)));
        Assert.False(operation.NeedsClearance(vehicle, other));

        operation.ReturnAll();
        Assert.True(operation.NeedsClearance(vehicle, other));
    }

    [Theory]
    [InlineData(FireAlertLevel.FullEmergency, false)]
    [InlineData(FireAlertLevel.LocalStandby, true)]
    public void Mayday_kreuzt_schon_vor_der_Landung_ohne_Halt(FireAlertLevel level, bool holds)
    {
        // Nachtest .21 EDDK 07.11.: FLF 3 stand bei Mayday mit Blaulicht bis zum Aufsetzen vor der 06/24 – die Freigabe ohne Abfrage
        // kam erst nach der Landung. Bei Pan Pan hält es (die Abfrage macht der FireService, den es hier nicht gibt).
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(level, 9), TrafficWake.Heavy, null)!;
        var operation = new FireOperation(graph, level, plan, TrafficWake.Heavy, 9);
        var vehicle = new FireVehicle(plan.Vehicles[0].Unit, "test", plan.Vehicles[0].Route, operation.Trail, graph.ToLatLon, 164);
        operation.Add(vehicle);
        vehicle.Drive(FirePlanner.BuildPath([(300, 150), (300, -150)], FireProfile.MaxMps), FireVehiclePhase.Responding, FireVehiclePhase.Standby, FireProfile.MaxMps);
        Assert.NotNull(vehicle.NextCrossing);

        var airborne = new FireAircraftState(-9000, 0, 90, 75, false);
        var held = false;
        for (var t = 0; t < 4 * 60; t++)
        {
            operation.Update(airborne, 0.25);
            vehicle.Advance(0.25);
            held |= vehicle.HoldingShort;
        }

        Assert.Equal(holds, held);
        Assert.Equal(holds, vehicle.NorthM > 0);
    }

    [Fact]
    public void Mayday_FLF_1_folgt_sofort_FLF_3_kommt_ueber_die_Bahn_entgegen()
    {
        // Nachtest EDDK 05.11. (A330, Mayday): FLF 1 fuhr erst unter 40 kt auf die Spur, kurz vor dem Stopp; FLF 3 wartete am Bahnende
        // auf den Stopp und fuhr dann 2,3 km über Rollwege und Straßen zum Flugzeug
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.FullEmergency, plan, TrafficWake.Heavy, 9);
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

        var airborne = new FireAircraftState(-3000, 0, 90, 75, false);
        for (var t = 0; t < 2400 && vehicles.Any(v => v.Phase != FireVehiclePhase.Standby); t++)
        {
            Step(airborne);
        }

        Assert.All(vehicles, v => Assert.Equal(FireVehiclePhase.Standby, v.Phase));
        var flf3Hold = (V("FLF 3").EastM, V("FLF 3").NorthM);
        Assert.True(flf3Hold.EastM > 600, "FLF 3 steht nicht am Bahnende");

        // Aufsetzen bei x = -700 mit 70 m/s, Bremsen mit 2,2 m/s² bis zum Stand bei gut x = 400
        var (x, speed) = (-700.0, 70.0);
        double? flf1JoinedAt = null;
        var flf3MinAhead = double.MaxValue;
        while (speed > 0)
        {
            speed = Math.Max(0, speed - (2.2 * dt));
            x += speed * dt;
            Step(new FireAircraftState(x, 0, 90, speed, true));
            if (flf1JoinedAt is null && V("FLF 1").Phase is FireVehiclePhase.Joining or FireVehiclePhase.Following)
            {
                flf1JoinedAt = speed;
            }

            if (Math.Abs(V("FLF 3").NorthM) < 50)
            {
                flf3MinAhead = Math.Min(flf3MinAhead, V("FLF 3").EastM - x);
            }
        }

        // FLF 1 geht auf die Spur, sobald das Flugzeug vorbei ist (bis .19 erst unter 20,6 m/s)
        Assert.True(flf1JoinedAt > 40, $"FLF 1 erst bei {flf1JoinedAt:0} m/s auf der Spur");

        // FLF 3 ist schon vor dem Stopp auf der Bahn unterwegs, bleibt aber vor dem rollenden Flugzeug
        Assert.True(Math.Abs(V("FLF 3").NorthM) < 30, $"FLF 3 ist beim Stopp nicht auf der Bahn ({V("FLF 3").EastM:0}/{V("FLF 3").NorthM:0})");
        Assert.True(V("FLF 3").EastM < flf3Hold.EastM, "FLF 3 ist beim Stopp nicht entgegengefahren");
        Assert.InRange(flf3MinAhead, FireOperation.ApproachClearM - 10, 5000);

        // Stillstand: FLF 3 fährt über die Bahn an seinen Platz hinten links, nie über Rollweg A (y = 200)
        var stopped = new FireAircraftState(x, 0, 90, 0, true);
        var maxNorth = double.MinValue;
        for (var t = 0; t < 4 * 180 && V("FLF 3").Phase != FireVehiclePhase.AtAircraft; t++)
        {
            Step(stopped);
            maxNorth = Math.Max(maxNorth, V("FLF 3").NorthM);
        }

        Assert.Equal(FireVehiclePhase.AtAircraft, V("FLF 3").Phase);
        Assert.True(maxNorth < 150, "FLF 3 ist über den Rollweg gefahren");
    }

    [Fact]
    public void Spaeter_Mayday_FLF_3_faehrt_von_unterwegs_zur_Stoppstelle_nicht_zur_Aufsetzstelle()
    {
        // Nachtest .20 EDDK 07.11.: FLF 3 war beim Aufsetzen noch unterwegs zum Bahnende, bekam die Aufsetzstelle als Ziel, fuhr 2 km vom
        // Flugzeug weg und bekam nach dem Stopp erst am Ende dieses Wegs seinen Platz (2 min)
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 9), TrafficWake.Heavy, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.FullEmergency, plan, TrafficWake.Heavy, 9);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        var vehicles = operation.Vehicles;
        var flf3 = vehicles.Single(v => v.Unit.Name == "FLF 3");
        const double dt = 0.25;
        void Step(FireAircraftState state)
        {
            operation.Update(state, dt);
            foreach (var vehicle in vehicles)
            {
                vehicle.Advance(dt);
            }
        }

        // 20 s unterwegs, dann landet das Flugzeug (x = -700, 70 m/s, 2,2 m/s²)
        for (var t = 0; t < 80; t++)
        {
            Step(new FireAircraftState(-3000, 0, 90, 75, false));
        }

        Assert.Equal(FireVehiclePhase.Responding, flf3.Phase);
        var eastAtTouchdown = flf3.EastM;
        var minEast = double.MaxValue;
        var (x, speed) = (-700.0, 70.0);
        while (speed > 0)
        {
            speed = Math.Max(0, speed - (2.2 * dt));
            x += speed * dt;
            Step(new FireAircraftState(x, 0, 90, speed, true));
            minEast = Math.Min(minEast, flf3.EastM);
        }

        Assert.True(minEast > eastAtTouchdown - 30, $"FLF 3 fuhr von {eastAtTouchdown:0} nach {minEast:0} zurück");

        // Nach dem Stopp an seinen Platz, nicht erst ans Ende eines Wegs
        var stopped = new FireAircraftState(x, 0, 90, 0, true);
        var seconds = 0.0;
        for (; seconds < 180 && flf3.Phase != FireVehiclePhase.AtAircraft; seconds += dt)
        {
            Step(stopped);
        }

        Assert.Equal(FireVehiclePhase.AtAircraft, flf3.Phase);
        Assert.True(seconds < 90, $"FLF 3 erst nach {seconds:0} s am Flugzeug");
    }

    [Fact]
    public void Freie_Abstellposition_ohne_Bruecke_lieber_Vorfeld_und_weg_vom_Gebaeude()
    {
        // Rollweg bei y = 300; Gates G1 (mit Brücke) und G2 vor dem Terminal (y 380–440), Vorfeld R2 daneben, Vorfeld R1 frei im Süden
        var points = new List<TaxiPointData>
        {
            new(1, 0, -100, 300), new(1, 0, 0, 300), new(1, 0, 100, 300), new(1, 0, 200, 300), new(1, 0, 400, 300), new(1, 0, 500, 300),
        };
        var paths = new List<TaxiPathData>();
        for (var i = 0; i < points.Count - 1; i++)
        {
            paths.Add(new TaxiPathData(1, 23, 0, 0, i, i + 1, 0));
        }

        paths.Add(new TaxiPathData(3, 20, 0, 0, 1, 0, 0));
        paths.Add(new TaxiPathData(3, 20, 0, 0, 2, 1, 0));
        paths.Add(new TaxiPathData(3, 20, 0, 0, 3, 2, 0));
        paths.Add(new TaxiPathData(3, 20, 0, 0, 4, 3, 0));

        // Die Bahn 09/27 (für die Zuordnung der Achsen), über einen Zubringer bei x = -100 am Rollweg
        points.Add(new TaxiPointData(1, 0, -1000, 0));
        points.Add(new TaxiPointData(1, 0, -100, 0));
        points.Add(new TaxiPointData(1, 0, 1000, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, points.Count - 3, points.Count - 2, 0));
        paths.Add(new TaxiPathData(2, 45, 9, 0, points.Count - 2, points.Count - 1, 0));
        paths.Add(new TaxiPathData(1, 23, 0, 0, 0, points.Count - 2, 0));
        var data = new AirportTaxiData(
            "EDXF",
            51.0,
            7.0,
            50,
            points,
            [
                new TaxiParkingData(9, 0, 12, 0, 1, 0, 0, 20, 0, 360),      // G1, Gate mit Brücke
                new TaxiParkingData(9, 0, 12, 0, 2, 0, 0, 20, 100, 360),    // G2, Gate
                new TaxiParkingData(3, 0, 12, 0, 3, 0, 0, 17, 200, 360),    // R2, Vorfeld am Terminal
                new TaxiParkingData(4, 0, 12, 0, 4, 0, 0, 25, 400, 240),    // R1, Vorfeld frei
            ],
            paths,
            ["A"]);
        var graph = TaxiGraph.Build(data, [Runway]);
        graph.SetBuildings([[(-50, 380), (250, 380), (250, 440), (-50, 440)]]);

        var withJetways = FireStands.Remote(graph, TrafficWake.Medium, new HashSet<int> { 0 }, (0, 0));
        Assert.Equal([400.0, 200.0, 100.0], withJetways.Select(n => Math.Round(n.EastM)));

        // Brücken unbekannt: Gates hinten an, unter ihnen die nähere zuerst
        var unknown = FireStands.Remote(graph, TrafficWake.Medium, null, (0, 0));
        Assert.Equal([400.0, 200.0, 0.0, 100.0], unknown.Select(n => Math.Round(n.EastM)));

        // Für eine A330 (Heavy, Radius ab 26 m) passt keine
        Assert.Empty(FireStands.Remote(graph, TrafficWake.Heavy, null, (0, 0)));
    }

    [Fact]
    public void Freie_Bahn_zaehlt_beim_Routen_wie_ein_Rollweg()
    {
        // Vom Haltepunkt am Ostende (800/90) zum Zubringer bei x = -400: sonst über Rollweg A (y = 200), mit freier Bahn über die Bahn
        var graph = Graph();
        var start = graph.Nodes.Single(n => Math.Abs(n.EastM - 800) < 1 && Math.Abs(n.NorthM - 90) < 1);
        var end = graph.Nodes.Single(n => Math.Abs(n.EastM + 400) < 1 && Math.Abs(n.NorthM - 90) < 1);
        var taxiway = graph.Route(start.Id, end.Id)!;
        Assert.Contains(taxiway, n => graph.Nodes[n].NorthM > 150);

        graph.FreeRunway = Runway;
        var runway = graph.Route(start.Id, end.Id)!;
        Assert.All(runway, n => Assert.True(graph.Nodes[n].NorthM < 100));
        graph.FreeRunway = null;
    }

    [Fact]
    public void Flughafen_gesperrt_fuer_den_eigenen_Verkehr_ausser_dem_Notfall()
    {
        var closures = new AirportClosures();
        Assert.Null(closures.ClosedFor("EDDK", "DUO02"));
        Assert.True(closures.Close("EDDK", "Mayday", "DUO01"));
        Assert.False(closures.Close("eddk", "Mayday", "DUO01"));
        Assert.Equal("Mayday", closures.ClosedFor("EDDK", "DUO02"));
        Assert.Equal("Mayday", closures.ClosedFor(" eddk ", null));
        Assert.Null(closures.ClosedFor("EDDK", "duo01"));                 // das Flugzeug des Notfalls selbst
        Assert.Null(closures.ClosedFor("EDDL", "DUO02"));
        Assert.True(closures.Open("EDDK"));
        Assert.Null(closures.ClosedFor("EDDK", "DUO02"));
        Assert.False(closures.Open("EDDK"));
    }

    [Fact]
    public void Z_Variablen_erst_nach_dem_Laden_des_Objekts()
    {
        // Test EDDK 05.11.: sieben DATA_ERROR je Fahrzeug im ersten Frame, solange das Objekt noch lädt
        var route = FirePlanner.BuildPath([(0, 0), (0, 400)], FireProfile.MaxMps);
        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Rollout), "test", route, new FireTrail(), (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 164);
        Assert.Empty(vehicle.Vars());
        Assert.Empty(vehicle.Pose().Vars!);

        for (var i = 0; i < 30; i++)
        {
            vehicle.Advance(0.05);
        }

        Assert.Empty(vehicle.Vars());
        for (var i = 0; i < 12; i++)
        {
            vehicle.Advance(0.05);
        }

        Assert.Equal(7, vehicle.Vars().Length);
        Assert.InRange(vehicle.Vars().Single(v => v.Name == "L:1:DUOPILO_WEG").Value, 0.1, 400);
    }

    [Fact]
    public void Boden_gleich_nach_dem_Spawn_uebernehmen_danach_gleiten()
    {
        // Test EDDK 05.11.: 30 ft zu tief gespawnt, mit 6 ft/s kamen die Fahrzeuge sichtbar aus dem Boden
        var route = FirePlanner.BuildPath([(0, 0), (0, 400)], FireProfile.MaxMps);
        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Rollout), "test", route, new FireTrail(), (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 223);
        vehicle.Advance(0.5);
        vehicle.MeasureGround(253);
        Assert.Equal(253, vehicle.GroundFt);

        for (var i = 0; i < 60; i++)
        {
            vehicle.Advance(0.05);
        }

        // Nach dem Spawn wieder wie bisher: auf den gemessenen Boden gleiten
        vehicle.MeasureGround(259);
        vehicle.Advance(0.5);
        Assert.InRange(vehicle.GroundFt, 255.9, 256.1);
    }

    [Theory]
    [InlineData(223.0, 100.0, 223.0)]   // eigenes Flugzeug an der Wache: seine Bodenhöhe
    [InlineData(223.0, 1300.0, 253.0)]  // 1,3 km weg (EDDK 05.11.): Platzhöhe
    [InlineData(150.0, 50.0, 253.0)]    // passt nicht zur Platzhöhe: Platzhöhe
    [InlineData(null, null, 253.0)]     // in der Luft: Platzhöhe
    public void Spawnhoehe_nur_vom_eigenen_Flugzeug_in_der_Naehe(double? own, double? distance, double expected) =>
        Assert.Equal(expected, FireService.SpawnGroundFt(253, own, distance));

    [Fact]
    public void Martinshorn_nur_auf_der_Anfahrt_Bremslicht_beim_Langsamerwerden()
    {
        var route = FirePlanner.BuildPath([(0, 0), (0, 400)], FireProfile.MaxMps);
        var vehicle = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 1, FireStandbyRole.Rollout), "test", route, new FireTrail(), (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 164);
        var quiet = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 2, FireStandbyRole.Rollout), "test", route, new FireTrail(), (e, n) => (51 + (n / 111_000), 7 + (e / 70_000)), 164)
        {
            SirenAllowed = false,
        };

        // Anfahren: Horn an, kein Bremslicht
        for (var i = 0; i < 40; i++)
        {
            vehicle.Advance(0.05);
            quiet.Advance(0.05);
        }

        Assert.True(vehicle.Siren);
        Assert.False(quiet.Siren);
        Assert.False(vehicle.Braking);

        // Bremsen vor dem Haltepunkt: Bremslicht; angekommen (Standby): Horn aus
        var braked = false;
        for (var i = 0; i < 2000 && vehicle.Phase == FireVehiclePhase.Responding; i++)
        {
            vehicle.Advance(0.05);
            braked |= vehicle.Braking;
        }

        Assert.True(braked);
        Assert.Equal(FireVehiclePhase.Standby, vehicle.Phase);
        Assert.False(vehicle.Siren);
        Assert.InRange(vehicle.Vars().Single(v => v.Name == "L:1:DUOPILO_TEMPO").Value, 0, 0.01);
    }

    [Fact]
    public void Hindernisse_am_Einsatzort_zaehlen_nicht()
    {
        // Ein geparktes Flugzeug 20 m neben P1 sperrt die Zufahrt; am Einsatzort P1 ist es kein Hindernis mehr, weiter weg schon
        var graph = Graph();
        var (lat, lon) = graph.ToLatLon(100, 290);
        var (farLat, farLon) = graph.ToLatLon(-600, 215);
        graph.SetObstacles([(lat, lon), (farLat, farLon)], 30);
        var blocked = graph.BlockedEdges;
        Assert.True(blocked > 0);

        graph.ClearObstacles(100, 320, 60);
        Assert.Single(graph.Obstacles);
        Assert.InRange(graph.BlockedEdges, 1, blocked - 1);

        graph.ClearObstacles();
        Assert.Empty(graph.Obstacles);
        Assert.Equal(0, graph.BlockedEdges);
        Assert.Equal("P1", graph.FindParking("p01")?.Parking?.Label);
    }

    [Fact]
    public void Am_Gate_nur_hinter_und_rechts_neben_dem_Flugzeug()
    {
        // Vor der Nase das Terminal, links vorn die Fluggastbrücke (Test EDDL B9, 03.11.: ein FLF stand halb in der Brücke)
        const double u = 30;
        var units = FireCategory.Lineup(FireAlertLevel.FullEmergency, 9);
        Assert.All(units, unit =>
        {
            var (x, y) = FireOperation.StandPost(unit, u);
            Assert.True(x <= 0.5 * u, unit.Name + " vor der Nase");
            Assert.False(x > 0 && y < 0, unit.Name + " links vorn");
        });

        // Umweg auf die andere Seite immer hinter dem Heck, nie vor der Nase
        var points = FireOperation.DetourPoints((-0.5 * u, -1.2 * u), FireOperation.StandPost(units[0], u), u, noseBlocked: true);
        Assert.All(points, p => Assert.True(p.X <= 0.01, $"Umweg über {p.X:0}/{p.Y:0}"));
    }

    [Fact]
    public void Notfall_am_Boden_alle_stellen_sich_auf_auch_von_hinten()
    {
        // Test EDDL V81 (03.11.): Die Fahrzeuge kamen über die Zufahrt von hinten an die Position; FLF 2 und ELW drehten um, weil das
        // Flugzeug schon über 10 s abseits der Bahn stand (Regel für das Abrollen nach der Landung)
        var graph = Graph();
        var p1 = graph.FindParking("P1")!;
        var plan = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 7), p1.EastM, p1.NorthM, 30, [Station(graph, "Wache", 900, 260)], p1)!;
        var operation = new FireOperation(graph, FireAlertLevel.GroundIncident, plan, TrafficWake.Medium, 7);
        foreach (var vp in plan.Vehicles)
        {
            operation.Add(new FireVehicle(vp.Unit, "test", vp.Route, operation.Trail, graph.ToLatLon, 164));
        }

        var scene = new FireAircraftState(p1.EastM, p1.NorthM, p1.Parking!.HeadingDeg, 0, true);
        for (var t = 0; t < 4 * 150; t++)
        {
            operation.Update(scene, 0.25);
            foreach (var vehicle in operation.Vehicles)
            {
                vehicle.Advance(0.25);
            }
        }

        Assert.Equal(3, operation.Vehicles.Count);
        Assert.All(operation.Vehicles, v => Assert.Contains(v.Phase, new[] { FireVehiclePhase.Positioning, FireVehiclePhase.AtAircraft }));
    }

    [Fact]
    public void Auf_demselben_Weg_mit_Abstand_statt_ineinander()
    {
        // Test EDDL 03.11.: FLF 2 und ELW fuhren gleichzeitig zurück und hingen ineinander. Zwei Fahrzeuge starten am selben Punkt auf
        // demselben 300-m-Weg: das zweite wartet, bis das erste weg ist, und bleibt dahinter; am Ende kommen beide an
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.LocalStandby, 7), TrafficWake.Medium, null)!;
        var operation = new FireOperation(graph, FireAlertLevel.LocalStandby, plan, TrafficWake.Medium, 7);
        var first = new FireVehicle(new FireUnit(FireVehicleKind.Flf, 2, FireStandbyRole.Rollout), "test", FirePlanner.BuildPath([(0, 0), (0, 300)], FireProfile.ReturnMps), operation.Trail, graph.ToLatLon, 164);
        var second = new FireVehicle(new FireUnit(FireVehicleKind.Elw, 1, FireStandbyRole.Rollout), "test", FirePlanner.BuildPath([(0, 0), (0, 300)], FireProfile.ReturnMps), operation.Trail, graph.ToLatLon, 164);
        operation.Add(first);
        operation.Add(second);

        var airborne = new FireAircraftState(-6000, 0, 90, 80, false);
        var closest = double.MaxValue;
        for (var t = 0; t < 4 * 120; t++)
        {
            operation.Update(airborne, 0.25);
            first.Advance(0.25);
            second.Advance(0.25);
            if (t > 4 * 5 && first.OnPath && second.OnPath)
            {
                closest = Math.Min(closest, Distance(first.EastM, first.NorthM, second.EastM, second.NorthM));
            }
        }

        Assert.InRange(closest, FireOperation.StopGapM - 2, double.MaxValue);
        Assert.False(first.OnPath);
        Assert.False(second.OnPath);
        Assert.InRange(Distance(second.EastM, second.NorthM, 0, 300), 0, 1);
    }

    [Fact]
    public void Am_Gate_ueber_die_Zufahrt_der_Position()
    {
        // P1 (100/320) hängt über seine Zufahrt an A bei x = 0; die Fahrzeuge halten auf dieser Zufahrt, 1,5 Grundmaß vor der Position
        var graph = Graph();
        var p1 = graph.FindParking("P1")!;
        var plan = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 6), p1.EastM, p1.NorthM, 30, [Station(graph, "Wache", 900, 260)], p1)!;

        Assert.Same(p1, plan.Stand);
        var (e, n, _) = plan.Vehicles[0].Route.At(plan.Vehicles[0].Route.LengthM);
        Assert.InRange(TaxiGraph.SegmentDistance(e, n, 0, 200, 100, 320), 0, 1.5);
        Assert.InRange(Distance(e, n, 100, 320), 40, 50);
    }

    [Fact]
    public void Overpass_mit_Umriss_Toren_und_Gebaeuden()
    {
        // Wache als Fläche (Halle 40 × 20 m) mit einem Hallentor, dazu ein Terminal und eine Fluggastbrücke
        const string json = """
            {"elements":[
              {"type":"way","id":1,"tags":{"amenity":"fire_station","building":"fire_station","name":"Flughafenfeuerwehr"},
               "geometry":[{"lat":51.0030,"lon":7.0000},{"lat":51.0030,"lon":7.0006},{"lat":51.0032,"lon":7.0006},{"lat":51.0032,"lon":7.0000},{"lat":51.0030,"lon":7.0000}]},
              {"type":"node","id":2,"lat":51.0030,"lon":7.0003,"tags":{"door":"overhead"}},
              {"type":"way","id":3,"tags":{"building":"terminal"},
               "geometry":[{"lat":51.0040,"lon":7.0010},{"lat":51.0040,"lon":7.0020},{"lat":51.0045,"lon":7.0020},{"lat":51.0045,"lon":7.0010},{"lat":51.0040,"lon":7.0010}]},
              {"type":"way","id":4,"tags":{"aeroway":"jet_bridge"},"geometry":[{"lat":51.0040,"lon":7.0012},{"lat":51.0038,"lon":7.0012}]}
            ]}
            """;
        var data = FireStationLookup.ParseAll(json);

        var station = Assert.Single(data.Stations);
        Assert.Equal("Flughafenfeuerwehr", station.Name);
        Assert.NotNull(station.Outline);
        Assert.True(Assert.Single(station.Doors).Garage);
        Assert.Equal(3, data.Buildings.Count);
        Assert.Contains(data.Buildings, b => !b.Closed);
    }

    [Fact]
    public void Plaetze_vor_der_Halle_nebeneinander_und_nicht_im_Gebaeude()
    {
        // Halle 40 × 20 m bei (500/270 … 540/290), Südseite zum Rollweg A (y = 200); ohne Tor: an der Hallenseite zur Straße
        var graph = Graph();
        var outline = new List<(double East, double North)> { (500, 270), (540, 270), (540, 290), (500, 290), (500, 270) };
        graph.SetBuildings([outline]);
        var (exits, source) = FireExits.ForOsm(520, 280, outline, [], graph);

        Assert.Equal("outline", source);
        Assert.All(exits.Take(7), e =>
        {
            Assert.InRange(e.NorthM, 262, 267);                  // 4 m vor der Südwand (erste Reihe)
            Assert.InRange(e.HeadingDeg, 170, 190);              // Front nach Süden, aus der Halle
        });
        var firstRow = exits.Take(5).ToList();
        Assert.All(firstRow, a => Assert.All(firstRow.Where(b => !ReferenceEquals(a, b)), b => Assert.True(Distance(a.EastM, a.NorthM, b.EastM, b.NorthM) >= 5.9)));

        // Mit Hallentor: das Tor gewinnt
        var (doorExits, doorSource) = FireExits.ForOsm(520, 280, outline, [(510, 270, true)], graph);
        Assert.Equal("door", doorSource);
        Assert.InRange(Distance(doorExits[0].EastM, doorExits[0].NorthM, 510, 266), 0, 0.5);

        // Liegt ein Fahrzeugstellplatz der Szenerie davor (P1 bei 100/320), nimmt die Wache dort Aufstellung
        Assert.Equal("vehicle_parking", FireExits.ForOsm(120, 280, null, [], graph).Source);

        // Die Wege beginnen vor der Halle und rollen nach Süden heraus, keiner durch das Gebäude
        var station = new FireStation("Halle", 520, 280, 0, 0, "osm") { Exits = exits };
        var plan = FirePlanner.PlanToAircraft(graph, FireCategory.Lineup(FireAlertLevel.GroundIncident, 7), -600, 200, 30, [station])!;
        Assert.Equal(3, plan.Vehicles.Count);
        var starts = plan.Vehicles.Select(v => v.Route.At(0)).ToList();
        Assert.All(starts, a => Assert.All(starts.Where(b => b != a), b => Assert.True(Distance(a.East, a.North, b.East, b.North) >= 5.9)));
        Assert.All(plan.Vehicles, v => Assert.False(graph.CrossesBuilding(v.Route.At(0).East, v.Route.At(0).North, v.Route.At(2).East, v.Route.At(2).North)));
    }

    [Fact]
    public void Parkposition_als_Wache_nebeneinander()
    {
        var graph = Graph();
        var plan = FirePlanner.Plan(graph, Runway09, FireCategory.Lineup(FireAlertLevel.FullEmergency, 7), TrafficWake.Medium, null)!;
        Assert.Equal("P1", plan.Spawn.Name);
        var starts = plan.Vehicles.Where(v => v.Station.Name == "P1").Select(v => v.Route.At(0)).ToList();
        Assert.True(starts.Count >= 3);
        Assert.All(starts, a => Assert.All(starts.Where(b => b != a), b => Assert.True(Distance(a.East, a.North, b.East, b.North) >= 5.9)));
    }

    [Fact]
    public void Gebaeude_als_Hindernis_fuer_gerade_Strecken()
    {
        var graph = Graph();
        graph.SetBuildings([new List<(double East, double North)> { (0, 0), (10, 0), (10, 10), (0, 10), (0, 0) }]);
        Assert.True(graph.CrossesBuilding(-5, 5, 15, 5));
        Assert.True(graph.CrossesBuilding(5, 5, 50, 50));                 // Anfang im Gebäude
        Assert.False(graph.CrossesBuilding(-5, 20, 15, 20));
        Assert.InRange(graph.FirstBuildingHitM([(-20, 5), (-5, 5), (15, 5)])!.Value, 19.9, 20.1);
        Assert.Null(graph.FirstBuildingHitM([(-20, 20), (20, 20)]));
        Assert.Equal((-5.0, 5.0), FirePlanner.CutAt([(-20, 5), (-5, 5), (15, 5)], 15)[^1]);
    }

    [Fact]
    public void Wache_als_Punkt_bekommt_den_Umriss_des_Gebaeudes_darum()
    {
        // Nur amenity=fire_station als Punkt; das Gebäude darum (Halle 500/270 … 540/290) liefert die Hallenseite
        var graph = Graph();
        (double Lat, double Lon) At(double e, double n) => graph.ToLatLon(e, n);
        var corners = new[] { At(500, 270), At(540, 270), At(540, 290), At(500, 290), At(500, 270) };
        var hall = new OsmShape([.. corners.Select(c => new OsmPoint(c.Lat, c.Lon))], true);
        var (lat, lon) = At(520, 280);
        var station = Assert.Single(FireStationLookup.ToStations([new OsmFireStation("Wache", lat, lon)], graph, [hall]));

        Assert.Equal("outline", station.ExitSource);
        Assert.InRange(station.Exits[0].NorthM, 265, 267);
    }

    [Fact]
    public void Wache_als_Gelaende_nimmt_die_Halle_darauf_mit_Seite_zum_Hof()
    {
        // Test EDDK 05.11.: amenity=fire_station auf dem ganzen Grundstück (480/240 … 560/380), Halle 500/270 … 540/290 im Süden,
        // der Hof liegt nördlich davon. Vorher standen die Fahrzeuge 4 m vor dem Grundstücksrand.
        var graph = Graph();
        OsmPoint P(double e, double n)
        {
            var (lat, lon) = graph.ToLatLon(e, n);
            return new OsmPoint(lat, lon);
        }

        OsmShape Shape(params (double E, double N)[] corners) => new([.. corners.Select(c => P(c.E, c.N))], true);
        var site = Shape((480, 240), (560, 240), (560, 380), (480, 380), (480, 240));
        var hall = Shape((500, 270), (540, 270), (540, 290), (500, 290), (500, 270));
        var shed = Shape((550, 360), (555, 360), (555, 365), (550, 365), (550, 360));
        var centre = P(520, 310);
        var osm = new OsmFireStation("Wache", centre.Lat, centre.Lon) { Outline = site.Points };
        IReadOnlyList<OsmShape> buildings = [site, hall, shed];

        Assert.Same(hall.Points, FireStationLookup.HallInSite(site.Points, buildings));
        Assert.Null(FireStationLookup.HallInSite(hall.Points, buildings));          // die Halle selbst ist kein Gelände

        var obstacles = FireStationLookup.WithoutSites(buildings, [osm]);
        Assert.Equal(2, obstacles.Count);
        Assert.DoesNotContain(site, obstacles);

        var station = Assert.Single(FireStationLookup.ToStations([osm], graph, obstacles));
        Assert.Equal("hall", station.ExitSource);
        Assert.All(station.Exits.Take(7), e =>
        {
            Assert.InRange(e.NorthM, 293, 295);                  // 4 m vor der Nordwand der Halle, im Hof
            Assert.InRange(e.EastM, 499, 541);
            Assert.True(e.HeadingDeg is < 10 or > 350);          // Front nach Norden, zum Hof
        });
    }

    [Fact]
    public void Plaetze_vor_einer_Halle_mit_Fluegeln_nie_im_Gebaeude()
    {
        // Nachtest EDDK 05.11.: Hauptwache Köln, Halle mit zwei Flügeln (Umriss aus OSM, Meter um die Wache), Hof im Nordosten. Platz 8
        // lag 24 m von der Mitte der 45-m-Wand im Winkel, 1,5 m hinter ihrem Ende im vorspringenden Südostflügel.
        var graph = Graph();
        (double East, double North)[] hall =
        [
            (-21, 19), (-37, 38), (-20, 54), (-3, 36), (-6, 33), (25, 0), (39, 13), (40, 12), (43, 12), (45, 9), (45, 6), (49, 1), (38, -9),
            (35, -5), (17, -22), (-19, 17), (-23, 13), (-9, -2), (-15, -8), (-43, 23), (-37, 28), (-25, 15), (-21, 19),
        ];
        var outline = hall.Select(p => (p.East + 500, p.North + 300)).ToList();
        var (exits, source) = FireExits.ForOsm(504, 313, outline, [], graph, (517.6, 326.2));

        Assert.Equal("outline", source);
        Assert.Equal(FireExits.Slots, exits.Count);
        Assert.All(exits, e =>
        {
            var rad = e.HeadingDeg * Math.PI / 180;
            Assert.False(
                FireExits.InsideOrAcross(outline, e.EastM, e.NorthM, e.EastM + (Math.Sin(rad) * FireExits.RollOutM), e.NorthM + (Math.Cos(rad) * FireExits.RollOutM)),
                $"Platz {e.EastM - 500:0}/{e.NorthM - 300:0} im Gebäude oder Herausrollen durch die Halle");
            Assert.InRange(e.HeadingDeg, 40, 55);                // Front zum Hof (Nordosten)
        });

        // Alle Plätze auf der Länge der Wand im Winkel (-6/33 … 25/0, 45 m): höchstens halbe Länge minus Rand von ihrer Mitte
        var length = Math.Sqrt((31 * 31) + (33 * 33));
        var (dx, dy) = (31 / length, -33 / length);
        Assert.All(exits, e => Assert.InRange(Math.Abs(((e.EastM - 509.5) * dx) + ((e.NorthM - 316.5) * dy)), 0, (length / 2) - FireExits.WallEndMarginM + 0.01));
    }

    [Fact]
    public void Gebaeude_die_ueber_den_Rollwegen_liegen_passen_nicht_zur_Szenerie()
    {
        // Eine Halle neben dem Netz passt; ein „Gebäude“ über Rollweg A (andere Szenerie) liegt auf den Rollwegen
        var graph = Graph();
        graph.SetBuildings([new List<(double East, double North)> { (500, 270), (540, 270), (540, 290), (500, 290), (500, 270) }]);
        var (parkings, taxi) = graph.BuildingOverlap();
        Assert.Equal(0, parkings);
        Assert.Equal(0, taxi);

        graph.SetBuildings([new List<(double East, double North)> { (-900, 180), (-100, 180), (-100, 220), (-900, 220), (-900, 180) }]);
        Assert.True(graph.BuildingOverlap().TaxiShare > 0.02);
    }

    [Fact]
    public void Plaetze_am_Gate_ohne_Gebaeude_und_ohne_Doppelbelegung()
    {
        // Alle Plätze am Gate liegen mindestens 10 m auseinander
        const double u = 30;
        var units = FireCategory.Lineup(FireAlertLevel.FullEmergency, 9);
        var posts = units.Select(unit => FireOperation.StandPost(unit, u)).ToList();
        for (var a = 0; a < posts.Count; a++)
        {
            for (var b = a + 1; b < posts.Count; b++)
            {
                Assert.True(Distance(posts[a].X, posts[a].Y, posts[b].X, posts[b].Y) >= 10, units[a].Name + " und " + units[b].Name);
            }
        }

        // Ausweichplätze: der geplante zuerst, am Gate nie vor die Nase links
        var candidates = FireOperation.PostCandidates(FireOperation.StandPost(units[0], u), u, atStand: true).ToList();
        Assert.Equal(FireOperation.StandPost(units[0], u), candidates[0]);
        Assert.DoesNotContain(candidates, c => c.X > 0 && c.Y < 0);
    }

    [Fact]
    public void Betriebsstrassen_werden_bevorzugt()
    {
        // Von A bei x = 200 zum Stück führt nur der Fahrweg (164 m): mit Faktor 0,7 kostet er 30 % weniger als ein Rollweg
        var graph = Graph(stub: true, roads: true);
        var stub = graph.Nodes.Count(n => !n.IsParking) - 2;
        var route = graph.Route(6, stub)!;
        var full = graph.CostM(route);
        graph.RoadCostFactor = 0.7;
        Assert.InRange(graph.CostM(route), full * 0.7, full * 0.99);
    }

    [Fact]
    public void Betriebsstrassen_aus_OSM_nur_auf_der_Luftseite()
    {
        // Luftseite: Straße 100 m nördlich von Rollweg A (y = 300) mit Enden 15 m neben A bei x = ±600. Zaun bei y = 400, Flughafen bis x = 1100.
        var graph = Graph();
        graph.RoadCostFactor = 0.7;
        List<(double East, double North)> airside = [(-600, 215), (-600, 300), (600, 300), (600, 215)];
        List<(double East, double North)> overFence = [(-600, 300), (-600, 450)];            // über den Zaun
        List<(double East, double North)> landside = [(-300, 450), (300, 450)];              // hinter dem Zaun, ohne Verbindung
        List<(double East, double North)> outside = [(1000, 215), (1300, 215)];             // Mitte außerhalb des Flughafens
        List<(double East, double North)> gated = [(-400, 215), (-400, 260), (-400, 290)];   // Schranke bei -400/260
        List<(double East, double North)> fence = [(-1100, 400), (1100, 400)];
        List<(double East, double North)> boundary = [(-1100, -100), (1100, -100), (1100, 500), (-1100, 500), (-1100, -100)];

        var merge = graph.AddRoads([airside, overFence, landside, outside, gated], [(-400, 260)], 0.08, [fence], [boundary]);

        Assert.True(merge.Added);
        Assert.Equal(3, merge.Sections);
        Assert.Equal(2, merge.Links);
        Assert.Equal(0, merge.Crossings);
        Assert.Equal(4, merge.Cut);          // Zaun, außerhalb, zweimal an der Schranke
        Assert.Equal(1, merge.Dropped);      // Landseite ohne Verbindung
        Assert.Equal(3, graph.AddedRoads);

        // Von A bei x = -600 nach x = 600 geht es über die Straße (Faktor 0,7) statt über den Rollweg
        var from = graph.Nodes.Single(n => !n.IsParking && Distance(n.EastM, n.NorthM, -600, 200) < 1).Id;
        var to = graph.Nodes.Single(n => !n.IsParking && Distance(n.EastM, n.NorthM, 600, 200) < 1).Id;
        var route = graph.Route(from, to)!;
        Assert.Contains(route, id => Math.Abs(graph.Nodes[id].NorthM - 300) < 1);
        Assert.DoesNotContain(route, id => graph.Nodes[id].NorthM > 350);
    }

    [Fact]
    public void Betriebsstrassen_aus_OSM_kreuzen_Rollwege_und_haengen_daneben_am_Netz()
    {
        // Eine Straße quer über Rollweg A bei x = -500, eine zweite 15 m daneben von x = -350 bis -250; beide Enden der ersten 80 m weg
        var graph = Graph();
        int At(double east, double north) => graph.Nodes.Single(n => !n.IsParking && Distance(n.EastM, n.NorthM, east, north) < 0.5).Id;
        var (a600, a400, a200) = (At(-600, 200), At(-400, 200), At(-200, 200));
        Assert.NotNull(graph.Edge(a600, a400));
        List<(double East, double North)> crossing = [(-500, 120), (-500, 280)];
        List<(double East, double North)> beside = [(-350, 215), (-250, 215)];

        var merge = graph.AddRoads([crossing, beside], [], 0.08);

        Assert.True(merge.Added);
        Assert.Equal(1, merge.Crossings);
        Assert.Equal(3, merge.Links);
        Assert.Equal(3, merge.Sections);

        // A ist an der Kreuzung geteilt: ein Punkt bei -500/200 mit beiden Nachbarn auf A und der Straße nach Süden und Norden
        var junction = At(-500, 200);
        Assert.Equal(4, graph.EdgesOf(junction).Count);
        Assert.Null(graph.Edge(a600, a400));

        // Neben A: zweimal geteilt (bei -350 und -250), dort je eine Verbindung zur Straße
        Assert.Null(graph.Edge(a400, a200));
        Assert.Equal(3, graph.EdgesOf(At(-350, 200)).Count);
        Assert.Equal(3, graph.EdgesOf(At(-250, 200)).Count);

        // Vom Rollweg über die Kreuzung ans Straßenende
        Assert.NotNull(graph.Route(a600, At(-500, 280)));
    }

    [Fact]
    public void Betriebsstrassen_aus_OSM_die_nicht_zur_Szenerie_passen_bleiben_weg()
    {
        // Straße mitten über Gate A5 (-200/330): zu viel davon auf der Parkposition, nichts kommt dazu
        var graph = Graph();
        var nodes = graph.Nodes.Count;
        var merge = graph.AddRoads([[(-200, 215), (-200, 340)]], [], 0.05);

        Assert.False(merge.Added);
        Assert.True(merge.ParkingShare > 0.05);
        Assert.Equal(nodes, graph.Nodes.Count);
        Assert.Equal(0, graph.AddedRoads);
    }

    [Fact]
    public void Overpass_mit_Strassen_Schranken_Zaeunen_und_Flughafen_Umriss()
    {
        // Eine Betriebsstraße, dazu Parkplatzgasse und Tunnel (bleiben weg), eine Schranke, ein Zaun und der Flughafen als Relation aus
        // zwei Wegen (der zweite andersherum)
        const string json = """
            {"elements":[
              {"type":"way","id":1,"tags":{"highway":"service","access":"private"},"geometry":[{"lat":51.001,"lon":7.000},{"lat":51.001,"lon":7.002}]},
              {"type":"way","id":2,"tags":{"highway":"service","service":"parking_aisle"},"geometry":[{"lat":51.002,"lon":7.000},{"lat":51.002,"lon":7.002}]},
              {"type":"way","id":3,"tags":{"highway":"service","tunnel":"yes"},"geometry":[{"lat":51.003,"lon":7.000},{"lat":51.003,"lon":7.002}]},
              {"type":"node","id":4,"lat":51.001,"lon":7.001,"tags":{"barrier":"lift_gate"}},
              {"type":"way","id":5,"tags":{"barrier":"fence"},"geometry":[{"lat":51.004,"lon":6.990},{"lat":51.004,"lon":7.010}]},
              {"type":"relation","id":6,"tags":{"aeroway":"aerodrome","icao":"EDXF","type":"multipolygon"},"members":[
                {"type":"way","ref":7,"role":"outer","geometry":[{"lat":50.990,"lon":6.990},{"lat":50.990,"lon":7.010},{"lat":51.010,"lon":7.010}]},
                {"type":"way","ref":8,"role":"outer","geometry":[{"lat":50.990,"lon":6.990},{"lat":51.010,"lon":6.990},{"lat":51.010,"lon":7.010}]}
              ]}
            ]}
            """;
        var data = FireStationLookup.ParseAll(json);

        Assert.Empty(data.Stations);
        Assert.Empty(data.Buildings);
        Assert.Single(data.Roads);
        Assert.Single(data.Barriers);
        Assert.Single(data.Fences);
        var ring = Assert.Single(data.Boundary);
        Assert.True(ring.Closed);
        Assert.Equal(5, ring.Points.Count);
        Assert.Contains("highway", FireStationLookup.Query("eddl"), StringComparison.Ordinal);
    }
}
