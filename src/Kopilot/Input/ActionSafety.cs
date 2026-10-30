using System.Text.RegularExpressions;
using Kopilot.Aircraft;
using Kopilot.Localization;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>
/// Was Kopilot überhaupt an MSFS schicken darf – im Code festgelegt, ein bearbeitetes Profil kommt nicht daran vorbei:
/// <list type="bullet">
/// <item>Events nur aus einer festen Liste (FCU, Autobrake, Fahrwerk, Klappen, Spoiler, Licht, Schilder, Transponder, COM).</item>
/// <item>Schreiben nur von L:-Variablen, nie von A:-SimVars. Fahrwerk, Klappen und Spoiler nur über ihre Events mit Sperren.</item>
/// <item>Sperrliste für Triebwerk, Feuer, Treibstoff (Pumpen, Crossfeed), Hydraulik (auch PTU, RAT), IDG, Schub, MCDU und
/// Sim-Steuerung – auch bei L:-Variablen. Solche Aktionen gibt es nie, auch nicht mit Rückfrage: Schädigendes oder
/// Irreversibles bestätigen die Piloten gegenseitig selbst.</item>
/// <item>Sicherheitsrelevante Schalter (Fahrwerk, Klappen, Speedbrake, Batterien, Packs, APU Master, Transponder, TCAS, AP/ATHR,
/// FCU-Höhe, ADIRS) nur unter ihrer Standard-ID – sonst greifen die Plausibilitätssperren in <see cref="SafetyRules"/> nicht.</item>
/// </list>
/// Geprüft beim Laden des Profils, beim Planen und unmittelbar vor dem Senden.
/// </summary>
public static partial class ActionSafety
{
    private const string ForbiddenCore =
        @"ENG(INE)?_?\d?_?(MASTER|START|MODE)|MASTER_?ENG|ENGINE|STARTER|IGNIT|COMBUSTION|FIRE|AGENT|FUEL|X_?FEED|CROSS_?FEED|THROTTLE|" +
        @"MIXTURE|PROP|CONDITION_?LEVER|CUTOFF|SHUTOFF|MCDU|CDU|EFB|FLYPAD|SLEW|SIM_?RATE|SIM_?DISABLED|PAUSE|FREEZE|REPAIR|" +
        @"FAILURE|CRASH|SITUATION|RELOAD|PLANE_|VELOCITY|PUSHBACK|TUG|" +
        // Hydraulik (Pumpen, PTU, RAT) und IDG-Trennung: irreversibel bzw. schädigend
        @"HYD|(?<![A-Z])IDG|(?<![A-Z])PTU(?![A-Z])|(?<![A-Z])RAT(?![A-Z])";

    [GeneratedRegex(ForbiddenCore + @"|GEAR|FLAP|SLAT|SPOILER", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenVariable();

    [GeneratedRegex(ForbiddenCore, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenEvent();

    /// <summary>Bereiche, die es im Profil nicht geben darf (Groß-/Kleinschreibung egal).</summary>
    private static readonly HashSet<string> ForbiddenAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine", "Engines", "Eng", "Fuel", "Fire", "Hydraulic", "Hydraulics", "Hyd", "IDG",
    };

    /// <summary>Teile einer Aktions-ID, die auf gesperrte Systeme zeigen ("eng.master1", "fuel.xfeed", "elec.idg1", "hyd.ptu").</summary>
    [GeneratedRegex(@"^(eng\d*|engines?|fuel|fire|hyd\w*)(\.|$)|(^|\.)(idg\d*|x?feed|crossfeed|ptu|rat|fire\w*)(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenId();

    /// <summary>
    /// Sicherheitsrelevante Schritte (Event oder Variable) und die IDs, unter denen sie stehen dürfen. Die Plausibilitätssperren
    /// in <see cref="SafetyRules"/> hängen an diesen IDs – eine umbenannte Aktion käme sonst an ihnen vorbei.
    /// What = Schlüssel des Namens in den Sprachdateien.
    /// </summary>
    private static readonly (Regex Pattern, string[] Ids, string What)[] Sensitive =
    [
        (new(@"^GEAR_DOWN$", RegexOptions.IgnoreCase), ["gear.down"], "input.sensitive.gearDown"),
        (new(@"^GEAR_UP$", RegexOptions.IgnoreCase), ["gear.up"], "input.sensitive.gearUp"),
        (new(@"^FLAPS_", RegexOptions.IgnoreCase), ["flaps"], "input.sensitive.flaps"),
        (new(@"(^|_)SPOILERS_SET$", RegexOptions.IgnoreCase), ["speedbrake"], "input.sensitive.speedbrake"),
        (new(@"(?<![A-Z])BAT(T|TERY)?(?![A-Z])", RegexOptions.IgnoreCase), ["elec.battery", "elec.bat1", "elec.bat2"], "input.sensitive.batteries"),
        (new(@"PACK_?\d", RegexOptions.IgnoreCase), ["air.pack1", "air.pack2"], "input.sensitive.packs"),
        (new(@"APU_?MASTER", RegexOptions.IgnoreCase), ["apu.master"], "input.sensitive.apuMaster"),
        (new(@"TRANSPONDER_(MODE|STATE)|XPNDR_?MODE", RegexOptions.IgnoreCase), ["xpdr.mode"], "input.sensitive.xpdrMode"),
        (new(@"ATC_ALT|ALT_?RPTG|ALT_?REPORT", RegexOptions.IgnoreCase), ["xpdr.altReporting"], "input.sensitive.altReporting"),
        (new(@"TCAS_(POSITION|MODE)", RegexOptions.IgnoreCase), ["tcas.mode"], "input.sensitive.tcasMode"),
        (new(@"XPNDR_SET|XPNDR_CODE|TRANSPONDER_CODE", RegexOptions.IgnoreCase), ["xpdr.code"], "input.sensitive.squawk"),
        (new(@"FCU_AP_?\d|(?<![A-Z])AP_?\d_(PUSH|DISC)", RegexOptions.IgnoreCase), ["fcu.ap1", "fcu.ap2"], "input.sensitive.autopilot"),
        (new(@"ATHR|AUTOTHRUST|AUTO_?THROTTLE", RegexOptions.IgnoreCase), ["fcu.athr"], "input.sensitive.autothrust"),
        (new(@"FCU_ALT_(SET|INC|DEC)(?![A-Z])", RegexOptions.IgnoreCase), ["fcu.altitude"], "input.sensitive.fcuAltitude"),
        (new(@"ADIRS|ADIRU|(?<![A-Z])IR_?\d_?MODE", RegexOptions.IgnoreCase), ["adirs.*"], "input.sensitive.adirs"),
    ];

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
            return Loc.T("input.safety.eventNoName");
        }

        var n = name.Trim();
        if (n.StartsWith("K:", StringComparison.OrdinalIgnoreCase))
        {
            n = n[2..].Trim();
        }

        if (ForbiddenEvent().IsMatch(Flat(n)))
        {
            return Loc.T("input.safety.eventForbidden", n);
        }

        if (!AllowedEvents.Contains(n) && !AllowedEventPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return Loc.T("input.safety.eventNotAllowed", n);
        }

        if (n.Equals("ELECTRICAL_CIRCUIT_TOGGLE", StringComparison.OrdinalIgnoreCase) && !AllowedCircuits.Contains(data))
        {
            return Loc.T("input.safety.circuitNotAllowed", data);
        }

        return null;
    }

    /// <summary>Grund, warum eine Variable nicht geschrieben werden darf, sonst null. Nur L:-Variablen.</summary>
    public static string? VariableProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Loc.T("input.safety.variableNoName");
        }

        var n = name.Trim();
        if (!n.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("input.safety.onlyLVariables", n);
        }

        return ForbiddenVariable().IsMatch(Flat(n[2..]))
            ? Loc.T("input.safety.variableForbidden", n)
            : null;
    }

    /// <summary>Gesperrter Bereich oder gesperrte ID (Triebwerk, Treibstoff, Feuer, Hydraulik, IDG), sonst null.</summary>
    public static string? ForbiddenActionProblem(ProfileAction action)
    {
        if (!string.IsNullOrWhiteSpace(action.Area) && ForbiddenAreas.Contains(action.Area.Trim()))
        {
            return Loc.T("input.safety.areaForbidden", action.Area);
        }

        return !string.IsNullOrWhiteSpace(action.Id) && ForbiddenId().IsMatch(action.Id.Trim())
            ? Loc.T("input.safety.idForbidden", action.Id)
            : null;
    }

    /// <summary>
    /// Sicherheitsrelevanter Schritt unter einer anderen als der Standard-ID (z. B. Pack-Variable in "air.packs"), sonst null.
    /// </summary>
    public static string? CanonicalIdProblem(ProfileAction action)
    {
        var id = action.Id?.Trim() ?? "";
        foreach (var step in action.Steps)
        {
            var raw = !string.IsNullOrWhiteSpace(step?.Event) ? step.Event : step?.Set;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = Flat(StripPrefix(raw));
            foreach (var (pattern, ids, what) in Sensitive)
            {
                if (pattern.IsMatch(name) && !ids.Any(allowed => IdMatches(id, allowed)))
                {
                    return Loc.T("input.safety.canonicalId", raw.Trim(), Loc.T(what), string.Join(" / ", ids));
                }
            }
        }

        return null;
    }

    /// <summary>"adirs.*" passt auf jede ID mit diesem Präfix.</summary>
    private static bool IdMatches(string id, string allowed) =>
        allowed.EndsWith(".*", StringComparison.Ordinal)
            ? id.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase)
            : id.Equals(allowed, StringComparison.OrdinalIgnoreCase);

    private static string StripPrefix(string name)
    {
        var n = name.Trim();
        return n.Length > 2 && n[1] == ':' && char.IsLetter(n[0]) ? n[2..].Trim() : n;
    }

    /// <summary>Erstes Problem einer Aktion aus dem Profil (gesperrter Bereich, Schritte, Standard-ID, Pflicht-Sperren), sonst null.</summary>
    public static string? Problem(ProfileAction action)
    {
        if (ForbiddenActionProblem(action) is { } forbidden)
        {
            return forbidden;
        }

        if (CanonicalIdProblem(action) is { } canonical)
        {
            return canonical;
        }

        foreach (var step in action.Steps)
        {
            if (step is null)
            {
                return Loc.T("input.safety.emptyStep");
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
                    return Loc.T("input.safety.interlockRequired", step.Event, needed);
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

/// <summary>Ergebnis einer Sperre: abgelehnt (Refusal), nur mit Rückfrage (Caution) oder erlaubt (beides null).</summary>
public sealed record InterlockOutcome(string? Refusal, SafetyCaution? Caution)
{
    public static readonly InterlockOutcome Allowed = new(null, null);

    public static InterlockOutcome Refuse(string reason) => new(reason, null);

    public static InterlockOutcome Ask(SafetyCaution caution) => new(null, caution);
}

/// <summary>
/// Sperren, die eine Aktion im Profil anfordern kann. Unbekannte Namen sperren die Aktion. Fehlt ein Grenzwert im Profil
/// (VLO/VLE, VFE der Stellung), geht die Aktion nur mit Rückfrage – vermerkt im Log.
/// </summary>
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

    /// <summary>Grund für die Sperre, oder null, wenn die Aktion erlaubt ist (Rückfragen zählen hier als erlaubt).</summary>
    public static string? Check(string? name, FlightSnapshot s, AircraftLimits limits, string? option) =>
        Evaluate(name, s, limits, option).Refusal;

    /// <summary>Sperre auswerten: ablehnen, nur mit Rückfrage oder erlaubt.</summary>
    public static InterlockOutcome Evaluate(string? name, FlightSnapshot s, AircraftLimits limits, string? option)
    {
        if (name is null)
        {
            return InterlockOutcome.Refuse(Loc.T("input.interlock.empty"));
        }

        if (name.Equals(NotOnGround, StringComparison.OrdinalIgnoreCase))
        {
            return s.OnGround switch
            {
                true => InterlockOutcome.Refuse(Loc.T("input.interlock.lockedOnGround")),
                null => InterlockOutcome.Refuse(Loc.T("input.interlock.groundUnknown")),
                _ => InterlockOutcome.Allowed,
            };
        }

        if (name.Equals(OnGround, StringComparison.OrdinalIgnoreCase))
        {
            return s.OnGround switch
            {
                true => InterlockOutcome.Allowed,
                false => InterlockOutcome.Refuse(Loc.T("input.interlock.groundOnly")),
                null => InterlockOutcome.Refuse(Loc.T("input.interlock.groundUnknown")),
            };
        }

        if (name.Equals(GearSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true)
            {
                return InterlockOutcome.Allowed;
            }

            if (s.IasKt is not { } ias)
            {
                return InterlockOutcome.Refuse(Loc.T("input.interlock.gearSpeedUnknown"));
            }

            if ((limits.VloKt ?? limits.VleKt) is not { } limit)
            {
                return InterlockOutcome.Ask(new SafetyCaution(
                    "LIMIT_MISSING_VLO", "gear speed limit unknown", Loc.T("input.interlock.vloMissing"), MissingLimit: true));
            }

            var label = limits.VloKt is null ? "VLE" : "VLO";
            return ias > limit
                ? InterlockOutcome.Refuse(Loc.T("input.interlock.gearOverspeed", ias, label, limit))
                : InterlockOutcome.Allowed;
        }

        if (name.Equals(FlapsSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (s.OnGround == true || option is "0")
            {
                return InterlockOutcome.Allowed;
            }

            if (option is null)
            {
                return InterlockOutcome.Refuse(Loc.T("input.interlock.flapsPositionMissing"));
            }

            if (s.IasKt is not { } ias)
            {
                return InterlockOutcome.Refuse(Loc.T("input.interlock.flapsSpeedUnknown"));
            }

            if (limits.VfeFor(null, option) is not { } vfe)
            {
                return InterlockOutcome.Ask(new SafetyCaution(
                    "LIMIT_MISSING_VFE", "flap speed limit unknown", Loc.T("input.interlock.vfeMissing", option), MissingLimit: true));
            }

            return ias > vfe
                ? InterlockOutcome.Refuse(Loc.T("input.interlock.flapsOverspeed", option, ias, vfe))
                : InterlockOutcome.Allowed;
        }

        return InterlockOutcome.Refuse(Loc.T("input.interlock.unknown", name));
    }
}
