namespace Kopilot.Sim;

public enum SimVarKind
{
    Number,
    Bool,
    String,
}

/// <summary>
/// Eine Variable, die Kopilot bei SimConnect abonniert.
/// </summary>
/// <param name="Key">Interner Schlüssel, unter dem der Wert abgelegt wird.</param>
/// <param name="Name">SimConnect-Name, z. B. "INDICATED ALTITUDE" oder "L:INI_IRGENDWAS".</param>
/// <param name="Unit">SimConnect-Einheit, z. B. "feet". Bei Strings ignoriert.</param>
/// <param name="Kind">Datentyp.</param>
public sealed record SimVarSpec(string Key, string Name, string Unit, SimVarKind Kind)
{
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

    public static SimVarKind KindFromUnit(string? unit) =>
        unit?.Trim().ToLowerInvariant() switch
        {
            "string" => SimVarKind.String,
            "bool" => SimVarKind.Bool,
            _ => SimVarKind.Number,
        };
}
