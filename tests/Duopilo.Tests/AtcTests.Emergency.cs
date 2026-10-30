using Duopilo.Atc;

namespace Duopilo.Tests;

/// <summary>
/// Notrufe (Testflug 30.10.: "Düsseldorf Tower, Mayday, Mayday, Mayday." blieb ohne Behandlung): Erkennung, Vorrang vor allem
/// anderen, ICAO-Phraseologie, Wünsche des Piloten ohne Einschränkung, keine automatischen Übergaben, Squawk 7700, Abbruch.
/// </summary>
public sealed partial class AtcTests
{
    /// <summary>Im Reiseflug bei Langen Radar (FIR EDGG), FL350.</summary>
    private static (AtcSession Session, Flight Pilot) InCenter()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        var (lat, lon) = Along(0.35);
        flight.State = flight.State with
        {
            OnGround = false, Lat = lat, Lon = lon, AltitudeFt = 35000, AglFt = 34500, GroundSpeedKt = 450, IasKt = 280, ParkingBrake = false,
        };
        flight.Start();
        Assert.Equal("Langen Radar", session.Station.Name);
        return (session, flight);
    }

    [Theory]
    [InlineData("Mayday, Mayday, Mayday, Lufthansa four alpha bravo, engine fire", AtcEmergencyCall.Mayday)]
    [InlineData("Düsseldorf Tower, Mayday, Mayday, Mayday.", AtcEmergencyCall.Mayday)]
    [InlineData("MAYDAY", AtcEmergencyCall.Mayday)]
    [InlineData("may day may day, engine failure", AtcEmergencyCall.Mayday)]
    [InlineData("Lufthansa 4AB, Mayday", AtcEmergencyCall.Mayday)]
    [InlineData("Pan-Pan, Pan-Pan, Pan-Pan, Lufthansa four alpha bravo", AtcEmergencyCall.PanPan)]
    [InlineData("pan pan, medical emergency", AtcEmergencyCall.PanPan)]
    [InlineData("Panpan", AtcEmergencyCall.PanPan)]
    [InlineData("Lufthansa four alpha bravo, cancel Mayday", AtcEmergencyCall.Cancel)]
    [InlineData("Cancel distress, Lufthansa four alpha bravo", AtcEmergencyCall.Cancel)]
    [InlineData("cancel pan pan", AtcEmergencyCall.Cancel)]
    [InlineData("Mayday cancelled, Lufthansa four alpha bravo", AtcEmergencyCall.Cancel)]
    [InlineData("Lufthansa four alpha bravo, request descent", AtcEmergencyCall.None)]
    [InlineData("May I have the weather for Japan, panorama view", AtcEmergencyCall.None)]
    [InlineData("Maintaining flight level three five zero", AtcEmergencyCall.None)]
    [InlineData("", AtcEmergencyCall.None)]
    public void Notrufe_werden_erkannt(string text, AtcEmergencyCall expected)
    {
        Assert.Equal(expected, AtcEmergencyText.Detect(text));
    }

    [Fact]
    public void Mayday_bestaetigt_sofort_nach_ICAO()
    {
        var (session, flight) = InCenter();

        var reply = Assert.Single(flight.Say("Mayday, Mayday, Mayday, Lufthansa four alpha bravo, engine fire"));

        Assert.Equal(AtcMessageIds.EmergencyMayday, reply.MessageId);
        Assert.Equal("DLH4AB, Langen Radar, roger Mayday, squawk 7700, say intentions", reply.Text);
        Assert.Contains("squawk seven seven zero zero", reply.Spoken);
        Assert.Equal(AtcEmergencyKind.Mayday, session.Emergency?.Kind);
    }

    [Theory]
    [InlineData("Mayday")]
    [InlineData("Mayday, Mayday, engine fire")]
    [InlineData("Frankfurt Tower, Mayday, Mayday, Mayday, Lufthansa four alpha bravo")]
    public void Mayday_auch_einmal_falsche_oder_keine_Station_vor_der_Pruefung_auf_falsche_Station(string text)
    {
        var (session, flight) = AtDuesseldorf();

        var reply = Assert.Single(flight.Say(text));

        Assert.Equal("DLH4AB, Düsseldorf Delivery, roger Mayday, squawk 7700, say intentions", reply.Text);
        Assert.NotNull(session.Emergency);
    }

    [Fact]
    public void Mayday_hat_Vorrang_vor_offenem_Readback_und_Say_again()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin");
        Assert.NotNull(session.PendingReadback);

        var reply = Assert.Single(flight.Say("Mayday, Mayday, Mayday"));

        Assert.Equal(AtcMessageIds.EmergencyMayday, reply.MessageId);
        Assert.Null(session.PendingReadback);

        // "say again" im Notfall wiederholt die Bestätigung
        Assert.Equal(reply.Text, Assert.Single(flight.Say("Say again")).Text);
    }

    [Fact]
    public void Pan_Pan_ohne_7700_und_Abbruch()
    {
        var (session, flight) = InCenter();
        var squawk = session.Squawk;

        var reply = Assert.Single(flight.Say("Pan-pan, pan-pan, pan-pan, Lufthansa four alpha bravo, sick passenger on board"));
        Assert.Equal("DLH4AB, Langen Radar, roger Pan Pan, say intentions", reply.Text);
        Assert.Equal(AtcEmergencyKind.PanPan, session.Emergency?.Kind);

        var cancel = Assert.Single(flight.Say("Lufthansa four alpha bravo, cancel Pan Pan"));
        Assert.Equal($"DLH4AB, roger, Pan Pan cancelled, squawk {squawk}", cancel.Text);
        Assert.Null(session.Emergency);
    }

    [Fact]
    public void Im_Notfall_Wuensche_ohne_Einschraenkung_dann_Personen_und_Endurance()
    {
        var (session, flight) = InCenter();
        flight.Say("Mayday, Mayday, Mayday, Lufthansa four alpha bravo, engine fire");

        // Absicht: zurück nach Frankfurt – danach einmal "say souls on board and fuel endurance"
        var intentions = flight.Say("Lufthansa four alpha bravo, returning to Frankfurt");
        Assert.Equal(["DLH4AB, cleared direct EDDF", "DLH4AB, say souls on board and fuel endurance"], intentions.Select(t => t.Text));
        Assert.Equal("Frankfurt", intentions[0].Spoken.Split(", cleared direct ")[1]);

        Assert.Equal("DLH4AB, roger", Assert.Single(flight.Say("Lufthansa four alpha bravo, one hundred fifty souls on board, fuel endurance two hours")).Text);

        // Höhe sofort, ohne "unable due traffic"
        var level = Assert.Single(flight.Say("Lufthansa four alpha bravo, request descent flight level one two zero"));
        Assert.Equal("DLH4AB, descend and maintain FL120", level.Text);
        Assert.Equal(12000, session.ClearedAltitudeFt);

        // Landefreigabe für die gewünschte Bahn – von der aktuellen Station, ohne Übergabe
        var landing = Assert.Single(flight.Say("Lufthansa four alpha bravo, request landing runway two five right"));
        Assert.StartsWith("DLH4AB, runway 25R", landing.Text);
        Assert.EndsWith("cleared to land", landing.Text);

        // Nächster geeigneter Flughafen ohne Namen: der nächste des Plans
        var nearest = flight.Say("Lufthansa four alpha bravo, request vectors to the nearest suitable airport");
        Assert.Equal("DLH4AB, cleared direct EDDF", Assert.Single(nearest).Text);
    }

    [Fact]
    public void Im_Notfall_keine_Uebergaben_und_keine_Pruefungen_nach_dem_Abbruch_normal()
    {
        var (session, flight) = InCenter();
        var squawk = session.Squawk;
        flight.Say("Mayday, Mayday, Mayday, Lufthansa four alpha bravo, engine fire");

        // Über die FIR-Grenze nach Bremen, Transponder 7700, COM1 auf 121.5: ATC schweigt
        var (lat, lon) = Along(0.6);
        var quiet = flight.Run(120, s => s with { Lat = lat, Lon = lon, TransponderCode = "7700", Com1ActiveMhz = 121.5 });
        Assert.Empty(quiet);
        Assert.Equal("Langen Radar", session.Station.Name);
        Assert.Null(session.SquawkDeviation);
        Assert.Null(session.FrequencyDeviation);

        var cancel = Assert.Single(flight.Say("Lufthansa four alpha bravo, cancel Mayday"));
        Assert.Equal($"DLH4AB, roger, Mayday cancelled, squawk {squawk}", cancel.Text);
        Assert.Null(session.Emergency);

        // Danach normaler Ablauf: die Übergabe nach Bremen kommt
        var after = flight.Run(2, s => s with { TransponderCode = squawk });
        Assert.Contains(after, t => t.MessageId == AtcMessageIds.CenterHandoff && t.Text.Contains("Bremen Radar", StringComparison.Ordinal));
    }

    [Fact]
    public void Squawk_7700_ohne_Funkspruch_ATC_fragt_einmal_nach()
    {
        var (session, flight) = InCenter();

        var asked = flight.Run(20, s => s with { TransponderCode = "7700" });
        var question = Assert.Single(asked);
        Assert.Equal("DLH4AB, confirm squawking 7700, say nature of emergency", question.Text);
        Assert.Empty(flight.Run(60));

        var reply = Assert.Single(flight.Say("Lufthansa four alpha bravo, affirm, engine failure"));
        Assert.Equal("DLH4AB, roger Mayday, say intentions", reply.Text);
        Assert.Equal(AtcEmergencyKind.Mayday, session.Emergency?.Kind);
    }

    [Fact]
    public void Squawk_7700_aus_Versehen()
    {
        var (session, flight) = InCenter();
        Assert.Single(flight.Run(20, s => s with { TransponderCode = "7700" }));

        var reply = Assert.Single(flight.Say("Negative, wrong code, Lufthansa four alpha bravo"));

        Assert.Equal($"DLH4AB, roger, squawk {session.Squawk}", reply.Text);
        Assert.Null(session.Emergency);
    }

    [Theory]
    [InlineData("7600")]
    [InlineData("7500")]
    public void Squawk_7600_und_7500_noch_ohne_Behandlung_aber_ohne_check_squawk(string code)
    {
        var (session, flight) = InCenter();

        Assert.Empty(flight.Run(120, s => s with { TransponderCode = code }));
        Assert.Null(session.Emergency);
        Assert.Null(session.SquawkDeviation);
    }

    [Fact]
    public void Notfall_bleibt_nach_einem_Neustart()
    {
        var (session, flight) = InCenter();
        flight.Say("Mayday, Mayday, Mayday, Lufthansa four alpha bravo");
        var snapshot = session.Snapshot(T0);

        var restored = Session(ShortHaul());
        restored.Restore(snapshot, flight.Input);

        Assert.Equal(AtcEmergencyKind.Mayday, restored.Emergency?.Kind);
        Assert.Equal(session.Squawk, restored.Emergency?.PreviousSquawk);
    }
}
