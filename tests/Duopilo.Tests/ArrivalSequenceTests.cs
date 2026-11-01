using Duopilo.FlightPlan;
using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Tests;

/// <summary>Takt der Landungen, Lücken für Abflüge und Durchstarten bei belegter Bahn (Mischbetrieb auf einer Bahn).</summary>
public sealed class ArrivalSequenceTests
{
    // Bahn ähnlich EDDL 05R/23L
    private static readonly AirportRunway Runway = new(51.2895, 6.7668, 45, 47, 3000, 45, "05R", "23L");

    private static RunwayEnd End(string designation) => RunwayEnd.Find("EDDL", [Runway], designation)!;

    [Theory]
    [InlineData(TrafficWake.Medium, TrafficWake.Medium, 3)]
    [InlineData(TrafficWake.Heavy, TrafficWake.Medium, 5)]
    [InlineData(TrafficWake.Heavy, TrafficWake.Heavy, 4)]
    [InlineData(TrafficWake.Super, TrafficWake.Medium, 7)]
    [InlineData(TrafficWake.Medium, TrafficWake.Light, 5)]
    [InlineData(TrafficWake.Light, TrafficWake.Heavy, 3)]
    public void Abstand_nach_Wirbelschleppen(TrafficWake leader, TrafficWake follower, double nm) =>
        Assert.Equal(nm, ArrivalSequence.SpacingNm(leader, follower));

    [Fact]
    public void Neuer_Anflug_hinter_dem_letzten_mit_Abstand()
    {
        // Frei: am gewünschten Punkt
        var free = ArrivalSequence.Plan(5, [], TrafficWake.Medium);
        Assert.Equal(5, free.SpawnNm);
        Assert.Null(free.Behind);
        Assert.Equal("free", free.Reason);

        // Hinter dem letzten (dem weitesten), 3 NM bzw. hinter einem Heavy 5 NM
        var behind = ArrivalSequence.Plan(5, [("DUO01", 2.0, TrafficWake.Medium), ("DUO02", 4.2, TrafficWake.Medium)], TrafficWake.Medium);
        Assert.Equal(7.2, behind.SpawnNm);
        Assert.Equal("DUO02", behind.Behind);
        Assert.Equal("wake", behind.Reason);
        Assert.Equal(9.2, ArrivalSequence.Plan(5, [("DUO02", 4.2, TrafficWake.Heavy)], TrafficWake.Medium).SpawnNm);

        // Der vordere ist schon nah an der Schwelle: der gewünschte Punkt reicht
        var near = ArrivalSequence.Plan(5, [("DUO01", 1.0, TrafficWake.Medium)], TrafficWake.Medium);
        Assert.Equal(5, near.SpawnNm);
        Assert.Null(near.Behind);

        // Wartet ein Abflug: Lücke, die zu ihm passt – die Landung ist noch 5 NM weg, wenn er bereit ist und die vordere (bei 4,2 NM)
        // die Bahn geräumt hat (104 s bis zur Schwelle, 60 s auf der Bahn), plus 20 s
        var gap = ArrivalSequence.Plan(5, [("DUO02", 4.2, TrafficWake.Medium)], TrafficWake.Medium, new WaitingDeparture("DUO01", 60, 5.0));
        Assert.Equal(12.4, gap.SpawnNm);
        Assert.Equal("departure_gap", gap.Reason);
        Assert.Equal("DUO01", gap.GapFor);
        Assert.Equal("DUO02", gap.Behind);
        Assert.Equal(8.2, gap.SpacingNm);

        // Später bereit (wie DUO02 in beta.34: 199 s): größere Lücke
        Assert.Equal(13.8, ArrivalSequence.Plan(5, [("DUO02", 4.2, TrafficWake.Medium)], TrafficWake.Medium, new WaitingDeparture("DUO01", 199, 5.0)).SpawnNm);

        // Erst in 10 min bereit: keine Lücke, die Landung kommt zuerst
        var late = ArrivalSequence.Plan(5, [("DUO02", 4.2, TrafficWake.Medium)], TrafficWake.Medium, new WaitingDeparture("DUO01", 600, 5.0));
        Assert.Equal(7.2, late.SpawnNm);
        Assert.Equal("wake", late.Reason);

        // Ohne Landung davor und der Abflug steht schon am Haltepunkt: knapp hinter seiner Freigabe-Entfernung, er startet zuerst
        var first = ArrivalSequence.Plan(5, [], TrafficWake.Medium, new WaitingDeparture("DUO01", 0, 5.0));
        Assert.Equal(5.8, first.SpawnNm);
        Assert.Equal("departure_gap", first.Reason);
        Assert.Null(first.Behind);

        // Rollt gerade ein Abflug auf die Bahn: nicht näher als seine Freigabe-Entfernung
        var departing = ArrivalSequence.Plan(5, [], TrafficWake.Medium, departingReleaseNm: 5.6);
        Assert.Equal(5.6, departing.SpawnNm);
        Assert.Equal("departing", departing.Reason);

        // Höchstens 30 NM
        Assert.Equal(ArrivalSequence.MaxSpawnNm, ArrivalSequence.Plan(5, [("DUO03", 26, TrafficWake.Super)], TrafficWake.Light).SpawnNm);
    }

    [Fact]
    public void Freigabe_Entfernung_fuer_den_Abflug()
    {
        // EDDL K: 240 m Aufreihen, Vr 140 kt mit 2 m/s² – gut 5 NM
        Assert.InRange(ArrivalSequence.DepartureReleaseNm(240, 140, 2.0), 5.3, 5.9);

        // Kürzer aufreihen, kürzere Freigabe – aber nie unter 4 NM, nie über 8 NM
        Assert.True(ArrivalSequence.DepartureReleaseNm(60, 140, 2.0) < ArrivalSequence.DepartureReleaseNm(240, 140, 2.0));
        Assert.Equal(4, ArrivalSequence.DepartureReleaseNm(0, 55, 4.0));
        Assert.Equal(8, ArrivalSequence.DepartureReleaseNm(900, 160, 1.4));
    }

    [Fact]
    public void Bahn_belegt_fuer_die_Landung_nur_auf_der_Bahn_und_beim_Aufreihen_weiter_draussen()
    {
        var end23 = End("23L");
        AircraftSample At(uint id, double alongM, double heightFt, bool onGround)
        {
            var (lat, lon) = end23.Point(alongM);
            return new(id, "aircraft", "DUO0" + id, null, null, null, lat, lon, end23.ElevationFt + heightFt, end23.HeadingTrueDeg, end23.HeadingTrueDeg, 140, 0, onGround, false);
        }

        var onRunway = At(2, 1500, 0, onGround: true);
        var nextLanding = At(3, -5.5 * RunwayEnd.MetersPerNm, TrafficKinematics.GlidePathHeightFt(-5.5 * RunwayEnd.MetersPerNm), onGround: false);

        // Landung: nur wer auf der Bahn ist, nicht die nächste Landung dahinter
        Assert.Equal("DUO02 on the runway", RunwayOccupancy.OnRunway("EDDL", Runway, [onRunway], 1));
        Assert.Null(RunwayOccupancy.OnRunway("EDDL", Runway, [nextLanding], 1));

        // Kreuzen (4 NM): die Landung bei 5,5 NM zählt nicht; Aufreihen mit 6 NM: zählt (auch 1800 ft hoch, dort liegt der Pfad)
        Assert.Null(RunwayOccupancy.Blocker("EDDL", Runway, [nextLanding], 1));
        Assert.Equal("DUO03 on final 23L 5.5 NM", RunwayOccupancy.Blocker("EDDL", Runway, [nextLanding], 1, finalNm: 6));
    }

    [Fact]
    public void Kinematik_startet_bei_belegter_Bahn_durch_und_steigt_auf_3000_ft()
    {
        var end = End("23L");
        var plan = TrafficTestPlan.Build(end, TrafficTestVariant.A3, approachKt: 135);
        var along = -2 * RunwayEnd.MetersPerNm;
        var kin = new TrafficKinematics(plan, KinematicProfile.Airliner, along, 0, TrafficKinematics.GlidePathHeightFt(along), 140, 2.5, end.ElevationFt, 9);

        // Bis 0,4 NM anfliegen, dann durchstarten
        while (kin.AlongM < -ArrivalSequence.GoAroundDecisionNm * RunwayEnd.MetersPerNm)
        {
            kin.Advance(1.0 / 30);
        }

        var goAroundFt = kin.HeightFt;
        Assert.True(kin.GoAround());
        Assert.Equal(KinematicPhase.Climb, kin.Phase);
        Assert.NotNull(kin.GoAroundAlongM);
        Assert.False(kin.IsTakeoff);
        Assert.Equal(145, kin.ClimbKt, 0);

        // Erst nicht weiter sinken (höchstens ein paar Fuß), dann steigen – ohne Aufsetzen, bis 3000 ft
        var lowest = kin.HeightFt;
        for (var i = 0; i < 30 * 240; i++)
        {
            kin.Advance(1.0 / 30);
            lowest = Math.Min(lowest, kin.HeightFt);
        }

        Assert.InRange(goAroundFt - lowest, 0, 5);
        Assert.Null(kin.Touchdown);
        Assert.InRange(kin.HeightFt, 2850, 3100);
        Assert.InRange(kin.PitchDeg, 0, 18);

        // Nach dem Aufsetzen geht es nicht mehr
        var landed = new TrafficKinematics(plan, KinematicProfile.Airliner, along, 0, TrafficKinematics.GlidePathHeightFt(along), 140, 2.5, end.ElevationFt, 9);
        for (var i = 0; i < 30 * 120 && landed.Touchdown is null; i++)
        {
            landed.Advance(1.0 / 30);
        }

        Assert.False(landed.GoAround());
    }
}
