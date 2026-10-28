using System.Text.RegularExpressions;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.Extensions.Options;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Kopilot.Speech;

/// <summary>Fertig synthetisierte Ansage: Mono-Samples (−1…1).</summary>
public sealed record SpeechClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(Samples.Length / (double)SampleRate) : TimeSpan.Zero;
}

/// <summary>
/// Kokoro-82M über KokoroSharp (ONNX Runtime, CPU). Nutzt bewusst nur Tokenizer und Modell, nicht die
/// Abspiel-Warteschlange von KokoroSharp: Die Synthese läuft synchron im eigenen Hintergrund-Task, Fehler bleiben
/// abfangbar und die Wiedergabe steuert Kopilot selbst (Warnings unterbrechen, veraltete Callouts verfallen).
/// </summary>
public sealed partial class KokoroSpeechEngine : IDisposable
{
    /// <summary>Gleiche Quelle, die KokoroSharp selbst nutzt (Kokoro v1.0, float32).</summary>
    public const string ModelUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";

    public const int SampleRate = 24_000;

    // Kleinere Dateien sind sicher kein vollständiges Modell (echtes: ~310 MB)
    private const long MinModelBytes = 50L * 1024 * 1024;

    private readonly SpeechOptions _options;
    private readonly string _voicesDirectory;
    private readonly ILogger<KokoroSpeechEngine> _log;
    private readonly object _voiceLock = new();
    private readonly Dictionary<string, KokoroVoice> _voices = new(StringComparer.OrdinalIgnoreCase);
    private OnnxSessionOptions? _sessionOptions;
    private KokoroModel? _model;

    public KokoroSpeechEngine(IOptions<KopilotOptions> options, ILogger<KokoroSpeechEngine> log)
    {
        var o = options.Value;
        _options = o.Speech;
        _log = log;
        ModelPath = string.IsNullOrWhiteSpace(_options.KokoroModelPath)
            ? Path.Combine(o.ResolveDataDirectory(), "models", "kokoro.onnx")
            : Path.GetFullPath(_options.KokoroModelPath, AppContext.BaseDirectory);
        _voicesDirectory = Path.Combine(AppContext.BaseDirectory, "voices");
    }

    public string ModelPath { get; }

    public bool IsLoaded => _model is not null;

    private IReadOnlyList<string>? _voiceList;

    /// <summary>Englische Stimmen neben der Kopilot.exe (einmal gelesen).</summary>
    public IReadOnlyList<string> Voices => _voiceList ??= ListVoices(_voicesDirectory);

    /// <summary>Englische Stimmen im Ordner: a = amerikanisch, b = britisch; f/m = weiblich/männlich. Weibliche zuerst wie bei Kokoro.</summary>
    public static IReadOnlyList<string> ListVoices(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.npy", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsEnglishVoice)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Modell liegt schon auf der Platte (kein Download nötig).</summary>
    public bool ModelPresent => File.Exists(ModelPath);

    /// <summary>Gültiger Name einer englischen Kokoro-Stimme (schützt auch vor Pfadangaben aus dem Dashboard).</summary>
    public static bool IsEnglishVoice(string? name) => name is not null && VoiceName().IsMatch(name);

    public async Task LoadAsync(Action<double>? downloadProgress, CancellationToken ct)
    {
        if (_model is not null)
        {
            return;
        }

        if (!File.Exists(ModelPath))
        {
            _log.LogInformation("Sprachmodell fehlt – lade Kokoro (~310 MB) nach {Path} …", ModelPath);
            await DownloadAsync(downloadProgress, ct);
            _log.LogInformation("Sprachmodell geladen.");
        }

        var threads = Math.Clamp(_options.KokoroThreads, 1, 16);
        _sessionOptions = new OnnxSessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            EnableMemoryPattern = true,
        };
        var sessionOptions = _sessionOptions;
        _model = await Task.Run(() => new KokoroModel(ModelPath, sessionOptions), ct);
    }

    public SpeechClip Synthesize(string text, string voice, double speed)
    {
        var model = _model ?? throw new InvalidOperationException("Das Sprachmodell ist noch nicht geladen.");
        var v = Voice(voice);
        var tokens = Tokenizer.Tokenize(text.Trim(), v.GetLangCode(), true);
        if (tokens.Length == 0)
        {
            return new SpeechClip([], SampleRate);
        }

        if (tokens.Length > KokoroModel.maxTokens)
        {
            tokens = tokens[..KokoroModel.maxTokens];
        }

        var samples = model.Infer(tokens, v.Features, (float)Math.Clamp(speed, 0.5, 2.0));
        return new SpeechClip(TrimSilence(samples), SampleRate);
    }

    /// <summary>Stille am Anfang und Ende abschneiden – sonst kommt jeder Callout spürbar verzögert.</summary>
    public static float[] TrimSilence(float[] samples, float threshold = 0.0001f)
    {
        var start = 0;
        while (start < samples.Length && Math.Abs(samples[start]) <= threshold)
        {
            start++;
        }

        var end = samples.Length - 1;
        while (end > start && Math.Abs(samples[end]) <= threshold)
        {
            end--;
        }

        if (start >= samples.Length)
        {
            return [];
        }

        return samples[start..(end + 1)];
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _sessionOptions?.Dispose();
        _sessionOptions = null;
    }

    private KokoroVoice Voice(string name)
    {
        if (!IsEnglishVoice(name))
        {
            throw new ArgumentException($"Unbekannte Stimme \"{name}\".", nameof(name));
        }

        lock (_voiceLock)
        {
            if (_voices.TryGetValue(name, out var voice))
            {
                return voice;
            }

            var path = Path.Combine(_voicesDirectory, name + ".npy");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Stimme \"{name}\" nicht gefunden (erwartet unter {path}).", path);
            }

            voice = KokoroVoice.FromPath(path);
            _voices[name] = voice;
            return voice;
        }
    }

    private async Task DownloadAsync(Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var temp = ModelPath + ".download";

        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kopilot");
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReported = -1.0;
                int read;
                while ((read = await ReadWithStallTimeoutAsync(source, buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total is > 0)
                    {
                        var p = Math.Min(1.0, done / (double)total.Value);
                        if (p - lastReported >= 0.01)
                        {
                            lastReported = p;
                            progress?.Invoke(p);
                        }
                    }
                }
            }

            var length = new FileInfo(temp).Length;
            if ((total is > 0 && length != total) || length < MinModelBytes)
            {
                File.Delete(temp);
                throw new IOException($"Download des Sprachmodells unvollständig ({length / 1048576.0:0} MB).");
            }
        }

        File.Move(temp, ModelPath, overwrite: true);
    }

    // Hängt die Verbindung, nach einer Minute ohne Daten abbrechen statt ewig zu warten.
    private static async Task<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            return await source.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Download des Sprachmodells hängt seit einer Minute.");
        }
    }

    [GeneratedRegex("^[ab][fm]_[a-z0-9]+$")]
    private static partial Regex VoiceName();
}
