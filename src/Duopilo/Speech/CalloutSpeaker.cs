using System.Diagnostics;
using Duopilo.Atc;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Microsoft.Extensions.Options;

namespace Duopilo.Speech;

/// <summary>
/// Spricht die Callouts des Monitorings: hört auf den <see cref="AlertBus"/>, ordnet über die <see cref="CalloutQueue"/>
/// und spielt nacheinander ab. Warnings unterbrechen eine laufende, weniger wichtige Ansage.
/// <para>
/// Zwei Stimmen: die Windows-Sprachausgabe (sofort, braucht eine installierte englische Stimme) und Kokoro
/// (neuronale englische Stimme, rechnet lokal auf der CPU, Modell wird bei Bedarf geladen). Automatik nimmt Windows,
/// wenn eine englische Stimme installiert ist, sonst Kokoro. Abgespielt wird beides über denselben Windows-Player
/// (Ausgabegerät wählbar).
/// </para>
/// <para>
/// Dieselbe Warteschlange spricht auch die Antworten der Sprachschleife (<see cref="IVoiceOutput"/>) und die
/// ATC-Funksprüche (<see cref="IAtcRadio"/>): Warnungen &gt; Callouts &gt; Antworten &gt; ATC. Jede ATC-Station spricht mit
/// ihrer eigenen Kokoro-Stimme; dafür wird Kokoro auch dann geladen, wenn die Callouts die Windows-Stimme nehmen.
/// Eine Warning unterbricht alles, ein unterbrochener Funkspruch kommt danach einmal erneut.
/// </para>
/// Läuft nur im HTTP-Modus (Tray-Instanz), damit die stdio-Instanz von Claude Desktop nicht doppelt spricht.
/// </summary>
public sealed class CalloutSpeaker : BackgroundService, ISpeechOutput, IVoiceOutput, IAtcRadio
{
    /// <summary>So lange warten Ansagen (außer Warnungen) höchstens, solange der Pilot spricht.</summary>
    private static readonly TimeSpan MaxHoldForPilot = TimeSpan.FromSeconds(6);

    /// <summary>Länger spricht der Pilot nicht am Stück (Sätze enden spätestens nach 12 s) – sonst hängt der Zustand.</summary>
    private static readonly TimeSpan MaxPilotSpeaking = TimeSpan.FromSeconds(15);

    /// <summary>Zeitkritische Callouts warten nicht, auch wenn der Pilot gerade spricht.</summary>
    private static readonly HashSet<string> TimeCritical = new(StringComparer.Ordinal)
    {
        Rules.Pm100Knots, Rules.PmV1, Rules.PmRotate, Rules.PmPositiveClimb, Rules.PmSpoilers, Rules.PmReverse, Rules.PmDecel,
        Rules.Pm70Knots, Rules.AutoRadioAltitude, Rules.AutoMinimum, Rules.AutoRetard,
    };

    private const int CacheLimit = 160;

    private readonly AlertBus _bus;
    private readonly MonitoringService _monitoring;
    private readonly KokoroSpeechEngine _kokoro;
    private readonly TtsEngineRegistry? _engines;
    private readonly SpeechSettingsStore _store;
    private readonly ILogger<CalloutSpeaker> _log;
    private readonly CalloutQueue _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, SpeechClip> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Queue<string> _prewarm = new();

    private WindowsSpeech? _speech;
    private volatile SpeechSettings _settings;
    private volatile string? _error;
    private IReadOnlyList<VoiceInfo> _voices = [];
    private IReadOnlyList<AudioDeviceInfo> _devices = [];
    private volatile SpeechItem? _current;
    private volatile bool _preempted;
    private volatile VoiceOutputOptions _voice = VoiceOutputOptions.Default;
    private volatile bool _pilotSpeaking;
    private long _lastSpeechEndTicks;
    private long _lastAtcSpeechEndTicks;
    private long _pilotSpeakingSinceTicks;
    private int _speakingNow;
    private DateTimeOffset? _holdSinceUtc;
    private CancellationTokenSource? _currentCts;
    private volatile bool _atcWantsKokoro;
    private string? _lastSpoken;
    private bool _windowsHasEnglish;

    private volatile KokoroState _kokoroState = KokoroState.Idle;
    private volatile string? _kokoroError;
    private double _kokoroProgress;
    private int? _lastSynthesisMs;
    private Task? _kokoroLoad;
    private CancellationToken _stopping;

    /// <param name="engines">Weitere lokale Engines für Smalltalk in anderen Sprachen (Piper). null = nur Englisch.</param>
    public CalloutSpeaker(
        IOptions<DuopiloOptions> options,
        AlertBus bus,
        MonitoringService monitoring,
        KokoroSpeechEngine kokoro,
        ILogger<CalloutSpeaker> log,
        TtsEngineRegistry? engines = null)
    {
        _bus = bus;
        _monitoring = monitoring;
        _kokoro = kokoro;
        _engines = engines;
        _log = log;
        _store = new SpeechSettingsStore(Path.Combine(options.Value.ResolveDataDirectory(), "speech.json"));
        _settings = (_store.Load() ?? SpeechSettings.FromOptions(options.Value.Speech, options.Value.Monitoring)).Normalized();
        _monitoring.AircraftAutoCallouts = _settings.AircraftAutoCallouts;
    }

    /// <summary>Welche Stimme gerade spricht: "windows", "kokoro" oder null (dann liest das Dashboard im Browser vor).</summary>
    public string? ActiveEngine =>
        SpeechEngines.Effective(_settings.Engine, _speech is not null, _windowsHasEnglish, _kokoroState == KokoroState.Ready);

    public SpeechStatus Status => new(
        ActiveEngine is not null,
        StatusError(),
        _settings,
        _speech?.ActiveVoice,
        _voices,
        _devices,
        _current is not null,
        _lastSpoken,
        ActiveEngine,
        new KokoroStatus(
            _kokoroState,
            _kokoroState == KokoroState.Downloading ? Math.Round(Volatile.Read(ref _kokoroProgress), 2) : null,
            _kokoroError,
            _kokoro.Voices,
            _lastSynthesisMs));

    /// <summary>Kokoro-Stimme, mit der Duopilo gerade spricht: die der Sprachschleife (First Officer), sonst die aus dem Dashboard.</summary>
    public string? KopilotVoice => _voice.KopilotVoice ?? _settings.KokoroVoice;

    // ------------------------------------------------------------------ IVoiceOutput (Sprachschleife)

    public bool Available => ActiveEngine is not null;

    /// <summary>
    /// Spricht gerade eine Windows-Stimme (statt Kokoro), ihr Geschlecht – damit Name und Stimme des First Officers zusammenpassen.
    /// null = Kokoro oder unbekannt.
    /// </summary>
    public VoiceGender? EngineVoiceGender
    {
        get
        {
            if (ActiveEngine != SpeechEngines.Windows || _speech?.ActiveVoice is not { } active)
            {
                return null;
            }

            var gender = _voices.FirstOrDefault(v => string.Equals(v.Name, active, StringComparison.OrdinalIgnoreCase))?.Gender;
            return gender?.ToLowerInvariant() switch
            {
                "female" => VoiceGender.Female,
                "male" => VoiceGender.Male,
                _ => null,
            };
        }
    }

    public bool IsSpeaking => _current is not null || Volatile.Read(ref _speakingNow) > 0;

    public DateTimeOffset LastSpeechEndUtc => new(Interlocked.Read(ref _lastSpeechEndTicks), TimeSpan.Zero);

    public DateTimeOffset? LastAtcSpeechEndUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAtcSpeechEndTicks);
            return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    public bool PilotSpeaking
    {
        get => _pilotSpeaking && !PilotSpeakingStale(DateTimeOffset.UtcNow);
        set
        {
            var changed = _pilotSpeaking != value;
            if (changed && value)
            {
                Interlocked.Exchange(ref _pilotSpeakingSinceTicks, DateTimeOffset.UtcNow.UtcTicks);
            }

            _pilotSpeaking = value;
            if (changed && !value)
            {
                // Wartende Ansagen sofort sprechen, sobald der Pilot fertig ist
                _signal.Release();
            }
        }
    }

    /// <summary>"Pilot spricht" hängt schon zu lange (z. B. Mikrofon mitten im Satz weg) – nicht mehr darauf warten.</summary>
    private bool PilotSpeakingStale(DateTimeOffset now) =>
        now - new DateTimeOffset(Interlocked.Read(ref _pilotSpeakingSinceTicks), TimeSpan.Zero) > MaxPilotSpeaking;

    /// <summary>Sim pausiert (laut letztem Monitoring-Takt).</summary>
    private bool SimPaused => _monitoring.LastSnapshot?.Paused == true;

    private bool Silenced => _voice.SilentWhenPaused && SimPaused;

    public void ConfigureVoice(VoiceOutputOptions options)
    {
        var old = _voice;
        var normalized = options.Normalized();
        _voice = normalized;
        if (old.KopilotVoice != normalized.KopilotVoice)
        {
            ClearCache();
            SchedulePrewarm();
        }
    }

    public bool Say(SpeechRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || ActiveEngine is null || Silenced)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var item = new SpeechItem
        {
            Text = request.Text.Trim(),
            Spoken = string.IsNullOrWhiteSpace(request.Spoken) ? null : request.Spoken.Trim(),
            Priority = request.Priority,
            TimeUtc = now,
            ExpiresUtc = now + (request.MaxAge ?? CalloutQueue.DefaultMaxAge(request.Priority)),
            Voice = request.Voice,
            Volume = Math.Clamp(double.IsFinite(request.Volume) ? request.Volume : 1, 0, 1),
            Source = request.Source,
            OnSpoken = request.OnSpoken,
            Tone = request.Tone,
            Plain = request.Plain,
        };

        var (accepted, preempt) = _queue.Offer(item, now, _current?.Priority);
        if (preempt)
        {
            Preempt();
        }

        if (accepted)
        {
            _signal.Release();
        }

        return accepted;
    }

    public async Task<SpeechStatus> RefreshAsync(CancellationToken ct)
    {
        if (_speech is not null)
        {
            try
            {
                _voices = WindowsSpeech.Voices();
                _devices = await WindowsSpeech.DevicesAsync();
                _windowsHasEnglish = _speech.HasEnglishVoice;
            }
            catch (Exception ex)
            {
                _log.LogWarning(Loc.Log("log.speech.devicesFailed"), ex.Message);
            }
        }

        return Status;
    }

    public async Task<SpeechStatus> UpdateAsync(SpeechSettings settings, CancellationToken ct)
    {
        var old = _settings;
        var normalized = settings.Normalized();
        _settings = normalized;
        _monitoring.AircraftAutoCallouts = normalized.AircraftAutoCallouts;
        try
        {
            _store.Save(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(Loc.Log("log.speech.saveFailed"), ex.Message);
        }

        if (!normalized.Enabled)
        {
            // Nur die Callouts verstummen – Antworten und ATC der Sprachschleife laufen weiter
            _queue.Drop(i => i.IsAlert);
            if (_current?.IsAlert == true)
            {
                CancelCurrent();
            }
        }

        if (old.KokoroVoice != normalized.KokoroVoice || Math.Abs(old.Rate - normalized.Rate) > 1e-6)
        {
            ClearCache();
            SchedulePrewarm();
        }

        if (old.Engine != normalized.Engine)
        {
            _log.LogInformation(Loc.Log("log.speech.engine"), normalized.Engine);
        }

        EnsureKokoroIfNeeded();
        await ApplyAsync(ct);
        return await RefreshAsync(ct);
    }

    public async Task SpeakNowAsync(string text, CancellationToken ct)
    {
        if (ActiveEngine is null)
        {
            throw new InvalidOperationException(StatusError() ?? Loc.T("speech.unavailable"));
        }

        // Wie eine Ansage aus der Warteschlange: Mikrofon der Sprachschleife stumm, danach die Nachlaufzeit
        Interlocked.Increment(ref _speakingNow);
        try
        {
            await SpeakAsync(text, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _speakingNow);
            Interlocked.Exchange(ref _lastSpeechEndTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            _speech = new WindowsSpeech();
            await ApplyAsync(stoppingToken);
            await RefreshAsync(stoppingToken);
            _windowsHasEnglish = _speech.HasEnglishVoice;
            if (_windowsHasEnglish)
            {
                _log.LogInformation(Loc.Log("log.speech.ready"), _speech.ActiveVoice ?? Loc.En("speech.word.none"));
            }
            else
            {
                _log.LogInformation(Loc.Log("log.speech.readyNoEnglish"), _speech.ActiveVoice ?? Loc.En("speech.word.none"));
            }
        }
        catch (Exception ex)
        {
            _error = Loc.T("speech.windowsUnavailable", ex.Message);
            _log.LogWarning("{Error}", _error);
            _speech = null;
            return;
        }

        EnsureKokoroIfNeeded();

        var reader = ReadAlertsAsync(stoppingToken);
        try
        {
            await SpeakLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }

        await reader.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ Kokoro

    /// <summary>Lädt Kokoro im Hintergrund, wenn die Einstellung es braucht (einmalig; nach einem Fehler erst nach Neustart).</summary>
    private void EnsureKokoroIfNeeded()
    {
        if (_speech is null || !(SpeechEngines.NeedsKokoro(_settings.Engine, _windowsHasEnglish) || _atcWantsKokoro))
        {
            return;
        }

        lock (_cacheLock)
        {
            if (_kokoroLoad is not null)
            {
                return;
            }

            _kokoroLoad = Task.Run(() => LoadKokoroAsync(_stopping));
        }
    }

    private async Task LoadKokoroAsync(CancellationToken ct)
    {
        try
        {
            _kokoroState = _kokoro.ModelPresent ? KokoroState.Loading : KokoroState.Downloading;
            await _kokoro.LoadAsync(
                p =>
                {
                    Volatile.Write(ref _kokoroProgress, p);
                    if (p >= 1)
                    {
                        _kokoroState = KokoroState.Loading;
                    }
                },
                ct);
            _kokoroState = KokoroState.Ready;
            _log.LogInformation(Loc.Log("log.speech.kokoroReady"), _kokoro.Voices.Count);
            SchedulePrewarm();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Beenden
        }
        catch (Exception ex)
        {
            _kokoroError = ex.Message;
            _kokoroState = KokoroState.Failed;
            _log.LogWarning(Loc.Log("log.speech.kokoroFailed"), ex.Message);
        }
    }

    /// <summary>Feste Callouts vorab rechnen, damit sie ohne Verzögerung kommen (zwischen den Ansagen, eine nach der anderen).</summary>
    private void SchedulePrewarm()
    {
        lock (_cacheLock)
        {
            _prewarm.Clear();
            foreach (var text in CalloutText.Common)
            {
                _prewarm.Enqueue(text);
            }
        }

        _signal.Release();
    }

    private async Task<SpeechClip> KokoroClipAsync(string text, string voice, CancellationToken ct)
    {
        var settings = _settings;
        var key = $"{voice}|{settings.Rate:0.00}|{text}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var watch = Stopwatch.StartNew();
        var clip = await Task.Run(() => _kokoro.Synthesize(text, voice, settings.Rate), ct);
        _lastSynthesisMs = (int)watch.ElapsedMilliseconds;
        _log.LogDebug(Loc.Log("log.speech.kokoroSynthesis"), voice, text, _lastSynthesisMs);

        lock (_cacheLock)
        {
            if (_cache.TryAdd(key, clip))
            {
                _cacheOrder.Enqueue(key);
                while (_cache.Count > CacheLimit && _cacheOrder.TryDequeue(out var oldest))
                {
                    _cache.Remove(oldest);
                }
            }
        }

        return clip;
    }

    /// <summary>Stimme für eine Ansage: eigene Stimme der Ansage (ATC), sonst die Duopilo-Stimme, sonst die aus dem Dashboard.</summary>
    private string VoiceFor(SpeechItem? item)
    {
        if (KokoroSpeechEngine.IsEnglishVoice(item?.Voice))
        {
            return item!.Voice!;
        }

        return _voice.KopilotVoice ?? _settings.KokoroVoice;
    }

    private void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    private async Task PrewarmOneAsync(CancellationToken ct)
    {
        if (_kokoroState != KokoroState.Ready || ActiveEngine != SpeechEngines.Kokoro)
        {
            return;
        }

        string? text;
        lock (_cacheLock)
        {
            _prewarm.TryDequeue(out text);
        }

        if (text is null)
        {
            return;
        }

        try
        {
            await KokoroClipAsync(Pronunciation.ForSpeech(text), VoiceFor(null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(Loc.Log("log.speech.prewarmFailed"), text, ex.Message);
        }

        _signal.Release();
    }

    // ------------------------------------------------------------------ ATC

    /// <summary>
    /// ATC-Funkspruch in die Warteschlange (Priorität ATC: nach Warnungen, Callouts und Antworten von Duopilo), gesprochen mit
    /// der Kokoro-Stimme der Station. Solange Kokoro lädt, spricht die Windows-Stimme (falls englisch). Kehrt sofort zurück –
    /// die Reihenfolge der Funksprüche bleibt in der Warteschlange erhalten, und das Mikrofon der Sprachschleife bleibt
    /// stumm, solange ATC spricht.
    /// </summary>
    public Task TransmitAsync(AtcTransmission transmission, CancellationToken ct)
    {
        if (_speech is null || !_settings.Enabled || string.IsNullOrWhiteSpace(transmission.Spoken))
        {
            return Task.CompletedTask;
        }

        if (!_atcWantsKokoro)
        {
            _atcWantsKokoro = true;
            EnsureKokoroIfNeeded();
        }

        if (Silenced)
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var item = new SpeechItem
        {
            Text = string.IsNullOrWhiteSpace(transmission.Text) ? transmission.Spoken : transmission.Text,
            Spoken = transmission.Spoken,
            Priority = SpeechPriority.Atc,
            TimeUtc = now,
            ExpiresUtc = now + CalloutQueue.AtcMaxAge,
            Voice = transmission.Voice,
            Volume = _voice.AtcVolume,
            Source = "atc:" + transmission.StationName,
        };

        var (accepted, preempt) = _queue.Offer(item, now, _current?.Priority);
        if (preempt)
        {
            // Funk unterbricht Smalltalk
            Preempt();
        }

        if (accepted)
        {
            _signal.Release();
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Abspielen

    private Task SpeakAsync(string callout, CancellationToken ct) =>
        SpeakAsync(new SpeechItem { Text = callout, Priority = SpeechPriority.Response }, ct);

    private async Task SpeakAsync(SpeechItem item, CancellationToken ct)
    {
        var speech = _speech ?? throw new InvalidOperationException(Loc.T("speech.playbackUnavailable"));
        if (item.Priority == SpeechPriority.Atc)
        {
            await SpeakAtcAsync(speech, item, ct);
            return;
        }

        if (item.Tone)
        {
            // Warnton (Bestätigungspflicht ohne Antwort) über denselben Player wie Kokoro
            await _engineLock.WaitAsync(ct);
            try
            {
                await speech.PlayWavAsync(ToneGenerator.AttentionWav(_settings.Volume * item.Volume), ct);
            }
            finally
            {
                _engineLock.Release();
            }

            return;
        }

        // Freier Text (Smalltalk) so, wie er ist – Funk und Callouts mit der gemeinsamen Aussprache
        var text = item.Plain ? (item.Spoken ?? item.Text).Trim() : Pronunciation.ForSpeech(item.Spoken ?? item.Text);
        if (text.Length == 0)
        {
            return;
        }

        if (item.Plain && _engines?.Find(item.Voice) is { } other)
        {
            // Andere Sprache (z. B. Deutsch mit Piper): gleiche Wiedergabe wie Kokoro
            var foreign = await other.Engine.SynthesizeAsync(text, other.Voice, _settings.Rate, ct);
            ct.ThrowIfCancellationRequested();
            await PlayClipAsync(speech, foreign, item, ct);
            return;
        }

        if (ActiveEngine == SpeechEngines.Kokoro)
        {
            // Smalltalk nicht zwischenspeichern – er verdrängte sonst die vorab gerechneten Callouts
            var voice = VoiceFor(item);
            var settings = _settings;
            var clip = item.Plain
                ? await Task.Run(() => _kokoro.Synthesize(text, voice, settings.Rate), ct)
                : await KokoroClipAsync(text, voice, ct);
            ct.ThrowIfCancellationRequested();
            if (clip.Samples.Length == 0)
            {
                return;
            }

            var wav = WavEncoder.Encode(clip.Samples, clip.SampleRate, _settings.Volume * item.Volume);
            await _engineLock.WaitAsync(ct);
            try
            {
                await speech.PlayWavAsync(wav, ct);
            }
            finally
            {
                _engineLock.Release();
            }

            return;
        }

        await _engineLock.WaitAsync(ct);
        try
        {
            await speech.SpeakAsync(text, ct);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private async Task PlayClipAsync(WindowsSpeech speech, SpeechClip clip, SpeechItem item, CancellationToken ct)
    {
        if (clip.Samples.Length == 0)
        {
            return;
        }

        var wav = WavEncoder.Encode(clip.Samples, clip.SampleRate, _settings.Volume * item.Volume);
        await _engineLock.WaitAsync(ct);
        try
        {
            await speech.PlayWavAsync(wav, ct);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>
    /// Funkspruch: Kokoro mit der Stimme der Station (ohne Cache – Funksprüche wiederholen sich selten), solange Kokoro lädt
    /// die englische Windows-Stimme, sonst nichts. Die Sprechform kommt fertig vom ATC-Modul.
    /// </summary>
    private async Task SpeakAtcAsync(WindowsSpeech speech, SpeechItem item, CancellationToken ct)
    {
        var text = (item.Spoken ?? item.Text).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var settings = _settings;
        if (_kokoroState == KokoroState.Ready && KokoroSpeechEngine.IsEnglishVoice(item.Voice))
        {
            var clip = await Task.Run(() => _kokoro.Synthesize(text, item.Voice!, settings.Rate), ct);
            ct.ThrowIfCancellationRequested();
            if (clip.Samples.Length == 0)
            {
                return;
            }

            var wav = WavEncoder.Encode(clip.Samples, clip.SampleRate, settings.Volume * item.Volume);
            await _engineLock.WaitAsync(ct);
            try
            {
                await speech.PlayWavAsync(wav, ct);
            }
            finally
            {
                _engineLock.Release();
            }
        }
        else if (_windowsHasEnglish)
        {
            await _engineLock.WaitAsync(ct);
            try
            {
                await speech.SpeakAsync(text, ct);
            }
            finally
            {
                _engineLock.Release();
            }
        }
    }

    private async Task ReadAlertsAsync(CancellationToken ct)
    {
        var since = _bus.LastId;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<AlertEvent> events;
            try
            {
                events = await _bus.WaitAsync(since, TimeSpan.FromSeconds(25), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (events.Count == 0)
            {
                continue;
            }

            since = events.Max(e => e.Id);
            if (ActiveEngine is null)
            {
                // Keine Stimme bereit (z. B. Kokoro lädt noch) – dann liest das Dashboard im Browser vor
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var voice = _voice;
            foreach (var e in events)
            {
                // Sprachschleife: aktive Callouts aus voice-settings.json, bei Pause gar nichts (auch nicht "Sim paused")
                if (!voice.SpeaksRule(e.Rule) || (voice.SilentWhenPaused && (SimPaused || e.Rule == Rules.SimPaused)))
                {
                    continue;
                }

                var (accepted, preempt) = _queue.OfferAlert(e, _settings, now, _current?.Priority, voice.KopilotVolume);
                if (preempt)
                {
                    Preempt();
                }

                if (accepted)
                {
                    _signal.Release();
                }
            }
        }
    }

    private async Task SpeakLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Mit Zeitlimit: wartende Ansagen nach einer Pause oder nach dem Piloten auch ohne neues Signal sprechen
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), ct);
            while (!ct.IsCancellationRequested && await SpeakNextAsync(ct))
            {
            }
        }
    }

    /// <summary>Spricht die nächste Ansage. false = nichts (mehr) zu tun.</summary>
    private async Task<bool> SpeakNextAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Silenced)
        {
            // Pause: Callouts und Antworten verfallen, ATC wartet bis nach der Pause
            _queue.Drop(i => i.Priority != SpeechPriority.Atc);
            return false;
        }

        if (_queue.Peek(now) is not { } next)
        {
            _holdSinceUtc = null;

            // Nichts zu sagen – Zeit für die Vorab-Synthese
            await PrewarmOneAsync(ct);
            return false;
        }

        if (ActiveEngine is null || (next.IsAlert && !_settings.Enabled))
        {
            _queue.Take(now);
            return true;
        }

        // Der Pilot spricht: kurz warten (nur Warnungen und zeitkritische Callouts fallen ins Wort)
        if (next.Priority < SpeechPriority.Warning && PilotSpeaking && !(next.Rule is not null && TimeCritical.Contains(next.Rule)))
        {
            _holdSinceUtc ??= now;
            if (now - _holdSinceUtc < MaxHoldForPilot)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                return true;
            }
        }

        _holdSinceUtc = null;
        if (_queue.Take(now) is not { } item)
        {
            return true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentCts = cts;
        _preempted = false;
        _current = item;
        try
        {
            await SpeakAsync(item, cts.Token);
            _lastSpoken = item.Text;
            if (!cts.IsCancellationRequested)
            {
                Spoken(item, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Von einer Warnung unterbrochen: Antworten und ATC einmal neu einreihen, Callouts verfallen
            if (_preempted && _queue.Requeue(item, DateTimeOffset.UtcNow))
            {
                _log.LogDebug(Loc.Log("log.speech.requeued"), item.Text);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(Loc.Log("log.speech.failed"), item.Text, ex.Message);
        }
        finally
        {
            _current = null;
            _currentCts = null;
            Interlocked.Exchange(ref _lastSpeechEndTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        return true;
    }

    /// <summary>Ansage ganz gesprochen: ATC-Zeit merken, Rückmeldung an die Sprachschleife (Readback gehört).</summary>
    private void Spoken(SpeechItem item, DateTimeOffset end)
    {
        if (item.Priority == SpeechPriority.Atc)
        {
            Interlocked.Exchange(ref _lastAtcSpeechEndTicks, end.UtcTicks);
        }

        try
        {
            item.OnSpoken?.Invoke(end);
        }
        catch (Exception ex)
        {
            _log.LogDebug(Loc.Log("log.speech.onSpokenFailed"), item.Text, ex.Message);
        }
    }

    /// <summary>Eine Warnung unterbricht die laufende Ansage.</summary>
    private void Preempt()
    {
        _preempted = true;
        CancelCurrent();
    }

    private void CancelCurrent()
    {
        try
        {
            _currentCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ansage war gerade fertig
        }
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        if (_speech is null)
        {
            return;
        }

        await _engineLock.WaitAsync(ct);
        try
        {
            await _speech.ApplyAsync(_settings);
        }
        catch (Exception ex)
        {
            _log.LogWarning(Loc.Log("log.speech.applyFailed"), ex.Message);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private string? StatusError()
    {
        if (_error is not null)
        {
            return _error;
        }

        if (ActiveEngine is not null)
        {
            return null;
        }

        if (_speech is null)
        {
            return Loc.T("speech.status.starting");
        }

        return _kokoroState switch
        {
            KokoroState.Downloading => Loc.T("speech.status.kokoroDownloading", Volatile.Read(ref _kokoroProgress)),
            KokoroState.Loading => Loc.T("speech.status.kokoroStarting"),
            KokoroState.Failed => Loc.T("speech.status.kokoroFailed", _kokoroError),
            _ when SpeechEngines.Normalize(_settings.Engine) == SpeechEngines.Kokoro => Loc.T("speech.status.kokoroStarting"),
            _ => Loc.T("speech.status.noEnglishVoice"),
        };
    }

    public override void Dispose()
    {
        base.Dispose();
        _speech?.Dispose();
        _kokoro.Dispose();
        _signal.Dispose();
        _engineLock.Dispose();
    }
}
