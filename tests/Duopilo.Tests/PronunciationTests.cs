using Kopilot.Atc;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Speech;

namespace Kopilot.Tests;

/// <summary>Gemeinsame Aussprache für ATC, Callouts, Fuel Check und Sprachschleife (Testflug CFG2205, Ergänzung zu Prompt C).</summary>
public sealed class PronunciationTests
{
    /// <summary>Navlog wie über Labrador: Funkfeuer YWK "WABUSH", Wegpunkt HENDY, Koordinatenpunkt, Flughäfen und STAR.</summary>
    private static readonly SpeechContext Labrador = SpeechContext.From(new Ofp
    {
        Origin = new OfpAirport("EDDF", "Frankfurt/Main", "25C", 50.03, 8.57, 364, 5000, 6000, null),
        Destination = new OfpAirport("KLAX", "Los Angeles", "24R", 33.94, -118.41, 128, 18000, 18000, null),
        Alternate = new OfpAirport("KONT", "Ontario", "26R", 34.06, -117.6, 944, 18000, 18000, null),
        StarIdent = "ANJLL4",
        Fixes =
        [
            new OfpFix { Ident = "5350N", Name = "53N050W", Type = "ltlg", Lat = 53, Lon = -50 },
            new OfpFix { Ident = "YWK", Name = "WABUSH", Type = "vor", Lat = 52.92, Lon = -66.87 },
            new OfpFix { Ident = "HENDY", Name = "HENDY", Type = "wpt", Lat = 50.5, Lon = -73.0 },
            new OfpFix { Ident = "QX", Name = null, Type = "ndb", Lat = 49.0, Lon = -80.0 },
            new OfpFix { Ident = "KLAX", Name = "LOS ANGELES INTL", Type = "apt", Lat = 33.94, Lon = -118.41 },
        ],
    });

    // ------------------------------------------------------------------ Wegpunkte

    [Theory]
    [InlineData("HENDY", null, "Hendy")]
    [InlineData("YWK", "WABUSH", "Wabush")]
    [InlineData("YWK", null, "Wabush")] // Name aus dem Navlog
    [InlineData("FFM", null, "foxtrot foxtrot mike")] // nicht im Navlog
    [InlineData("QX", null, "quebec x-ray")]
    [InlineData("YYR", "GOOSE BAY", "Goose Bay")]
    [InlineData("YYR", "YYR", "yankee yankee romeo")]
    [InlineData("YYR", "VOR/DME", "yankee yankee romeo")]
    [InlineData("KLAX", null, "kilo lima alpha x-ray")] // ICAO immer buchstabiert
    [InlineData("A26", null, "alpha two six")]
    [InlineData("5350N", null, "fifty three north fifty west")] // aus dem Navlog
    [InlineData("55N020W", null, "fifty five north twenty west")]
    [InlineData("N55W020", null, "fifty five north twenty west")]
    [InlineData("5520N", null, "fifty five north twenty west")] // ARINC 424, Navlog kennt ihn nicht
    [InlineData("55N30", null, "fifty five north one hundred thirty west")]
    public void Wegpunkte(string ident, string? name, string spoken) => Assert.Equal(spoken, Pronunciation.Waypoint(ident, name, context: Labrador));

    [Fact]
    public void Koordinaten_mit_Minuten_und_Kennungen()
    {
        Assert.Equal("fifty five thirty north twenty west", Pronunciation.Coordinate(55.5, -20));
        Assert.Equal("forty five zero five south one hundred seventy east", Pronunciation.Coordinate(-45.0833, 170));
        Assert.Equal((55.5, -20.0), Pronunciation.ParseCoordinate("5530N02000W"));
        Assert.Null(Pronunciation.ParseCoordinate("HENDY"));
        Assert.Equal("Marun seven golf", Pronunciation.Ident("MARUN7G"));
        Assert.Equal("Anjll four", Pronunciation.Ident("ANJLL4"));
    }

    // ------------------------------------------------------------------ Zahlen nach ICAO

    [Theory]
    [InlineData(340, "flight level three four zero")]
    [InlineData(100, "flight level one hundred")]
    [InlineData(200, "flight level two hundred")]
    [InlineData(110, "flight level one one zero")]
    [InlineData(70, "flight level seven zero")]
    public void Flugflaechen(int level, string spoken) => Assert.Equal(spoken, Pronunciation.FlightLevel(level));

    [Fact]
    public void Bahnen_Squawks_Hoehen_Frequenzen()
    {
        Assert.Equal("two five right", Pronunciation.Runway(25, "R"));
        Assert.Equal("zero niner", Pronunciation.Runway(9, null));
        Assert.Equal("two three four zero", Pronunciation.Squawk("2340"));
        Assert.Equal("seven zero zero zero", Pronunciation.Squawk("7000"));
        Assert.Equal("niner thousand", Pronunciation.Thousands(9000));
        Assert.Equal("one zero thousand", Pronunciation.Thousands(10000));
        Assert.Equal("two thousand five hundred feet", Pronunciation.Feet(2500));
        Assert.Equal("one three two decimal two five", Pronunciation.Frequency(132.25));
        Assert.Equal("one one niner decimal zero", Pronunciation.Frequency(119.0));
        Assert.Equal("zero niner zero", Pronunciation.Heading(90));
        Assert.Equal("kilo lima alpha x-ray", Pronunciation.Airport("klax"));
    }

    // ------------------------------------------------------------------ Text vor der Sprachausgabe

    [Theory]
    [InlineData("YWK, fuel 34.1, 2.2 below plan", "Wabush, fuel thirty four point one, two point two below plan")]
    [InlineData("HENDY, fuel 18.4, on plan", "Hendy, fuel eighteen point four, on plan")]
    [InlineData("53N050W, fuel 21.0, on plan", "fifty three north fifty west, fuel twenty one point zero, on plan")]
    [InlineData("Fuel 850 kilos", "Fuel eight hundred fifty kilos")]
    [InlineData("Fuel 121.4, 2.2 below plan", "Fuel one hundred twenty one point four, two point two below plan")]
    [InlineData("Runway 25R assigned", "Runway two five right assigned")]
    [InlineData("Destination KLAX, alternate KONT", "Destination kilo lima alpha x-ray, alternate kilo oscar november tango")]
    [InlineData("Descend via ANJLL4", "Descend via Anjll four")]
    [InlineData("Squawk 2340", "Squawk two three four zero")]
    [InlineData("FL100", "flight level one hundred")]
    [InlineData("Altitude FL340 set", "Altitude flight level three four zero set")]
    [InlineData("Contact 132.250", "Contact one three two decimal two five")]
    [InlineData("Master caution, HYD G RSVR LO PR", "Master caution, hydraulic green reservoir low pressure")]
    [InlineData("V1", "V1")]
    public void Text_vor_der_Sprachausgabe(string text, string spoken) => Assert.Equal(spoken, Pronunciation.ForSpeech(text, Labrador));

    [Fact]
    public void Ohne_Plan_bleiben_unbekannte_Kennungen_Woerter()
    {
        Assert.Equal("ywk, fuel thirty four point one", Pronunciation.ForSpeech("YWK, fuel 34.1", SpeechContext.Empty));
        Assert.Equal("fifty three north fifty west", Pronunciation.ForSpeech("53N050W", SpeechContext.Empty));
    }

    [Theory]
    [InlineData("YWK, fuel 34.1, 2.2 below plan")]
    [InlineData("Master warning, ENG 1 FIRE")]
    [InlineData("Speed 250 selected, QNH 1013, FL100, runway 09L")]
    [InlineData("Condor two two zero five, descend via Anjll four arrival, altimeter two niner niner two")]
    public void Zweimal_aufbereitet_bleibt_gleich(string text)
    {
        var once = Pronunciation.ForSpeech(text, Labrador);
        Assert.Equal(once, Pronunciation.ForSpeech(once, Labrador));
    }

    [Fact]
    public void Vorlagentext_behaelt_die_Leerzeichen()
    {
        Assert.Equal(", climb ", Pronunciation.Fragment(", climb ", Labrador));
        Assert.Equal(" ", Pronunciation.Fragment(" ", Labrador));
        Assert.Equal(", request I F R clearance", Pronunciation.Fragment(", request IFR clearance", Labrador));
        Assert.Equal("ten", Pronunciation.Fragment("10", Labrador));
    }

    // ------------------------------------------------------------------ Dieselbe Aussprache in ATC und Fuel Check

    [Fact]
    public void ATC_und_Fuel_Check_sprechen_Fixe_gleich()
    {
        var fuel = FuelCheckText.Place(new FuelCheckFix("YWK", "WABUSH", "vor", "CRZ", 52.92, -66.87));
        var atc = AtcSpeech.Fix("YWK", "WABUSH");
        Assert.Equal("Wabush", fuel);
        Assert.Equal(fuel, atc.Spoken);
        Assert.Equal("YWK", atc.Written);

        // Readback: Kennung oder Name
        Assert.True(ReadbackChecker.Check("direct Wabush, Condor 2205", [new ReadbackItem("fix", atc)]).Correct);
        Assert.True(ReadbackChecker.Check("direct YWK, Condor 2205", [new ReadbackItem("fix", atc)]).Correct);
        Assert.False(ReadbackChecker.Check("direct Hendy, Condor 2205", [new ReadbackItem("fix", atc)]).Correct);

        Assert.Equal(
            FuelCheckText.Place(new FuelCheckFix("5350N", "53N050W", "ltlg", "CRZ", 53, -50)),
            AtcSpeech.Position(53, -50).Spoken);
    }

    [Fact]
    public void Readback_versteht_natuerlich_gesprochene_Zahlen()
    {
        var position = AtcSpeech.Position(53, -50);
        Assert.True(ReadbackChecker.Check("position fifty three north fifty west", [new ReadbackItem("fix", position)]).Correct);
        Assert.False(ReadbackChecker.Check("position fifty three north forty west", [new ReadbackItem("fix", position)]).Correct);
        Assert.Equal(10000, Assert.Single(ReadbackChecker.Altitudes(HeardText.Parse("climb ten thousand feet"))));
    }
}
