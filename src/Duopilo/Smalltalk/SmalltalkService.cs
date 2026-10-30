using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Kopilot.Localization;
using Microsoft.Extensions.Options;

namespace Kopilot.Smalltalk;

/// <summary>Eine Frage an den First Officer.</summary>
/// <param name="Text">Was der Pilot gesagt hat (Abschrift).</param>
/// <param name="Language">Sprache der Antwort ("de", "en").</param>
/// <param name="Name">Name des First Officers (null = ohne Namen).</param>
/// <param name="Context">Lesender Kontext (Flugphase, Position, Route, Sprit, Wetter).</param>
public sealed record SmalltalkQuestion(string Text, string Language, string? Name, SmalltalkContext Context);

/// <summary>Antwort: sprechbarer Text. Bei Fehlern der kurze Ersatz („I can't chat right now“) und der Grund (englisch, ohne Schlüssel).</summary>
public sealed record SmalltalkAnswer(bool Ok, string Text, string? Reason = null, int? ElapsedMs = null);

/// <summary>Stand für Dashboard und Status (nie der Schlüssel).</summary>
public sealed record SmalltalkStatus(
    string Provider,
    string? Model,
    string? Host,
    bool KeySet,
    string? Problem,
    int RequestsLastMinute,
    int RequestsPerMinute,
    DateTimeOffset? LastUtc,
    bool? LastOk,
    string? LastError,
    int? LastMs);

/// <summary>
/// Smalltalk mit einem Sprachmodell (optional, standardmäßig aus): nur für freie Gespräche und Erklärungen.
/// <list type="bullet">
/// <item>Das Modell bekommt nur lesenden Kontext und den kurzen Gesprächsverlauf – was es antwortet, wird nur gesprochen, nie
/// ausgeführt (die Sprachschleife wertet die Antwort nicht als Befehl aus).</item>
/// <item>Hängt nie: ohne Netz, über der Grenze pro Minute oder bei Fehlern sofort der kurze Ersatz; jede Anfrage hat ein Zeitlimit.</item>
/// <item>Der Schlüssel steht nur in appsettings.Local.json und im Speicher – Log, Status und Fehlermeldungen sind bereinigt.</item>
/// </list>
/// Einstellungen gelten sofort (<see cref="Update"/> aus der Einstellungsseite). Threadsicher.
/// </summary>
public sealed partial class SmalltalkService
{
    /// <summary>So viele Beiträge (Pilot und First Officer) gehen als Verlauf mit – genug für Rückfragen, wenig Kosten.</summary>
    public const int HistoryTurns = 6;

    /// <summary>Nach so langer Pause beginnt das Gespräch neu.</summary>
    public static readonly TimeSpan HistoryExpiry = TimeSpan.FromMinutes(10);

    private readonly IChatModel _model;
    private readonly ILogger<SmalltalkService> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _timeoutOverride;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly List<ChatTurn> _history = [];
    private volatile SmalltalkConfig _config;
    private volatile FirstOfficerConfig _firstOfficer;
    private DateTimeOffset? _lastChatUtc;
    private DateTimeOffset? _lastUtc;
    private bool? _lastOk;
    private string? _lastError;
    private int? _lastMs;

    public SmalltalkService(IOptions<KopilotOptions> options, ILogger<SmalltalkService> log, IChatModel? model = null, TimeProvider? time = null, TimeSpan? timeout = null)
    {
        _config = SmalltalkConfig.From(options.Value.Smalltalk);
        _firstOfficer = FirstOfficerConfig.From(options.Value.FirstOfficer);
        _model = model ?? new HttpChatModel();
        _log = log;
        _time = time ?? TimeProvider.System;
        _timeoutOverride = timeout;
    }

    /// <summary>Einstellungen geändert (Dashboard) – Sprachschleife übernimmt Name und Stimme sofort.</summary>
    public event Action? Changed;

    public SmalltalkConfig Config => _config;

    public FirstOfficerConfig FirstOfficer => _firstOfficer;

    /// <summary>Neue Einstellungen übernehmen (null = unverändert). Der Verlauf beginnt neu, wenn sich Anbieter oder Modell ändern.</summary>
    public void Update(SmalltalkConfig? config, FirstOfficerConfig? firstOfficer)
    {
        if (config is not null)
        {
            var old = _config;
            _config = config;
            if (old.Provider != config.Provider || old.EffectiveModel != config.EffectiveModel)
            {
                ResetHistory();
            }

            _log.LogInformation(Loc.Log("log.smalltalk.configured"), config.ToString());
        }

        if (firstOfficer is not null)
        {
            _firstOfficer = firstOfficer;
        }

        Changed?.Invoke();
    }

    /// <summary>Gesprächsverlauf vergessen (neuer Flug, neuer First Officer).</summary>
    public void ResetHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _lastChatUtc = null;
        }
    }

    public SmalltalkStatus Status
    {
        get
        {
            var c = _config;
            lock (_gate)
            {
                Prune(_time.GetUtcNow());
                return new SmalltalkStatus(
                    c.Provider,
                    c.Enabled ? c.EffectiveModel : null,
                    c.Enabled ? c.Host : null,
                    c.HasApiKey,
                    c.Problem(),
                    _requests.Count,
                    c.RequestsPerMinute,
                    _lastUtc,
                    _lastOk,
                    _lastError,
                    _lastMs);
            }
        }
    }

    /// <summary>Ersatz, wenn das Modell nicht antworten kann – sofort, statt zu hängen.</summary>
    public static string Unavailable(string language) =>
        language == "en" ? Loc.En("smalltalk.reply.unavailable") : Loc.T("smalltalk.reply.unavailable");

    /// <summary>
    /// Frage an das Modell. Wirft nie (außer bei Abbruch durch <paramref name="ct"/>): Fehler, Zeitlimit, kein Netz und die Grenze pro
    /// Minute ergeben <see cref="Unavailable"/> mit dem Grund.
    /// </summary>
    public async Task<SmalltalkAnswer> AskAsync(SmalltalkQuestion question, CancellationToken ct)
    {
        var config = _config;
        if (!config.Enabled)
        {
            return Fail(question, config, "off", record: false);
        }

        if (config.Problem() is { } problem)
        {
            return Fail(question, config, Loc.ToEnglish(problem) ?? problem);
        }

        if (!IsLocal(config.Host) && !NetworkAvailable())
        {
            return Fail(question, config, "no network");
        }

        List<ChatTurn> messages;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (_requests.Count >= config.RequestsPerMinute)
            {
                return Fail(question, config, $"limit of {config.RequestsPerMinute} requests per minute reached", recordLocked: true);
            }

            _requests.Enqueue(now);
            if (_lastChatUtc is { } last && now - last > HistoryExpiry)
            {
                _history.Clear();
            }

            messages = [.. _history, new ChatTurn(ChatTurn.User, question.Text.Trim())];
        }

        var system = SmalltalkPrompt.System(question.Name, Kopilot.Speech.SmalltalkVoices.LanguageName(question.Language), config.MaxReplyWords, question.Context);
        var request = new ChatRequest(system, messages, config.MaxTokens);
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutOverride ?? TimeSpan.FromSeconds(config.TimeoutSec));
        string raw;
        try
        {
            raw = await _model.CompleteAsync(config, request, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(question, config, $"no answer within {(_timeoutOverride ?? TimeSpan.FromSeconds(config.TimeoutSec)).TotalSeconds:0.#} s", ms: (int)watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return Fail(question, config, Redact(Loc.ToEnglish(ex.Message) ?? ex.Message, config.ApiKey), ms: (int)watch.ElapsedMilliseconds);
        }

        var ms = (int)watch.ElapsedMilliseconds;
        var text = SmalltalkPrompt.Clean(raw, config.MaxReplyWords);
        if (text.Length == 0)
        {
            return Fail(question, config, "empty answer", ms: ms);
        }

        lock (_gate)
        {
            _history.Add(new ChatTurn(ChatTurn.User, question.Text.Trim()));
            _history.Add(new ChatTurn(ChatTurn.Assistant, text));
            while (_history.Count > HistoryTurns)
            {
                _history.RemoveAt(0);
            }

            _lastChatUtc = _time.GetUtcNow();
            _lastUtc = _lastChatUtc;
            _lastOk = true;
            _lastError = null;
            _lastMs = ms;
        }

        _log.LogInformation(Loc.Log("log.smalltalk.answer"), SmalltalkProviders.DisplayName(config.Provider), config.EffectiveModel, ms, Words(question.Text), Words(text), question.Language);
        _log.LogDebug(Loc.Log("log.smalltalk.answerText"), text);
        return new SmalltalkAnswer(true, text, null, ms);
    }

    /// <summary>Schlüssel und alles, was wie ein API-Schlüssel aussieht, aus einem Text entfernen.</summary>
    public static string Redact(string? text, string? apiKey)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        if (!string.IsNullOrEmpty(apiKey) && apiKey.Length >= 6)
        {
            text = text.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return KeyLike().Replace(text, "$1$2***");
    }

    private SmalltalkAnswer Fail(SmalltalkQuestion question, SmalltalkConfig config, string reason, bool record = true, int? ms = null, bool recordLocked = false)
    {
        reason = Redact(reason, config.ApiKey);
        if (recordLocked)
        {
            Record(reason, ms);
        }
        else if (record)
        {
            lock (_gate)
            {
                Record(reason, ms);
            }
        }

        if (record || recordLocked)
        {
            _log.LogWarning(Loc.Log("log.smalltalk.failed"), SmalltalkProviders.DisplayName(config.Provider), config.EffectiveModel, reason);
        }

        return new SmalltalkAnswer(false, Unavailable(question.Language), reason, ms);
    }

    private void Record(string reason, int? ms)
    {
        _lastUtc = _time.GetUtcNow();
        _lastOk = false;
        _lastError = reason;
        _lastMs = ms;
    }

    private void Prune(DateTimeOffset now)
    {
        while (_requests.Count > 0 && now - _requests.Peek() >= TimeSpan.FromMinutes(1))
        {
            _requests.Dequeue();
        }
    }

    private static int Words(string? text) => (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Rechner im eigenen PC (Ollama auf localhost) – dann ist „kein Netz“ kein Grund.</summary>
    private static bool IsLocal(string? host) =>
        host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1");

    private static bool NetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    // sk-…, sk-ant-…, Bearer … – falls ein Anbieter den Schlüssel in der Fehlermeldung wiederholt
    [GeneratedRegex(@"\b((?:sk|key|pk)-(?:[A-Za-z]+-)?)[A-Za-z0-9_\-]{8,}|(Bearer\s+)[A-Za-z0-9._\-]{8,}")]
    private static partial Regex KeyLike();
}
