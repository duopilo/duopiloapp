using System.Text.Json.Serialization;
using Kopilot.Monitoring;
using Microsoft.Extensions.Options;

namespace Kopilot.Speech;

[JsonConverter(typeof(JsonStringEnumConverter<SpeechState>))]
public enum SpeechState
{
    /// <summary>Per Konfiguration aus (Kopilot:Speech:Enabled = false).</summary>
    Off,

    /// <summary>Modell wird geladen.</summary>
    Loading,

    /// <summary>Modell wird heruntergeladen (erster Start).</summary>
    Downloading,

    Ready,

    /// <summary>Laden fehlgeschlagen – das Dashboard liest dann im Browser vor.</summary>
    Failed,
}

/// <summary>Zustand für Dashboard und Tray.</summary>
public sealed record SpeechStatus(
    bool Available,
    SpeechState State,
    bool Enabled,
    string Voice,
    IReadOnlyList<string> Voices,
    double? DownloadProgress,
    string? Error,
    string? Device,
    string? LastSpoken,
    DateTimeOffset? LastSpokenUtc);

/// <summary>
/// Spricht die Callouts des Monitorings selbst – der echte Push-Weg, unabhängig von Claude und Browser.
/// Hört auf den <see cref="AlertBus"/>, spricht nacheinander und hält sich an Cockpit-Logik:
/// Warnings unterbrechen alles andere, veraltete Callouts verfallen, doppelte warten nicht zweimal in der Schlange.
/// Die festen Callouts werden nach dem Laden vorab synthetisiert, damit sie ohne Verzögerung kommen.
/// </summary>
public sealed class CalloutSpeaker : BackgroundService
{
    private const int QueueLimit = 6;
    private const int CacheLimit = 128;

    private readonly SpeechOptions _options;
    private readonly AlertBus _bus;
    private readonly ISpeechEngine _engine;
    private readonly IAudioOutput _audio;
    private readonly SpeechSettingsStore _store;
    private readonly ILogger<CalloutSpeaker> _log;
    private readonly HashSet<string> _mutedRules;

    private readonly object _lock = new();
    private readonly List<Item> _pending = [];
    private readonly Queue<string> _prewarm = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Item? _current;
    private CancellationTokenSource? _currentCts;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, SpeechClip> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    private IReadOnlyList<string>? _voices;
    private volatile SpeechState _state;
    private volatile bool _enabled;
    private volatile string _voice;
    private volatile string? _error;
    private volatile string? _lastSpoken;
    private double _progress;
    private DateTimeOffset? _lastSpokenUtc;
    private DateTimeOffset _lastErrorLogUtc;

    public CalloutSpeaker(IOptions<KopilotOptions> options, AlertBus bus, ISpeechEngine engine, IAudioOutput audio, SpeechSettingsStore store, ILogger<CalloutSpeaker> log)
    {
        _options = options.Value.Speech;
        _bus = bus;
        _engine = engine;
        _audio = audio;
        _store = store;
        _log = log;
        _mutedRules = new HashSet<string>(_options.MutedRules, StringComparer.OrdinalIgnoreCase);

        var saved = _options.Enabled ? store.Load() : new SpeechSettings(null, null);
        _enabled = saved.Enabled ?? true;
        _voice = string.IsNullOrWhiteSpace(saved.Voice) ? _options.Voice : saved.Voice;
        _state = _options.Enabled ? SpeechState.Loading : SpeechState.Off;
    }

    /// <summary>Modul per Konfiguration eingeschaltet.</summary>
    public bool Available => _options.Enabled;

    public SpeechState State => _state;

    /// <summary>Laufzeit-Schalter (Dashboard/Tray). Aus = nichts sprechen, Modell bleibt geladen.</summary>
    public bool Enabled => _enabled;

    public string Voice => _voice;

    public SpeechStatus Status()
    {
        var state = _state;
        return new SpeechStatus(
            Available,
            state,
            _enabled,
            _voice,
            Available ? Voices() : [],
            state == SpeechState.Downloading ? Math.Round(Volatile.Read(ref _progress), 2) : null,
            _error,
            Available && state is not SpeechState.Failed ? _audio.DeviceName : null,
            _lastSpoken,
            _lastSpokenUtc);
    }

    /// <summary>Sprachausgabe an/aus. Aus bricht eine laufende Ansage ab.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Persist();
        _log.LogInformation("Sprachausgabe {State}.", enabled ? "an" : "aus");
        if (!enabled)
        {
            lock (_lock)
            {
                _pending.Clear();
                _currentCts?.Cancel();
            }
        }
        else if (_state == SpeechState.Ready)
        {
            Enqueue(Item.Test("Callouts on"));
        }
    }

    /// <summary>Stimme wechseln. Gibt eine Fehlermeldung zurück oder null. Spricht danach eine Probe.</summary>
    public string? SetVoice(string voice)
    {
        var match = Voices().FirstOrDefault(v => string.Equals(v, voice, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return $"Unbekannte Stimme \"{voice}\".";
        }

        _voice = match;
        Persist();
        _log.LogInformation("Stimme: {Voice}.", match);
        lock (_lock)
        {
            _prewarm.Clear();
            foreach (var phrase in CalloutText.Common)
            {
                _prewarm.Enqueue(phrase);
            }
        }

        if (_state == SpeechState.Ready)
        {
            Enqueue(Item.Test(CalloutText.TestPhrase));
        }

        _signal.Release();
        return null;
    }

    /// <summary>Test-Ansage, auch wenn die Sprachausgabe gerade aus ist. False, solange das Modell nicht bereit ist.</summary>
    public bool Test()
    {
        if (_state != SpeechState.Ready)
        {
            return false;
        }

        Enqueue(Item.Test(CalloutText.TestPhrase));
        return true;
    }

    /// <summary>Soll diese Meldung gesprochen werden? (Zustand, Schalter, Stufe, stummgeschaltete Regeln)</summary>
    public bool ShouldSpeak(AlertEvent e) =>
        _state is SpeechState.Loading or SpeechState.Downloading or SpeechState.Ready
        && _enabled
        && e.Kind != AlertKind.Clear
        && !string.IsNullOrWhiteSpace(e.Callout)
        && e.Severity >= _options.MinSeverity
        && !_mutedRules.Contains(e.Rule);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Eigene Sprachausgabe ist ausgeschaltet (Kopilot:Speech:Enabled).");
            return;
        }

        await Task.Yield();

        // Schon während des Ladens zuhören: kommt das Modell schnell, gehen frische Callouts nicht verloren.
        var listen = ListenAsync(_bus.LastId, stoppingToken);
        try
        {
            await _engine.LoadAsync(
                p =>
                {
                    Volatile.Write(ref _progress, p);
                    _state = SpeechState.Downloading;
                },
                stoppingToken);

            var voices = Voices();
            if (!voices.Contains(_voice, StringComparer.OrdinalIgnoreCase))
            {
                var fallback = voices.FirstOrDefault(v => string.Equals(v, _options.Voice, StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault()
                    ?? throw new InvalidOperationException("Keine englischen Kokoro-Stimmen im Ordner \"voices\" neben Kopilot.exe gefunden.");
                _log.LogWarning("Stimme \"{Voice}\" gibt es nicht – nutze {Fallback}.", _voice, fallback);
                _voice = fallback;
            }

            lock (_lock)
            {
                foreach (var phrase in CalloutText.Common)
                {
                    _prewarm.Enqueue(phrase);
                }
            }

            _error = null;
            _state = SpeechState.Ready;
            _log.LogInformation("Sprachausgabe bereit: Stimme {Voice}, Ausgabe {Device}{Muted}.", _voice, _audio.DeviceName, _enabled ? "" : " (stumm geschaltet)");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _state = SpeechState.Failed;
            lock (_lock)
            {
                _pending.Clear();
            }

            _log.LogError(ex, "Eigene Sprachausgabe nicht verfügbar – das Dashboard liest stattdessen im Browser vor.");
            await listen;
            return;
        }

        try
        {
            await Task.WhenAll(listen, PlayLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    private async Task ListenAsync(long sinceId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<AlertEvent> events;
            try
            {
                events = await _bus.WaitAsync(sinceId, TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            foreach (var e in events)
            {
                sinceId = Math.Max(sinceId, e.Id);
                if (ShouldSpeak(e))
                {
                    Enqueue(new Item(CalloutText.ForSpeech(e.Callout!), e.Severity, e.TimeUtc, IsTest: false));
                }
            }
        }
    }

    private void Enqueue(Item item)
    {
        if (item.Text.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            // Warning: alles Leisere fliegt raus, eine laufende leisere Ansage wird abgebrochen
            if (item.Severity == AlertSeverity.Warning)
            {
                _pending.RemoveAll(p => p.Severity != AlertSeverity.Warning);
                if (_current is { } current && current.Severity != AlertSeverity.Warning)
                {
                    _currentCts?.Cancel();
                }
            }

            if (_pending.Any(p => p.Text == item.Text))
            {
                return;
            }

            _pending.Add(item);
            while (_pending.Count > QueueLimit)
            {
                // Die älteste Meldung der niedrigsten Stufe verfällt
                _pending.Remove(_pending.OrderBy(p => p.Severity).First());
            }
        }

        _signal.Release();
    }

    private async Task PlayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Item? item;
            lock (_lock)
            {
                item = _pending.Count > 0 ? _pending[0] : null;
                if (item is not null)
                {
                    _pending.RemoveAt(0);
                }
            }

            if (item is not null)
            {
                await SpeakAsync(item, ct);
                continue;
            }

            if (await PrewarmOneAsync(ct))
            {
                continue;
            }

            await _signal.WaitAsync(ct);
        }
    }

    private async Task SpeakAsync(Item item, CancellationToken ct)
    {
        if (!item.IsTest && (!_enabled || IsStale(item)))
        {
            _log.LogDebug("Callout verworfen ({Reason}): {Text}", _enabled ? "zu alt" : "aus", item.Text);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
        {
            _current = item;
            _currentCts = cts;
        }

        try
        {
            var clip = await GetClipAsync(item.Text, cts.Token);
            if (!item.IsTest && IsStale(item))
            {
                return;
            }

            await _audio.PlayAsync(clip, (float)Math.Clamp(_options.Volume, 0, 1), cts.Token);
            _lastSpoken = item.Text;
            _lastSpokenUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Von einer Warning unterbrochen oder ausgeschaltet
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(ex, item.Text);
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
                _currentCts = null;
            }
        }
    }

    private async Task<bool> PrewarmOneAsync(CancellationToken ct)
    {
        string? phrase;
        lock (_lock)
        {
            if (_state != SpeechState.Ready || !_prewarm.TryDequeue(out phrase))
            {
                return false;
            }
        }

        try
        {
            await GetClipAsync(CalloutText.ForSpeech(phrase), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(ex, phrase);
            lock (_lock)
            {
                _prewarm.Clear();
            }
        }

        return true;
    }

    private async Task<SpeechClip> GetClipAsync(string text, CancellationToken ct)
    {
        var voice = _voice;
        var key = voice + "|" + text;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var speed = Math.Clamp(_options.Speed, 0.7, 1.4);
        var clip = await Task.Run(() => _engine.Synthesize(text, voice, speed), ct);
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

        ct.ThrowIfCancellationRequested();
        return clip;
    }

    private bool IsStale(Item item) =>
        DateTimeOffset.UtcNow - item.CreatedUtc > TimeSpan.FromSeconds(Math.Max(1, _options.MaxAgeSeconds));

    private IReadOnlyList<string> Voices() => _voices ??= _engine.Voices;

    private void Persist()
    {
        if (!_store.Save(new SpeechSettings(_enabled, _voice)))
        {
            _log.LogWarning("Einstellungen der Sprachausgabe konnten nicht gespeichert werden ({Path}).", _store.Path);
        }
    }

    private void LogError(Exception ex, string text)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorLogUtc > TimeSpan.FromMinutes(1))
        {
            _lastErrorLogUtc = now;
            _log.LogError(ex, "Sprachausgabe fehlgeschlagen bei \"{Text}\"", text);
        }
    }

    private sealed record Item(string Text, AlertSeverity Severity, DateTimeOffset CreatedUtc, bool IsTest)
    {
        public static Item Test(string text) => new(text, AlertSeverity.Info, DateTimeOffset.UtcNow, IsTest: true);
    }
}
