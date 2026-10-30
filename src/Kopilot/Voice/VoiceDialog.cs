using Kopilot.Input;

namespace Kopilot.Voice;

/// <summary>Zugriff auf das Eingabe-Modul (prepare_action → confirm_action / cancel_action) – austauschbar für Tests.</summary>
public interface IVoiceActionGateway
{
    Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct);

    Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct);

    InputResult Cancel(string token, string reason);
}

/// <summary>Das echte Eingabe-Modul.</summary>
public sealed class InputActionGateway(InputService input) : IVoiceActionGateway
{
    public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct) => input.PrepareAsync(actionId, value, ct);

    public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct) => input.ConfirmAsync(token, pilotWords, ct);

    public InputResult Cancel(string token, string reason) => input.Cancel(token, reason);
}

/// <summary>Antwort von Kopilot: Anzeigetext und Text für die Sprachsynthese.</summary>
public sealed record VoiceReply(string Text, string Spoken)
{
    /// <summary>Readback einer vorbereiteten Aktion: Erst wenn er ganz gesprochen ist, zählt "Confirm" (siehe <see cref="VoiceDialog.Spoken"/>).</summary>
    public string? ReadbackToken { get; init; }

    public static VoiceReply Of(string text) => new(text, VoicePhraseology.ForSpeech(text));
}

/// <summary>Vorbereitete Aktion, die auf "Confirm" wartet.</summary>
/// <param name="HeardUtc">Ende des gesprochenen Readbacks – erst danach zählt "Confirm". null = noch nicht gehört.</param>
public sealed record PendingVoiceAction(string Token, string ActionId, string? Value, string Readback, DateTimeOffset ExpiresUtc, DateTimeOffset? HeardUtc = null);

/// <summary>
/// Gesprächsablauf mit Kopilot (ohne Sprachmodell): Befehl → prepare_action → Readback → "Confirm"/"Correct"/"Affirm" →
/// confirm_action; "Negative"/"Cancel" → cancel_action; nach dem Timeout verfällt die Aktion still.
/// Bestätigt wird nur, was der Pilot gehört hat: "Confirm" zählt erst, wenn der Readback ganz gesprochen ist
/// (<see cref="VoiceDialog.Spoken"/>), und nur, wenn der Pilot danach zu sprechen begonnen hat.
/// Kritische Aktionen (z. B. Transponder STBY im Flug) fragen nach dem ersten "Confirm" mit Warnhinweis nach; erst ein zweites
/// "Confirm" nach der gehörten Rückfrage führt aus.
/// Abfragen werden mit dem reinen Callout beantwortet, Unbekanntes mit "Say again".
/// Nicht threadsicher – die Sprachschleife ruft alles nacheinander auf.
/// </summary>
public sealed class VoiceDialog(IVoiceActionGateway gateway, IVoiceFlightStateSource state, TimeProvider? time = null)
{
    public const string SayAgain = "Say again";
    public const string Unable = "Unable";
    public const string InputDisabled = "Input disabled";
    public const string NotConfirmed = "Not confirmed";
    public const string Cancelled = "Cancelled";
    public const string Paused = "Voice control paused";
    public const string Resumed = "Voice control active";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile bool _muted;

    /// <summary>So lange wartet ein Readback auf die Bestätigung.</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Unbekannte Sätze bis zu dieser Wortzahl bekommen "Say again", längere werden still verworfen (0 = immer still).</summary>
    public int SayAgainMaxWords { get; set; } = 8;

    /// <summary>Befehle nur mit Anrede ("Kopilot, …").</summary>
    public bool RequireWakeWord { get; set; }

    /// <summary>Spracheingabe stumm ("Kopilot Pause" oder Tastenkürzel). Dann zählt nur noch "Kopilot weiter".</summary>
    public bool Muted => _muted;

    public PendingVoiceAction? Pending { get; private set; }

    /// <summary>Letzte Antwort (für "Say again" des Piloten).</summary>
    public VoiceReply? LastReply { get; private set; }

    /// <summary>Wann Kopilot zuletzt geantwortet hat (für "Say again": Kopilot oder ATC?).</summary>
    public DateTimeOffset? LastReplyUtc { get; private set; }

    /// <summary>Ende der zuletzt ganz gesprochenen Antwort von Kopilot.</summary>
    public DateTimeOffset? LastReplyHeardUtc { get; private set; }

    /// <summary>Wann Kopilot zuletzt gesprochen hat (Antwort erzeugt oder zu Ende gesprochen, das Spätere).</summary>
    public DateTimeOffset? LastSpokeUtc => Latest(LastReplyUtc, LastReplyHeardUtc);

    /// <summary>Letztes Ergebnis des Eingabe-Moduls (Status, Grund) – für Log und Status.</summary>
    public InputResult? LastResult { get; private set; }

    /// <summary>Stumm schalten oder wieder an (Tastenkürzel). Antwort zum Sprechen.</summary>
    public VoiceReply SetMuted(bool muted)
    {
        _muted = muted;
        if (muted)
        {
            CancelPending("Spracheingabe stumm");
        }

        return Remember(VoiceReply.Of(muted ? Paused : Resumed));
    }

    /// <summary>Verarbeitet einen erkannten Satz. Rückgabe: was Kopilot sagt (null = nichts).</summary>
    /// <param name="intent">Erkannter Befehl.</param>
    /// <param name="normalized">Der Satz in normalisierter Form (für "Confirm" und die Wortzahl).</param>
    /// <param name="addressed">Mit Anrede ("Kopilot, …").</param>
    public Task<VoiceReply?> HandleAsync(VoiceIntent intent, string normalized, bool addressed, CancellationToken ct) =>
        HandleAsync(intent, normalized, addressed, null, ct);

    /// <inheritdoc cref="HandleAsync(VoiceIntent, string, bool, CancellationToken)"/>
    /// <param name="startUtc">Wann der Pilot den Satz zu sprechen begonnen hat (null = jetzt). Ein "Confirm", das vor dem Ende des Readbacks begann, zählt nicht.</param>
    public async Task<VoiceReply?> HandleAsync(VoiceIntent intent, string normalized, bool addressed, DateTimeOffset? startUtc, CancellationToken ct)
    {
        Expire();
        if (Muted)
        {
            return intent.Kind == IntentKind.Resume ? SetMuted(false) : null;
        }

        switch (intent.Kind)
        {
            case IntentKind.Pause:
                return SetMuted(true);

            case IntentKind.Resume:
                return null;

            case IntentKind.SayAgain:
                return LastReply;

            case IntentKind.Confirm:
                return await ConfirmAsync(normalized, startUtc ?? _time.GetUtcNow(), ct);

            case IntentKind.Acknowledge:
                // "Ja", "OK", "Roger" bestätigen nichts – nach einem Readback nachfragen, sonst still
                return Pending is { HeardUtc: not null } ? VoiceReply.Of(SayAgain) : null;

            case IntentKind.Negative:
                return Pending is null ? null : Remember(CancelPending("Pilot: negative") ? VoiceReply.Of(Cancelled) : VoiceReply.Of(Unable));

            case IntentKind.Query when RequireWakeWord && !addressed:
            case IntentKind.Action when RequireWakeWord && !addressed:
                return null;

            case IntentKind.Query:
                return Remember(VoiceReply.Of(VoiceQueries.Answer(intent.Query!.Value, state.Read())));

            case IntentKind.Action:
                return await PrepareAsync(intent, ct);

            default:
                if (RequireWakeWord && !addressed)
                {
                    return null;
                }

                var words = VoiceText.Words(normalized).Length;
                return words > 0 && words <= SayAgainMaxWords ? VoiceReply.Of(SayAgain) : null;
        }
    }

    /// <summary>
    /// Eine Antwort ist ganz gesprochen (Rückmeldung der Sprachausgabe). Ist es der Readback der offenen Aktion, zählt ab jetzt
    /// "Confirm" – das Zeitfenster beginnt neu.
    /// </summary>
    public void Spoken(VoiceReply reply, DateTimeOffset endUtc)
    {
        LastReplyHeardUtc = Latest(LastReplyHeardUtc, endUtc);
        if (reply.ReadbackToken is { } token && Pending is { } p && p.Token == token)
        {
            Pending = p with { HeardUtc = endUtc, ExpiresUtc = endUtc + ConfirmTimeout };
        }
    }

    /// <summary>Eine Antwort wurde nicht angenommen (keine Stimme, Sim pausiert). Ein nie gehörter Readback ist nicht bestätigbar.</summary>
    public void NotSpoken(VoiceReply reply)
    {
        if (reply.ReadbackToken is { } token && Pending is { HeardUtc: null } p && p.Token == token)
        {
            CancelPending("Readback nicht gesprochen");
        }
    }

    /// <summary>Offene Aktion verwerfen (z. B. weil ATC nach dem Readback gesprochen hat). true = es gab eine.</summary>
    public bool CancelPending(string reason)
    {
        if (Pending is not { } pending)
        {
            return false;
        }

        Pending = null;
        LastResult = gateway.Cancel(pending.Token, reason);
        return LastResult.Status == InputResult.Cancelled;
    }

    /// <summary>Timeout prüfen (regelmäßig aufrufen). Eine abgelaufene Aktion wird still verworfen.</summary>
    public void Expire()
    {
        if (Pending is { } p && _time.GetUtcNow() > p.ExpiresUtc)
        {
            CancelPending("Keine Bestätigung (Sprache)");
        }
    }

    private async Task<VoiceReply?> PrepareAsync(VoiceIntent intent, CancellationToken ct)
    {
        // Eine neue Aktion ersetzt die offene – auch im Eingabe-Modul, falls die neue abgelehnt wird
        CancelPending("Neuer Befehl (Sprache)");

        var result = await gateway.PrepareAsync(intent.ActionId!, intent.Value, ct);
        LastResult = result;
        if (result.Status == InputResult.AwaitingConfirmation && result.Token is { } token)
        {
            var readback = string.IsNullOrWhiteSpace(result.Readback) ? intent.ToString() : result.Readback;
            Pending = new PendingVoiceAction(token, result.Action ?? intent.ActionId!, result.Value, readback, _time.GetUtcNow() + ConfirmTimeout);
            return Remember(VoiceReply.Of(readback) with { ReadbackToken = token });
        }

        return Remember(VoiceReply.Of(result.Status == InputResult.Disabled ? InputDisabled : Unable));
    }

    private async Task<VoiceReply?> ConfirmAsync(string normalized, DateTimeOffset startUtc, CancellationToken ct)
    {
        if (Pending is not { } pending)
        {
            // Nichts offen – "Confirm" ohne Readback wird ignoriert
            return null;
        }

        // Readback noch nicht (ganz) gehört, oder das "Confirm" begann schon vorher – zählt nicht, die Aktion bleibt offen
        if (pending.HeardUtc is not { } heard || startUtc < heard)
        {
            return null;
        }

        Pending = null;
        var result = await gateway.ConfirmAsync(pending.Token, normalized, ct);
        LastResult = result;
        if (result.Status == InputResult.AwaitingCautionConfirmation && result.Token is { } token)
        {
            // Kritische Aktion: Rückfrage mit Warnhinweis vorlesen, erst ein weiteres "Confirm" danach führt aus
            var caution = result.Caution ?? result.Readback ?? pending.Readback;
            Pending = new PendingVoiceAction(token, result.Action ?? pending.ActionId, result.Value, caution, _time.GetUtcNow() + ConfirmTimeout);
            return Remember(VoiceReply.Of(caution) with { ReadbackToken = token });
        }

        return result.Status switch
        {
            // Ausgeführt: nichts sagen (die Bewegung im Cockpit ist die Antwort), nur wenn das Flugzeug den Wert nicht übernommen hat
            InputResult.Executed when result.Confirmed == false => Remember(VoiceReply.Of(NotConfirmed)),
            InputResult.Executed => null,
            InputResult.Cancelled => null,
            InputResult.Disabled => Remember(VoiceReply.Of(InputDisabled)),
            _ => Remember(VoiceReply.Of(Unable)),
        };
    }

    private VoiceReply Remember(VoiceReply reply)
    {
        LastReply = reply;
        LastReplyUtc = _time.GetUtcNow();
        return reply;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
