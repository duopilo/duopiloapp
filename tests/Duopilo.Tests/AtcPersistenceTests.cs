using Duopilo.Atc;
using Duopilo.FlightPlan;

namespace Duopilo.Tests;

/// <summary>
/// Testlog 0.9: Nach dem Absturz von Duopilo war die ATC-Sitzung weg. Jetzt wird sie gesichert und im selben Flug
/// fortgesetzt – Station, Phase, freigegebene Höhe, Werte von Hand, Stimmen und Funkverlauf.
/// </summary>
public sealed partial class AtcTests
{
    /// <summary>Wie nach einem Neustart: Sicherung als JSON hin und zurück, neue Vorlagen, neuer Plan, neue Sitzung.</summary>
    private static (AtcSession Session, string? Note) RestartWith(AtcSession old, Ofp ofp, AtcInput input)
    {
        var json = AtcSessionStore.Serialize(old.Snapshot(input.TimeUtc));
        var snapshot = AtcSessionStore.Deserialize(json)!;
        var restored = Session(ofp);
        var note = restored.Restore(snapshot, input);
        return (restored, note);
    }

    /// <summary>Kurzstrecke bis Langen Radar, Steigflug auf FL350 freigegeben und zurückgelesen.</summary>
    private static (AtcSession Session, Flight Flight) ToLangenRadar(bool readBack = true)
    {
        var session = Session(ShortHaul());
        session.SetOverride("gate", "A26");
        var flight = new Flight(session);
        flight.Start();
        var squawk = AtcSpeech.Spell(session.Squawk);
        flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin");
        flight.Say($"cleared to Berlin, MARUN seven golf, climb five thousand feet, squawk {squawk}");
        flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, gate alpha two six, request push and start");
        flight.Say("push and start approved, Lufthansa four alpha bravo");
        flight.Say("Frankfurt Ground, Lufthansa four alpha bravo, request taxi");
        flight.Say("taxi holding point runway two five center, Lufthansa four alpha bravo");
        flight.Run(60, s => s with { GroundSpeedKt = 15, ParkingBrake = false });
        flight.Run(10, s => s with { GroundSpeedKt = 0 });
        flight.Say("Frankfurt Tower, Lufthansa four alpha bravo, holding point runway two five center, ready for departure");
        flight.Say("line up and wait runway two five center, Lufthansa four alpha bravo");
        flight.Run(10, s => s with { GroundSpeedKt = 6 });
        flight.Run(5, s => s with { GroundSpeedKt = 0 });
        flight.Say("cleared for takeoff runway two five center, Lufthansa four alpha bravo");
        flight.Run(20, s => s with { GroundSpeedKt = 150, IasKt = 150 });
        flight.Run(2, s => s with { OnGround = false, AglFt = 800, AltitudeFt = 1200 });
        flight.Run(2, s => s with { AglFt = 1600, AltitudeFt = 2000 });
        flight.Say("Frankfurt Departure, Lufthansa four alpha bravo, passing two thousand, climbing five thousand feet");
        flight.Say("climb flight level one five zero, Lufthansa four alpha bravo");
        flight.FlyTo(0.15, 8000, gs: 300, ias: 250, vs: 2000);
        flight.Say("direct ERNAS, Lufthansa four alpha bravo");
        flight.FlyTo(0.2, 13500, gs: 380, ias: 280, vs: 2000);
        Assert.Equal("DLH4AB, climb FL350",
            Assert.Single(flight.Say("Langen Radar, Lufthansa four alpha bravo, passing flight level one three five, climbing flight level one five zero")).Text);
        if (readBack)
        {
            Assert.Empty(flight.Say("climb flight level three five zero, Lufthansa four alpha bravo"));
            Assert.Equal(AtcStep.CenterCruise, session.Step);
        }
        else
        {
            Assert.Equal(AtcStep.CenterReadback, session.Step);
        }

        return (session, flight);
    }

    [Fact]
    public void Sitzung_wird_nach_einem_Neustart_fortgesetzt()
    {
        var (old, flight) = ToLangenRadar();
        var (session, note) = RestartWith(old, ShortHaul(), flight.Input);

        Assert.Null(note);
        Assert.Equal(AtcPhase.Center, session.Phase);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        Assert.Equal("Langen Radar", session.Station.Name);
        Assert.Equal(old.Station.Voice, session.Station.Voice);
        Assert.Equal(35000, session.ClearedAltitudeFt);
        Assert.Equal(old.Squawk, session.Squawk);
        Assert.Equal("A26", session.Overrides["gate"]);
        Assert.Equal(old.Transcript.Select(t => t.Text), session.Transcript.Select(t => t.Text));
        Assert.Equal(old.Voices, session.Voices);
        Assert.Equal(old.AlongTrackNm!.Value, session.AlongTrackNm!.Value, 1);

        // Say again kennt den letzten Funkspruch noch
        var next = new Flight(session) { State = flight.State };
        Assert.Equal("DLH4AB, climb FL350", Assert.Single(next.Say("Say again, Lufthansa four alpha bravo")).Text);

        // Der Flug geht weiter: FIR-Wechsel Langen → Bremen wie ohne Neustart, die Funknummern laufen weiter
        Assert.Empty(next.FlyTo(0.4, 35000));
        var toBremen = Assert.Single(next.FlyTo(0.55, 35000));
        Assert.Equal("DLH4AB, contact Bremen Radar 132.6", toBremen.Text);
        Assert.True(toBremen.Id > old.Transcript[^1].Id);
        Assert.Equal("DLH4AB, identified, maintain FL350",
            Assert.Single(next.Say("Bremen Radar, Lufthansa four alpha bravo, maintaining flight level three five zero")).Text);
    }

    [Fact]
    public void Offenes_Readback_gilt_nach_dem_Neustart_als_erledigt()
    {
        var (old, flight) = ToLangenRadar(readBack: false);
        Assert.NotNull(old.PendingReadback);

        var (session, note) = RestartWith(old, ShortHaul(), flight.Input);

        Assert.Null(session.PendingReadback);
        Assert.Equal(AtcStep.CenterCruise, session.Step);
        Assert.Equal(35000, session.ClearedAltitudeFt);
        Assert.NotNull(note);
        Assert.Contains("CenterReadback → CenterCruise", note);
    }

    [Theory]
    [InlineData(AtcStep.ClearanceReadback, AtcStep.ClearanceRequest)]
    [InlineData(AtcStep.TaxiReadback, AtcStep.Taxiing)]
    [InlineData(AtcStep.LineUpReadback, AtcStep.LinedUp)]
    [InlineData(AtcStep.TakeoffReadback, AtcStep.TakeoffRoll)]
    [InlineData(AtcStep.DepartureReadback, AtcStep.DepartureClimb)]
    [InlineData(AtcStep.DirectReadback, AtcStep.DepartureClimb)]
    [InlineData(AtcStep.CenterReadback, AtcStep.CenterCruise)]
    [InlineData(AtcStep.ApproachReadback, AtcStep.ApproachVectoring)]
    [InlineData(AtcStep.ApproachClearanceReadback, AtcStep.ApproachEstablished)]
    [InlineData(AtcStep.LandingReadback, AtcStep.Landing)]
    [InlineData(AtcStep.GateReadback, AtcStep.TaxiIn)]
    [InlineData(AtcStep.OceanicReports, AtcStep.OceanicReports)]
    public void Readback_Schritte_nach_dem_Neustart(AtcStep saved, AtcStep restored) =>
        Assert.Equal(restored, AtcSession.StepAfterReadback(saved));

    [Fact]
    public void Oceanic_Freigabe_ueberlebt_den_Neustart()
    {
        var ofp = Transatlantic();
        var session = Session(ofp);
        var plan = session.Plan;

        // Position auf der Route bei einer Streckenposition (nm)
        AtcInput At(double nm, int second)
        {
            var points = plan.Points;
            var (lat, lon) = (points[^1].Lat, points[^1].Lon);
            for (var i = 1; i < points.Count; i++)
            {
                if (plan.PointDistanceNm[i] >= nm)
                {
                    var f = (nm - plan.PointDistanceNm[i - 1]) / (plan.PointDistanceNm[i] - plan.PointDistanceNm[i - 1]);
                    (lat, lon) = (points[i - 1].Lat + ((points[i].Lat - points[i - 1].Lat) * f), points[i - 1].Lon + ((points[i].Lon - points[i - 1].Lon) * f));
                    break;
                }
            }

            return new AtcInput
            {
                TimeUtc = T0.AddSeconds(second),
                OnGround = false,
                Lat = lat,
                Lon = lon,
                AltitudeFt = 35000,
                AglFt = 34500,
                GroundSpeedKt = 450,
                IasKt = 280,
                ZuluTimeSec = (12 * 3600) + second,
            };
        }

        var resno = plan.FixDistanceNm(3)!.Value;
        session.Start(At(resno - 400, 0));
        session.Tick(At(resno - 200, 1));
        Assert.Equal(OceanicClearanceState.Due, session.Oceanic);
        session.PilotTransmission(
            "Shanwick, Lufthansa four zero zero, request oceanic clearance, estimating RESNO at one two three zero, requesting flight level three seven zero, speed two eight zero knots",
            At(resno - 200, 2));
        Assert.Equal(OceanicClearanceState.ReadbackPending, session.Oceanic);

        // Ohne den Rückruf des Readbacks: wieder "Due" – der Pilot fordert neu an, Duopilo sagt ihm wie
        var (pending, note) = RestartWith(session, ofp, At(resno - 199, 3));
        Assert.Equal(OceanicClearanceState.Due, pending.Oceanic);
        Assert.Null(pending.PendingOceanicReadback);
        Assert.NotNull(note);
        Assert.StartsWith("Shanwick Oceanic, DLH400, request oceanic clearance", pending.ExpectedPilotCalls(At(resno - 199, 3)).First().Text);

        // Erteilt bleibt erteilt
        var cleared = session.Snapshot(T0) with { Oceanic = OceanicClearanceState.Cleared, PendingReadback = null };
        var restored = Session(ofp);
        restored.Restore(AtcSessionStore.Deserialize(AtcSessionStore.Serialize(cleared))!, At(resno - 199, 3));
        Assert.Equal(OceanicClearanceState.Cleared, restored.Oceanic);
    }

    [Fact]
    public void Sicherungsdatei_schreiben_lesen_loeschen()
    {
        var root = Directory.CreateTempSubdirectory("duopilo-atc-").FullName;
        try
        {
            var (session, flight) = ToLangenRadar();
            var store = new AtcSessionStore(Path.Combine(root, "atc-session.json"));
            Assert.Null(store.Load());

            Assert.True(store.Save(session.Snapshot(flight.Now)));
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("fra-ber-1", loaded!.FlightKey);
            Assert.Equal(AtcPhase.Center, loaded.Phase);
            Assert.Equal("FIR/EDGG", loaded.Station!.Key);
            Assert.Equal(session.Transcript.Count, loaded.Transcript.Count);
            Assert.Contains("\"phase\": \"Center\"", File.ReadAllText(store.FilePath!));
            Assert.False(File.Exists(store.FilePath + ".tmp"));

            // Andere Version oder kaputte Datei: nicht wiederherstellen
            File.WriteAllText(store.FilePath!, AtcSessionStore.Serialize(loaded with { Version = 99 }));
            Assert.Null(store.Load());
            File.WriteAllText(store.FilePath!, "{ kaputt");
            Assert.Null(store.Load());
            Assert.NotNull(store.LastError);

            store.Delete();
            Assert.False(File.Exists(store.FilePath));
            Assert.False(new AtcSessionStore(null).Save(loaded));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fingerabdruck_aendert_sich_nur_bei_echten_Aenderungen()
    {
        var (session, flight) = ToLangenRadar();
        var before = session.StateFingerprint();

        flight.Run(3);
        Assert.Equal(before, session.StateFingerprint());

        session.SetOverride("gate", "B20");
        Assert.NotEqual(before, session.StateFingerprint());
    }
}
