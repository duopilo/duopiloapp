using System.Threading.Channels;
using Kopilot.Input;
using Kopilot.Localization;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Speech;
using Microsoft.Extensions.Options;

namespace Kopilot.Voice;

/// <summary>
/// Lokale Sprachschleife: Mikrofon dauerhaft offen, Silero VAD schneidet Sätze aus, Whisper schreibt sie ab,
/// der Router schickt Funksprüche ans ATC-Modul und alles andere an die Kopilot-Befehle; Kopilot antwortet über die
/// gemeinsame Sprachausgabe. Halbduplex: Solange Kopilot spricht, ist das Mikrofon stumm.
/// Kein Sprachmodell, kein Push-to-Talk, alles lokal. Läuft nur in der Tray-Instanz (HTTP-Modus).
/// Änderungen im Dashboard (an/aus, Mikrofon) starten die Schleife neu, ohne Kopilot neu zu starten.
/// </summary>
public sealed class VoiceLoop : BackgroundService, IVoiceStatusSource
{
    /// <summary>So viele Sätze dürfen höchstens auf Whisper warten – mehr wären ohnehin veraltet.</summary>
    private const int MaxQueuedUtterances = 3;

    /// <summary>Lagebericht im Log, solange die Schleife zuhört.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    private readonly KopilotOptions _options;
    private readonly IVoiceOutput _output;
    private readonly MonitoringService _monitoring;
    private readonly SimConnectClient _sim;
    private readonly IAtcLink _atc;
    private readonly InputService _input;
    private readonly ILogger<VoiceLoop> _log;
    private readonly VoiceSettingsStore _store;
    private readonly VoiceDialog _dialog;
    private readonly VoiceDiagnostics _diag = new();
    private readonly object _statusLock = new();
    private readonly object _runLock = new();
    private readonly SemaphoreSlim _reloadSignal = new(0, 1);

    // Ungebunden, damit Stummschalten und "Readback gesprochen" nie verloren gehen. Ticks und Sätze sind begrenzt (siehe unten).
    private readonly Channel<VoiceEvent> _events = Channel.CreateUnbounded<VoiceEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<float[]> _audio = Channel.CreateBounded<float[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private VoiceSettings _settings = new VoiceSettings().Normalized();
    private VoiceDispatcher? _dispatcher;
    private VoiceStatus _status = new();
    private CancellationTokenSource? _runCts;
    private DateTimeOffset? _microphoneOpenedUtc;
    private bool _outputConfigured;
    private int _tickQueued;
    private int _queuedUtterances;

    public VoiceLoop(
        IOptions<KopilotOptions> options,
        IVoiceOutput output,
        MonitoringService monitoring,
        SimConnectClient sim,
        InputService input,
        IAtcLink atc,
        ILogger<VoiceLoop> log)
    {
        _options = options.Value;
        _output = output;
        _monitoring = monitoring;
        _sim = sim;
        _atc = atc;
        _input = input;
        _log = log;
        _store = new VoiceSettingsStore(_options.ResolveDataDirectory());
        _dialog = new VoiceDialog(new InputActionGateway(input), new SimVoiceFlightState(sim, monitoring));
        _status = new VoiceStatus { SettingsFile = _store.FilePath, AtcModule = atc.Enabled };
    }

    /// <summary>Einstellungsdatei (fürs Dashboard).</summary>
    public VoiceSettingsStore Store => _store;

    public VoiceStatus Status
    {
        get
        {
            VoiceStatus s;
            lock (_statusLock)
            {
                s = _status;
            }

            return _diag.Apply(
                s with
                {
                    PendingReadback = _dialog.Pending?.Readback,
                    State = s.State is "listening" or "muted" ? (_dialog.Muted ? "muted" : "listening") : s.State,
                },
                DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Einstellungen geändert (Dashboard): Schleife mit den neuen Werten neu starten – Kopilot läuft weiter.</summary>
    public void Reload(string reason)
    {
        _log.LogInformation(Loc.Log("log.voice.loop.restart"), reason);
        lock (_runLock)
        {
            _runCts?.Cancel();
        }

        try
        {
            _reloadSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Neustart ist schon angefordert
        }
    }

    /// <summary>Aufnahmegeräte neu einlesen (Dashboard), ohne die Schleife anzuhalten.</summary>
    public IReadOnlyList<MicrophoneDevice> RefreshDevices()
    {
        var devices = SafeDeviceList();
        SetStatus(s => s with { Microphones = devices, DefaultMicrophone = DefaultName(devices) });
        return devices;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Auf einem eigenen Thread weiter, damit der Start der anderen Dienste nicht wartet
        await Task.Yield();

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Alte Neustart-Wünsche sind mit diesem Durchlauf erledigt
            while (_reloadSignal.Wait(0))
            {
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_runLock)
            {
                _runCts = run;
            }

            RunResult result;
            try
            {
                result = await RunAsync(run.Token);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                result = RunResult.Restart;
            }
            finally
            {
                lock (_runLock)
                {
                    _runCts = null;
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            switch (result)
            {
                case RunResult.Restart:
                    failures = 0;
                    break;
                case RunResult.Disabled:
                    failures = 0;
                    await WaitForReloadAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                    break;
                default:
                    failures++;
                    var delay = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, failures - 1)));
                    _log.LogWarning(Loc.Log("log.voice.loop.retry"), (int)delay.TotalSeconds);
                    await WaitForReloadAsync(delay, stoppingToken);
                    break;
            }
        }

        ResetOutputSettings();
    }

    private async Task WaitForReloadAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _reloadSignal.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Beenden
        }
    }

    private async Task<RunResult> RunAsync(CancellationToken ct)
    {
        var (settings, error) = _store.Load();
        _settings = settings;
        _diag.Reset();
        DrainQueues();
        var devices = SafeDeviceList();
        lock (_statusLock)
        {
            _status = new VoiceStatus
            {
                Enabled = settings.Enabled,
                SettingsFile = _store.FilePath,
                AtcModule = _atc.Enabled,
                Error = error,
                ConfiguredMicrophone = settings.Microphone.Device,
                Microphones = devices,
                DefaultMicrophone = DefaultName(devices),
                Model = settings.Recognition.Model,
            };
        }

        if (error is not null)
        {
            _log.LogWarning(Loc.Log("log.voice.loop.settingsError"), error);
        }

        LogSettings(settings, devices);
        if (_store.TemplateMismatch(settings) is { } hint)
        {
            _log.LogWarning(Loc.Log("log.voice.loop.templateHint"), hint);
        }

        // Ohne Sprachschleife bleibt alles wie bisher (auch "Sim paused" und die Taxi-Speed-Werte aus appsettings.json)
        if (!settings.Enabled)
        {
            ResetOutputSettings();
            SetStatus(s => s with
            {
                State = "off",
                Note = Loc.T("voice.loop.offNote", _store.FilePath),
            });
            _log.LogInformation(Loc.Log("log.voice.loop.off"), _store.FilePath);
            return RunResult.Disabled;
        }

        ApplyOutputSettings(settings);
        _dialog.ConfirmTimeout = TimeSpan.FromSeconds(settings.Commands.ConfirmTimeoutSec);
        _dialog.SayAgainMaxWords = settings.Commands.SayAgainMaxWords;
        _dialog.RequireWakeWord = settings.Commands.RequireWakeWord;
        _dispatcher = new VoiceDispatcher(_dialog, _atc, new VoiceRouter(settings.Commands.WakeWords), settings.Commands.WakeWords)
        {
            ExtraCallsigns = ExtraCallsigns,
            ExtraStations = settings.Atc.Stations,
            AtcHeardUtc = () => _output.LastAtcSpeechEndUtc,
        };

        IVoiceActivityDetector? vad = null;
        WhisperRecognizer? recognizer = null;
        GlobalHotkey? hotkey = null;
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            (vad, recognizer) = await LoadModelsAsync(settings, ct);
            hotkey = RegisterHotkey(settings.Hotkeys.Mute);

            var runtime = recognizer.Runtime;
            SetStatus(s => s with { State = "listening", Runtime = runtime, ListeningSinceUtc = DateTimeOffset.UtcNow, Error = null });
            _log.LogInformation(
                Loc.Log("log.voice.loop.ready"),
                settings.Recognition.Model,
                recognizer.Runtime ?? "?",
                settings.Recognition.Language,
                settings.Microphone.Threshold,
                _atc.Enabled ? Loc.En("voice.word.on") : Loc.En("voice.word.off"),
                _input.Enabled ? Loc.En("voice.loop.inputFree") : Loc.En("voice.loop.inputLocked"));

            Task[] tasks =
            [
                AudioLoopAsync(vad, settings, loops.Token),
                RecognitionLoopAsync(recognizer, loops.Token),
                TimerLoopAsync(loops.Token),
                CaptureLoopAsync(settings.Microphone.Device, loops.Token),
                WatchdogLoopAsync(loops.Token),
            ];

            // Endet ein Teil (Fehler) oder kommt der Neustart, hören alle auf
            var first = await Task.WhenAny(tasks);
            await loops.CancelAsync();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // Abbruch der übrigen Teile – der eigentliche Grund steckt in "first"
            }

            ct.ThrowIfCancellationRequested();
            await first;
            throw new InvalidOperationException(Loc.T("voice.loop.partEnded"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(s => s with { State = "error", Error = ex.Message });
            _log.LogError(ex, Loc.Log("log.voice.loop.stopped"), ex.Message);
            return RunResult.Error;
        }
        finally
        {
            hotkey?.Dispose();
            recognizer?.Dispose();
            vad?.Dispose();
            _output.PilotSpeaking = false;
            _microphoneOpenedUtc = null;
        }
    }

    // ------------------------------------------------------------------ Einstellungen

    private void ApplyOutputSettings(VoiceSettings s)
    {
        _output.ConfigureVoice(new VoiceOutputOptions
        {
            KopilotVoice = s.Voices.Kopilot.Length > 0 ? s.Voices.Kopilot : null,
            KopilotVolume = s.Voices.KopilotVolume,
            AtcVolume = s.Voices.AtcVolume,
            SilentWhenPaused = s.Callouts.SilentWhenPaused,
            CalloutsEnabled = s.Callouts.Enabled,
            ActiveRules = s.Callouts.Active,
            MutedRules = s.Callouts.Muted,
        });

        var taxi = s.Callouts.TaxiSpeed;
        _monitoring.ConfigureTaxiSpeed(taxi.Enabled, taxi.ThresholdKt, taxi.CooldownSec);
        _outputConfigured = true;
    }

    /// <summary>Sprachschleife aus (auch im laufenden Betrieb): Sprachausgabe und Taxi-Speed wie ohne Sprachschleife.</summary>
    private void ResetOutputSettings()
    {
        if (!_outputConfigured)
        {
            return;
        }

        _output.ConfigureVoice(VoiceOutputOptions.Default);
        _monitoring.ConfigureTaxiSpeed(true, _options.Monitoring.TaxiSpeedKt, _options.Monitoring.TaxiSpeedCooldownSec);
        _outputConfigured = false;
    }

    private void LogSettings(VoiceSettings s, IReadOnlyList<MicrophoneDevice> devices)
    {
        _log.LogInformation(
            Loc.Log("log.voice.loop.settings"),
            _store.FilePath,
            s.Enabled,
            s.Microphone.Device.Length > 0 ? s.Microphone.Device : Loc.En("voice.mic.windowsDefault"),
            s.Recognition.Model,
            s.Recognition.Device,
            s.Recognition.Threads,
            s.Recognition.Language,
            s.Microphone.Threshold,
            s.Microphone.SilenceMs,
            s.Microphone.MinSpeechMs,
            s.Commands.RequireWakeWord ? Loc.En("voice.word.yes") : Loc.En("voice.word.no"),
            string.IsNullOrWhiteSpace(s.Hotkeys.Mute) ? Loc.En("voice.word.none") : s.Hotkeys.Mute);

        _log.LogInformation(
            Loc.Log("log.voice.mic.devices"),
            devices.Count == 0
                ? Loc.En("voice.mic.noneFound")
                : string.Join(" | ", devices.Select(d => d.IsDefault ? Loc.En("voice.mic.listItemDefault", d.Number, d.FullName) : $"[{d.Number}] {d.FullName}")));
    }

    private async Task<(IVoiceActivityDetector Vad, WhisperRecognizer Recognizer)> LoadModelsAsync(VoiceSettings settings, CancellationToken ct)
    {
        var models = Path.Combine(_options.ResolveDataDirectory(), "models");
        var r = settings.Recognition;
        var whisperPath = string.IsNullOrWhiteSpace(r.ModelPath)
            ? Path.Combine(models, WhisperRecognizer.ModelFileName(r.Model))
            : Path.GetFullPath(r.ModelPath, AppContext.BaseDirectory);
        var vadPath = Path.Combine(models, SileroVad.ModelFileName);
        SetStatus(s => s with { State = "loading", Model = Path.GetFileName(whisperPath) });

        if (!File.Exists(vadPath))
        {
            _log.LogInformation(Loc.Log("log.voice.model.vadMissing"), vadPath);
        }

        await ModelDownloader.EnsureAsync(vadPath, SileroVad.ModelUrl, SileroVad.MinModelBytes, null, ct);
        if (string.IsNullOrWhiteSpace(r.ModelPath))
        {
            if (!File.Exists(whisperPath))
            {
                _log.LogInformation(Loc.Log("log.voice.model.whisperMissing"), r.Model, whisperPath);
            }

            var lastLogged = -1;
            await ModelDownloader.EnsureAsync(
                whisperPath,
                WhisperRecognizer.ModelUrl(r.Model),
                WhisperRecognizer.MinModelBytes(r.Model),
                p =>
                {
                    SetStatus(s => s with { DownloadProgress = Math.Round(p, 2) });
                    var quarter = (int)(p * 4);
                    if (quarter > lastLogged && quarter < 4)
                    {
                        lastLogged = quarter;
                        _log.LogInformation(Loc.Log("log.voice.model.whisperProgress"), quarter * 25);
                    }
                },
                ct);
        }
        else if (!File.Exists(whisperPath))
        {
            throw new FileNotFoundException(Loc.T("voice.model.whisperNotFound", whisperPath), whisperPath);
        }

        SetStatus(s => s with { DownloadProgress = null });
        var started = DateTimeOffset.UtcNow;
        var vad = new SileroVad(vadPath);
        try
        {
            var recognizer = await Task.Run(() => WhisperRecognizer.Load(whisperPath, r), ct);
            _log.LogInformation(
                Loc.Log("log.voice.model.loaded"),
                (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                vadPath,
                whisperPath,
                recognizer.Runtime ?? "?");
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
            _log.LogWarning(Loc.Log("log.voice.loop.hotkeyProblem"), problem);
            return null;
        }

        SetStatus(s => s with { Hotkey = binding!.Text });
        return hotkey;
    }

    // ------------------------------------------------------------------ Mikrofon

    /// <summary>
    /// Hält das Mikrofon offen; fällt das Gerät weg, nach 5 Sekunden neu verbinden. Fehlt das eingestellte Gerät
    /// (z. B. VR-Brille noch aus), läuft das Standardgerät, und Kopilot wechselt, sobald das eingestellte auftaucht.
    /// </summary>
    private async Task CaptureLoopAsync(string device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            MicrophoneCapture? capture = null;
            try
            {
                var devices = SafeDeviceList();
                _diag.MicrophoneOpened();
                capture = MicrophoneCapture.Start(device, OnSamples, e => stopped.TrySetResult(e), out var found);
                _microphoneOpenedUtc = DateTimeOffset.UtcNow;
                var deviceName = capture.DeviceName;
                SetStatus(s => s with { Microphone = deviceName, MicrophoneFound = found, Microphones = devices, DefaultMicrophone = DefaultName(devices) });
                if (!found)
                {
                    _log.LogWarning(
                        Loc.Log("log.voice.mic.notFound"),
                        device,
                        capture.DeviceName,
                        string.Join(", ", devices.Select(d => d.Name)));
                }
                else
                {
                    _log.LogInformation(Loc.Log("log.voice.mic.open"), capture.DeviceName, capture.DeviceNumber);
                }

                Exception? reason = null;
                using (ct.Register(() => stopped.TrySetResult(null)))
                {
                    while (true)
                    {
                        if (found)
                        {
                            reason = await stopped.Task;
                            break;
                        }

                        var done = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
                        if (done == stopped.Task)
                        {
                            reason = await stopped.Task;
                            break;
                        }

                        if (MicrophoneCapture.Resolve(device, MicrophoneCapture.Devices()).Found)
                        {
                            _log.LogInformation(Loc.Log("log.voice.mic.nowAvailable"), device);
                            break;
                        }
                    }
                }

                if (reason is not null)
                {
                    _log.LogWarning(Loc.Log("log.voice.mic.disconnected"), reason.Message);
                }
                else if (!ct.IsCancellationRequested && found)
                {
                    _log.LogWarning(Loc.Log("log.voice.mic.reopen"), capture.DeviceName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetStatus(s => s with { Error = Loc.T("voice.mic.error", ex.Message) });
                _log.LogWarning(Loc.Log("log.voice.mic.unavailable"), ex.Message);
            }
            finally
            {
                capture?.Dispose();
                _microphoneOpenedUtc = null;

                // Mitten im Satz weg: angefangenen Satz verwerfen, "Pilot spricht" zurücksetzen (leerer Block = Reset)
                _audio.Writer.TryWrite([]);
                _output.PilotSpeaking = false;
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>Audioblock vom Mikrofon (Aufnahme-Thread).</summary>
    private void OnSamples(float[] samples)
    {
        var now = DateTimeOffset.UtcNow;
        if (_diag.Audio(samples, now))
        {
            _log.LogInformation(
                Loc.Log("log.voice.mic.firstAudio"),
                _microphoneOpenedUtc is { } opened ? (int)(now - opened).TotalMilliseconds : 0,
                MicrophoneCapture.PeakDbfs(samples));
        }

        _audio.Writer.TryWrite(samples);
    }

    /// <summary>
    /// Wacht über das Mikrofon: keine Daten, nur digitale Stille (Windows-Datenschutz oder stummgeschaltet), Ausfall
    /// mitten im Betrieb. Dazu alle 5 Minuten ein kurzer Lagebericht im Log.
    /// </summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var nextReport = DateTimeOffset.UtcNow + ReportInterval;
        var warnedNoData = false;
        var warnedSilence = false;
        var warnedStall = false;
        DateTimeOffset? openedSeen = null;

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            var opened = _microphoneOpenedUtc;
            if (opened != openedSeen)
            {
                // Mikrofon neu geöffnet: Warnungen wieder scharf
                openedSeen = opened;
                warnedNoData = warnedSilence = warnedStall = false;
            }

            if (opened is { } since)
            {
                var first = _diag.FirstAudioUtc;
                if (first is null && now - since > TimeSpan.FromSeconds(5) && !warnedNoData)
                {
                    warnedNoData = true;
                    _log.LogWarning(
                        Loc.Log("log.voice.mic.noData"),
                        Status.Microphone,
                        (int)(now - since).TotalSeconds);
                }

                if (first is not null && now - since > TimeSpan.FromSeconds(20) && _diag.PeakSinceOpen <= -119 && !warnedSilence)
                {
                    warnedSilence = true;
                    _log.LogWarning(
                        Loc.Log("log.voice.mic.silenceOnly"),
                        Status.Microphone);
                }

                if (_diag.LastAudioUtc is { } last && now - last > TimeSpan.FromSeconds(5) && !warnedStall)
                {
                    warnedStall = true;
                    _log.LogWarning(Loc.Log("log.voice.mic.stalled"), Status.Microphone, (int)(now - last).TotalSeconds);
                }
                else if (_diag.LastAudioUtc is { } again && now - again <= TimeSpan.FromSeconds(5) && warnedStall)
                {
                    warnedStall = false;
                    _log.LogInformation(Loc.Log("log.voice.mic.resumed"), Status.Microphone);
                }
            }

            if (now >= nextReport)
            {
                nextReport = now + ReportInterval;
                var w = _diag.TakeWindow();
                var status = Status;
                _log.LogInformation(
                    Loc.Log("log.voice.loop.report"),
                    status.State,
                    status.Microphone,
                    (int)ReportInterval.TotalMinutes,
                    w.Blocks,
                    w.PeakDbfs,
                    w.SpeechSec,
                    w.Utterances,
                    w.Dropped);
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
        var vadFailures = 0;

        await foreach (var chunk in _audio.Reader.ReadAllAsync(ct))
        {
            if (chunk.Length == 0)
            {
                // Mikrofon getrennt
                segmenter.Reset();
                vad.Reset();
                filled = 0;
                _output.PilotSpeaking = false;
                continue;
            }

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
                        if (segmenter.InSpeech)
                        {
                            _log.LogDebug(Loc.Log("log.voice.audio.halfDuplexDrop"));
                        }

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
                    // Nicht bei jedem 32-ms-Block loggen – das Log liefe voll
                    if (vadFailures++ % 100 == 0)
                    {
                        _log.LogWarning(Loc.Log("log.voice.vad.failed"), vadFailures, ex.Message);
                    }

                    continue;
                }

                var wasInSpeech = segmenter.InSpeech;
                var utterance = segmenter.Push(frame, probability);
                if (!wasInSpeech && segmenter.InSpeech)
                {
                    _log.LogDebug(Loc.Log("log.voice.vad.speechStart"), probability);
                }

                // Stumm (z. B. Funk über VATSIM): Ansagen nicht aufhalten
                _output.PilotSpeaking = segmenter.InSpeech && !_dialog.Muted;
                if (utterance is null)
                {
                    continue;
                }

                _output.PilotSpeaking = false;
                var seconds = (double)utterance.Length / SegmenterOptions.SampleRate;
                _diag.Utterance(seconds);
                if (Interlocked.Increment(ref _queuedUtterances) > MaxQueuedUtterances)
                {
                    Interlocked.Decrement(ref _queuedUtterances);
                    _diag.Dropped(Loc.T("voice.drop.whisperBehind"));
                    _log.LogInformation(Loc.Log("log.voice.drop.whisperBehind"), seconds, MaxQueuedUtterances);
                }
                else
                {
                    _log.LogDebug(Loc.Log("log.voice.vad.utterance"), seconds);
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
            // Höchstens ein Tick wartet – ein langsamer Satz staut sonst Ticks auf
            if (Interlocked.Exchange(ref _tickQueued, 1) == 0)
            {
                _events.Writer.TryWrite(new VoiceEvent.Tick());
            }
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
                        Volatile.Write(ref _tickQueued, 0);
                        _dialog.Expire();
                        break;
                    case VoiceEvent.ToggleMute _:
                        var reply = _dialog.SetMuted(!_dialog.Muted);
                        _log.LogInformation(Loc.Log("log.voice.mute.hotkey"), _dialog.Muted ? Loc.En("voice.word.muted") : Loc.En("voice.word.on"));
                        Speak(reply);
                        break;
                    case VoiceEvent.ReplySpoken spoken:
                        _dialog.Spoken(spoken.Reply, spoken.EndUtc);
                        break;
                    case VoiceEvent.Utterance u:
                        Interlocked.Decrement(ref _queuedUtterances);
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
                _log.LogWarning(ex, Loc.Log("log.voice.loop.utteranceFailed"), ex.Message);
            }
        }
    }

    private async Task HandleUtteranceAsync(ISpeechRecognizer recognizer, VoiceEvent.Utterance u, CancellationToken ct)
    {
        var seconds = (double)u.Samples.Length / SegmenterOptions.SampleRate;

        // Sim pausiert: nichts verarbeiten, nichts sagen
        if (_monitoring.LastSnapshot?.Paused == true && _settings.Callouts.SilentWhenPaused)
        {
            _diag.Dropped(Loc.T("voice.drop.simPaused"));
            _log.LogInformation(Loc.Log("log.voice.drop.simPaused"), seconds);
            return;
        }

        // Stumm: nur kurze Sätze abschreiben (für "Kopilot weiter")
        if (_dialog.Muted && u.Samples.Length > SegmenterOptions.SampleRate * 4)
        {
            _diag.Dropped(Loc.T("voice.inputMuted"));
            _log.LogInformation(Loc.Log("log.voice.drop.muted"), seconds);
            return;
        }

        var transcript = await recognizer.TranscribeAsync(u.Samples, ct);
        var text = TranscriptFilter.Clean(transcript.Text);
        SetStatus(s => s with { LastTranscriptionMs = transcript.ElapsedMs, LastHeardUtc = u.EndUtc });
        if (text.Length == 0)
        {
            _diag.Dropped(Loc.T("voice.drop.nothingRecognized"));
            _log.LogInformation(
                Loc.Log("log.voice.whisper.nothing"),
                seconds,
                transcript.ElapsedMs,
                transcript.Text?.Trim());
            return;
        }

        _diag.Recognized();
        if (!string.Equals(text, transcript.Text?.Trim(), StringComparison.Ordinal))
        {
            _log.LogDebug(Loc.Log("log.voice.whisper.raw"), transcript.Text?.Trim(), text);
        }

        var startUtc = u.EndUtc - TimeSpan.FromSeconds(seconds);
        var outcome = await _dispatcher!.HandleAsync(text, startUtc, ct);
        var route = outcome.Route;
        var lastRoute = route.Target == VoiceTarget.Atc ? $"ATC {route.Station ?? route.Callsign} ({route.Reason})" : "Kopilot";
        var lastInput = outcome.Atc is { } atc
            ? atc.Ok ? Loc.T("voice.status.atcReplies", atc.Responses.Count(r => r.From == "atc")) : $"ATC: {atc.Error}"
            : _dialog.LastResult is { } r ? r.Reason is null ? r.Status : $"{r.Status}: {r.Reason}" : null;
        // Dasselbe englisch fürs Log (Logs bleiben immer Englisch)
        var lastInputLog = outcome.Atc is { } atcLog
            ? atcLog.Ok ? Loc.En("voice.status.atcReplies", atcLog.Responses.Count(x => x.From == "atc")) : $"ATC: {Loc.ToEnglish(atcLog.Error)}"
            : _dialog.LastResult is { } rLog ? rLog.Reason is null ? rLog.Status : $"{rLog.Status}: {Loc.ToEnglish(rLog.Reason)}" : null;
        SetStatus(s => s with
        {
            LastTranscript = text,
            LastRoute = lastRoute,
            LastIntent = outcome.Intent?.ToString(),
            LastReply = outcome.Reply?.Text,
            LastInputResult = lastInput,
        });

        if (outcome.Atc is { } result)
        {
            _log.LogInformation(
                Loc.Log("log.voice.atc.transmission"),
                route.Station ?? route.Callsign ?? "ATC",
                route.Reason,
                seconds,
                transcript.ElapsedMs,
                text,
                result.Ok ? Loc.En("voice.atc.repliesFromAtc", result.Responses.Count(x => x.From == "atc")) : Loc.En("voice.atc.error", Loc.ToEnglish(result.Error)));
        }
        else
        {
            _log.LogInformation(
                Loc.Log("log.voice.pilot.utterance"),
                transcript.Language ?? "?",
                seconds,
                transcript.ElapsedMs,
                text,
                outcome.Intent,
                outcome.Reply is null ? "" : $" → \"{outcome.Reply.Text}\"",
                lastInputLog is null ? "" : $" ({lastInputLog})");
        }

        if (outcome.Reply is not null)
        {
            Speak(outcome.Reply);
        }
    }

    private void Speak(VoiceReply reply)
    {
        var accepted = _output.Say(new SpeechRequest(reply.Text, SpeechPriority.Response)
        {
            Spoken = reply.Spoken,
            Volume = _settings.Voices.KopilotVolume,
            Source = "kopilot",

            // Erst wenn der Readback ganz gesprochen ist, zählt "Confirm" (Rückmeldung kommt auf dem Sprach-Thread)
            OnSpoken = end => _events.Writer.TryWrite(new VoiceEvent.ReplySpoken(reply, end)),
        });
        if (!accepted)
        {
            _dialog.NotSpoken(reply);
            _log.LogInformation(Loc.Log("log.voice.reply.notSpoken"), reply.Text);
        }
    }

    /// <summary>Eigene Rufzeichen aus den Einstellungen und (wenn erlaubt) aus MSFS – zusätzlich zu denen des ATC-Moduls.</summary>
    private IEnumerable<string> ExtraCallsigns()
    {
        var list = new List<string>(_settings.Atc.Callsigns);
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

    // ------------------------------------------------------------------ Hilfen

    private void SetStatus(Func<VoiceStatus, VoiceStatus> change)
    {
        lock (_statusLock)
        {
            _status = change(_status);
        }
    }

    private void DrainQueues()
    {
        while (_audio.Reader.TryRead(out _))
        {
        }

        while (_events.Reader.TryRead(out _))
        {
        }

        Volatile.Write(ref _queuedUtterances, 0);
        Volatile.Write(ref _tickQueued, 0);
    }

    private IReadOnlyList<MicrophoneDevice> SafeDeviceList()
    {
        try
        {
            return MicrophoneCapture.DeviceList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(Loc.Log("log.voice.mic.listFailed"), ex.Message);
            return [];
        }
    }

    private static string? DefaultName(IReadOnlyList<MicrophoneDevice> devices) =>
        devices.FirstOrDefault(d => d.IsDefault)?.FullName;

    private enum RunResult
    {
        Disabled,
        Error,
        Restart,
    }

    private abstract record VoiceEvent
    {
        public sealed record Utterance(float[] Samples, DateTimeOffset EndUtc) : VoiceEvent;

        public sealed record ToggleMute : VoiceEvent;

        public sealed record ReplySpoken(VoiceReply Reply, DateTimeOffset EndUtc) : VoiceEvent;

        public sealed record Tick : VoiceEvent;
    }
}
