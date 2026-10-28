using System.Globalization;
using System.Text.Json.Serialization;

namespace Kopilot.Aircraft;

/// <summary>
/// Flugzeugspezifische Zusatzvariablen (meist L:-Variablen), z. B. FMA-Modi eines Airbus.
/// Wird über den TITLE des Flugzeugs ausgewählt.
/// </summary>
public sealed class AircraftProfile
{
    public string Name { get; set; } = "";

    /// <summary>Profil passt, wenn der Flugzeug-TITLE einen dieser Texte enthält (Groß-/Kleinschreibung egal).</summary>
    public List<string> TitleContains { get; set; } = [];

    public string? Notes { get; set; }

    public List<ProfileVariable> Variables { get; set; } = [];

    [JsonIgnore]
    public string? SourceFile { get; set; }

    public bool Matches(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && TitleContains.Any(t => !string.IsNullOrWhiteSpace(t) && title.Contains(t, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProfileVariable
{
    /// <summary>Eigener Schlüssel, z. B. "fmaLateral".</summary>
    public string Key { get; set; } = "";

    /// <summary>SimConnect-Name, z. B. "L:INI_FMA_LATERAL" (Beispiel).</summary>
    public string Name { get; set; } = "";

    public string Unit { get; set; } = "number";

    public string? Description { get; set; }

    /// <summary>Optionale Übersetzung Rohwert → Text, z. B. { "1": "HDG", "2": "NAV" }.</summary>
    public Dictionary<string, string>? Values { get; set; }

    public string? Translate(object? raw)
    {
        if (Values is null || raw is not double d)
        {
            return null;
        }

        var key = Math.Round(d).ToString(CultureInfo.InvariantCulture);
        return Values.TryGetValue(key, out var text) ? text : null;
    }
}
