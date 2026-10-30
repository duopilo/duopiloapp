using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Smalltalk;
using Duopilo.Speech;

namespace Duopilo.Voice;

/// <summary>Der First Officer eines Flugs: Name, Geschlecht und Kokoro-Stimme. Bleibt während des Flugs gleich.</summary>
/// <param name="FlightKey">Flug (SimBrief-Request-ID; <see cref="FirstOfficerRoster.Unplanned"/> ohne Plan).</param>
/// <param name="Voice">Kokoro-Stimme für Callouts, Antworten und englischen Smalltalk ("bf_emma").</param>
/// <param name="FixedName">Name aus den Einstellungen (sonst zufällig).</param>
/// <param name="FixedVoice">Stimme aus voice-settings.json (voices.kopilot).</param>
/// <param name="Introduced">Hat sich in diesem Flug schon vorgestellt.</param>
public sealed record FirstOfficerPersona(
    string FlightKey,
    string Name,
    VoiceGender Gender,
    string Voice,
    DateTimeOffset CreatedUtc,
    bool FixedName = false,
    bool FixedVoice = false,
    bool Introduced = false)
{
    /// <summary>Anrede in normalisierter Form (erstes Wort des Namens, z. B. "sam") – null, wenn der Name keine taugliche Anrede ist.</summary>
    public string? AddressWord => FirstOfficerNames.AddressWord(Name);

    public string GenderText => Gender == VoiceGender.Female ? "female" : "male";
}

/// <summary>
/// Namen für den First Officer, getrennt nach weiblich und männlich: international gut aussprechbar (Deutsch und Englisch), ein Wort,
/// und kein Wort, das die Spracherkennung schon kennt (NATO-Alphabet wie „Victor“, Befehle wie „Max“, „Roger“).
/// </summary>
public static partial class FirstOfficerNames
{
    public static readonly IReadOnlyList<string> Female =
    [
        "Anna", "Emma", "Laura", "Sarah", "Julia", "Lena", "Mia", "Nina", "Sophie", "Clara", "Maria", "Elena", "Hannah", "Lisa",
        "Eva", "Olivia", "Nora", "Alice", "Lucy", "Ella",
    ];

    public static readonly IReadOnlyList<string> Male =
    [
        "Sam", "Ben", "Daniel", "David", "Leo", "Lucas", "Noah", "Adam", "Jonas", "Thomas", "Martin", "Paul", "Peter", "Erik",
        "Felix", "Oliver", "Simon", "Adrian", "Marco", "Hugo", "Nico", "Elias", "Leon", "Tim",
    ];

    public static IReadOnlyList<string> For(VoiceGender gender) => gender == VoiceGender.Female ? Female : Male;

    /// <summary>Geschlecht eines Namens aus der Liste, sonst null.</summary>
    public static VoiceGender? GenderOf(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n))
        {
            return null;
        }

        if (Female.Contains(n, StringComparer.OrdinalIgnoreCase))
        {
            return VoiceGender.Female;
        }

        return Male.Contains(n, StringComparer.OrdinalIgnoreCase) ? VoiceGender.Male : null;
    }

    /// <summary>Erstes Wort des Namens in der Form der Spracherkennung (nur Buchstaben, mindestens zwei), sonst null.</summary>
    public static string? AddressWord(string? name)
    {
        var words = VoiceText.Words(VoiceText.Normalize(name));
        return words.Length > 0 && words[0].Length >= 2 && Letters().IsMatch(words[0]) && words[0] != "kopilot" ? words[0] : null;
    }

    /// <summary>
    /// Häufige englische und deutsche Wörter, die einem Namen der Listen zum Verwechseln ähnlich sind (gleiche Schreibform oder ein
    /// Buchstabe Unterschied: "been"/Ben, "pull"/Paul, "nice"/Nico, "klar"/Clara) – sie gelten nie als Anrede, nur der genaue Name.
    /// Ermittelt gegen die häufigsten Wörter beider Sprachen; dazu das NATO-Alphabet.
    /// </summary>
    public static readonly IReadOnlySet<string> CommonWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "any", "ans", "aha", "ant", "anal", "aka", "aua", "era", "eta", "sarg", "saga", "july", "juli", "lend", "lens", "lent", "lenk",
        "nine", "ninth", "ninja", "sorry", "soft", "sowie", "sofa", "soy", "soggy", "sony", "klar", "klare", "klaro", "marry", "mafia",
        "mania", "elend", "hand", "hang", "haha", "list", "liga", "lira", "north", "nor", "norm", "nord", "nova", "alive", "alike", "luck",
        "lucky", "lucid", "looky", "elf", "eel", "eels", "elk", "elm", "ell", "been", "luchs", "not", "now", "nah", "noon", "non", "nod",
        "nook", "nos", "tombs", "martyr", "margin", "martini", "martian", "pull", "pal", "pail", "pall", "pater", "penner", "pepper",
        "potter", "pester", "putter", "epic", "eerie", "ethik", "ethic", "feelin", "olive", "olives", "oliven", "summon", "aryan", "mark",
        "march", "marks", "markt", "marke", "huge", "hug", "hugs", "nice", "nick", "nich", "nike", "lesson", "lion", "lemon", "lean",
        "lern", "loon", "lehn",
        "alpha", "alfa", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "juliett", "kilo", "lima",
        "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "whisky", "xray",
        "yankee", "zulu",
    };

    /// <summary>
    /// Ist <paramref name="word"/> (normalisiert, ein Wort) die Anrede <paramref name="addressWord"/>? Whisper schreibt Namen oft
    /// anders ("Erik" → "Eric", "Nico" → "Niko", "Hannah" → "Hanna"): Gleich ist, was nach <see cref="SpellingKey"/> gleich
    /// aussieht (k/c, ph/f, th/t, y/i, doppelte Buchstaben, stummes h am Ende), bei Namen ab 4 Buchstaben auch ein Buchstabe
    /// Unterschied bei gleichem Anfang ("Jonah" → Jonas). Normale Wörter zählen dabei nie (<see cref="CommonWords"/>, Befehle,
    /// Abfragen, Ansprachewörter) – der genaue Name dagegen immer.
    /// </summary>
    public static bool IsAddress(string? word, string? addressWord)
    {
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(addressWord))
        {
            return false;
        }

        if (word == addressWord)
        {
            return true;
        }

        if (word.Length < 2 || !Letters().IsMatch(word) || CommonWords.Contains(word) || word is "kopilot" or "copilot"
            || VoiceIntents.Recognize(word, ["kopilot"]).Kind != IntentKind.Unknown)
        {
            return false;
        }

        var heard = SpellingKey(word);
        var name = SpellingKey(addressWord);
        if (heard.Length == 0 || name.Length == 0)
        {
            return false;
        }

        if (heard == name)
        {
            return true;
        }

        return addressWord.Length >= 4 && word.Length >= 3 && heard[0] == name[0] && AtMostOneEdit(heard, name);
    }

    /// <summary>
    /// Schreibform eines Namens, in der Varianten zusammenfallen: klein, ohne Akzente, ph→f, th→t, ck/c→k, y→i, "ie" am Ende → i,
    /// h nach einem Vokal am Ende weg, doppelte Buchstaben einfach ("Sophie" → "sofi", "Hannah" → "hana", "Erick" → "erik").
    /// </summary>
    public static string SpellingKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var folded = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetter(c))
            {
                folded.Append(c);
            }
        }

        var w = folded.ToString()
            .Replace("ph", "f", StringComparison.Ordinal)
            .Replace("th", "t", StringComparison.Ordinal)
            .Replace("ck", "k", StringComparison.Ordinal)
            .Replace('c', 'k')
            .Replace('y', 'i');
        if (w.EndsWith("ie", StringComparison.Ordinal))
        {
            w = w[..^1];
        }

        if (w.Length > 2 && w[^1] == 'h' && w[^2] is 'a' or 'e' or 'i' or 'o' or 'u')
        {
            w = w[..^1];
        }

        var sb = new StringBuilder(w.Length);
        foreach (var c in w)
        {
            if (sb.Length == 0 || sb[^1] != c)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Höchstens ein Buchstabe anders, zu viel oder zu wenig (Levenshtein ≤ 1).</summary>
    private static bool AtMostOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        var i = 0;
        var j = 0;
        var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (a.Length == b.Length)
            {
                i++;
            }

            j++;
        }

        return edits + (b.Length - j) + (a.Length - i) <= 1;
    }

    [GeneratedRegex(@"^\p{L}+$")]
    private static partial Regex Letters();
}

/// <summary>
/// Besetzung pro Flug (rein rechnend, damit testbar): Name zufällig aus der Liste oder fest aus den Einstellungen, Stimme
/// passend zum Geschlecht (Kokoro: af_/bf_ weiblich, am_/bm_ männlich) und nie die Stimme einer ATC-Station.
/// </summary>
public static class FirstOfficerCasting
{
    /// <summary>
    /// Weibliche Stimmen für den First Officer: nur gute laut VOICES.md von hexgrad/Kokoro-82M (Note C+ oder besser). Nicht
    /// af_nicole (Flüster-/ASMR-Stimme, B−), die übrigen weiblichen Stimmen liegen bei C oder darunter.
    /// </summary>
    public static readonly IReadOnlyList<string> FemaleVoices = ["af_heart", "af_bella", "bf_emma", "af_aoede", "af_kore", "af_sarah"];

    /// <summary>Männliche Stimmen für den First Officer (C+). Nicht am_liam, am_eric, am_echo, am_onyx, am_adam, am_santa (D und schlechter).</summary>
    public static readonly IReadOnlyList<string> MaleVoices = ["am_michael", "am_fenrir", "am_puck"];

    /// <summary>Reserve (C), nur wenn die männlichen Stimmen oben fehlen oder alle schon eine ATC-Station hat.</summary>
    public static readonly IReadOnlyList<string> MaleReserve = ["bm_george", "bm_fable"];

    /// <summary>Alle Stimmen, die der First Officer bekommen kann (Auswahl im Dashboard).</summary>
    public static IReadOnlyList<string> Voices { get; } = [.. FemaleVoices, .. MaleVoices, .. MaleReserve];

    /// <summary>Ersatz, wenn keine Kokoro-Stimmen neben der Duopilo.exe liegen (Entwicklung, Tests).</summary>
    public static readonly IReadOnlyList<string> DefaultVoices =
    [
        "af_heart", "af_bella", "bf_emma", "af_aoede", "af_kore", "af_sarah", "bf_isabella", "bf_alice", "bf_lily",
        "am_michael", "am_fenrir", "am_puck", "bm_george", "bm_fable", "bm_lewis", "bm_daniel", "am_adam", "am_eric", "am_liam",
    ];

    /// <summary>Geschlecht einer Kokoro-Stimme: zweiter Buchstabe f/m ("bf_emma" → weiblich).</summary>
    public static VoiceGender? GenderOfVoice(string? voice) =>
        KokoroSpeechEngine.IsEnglishVoice(voice) ? voice![1] == 'f' ? VoiceGender.Female : VoiceGender.Male : null;

    /// <param name="flightKey">Flug.</param>
    /// <param name="config">Einstellungen (fester Name, Geschlecht).</param>
    /// <param name="fixedVoice">Feste Kokoro-Stimme aus voice-settings.json (null = frei).</param>
    /// <param name="engineGender">Geschlecht der Windows-Stimme, wenn sie statt Kokoro spricht (dann passt der Name zu ihr).</param>
    /// <param name="installed">Vorhandene Kokoro-Stimmen (leer = <see cref="DefaultVoices"/>).</param>
    /// <param name="avoid">Stimmen der ATC-Stationen – der First Officer bekommt keine davon.</param>
    public static FirstOfficerPersona Create(
        string flightKey,
        FirstOfficerConfig config,
        string? fixedVoice,
        VoiceGender? engineGender,
        IReadOnlyCollection<string> installed,
        IReadOnlyCollection<string> avoid,
        Random random,
        DateTimeOffset now)
    {
        var voiceGender = GenderOfVoice(fixedVoice);
        var hasFixedName = config.Name.Length > 0;
        VoiceGender gender;
        if (voiceGender is { } vg)
        {
            // Feste Stimme: ihr Geschlecht entscheidet (ein fester Name bleibt trotzdem)
            gender = vg;
        }
        else if (hasFixedName)
        {
            gender = config.Gender switch
            {
                FirstOfficerConfig.Female => VoiceGender.Female,
                FirstOfficerConfig.Male => VoiceGender.Male,
                _ => FirstOfficerNames.GenderOf(config.Name)
                     ?? (AtcVoiceAssigner.StableHash(config.Name.ToLowerInvariant()) % 2 == 0 ? VoiceGender.Female : VoiceGender.Male),
            };
        }
        else
        {
            gender = engineGender ?? (random.Next(2) == 0 ? VoiceGender.Female : VoiceGender.Male);
        }

        var names = FirstOfficerNames.For(gender);
        var name = hasFixedName ? config.Name : names[random.Next(names.Count)];
        var voice = voiceGender is not null ? fixedVoice! : PickVoice(gender, installed, avoid, random);
        return new FirstOfficerPersona(flightKey, name, gender, voice, now, hasFixedName, voiceGender is not null);
    }

    /// <summary>
    /// Zufällige gute Kokoro-Stimme des Geschlechts (<see cref="FemaleVoices"/>, <see cref="MaleVoices"/>, dann
    /// <see cref="MaleReserve"/>), keine der ATC-Stationen. Hat ATC alle, irgendeine gute – neue Stationen meiden sie dann.
    /// </summary>
    public static string PickVoice(VoiceGender gender, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> avoid, Random random)
    {
        var have = installed.Count > 0 ? new HashSet<string>(installed, StringComparer.Ordinal) : null;
        var tiers = gender == VoiceGender.Female
            ? new IReadOnlyList<string>[] { FemaleVoices }
            : new IReadOnlyList<string>[] { MaleVoices, MaleReserve };
        foreach (var tier in tiers)
        {
            var free = tier.Where(v => (have is null || have.Contains(v)) && !avoid.Contains(v, StringComparer.Ordinal)).ToList();
            if (free.Count > 0)
            {
                return free[random.Next(free.Count)];
            }
        }

        var any = tiers.SelectMany(t => t).Where(v => have is null || have.Contains(v)).ToList();
        if (any.Count == 0)
        {
            any = [.. tiers[0]];
        }

        return any[random.Next(any.Count)];
    }

    /// <summary>Satz zum Probehören einer Stimme im Dashboard (englisch, wie Callouts und Funk).</summary>
    public static string PreviewText(string? name) =>
        Loc.En("voice.firstOfficer.introduction", string.IsNullOrWhiteSpace(name) ? "Alex" : name.Trim()) + " Flaps one, speed checked.";

    /// <summary>Vorstellung beim Briefing in der Sprache des Smalltalks ("Hi, I'm Sam, I'll be your first officer today.").</summary>
    public static string Introduction(string name, string language) =>
        language == "en" ? Loc.En("voice.firstOfficer.introduction", name) : Loc.T("voice.firstOfficer.introduction", name);
}

/// <summary>Speichert den First Officer des laufenden Flugs (Datenordner), damit er nach einem Neustart von Duopilo derselbe bleibt.</summary>
public sealed class FirstOfficerStore(string path)
{
    public const string FileName = "first-officer.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = path;

    public FirstOfficerPersona? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<FirstOfficerPersona>(File.ReadAllText(FilePath), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(FirstOfficerPersona persona)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(persona, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nur eine Hilfe für den Neustart – ohne Datei bekommt der Flug nach einem Neustart einen neuen First Officer
        }
    }
}

/// <summary>
/// Hält den First Officer des laufenden Flugs: pro Flug (SimBrief-Plan) ein neuer, während des Flugs derselbe – auch nach einem
/// Neustart von Duopilo. Ohne Plan gilt er, bis ein Plan geladen wird, und bleibt dann für diesen Flug.
/// Nicht threadsicher – die Sprachschleife ruft alles nacheinander auf.
/// </summary>
public sealed class FirstOfficerRoster(FirstOfficerStore? store = null, TimeProvider? time = null, Random? random = null)
{
    /// <summary>Kennung ohne SimBrief-Plan.</summary>
    public const string Unplanned = "-";

    /// <summary>Ältere Sicherungen gelten als anderer Flug.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Random _random = random ?? new Random();
    private bool _loaded;

    public FirstOfficerPersona? Current { get; private set; }

    /// <summary>Kennung des Flugs wie beim ATC-Modul: SimBrief-Request-ID, sonst Start-Ziel-Erstellzeit, ohne Plan <see cref="Unplanned"/>.</summary>
    public static string FlightKeyOf(Ofp? ofp) => ofp is null
        ? Unplanned
        : ofp.RequestId ?? $"{ofp.Origin.Icao}-{ofp.Destination.Icao}-{ofp.GeneratedUtc?.ToUnixTimeSeconds()}";

    /// <summary>
    /// Den First Officer für diesen Flug sicherstellen. Rückgabe: der aktuelle (null = ausgeschaltet) und ob er neu ist.
    /// </summary>
    public (FirstOfficerPersona? Persona, bool Changed) Ensure(
        string flightKey,
        FirstOfficerConfig config,
        string? fixedVoice,
        VoiceGender? engineGender,
        IReadOnlyCollection<string> installed,
        IReadOnlyCollection<string> avoid)
    {
        if (!config.Enabled)
        {
            var had = Current is not null;
            Current = null;
            return (null, had);
        }

        fixedVoice = KokoroSpeechEngine.IsEnglishVoice(fixedVoice) ? fixedVoice : null;
        var now = _time.GetUtcNow();
        if (Current is null && !_loaded)
        {
            _loaded = true;
            if (store?.Load() is { } saved && now - saved.CreatedUtc < MaxAge && Matches(saved, config, fixedVoice)
                && (saved.FlightKey == flightKey || saved.FlightKey == Unplanned || flightKey == Unplanned))
            {
                Current = saved;
            }
        }

        if (Current is { } current && Matches(current, config, fixedVoice))
        {
            if (current.FlightKey == flightKey || flightKey == Unplanned)
            {
                return (current, false);
            }

            if (current.FlightKey == Unplanned)
            {
                // Plan erst nach dem Start geladen: derselbe First Officer gehört jetzt zu diesem Flug
                Current = current with { FlightKey = flightKey };
                store?.Save(Current);
                return (Current, false);
            }
        }

        // Nur die Stimme geändert (andere feste Stimme gleichen Geschlechts, fest ↔ automatisch): Name und Vorstellung bleiben
        if (Current is { } same && (same.FlightKey == flightKey || flightKey == Unplanned || same.FlightKey == Unplanned)
            && NameMatches(same, config) && GenderMatches(same, config, fixedVoice) && same.Name.Length > 0
            && (fixedVoice is null || FirstOfficerCasting.GenderOfVoice(fixedVoice) == same.Gender))
        {
            var voice = fixedVoice ?? FirstOfficerCasting.PickVoice(same.Gender, installed, avoid, _random);
            Current = same with { Voice = voice, FixedVoice = fixedVoice is not null, FlightKey = flightKey == Unplanned ? same.FlightKey : flightKey };
            store?.Save(Current);
            return (Current, true);
        }

        Current = FirstOfficerCasting.Create(flightKey, config, fixedVoice, engineGender, installed, avoid, _random, now);
        store?.Save(Current);
        return (Current, true);
    }

    /// <summary>Hat sich vorgestellt – nicht noch einmal in diesem Flug.</summary>
    public void MarkIntroduced()
    {
        if (Current is { Introduced: false } current)
        {
            Current = current with { Introduced = true };
            store?.Save(Current);
        }
    }

    /// <summary>Passt der gespeicherte First Officer noch zu den Einstellungen (fester Name, Geschlecht, feste Stimme)?</summary>
    private static bool Matches(FirstOfficerPersona p, FirstOfficerConfig config, string? fixedVoice)
    {
        var voiceOk = fixedVoice is not null ? p.FixedVoice && p.Voice == fixedVoice : !p.FixedVoice;
        return NameMatches(p, config) && voiceOk && GenderMatches(p, config, fixedVoice) && KokoroSpeechEngine.IsEnglishVoice(p.Voice) && p.Name.Length > 0;
    }

    private static bool NameMatches(FirstOfficerPersona p, FirstOfficerConfig config) =>
        config.Name.Length > 0 ? p.FixedName && p.Name == config.Name : !p.FixedName;

    private static bool GenderMatches(FirstOfficerPersona p, FirstOfficerConfig config, string? fixedVoice) =>
        config.Name.Length == 0 || fixedVoice is not null || config.Gender == FirstOfficerConfig.Auto
        || (config.Gender == FirstOfficerConfig.Female) == (p.Gender == VoiceGender.Female);
}
