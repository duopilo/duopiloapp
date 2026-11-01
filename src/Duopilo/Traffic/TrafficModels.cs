namespace Duopilo.Traffic;

/// <summary>
/// Flugzeugmodelle, die nicht in den eigenen Verkehr gehören. Die Modelle von FlyByWire (A32NX, A380X) und Headwind (A330-900neo)
/// steuern Fahrwerk und Triebwerke über eigene Systeme, die für KI-Objekte nicht laufen: Im Test (EDDL, 1.11.2026) kam das Fahrwerk
/// mit jedem Befehl nur halb heraus, obwohl alle Fahrwerkswerte 100 % meldeten, und die Triebwerke drehten nicht.
/// Die Titel im Sim nennen den Hersteller nicht immer (Headwind: „Airbus A330-900neo Condor Beach“), deshalb Titelmuster.
/// </summary>
public static class TrafficModels
{
    /// <summary>Teilstücke von Titel oder Livery (ohne Groß-/Kleinschreibung), bei denen ein Modell gesperrt ist.</summary>
    public static readonly IReadOnlyList<string> ExcludedPatterns = ["FlyByWire", "A32NX", "A380X", "Headwind", "A330-900neo"];

    /// <summary>Das Muster, an dem ein Modell gesperrt ist, sonst null.</summary>
    public static string? ExcludedBy(string title, string? livery = null) =>
        ExcludedPatterns.FirstOrDefault(p =>
            title.Contains(p, StringComparison.OrdinalIgnoreCase)
            || (livery?.Contains(p, StringComparison.OrdinalIgnoreCase) ?? false));

    public static bool IsExcluded(string title, string? livery = null) => ExcludedBy(title, livery) is not null;
}
