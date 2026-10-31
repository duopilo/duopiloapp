using Duopilo.Sim;

namespace Duopilo.Tests;

/// <summary>Input Events auslesen (list_input_events): Filter, Dateiname, Rundung. Die SimConnect-Seite lässt sich nur im Sim prüfen.</summary>
public sealed class InputEventExplorerTests
{
    private static readonly InputEventInfo[] Events =
    [
        new("INSTRUMENT_FCU_SPD_PULL", 1, "double"),
        new("INSTRUMENT_FCU_SPD_Knob", 2, "double"),
        new("INSTRUMENT_FCU_HDG_PULL", 3, "double"),
        new("LIGHTING_STROBE_Switch", 4, "double"),
        new("ELECTRICAL_Battery_1", 5, "double"),
    ];

    [Fact]
    public void Ohne_Filter_alle_sortiert()
    {
        var all = InputEventExplorer.Filter(Events, null);

        Assert.Equal(5, all.Count);
        Assert.Equal("ELECTRICAL_Battery_1", all[0].Name);
    }

    [Theory]
    [InlineData("fcu", 3)]
    [InlineData("FCU SPD", 2)]
    [InlineData("fcu, pull", 2)]
    [InlineData("strobe", 1)]
    [InlineData("  ", 5)]
    [InlineData("gear", 0)]
    public void Alle_Woerter_muessen_vorkommen(string filter, int expected) =>
        Assert.Equal(expected, InputEventExplorer.Filter(Events, filter).Count);

    [Theory]
    [InlineData("Airbus A321LR Condor", "Airbus_A321LR_Condor")]
    [InlineData("iniBuilds A321LR (CFG) / Test", "iniBuilds_A321LR__CFG____Test")]
    [InlineData(null, "aircraft")]
    [InlineData("///", "aircraft")]
    public void Dateiname_nur_sichere_Zeichen(string? title, string expected) =>
        Assert.Equal(expected, InputEventExplorer.SafeName(title));

    [Fact]
    public void Langer_Titel_wird_gekuerzt() =>
        Assert.Equal(80, InputEventExplorer.SafeName(new string('A', 200)).Length);

    [Fact]
    public void Werte_gerundet_Texte_unveraendert()
    {
        Assert.Equal(1.0, InputEventExplorer.Round(0.99999999));
        Assert.Equal("ON", InputEventExplorer.Round("ON"));
        Assert.Null(InputEventExplorer.Round(null));
    }
}
