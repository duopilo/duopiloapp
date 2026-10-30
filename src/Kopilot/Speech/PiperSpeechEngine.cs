using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Kopilot.Localization;
using Kopilot.Voice;
using Microsoft.Extensions.Options;

namespace Kopilot.Speech;

/// <summary>
/// Piper (MIT, rhasspy/piper) als zweite lokale Sprachsynthese – für Smalltalk in Sprachen ohne Kokoro-Stimme (Deutsch).
/// Läuft als eigener Prozess (piper.exe, ONNX Runtime auf der CPU): ein Satz → WAV-Datei → Windows-Player wie Kokoro.
/// Programm (~20 MB) und Stimme (~60 MB) lädt Kopilot erst, wenn Smalltalk in der Sprache gebraucht wird, nach
/// %LOCALAPPDATA%\Kopilot\piper bzw. \models\piper. Callouts und Funk bleiben Englisch und laufen nie über Piper.
/// </summary>
public sealed class PiperSpeechEngine : ITtsEngine
{
    public const string EngineId = "piper";

    /// <summary>Letzte Windows-Version von Piper (rhasspy/piper, Release 2023.11.14-2).</summary>
    public const string ReleaseUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";

    /// <summary>Stimmen von rhasspy/piper-voices (Hugging Face). Lizenz je Stimme in ihrer MODEL_CARD.</summary>
    public const string VoiceBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    private const long MinZipBytes = 5L * 1024 * 1024;
    private const long MinModelBytes = 5L * 1024 * 1024;
    private const long MinConfigBytes = 500;

    /// <summary>Länger rechnet Piper an einem Satz nie – sonst hängt der Prozess.</summary>
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Nach einem Fehler so lange nicht erneut laden.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Stimmen, die Kopilot kennt: je Sprache weiblich und männlich. Neue Sprachen hier eintragen (Kennung wie auf Hugging Face:
    /// &lt;locale&gt;-&lt;name&gt;-&lt;quality&gt;).
    /// </summary>
    public static readonly IReadOnlyList<TtsVoice> Catalog =
    [
        new(EngineId, "de_DE-thorsten-medium", "de", VoiceGender.Male),
        new(EngineId, "de_DE-karlsson-low", "de", VoiceGender.Male),
        new(EngineId, "de_DE-kerstin-low", "de", VoiceGender.Female),
        new(EngineId, "de_DE-ramona-low", "de", VoiceGender.Female),
    ];

    private readonly string _root;
    private readonly string _models;
    private readonly string? _configuredExe;
    private readonly ILogger<PiperSpeechEngine> _log;
    private readonly SemaphoreSlim _synthLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _preparing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Error)> _failed = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _error;

    public PiperSpeechEngine(IOptions<KopilotOptions> options, ILogger<PiperSpeechEngine> log)
    {
        var o = options.Value;
        var data = o.ResolveDataDirectory();
        _root = Path.Combine(data, "piper");
        _models = Path.Combine(data, "models", "piper");
        _configuredExe = string.IsNullOrWhiteSpace(o.Smalltalk.PiperPath) ? null : Path.GetFullPath(o.Smalltalk.PiperPath, AppContext.BaseDirectory);
        _log = log;
    }

    public string Id => EngineId;

    public IReadOnlyList<TtsVoice> Voices => Catalog;

    public string? Error => _error;

    /// <summary>piper.exe (eingestellt oder heruntergeladen), null = noch nicht da.</summary>
    public string? ExePath
    {
        get
        {
            if (_configuredExe is not null)
            {
                return File.Exists(_configuredExe) ? _configuredExe : null;
            }

            foreach (var candidate in new[] { Path.Combine(_root, "piper", "piper.exe"), Path.Combine(_root, "piper.exe") })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public TtsState StateOf(TtsVoice voice)
    {
        if (ExePath is not null && File.Exists(ModelPath(voice)) && File.Exists(ConfigPath(voice)))
        {
            return TtsState.Ready;
        }

        if (_preparing.TryGetValue(voice.Id, out var running) && running.IsValueCreated && !running.Value.IsCompleted)
        {
            return TtsState.Downloading;
        }

        return _failed.TryGetValue(voice.Id, out var failed) && DateTimeOffset.UtcNow - failed.At < RetryAfter ? TtsState.Failed : TtsState.Idle;
    }

    public Task PrepareAsync(TtsVoice voice, CancellationToken ct)
    {
        if (StateOf(voice) is TtsState.Ready or TtsState.Failed)
        {
            return Task.CompletedTask;
        }

        // Einmal pro Stimme gleichzeitig; ein fertiger (auch gescheiterter) Lauf darf später erneut starten
        var lazy = _preparing.AddOrUpdate(
            voice.Id,
            _ => new Lazy<Task>(() => DownloadAsync(voice, ct)),
            (_, existing) => existing.IsValueCreated && existing.Value.IsCompleted ? new Lazy<Task>(() => DownloadAsync(voice, ct)) : existing);
        return lazy.Value;
    }

    public async Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct)
    {
        var exe = ExePath ?? throw new InvalidOperationException(Loc.T("speech.piper.notReady", voice.Id));
        var model = ModelPath(voice);
        if (!File.Exists(model))
        {
            throw new InvalidOperationException(Loc.T("speech.piper.notReady", voice.Id));
        }

        var line = OneLine(text);
        if (line.Length == 0)
        {
            return new SpeechClip([], 22_050);
        }

        await _synthLock.WaitAsync(ct);
        var output = Path.Combine(Path.GetTempPath(), $"kopilot-piper-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(ConfigPath(voice));
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add("--length_scale");
            psi.ArgumentList.Add(LengthScale(rate));

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException(Loc.T("speech.piper.failed", -1, ""));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SynthesisTimeout);
            using var kill = timeout.Token.Register(() => TryKill(process));
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardInput.WriteLineAsync(line);
            process.StandardInput.Close();
            await process.WaitForExitAsync(CancellationToken.None);
            await stdout;
            var errors = await stderr;
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.T("speech.piper.timeout", (int)SynthesisTimeout.TotalSeconds));
            }

            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new InvalidOperationException(Loc.T("speech.piper.failed", process.ExitCode, LastLine(errors)));
            }

            var clip = ReadWav(await File.ReadAllBytesAsync(output, ct));
            return clip with { Samples = KokoroSpeechEngine.TrimSilence(clip.Samples) };
        }
        finally
        {
            _synthLock.Release();
            TryDelete(output);
        }
    }

    /// <summary>16-Bit-PCM-WAV (wie Piper schreibt) → Samples −1…1 (bei Stereo der erste Kanal).</summary>
    public static SpeechClip ReadWav(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            throw new InvalidDataException(Loc.T("speech.piper.badWav"));
        }

        var sampleRate = 22_050;
        var channels = 1;
        var bits = 16;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
            pos += 8;
            if (id == "fmt " && pos + 16 <= wav.Length)
            {
                channels = Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(pos + 2)));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(pos + 14));
            }
            else if (id == "data")
            {
                if (bits != 16)
                {
                    throw new InvalidDataException(Loc.T("speech.piper.badWav"));
                }

                // Unbekannte Länge (Datei während des Schreibens) → bis zum Ende
                var length = size <= 0 || pos + size > wav.Length ? wav.Length - pos : size;
                var frames = length / (2 * channels);
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(pos + (i * 2 * channels))) / 32768f;
                }

                return new SpeechClip(samples, sampleRate);
            }

            if (size < 0)
            {
                break;
            }

            pos += size + (size & 1);
        }

        throw new InvalidDataException(Loc.T("speech.piper.badWav"));
    }

    /// <summary>Sprechtempo wie im Dashboard (1,1 = etwas schneller) → Piper length_scale (kleiner = schneller).</summary>
    public static string LengthScale(double rate) =>
        (1.0 / Math.Clamp(double.IsFinite(rate) && rate > 0 ? rate : 1, 0.5, 2)).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Piper liest zeilenweise – ein Satz, eine Zeile, ohne Steuerzeichen.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Download-Adresse einer Stimme: "de_DE-thorsten-medium" → …/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx.</summary>
    public static string VoiceUrl(string id, string extension)
    {
        var parts = id.Split('-');
        if (parts.Length != 3 || parts[0].Length < 2)
        {
            throw new ArgumentException(Loc.T("speech.piper.unknownVoice", id), nameof(id));
        }

        var language = parts[0].Split('_')[0];
        return $"{VoiceBaseUrl}{language}/{parts[0]}/{parts[1]}/{parts[2]}/{id}{extension}";
    }

    private string ModelPath(TtsVoice voice) => Path.Combine(_models, voice.Id + ".onnx");

    private string ConfigPath(TtsVoice voice) => Path.Combine(_models, voice.Id + ".onnx.json");

    private async Task DownloadAsync(TtsVoice voice, CancellationToken ct)
    {
        // Eigener Task, damit der Aufrufer nicht auf den Download wartet
        await Task.Yield();
        try
        {
            if (ExePath is null)
            {
                if (_configuredExe is not null)
                {
                    throw new FileNotFoundException(Loc.T("speech.piper.exeNotFound", _configuredExe), _configuredExe);
                }

                _log.LogInformation(Loc.Log("log.speech.piperDownloading"), ReleaseUrl, _root);
                var zip = Path.Combine(_root, "piper_windows_amd64.zip");
                await ModelDownloader.EnsureAsync(zip, ReleaseUrl, MinZipBytes, null, ct);
                ZipFile.ExtractToDirectory(zip, _root, overwriteFiles: true);
                TryDelete(zip);
                if (ExePath is null)
                {
                    throw new FileNotFoundException(Loc.T("speech.piper.exeNotFound", _root), _root);
                }
            }

            if (!File.Exists(ModelPath(voice)) || !File.Exists(ConfigPath(voice)))
            {
                _log.LogInformation(Loc.Log("log.speech.piperVoiceDownloading"), voice.Id, _models);
                await ModelDownloader.EnsureAsync(ConfigPath(voice), VoiceUrl(voice.Id, ".onnx.json"), MinConfigBytes, null, ct);
                await ModelDownloader.EnsureAsync(ModelPath(voice), VoiceUrl(voice.Id, ".onnx"), MinModelBytes, null, ct);
            }

            _failed.TryRemove(voice.Id, out _);
            _error = null;
            _log.LogInformation(Loc.Log("log.speech.piperReady"), voice.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _failed[voice.Id] = (DateTimeOffset.UtcNow, ex.Message);
            _log.LogWarning(Loc.Log("log.speech.piperFailed"), voice.Id, ex.Message);
        }
    }

    private static string LastLine(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Schon beendet
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp-Datei bleibt liegen – kein Grund für einen Fehler
        }
    }
}
