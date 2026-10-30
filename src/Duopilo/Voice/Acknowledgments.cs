using Duopilo.Monitoring;

namespace Duopilo.Voice;

/// <summary>Eine wichtige Meldung, die auf "check"/"copy" wartet.</summary>
/// <param name="Rule">Regel-ID der Meldung (z. B. "FUEL_WARNING").</param>
/// <param name="Callout">Was bei der Wiederholung gesagt wird (Callout oder Erinnerung, z. B. "Check squawk 2340").</param>
/// <param name="Severity">Schwere der Meldung (Warnungen unterbrechen auch die Wiederholung).</param>
/// <param name="AnnouncedUtc">Zeit der Meldung – ein "check", das später begann, quittiert sie.</param>
/// <param name="DueUtc">Ohne Quittung bis hierher: wiederholen bzw. eskalieren.</param>
/// <param name="Repeats">Schon wiederholt (0 oder 1).</param>
public sealed record PendingAcknowledgment(string Rule, string Callout, AlertSeverity Severity, DateTimeOffset AnnouncedUtc, DateTimeOffset DueUtc, int Repeats = 0);

/// <summary>Was die Sprachschleife tun soll.</summary>
public abstract record AcknowledgmentAction(PendingAcknowledgment Item)
{
    /// <summary>Meldung einmal wiederholen.</summary>
    public sealed record Repeat(PendingAcknowledgment Item) : AcknowledgmentAction(Item);

    /// <summary>Auch nach der Wiederholung keine Antwort: Warnton und Eintrag im Dashboard (<see cref="AcknowledgmentTracker.EscalationRule"/>).</summary>
    public sealed record Escalate(PendingAcknowledgment Item) : AcknowledgmentAction(Item);

    /// <summary>Eine eskalierte Meldung ist erledigt (quittiert oder Zustand vorbei) – Eintrag im Dashboard aufheben.</summary>
    public sealed record Resolved(PendingAcknowledgment Item) : AcknowledgmentAction(Item);
}

/// <summary>
/// Bestätigungspflicht wie in einer echten Crew: Wichtige Meldungen (Fuel-Warnung, Sicherheits-Cautions, Squawk- und
/// Frequenzabweichung, TCAS RA) erwarten "check" oder "copy" des Piloten. Ohne Antwort nach <see cref="RepeatAfter"/> einmal
/// wiederholen, danach eskalieren (Warnton und Eintrag im Dashboard) – nie endlos wiederholen (Two-Communication-Rule:
/// zwei unbeantwortete Ansagen → Verdacht auf Incapacitation). Ist der Zustand vorbei (Meldung aufgehoben), entfällt die Quittung.
/// Rein rechnend, ohne Sprachausgabe – damit testbar. Nicht threadsicher (die Sprachschleife ruft nacheinander auf).
/// </summary>
public sealed class AcknowledgmentTracker
{
    /// <summary>Regel-ID des Dashboard-Eintrags bei fehlender Quittung.</summary>
    public const string EscalationRule = "ACK_MISSING";

    private readonly List<PendingAcknowledgment> _pending = [];
    private readonly Dictionary<string, PendingAcknowledgment> _escalated = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rules;
    private DateTimeOffset? _pausedSince;
    private volatile string[] _pendingTexts = [];
    private volatile string[] _escalatedTexts = [];

    public AcknowledgmentTracker(AcknowledgeSettings? settings = null)
    {
        var s = (settings ?? new AcknowledgeSettings()).Normalized();
        Enabled = s.Enabled;
        RepeatAfter = TimeSpan.FromSeconds(s.RepeatAfterSec);
        _rules = new HashSet<string>(s.Rules, StringComparer.OrdinalIgnoreCase);
    }

    public bool Enabled { get; }

    /// <summary>Wartezeit bis zur Wiederholung und – noch einmal – bis zur Eskalation.</summary>
    public TimeSpan RepeatAfter { get; }

    public IReadOnlyList<PendingAcknowledgment> Pending => _pending;

    /// <summary>Eskalierte Meldungen, die noch nicht quittiert sind.</summary>
    public IReadOnlyCollection<PendingAcknowledgment> Escalated => _escalated.Values;

    /// <summary>Momentaufnahme für den Status (aus einem anderen Thread lesbar): wartende Meldungen.</summary>
    public IReadOnlyList<string> PendingTexts => _pendingTexts;

    /// <summary>Momentaufnahme für den Status: eskalierte Meldungen.</summary>
    public IReadOnlyList<string> EscalatedTexts => _escalatedTexts;

    /// <summary>Braucht diese Meldung eine Quittung? Ab Caution, mit Text zum Wiederholen, Regel in der Liste.</summary>
    public bool Requires(AlertEvent e) =>
        Enabled && e.Kind != AlertKind.Clear && e.Severity >= AlertSeverity.Caution && _rules.Contains(e.Rule)
        && !string.IsNullOrWhiteSpace(e.Reminder ?? e.Callout);

    /// <summary>Neue Meldung aus dem AlertBus. Rückgabe: Aktionen (Eintrag im Dashboard aufheben, wenn der Zustand vorbei ist).</summary>
    public IReadOnlyList<AcknowledgmentAction> Observe(AlertEvent e)
    {
        if (!Enabled || e.Rule == EscalationRule)
        {
            return [];
        }

        if (e.Kind == AlertKind.Clear)
        {
            // Zustand vorbei (z. B. Sink rate korrigiert): keine Quittung mehr nötig
            _pending.RemoveAll(p => p.Rule == e.Rule);
            var removed = _escalated.Remove(e.Rule, out var resolved);
            Snapshot();
            return removed ? [new AcknowledgmentAction.Resolved(resolved!)] : [];
        }

        if (!Requires(e))
        {
            return [];
        }

        // Dieselbe Regel nur einmal offen – die neueste Ansage zählt
        _pending.RemoveAll(p => p.Rule == e.Rule);
        _pending.Add(new PendingAcknowledgment(e.Rule, (e.Reminder ?? e.Callout)!.Trim(), e.Severity, e.TimeUtc, e.TimeUtc + RepeatAfter));
        Snapshot();
        return [];
    }

    /// <summary>
    /// "Check"/"Copy" des Piloten: quittiert alle Meldungen, die vor dem Beginn des Satzes kamen, und eskalierte Meldungen.
    /// Rückgabe: Aktionen (eskalierte Meldungen aufheben).
    /// </summary>
    public IReadOnlyList<AcknowledgmentAction> Acknowledge(DateTimeOffset spokenFromUtc)
    {
        _pending.RemoveAll(p => p.AnnouncedUtc <= spokenFromUtc);
        var resolved = _escalated.Values.Select(e => (AcknowledgmentAction)new AcknowledgmentAction.Resolved(e)).ToList();
        _escalated.Clear();
        Snapshot();
        return resolved;
    }

    /// <summary>
    /// Regelmäßig aufrufen. Pausiert der Sim, laufen die Wartezeiten nicht weiter. Rückgabe: zu wiederholende und zu eskalierende Meldungen.
    /// </summary>
    public IReadOnlyList<AcknowledgmentAction> Tick(DateTimeOffset now, bool paused)
    {
        if (paused)
        {
            _pausedSince ??= now;
            return [];
        }

        if (_pausedSince is { } since)
        {
            // Nach der Pause: Wartezeiten um die Pause verschieben
            var pause = now - since;
            _pausedSince = null;
            for (var i = 0; i < _pending.Count; i++)
            {
                _pending[i] = _pending[i] with { DueUtc = _pending[i].DueUtc + pause };
            }
        }

        var actions = new List<AcknowledgmentAction>();
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueUtc)
            {
                continue;
            }

            if (item.Repeats == 0)
            {
                var repeated = item with { Repeats = 1, DueUtc = now + RepeatAfter };
                _pending[i] = repeated;
                actions.Add(new AcknowledgmentAction.Repeat(repeated));
            }
            else
            {
                _pending.RemoveAt(i);
                _escalated[item.Rule] = item;
                actions.Add(new AcknowledgmentAction.Escalate(item));
            }
        }

        actions.Reverse();
        if (actions.Count > 0)
        {
            Snapshot();
        }

        return actions;
    }

    /// <summary>Alles vergessen (z. B. Sprachschleife stumm – der Pilot kann nicht antworten). Eskalierte Einträge bleiben.</summary>
    public void Clear()
    {
        if (_pending.Count > 0)
        {
            _pending.Clear();
            Snapshot();
        }
    }

    private void Snapshot()
    {
        _pendingTexts = _pending.Select(p => p.Callout).ToArray();
        _escalatedTexts = _escalated.Values.Select(p => p.Callout).ToArray();
    }
}
