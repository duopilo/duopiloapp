using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Kopilot.Voice;

/// <summary>Ergebnis der Spracherkennung.</summary>
public sealed record Transcript(string Text, string? Language, int ElapsedMs);

/// <summary>Spracherkennung – austauschbar für Tests.</summary>
public interface ISpeechRecognizer : IDisposable
{
    Task<Transcript> TranscribeAsync(float[] samples, CancellationToken ct);
}

/// <summary>
/// Whisper über Whisper.net (whisper.cpp). Ein Modell, ein Prozessor, nacheinander benutzt.
/// "auto": Sprache je Satz erkennen, aber nur zwischen Deutsch und Englisch wählen.
/// </summary>
public sealed class WhisperRecognizer : ISpeechRecognizer
{
    /// <summary>Whisper verarbeitet Sätze unter einer Sekunde nicht – kürzere werden mit Stille aufgefüllt.</summary>
    private const int MinSamples = SegmenterOptions.SampleRate * 12 / 10;

    /// <summary>
    /// Eingebauter Wortschatz-Hinweis: Funk- und Cockpit-Begriffe, deutsch und englisch. Ohne "confirm"/"correct"/"affirm" –
    /// sonst schreibt Whisper aus einem Räuspern gern genau das ab, und das würde eine Aktion bestätigen.
    /// </summary>
    public const string DefaultPrompt =
        "Kopilot. Gear up, gear down, flaps one, flaps full, speedbrake retract, arm spoilers, autobrake medium, landing lights on, " +
        "seat belts on, APU start, QNH 1013, standard, altitude 5000, flight level 120, speed 250, heading 090, squawk 7000, TCAS TA/RA, " +
        "speed check, say again. Frankfurt Tower, Langen Radar, Shanwick, Lufthansa. " +
        "Fahrwerk einfahren, Klappen eins, Anschnallzeichen an.";

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly string _language;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _currentLanguage;

    private WhisperRecognizer(WhisperFactory factory, WhisperProcessor processor, string language)
    {
        _factory = factory;
        _processor = processor;
        _language = language;
        _currentLanguage = language == "auto" ? "de" : language;
    }

    /// <summary>Welche Laufzeit whisper.cpp geladen hat (Cpu, Cuda, Vulkan …).</summary>
    public string? Runtime => RuntimeOptions.LoadedLibrary?.ToString();

    public static string ModelFileName(string model) => $"ggml-{model}.bin";

    public static string ModelUrl(string model) => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin";

    /// <summary>Kleinste plausible Größe der Modelldatei (kleiner = abgebrochener Download).</summary>
    public static long MinModelBytes(string model) => model.StartsWith("tiny", StringComparison.Ordinal) ? 20_000_000 : 50_000_000;

    /// <summary>Modell laden. Die Laufzeit (CPU/GPU) muss vor dem ersten Modell feststehen.</summary>
    public static WhisperRecognizer Load(string modelPath, RecognitionSettings settings)
    {
        var gpu = settings.Device == "gpu";
        RuntimeOptions.RuntimeLibraryOrder = gpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu });
        try
        {
            var builder = factory.CreateBuilder()
                .WithThreads(settings.Threads)
                .WithNoContext()
                .WithSingleSegment()
                .WithTemperature(0f)
                .WithPrompt(string.IsNullOrWhiteSpace(settings.Prompt) ? DefaultPrompt : settings.Prompt)
                .WithLanguage(settings.Language == "auto" ? "de" : settings.Language);
            if (settings.AudioContext > 0)
            {
                builder = builder.WithAudioContextSize(settings.AudioContext);
            }

            return new WhisperRecognizer(factory, builder.Build(), settings.Language);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        if (samples.Length < MinSamples)
        {
            Array.Resize(ref samples, MinSamples);
        }

        await _lock.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            if (_language == "auto")
            {
                // Nur Deutsch oder Englisch – Whisper rät bei kurzen Sätzen sonst gern Niederländisch oder Walisisch
                var (detected, _) = await Task.Run(() => _processor.DetectLanguageWithProbability(samples.AsSpan(), "de", "en"), ct);
                var language = detected is "de" or "en" ? detected : "en";
                if (language != _currentLanguage)
                {
                    _processor.ChangeLanguage(language);
                    _currentLanguage = language;
                }
            }

            var sb = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                sb.Append(segment.Text);
            }

            return new Transcript(sb.ToString().Trim(), _currentLanguage, (int)watch.ElapsedMilliseconds);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _lock.Dispose();
    }
}

/// <summary>Bekannte Whisper-Halluzinationen (bei Stille oder Geräuschen) aussortieren.</summary>
public static partial class TranscriptFilter
{
    private static readonly string[] Hallucinations =
    [
        "untertitel", "amara.org", "untertitelung", "copyright", "swr ", "zdf", "thanks for watching", "thank you for watching",
        "please subscribe", "vielen dank für ihre aufmerksamkeit", "bis zum nächsten mal", "subtitles by", "transcribed by",
    ];

    /// <summary>Bereinigte Abschrift oder "" (nichts Brauchbares).</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var t = Bracketed().Replace(text, " ");
        t = MultiSpace().Replace(t, " ").Trim();
        if (t.Length == 0 || !t.Any(char.IsLetterOrDigit))
        {
            return "";
        }

        var lower = t.ToLowerInvariant();
        if (Hallucinations.Any(h => lower.Contains(h, StringComparison.Ordinal)))
        {
            return "";
        }

        // Der Wortschatz-Hinweis selbst kommt bei Stille manchmal zurück
        if (lower.StartsWith("kopilot. gear up, gear down", StringComparison.Ordinal))
        {
            return "";
        }

        return t;
    }

    // [BLANK_AUDIO], (Musik), *Applaus*, ♪ …
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
