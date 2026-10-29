using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Sprachschleife (v0.9): Normalisierung der Whisper-Abschrift, Kopilot-Befehle und Router – deutsch und englisch gemischt,
/// mit typischen Transkriptionsfehlern ("Flaps one" / "Flaps 1" / "Flaps won").
/// </summary>
public sealed class VoiceRecognitionTests
{
    private static readonly string[] OwnCallsigns = ["Lufthansa 4AB", "DLH4AB"];

    private static VoiceIntent Intent(string raw) => VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]);

    private static VoiceRoute Route(string raw, VoiceRouter? router = null) =>
        (router ?? new VoiceRouter(["kopilot"])).Route(VoiceText.Normalize(raw), OwnCallsigns);

    // ------------------------------------------------------------------ Normalisierung

    [Theory]
    [InlineData("Flaps one.", "flaps 1")]
    [InlineData("Flaps 1", "flaps 1")]
    [InlineData("flaps won", "flaps 1")]
    [InlineData("Flaps too!", "flaps 2")]
    [InlineData("Set flaps to 2", "set flaps to 2")]
    [InlineData("QNH one zero one three", "qnh 1013")]
    [InlineData("Q.N.H. 1013", "qnh 1013")]
    [InlineData("Altitude five thousand", "altitude 5000")]
    [InlineData("Altitude one zero thousand", "altitude 10000")]
    [InlineData("Altitude four thousand five hundred", "altitude 4500")]
    [InlineData("Flight level one two zero", "fl 120")]
    [InlineData("Speed two hundred fifty", "speed 250")]
    [InlineData("Speed two five zero knots", "speed 250 knots")]
    [InlineData("Heading zero niner zero", "heading 090")]
    [InlineData("Squawk seven thousand", "squawk 7000")]
    [InlineData("Klappen eins", "klappen 1")]
    [InlineData("Geschwindigkeit zweihundertfünfzig", "geschwindigkeit 250")]
    [InlineData("Höhe 5.000", "höhe 5000")]
    [InlineData("Altitude 5,000", "altitude 5000")]
    [InlineData("COM one standby 121.800", "com 1 standby 121.800")]
    [InlineData("Vertical speed minus one thousand", "vs -1000")]
    [InlineData("Co-Pilot, speed break retract", "kopilot speedbrake retract")]
    [InlineData("TCAS TA/RA", "tcas tara")]
    [InlineData("Seat belt sign on", "seatbelts on")]
    [InlineData("A.P.U. start", "apu start")]
    [InlineData("That's correct.", "thats correct")]
    [InlineData("", "")]
    public void Abschrift_wird_normalisiert(string raw, string expected)
    {
        Assert.Equal(expected, VoiceText.Normalize(raw));
    }

    [Theory]
    [InlineData("Lufthansa 4AB", "lufthansa4ab")]
    [InlineData("Lufthansa four alpha bravo", "lufthansa4ab")]
    [InlineData("DLH4AB", "dlh4ab")]
    [InlineData("Delta Lima Hotel four Alpha Bravo", "dlh4ab")]
    [InlineData("D-AIAB", "daiab")]
    public void Rufzeichen_in_kompakter_Form(string text, string expected)
    {
        Assert.Equal(expected, VoiceText.Compact(text));
    }

    [Theory]
    [InlineData("zweihundertfünfzig", 250)]
    [InlineData("fünfundzwanzig", 25)]
    [InlineData("dreitausend", 3000)]
    [InlineData("eintausendfünfhundert", 1500)]
    [InlineData("siebenhundertsiebenundsiebzig", 777)]
    public void Deutsche_Zahlwoerter(string word, int expected)
    {
        Assert.True(GermanNumber.TryParse(word, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("ein")]
    [InlineData("acht")]
    [InlineData("achtung")]
    [InlineData("stunde")]
    [InlineData("einen")]
    public void Keine_deutschen_Zahlwoerter(string word)
    {
        Assert.False(GermanNumber.TryParse(word, out _));
    }

    // ------------------------------------------------------------------ Kopilot-Befehle

    [Theory]
    [InlineData("Gear up", "gear.up", null)]
    [InlineData("Fahrwerk einfahren", "gear.up", null)]
    [InlineData("Kopilot, Fahrwerk raus", "gear.down", null)]
    [InlineData("Gear down.", "gear.down", null)]
    [InlineData("Flaps one", "flaps", "1")]
    [InlineData("Flaps 1.", "flaps", "1")]
    [InlineData("flaps won", "flaps", "1")]
    [InlineData("Flaps to", "flaps", "2")]
    [InlineData("Set flaps to 3", "flaps", "3")]
    [InlineData("Klappen voll", "flaps", "FULL")]
    [InlineData("Flaps full", "flaps", "FULL")]
    [InlineData("Flaps up", "flaps", "0")]
    [InlineData("Flaps zero", "flaps", "0")]
    [InlineData("Klappen eins bitte", "flaps", "1")]
    [InlineData("Speedbrake retract", "speedbrake", "retract")]
    [InlineData("Speed brakes retracted", "speedbrake", "retract")]
    [InlineData("Speedbrake half", "speedbrake", "half")]
    [InlineData("Arm spoilers", "spoilers.arm", "arm")]
    [InlineData("Spoiler armen", "spoilers.arm", "arm")]
    [InlineData("Ground spoilers armed", "spoilers.arm", "arm")]
    [InlineData("Disarm spoilers", "spoilers.arm", "disarm")]
    [InlineData("Autobrake medium", "brakes.autobrake", "med")]
    [InlineData("Auto brake max", "brakes.autobrake", "max")]
    [InlineData("Autobrake low", "brakes.autobrake", "lo")]
    [InlineData("Landing lights on", "lights.landing", "on")]
    [InlineData("Landelichter aus", "lights.landing", "off")]
    [InlineData("Strobe auto", "lights.strobe", "auto")]
    [InlineData("Beacon on", "lights.beacon", "on")]
    [InlineData("Taxi light on", "lights.nose", "taxi")]
    [InlineData("Seat belts on", "signs.seatbelts", "on")]
    [InlineData("Fasten seat belts", "signs.seatbelts", "on")]
    [InlineData("Anschnallzeichen aus", "signs.seatbelts", "off")]
    [InlineData("APU start", "apu.start", null)]
    [InlineData("Start the APU", "apu.start", null)]
    [InlineData("APU master on", "apu.master", "on")]
    [InlineData("APU off", "apu.master", "off")]
    [InlineData("APU bleed on", "apu.bleed", "on")]
    [InlineData("QNH 1013", "efis.baro", "1013")]
    [InlineData("QNH one zero one three", "efis.baro", "1013")]
    [InlineData("Baro 1 0 0 8", "efis.baro", "1008")]
    [InlineData("Baro standard", "efis.baroMode", "STD")]
    [InlineData("Standard", "efis.baroMode", "STD")]
    [InlineData("Altitude five thousand", "fcu.altitude", "5000")]
    [InlineData("Set altitude 12000", "fcu.altitude", "12000")]
    [InlineData("Flight level one two zero", "fcu.altitude", "FL120")]
    [InlineData("Speed two five zero", "fcu.speed", "250")]
    [InlineData("Geschwindigkeit zweihundertfünfzig", "fcu.speed", "250")]
    [InlineData("Managed speed", "fcu.speedManaged", null)]
    [InlineData("Heading zero niner zero", "fcu.heading", "090")]
    [InlineData("Heading 90", "fcu.heading", "090")]
    [InlineData("Vertical speed minus one thousand", "fcu.vs", "-1000")]
    [InlineData("Squawk seven thousand", "xpdr.code", "7000")]
    [InlineData("Squawk 1 2 3 4", "xpdr.code", "1234")]
    [InlineData("TCAS TA/RA", "tcas.mode", "tara")]
    [InlineData("TCAS TA only", "tcas.mode", "ta")]
    [InlineData("Autopilot one on", "fcu.ap1", "on")]
    [InlineData("Approach on", "fcu.appr", "on")]
    [InlineData("Engine anti ice on", "antiice.engines", "on")]
    public void Befehle_werden_erkannt(string raw, string action, string? value)
    {
        var intent = Intent(raw);

        Assert.Equal(IntentKind.Action, intent.Kind);
        Assert.Equal(action, intent.ActionId);
        Assert.Equal(value, intent.Value);
    }

    [Theory]
    [InlineData("Speed check", QueryKind.Speed)]
    [InlineData("Engine check", QueryKind.Engines)]
    [InlineData("Engines check", QueryKind.Engines)]
    [InlineData("Fuel check", QueryKind.Fuel)]
    [InlineData("Altitude check", QueryKind.Altitude)]
    [InlineData("Speedbrake check", QueryKind.Speedbrake)]
    [InlineData("Check gear", QueryKind.Gear)]
    [InlineData("Kopilot, flaps check", QueryKind.Flaps)]
    [InlineData("Fahrwerk Kontrolle", QueryKind.Gear)]
    [InlineData("Treibstoff Kontrolle", QueryKind.Fuel)]
    [InlineData("Altitude control", QueryKind.Altitude)]
    public void Abfragen_werden_erkannt(string raw, QueryKind query)
    {
        var intent = Intent(raw);

        Assert.Equal(IntentKind.Query, intent.Kind);
        Assert.Equal(query, intent.Query);
    }

    [Theory]
    [InlineData("Confirm", IntentKind.Confirm)]
    [InlineData("Correct.", IntentKind.Confirm)]
    [InlineData("Affirm", IntentKind.Confirm)]
    [InlineData("That's correct", IntentKind.Confirm)]
    [InlineData("Ja, korrekt", IntentKind.Confirm)]
    [InlineData("OK, confirm", IntentKind.Confirm)]
    [InlineData("Yes", IntentKind.Acknowledge)]
    [InlineData("Ja", IntentKind.Acknowledge)]
    [InlineData("Okay", IntentKind.Acknowledge)]
    [InlineData("Richtig", IntentKind.Acknowledge)]
    [InlineData("Roger", IntentKind.Acknowledge)]
    [InlineData("Klappen ein", IntentKind.Unknown)]
    [InlineData("Negative", IntentKind.Negative)]
    [InlineData("Cancel", IntentKind.Negative)]
    [InlineData("Nein", IntentKind.Negative)]
    [InlineData("Kopilot Pause", IntentKind.Pause)]
    [InlineData("Kopilot, Pause.", IntentKind.Pause)]
    [InlineData("Kopilot weiter", IntentKind.Resume)]
    [InlineData("Say again", IntentKind.SayAgain)]
    [InlineData("Was gibt es zum Mittagessen", IntentKind.Unknown)]
    [InlineData("Gear up, flaps zero", IntentKind.Unknown)]
    [InlineData("Pause", IntentKind.Unknown)]
    public void Steuerung_und_Unbekanntes(string raw, IntentKind kind)
    {
        Assert.Equal(kind, Intent(raw).Kind);
    }

    // ------------------------------------------------------------------ Router

    [Theory]
    [InlineData("Frankfurt Tower, Lufthansa 4AB, ready for departure", "frankfurt tower")]
    [InlineData("Langen Radar, Lufthansa four alpha bravo, descending flight level one two zero", "langen radar")]
    [InlineData("Düsseldorf Ground, request taxi", "düsseldorf ground")]
    [InlineData("Shanwick, Lufthansa 4AB, request oceanic clearance", "shanwick")]
    [InlineData("New York Center, good morning", "new york center")]
    [InlineData("München Delivery, Lufthansa 4AB, information Alpha", "münchen delivery")]
    public void Station_geht_ans_ATC_Modul(string raw, string station)
    {
        var route = Route(raw);

        Assert.Equal(VoiceTarget.Atc, route.Target);
        Assert.Equal(station, route.Station);
    }

    [Theory]
    [InlineData("Lufthansa four alpha bravo, request descent", "Lufthansa 4AB")]
    [InlineData("Lufthansa 4AB, wilco", "Lufthansa 4AB")]
    [InlineData("DLH4AB request descent", "DLH4AB")]
    [InlineData("Request descent, Lufthansa 4AB", "Lufthansa 4AB")]
    [InlineData("Descending flight level one two zero, Lufthansa four alpha bravo", "Lufthansa 4AB")]
    public void Eigenes_Rufzeichen_am_Anfang_oder_hinten_geht_ans_ATC_Modul(string raw, string callsign)
    {
        var route = Route(raw);

        Assert.Equal(VoiceTarget.Atc, route.Target);
        Assert.Equal(callsign, route.Callsign);
    }

    [Theory]
    [InlineData("Flaps one")]
    [InlineData("Approach on")]
    [InlineData("Arm approach")]
    [InlineData("Ground spoilers arm")]
    [InlineData("Kopilot, Frankfurt Tower frequency check")]
    [InlineData("Speed check")]
    [InlineData("Fahrwerk ausfahren")]
    [InlineData("Fahrwerk Kontrolle")]
    [InlineData("Klappen Kontrolle")]
    [InlineData("Geschwindigkeit Kontrolle")]
    [InlineData("Höhe Kontrolle")]
    [InlineData("Treibstoff Kontrolle")]
    [InlineData("Kurs Kontrolle")]
    [InlineData("APU Kontrolle")]
    [InlineData("Triebwerke Kontrolle")]
    [InlineData("Anschnallzeichen Kontrolle")]
    [InlineData("Bremsklappen Kontrolle")]
    [InlineData("Altitude control")]
    [InlineData("Bitte Anflug an")]
    [InlineData("Jetzt Anflug an")]
    public void Alles_andere_geht_an_Kopilot(string raw)
    {
        Assert.Equal(VoiceTarget.Kopilot, Route(raw).Target);
    }

    [Fact]
    public void Weitere_Stationen_aus_den_Einstellungen()
    {
        var router = new VoiceRouter(["kopilot"], ["Scottish"]);

        var route = Route("Scottish, Lufthansa 4AB, level three five zero", router);

        Assert.Equal(VoiceTarget.Atc, route.Target);
        Assert.Equal("scottish", route.Station);
        Assert.Equal(VoiceTarget.Kopilot, Route("Scottish", new VoiceRouter(["kopilot"])).Target);
    }
}
