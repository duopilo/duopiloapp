using System.Text.RegularExpressions;
using Kopilot.Aircraft;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>
/// Harte Sperrliste: Triebwerk (Master, Start, Zündung, Schub), Feuer, Treibstoff, MCDU und Sim-Steuerung.
/// Gilt beim Laden des Profils UND unmittelbar vor dem Senden – auch ein bearbeitetes Profil kommt nicht daran vorbei.
/// </summary>
public static partial class ActionSafety
{
    [GeneratedRegex(
        @"ENG(INE)?_?\d?_?MASTER|MASTER_?ENG|ENG(INE)?_?MODE|STARTER|IGNIT|ENGINE_AUTO|TOGGLE_ENGINE|FIRE|AGENT|FUEL|THROTTLE|MIXTURE|PROP_PITCH|" +
        @"CONDITION_LEVER|CUTOFF|SHUTOFF|MCDU|CDU|EFB|FLYPAD|SLEW|SIM_RATE|PAUSE|FREEZE|REPAIR|FAILURE|CRASH|SITUATION|PUSHBACK|TUG",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    /// <summary>true, wenn der Name (Event oder Variable) gesperrt ist.</summary>
    public static bool IsForbidden(string? name) => !string.IsNullOrWhiteSpace(name) && Forbidden().IsMatch(name);

    /// <summary>Erster gesperrter Name in einer Aktion (Schritte, Bedingungen), sonst null.</summary>
    public static string? FirstForbidden(ProfileAction action)
    {
        foreach (var step in action.Steps)
        {
            if (IsForbidden(step.Event))
            {
                return step.Event;
            }

            if (IsForbidden(step.Set))
            {
                return step.Set;
            }
        }

        return null;
    }

    /// <summary>Wirft, wenn der Name gesperrt ist – letzte Prüfung direkt vor SimConnect.</summary>
    public static void EnsureAllowed(string name)
    {
        if (IsForbidden(name))
        {
            throw new InvalidOperationException($"{name} ist für Eingaben gesperrt (Triebwerk, Feuer, Treibstoff, MCDU, Sim-Steuerung).");
        }
    }
}

/// <summary>Sperren, die eine Aktion im Profil anfordern kann. Unbekannte Namen sperren die Aktion.</summary>
public static class Interlocks
{
    /// <summary>Nicht am Boden (z. B. Fahrwerk einfahren).</summary>
    public const string NotOnGround = "notOnGround";

    /// <summary>Fahrwerk ausfahren nur bis VLO (ohne VLO: VLE).</summary>
    public const string GearSpeed = "gearSpeed";

    /// <summary>Klappenstellung nur bis zu ihrer VFE (Auswahl = Stellung, z. B. "2" oder "FULL").</summary>
    public const string FlapsSpeed = "flapsSpeed";

    public static readonly IReadOnlyList<string> All = [NotOnGround, GearSpeed, FlapsSpeed];

    /// <summary>Grund für die Sperre, oder null, wenn die Aktion erlaubt ist.</summary>
    public static string? Check(string name, FlightSnapshot s, AircraftLimits limits, string? option)
    {
        if (name.Equals(NotOnGround, StringComparison.OrdinalIgnoreCase))
        {
            return s.OnGround switch
            {
                true => "Am Boden gesperrt.",
                null => "Unklar, ob das Flugzeug am Boden ist – gesperrt.",
                _ => null,
            };
        }

        if (name.Equals(GearSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true || (limits.VloKt ?? limits.VleKt) is not { } limit)
            {
                return null;
            }

            var name2 = limits.VloKt is null ? "VLE" : "VLO";
            return s.IasKt switch
            {
                null => "Fahrt unbekannt – Fahrwerk gesperrt.",
                { } ias when ias > limit => $"Fahrwerk gesperrt: {ias:0} kt über {name2} {limit:0} kt.",
                _ => null,
            };
        }

        if (name.Equals(FlapsSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true || option is null or "0")
            {
                return null;
            }

            if (limits.VfeFor(null, option) is not { } vfe)
            {
                return null;
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
