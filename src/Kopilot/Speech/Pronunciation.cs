using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kopilot.FlightPlan;

namespace Kopilot.Speech;

/// <summary>Ein Fix des OFP-Navlogs für die Aussprache: Kennung, Name (Funkfeuer: "WABUSH") und Lage.</summary>
public sealed record SpeechFix(string Ident, string? Name, double? Lat, double? Lon);

/// <summary>
/// Was die Aussprache vom aktuellen Flug weiß: die Fixe des SimBrief-Navlogs (Funkfeuer mit Namen), die Flughäfen des Plans
/// (ICAO-Codes, die buchstabiert werden) und SID/STAR. <see cref="Current"/> setzt der <see cref="SimBriefService"/>,
/// sobald ein OFP geladen ist.
/// </summary>
public sealed class SpeechContext
{
    public static readonly SpeechContext Empty = new(new Dictionary<string, SpeechFix>(), new HashSet<string>(), new HashSet<string>());

    private static volatile SpeechContext _current = Empty;

    public SpeechContext(IReadOnlyDictionary<string, SpeechFix> fixes, IReadOnlySet<string> airports, IReadOnlySet<string> procedures)
    {
        Fixes = fixes;
        Airports = airports;
        Procedures = procedures;
    }

    /// <summary>Aussprache-Kontext des geladenen SimBrief-Plans (leer ohne Plan).</summary>
    public static SpeechContext Current
    {
        get => _current;
        set => _current = value ?? Empty;
    }

    /// <summary>Fixe des Navlogs nach Kennung (Großbuchstaben).</summary>
    public IReadOnlyDictionary<string, SpeechFix> Fixes { get; }

    /// <summary>ICAO-Codes von Start, Ziel und Ausweichflughafen.</summary>
    public IReadOnlySet<string> Airports { get; }

    /// <summary>SID und STAR des Plans ("ANJLL4").</summary>
    public IReadOnlySet<string> Procedures { get; }

    public static SpeechContext From(Ofp? ofp)
    {
        if (ofp is null)
        {
            return Empty;
        }

        var fixes = new Dictionary<string, SpeechFix>(StringComparer.Ordinal);
        foreach (var fix in ofp.Fixes)
        {
            var ident = fix.Ident.Trim().ToUpperInvariant();
            if (ident.Length > 0 && fix.Type != "apt" && ident is not ("TOC" or "TOD"))
            {
                fixes.TryAdd(ident, new SpeechFix(ident, fix.Name, fix.Lat, fix.Lon));
            }
        }

        var airports = new[] { ofp.Origin.Icao, ofp.Destination.Icao, ofp.Alternate?.Icao }
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var procedures = new[] { ofp.SidIdent, ofp.StarIdent }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return new SpeechContext(fixes, airports, procedures);
    }
}

/// <summary>
/// Die eine Aussprache für alles, was Kopilot spricht – ATC, Callouts, Fuel Check und Antworten der Sprachschleife:
/// Ziffern ICAO-gemäß ("niner"), Höhen in Tausendern, Flugflächen ("flight level three four zero", "flight level one
/// hundred"), Bahnen ("two five right"), Squawks und Frequenzen Ziffer für Ziffer, ICAO-Codes und kurze Kennungen im
/// NATO-Alphabet, fünfbuchstabige Fixe als Wort, Funkfeuer mit dem Namen aus dem OFP-Navlog ("Wabush" statt YWK) und
/// Koordinatenpunkte als "fifty three north fifty west". <see cref="ForSpeech"/> bereitet fertigen Text vor der
/// Sprachausgabe auf (auch ECAM-Titel wie "HYD G RSVR LO PR").
/// </summary>
public static partial class Pronunciation
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    private static readonly string[] NatoWords =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
        "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "x-ray", "yankee", "zulu",
    ];

    private static readonly string[] Small =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen",
        "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] TensNames = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    // ------------------------------------------------------------------ Bausteine

    /// <summary>Eine Ziffer, 9 als "niner".</summary>
    public static string Digit(char c) => DigitWords[c - '0'];

    /// <summary>Buchstabe im NATO-Alphabet.</summary>
    public static string Letter(char c) => NatoWords[char.ToUpperInvariant(c) - 'A'];

    /// <summary>Ziffer für Ziffer: "250" → "two five zero", "121.8" → "one two one decimal eight", "-5" → "minus five".</summary>
    public static string Digits(string digits)
    {
        var parts = new List<string>(digits.Length);
        foreach (var c in digits)
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(Digit(c));
            }
            else if (c == '.')
            {
                parts.Add("decimal");
            }
            else if (c == '-')
            {
                parts.Add("minus");
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>Zahl Ziffer für Ziffer, mit führenden Nullen bis <paramref name="minDigits"/>: 5 → "zero five" bei 2.</summary>
    public static string Digits(long value, int minDigits = 1) =>
        Digits(Math.Abs(value).ToString(new string('0', Math.Max(1, minDigits)), Inv));

    /// <summary>Buchstaben im NATO-Alphabet, Ziffern einzeln: "4AB" → "four alpha bravo", "EDDL" → "echo delta delta lima".</summary>
    public static string Spell(string text)
    {
        var parts = new List<string>(text.Length);
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(Digit(c));
            }
            else if (char.IsAsciiLetter(c))
            {
                parts.Add(Letter(c));
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>Natürliche englische Zahl: 25 → "twenty five", 120 → "one hundred twenty", 53 → "fifty three".</summary>
    public static string Natural(long n)
    {
        if (n < 0)
        {
            return "minus " + Natural(-n);
        }

        if (n < 20)
        {
            return Small[n];
        }

        if (n < 100)
        {
            return TensNames[n / 10] + (n % 10 == 0 ? "" : " " + Small[n % 10]);
        }

        if (n < 1000)
        {
            return Small[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + Natural(n % 100));
        }

        if (n < 1_000_000)
        {
            return Natural(n / 1000) + " thousand" + (n % 1000 == 0 ? "" : " " + Natural(n % 1000));
        }

        return Digits(n.ToString(Inv));
    }

    /// <summary>
    /// Höhe nach ICAO, ohne Einheit: Tausender Ziffer für Ziffer ("one zero thousand", "niner thousand"), Hunderter mit
    /// "hundred" ("two thousand five hundred"), ein Rest natürlich.
    /// </summary>
    public static string Thousands(long feet)
    {
        var sign = feet < 0 ? "minus " : "";
        feet = Math.Abs(feet);
        var thousands = feet / 1000;
        var hundreds = feet % 1000 / 100;
        var rest = feet % 100;
        var parts = new List<string>(3);
        if (thousands > 0)
        {
            parts.Add(Digits(thousands) + " thousand");
        }

        if (hundreds > 0)
        {
            parts.Add(Digit((char)('0' + hundreds)) + " hundred");
        }

        if (rest > 0 || parts.Count == 0)
        {
            parts.Add(Natural(rest));
        }

        return sign + string.Join(' ', parts);
    }

    /// <summary>Höhe in Fuß: 5000 → "five thousand feet".</summary>
    public static string Feet(long feet) => Thousands(feet) + " feet";

    /// <summary>Flugfläche nach ICAO: 340 → "flight level three four zero", 100 → "flight level one hundred", 70 → "flight level seven zero".</summary>
    public static string FlightLevel(int level) =>
        "flight level " + (level > 0 && level % 100 == 0 ? Digit((char)('0' + (level / 100 % 10))) + " hundred" : Digits(level, 2));

    /// <summary>Bahn: 25, "R" → "two five right", 9 → "zero niner".</summary>
    public static string Runway(int number, string? designator)
    {
        var spoken = Digits(number, 2);
        return designator?.Trim().ToUpperInvariant() switch
        {
            "L" => spoken + " left",
            "R" => spoken + " right",
            "C" => spoken + " center",
            _ => spoken,
        };
    }

    /// <summary>Squawk Ziffer für Ziffer: "2340" → "two three four zero".</summary>
    public static string Squawk(string code) => Digits(code);

    /// <summary>Frequenz: 132.25 → "one three two decimal two five", 119.0 → "one one niner decimal zero".</summary>
    public static string Frequency(double mhz)
    {
        var khz = (long)Math.Round(mhz * 1000, MidpointRounding.AwayFromZero);
        var fraction = (khz % 1000).ToString("000", Inv).TrimEnd('0');
        return $"{Digits(khz / 1000)} decimal {Digits(fraction.Length == 0 ? "0" : fraction)}";
    }

    /// <summary>Kurs dreistellig: 90 → "zero niner zero".</summary>
    public static string Heading(int degrees) => Digits(degrees, 3);

    /// <summary>Koordinatenpunkt: 53°N 50°W → "fifty three north fifty west", 55°30'N → "fifty five thirty north".</summary>
    public static string Coordinate(double lat, double lon)
    {
        static string Part(double value, string positive, string negative)
        {
            var minutes = (int)Math.Round(Math.Abs(value) * 60, MidpointRounding.AwayFromZero);
            var deg = minutes / 60;
            var min = minutes % 60;
            return Natural(deg) + (min > 0 ? " " + (min < 10 ? "zero " + Natural(min) : Natural(min)) : "") + " " + (value >= 0 ? positive : negative);
        }

        return Part(lat, "north", "south") + " " + Part(lon, "east", "west");
    }

    /// <summary>ICAO-Code eines Flughafens – immer buchstabiert: "KLAX" → "kilo lima alpha x-ray".</summary>
    public static string Airport(string icao) => Spell(icao.Trim().ToUpperInvariant());

    /// <summary>
    /// Wegpunkt: Koordinatenpunkte als Lage ("fifty three north fifty west"), fünf und mehr Buchstaben als Wort ("Hendy"),
    /// vier Buchstaben (ICAO-Code) buchstabiert, Funkfeuer mit bis zu drei Buchstaben mit dem Namen aus dem Navlog
    /// ("Wabush"), sonst im NATO-Alphabet. Ohne Namen wird der Navlog des geladenen Plans befragt.
    /// </summary>
    public static string Waypoint(string ident, string? name = null, double? lat = null, double? lon = null, SpeechContext? context = null)
    {
        var code = Compact(ident);
        if (code.Length == 0)
        {
            return ident.Trim();
        }

        var known = (context ?? SpeechContext.Current).Fixes.GetValueOrDefault(code);
        name ??= known?.Name;
        lat ??= known?.Lat;
        lon ??= known?.Lon;

        // Koordinatenpunkt ("55N020W", "5350N") oder halber Grad ("H5330"): aus der Lage
        if (code.Count(char.IsAsciiDigit) >= 4)
        {
            if (lat is { } la && lon is { } lo)
            {
                return Coordinate(la, lo);
            }

            return ParseCoordinate(code) is { } parsed ? Coordinate(parsed.Lat, parsed.Lon) : Spell(code);
        }

        if (!code.All(char.IsAsciiLetter))
        {
            return Spell(code);
        }

        if (code.Length >= 5)
        {
            return TitleCase(code);
        }

        if (code.Length == 4)
        {
            return Spell(code);
        }

        return NavaidName(code, name) ?? Spell(code);
    }

    /// <summary>
    /// Kennung im Funk: Verfahren als Name plus Ziffer und Buchstabe ("MARUN7G" → "Marun seven golf"), sonst wie
    /// <see cref="Waypoint"/> (Rollwege, Haltepunkte und Gates mit Ziffern werden buchstabiert: "A26" → "alpha two six").
    /// </summary>
    public static string Ident(string ident, SpeechContext? context = null)
    {
        var code = Compact(ident);
        if (ProcedureRegex().Match(code) is { Success: true } m)
        {
            return TitleCase(m.Groups[1].Value) + " " + Digit(m.Groups[2].Value[0]) + (m.Groups[3].Success ? " " + Letter(m.Groups[3].Value[0]) : "");
        }

        return Waypoint(code, context: context);
    }

    /// <summary>Kennung als Code: nur Buchstaben und Ziffern, groß.</summary>
    public static string Compact(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    public static string TitleCase(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    /// <summary>Koordinaten-Kennung wie "55N020W", "5520N", "N55W020", "5530N02000W" → Lage, sonst null.</summary>
    public static (double Lat, double Lon)? ParseCoordinate(string ident)
    {
        var code = Compact(ident);
        if (FullCoordinate().Match(code) is { Success: true } full)
        {
            var lat = Degrees(full.Groups["lat"].Value) * (full.Groups["ns"].Value == "S" ? -1 : 1);
            var lon = Degrees(full.Groups["lon"].Value) * (full.Groups["ew"].Value == "W" ? -1 : 1);
            return (lat, lon);
        }

        if (PrefixCoordinate().Match(code) is { Success: true } prefix)
        {
            var lat = Degrees(prefix.Groups["lat"].Value) * (prefix.Groups["ns"].Value == "S" ? -1 : 1);
            var lon = Degrees(prefix.Groups["lon"].Value) * (prefix.Groups["ew"].Value == "W" ? -1 : 1);
            return (lat, lon);
        }

        // ARINC 424, fünf Zeichen: "5520N" = 55N 20W, "5520E" = 55N 20E, "5520S" = 55S 20E, "5520W" = 55S 20W; Länge ab 100° mit
        // dem Buchstaben in der Mitte ("55N20" = 55N 120W)
        if (ShortCoordinate().Match(code) is { Success: true } shortCode)
        {
            var a = int.Parse(shortCode.Groups["a"].Value, Inv);
            var b = int.Parse(shortCode.Groups["b"].Value, Inv);
            return shortCode.Groups["q"].Value switch
            {
                "N" => (a, -b),
                "E" => (a, b),
                "S" => (-a, b),
                _ => (-a, -b),
            };
        }

        if (ShortCoordinateHundred().Match(code) is { Success: true } hundred)
        {
            var a = int.Parse(hundred.Groups["a"].Value, Inv);
            var b = 100 + int.Parse(hundred.Groups["b"].Value, Inv);
            return hundred.Groups["q"].Value switch
            {
                "N" => (a, -b),
                "E" => (a, b),
                "S" => (-a, b),
                _ => (-a, -b),
            };
        }

        return null;
    }

    private static double Degrees(string text)
    {
        // "55" → 55, "5530" → 55°30', "020" → 20, "02030" → 20°30'
        var degreeDigits = text.Length is 2 or 4 ? 2 : 3;
        var deg = int.Parse(text[..Math.Min(degreeDigits, text.Length)], Inv);
        var min = text.Length > degreeDigits ? int.Parse(text[degreeDigits..], Inv) : 0;
        return deg + (min / 60.0);
    }

    /// <summary>Name eines Funkfeuers aus dem Navlog ("WABUSH" → "Wabush", "GOOSE BAY" → "Goose Bay"), null wenn unbrauchbar.</summary>
    private static string? NavaidName(string ident, string? name)
    {
        var trimmed = name?.Trim();
        if (trimmed is not { Length: >= 3 and <= 24 } || trimmed.Equals(ident, StringComparison.OrdinalIgnoreCase)
            || !trimmed.All(c => char.IsAsciiLetter(c) || c is ' ' or '-'))
        {
            return null;
        }

        return Inv.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
    }

    [GeneratedRegex(@"^([A-Z]{2,6})(\d)([A-Z])?$")]
    private static partial Regex ProcedureRegex();

    [GeneratedRegex(@"^(?<lat>\d{2}|\d{4})(?<ns>[NS])(?<lon>\d{3}|\d{5})(?<ew>[EW])$")]
    private static partial Regex FullCoordinate();

    [GeneratedRegex(@"^(?<ns>[NS])(?<lat>\d{2}|\d{4})(?<ew>[EW])(?<lon>\d{3}|\d{5})$")]
    private static partial Regex PrefixCoordinate();

    [GeneratedRegex(@"^(?<a>\d{2})(?<b>\d{2})(?<q>[NESW])$")]
    private static partial Regex ShortCoordinate();

    [GeneratedRegex(@"^(?<a>\d{2})(?<q>[NESW])(?<b>\d{2})$")]
    private static partial Regex ShortCoordinateHundred();
}
