using System.Diagnostics;
using Kopilot.Monitoring;
using Microsoft.Extensions.Options;

namespace Kopilot.Speech;

/// <summary>
/// Spricht die Callouts des Monitorings: hört auf den <see cref="AlertBus"/>, ordnet über die <see cref="CalloutQueue"/>
/// und spielt nacheinander ab. Warnings unterbrechen eine laufende, weniger wichtige Ansage.
/// <para>
/// Zwei Stimmen: die Windows-Sprachausgabe (sofort, braucht eine installierte englische Stimme) und Kokoro
/// (neuronale englische Stimme, rechnet lokal auf der CPU, Modell wird bei Bedarf geladen). Automatik nimmt Windows,
/// wenn eine englische Stimme installiert ist, sonst Kokoro. Abgespielt wird beides über denselben Windows-Player
/// (Ausgabegerät wählbar).
/// </para>
/// Läuft nur im HTTP-Modus (Tray-Instanz), damit die stdio-Instanz von Claude Desktop nicht doppelt spricht.
/// </summary>
public sealed class CalloutSpeaker : BackgroundService, ISpeechOutput
{
    private const int CacheLimit = 160;

    private readonly AlertBus _bus;
    private readonly MonitoringService _monitoring;
    private readonly KokoroSpeechEngine _kokoro;
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
    private AlertSeverity? _speaking;
    private CancellationTokenSource? _currentCts;
    private string? _lastSpoken;
    private bool _windowsHasEnglish;

    private volatile KokoroState _kokoroState = KokoroState.Idle;
    private volatile string? _kokoroError;
    private double _kokoroProgress;
    private int? _lastSynthesisMs;
    private Task? _kokoroLoad;
    private CancellationToken _stopping;

    public CalloutSpeaker(IOptions<KopilotOptions> options, AlertBus bus, MonitoringService monitoring, KokoroSpeechEngine kokoro, ILogger<CalloutSpeaker> log)
    {
        _bus = bus;
        _monitoring = monitoring;
        _kokoro = kokoro;
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
        _speaking is not null,
        _lastSpoken,
        ActiveEngine,
        new KokoroStatus(
            _kokoroState,
            _kokoroState == KokoroState.Downloading ? Math.Round(Volatile.Read(ref _kokoroProgress), 2) : null,
            _kokoroError,
            _kokoro.Voices,
            _lastSynthesisMs));

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
                _log.LogWarning("Stimmen/Geräte konnten nicht gelesen werden: {Message}", ex.Message);
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
            _log.LogWarning("speech.json konnte nicht gespeichert werden: {Message}", ex.Message);
        }

        if (!normalized.Enabled)
        {
            _queue.Clear();
            CancelCurrent();
        }

        if (old.KokoroVoice != normalized.KokoroVoice || Math.Abs(old.Rate - normalized.Rate) > 1e-6)
        {
            ClearCache();
            SchedulePrewarm();
        }

        if (old.Engine != normalized.Engine)
        {
            _log.LogInformation("Stimme: {Engine}", normalized.Engine);
        }

        EnsureKokoroIfNeeded();
        await ApplyAsync(ct);
        return await RefreshAsync(ct);
    }

    public async Task SpeakNowAsync(string text, CancellationToken ct)
    {
        if (ActiveEngine is null)
        {
            throw new InvalidOperationException(StatusError() ?? "Sprachausgabe ist nicht verfügbar.");
        }

        await SpeakAsync(text, ct);
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
            _log.LogInformation(
                "Sprachausgabe bereit: Windows-Stimme {Voice}{English}",
                _speech.ActiveVoice ?? "keine",
                _windowsHasEnglish ? "" : " (keine englische Stimme installiert)");
        }
        catch (Exception ex)
        {
            _error = $"Windows-Sprachausgabe nicht verfügbar: {ex.Message}";
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
        if (_speech is null || !SpeechEngines.NeedsKokoro(_settings.Engine, _windowsHasEnglish))
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
            _log.LogInformation("Kokoro bereit ({Voices} englische Stimmen).", _kokoro.Voices.Count);
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
            _log.LogWarning("Kokoro konnte nicht geladen werden: {Message}", ex.Message);
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

    private async Task<SpeechClip> KokoroClipAsync(string text, CancellationToken ct)
    {
        var settings = _settings;
        var key = $"{settings.KokoroVoice}|{settings.Rate:0.00}|{text}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var watch = Stopwatch.StartNew();
        var clip = await Task.Run(() => _kokoro.Synthesize(text, settings.KokoroVoice, settings.Rate), ct);
        _lastSynthesisMs = (int)watch.ElapsedMilliseconds;
        _log.LogDebug("Kokoro: \"{Text}\" in {Ms} ms", text, _lastSynthesisMs);

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
            await KokoroClipAsync(CalloutText.ForSpeech(text), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("Vorab-Synthese \"{Text}\" fehlgeschlagen: {Message}", text, ex.Message);
        }

        _signal.Release();
    }

    // ------------------------------------------------------------------ Abspielen

    private async Task SpeakAsync(string callout, CancellationToken ct)
    {
        var speech = _speech ?? throw new InvalidOperationException("Windows-Wiedergabe nicht verfügbar.");
        var text = CalloutText.ForSpeech(callout);
        if (text.Length == 0)
        {
            return;
        }

        if (ActiveEngine == SpeechEngines.Kokoro)
        {
            var clip = await KokoroClipAsync(text, ct);
            ct.ThrowIfCancellationRequested();
            if (clip.Samples.Length == 0)
            {
                return;
            }

            var wav = WavEncoder.Encode(clip.Samples, clip.SampleRate, _settings.Volume);
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
            foreach (var e in events)
            {
                var (accepted, preempt) = _queue.Offer(e, _settings, now, _speaking);
                if (preempt)
                {
                    CancelCurrent();
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
            await _signal.WaitAsync(ct);
            if (_queue.Take(DateTimeOffset.UtcNow) is not { } next)
            {
                // Nichts zu sagen – Zeit für die Vorab-Synthese
                await PrewarmOneAsync(ct);
                continue;
            }

            if (!_settings.Enabled || ActiveEngine is null)
            {
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts = cts;
            _speaking = next.Severity;
            try
            {
                await SpeakAsync(next.Callout!, cts.Token);
                _lastSpoken = next.Callout;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Von einer Warning unterbrochen
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Ansage \"{Callout}\" fehlgeschlagen: {Message}", next.Callout, ex.Message);
            }
            finally
            {
                _speaking = null;
                _currentCts = null;
            }
        }
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
            _log.LogWarning("Spracheinstellungen nicht übernommen: {Message}", ex.Message);
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
            return "Sprachausgabe startet …";
        }

        return _kokoroState switch
        {
            KokoroState.Downloading => $"Kokoro wird geladen ({Volatile.Read(ref _kokoroProgress):P0} von ~310 MB) – bis dahin liest der Browser vor.",
            KokoroState.Loading => "Kokoro startet …",
            KokoroState.Failed => $"Kokoro konnte nicht geladen werden: {_kokoroError}",
            _ when SpeechEngines.Normalize(_settings.Engine) == SpeechEngines.Kokoro => "Kokoro startet …",
            _ => "Keine englische Windows-Stimme installiert. Stimme auf „Kokoro“ oder „Automatisch“ stellen, oder Englisch (Vereinigtes Königreich) mit Sprachausgabe in Windows hinzufügen.",
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
