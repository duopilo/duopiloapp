using Kopilot.Monitoring;
using Microsoft.Extensions.Options;

namespace Kopilot.Speech;

/// <summary>
/// Spricht die Callouts des Monitorings über Windows: hört auf den <see cref="AlertBus"/>, ordnet über die <see cref="CalloutQueue"/>
/// und spielt nacheinander ab. Warnings unterbrechen eine laufende, weniger wichtige Ansage.
/// Läuft nur im HTTP-Modus (Tray-Instanz), damit die stdio-Instanz von Claude Desktop nicht doppelt spricht.
/// </summary>
public sealed class CalloutSpeaker : BackgroundService, ISpeechOutput
{
    private readonly AlertBus _bus;
    private readonly MonitoringService _monitoring;
    private readonly SpeechSettingsStore _store;
    private readonly ILogger<CalloutSpeaker> _log;
    private readonly CalloutQueue _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _engineLock = new(1, 1);

    private WindowsSpeech? _speech;
    private volatile SpeechSettings _settings;
    private volatile string? _error;
    private IReadOnlyList<VoiceInfo> _voices = [];
    private IReadOnlyList<AudioDeviceInfo> _devices = [];
    private AlertSeverity? _speaking;
    private CancellationTokenSource? _currentCts;
    private string? _lastSpoken;

    public CalloutSpeaker(IOptions<KopilotOptions> options, AlertBus bus, MonitoringService monitoring, ILogger<CalloutSpeaker> log)
    {
        _bus = bus;
        _monitoring = monitoring;
        _log = log;
        _store = new SpeechSettingsStore(Path.Combine(options.Value.ResolveDataDirectory(), "speech.json"));
        _settings = (_store.Load() ?? SpeechSettings.FromOptions(options.Value.Speech, options.Value.Monitoring)).Normalized();
        _monitoring.AircraftAutoCallouts = _settings.AircraftAutoCallouts;
    }

    public SpeechStatus Status => new(
        _speech is not null,
        _error,
        _settings,
        _speech?.ActiveVoice,
        _voices,
        _devices,
        _speaking is not null,
        _lastSpoken);

    public async Task<SpeechStatus> RefreshAsync(CancellationToken ct)
    {
        if (_speech is not null)
        {
            try
            {
                _voices = WindowsSpeech.Voices();
                _devices = await WindowsSpeech.DevicesAsync();
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

        await ApplyAsync(ct);
        return await RefreshAsync(ct);
    }

    public async Task SpeakNowAsync(string text, CancellationToken ct)
    {
        if (_speech is null)
        {
            throw new InvalidOperationException(_error ?? "Sprachausgabe ist nicht verfügbar.");
        }

        await _engineLock.WaitAsync(ct);
        try
        {
            await _speech.SpeakAsync(text, ct);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _speech = new WindowsSpeech();
            await ApplyAsync(stoppingToken);
            await RefreshAsync(stoppingToken);
            if (!_speech.HasEnglishVoice)
            {
                _error = "Keine englische Stimme installiert. Windows-Einstellungen → Zeit und Sprache → Sprache: Englisch (Vereinigtes Königreich) mit Sprachausgabe hinzufügen.";
                _log.LogWarning("{Error}", _error);
            }

            _log.LogInformation("Sprachausgabe bereit: {Voice}", _speech.ActiveVoice ?? "keine Stimme");
        }
        catch (Exception ex)
        {
            _error = $"Windows-Sprachausgabe nicht verfügbar: {ex.Message}";
            _log.LogWarning("{Error}", _error);
            _speech = null;
            return;
        }

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
            if (_queue.Take(DateTimeOffset.UtcNow) is not { } next || _speech is null || !_settings.Enabled)
            {
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts = cts;
            _speaking = next.Severity;
            await _engineLock.WaitAsync(ct);
            try
            {
                await _speech.SpeakAsync(next.Callout!, cts.Token);
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
                _engineLock.Release();
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

    public override void Dispose()
    {
        base.Dispose();
        _speech?.Dispose();
        _signal.Dispose();
        _engineLock.Dispose();
    }
}
