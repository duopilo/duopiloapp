using System.Security.Cryptography;
using Kopilot.Aircraft;
using Kopilot.Monitoring;
using Microsoft.Extensions.Options;

namespace Kopilot.Input;

/// <summary>Zugriff auf den Sim für das Eingabe-Modul – austauschbar für Tests.</summary>
public interface IInputBackend
{
    bool Connected { get; }

    AircraftProfile? Profile { get; }

    FlightSnapshot Snapshot();

    Task<double?> ReadAsync(string name, string unit, CancellationToken ct);

    Task SendEventAsync(string name, uint data, CancellationToken ct);

    Task SetAsync(string name, string unit, double value, CancellationToken ct);
}

/// <summary>Antwort an Claude bzw. das Dashboard.</summary>
public sealed record InputResult(
    string Status,
    string? Reason = null,
    string? Token = null,
    string? Action = null,
    string? Value = null,
    string? Readback = null,
    int? ExpiresInSec = null,
    bool? Verified = null,
    bool? Confirmed = null,
    double? StateAfter = null,
    int? Skipped = null)
{
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Executed = "executed";
    public const string Refused = "refused";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Disabled = "disabled";
}

/// <summary>
/// Eingabe-Modul: Aktion vorbereiten (Readback), nach Bestätigung des Piloten ausführen, danach prüfen.
/// Es gibt immer höchstens eine offene Aktion; sie verfällt nach <see cref="InputOptions.ConfirmWindowSeconds"/>.
/// Vor dem Ausführen werden Freigabe, Flugzeug und Sperren noch einmal geprüft.
/// </summary>
public sealed class InputService
{
    private const int MaxLog = 40;

    private static readonly string[] YesWords =
    [
        "confirm", "confirmed", "affirm", "affirmative", "yes", "correct", "approved", "proceed",
        "bestätigt", "bestätige", "bestätigen", "ja", "jawohl", "korrekt", "richtig", "ok", "okay",
    ];

    private static readonly string[] NoWords =
    [
        "no", "not", "negative", "cancel", "disregard", "wrong", "stop", "abort",
        "nein", "nicht", "abbrechen", "abbruch", "falsch", "halt",
    ];

    private readonly IInputBackend _backend;
    private readonly InputOptions _options;
    private readonly InputSettingsStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<InputService> _log;
    private readonly object _lock = new();
    private readonly LinkedList<InputLogEntry> _history = new();
    private readonly SemaphoreSlim _execution = new(1, 1);

    private PendingAction? _pending;
    private string? _pendingInput;
    private string? _pendingProfile;

    public InputService(IOptions<KopilotOptions> options, IInputBackend backend, ILogger<InputService> log, TimeProvider? time = null)
    {
        _backend = backend;
        _options = options.Value.Input;
        _store = new InputSettingsStore(Path.Combine(options.Value.ResolveDataDirectory(), "input.json"));
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Eingaben erlaubt? Dashboard-Schalter (input.json) vor appsettings.</summary>
    public bool Enabled => _store.Load()?.Enabled ?? _options.Enabled;

    public TimeSpan ConfirmWindow => TimeSpan.FromSeconds(Math.Clamp(_options.ConfirmWindowSeconds, 10, 300));

    public PendingAction? Pending
    {
        get
        {
            lock (_lock)
            {
                ExpireLocked();
                return _pending;
            }
        }
    }

    public IReadOnlyList<InputLogEntry> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>Aktionen des aktuellen Flugzeugs (leer ohne Profil).</summary>
    public IReadOnlyList<ProfileAction> Actions => _backend.Profile?.Actions ?? [];

    public void SetEnabled(bool enabled)
    {
        _store.Save(new InputSettings { Enabled = enabled });
        _log.LogInformation("Eingabe {State} (Dashboard).", enabled ? "freigegeben" : "gesperrt");
        if (!enabled)
        {
            Cancel(null, "Eingabe gesperrt");
        }
    }

    // ------------------------------------------------------------------ Schritt 1: vorbereiten

    public async Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new InputResult(InputResult.Disabled, "Eingaben sind gesperrt. Freigabe im Kopilot-Dashboard (Eingabe) oder Kopilot:Input:Enabled.");
        }

        if (Precheck() is { } problem)
        {
            return Refuse(actionId, value, problem);
        }

        var profile = _backend.Profile!;
        var action = profile.Actions.FirstOrDefault(a => a.Id.Equals(actionId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            return Refuse(actionId, value, $"Aktion \"{actionId}\" gibt es für {profile.Name} nicht – siehe list_actions.");
        }

        var result = ActionPlanner.Plan(action, value, profile.Limits, _backend.Snapshot());
        if (result.Plan is not { } plan)
        {
            return Refuse(action.Id, value, result.Refusal!);
        }

        if (await CheckRequirementsAsync(plan, ct) is { } unmet)
        {
            return Refuse(action.Id, plan.ValueText, unmet);
        }

        var now = _time.GetUtcNow();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        lock (_lock)
        {
            if (_pending is { } old)
            {
                AddLogLocked(old.Plan.Action.Id, old.Plan.ValueText, "ersetzt", "Neue Aktion vorbereitet", null);
            }

            _pending = new PendingAction(token, plan, now, now + ConfirmWindow);
            _pendingInput = value;
            _pendingProfile = profile.Name;
            AddLogLocked(plan.Action.Id, plan.ValueText, "vorbereitet", plan.Readback, null);
        }

        return new InputResult(
            InputResult.AwaitingConfirmation,
            Token: token,
            Action: plan.Action.Id,
            Value: plan.ValueText,
            Readback: plan.Readback,
            ExpiresInSec: (int)ConfirmWindow.TotalSeconds,
            Verified: plan.Action.Verified);
    }

    // ------------------------------------------------------------------ Schritt 2: bestätigen und ausführen

    public async Task<InputResult> ConfirmAsync(string token, string? pilotConfirmation, CancellationToken ct)
    {
        PendingAction pending;
        string? input;
        string? profileName;
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is null)
            {
                return new InputResult(InputResult.Failed, "Keine offene Aktion – abgelaufen oder schon ausgeführt. Neu mit prepare_action.");
            }

            if (!string.Equals(_pending.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new InputResult(InputResult.Failed, "Token passt nicht zur offenen Aktion.");
            }

            var words = Words(pilotConfirmation);
            if (words.Overlaps(NoWords))
            {
                var cancelled = _pending;
                _pending = null;
                AddLogLocked(cancelled.Plan.Action.Id, cancelled.Plan.ValueText, "abgebrochen", $"Pilot: \"{pilotConfirmation}\"", null);
                return new InputResult(InputResult.Cancelled, "Pilot hat nicht bestätigt – Aktion verworfen.", Action: cancelled.Plan.Action.Id);
            }

            if (!words.Overlaps(YesWords))
            {
                return new InputResult(InputResult.Failed, "Keine klare Bestätigung des Piloten. Readback vorlesen und auf \"confirm\" warten.");
            }

            if (_time.GetUtcNow() - _pending.CreatedUtc < TimeSpan.FromSeconds(1))
            {
                return new InputResult(InputResult.Failed, "Erst den Readback vorlesen und die Antwort des Piloten abwarten.");
            }

            pending = _pending;
            input = _pendingInput;
            profileName = _pendingProfile;
            _pending = null;
        }

        var action = pending.Plan.Action;
        if (!Enabled)
        {
            return Refuse(action.Id, pending.Plan.ValueText, "Eingaben wurden inzwischen gesperrt.");
        }

        if (Precheck() is { } problem)
        {
            return Refuse(action.Id, pending.Plan.ValueText, problem);
        }

        if (!string.Equals(_backend.Profile!.Name, profileName, StringComparison.Ordinal))
        {
            return Refuse(action.Id, pending.Plan.ValueText, "Flugzeug hat gewechselt – Aktion verworfen.");
        }

        // Sperren mit dem aktuellen Zustand erneut prüfen (Fahrt/Boden können sich seit dem Readback geändert haben)
        var fresh = ActionPlanner.Plan(action, input, _backend.Profile.Limits, _backend.Snapshot());
        if (fresh.Plan is not { } plan)
        {
            return Refuse(action.Id, pending.Plan.ValueText, fresh.Refusal!);
        }

        await _execution.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(plan, ct);
        }
        finally
        {
            _execution.Release();
        }
    }

    public InputResult Cancel(string? token, string reason = "Abgebrochen")
    {
        lock (_lock)
        {
            if (_pending is null || (token is not null && !string.Equals(_pending.Token, token.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return new InputResult(InputResult.Cancelled, "Keine passende offene Aktion.");
            }

            var cancelled = _pending;
            _pending = null;
            AddLogLocked(cancelled.Plan.Action.Id, cancelled.Plan.ValueText, "abgebrochen", reason, null);
            return new InputResult(InputResult.Cancelled, Action: cancelled.Plan.Action.Id, Value: cancelled.Plan.ValueText);
        }
    }

    // ------------------------------------------------------------------ intern

    private async Task<string?> CheckRequirementsAsync(ActionPlan plan, CancellationToken ct)
    {
        foreach (var requirement in plan.Requires)
        {
            var state = await _backend.ReadAsync(requirement.Variable, requirement.Unit, ct);
            if (state is null)
            {
                return $"Zustand {requirement.Variable} nicht lesbar.";
            }

            if (!requirement.Matches(state.Value))
            {
                return requirement.Message;
            }
        }

        return null;
    }

    private async Task<InputResult> ExecuteAsync(ActionPlan plan, CancellationToken ct)
    {
        if (await CheckRequirementsAsync(plan, ct) is { } unmet)
        {
            return Refuse(plan.Action.Id, plan.ValueText, unmet);
        }

        var skipped = 0;
        var sent = 0;
        try
        {
            foreach (var step in plan.Steps)
            {
                if (step.SkipIf is { } condition)
                {
                    var state = await _backend.ReadAsync(condition.Variable, condition.Unit, ct);
                    if (state is null)
                    {
                        return Fail(plan, $"Zustand {condition.Variable} nicht lesbar.", executedAny: sent > 0);
                    }

                    if (condition.Matches(state.Value))
                    {
                        skipped++;
                        continue;
                    }
                }

                if (step.Event is { } name)
                {
                    await _backend.SendEventAsync(name, step.EventData, ct);
                }
                else
                {
                    await _backend.SetAsync(step.Set!, step.Unit, step.SetValue, ct);
                }

                sent++;
                if (step.DelayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(step.DelayMs), _time, ct);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Fail(plan, ex.Message, executedAny: sent > 0);
        }

        bool? verified = null;
        double? after = null;
        if (plan.Check is { } check)
        {
            (verified, after) = await VerifyAsync(check, plan.CheckTimeoutMs, ct);
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var detail = verified switch
        {
            true => "vom Flugzeug bestätigt",
            false => $"Flugzeug zeigt {after?.ToString(inv) ?? "nichts"} statt {plan.Check!.Value.ToString(inv)}",
            null => skipped == plan.Steps.Count ? "war schon so" : null,
        };

        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, "ausgeführt", detail, verified);
        }

        return new InputResult(
            InputResult.Executed,
            Reason: verified == false ? "Das Flugzeug hat den Wert (noch) nicht übernommen – bitte im Cockpit prüfen." : null,
            Action: plan.Action.Id,
            Value: plan.ValueText,
            Readback: plan.Readback,
            Verified: plan.Action.Verified,
            Confirmed: verified,
            StateAfter: after,
            Skipped: skipped);
    }

    private async Task<(bool? Ok, double? Value)> VerifyAsync(ResolvedCondition check, int timeoutMs, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs <= 0 ? 3000 : timeoutMs);
        double? last = null;
        while (true)
        {
            last = await _backend.ReadAsync(check.Variable, check.Unit, ct) ?? last;
            if (last is { } v && check.Matches(v))
            {
                return (true, v);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return (last is null ? null : false, last);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
        }
    }

    private string? Precheck()
    {
        if (!_backend.Connected)
        {
            return "Keine Verbindung zu MSFS.";
        }

        if (_backend.Profile is null)
        {
            return "Für dieses Flugzeug gibt es kein Profil und damit keine Eingaben.";
        }

        var s = _backend.Snapshot();
        if (s.FlightRunning == false)
        {
            return "Kein Flug geladen.";
        }

        if (s.DataAgeSec is not { } age || age > 5)
        {
            return "Sim-Daten veraltet – Eingabe gesperrt.";
        }

        return null;
    }

    private InputResult Refuse(string? action, string? value, string reason)
    {
        lock (_lock)
        {
            AddLogLocked(action ?? "?", value, "abgelehnt", reason, null);
        }

        return new InputResult(InputResult.Refused, reason, Action: action, Value: value);
    }

    private InputResult Fail(ActionPlan plan, string reason, bool executedAny)
    {
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, "fehlgeschlagen", reason, null);
        }

        return new InputResult(InputResult.Failed, executedAny ? reason + " Teilweise ausgeführt – bitte im Cockpit prüfen." : reason, Action: plan.Action.Id, Value: plan.ValueText);
    }

    private void ExpireLocked()
    {
        if (_pending is { } p && _time.GetUtcNow() > p.ExpiresUtc)
        {
            _pending = null;
            AddLogLocked(p.Plan.Action.Id, p.Plan.ValueText, "abgelaufen", "Keine Bestätigung", null);
        }
    }

    private void AddLogLocked(string action, string? value, string outcome, string? detail, bool? verified)
    {
        _history.AddFirst(new InputLogEntry(_time.GetUtcNow(), action, value, outcome, detail, verified));
        while (_history.Count > MaxLog)
        {
            _history.RemoveLast();
        }

        _log.LogInformation("Eingabe {Outcome}: {Action}{Value} – {Detail}", outcome, action, value is null ? "" : " = " + value, detail ?? "");
    }

    private static HashSet<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.ToLowerInvariant()
                .Split([' ', ',', '.', '!', '?', ';', ':', '-', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
}
