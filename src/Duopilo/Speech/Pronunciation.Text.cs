using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Duopilo.Speech;

/// <summary>
/// Fertigen Text für die Sprachausgabe aufbereiten – der letzte Schritt vor der TTS für Callouts, Fuel Check, Antworten
/// der Sprachschleife und den festen Text der ATC-Vorlagen.
/// </summary>
public static partial class Pronunciation
{
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

    // ECAM-Abkürzungen, ausgeschrieben
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
        ["STD"] = "standard",
        ["FL"] = "flight level",
    };

    // Abkürzungen, die Buchstabe für Buchstabe gesprochen werden (Cockpit und Funk)
    private static readonly HashSet<string> Spelled = new(StringComparer.Ordinal)
    {
        "APU", "ADR", "IR", "ILS", "GPS", "GPWS", "EGPWS", "FMGC", "FMS", "FCU", "MCDU", "ATC", "VHF", "HF", "CVR", "FWC",
        "SDAC", "BSCU", "DMC", "RA", "IAS", "ND", "PFD", "LGCIU", "BMC", "CIDS", "FADEC", "EIU", "FCDC", "SFCC", "WAI", "EAI",
        "QNH", "LS", "FD", "TA", "VS", "IFR", "VFR", "RNP", "VOR", "NDB", "DME", "ETA", "FIR", "SID", "LOC",
        "SRS", "FPA",
    };

    // Farben der Hydrauliksysteme nach "HYD"
    private static readonly Dictionary<string, string> HydraulicColors = new(StringComparer.Ordinal)
    {
        ["G"] = "green",
        ["B"] = "blue",
        ["Y"] = "yellow",
    };

    // Danach wird eine Zahl Ziffer für Ziffer gesprochen
    private static readonly HashSet<string> DigitwiseAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        "speed", "heading", "squawk", "squawking", "qnh", "baro", "transponder", "code", "course", "runway", "hdg", "spd", "altimeter",
        // Uhrzeit nach "ETA" ("ETA 1400" → "one four zero zero") und Windrichtung ("Wind 300 at 15" → "three zero zero")
        "eta", "wind",
    };

    // Davor wird eine Zahl natürlich gesprochen ("850 kilos" → "eight hundred fifty kilos")
    private static readonly HashSet<string> NaturalBefore = new(StringComparer.OrdinalIgnoreCase)
    {
        "kilos", "kilo", "kg", "tons", "tonnes", "percent", "minutes", "minute", "seconds", "second", "miles", "mile", "nm", "passengers",
    };

    // Bleiben, wie sie sind (die Sprachsynthese spricht sie richtig)
    private static readonly HashSet<string> Verbatim = new(StringComparer.Ordinal) { "V1", "V2", "VR" };

    /// <summary>
    /// Text → Sprechtext. Zahlen nach ICAO bzw. natürlich je nach Zusammenhang ("Speed 250" → "Speed two five zero",
    /// "Altitude 12500" → "one two thousand five hundred", "FL350", "runway 25R", "121.800" als Frequenz), Koordinaten-Kennungen
    /// als Lage, ECAM-Abkürzungen ausgeschrieben bzw. buchstabiert, übrige Großbuchstaben-Wörter klein ("FIRE" → "fire").
    /// Mit dem Plan im <paramref name="context"/> (sonst <see cref="SpeechContext.Current"/>) werden Fixe des Navlogs wie in
    /// <see cref="Waypoint"/> und die Flughäfen des Plans buchstabiert gesprochen. Leerzeichen und Satzzeichen bleiben;
    /// bereits aufbereiteter Text bleibt unverändert.
    /// </summary>
    public static string ForSpeech(string? text, SpeechContext? context = null) =>
        string.IsNullOrWhiteSpace(text) ? "" : MultiSpace().Replace(Process(text, context ?? SpeechContext.Current), " ").Trim();

    /// <summary>
    /// Wie <see cref="ForSpeech"/>, aber für ein Stück Text zwischen zwei Werten (fester Text einer ATC-Vorlage wie ", climb "):
    /// Leerzeichen am Rand bleiben stehen.
    /// </summary>
    public static string Fragment(string text, SpeechContext? context = null) =>
        string.IsNullOrWhiteSpace(text) ? text : Process(text, context ?? SpeechContext.Current);

    private static string Process(string text, SpeechContext context)
    {
        var source = text;
        foreach (var (from, to) in Phrases)
        {
            source = source.Replace(from, to, StringComparison.Ordinal);
        }

        var tokens = TokenRegex().Matches(source).Select(m => m.Value).ToList();
        var words = Enumerable.Range(0, tokens.Count).Where(i => IsWord(tokens[i])).ToList();
        var sb = new StringBuilder(source.Length * 2);
        var w = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (w < words.Count && words[w] == i)
            {
                var previous = w > 0 ? tokens[words[w - 1]] : null;
                var beforePrevious = w > 1 ? tokens[words[w - 2]] : null;
                var next = w + 1 < words.Count ? tokens[words[w + 1]] : null;
                sb.Append(Word(tokens[i], previous, beforePrevious, next, context));
                w++;
            }
            else
            {
                sb.Append(tokens[i]);
            }
        }

        return sb.ToString();
    }

    private static bool IsWord(string token) => token.Length > 0 && (char.IsLetterOrDigit(token[0]) || (token[0] == '-' && token.Length > 1));

    private static string Word(string word, string? previous, string? beforePrevious, string? next, SpeechContext context)
    {
        if (Verbatim.Contains(word))
        {
            return word;
        }

        // Koordinatenpunkt ("53N050W", "5350N")
        if (word.Any(char.IsAsciiDigit) && word.Any(char.IsAsciiLetter) && word.Count(char.IsAsciiDigit) >= 4 && word.All(c => char.IsAsciiDigit(c) || char.IsUpper(c)))
        {
            if (context.Fixes.TryGetValue(word, out var fix))
            {
                return Waypoint(word, fix.Name, fix.Lat, fix.Lon, context);
            }

            if (ParseCoordinate(word) is { } position)
            {
                return Coordinate(position.Lat, position.Lon);
            }
        }

        if (FlightLevelRegex().Match(word) is { Success: true } fl)
        {
            return FlightLevel(int.Parse(fl.Groups[1].Value, Inv));
        }

        // Bahn nach "runway": "25R" → "two five right"
        if (previous is not null && previous.ToLowerInvariant() is "runway" or "rwy" && RunwayRegex().Match(word) is { Success: true } rwy
            && int.Parse(rwy.Groups[1].Value, Inv) is >= 1 and <= 36)
        {
            return Runway(int.Parse(rwy.Groups[1].Value, Inv), rwy.Groups[2].Success ? rwy.Groups[2].Value : null);
        }

        if (DecimalNumber().IsMatch(word))
        {
            return Decimal(word, previous, next);
        }

        if (IntegerNumber().IsMatch(word))
        {
            return Integer(word, previous, beforePrevious, next);
        }

        if (word.All(char.IsUpper) && word.All(char.IsLetter))
        {
            return UpperWord(word, previous, context);
        }

        // Kennung mit Ziffern aus dem Plan: SID/STAR ("ANJLL4"), Fix des Navlogs
        if (word.All(c => char.IsUpper(c) || char.IsAsciiDigit(c)))
        {
            if (context.Procedures.Contains(word))
            {
                return Ident(word, context);
            }

            if (context.Fixes.TryGetValue(word, out var fix))
            {
                return Waypoint(word, fix.Name, fix.Lat, fix.Lon, context);
            }
        }

        return word;
    }

    /// <summary>Wort in Großbuchstaben: ECAM-Abkürzung, buchstabierte Abkürzung, Fix oder Flughafen des Plans, sonst klein.</summary>
    private static string UpperWord(string word, string? previous, SpeechContext context)
    {
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

        if (context.Airports.Contains(word))
        {
            return Airport(word);
        }

        if (context.Fixes.TryGetValue(word, out var fix))
        {
            return Waypoint(word, fix.Name, fix.Lat, fix.Lon, context);
        }

        return word.ToLowerInvariant();
    }

    private static string Integer(string word, string? previous, string? beforePrevious, string? next)
    {
        var unsigned = word.TrimStart('-');
        var negative = word.StartsWith('-');

        // "Vertical speed -1000" wie eine Höhe ("minus one thousand"), "Speed 250" Ziffer für Ziffer
        var verticalSpeed = string.Equals(beforePrevious, "vertical", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(previous, "vs", StringComparison.OrdinalIgnoreCase);
        var natural = next is not null && NaturalBefore.Contains(next);
        if (!verticalSpeed && !natural && ((previous is not null && DigitwiseAfter.Contains(previous)) || (unsigned.Length > 1 && unsigned[0] == '0')))
        {
            return Digits(word);
        }

        if (!long.TryParse(unsigned, NumberStyles.None, Inv, out var n))
        {
            return Digits(word);
        }

        var spoken = n switch
        {
            _ when natural => Natural(n),
            >= 100 when n % 100 == 0 => Thousands(n),
            < 100 => Natural(n),
            _ => Digits(unsigned),
        };

        return negative ? "minus " + spoken : spoken;
    }

    private static string Decimal(string word, string? previous, string? next)
    {
        var negative = word.StartsWith('-');
        var parts = word.TrimStart('-').Split('.');
        var whole = parts[0];
        var fraction = parts[1].TrimEnd('0');
        if (fraction.Length == 0)
        {
            fraction = "0";
        }

        // Frequenz nach "standby", "contact" … oder im Flugfunkband – nicht bei Mengen ("fuel 121.4", "121.4 tons")
        var amount = (previous is not null && previous.ToLowerInvariant() is "fuel" or "minimum" or "by" or "plan")
                     || (next is not null && NaturalBefore.Contains(next));
        var frequency = (previous is not null && previous.ToLowerInvariant() is "standby" or "active" or "frequency" or "com" or "contact")
                        || (!amount && int.TryParse(whole, NumberStyles.None, Inv, out var w) && w is >= 108 and <= 137);
        var spoken = frequency
            ? Digits(whole) + " decimal " + Digits(fraction)
            : Natural(long.Parse(whole, Inv)) + " point " + Digits(fraction);
        return negative ? "minus " + spoken : spoken;
    }

    // Wörter (auch "x-ray", "121.800", "-1000", "55N020W"), Leerraum und übrige Zeichen
    [GeneratedRegex(@"(?:(?<=^|\s)-(?=\d))?[\p{L}\p{N}]+(?:[.\-'][\p{L}\p{N}]+)*|\s+|[^\p{L}\p{N}\s]")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^FL(\d{2,3})$")]
    private static partial Regex FlightLevelRegex();

    [GeneratedRegex(@"^(\d{1,2})([LRC])?$")]
    private static partial Regex RunwayRegex();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex DecimalNumber();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerNumber();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultiSpace();
}
