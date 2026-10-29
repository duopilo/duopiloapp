using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Voice;

/// <summary>
/// Zahlen so, wie sie im Funk und im Cockpit gesprochen werden (englisch):
/// Speed, Heading, Squawk, QNH Ziffer für Ziffer ("two five zero"), Höhen in Tausendern ("five thousand", "one zero thousand"),
/// Flugflächen als "flight level three five zero", Frequenzen mit "decimal", 9 als "niner".
/// </summary>
public static partial class VoicePhraseology
{
    private static readonly string[] Digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    private static readonly string[] Small =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen",
        "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] TensNames = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    // Danach wird die Zahl Ziffer für Ziffer gesprochen
    private static readonly HashSet<string> DigitwiseAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        "speed", "heading", "squawk", "qnh", "baro", "transponder", "code", "course", "runway", "hdg", "spd",
    };

    private static readonly Dictionary<char, string> NatoLetters = new()
    {
        ['a'] = "alpha", ['b'] = "bravo", ['c'] = "charlie", ['d'] = "delta", ['e'] = "echo", ['f'] = "foxtrot", ['g'] = "golf",
        ['h'] = "hotel", ['i'] = "india", ['j'] = "juliett", ['k'] = "kilo", ['l'] = "lima", ['m'] = "mike", ['n'] = "november",
        ['o'] = "oscar", ['p'] = "papa", ['q'] = "quebec", ['r'] = "romeo", ['s'] = "sierra", ['t'] = "tango", ['u'] = "uniform",
        ['v'] = "victor", ['w'] = "whiskey", ['x'] = "x-ray", ['y'] = "yankee", ['z'] = "zulu",
    };

    /// <summary>Anzeigetext (z. B. Readback "Speed 250 selected") → Text für die Sprachsynthese ("Speed two five zero selected").</summary>
    public static string ForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(text.Length * 2);
        string? previous = null;
        string? beforePrevious = null;
        foreach (var raw in words)
        {
            // Satzzeichen am Wortende erhalten ("Speed 250," → "two five zero,")
            var trail = "";
            var word = raw;
            while (word.Length > 1 && word[^1] is ',' or '.' or '!' or '?')
            {
                trail = word[^1] + trail;
                word = word[..^1];
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Word(word, previous, beforePrevious)).Append(trail);
            beforePrevious = previous;
            previous = word;
        }

        return sb.ToString();
    }

    /// <summary>Ziffer für Ziffer: "250" → "two five zero", "090" → "zero niner zero".</summary>
    public static string DigitByDigit(string digits)
    {
        var parts = new List<string>(digits.Length);
        foreach (var c in digits)
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(Digits[c - '0']);
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

    /// <summary>Rufzeichen und Kennungen buchstabieren: "4AB" → "four alpha bravo".</summary>
    public static string Spell(string text)
    {
        var parts = new List<string>();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(Digits[c - '0']);
            }
            else if (NatoLetters.TryGetValue(c, out var word))
            {
                parts.Add(word);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>Höhe nach ICAO: 5000 → "five thousand", 10000 → "one zero thousand", 4500 → "four thousand five hundred".</summary>
    public static string Altitude(long feet)
    {
        var sign = feet < 0 ? "minus " : "";
        feet = Math.Abs(feet);
        var thousands = feet / 1000;
        var hundreds = feet % 1000 / 100;
        var rest = feet % 100;
        var parts = new List<string>();
        if (thousands > 0)
        {
            parts.Add((thousands >= 10 ? DigitByDigit(thousands.ToString(CultureInfo.InvariantCulture)) : Small[thousands]) + " thousand");
        }

        if (hundreds > 0)
        {
            parts.Add(Small[hundreds] + " hundred");
        }

        if (rest > 0 || parts.Count == 0)
        {
            parts.Add(Natural(rest));
        }

        return sign + string.Join(' ', parts);
    }

    /// <summary>Natürliche englische Zahl bis 999 999: 25 → "twenty five", 120 → "one hundred twenty".</summary>
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

        return DigitByDigit(n.ToString(CultureInfo.InvariantCulture));
    }

    private static string Word(string word, string? previous, string? beforePrevious)
    {
        if (FlightLevel().Match(word) is { Success: true } fl)
        {
            return "flight level " + DigitByDigit(fl.Groups[1].Value);
        }

        if (DecimalNumber().IsMatch(word))
        {
            return Decimal(word, previous);
        }

        if (!IntegerNumber().IsMatch(word))
        {
            return word;
        }

        var unsigned = word.TrimStart('-');
        var negative = word.StartsWith('-');

        // "Vertical speed -1000" wie eine Höhe ("minus one thousand"), "Speed 250" Ziffer für Ziffer
        var verticalSpeed = string.Equals(beforePrevious, "vertical", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(previous, "vs", StringComparison.OrdinalIgnoreCase);
        if (!verticalSpeed && ((previous is not null && DigitwiseAfter.Contains(previous)) || (unsigned.Length > 1 && unsigned[0] == '0')))
        {
            return DigitByDigit(word);
        }

        if (!long.TryParse(unsigned, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return DigitByDigit(word);
        }

        var spoken = n switch
        {
            >= 100 when n % 100 == 0 => Altitude(n),
            < 100 => Natural(n),
            _ => DigitByDigit(unsigned),
        };

        return negative ? "minus " + spoken : spoken;
    }

    private static string Decimal(string word, string? previous)
    {
        var negative = word.StartsWith('-');
        var parts = word.TrimStart('-').Split('.');
        var whole = parts[0];
        var fraction = parts[1].TrimEnd('0');
        if (fraction.Length == 0)
        {
            fraction = "0";
        }

        var frequency = (previous is not null && previous.ToLowerInvariant() is "standby" or "active" or "frequency" or "com" or "contact")
                        || (int.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w is >= 108 and <= 137);
        var spoken = frequency
            ? DigitByDigit(whole) + " decimal " + DigitByDigit(fraction)
            : Natural(long.Parse(whole, CultureInfo.InvariantCulture)) + " point " + DigitByDigit(fraction);
        return negative ? "minus " + spoken : spoken;
    }

    [GeneratedRegex(@"^FL(\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex FlightLevel();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex DecimalNumber();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerNumber();
}
