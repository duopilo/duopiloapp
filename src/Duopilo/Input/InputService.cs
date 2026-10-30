using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Duopilo.Aircraft;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Microsoft.Extensions.Options;

namespace Duopilo.Input;

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
    /// <summary>Rückfrage mit Warnhinweis (englisch, zum Vorlesen) – bei <see cref="AwaitingCautionConfirmation"/>.</summary>
    public string? Caution { get; init; }

    /// <summary>Warnhinweise in der eingestellten Sprache (Gründe der Rückfrage).</summary>
    public IReadOnlyList<string>? Cautions { get; init; }

    /// <summary>Nach dem Readback folgt noch eine Rückfrage mit Warnhinweis (bei <see cref="AwaitingConfirmation"/>).</summary>
    public bool CautionFollows { get; init; }

    /// <summary>Was nach der Ausführung noch geprüft wird (z. B. Fahrwerk verriegelt) – das Ergebnis kommt als Callout.</summary>
    public string? FollowUp { get; init; }

    /// <summary>Callout nach der Ausführung ("Speed 250 set") – für den Modus "ausführen + Callout" der Sprachschleife.</summary>
    public string? Callout { get; init; }

    /// <summary>Readback bestätigt: jetzt die Rückfrage mit Warnhinweis vorlesen und mit demselben Token erneut bestätigen lassen.</summary>
    public const string AwaitingCautionConfirmation = "awaiting_caution_confirmation";

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
/// <para>
/// Plausibilitätssperren und Rückfragen (<see cref="SafetyRules"/>): Verlangt eine Aktion eine Rückfrage mit Warnhinweis
/// (z. B. Transponder STBY im Flug), führt das erste "confirm" nicht aus, sondern liefert die Rückfrage
/// (<see cref="InputResult.AwaitingCautionConfirmation"/>); erst das zweite "confirm" mit demselben Token führt aus.
/// Nach Fahrwerk und Klappen liest <see cref="ConfigVerifier"/> die erreichte Stellung nach und sagt sie an.
/// </para>
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
    private readonly IInputContext? _context;
    private readonly ConfigVerifier? _verifier;
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

    public InputService(
        IOptions<DuopiloOptions> options,
        IInputBackend backend,
        ILogger<InputService> log,
        TimeProvider? time = null,
        IInputContext? context = null,
        ConfigVerifier? verifier = null)
    {
        _backend = backend;
        _options = options.Value.Input;
        _store = new InputSettingsStore(options.Value.ResolveDataDirectory());
        _verified = new VerifiedActionStore(options.Value.ResolveDataDirectory());
        _time = time ?? TimeProvider.System;
        _log = log;
        _context = context;
        _verifier = verifier;
    }

    /// <summary>Zuletzt gestartetes Nachlesen von Fahrwerk/Klappen (für Tests), null = keins.</summary>
    public Task? LastVerification { get; private set; }

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

    /// <summary>Offene Aktion einer anderen Duopilo-Instanz (z. B. stdio für Claude Desktop), sonst null.</summary>
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
    /// bei denen Duopilo nur die Schalterstellung lesen kann. Geht nur nach einer Ausführung in den letzten 15 Minuten.
    /// </summary>
    public InputResult MarkVerified(string? actionId, string? pilotConfirmation)
    {
        var profile = _backend.Profile;
        var action = profile?.Actions.FirstOrDefault(a => a.Id.Equals(actionId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null || action is null)
        {
            return new InputResult(InputResult.Refused, Loc.T("input.verified.unknownAction", actionId), Action: actionId);
        }

        var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(15);
        var executed = History.FirstOrDefault(e => e.Action.Equals(action.Id, StringComparison.OrdinalIgnoreCase) && e.Code == InputOutcome.Executed && e.TimeUtc >= cutoff);

        if (!PilotConfirmsWorked(action, pilotConfirmation, executed?.Value))
        {
            return new InputResult(
                InputResult.Refused,
                Loc.T("input.verified.notConfirmed"),
                Action: action.Id);
        }

        if (executed is null)
        {
            return new InputResult(InputResult.Refused, Loc.T("input.verified.notExecuted"), Action: action.Id);
        }

        if (executed.Verified == false)
        {
            return new InputResult(InputResult.Refused, Loc.T("input.verified.lastFailed"), Action: action.Id);
        }

        _verified.Record(profile.Name, action, "pilot", executed.Value, _time.GetUtcNow());
        lock (_lock)
        {
            AddLogLocked(action.Id, executed.Value, InputOutcome.Verified, Loc.T("input.detail.confirmedByPilot"), true);
        }

        return new InputResult(InputResult.Executed, Loc.T("input.verified.saved"), Action: action.Id, Value: executed.Value, Verified: true);
    }

    /// <summary>Schalter im Dashboard. Jede Änderung verwirft offene Aktionen in allen Instanzen.</summary>
    public void SetEnabled(bool enabled)
    {
        _store.Save(new InputSettings { Enabled = enabled, Epoch = Epoch + 1 });
        if (enabled)
        {
            _log.LogInformation(Loc.Log("log.input.enabledByDashboard"));
        }
        else
        {
            _log.LogInformation(Loc.Log("log.input.disabledByDashboard"));
        }

        Cancel(null, enabled ? Loc.T("input.cancel.enabled") : Loc.T("input.cancel.disabled"));
    }

    /// <summary>Abbrechen im Dashboard: verwirft offene Aktionen in allen Duopilo-Instanzen.</summary>
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
            _log.LogWarning(Loc.Log("log.input.prepareCancelled"), actionId, value);
            return new InputResult(InputResult.Failed, Loc.T("input.prepare.callCancelled"), Action: actionId, Value: value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, Loc.Log("log.input.prepareFailed"), actionId, value);
            return new InputResult(InputResult.Error, Loc.T("input.error.internal", ex.Message), Action: actionId, Value: value);
        }
    }

    private async Task<InputResult> PrepareCoreAsync(string actionId, string? value, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new InputResult(InputResult.Disabled, Loc.T("input.prepare.disabled"));
        }

        // Jede neue Anfrage verwirft die offene Aktion – auch wenn sie selbst scheitert. Ihr Readback gilt dann nicht mehr,
        // und ein älterer, langsamer Aufruf darf später keine Aktion mehr anlegen (siehe Nummer beim Anlegen).
        var sequence = Interlocked.Increment(ref _prepareSequence);
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is not null)
            {
                RetireLocked(InputOutcome.Replaced, Loc.T("input.detail.newRequested"));
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
            return Refuse(actionId, value, Loc.T("input.prepare.unknownAction", actionId, profile.Name));
        }

        var snapshot = _backend.Snapshot();
        var result = ActionPlanner.Plan(action, value, profile.Limits, snapshot);
        if (result.Plan is not { } planned)
        {
            return Refuse(action.Id, value, result.Refusal!);
        }

        if (await CheckRequirementsAsync(planned, ct) is { } unmet)
        {
            return Refuse(action.Id, planned.ValueText, unmet);
        }

        var safety = await ApplySafetyAsync(planned, profile, snapshot, ct);
        if (safety.Plan is not { } plan)
        {
            return Refuse(action.Id, planned.ValueText, safety.Refusal!);
        }

        return Hold(plan, value, profile, sequence, ct);
    }

    /// <summary>Geplante Aktion als offene Aktion anlegen (Readback, wartet auf "confirm") – nur, wenn es die neueste Anfrage ist.</summary>
    private InputResult Hold(ActionPlan plan, string? value, AircraftProfile profile, long sequence, CancellationToken ct)
    {
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
                stale = Loc.T("input.prepare.superseded");
            }
            else if (ct.IsCancellationRequested)
            {
                stale = Loc.T("input.prepare.callCancelled");
            }
            else
            {
                if (_pending is not null)
                {
                    RetireLocked(InputOutcome.Replaced, Loc.T("input.detail.newPrepared"));
                }

                _pending = pending;
                _pendingInput = value;
                _pendingProfile = profile;
                _pendingEpoch = epoch;
                LogMissingLimitsLocked(plan);
                AddLogLocked(
                    plan.Action.Id,
                    plan.ValueText,
                    InputOutcome.Prepared,
                    plan.NeedsCrossConfirmation ? Loc.T("input.detail.preparedWithCaution", plan.Readback, CautionText(plan)) : plan.Readback,
                    null);
                Share(pending);
            }

            if (stale is not null)
            {
                AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Discarded, stale, null);
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
            Verified: VerifiedBy(plan.Action) is not null)
        {
            CautionFollows = plan.NeedsCrossConfirmation,
            Cautions = plan.NeedsCrossConfirmation ? plan.Cautions.Select(c => c.Reason).ToList() : null,
        };
    }

    // ------------------------------------------------------------------ Sprachschleife: ausführen + Callout

    /// <summary>
    /// Modus "ausführen + Callout" der Sprachschleife: Der Befehl des Piloten ist die Anweisung (PF → PM) – Duopilo führt ihn
    /// sofort aus, ohne Readback und "confirm". Alle Prüfungen bleiben: Freigabe, Flugzeug, Sperrliste, Sperren, Bedingungen.
    /// Verlangt die Aktion eine Rückfrage mit Warnhinweis (<see cref="SafetyRules"/>), wird sie stattdessen wie bei
    /// <see cref="PrepareAsync"/> als offene Aktion angelegt (<see cref="InputResult.AwaitingConfirmation"/>) – der zweistufige Ablauf
    /// mit Readback, "confirm" und Rückfrage gilt dann unverändert. Nur für die Sprachschleife, nicht als MCP-Tool.
    /// </summary>
    public async Task<InputResult> ExecuteNowAsync(string? actionId, string? value, CancellationToken ct)
    {
        try
        {
            return await ExecuteNowCoreAsync(actionId ?? "", value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning(Loc.Log("log.input.prepareCancelled"), actionId, value);
            return new InputResult(InputResult.Failed, Loc.T("input.prepare.callCancelled"), Action: actionId, Value: value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, Loc.Log("log.input.prepareFailed"), actionId, value);
            return new InputResult(InputResult.Error, Loc.T("input.error.internal", ex.Message), Action: actionId, Value: value);
        }
    }

    private async Task<InputResult> ExecuteNowCoreAsync(string actionId, string? value, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new InputResult(InputResult.Disabled, Loc.T("input.prepare.disabled"));
        }

        // Wie eine neue Anfrage: Die offene Aktion verfällt, ein älterer Aufruf darf danach nichts mehr anlegen
        var sequence = Interlocked.Increment(ref _prepareSequence);
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is not null)
            {
                RetireLocked(InputOutcome.Replaced, Loc.T("input.detail.newRequested"));
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
            return Refuse(actionId, value, Loc.T("input.prepare.unknownAction", actionId, profile.Name));
        }

        var snapshot = _backend.Snapshot();
        var result = ActionPlanner.Plan(action, value, profile.Limits, snapshot);
        if (result.Plan is not { } planned)
        {
            return Refuse(action.Id, value, result.Refusal!);
        }

        if (await CheckRequirementsAsync(planned, ct) is { } unmet)
        {
            return Refuse(action.Id, planned.ValueText, unmet);
        }

        var safety = await ApplySafetyAsync(planned, profile, snapshot, ct);
        if (safety.Plan is not { } plan)
        {
            return Refuse(action.Id, planned.ValueText, safety.Refusal!);
        }

        // Rückfrage nötig: nie sofort ausführen – zweistufiger Ablauf wie bei prepare_action
        if (plan.NeedsCrossConfirmation)
        {
            return Hold(plan, value, profile, sequence, ct);
        }

        var epoch = Epoch;

        // Ab hier läuft die Aktion zu Ende, auch wenn die Sprachschleife neu startet – sonst bliebe sie halb ausgeführt
        using var timeout = new CancellationTokenSource(ExecutionTimeout, _time);
        await _execution.WaitAsync(CancellationToken.None);
        try
        {
            // Unmittelbar vor dem Senden alles erneut prüfen: Eine vorherige Aktion kann die Sperre einige Sekunden gehalten haben
            if (sequence != Interlocked.Read(ref _prepareSequence))
            {
                lock (_lock)
                {
                    AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Discarded, Loc.T("input.prepare.superseded"), null);
                }

                return new InputResult(InputResult.Failed, Loc.T("input.prepare.superseded"), Action: plan.Action.Id, Value: plan.ValueText);
            }

            if (!Enabled)
            {
                return Refuse(action.Id, plan.ValueText, Loc.T("input.confirm.disabledMeanwhile"));
            }

            if (Epoch != epoch)
            {
                return Refuse(action.Id, plan.ValueText, Loc.T("input.confirm.cancelledInDashboard"));
            }

            var current = _backend.Profile;
            if (Precheck(current) is { } again)
            {
                return Refuse(action.Id, plan.ValueText, again);
            }

            if (!ReferenceEquals(current, profile))
            {
                return Refuse(action.Id, plan.ValueText, Loc.T("input.confirm.profileChanged"));
            }

            var fresh = ActionPlanner.Plan(action, value, profile.Limits, _backend.Snapshot());
            if (fresh.Plan is not { } replanned)
            {
                return Refuse(action.Id, plan.ValueText, fresh.Refusal!);
            }

            var freshSafety = await ApplySafetyAsync(replanned, profile, _backend.Snapshot(), timeout.Token);
            if (freshSafety.Plan is not { } final)
            {
                return Refuse(action.Id, plan.ValueText, freshSafety.Refusal!);
            }

            if (final.NeedsCrossConfirmation)
            {
                // Zustand hat sich inzwischen geändert – jetzt doch mit Readback und Rückfrage
                return Hold(final, value, profile, sequence, timeout.Token);
            }

            lock (_lock)
            {
                AddLogLocked(final.Action.Id, final.ValueText, InputOutcome.Prepared, Loc.T("input.detail.voiceDirect", final.Readback), null);
            }

            return await ExecuteAsync(final, profile, timeout.Token);
        }
        catch (Exception ex)
        {
            return ErrorResult(plan, ex, executedAny: true);
        }
        finally
        {
            _execution.Release();
        }
    }

    // ------------------------------------------------------------------ Schritt 2: bestätigen und ausführen

    public async Task<InputResult> ConfirmAsync(string? token, string? pilotConfirmation, CancellationToken ct)
    {
        PendingAction pending;
        string? input;
        AircraftProfile? pendingProfile;
        long pendingEpoch;
        long sequence;
        lock (_lock)
        {
            ExpireLocked();
            if (_pending is null)
            {
                return new InputResult(
                    InputResult.Failed,
                    TokenProblemLocked(token) ?? Loc.T("input.confirm.nonePending"));
            }

            if (!string.Equals(_pending.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new InputResult(
                    InputResult.Failed,
                    TokenProblemLocked(token)
                    ?? Loc.T("input.confirm.tokenMismatch", _pending.Plan.Summary),
                    Action: _pending.Plan.Action.Id);
            }

            var words = Words(pilotConfirmation);
            if (words.Overlaps(NoWords))
            {
                var cancelled = RetireLocked(InputOutcome.Cancelled, Loc.T("input.detail.pilotSaid", pilotConfirmation));
                return new InputResult(InputResult.Cancelled, Loc.T("input.confirm.pilotDeclined"), Action: cancelled.Plan.Action.Id);
            }

            if (!words.Overlaps(YesWords))
            {
                return new InputResult(InputResult.Failed, Loc.T("input.confirm.notClear"));
            }

            if (_time.GetUtcNow() - _pending.CreatedUtc < TimeSpan.FromSeconds(1))
            {
                return new InputResult(InputResult.Failed, Loc.T("input.confirm.tooFast"));
            }

            input = _pendingInput;
            pendingProfile = _pendingProfile;
            pendingEpoch = _pendingEpoch;
            sequence = Interlocked.Read(ref _prepareSequence);
            pending = TakeLocked(Loc.T("input.fate.used"));
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
                return Refuse(action.Id, pending.Plan.ValueText, Loc.T("input.confirm.disabledMeanwhile"));
            }

            if (Epoch != pendingEpoch)
            {
                return Refuse(action.Id, pending.Plan.ValueText, Loc.T("input.confirm.cancelledInDashboard"));
            }

            var profile = _backend.Profile;
            if (Precheck(profile) is { } problem)
            {
                return Refuse(action.Id, pending.Plan.ValueText, problem);
            }

            if (!ReferenceEquals(profile, pendingProfile))
            {
                return Refuse(action.Id, pending.Plan.ValueText, Loc.T("input.confirm.profileChanged"));
            }

            var snapshot = _backend.Snapshot();
            var fresh = ActionPlanner.Plan(action, input, profile!.Limits, snapshot);
            if (fresh.Plan is not { } planned)
            {
                return Refuse(action.Id, pending.Plan.ValueText, fresh.Refusal!);
            }

            var safety = await ApplySafetyAsync(planned, profile, snapshot, timeout.Token);
            if (safety.Plan is not { } plan)
            {
                return Refuse(action.Id, pending.Plan.ValueText, safety.Refusal!);
            }

            // Rückfrage mit Warnhinweis: Der bestätigte Readback reicht nicht – erst die Rückfrage hören und erneut bestätigen.
            // Ist inzwischen ein neuer Warnhinweis dazugekommen (Zustand geändert), wird auch der erst noch abgefragt.
            if (plan.Cautions.Any(c => !pending.ConfirmedCautions.Contains(c.Code, StringComparer.Ordinal)))
            {
                return AskCaution(pending, plan, input, profile, pendingEpoch, sequence);
            }

            return await ExecuteAsync(plan, profile, timeout.Token);
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

    public InputResult Cancel(string? token, string? reason = null)
    {
        lock (_lock)
        {
            if (_pending is null || (token is not null && !string.Equals(_pending.Token, token.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return new InputResult(InputResult.Cancelled, Loc.T("input.cancel.noMatch"));
            }

            var cancelled = RetireLocked(InputOutcome.Cancelled, reason ?? Loc.T("input.cancel.default"));
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
                return Loc.T("input.state.unreadable", requirement.Variable);
            }

            if (!requirement.Matches(state.Value))
            {
                return requirement.Message;
            }
        }

        return null;
    }

    private async Task<InputResult> ExecuteAsync(ActionPlan plan, AircraftProfile profile, CancellationToken ct)
    {
        var profileName = profile.Name;
        if (await CheckRequirementsAsync(plan, ct) is { } unmet)
        {
            return Refuse(plan.Action.Id, plan.ValueText, unmet);
        }

        // Fahrwerk/Klappen: das Monitoring sagt den Hebelwechsel nicht an, die Stellung kommt nach dem Nachlesen
        var job = _verifier is null ? null : ConfigVerifier.JobFor(plan, profile);
        if (job is not null)
        {
            _verifier!.Claim(job);
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
                        Unclaim(job);
                        return Fail(plan, Loc.T("input.state.unreadable", condition.Variable), executedAny: sent > 0);
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
            Unclaim(job);
            return Fail(plan, ex.Message, executedAny: sent > 0);
        }
        catch (Exception ex)
        {
            Unclaim(job);
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
                _log.LogWarning(Loc.Log("log.input.checkFailed"), plan.Action.Id, ex.Message);
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
        string Shown(double? v, string? name) => v is not { } d ? Loc.T("input.detail.nothing") : name is null ? d.ToString(inv) : $"{d.ToString(inv)} ({name})";
        var detail = verified switch
        {
            true when alreadySet => Loc.T("input.detail.alreadySet"),
            true => plan.Check!.Independent ? Loc.T("input.detail.confirmedByAircraft") : Loc.T("input.detail.switchOnly"),
            false when reverted => Loc.T("input.detail.reverted", Shown(after, afterText)),
            false => Loc.T("input.detail.mismatch", Shown(after, afterText), Shown(plan.Check!.Condition.Value, expectedText)),
            null => alreadySet ? Loc.T("input.detail.alreadySet") : null,
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
                _log.LogWarning(Loc.Log("log.input.verifiedNotWritten"), ex.Message);
            }
        }

        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Executed, newlyVerified ? Loc.T("input.detail.savedAsVerified", detail) : detail, verified);
        }

        // Nachlesen mit Callout ("Gear down, three green" / "Gear disagree") – nur wenn wirklich etwas gesendet wurde und der Hebel steht
        string? followUp = null;
        if (job is not null)
        {
            if (sent > 0 && verified != false)
            {
                followUp = job.Describe();
                LastVerification = StartVerification(job, plan);
            }
            else
            {
                Unclaim(job);
            }
        }

        return new InputResult(
            InputResult.Executed,
            Reason: verified == false
                ? reverted
                    ? Loc.T("input.result.reverted")
                    : Loc.T("input.result.notTaken")
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
            StateAfterText: afterText)
        {
            FollowUp = followUp,
            Callout = plan.Callout,
        };
    }

    private void Unclaim(VerificationJob? job)
    {
        if (job is not null)
        {
            _verifier?.Unclaim(job);
        }
    }

    /// <summary>Nachlesen im Hintergrund – confirm_action wartet nicht darauf, das Ergebnis kommt als Callout und im Protokoll.</summary>
    private Task StartVerification(VerificationJob job, ActionPlan plan) => Task.Run(async () =>
    {
        var outcome = await _verifier!.RunAsync(job);
        if (outcome.Superseded)
        {
            return;
        }

        var result = outcome.Ok ? InputOutcome.Checked : outcome.Readable ? InputOutcome.Disagree : InputOutcome.Unreadable;
        var detail = outcome.Callout is null ? outcome.Message : $"\"{outcome.Callout}\" – {outcome.Message}";
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, result, detail, outcome.Ok ? true : outcome.Readable ? false : null);
        }
    });

    // ------------------------------------------------------------------ Plausibilitätssperren und Rückfragen

    /// <summary>
    /// Code-Sperren (<see cref="SafetyRules"/>) mit Live-Zuständen auswerten. Ergebnis: abgelehnt, oder der Plan mit allen
    /// Warnhinweisen (aus den Profil-Sperren und den Code-Sperren). Steht das Flugzeug schon auf dem Zielwert, entfällt die Rückfrage.
    /// </summary>
    private async Task<PlanResult> ApplySafetyAsync(ActionPlan plan, AircraftProfile profile, FlightSnapshot snapshot, CancellationToken ct)
    {
        var states = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var read in SafetyRules.Reads(plan, profile))
        {
            var state = await _backend.ReadAsync(read.Condition.Variable, read.Condition.Unit, ct);
            states[read.Key] = state is { } v ? read.Condition.Matches(v) : null;
        }

        int? cleared = null;
        MsaReading? msa = null;
        if (plan.Action.Id.Equals("fcu.altitude", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                cleared = _context?.ClearedAltitudeFt();
                msa = _context?.Msa(profile.Limits.MsaSource, snapshot) ?? new MsaReading(null, profile.Limits.MsaSource, Loc.T("input.msa.noData"));
            }
            catch (Exception ex)
            {
                _log.LogWarning(Loc.Log("log.input.clearanceMsaUnreadable"), ex.Message);
                msa = new MsaReading(null, profile.Limits.MsaSource, ex.Message);
            }
        }

        var verdict = SafetyRules.Evaluate(plan, new SafetyContext
        {
            Snapshot = snapshot,
            Limits = profile.Limits,
            States = states,
            ClearedAltitudeFt = cleared,
            Msa = msa,
        });
        if (verdict.Refusal is { } refusal)
        {
            return PlanResult.Refuse(refusal);
        }

        var cautions = plan.Cautions.Concat(verdict.Cautions).DistinctBy(c => c.Code).ToList();

        // Steht das Flugzeug schon so (z. B. AP schon aus), ändert sich nichts – dann keine Rückfrage. Squawk hat keine Prüfung.
        if (cautions.Count > 0 && plan.Check is { } check)
        {
            var current = await _backend.ReadAsync(check.Condition.Variable, check.Condition.Unit, ct);
            if (current is { } c && check.Condition.Matches(c))
            {
                cautions.Clear();
            }
        }

        return PlanResult.Ok(plan with { Cautions = cautions });
    }

    /// <summary>Readback ist bestätigt – jetzt die Rückfrage mit Warnhinweis stellen (gleicher Token, neues Zeitfenster).</summary>
    private InputResult AskCaution(PendingAction old, ActionPlan plan, string? input, AircraftProfile profile, long epoch, long sequence)
    {
        var now = _time.GetUtcNow();
        var next = new PendingAction(old.Token, plan, now, now + ConfirmWindow)
        {
            CautionStage = true,
            ConfirmedCautions = plan.Cautions.Select(c => c.Code).ToList(),
        };
        var reasons = plan.Cautions.Select(c => c.Reason).ToList();
        lock (_lock)
        {
            if (sequence != Interlocked.Read(ref _prepareSequence) || _pending is not null)
            {
                AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Discarded, Loc.T("input.caution.newerRequested"), null);
                return new InputResult(
                    InputResult.Failed,
                    Loc.T("input.caution.superseded"),
                    Action: plan.Action.Id,
                    Value: plan.ValueText);
            }

            // Der Token lebt weiter: aus der Liste der beendeten Tokens nehmen
            for (var node = _retired.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Token, old.Token, StringComparison.OrdinalIgnoreCase))
                {
                    _retired.Remove(node);
                    break;
                }
            }

            _pending = next;
            _pendingInput = input;
            _pendingProfile = profile;
            _pendingEpoch = epoch;
            LogMissingLimitsLocked(plan);
            AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Caution, CautionText(plan), null);
            Share(next);
        }

        return new InputResult(
            InputResult.AwaitingCautionConfirmation,
            Reason: string.Join(" ", reasons),
            Token: old.Token,
            Action: plan.Action.Id,
            Value: plan.ValueText,
            Readback: plan.CautionPrompt,
            ExpiresInSec: (int)ConfirmWindow.TotalSeconds,
            Verified: VerifiedBy(plan.Action) is not null)
        {
            Caution = plan.CautionPrompt,
            Cautions = reasons,
        };
    }

    private static string CautionText(ActionPlan plan) => string.Join(" · ", plan.Cautions.Select(c => c.Reason));

    /// <summary>Fehlende Grenzwerte im Protokoll und im Log vermerken.</summary>
    private void LogMissingLimitsLocked(ActionPlan plan)
    {
        foreach (var caution in plan.Cautions.Where(c => c.MissingLimit))
        {
            _log.LogWarning(Loc.Log("log.input.limitMissing"), plan.Action.Id, caution.Code, caution.Reason);
            AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.LimitMissing, caution.Reason, null);
        }
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
            return Loc.T("input.precheck.notConnected");
        }

        if (profile is null)
        {
            return Loc.T("input.precheck.noProfile");
        }

        var s = _backend.Snapshot();
        if (s.FlightRunning == false)
        {
            return Loc.T("input.precheck.noFlight");
        }

        if (s.DataAgeSec is not { } age || age > 5)
        {
            return Loc.T("input.precheck.staleData");
        }

        return null;
    }

    private InputResult Refuse(string? action, string? value, string reason)
    {
        lock (_lock)
        {
            AddLogLocked(action ?? "?", value, InputOutcome.Refused, reason, null);
        }

        return new InputResult(InputResult.Refused, reason, Action: action, Value: value);
    }

    private InputResult ErrorResult(ActionPlan plan, Exception ex, bool executedAny)
    {
        _log.LogError(ex, Loc.Log("log.input.executionException"), plan.Action.Id, plan.ValueText);
        var reason = ex is OperationCanceledException ? Loc.T("input.error.timeout") : Loc.T("input.error.internal", ex.Message);
        var text = executedAny ? Loc.T("input.result.maybePartial", reason) : reason;
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Failed, text, null);
        }

        return new InputResult(InputResult.Error, text, Action: plan.Action.Id, Value: plan.ValueText);
    }

    private InputResult Fail(ActionPlan plan, string reason, bool executedAny)
    {
        var text = executedAny ? Loc.T("input.result.maybePartial", reason) : reason;
        lock (_lock)
        {
            AddLogLocked(plan.Action.Id, plan.ValueText, InputOutcome.Failed, text, null);
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

    /// <summary>Offene Aktion verwerfen und protokollieren (<see cref="InputOutcome.Replaced"/>, <see cref="InputOutcome.Cancelled"/>, <see cref="InputOutcome.Expired"/>).</summary>
    private PendingAction RetireLocked(string outcome, string? detail)
    {
        var fate = outcome switch
        {
            InputOutcome.Replaced => Loc.T("input.fate.replaced"),
            InputOutcome.Expired => Loc.T("input.fate.expired"),
            InputOutcome.Cancelled => Loc.T("input.fate.cancelled"),
            _ => InputOutcome.Text(outcome),
        };
        var taken = TakeLocked(fate);
        AddLogLocked(taken.Plan.Action.Id, taken.Plan.ValueText, outcome, detail, null);
        return taken;
    }

    private void ExpireLocked()
    {
        if (_pending is { } p && _time.GetUtcNow() > p.ExpiresUtc)
        {
            RetireLocked(InputOutcome.Expired, Loc.T("input.detail.noConfirmation"));
        }
    }

    /// <summary>Genaue Meldung für einen Token, der nicht (mehr) zur offenen Aktion gehört, sonst null.</summary>
    private string? TokenProblemLocked(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return _pending is null ? null : Loc.T("input.token.missing");
        }

        if (_retired.FirstOrDefault(r => string.Equals(r.Token, t, StringComparison.OrdinalIgnoreCase)) is { } retired)
        {
            return _pending is { } p
                ? Loc.T("input.token.retiredOtherOpen", t, retired.Summary, retired.Fate, p.Plan.Summary)
                : Loc.T("input.token.retired", t, retired.Summary, retired.Fate);
        }

        if (ForeignPending is { TokenHash: { } hash } foreign && string.Equals(hash, TokenHash(t), StringComparison.Ordinal))
        {
            return Loc.T("input.token.foreign", t, foreign.Action);
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
                    pending.Plan.Action.Id, pending.Plan.ValueText, pending.Prompt, pending.ExpiresUtc, Environment.ProcessId, TokenHash(pending.Token)));
            }
            else if (_store.LoadPending()?.ProcessId == Environment.ProcessId)
            {
                // Nur die eigene Anzeige löschen, nicht die einer anderen Instanz
                _store.SavePending(null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(Loc.Log("log.input.pendingNotWritten"), ex.Message);
        }
    }

    /// <param name="outcome">Code aus <see cref="InputOutcome"/>.</param>
    private void AddLogLocked(string action, string? value, string outcome, string? detail, bool? verified)
    {
        var text = InputOutcome.Text(outcome);
        _history.AddFirst(new InputLogEntry(_time.GetUtcNow(), action, value, text, detail, verified) { Code = outcome });
        while (_history.Count > MaxLog)
        {
            _history.RemoveLast();
        }

        _log.LogInformation(Loc.Log("log.input.entry"), text, action, value is null ? "" : " = " + value, detail ?? "");
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
