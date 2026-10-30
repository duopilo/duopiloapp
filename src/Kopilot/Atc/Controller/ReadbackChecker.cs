using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Atc;

public enum ReadbackProblem
{
    /// <summary>Der Wert kommt im Readback nicht vor.</summary>
    Missing,

    /// <summary>Ein anderer Wert wurde zurückgelesen.</summary>
    Wrong,
}

/// <summary>Ein Punkt, den der Pilot zurücklesen muss: Variablenname und erwarteter Wert.</summary>
public sealed record ReadbackItem(string Name, AtcValue Expected);

public sealed record ReadbackIssue(string Item, AtcValue Expected, string? Heard, ReadbackProblem Problem);

public sealed record ReadbackResult(IReadOnlyList<ReadbackIssue> Issues)
{
    public bool Correct => Issues.Count == 0;
}

/// <summary>
/// Prüft das Readback des Piloten gegen die Werte der Freigabe. Versteht Ziffern ("2205") ebenso wie gesprochene Zahlen
/// ("two two zero five", "five thousand", "one two one decimal eight") und das NATO-Alphabet ("seven golf").
/// </summary>
public static class ReadbackChecker
{
    private static readonly string[] SquawkWords = ["squawk", "squawking", "transponder", "code"];
    private static readonly string[] FeetWords = ["feet", "ft", "foot"];
    private static readonly string[] KnotWords = ["knots", "knot", "kts", "kt"];
    private static readonly string[] AltitudeVerbs = ["climb", "climbing", "descend", "descending", "descent", "maintain", "maintaining", "altitude", "cleared"];
    private static readonly string[] Fillers = ["to", "and", "altitude", "now", "via"];

    public static ReadbackResult Check(string transmission, IReadOnlyList<ReadbackItem> items) =>
        Check(HeardText.Parse(transmission), items);

    public static ReadbackResult Check(HeardText heard, IReadOnlyList<ReadbackItem> items)
    {
        var issues = new List<ReadbackIssue>();
        foreach (var item in items)
        {
            if (CheckItem(heard, item) is { } issue)
            {
                issues.Add(issue);
            }
        }

        return new ReadbackResult(issues);
    }

    private static ReadbackIssue? CheckItem(HeardText heard, ReadbackItem item)
    {
        var expected = item.Expected;
        var number = expected.Number ?? double.NaN;

        // Steht der erwartete Wert irgendwo als Zahl, gilt er als zurückgelesen, auch ohne Schlüsselwort ("… five thousand, squawk …")
        bool AnyNumber(double value) => heard.Tokens.Any(t => t.Kind is HeardKind.Number or HeardKind.Decimal && Math.Abs(t.Value - value) < 0.001);

        return expected.Kind switch
        {
            AtcValueKind.Squawk => Compare(item, Squawks(heard), h => h == expected.Code, () => heard.HasNumber(expected.Code)),
            AtcValueKind.Altitude => Compare(
                item,
                Altitudes(heard),
                h => Math.Abs(h - number) < 1,
                () => AnyNumber(number) || (expected.FlightLevel && AnyNumber(number / 100)),
                Format),
            AtcValueKind.Runway => Compare(item, Runways(heard), h => SameRunway(h, expected), null, h => h.Number.ToString("00", CultureInfo.InvariantCulture) + h.Designator),
            AtcValueKind.Frequency => Compare(item, Frequencies(heard), h => Math.Abs(h - number) < 0.003, null, f => f.ToString("0.000", CultureInfo.InvariantCulture)),
            AtcValueKind.Speed => Compare(item, Speeds(heard), h => Math.Abs(h - number) < 1, () => AnyNumber(number), Format),
            AtcValueKind.Heading => Compare(item, NumbersNear(heard, ["heading"], ["degrees"]), h => Math.Abs(h % 360 - number % 360) < 1, () => AnyNumber(number), Format),
            AtcValueKind.Qnh => Compare(item, Qnhs(heard), h => Math.Abs(h - number) < 1, () => AnyNumber(number), Format),
            _ => CodeIssue(heard, item),
        };
    }

    private static string Format(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    private static ReadbackIssue? Compare<T>(ReadbackItem item, IReadOnlyList<T> heard, Func<T, bool> matches, Func<bool>? fallback, Func<T, string>? format = null)
    {
        if (heard.Any(matches))
        {
            return null;
        }

        if (heard.Count > 0)
        {
            var first = heard[0];
            return new ReadbackIssue(item.Name, item.Expected, format is null ? Convert.ToString(first, CultureInfo.InvariantCulture) : format(first), ReadbackProblem.Wrong);
        }

        return fallback?.Invoke() == true ? null : new ReadbackIssue(item.Name, item.Expected, null, ReadbackProblem.Missing);
    }

    /// <summary>
    /// Kennungen (SID, Fix, Haltepunkt, Gate, Anflug): kommt die Kennung im Readback vor? Kurze Kennungen ("S", "N3", "A26")
    /// müssen als eigene Wörter vorkommen – sonst passt "S" auf jedes S im Text.
    /// </summary>
    private static ReadbackIssue? CodeIssue(HeardText heard, ReadbackItem item)
    {
        var codes = new List<string> { item.Expected.Code is { Length: > 0 } code ? code : AtcSpeech.Compact(item.Expected.Written) };
        codes.AddRange(item.Expected.Alternatives.Select(AtcSpeech.Compact));
        return codes.Any(c => c.Length > 0 && (c.Length <= 3 ? heard.ContainsTokens(c) : heard.Compact.Contains(c, StringComparison.Ordinal)))
            ? null
            : new ReadbackIssue(item.Name, item.Expected, null, ReadbackProblem.Missing);
    }

    // ------------------------------------------------------------------ Werte aus dem Readback

    private static List<string> Squawks(HeardText heard)
    {
        var result = new List<string>();
        var t = heard.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord(SquawkWords) && heard.NextNumber(i, 3) is { } n)
            {
                result.Add(n.Text);
            }
        }

        return result;
    }

    /// <summary>Alle Höhenangaben in Fuß: "FL110"/"flight level one one zero" = 11000, "five thousand feet" = 5000.</summary>
    public static List<double> Altitudes(HeardText heard) => AltitudesAt(heard).Select(a => a.Feet).ToList();

    /// <summary>Höhenangaben wie <see cref="Altitudes"/>, mit der Stelle der Zahl im Funkspruch (Index in <see cref="HeardText.Tokens"/>).</summary>
    public static List<(int Index, double Feet)> AltitudesAt(HeardText heard)
    {
        var result = new List<(int, double)>();
        var t = heard.Tokens;
        var used = new HashSet<int>();
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord("fl") || t[i].IsWord("level"))
            {
                var j = i + 1;
                if (j < t.Count && t[j].IsWord("level"))
                {
                    j++;
                }

                if (j < t.Count && t[j].Kind == HeardKind.Number)
                {
                    result.Add((j, t[j].Value * 100));
                    used.Add(j);
                }

                continue;
            }

            if (t[i].IsWord("flight") && i + 1 < t.Count && t[i + 1].IsWord("level"))
            {
                continue;
            }

            if (t[i].Kind != HeardKind.Number || used.Contains(i))
            {
                continue;
            }

            if (i + 1 < t.Count && t[i + 1].IsWord(FeetWords))
            {
                result.Add((i, t[i].Value));
                continue;
            }

            if (heard.PreviousWord(i, 2, Fillers) is { } previous && previous.IsWord(AltitudeVerbs))
            {
                result.Add((i, t[i].Value < 1000 ? t[i].Value * 100 : t[i].Value));
            }
        }

        return result;
    }

    private static List<(int Number, string? Designator)> Runways(HeardText heard)
    {
        var result = new List<(int, string?)>();
        var t = heard.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            var isKeyword = t[i].IsWord("runway") || t[i].IsWord("rwy");
            var start = isKeyword ? i + 1 : i;
            if (start >= t.Count || t[start].Kind != HeardKind.Number || t[start].Value is < 1.0 or > 36.0 || t[start].Text.Length > 2)
            {
                continue;
            }

            var designator = start + 1 < t.Count ? Designator(t[start + 1]) : null;
            if (isKeyword || designator is not null)
            {
                result.Add(((int)t[start].Value, designator));
            }
        }

        return result;
    }

    private static string? Designator(HeardToken token)
    {
        if (token.Kind == HeardKind.Letter && token.Text is "L" or "R" or "C")
        {
            return token.Text;
        }

        return token.Kind == HeardKind.Word
            ? token.Text switch
            {
                "left" => "L",
                "right" => "R",
                "center" or "centre" => "C",
                _ => null,
            }
            : null;
    }

    private static bool SameRunway((int Number, string? Designator) heard, AtcValue expected)
    {
        if (expected.Number is not { } number || heard.Number != (int)number)
        {
            return false;
        }

        var code = expected.Code ?? "";
        var designator = code.Length > 2 ? code[2..] : null;
        return designator is null || heard.Designator == designator;
    }

    private static List<double> Frequencies(HeardText heard)
    {
        var result = new List<double>();
        foreach (var token in heard.Tokens)
        {
            if (token.Kind == HeardKind.Decimal && token.Value is >= 108.0 and < 137.0)
            {
                result.Add(token.Value);
            }
            else if (token.Kind == HeardKind.Number && token.Text.Length is >= 4 and <= 6 && token.Text[0] == '1'
                && double.TryParse(token.Text[..3] + "." + token.Text[3..], NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                && f is >= 108.0 and < 137.0)
            {
                // "one one eight seven" ohne "decimal"
                result.Add(f);
            }
        }

        return result;
    }

    /// <summary>Geschwindigkeiten in Knoten aus dem Funkspruch ("speed two eight zero knots", "280 kt").</summary>
    public static List<double> Speeds(HeardText heard) => NumbersNear(heard, ["speed"], KnotWords);

    private static List<double> NumbersNear(HeardText heard, string[] before, string[] after)
    {
        var result = new List<double>();
        var t = heard.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord(before) && heard.NextNumber(i, 2) is { } n)
            {
                result.Add(n.Value);
            }
            else if (t[i].Kind == HeardKind.Number && i + 1 < t.Count && t[i + 1].IsWord(after) && (i == 0 || !t[i - 1].IsWord(before)))
            {
                result.Add(t[i].Value);
            }
        }

        return result;
    }

    /// <summary>QNH/Altimeter: "QNH one zero one three", "Q N H 1013", "Q1013", "altimeter two niner niner two" (= 2992).</summary>
    private static List<double> Qnhs(HeardText heard)
    {
        var result = new List<double>();
        var t = heard.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            var skip = -1;
            if (t[i].IsWord("qnh") || t[i].IsWord("altimeter"))
            {
                skip = i;
            }
            else if (t[i].Kind == HeardKind.Letter && t[i].Text == "Q")
            {
                skip = i + 2 < t.Count && t[i + 1].Text == "N" && t[i + 2].Text == "H" ? i + 2 : i;
            }

            if (skip >= 0 && heard.NextNumber(skip, 2) is { } n)
            {
                // "two niner decimal niner two" → 29.92 → 2992
                result.Add(n.Kind == HeardKind.Decimal && n.Value < 100 ? Math.Round(n.Value * 100) : n.Value);
            }
        }

        return result;
    }
}

public enum HeardKind
{
    Word,
    Number,
    Decimal,
    Letter,
}

/// <summary>Ein Wort des Readbacks: Wort (klein), Zahl (Ziffern, führende Nullen bleiben), Dezimalzahl oder Buchstabe (groß).</summary>
public readonly record struct HeardToken(HeardKind Kind, string Text, double Value)
{
    public bool IsWord(string word) => Kind == HeardKind.Word && Text == word;

    public bool IsWord(string[] words) => Kind == HeardKind.Word && words.Contains(Text);

    public override string ToString() => Text;
}

/// <summary>Aufbereiteter Funkspruch des Piloten: gesprochene Zahlen und NATO-Buchstaben sind schon umgewandelt.</summary>
public sealed partial class HeardText
{
    private static readonly Dictionary<string, char> DigitWords = new(StringComparer.Ordinal)
    {
        ["zero"] = '0', ["oh"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["tree"] = '3', ["four"] = '4', ["fower"] = '4',
        ["five"] = '5', ["fife"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9', ["niner"] = '9',
    };

    private static readonly Dictionary<string, char> Nato = new(StringComparer.Ordinal)
    {
        ["alpha"] = 'A', ["alfa"] = 'A', ["bravo"] = 'B', ["charlie"] = 'C', ["delta"] = 'D', ["echo"] = 'E', ["foxtrot"] = 'F',
        ["golf"] = 'G', ["hotel"] = 'H', ["india"] = 'I', ["juliett"] = 'J', ["juliet"] = 'J', ["kilo"] = 'K', ["lima"] = 'L',
        ["mike"] = 'M', ["november"] = 'N', ["oscar"] = 'O', ["papa"] = 'P', ["quebec"] = 'Q', ["romeo"] = 'R', ["sierra"] = 'S',
        ["tango"] = 'T', ["uniform"] = 'U', ["victor"] = 'V', ["whiskey"] = 'W', ["whisky"] = 'W', ["xray"] = 'X', ["yankee"] = 'Y',
        ["zulu"] = 'Z',
    };

    private static readonly HashSet<string> DecimalWords = new(StringComparer.Ordinal) { "decimal", "point", "dot" };

    private HeardText(IReadOnlyList<HeardToken> tokens)
    {
        Tokens = tokens;
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            sb.Append(t.Kind == HeardKind.Decimal ? t.Text.Replace(".", "", StringComparison.Ordinal) : t.Text.ToUpperInvariant());
        }

        Compact = sb.ToString();
    }

    public IReadOnlyList<HeardToken> Tokens { get; }

    /// <summary>Alles hintereinander in Großbuchstaben ohne Leerzeichen ("MARUN7GDEPARTURE…") für Kennungen.</summary>
    public string Compact { get; }

    public bool HasNumber(string? digits) => digits is not null && Tokens.Any(t => t.Kind == HeardKind.Number && t.Text == digits);

    public bool ContainsCode(string code) => code.Length > 0 && Compact.Contains(AtcSpeech.Compact(code), StringComparison.Ordinal);

    /// <summary>Kennung aus 1–3 aufeinanderfolgenden ganzen Wörtern ("sierra" → "S", "november three" → "N3", "A26").</summary>
    public bool ContainsTokens(string code)
    {
        var target = AtcSpeech.Compact(code);
        for (var i = 0; i < Tokens.Count; i++)
        {
            var joined = "";
            for (var j = i; j < Tokens.Count && j < i + 3; j++)
            {
                if (Tokens[j].Kind == HeardKind.Word && Tokens[j].Text.Length > 3)
                {
                    break;
                }

                joined += Tokens[j].Kind == HeardKind.Decimal ? Tokens[j].Text.Replace(".", "", StringComparison.Ordinal) : Tokens[j].Text.ToUpperInvariant();
                if (joined == target)
                {
                    return true;
                }

                if (joined.Length >= target.Length)
                {
                    break;
                }
            }
        }

        return false;
    }

    public HeardToken? NextNumber(int index, int maxAhead)
    {
        for (var j = index + 1; j < Tokens.Count && j <= index + maxAhead; j++)
        {
            if (Tokens[j].Kind is HeardKind.Number or HeardKind.Decimal)
            {
                return Tokens[j];
            }
        }

        return null;
    }

    /// <summary>Wort vor <paramref name="index"/>, Füllwörter übersprungen.</summary>
    public HeardToken? PreviousWord(int index, int maxBack, string[] skip)
    {
        var steps = 0;
        for (var j = index - 1; j >= 0 && steps <= maxBack; j--)
        {
            steps++;
            if (Tokens[j].IsWord(skip))
            {
                continue;
            }

            return Tokens[j];
        }

        return null;
    }

    public static HeardText Parse(string text)
    {
        var raw = new List<HeardToken>();
        var lower = text.ToLowerInvariant().Replace("x-ray", "xray", StringComparison.Ordinal);
        foreach (Match m in TokenRegex().Matches(lower))
        {
            var s = m.Value;
            if (char.IsAsciiDigit(s[0]))
            {
                var normalized = s.Replace(',', '.');
                if (normalized.Contains('.'))
                {
                    raw.Add(new HeardToken(HeardKind.Decimal, normalized, double.Parse(normalized, CultureInfo.InvariantCulture)));
                }
                else
                {
                    raw.Add(new HeardToken(HeardKind.Number, s, double.Parse(s, CultureInfo.InvariantCulture)));
                }
            }
            else if (DigitWords.TryGetValue(s, out var digit))
            {
                raw.Add(new HeardToken(HeardKind.Number, digit.ToString(), digit - '0'));
            }
            else if (Nato.TryGetValue(s, out var letter))
            {
                raw.Add(new HeardToken(HeardKind.Letter, letter.ToString(), 0));
            }
            else if (s.Length == 1)
            {
                raw.Add(new HeardToken(HeardKind.Letter, s.ToUpperInvariant(), 0));
            }
            else
            {
                raw.Add(new HeardToken(HeardKind.Word, DecimalWords.Contains(s) ? "decimal" : s, 0));
            }
        }

        return new HeardText(MergeNumbers(raw));
    }

    /// <summary>
    /// Einzelne Ziffern zu Zahlen zusammenfassen ("two two zero five" → 2205) und "thousand"/"hundred"/"decimal" auswerten
    /// ("one zero thousand" → 10000, "three thousand five hundred" → 3500, "one two one decimal eight" → 121.8).
    /// </summary>
    private static List<HeardToken> MergeNumbers(List<HeardToken> raw)
    {
        var result = new List<HeardToken>(raw.Count);
        var i = 0;
        while (i < raw.Count)
        {
            if (raw[i].Kind != HeardKind.Number)
            {
                result.Add(raw[i]);
                i++;
                continue;
            }

            var digits = ReadDigits(raw, ref i);
            var value = double.Parse(digits, CultureInfo.InvariantCulture);
            var text = digits;

            if (i < raw.Count && raw[i].IsWord("thousand"))
            {
                value *= 1000;
                i++;
                if (i < raw.Count && raw[i].Kind == HeardKind.Number)
                {
                    var save = i;
                    var hundreds = ReadDigits(raw, ref i);
                    if (i < raw.Count && raw[i].IsWord("hundred"))
                    {
                        value += double.Parse(hundreds, CultureInfo.InvariantCulture) * 100;
                        i++;
                    }
                    else
                    {
                        i = save;
                    }
                }

                text = value.ToString("0", CultureInfo.InvariantCulture);
            }
            else if (i < raw.Count && raw[i].IsWord("hundred"))
            {
                value *= 100;
                i++;
                text = value.ToString("0", CultureInfo.InvariantCulture);
            }
            else if (i + 1 < raw.Count && raw[i].IsWord("decimal") && raw[i + 1].Kind == HeardKind.Number)
            {
                i++;
                var fraction = ReadDigits(raw, ref i);
                text = digits + "." + fraction;
                result.Add(new HeardToken(HeardKind.Decimal, text, double.Parse(text, CultureInfo.InvariantCulture)));
                continue;
            }

            result.Add(new HeardToken(HeardKind.Number, text, value));
        }

        return result;
    }

    /// <summary>Folge einzelner Ziffern lesen; eine mehrstellige Zahl ("2205") steht für sich.</summary>
    private static string ReadDigits(List<HeardToken> raw, ref int i)
    {
        if (raw[i].Text.Length > 1)
        {
            return raw[i++].Text;
        }

        var sb = new StringBuilder();
        while (i < raw.Count && raw[i].Kind == HeardKind.Number && raw[i].Text.Length == 1)
        {
            sb.Append(raw[i].Text);
            i++;
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[a-z]+|\d+(?:[.,]\d+)?")]
    private static partial Regex TokenRegex();
}
