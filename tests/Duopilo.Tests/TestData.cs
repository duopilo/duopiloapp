using Duopilo.Atc;
using Duopilo.Atc.Airspace;

namespace Duopilo.Tests;

/// <summary>Mitgelieferte Daten aus dem Repository: ATC-Vorlagen, OpenFlights-Airlines, VAT-Spy und Sektoren (einmal geladen).</summary>
internal static class TestData
{
    private static readonly Lazy<AirlineDirectory> LazyAirlines = new(() =>
    {
        var messages = new List<string>();
        var airlines = AirspaceLoader.LoadAirlines([DataRoot], messages);
        Assert.True(airlines.Count > 1000, string.Join(" | ", messages));
        return airlines;
    });

    private static readonly Lazy<AirspaceDatabase> LazyAirspace = new(() =>
    {
        var messages = new List<string>();
        var airspace = AirspaceLoader.Load([DataRoot], messages);
        Assert.True(airspace.Sectors.Count > 1000 && airspace.HasBoundary("CZUL"), string.Join(" | ", messages));
        return airspace;
    });

    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Duopilo", AtcTemplateLoader.FileName)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    public static string DataRoot => RepoFile("src", "Duopilo", AirspaceLoader.DataFolder);

    public static AirlineDirectory Airlines => LazyAirlines.Value;

    public static AirspaceDatabase Airspace => LazyAirspace.Value;

    /// <summary>Mitgelieferte Vorlagen mit der Airline-Datenbank (wie im AtcService).</summary>
    public static AtcTemplates Templates()
    {
        var templates = AtcTemplateLoader.Parse(File.ReadAllText(RepoFile("src", "Duopilo", AtcTemplateLoader.FileName)));
        templates.Airlines = Airlines;
        return templates;
    }
}
