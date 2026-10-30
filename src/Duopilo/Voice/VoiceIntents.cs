using System.Text.RegularExpressions;

namespace Duopilo.Voice;

public enum IntentKind
{
    /// <summary>Eingabe im Cockpit (über prepare_action / confirm_action).</summary>
    Action,

    /// <summary>Abfrage, z. B. "Speed check".</summary>
    Query,

    /// <summary>"Confirm", "Correct", "Affirm".</summary>
    Confirm,

    /// <summary>"Negative", "Cancel".</summary>
    Negative,

    /// <summary>"Kopilot Pause" – Spracheingabe stumm.</summary>
    Pause,

    /// <summary>"Kopilot weiter" – Spracheingabe wieder an.</summary>
    Resume,

    /// <summary>"Say again" – letzte Antwort wiederholen.</summary>
    SayAgain,

    /// <summary>"Ja", "OK", "Roger" – bestätigt nichts (dafür braucht es "Confirm", "Correct" oder "Affirm").</summary>
    Acknowledge,

    /// <summary>"Check", "Copy" – der Pilot hat eine wichtige Meldung gehört (Bestätigungspflicht). Führt nie etwas aus.</summary>
    Check,

    /// <summary>Checkliste starten, z. B. "Before start checklist". <see cref="VoiceIntent.Value"/> = Kennung der Checkliste.</summary>
    Checklist,

    /// <summary>Während einer Checkliste: "Skip" (Punkt überspringen) oder "Stop checklist". <see cref="VoiceIntent.Value"/> = "skip" oder "stop".</summary>
    ChecklistControl,

    /// <summary>"Briefing" – der First Officer stellt sich vor ("Hi, I'm Sam, I'll be your first officer today").</summary>
    Briefing,

    Unknown,
}

public enum QueryKind
{
    Speed,
    Altitude,
    Fuel,
    Engines,
    Gear,
    Flaps,
    Speedbrake,
    Spoilers,
    Seatbelts,
    Apu,
    Baro,
    Heading,

    /// <summary>Treibstoff gegen den SimBrief-Plan und prognostizierte Landemenge.</summary>
    FuelPlan,

    /// <summary>Nächster Wegpunkt mit Distanz und ETA.</summary>
    NextWaypoint,

    /// <summary>ETA am Ziel.</summary>
    Eta,

    TopOfDescent,

    /// <summary>Nächster Verkehr – wie der Callout von get_traffic.</summary>
    Traffic,

    Wind,

    /// <summary>Außentemperatur (SAT).</summary>
    Temperature,

    /// <summary>Wind und Temperatur zusammen.</summary>
    Weather,

    Squawk,

    /// <summary>Aktive ATC-Station und Frequenz.</summary>
    Station,
}

/// <summary>Erkannter Befehl. <see cref="ActionId"/>/<see cref="Value"/> entsprechen den Aktionen im Flugzeugprofil.</summary>
public sealed record VoiceIntent(IntentKind Kind, string? ActionId = null, string? Value = null, QueryKind? Query = null)
{
    public static readonly VoiceIntent Unknown = new(IntentKind.Unknown);

    public override string ToString() => Kind switch
    {
        IntentKind.Action => Value is null ? ActionId! : $"{ActionId} = {Value}",
        IntentKind.Query => $"query {Query}",
        IntentKind.Checklist => $"checklist {Value}",
        IntentKind.ChecklistControl => $"checklist {Value}",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Befehle an den Kopiloten nach festen Mustern (deutsch und englisch, mit Synonymen) – kein Sprachmodell.
/// Arbeitet auf der Form von <see cref="VoiceText.Normalize"/>. Passen mehrere verschiedene Befehle, gilt der Satz als unklar
/// ("Say again") – ein Satz, ein Befehl.
/// </summary>
public static partial class VoiceIntents
{
    // Nur die Funkwörter bestätigen – "ja", "ok" fallen im Cockpit zu leicht (alle auch in InputService.ConfirmAsync erlaubt).
    // "korrekt": So schreibt Whisper ein deutsch klingendes "correct".
    private static readonly HashSet<string> YesWords = new(StringComparer.Ordinal)
    {
        "confirm", "confirmed", "correct", "korrekt", "affirm", "affirmative",
    };

    // Allein bestätigen sie nichts, neben einem Bestätigungswort stören sie nicht ("Ja, korrekt")
    private static readonly HashSet<string> AckWords = new(StringComparer.Ordinal)
    {
        "yes", "ja", "jawohl", "richtig", "ok", "okay", "roger", "wilco", "verstanden",
    };

    // Quittung einer wichtigen Meldung ("check", "copy"; "correcting" bei Abweichungen wie "Sink rate")
    private static readonly HashSet<string> CheckWords = new(StringComparer.Ordinal)
    {
        "check", "checked", "copy", "copied", "correcting",
    };

    private static readonly HashSet<string> NoWords = new(StringComparer.Ordinal)
    {
        "negative", "cancel", "disregard", "no", "nein", "abbrechen", "abbruch", "falsch", "wrong", "storno",
    };

    // Füllwörter, die um Bestätigung/Ablehnung herum stehen dürfen
    private static readonly HashSet<string> ConfirmFiller = new(StringComparer.Ordinal)
    {
        "that", "thats", "is", "it", "s", "das", "ist", "please", "bitte", "kopilot", "sir",
    };

    // Werden vor der Mustersuche entfernt
    private static readonly HashSet<string> Politeness = new(StringComparer.Ordinal)
    {
        "please", "bitte", "danke", "thanks", "thank", "you", "set", "setze", "setzen", "select", "now", "jetzt", "the", "die", "der", "das",
    };

    private sealed record Rule(Regex Pattern, string ActionId, Func<Match, string?> Value);

    private static readonly Dictionary<string, string> OnOff = new(StringComparer.Ordinal)
    {
        ["on"] = "on", ["an"] = "on", ["ein"] = "on", ["einschalten"] = "on", ["engage"] = "on", ["engaged"] = "on",
        ["off"] = "off", ["aus"] = "off", ["ausschalten"] = "off", ["disengage"] = "off", ["disconnect"] = "off",
    };

    private const string OnOffWords = "on|an|ein|einschalten|engage|engaged|off|aus|ausschalten|disengage|disconnect";

    private static readonly Rule[] Rules = BuildRules();

    /// <summary>Erkennt den Befehl in einem normalisierten Satz.</summary>
    public static VoiceIntent Recognize(string normalized, IReadOnlyCollection<string>? wakeWords = null)
    {
        var words = VoiceText.Words(normalized).ToList();
        if (words.Count == 0)
        {
            return VoiceIntent.Unknown;
        }

        var wake = wakeWords ?? ["kopilot"];
        var addressed = words.Count > 0 && wake.Contains(words[0]);

        // Steuerung mit Anrede: "Kopilot Pause" / "Kopilot weiter" (auch "Pause Kopilot")
        var rest = words.Where(w => !wake.Contains(w)).ToList();
        var mentionsKopilot = rest.Count < words.Count;
        if (mentionsKopilot && rest.Count is >= 1 and <= 2)
        {
            var control = string.Join(' ', rest);
            if (PauseWords().IsMatch(control))
            {
                return new VoiceIntent(IntentKind.Pause);
            }

            if (ResumeWords().IsMatch(control))
            {
                return new VoiceIntent(IntentKind.Resume);
            }
        }

        // Anrede und Höflichkeit weg
        if (addressed)
        {
            words.RemoveAt(0);
        }

        if (words.Count > 1 && words[0] is "ok" or "okay")
        {
            words.RemoveAt(0);
        }

        if (words.Count == 0)
        {
            return VoiceIntent.Unknown;
        }

        // Bestätigung / Ablehnung: der ganze Satz besteht daraus
        if (words.All(w => YesWords.Contains(w) || NoWords.Contains(w) || AckWords.Contains(w) || ConfirmFiller.Contains(w)))
        {
            if (words.Any(NoWords.Contains))
            {
                return new VoiceIntent(IntentKind.Negative);
            }

            if (words.Any(YesWords.Contains))
            {
                return new VoiceIntent(IntentKind.Confirm);
            }

            if (words.Any(AckWords.Contains))
            {
                return new VoiceIntent(IntentKind.Acknowledge);
            }
        }

        // "Check" / "Copy": eine wichtige Meldung gehört (Bestätigungspflicht) – der ganze Satz besteht daraus
        if (words.Any(CheckWords.Contains) && words.All(w => CheckWords.Contains(w) || AckWords.Contains(w) || ConfirmFiller.Contains(w)))
        {
            return new VoiceIntent(IntentKind.Check);
        }

        var text = string.Join(' ', words.Where(w => !Politeness.Contains(w)));
        if (text.Length == 0)
        {
            return VoiceIntent.Unknown;
        }

        if (SayAgainPattern().IsMatch(text))
        {
            return new VoiceIntent(IntentKind.SayAgain);
        }

        if (Checklist(text) is { } checklist)
        {
            return checklist;
        }

        if (BriefingPattern().IsMatch(text))
        {
            return new VoiceIntent(IntentKind.Briefing);
        }

        if (Query(text) is { } query)
        {
            return new VoiceIntent(IntentKind.Query, Query: query);
        }

        VoiceIntent? found = null;
        foreach (var rule in Rules)
        {
            foreach (Match m in rule.Pattern.Matches(text))
            {
                var value = rule.Value(m);
                if (value == "")
                {
                    continue;
                }

                var intent = new VoiceIntent(IntentKind.Action, rule.ActionId, value);
                if (found is null)
                {
                    found = intent;
                }
                else if (found != intent)
                {
                    // Zwei verschiedene Befehle in einem Satz – lieber nachfragen
                    return VoiceIntent.Unknown;
                }
            }
        }

        return found ?? VoiceIntent.Unknown;
    }

    // ------------------------------------------------------------------ Abfragen

    private static readonly (string Pattern, QueryKind Kind)[] QueryItems =
    [
        ("speed|geschwindigkeit|fahrt", QueryKind.Speed),
        ("altitude|alt|höhe|hoehe|level", QueryKind.Altitude),
        ("fuel|treibstoff|sprit|kraftstoff", QueryKind.Fuel),
        ("engines?|triebwerke?|eng", QueryKind.Engines),
        ("gear|fahrwerk", QueryKind.Gear),
        ("flaps|klappen", QueryKind.Flaps),
        ("speedbrake|bremsklappen|luftbremse", QueryKind.Speedbrake),
        ("spoilers|ground spoilers", QueryKind.Spoilers),
        ("seatbelts|anschnallzeichen", QueryKind.Seatbelts),
        ("apu", QueryKind.Apu),
        ("baro|qnh|höhenmesser|altimeter", QueryKind.Baro),
        ("heading|kurs|hdg", QueryKind.Heading),
    ];

    private static readonly (Regex Pattern, QueryKind Kind)[] QueryRules = QueryItems
        .Select(q => (new Regex($@"^(?:(?:{q.Pattern}) (?:check|checks|control|prüfen|pruefen|kontrolle|status)|(?:check|prüfe|pruefe|kontrolle) (?:{q.Pattern}))$", RegexOptions.CultureInvariant | RegexOptions.Compiled), q.Kind))
        .ToArray();

    // Abfragen, die nicht dem Muster "<Begriff> check" folgen (ganzer Satz, optional mit "check"/"say" davor oder "check" dahinter)
    private static readonly (string Pattern, QueryKind Kind)[] PhraseItems =
    [
        (@"(?:fuel|sprit|treibstoff|kraftstoff) (?:versus |vs |against |gegen |zum |vom |und |and )?plan|landing fuel|fuel (?:at|for) landing|fuel prediction|"
         + @"predicted landing fuel|landemenge|(?:sprit|treibstoff|kraftstoff) (?:bei|zur|für|fuer) landung|(?:sprit|treibstoff) ?prognose", QueryKind.FuelPlan),
        (@"(?:next|nächster|naechster|nächste|naechste) (?:waypoint|wegpunkt|fix|point|punkt)(?: eta| distance| distanz)?", QueryKind.NextWaypoint),
        (@"eta|eta destination|eta ziel|arrival time|ankunftszeit|ankunft|wann landen wir|when do we land|time of arrival", QueryKind.Eta),
        (@"top of descent|tod|sinkflugbeginn|beginn (?:des )?sinkflugs|beginn sinkflug|descent point", QueryKind.TopOfDescent),
        (@"(?:any )?traffic|verkehr", QueryKind.Traffic),
        (@"winds?|wind (?:direction|richtung)", QueryKind.Wind),
        (@"temperature|temp|temperatur|sat|oat|outside air temperature|(?:außen|aussen) ?temperatur", QueryKind.Temperature),
        (@"wind (?:and|und) (?:temperature|temperatur)|wetter|weather", QueryKind.Weather),
        (@"squawk|squawk code|transponder|transponder code|(?:transponder ?)?code", QueryKind.Squawk),
        (@"station|frequency|frequenz|active (?:station|frequency)|aktive (?:station|frequenz)|current (?:station|frequency)|"
         + @"aktuelle (?:station|frequenz)|which (?:station|frequency)|welche (?:station|frequenz)|who are we talking to|mit wem sprechen wir", QueryKind.Station),
    ];

    private static readonly (Regex Pattern, QueryKind Kind)[] PhraseRules = PhraseItems
        .Select(q => (new Regex($@"^(?:(?:check|say|prüfe|pruefe|kontrolle) )?(?:{q.Pattern})(?: (?:check|checks|status|report|prüfen|pruefen|kontrolle|please))?$", RegexOptions.CultureInvariant | RegexOptions.Compiled), q.Kind))
        .ToArray();

    private static QueryKind? Query(string text)
    {
        foreach (var (pattern, kind) in QueryRules)
        {
            if (pattern.IsMatch(text))
            {
                return kind;
            }
        }

        foreach (var (pattern, kind) in PhraseRules)
        {
            if (pattern.IsMatch(text))
            {
                return kind;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ Checklisten

    /// <summary>Gesprochene Namen der Checklisten (englisch wie im Cockpit, deutsch als Umschreibung) → Kennung.</summary>
    private static readonly (string Pattern, string Id)[] ChecklistNames =
    [
        (@"before start|vor (?:dem )?anlassen", "beforeStart"),
        (@"after start|nach (?:dem )?anlassen", "afterStart"),
        (@"before takeoff|vor (?:dem )?start|vor (?:dem )?abflug", "beforeTakeoff"),
        (@"approach|anflug", "approach"),
        (@"landing|landung", "landing"),
        (@"after landing|nach (?:der )?landung", "afterLanding"),
        (@"parking|parken|abstellen", "parking"),
    ];

    private static readonly (Regex Pattern, string Id)[] ChecklistRules = ChecklistNames
        .Select(c => (new Regex($@"^(?:(?:start|run|read|begin|starte|lies) )?(?:(?:{c.Pattern}) checklist|checklist (?:{c.Pattern}))(?: please)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled), c.Id))
        .ToArray();

    /// <summary>Checkliste starten oder steuern ("skip", "stop checklist"), sonst null.</summary>
    private static VoiceIntent? Checklist(string text)
    {
        foreach (var (pattern, id) in ChecklistRules)
        {
            if (pattern.IsMatch(text))
            {
                return new VoiceIntent(IntentKind.Checklist, Value: id);
            }
        }

        if (ChecklistStopPattern().IsMatch(text))
        {
            return new VoiceIntent(IntentKind.ChecklistControl, Value: "stop");
        }

        if (ChecklistSkipPattern().IsMatch(text))
        {
            return new VoiceIntent(IntentKind.ChecklistControl, Value: "skip");
        }

        return null;
    }

    // ------------------------------------------------------------------ Befehle

    private static Rule[] BuildRules()
    {
        var rules = new List<Rule>();

        void Add(string pattern, string actionId, Func<Match, string?> value) =>
            rules.Add(new Rule(new Regex($@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant | RegexOptions.Compiled), actionId, value));

        void Fixed(string pattern, string actionId, string? value = null) => Add(pattern, actionId, _ => value);

        string Map(Match m, Dictionary<string, string> map) => map.TryGetValue(m.Groups["v"].Value, out var v) ? v : "";

        // ---------------- Fahrwerk
        Fixed(@"(?:gear|fahrwerk) (?:up|hoch|rein|ein|einfahren)", "gear.up");
        Fixed(@"(?:retract (?:the )?gear|fahrwerk einziehen)", "gear.up");
        Fixed(@"(?:gear|fahrwerk) (?:down|runter|raus|aus|ausfahren)", "gear.down");

        // ---------------- Klappen ("Klappen ein" absichtlich nicht: Whisper verschluckt gern das s von "eins")
        var flaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = "0", ["up"] = "0", ["zero"] = "0", ["retract"] = "0", ["einfahren"] = "0",
            ["1"] = "1", ["2"] = "2", ["3"] = "3", ["full"] = "FULL", ["voll"] = "FULL",
        };
        Add(@"(?:flaps|klappen)(?: to| auf)? (?<v>0|1|2|3|up|zero|retract|einfahren|full|voll)", "flaps", m => Map(m, flaps));
        Add(@"config(?:uration)? (?<v>1|2|3|full)", "flaps", m => Map(m, flaps));

        // ---------------- Speedbrake und Ground Spoiler
        var speedbrake = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retract"] = "retract", ["retracted"] = "retract", ["in"] = "retract", ["ein"] = "retract", ["einfahren"] = "retract",
            ["zero"] = "retract", ["0"] = "retract", ["stow"] = "retract", ["stowed"] = "retract",
            ["half"] = "half", ["halb"] = "half", ["50"] = "half",
            ["full"] = "full", ["voll"] = "full", ["100"] = "full",
        };
        Add(@"(?:speedbrake|bremsklappen|luftbremse)(?: to| auf)? (?<v>retract|retracted|in|ein|einfahren|zero|0|stow|stowed|half|halb|50|full|voll|100)", "speedbrake", m => Map(m, speedbrake));
        Fixed(@"retract (?:the )?speedbrake", "speedbrake", "retract");

        Fixed(@"(?:arm|armed?) (?:the )?(?:ground )?spoilers", "spoilers.arm", "arm");
        Fixed(@"(?:ground )?spoilers (?:arm|armed|armen|scharf)", "spoilers.arm", "arm");
        Fixed(@"disarm (?:the )?(?:ground )?spoilers", "spoilers.arm", "disarm");
        Fixed(@"(?:ground )?spoilers (?:disarm|disarmed|entschärfen|entschaerfen|unscharf|aus)", "spoilers.arm", "disarm");

        // ---------------- Autobrake
        var autobrake = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lo"] = "lo", ["low"] = "lo", ["niedrig"] = "lo", ["med"] = "med", ["medium"] = "med", ["mittel"] = "med",
            ["max"] = "max", ["maximum"] = "max", ["off"] = "off", ["aus"] = "off", ["disarm"] = "off",
        };
        Add(@"(?:autobrake|autobremse)(?: to| auf)? (?<v>lo|low|niedrig|med|medium|mittel|max|maximum|off|aus|disarm)", "brakes.autobrake", m => Map(m, autobrake));

        // ---------------- Licht
        var landing = new Dictionary<string, string>(OnOff, StringComparer.Ordinal) { ["retract"] = "retract", ["einfahren"] = "retract" };
        Add($@"(?:landing lights?|landelichter|landelicht|landescheinwerfer) (?<v>{OnOffWords}|retract|einfahren)", "lights.landing", m => Map(m, landing));
        var strobe = new Dictionary<string, string>(OnOff, StringComparer.Ordinal) { ["auto"] = "auto" };
        Add($@"(?:strobes?|strobe lights?|blitzlichter|blitzlicht) (?<v>{OnOffWords}|auto)", "lights.strobe", m => Map(m, strobe));
        Add($@"(?:beacon|beacon lights?|antikollisionslicht|rundumleuchte) (?<v>{OnOffWords})", "lights.beacon", m => Map(m, OnOff));
        var taxi = new Dictionary<string, string>(StringComparer.Ordinal) { ["on"] = "taxi", ["an"] = "taxi", ["ein"] = "taxi", ["off"] = "off", ["aus"] = "off" };
        Add(@"(?:taxi lights?|rollscheinwerfer|taxilicht) (?<v>on|an|ein|off|aus)", "lights.nose", m => Map(m, taxi));
        var takeoff = new Dictionary<string, string>(StringComparer.Ordinal) { ["on"] = "to", ["an"] = "to", ["ein"] = "to", ["off"] = "off", ["aus"] = "off" };
        Add(@"(?:takeoff lights?|startlicht) (?<v>on|an|ein|off|aus)", "lights.nose", m => Map(m, takeoff));
        var nose = new Dictionary<string, string>(StringComparer.Ordinal) { ["taxi"] = "taxi", ["takeoff"] = "to", ["to"] = "to", ["off"] = "off", ["aus"] = "off" };
        Add(@"nose lights? (?<v>taxi|takeoff|to|off|aus)", "lights.nose", m => Map(m, nose));
        Add($@"(?:(?:runway )?turnoff lights|abrolllichter|abrolllicht|abrollscheinwerfer) (?<v>{OnOffWords})", "lights.runwayTurnoff", m => Map(m, OnOff));
        Add($@"(?:wing lights?|flügelbeleuchtung|fluegelbeleuchtung|tragflächenbeleuchtung|tragflaechenbeleuchtung) (?<v>{OnOffWords})", "lights.wing", m => Map(m, OnOff));

        // ---------------- Schilder
        var seatbelts = new Dictionary<string, string>(OnOff, StringComparer.Ordinal) { ["auto"] = "auto" };
        Add($@"(?:seatbelts|anschnallzeichen) (?<v>{OnOffWords}|auto)", "signs.seatbelts", m => Map(m, seatbelts));

        // ---------------- APU
        Fixed(@"(?:apu start|start (?:the )?apu|apu starten|apu anlassen)", "apu.start");
        Add($@"apu(?: master)?(?: switch)? (?<v>{OnOffWords})", "apu.master", m => Map(m, OnOff));
        Add($@"apu bleed (?<v>{OnOffWords})", "apu.bleed", m => Map(m, OnOff));

        // ---------------- Baro / QNH
        Add(@"(?:qnh|baro|altimeter|höhenmesser)(?: to| auf)? (?<v>\d{3,4})", "efis.baro", m => int.TryParse(m.Groups["v"].Value, out var hpa) && hpa is >= 900 and <= 1100 ? m.Groups["v"].Value : "");
        Fixed(@"(?:(?:baro|qnh|altimeter|höhenmesser) )?(?:standard|std)(?: set)?", "efis.baroMode", "STD");
        Fixed(@"(?:baro qnh|qnh mode|qnh modus|höhenmesser qnh|hoehenmesser qnh)", "efis.baroMode", "QNH");

        // ---------------- FCU
        Add(@"(?:altitude|alt|höhe|hoehe)(?: to| auf)? (?<v>\d{4,5})", "fcu.altitude", m => int.TryParse(m.Groups["v"].Value, out var ft) && ft >= 1000 ? m.Groups["v"].Value : "");
        Add(@"fl(?: to| auf)? (?<v>\d{2,3})", "fcu.altitude", m => "FL" + m.Groups["v"].Value.PadLeft(3, '0'));
        Add(@"(?:speed|geschwindigkeit|fahrt|spd)(?: to| auf)? (?<v>\d{3})", "fcu.speed", m => m.Groups["v"].Value);
        Fixed(@"(?:speed managed|managed speed|speed push|geschwindigkeit managed)", "fcu.speedManaged");
        Add(@"(?:heading|hdg|kurs|steuerkurs)(?: to| auf)? (?<v>\d{1,3})", "fcu.heading", m => m.Groups["v"].Value.PadLeft(3, '0'));
        Fixed(@"(?:heading managed|managed heading|managed nav|nav managed|heading push|kurs managed|steuerkurs managed)", "fcu.headingManaged");
        Add(@"(?:vs|steigrate|sinkrate)(?: to| auf)? (?<v>-?\d{2,4})", "fcu.vs", m => m.Groups["v"].Value);
        Fixed(@"(?:altitude pull|pull altitude|open climb|open descent|höhe ziehen|hoehe ziehen)", "fcu.altitudePull");
        Fixed(@"(?:altitude push|push altitude|managed climb|managed descent|höhe drücken|hoehe druecken|höhe drucken)", "fcu.altitudePush");
        Add($@"(?:autopilot|ap)(?: 1)? (?<v>{OnOffWords})", "fcu.ap1", m => Map(m, OnOff));
        Add($@"(?:autopilot|ap) 2 (?<v>{OnOffWords})", "fcu.ap2", m => Map(m, OnOff));
        Add($@"(?:autothrust|schubautomatik) (?<v>{OnOffWords})", "fcu.athr", m => Map(m, OnOff));
        var arm = new Dictionary<string, string>(OnOff, StringComparer.Ordinal) { ["arm"] = "on", ["armed"] = "on" };
        Add($@"(?:loc|localizer|localiser|lock) (?<v>{OnOffWords}|arm|armed)", "fcu.loc", m => Map(m, arm));
        Add($@"(?:approach|appr|anflug) (?<v>{OnOffWords}|arm|armed)", "fcu.appr", m => Map(m, arm));
        Fixed(@"arm (?:the )?approach", "fcu.appr", "on");
        Add($@"fd (?<v>{OnOffWords})", "efis.fd", m => Map(m, OnOff));
        Add($@"ls (?<v>{OnOffWords})", "efis.ls", m => Map(m, OnOff));

        // ---------------- Anti-Ice, Packs
        Add($@"(?:engine|eng|triebwerks?) antiice (?<v>{OnOffWords})", "antiice.engines", m => Map(m, OnOff));
        Add($@"wing antiice (?<v>{OnOffWords})", "antiice.wing", m => Map(m, OnOff));
        Add($@"pack 1 (?<v>{OnOffWords})", "air.pack1", m => Map(m, OnOff));
        Add($@"pack 2 (?<v>{OnOffWords})", "air.pack2", m => Map(m, OnOff));

        // ---------------- Transponder / TCAS
        Add(@"(?:squawk|transponder|transponder code)(?: to| auf)? (?<v>\d{4})", "xpdr.code", m => m.Groups["v"].Value);
        var xpdr = new Dictionary<string, string>(StringComparer.Ordinal) { ["standby"] = "stby", ["stby"] = "stby", ["auto"] = "auto", ["on"] = "on", ["an"] = "on" };
        Add(@"transponder (?<v>standby|stby|auto|on|an)", "xpdr.mode", m => Map(m, xpdr));
        Fixed(@"squawk (?:standby|stby)", "xpdr.mode", "stby");
        var tcas = new Dictionary<string, string>(StringComparer.Ordinal) { ["tara"] = "tara", ["ta"] = "ta", ["standby"] = "stby", ["stby"] = "stby" };
        Add(@"tcas(?: to| auf)? (?<v>tara|ta|standby|stby)(?: only)?", "tcas.mode", m => Map(m, tcas));
        Fixed(@"(?<!tcas )tara", "tcas.mode", "tara");
        Fixed(@"(?<!tcas )ta only", "tcas.mode", "ta");
        var traffic = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["thrt"] = "thrt", ["threat"] = "thrt", ["bedrohung"] = "thrt", ["all"] = "all", ["alle"] = "all", ["abv"] = "abv", ["above"] = "abv", ["oben"] = "abv",
            ["darüber"] = "abv", ["darueber"] = "abv", ["blw"] = "blw", ["below"] = "blw", ["unten"] = "blw", ["darunter"] = "blw",
        };
        Add(@"(?:tcas )?(?:traffic|verkehr)(?: to| auf| mode| anzeige| display)? (?<v>thrt|threat|bedrohung|all|alle|abv|above|oben|darüber|darueber|blw|below|unten|darunter)", "tcas.traffic", m => Map(m, traffic));
        Add($@"(?:altitude reporting|alt reporting|alt rptg|alt report|höhenübermittlung|hoehenuebermittlung|höhenmeldung|hoehenmeldung)(?: to| auf)? (?<v>{OnOffWords})", "xpdr.altReporting", m => Map(m, OnOff));

        // ---------------- Funk (COM 1)
        Add(@"(?:(?:com|kom|radio|funk)(?: 1)? )?(?:standby|stby)(?: frequency| frequenz)?(?: to| auf)? (?<v>1[1-3]\d(?:\.\d{1,3})?)", "com1.standby", m => m.Groups["v"].Value);
        Fixed(@"(?:(?:com|kom)(?: 1)? (?:swap|transfer|tauschen|wechseln|umschalten)|(?:swap|transfer) (?:com|kom)(?: 1)?|swap (?:frequencies|frequency)|frequenzen? tauschen)", "com1.swap");

        // ---------------- Elektrik
        Add($@"(?:batteries|battery|batterien|batterie|bat)(?: 1 and 2| 1 2| both| beide)? (?<v>{OnOffWords})", "elec.battery", m => Map(m, OnOff));
        Add($@"(?:external power|ext power|ext pwr|externe stromversorgung|externer strom|bodenstrom|ground power|gpu)(?: power)? (?<v>{OnOffWords})", "elec.extPwr", m => Map(m, OnOff));

        // ---------------- Licht und Schilder (Nav/Logo, No smoking)
        Add($@"(?:nav and logo lights?|nav logo lights?|nav lights?|navigation lights?|positionslichter|positionslicht|navigationslichter|navigationslicht)(?: and logo)? (?<v>{OnOffWords})", "lights.nav", m => Map(m, OnOff));
        Add($@"(?<!(?:and|und|nav) )(?:logo lights?|logolichter|logolicht|logo beleuchtung)(?<!nav logo lights?) (?<v>{OnOffWords})", "lights.logo", m => Map(m, OnOff));
        var noSmoking = new Dictionary<string, string>(OnOff, StringComparer.Ordinal) { ["auto"] = "auto" };
        Add($@"(?:no smoking|nichtraucherzeichen|nichtraucher|rauchverbotszeichen|rauchverbot)(?: signs?| zeichen)? (?<v>{OnOffWords}|auto)", "signs.noSmoking", m => Map(m, noSmoking));

        // ---------------- Anti-Ice je Triebwerk, Sonden- und Scheibenheizung
        Add($@"(?:(?:engine|eng|triebwerk|triebwerks) 1 (?:antiice|enteisung)|(?:antiice|enteisung) (?:engine|eng|triebwerk) 1) (?<v>{OnOffWords})", "antiice.eng1", m => Map(m, OnOff));
        Add($@"(?:(?:engine|eng|triebwerk|triebwerks) 2 (?:antiice|enteisung)|(?:antiice|enteisung) (?:engine|eng|triebwerk) 2) (?<v>{OnOffWords})", "antiice.eng2", m => Map(m, OnOff));
        Add($@"(?:triebwerksenteisung|triebwerk enteisung|engines antiice) (?<v>{OnOffWords})", "antiice.engines", m => Map(m, OnOff));
        Add($@"(?:flügelenteisung|fluegelenteisung|flügel enteisung|wing enteisung) (?<v>{OnOffWords})", "antiice.wing", m => Map(m, OnOff));

        // Sondenheizung kennt nur ON und AUTO – "aus" heißt der Taster AUTO
        var probe = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["on"] = "on", ["an"] = "on", ["ein"] = "on", ["auto"] = "auto", ["off"] = "auto", ["aus"] = "auto",
        };
        Add(@"(?:probe and window heat|probe window heat|probe heat|probes|window heat|sondenheizung|sonden heizung|scheibenheizung)(?: and window heat)? (?<v>on|an|ein|auto|off|aus)", "antiice.probeHeat", m => Map(m, probe));

        // ---------------- EFIS: ND-Modus und -Reichweite
        var ndMode = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ls"] = "ls", ["vor"] = "vor", ["nav"] = "nav", ["arc"] = "arc", ["bogen"] = "arc", ["plan"] = "plan",
        };
        Add(@"nd(?: mode| modus)?(?: to| auf)? (?:rose )?(?<v>ls|vor|nav|arc|bogen|plan)", "efis.ndMode", m => Map(m, ndMode));
        Add(@"(?:(?:nd )?(?:range|reichweite)|nd)(?: to| auf)? (?<v>10|20|40|80|160|320)", "efis.ndRange", m => m.Groups["v"].Value);

        // ---------------- FCU: Höhen-Schrittweite, Expedite
        Add(@"(?:(?:altitude|alt|höhe|hoehe|fcu) (?:increment|inkrement|schritt|schrittweite|step)|höhenschritt|hoehenschritt|increment)(?: to| auf)? (?<v>100|1000)", "fcu.altIncrement", m => m.Groups["v"].Value);
        Add($@"(?:expedite|exped)(?: mode)? (?<v>{OnOffWords})", "fcu.exped", m => Map(m, OnOff));
        Fixed(@"(?:expedite|exped) (?:climb|descent|descend)", "fcu.exped", "on");

        return [.. rules];
    }

    [GeneratedRegex(@"^(?:pause|stop|standby|ruhe|halt|mute|stumm|schweig|aus)$")]
    private static partial Regex PauseWords();

    [GeneratedRegex(@"^(?:weiter|resume|continue|listen|unmute|zuhören|zuhoeren|an|on|back|wieder da|hör zu|hoer zu)$")]
    private static partial Regex ResumeWords();

    [GeneratedRegex(@"^(?:say again|bitte say again)$")]
    private static partial Regex SayAgainPattern();

    // "Briefing", "Start briefing", "Let's do the briefing", "Briefing starten", "Lass uns briefen" (Höflichkeit und Artikel sind schon weg)
    [GeneratedRegex(@"^(?:(?:start|begin|starte|beginne|lets|let s|let us|lass uns|time for|zeit für|zeit fuer)(?: do| the| den| das| zum)? )?(?:crew |departure |abflug )?(?:briefing|briefen)(?: (?:time|starten|beginnen|machen|anfangen))?$")]
    private static partial Regex BriefingPattern();

    [GeneratedRegex(@"^(?:(?:stop|cancel|abort|end|beenden|abbrechen|stopp) checklist|checklist (?:stop|cancel|abort|end|beenden|abbrechen|stopp))$")]
    private static partial Regex ChecklistStopPattern();

    [GeneratedRegex(@"^(?:skip|skip (?:item|it|that)|next item|überspringen|ueberspringen|punkt überspringen|punkt ueberspringen|nächster punkt|naechster punkt)$")]
    private static partial Regex ChecklistSkipPattern();
}
