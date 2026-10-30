using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Kopilot.Atc;

/// <summary>
/// Überwachung von Transpondercode und COM1-Frequenz gegen die Zuweisung ("check squawk/frequency, should be …") und
/// Funksprüche an die falsche Station ("this is Montreal Center"). MSFS setzt Squawk und Frequenz teils selbst, auch bei
/// stumm geschaltetem Sim-ATC – Kopilot weist darauf hin, setzt aber nie selbst zurück.
/// </summary>
public sealed partial class AtcSession
{
    /// <summary>
    /// Gleiche Frequenz: 8,33-kHz-Kanalnamen weichen bis 5 kHz von der Frequenz ab (132.605 = 132.600), der nächste Kanal
    /// liegt 8,33 kHz daneben.
    /// </summary>
    private const double FrequencyToleranceMhz = 0.006;

    /// <summary>Notfallcodes: kein "check squawk" – das ist Absicht des Piloten.</summary>
    private static readonly HashSet<string> EmergencySquawks = new(StringComparer.Ordinal) { "7500", "7600", "7700" };

    /// <summary>Keine vergebenen Codes (VFR, Conspicuity, Mode S) – werden mit SquawkFromSim nicht übernommen.</summary>
    private static readonly HashSet<string> GenericSquawks = new(StringComparer.Ordinal) { "0000", "1000", "1200", "2000", "7000" };

    /// <summary>Wörter einer Station, die nur die Art nennen ("Montreal Center" → nur "montreal" zählt).</summary>
    private static readonly HashSet<string> UnitWords = new(StringComparer.Ordinal)
    {
        "control", "radar", "center", "centre", "oceanic", "information", "radio", "approach", "departure", "delivery", "ground", "tower",
        "clearance", "director", "apron", "arrival", "arrivals", "departures", "area", "flight", "service", "services", "terminal", "ramp",
        "turm", "rollkontrolle", "vorfeld", "anflug", "abflug", "kontrolle",
    };

    /// <summary>Wörter am Anfang eines Funkspruchs, die kein Stationsname sind (Gruß, Quittung, Anfang einer Meldung).</summary>
    private static readonly HashSet<string> LeadingFillers = new(StringComparer.Ordinal)
    {
        "good", "morning", "afternoon", "evening", "day", "night", "hello", "hi", "hey", "servus", "moin", "bonjour",
        "roger", "wilco", "affirm", "affirmative", "negative", "copy", "copied", "check", "and", "the", "with", "on", "to", "for",
        "this", "is", "again", "contact", "monitor", "request", "requesting", "ready", "passing", "maintaining", "climbing",
        "descending", "position", "level", "fl", "say", "confirm", "unable", "standby", "climb", "descend", "maintain", "cleared",
        "direct", "squawk", "squawking", "radio", "how", "do", "you", "read", "now", "we", "are", "at", "of", "via",
        "bye", "goodbye", "cheers", "thanks", "thank", "over",
    };

    private string? _squawkSeen;
    private DateTimeOffset? _squawkMismatchSince;
    private DateTimeOffset? _squawkNotifiedUtc;
    private int _squawkCalls;

    private double? _frequencySeen;
    private DateTimeOffset? _frequencyMismatchSince;
    private DateTimeOffset? _frequencyNotifiedUtc;
    private int _frequencyCalls;

    /// <summary>Ab wann COM1 auf der Frequenz der Station sein muss (nach dem ersten Anruf sofort, nach einer Übergabe nach der Schonfrist).</summary>
    private DateTimeOffset? _frequencyExpectedFrom;

    // Letzte Übergabe: meldet sich der Pilot danach noch bei der alten Station, wiederholt sie die Übergabe
    private AtcStation? _handoffFrom;
    private string? _handoffMessageId;
    private DateTimeOffset? _handoffAtUtc;

    private readonly List<AtcDeviationEvent> _deviationEvents = [];

    /// <summary>Herkunft des Squawks: "kopilot" (pro Flug fest) oder "sim" (aus dem Transponder übernommen, SquawkFromSim).</summary>
    public string SquawkSource { get; private set; } = "kopilot";

    /// <summary>Transponder weicht vom zugewiesenen Code ab und ATC hat es angesprochen (null = passt).</summary>
    public AtcDeviation? SquawkDeviation { get; private set; }

    /// <summary>COM1 steht nicht auf der Frequenz der Station und ATC hat es angesprochen (null = passt oder nicht geprüft).</summary>
    public AtcDeviation? FrequencyDeviation { get; private set; }

    /// <summary>Was sich seit dem letzten Aufruf an den Abweichungen geändert hat (für den AlertBus).</summary>
    public IReadOnlyList<AtcDeviationEvent> TakeDeviationEvents()
    {
        if (_deviationEvents.Count == 0)
        {
            return [];
        }

        var events = _deviationEvents.ToList();
        _deviationEvents.Clear();
        return events;
    }

    /// <summary>Gültiger Squawk: vier Oktalziffern.</summary>
    public static bool IsValidSquawk([NotNullWhen(true)] string? code) => code is { Length: 4 } && code.All(c => c is >= '0' and <= '7');

    // ------------------------------------------------------------------ Transponder

    /// <summary>
    /// Im Flug: weicht der Transpondercode länger als <see cref="AtcRules.DeviationSeconds"/> vom zugewiesenen ab, sagt ATC
    /// "check squawk, should be …" (wiederholt nach <see cref="AtcRules.DeviationRepeatSeconds"/>, höchstens
    /// <see cref="AtcRules.DeviationMaxCalls"/>-mal). Mit SquawkFromSim übernimmt Kopilot stattdessen einen vergebenen Code.
    /// </summary>
    private void CheckSquawk(AtcInput input, List<AtcTransmission> output)
    {
        var code = input.TransponderCode;
        var adopt = _context.SquawkFromSim;
        if ((!Rules.CheckSquawk && !adopt) || !IsValidSquawk(code) || input.OnGround != false
            || Phase is AtcPhase.Inactive or AtcPhase.Done or AtcPhase.Delivery or AtcPhase.GroundDeparture
            || code == Squawk || EmergencySquawks.Contains(code!))
        {
            ResetSquawkCheck();
            return;
        }

        var now = input.TimeUtc;
        if (_squawkSeen != code)
        {
            // Neuer Wert: der Pilot dreht vielleicht noch – Wartezeit beginnt neu
            _squawkSeen = code;
            _squawkMismatchSince = now;
        }

        if (_squawkMismatchSince is not { } since || (now - since).TotalSeconds < Rules.DeviationSeconds)
        {
            return;
        }

        if (adopt && !GenericSquawks.Contains(code!))
        {
            var previous = Squawk;
            Squawk = code!;
            SquawkSource = "sim";
            ResetSquawkCheck();
            _deviationEvents.Add(new AtcDeviationEvent(AtcDeviationKind.Squawk, AtcDeviationChange.Adopted, Squawk, Squawk, null, previous));
            return;
        }

        if (!Rules.CheckSquawk)
        {
            return;
        }

        var deviation = SquawkDeviation;
        if (deviation is null || deviation.Actual != code)
        {
            if (deviation is null)
            {
                _deviationEvents.Add(new AtcDeviationEvent(AtcDeviationKind.Squawk, AtcDeviationChange.Raised, Squawk, code, null));
            }

            deviation = new AtcDeviation(AtcDeviationKind.Squawk, Squawk, code, null, since, _squawkCalls, [new AtcSuggestedAction("xpdr.code", Squawk)]);
        }

        if (DeviationCallDue(_squawkNotifiedUtc, _squawkCalls, now) && PendingReadback is null && output.Count == 0
            && Say(output, AtcMessageIds.CommonCheckSquawk, null) is not null)
        {
            _squawkNotifiedUtc = now;
            _squawkCalls++;
            deviation = deviation with { Calls = _squawkCalls };
        }

        SquawkDeviation = deviation;
    }

    private void ResetSquawkCheck()
    {
        if (SquawkDeviation is { } deviation)
        {
            _deviationEvents.Add(new AtcDeviationEvent(AtcDeviationKind.Squawk, AtcDeviationChange.Cleared, deviation.Expected, null, null));
        }

        SquawkDeviation = null;
        _squawkSeen = null;
        _squawkMismatchSince = null;
        _squawkNotifiedUtc = null;
        _squawkCalls = 0;
    }

    // ------------------------------------------------------------------ COM1

    /// <summary>
    /// Steht COM1 länger als <see cref="AtcRules.DeviationSeconds"/> nicht auf der Frequenz der Station, sagt ATC
    /// "check frequency, should be …". Geprüft wird erst nach dem ersten Anruf bei der Station bzw. nach der Schonfrist einer
    /// Übergabe – und nie bei Platzhalter-Frequenzen oder Oceanic (dort HF).
    /// </summary>
    private void CheckFrequency(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var com = input.Com1ActiveMhz;
        if (!Rules.CheckFrequency || com is not { } active || active < 100 || Station.Frequency.Number is not { } expected
            || Station.FrequencySource == "placeholder" || Station.Type == AtcStationType.Oceanic
            || Phase is AtcPhase.Inactive or AtcPhase.Done
            || _frequencyExpectedFrom is not { } from || now < from
            || Math.Abs(active - expected) < FrequencyToleranceMhz)
        {
            ResetFrequencyCheck();
            return;
        }

        if (_frequencySeen is not { } seen || Math.Abs(seen - active) > 0.001)
        {
            _frequencySeen = active;
            _frequencyMismatchSince = now;
        }

        if (_frequencyMismatchSince is not { } since || (now - since).TotalSeconds < Rules.DeviationSeconds)
        {
            return;
        }

        var expectedText = Station.Frequency.Written;
        var actualText = AtcSpeech.Frequency(active).Written;
        var deviation = FrequencyDeviation;
        if (deviation is null || deviation.Actual != actualText || deviation.Expected != expectedText)
        {
            if (deviation is null)
            {
                _deviationEvents.Add(new AtcDeviationEvent(AtcDeviationKind.Frequency, AtcDeviationChange.Raised, expectedText, actualText, Station.Name));
            }

            var standby = expected.ToString("0.000", CultureInfo.InvariantCulture);
            deviation = new AtcDeviation(
                AtcDeviationKind.Frequency,
                expectedText,
                actualText,
                Station.Name,
                since,
                _frequencyCalls,
                [new AtcSuggestedAction("com1.standby", standby), new AtcSuggestedAction("com1.swap", null)]);
        }

        if (DeviationCallDue(_frequencyNotifiedUtc, _frequencyCalls, now) && PendingReadback is null && output.Count == 0
            && Say(output, AtcMessageIds.CommonCheckFrequency, null) is not null)
        {
            _frequencyNotifiedUtc = now;
            _frequencyCalls++;
            deviation = deviation with { Calls = _frequencyCalls };
        }

        FrequencyDeviation = deviation;
    }

    private void ResetFrequencyCheck()
    {
        if (FrequencyDeviation is { } deviation)
        {
            _deviationEvents.Add(new AtcDeviationEvent(AtcDeviationKind.Frequency, AtcDeviationChange.Cleared, deviation.Expected, null, deviation.Station));
        }

        FrequencyDeviation = null;
        _frequencySeen = null;
        _frequencyMismatchSince = null;
        _frequencyNotifiedUtc = null;
        _frequencyCalls = 0;
    }

    private bool DeviationCallDue(DateTimeOffset? lastCall, int calls, DateTimeOffset now) =>
        calls == 0 || (calls < Math.Max(1, Rules.DeviationMaxCalls) && lastCall is { } last && (now - last).TotalSeconds >= Math.Max(10, Rules.DeviationRepeatSeconds));

    /// <summary>Neue Station: ihre Frequenz gilt erst nach dem ersten Anruf bzw. nach der Schonfrist einer Übergabe.</summary>
    private void StationChanged(DateTimeOffset now, bool handoff)
    {
        ResetFrequencyCheck();
        _frequencyExpectedFrom = handoff ? now + TimeSpan.FromSeconds(Math.Max(0, Rules.FrequencyGraceSeconds)) : null;
    }

    /// <summary>Der Pilot hat die aktuelle Station gerufen – er ist (spätestens jetzt) auf ihrer Frequenz.</summary>
    private void StationContacted(DateTimeOffset now)
    {
        if (_frequencyExpectedFrom is not { } from || from > now)
        {
            _frequencyExpectedFrom = now;
        }
    }

    // ------------------------------------------------------------------ Falsche Station

    /// <summary>
    /// Ruft der Pilot eine andere Station ("Gander, Condor 2205, position …" auf der Frequenz von Montreal)? Dann weist ATC
    /// darauf hin statt normal zu antworten: Ist es die Station von kurz vorher, wiederholt sie die Übergabe ("contact Montreal
    /// Center 132.6"), sonst sagt die aktuelle "this is Montreal Center, say again". Rückgabe: true, wenn so beantwortet.
    /// </summary>
    private bool HandleWrongStation(HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        if (AddressedOtherStation(heard) is not { } addressed)
        {
            return false;
        }

        var now = input.TimeUtc;
        if (_handoffFrom is { } previous && _handoffMessageId is { } messageId && _handoffAtUtc is { } at
            && (now - at).TotalMinutes <= 15 && DistinctWords(previous.Name).Overlaps(addressed))
        {
            var locals = new Dictionary<string, AtcValue>
            {
                ["freq"] = Station.Frequency,
                ["nextCenter"] = Station.NameValue,
            };
            if (Say(output, messageId, locals, previous) is not null)
            {
                _ackFrequency = Station.Frequency;
                return true;
            }
        }

        return Say(output, AtcMessageIds.CommonWrongStation, null) is not null;
    }

    /// <summary>
    /// Kennwörter einer anderen Station am Anfang des Funkspruchs, null = keine andere Station. Nur bei einem Anruf der Form
    /// "Station, Rufzeichen, …" (Readbacks mit dem Rufzeichen am Ende zählen nicht) und wenn die aktuelle Station einen
    /// eigenen Namen hat (nicht nur "Center"): ein bekannter Name einer anderen Station – oder ein unbekannter Name mit einer
    /// Streckenstelle dahinter ("Edmonton Center").
    /// </summary>
    private HashSet<string>? AddressedOtherStation(HeardText heard)
    {
        var current = CurrentStationWords();
        if (current.Count == 0)
        {
            return null;
        }

        var own = OwnCallsignWords();
        var lead = new List<string>();
        var beforeCallsign = false;
        foreach (var token in heard.Tokens)
        {
            if (token.Kind == HeardKind.Word && own.Contains(token.Text))
            {
                beforeCallsign = true;
                break;
            }

            if (token.Kind != HeardKind.Word || lead.Count >= 4)
            {
                break;
            }

            lead.Add(token.Text);
        }

        var addressed = lead.Where(w => w.Length >= 3 && !UnitWords.Contains(w) && !LeadingFillers.Contains(w)).ToHashSet(StringComparer.Ordinal);
        if (addressed.Count == 0 || addressed.Overlaps(current))
        {
            return null;
        }

        // Bekannter Name ohne Stationsart ("Gander, Condor 2205, …") – nur als Anruf mit Rufzeichen dahinter
        if (beforeCallsign && OtherStationNames().Any(name => DistinctWords(name).Overlaps(addressed)))
        {
            return addressed;
        }

        // Name mit Stationsart vorn ("Edmonton Center, …", "Frankfurt Tower, …") – bekannt oder nicht. Mit Rufzeichen dahinter
        // reicht das; ohne Rufzeichen nur, wenn der Funkspruch damit beginnt (Name aus höchstens zwei Wörtern). Nicht
        // "ILS approach", "final approach" oder "contact Frankfurt Tower" (Wörter davor, die kein Name sind).
        var unit = lead.FindIndex(StationUnitWords.Contains);
        return unit > 0 && (beforeCallsign || unit <= 2)
            && lead.Take(unit).All(w => addressed.Contains(w) && !ProcedureWords.Contains(w))
            ? addressed
            : null;
    }

    /// <summary>Stationsarten, nach denen ein Name als Station gilt (Strecke und Flughafen, englisch und deutsch).</summary>
    private static readonly HashSet<string> StationUnitWords = new(StringComparer.Ordinal)
    {
        "control", "radar", "center", "centre", "oceanic", "radio", "tower", "ground", "delivery", "clearance", "apron", "ramp",
        "director", "approach", "departure", "arrival", "information", "turm", "rollkontrolle", "vorfeld", "anflug", "abflug", "kontrolle",
    };

    /// <summary>Wörter vor "approach"/"departure"/"arrival", die ein Verfahren meinen, keine Station.</summary>
    private static readonly HashSet<string> ProcedureWords = new(StringComparer.Ordinal)
    {
        "final", "ils", "rnav", "rnp", "visual", "missed", "loc", "localizer", "vor", "ndb", "gls", "circling", "sid", "star",
        "standard", "instrument", "initial", "intermediate", "expect", "expecting", "runway",
    };

    /// <summary>Kennwörter der aktuellen Station: ihr Name, bei Flughafenstationen auch der Flughafen, bei FIRs der Name aus den Vorlagen.</summary>
    private HashSet<string> CurrentStationWords()
    {
        var words = DistinctWords(Station.Name);
        if (Station.Type is AtcStationType.Center or AtcStationType.Oceanic)
        {
            if (Station.Facility.Length > 0 && _t.Fir(Station.Facility)?.Name is { Length: > 0 } firName)
            {
                words.UnionWith(DistinctWords(firName));
            }
        }
        else if (Station.Facility.Length > 0)
        {
            var ofpName = string.Equals(Station.Facility, OriginIcao, StringComparison.OrdinalIgnoreCase) ? Plan.Ofp.Origin.Name
                : string.Equals(Station.Facility, DestIcao, StringComparison.OrdinalIgnoreCase) ? Plan.Ofp.Destination.Name
                : null;
            words.UnionWith(DistinctWords(_stations.AirportName(Station.Facility, ofpName)));
        }

        return words;
    }

    /// <summary>Namen der übrigen Stationen des Flugs: Strecke, Start und Ziel, Oceanic, alle FIRs der Vorlagen.</summary>
    private IEnumerable<string> OtherStationNames()
    {
        foreach (var segment in Plan.Firs)
        {
            yield return _stations.SegmentName(segment);
        }

        foreach (var (icao, name) in new[] { (OriginIcao, Plan.Ofp.Origin.Name), (DestIcao, Plan.Ofp.Destination.Name) })
        {
            yield return _stations.AirportName(icao, name);
            foreach (var custom in _t.Airport(icao)?.Stations.Values ?? Enumerable.Empty<string>())
            {
                yield return custom;
            }
        }

        if (Plan.Oceanic is not null)
        {
            yield return OceanicStation().Name;
        }

        foreach (var fir in _t.Firs.Values)
        {
            if (fir.Name is { Length: > 0 } name)
            {
                yield return name;
            }
        }

        if (_handoffFrom is { } previous)
        {
            yield return previous.Name;
        }
    }

    /// <summary>Wörter des eigenen Rufzeichens ("condor", "cfg") – dort endet der Stationsname.</summary>
    private HashSet<string> OwnCallsignWords()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (Resolve("callsign", null, Station) is { } callsign)
        {
            foreach (var word in callsign.Spoken.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.All(char.IsAsciiLetter))
                {
                    words.Add(word);
                }
            }
        }

        var prefix = new string(Plan.Callsign.TakeWhile(char.IsAsciiLetter).ToArray()).ToLowerInvariant();
        if (prefix.Length > 0)
        {
            words.Add(prefix);
        }

        // Ziffern- und Buchstabenwörter des Rufzeichens ("four alpha bravo") sind keine Stationsnamen
        words.ExceptWith(["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner", "nine"]);
        return words;
    }

    /// <summary>Kennwörter eines Stationsnamens: klein, ohne Stationsart ("Montreal Center" → "montreal", "SoCal Approach" → "socal").</summary>
    private static HashSet<string> DistinctWords(string? name)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
        {
            return words;
        }

        foreach (var word in HeardText.FoldDiacritics(name.ToLowerInvariant()).Split([' ', '-', '/', ',', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 3 && word.All(char.IsAsciiLetter) && !UnitWords.Contains(word))
            {
                words.Add(word);
            }
        }

        return words;
    }
}
