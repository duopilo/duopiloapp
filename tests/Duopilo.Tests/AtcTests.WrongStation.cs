using Duopilo.Atc;
using Duopilo.FlightPlan;

namespace Duopilo.Tests;

/// <summary>
/// Anruf an eine falsche Station am Boden (Testflug 30.10.: in Düsseldorf wurde "Frankfurt Tower" angenommen).
/// Ursachen: "Düsseldorf" fiel als Nicht-ASCII-Wort aus dem Vergleich (Prüfung damit ganz aus), und ein unbekannter
/// Flughafen galt nur vor Control/Radar/Center als Station, nicht vor Tower/Ground/Delivery.
/// </summary>
public sealed partial class AtcTests
{
    private const double DusLat = 51.2895;
    private const double DusLon = 6.7668;

    /// <summary>Wie <see cref="ShortHaul"/>, aber ab Düsseldorf; die Session steht in Düsseldorf am Boden.</summary>
    private static (AtcSession Session, Flight Pilot) AtDuesseldorf()
    {
        var ofp = ShortHaul() with
        {
            RequestId = "dus-ber-1",
            Origin = Airport("EDDL", "Düsseldorf International", "23R", DusLat, DusLon, 147, 5000),
        };
        var session = Session(ofp);
        var flight = new Flight(session);
        flight.State = flight.State with { Lat = DusLat, Lon = DusLon, AltitudeFt = 147 };
        flight.Start();
        return (session, flight);
    }

    [Fact]
    public void Umlaute_werden_zu_ganzen_Woertern()
    {
        var words = HeardText.Parse("Düsseldorf Tower, Zürich, Straße").Tokens.Where(t => t.Kind == HeardKind.Word).Select(t => t.Text);
        Assert.Equal(["dusseldorf", "tower", "zurich", "strasse"], words);
    }

    [Theory]
    [InlineData("Frankfurt Tower, Lufthansa four alpha bravo, request IFR clearance to Berlin", true)]
    [InlineData("Frankfurt Tower, request IFR clearance to Berlin", true)]
    [InlineData("Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", true)]
    [InlineData("Köln Ground, Lufthansa four alpha bravo, request IFR clearance to Berlin", true)]
    [InlineData("Düsseldorf Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", false)]
    [InlineData("Dusseldorf Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin", false)]
    [InlineData("Lufthansa four alpha bravo, request IFR clearance to Berlin", false)]
    [InlineData("Request IFR clearance to Berlin, Lufthansa four alpha bravo", false)]
    public void Falsche_Station_in_Duesseldorf(string text, bool wrongStation)
    {
        var (session, flight) = AtDuesseldorf();
        Assert.Equal("Düsseldorf Delivery", session.Station.Name);

        var replies = flight.Say(text);
        Assert.Equal(wrongStation, replies.Any(r => r.MessageId == AtcMessageIds.CommonWrongStation));
        if (wrongStation)
        {
            Assert.Equal("DLH4AB, this is Düsseldorf Delivery, say again", Assert.Single(replies).Text);
            Assert.Equal(AtcPhase.Delivery, session.Phase);
        }
    }

    [Theory]
    [InlineData("Düsseldorf Tower, Lufthansa four alpha bravo, request IFR clearance to Berlin", true)]
    [InlineData("Langen Radar, request IFR clearance to Berlin", true)]
    [InlineData("Frankfurt Tower, Lufthansa four alpha bravo, request IFR clearance to Berlin", false)]
    [InlineData("Expect ILS approach, Lufthansa four alpha bravo", false)]
    [InlineData("Good morning, Lufthansa four alpha bravo, request IFR clearance to Berlin", false)]
    public void Falsche_Station_in_Frankfurt(string text, bool wrongStation)
    {
        var session = Session(ShortHaul());
        var flight = new Flight(session);
        flight.Start();
        Assert.Equal("Frankfurt Delivery", session.Station.Name);

        var replies = flight.Say(text);
        Assert.Equal(wrongStation, replies.Any(r => r.MessageId == AtcMessageIds.CommonWrongStation));
        if (wrongStation)
        {
            Assert.Equal("DLH4AB, this is Frankfurt Delivery, say again", Assert.Single(replies).Text);
        }
    }
}
