namespace Duopilo.Sim;

public enum SimVarKind
{
    Number,
    Bool,
    String,
}

/// <summary>
/// Eine Variable, die Duopilo bei SimConnect abonniert.
/// </summary>
/// <param name="Key">Interner Schlüssel, unter dem der Wert abgelegt wird.</param>
/// <param name="Name">SimConnect-Name, z. B. "INDICATED ALTITUDE" oder "L:INI_IRGENDWAS".</param>
/// <param name="Unit">SimConnect-Einheit, z. B. "feet". Bei Strings ignoriert.</param>
/// <param name="Kind">Datentyp.</param>
public sealed record SimVarSpec(string Key, string Name, string Unit, SimVarKind Kind)
{
    /// <summary>Schnell abonniert (mehrmals pro Sekunde) statt 1x pro Sekunde – für Monitoring und Callouts.</summary>
    public bool Fast { get; init; }

    public SimVarSpec AsFast() => this with { Fast = true };

    public static SimVarSpec Num(string key, string name, string unit) => new(key, name, unit, SimVarKind.Number);

    public static SimVarSpec Bool(string key, string name) => new(key, name, "Bool", SimVarKind.Bool);

    public static SimVarSpec Str(string key, string name) => new(key, name, "", SimVarKind.String);

    /// <summary>
    /// Normalisiert einen vom Nutzer/Modell angegebenen Variablennamen.
    /// "A:" wird entfernt (SimConnect erwartet A-Variablen ohne Präfix), L:/Z:/I:/O: bleiben erhalten.
    /// </summary>
    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..].Trim();
        }

        return trimmed;
    }

    // Text-SimVars, die SimConnect nur als String liefert (bei "number" käme 0 bzw. nichts zurück)
    private static readonly HashSet<string> TextVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "TITLE", "ATC ID", "ATC AIRLINE", "ATC FLIGHT NUMBER", "ATC TYPE", "ATC MODEL", "ATC RUNWAY AIRPORT NAME",
        "GPS WP NEXT ID", "GPS WP PREV ID", "GPS APPROACH AIRPORT ID", "GPS APPROACH APPROACH ID", "GPS APPROACH TRANSITION ID",
        "LIVERY NAME", "LIVERY FOLDER", "CATEGORY", "ATC PARKING NAME",
    };

    /// <summary>Bekannte Text-SimVar (z. B. "ATC RUNWAY AIRPORT NAME", "NAV IDENT:1")? L:-Variablen sind nie Text.</summary>
    public static bool IsTextVariable(string name)
    {
        var n = NormalizeName(name);
        if (n.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var colon = n.IndexOf(':');
        if (colon > 1 && n[..colon].All(c => char.IsLetterOrDigit(c) || c == ' '))
        {
            n = n[..colon].Trim();
        }

        return TextVariables.Contains(n) || n.EndsWith(" IDENT", StringComparison.OrdinalIgnoreCase) || n.EndsWith(" NAME", StringComparison.OrdinalIgnoreCase);
    }

    public static SimVarKind KindFromUnit(string? unit) =>
        unit?.Trim().ToLowerInvariant() switch
        {
            "string" or "text" => SimVarKind.String,
            "bool" => SimVarKind.Bool,
            _ => SimVarKind.Number,
        };
}
