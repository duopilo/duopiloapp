using Duopilo.Atc;

namespace Duopilo.Tests;

/// <summary>Follow-me nach der Landung: nur auf Anfrage beim Ground, Warten aufs Fahrzeug, "follow the follow-me", Rückfallebenen.</summary>
public sealed partial class AtcTests
{
    private const string Vacated = "Berlin Ground, Lufthansa four alpha bravo, runway two five right vacated";

    /// <summary>Am Boden in Berlin nach dem Abrollen: Ground ist zuständig und wartet auf den Erstanruf.</summary>
    private static (AtcSession Session, Flight Flight) OnGroundAtBerlin(ITaxiRouteProvider? taxi = null, AtcFollowMeStatus? followMe = null)
    {
        var session = Session(ShortHaul(), taxi);
        var flight = new Flight(session);
        flight.State = flight.State with
        {
            Lat = BerLat,
            Lon = BerLon,
            AltitudeFt = 157,
            HeadingMagDeg = 250,
            ParkingBrake = false,
            FollowMe = followMe ?? new AtcFollowMeStatus(0, AtcFollowMeVehicle.None),
        };
        flight.Start();
        Assert.Equal(AtcPhase.GroundArrival, session.Phase);
        Assert.Equal(AtcStep.ArrivalContact, session.Step);
        return (session, flight);
    }

    [Theory]
    [InlineData("Berlin Ground, Lufthansa four alpha bravo, request follow-me", AtcFollowMeCallKind.Request)]
    [InlineData("request follow me", AtcFollowMeCallKind.Request)]
    [InlineData("Lufthansa four alpha bravo, requesting followme to the gate", AtcFollowMeCallKind.Request)]
    [InlineData("cancel follow-me, Lufthansa four alpha bravo", AtcFollowMeCallKind.Cancel)]
    [InlineData("follow-me no longer required", AtcFollowMeCallKind.Cancel)]
    [InlineData("we don't need the follow me", AtcFollowMeCallKind.Cancel)]
    [InlineData("follow the follow-me to gate bravo one two, Lufthansa four alpha bravo", null)]
    [InlineData("runway two five right vacated", null)]
    public void Follow_me_im_Funk_erkennen(string text, AtcFollowMeCallKind? expected)
    {
        Assert.Equal(expected, AtcFollowMeCall.Parse(HeardText.Parse(text)));
    }

    [Fact]
    public void Ohne_Anfrage_kommt_kein_Follow_me()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());

        Assert.Equal("DLH4AB, taxi to gate A26 via L, L5", Assert.Single(flight.Say(Vacated)).Text);
        Assert.Empty(flight.Say("taxi to gate alpha two six, Lufthansa four alpha bravo"));
        flight.Run(30, s => s with { GroundSpeedKt = 12 });

        Assert.Equal(AtcFollowMe.None, session.FollowMe);
        Assert.Equal(0, session.FollowMeRequestId);
        Assert.DoesNotContain(flight.Heard, t => t.Text.Contains("follow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Follow_me_auf_Anfrage_bis_zum_Gate()
    {
        var (session, flight) = OnGroundAtBerlin();

        // Erstanruf mit Anfrage: halten, das Fahrzeug kommt
        var hold = Assert.Single(flight.Say(Vacated + ", request follow-me"));
        Assert.Equal("DLH4AB, hold position, follow-me is on the way", hold.Text);
        Assert.Equal(AtcFollowMe.Requested, session.FollowMe);
        Assert.Equal(AtcStep.FollowMeWait, session.Step);
        Assert.Empty(flight.Say("hold position, Lufthansa four alpha bravo"));

        // Unterwegs: Ground wartet; ein Stand zu einer anderen Anfrage zählt nicht
        Assert.Empty(flight.Run(30, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.OnTheWay) }));
        Assert.Empty(flight.Run(3, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId + 7, AtcFollowMeVehicle.InPosition, "C3") }));

        // Gewendet vor dem Flugzeug: hinterher zum Gate des Fahrzeugs (ATC kennt keins)
        var follow = Assert.Single(flight.Run(1, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.InPosition, "B12") }));
        Assert.Equal("DLH4AB, follow the follow-me to gate B12", follow.Text);
        Assert.Contains("bravo one two", follow.Spoken);
        Assert.Equal(AtcFollowMe.Following, session.FollowMe);
        Assert.Equal(AtcStep.FollowMeReadback, session.Step);

        Assert.Empty(flight.Say("follow the follow-me to gate bravo one two, Lufthansa four alpha bravo"));
        Assert.Equal(AtcStep.TaxiIn, session.Step);

        // Hinterherrollen und am Gate stehen: fertig
        Assert.Empty(flight.Run(60, s => s with { GroundSpeedKt = 12, FollowMe = s.FollowMe! with { State = AtcFollowMeVehicle.Leading } }));
        flight.Run(15, s => s with { GroundSpeedKt = 0, ParkingBrake = true, FollowMe = s.FollowMe! with { State = AtcFollowMeVehicle.Finished } });
        Assert.Equal(AtcPhase.Done, session.Phase);
    }

    [Fact]
    public void Follow_me_folgt_dem_Gate_von_ATC()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.Say(Vacated + ", request follow-me");

        // Das Fahrzeug meldet ein anderes Gate – ATC bleibt bei seinem
        var follow = Assert.Single(flight.Run(1, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.InPosition, "B12") }));
        Assert.Equal("DLH4AB, follow the follow-me to gate A26", follow.Text);
        Assert.Equal("A26", session.KnownGate);
    }

    [Fact]
    public void Follow_me_auch_noch_beim_Rollen()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.Say(Vacated);
        flight.Say("taxi to gate alpha two six, Lufthansa four alpha bravo");
        flight.Run(20, s => s with { GroundSpeedKt = 12 });
        Assert.Equal(AtcStep.TaxiIn, session.Step);

        Assert.Equal("DLH4AB, hold position, follow-me is on the way",
            Assert.Single(flight.Say("Berlin Ground, Lufthansa four alpha bravo, request follow-me")).Text);
        Assert.Equal(AtcStep.FollowMeWait, session.Step);
    }

    [Fact]
    public void Follow_me_auch_bei_offenem_Readback_des_Gates()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.Say(Vacated);
        Assert.NotNull(session.PendingReadback);

        // Kein "negative, gate A26": die Anfrage ersetzt die Rollfreigabe
        Assert.Equal("DLH4AB, hold position, follow-me is on the way",
            Assert.Single(flight.Say("request follow-me, Lufthansa four alpha bravo")).Text);
        Assert.Null(session.PendingReadback);
    }

    [Fact]
    public void Ohne_Fahrzeug_gleich_die_Rollfreigabe()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.State = flight.State with { FollowMe = null };

        var answer = Assert.Single(flight.Say(Vacated + ", request follow-me"));
        Assert.Equal("DLH4AB, follow-me not available, taxi to gate A26 via L, L5", answer.Text);
        Assert.Equal(AtcFollowMe.Cancelled, session.FollowMe);
        Assert.Empty(flight.Say("taxi to gate alpha two six, Lufthansa four alpha bravo"));
        Assert.Equal(AtcStep.TaxiIn, session.Step);
    }

    [Fact]
    public void Fahrzeug_nicht_verfuegbar()
    {
        var (session, flight) = OnGroundAtBerlin();
        flight.Say(Vacated + ", request follow-me");
        flight.Say("hold position, Lufthansa four alpha bravo");

        var answer = Assert.Single(flight.Run(2, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.Unavailable) }));
        Assert.Equal("DLH4AB, follow-me not available, taxi to parking", answer.Text);
        Assert.Equal(AtcFollowMe.Cancelled, session.FollowMe);
        Assert.Equal(AtcStep.TaxiIn, session.Step);
    }

    [Fact]
    public void Kommt_das_Fahrzeug_nicht_gibt_Ground_auf()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.Say(Vacated + ", request follow-me");
        flight.Say("hold position, Lufthansa four alpha bravo");

        Assert.Empty(flight.Run(230, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.OnTheWay) }));
        var answer = Assert.Single(flight.Run(15));
        Assert.Equal("DLH4AB, follow-me not available, taxi to gate A26 via L, L5", answer.Text);
        Assert.Equal(AtcStep.GateReadback, session.Step);
    }

    [Fact]
    public void Follow_me_abbestellen()
    {
        var (session, flight) = OnGroundAtBerlin(new FixedTaxiRoutes());
        flight.Say(Vacated + ", request follow-me");
        flight.Say("hold position, Lufthansa four alpha bravo");
        flight.Run(10, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.OnTheWay) });

        // Noch am Warten: die normale Rollfreigabe
        Assert.Equal("DLH4AB, taxi to gate A26 via L, L5",
            Assert.Single(flight.Say("Berlin Ground, Lufthansa four alpha bravo, cancel follow-me")).Text);
        Assert.Equal(AtcFollowMe.Cancelled, session.FollowMe);
        Assert.Empty(flight.Say("taxi to gate alpha two six, Lufthansa four alpha bravo"));

        // Ein spätes "in position" ändert nichts mehr
        Assert.Empty(flight.Run(5, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.InPosition) }));
        Assert.Equal(AtcStep.TaxiIn, session.Step);
    }

    [Fact]
    public void Follow_me_beim_Hinterherrollen_abbestellen()
    {
        var (session, flight) = OnGroundAtBerlin();
        flight.Say(Vacated + ", request follow-me");
        flight.Run(1, s => s with { FollowMe = new AtcFollowMeStatus(session.FollowMeRequestId, AtcFollowMeVehicle.InPosition, "B12") });
        flight.Say("follow the follow-me to gate bravo one two, Lufthansa four alpha bravo");

        Assert.Equal("DLH4AB, roger", Assert.Single(flight.Say("Berlin Ground, Lufthansa four alpha bravo, follow-me not required")).Text);
        Assert.Equal(AtcFollowMe.Cancelled, session.FollowMe);
        Assert.Equal(AtcStep.TaxiIn, session.Step);
    }

    [Fact]
    public void Vor_dem_Abflug_gibt_es_kein_Follow_me()
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();

        Assert.Equal("DLH4AB, negative, follow-me not available",
            Assert.Single(flight.Say("Frankfurt Delivery, Lufthansa four alpha bravo, request follow-me")).Text);
        Assert.Equal(AtcPhase.Delivery, session.Phase);
        Assert.Equal(AtcFollowMe.None, session.FollowMe);
    }

    [Fact]
    public void Nach_dem_Neustart_ruft_der_Pilot_Ground_neu()
    {
        var (session, flight) = OnGroundAtBerlin();
        flight.Say(Vacated + ", request follow-me");
        Assert.Equal(AtcStep.FollowMeWait, session.Step);

        var (restored, _) = RestartWith(session, ShortHaul(), flight.Input);

        Assert.Equal(AtcPhase.GroundArrival, restored.Phase);
        Assert.Equal(AtcStep.ArrivalContact, restored.Step);
        Assert.Equal(AtcFollowMe.None, restored.FollowMe);
    }
}
