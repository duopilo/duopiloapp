using System.Text.RegularExpressions;
using Kopilot.Aircraft;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>
/// Was Kopilot überhaupt an MSFS schicken darf – im Code festgelegt, ein bearbeitetes Profil kommt nicht daran vorbei:
/// <list type="bullet">
/// <item>Events nur aus einer festen Liste (FCU, Autobrake, Fahrwerk, Klappen, Spoiler, Licht, Schilder, Transponder, COM).</item>
/// <item>Schreiben nur von L:-Variablen, nie von A:-SimVars. Fahrwerk, Klappen und Spoiler nur über ihre Events mit Sperren.</item>
/// <item>Sperrliste für Triebwerk, Feuer, Treibstoff, Schub, MCDU und Sim-Steuerung – auch bei L:-Variablen.</item>
/// </list>
/// Geprüft beim Laden des Profils, beim Planen und unmittelbar vor dem Senden.
/// </summary>
public static partial class ActionSafety
{
    [GeneratedRegex(
        @"ENG(INE)?_?\d?_?(MASTER|START|MODE)|MASTER_?ENG|ENGINE|STARTER|IGNIT|COMBUSTION|FIRE|AGENT|FUEL|X_?FEED|CROSS_?FEED|THROTTLE|" +
        @"MIXTURE|PROP|CONDITION_?LEVER|CUTOFF|SHUTOFF|MCDU|CDU|EFB|FLYPAD|SLEW|SIM_?RATE|SIM_?DISABLED|PAUSE|FREEZE|REPAIR|" +
        @"FAILURE|CRASH|SITUATION|RELOAD|PLANE_|VELOCITY|PUSHBACK|TUG|GEAR|FLAP|SLAT|SPOILER",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenVariable();

    [GeneratedRegex(
        @"ENG(INE)?_?\d?_?(MASTER|START|MODE)|MASTER_?ENG|ENGINE|STARTER|IGNIT|COMBUSTION|FIRE|AGENT|FUEL|X_?FEED|CROSS_?FEED|THROTTLE|" +
        @"MIXTURE|PROP|CONDITION_?LEVER|CUTOFF|SHUTOFF|MCDU|CDU|EFB|FLYPAD|SLEW|SIM_?RATE|SIM_?DISABLED|PAUSE|FREEZE|REPAIR|" +
        @"FAILURE|CRASH|SITUATION|RELOAD|PLANE_|VELOCITY|PUSHBACK|TUG",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenEvent();

    /// <summary>Erlaubte Standard-Events (genauer Name).</summary>
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "GEAR_UP", "GEAR_DOWN",
        "FLAPS_UP", "FLAPS_1", "FLAPS_2", "FLAPS_3", "FLAPS_DOWN",
        "SPOILERS_SET", "SPOILERS_ARM_SET",
        "BEACON_LIGHTS_SET", "WING_LIGHTS_SET", "STROBES_ON", "STROBES_OFF", "ELECTRICAL_CIRCUIT_TOGGLE",
        "NAV_LIGHTS_SET", "LOGO_LIGHTS_SET",
        "CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE",
        "XPNDR_SET", "COM_STBY_RADIO_SET_HZ", "COM1_RADIO_SWAP",
        "A32NX.AUTOBRAKE_SET", "A32NX.AUTOBRAKE_SET_DISARM", "A32NX.AUTOBRAKE_SET_LO", "A32NX.AUTOBRAKE_SET_MED", "A32NX.AUTOBRAKE_SET_MAX",
    };

    /// <summary>Erlaubte Custom-Events (Präfix).</summary>
    private static readonly string[] AllowedEventPrefixes = ["A32NX.FCU_"];

    /// <summary>ELECTRICAL_CIRCUIT_TOGGLE nur für Lichtstromkreise des A32NX (17–22 Nose/Landing/Turn-off, 27/28 Wing).</summary>
    private static readonly HashSet<uint> AllowedCircuits = [17, 18, 19, 20, 21, 22, 27, 28];

    /// <summary>Events, die eine Sperre brauchen.</summary>
    private static readonly Dictionary<string, string> RequiredInterlock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GEAR_UP"] = Interlocks.NotOnGround,
        ["GEAR_DOWN"] = Interlocks.GearSpeed,
        ["FLAPS_1"] = Interlocks.FlapsSpeed,
        ["FLAPS_2"] = Interlocks.FlapsSpeed,
        ["FLAPS_3"] = Interlocks.FlapsSpeed,
        ["FLAPS_DOWN"] = Interlocks.FlapsSpeed,
    };

    /// <summary>Leerzeichen, Punkte und Doppelpunkte wie Unterstriche behandeln ("GENERAL ENG COMBUSTION:1").</summary>
    private static string Flat(string name) => Regex.Replace(name.Trim(), @"[\s.:]+", "_");

    /// <summary>Grund, warum ein Event nicht gesendet werden darf, sonst null.</summary>
    public static string? EventProblem(string? name, uint data)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Event ohne Namen";
        }

        var n = name.Trim();
        if (n.StartsWith("K:", StringComparison.OrdinalIgnoreCase))
        {
            n = n[2..].Trim();
        }

        if (ForbiddenEvent().IsMatch(Flat(n)))
        {
            return $"{n} ist gesperrt (Triebwerk, Feuer, Treibstoff, Schub, MCDU, Sim-Steuerung)";
        }

        if (!AllowedEvents.Contains(n) && !AllowedEventPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{n} steht nicht auf der Liste erlaubter Events";
        }

        if (n.Equals("ELECTRICAL_CIRCUIT_TOGGLE", StringComparison.OrdinalIgnoreCase) && !AllowedCircuits.Contains(data))
        {
            return $"Stromkreis {data} ist nicht freigegeben (nur Lichtstromkreise)";
        }

        return null;
    }

    /// <summary>Grund, warum eine Variable nicht geschrieben werden darf, sonst null. Nur L:-Variablen.</summary>
    public static string? VariableProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Variable ohne Namen";
        }

        var n = name.Trim();
        if (!n.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{n}: Es dürfen nur L:-Variablen geschrieben werden";
        }

        return ForbiddenVariable().IsMatch(Flat(n[2..]))
            ? $"{n} ist gesperrt (Triebwerk, Feuer, Treibstoff, MCDU, Sim-Steuerung; Fahrwerk/Klappen/Spoiler nur über ihre Events)"
            : null;
    }

    /// <summary>Erstes Problem einer Aktion aus dem Profil (Schritte, Pflicht-Sperren), sonst null.</summary>
    public static string? Problem(ProfileAction action)
    {
        foreach (var step in action.Steps)
        {
            if (step is null)
            {
                return "leerer Schritt";
            }

            if (!string.IsNullOrWhiteSpace(step.Event))
            {
                // Feste Werte prüfen (Stromkreis); berechnete Werte prüft EventProblem beim Planen und Senden
                var data = step.Value is { ValueKind: System.Text.Json.JsonValueKind.Number } v && v.TryGetUInt32(out var u) ? u : 0;
                var isCircuit = step.Event.Trim().Equals("ELECTRICAL_CIRCUIT_TOGGLE", StringComparison.OrdinalIgnoreCase);
                if (EventProblem(step.Event, isCircuit ? data : 0) is { } problem)
                {
                    return problem;
                }

                if (RequiredInterlock.TryGetValue(step.Event.Trim(), out var needed)
                    && !action.Interlocks.Contains(needed, StringComparer.OrdinalIgnoreCase))
                {
                    return $"{step.Event} braucht die Sperre \"{needed}\"";
                }
            }
            else if (VariableProblem(step.Set) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Wirft, wenn das Event nicht erlaubt ist – letzte Prüfung direkt vor SimConnect.</summary>
    public static void EnsureEventAllowed(string name, uint data)
    {
        if (EventProblem(name, data) is { } problem)
        {
            throw new InvalidOperationException(problem + ".");
        }
    }

    /// <summary>Wirft, wenn die Variable nicht geschrieben werden darf – letzte Prüfung direkt vor SimConnect.</summary>
    public static void EnsureVariableAllowed(string name)
    {
        if (VariableProblem(name) is { } problem)
        {
            throw new InvalidOperationException(problem + ".");
        }
    }
}

/// <summary>Sperren, die eine Aktion im Profil anfordern kann. Unbekannte Namen und fehlende Grenzwerte sperren die Aktion.</summary>
public static class Interlocks
{
    /// <summary>Nicht am Boden (z. B. Fahrwerk einfahren).</summary>
    public const string NotOnGround = "notOnGround";

    /// <summary>Nur am Boden (z. B. External Power).</summary>
    public const string OnGround = "onGround";

    /// <summary>Fahrwerk ausfahren nur bis VLO (ohne VLO: VLE).</summary>
    public const string GearSpeed = "gearSpeed";

    /// <summary>Klappenstellung nur bis zu ihrer VFE (Auswahl = Stellung, z. B. "2" oder "FULL").</summary>
    public const string FlapsSpeed = "flapsSpeed";

    public static readonly IReadOnlyList<string> All = [NotOnGround, OnGround, GearSpeed, FlapsSpeed];

    /// <summary>Grund für die Sperre, oder null, wenn die Aktion erlaubt ist.</summary>
    public static string? Check(string? name, FlightSnapshot s, AircraftLimits limits, string? option)
    {
        if (name is null)
        {
            return "Leere Sperre im Profil – Aktion gesperrt.";
        }

        if (name.Equals(NotOnGround, StringComparison.OrdinalIgnoreCase))
        {
            return s.OnGround switch
            {
                true => "Am Boden gesperrt.",
                null => "Unklar, ob das Flugzeug am Boden ist – gesperrt.",
                _ => null,
            };
        }

        if (name.Equals(OnGround, StringComparison.OrdinalIgnoreCase))
        {
            return s.OnGround switch
            {
                true => null,
                false => "Nur am Boden möglich.",
                null => "Unklar, ob das Flugzeug am Boden ist – gesperrt.",
            };
        }

        if (name.Equals(GearSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true)
            {
                return null;
            }

            if ((limits.VloKt ?? limits.VleKt) is not { } limit)
            {
                return "Im Profil fehlt VLO/VLE – Fahrwerk gesperrt.";
            }

            var label = limits.VloKt is null ? "VLE" : "VLO";
            return s.IasKt switch
            {
                null => "Fahrt unbekannt – Fahrwerk gesperrt.",
                { } ias when ias > limit => $"Fahrwerk gesperrt: {ias:0} kt über {label} {limit:0} kt.",
                _ => null,
            };
        }

        if (name.Equals(FlapsSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true || option is "0")
            {
                return null;
            }

            if (option is null || limits.VfeFor(null, option) is not { } vfe)
            {
                return $"Im Profil fehlt die VFE für Flaps {option} – gesperrt.";
            }

            return s.IasKt switch
            {
                null => "Fahrt unbekannt – Klappen gesperrt.",
                { } ias when ias > vfe => $"Flaps {option} gesperrt: {ias:0} kt über VFE {vfe:0} kt.",
                _ => null,
            };
        }

        return $"Unbekannte Sperre \"{name}\" im Profil – Aktion gesperrt.";
    }
}
