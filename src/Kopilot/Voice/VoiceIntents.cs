using System.Text.RegularExpressions;

namespace Kopilot.Voice;

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
}

/// <summary>Erkannter Befehl. <see cref="ActionId"/>/<see cref="Value"/> entsprechen den Aktionen im Flugzeugprofil.</summary>
public sealed record VoiceIntent(IntentKind Kind, string? ActionId = null, string? Value = null, QueryKind? Query = null)
{
    public static readonly VoiceIntent Unknown = new(IntentKind.Unknown);

    public override string ToString() => Kind switch
    {
        IntentKind.Action => Value is null ? ActionId! : $"{ActionId} = {Value}",
        IntentKind.Query => $"query {Query}",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Kopilot-Befehle nach festen Mustern (deutsch und englisch, mit Synonymen) – kein Sprachmodell.
/// Arbeitet auf der Form von <see cref="VoiceText.Normalize"/>. Passen mehrere verschiedene Befehle, gilt der Satz als unklar
/// ("Say again") – ein Satz, ein Befehl.
/// </summary>
public static partial class VoiceIntents
{
    private static readonly HashSet<string> YesWords = new(StringComparer.Ordinal)
    {
        // Gleiche Wörter wie InputService.ConfirmAsync – sonst würde die Bestätigung dort abgelehnt
        "confirm", "confirmed", "correct", "affirm", "affirmative", "yes", "ja", "jawohl", "korrekt", "richtig", "bestätigt", "bestätige",
        "ok", "okay",
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
        if (words.All(w => YesWords.Contains(w) || NoWords.Contains(w) || ConfirmFiller.Contains(w)))
        {
            var yes = words.Any(YesWords.Contains);
            var no = words.Any(NoWords.Contains);
            if (no)
            {
                return new VoiceIntent(IntentKind.Negative);
            }

            if (yes)
            {
                return new VoiceIntent(IntentKind.Confirm);
            }
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
        .Select(q => (new Regex($@"^(?:(?:{q.Pattern}) (?:check|checks|prüfen|pruefen|kontrolle|status)|(?:check|prüfe|pruefe|kontrolle) (?:{q.Pattern}))$", RegexOptions.CultureInvariant | RegexOptions.Compiled), q.Kind))
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

        // ---------------- Klappen
        var flaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = "0", ["up"] = "0", ["zero"] = "0", ["retract"] = "0", ["einfahren"] = "0", ["ein"] = "0",
            ["1"] = "1", ["2"] = "2", ["3"] = "3", ["full"] = "FULL", ["voll"] = "FULL",
        };
        Add(@"(?:flaps|klappen)(?: to| auf)? (?<v>0|1|2|3|up|zero|retract|einfahren|ein|full|voll)", "flaps", m => Map(m, flaps));
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
        Add($@"(?:runway )?turnoff lights (?<v>{OnOffWords})", "lights.runwayTurnoff", m => Map(m, OnOff));
        Add($@"wing lights? (?<v>{OnOffWords})", "lights.wing", m => Map(m, OnOff));

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
        Fixed(@"(?:baro qnh|qnh mode)", "efis.baroMode", "QNH");

        // ---------------- FCU
        Add(@"(?:altitude|alt|höhe|hoehe)(?: to| auf)? (?<v>\d{4,5})", "fcu.altitude", m => int.TryParse(m.Groups["v"].Value, out var ft) && ft >= 1000 ? m.Groups["v"].Value : "");
        Add(@"fl(?: to| auf)? (?<v>\d{2,3})", "fcu.altitude", m => "FL" + m.Groups["v"].Value.PadLeft(3, '0'));
        Add(@"(?:speed|geschwindigkeit|fahrt|spd)(?: to| auf)? (?<v>\d{3})", "fcu.speed", m => m.Groups["v"].Value);
        Fixed(@"(?:speed managed|managed speed|speed push|geschwindigkeit managed)", "fcu.speedManaged");
        Add(@"(?:heading|hdg|kurs|steuerkurs)(?: to| auf)? (?<v>\d{1,3})", "fcu.heading", m => m.Groups["v"].Value.PadLeft(3, '0'));
        Fixed(@"(?:heading managed|managed heading|managed nav|nav managed|heading push)", "fcu.headingManaged");
        Add(@"(?:vs|steigrate|sinkrate)(?: to| auf)? (?<v>-?\d{2,4})", "fcu.vs", m => m.Groups["v"].Value);
        Fixed(@"(?:altitude pull|pull altitude|open climb|open descent)", "fcu.altitudePull");
        Fixed(@"(?:altitude push|push altitude|managed climb|managed descent)", "fcu.altitudePush");
        Add($@"(?:autopilot|ap)(?: 1)? (?<v>{OnOffWords})", "fcu.ap1", m => Map(m, OnOff));
        Add($@"(?:autopilot|ap) 2 (?<v>{OnOffWords})", "fcu.ap2", m => Map(m, OnOff));
        Add($@"autothrust (?<v>{OnOffWords})", "fcu.athr", m => Map(m, OnOff));
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

        return [.. rules];
    }

    [GeneratedRegex(@"^(?:pause|stop|standby|ruhe|halt|mute|stumm|schweig|aus)$")]
    private static partial Regex PauseWords();

    [GeneratedRegex(@"^(?:weiter|resume|continue|listen|unmute|zuhören|zuhoeren|an|on|back|wieder da|hör zu|hoer zu)$")]
    private static partial Regex ResumeWords();

    [GeneratedRegex(@"^(?:say again|bitte say again)$")]
    private static partial Regex SayAgainPattern();
}
