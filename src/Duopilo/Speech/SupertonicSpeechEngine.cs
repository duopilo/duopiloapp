using System.Collections.Concurrent;
using Duopilo.Localization;
using Duopilo.Voice;
using Microsoft.Extensions.Options;

namespace Duopilo.Speech;

/// <summary>Eine Datei des Supertonic-Modells: Pfad im Hugging-Face-Repo, Größe und SHA-256 der gepinnten Revision.</summary>
public sealed record SupertonicFile(string Path, long Bytes, string Sha256);

/// <summary>
/// Supertonic 3 (Supertone, Modell OpenRAIL-M) als mehrsprachige Stimme des First Officers: Callouts und Antworten auf Englisch,
/// Smalltalk in der Oberflächensprache – derselbe Sprecher (<see cref="MultilingualVoices"/>).
/// Läuft im Prozess über die ONNX Runtime (CPU), die Duopilo für Kokoro und Silero VAD schon mitbringt; liest Zeichen statt
/// Phonemen, braucht also kein espeak. Gemessen auf 2 vCPU: ~1,3 s für einen kurzen Satz, ~0,33 CPU-s je Sekunde Sprache, Whisper
/// versteht es so gut wie Piper Thorsten (docs/research/messung-supertonic.md). Modell (~400 MB) und Stimmen lädt Duopilo erst, wenn
/// der First Officer eine Supertonic-Stimme hat, nach %LOCALAPPDATA%\Duopilo\models\supertonic-3 – aus einer festen Revision, jede
/// Datei mit SHA-256 geprüft. Die festen Callouts rechnet Duopilo danach vorab. Funk bleibt Kokoro.
/// </summary>
public sealed class SupertonicSpeechEngine : ITtsEngine, IDisposable
{
    public const string EngineId = "supertonic";

    /// <summary>Archiv von Supertonic 3 auf Hugging Face (das Original-Repo ist archiviert, es kommt nichts mehr nach).</summary>
    public const string Repository = "supertone-oss-archive/supertonic-3";

    /// <summary>Gepinnte Revision – die Dateien darunter ändern sich nie, die Prüfsummen bleiben gültig.</summary>
    public const string Revision = "aafc6e32416a594460b32413efc49d7fe4ce6d46";

    public const string BaseUrl = "https://huggingface.co/" + Repository + "/resolve/" + Revision + "/";

    /// <summary>Lizenz der Gewichte (OpenRAIL-M, mit Nutzungsbeschränkungen) – wird neben das Modell gelegt.</summary>
    public const string LicenseUrl = "https://huggingface.co/" + Repository + "/blob/" + Revision + "/LICENSE";

    /// <summary>Modell, Konfiguration und Lizenz (Größen und Prüfsummen der Revision <see cref="Revision"/>).</summary>
    public static readonly IReadOnlyList<SupertonicFile> ModelFiles =
    [
        new("onnx/duration_predictor.onnx", 3_700_147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        new("onnx/text_encoder.onnx", 36_416_150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        new("onnx/vector_estimator.onnx", 256_534_781, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
        new("onnx/vocoder.onnx", 101_424_195, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
        new("onnx/tts.json", 8_253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        new("onnx/unicode_indexer.json", 277_676, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
        new("LICENSE", 15_007, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
    ];

    /// <summary>Stimmen (voice_styles/&lt;Id&gt;.json). Nach Hörprobe ausgewählt: M1, M2 männlich, F1 weiblich.</summary>
    public static readonly IReadOnlyDictionary<string, SupertonicFile> StyleFiles = new Dictionary<string, SupertonicFile>(StringComparer.OrdinalIgnoreCase)
    {
        ["M1"] = new("voice_styles/M1.json", 291_748, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
        ["M2"] = new("voice_styles/M2.json", 292_055, "b76cbf62bac707c710cf0ae5aba5e31eea1a6339a9734bfae33ab98499534a50"),
        ["F1"] = new("voice_styles/F1.json", 292_046, "bbdec6ee00231c2c742ad05483df5334cab3b52fda3ba38e6a07059c4563dbc2"),
    };

    /// <summary>
    /// Stimmen, die Duopilo nutzt. Das Modell spricht alle 31 Sprachen mit jeder Stimme (<see cref="SupertonicText.Languages"/>,
    /// <see cref="MultilingualVoices.Speaks"/>); die Sprache je Satz kommt über <c>voice with { Language = … }</c>.
    /// </summary>
    public static readonly IReadOnlyList<TtsVoice> Catalog =
    [
        new(EngineId, "M1", "de", VoiceGender.Male),
        new(EngineId, "M2", "de", VoiceGender.Male),
        new(EngineId, "F1", "de", VoiceGender.Female),
    ];

    /// <summary>Nach einem Fehler so lange nicht erneut laden.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly string _root;
    private readonly int _threads;
    private readonly ILogger<SupertonicSpeechEngine> _log;
    private readonly HttpMessageHandler? _downloadHandler;

    // Eine Datei nie zweimal gleichzeitig laden (zwei Stimmen teilen sich das Modell)
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    // Immer nur ein Satz gleichzeitig – das Modell ist nicht threadsicher und soll MSFS nicht ausbremsen
    private readonly SemaphoreSlim _synthLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _preparing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Error)> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SupertonicStyle> _styles = new(StringComparer.OrdinalIgnoreCase);
    private SupertonicModel? _model;
    private volatile string? _error;

    // Prüfsumme falsch: bis zum Neustart nicht erneut laden (kein Download alle paar Minuten)
    private volatile bool _checksumRejected;

    /// <param name="downloadHandler">Eigener HTTP-Handler für die Downloads (Tests). null = normales Netz.</param>
    public SupertonicSpeechEngine(IOptions<DuopiloOptions> options, ILogger<SupertonicSpeechEngine> log, HttpMessageHandler? downloadHandler = null)
    {
        var o = options.Value;
        _root = Path.Combine(o.ResolveDataDirectory(), "models", "supertonic-3");
        _threads = Math.Clamp(o.Smalltalk.SupertonicThreads, 1, 16);
        _log = log;
        _downloadHandler = downloadHandler;
    }

    public string Id => EngineId;

    public IReadOnlyList<TtsVoice> Voices => Catalog;

    public string? Error => _error;

    /// <summary>Ordner des Modells (onnx\, voice_styles\, LICENSE).</summary>
    public string Root => _root;

    /// <summary>Download-Adresse einer Datei der gepinnten Revision.</summary>
    public static string Url(SupertonicFile file) => BaseUrl + file.Path;

    /// <summary>Alle Dateien, die eine Stimme braucht: Modell, Konfiguration, Lizenz und ihr Stil.</summary>
    public static IReadOnlyList<SupertonicFile> FilesFor(TtsVoice voice)
    {
        if (!StyleFiles.TryGetValue(voice.Id, out var style))
        {
            throw new ArgumentException(Loc.T("speech.supertonic.unknownVoice", voice.Id), nameof(voice));
        }

        return [.. ModelFiles, style];
    }

    /// <summary>Sprechtempo wie im Dashboard (1 = normal, 1,1 = etwas schneller) → Supertonic speed, im empfohlenen Bereich 0,9–1,5.</summary>
    public static float Speed(double rate) => (float)Math.Clamp(double.IsFinite(rate) && rate > 0 ? rate : 1, 0.9, 1.5);

    public TtsState StateOf(TtsVoice voice)
    {
        if (StyleFiles.ContainsKey(voice.Id) && FilesFor(voice).All(Present))
        {
            return TtsState.Ready;
        }

        if (_preparing.TryGetValue(voice.Id, out var running) && running.IsValueCreated && !running.Value.IsCompleted)
        {
            return TtsState.Downloading;
        }

        if (_checksumRejected)
        {
            return TtsState.Failed;
        }

        return _failed.TryGetValue(voice.Id, out var failed) && DateTimeOffset.UtcNow - failed.At < RetryAfter ? TtsState.Failed : TtsState.Idle;
    }

    public Task PrepareAsync(TtsVoice voice, CancellationToken ct)
    {
        if (StateOf(voice) is TtsState.Ready or TtsState.Failed)
        {
            return Task.CompletedTask;
        }

        var lazy = _preparing.AddOrUpdate(
            voice.Id,
            _ => new Lazy<Task>(() => DownloadAsync(voice, ct)),
            (_, existing) => existing.IsValueCreated && existing.Value.IsCompleted ? new Lazy<Task>(() => DownloadAsync(voice, ct)) : existing);
        return lazy.Value;
    }

    public async Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct)
    {
        if (StateOf(voice) != TtsState.Ready)
        {
            throw new InvalidOperationException(Loc.T("speech.supertonic.notReady", voice.Id));
        }

        var line = PiperSpeechEngine.OneLine(text);
        if (line.Length == 0)
        {
            return new SpeechClip([], 44_100);
        }

        await _synthLock.WaitAsync(ct);
        try
        {
            return await Task.Run(
                () =>
                {
                    var model = _model ??= LoadModel();
                    var style = _styles.GetOrAdd(voice.Id, id => SupertonicStyle.Parse(File.ReadAllText(LocalPath(StyleFiles[id]))));
                    var samples = model.Synthesize(line, voice.Language, style, Speed(rate), ct);
                    return new SpeechClip(KokoroSpeechEngine.TrimSilence(samples), model.SampleRate);
                },
                ct);
        }
        finally
        {
            _synthLock.Release();
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }

    private SupertonicModel LoadModel()
    {
        var model = new SupertonicModel(Path.Combine(_root, "onnx"), _threads);
        _log.LogInformation(Loc.Log("log.speech.supertonicLoaded"), _threads);
        return model;
    }

    private string LocalPath(SupertonicFile file) => Path.Combine(_root, file.Path.Replace('/', Path.DirectorySeparatorChar));

    // Vollständig = genau die erwartete Größe (die Prüfsumme kontrolliert der Download; nicht bei jedem Satz 400 MB hashen)
    private bool Present(SupertonicFile file)
    {
        var info = new FileInfo(LocalPath(file));
        return info.Exists && info.Length == file.Bytes;
    }

    private async Task DownloadAsync(TtsVoice voice, CancellationToken ct)
    {
        // Eigener Task, damit der Aufrufer nicht auf den Download wartet
        await Task.Yield();
        await _downloadLock.WaitAsync(ct);
        try
        {
            var missing = FilesFor(voice).Where(f => !Present(f)).ToList();
            if (missing.Count > 0)
            {
                _log.LogInformation(Loc.Log("log.speech.supertonicDownloading"), voice.Id, missing.Sum(f => f.Bytes) / (1024 * 1024), _root, LicenseUrl);
                foreach (var file in missing)
                {
                    await ModelDownloader.EnsureAsync(LocalPath(file), Url(file), file.Bytes, null, ct, file.Sha256, _downloadHandler);
                }
            }

            _failed.TryRemove(voice.Id, out _);
            _error = null;
            _log.LogInformation(Loc.Log("log.speech.supertonicReady"), voice.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelChecksumException ex)
        {
            // Gelöscht; Smalltalk nimmt Piper oder bleibt Englisch
            _checksumRejected = true;
            _error = ex.Message;
            _failed[voice.Id] = (DateTimeOffset.UtcNow, ex.Message);
            _log.LogError(Loc.Log("log.speech.supertonicChecksum"), ex.File, ex.Actual, ex.Expected);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _failed[voice.Id] = (DateTimeOffset.UtcNow, ex.Message);
            _log.LogWarning(Loc.Log("log.speech.supertonicFailed"), voice.Id, ex.Message);
        }
        finally
        {
            _downloadLock.Release();
        }
    }
}
