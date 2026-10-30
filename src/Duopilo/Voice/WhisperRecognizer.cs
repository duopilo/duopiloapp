using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Duopilo.Voice;

/// <summary>Ergebnis der Spracherkennung.</summary>
public sealed record Transcript(string Text, string? Language, int ElapsedMs)
{
    /// <summary>Wie sicher Whisper ist (0–1, Mittel der Token-Wahrscheinlichkeiten), null = unbekannt.</summary>
    public double? Confidence { get; init; }
}

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
    /// Vorne die Ansprachewörter: „Kopilot“ (fest) und „Duopilo“ (Produktname, damit Whisper ihn so schreibt).
    /// </summary>
    public const string DefaultPrompt =
        "Kopilot, Duopilo. Gear up, gear down, flaps one, flaps full, speedbrake retract, arm spoilers, autobrake medium, landing lights on, " +
        "seat belts on, APU start, QNH 1013, standard, altitude 5000, flight level 120, speed 250, heading 090, squawk 7000, TCAS TA/RA, " +
        "speed check, say again. Frankfurt Tower, Langen Radar, Shanwick, Lufthansa. " +
        "Fahrwerk einfahren, Klappen eins, Anschnallzeichen an.";

    private readonly WhisperFactory _factory;
    private readonly RecognitionSettings _settings;
    private readonly string _language;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperProcessor _processor;
    private string? _currentLanguage;
    private string? _name;

    private WhisperRecognizer(WhisperFactory factory, WhisperProcessor processor, RecognitionSettings settings, string? name)
    {
        _factory = factory;
        _processor = processor;
        _settings = settings;
        _language = settings.Language;
        _currentLanguage = _language == "auto" ? "de" : _language;
        _name = name;
    }

    /// <summary>Name des First Officers im Wortschatz-Hinweis (null = keiner).</summary>
    public string? Name => _name;

    /// <summary>Welche Laufzeit whisper.cpp geladen hat (Cpu, Cuda, Vulkan …).</summary>
    public string? Runtime => RuntimeOptions.LoadedLibrary?.ToString();

    public static string ModelFileName(string model) => $"ggml-{model}.bin";

    public static string ModelUrl(string model) => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin";

    /// <summary>Kleinste plausible Größe der Modelldatei (kleiner = abgebrochener Download).</summary>
    public static long MinModelBytes(string model) => model.StartsWith("tiny", StringComparison.Ordinal) ? 20_000_000 : 50_000_000;

    /// <summary>Modell laden. Die Laufzeit (CPU/GPU) muss vor dem ersten Modell feststehen.</summary>
    /// <param name="name">Name des First Officers für den Wortschatz-Hinweis (null = keiner).</param>
    public static WhisperRecognizer Load(string modelPath, RecognitionSettings settings, string? name = null)
    {
        var gpu = settings.Device == "gpu";
        RuntimeOptions.RuntimeLibraryOrder = gpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu });
        try
        {
            name = CleanName(name);
            return new WhisperRecognizer(factory, Build(factory, settings, name), settings, name);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wortschatz-Hinweis: der eingestellte (leer = <see cref="DefaultPrompt"/>), vorn der Name des First Officers, damit Whisper
    /// ihn richtig schreibt ("Erik" statt "Eric"). Steht der Name schon drin, bleibt der Hinweis, wie er ist.
    /// </summary>
    public static string PromptFor(string? configured, string? name)
    {
        var prompt = string.IsNullOrWhiteSpace(configured) ? DefaultPrompt : configured.Trim();
        var n = CleanName(name);
        if (n is null || Regex.IsMatch(prompt, @"(?<!\p{L})" + Regex.Escape(n) + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return prompt;
        }

        return n + ", " + prompt;
    }

    /// <summary>
    /// Name des First Officers für den Wortschatz-Hinweis setzen (neuer Flug, neuer Name). Whisper.net kann den Hinweis eines
    /// Prozessors nicht ändern – dafür wird der Prozessor neu gebaut (das Modell bleibt geladen). Gleicher Name: nichts zu tun.
    /// </summary>
    public void SetName(string? name)
    {
        name = CleanName(name);
        if (string.Equals(name, _name, StringComparison.Ordinal))
        {
            return;
        }

        _lock.Wait();
        try
        {
            var next = Build(_factory, _settings, name);
            var old = _processor;
            _processor = next;
            _name = name;
            _currentLanguage = _language == "auto" ? "de" : _language;
            old.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static WhisperProcessor Build(WhisperFactory factory, RecognitionSettings settings, string? name)
    {
        var builder = factory.CreateBuilder()
            .WithThreads(settings.Threads)
            .WithNoContext()
            .WithSingleSegment()
            .WithTemperature(0f)

            // Token-Wahrscheinlichkeiten für die Konfidenz – ohne bleibt SegmentData.Probability 0 (bis 0.10.0-beta.1:
            // jeder Befehl landete deshalb im Readback mit „Confirm“)
            .WithProbabilities()
            .WithPrompt(PromptFor(settings.Prompt, name))
            .WithLanguage(settings.Language == "auto" ? "de" : settings.Language);
        if (settings.AudioContext > 0)
        {
            builder = builder.WithAudioContextSize(settings.AudioContext);
        }

        return builder.Build();
    }

    private static string? CleanName(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) ? null : n;
    }

    /// <summary>
    /// Konfidenz aus den Segmenten: mittlere Token-Wahrscheinlichkeit, nach Textlänge gewichtet (ein Satz ist meist ein Segment).
    /// Segmente ohne berechnete Wahrscheinlichkeit (0 bzw. kein Wert – der Prozessor lief ohne WithProbabilities) zählen nicht;
    /// bleibt keines übrig, ist die Konfidenz unbekannt (null) – unbekannt heißt nicht unsicher.
    /// </summary>
    public static double? Confidence(IEnumerable<(string? Text, float Probability, float MaxProbability)> segments)
    {
        double weighted = 0;
        var weight = 0;
        foreach (var (text, probability, max) in segments)
        {
            if (!float.IsFinite(probability) || !float.IsFinite(max) || (probability <= 0 && max <= 0))
            {
                continue;
            }

            var length = Math.Max(1, text?.Trim().Length ?? 0);
            weighted += probability * length;
            weight += length;
        }

        return weight > 0 ? Math.Clamp(weighted / weight, 0, 1) : null;
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
            var segments = new List<(string? Text, float Probability, float MaxProbability)>();
            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                sb.Append(segment.Text);
                segments.Add((segment.Text, segment.Probability, segment.MaxProbability));
            }

            return new Transcript(sb.ToString().Trim(), _currentLanguage, (int)watch.ElapsedMilliseconds)
            {
                Confidence = WhisperRecognizer.Confidence(segments),
            };
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

        // Der Wortschatz-Hinweis selbst kommt bei Stille manchmal zurück (auch mit dem Namen des First Officers davor und der
        // bis 0.9.x, nur mit „Kopilot“) – diese Folge sagt kein Pilot
        if (lower.Contains("gear up, gear down, flaps one", StringComparison.Ordinal)
            || lower.StartsWith("kopilot. gear up, gear down", StringComparison.Ordinal))
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
