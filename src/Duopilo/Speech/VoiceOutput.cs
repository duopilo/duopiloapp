namespace Duopilo.Speech;

/// <summary>
/// Sprachausgabe für die Sprachschleife: Antworten und ATC-Funksprüche in dieselbe Warteschlange wie die Callouts,
/// dazu der Zustand für den Halbduplex-Betrieb (Mikrofon stumm, solange Duopilo spricht).
/// </summary>
public interface IVoiceOutput
{
    /// <summary>Gibt es gerade eine Stimme (Windows oder Kokoro)?</summary>
    bool Available { get; }

    /// <summary>Spricht gerade etwas.</summary>
    bool IsSpeaking { get; }

    /// <summary>Ende der letzten Ansage (für die Nachlaufzeit, in der das Mikrofon noch stumm bleibt).</summary>
    DateTimeOffset LastSpeechEndUtc { get; }

    /// <summary>Ende des letzten ganz gesprochenen ATC-Funkspruchs (null = noch keiner) – wer hat zuletzt gesprochen, ATC oder Duopilo?</summary>
    DateTimeOffset? LastAtcSpeechEndUtc { get; }

    /// <summary>
    /// Der Pilot spricht gerade (Sprache erkannt). Callouts, Antworten und ATC warten dann kurz – nur Warnungen und die
    /// zeitkritischen PM-Callouts (V1, Rotate …) fallen ins Wort. Bleibt der Wert hängen, gilt er nach einigen Sekunden als false.
    /// </summary>
    bool PilotSpeaking { get; set; }

    /// <summary>Reiht eine Ansage ein. false = nicht angenommen (keine Stimme, Sim pausiert, leer).</summary>
    bool Say(SpeechRequest request);

    /// <summary>Einstellungen aus voice-settings.json übernehmen (Stimme von Duopilo, aktive Callouts, Pause).</summary>
    void ConfigureVoice(VoiceOutputOptions options);

    /// <summary>Geschlecht der Windows-Stimme, wenn sie statt Kokoro spricht (null = Kokoro oder unbekannt).</summary>
    VoiceGender? EngineVoiceGender => null;
}

/// <summary>Was die Sprachschleife an der Sprachausgabe einstellt.</summary>
public sealed record VoiceOutputOptions
{
    public static VoiceOutputOptions Default { get; } = new();

    /// <summary>Kokoro-Stimme für Duopilo (Callouts und Antworten). Leer = Stimme aus dem Dashboard.</summary>
    public string? KopilotVoice { get; init; }

    /// <summary>Faktor auf die Lautstärke für Duopilo (0–1).</summary>
    public double KopilotVolume { get; init; } = 1;

    /// <summary>Faktor auf die Lautstärke für ATC-Funksprüche (0–1).</summary>
    public double AtcVolume { get; init; } = 1;

    /// <summary>Sim pausiert → nichts sprechen (auch nicht "Sim paused").</summary>
    public bool SilentWhenPaused { get; init; }

    /// <summary>Monitoring-Callouts überhaupt sprechen.</summary>
    public bool CalloutsEnabled { get; init; } = true;

    /// <summary>Regeln, die gesprochen werden. "*" = alle.</summary>
    public IReadOnlyList<string> ActiveRules { get; init; } = ["*"];

    /// <summary>Regeln, die nie gesprochen werden (zusätzlich zu speech.json).</summary>
    public IReadOnlyList<string> MutedRules { get; init; } = [];

    public bool SpeaksRule(string rule) =>
        CalloutsEnabled
        && !MutedRules.Contains(rule, StringComparer.OrdinalIgnoreCase)
        && (ActiveRules.Contains("*") || ActiveRules.Contains(rule, StringComparer.OrdinalIgnoreCase));

    public VoiceOutputOptions Normalized() => this with
    {
        KopilotVoice = KokoroSpeechEngine.IsEnglishVoice(KopilotVoice?.Trim()) ? KopilotVoice!.Trim() : null,
        KopilotVolume = Math.Clamp(double.IsFinite(KopilotVolume) ? KopilotVolume : 1, 0, 1),
        AtcVolume = Math.Clamp(double.IsFinite(AtcVolume) ? AtcVolume : 1, 0, 1),
        ActiveRules = Clean(ActiveRules ?? ["*"]) is { Count: > 0 } active ? active : ["*"],
        MutedRules = Clean(MutedRules ?? []),
    };

    private static List<string> Clean(IEnumerable<string> rules) =>
        rules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToUpperInvariant()).Distinct().ToList();
}
