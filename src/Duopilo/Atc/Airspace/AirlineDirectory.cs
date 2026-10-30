using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Duopilo.Atc.Airspace;

/// <summary>
/// Telephony-Rufzeichen aller Airlines aus der OpenFlights-Datenbank (airlines.dat, Originalformat): ICAO-Kennung →
/// Rufzeichen in normaler Schreibweise ("DLH" → "Lufthansa", "ACA" → "Air Canada"). Aktive Airlines haben Vorrang vor
/// eingestellten mit derselben Kennung. Ausnahmen und Korrekturen stehen in atc-templates.json unter "telephony".
/// </summary>
public sealed class AirlineDirectory
{
    public static readonly AirlineDirectory Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);

    private AirlineDirectory(Dictionary<string, string> telephony, string? source)
    {
        Telephony = telephony;
        Source = source;
    }

    /// <summary>ICAO-Kennung → Rufzeichen (Groß-/Kleinschreibung der Kennung egal).</summary>
    public IReadOnlyDictionary<string, string> Telephony { get; }

    public string? Source { get; }

    public int Count => Telephony.Count;

    /// <summary>
    /// airlines.dat: "ID","Name","Alias","IATA","ICAO","Callsign","Country","Active" je Zeile, \N für leer. Zeilen ohne
    /// dreistellige ICAO-Kennung oder ohne brauchbares Rufzeichen werden übersprungen.
    /// </summary>
    public static AirlineDirectory Parse(TextReader reader, string? source = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var fields = SplitCsv(line);
            if (fields.Count < 8)
            {
                continue;
            }

            var icao = Field(fields[4])?.Trim().ToUpperInvariant();
            var callsign = TelephonyName(Field(fields[5]));
            if (icao is not { Length: 3 } || !icao.All(char.IsAsciiLetterUpper) || callsign is null)
            {
                continue;
            }

            var isActive = Field(fields[7]) == "Y";
            if (!result.ContainsKey(icao) || (isActive && !active.Contains(icao)))
            {
                result[icao] = callsign;
                if (isActive)
                {
                    active.Add(icao);
                }
            }
        }

        return new AirlineDirectory(result, source);
    }

    /// <summary>
    /// Rufzeichen aus OpenFlights ("AIR CANADA") in normaler Schreibweise ("Air Canada"). Null für Leeres und offensichtlich
    /// verrutschte Felder (führendes Leerzeichen, Punkt wie in " Inc.").
    /// </summary>
    public static string? TelephonyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || char.IsWhiteSpace(raw[0]) || raw.Contains('.') || !char.IsLetter(raw[0]))
        {
            return null;
        }

        var text = raw.Trim();
        var sb = new StringBuilder(text.Length);
        var startOfWord = true;
        foreach (var c in text)
        {
            sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = c is ' ' or '-' or '/';
        }

        return sb.ToString();
    }

    private static string? Field(string value) => value is "\\N" or "" ? null : value;

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}

/// <summary>
/// Rufzeichen-Nachschlag für den Funk: zuerst die Ausnahmen aus atc-templates.json ("telephony"), dann die Airline-Datenbank.
/// </summary>
public sealed class TelephonyView(IReadOnlyDictionary<string, string> overrides, IReadOnlyDictionary<string, string> airlines)
    : IReadOnlyDictionary<string, string>
{
    public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => this.Select(p => p.Key);

    public IEnumerable<string> Values => this.Select(p => p.Value);

    public int Count
    {
        get
        {
            var count = 0;
            using var e = GetEnumerator();
            while (e.MoveNext())
            {
                count++;
            }

            return count;
        }
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        if (overrides.TryGetValue(key, out var own) && !string.IsNullOrWhiteSpace(own))
        {
            value = own;
            return true;
        }

        if (airlines.TryGetValue(key, out var fromDatabase) && !string.IsNullOrWhiteSpace(fromDatabase))
        {
            value = fromDatabase;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach (var pair in overrides)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                yield return pair;
            }
        }

        foreach (var pair in airlines)
        {
            if (!overrides.ContainsKey(pair.Key))
            {
                yield return pair;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
