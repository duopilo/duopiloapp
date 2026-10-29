using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Atc;

/// <summary>Art eines Vorlagen-Werts – bestimmt Sprechweise und Readback-Vergleich.</summary>
public enum AtcValueKind
{
    Text,
    Ident,
    Callsign,
    Airport,
    Altitude,
    Squawk,
    Runway,
    Frequency,
    Speed,
    Heading,
    Wind,
    Qnh,
    Time,
    Position,
    Approach,
}

/// <summary>
/// Wert einer Vorlagen-Variable: Anzeigetext ("FL110", "2205"), Sprechform ("flight level one one zero", "two two zero five")
/// und der Vergleichswert für die Readback-Prüfung.
/// </summary>
public sealed record AtcValue(AtcValueKind Kind, string Written, string Spoken)
{
    /// <summary>Zahl zum Vergleichen: Fuß bei Höhen, Knoten, Grad, QNH, MHz bei Frequenzen.</summary>
    public double? Number { get; init; }

    /// <summary>Kennung in Großbuchstaben ohne Leerzeichen zum Vergleichen ("MARUN7G", "25C", "2205").</summary>
    public string? Code { get; init; }

    /// <summary>Weitere gültige Schreibweisen der Kennung (z. B. Koordinaten "55N020W" / "55N20W").</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>Höhe als Flight Level.</summary>
    public bool FlightLevel { get; init; }

    public static AtcValue Text(string text) => new(AtcValueKind.Text, text, AtcSpeech.Literal(text));

    public static AtcValue Text(string written, string spoken) => new(AtcValueKind.Text, written, spoken);
}

/// <summary>
/// ICAO-Sprechweise für den Funk: Zahlen Ziffer für Ziffer ("two two zero five"), Buchstaben im NATO-Alphabet,
/// Geschwindigkeiten immer in Knoten. Liefert fertige <see cref="AtcValue"/>s für die Vorlagen.
/// </summary>
public static partial class AtcSpeech
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    private static readonly string[] NatoWords =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
        "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "x-ray", "yankee", "zulu",
    ];

    // Abkürzungen im festen Vorlagentext, die Buchstabe für Buchstabe gesprochen werden
    private static readonly HashSet<string> SpelledAcronyms = new(StringComparer.Ordinal)
    {
        "IFR", "VFR", "QNH", "ILS", "ATC", "RNP", "VOR", "NDB", "DME", "ETA", "FIR", "SID", "GPS", "LOC",
    };

    // Sprechweise der Anflugarten
    private static readonly Dictionary<string, string> ApproachWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ILS"] = "I L S",
        ["LOC"] = "localizer",
        ["RNP"] = "R N P",
        ["RNAV"] = "R-NAV",
        ["GPS"] = "G P S",
        ["VOR"] = "V O R",
        ["NDB"] = "N D B",
        ["VISUAL"] = "visual",
        ["LDA"] = "L D A",
        ["GLS"] = "G L S",
    };

    public static string Digit(char c) => DigitWords[c - '0'];

    public static string Letter(char c) => NatoWords[char.ToUpperInvariant(c) - 'A'];

    /// <summary>Ziffern und Buchstaben einzeln: "4AB" → "four alpha bravo". Andere Zeichen fallen weg.</summary>
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

    /// <summary>Zahl Ziffer für Ziffer, z. B. 250 → "two five zero", 5 → "zero five" bei <paramref name="minDigits"/> = 2.</summary>
    public static string Digits(long value, int minDigits = 1) =>
        Spell(Math.Abs(value).ToString(new string('0', Math.Max(1, minDigits)), Inv));

    /// <summary>Kennung als Code zum Vergleichen: nur Buchstaben und Ziffern, groß.</summary>
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

    /// <summary>Fester Vorlagentext: bekannte Abkürzungen ("IFR", "QNH") buchstabieren, alles andere bleibt.</summary>
    public static string Literal(string text) =>
        AcronymRegex().Replace(text, m => SpelledAcronyms.Contains(m.Value) ? string.Join(' ', m.Value.ToCharArray()) : m.Value);

    // ------------------------------------------------------------------ Werte

    /// <summary>
    /// Höhe nach ICAO: über der Übergangshöhe als Flight Level ("flight level one one zero"), sonst in Fuß
    /// ("five thousand feet", "one zero thousand feet", "two thousand five hundred feet"). Auf 100 ft gerundet.
    /// </summary>
    public static AtcValue Altitude(double feet, double transitionAltitudeFt)
    {
        var rounded = (int)(Math.Round(feet / 100.0, MidpointRounding.AwayFromZero) * 100);
        if (rounded > transitionAltitudeFt)
        {
            var level = rounded / 100;
            return new AtcValue(AtcValueKind.Altitude, "FL" + level.ToString("000", Inv), "flight level " + Digits(level, 2))
            {
                Number = rounded,
                FlightLevel = true,
            };
        }

        return new AtcValue(AtcValueKind.Altitude, rounded.ToString(Inv) + " ft", FeetWords(rounded) + " feet") { Number = rounded };
    }

    /// <summary>Fuß ohne Einheit: 5000 → "five thousand", 12500 → "one two thousand five hundred".</summary>
    public static string FeetWords(int feet)
    {
        var thousands = feet / 1000;
        var hundreds = feet % 1000 / 100;
        var parts = new List<string>(2);
        if (thousands > 0)
        {
            parts.Add(Digits(thousands) + " thousand");
        }

        if (hundreds > 0)
        {
            parts.Add(Digit((char)('0' + hundreds)) + " hundred");
        }

        return parts.Count == 0 ? "zero" : string.Join(' ', parts);
    }

    public static AtcValue Squawk(string code) =>
        new(AtcValueKind.Squawk, code, Spell(code)) { Code = code, Number = int.Parse(code, Inv) };

    /// <summary>Frequenz: 121.8 → "one two one decimal eight", 118.275 → "one one eight decimal two seven five".</summary>
    public static AtcValue Frequency(double mhz)
    {
        var khz = (long)Math.Round(mhz * 1000, MidpointRounding.AwayFromZero);
        var whole = khz / 1000;
        var fraction = (khz % 1000).ToString("000", Inv).TrimEnd('0');
        if (fraction.Length == 0)
        {
            fraction = "0";
        }

        return new AtcValue(AtcValueKind.Frequency, $"{whole.ToString(Inv)}.{fraction}", $"{Digits(whole)} decimal {Spell(fraction)}")
        {
            Number = khz / 1000.0,
        };
    }

    /// <summary>Geschwindigkeit in Knoten, Ziffer für Ziffer ("two five zero"). Die Einheit steht in der Vorlage.</summary>
    public static AtcValue Speed(double knots)
    {
        var kt = (int)Math.Round(knots, MidpointRounding.AwayFromZero);
        return new AtcValue(AtcValueKind.Speed, kt.ToString(Inv), Digits(kt)) { Number = kt };
    }

    public static AtcValue Heading(double degrees)
    {
        var h = (int)Math.Round(degrees, MidpointRounding.AwayFromZero) % 360;
        if (h <= 0)
        {
            h += 360;
        }

        return new AtcValue(AtcValueKind.Heading, h.ToString("000", Inv), Digits(h, 3)) { Number = h };
    }

    /// <summary>Wind "two seven zero degrees, one two knots"; unter 3 kt "calm". Richtung auf 10° gerundet.</summary>
    public static AtcValue Wind(double directionDeg, double speedKt)
    {
        var kt = (int)Math.Round(speedKt, MidpointRounding.AwayFromZero);
        if (kt < 3)
        {
            return new AtcValue(AtcValueKind.Wind, "calm", "calm") { Number = 0 };
        }

        var dir = (int)(Math.Round(directionDeg / 10.0, MidpointRounding.AwayFromZero) * 10) % 360;
        if (dir <= 0)
        {
            dir += 360;
        }

        return new AtcValue(AtcValueKind.Wind, $"{dir.ToString("000", Inv)}/{kt.ToString(Inv)}", $"{Digits(dir, 3)} degrees, {Digits(kt)} knots")
        {
            Number = dir,
        };
    }

    /// <summary>"QNH one zero one three" oder in Nordamerika "altimeter two niner niner two".</summary>
    public static AtcValue Qnh(double hpa, bool inchesOfMercury)
    {
        if (inchesOfMercury)
        {
            var hundredths = (int)Math.Round(hpa * 0.0295299830714 * 100, MidpointRounding.AwayFromZero);
            var code = hundredths.ToString("0000", Inv);
            return new AtcValue(AtcValueKind.Qnh, "A" + code, "altimeter " + Spell(code)) { Number = hundredths };
        }

        var q = (int)Math.Round(hpa, MidpointRounding.AwayFromZero);
        return new AtcValue(AtcValueKind.Qnh, "Q" + q.ToString(Inv), "Q N H " + Digits(q)) { Number = q };
    }

    /// <summary>UTC-Zeit "1234Z", gesprochen "one two three four".</summary>
    public static AtcValue Time(TimeSpan timeOfDay)
    {
        var t = TimeSpan.FromMinutes(Math.Round(timeOfDay.TotalMinutes) % (24 * 60));
        var code = t.ToString("hhmm", Inv);
        return new AtcValue(AtcValueKind.Time, code + "Z", Spell(code)) { Code = code };
    }

    /// <summary>
    /// Rufzeichen: ICAO-Kennung der Airline wird durch das Telephony-Rufzeichen ersetzt, der Rest buchstabiert
    /// ("DLH4AB" → "Lufthansa four alpha bravo"). Unbekannte Kennungen werden ganz buchstabiert.
    /// </summary>
    public static AtcValue Callsign(string callsign, IReadOnlyDictionary<string, string> telephony)
    {
        var code = Compact(callsign);
        if (code.Length >= 4 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]) && char.IsAsciiLetter(code[2])
            && telephony.TryGetValue(code[..3], out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return new AtcValue(AtcValueKind.Callsign, code, name.Trim() + " " + Spell(code[3..])) { Code = code };
        }

        return new AtcValue(AtcValueKind.Callsign, code, Spell(code)) { Code = code };
    }

    /// <summary>Flughafen: angezeigt als ICAO-Code, gesprochen mit Namen ("Berlin").</summary>
    public static AtcValue Airport(string icao, string? name)
    {
        var spoken = string.IsNullOrWhiteSpace(name) ? Spell(icao) : name.Trim();
        return new AtcValue(AtcValueKind.Airport, icao.ToUpperInvariant(), spoken) { Code = Compact(icao) };
    }

    public static AtcValue Runway(int number, string? designator)
    {
        var code = number.ToString("00", Inv) + designator;
        return new AtcValue(AtcValueKind.Runway, code, Phraseology.Runway(number, designator)) { Code = code, Number = number };
    }

    public static AtcValue? Runway(string? ident) =>
        RunwayMath.Parse(ident) is { } r ? Runway(r.Number, r.Designator) : null;

    /// <summary>Anflugart: "ILS" → "I L S", "RNP Z" → "R N P zulu".</summary>
    public static AtcValue Approach(string approach)
    {
        var words = approach.Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        var spoken = words.Select(w => ApproachWords.TryGetValue(w, out var s) ? s : w.Length == 1 || w.Any(char.IsAsciiDigit) ? Spell(w) : TitleCase(w));
        return new AtcValue(AtcValueKind.Approach, approach.Trim().ToUpperInvariant(), string.Join(' ', spoken)) { Code = Compact(approach) };
    }

    /// <summary>
    /// Wegpunkt oder Verfahren: "MARUN7G" → "Marun seven golf", "TOBAK" → "Tobak", "FFM" → "foxtrot foxtrot mike",
    /// "A26"/"N3" → "alpha two six"/"november three".
    /// </summary>
    public static AtcValue Ident(string ident)
    {
        var code = Compact(ident);
        string spoken;
        if (ProcedureRegex().Match(code) is { Success: true } m)
        {
            spoken = TitleCase(m.Groups[1].Value) + " " + Digit(m.Groups[2].Value[0]) + (m.Groups[3].Success ? " " + Letter(m.Groups[3].Value[0]) : "");
        }
        else if (code.All(char.IsAsciiLetter))
        {
            spoken = code.Length <= 3 ? Spell(code) : TitleCase(code);
        }
        else
        {
            spoken = Spell(code);
        }

        return new AtcValue(AtcValueKind.Ident, code, spoken) { Code = code };
    }

    /// <summary>Koordinate für Oceanic-Meldungen: 55°N 20°W → "55N020W", gesprochen "five five north two zero west".</summary>
    public static AtcValue Position(double lat, double lon)
    {
        var (latDeg, latMin) = DegMin(lat);
        var (lonDeg, lonMin) = DegMin(lon);
        var ns = lat >= 0 ? "N" : "S";
        var ew = lon >= 0 ? "E" : "W";
        var latText = latDeg.ToString("00", Inv) + (latMin > 0 ? latMin.ToString("00", Inv) : "");
        var lonText = lonDeg.ToString("000", Inv) + (lonMin > 0 ? lonMin.ToString("00", Inv) : "");
        var spokenLat = Digits(latDeg) + (latMin > 0 ? " " + Digits(latMin, 2) : "");
        var spokenLon = Digits(lonDeg) + (lonMin > 0 ? " " + Digits(lonMin, 2) : "");
        var written = latText + ns + lonText + ew;
        var shortLon = lonDeg.ToString(Inv) + (lonMin > 0 ? lonMin.ToString("00", Inv) : "");
        var words = $"{latText}{(ns == "N" ? "NORTH" : "SOUTH")}{shortLon}{(ew == "E" ? "EAST" : "WEST")}";
        return new AtcValue(
            AtcValueKind.Position,
            written,
            $"{spokenLat} {(ns == "N" ? "north" : "south")} {spokenLon} {(ew == "E" ? "east" : "west")}")
        {
            Code = written,
            Alternatives = [latText + ns + shortLon + ew, words, $"{latText}{(ns == "N" ? "NORTH" : "SOUTH")}{lonText}{(ew == "E" ? "EAST" : "WEST")}"],
        };
    }

    /// <summary>Rollweg-Liste "N, N3" → "november, november three".</summary>
    public static AtcValue TaxiRoute(IReadOnlyList<string> taxiways)
    {
        var clean = taxiways.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).ToList();
        return new AtcValue(AtcValueKind.Ident, string.Join(", ", clean), string.Join(", ", clean.Select(Spell)))
        {
            Code = string.Concat(clean.Select(Compact)),
        };
    }

    /// <summary>Erkennt Koordinaten-Wegpunkte wie "5520N", "55N020W" oder "N55W020".</summary>
    public static bool IsLatLonIdent(string? ident) =>
        ident is not null && LatLonRegex().IsMatch(ident.Trim().ToUpperInvariant());

    public static string TitleCase(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static (int Deg, int Min) DegMin(double value)
    {
        var abs = Math.Abs(value);
        var totalMinutes = (int)Math.Round(abs * 60, MidpointRounding.AwayFromZero);
        return (totalMinutes / 60, totalMinutes % 60);
    }

    [GeneratedRegex(@"\b[A-Z]{2,4}\b")]
    private static partial Regex AcronymRegex();

    [GeneratedRegex(@"^([A-Z]{2,6})(\d)([A-Z])?$")]
    private static partial Regex ProcedureRegex();

    [GeneratedRegex(@"^(?=(?:.*\d){4})(?=.*[NS])[0-9NSEW/]{5,14}$")]
    private static partial Regex LatLonRegex();
}
