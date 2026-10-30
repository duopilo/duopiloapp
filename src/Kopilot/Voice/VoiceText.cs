using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Voice;

/// <summary>
/// Bringt die Abschrift von Whisper in eine feste Form, gegen die Router und Befehle prüfen:
/// klein, ohne Satzzeichen, Zahlwörter (deutsch und englisch, auch ICAO wie "niner", "tree") als Ziffern,
/// buchstabierte Ziffernfolgen zusammengezogen ("one zero one three" → "1013", "five thousand" → "5000"),
/// typische Hörfehler und Schreibweisen vereinheitlicht ("speed break" → "speedbrake", "Flaps won" → "flaps 1").
/// </summary>
public static partial class VoiceText
{
    // Ziffernwörter: werden aneinandergehängt ("one two zero" → 120)
    private static readonly Dictionary<string, int> DigitWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["won"] = 1, ["two"] = 2, ["three"] = 3, ["tree"] = 3, ["four"] = 4, ["fower"] = 4,
        ["five"] = 5, ["fife"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["niner"] = 9,
        ["null"] = 0, ["eins"] = 1, ["zwei"] = 2, ["zwo"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["fuenf"] = 5,
        ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9,
    };

    // Zehner und 10–19: natürliche Zahlen ("twenty five" → 25)
    private static readonly Dictionary<string, int> TensWords = new(StringComparer.Ordinal)
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40,
        ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["zehn"] = 10, ["elf"] = 11, ["zwölf"] = 12, ["zwoelf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14, ["fünfzehn"] = 15,
        ["sechzehn"] = 16, ["siebzehn"] = 17, ["achtzehn"] = 18, ["neunzehn"] = 19, ["zwanzig"] = 20, ["dreißig"] = 30,
        ["dreissig"] = 30, ["vierzig"] = 40, ["fünfzig"] = 50, ["sechzig"] = 60, ["siebzig"] = 70, ["achtzig"] = 80, ["neunzig"] = 90,
    };

    private static readonly Dictionary<string, int> ScaleWords = new(StringComparer.Ordinal)
    {
        ["hundred"] = 100, ["hundert"] = 100, ["thousand"] = 1000, ["tausend"] = 1000,
    };

    // Wörter, nach denen "to"/"too"/"for" eher eine Zahl als eine Präposition sind ("flaps to" → "flaps 2")
    private static readonly HashSet<string> ValueKeywords = new(StringComparer.Ordinal)
    {
        "flaps", "klappen", "autopilot", "ap", "pack", "engine",
    };

    // Mehrwortige Begriffe → ein Wort (vor den Zahlen)
    private static readonly (Regex Pattern, string Replacement)[] PhraseFolds =
    [
        (Fold(@"co ?pilot|ko ?pilot|kopi lot|co pi lot"), "kopilot"),
        (Fold(@"speed ?brakes?|speed ?breaks?"), "speedbrake"),
        (Fold(@"seat ?belts?(?: signs?)?|seatbelts? signs?"), "seatbelts"),
        (Fold(@"fasten seatbelts"), "seatbelts on"),
        (Fold(@"auto ?brakes?|auto ?breaks?"), "autobrake"),
        (Fold(@"auto ?pilot"), "autopilot"),
        (Fold(@"auto ?thrust|a thr|athr"), "autothrust"),
        (Fold(@"flight directors?|f ?d ?s?"), "fd"),
        (Fold(@"flight level|flugfläche|flugflaeche"), "fl"),
        (Fold(@"landing gear"), "gear"),
        (Fold(@"anti ?ice"), "antiice"),
        (Fold(@"tee ?cas|t ?cas|tea ?cas|ti ?cas|tcass?"), "tcas"),
        (Fold(@"ta ?ra|ta and ra|t a r a"), "tara"),
        (Fold(@"a ?p ?u|apu"), "apu"),
        (Fold(@"q ?n ?h"), "qnh"),
        (Fold(@"i ?l ?s|l ?s"), "ls"),
        (Fold(@"turn ?off lights?"), "turnoff lights"),
        (Fold(@"take ?off lights?|t ?o lights?"), "takeoff lights"),
        (Fold(@"spoilers?|spoiler"), "spoilers"),
        (Fold(@"flaps?|flabs?|flops"), "flaps"),
        (Fold(@"klappe"), "klappen"),
        (Fold(@"squawks?|squak|squark|squok|squaw"), "squawk"),
        (Fold(@"say again|repeat|wiederholen|noch ?mal|nochmal"), "say again"),
        (Fold(@"stand ?by|stand by"), "standby"),
        (Fold(@"vertical speed|v ?s"), "vs"),
        (Fold(@"check ?lists?|check listen?|checklisten?"), "checklist"),
        (Fold(@"take off"), "takeoff"),
        (Fold(@"n d|navigation display"), "nd"),
        (Fold(@"e t a"), "eta"),
        (Fold(@"t o d"), "tod"),
    ];

    private static readonly Dictionary<string, char> Nato = new(StringComparer.Ordinal)
    {
        ["alpha"] = 'a', ["alfa"] = 'a', ["bravo"] = 'b', ["charlie"] = 'c', ["delta"] = 'd', ["echo"] = 'e', ["foxtrot"] = 'f',
        ["golf"] = 'g', ["hotel"] = 'h', ["india"] = 'i', ["juliet"] = 'j', ["juliett"] = 'j', ["kilo"] = 'k', ["lima"] = 'l',
        ["mike"] = 'm', ["november"] = 'n', ["oscar"] = 'o', ["papa"] = 'p', ["quebec"] = 'q', ["romeo"] = 'r', ["sierra"] = 's',
        ["tango"] = 't', ["uniform"] = 'u', ["victor"] = 'v', ["whiskey"] = 'w', ["whisky"] = 'w', ["xray"] = 'x', ["x-ray"] = 'x',
        ["yankee"] = 'y', ["zulu"] = 'z',
    };

    /// <summary>Normalisierte Form (klein, Ziffern, ein Leerzeichen zwischen den Wörtern).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var text = raw.ToLowerInvariant();
        text = Punctuation(text);
        text = SplitAlnum().Replace(text, "$1 $2");
        text = SplitAlnum2().Replace(text, "$1 $2");
        text = Spaces().Replace(text, " ").Trim();
        foreach (var (pattern, replacement) in PhraseFolds)
        {
            text = pattern.Replace(text, replacement);
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        tokens = Numbers(tokens);
        text = string.Join(' ', tokens);

        // Nach den Zahlen: "level 120" → "fl 120", "minus 1000" → "-1000"
        text = LevelFold().Replace(text, "fl $1");
        text = MinusFold().Replace(text, "-$1");
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>Wörter der normalisierten Form.</summary>
    public static string[] Words(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Kompakte Form für Rufzeichen: NATO-Alphabet → Buchstaben, ohne Leerzeichen.
    /// "Lufthansa four alpha bravo" → "lufthansa4ab", "D-AIAB" → "daiab".
    /// </summary>
    public static string Compact(string? text)
    {
        var normalized = Normalize(text);
        var sb = new StringBuilder(normalized.Length);
        foreach (var word in Words(normalized))
        {
            if (Nato.TryGetValue(word, out var letter))
            {
                sb.Append(letter);
            }
            else
            {
                foreach (var c in word)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        sb.Append(c);
                    }
                }
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ Satzzeichen und Zahlenformate

    private static string Punctuation(string text)
    {
        // Tausenderpunkte (deutsch "5.000", "1.013") – Frequenzen wie "121.800" bleiben Dezimalzahlen
        text = GermanThousands().Replace(text, m =>
        {
            var head = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var groups = m.Groups[2].Value.Length / 4;
            return groups == 1 && head is >= 118 and <= 137 ? m.Value : m.Value.Replace(".", "", StringComparison.Ordinal);
        });

        // Tausenderkommas (englisch "5,000") und Dezimalkomma (deutsch "121,8")
        text = EnglishThousands().Replace(text, m => m.Value.Replace(",", "", StringComparison.Ordinal));
        text = DecimalComma().Replace(text, "$1.$2");

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var prevDigit = i > 0 && char.IsDigit(text[i - 1]);
            var nextDigit = i + 1 < text.Length && char.IsDigit(text[i + 1]);
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c == '.' && prevDigit && nextDigit)
            {
                sb.Append('.');
            }
            else if (c == '-' && nextDigit && !prevDigit && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                sb.Append('-');
            }
            else if (c is '\'' or '’' && i > 0 && char.IsLetter(text[i - 1]))
            {
                // "that's" → "thats"
            }
            else
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ Zahlen

    private enum NumKind
    {
        None,
        Digit,
        Tens,
        Scale,
        Numeral,
        Decimal,
    }

    private static NumKind Kind(string token, out int value)
    {
        value = 0;
        if (token.Length > 0 && char.IsDigit(token[0]) && token.All(char.IsDigit))
        {
            return NumKind.Numeral;
        }

        if (DecimalNumber().IsMatch(token))
        {
            return NumKind.Decimal;
        }

        if (DigitWords.TryGetValue(token, out value))
        {
            return NumKind.Digit;
        }

        if (TensWords.TryGetValue(token, out value))
        {
            return NumKind.Tens;
        }

        if (ScaleWords.TryGetValue(token, out value))
        {
            return NumKind.Scale;
        }

        if (GermanNumber.TryParse(token, out value))
        {
            return value < 10 ? NumKind.Digit : NumKind.Tens;
        }

        return NumKind.None;
    }

    private static bool IsNumberToken(string token) => Kind(token, out _) != NumKind.None;

    /// <summary>Folgen von Zahlwörtern/Ziffern zu einer Zahl zusammenziehen.</summary>
    private static List<string> Numbers(List<string> tokens)
    {
        // Mehrdeutige Hörfehler nur im passenden Zusammenhang als Zahl lesen
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t is "to" or "too" or "for" or "fore")
            {
                var prev = i > 0 ? tokens[i - 1] : null;
                var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                var nextIsNumber = next is not null && IsNumberToken(next);
                if (prev is not null && ValueKeywords.Contains(prev) && !nextIsNumber)
                {
                    tokens[i] = t is "to" or "too" ? "2" : "4";
                }
            }
            else if (t is "oh" or "o" && i > 0 && i + 1 < tokens.Count && IsNumberToken(tokens[i - 1]) && IsNumberToken(tokens[i + 1]))
            {
                tokens[i] = "0";
            }
        }

        var result = new List<string>(tokens.Count);
        var j = 0;
        while (j < tokens.Count)
        {
            if (!IsNumberToken(tokens[j]))
            {
                result.Add(tokens[j]);
                j++;
                continue;
            }

            // Lauf aus Zahlen, "decimal"/"point"/"komma" dazwischen und "and"/"und" vor Zahlwörtern
            var run = new List<string>();
            while (j < tokens.Count)
            {
                var t = tokens[j];
                if (IsNumberToken(t))
                {
                    run.Add(t);
                    j++;
                }
                else if (t is "decimal" or "point" or "komma" or "dezimal" && run.Count > 0 && j + 1 < tokens.Count && IsNumberToken(tokens[j + 1]))
                {
                    run.Add(".");
                    j++;
                }
                else if (t is "and" or "und" && run.Count > 0 && j + 1 < tokens.Count && Kind(tokens[j + 1], out _) is NumKind.Tens or NumKind.Digit
                         && run.Any(r => Kind(r, out _) == NumKind.Scale))
                {
                    // "two hundred and fifty"
                    j++;
                }
                else
                {
                    break;
                }
            }

            result.Add(Combine(run));
        }

        return result;
    }

    /// <summary>Ein Lauf von Zahlen → eine Zahl als Text.</summary>
    private static string Combine(List<string> run)
    {
        var dot = run.IndexOf(".");
        if (dot > 0)
        {
            var whole = Combine(run.GetRange(0, dot));
            var fraction = Combine(run.GetRange(dot + 1, run.Count - dot - 1));
            return fraction.Length > 0 ? $"{whole}.{fraction}" : whole;
        }

        if (run.Count == 1 && Kind(run[0], out _) == NumKind.Decimal)
        {
            return run[0];
        }

        var natural = run.Any(t => Kind(t, out _) is NumKind.Tens or NumKind.Scale);
        if (!natural)
        {
            // Nur Ziffern: aneinanderhängen ("one two zero" → 120, "1 0 1 3" → 1013)
            var sb = new StringBuilder();
            foreach (var t in run)
            {
                sb.Append(Kind(t, out var v) == NumKind.Digit ? v.ToString(CultureInfo.InvariantCulture) : t);
            }

            return sb.ToString();
        }

        // Natürliche Zahl, auch ICAO ("one zero thousand", "four thousand five hundred", "two hundred fifty")
        long total = 0;
        long group = 0;
        var digits = new StringBuilder();
        void Flush()
        {
            if (digits.Length > 0)
            {
                group += long.Parse(digits.ToString(), CultureInfo.InvariantCulture);
                digits.Clear();
            }
        }

        foreach (var t in run)
        {
            switch (Kind(t, out var v))
            {
                case NumKind.Digit:
                    digits.Append(v.ToString(CultureInfo.InvariantCulture));
                    break;
                case NumKind.Numeral:
                    digits.Append(t);
                    break;
                case NumKind.Tens:
                    Flush();
                    group += v;
                    break;
                case NumKind.Scale:
                    Flush();
                    if (v >= 1000)
                    {
                        total += (group == 0 ? 1 : group) * v;
                        group = 0;
                    }
                    else
                    {
                        group = (group == 0 ? 1 : group) * v;
                    }

                    break;
            }
        }

        Flush();
        return (total + group).ToString(CultureInfo.InvariantCulture);
    }

    private static Regex Fold(string pattern) =>
        new($@"(?<![\p{{L}}\p{{N}}])(?:{pattern})(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "ap1" → "ap 1", "fl120" → "fl 120" (aber nicht innerhalb von Dezimalzahlen)
    [GeneratedRegex(@"(?<=^|\s)(\p{L}+)(\d+)(?=\s|$)")]
    private static partial Regex SplitAlnum();

    // "4ab" → "4 ab"
    [GeneratedRegex(@"(?<=^|\s)(\d+)(\p{L}+)(?=\s|$)")]
    private static partial Regex SplitAlnum2();

    [GeneratedRegex(@"(?<!\d)(\d{1,3})((?:\.\d{3})+)(?![\d.])")]
    private static partial Regex GermanThousands();

    [GeneratedRegex(@"(?<!\d)\d{1,3}(?:,\d{3})+(?![\d,])")]
    private static partial Regex EnglishThousands();

    [GeneratedRegex(@"(\d),(\d)")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex DecimalNumber();

    [GeneratedRegex(@"(?<![\p{L}])(?<!fl )level (\d{2,3})(?!\d)")]
    private static partial Regex LevelFold();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])minus (\d+(?:\.\d+)?)")]
    private static partial Regex MinusFold();
}

/// <summary>Deutsche Zahlwörter, auch zusammengesetzt: "zweihundertfünfzig", "fünfundzwanzig", "dreitausend".</summary>
public static class GermanNumber
{
    private static readonly (string Word, int Value)[] Units =
    [
        ("null", 0), ("eins", 1), ("ein", 1), ("eine", 1), ("zwei", 2), ("zwo", 2), ("drei", 3), ("vier", 4), ("fünf", 5), ("fuenf", 5),
        ("sechs", 6), ("sieben", 7), ("acht", 8), ("neun", 9),
    ];

    private static readonly (string Word, int Value)[] Teens =
    [
        ("zehn", 10), ("elf", 11), ("zwölf", 12), ("zwoelf", 12), ("dreizehn", 13), ("vierzehn", 14), ("fünfzehn", 15), ("fuenfzehn", 15),
        ("sechzehn", 16), ("siebzehn", 17), ("achtzehn", 18), ("neunzehn", 19),
    ];

    private static readonly (string Word, int Value)[] Tens =
    [
        ("zwanzig", 20), ("dreißig", 30), ("dreissig", 30), ("vierzig", 40), ("fünfzig", 50), ("fuenfzig", 50), ("sechzig", 60),
        ("siebzig", 70), ("achtzig", 80), ("neunzig", 90),
    ];

    /// <summary>
    /// Zusammengesetzte deutsche Zahl. Einzelne Grundwörter ("ein", "acht") gelten hier nicht –
    /// "ein" ist meist "an" ("Licht ein") und die Ziffern liest <see cref="VoiceText"/> selbst.
    /// </summary>
    public static bool TryParse(string word, out int value)
    {
        value = 0;
        if (word.Length < 6 || !word.All(char.IsLetter))
        {
            return false;
        }

        if (Units.Any(u => u.Word == word) || Teens.Any(t => t.Word == word) || Tens.Any(t => t.Word == word))
        {
            return false;
        }

        return Parse(word, out value) && value > 0;
    }

    private static bool Parse(string w, out int value)
    {
        value = 0;
        if (w.Length == 0)
        {
            return true;
        }

        var thousand = w.IndexOf("tausend", StringComparison.Ordinal);
        if (thousand >= 0)
        {
            var left = w[..thousand];
            var right = w[(thousand + "tausend".Length)..];
            if (!ParseBelow1000(left.Length == 0 ? "eins" : left, out var l) || !ParseBelow1000(right, out var r))
            {
                return false;
            }

            value = (l == 0 ? 1 : l) * 1000 + r;
            return true;
        }

        return ParseBelow1000(w, out value);
    }

    private static bool ParseBelow1000(string w, out int value)
    {
        value = 0;
        if (w.Length == 0)
        {
            return true;
        }

        var hundred = w.IndexOf("hundert", StringComparison.Ordinal);
        if (hundred >= 0)
        {
            var left = w[..hundred];
            var right = w[(hundred + "hundert".Length)..];
            if (right.StartsWith("und", StringComparison.Ordinal))
            {
                right = right[3..];
            }

            if (!ParseBelow100(left.Length == 0 ? "eins" : left, out var l) || l is < 1 or > 9 || !ParseBelow100(right, out var r))
            {
                return false;
            }

            value = (l * 100) + r;
            return true;
        }

        return ParseBelow100(w, out value);
    }

    private static bool ParseBelow100(string w, out int value)
    {
        value = 0;
        if (w.Length == 0)
        {
            return true;
        }

        foreach (var (word, v) in Units.Concat(Teens).Concat(Tens))
        {
            if (w == word)
            {
                value = v;
                return true;
            }
        }

        // "fünfundzwanzig" = 5 + 20
        var und = w.IndexOf("und", StringComparison.Ordinal);
        if (und > 0)
        {
            var unit = Units.FirstOrDefault(u => u.Word == w[..und]);
            var ten = Tens.FirstOrDefault(t => t.Word == w[(und + 3)..]);
            if (unit.Word is not null && ten.Word is not null && unit.Value > 0)
            {
                value = unit.Value + ten.Value;
                return true;
            }
        }

        return false;
    }
}
