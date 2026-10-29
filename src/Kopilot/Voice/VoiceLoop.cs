using System.Threading.Channels;
using Kopilot.Input;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Speech;
using Microsoft.Extensions.Options;

namespace Kopilot.Voice;

/// <summary>Zustand der Sprachschleife für /voice und das Log.</summary>
public sealed record VoiceStatus
{
    /// <summary>off, loading, listening, muted, error.</summary>
    public string State { get; init; } = "off";

    public string? Error { get; init; }

    public string? SettingsFile { get; init; }

    public string? Microphone { get; init; }

    public string? Model { get; init; }

    public string? Runtime { get; init; }

    public double? DownloadProgress { get; init; }

    public string? Hotkey { get; init; }

    public bool AtcModule { get; init; }

    public string? LastTranscript { get; init; }

    public string? LastRoute { get; init; }

    public string? LastIntent { get; init; }

    public string? LastReply { get; init; }

    public string? LastInputResult { get; init; }

    public int? LastTranscriptionMs { get; init; }

    public DateTimeOffset? LastHeardUtc { get; init; }

    public string? PendingReadback { get; init; }
}

/// <summary>
/// Lokale Sprachschleife: Mikrofon dauerhaft offen, Silero VAD schneidet Sätze aus, Whisper schreibt sie ab,
/// der Router schickt Funksprüche ans ATC-Modul und alles andere an die Kopilot-Befehle; Kopilot antwortet über die
/// gemeinsame Sprachausgabe. Halbduplex: Solange Kopilot spricht, ist das Mikrofon stumm.
/// Kein Sprachmodell, kein Push-to-Talk, alles lokal. Läuft nur in der Tray-Instanz (HTTP-Modus).
/// </summary>
public sealed class VoiceLoop : BackgroundService, IAtcRadio
{
    private readonly KopilotOptions _options;
    private readonly IVoiceOutput _output;
    private readonly MonitoringService _monitoring;
    private readonly SimConnectClient _sim;
    private readonly IAtcVoiceModule _atc;
    private readonly ILogger<VoiceLoop> _log;
    private readonly VoiceSettingsStore _store;
    private readonly VoiceDialog _dialog;
    private readonly Channel<VoiceEvent> _events = Channel.CreateBounded<VoiceEvent>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly Channel<float[]> _audio = Channel.CreateBounded<float[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private VoiceSettings _settings = new VoiceSettings().Normalized();
    private VoiceRouter _router = new();
    private volatile VoiceStatus _status = new();

    public VoiceLoop(
        IOptions<KopilotOptions> options,
        IVoiceOutput output,
        MonitoringService monitoring,
        SimConnectClient sim,
        InputService input,
        IAtcVoiceModule atc,
        ILogger<VoiceLoop> log)
    {
        _options = options.Value;
        _output = output;
        _monitoring = monitoring;
        _sim = sim;
        _atc = atc;
        _log = log;
        _store = new VoiceSettingsStore(_options.ResolveDataDirectory());
        _dialog = new VoiceDialog(new InputActionGateway(input), new SimVoiceFlightState(sim, monitoring));
    }

    public VoiceStatus Status => _status with
    {
        PendingReadback = _dialog.Pending?.Readback,
        State = _status.State is "listening" or "muted" ? (_dialog.Muted ? "muted" : "listening") : _status.State,
    };

    /// <summary>Funkspruch des ATC-Moduls (unaufgefordert) mit der Stimme der Station.</summary>
    public bool Transmit(AtcTransmission transmission)
    {
        if (string.IsNullOrWhiteSpace(transmission.Text))
        {
            return false;
        }

        var voices = _settings.Voices;
        var accepted = _output.Say(new SpeechRequest(transmission.Text, SpeechPriority.Atc)
        {
            Spoken = transmission.Spoken,
            Voice = KokoroSpeechEngine.IsEnglishVoice(transmission.Voice) ? transmission.Voice : voices.AtcVoiceFor(transmission.Station),
            Volume = voices.AtcVolume,
            Source = "atc:" + (transmission.Station ?? "?"),
        });
        _log.LogInformation("ATC {Station}: {Text}{Dropped}", transmission.Station ?? "?", transmission.Text, accepted ? "" : " (nicht gesprochen)");
        return accepted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (settings, error) = _store.Load();
        _settings = settings;
        if (error is not null)
        {
            _log.LogWarning("Sprachschleife: {Error} – es gelten die Standardwerte.", error);
        }

        ApplyOutputSettings(settings);
        _status = new VoiceStatus { SettingsFile = _store.FilePath, AtcModule = _atc.IsAvailable, Error = error };

        if (!settings.Enabled)
        {
            _log.LogInformation("Sprachschleife aus ({File}: \"enabled\": false).", _store.FilePath);
            return;
        }

        // Auf einem eigenen Thread weiter, damit der Start der anderen Dienste nicht wartet
        await Task.Yield();

        _router = new VoiceRouter(settings.Commands.WakeWords, settings.Atc.Stations.Concat(_atc.KnownStations));
        _dialog.ConfirmTimeout = TimeSpan.FromSeconds(settings.Commands.ConfirmTimeoutSec);
        _dialog.SayAgainMaxWords = settings.Commands.SayAgainMaxWords;
        _dialog.RequireWakeWord = settings.Commands.RequireWakeWord;
        _atc.Attach(this);

        IVoiceActivityDetector? vad = null;
        WhisperRecognizer? recognizer = null;
        GlobalHotkey? hotkey = null;
        try
        {
            var (loadedVad, loadedRecognizer) = await LoadModelsAsync(settings, stoppingToken);
            vad = loadedVad;
            recognizer = loadedRecognizer;
            hotkey = RegisterHotkey(settings.Hotkeys.Mute);

            _status = _status with { State = "listening", Runtime = loadedRecognizer.Runtime };
            _log.LogInformation(
                "Sprachschleife bereit: Whisper {Model} ({Runtime}), Sprache {Language}, VAD-Schwelle {Threshold}, ATC-Modul {Atc}.",
                settings.Recognition.Model,
                loadedRecognizer.Runtime ?? "?",
                settings.Recognition.Language,
                settings.Microphone.Threshold,
                _atc.IsAvailable ? "aktiv" : "nicht installiert");

            var audio = AudioLoopAsync(loadedVad, settings, stoppingToken);
            var recognition = RecognitionLoopAsync(loadedRecognizer, stoppingToken);
            var timer = TimerLoopAsync(stoppingToken);
            await CaptureLoopAsync(settings.Microphone.Device, stoppingToken);
            await Task.WhenAll(audio, recognition, timer);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }
        catch (Exception ex)
        {
            _status = _status with { State = "error", Error = ex.Message };
            _log.LogError(ex, "Sprachschleife gestoppt: {Message}", ex.Message);
        }
        finally
        {
            hotkey?.Dispose();
            recognizer?.Dispose();
            vad?.Dispose();
            _output.PilotSpeaking = false;
        }
    }

    // ------------------------------------------------------------------ Einstellungen

    private void ApplyOutputSettings(VoiceSettings s)
    {
        _output.ConfigureVoice(new VoiceOutputOptions
        {
            KopilotVoice = s.Voices.Kopilot.Length > 0 ? s.Voices.Kopilot : null,
            KopilotVolume = s.Voices.KopilotVolume,
            SilentWhenPaused = s.Callouts.SilentWhenPaused,
            CalloutsEnabled = s.Callouts.Enabled,
            ActiveRules = s.Callouts.Active,
            MutedRules = s.Callouts.Muted,
        });

        var taxi = s.Callouts.TaxiSpeed;
        _monitoring.ConfigureTaxiSpeed(taxi.Enabled, taxi.ThresholdKt, taxi.CooldownSec);
    }

    private async Task<(IVoiceActivityDetector Vad, WhisperRecognizer Recognizer)> LoadModelsAsync(VoiceSettings settings, CancellationToken ct)
    {
        var models = Path.Combine(_options.ResolveDataDirectory(), "models");
        var r = settings.Recognition;
        var whisperPath = string.IsNullOrWhiteSpace(r.ModelPath)
            ? Path.Combine(models, WhisperRecognizer.ModelFileName(r.Model))
            : Path.GetFullPath(r.ModelPath, AppContext.BaseDirectory);
        var vadPath = Path.Combine(models, SileroVad.ModelFileName);
        _status = _status with { State = "loading", Model = Path.GetFileName(whisperPath) };

        await ModelDownloader.EnsureAsync(vadPath, SileroVad.ModelUrl, SileroVad.MinModelBytes, null, ct);
        if (string.IsNullOrWhiteSpace(r.ModelPath))
        {
            if (!File.Exists(whisperPath))
            {
                _log.LogInformation("Whisper-Modell {Model} fehlt – lade nach {Path} …", r.Model, whisperPath);
            }

            await ModelDownloader.EnsureAsync(
                whisperPath,
                WhisperRecognizer.ModelUrl(r.Model),
                WhisperRecognizer.MinModelBytes(r.Model),
                p => _status = _status with { DownloadProgress = Math.Round(p, 2) },
                ct);
        }
        else if (!File.Exists(whisperPath))
        {
            throw new FileNotFoundException($"Whisper-Modell nicht gefunden: {whisperPath}", whisperPath);
        }

        _status = _status with { DownloadProgress = null };
        var vad = new SileroVad(vadPath);
        try
        {
            var recognizer = await Task.Run(() => WhisperRecognizer.Load(whisperPath, r), ct);
            return (vad, recognizer);
        }
        catch
        {
            vad.Dispose();
            throw;
        }
    }

    private GlobalHotkey? RegisterHotkey(string? text)
    {
        if (!HotkeyBinding.TryParse(text, out var binding, out var error))
        {
            if (error is not null)
            {
                _log.LogWarning("{Error}", error);
            }

            return null;
        }

        var (hotkey, problem) = GlobalHotkey.Register(binding!, () => _events.Writer.TryWrite(new VoiceEvent.ToggleMute()));
        if (problem is not null)
        {
            _log.LogWarning("Sprachschleife: {Problem}", problem);
            return null;
        }

        _status = _status with { Hotkey = binding!.Text };
        return hotkey;
    }

    // ------------------------------------------------------------------ Mikrofon

    /// <summary>Hält das Mikrofon offen; fällt das Gerät weg, nach 5 Sekunden neu verbinden.</summary>
    private async Task CaptureLoopAsync(string device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            MicrophoneCapture? capture = null;
            try
            {
                capture = MicrophoneCapture.Start(device, samples => _audio.Writer.TryWrite(samples), e => stopped.TrySetResult(e), out var found);
                _status = _status with { Microphone = capture.DeviceName };
                if (!found)
                {
                    _log.LogWarning(
                        "Mikrofon \"{Device}\" nicht gefunden – nehme das Standardgerät. Vorhanden: {Devices}",
                        device,
                        string.Join(", ", MicrophoneCapture.Devices()));
                }
                else
                {
                    _log.LogInformation("Mikrofon: {Device}", capture.DeviceName);
                }

                using (ct.Register(() => stopped.TrySetResult(null)))
                {
                    var reason = await stopped.Task;
                    if (reason is not null)
                    {
                        _log.LogWarning("Mikrofon getrennt: {Message}", reason.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status = _status with { Error = $"Mikrofon: {ex.Message}" };
                _log.LogWarning("Mikrofon nicht verfügbar: {Message}", ex.Message);
            }
            finally
            {
                capture?.Dispose();
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>Audio in 512er-Blöcke, Halbduplex, VAD, Satzgrenzen.</summary>
    private async Task AudioLoopAsync(IVoiceActivityDetector vad, VoiceSettings settings, CancellationToken ct)
    {
        var segmenter = new UtteranceSegmenter(SegmenterOptions.From(settings.Microphone));
        var tail = TimeSpan.FromMilliseconds(settings.Microphone.EchoTailMs);
        var frame = new float[SegmenterOptions.FrameSamples];
        var filled = 0;
        var wasOpen = true;

        await foreach (var chunk in _audio.Reader.ReadAllAsync(ct))
        {
            var offset = 0;
            while (offset < chunk.Length)
            {
                var n = Math.Min(frame.Length - filled, chunk.Length - offset);
                Array.Copy(chunk, offset, frame, filled, n);
                filled += n;
                offset += n;
                if (filled < frame.Length)
                {
                    break;
                }

                filled = 0;
                var open = HalfDuplex.MicOpen(_output.IsSpeaking, _output.LastSpeechEndUtc, DateTimeOffset.UtcNow, tail);
                if (!open)
                {
                    if (wasOpen)
                    {
                        // Kopilot spricht: angefangenen Satz verwerfen (sonst hören wir uns selbst)
                        segmenter.Reset();
                        vad.Reset();
                        _output.PilotSpeaking = false;
                    }

                    wasOpen = false;
                    continue;
                }

                wasOpen = true;
                float probability;
                try
                {
                    probability = vad.Process(frame);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("VAD fehlgeschlagen: {Message}", ex.Message);
                    continue;
                }

                var utterance = segmenter.Push(frame, probability);
                _output.PilotSpeaking = segmenter.InSpeech;
                if (utterance is not null)
                {
                    _output.PilotSpeaking = false;
                    _events.Writer.TryWrite(new VoiceEvent.Utterance(utterance, DateTimeOffset.UtcNow));
                }
            }
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            _events.Writer.TryWrite(new VoiceEvent.Tick());
        }
    }

    // ------------------------------------------------------------------ Erkennen und antworten

    private async Task RecognitionLoopAsync(ISpeechRecognizer recognizer, CancellationToken ct)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct))
        {
            try
            {
                switch (e)
                {
                    case VoiceEvent.Tick _:
                        _dialog.Expire();
                        break;
                    case VoiceEvent.ToggleMute _:
                        var reply = _dialog.SetMuted(!_dialog.Muted);
                        _log.LogInformation("Spracheingabe {State} (Tastenkürzel).", _dialog.Muted ? "stumm" : "an");
                        Speak(reply);
                        break;
                    case VoiceEvent.Utterance u:
                        await HandleUtteranceAsync(recognizer, u, ct);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sprachschleife: Satz nicht verarbeitet: {Message}", ex.Message);
            }
        }
    }

    private async Task HandleUtteranceAsync(ISpeechRecognizer recognizer, VoiceEvent.Utterance u, CancellationToken ct)
    {
        // Sim pausiert: nichts verarbeiten, nichts sagen
        if (_monitoring.LastSnapshot?.Paused == true && _settings.Callouts.SilentWhenPaused)
        {
            _log.LogDebug("Satz verworfen – Sim pausiert.");
            return;
        }

        // Stumm: nur kurze Sätze abschreiben (für "Kopilot weiter")
        if (_dialog.Muted && u.Samples.Length > SegmenterOptions.SampleRate * 4)
        {
            return;
        }

        var transcript = await recognizer.TranscribeAsync(u.Samples, ct);
        var text = TranscriptFilter.Clean(transcript.Text);
        _status = _status with { LastTranscriptionMs = transcript.ElapsedMs, LastHeardUtc = u.EndUtc };
        if (text.Length == 0)
        {
            _log.LogDebug("Whisper: nichts Brauchbares ({Ms} ms, \"{Raw}\").", transcript.ElapsedMs, transcript.Text);
            return;
        }

        var normalized = VoiceText.Normalize(text);
        var route = _router.Route(normalized, OwnCallsigns());
        _status = _status with { LastTranscript = text, LastRoute = route.Target == VoiceTarget.Atc ? $"ATC {route.Station ?? route.Callsign}" : "Kopilot" };

        if (route.Target == VoiceTarget.Atc && !_dialog.Muted)
        {
            await HandleAtcAsync(text, normalized, route, u.EndUtc, ct);
            return;
        }

        var intent = VoiceIntents.Recognize(normalized, _settings.Commands.WakeWords);
        var addressed = VoiceText.Words(normalized) is { Length: > 0 } w && _settings.Commands.WakeWords.Contains(w[0]);
        var reply = await _dialog.HandleAsync(intent, normalized, addressed, ct);
        _status = _status with
        {
            LastIntent = intent.ToString(),
            LastReply = reply?.Text,
            LastInputResult = _dialog.LastResult is { } r ? r.Reason is null ? r.Status : $"{r.Status}: {r.Reason}" : null,
        };

        _log.LogInformation(
            "Pilot ({Language}, {Ms} ms): \"{Text}\" → {Intent}{Reply}",
            transcript.Language ?? "?",
            transcript.ElapsedMs,
            text,
            intent,
            reply is null ? "" : $" → \"{reply.Text}\"");

        if (reply is not null)
        {
            Speak(reply);
        }
    }

    private async Task HandleAtcAsync(string text, string normalized, VoiceRoute route, DateTimeOffset time, CancellationToken ct)
    {
        _log.LogInformation("Funk an {Station}: \"{Text}\"", route.Station ?? route.Callsign ?? "ATC", text);
        if (!_atc.IsAvailable)
        {
            _log.LogInformation("Kein ATC-Modul installiert – Funkspruch bleibt unbeantwortet.");
            return;
        }

        var replies = await _atc.HandleAsync(new PilotTransmission(text, normalized, route.Station, route.Callsign, time), ct);
        foreach (var reply in replies)
        {
            Transmit(reply.Station is null && route.Station is not null ? reply with { Station = route.Station } : reply);
        }
    }

    private void Speak(VoiceReply reply)
    {
        var accepted = _output.Say(new SpeechRequest(reply.Text, SpeechPriority.Response)
        {
            Spoken = reply.Spoken,
            Volume = _settings.Voices.KopilotVolume,
            Source = "kopilot",
        });
        if (!accepted)
        {
            _log.LogDebug("Antwort \"{Text}\" nicht gesprochen (keine Stimme oder Sim pausiert).", reply.Text);
        }
    }

    /// <summary>Eigene Rufzeichen: Einstellungen, ATC-Modul und (wenn erlaubt) MSFS.</summary>
    private List<string> OwnCallsigns()
    {
        var list = new List<string>(_settings.Atc.Callsigns);
        list.AddRange(_atc.OwnCallsigns);
        if (_settings.Atc.UseSimCallsign && _sim.Connection.IsConnected)
        {
            var airline = _sim.Str("atcAirline");
            var number = _sim.Str("atcFlightNumber");
            if (airline is not null && number is not null)
            {
                list.Add($"{airline} {number}");
            }

            if (_sim.Str("atcId") is { } id)
            {
                list.Add(id);
            }
        }

        return list;
    }

    private abstract record VoiceEvent
    {
        public sealed record Utterance(float[] Samples, DateTimeOffset EndUtc) : VoiceEvent;

        public sealed record ToggleMute : VoiceEvent;

        public sealed record Tick : VoiceEvent;
    }
}
