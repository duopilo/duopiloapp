using Duopilo.FlightPlan;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>Übergänge zwischen den Phasen: Anflug aus dem Reiseflug auf den Final (A4) und Abflug bis in den Reiseflug (B3).</summary>
public sealed class EnrouteTests
{
    // Bahn ähnlich EDDL 05R/23L
    private static readonly AirportRunway Runway = new(51.2895, 6.7668, 45, 47, 3000, 45, "05R", "23L");

    private const double Nm = RunwayEnd.MetersPerNm;

    private static RunwayEnd End(string designation) => RunwayEnd.Find("EDDL", [Runway], designation)!;

    [Fact]
    public void Weg_auf_den_Final_von_hinten_geradeaus()
    {
        var path = ArrivalPath.Build(-40 * Nm, 0, -10 * Nm, 3000);

        Assert.Equal(0, path.SweepDeg, 1);
        Assert.Equal(30 * Nm, path.LengthM, 0);
        var end = path.At(path.LengthM);
        Assert.Equal(-10 * Nm, end.AlongM, 0);
        Assert.Equal(0, end.CrossM, 1);
        Assert.Equal(0, end.CurvaturePerM);
    }

    [Theory]
    [InlineData(-30, 20, true)]
    [InlineData(-30, -20, false)]
    [InlineData(-15, 25, true)]
    [InlineData(5, 30, true)]
    [InlineData(30, 1, true)]
    [InlineData(30, -1, false)]
    public void Weg_auf_den_Final_ohne_Sprung_und_genau_im_Landekurs(double alongNm, double crossNm, bool right)
    {
        var path = ArrivalPath.Build(alongNm * Nm, crossNm * Nm, -10 * Nm, 3000);
        Assert.Equal(right, path.RightTurn);
        Assert.InRange(path.SweepDeg, 0, 359.5);

        // Gleichmäßig entlang: jeder Schritt von 50 m Weg liegt 50 m weiter, ohne Knick im Kurs
        var previous = path.At(0);
        for (var s = 50.0; s <= path.LengthM; s += 50)
        {
            var next = path.At(s);
            var step = Math.Sqrt(Math.Pow(next.AlongM - previous.AlongM, 2) + Math.Pow(next.CrossM - previous.CrossM, 2));
            Assert.InRange(step, 49, 50.5);
            var turn = TrafficMath.Normalize(next.HeadingDeg - previous.HeadingDeg);
            Assert.True(turn < 1.5 || turn > 358.5, $"heading jumps by {turn:0.0}° at {s:0} m");
            previous = next;
        }

        var end = path.At(path.LengthM);
        Assert.Equal(-10 * Nm, end.AlongM, 0);
        Assert.Equal(0, end.CrossM, 0);
        Assert.True(end.HeadingDeg < 0.5 || end.HeadingDeg > 359.5);
    }

    [Fact]
    public void Weg_von_hinter_dem_Flughafen_dreht_rund_180_Grad()
    {
        var path = ArrivalPath.Build(30 * Nm, 1 * Nm, -10 * Nm, 3000);
        Assert.InRange(path.SweepDeg, 150, 210);

        // Von der Seite: etwa 90° plus der Winkel, mit dem die Gerade auf den Kreis zuläuft
        Assert.InRange(ArrivalPath.Build(-10 * Nm, 30 * Nm, -10 * Nm, 3000).SweepDeg, 85, 120);

        // Zu nah am Kreis geht nicht
        Assert.Throws<ArgumentException>(() => ArrivalPath.Build(-10 * Nm, 3000, -10 * Nm, 3000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(320.0)]
    [InlineData(150.0)]
    [InlineData(47.0)]
    public void Anflug_aus_dem_Reiseflug_kommt_am_Eindrehpunkt_auf_dem_Gleitpfad_an(double? fromDeg)
    {
        var end = End("23L");
        var plan = TrafficTestPlan.BuildArrival(end, TrafficCategory.Airliner, approachKt: 135, fromDeg: fromDeg, spawnNm: 40);
        var leg = plan.Arrival!;
        Assert.Equal(TrafficTestVariant.A4, plan.Variant);
        Assert.True(plan.TaxiAfterLanding);
        Assert.False(plan.GearAtSpawn);
        Assert.Null(plan.GearDownFt);
        Assert.Equal(40, RouteMath.DistanceNm(end.ThresholdLatitude, end.ThresholdLongitude, plan.Spawn.Latitude, plan.Spawn.Longitude), 1);
        Assert.InRange(plan.Spawn.AltitudeFt, end.ElevationFt + 3000, leg.CruiseFt);

        var kin = EnrouteKinematics.Arrival(end, leg.Path, leg.Profile, leg.CruiseFt, leg.CruiseTasKt, leg.SpawnHeightFt, leg.SpawnKt, end.ElevationFt, 9, 5.5, 140);
        var maxAltitude = kin.AltitudeFt;
        var minVs = 0.0;
        var below10000Kt = 0.0;
        for (var i = 0; i < 30 * 60 * 30 && !kin.Joined; i++)
        {
            kin.Advance(1.0 / 30);
            maxAltitude = Math.Max(maxAltitude, kin.AltitudeFt);
            minVs = Math.Min(minVs, kin.VerticalSpeedFpm);
            if (kin.AltitudeFt < EnrouteProfile.SpeedLimitFt - 500)
            {
                below10000Kt = Math.Max(below10000Kt, kin.SpeedKt);
            }
        }

        Assert.True(kin.Joined);
        Assert.Equal(-10 * Nm, kin.AlongM, 0);
        Assert.Equal(0, kin.CrossM, 0);
        Assert.InRange(kin.HeightFt - TrafficKinematics.GlidePathHeightFt(kin.AlongM), -150, 150);
        Assert.Equal(210, kin.SpeedKt, 0);
        Assert.InRange(kin.MaxBankDeg, 0, 30);

        // Am Eindrehpunkt wieder gerade – der Endanflug fliegt ohne Querneigung weiter
        Assert.InRange(kin.BankRightDeg, -0.5, 0.5);
        Assert.True(maxAltitude <= leg.CruiseFt + 50);
        Assert.True(minVs > -4000);
        Assert.InRange(below10000Kt, 0, 251);

        // Ab hier der Endanflug, mit Fahrwerk bei 3000 ft
        var final = plan.FinalAfterJoin();
        Assert.Null(final.Arrival);
        Assert.Equal(TrafficTestPlan.GearCommandFt, final.GearDownFt);
        Assert.Equal(TrafficTestVariant.A4, final.Variant);
    }

    [Fact]
    public void Endanflug_nach_dem_Eindrehen_erst_Vapp_plus_25_ab_4_NM_Vapp_plus_5()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.BuildArrival(end, TrafficCategory.Airliner, approachKt: 135).FinalAfterJoin();
        var along = -10 * Nm;
        var kin = new TrafficKinematics(plan, KinematicProfile.Airliner, along, 0, TrafficKinematics.GlidePathHeightFt(along), 210, -0.5, end.ElevationFt, 9);
        double? at6 = null;
        double? at2 = null;
        for (var i = 0; i < 30 * 600 && kin.Touchdown is null; i++)
        {
            kin.Advance(1.0 / 30);
            if (at6 is null && kin.AlongM >= -6 * Nm)
            {
                at6 = kin.SpeedKt;
            }

            if (at2 is null && kin.AlongM >= -2 * Nm)
            {
                at2 = kin.SpeedKt;
            }
        }

        Assert.Equal(160, at6!.Value, 0);
        Assert.Equal(140, at2!.Value, 0);
        Assert.NotNull(kin.Touchdown);
    }

    [Fact]
    public void Anflug_aus_dem_Reiseflug_spawnt_auf_dem_Profil_hoechstens_auf_der_Flugflaeche()
    {
        var end = End("23L");

        // 75 NM von der Seite: oben gedeckelt
        var high = TrafficTestPlan.BuildArrival(end, TrafficCategory.Airliner, fromDeg: 140, spawnNm: 75, flightLevel: 180);
        Assert.Equal(18000, high.Spawn.AltitudeFt, 0);
        Assert.Equal(18000, high.Arrival!.CruiseFt);

        // 20 NM geradeaus: auf dem 3°-Profil (rund 318 ft/NM über 20 NM Weg)
        var low = TrafficTestPlan.BuildArrival(end, TrafficCategory.Airliner, spawnNm: 20);
        Assert.InRange(low.Spawn.AltitudeFt - end.ElevationFt, 6200, 6500);
        Assert.Equal(20, low.SpawnNm, 0);

        // Allgemeine Luftfahrt: langsamer, kleiner Kreis, Eindrehen bei 5 NM
        var ga = TrafficTestPlan.BuildArrival(end, TrafficCategory.GeneralAviation, approachKt: 65, fromDeg: 140, spawnNm: 25);
        Assert.Equal(5, ga.Arrival!.Profile.JoinNm);
        Assert.Equal(90, ga.Arrival.Profile.InterceptKt);
        Assert.InRange(ga.Arrival.Path.RadiusM, 300, 1200);
        Assert.Equal(8500, ga.Arrival.CruiseFt);
    }

    [Fact]
    public void Abflug_dreht_auf_den_Kurs_und_steigt_in_den_Reiseflug()
    {
        var end = End("05R");
        var leg = TrafficTestPlan.DepartureLegFor(TrafficCategory.Airliner, approachKt: 135, trackDeg: 137, flightLevel: 240, endNm: 200);
        Assert.Equal(TrafficTestPlan.MaxOverflightNm, leg.EndNm);
        Assert.Equal(24000, leg.CruiseFt);

        var kin = EnrouteKinematics.Departure(end, leg.Profile, leg.TrackDeg, leg.CruiseFt, leg.CruiseTasKt, 3500, 0, 1500, 175, 1500, 12, end.ElevationFt, 9, 5.5, 140);
        var maxBelow10000 = 0.0;
        var maxPitch = kin.PitchDeg;
        for (var i = 0; i < 30 * 60 * 25; i++)
        {
            kin.Advance(1.0 / 30);
            if (kin.AltitudeFt < EnrouteProfile.SpeedLimitFt)
            {
                maxBelow10000 = Math.Max(maxBelow10000, kin.SpeedKt);
            }

            maxPitch = Math.Max(maxPitch, kin.PitchDeg);
        }

        Assert.Equal(137, kin.HeadingTrueDeg, 0);
        Assert.InRange(kin.MaxBankDeg, 10, 25.5);
        Assert.InRange(maxBelow10000, 249, 250.5);
        Assert.InRange(kin.AltitudeFt, 23950, 24050);
        Assert.InRange(kin.TrueAirspeedKt, 445, 455);
        Assert.Equal("cruise", kin.Phase);
        Assert.InRange(maxPitch, 5, 15);
        Assert.True(kin.DistanceFromRunwayNm > 75);

        // Querneigung für den Sim links positiv: eine Rechtskurve (Kurs 47 → 137) setzt sie negativ
        var turning = EnrouteKinematics.Departure(end, leg.Profile, leg.TrackDeg, leg.CruiseFt, leg.CruiseTasKt, 3500, 0, 1500, 175, 1500, 12, end.ElevationFt, 9, 5.5, 140);
        for (var i = 0; i < 30 * 10; i++)
        {
            turning.Advance(1.0 / 30);
        }

        Assert.True(turning.BankRightDeg > 10);
        Assert.True(turning.Pose().BankDeg < -10);
    }

    [Fact]
    public void Abflug_ohne_Kurs_geradeaus_und_B3_rollt_wie_B2()
    {
        var end = End("23L");
        var leg = TrafficTestPlan.DepartureLegFor(TrafficCategory.GeneralAviation, approachKt: 65);
        Assert.Null(leg.TrackDeg);
        Assert.Equal(8500, leg.CruiseFt);
        Assert.Equal(TrafficTestPlan.DefaultDepartureEndNm, leg.EndNm);
        Assert.Equal(85, leg.Profile.ClimbLowKt);

        var kin = EnrouteKinematics.Departure(end, leg.Profile, leg.TrackDeg, leg.CruiseFt, leg.CruiseTasKt, 2000, 0, 1500, 75, 700, 8, end.ElevationFt, 4, 6, 70);
        for (var i = 0; i < 30 * 60 * 5; i++)
        {
            kin.Advance(1.0 / 30);
        }

        Assert.Equal(end.HeadingTrueDeg, kin.HeadingTrueDeg, 0);
        Assert.Equal(0, kin.CrossM, 0);
        Assert.Equal(0, kin.MaxBankDeg, 1);
        Assert.InRange(kin.VerticalSpeedFpm, 650, 710);

        var b3 = TrafficTestPlan.BuildTakeoff(end) with { Variant = TrafficTestVariant.B3, Departure = leg };
        Assert.True(b3.IsTaxi);
        Assert.True(b3.TakeoffAfterTaxi);
        Assert.False(b3.TaxiAfterLanding);
    }
}
