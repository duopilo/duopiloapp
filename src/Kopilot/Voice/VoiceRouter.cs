using System.Text.RegularExpressions;

namespace Kopilot.Voice;

public enum VoiceTarget
{
    /// <summary>Befehl oder Abfrage an Kopilot.</summary>
    Kopilot,

    /// <summary>Funkspruch an eine ATC-Station (→ ATC-Modul).</summary>
    Atc,
}

/// <summary>Wohin ein Satz geht. Bei ATC: erkannte Station und/oder eigenes Rufzeichen.</summary>
public sealed record VoiceRoute(VoiceTarget Target, string? Station = null, string? Callsign = null, string Reason = "");

/// <summary>
/// Ordnet einen normalisierten Satz zu:
/// enthält er ein Stationsrufzeichen ("Frankfurt Tower", "Langen Radar", "Shanwick") oder das eigene Rufzeichen
/// (am Anfang beim Anruf, hinten beim Readback) → ATC-Modul, sonst → Kopilot. Mit Anrede ("Kopilot, …") immer Kopilot.
/// </summary>
public sealed partial class VoiceRouter
{
    // Wörter vor "Ground", "Approach", "Radar" …, die keine Station sind ("ground spoilers", "arm approach", "fuel center")
    private static readonly HashSet<string> NotAStationName = new(StringComparer.Ordinal)
    {
        "arm", "armed", "disarm", "on", "off", "an", "aus", "set", "select", "selected", "managed", "push", "pull", "the", "a", "and", "und",
        "to", "auf", "flaps", "gear", "speed", "heading", "altitude", "fl", "loc", "lights", "landing", "fuel", "tank", "engine", "my",
        "your", "our", "final", "missed", "visual", "ils", "rnav", "rnp", "kopilot", "check", "request", "for", "with", "of", "in",
        "der", "die", "das", "den", "dem", "mit", "zum", "zur", "vom", "im",
        // Funk: "contact ground", "monitor tower", "cleared ILS approach", "expect vectors approach"
        "contact", "monitor", "call", "switching", "cleared", "expect", "via", "taxi", "established", "vectors", "leaving", "join",
    };

    // Ozeanische und andere Stationen, die ohne Zusatz gerufen werden
    private static readonly string[] StandaloneStations =
    [
        "shanwick", "gander", "santa maria", "reykjavik", "bodo", "bodø", "new york oceanic", "shanwick oceanic", "gander oceanic",
    ];

    private readonly IReadOnlyList<string> _wakeWords;
    private readonly IReadOnlyList<string> _extraStations;

    /// <param name="wakeWords">Anrede für Kopilot (normalisiert), z. B. "kopilot".</param>
    /// <param name="extraStations">Weitere Stationen (Einstellungen, ATC-Modul), z. B. "Langen Information".</param>
    public VoiceRouter(IEnumerable<string>? wakeWords = null, IEnumerable<string>? extraStations = null)
    {
        _wakeWords = (wakeWords ?? ["kopilot"]).Select(VoiceText.Normalize).Where(w => w.Length > 0).Distinct().ToList();
        _extraStations = Normalized(extraStations ?? []).Distinct().OrderByDescending(s => s.Length).ToList();
    }

    /// <param name="normalized">Satz in der Form von <see cref="VoiceText.Normalize"/>.</param>
    /// <param name="ownCallsigns">Eigene Rufzeichen (beliebige Schreibweise, z. B. "Lufthansa four alpha bravo", "DLH4AB", "D-AIAB").</param>
    /// <param name="stations">Stationen des Flugs vom ATC-Modul (z. B. "Frankfurt Delivery", "Langen Radar").</param>
    public VoiceRoute Route(string normalized, IEnumerable<string>? ownCallsigns = null, IEnumerable<string>? stations = null)
    {
        var words = VoiceText.Words(normalized);
        if (words.Length == 0)
        {
            return new VoiceRoute(VoiceTarget.Kopilot, Reason: "leer");
        }

        if (_wakeWords.Contains(words[0]))
        {
            return new VoiceRoute(VoiceTarget.Kopilot, Reason: "Anrede");
        }

        var known = stations is null ? _extraStations : Normalized(stations).Concat(_extraStations).Distinct().OrderByDescending(x => x.Length).ToList();
        var station = FindStation(normalized, known);

        // Eigenes Rufzeichen: am Anfang (Erstanruf, Meldung) oder dahinter (Readback "…, Lufthansa four alpha bravo")
        if (MatchCallsign(normalized, ownCallsigns) is { } match)
        {
            var reason = match.AtStart ? "Rufzeichen am Anfang" : station is not null ? "Station" : "Rufzeichen im Readback";
            return new VoiceRoute(VoiceTarget.Atc, station, match.Callsign, reason);
        }

        if (station is not null)
        {
            return new VoiceRoute(VoiceTarget.Atc, station, Reason: "Station");
        }

        return new VoiceRoute(VoiceTarget.Kopilot, Reason: "kein Stationsrufzeichen");
    }

    /// <summary>Stationsrufzeichen im Satz, z. B. "frankfurt tower" – oder null.</summary>
    public string? FindStation(string normalized) => FindStation(normalized, _extraStations);

    private static string? FindStation(string normalized, IReadOnlyList<string> known)
    {
        var padded = " " + normalized + " ";
        foreach (var extra in known)
        {
            if (padded.Contains(" " + extra + " ", StringComparison.Ordinal))
            {
                return extra;
            }
        }

        foreach (var standalone in StandaloneStations)
        {
            if (padded.Contains(" " + standalone + " ", StringComparison.Ordinal))
            {
                return standalone;
            }
        }

        foreach (Match m in StationPattern().Matches(normalized))
        {
            var name = m.Groups["name"].Value;
            if (name.Length < 3 || NotAStationName.Contains(name))
            {
                continue;
            }

            var prefix = m.Groups["prefix"].Success && !NotAStationName.Contains(m.Groups["prefix"].Value) ? m.Groups["prefix"].Value + " " : "";
            return prefix + name + " " + m.Groups["unit"].Value;
        }

        return null;
    }

    /// <summary>
    /// Kommt eines der eigenen Rufzeichen im Satz vor? Vergleich in kompakter Form ("lufthansa4ab").
    /// Am Anfang reichen 3 Zeichen, sonst mindestens 5 – ein Kopilot-Befehl enthält nie das eigene Rufzeichen.
    /// </summary>
    public static (string Callsign, bool AtStart)? MatchCallsign(string normalized, IEnumerable<string>? ownCallsigns)
    {
        if (ownCallsigns is null)
        {
            return null;
        }

        var compact = VoiceText.Compact(normalized);
        (string, bool)? inside = null;
        foreach (var callsign in ownCallsigns)
        {
            var c = VoiceText.Compact(callsign);
            if (c.Length >= 3 && compact.StartsWith(c, StringComparison.Ordinal))
            {
                return (callsign.Trim(), true);
            }

            if (inside is null && c.Length >= 5 && compact.Contains(c, StringComparison.Ordinal))
            {
                inside = (callsign.Trim(), false);
            }
        }

        return inside;
    }

    private static IEnumerable<string> Normalized(IEnumerable<string> names) =>
        names.Select(VoiceText.Normalize).Where(n => n.Length > 0);

    // Name (ein Wort, bei "New York"/"Santa Maria"/"San Francisco" zwei) + Stationsart
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?<prefix>new|san|santa|st|saint|bad|sankt|la|le|los|las|el|al|port|porto|rio|sao|são|hong) )?(?<name>[\p{L}]+) (?<unit>tower|turm|ground|rollkontrolle|delivery|clearance|apron|vorfeld|approach|anflug|departure|abflug|director|radar|control|kontrolle|center|centre|information|radio|arrival|oceanic|platz)(?![\p{L}\p{N}])")]
    private static partial Regex StationPattern();
}
