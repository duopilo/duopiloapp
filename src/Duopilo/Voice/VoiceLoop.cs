using System.Threading.Channels;
using Duopilo.FlightPlan;
using Duopilo.Input;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Sim;
using Duopilo.Smalltalk;
using Duopilo.Speech;
using Duopilo.Traffic;
using Microsoft.Extensions.Options;

namespace Duopilo.Voice;

/// <summary>
/// Lokale Sprachschleife: Mikrofon dauerhaft offen, Silero VAD schneidet Sätze aus, Whisper schreibt sie ab,
/// der Router schickt Funksprüche ans ATC-Modul und alles andere an die Befehle für den Kopiloten; der Kopilot antwortet über die
/// gemeinsame Sprachausgabe. Halbduplex: Solange der Kopilot spricht, ist das Mikrofon stumm.
/// Kein Push-to-Talk, alles lokal – nur der optionale Smalltalk geht an ein Sprachmodell (<see cref="SmalltalkService"/>): Sätze,
/// die weder Befehl noch Abfrage noch Funkspruch sind. Dessen Antwort wird nur gesprochen (ganz unten in der Warteschlange), nie
/// ausgeführt. Der First Officer (Name und Stimme pro Flug) stellt sich beim Briefing vor und hört auf seinen Namen.
/// Läuft nur in der Tray-Instanz (HTTP-Modus). Änderungen im Dashboard (an/aus, Mikrofon) starten die Schleife neu, ohne Duopilo
/// neu zu starten.
/// </summary>
public sealed class VoiceLoop : BackgroundService, IVoiceStatusSource
{
    /// <summary>So viele Sätze dürfen höchstens auf Whisper warten – mehr wären ohnehin veraltet.</summary>
    private const int MaxQueuedUtterances = 3;

    /// <summary>Lagebericht im Log, solange die Schleife zuhört.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    private readonly DuopiloOptions _options;
    private readonly IVoiceOutput _output;
    private readonly MonitoringService _monitoring;
    private readonly SimConnectClient _sim;
    private readonly IAtcLink _atc;
    private readonly InputService _input;
    private readonly AlertBus _bus;
    private readonly ILogger<VoiceLoop> _log;
    private readonly VoiceSettingsStore _store;
    private readonly VoiceDialog _dialog;
    private readonly SmalltalkService _smalltalk;
    private readonly TtsEngineRegistry _engines;
    private readonly SimBriefService _simBrief;
    private readonly IVoiceFlightStateSource _state;
    private readonly FirstOfficerRoster _roster;
    private readonly VoiceDiagnostics _diag = new();
    private readonly SimGate _simGate = new();
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
    private VoiceOutputOptions _outputOptions = VoiceOutputOptions.Default;
    private string? _appliedPersonaVoice;
    private IReadOnlyCollection<string>? _installedVoices;
    private CancellationToken _runToken;
    private CancellationToken _stoppingToken;
    private CancellationTokenSource? _chatCts;
    private long _chatSeq;
    private int _ticks;
    private WhisperRecognizer? _recognizer;
    private string? _whisperName;

    // Sim läuft nicht (nicht verbunden, kein Flug, pausiert): Mikrofon wird nicht ausgewertet. Gesetzt im Sekundentakt.
    private volatile bool _simGated;

    public VoiceLoop(
        IOptions<DuopiloOptions> options,
        IVoiceOutput output,
        MonitoringService monitoring,
        SimConnectClient sim,
        InputService input,
        IAtcLink atc,
        AlertBus bus,
        SimBriefService simBrief,
        TrafficService traffic,
        ILogger<VoiceLoop> log,
        SmalltalkService smalltalk,
        TtsEngineRegistry? engines = null)
    {
        _options = options.Value;
        _output = output;
        _monitoring = monitoring;
        _sim = sim;
        _atc = atc;
        _input = input;
        _bus = bus;
        _log = log;
        _store = new VoiceSettingsStore(_options.ResolveDataDirectory());
        _state = new SimVoiceFlightState(sim, monitoring, simBrief, traffic, atc);
        _dialog = new VoiceDialog(new InputActionGateway(input), _state);
        _status = new VoiceStatus { SettingsFile = _store.FilePath, AtcModule = atc.Enabled };
        _smalltalk = smalltalk;
        _engines = engines ?? new TtsEngineRegistry([]);
        _simBrief = simBrief;
        _roster = new FirstOfficerRoster(new FirstOfficerStore(Path.Combine(_options.ResolveDataDirectory(), FirstOfficerStore.FileName)));
        _dialog.Briefing = Introduction;

        // Einstellungen (Name, Smalltalk) oder Oberflächensprache geändert: First Officer und Stimme neu prüfen
        _smalltalk.Changed += () => _events.Writer.TryWrite(new VoiceEvent.SettingsChanged());
        Loc.LanguageChanged += _ => _events.Writer.TryWrite(new VoiceEvent.SettingsChanged());
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

            // Checkliste und Quittungen gehören dem Erkennungs-Thread – hier nur Momentaufnahmen lesen
            var checklist = _dialog.Checklists.Active is { } active ? $"{active.Title}: {_dialog.Checklists.Current?.Challenge}" : null;
            var acks = _dialog.Acknowledgments;
            var persona = s.Enabled ? _roster.Current : null;
            var config = _smalltalk.Config;
            var choice = config.Enabled ? SmalltalkChoice(persona, Loc.Language) : null;
            var gated = _simGated && s.State is "listening" or "muted";
            return _diag.Apply(
                s with
                {
                    PendingReadback = _dialog.Pending?.Readback,
                    State = gated ? "inactive" : s.State is "listening" or "muted" ? (_dialog.Muted ? "muted" : "listening") : s.State,
                    InactiveReason = gated ? s.InactiveReason : null,
                    ExecuteWithCallout = _dialog.ExecuteWithCallout,
                    Checklist = checklist,
                    AwaitingAcknowledgment = acks?.PendingTexts,
                    MissedAcknowledgment = acks?.EscalatedTexts,
                    FirstOfficer = persona is null
                        ? null
                        : new FirstOfficerStatus(
                            persona.Name,
                            persona.GenderText,
                            persona.SpeakingVoice,
                            choice is null ? null : VoiceKey(choice),
                            persona.Introduced,
                            persona.FixedName,
                            persona.FlightKey),
                    Smalltalk = config.Provider,
                    SmalltalkLanguage = choice?.Language,
                    SmalltalkNote = choice is null ? null : SmalltalkNote(choice),
                },
                DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Einstellungen geändert (Dashboard): Schleife mit den neuen Werten neu starten – Duopilo läuft weiter.</summary>
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

    /// <summary>
    /// Feste Stimme des First Officers (voices.kopilot in voice-settings.json, "" = automatisch pro Flug) speichern und sofort
    /// übernehmen – ohne Neustart der Schleife. Gleiches Geschlecht: Name und Vorstellung bleiben.
    /// </summary>
    public VoiceSettings SetFixedVoice(string voice)
    {
        var saved = _store.Update(null, null, voice);
        _events.Writer.TryWrite(new VoiceEvent.FixedVoice(saved.Voices.Kopilot));
        _log.LogInformation(Loc.Log("log.voice.firstOfficer.voiceSetting"), saved.Voices.Kopilot.Length > 0 ? saved.Voices.Kopilot : "auto");
        return saved;
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
        _stoppingToken = stoppingToken;

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
        _simGate.Reset();
        _simGated = false;
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
        _dialog.ExecuteWithCallout = settings.Commands.ExecuteWithCallout;
        _dialog.MinConfidence = settings.Commands.MinConfidence;
        _dialog.Acknowledgments = settings.Acknowledge.Enabled ? new AcknowledgmentTracker(settings.Acknowledge) : null;
        _log.LogInformation(
            Loc.Log("log.voice.loop.modes"),
            settings.Commands.ExecuteWithCallout ? Loc.En("voice.word.on") : Loc.En("voice.word.off"),
            settings.Commands.MinConfidence,
            settings.Acknowledge.Enabled ? Loc.En("voice.word.on") : Loc.En("voice.word.off"),
            settings.Acknowledge.RepeatAfterSec);
        _dispatcher = new VoiceDispatcher(_dialog, _atc, new VoiceRouter(settings.Commands.WakeWords), settings.Commands.WakeWords)
        {
            ExtraCallsigns = ExtraCallsigns,
            ExtraStations = settings.Atc.Stations,
            AtcHeardUtc = () => _output.LastAtcSpeechEndUtc,
            PersonaName = () => _roster.Current?.AddressWord,
            ChatWords = () => _smalltalk.Config.Enabled ? _smalltalk.Config.UnaddressedMaxWords : null,
        };

        IVoiceActivityDetector? vad = null;
        WhisperRecognizer? recognizer = null;
        GlobalHotkey? hotkey = null;
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runToken = loops.Token;
        try
        {
            (vad, recognizer) = await LoadModelsAsync(settings, ct);
            _recognizer = recognizer;
            _whisperName = recognizer.Name;
            hotkey = RegisterHotkey(settings.Hotkeys.Mute);
            UpdatePersona();
            PrepareFirstOfficerVoice();
            UpdateSimGate();

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
                AlertLoopAsync(loops.Token),
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
            CancelChat();
            hotkey?.Dispose();
            _recognizer = null;
            recognizer?.Dispose();
            vad?.Dispose();
            _output.PilotSpeaking = false;
            _microphoneOpenedUtc = null;
            if (_simGated)
            {
                _simGated = false;
                _dialog.Thaw();
                _output.Suspend(null);
            }
        }
    }

    // ------------------------------------------------------------------ Einstellungen

    private void ApplyOutputSettings(VoiceSettings s)
    {
        _outputOptions = new VoiceOutputOptions
        {
            KopilotVoice = s.Voices.Kopilot.Length > 0 ? s.Voices.Kopilot : null,
            KopilotVolume = s.Voices.KopilotVolume,
            AtcVolume = s.Voices.AtcVolume,
            SilentWhenPaused = s.Callouts.SilentWhenPaused,
            CalloutsEnabled = s.Callouts.Enabled,
            ActiveRules = s.Callouts.Active,
            MutedRules = s.Callouts.Muted,
        };
        _output.ConfigureVoice(WithPersona(_outputOptions));
        _appliedPersonaVoice = PersonaVoices(_roster.Current);

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
        _output.Suspend(null);
        _monitoring.ConfigureTaxiSpeed(true, _options.Monitoring.TaxiSpeedKt, _options.Monitoring.TaxiSpeedCooldownSec);
        _outputConfigured = false;
        _appliedPersonaVoice = null;
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
            : Duopilo.Hosting.DataDirectoryMigration.MapLegacyPath(Path.GetFullPath(r.ModelPath, AppContext.BaseDirectory));
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
            var name = _roster.Current?.Name;
            var recognizer = await Task.Run(() => WhisperRecognizer.Load(whisperPath, r, name), ct);
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
    /// (z. B. VR-Brille noch aus), läuft das Standardgerät, und Duopilo wechselt, sobald das eingestellte auftaucht.
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
        var wasGated = false;
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

                // Sim läuft nicht: nicht auswerten – kein VAD, kein Whisper (spart CPU)
                if (_simGated)
                {
                    if (!wasGated)
                    {
                        segmenter.Reset();
                        vad.Reset();
                        _output.PilotSpeaking = false;
                        wasGated = true;
                    }

                    continue;
                }

                wasGated = false;
                var open = HalfDuplex.MicOpen(_output.IsSpeaking, _output.LastSpeechEndUtc, DateTimeOffset.UtcNow, tail);
                if (!open)
                {
                    if (wasOpen)
                    {
                        // Der Kopilot spricht: angefangenen Satz verwerfen (sonst hören wir uns selbst)
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
                        OnTick();
                        break;
                    case VoiceEvent.Alert alert:
                        OnAlert(alert.Event);
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
                    case VoiceEvent.ChatAnswer answer:
                        OnChatAnswer(answer);
                        break;
                    case VoiceEvent.SettingsChanged _:
                        UpdatePersona();
                        PrepareFirstOfficerVoice();
                        break;
                    case VoiceEvent.FixedVoice fixedVoice:
                        _settings = _settings with { Voices = _settings.Voices with { Kopilot = fixedVoice.Voice } };
                        if (_outputConfigured)
                        {
                            ApplyOutputSettings(_settings);
                        }

                        UpdatePersona();
                        PrepareFirstOfficerVoice();
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

        // Sim läuft nicht (nicht verbunden, kein Flug, pausiert): nichts verarbeiten, nichts sagen
        var simState = SimGate.Read(_sim);
        if (_simGated || SimGate.Blocks(simState, _settings.Callouts.SilentWhenPaused))
        {
            _diag.Dropped(InactiveReason(simState));
            _log.LogInformation(Loc.Log("log.voice.drop.simInactive"), seconds, SimGate.Code(simState));
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
        var outcome = await _dispatcher!.HandleAsync(text, startUtc, transcript.Confidence, ct);
        Apply(_dialog.TakeAcknowledgmentActions());
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
            LastConfidence = transcript.Confidence is { } c ? Math.Round(c, 2) : null,
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
                transcript.Confidence is { } confidence ? confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?",
                text,
                outcome.Intent,
                outcome.Reply is null ? "" : $" → \"{outcome.Reply.Text}\"",
                lastInputLog is null ? "" : $" ({lastInputLog})");
        }

        if (outcome.Reply is not null)
        {
            Speak(outcome.Reply);
        }

        if (outcome.Chat)
        {
            StartChat(text);
        }
    }

    private void Speak(VoiceReply reply)
    {
        if (_simGated)
        {
            // Sim läuft nicht: keine Ansage (auch nicht zum Stummschalten per Tastenkürzel)
            _dialog.NotSpoken(reply);
            return;
        }

        var accepted = _output.Say(new SpeechRequest(reply.Text, reply.Smalltalk ? SpeechPriority.Smalltalk : SpeechPriority.Response)
        {
            Spoken = reply.Spoken,
            Volume = _settings.Voices.KopilotVolume,
            Source = reply.Smalltalk ? "kopilot:smalltalk" : "kopilot",
            Plain = reply.Smalltalk,
            Voice = reply.Voice,
            Language = reply.Language,

            // Erst wenn der Readback ganz gesprochen ist, zählt "Confirm" (Rückmeldung kommt auf dem Sprach-Thread)
            OnSpoken = end => _events.Writer.TryWrite(new VoiceEvent.ReplySpoken(reply, end)),
        });
        if (!accepted)
        {
            _dialog.NotSpoken(reply);
            _log.LogInformation(Loc.Log("log.voice.reply.notSpoken"), reply.Text);
        }
    }

    // ------------------------------------------------------------------ Bestätigungspflicht und Checklisten

    /// <summary>Meldungen des Monitorings und des ATC-Moduls mitlesen (Bestätigungspflicht).</summary>
    private async Task AlertLoopAsync(CancellationToken ct)
    {
        var since = _bus.LastId;
        while (!ct.IsCancellationRequested)
        {
            var events = await _bus.WaitAsync(since, TimeSpan.FromSeconds(25), ct);
            foreach (var e in events)
            {
                since = Math.Max(since, e.Id);
                _events.Writer.TryWrite(new VoiceEvent.Alert(e));
            }
        }
    }

    private void OnAlert(AlertEvent e)
    {
        // Sim läuft nicht: keine neue Bestätigungspflicht (Callouts werden dann auch nicht gesprochen)
        if (_dialog.Acknowledgments is not { } acks || _dialog.Muted || _simGated)
        {
            return;
        }

        if (acks.Requires(e))
        {
            _log.LogInformation(Loc.Log("log.voice.ack.expected"), e.Rule, e.Reminder ?? e.Callout, acks.RepeatAfter.TotalSeconds);
        }

        Apply(acks.Observe(e));
    }

    /// <summary>Sekundentakt: Readbacks verfallen lassen, Challenge wiederholen, Quittungen nachhalten, First Officer prüfen.</summary>
    private void OnTick()
    {
        UpdateSimGate();
        if (++_ticks % 5 == 0)
        {
            // Neuer SimBrief-Plan = neuer Flug = neuer First Officer; am Gate stellt er sich vor
            UpdatePersona();
            MaybeIntroduce();
        }

        if (_dialog.Tick() is { } repeat)
        {
            _log.LogInformation(Loc.Log("log.voice.checklist.repeat"), repeat.Text);
            Speak(repeat);
        }

        if (_dialog.Acknowledgments is not { } acks)
        {
            return;
        }

        if (_dialog.Muted)
        {
            // Stumm: der Pilot kann nicht antworten – keine Wiederholungen
            acks.Clear();
            return;
        }

        // Sim läuft nicht: Wartezeiten der Quittungen stehen (wie bisher bei Pause)
        Apply(acks.Tick(DateTimeOffset.UtcNow, _simGated));
    }

    /// <summary>
    /// Läuft der Sim (verbunden, Flug geladen, nicht pausiert)? Sonst ist die Sprachschleife inaktiv: Mikrofon nicht auswerten,
    /// kein ATC, kein Smalltalk, keine Vorstellung, keine Callouts und Wiederholungen; eine laufende Ansage bricht ab; offene
    /// Readbacks, Checklisten und Quittungen frieren ein. Keine Ansage dazu – nur das Dashboard zeigt den Grund.
    /// </summary>
    private void UpdateSimGate()
    {
        var state = SimGate.Read(_sim);
        var change = _simGate.Update(state, _settings.Callouts.SilentWhenPaused, DateTimeOffset.UtcNow);
        var reason = _simGate.Gated ? InactiveReason(state) : null;
        SetStatus(s => s with { SimState = SimGate.Code(state), InactiveReason = reason });
        if (change is null)
        {
            return;
        }

        if (change.Gated)
        {
            _simGated = true;
            _dialog.Freeze();
            CancelChat();
            SetStatus(s => s with { SmalltalkPending = false });
            _output.PilotSpeaking = false;
            _output.Suspend(SimGate.Code(state));
            _log.LogInformation(Loc.Log("log.voice.loop.simInactive"), SimGate.Code(state));
        }
        else
        {
            _dialog.Thaw();
            _output.Suspend(null);
            _simGated = false;
            _log.LogInformation(Loc.Log("log.voice.loop.simActive"), (int)(change.InactiveFor ?? TimeSpan.Zero).TotalSeconds);
        }
    }

    /// <summary>Grund für das Dashboard (in der eingestellten Sprache).</summary>
    private static string InactiveReason(SimActivityState state) => state switch
    {
        SimActivityState.NotConnected => Loc.T("voice.sim.notConnected"),
        SimActivityState.NoFlight => Loc.T("voice.sim.noFlight"),
        _ => Loc.T("voice.sim.paused"),
    };

    /// <summary>Wiederholen (einmal), eskalieren (Warnton und Eintrag im Dashboard) oder den Eintrag wieder aufheben.</summary>
    private void Apply(IReadOnlyList<AcknowledgmentAction> actions)
    {
        foreach (var action in actions)
        {
            var item = action.Item;
            switch (action)
            {
                case AcknowledgmentAction.Repeat:
                    _log.LogInformation(Loc.Log("log.voice.ack.repeat"), item.Rule, item.Callout);
                    _output.Say(new SpeechRequest(item.Callout, item.Severity == AlertSeverity.Warning ? SpeechPriority.Warning : SpeechPriority.Callout)
                    {
                        Spoken = Pronunciation.ForSpeech(item.Callout),
                        Volume = _settings.Voices.KopilotVolume,
                        Source = "kopilot:ack",
                    });
                    break;
                case AcknowledgmentAction.Escalate:
                    _log.LogWarning(Loc.Log("log.voice.ack.escalated"), item.Rule, item.Callout);
                    _output.Say(new SpeechRequest(Loc.En("voice.ack.toneText"), SpeechPriority.Warning)
                    {
                        Tone = true,
                        Volume = _settings.Voices.KopilotVolume,
                        Source = "kopilot:ack",
                    });
                    _bus.Publish([new AlertEvent(0, DateTimeOffset.UtcNow, AcknowledgmentTracker.EscalationRule, AlertSeverity.Warning, AlertKind.Raise, null, Loc.T("voice.ack.missing", item.Callout))]);
                    break;
                case AcknowledgmentAction.Resolved:
                    _log.LogInformation(Loc.Log("log.voice.ack.resolved"), item.Rule, item.Callout);
                    if (_dialog.Acknowledgments is not { Escalated.Count: > 0 })
                    {
                        _bus.Publish([new AlertEvent(0, DateTimeOffset.UtcNow, AcknowledgmentTracker.EscalationRule, AlertSeverity.Info, AlertKind.Clear, null, Loc.T("voice.ack.resolved", item.Callout))]);
                    }

                    break;
            }
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

    // ------------------------------------------------------------------ First Officer und Smalltalk

    /// <summary>
    /// First Officer für den laufenden Flug sicherstellen (neuer Plan → neuer Name und neue Stimme) und seine Stimme an die
    /// Sprachausgabe geben. Nie die Stimme einer ATC-Station.
    /// </summary>
    private void UpdatePersona()
    {
        FirstOfficerPersona? persona;
        bool changed;
        try
        {
            IReadOnlyCollection<string> avoid = _atc.Enabled ? _atc.AssignedVoices() : [];
            (persona, changed) = _roster.Ensure(
                FirstOfficerRoster.FlightKeyOf(_simBrief.Current),
                _smalltalk.FirstOfficer,
                FixedPersonaVoice(),
                _output.EngineVoiceGender,
                InstalledVoices(),
                avoid,
                AutoEngineVoices());
        }
        catch (Exception ex)
        {
            _log.LogWarning(Loc.Log("log.voice.firstOfficer.failed"), ex.Message);
            return;
        }

        if (changed)
        {
            _smalltalk.ResetHistory();
            if (persona is null)
            {
                _log.LogInformation(Loc.Log("log.voice.firstOfficer.off"));
            }
            else
            {
                _log.LogInformation(Loc.Log("log.voice.firstOfficer.assigned"), persona.FlightKey, persona.Name, persona.GenderText, persona.SpeakingVoice);
            }
        }

        if (_outputConfigured && PersonaVoices(persona) != _appliedPersonaVoice)
        {
            _appliedPersonaVoice = PersonaVoices(persona);
            _output.ConfigureVoice(WithPersona(_outputOptions));
        }

        // Neuer Flug, andere mehrsprachige Stimme: deren Stil laden (das Modell teilen sich alle Stimmen)
        PrepareFirstOfficerVoice();

        // Name in den Wortschatz-Hinweis von Whisper, damit er richtig geschrieben wird ("Erik" statt "Eric")
        // (einmal je Name versuchen – schlägt das Neubauen fehl, bleibt der bisherige Hinweis)
        if (_recognizer is { } recognizer && recognizer.Name != persona?.Name && _whisperName != persona?.Name)
        {
            _whisperName = persona?.Name;
            try
            {
                recognizer.SetName(persona?.Name);
                _log.LogInformation(Loc.Log("log.voice.whisper.promptName"), persona?.Name ?? "-");
            }
            catch (Exception ex)
            {
                _log.LogWarning(Loc.Log("log.voice.whisper.promptFailed"), ex.Message);
            }
        }
    }

    private VoiceOutputOptions WithPersona(VoiceOutputOptions options) =>
        _roster.Current is { } persona ? options with { KopilotVoice = persona.Voice, KopilotEngineVoice = persona.EngineVoice } : options;

    /// <summary>Beide Stimmen des First Officers als ein Wert (geändert → an die Sprachausgabe geben).</summary>
    private static string? PersonaVoices(FirstOfficerPersona? persona) => persona is null ? null : persona.Voice + "|" + persona.EngineVoice;

    /// <summary>
    /// Feste Stimme aus voice-settings.json (voices.kopilot): Kokoro oder mehrsprachig. Eine mehrsprachige Stimme, deren Engine
    /// ausgeschaltet ist (Smalltalk:Supertonic: false), gilt als „automatisch“.
    /// </summary>
    private string? FixedPersonaVoice()
    {
        var voice = FirstOfficerCasting.NormalizeVoice(_settings.Voices.Kopilot);
        return voice is null || KokoroSpeechEngine.IsEnglishVoice(voice) || _engines.Find(voice) is not null ? voice : null;
    }

    /// <summary>
    /// Automatische Stimme: Läuft der Smalltalk in einer anderen Sprache als Englisch, bekommt der First Officer eine mehrsprachige
    /// Stimme (Supertonic) – sie spricht Callouts und Smalltalk mit demselben Sprecher. Sonst Kokoro (leer).
    /// </summary>
    private IReadOnlyList<TtsVoice> AutoEngineVoices()
    {
        var language = Loc.Language.Trim().ToLowerInvariant();
        if (!_smalltalk.Config.Enabled || language == SmalltalkVoices.English)
        {
            return [];
        }

        return MultilingualVoices.All.Where(v => _engines.Find(v.Key) is not null && MultilingualVoices.Speaks(v, language)).ToList();
    }

    private IReadOnlyCollection<string> InstalledVoices() =>
        _installedVoices ??= KokoroSpeechEngine.ListVoices(Path.Combine(AppContext.BaseDirectory, "voices"));

    /// <summary>Sprache und Stimme des Smalltalks: <paramref name="language"/>, wenn die Stimme des First Officers sie spricht.</summary>
    private SmalltalkVoiceChoice SmalltalkChoice(FirstOfficerPersona? persona, string language) =>
        SmalltalkVoices.Choose(language, persona?.EngineVoice, _engines.Engines);

    /// <summary>Mehrsprachige Stimme, die den Smalltalk spricht (null = die Kokoro-Stimme des First Officers).</summary>
    private static string? VoiceKey(SmalltalkVoiceChoice choice) => choice.Voice?.Key;

    /// <summary>Hinweis im Dashboard, wenn der Smalltalk auf Englisch zurückfällt.</summary>
    private string? SmalltalkNote(SmalltalkVoiceChoice choice)
    {
        var language = Loc.Available.FirstOrDefault(l => l.Code == choice.Wanted)?.Name ?? choice.Wanted;
        return choice.Reason switch
        {
            "englishVoice" => Loc.T("voice.smalltalk.englishVoice", language),
            "noVoice" => Loc.T("voice.smalltalk.noVoice", language),
            "downloading" => Loc.T("voice.smalltalk.voiceLoading", language),
            "failed" => Loc.T("voice.smalltalk.voiceFailed", language, _engines.Find(_roster.Current?.EngineVoice)?.Engine.Error ?? "?"),
            _ => null,
        };
    }

    /// <summary>
    /// Mehrsprachige Stimme des First Officers (Supertonic) im Hintergrund laden, sobald er sie hat – auch ohne Smalltalk, sie spricht
    /// ja auch die Callouts. Solange spricht seine Kokoro-Stimme.
    /// </summary>
    private void PrepareFirstOfficerVoice()
    {
        if (_roster.Current?.EngineVoice is not { } key || _engines.Find(key) is not { } found || found.Engine.StateOf(found.Voice) != TtsState.Idle)
        {
            return;
        }

        _log.LogInformation(Loc.Log("log.voice.firstOfficer.preparingVoice"), key);
        _ = found.Engine.PrepareAsync(found.Voice, _stoppingToken);
    }

    /// <summary>
    /// Satz ans Sprachmodell – im Hintergrund, damit die Schleife weiter Callouts, Quittungen und Funk verarbeitet. Die Antwort kommt
    /// als Ereignis zurück und wird nur gesprochen. Ein neuer Satz ersetzt eine noch offene Frage.
    /// </summary>
    private void StartChat(string text)
    {
        var persona = _roster.Current;
        var choice = SmalltalkChoice(persona, Loc.Language);
        PrepareFirstOfficerVoice();
        var question = new SmalltalkQuestion(text, choice.Language, persona?.Name, ReadContext());
        _log.LogInformation(Loc.Log("log.voice.smalltalk.asked"), SmalltalkProviders.DisplayName(_smalltalk.Config.Provider), choice.Language);
        CancelChat();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_runToken);
        _chatCts = cts;
        var seq = ++_chatSeq;
        SetStatus(s => s with { SmalltalkPending = true });
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var answer = await _smalltalk.AskAsync(question, cts.Token);
                    _events.Writer.TryWrite(new VoiceEvent.ChatAnswer(seq, answer, choice));
                }
                catch (OperationCanceledException)
                {
                    // Ersetzt oder Schleife beendet
                }
                catch (Exception ex)
                {
                    _events.Writer.TryWrite(new VoiceEvent.ChatAnswer(seq, new SmalltalkAnswer(false, SmalltalkService.Unavailable(question.Language), ex.Message), choice));
                }
                finally
                {
                    cts.Dispose();
                }
            },
            CancellationToken.None);
    }

    private void CancelChat()
    {
        try
        {
            _chatCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Frage war schon beantwortet
        }

        _chatCts = null;
    }

    /// <summary>
    /// Antwort des Sprachmodells: sprechen (ganz unten in der Warteschlange, jede andere Ansage unterbricht sie) – aber nicht, wenn
    /// inzwischen etwas Wichtigeres läuft (Readback, Checkliste, stumm). Nie als Befehl ausgewertet.
    /// </summary>
    private void OnChatAnswer(VoiceEvent.ChatAnswer e)
    {
        if (e.Seq != _chatSeq)
        {
            return;
        }

        SetStatus(s => s with { SmalltalkPending = false });
        if (_simGated || _dialog.Muted || _dialog.Checklists.Active is not null || _dialog.Pending is not null)
        {
            _log.LogInformation(Loc.Log("log.voice.smalltalk.dropped"), e.Answer.Text);
            return;
        }

        var reply = _dialog.RememberChat(VoiceReply.Chat(e.Answer.Text, e.Choice.Language, VoiceKey(e.Choice)));
        SetStatus(s => s with { LastSmalltalk = reply.Text, LastReply = reply.Text });
        _log.LogInformation(
            Loc.Log("log.voice.smalltalk.reply"),
            e.Choice.Language,
            e.Answer.Ok ? Loc.En("voice.word.yes") : Loc.En("voice.word.no"),
            e.Answer.ElapsedMs ?? 0,
            reply.Text);
        Speak(reply);
    }

    /// <summary>Lesender Kontext fürs Sprachmodell: Flugphase, Position, Route, Sprit, Wetter.</summary>
    private SmalltalkContext ReadContext()
    {
        try
        {
            var state = _state.Read();
            var connected = _sim.Connection.IsConnected;
            return SmalltalkContext.From(
                state,
                _monitoring.Phase,
                _simBrief.Current,
                connected ? _sim.Num("lat") : null,
                connected ? _sim.Num("lon") : null,
                connected ? _sim.Str("title") : null,
                connected ? _sim.Num("zuluTimeSec") : null);
        }
        catch (Exception ex)
        {
            _log.LogDebug(Loc.Log("log.voice.smalltalk.contextFailed"), ex.Message);
            return SmalltalkContext.Empty;
        }
    }

    /// <summary>"Briefing" (oder am Gate von selbst): Vorstellung des First Officers – im Smalltalk-Modus in dessen Sprache.</summary>
    private VoiceReply? Introduction()
    {
        if (_roster.Current is not { } persona)
        {
            return null;
        }

        // Ohne Smalltalk auf Englisch – aber immer mit seiner Stimme (mehrsprachig, sobald sie geladen ist)
        var choice = SmalltalkChoice(persona, _smalltalk.Config.Enabled ? Loc.Language : SmalltalkVoices.English);
        var language = choice.Language;
        _roster.MarkIntroduced();
        _log.LogInformation(Loc.Log("log.voice.firstOfficer.introduced"), persona.Name, language);
        return VoiceReply.Chat(FirstOfficerCasting.Introduction(persona.Name, language), language, VoiceKey(choice));
    }

    /// <summary>
    /// Vorstellung von selbst, einmal pro Flug: am Boden am Startflughafen (höchstens 5 NM), Sim läuft, nichts anderes offen.
    /// Nach der Landung am Ziel nie.
    /// </summary>
    private void MaybeIntroduce()
    {
        if (_roster.Current is not { Introduced: false } || !_smalltalk.FirstOfficer.Introduce || _dialog.Muted || _simGated
            || _dialog.Checklists.Active is not null || _dialog.Pending is not null
            || !_output.Available || _output.IsSpeaking || _output.PilotSpeaking)
        {
            return;
        }

        if (!_sim.Connection.IsConnected || _monitoring.LastSnapshot is not { Paused: false } || _monitoring.Phase != "ground")
        {
            return;
        }

        if (_simBrief.Current is not { } ofp || ofp.Origin.Lat is not { } originLat || ofp.Origin.Lon is not { } originLon
            || _sim.Num("lat") is not { } lat || _sim.Num("lon") is not { } lon
            || RouteMath.DistanceNm(lat, lon, originLat, originLon) > 5)
        {
            return;
        }

        if (Introduction() is { } introduction)
        {
            Speak(_dialog.RememberChat(introduction));
        }
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

        public sealed record Alert(AlertEvent Event) : VoiceEvent;

        public sealed record ToggleMute : VoiceEvent;

        public sealed record ReplySpoken(VoiceReply Reply, DateTimeOffset EndUtc) : VoiceEvent;

        public sealed record Tick : VoiceEvent;

        /// <summary>Antwort des Sprachmodells auf Frage Nummer <paramref name="Seq"/> (ältere werden verworfen).</summary>
        public sealed record ChatAnswer(long Seq, SmalltalkAnswer Answer, SmalltalkVoiceChoice Choice) : VoiceEvent;

        /// <summary>Einstellungen des First Officers/Smalltalks oder Oberflächensprache geändert.</summary>
        public sealed record SettingsChanged : VoiceEvent;

        /// <summary>Feste Stimme des First Officers im Dashboard geändert ("" = automatisch).</summary>
        public sealed record FixedVoice(string Voice) : VoiceEvent;
    }
}
