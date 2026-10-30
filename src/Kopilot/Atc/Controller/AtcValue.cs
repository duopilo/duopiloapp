using System.Globalization;
using System.Text.RegularExpressions;
using Kopilot.Speech;

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
/// Werte für die ATC-Vorlagen: Anzeigetext, Sprechform und Vergleichswert für die Readback-Prüfung. Die Sprechform kommt
/// immer aus der gemeinsamen Aussprache (<see cref="Pronunciation"/>) – dieselbe wie für Callouts, Fuel Check und die
/// Sprachschleife: Zahlen Ziffer für Ziffer ("two two zero five"), Buchstaben im NATO-Alphabet, Geschwindigkeiten in Knoten.
/// </summary>
public static partial class AtcSpeech
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

    public static string Digit(char c) => Pronunciation.Digit(c);

    public static string Letter(char c) => Pronunciation.Letter(c);

    /// <summary>Ziffern und Buchstaben einzeln: "4AB" → "four alpha bravo". Andere Zeichen fallen weg.</summary>
    public static string Spell(string text) => Pronunciation.Spell(text);

    /// <summary>Zahl Ziffer für Ziffer, z. B. 250 → "two five zero", 5 → "zero five" bei <paramref name="minDigits"/> = 2.</summary>
    public static string Digits(long value, int minDigits = 1) => Pronunciation.Digits(value, minDigits);

    /// <summary>Kennung als Code zum Vergleichen: nur Buchstaben und Ziffern, groß.</summary>
    public static string Compact(string text) => Pronunciation.Compact(text);

    /// <summary>Fester Vorlagentext für die Sprachausgabe (Abkürzungen wie "IFR" buchstabiert, Zahlen ausgesprochen), Leerzeichen am Rand bleiben.</summary>
    public static string Literal(string text) => Pronunciation.Fragment(text);

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
            return new AtcValue(AtcValueKind.Altitude, "FL" + level.ToString("000", Inv), Pronunciation.FlightLevel(level))
            {
                Number = rounded,
                FlightLevel = true,
            };
        }

        return new AtcValue(AtcValueKind.Altitude, rounded.ToString(Inv) + " ft", Pronunciation.Feet(rounded)) { Number = rounded };
    }

    public static AtcValue Squawk(string code) =>
        new(AtcValueKind.Squawk, code, Pronunciation.Squawk(code)) { Code = code, Number = int.Parse(code, Inv) };

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

        return new AtcValue(AtcValueKind.Frequency, $"{whole.ToString(Inv)}.{fraction}", Pronunciation.Frequency(mhz))
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

        return new AtcValue(AtcValueKind.Heading, h.ToString("000", Inv), Pronunciation.Heading(h)) { Number = h };
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
    /// ("DLH4AB" → "Lufthansa four alpha bravo"). Nach der Kennung muss eine Ziffer folgen – sonst ist es ein
    /// Kennzeichen wie "D-EABC", das ganz buchstabiert wird (mit der vollen Airline-Datenbank ist fast jede
    /// Buchstabenfolge irgendeine Airline). Unbekannte Kennungen werden ganz buchstabiert.
    /// </summary>
    public static AtcValue Callsign(string callsign, IReadOnlyDictionary<string, string> telephony)
    {
        var code = Compact(callsign);
        if (code.Length >= 4 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]) && char.IsAsciiLetter(code[2])
            && char.IsAsciiDigit(code[3])
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
        return new AtcValue(AtcValueKind.Runway, code, Pronunciation.Runway(number, designator)) { Code = code, Number = number };
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
        return new AtcValue(AtcValueKind.Ident, code, Pronunciation.Ident(code)) { Code = code };
    }

    /// <summary>
    /// Fix des OFP-Navlogs: gesprochen wie in <see cref="Pronunciation.Waypoint"/> – Funkfeuer mit Namen ("YWK" → "Wabush"),
    /// fünf Buchstaben als Wort, sonst im NATO-Alphabet. Im Readback gilt auch der Name ("direct Wabush").
    /// </summary>
    public static AtcValue Fix(string ident, string? name, double? lat = null, double? lon = null)
    {
        var code = Compact(ident);
        var spoken = Pronunciation.Waypoint(code, name, lat, lon);
        var alternatives = !string.IsNullOrWhiteSpace(name) && Compact(name) is { Length: >= 3 } compactName && compactName != code
            ? new[] { compactName }
            : [];
        return new AtcValue(AtcValueKind.Ident, code, spoken) { Code = code, Alternatives = alternatives };
    }

    /// <summary>
    /// Koordinate für Oceanic-Meldungen: 55°N 20°W → "55N020W", gesprochen "fifty five north twenty west". Im Readback gilt
    /// auch "55 north 20 west" bzw. Ziffer für Ziffer.
    /// </summary>
    public static AtcValue Position(double lat, double lon)
    {
        var (latDeg, latMin) = DegMin(lat);
        var (lonDeg, lonMin) = DegMin(lon);
        var ns = lat >= 0 ? "N" : "S";
        var ew = lon >= 0 ? "E" : "W";
        var latText = latDeg.ToString("00", Inv) + (latMin > 0 ? latMin.ToString("00", Inv) : "");
        var lonText = lonDeg.ToString("000", Inv) + (lonMin > 0 ? lonMin.ToString("00", Inv) : "");
        var written = latText + ns + lonText + ew;
        var shortLon = lonDeg.ToString(Inv) + (lonMin > 0 ? lonMin.ToString("00", Inv) : "");
        var words = $"{latText}{(ns == "N" ? "NORTH" : "SOUTH")}{shortLon}{(ew == "E" ? "EAST" : "WEST")}";
        return new AtcValue(
            AtcValueKind.Position,
            written,
            Pronunciation.Coordinate(lat, lon))
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

    public static string TitleCase(string word) => Pronunciation.TitleCase(word);

    private static (int Deg, int Min) DegMin(double value)
    {
        var abs = Math.Abs(value);
        var totalMinutes = (int)Math.Round(abs * 60, MidpointRounding.AwayFromZero);
        return (totalMinutes / 60, totalMinutes % 60);
    }

    [GeneratedRegex(@"^(?=(?:.*\d){4})(?=.*[NS])[0-9NSEW/]{5,14}$")]
    private static partial Regex LatLonRegex();
}
