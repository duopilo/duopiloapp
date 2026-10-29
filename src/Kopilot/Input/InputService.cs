using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    int? Skipped = null,
    bool AlreadySet = false,
    bool? CheckIndependent = null,
    string? StateAfterText = null)
{
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Executed = "executed";
    public const string Refused = "refused";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Disabled = "disabled";

    /// <summary>Unerwarteter Fehler (Ausnahme) – Details im Log.</summary>
    public const string Error = "error";
}

/// <summary>
/// Eingabe-Modul: Aktion vorbereiten (Readback), nach Bestätigung des Piloten ausführen, danach prüfen.
/// Es gibt immer höchstens eine offene Aktion; sie verfällt nach <see cref="InputOptions.ConfirmWindowSeconds"/>.
/// Unmittelbar vor dem Senden (unter der Ausführungssperre) werden Freigabe, Flugzeug, Sperren und Bedingungen erneut geprüft.
/// </summary>
public sealed partial class InputService
{
    private const int MaxLog = 40;

    private const int MaxRetiredTokens = 20;

    /// <summary>Höchstdauer einer Ausführung samt Prüfung – läuft nach der Bestätigung unabhängig vom MCP-Aufruf zu Ende.</summary>
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> YesWords = new(StringComparer.Ordinal)
    {
        "confirm", "confirmed", "affirm", "affirmative", "yes", "correct", "approved", "proceed",
        "bestätigt", "bestätige", "bestätigen", "ja", "jawohl", "korrekt", "richtig", "ok", "okay",
    };

    private static readonly HashSet<string> NoWords = new(StringComparer.Ordinal)
    {
        "no", "not", "negative", "cancel", "disregard", "wrong", "stop", "abort", "wait",
        "nein", "nicht", "abbrechen", "abbruch", "falsch", "halt", "warte",
    };

    /// <summary>
    /// Für mark_action_verified zusätzlich zu <see cref="YesWords"/>: Der Pilot meldet den Zustand, den er im Cockpit sieht
    /// ("Seatbelts on", "APU avail", "läuft"). Gilt nur fürs Merken als geprüft, nie fürs Ausführen (confirm_action bleibt streng).
    /// </summary>
    private static readonly HashSet<string> StateWords = new(StringComparer.Ordinal)
    {
        "check", "checked", "checks", "on", "off", "auto", "avail", "available", "running", "runs", "works", "worked", "working",
        "set", "armed", "engaged", "open", "closed", "good", "fine", "roger",
        "läuft", "laeuft", "funktioniert", "geht", "an", "aus", "steht", "passt", "gut", "drin",
    };

    /// <summary>Für mark_action_verified zusätzlich zu <see cref="NoWords"/>: Der Pilot meldet, dass es nicht geklappt hat.</summary>
    private static readonly HashSet<string> FailWords = new(StringComparer.Ordinal)
    {
        "didn", "doesn", "isn", "wasn", "won", "fail", "failed", "error", "broken", "nope", "never", "still",
        "kaputt", "fehler", "nee", "nö", "noch",
    };

    private readonly IInputBackend _backend;
    private readonly InputOptions _options;
    private readonly InputSettingsStore _store;
    private readonly VerifiedActionStore _verified;
    private readonly TimeProvider _time;
    private readonly ILogger<InputService> _log;
    private readonly object _lock = new();
    private readonly LinkedList<InputLogEntry> _history = new();
    private readonly SemaphoreSlim _execution = new(1, 1);

    private PendingAction? _pending;
    private string? _pendingInput;
    private AircraftProfile? _pendingProfile;
    private long _pendingEpoch;

    /// <summary>Zählt prepare_action-Aufrufe. Nur der zuletzt begonnene darf eine Aktion anlegen.</summary>
    private long _prepareSequence;

    /// <summary>Zuletzt beendete Tokens (neueste zuerst) – nur für klare Fehlermeldungen bei confirm_action.</summary>
    private readonly LinkedList<RetiredToken> _retired = new();

    private sealed record RetiredToken(string Token, string Summary, string Fate);

    public InputService(IOptions<KopilotOptions> options, IInputBackend backend, ILogger<InputService> log, TimeProvider? time = null)
    {
        _backend = backend;
        _options = options.Value.Input;
        _store = new InputSettingsStore(options.Value.ResolveDataDirectory());
        _verified = new VerifiedActionStore(options.Value.ResolveDataDirectory());
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Eingaben erlaubt? Dashboard-Schalter (input.json) vor appsettings.</summary>
    public bool Enabled => _store.Load()?.Enabled ?? _options.Enabled;

    private long Epoch => _store.Load()?.Epoch ?? 0;

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

    /// <summary>Offene Aktion einer anderen Kopilot-Instanz (z. B. stdio für Claude Desktop), sonst null.</summary>
    public SharedPending? ForeignPending =>
        _store.LoadPending() is { } p && p.ProcessId != Environment.ProcessId && p.ExpiresUtc > _time.GetUtcNow() ? p : null;

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

    /// <summary>
    /// Geprüft? "profile" = im Profil markiert, "sim"/"pilot" = in verified-actions.json mit passender Definition, null = ungeprüft.
    /// </summary>
    public string? VerifiedBy(ProfileAction action)
    {
        if (action.Verified)
        {
            return "profile";
        }

        return _backend.Profile is { } profile ? _verified.Find(profile.Name, action)?.Source : null;
    }

    /// <summary>
    /// Der Pilot bestätigt, dass eine gerade ausgeführte Aktion im Cockpit wirklich funktioniert hat – für Aktionen,
    /// bei denen Kopilot nur die Schalterstellung lesen kann. Geht nur nach einer Ausführung in den letzten 15 Minuten.
    /// </summary>
    public InputResult MarkVerified(string? actionId, string? pilotConfirmation)
    {
        var profile = _backend.Profile;
        var action = profile?.Actions.FirstOrDefault(a => a.Id.Equals(actionId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null || action is null)
        {
            return new InputResult(InputResult.Refused, $"Aktion \"{actionId}\" gibt es für dieses Flugzeug nicht.", Action: actionId);
        }

        var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(15);
        var executed = History.FirstOrDefault(e => e.Action.Equals(action.Id, StringComparison.OrdinalIgnoreCase) && e.Outcome == "ausgeführt" && e.TimeUtc >= cutoff);

        if (!PilotConfirmsWorked(action, pilotConfirmation, executed?.Value))
        {
            return new InputResult(
                InputResult.Refused,
                "Nur speichern, wenn der Pilot klar bestätigt, dass es im Cockpit funktioniert hat (z. B. \"check\", \"confirm\", \"on\", \"avail\", \"läuft\" oder der Name der Aktion) – ohne Verneinung.",
                Action: action.Id);
        }

        if (executed is null)
        {
            return new InputResult(InputResult.Refused, "Diese Aktion wurde in den letzten 15 Minuten nicht ausgeführt – erst ausführen, dann im Cockpit prüfen.", Action: action.Id);
        }

        if (executed.Verified == false)
        {
            return new InputResult(InputResult.Refused, "Das Flugzeug hat den Wert bei der letzten Ausführung nicht übernommen – erst die Ursache klären.", Action: action.Id);
        }

        _verified.Record(profile.Name, action, "pilot", executed.Value, _time.GetUtcNow());
        lock (_lock)
        {
            AddLogLocked(action.Id, executed.Value, "geprüft", "vom Piloten im Cockpit bestätigt", true);
        }

        return new InputResult(InputResult.Executed, "Als geprüft gespeichert.", Action: action.Id, Value: executed.Value, Verified: true);
    }

    /// <summary>Schalter im Dashboard. Jede Änderung verwirft offene Aktionen in allen Instanzen.</summary>
    public void SetEnabled(bool enabled)
    {
        _store.Save(new InputSettings { Enabled = enabled, Epoch = Epoch + 1 });
        _log.LogInformation("Eingabe {State} (Dashboard).", enabled ? "freigegeben" : "gesperrt");
        Cancel(null, enabled ? "Eingabe freigegeben" : "Eingabe gesperrt");
    }

    /// <summary>Abbrechen im Dashboard: verwirft offene Aktionen in allen Kopilot-Instanzen.</summary>
    public InputResult CancelEverywhere(string reason)
    {
        _store.Save(new InputSettings { Enabled = Enabled, Epoch = Epoch + 1 });
        _store.SavePending(null);
        return Cancel(null, reason);
    }

    // ------------------------------------------------------------------ Schritt 1: vorbereiten

    public async Task<InputResult> PrepareAsync(string? actionId, string? value, CancellationToken ct)
    {
        try
        {
            return await PrepareCoreAsync(actionId ?? "", value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Der MCP-Aufruf wurde beendet (z. B. Zeitüberschreitung beim Client) – dann darf auch keine Aktion offen bleiben
            _log.LogWarning("Eingabe: prepare_action {Action} = {Value} vom Client abgebrochen", actionId, value);
            return new InputResult(InputResult.Failed, "Aufruf abgebrochen – es wurde keine Aktion vorbereitet. Neu mit prepare_action.", Action: actionId, Value: value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Eingabe: prepare_action {Action} = {Value} fehlgeschlagen", actionId, value);
            return new InputResult(InputResult.Error, $"Interner Fehler: {ex.Message}", Action: actionId, Value: value);
        }
    }

    private async Task<InputResult> PrepareCoreAsync(string actionId, string? value, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new InputResult(InputResult.Disabled, "Eingaben sind gesperrt. Freigabe im Kopilot-Dashboard (Eingabe) oder Kopilot:Input:Enabled.");
        }

        // Jede neue Anfrage verwirft die offene Aktion – auch wenn sie selbst scheitert. Ihr Readback gilt dann nicht mehr,
        // und ein älterer, langsamer Aufruf darf später keine Aktion mehr anlegen (siehe Nummer beim Anlegen).
        var sequence = Interlocked.Increment(ref _prepareSequence);
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is not null)
            {
                RetireLocked("ersetzt", "Neue Aktion angefordert");
            }
        }

        var profile = _backend.Profile;
        if (Precheck(profile) is { } problem)
        {
            return Refuse(actionId, value, problem);
        }

        var action = profile!.Actions.FirstOrDefault(a => a.Id.Equals(actionId?.Trim(), StringComparison.OrdinalIgnoreCase));
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
        var pending = new PendingAction(token, plan, now, now + ConfirmWindow);
        var epoch = Epoch;
        string? stale = null;
        lock (_lock)
        {
            // Nur die neueste Anfrage legt eine Aktion an: Der zurückgegebene Token gehört damit immer zur offenen Aktion.
            if (sequence != Interlocked.Read(ref _prepareSequence))
            {
                stale = "Inzwischen wurde eine neuere Aktion angefordert – diese Vorbereitung ist verworfen und hat keinen Token.";
            }
            else if (ct.IsCancellationRequested)
            {
                stale = "Aufruf abgebrochen – es wurde keine Aktion vorbereitet. Neu mit prepare_action.";
            }
            else
            {
                if (_pending is not null)
                {
                    RetireLocked("ersetzt", "Neue Aktion vorbereitet");
                }

                _pending = pending;
                _pendingInput = value;
                _pendingProfile = profile;
                _pendingEpoch = epoch;
                AddLogLocked(plan.Action.Id, plan.ValueText, "vorbereitet", plan.Readback, null);
                Share(pending);
            }

            if (stale is not null)
            {
                AddLogLocked(plan.Action.Id, plan.ValueText, "verworfen", stale, null);
            }
        }

        if (stale is not null)
        {
            return new InputResult(InputResult.Failed, stale, Action: plan.Action.Id, Value: plan.ValueText);
        }

        return new InputResult(
            InputResult.AwaitingConfirmation,
            Token: token,
            Action: plan.Action.Id,
            Value: plan.ValueText,
            Readback: plan.Readback,
            ExpiresInSec: (int)ConfirmWindow.TotalSeconds,
            Verified: VerifiedBy(plan.Action) is not null);
    }

    // ------------------------------------------------------------------ Schritt 2: bestätigen und ausführen

    public async Task<InputResult> ConfirmAsync(string? token, string? pilotConfirmation, CancellationToken ct)
    {
        PendingAction pending;
        string? input;
        AircraftProfile? pendingProfile;
        long pendingEpoch;
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is null)
            {
                return new InputResult(
                    InputResult.Failed,
                    TokenProblemLocked(token) ?? "Keine offene Aktion – abgelaufen oder schon ausgeführt. Neu mit prepare_action.");
            }

            if (!string.Equals(_pending.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new InputResult(
                    InputResult.Failed,
                    TokenProblemLocked(token)
                    ?? $"Token passt nicht zur offenen Aktion ({_pending.Plan.Summary}). Den Token aus der letzten prepare_action-Antwort verwenden.",
                    Action: _pending.Plan.Action.Id);
            }

            var words = Words(pilotConfirmation);
            if (words.Overlaps(NoWords))
            {
                var cancelled = RetireLocked("abgebrochen", $"Pilot: \"{pilotConfirmation}\"");
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

            input = _pendingInput;
            pendingProfile = _pendingProfile;
            pendingEpoch = _pendingEpoch;
            pending = TakeLocked("schon mit confirm_action verwendet");
        }

        // Ab hier läuft die Aktion zu Ende, auch wenn der MCP-Aufruf abgebrochen wird – sonst bliebe sie halb ausgeführt.
        using var timeout = new CancellationTokenSource(ExecutionTimeout, _time);
        await _execution.WaitAsync(CancellationToken.None);
        try
        {
            // Alles unmittelbar vor dem Senden prüfen: Eine vorherige Aktion kann die Sperre einige Sekunden gehalten haben.
            var action = pending.Plan.Action;
            if (!Enabled)
            {
                return Refuse(action.Id, pending.Plan.ValueText, "Eingaben wurden inzwischen gesperrt.");
            }

            if (Epoch != pendingEpoch)
            {
                return Refuse(action.Id, pending.Plan.ValueText, "Im Dashboard abgebrochen.");
            }

            var profile = _backend.Profile;
            if (Precheck(profile) is { } problem)
            {
                return Refuse(action.Id, pending.Plan.ValueText, problem);
            }

            if (!ReferenceEquals(profile, pendingProfile))
            {
                return Refuse(action.Id, pending.Plan.ValueText, "Flugzeug oder Profil hat gewechselt – bitte neu vorbereiten.");
            }

            var fresh = ActionPlanner.Plan(action, input, profile!.Limits, _backend.Snapshot());
            if (fresh.Plan is not { } plan)
            {
                return Refuse(action.Id, pending.Plan.ValueText, fresh.Refusal!);
            }

            return await ExecuteAsync(plan, profile.Name, timeout.Token);
        }
        catch (Exception ex)
        {
            return ErrorResult(pending.Plan, ex, executedAny: true);
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

            var cancelled = RetireLocked("abgebrochen", reason);
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

    private async Task<InputResult> ExecuteAsync(ActionPlan plan, string profileName, CancellationToken ct)
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
            // Bekannte Fälle: Verbindung weg, Event/Variable gesperrt, MSFS antwortet nicht
            return Fail(plan, ex.Message, executedAny: sent > 0);
        }
        catch (Exception ex)
        {
            return ErrorResult(plan, ex, executedAny: sent > 0);
        }

        bool? verified = null;
        double? after = null;
        var reverted = false;
        if (plan.Check is { } check)
        {
            try
            {
                (verified, after, reverted) = await VerifyAsync(check, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Eingabe: Prüfung von {Action} fehlgeschlagen: {Message}", plan.Action.Id, ex.Message);
                verified = null;
            }
        }

        // Kurs 360: Das FCU zeigt 000 – im Ergebnis wie gesagt als 360
        if (plan.Action.Parameter?.Format == "heading" && plan.ValueText == "360" && after is { } shown && Math.Abs(shown % 360) < 0.5)
        {
            after = 360;
        }

        var alreadySet = sent == 0 && skipped > 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Rohwert der Prüfvariable mit Namen der Auswahl, z. B. Schalterstellung 0 = "on" – sonst liest sich 0 wie "aus"
        var afterText = after is { } afterValue ? ActionPlanner.StateName(plan.Action, afterValue) : null;
        var expectedText = plan.Action.Parameter?.IsChoice == true ? plan.ValueText : null;
        string Shown(double? v, string? name) => v is not { } d ? "nichts" : name is null ? d.ToString(inv) : $"{d.ToString(inv)} ({name})";
        var detail = verified switch
        {
            true when alreadySet => "war schon so",
            true => plan.Check!.Independent ? "vom Flugzeug bestätigt" : "Wert steht (nur Schaltervariable geprüft)",
            false when reverted => $"vom Flugzeug zurückgesetzt (zeigt {Shown(after, afterText)})",
            false => $"Flugzeug zeigt {Shown(after, afterText)} statt {Shown(plan.Check!.Condition.Value, expectedText)}",
            null => alreadySet ? "war schon so" : null,
        };

        // Echter Nachweis (Flugzeugzustand, nicht nur Schalterstellung) nach einer wirklichen Änderung: als geprüft merken
        var newlyVerified = false;
        if (verified == true && sent > 0 && plan.Check!.Independent && !plan.Action.Verified)
        {
            try
            {
                if (_verified.Find(profileName, plan.Action) is null)
                {
                    _verified.Record(profileName, plan.Action, "sim", plan.ValueText, _time.GetUtcNow());
                    newlyVerified = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("verified-actions.json nicht geschrieben: {Message}", ex.Message);
            }
        }

        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, "ausgeführt", newlyVerified ? detail + " · als geprüft gespeichert" : detail, verified);
        }

        return new InputResult(
            InputResult.Executed,
            Reason: verified == false
                ? reverted
                    ? "Das Flugzeug hat den Wert kurz übernommen und wieder zurückgesetzt – Voraussetzungen im Cockpit prüfen (z. B. Strom)."
                    : "Das Flugzeug hat den Wert (noch) nicht übernommen – bitte im Cockpit prüfen."
                : null,
            Action: plan.Action.Id,
            Value: plan.ValueText,
            Readback: plan.Readback,
            Verified: plan.Action.Verified || newlyVerified || _verified.Find(profileName, plan.Action) is not null,
            Confirmed: verified,
            StateAfter: after,
            Skipped: skipped,
            AlreadySet: alreadySet,
            CheckIndependent: plan.Check?.Independent,
            StateAfterText: afterText);
    }

    /// <summary>
    /// Wartet, bis der erwartete Wert erscheint UND <see cref="ResolvedCheck.HoldMs"/> lang stehen bleibt.
    /// Ein Wert, der kurz da ist und wieder verschwindet, gilt als zurückgesetzt (reverted).
    /// </summary>
    private async Task<(bool? Ok, double? Value, bool Reverted)> VerifyAsync(ResolvedCheck check, CancellationToken ct)
    {
        var hold = TimeSpan.FromMilliseconds(check.HoldMs);
        var deadline = _time.GetUtcNow() + TimeSpan.FromMilliseconds(check.TimeoutMs) + hold;
        double? last = null;
        DateTimeOffset? matchedSince = null;
        var reverted = false;
        while (true)
        {
            var read = await _backend.ReadAsync(check.Condition.Variable, check.Condition.Unit, ct);
            last = read ?? last;
            var now = _time.GetUtcNow();
            if (read is { } v && check.Condition.Matches(v))
            {
                matchedSince ??= now;
                if (now - matchedSince >= hold)
                {
                    return (true, v, false);
                }
            }
            else if (read is not null && matchedSince is not null)
            {
                matchedSince = null;
                reverted = true;
            }

            if (now >= deadline)
            {
                return (last is null ? null : false, last, reverted);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
        }
    }

    private string? Precheck(AircraftProfile? profile)
    {
        if (!_backend.Connected)
        {
            return "Keine Verbindung zu MSFS.";
        }

        if (profile is null)
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

    private InputResult ErrorResult(ActionPlan plan, Exception ex, bool executedAny)
    {
        _log.LogError(ex, "Eingabe: {Action} = {Value} mit Ausnahme abgebrochen", plan.Action.Id, plan.ValueText);
        var reason = ex is OperationCanceledException ? "Zeitüberschreitung beim Ausführen." : $"Interner Fehler: {ex.Message}";
        var text = executedAny ? reason + " Möglicherweise teilweise ausgeführt – bitte im Cockpit prüfen." : reason;
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, "fehlgeschlagen", text, null);
        }

        return new InputResult(InputResult.Error, text, Action: plan.Action.Id, Value: plan.ValueText);
    }

    private InputResult Fail(ActionPlan plan, string reason, bool executedAny)
    {
        var text = executedAny ? reason + " Möglicherweise teilweise ausgeführt – bitte im Cockpit prüfen." : reason;
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, "fehlgeschlagen", text, null);
        }

        return new InputResult(InputResult.Failed, text, Action: plan.Action.Id, Value: plan.ValueText);
    }

    /// <summary>
    /// Offene Aktion entnehmen (einmalig) und die geteilte Anzeige löschen. Der Token kommt in die Liste der beendeten Tokens,
    /// damit confirm_action mit einem alten Token sagen kann, was aus der Aktion geworden ist.
    /// </summary>
    /// <param name="fate">Was mit der Aktion passiert ist, für die Fehlermeldung, z. B. "durch eine neuere Anfrage ersetzt".</param>
    private PendingAction TakeLocked(string fate)
    {
        var taken = _pending!;
        _pending = null;
        _pendingInput = null;
        _pendingProfile = null;
        Share(null);

        _retired.AddFirst(new RetiredToken(taken.Token, taken.Plan.Summary, fate));
        while (_retired.Count > MaxRetiredTokens)
        {
            _retired.RemoveLast();
        }

        return taken;
    }

    /// <summary>Offene Aktion verwerfen und protokollieren ("ersetzt", "abgebrochen", "abgelaufen").</summary>
    private PendingAction RetireLocked(string outcome, string? detail)
    {
        var fate = outcome switch
        {
            "ersetzt" => "durch eine neuere prepare_action-Anfrage ersetzt",
            "abgelaufen" => "abgelaufen (keine Bestätigung im Zeitfenster)",
            _ => outcome,
        };
        var taken = TakeLocked(fate);
        AddLogLocked(taken.Plan.Action.Id, taken.Plan.ValueText, outcome, detail, null);
        return taken;
    }

    private void ExpireLocked()
    {
        if (_pending is { } p && _time.GetUtcNow() > p.ExpiresUtc)
        {
            RetireLocked("abgelaufen", "Keine Bestätigung");
        }
    }

    /// <summary>Genaue Meldung für einen Token, der nicht (mehr) zur offenen Aktion gehört, sonst null.</summary>
    private string? TokenProblemLocked(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return _pending is null ? null : "Kein Token angegeben – den Token aus der letzten prepare_action-Antwort verwenden.";
        }

        var open = _pending is { } p
            ? $" Offen ist jetzt {p.Plan.Summary} – deren Readback dem Piloten vorlesen und seine Antwort abwarten."
            : " Neu mit prepare_action.";

        if (_retired.FirstOrDefault(r => string.Equals(r.Token, t, StringComparison.OrdinalIgnoreCase)) is { } retired)
        {
            return $"Token {t} gehört zu {retired.Summary} – {retired.Fate}.{open}";
        }

        if (ForeignPending is { TokenHash: { } hash } foreign && string.Equals(hash, TokenHash(t), StringComparison.Ordinal))
        {
            return $"Token {t} gehört zu {foreign.Action} in einer anderen Kopilot-Instanz – confirm_action über dieselbe Verbindung aufrufen wie prepare_action.";
        }

        return null;
    }

    /// <summary>Kurzer Hash des Tokens für die geteilte Anzeige – der Token selbst bleibt in der Instanz.</summary>
    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.Trim().ToLowerInvariant())))[..16].ToLowerInvariant();

    private void Share(PendingAction? pending)
    {
        try
        {
            if (pending is not null)
            {
                _store.SavePending(new SharedPending(
                    pending.Plan.Action.Id, pending.Plan.ValueText, pending.Plan.Readback, pending.ExpiresUtc, Environment.ProcessId, TokenHash(pending.Token)));
            }
            else if (_store.LoadPending()?.ProcessId == Environment.ProcessId)
            {
                // Nur die eigene Anzeige löschen, nicht die einer anderen Instanz
                _store.SavePending(null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("input-pending.json nicht geschrieben: {Message}", ex.Message);
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

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordPattern();

    /// <summary>Großbuchstabe nach Kleinbuchstabe trennt Wörter: "noSmoking" → "no Smoking", "runwayTurnoff" → "runway Turnoff".</summary>
    [GeneratedRegex(@"(?<=\p{Ll})(?=\p{Lu})")]
    private static partial Regex CamelPattern();

    private static HashSet<string> Words(string? text) => WordList(text).ToHashSet(StringComparer.Ordinal);

    private static List<string> WordList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    /// <summary>
    /// Bestätigt der Pilot, dass die Aktion im Cockpit funktioniert hat? Toleranter als confirm_action:
    /// Es reicht ein Bestätigungs- oder Zustandswort ("confirm", "check", "on", "off", "avail", "läuft" …), der ausgeführte Wert
    /// oder der Name der Aktion – solange nichts verneint wird. Der Name wird vor der Prüfung auf Verneinung herausgenommen,
    /// damit "Confirm, no smoking, auto" nicht am "no" aus "no smoking" scheitert.
    /// </summary>
    public static bool PilotConfirmsWorked(ProfileAction action, string? pilotWords, string? executedValue = null)
    {
        var words = WordList(pilotWords);
        if (words.Count == 0)
        {
            return false;
        }

        // Längste Namen zuerst, jedes Vorkommen als zusammenhängende Wortfolge entfernen
        var named = false;
        var rest = words.Select(w => (string?)w).ToList();
        foreach (var phrase in NamePhrases(action).OrderByDescending(p => p.Count))
        {
            for (var i = 0; i + phrase.Count <= rest.Count; i++)
            {
                if (Enumerable.Range(0, phrase.Count).All(k => rest[i + k] == phrase[k]))
                {
                    for (var k = 0; k < phrase.Count; k++)
                    {
                        rest[i + k] = null;
                    }

                    named = true;
                }
            }
        }

        var remaining = rest.OfType<string>().ToList();
        if (remaining.Any(w => NoWords.Contains(w) || FailWords.Contains(w)))
        {
            return false;
        }

        var valueWords = WordList(executedValue);
        return named
               || remaining.Any(w => YesWords.Contains(w) || StateWords.Contains(w))
               || (valueWords.Count > 0 && remaining.Any(valueWords.Contains));
    }

    /// <summary>Namen einer Aktion als Wortfolgen: Label (ganz und je Teil um "/"), Readback ohne Wert, Id-Teile ("signs.noSmoking" → "no smoking").</summary>
    private static IEnumerable<List<string>> NamePhrases(ProfileAction action)
    {
        var texts = new List<string> { action.Label, action.Readback.Replace("{value}", " ", StringComparison.OrdinalIgnoreCase) };
        texts.AddRange(action.Label.Split(new[] { '/', '(', ')', '&', ',' }, StringSplitOptions.RemoveEmptyEntries));

        var idParts = action.Id.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => CamelPattern().Replace(p, " ")).ToList();
        texts.Add(string.Join(' ', idParts));
        if (idParts.Count > 1)
        {
            texts.Add(idParts[^1]);
        }

        return texts
            .Select(WordList)
            .Where(p => p.Count > 0)
            .DistinctBy(p => string.Join(' ', p));
    }
}
