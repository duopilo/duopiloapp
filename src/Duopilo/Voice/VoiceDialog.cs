using Duopilo.Input;
using Duopilo.Localization;

namespace Duopilo.Voice;

/// <summary>Zugriff auf das Eingabe-Modul (prepare_action → confirm_action / cancel_action) – austauschbar für Tests.</summary>
public interface IVoiceActionGateway
{
    Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct);

    Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct);

    InputResult Cancel(string token, string reason);

    /// <summary>
    /// "Ausführen + Callout": sofort ausführen, wenn die Aktion keine Rückfrage braucht – sonst wie <see cref="PrepareAsync"/>
    /// (Readback, wartet auf "confirm"). Standard: immer wie <see cref="PrepareAsync"/>.
    /// </summary>
    Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct) => PrepareAsync(actionId, value, ct);
}

/// <summary>Das echte Eingabe-Modul.</summary>
public sealed class InputActionGateway(InputService input) : IVoiceActionGateway
{
    public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct) => input.PrepareAsync(actionId, value, ct);

    public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct) => input.ConfirmAsync(token, pilotWords, ct);

    public InputResult Cancel(string token, string reason) => input.Cancel(token, reason);

    public Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct) => input.ExecuteNowAsync(actionId, value, ct);
}

/// <summary>Antwort des Kopiloten: Anzeigetext und Text für die Sprachsynthese.</summary>
public sealed record VoiceReply(string Text, string Spoken)
{
    /// <summary>Readback einer vorbereiteten Aktion: Erst wenn er ganz gesprochen ist, zählt "Confirm" (siehe <see cref="VoiceDialog.Spoken"/>).</summary>
    public string? ReadbackToken { get; init; }

    /// <summary>Challenge oder Abweichung einer Checkliste: Erst wenn sie ganz gesprochen ist, läuft die Wartezeit für die Wiederholung.</summary>
    public bool Checklist { get; init; }

    /// <summary>
    /// Smalltalk (Sprachmodell, Vorstellung): freier Text ohne Funk-Aussprache, ganz unten in der Warteschlange – jede andere Ansage
    /// unterbricht ihn. Nie ein Readback, nie bestätigbar.
    /// </summary>
    public bool Smalltalk { get; init; }

    /// <summary>Sprache des Smalltalks ("de"); null = Englisch.</summary>
    public string? Language { get; init; }

    /// <summary>Stimme für den Smalltalk in einer anderen Sprache ("piper:de_DE-thorsten-medium"); null = die Stimme des Kopiloten.</summary>
    public string? Voice { get; init; }

    public static VoiceReply Of(string text) => new(text, Duopilo.Speech.Pronunciation.ForSpeech(text));

    /// <summary>Smalltalk: so sprechen, wie er ist.</summary>
    public static VoiceReply Chat(string text, string? language = null, string? voice = null) =>
        new(text, text) { Smalltalk = true, Language = language is null or "en" ? null : language, Voice = voice };
}

/// <summary>Vorbereitete Aktion, die auf "Confirm" wartet.</summary>
/// <param name="HeardUtc">Ende des gesprochenen Readbacks – erst danach zählt "Confirm". null = noch nicht gehört.</param>
public sealed record PendingVoiceAction(string Token, string ActionId, string? Value, string Readback, DateTimeOffset ExpiresUtc, DateTimeOffset? HeardUtc = null);

/// <summary>
/// Gesprächsablauf mit dem Kopiloten (ohne Sprachmodell).
/// <para>
/// Modus "ausführen + Callout" (Standard, <see cref="ExecuteWithCallout"/>): Der Befehl des Piloten ist die Anweisung – der Kopilot
/// führt ihn sofort aus und sagt ihn nach der Prüfung an ("Speed 250 set", "Flaps 2"). Readback mit "Confirm" nur, wenn die
/// Erkennung unsicher war (Konfidenz unter <see cref="MinConfidence"/>) oder die Aktion laut Plausibilitätssperren eine
/// Rückfrage braucht. Gesperrte Aktionen bleiben gesperrt ("Unable").
/// </para>
/// <para>
/// Zweistufiger Ablauf (Modus aus, unsicher erkannt oder Rückfrage nötig): Befehl → Readback → "Confirm"/"Correct"/"Affirm" →
/// ausführen; "Negative"/"Cancel" bricht ab; nach dem Timeout verfällt die Aktion still. "Confirm" zählt erst, wenn der Readback
/// ganz gesprochen ist (<see cref="Spoken"/>), und nur, wenn der Pilot danach zu sprechen begonnen hat. Kritische Aktionen fragen
/// nach dem ersten "Confirm" mit Warnhinweis nach; erst ein zweites "Confirm" nach der gehörten Rückfrage führt aus.
/// </para>
/// Abfragen werden mit dem reinen Callout beantwortet, Unbekanntes mit "Say again". "Check"/"Copy" quittieren wichtige Meldungen
/// (<see cref="Acknowledgments"/>). Läuft eine Checkliste, ist jeder Satz ohne Anrede die Antwort auf die Challenge.
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
    private readonly List<AcknowledgmentAction> _ackActions = [];
    private volatile bool _muted;
    private DateTimeOffset? _frozenSince;

    /// <summary>So lange wartet ein Readback auf die Bestätigung.</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Unbekannte Sätze bis zu dieser Wortzahl bekommen "Say again", längere werden still verworfen (0 = immer still).</summary>
    public int SayAgainMaxWords { get; set; } = 8;

    /// <summary>Befehle nur mit Anrede ("Kopilot, …").</summary>
    public bool RequireWakeWord { get; set; }

    /// <summary>Modus "ausführen + Callout": sicher erkannte Befehle ohne Rückfrage sofort ausführen und danach ansagen.</summary>
    public bool ExecuteWithCallout { get; set; } = true;

    /// <summary>Darunter gilt die Erkennung als unsicher (Konfidenz 0–1) – dann Readback mit "Confirm".</summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>Wichtige Meldungen, die "check"/"copy" erwarten (null = keine Bestätigungspflicht).</summary>
    public AcknowledgmentTracker? Acknowledgments { get; set; }

    /// <summary>Challenge-Response-Checklisten.</summary>
    public ChecklistRunner Checklists { get; } = new(time);

    /// <summary>"Briefing": Vorstellung des First Officers (null = es gibt keinen, dann still).</summary>
    public Func<VoiceReply?>? Briefing { get; set; }

    /// <summary>Spracheingabe stumm ("Kopilot Pause" oder Tastenkürzel). Dann zählt nur noch "Kopilot weiter".</summary>
    public bool Muted => _muted;

    public PendingVoiceAction? Pending { get; private set; }

    /// <summary>Letzte Antwort (für "Say again" des Piloten).</summary>
    public VoiceReply? LastReply { get; private set; }

    /// <summary>Wann der Kopilot zuletzt geantwortet hat (für "Say again": Kopilot oder ATC?).</summary>
    public DateTimeOffset? LastReplyUtc { get; private set; }

    /// <summary>Ende der zuletzt ganz gesprochenen Antwort des Kopiloten.</summary>
    public DateTimeOffset? LastReplyHeardUtc { get; private set; }

    /// <summary>Wann der Kopilot zuletzt gesprochen hat (Antwort erzeugt oder zu Ende gesprochen, das Spätere).</summary>
    public DateTimeOffset? LastSpokeUtc => Latest(LastReplyUtc, LastReplyHeardUtc);

    /// <summary>Letztes Ergebnis des Eingabe-Moduls (Status, Grund) – für Log und Status.</summary>
    public InputResult? LastResult { get; private set; }

    /// <summary>Letzter Befehl wurde sofort ausgeführt (ausführen + Callout) – für Log und Status.</summary>
    public bool LastExecutedDirectly { get; private set; }

    /// <summary>Stumm schalten oder wieder an (Tastenkürzel). Antwort zum Sprechen.</summary>
    public VoiceReply SetMuted(bool muted)
    {
        _muted = muted;
        if (muted)
        {
            CancelPending(Loc.T("voice.inputMuted"));
        }

        return Remember(VoiceReply.Of(muted ? Paused : Resumed));
    }

    /// <summary>Verarbeitet einen erkannten Satz. Rückgabe: was der Kopilot sagt (null = nichts).</summary>
    /// <param name="intent">Erkannter Befehl.</param>
    /// <param name="normalized">Der Satz in normalisierter Form (für "Confirm" und die Wortzahl).</param>
    /// <param name="addressed">Mit Anrede ("Kopilot, …").</param>
    public Task<VoiceReply?> HandleAsync(VoiceIntent intent, string normalized, bool addressed, CancellationToken ct) =>
        HandleAsync(intent, normalized, addressed, null, null, ct);

    /// <inheritdoc cref="HandleAsync(VoiceIntent, string, bool, CancellationToken)"/>
    /// <param name="startUtc">Wann der Pilot den Satz zu sprechen begonnen hat (null = jetzt). Ein "Confirm", das vor dem Ende des Readbacks begann, zählt nicht.</param>
    public Task<VoiceReply?> HandleAsync(VoiceIntent intent, string normalized, bool addressed, DateTimeOffset? startUtc, CancellationToken ct) =>
        HandleAsync(intent, normalized, addressed, startUtc, null, ct);

    /// <inheritdoc cref="HandleAsync(VoiceIntent, string, bool, CancellationToken)"/>
    /// <param name="startUtc">Wann der Pilot den Satz zu sprechen begonnen hat (null = jetzt).</param>
    /// <param name="confidence">Konfidenz der Erkennung (0–1, null = unbekannt, gilt als sicher).</param>
    public async Task<VoiceReply?> HandleAsync(VoiceIntent intent, string normalized, bool addressed, DateTimeOffset? startUtc, double? confidence, CancellationToken ct)
    {
        Expire();
        var start = startUtc ?? _time.GetUtcNow();
        if (Muted)
        {
            return intent.Kind == IntentKind.Resume ? SetMuted(false) : null;
        }

        // "Check"/"Copy" quittieren wichtige Meldungen – auch mitten in einer Checkliste
        if (intent.Kind == IntentKind.Check && Acknowledgments is { } acks)
        {
            _ackActions.AddRange(acks.Acknowledge(start));
        }

        // Laufende Checkliste: jeder Satz ohne Anrede ist die Antwort auf die Challenge
        if (Checklists.Active is not null && HandleChecklist(intent, normalized, addressed) is { } handled)
        {
            return handled.Reply;
        }

        switch (intent.Kind)
        {
            case IntentKind.Pause:
                return SetMuted(true);

            case IntentKind.Resume:
            case IntentKind.Check:
                return null;

            case IntentKind.SayAgain:
                return LastReply;

            case IntentKind.Confirm:
                return await ConfirmAsync(normalized, start, ct);

            case IntentKind.Acknowledge:
                // "Ja", "OK", "Roger" bestätigen nichts – nach einem Readback nachfragen, sonst still
                return Pending is { HeardUtc: not null } ? VoiceReply.Of(SayAgain) : null;

            case IntentKind.Negative:
                return Pending is null ? null : Remember(CancelPending(Loc.T("voice.cancel.pilotNegative")) ? VoiceReply.Of(Cancelled) : VoiceReply.Of(Unable));

            case IntentKind.Query when RequireWakeWord && !addressed:
            case IntentKind.Action when RequireWakeWord && !addressed:
            case IntentKind.Checklist when RequireWakeWord && !addressed:
                return null;

            case IntentKind.Query:
                return Remember(await AnswerAsync(intent.Query!.Value, ct));

            case IntentKind.Action:
                return await CommandAsync(intent, confidence, ct);

            case IntentKind.Checklist:
                return StartChecklist(intent.Value);

            case IntentKind.Briefing when RequireWakeWord && !addressed:
                return null;

            case IntentKind.Briefing:
                return Briefing?.Invoke() is { } introduction ? Remember(introduction) : null;

            case IntentKind.ChecklistControl:
                // Ohne laufende Checkliste gibt es nichts zu überspringen
                return null;

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
    /// Darf ein Satz ans Sprachmodell (Smalltalk)? Nur, wenn ihn die Sprachschleife sonst mit "Say again" beantworten oder verwerfen
    /// würde – und nie, solange etwas Wichtigeres offen ist: ein Readback, eine Checkliste, eine Meldung, die auf "check" wartet,
    /// oder die Spracheingabe stumm ist. Ohne Anrede nur kurze Sätze (längere sind meist Gespräche im Raum).
    /// </summary>
    /// <param name="unaddressedMaxWords">Höchstzahl der Wörter ohne Anrede (0 = nur mit Anrede).</param>
    public bool CanChat(VoiceIntent intent, string normalized, bool addressed, int unaddressedMaxWords)
    {
        Expire();
        if (intent.Kind != IntentKind.Unknown || Muted || Pending is not null || Checklists.Active is not null
            || Acknowledgments is { PendingTexts.Count: > 0 })
        {
            return false;
        }

        if (RequireWakeWord && !addressed)
        {
            return false;
        }

        var words = VoiceText.Words(normalized).Length - (addressed ? 1 : 0);
        return words > 0 && (addressed || words <= unaddressedMaxWords);
    }

    /// <summary>Antwort des Smalltalks merken ("Say again" wiederholt sie). Ein offener Readback bleibt unberührt.</summary>
    public VoiceReply RememberChat(VoiceReply reply) => Remember(reply with { Smalltalk = true, ReadbackToken = null, Checklist = false });

    /// <summary>Was die letzten "check"/"copy" ausgelöst haben (eskalierte Meldungen aufheben) – holt die Sprachschleife ab.</summary>
    public IReadOnlyList<AcknowledgmentAction> TakeAcknowledgmentActions()
    {
        if (_ackActions.Count == 0)
        {
            return [];
        }

        var actions = _ackActions.ToList();
        _ackActions.Clear();
        return actions;
    }

    /// <summary>
    /// Regelmäßig aufrufen (Sekundentakt): verfallene Readbacks verwerfen, unbeantwortete Challenge einmal wiederholen.
    /// Rückgabe: was der Kopilot sagt (null = nichts).
    /// </summary>
    public VoiceReply? Tick()
    {
        if (Frozen)
        {
            // Sim läuft nicht: offene Readbacks und Checklisten warten, statt zu verfallen
            return null;
        }

        Expire();
        if (Muted || Checklists.Tick() is not { } repeat)
        {
            return null;
        }

        return Remember(repeat with { Checklist = true });
    }

    /// <summary>Eingefroren (Sim nicht verbunden, kein Flug, pausiert): Zeitfenster stehen, nichts verfällt, nichts wird wiederholt.</summary>
    public bool Frozen => _frozenSince is not null;

    /// <summary>Einfrieren – die Zeit bis zu <see cref="Thaw"/> zählt für Readbacks und Checklisten nicht.</summary>
    public void Freeze()
    {
        _frozenSince ??= _time.GetUtcNow();
    }

    /// <summary>Weiter: Zeitfenster des offenen Readbacks und der Checkliste um die eingefrorene Zeit verschieben.</summary>
    public void Thaw()
    {
        if (_frozenSince is not { } since)
        {
            return;
        }

        _frozenSince = null;
        var frozen = _time.GetUtcNow() - since;
        if (frozen <= TimeSpan.Zero)
        {
            return;
        }

        if (Pending is { } p)
        {
            Pending = p with { ExpiresUtc = p.ExpiresUtc + frozen };
        }

        Checklists.Shift(frozen);
    }

    /// <summary>
    /// Eine Antwort ist ganz gesprochen (Rückmeldung der Sprachausgabe). Ist es der Readback der offenen Aktion, zählt ab jetzt
    /// "Confirm" – das Zeitfenster beginnt neu. Bei einer Challenge beginnt die Wartezeit bis zur Wiederholung.
    /// </summary>
    public void Spoken(VoiceReply reply, DateTimeOffset endUtc)
    {
        LastReplyHeardUtc = Latest(LastReplyHeardUtc, endUtc);
        if (reply.ReadbackToken is { } token && Pending is { } p && p.Token == token)
        {
            Pending = p with { HeardUtc = endUtc, ExpiresUtc = endUtc + ConfirmTimeout };
        }

        if (reply.Checklist)
        {
            Checklists.Spoken(endUtc);
        }
    }

    /// <summary>Eine Antwort wurde nicht angenommen (keine Stimme, Sim pausiert). Ein nie gehörter Readback ist nicht bestätigbar.</summary>
    public void NotSpoken(VoiceReply reply)
    {
        if (reply.ReadbackToken is { } token && Pending is { HeardUtc: null } p && p.Token == token)
        {
            CancelPending(Loc.T("voice.cancel.readbackNotSpoken"));
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
        if (!Frozen && Pending is { } p && _time.GetUtcNow() > p.ExpiresUtc)
        {
            CancelPending(Loc.T("voice.cancel.noConfirmation"));
        }
    }

    // ------------------------------------------------------------------ Abfragen

    private async Task<VoiceReply> AnswerAsync(QueryKind query, CancellationToken ct)
    {
        if (query != QueryKind.Traffic)
        {
            return VoiceQueries.Reply(query, state.Read());
        }

        try
        {
            return VoiceQueries.Traffic(await state.TrafficAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Verkehrsabfrage gescheitert (SimConnect antwortet nicht) – kein Grund, die Sprachschleife anzuhalten
            return VoiceReply.Of(Unable);
        }
    }

    // ------------------------------------------------------------------ Befehle

    private async Task<VoiceReply?> CommandAsync(VoiceIntent intent, double? confidence, CancellationToken ct)
    {
        // Eine neue Aktion ersetzt die offene – auch im Eingabe-Modul, falls die neue abgelehnt wird
        CancelPending(Loc.T("voice.cancel.newCommand"));

        var direct = ExecuteWithCallout && (confidence is not { } c || c >= MinConfidence);
        var result = direct
            ? await gateway.ExecuteAsync(intent.ActionId!, intent.Value, ct)
            : await gateway.PrepareAsync(intent.ActionId!, intent.Value, ct);
        LastResult = result;
        LastExecutedDirectly = direct && result.Status == InputResult.Executed;

        if (result.Status == InputResult.AwaitingConfirmation && result.Token is { } token)
        {
            // Readback: unsicher erkannt, Modus aus oder die Aktion braucht eine Rückfrage
            var readback = string.IsNullOrWhiteSpace(result.Readback) ? intent.ToString() : result.Readback;
            Pending = new PendingVoiceAction(token, result.Action ?? intent.ActionId!, result.Value, readback, _time.GetUtcNow() + ConfirmTimeout);
            return Remember(VoiceReply.Of(readback) with { ReadbackToken = token });
        }

        if (result.Status == InputResult.Executed)
        {
            return Callout(result);
        }

        return Remember(VoiceReply.Of(result.Status == InputResult.Disabled ? InputDisabled : Unable));
    }

    /// <summary>
    /// Nach dem sofortigen Ausführen: Callout nach der Prüfung ("Speed 250 set"). Fahrwerk und Klappen sagt das Nachlesen an,
    /// sobald die Stellung erreicht ist ("Gear down, three green"); hat das Flugzeug den Wert nicht übernommen: "Not confirmed".
    /// </summary>
    private VoiceReply? Callout(InputResult result)
    {
        if (result.Confirmed == false)
        {
            return Remember(VoiceReply.Of(NotConfirmed));
        }

        if (result.FollowUp is not null)
        {
            return null;
        }

        var text = result.Callout ?? result.Readback;
        return string.IsNullOrWhiteSpace(text) ? null : Remember(VoiceReply.Of(text));
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
        LastExecutedDirectly = false;
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

    // ------------------------------------------------------------------ Checklisten

    private VoiceReply? StartChecklist(string? id)
    {
        if (AirbusChecklists.Find(id) is not { } checklist)
        {
            return Remember(VoiceReply.Of(SayAgain));
        }

        CancelPending(Loc.T("voice.cancel.checklist"));
        return Remember(Checklists.Start(checklist) with { Checklist = true });
    }

    /// <summary>
    /// Satz während einer Checkliste. Rückgabe null = nicht für die Checkliste (normal weiter): Steuerung (Pause), Bestätigung
    /// eines Readbacks und Befehle oder Abfragen mit Anrede ("Kopilot, gear up").
    /// </summary>
    private ChecklistOutcome? HandleChecklist(VoiceIntent intent, string normalized, bool addressed)
    {
        switch (intent.Kind)
        {
            case IntentKind.Pause or IntentKind.Resume:
                return null;
            case IntentKind.Confirm or IntentKind.Negative when Pending is not null:
                return null;
            case IntentKind.Action or IntentKind.Query when addressed:
                return null;
            case IntentKind.Checklist:
                return new ChecklistOutcome(StartChecklist(intent.Value));
            case IntentKind.ChecklistControl when intent.Value == "stop":
                return new ChecklistOutcome(Keep(Checklists.Stop()));
            case IntentKind.ChecklistControl:
                return new ChecklistOutcome(Keep(Checklists.Skip() is { } skipped ? skipped with { Checklist = Checklists.Active is not null } : null));
            case IntentKind.SayAgain:
                return new ChecklistOutcome(Keep(Checklists.Repeat() is { } again ? again with { Checklist = true } : null));
        }

        // Antwort auf die Challenge – Befehle ohne Anrede werden dabei nie ausgeführt ("seat belts on" ist hier eine Antwort)
        var reply = Checklists.Respond(normalized, state.Read());
        return new ChecklistOutcome(Keep(reply is null ? null : reply with { Checklist = Checklists.Active is not null }));
    }

    private sealed record ChecklistOutcome(VoiceReply? Reply);

    private VoiceReply Remember(VoiceReply reply)
    {
        LastReply = reply;
        LastReplyUtc = _time.GetUtcNow();
        return reply;
    }

    private VoiceReply? Keep(VoiceReply? reply) => reply is null ? null : Remember(reply);

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
