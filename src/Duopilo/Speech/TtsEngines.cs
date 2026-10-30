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

    /// <summary>Text sprechen (wie er ist, ohne Funk-Aussprache). <paramref name="rate"/> wie im Dashboard (1 = normal).</summary>
    Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct);
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
/// <param name="Voice">Stimme einer anderen Engine (null = die Kokoro-Stimme von Duopilo, für Englisch).</param>
/// <param name="Wanted">Gewünschte Sprache (Oberflächensprache) – weicht von <paramref name="Language"/> ab, wenn es auf Englisch zurückfällt.</param>
/// <param name="Reason">Warum Englisch statt der gewünschten Sprache: "noVoice", "downloading", "failed" (null = alles wie gewünscht).</param>
/// <param name="Prepare">
/// Bevorzugte Stimme, die noch geladen werden muss, während eine andere (Rückfall, z. B. Piper) schon spricht. null = nichts zu laden
/// außer ggf. <paramref name="Voice"/> selbst (bei "downloading").
/// </param>
public sealed record SmalltalkVoiceChoice(string Language, TtsVoice? Voice, string Wanted, string? Reason = null, TtsVoice? Prepare = null)
{
    public bool FellBack => Reason is not null;
}

/// <summary>
/// Sprache und Stimme für den Smalltalk: die Oberflächensprache (Prompt K), Englisch mit der Kokoro-Stimme von Duopilo, andere
/// Sprachen mit einer lokalen Engine – Geschlecht passend zum First Officer. Engines in Reihenfolge der Vorliebe (Supertonic, dann
/// Piper): Die erste, die nicht gescheitert ist, ist die gewünschte. Ist sie noch nicht geladen, spricht eine andere, die schon
/// bereit ist (und die gewünschte lädt im Hintergrund); sonst Englisch (Hinweis im Dashboard), ebenso ohne Stimme für die Sprache.
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

        // Je Engine eine Stimme gleichen Geschlechts; pro Flug (seed) fest, aber zufällig unter mehreren passenden
        var options = new List<(TtsVoice Voice, TtsState State)>();
        foreach (var e in engines)
        {
            var candidates = e.Voices.Where(v => v.Language == wanted && v.Gender == gender).ToList();
            if (candidates.Count > 0)
            {
                var voice = candidates[Duopilo.Atc.AtcVoiceAssigner.StableHash(seed) % candidates.Count];
                options.Add((voice, e.StateOf(voice)));
            }
        }

        if (options.Count == 0)
        {
            return new SmalltalkVoiceChoice(English, null, wanted, "noVoice");
        }

        // Gewünscht: die erste Engine, die nicht gescheitert ist
        var preferred = options.FindIndex(o => o.State != TtsState.Failed);
        if (preferred < 0)
        {
            return new SmalltalkVoiceChoice(English, options[0].Voice, wanted, "failed");
        }

        var (wish, state) = options[preferred];
        if (state == TtsState.Ready)
        {
            return new SmalltalkVoiceChoice(wanted, wish, wanted);
        }

        // Noch nicht geladen: eine andere Stimme, die schon bereit ist, spricht solange
        var ready = options.FindIndex(preferred + 1, o => o.State == TtsState.Ready);
        return ready >= 0
            ? new SmalltalkVoiceChoice(wanted, options[ready].Voice, wanted, Prepare: wish)
            : new SmalltalkVoiceChoice(English, wish, wanted, "downloading");
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
