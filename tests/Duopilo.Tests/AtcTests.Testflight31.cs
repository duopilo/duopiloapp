using Duopilo.Atc;

namespace Duopilo.Tests;

/// <summary>
/// Testflug 31.10. (EDDL → EDDK, derselbe SimBrief-Plan wie am Vorabend): Am Gate in Düsseldorf kam „Cologne Approach“ aus der
/// Sicherung des Vorabends; ein Radio Check an „Düsseldorf Delivery“ bekam von dort „read you five“, und „Frankfurt Tower,“ –
/// nach einer Pause abgesetzt – erst „this is …, say again“ und der Rest danach doch eine Antwort.
/// </summary>
public sealed partial class AtcTests
{
    // ------------------------------------------------------------------ Sicherung passt nicht zur Lage

    [Fact]
    public void Sicherung_im_Anflug_passt_nicht_wenn_man_wieder_am_Start_steht()
    {
        var (session, flight) = AtDuesseldorf();
        var approach = session.Snapshot(flight.Now) with { Phase = AtcPhase.Approach, Step = AtcStep.None };

        var reason = AtcSession.SnapshotMismatch(approach, session.Plan, flight.Input);

        Assert.NotNull(reason);
        Assert.Contains("EDDL", reason);
        Assert.Contains("Approach", reason);
    }

    [Fact]
    public void Sicherung_am_Boden_am_Start_passt_dort_weiter()
    {
        var (session, flight) = AtDuesseldorf();
        var snapshot = session.Snapshot(flight.Now);
        Assert.Equal(AtcPhase.Delivery, snapshot.Phase);

        Assert.Null(AtcSession.SnapshotMismatch(snapshot, session.Plan, flight.Input));
        Assert.Null(AtcSession.SnapshotMismatch(snapshot with { Phase = AtcPhase.GroundDeparture }, session.Plan, flight.Input));
    }

    [Fact]
    public void In_der_Luft_oder_ohne_Position_entscheidet_die_Lage_nicht()
    {
        var (session, flight) = AtDuesseldorf();
        var approach = session.Snapshot(flight.Now) with { Phase = AtcPhase.Approach };

        Assert.Null(AtcSession.SnapshotMismatch(approach, session.Plan, flight.Input with { OnGround = false, AltitudeFt = 3000 }));
        Assert.Null(AtcSession.SnapshotMismatch(approach, session.Plan, flight.Input with { Lat = null, Lon = null }));
        Assert.Null(AtcSession.SnapshotMismatch(approach, session.Plan, flight.Input with { OnGround = null }));
    }

    [Fact]
    public void Sicherung_noch_am_Start_passt_nicht_wenn_man_schon_am_Ziel_steht()
    {
        var (session, flight) = AtDuesseldorf();
        var delivery = session.Snapshot(flight.Now);

        var reason = AtcSession.SnapshotMismatch(delivery, session.Plan, flight.Input with { Lat = BerLat, Lon = BerLon });

        Assert.NotNull(reason);
        Assert.Contains("Delivery", reason);

        // Am Ziel nach der Landung mit einer Sicherung im Anflug: passt
        Assert.Null(AtcSession.SnapshotMismatch(delivery with { Phase = AtcPhase.TowerArrival }, session.Plan, flight.Input with { Lat = BerLat, Lon = BerLon }));
    }

    // ------------------------------------------------------------------ Radio Check an die falsche Station

    [Fact]
    public void Radio_check_an_eine_andere_Station_bekommt_den_Hinweis()
    {
        var (_, flight) = AtDuesseldorf();

        var wrong = Assert.Single(flight.Say("Frankfurt Tower, Lufthansa four alpha bravo, radio check"));
        Assert.Equal(AtcMessageIds.CommonWrongStation, wrong.MessageId);
        Assert.Equal("DLH4AB, this is Düsseldorf Delivery, say again", wrong.Text);

        var right = Assert.Single(flight.Say("Düsseldorf Delivery, Lufthansa four alpha bravo, radio check"));
        Assert.Equal(AtcMessageIds.CommonRadioCheck, right.MessageId);

        // Ohne Station wie bisher: die aktuelle antwortet
        Assert.Equal(AtcMessageIds.CommonRadioCheck, Assert.Single(flight.Say("Lufthansa four alpha bravo, radio check")).MessageId);
    }

    // ------------------------------------------------------------------ Anrede nach einer Pause getrennt

    [Fact]
    public void Anrede_allein_wartet_auf_den_Rest()
    {
        var (session, flight) = AtDuesseldorf();

        Assert.Empty(flight.Say("Frankfurt Tower,"));
        var replies = flight.Say("Lufthansa four alpha bravo, radio check.");

        var reply = Assert.Single(replies);
        Assert.Equal(AtcMessageIds.CommonWrongStation, reply.MessageId);
        Assert.Contains(session.Transcript, t => t.From == "pilot" && t.Text.StartsWith("Frankfurt Tower, Lufthansa", StringComparison.Ordinal));
    }

    [Fact]
    public void Anrede_der_eigenen_Station_wartet_ebenso_und_gilt_mit_dem_Rest()
    {
        var (session, flight) = AtDuesseldorf();

        Assert.Empty(flight.Say("Düsseldorf Delivery"));
        var reply = Assert.Single(flight.Say("Lufthansa four alpha bravo, radio check"));

        Assert.Equal(AtcMessageIds.CommonRadioCheck, reply.MessageId);
        Assert.Equal(AtcStep.ClearanceRequest, session.Step);
    }

    [Fact]
    public void Kommt_kein_Rest_gilt_die_Anrede_allein()
    {
        var (_, flight) = AtDuesseldorf();

        Assert.Empty(flight.Say("Frankfurt Tower"));
        Assert.DoesNotContain(flight.Run(5), t => t.MessageId == AtcMessageIds.CommonWrongStation);
        var later = flight.Run(3);

        Assert.Single(later, t => t.MessageId == AtcMessageIds.CommonWrongStation);

        // Danach wartet nichts mehr: der nächste Funkspruch steht für sich
        Assert.Equal(AtcMessageIds.CommonRadioCheck, Assert.Single(flight.Say("Lufthansa four alpha bravo, radio check")).MessageId);
    }

    [Fact]
    public void Zu_spaeter_Rest_wird_nicht_angehaengt()
    {
        var (_, flight) = AtDuesseldorf();

        Assert.Empty(flight.Say("Frankfurt Tower"));
        var later = flight.Run(10);
        Assert.Single(later, t => t.MessageId == AtcMessageIds.CommonWrongStation);
        Assert.Equal(AtcMessageIds.CommonRadioCheck, Assert.Single(flight.Say("Lufthansa four alpha bravo, radio check")).MessageId);
    }

    [Theory]
    [InlineData("Lufthansa four alpha bravo")]
    [InlineData("Tower")]
    [InlineData("ILS approach")]
    [InlineData("Final approach")]
    [InlineData("Runway 23 right")]
    public void Keine_reine_Anrede(string text)
    {
        var (session, flight) = AtDuesseldorf();

        flight.Say(text);

        // Sofort verarbeitet (im Funkverlauf), nicht zurückgehalten
        Assert.Contains(session.Transcript, t => t.From == "pilot" && t.Text == text);
    }
}
