using System.Text.RegularExpressions;

namespace Duopilo.Atc;

/// <summary>Art des Notfalls: Mayday (Distress) oder Pan-Pan (Urgency).</summary>
public enum AtcEmergencyKind
{
    Mayday,
    PanPan,
}

/// <summary>Was ein Funkspruch zum Notfall sagt.</summary>
public enum AtcEmergencyCall
{
    None,

    /// <summary>„Mayday“ (auch nur ein- oder zweimal, „may day“).</summary>
    Mayday,

    /// <summary>„Pan Pan“ („pan-pan“, „panpan“).</summary>
    PanPan,

    /// <summary>„cancel Mayday“, „cancel distress“, „cancel Pan Pan“.</summary>
    Cancel,
}

/// <summary>Laufender Notfall des eigenen Flugs.</summary>
/// <param name="PreviousSquawk">Code vor dem Notfall – nach „cancel Mayday“ wieder zu setzen.</param>
/// <param name="DetailsAsked">„say souls on board and fuel endurance“ schon gefragt.</param>
/// <param name="Target">Freigegebenes Ziel (direct), null = keins.</param>
public sealed record AtcEmergency(AtcEmergencyKind Kind, DateTimeOffset SinceUtc, string PreviousSquawk, bool DetailsAsked = false, string? Target = null)
{
    /// <summary>"Mayday" oder "Pan Pan" (so, wie ATC es sagt).</summary>
    public string Name => Kind == AtcEmergencyKind.Mayday ? "Mayday" : "Pan Pan";
}

/// <summary>Erkennt Notrufe im Funkspruch – vor allem anderen (Stationsprüfung, Readbacks, Smalltalk).</summary>
public static partial class AtcEmergencyText
{
    /// <summary>
    /// Notruf, Abbruch oder nichts. Großschreibung, Satzzeichen und Bindestriche egal; „Mayday“ und „Pan Pan“ reichen einmal,
    /// in jeder Phase und mit beliebiger, falscher oder ohne Stationsanrede.
    /// </summary>
    public static AtcEmergencyCall Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AtcEmergencyCall.None;
        }

        var t = " " + NotLetter().Replace(HeardText.FoldDiacritics(text.ToLowerInvariant()), " ") + " ";
        if (Cancel().IsMatch(t))
        {
            return AtcEmergencyCall.Cancel;
        }

        if (Mayday().IsMatch(t))
        {
            return AtcEmergencyCall.Mayday;
        }

        return PanPan().IsMatch(t) ? AtcEmergencyCall.PanPan : AtcEmergencyCall.None;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotLetter();

    // "may day", "mayday", "maydays", "maiday", "m'aidez" (→ "m aidez")
    [GeneratedRegex(@" (?:may ?days?|maidays?|m ?aidez) ")]
    private static partial Regex Mayday();

    // "pan pan", "pan-pan", "panpan", "pan pan pan", "pon pon"
    [GeneratedRegex(@" (?:pan ?pan|pon ?pon)")]
    private static partial Regex PanPan();

    // "cancel (the) mayday/distress/pan pan/emergency", "mayday cancelled", "cancelling distress". Dazu Whisper-Verhörer von
    // „cancel“ – nur direkt vor dem Notruf-Wort (Testflug 31.10.: „Council Pan Pan“ wurde dreimal als neues Pan Pan bestätigt):
    // "council", "counsel", "consul", "cancer", "can sell", "kanzel" (deutsche Erkennung)
    [GeneratedRegex(@" (?:cancel(?:l?ing|l?ed)?|terminate|terminating|council|counsel|consul|cancer|can sell|kanzel) (?:the |our |my )?(?:may ?days?|maidays?|distress|pan ?pan|urgency|emergency) | (?:may ?days?|maidays?|distress|pan ?pan|urgency|emergency) (?:is )?(?:cancel(?:l?ed)?|terminated) ")]
    private static partial Regex Cancel();
}

/// <summary>
/// Notrufe nach ICAO-Phraseologie: „Mayday“ und „Pan Pan“ in jeder Phase, mit Vorrang vor allem anderen. Die aktuelle Station
/// bestätigt sofort ("roger Mayday, squawk seven seven zero zero, say intentions"), fragt danach Personen an Bord und
/// Endurance ab und gibt, was der Pilot wünscht: direkt zum gewünschten oder nächsten geeigneten Flughafen, jede Höhe, die
/// Landung auf jeder Bahn. Bis „cancel Mayday“ keine automatischen Übergaben, Freigaben und Prüfungen (Squawk, Frequenz).
/// Squawk 7700 ohne Funkspruch: ATC fragt nach. 7600 und 7500 behandelt ATC (noch) nicht – nur kein „check squawk“.
/// </summary>
public sealed partial class AtcSession
{
    public const string EmergencySquawk = "7700";

    // Wörter einer Landeanfrage, einer Ausweichanfrage und der Angaben zu Personen an Bord und Sprit
    private static readonly string[] LandingWords = ["land", "landing", "touchdown"];
    private static readonly string[] DivertWords = ["direct", "divert", "diverting", "diversion", "return", "returning", "nearest", "closest", "vectors", "proceed", "proceeding"];
    private static readonly string[] NearestWords = ["nearest", "closest", "suitable"];
    private static readonly string[] DetailWords = ["souls", "persons", "people", "pob", "passengers", "pax", "fuel", "endurance", "hours", "minutes"];
    private static readonly string[] NotEmergencyWords = ["negative", "wrong", "error", "mistake", "inadvertent", "inadvertently", "sorry", "correcting", "corrected"];
    private static readonly string[] TargetFillers = ["to", "the", "for", "at", "back", "airport", "field", "request", "requesting", "direct", "please"];
    private static readonly string[] ClimbWords = ["climb", "climbing", "higher"];
    private static readonly string[] DescendWords = ["descend", "descending", "descent", "lower"];
    private static readonly string[] DirectWords = ["direct", "proceed", "proceeding"];

    // Squawk 7700 ohne Funkspruch: seit wann, schon nachgefragt
    private DateTimeOffset? _emergencySquawkSince;
    private bool _emergencySquawkAsked;
    private bool _emergencySquawkAnswerPending;

    /// <summary>Laufender Notfall (null = keiner).</summary>
    public AtcEmergency? Emergency { get; private set; }

    /// <summary>
    /// Notruf, Abbruch oder – während eines Notfalls – jeder Funkspruch. Rückgabe: true, wenn so beantwortet (dann nichts weiter).
    /// </summary>
    private bool HandleEmergencyTransmission(string text, HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        var call = AtcEmergencyText.Detect(text);
        switch (call)
        {
            case AtcEmergencyCall.Mayday:
            case AtcEmergencyCall.PanPan:
                DeclareEmergency(call == AtcEmergencyCall.Mayday ? AtcEmergencyKind.Mayday : AtcEmergencyKind.PanPan, input);
                Say(output, call == AtcEmergencyCall.Mayday ? AtcMessageIds.EmergencyMayday : AtcMessageIds.EmergencyPanPan, EmergencyLocals());
                return true;

            case AtcEmergencyCall.Cancel:
                if (Emergency is not { } active)
                {
                    Say(output, AtcMessageIds.CommonRoger, null);
                    return true;
                }

                Say(output, AtcMessageIds.EmergencyCancelled, new Dictionary<string, AtcValue>
                {
                    ["emergency"] = AtcValue.Text(active.Name),
                    ["previousSquawk"] = AtcSpeech.Squawk(active.PreviousSquawk),
                });
                Emergency = null;
                _emergencySquawkSince = null;
                _emergencySquawkAsked = false;
                _emergencySquawkAnswerPending = false;

                // Ab jetzt normaler Ablauf: die Station ist gerufen, eine Quittung des Piloten bleibt unbeantwortet
                StationContacted(input.TimeUtc);
                return true;
        }

        // Antwort auf "confirm squawking seven seven zero zero, say nature of emergency"
        if (Emergency is null && _emergencySquawkAnswerPending)
        {
            _emergencySquawkAnswerPending = false;
            if (heard.Tokens.Any(t => t.IsWord(NotEmergencyWords)))
            {
                Say(output, AtcMessageIds.EmergencySquawkNormal, null);
                return true;
            }

            DeclareEmergency(AtcEmergencyKind.Mayday, input);
            Say(output, AtcMessageIds.EmergencyConfirmed, EmergencyLocals());
            return true;
        }

        if (Emergency is not { } emergency)
        {
            return false;
        }

        StationContacted(input.TimeUtc);
        if (heard.Compact.Contains("SAYAGAIN", StringComparison.Ordinal) && _lastAtc is { } last)
        {
            Record(output, "atc", _lastAtcStation ?? Station, last.MessageId, last.Text, last.Spoken);
            return true;
        }

        if (IsRadioCheck(heard))
        {
            Say(output, AtcMessageIds.CommonRadioCheck, null);
            return true;
        }

        var gaveDetails = heard.Tokens.Any(t => t.IsWord(DetailWords));
        var answered = EmergencyRequest(heard, input, output);

        // Angaben oder Lage gemeldet ("150 souls, two hours", "engine failure, we continue") – ohne eigene Freigabe: roger. Gleich
        // nach der Bestätigung des Notrufs folgt stattdessen die Frage nach Personen an Bord und Endurance.
        if (!answered && (emergency.DetailsAsked || gaveDetails))
        {
            Say(output, AtcMessageIds.EmergencyRoger, null);
        }

        // Danach (einmal): Personen an Bord und Endurance – außer der Pilot hat sie schon genannt
        if (Emergency is { DetailsAsked: false } current)
        {
            if (!gaveDetails)
            {
                Say(output, AtcMessageIds.EmergencyDetails, null);
            }

            Emergency = current with { DetailsAsked = true };
        }

        return true;
    }

    /// <summary>Wünsche des Piloten im Notfall – ohne Einschränkung. Rückgabe: true, wenn eine Freigabe gesprochen wurde.</summary>
    private bool EmergencyRequest(HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        var t = heard.Tokens;

        // Landung auf jeder Bahn: die genannte, sonst die geplante bzw. zugewiesene
        if (t.Any(x => x.IsWord(LandingWords)))
        {
            var runway = ReadbackChecker.Runways(heard).Select(r => (AtcValue?)AtcSpeech.Runway(r.Number, r.Designator)).FirstOrDefault()
                ?? Resolve("runway", null, Station);
            if (runway is not null)
            {
                _landingCleared = true;
                Say(output, AtcMessageIds.EmergencyLanding, new Dictionary<string, AtcValue> { ["emergencyRunway"] = runway });
                return true;
            }
        }

        // Höhe ohne Einschränkung (keine "unable due traffic")
        if ((ParseLevelRequest(heard) ?? EmergencyLevel(heard)) is { } level)
        {
            if (level.Feet is { } feet)
            {
                ClearedAltitudeFt = feet;
                var verb = feet > (input.AltitudeFt ?? 0) + 300 ? "climbAndMaintain" : feet < (input.AltitudeFt ?? 0) - 300 ? "descendAndMaintain" : "maintain";
                var locals = LevelLocals(feet, verb);
                Say(output, AtcMessageIds.EmergencyLevel, locals);
            }
            else
            {
                var verb = level.Direction >= 0 ? "climb" : "descend";
                Say(output, AtcMessageIds.EmergencyLevelDiscretion, new Dictionary<string, AtcValue> { ["levelVerb"] = AtcValue.Text(_t.Word(verb, verb)) });
            }

            return true;
        }

        // Direkt zum gewünschten oder zum nächsten geeigneten Flughafen
        if (t.Any(x => x.IsWord(DivertWords)))
        {
            var target = EmergencyTarget(heard, input);
            Emergency = Emergency! with { Target = target.Written };
            Say(output, AtcMessageIds.EmergencyDirect, new Dictionary<string, AtcValue> { ["emergencyTarget"] = target });
            return true;
        }

        return false;
    }

    /// <summary>"climb FL120", "descending to five thousand" ohne "request" (im Notfall meldet der Pilot oft nur, was er tut).</summary>
    private static AtcLevelRequest? EmergencyLevel(HeardText heard)
    {
        var t = heard.Tokens;
        var index = -1;
        var direction = 0;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord(ClimbWords))
            {
                index = i;
                direction = 1;
                break;
            }

            if (t[i].IsWord(DescendWords))
            {
                index = i;
                direction = -1;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        foreach (var (at, feet) in ReadbackChecker.AltitudesAt(heard))
        {
            if (at > index && at <= index + 6 && feet >= 1000)
            {
                return new AtcLevelRequest(RoundToHundreds(feet), direction);
            }
        }

        return new AtcLevelRequest(null, direction);
    }

    /// <summary>
    /// Ziel einer Ausweichanfrage: ein Flughafen des Plans (Start, Ziel, Ausweichflughafen) per ICAO oder Name, "nearest" = der
    /// nächste davon, sonst die genannte Kennung ("direct DOMUX"). Ohne Angabe der nächste Flughafen des Plans.
    /// </summary>
    private AtcValue EmergencyTarget(HeardText heard, AtcInput input)
    {
        var ofp = Plan.Ofp;
        var airports = new[] { ofp.Origin, ofp.Destination, ofp.Alternate }
            .Where(a => a?.Icao is { Length: > 0 })
            .Select(a => a!)
            .ToList();

        foreach (var airport in airports)
        {
            var name = _stations.AirportName(airport.Icao!, airport.Name);
            var words = name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToList();
            if (heard.ContainsCode(airport.Icao!) || words.Any(w => heard.Tokens.Any(x => x.IsWord(HeardText.FoldDiacritics(w)))))
            {
                return AtcSpeech.Airport(airport.Icao!, name);
            }
        }

        var wantsNearest = heard.Tokens.Any(x => x.IsWord(NearestWords));
        if (!wantsNearest)
        {
            // "direct DOMUX", "direct to Kilo Lima Echo": was nach "direct" kommt
            var t = heard.Tokens;
            var at = -1;
            for (var i = 0; i < t.Count; i++)
            {
                if (t[i].IsWord(DirectWords))
                {
                    at = i;
                    break;
                }
            }

            if (at >= 0)
            {
                var code = "";
                for (var j = at + 1; j < t.Count && code.Length < 5; j++)
                {
                    if (t[j].IsWord(TargetFillers) && code.Length == 0)
                    {
                        continue;
                    }

                    if (t[j].Kind == HeardKind.Letter || (t[j].Kind == HeardKind.Word && t[j].Text.Length >= 3 && code.Length == 0))
                    {
                        code += t[j].Kind == HeardKind.Letter ? t[j].Text : t[j].Text.ToUpperInvariant();
                        if (t[j].Kind == HeardKind.Word)
                        {
                            break;
                        }

                        continue;
                    }

                    break;
                }

                if (code.Length >= 2)
                {
                    return AtcSpeech.Ident(code);
                }
            }
        }

        // Nächster Flughafen des Plans (mit Position)
        var nearest = airports
            .Where(a => a.Lat is not null && a.Lon is not null && input.Lat is not null && input.Lon is not null)
            .OrderBy(a => Duopilo.FlightPlan.RouteMath.DistanceNm(input.Lat!.Value, input.Lon!.Value, a.Lat!.Value, a.Lon!.Value))
            .FirstOrDefault() ?? airports.FirstOrDefault();
        return nearest is null
            ? AtcValue.Text("nearest suitable airport")
            : AtcSpeech.Airport(nearest.Icao!, _stations.AirportName(nearest.Icao!, nearest.Name));
    }

    /// <summary>Notfall beginnt: offene Readbacks und Prüfungen fallen weg, der bisherige Squawk wird gemerkt.</summary>
    private void DeclareEmergency(AtcEmergencyKind kind, AtcInput input)
    {
        _emergencySquawkAnswerPending = false;
        if (Phase == AtcPhase.Inactive)
        {
            Place(input);
        }

        if (Emergency is { } running)
        {
            // Noch einmal gerufen oder von Pan Pan auf Mayday: gleicher Notfall, Art wie zuletzt gerufen
            Emergency = running with { Kind = kind == AtcEmergencyKind.Mayday ? kind : running.Kind };
            return;
        }

        Emergency = new AtcEmergency(kind, input.TimeUtc, Squawk);
        PendingReadback = null;
        _ackPending = false;
        _ackFrequency = null;
        ResetSquawkCheck();
        ResetFrequencyCheck();
        StationContacted(input.TimeUtc);
    }

    private Dictionary<string, AtcValue> EmergencyLocals() => new()
    {
        ["emergencySquawk"] = AtcSpeech.Squawk(EmergencySquawk),
    };

    /// <summary>
    /// Squawk 7700 ohne Notruf: nach <see cref="AtcRules.DeviationSeconds"/> fragt ATC einmal nach ("confirm squawking seven seven
    /// zero zero, say nature of emergency"). Rückgabe: true, wenn ATC gerade gefragt hat.
    /// </summary>
    private bool CheckEmergencySquawk(AtcInput input, List<AtcTransmission> output)
    {
        if (Emergency is not null || input.TransponderCode != EmergencySquawk || Phase is AtcPhase.Inactive or AtcPhase.Done)
        {
            if (input.TransponderCode != EmergencySquawk)
            {
                _emergencySquawkSince = null;
                _emergencySquawkAsked = false;
                _emergencySquawkAnswerPending = false;
            }

            return false;
        }

        var now = input.TimeUtc;
        _emergencySquawkSince ??= now;
        if (_emergencySquawkAsked || (now - _emergencySquawkSince.Value).TotalSeconds < Math.Max(3, Rules.DeviationSeconds))
        {
            return false;
        }

        if (Say(output, AtcMessageIds.EmergencyConfirmSquawk, EmergencyLocals()) is null)
        {
            return false;
        }

        _emergencySquawkAsked = true;
        _emergencySquawkAnswerPending = true;
        PendingReadback = null;
        return true;
    }

    /// <summary>Notfall aus einer Sicherung.</summary>
    private void RestoreEmergency(AtcEmergency? emergency) => Emergency = emergency;
}
