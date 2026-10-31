using System.Globalization;

namespace Duopilo.Speech;

/// <summary>Geschlecht einer Stimme (und des First Officers, der mit ihr spricht).</summary>
public enum VoiceGender
{
    Female,
    Male,
}

/// <summary>Zustand einer lokalen Sprachsynthese, die erst bei Bedarf geladen wird.</summary>
public enum TtsState
{
    /// <summary>Noch nicht gebraucht – nichts geladen.</summary>
    Idle,

    Downloading,

    Ready,

    Failed,
}

/// <summary>Eine Stimme einer Sprachsynthese-Engine.</summary>
/// <param name="Engine">Kennung der Engine, z. B. "piper".</param>
/// <param name="Id">Kennung der Stimme in der Engine, z. B. "de_DE-thorsten-medium".</param>
/// <param name="Language">Sprachcode ("de").</param>
public sealed record TtsVoice(string Engine, string Id, string Language, VoiceGender Gender)
{
    /// <summary>Stimme für <see cref="SpeechRequest.Voice"/>: "piper:de_DE-thorsten-medium".</summary>
    public string Key => Engine + ":" + Id;

    /// <summary>"piper:de_DE-thorsten-medium" → ("piper", "de_DE-thorsten-medium"). Kokoro-Stimmen ("bm_george") haben keinen Präfix.</summary>
    public static (string Engine, string Id)? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < key.Length - 1 ? (key[..colon].Trim().ToLowerInvariant(), key[(colon + 1)..].Trim()) : null;
    }
}

/// <summary>
/// Lokale Sprachsynthese neben Kokoro. Supertonic spricht mit jeder Stimme Englisch und die Oberflächensprache – als Stimme des
/// First Officers spricht es dann Callouts, Antworten und Smalltalk (derselbe Sprecher, <see cref="MultilingualVoices"/>). Kokoro bleibt
/// die Stimme für ATC und für einen First Officer mit Kokoro-Stimme (dann alles Englisch).
/// </summary>
public interface ITtsEngine
{
    /// <summary>Kennung, z. B. "supertonic" oder "piper" – Präfix von <see cref="TtsVoice.Key"/>.</summary>
    string Id { get; }

    /// <summary>Alle Stimmen, die die Engine laden kann (auch noch nicht heruntergeladene).</summary>
    IReadOnlyList<TtsVoice> Voices { get; }

    /// <summary>Zustand einer Stimme (Programm und Modell geladen?).</summary>
    TtsState StateOf(TtsVoice voice);

    /// <summary>Fehler der letzten Vorbereitung (null = keiner).</summary>
    string? Error { get; }

    /// <summary>Programm und Stimme bei Bedarf herunterladen. Kehrt sofort zurück, wenn schon alles da ist.</summary>
    Task PrepareAsync(TtsVoice voice, CancellationToken ct);

    /// <summary>
    /// Text sprechen (wie er ist, ohne Funk-Aussprache) in der Sprache <see cref="TtsVoice.Language"/> – für eine mehrsprachige
    /// Stimme mit <c>voice with { Language = … }</c>. <paramref name="rate"/> wie im Dashboard (1 = normal).
    /// </summary>
    Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct);
}

/// <summary>
/// Stimmen, die mehrere Sprachen sprechen: Supertonic spricht mit jeder Stimme alle Sprachen von <see cref="SupertonicText.Languages"/>.
/// Nur mit so einer Stimme spricht der First Officer Callouts (Englisch) und Smalltalk (Oberflächensprache) mit demselben Sprecher –
/// Kokoro kann nur Englisch.
/// </summary>
public static class MultilingualVoices
{
    /// <summary>Alle mehrsprachigen Stimmen (je Stimme ein Eintrag, Schlüssel wie "supertonic:M1").</summary>
    public static IReadOnlyList<TtsVoice> All { get; } = SupertonicSpeechEngine.Catalog.DistinctBy(v => v.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"supertonic:m1" → "supertonic:M1" (Schreibweise des Katalogs); null = keine mehrsprachige Stimme.</summary>
    public static string? Normalize(string? key) => Find(key)?.Key;

    public static bool IsMultilingual(string? key) => Find(key) is not null;

    public static TtsVoice? Find(string? key)
    {
        var trimmed = key?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : All.FirstOrDefault(v => string.Equals(v.Key, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Spricht die Stimme diese Sprache ("de", "en")?</summary>
    public static bool Speaks(TtsVoice voice, string language) =>
        voice.Engine == SupertonicSpeechEngine.EngineId ? SupertonicText.Languages.Contains(language) : voice.Language == language;
}

/// <summary>Alle Engines für freien Text in anderen Sprachen als Englisch (in der Tray-Instanz: Supertonic, dann Piper).</summary>
public sealed class TtsEngineRegistry(IEnumerable<ITtsEngine> engines)
{
    public IReadOnlyList<ITtsEngine> Engines { get; } = engines.ToList();

    public ITtsEngine? Engine(string? id) =>
        Engines.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stimme zu einem Schlüssel wie "piper:de_DE-thorsten-medium" – null, wenn es Engine oder Stimme nicht gibt.</summary>
    public (ITtsEngine Engine, TtsVoice Voice)? Find(string? key)
    {
        if (TtsVoice.ParseKey(key) is not { } parsed || Engine(parsed.Engine) is not { } engine)
        {
            return null;
        }

        var voice = engine.Voices.FirstOrDefault(v => string.Equals(v.Id, parsed.Id, StringComparison.OrdinalIgnoreCase));
        return voice is null ? null : (engine, voice);
    }
}

/// <summary>In welcher Sprache und mit welcher Stimme der Smalltalk gesprochen wird.</summary>
/// <param name="Language">Sprachcode der Antwort ("de", "en").</param>
/// <param name="Voice">
/// Mehrsprachige Stimme des First Officers, die jetzt spricht – <see cref="TtsVoice.Language"/> = <paramref name="Language"/>.
/// null = seine Kokoro-Stimme (bzw. die Windows-Stimme), wie bei den Callouts.
/// </param>
/// <param name="Wanted">Gewünschte Sprache (Oberflächensprache) – weicht von <paramref name="Language"/> ab, wenn es auf Englisch zurückfällt.</param>
/// <param name="Reason">
/// Warum Englisch statt der gewünschten Sprache: "englishVoice" (die Stimme des First Officers spricht nur Englisch), "noVoice",
/// "downloading", "failed" (null = alles wie gewünscht).
/// </param>
/// <param name="Prepare">Stimme des First Officers, die noch geladen wird (solange spricht seine Kokoro-Stimme).</param>
public sealed record SmalltalkVoiceChoice(string Language, TtsVoice? Voice, string Wanted, string? Reason = null, TtsVoice? Prepare = null)
{
    public bool FellBack => Reason is not null;
}

/// <summary>
/// Sprache und Stimme für den Smalltalk – immer derselbe Sprecher wie bei den Callouts, die Stimme des First Officers entscheidet:
/// Eine Kokoro-Stimme spricht nur Englisch, also ist auch der Smalltalk englisch. Eine mehrsprachige Stimme (Supertonic) spricht den
/// Smalltalk in der Oberflächensprache (Prompt K) und die Callouts auf Englisch. Solange sie lädt oder wenn sie fehlt, spricht seine
/// Kokoro-Stimme – auch den Smalltalk, dann auf Englisch (Hinweis im Dashboard).
/// </summary>
public static class SmalltalkVoices
{
    public const string English = "en";

    /// <param name="language">Gewünschte Sprache (Oberflächensprache; Englisch, wenn der Smalltalk aus ist).</param>
    /// <param name="personaVoice">Mehrsprachige Stimme des First Officers ("supertonic:M1"), null = Kokoro.</param>
    public static SmalltalkVoiceChoice Choose(string? language, string? personaVoice, IEnumerable<ITtsEngine> engines)
    {
        var wanted = string.IsNullOrWhiteSpace(language) ? English : language.Trim().ToLowerInvariant();
        string? Unless(string reason) => wanted == English ? null : reason;

        if (MultilingualVoices.Normalize(personaVoice) is not { } key)
        {
            // Kokoro kann nur Englisch – derselbe Sprecher wie bei den Callouts, also englischer Smalltalk
            return new SmalltalkVoiceChoice(English, null, wanted, Unless("englishVoice"));
        }

        var parsed = TtsVoice.ParseKey(key)!.Value;
        var engine = engines.FirstOrDefault(e => string.Equals(e.Id, parsed.Engine, StringComparison.OrdinalIgnoreCase));
        var voice = engine?.Voices.FirstOrDefault(v => string.Equals(v.Id, parsed.Id, StringComparison.OrdinalIgnoreCase));
        if (engine is null || voice is null)
        {
            return new SmalltalkVoiceChoice(English, null, wanted, Unless("noVoice"));
        }

        switch (engine.StateOf(voice))
        {
            case TtsState.Failed:
                return new SmalltalkVoiceChoice(English, null, wanted, Unless("failed"));
            case TtsState.Idle or TtsState.Downloading:
                return new SmalltalkVoiceChoice(English, null, wanted, Unless("downloading"), voice);
        }

        // Bereit: in der gewünschten Sprache, falls die Stimme sie spricht – sonst Englisch, aber mit derselben Stimme
        var spoken = MultilingualVoices.Speaks(voice, wanted) ? wanted : English;
        return new SmalltalkVoiceChoice(spoken, voice with { Language = spoken }, wanted, spoken == wanted ? null : "noVoice");
    }

    /// <summary>Englischer Name einer Sprache für das Sprachmodell ("de" → "German").</summary>
    public static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
