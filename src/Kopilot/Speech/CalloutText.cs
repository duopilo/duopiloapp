using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Speech;

/// <summary>
/// Bereitet Callouts zum Sprechen auf. Die Regeln liefern kurze englische Sätze; heikel sind nur die ECAM-Titel
/// in Großbuchstaben ("ENG 1 FIRE", "HYD G RSVR LO PR"). Die würde die Sprachsynthese buchstabieren oder raten –
/// deshalb Abkürzungen ausschreiben, echte Initialwörter buchstabieren und den Rest kleinschreiben.
/// </summary>
public static partial class CalloutText
{
    /// <summary>Feste Callouts der Monitoring-Regeln – werden nach dem Laden vorab synthetisiert, damit sie ohne Verzögerung kommen.</summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "Gear speed", "Flap speed", "Overspeed", "Stall", "Sink rate", "Bank angle", "Master warning", "Master caution",
        "Gear still down", "Speed limit", "Speed", "Autopilot off", "One thousand to go", "Sim paused", "Callouts on",
    ];

    /// <summary>Test-Ansage aus Dashboard und Tray.</summary>
    public const string TestPhrase = "Runway two three left assigned";

    // Ganze Wendungen mit Schrägstrich oder Punkt, vor dem Zerlegen in Wörter
    private static readonly (string From, string To)[] Phrases =
    [
        ("A/THR", "auto thrust"),
        ("F/CTL", "flight controls"),
        ("L/G", "landing gear"),
        ("T.O", "takeoff"),
        ("A/SKID", "anti skid"),
        ("N/W", "nose wheel"),
    ];

    private static readonly Dictionary<string, string> Expansions = new(StringComparer.Ordinal)
    {
        ["ENG"] = "engine",
        ["LDG"] = "landing",
        ["ENGS"] = "engines",
        ["HYD"] = "hydraulic",
        ["ELEC"] = "electrical",
        ["GEN"] = "generator",
        ["GENS"] = "generators",
        ["PRESS"] = "pressure",
        ["PR"] = "pressure",
        ["LO"] = "low",
        ["HI"] = "high",
        ["RSVR"] = "reservoir",
        ["OVHT"] = "overheat",
        ["TEMP"] = "temperature",
        ["CTL"] = "control",
        ["ALTN"] = "alternate",
        ["CAB"] = "cabin",
        ["ALT"] = "altitude",
        ["AP"] = "autopilot",
        ["ATHR"] = "auto thrust",
        ["LG"] = "landing gear",
        ["XFR"] = "transfer",
        ["SPD"] = "speed",
        ["STAB"] = "stabilizer",
        ["THR"] = "thrust",
        ["LVR"] = "lever",
        ["LVRS"] = "levers",
        ["EMER"] = "emergency",
        ["AVNCS"] = "avionics",
        ["FWD"] = "forward",
        ["VENT"] = "ventilation",
        ["COND"] = "conditioning",
        ["SYS"] = "system",
        ["BAT"] = "battery",
        ["FLT"] = "flight",
        ["CTR"] = "center",
        ["INR"] = "inner",
        ["OUTR"] = "outer",
        ["TK"] = "tank",
        ["QTY"] = "quantity",
        ["IMBAL"] = "imbalance",
        ["PB"] = "pushbutton",
        ["DISCH"] = "discharge",
        ["AGENT"] = "agent",
        ["ECAM"] = "ee cam",
        ["TCAS"] = "tee cas",
        ["ACARS"] = "ay cars",
        ["TOGA"] = "toga",
        ["FLX"] = "flex",
        ["MCT"] = "max continuous",
        ["CONFIG"] = "config",
    };

    // Werden Buchstabe für Buchstabe gesprochen
    private static readonly HashSet<string> Spelled = new(StringComparer.Ordinal)
    {
        "APU", "ADR", "IR", "ILS", "GPS", "GPWS", "EGPWS", "FMGC", "FMS", "FCU", "MCDU", "ATC", "VHF", "HF", "CVR", "FWC",
        "SDAC", "BSCU", "DMC", "RA", "IAS", "ND", "PFD", "LGCIU", "BMC", "CIDS", "FADEC", "EIU", "FCDC", "SFCC", "WAI", "EAI",
    };

    // Farben der Hydrauliksysteme nach "HYD"
    private static readonly Dictionary<string, string> HydraulicColors = new(StringComparer.Ordinal)
    {
        ["G"] = "green",
        ["B"] = "blue",
        ["Y"] = "yellow",
    };

    /// <summary>Callout → Text für die Sprachsynthese. Normale Sätze ("Gear speed") bleiben unverändert.</summary>
    public static string ForSpeech(string callout)
    {
        if (string.IsNullOrWhiteSpace(callout))
        {
            return "";
        }

        var text = callout.Trim();
        foreach (var (from, to) in Phrases)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        var sb = new StringBuilder(text.Length + 16);
        string? previous = null;
        foreach (Match m in TokenRegex().Matches(text))
        {
            var token = m.Value;
            if (m.Groups["word"].Success)
            {
                token = Word(token, previous);
                previous = m.Value;
            }
            else if (!char.IsWhiteSpace(token[0]))
            {
                previous = null;
            }

            sb.Append(token);
        }

        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    private static string Word(string word, string? previous)
    {
        // Nur reine Großbuchstaben-Wörter anfassen – "Master warning" oder "Runway" bleiben, wie sie sind
        if (!IsUpper(word))
        {
            return word;
        }

        if (previous == "HYD" && HydraulicColors.TryGetValue(word, out var color))
        {
            return color;
        }

        if (word.Length == 1)
        {
            return word;
        }

        if (Expansions.TryGetValue(word, out var expanded))
        {
            return expanded;
        }

        if (Spelled.Contains(word))
        {
            return string.Join(' ', word.ToCharArray());
        }

        return word.ToLowerInvariant();
    }

    private static bool IsUpper(string word)
    {
        foreach (var c in word)
        {
            if (!char.IsUpper(c))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"(?<word>\p{L}+)|\s+|[^\p{L}\s]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
