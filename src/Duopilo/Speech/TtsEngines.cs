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
/// Lokale Sprachsynthese für Sprachen ohne Kokoro-Stimme (Smalltalk in der Oberflächensprache). Kokoro bleibt die Stimme für
/// alles Englische (Callouts, Funk, englischer Smalltalk); weitere Engines kommen über diese Schnittstelle dazu.
/// </summary>
public interface ITtsEngine
{
    /// <summary>Kennung, z. B. "piper" – Präfix von <see cref="TtsVoice.Key"/>.</summary>
    string Id { get; }

    /// <summary>Alle Stimmen, die die Engine laden kann (auch noch nicht heruntergeladene).</summary>
    IReadOnlyList<TtsVoice> Voices { get; }

    /// <summary>Zustand einer Stimme (Programm und Modell geladen?).</summary>
    TtsState StateOf(TtsVoice voice);

    /// <summary>Fehler der letzten Vorbereitung (null = keiner).</summary>
    string? Error { get; }

    /// <summary>Programm und Stimme bei Bedarf herunterladen. Kehrt sofort zurück, wenn schon alles da ist.</summary>
    Task PrepareAsync(TtsVoice voice, CancellationToken ct);

    /// <summary>Text sprechen (wie er ist, ohne Funk-Aussprache). <paramref name="rate"/> wie im Dashboard (1 = normal).</summary>
    Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct);
}

/// <summary>Alle Engines für freien Text in anderen Sprachen als Englisch (in der Tray-Instanz: Piper).</summary>
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
/// <param name="Voice">Stimme einer anderen Engine (null = die Kokoro-Stimme von Duopilo, für Englisch).</param>
/// <param name="Wanted">Gewünschte Sprache (Oberflächensprache) – weicht von <paramref name="Language"/> ab, wenn es auf Englisch zurückfällt.</param>
/// <param name="Reason">Warum Englisch statt der gewünschten Sprache: "noVoice", "downloading", "failed" (null = alles wie gewünscht).</param>
public sealed record SmalltalkVoiceChoice(string Language, TtsVoice? Voice, string Wanted, string? Reason = null)
{
    public bool FellBack => Reason is not null;
}

/// <summary>
/// Sprache und Stimme für den Smalltalk: die Oberflächensprache (Prompt K), Englisch mit der Kokoro-Stimme von Duopilo, andere
/// Sprachen mit einer lokalen Engine (Piper) – Geschlecht passend zum First Officer. Gibt es keine Stimme für die Sprache oder
/// ist sie noch nicht geladen, Englisch (Hinweis im Dashboard).
/// </summary>
public static class SmalltalkVoices
{
    public const string English = "en";

    public static SmalltalkVoiceChoice Choose(string? language, VoiceGender gender, string seed, IEnumerable<ITtsEngine> engines)
    {
        var wanted = string.IsNullOrWhiteSpace(language) ? English : language.Trim().ToLowerInvariant();
        if (wanted == English)
        {
            return new SmalltalkVoiceChoice(English, null, wanted);
        }

        // Gleiches Geschlecht zuerst; pro Flug (seed) fest, aber zufällig unter mehreren passenden Stimmen
        ITtsEngine? engine = null;
        TtsVoice? voice = null;
        foreach (var e in engines)
        {
            var candidates = e.Voices.Where(v => v.Language == wanted && v.Gender == gender).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            engine = e;
            voice = candidates[Duopilo.Atc.AtcVoiceAssigner.StableHash(seed) % candidates.Count];
            break;
        }

        if (engine is null || voice is null)
        {
            return new SmalltalkVoiceChoice(English, null, wanted, "noVoice");
        }

        return engine.StateOf(voice) switch
        {
            TtsState.Ready => new SmalltalkVoiceChoice(wanted, voice, wanted),
            TtsState.Failed => new SmalltalkVoiceChoice(English, voice, wanted, "failed"),
            _ => new SmalltalkVoiceChoice(English, voice, wanted, "downloading"),
        };
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
