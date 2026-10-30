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

    [GeneratedRegex(@"^\p{L}+$")]
    private static partial Regex Letters();
}

/// <summary>
/// Besetzung pro Flug (rein rechnend, damit testbar): Name zufällig aus der Liste oder fest aus den Einstellungen, Stimme
/// passend zum Geschlecht (Kokoro: af_/bf_ weiblich, am_/bm_ männlich) und nie die Stimme einer ATC-Station.
/// </summary>
public static class FirstOfficerCasting
{
    /// <summary>Ersatz, wenn keine Kokoro-Stimmen neben der Duopilo.exe liegen (Entwicklung, Tests).</summary>
    public static readonly IReadOnlyList<string> DefaultVoices =
    [
        "bf_emma", "af_heart", "bf_isabella", "af_bella", "bf_alice", "af_nicole", "af_sarah", "bf_lily",
        "bm_george", "bm_lewis", "am_michael", "bm_daniel", "am_adam", "bm_fable", "am_eric", "am_liam",
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

    /// <summary>Zufällige Kokoro-Stimme des Geschlechts, keine der ATC-Stationen (gibt es keine freie mehr, irgendeine passende).</summary>
    public static string PickVoice(VoiceGender gender, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> avoid, Random random)
    {
        var all = (installed.Count > 0 ? installed : DefaultVoices)
            .Where(v => GenderOfVoice(v) == gender)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        if (all.Count == 0)
        {
            all = DefaultVoices.Where(v => GenderOfVoice(v) == gender).ToList();
        }

        var free = all.Where(v => !avoid.Contains(v, StringComparer.Ordinal)).ToList();
        var pool = free.Count > 0 ? free : all;
        return pool[random.Next(pool.Count)];
    }

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
        var nameOk = config.Name.Length > 0 ? p.FixedName && p.Name == config.Name : !p.FixedName;
        var voiceOk = fixedVoice is not null ? p.FixedVoice && p.Voice == fixedVoice : !p.FixedVoice;
        var genderOk = config.Name.Length == 0 || fixedVoice is not null || config.Gender == FirstOfficerConfig.Auto
                       || (config.Gender == FirstOfficerConfig.Female) == (p.Gender == VoiceGender.Female);
        return nameOk && voiceOk && genderOk && KokoroSpeechEngine.IsEnglishVoice(p.Voice) && p.Name.Length > 0;
    }
}
