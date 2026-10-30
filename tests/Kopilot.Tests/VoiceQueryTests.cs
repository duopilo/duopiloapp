using Kopilot.FlightPlan;
using Kopilot.Speech;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Abfragen per Sprache (v0.9.2, Punkt 2): Sprit gegen Plan und Landemenge, nächster Wegpunkt, ETA, Top of Descent, Verkehr,
/// Wind und Temperatur, Squawk, Station und Frequenz – als reiner Callout, gesprochen über die gemeinsame Aussprache.
/// </summary>
public sealed class VoiceQueryTests
{
    private static VoiceIntent Intent(string raw) => VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]);

    private static readonly VoiceFlightState Cruise = new()
    {
        Connected = true, OnGround = false, IasKt = 268, GroundSpeedKt = 480, AltitudeFt = 34000, PressureAltitudeFt = 36000, BaroHpa = 1013.25,
        FuelKg = 34_120, WindDirectionDeg = 271.4, WindSpeedKt = 45.2, OutsideTempC = -47.4, Squawk = "2340", TransponderMode = "ALT",
        Com1ActiveMhz = 132.6,
        HasPlan = true,
        FuelPlan = new FuelPlanSample { PlanId = "1", ActualKg = 34_100, PlannedKg = 36_300, PlannedLandingKg = 9_000, MinimumLandingKg = 5_200 },
        NextWaypoint = new WaypointReading("HENDY", null, 52.5, -61.1, 119.6, (3 * 3600) + (42 * 60)),
        DestinationEtaZuluSec = (4 * 3600) + (18 * 60),
        TodDistanceNm = 85.3,
        TodEtaZuluSec = (3 * 3600) + (12 * 60) + 20,
    };

    [Theory]
    [InlineData("Fuel versus plan", QueryKind.FuelPlan)]
    [InlineData("Landing fuel", QueryKind.FuelPlan)]
    [InlineData("Sprit bei der Landung", QueryKind.FuelPlan)]
    [InlineData("Landemenge", QueryKind.FuelPlan)]
    [InlineData("Next waypoint", QueryKind.NextWaypoint)]
    [InlineData("Nächster Wegpunkt", QueryKind.NextWaypoint)]
    [InlineData("ETA", QueryKind.Eta)]
    [InlineData("Ankunftszeit", QueryKind.Eta)]
    [InlineData("Top of descent", QueryKind.TopOfDescent)]
    [InlineData("Sinkflugbeginn", QueryKind.TopOfDescent)]
    [InlineData("Traffic check", QueryKind.Traffic)]
    [InlineData("Verkehr Kontrolle", QueryKind.Traffic)]
    [InlineData("Wind check", QueryKind.Wind)]
    [InlineData("Temperatur", QueryKind.Temperature)]
    [InlineData("Wind and temperature", QueryKind.Weather)]
    [InlineData("Squawk check", QueryKind.Squawk)]
    [InlineData("Transponder Code", QueryKind.Squawk)]
    [InlineData("Which frequency", QueryKind.Station)]
    [InlineData("Aktive Station", QueryKind.Station)]
    [InlineData("Kopilot, station check", QueryKind.Station)]
    public void Neue_Abfragen_werden_erkannt(string raw, QueryKind query)
    {
        var intent = Intent(raw);

        Assert.Equal(IntentKind.Query, intent.Kind);
        Assert.Equal(query, intent.Query);
    }

    [Theory]
    [InlineData("Verkehr Kontrolle")]
    [InlineData("Wind Kontrolle")]
    [InlineData("Start approach checklist")]
    public void Abfragen_und_Checklisten_gehen_nicht_ans_ATC_Modul(string raw)
    {
        Assert.Equal(VoiceTarget.Kopilot, new VoiceRouter(["kopilot"]).Route(VoiceText.Normalize(raw)).Target);
    }

    [Theory]
    [InlineData(QueryKind.FuelPlan, "Fuel 34.1, 2.2 below plan, landing fuel 6.8")]
    [InlineData(QueryKind.NextWaypoint, "HENDY, 120 miles, ETA 0342")]
    [InlineData(QueryKind.Eta, "ETA 0418")]
    [InlineData(QueryKind.TopOfDescent, "Top of descent 85 miles, ETA 0312")]
    [InlineData(QueryKind.Wind, "Wind 271 at 45")]
    [InlineData(QueryKind.Temperature, "Temperature -47")]
    [InlineData(QueryKind.Weather, "Wind 271 at 45, temperature -47")]
    [InlineData(QueryKind.Squawk, "Squawk 2340")]
    [InlineData(QueryKind.Station, "Active frequency 132.6")]
    public void Antwort_nur_mit_dem_reinen_Callout(QueryKind query, string answer)
    {
        Assert.Equal(answer, VoiceQueries.Answer(query, Cruise));
    }

    [Theory]
    [InlineData(QueryKind.FuelPlan, "Fuel thirty four point one, two point two below plan, landing fuel six point eight")]
    [InlineData(QueryKind.Eta, "E T A zero four one eight")]
    [InlineData(QueryKind.TopOfDescent, "Top of descent eighty five miles, E T A zero three one two")]
    [InlineData(QueryKind.Wind, "Wind two seven one at forty five")]
    [InlineData(QueryKind.Temperature, "Temperature minus forty seven")]
    [InlineData(QueryKind.Squawk, "Squawk two three four zero")]
    [InlineData(QueryKind.Station, "Active frequency one three two decimal six")]
    public void Aussprache_ueber_die_gemeinsame_Funktion(QueryKind query, string spoken)
    {
        Assert.Equal(spoken, VoiceQueries.Reply(query, Cruise).Spoken);
    }

    [Fact]
    public void Wegpunkt_mit_Namen_aus_dem_Navlog_und_Zeit_Ziffer_fuer_Ziffer()
    {
        var reply = VoiceQueries.Reply(QueryKind.NextWaypoint, Cruise with
        {
            NextWaypoint = new WaypointReading("YWK", "WABUSH", 52.9, -66.9, 4.46, (14 * 3600) + 0),
        });

        Assert.Equal("YWK, 4.5 miles, ETA 1400", reply.Text);
        Assert.Equal("Wabush, four point five miles, E T A one four zero zero", reply.Spoken);
    }

    [Fact]
    public void Station_der_ATC_Sitzung_vor_COM1()
    {
        var withSession = Cruise with { Station = new StationReading("Montreal Center", 132.25) };

        Assert.Equal("Montreal Center, 132.25", VoiceQueries.Answer(QueryKind.Station, withSession));
        Assert.Equal("Montreal Center, one three two decimal two five", VoiceQueries.Reply(QueryKind.Station, withSession).Spoken);
    }

    [Fact]
    public void Ohne_Plan_Unable_und_Sonderfaelle()
    {
        var noPlan = Cruise with { HasPlan = false, FuelPlan = null, NextWaypoint = null, DestinationEtaZuluSec = null, TodDistanceNm = null };

        Assert.Equal(VoiceQueries.Unable, VoiceQueries.Answer(QueryKind.FuelPlan, noPlan));
        Assert.Equal(VoiceQueries.Unable, VoiceQueries.Answer(QueryKind.NextWaypoint, noPlan));
        Assert.Equal(VoiceQueries.Unable, VoiceQueries.Answer(QueryKind.Eta, noPlan));
        Assert.Equal(VoiceQueries.Unable, VoiceQueries.Answer(QueryKind.TopOfDescent, noPlan));
        Assert.Equal("Top of descent passed", VoiceQueries.Answer(QueryKind.TopOfDescent, noPlan with { TodPassed = true }));
        Assert.Equal("Wind calm", VoiceQueries.Answer(QueryKind.Wind, Cruise with { WindSpeedKt = 2 }));
        Assert.Equal("Squawk 2340, standby", VoiceQueries.Answer(QueryKind.Squawk, Cruise with { TransponderMode = "STBY" }));
        Assert.Equal(
            "Fuel 31.0, 5.3 below plan, landing fuel 3.7, below minimum",
            VoiceQueries.Answer(QueryKind.FuelPlan, Cruise with { FuelKg = 31_000 }));
    }

    [Fact]
    public void Verkehr_wie_der_Callout_von_get_traffic()
    {
        Assert.Equal("Traffic, 2 o'clock, 5 miles, 1000 feet above, descending", VoiceQueries.Traffic(new TrafficReading(true, "Traffic, 2 o'clock, 5 miles, 1000 feet above, descending")).Text);
        Assert.Equal(VoiceQueries.NoTraffic, VoiceQueries.Traffic(new TrafficReading(true, null)).Text);
        Assert.Equal(VoiceQueries.Unable, VoiceQueries.Traffic(new TrafficReading(false, null)).Text);
    }

    [Fact]
    public void Eta_Uhrzeit_vierstellig_auch_ueber_Mitternacht()
    {
        Assert.Equal("0005", VoiceQueries.Clock(86_400 + 300));
        Assert.Equal("2359", VoiceQueries.Clock((23 * 3600) + (59 * 60) + 10));
    }

    [Fact]
    public void Wind_und_ETA_Ziffer_fuer_Ziffer_in_der_gemeinsamen_Aussprache()
    {
        Assert.Equal("Wind three zero zero at fifteen", Pronunciation.ForSpeech("Wind 300 at 15", SpeechContext.Empty));
        Assert.Equal("E T A one four zero zero", Pronunciation.ForSpeech("ETA 1400", SpeechContext.Empty));
    }
}
