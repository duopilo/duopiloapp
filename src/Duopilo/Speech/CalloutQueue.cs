using Duopilo.Monitoring;

namespace Duopilo.Speech;

/// <summary>Rangfolge der Sprachausgabe: Warnungen vor Callouts vor Antworten vor ATC vor Smalltalk.</summary>
public enum SpeechPriority
{
    /// <summary>
    /// Smalltalk des First Officers (Sprachmodell, Vorstellung). Ganz unten: Jede andere Ansage – Callout, Warnung, Antwort,
    /// Checkliste, Quittung, ATC – unterbricht ihn sofort, und er wird danach nicht wiederholt.
    /// </summary>
    Smalltalk = -1,

    /// <summary>Funkspruch einer ATC-Station (vom ATC-Modul).</summary>
    Atc = 0,

    /// <summary>Antwort von Duopilo auf den Piloten (Readback, Abfrage, "Say again").</summary>
    Response = 1,

    /// <summary>Callout des Monitorings (Info und Caution).</summary>
    Callout = 2,

    /// <summary>Warning des Monitorings – unterbricht alles andere.</summary>
    Warning = 3,
}

/// <summary>Etwas, das gesprochen werden soll (nicht vom Monitoring, z. B. eine Antwort oder ein ATC-Funkspruch).</summary>
/// <param name="Text">Anzeigetext, z. B. "Speed 250".</param>
/// <param name="Priority">Rang in der Warteschlange.</param>
public sealed record SpeechRequest(string Text, SpeechPriority Priority)
{
    /// <summary>Text für die Sprachsynthese, z. B. "Speed two five zero". Leer = <see cref="Text"/>.</summary>
    public string? Spoken { get; init; }

    /// <summary>Kokoro-Stimme (z. B. "am_michael"). Leer = Stimme von Duopilo.</summary>
    public string? Voice { get; init; }

    /// <summary>Faktor auf die eingestellte Lautstärke (0–1).</summary>
    public double Volume { get; init; } = 1;

    /// <summary>So lange darf die Ansage warten. Leer = Standard je Rang.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>Herkunft für Log und Status, z. B. "kopilot" oder "atc:frankfurt tower".</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Wird aufgerufen, sobald die Ansage ganz gesprochen ist (mit dem Zeitpunkt des Endes) – z. B. damit ein Readback erst danach
    /// bestätigt werden kann. Nicht aufgerufen, wenn sie verfällt, verworfen oder endgültig unterbrochen wird. Läuft auf dem Sprach-Thread.
    /// </summary>
    public Action<DateTimeOffset>? OnSpoken { get; init; }

    /// <summary>Statt Sprache den Warnton spielen (<see cref="ToneGenerator"/>); <see cref="Text"/> steht dann nur in Log und Status.</summary>
    public bool Tone { get; init; }

    /// <summary>
    /// Freier Text (Smalltalk): so sprechen, wie er ist – ohne die Funk-Aussprache (Ziffern einzeln, NATO-Alphabet) und ohne
    /// Zwischenspeicher. <see cref="Voice"/> darf dann auch eine Stimme einer anderen Engine sein ("piper:de_DE-thorsten-medium").
    /// </summary>
    public bool Plain { get; init; }
}

/// <summary>Eine Ansage in der Warteschlange.</summary>
public sealed record SpeechItem
{
    public required string Text { get; init; }

    /// <summary>Text für die Synthese (null = <see cref="Text"/>).</summary>
    public string? Spoken { get; init; }

    public SpeechPriority Priority { get; init; }

    /// <summary>Reihenfolge innerhalb desselben Rangs, höher zuerst (bei Callouts: Caution vor Info).</summary>
    public int Rank { get; init; }

    public DateTimeOffset TimeUtc { get; init; }

    public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Fortlaufende Nummer – bei gleichem Rang kommt die ältere zuerst.</summary>
    public long Seq { get; init; }

    /// <summary>Gleiche Ansage innerhalb von <see cref="CalloutQueue.RepeatWindow"/> nur einmal (null = keine Prüfung).</summary>
    public string? DedupeKey { get; init; }

    public string? Voice { get; init; }

    public double Volume { get; init; } = 1;

    /// <summary>Regel-ID bei Callouts des Monitorings.</summary>
    public string? Rule { get; init; }

    public string? Source { get; init; }

    /// <summary>Schon einmal von einer Warnung unterbrochen und neu eingereiht.</summary>
    public bool Requeued { get; init; }

    /// <summary>Siehe <see cref="SpeechRequest.OnSpoken"/>.</summary>
    public Action<DateTimeOffset>? OnSpoken { get; init; }

    /// <summary>Siehe <see cref="SpeechRequest.Tone"/>.</summary>
    public bool Tone { get; init; }

    /// <summary>Siehe <see cref="SpeechRequest.Plain"/>.</summary>
    public bool Plain { get; init; }

    public bool IsAlert => Priority >= SpeechPriority.Callout;
}

/// <summary>
/// Entscheidet, was gesprochen wird und in welcher Reihenfolge: Warnungen &gt; Callouts &gt; Antworten &gt; ATC.
/// Warnungen unterbrechen alles andere und verdrängen wartende Callouts (Antworten und ATC bleiben stehen),
/// alte Ansagen verfallen, dieselbe Ansage kommt nicht doppelt kurz hintereinander.
/// Threadsicher, ohne Abhängigkeit von Windows – damit testbar.
/// </summary>
public sealed class CalloutQueue
{
    /// <summary>Ältere Callouts werden nicht mehr gesprochen.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(8);

    /// <summary>Antworten von Duopilo warten höchstens so lange.</summary>
    public static readonly TimeSpan ResponseMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>ATC-Funksprüche warten höchstens so lange (z. B. hinter einer Warnung oder während einer Pause).</summary>
    public static readonly TimeSpan AtcMaxAge = TimeSpan.FromSeconds(60);

    /// <summary>Smalltalk wartet höchstens so lange – danach passt er nicht mehr zum Gespräch.</summary>
    public static readonly TimeSpan SmalltalkMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>Dieselbe Ansage innerhalb dieser Zeit nur einmal.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(4);

    private readonly object _lock = new();
    private readonly List<SpeechItem> _items = [];
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public static TimeSpan DefaultMaxAge(SpeechPriority priority) => priority switch
    {
        SpeechPriority.Atc => AtcMaxAge,
        SpeechPriority.Response => ResponseMaxAge,
        SpeechPriority.Smalltalk => SmalltalkMaxAge,
        _ => MaxAge,
    };

    /// <summary>Rang eines Callouts aus dem Monitoring: Warning → Warnung, alles andere → Callout.</summary>
    public static SpeechPriority PriorityOf(AlertSeverity severity) =>
        severity == AlertSeverity.Warning ? SpeechPriority.Warning : SpeechPriority.Callout;

    /// <summary>
    /// Nimmt eine Meldung des Monitorings an, wenn sie gesprochen werden soll. Rückgabe: angenommen, und ob eine laufende,
    /// weniger wichtige Ansage abgebrochen werden soll.
    /// </summary>
    public (bool Accepted, bool Preempt) Offer(AlertEvent e, SpeechSettings settings, DateTimeOffset now, AlertSeverity? speaking) =>
        OfferAlert(e, settings, now, speaking is { } s ? PriorityOf(s) : null);

    /// <inheritdoc cref="Offer(AlertEvent, SpeechSettings, DateTimeOffset, AlertSeverity?)"/>
    public (bool Accepted, bool Preempt) OfferAlert(AlertEvent e, SpeechSettings settings, DateTimeOffset now, SpeechPriority? speaking, double volume = 1)
    {
        if (!settings.Enabled || e.Kind == AlertKind.Clear || string.IsNullOrWhiteSpace(e.Callout)
            || e.Severity < settings.MinSeverity || settings.MutedRules.Contains(e.Rule, StringComparer.OrdinalIgnoreCase))
        {
            return (false, false);
        }

        return Offer(
            new SpeechItem
            {
                Text = e.Callout,
                Priority = PriorityOf(e.Severity),
                Rank = (int)e.Severity,
                TimeUtc = e.TimeUtc,
                ExpiresUtc = e.TimeUtc + MaxAge,
                DedupeKey = e.Callout,
                Rule = e.Rule,
                Volume = volume,
                Source = "monitoring",
            },
            now,
            speaking);
    }

    /// <summary>Nimmt eine Ansage an. Rückgabe: angenommen, und ob die laufende Ansage unterbrochen werden soll.</summary>
    public (bool Accepted, bool Preempt) Offer(SpeechItem item, DateTimeOffset now, SpeechPriority? speaking)
    {
        if (string.IsNullOrWhiteSpace(item.Text) || now > item.ExpiresUtc)
        {
            return (false, false);
        }

        lock (_lock)
        {
            Prune(now);
            if (item.DedupeKey is { } key)
            {
                if (_recent.TryGetValue(key, out var last) && now - last < RepeatWindow)
                {
                    return (false, false);
                }

                _recent[key] = now;
            }

            if (item.Priority == SpeechPriority.Warning)
            {
                // Eine Warnung verdrängt wartende Callouts – Antworten und ATC bleiben stehen
                _items.RemoveAll(i => i.Priority == SpeechPriority.Callout);
            }

            _items.Add(item with { Seq = ++_seq });
            return (true, Interrupts(item.Priority, speaking));
        }
    }

    /// <summary>
    /// Unterbricht eine neue Ansage die laufende? Warnungen unterbrechen alles darunter; Smalltalk wird von allem anderen
    /// unterbrochen (Callouts, Antworten, Checklisten, Quittungen, ATC).
    /// </summary>
    public static bool Interrupts(SpeechPriority incoming, SpeechPriority? speaking) => speaking is { } current
        && ((incoming == SpeechPriority.Warning && current < SpeechPriority.Warning)
            || (current == SpeechPriority.Smalltalk && incoming > SpeechPriority.Smalltalk));

    /// <summary>Unterbrochene Antwort oder ATC-Meldung einmal neu einreihen (mit frischer Wartezeit). Smalltalk nie.</summary>
    public bool Requeue(SpeechItem item, DateTimeOffset now)
    {
        if (item.Requeued || item.IsAlert || item.Priority == SpeechPriority.Smalltalk)
        {
            return false;
        }

        var age = item.ExpiresUtc - item.TimeUtc;
        lock (_lock)
        {
            _items.Add(item with { Requeued = true, TimeUtc = now, ExpiresUtc = now + age, Seq = ++_seq });
            return true;
        }
    }

    /// <summary>Nächste Ansage, ohne sie zu entnehmen. Verfallene werden verworfen.</summary>
    public SpeechItem? Peek(DateTimeOffset now)
    {
        lock (_lock)
        {
            return NextLocked(now);
        }
    }

    /// <summary>Nächste Ansage: wichtigste zuerst, bei gleichem Rang die ältere. Verfallene werden verworfen.</summary>
    public SpeechItem? Take(DateTimeOffset now)
    {
        lock (_lock)
        {
            var next = NextLocked(now);
            if (next is not null)
            {
                _items.Remove(next);
            }

            return next;
        }
    }

    /// <summary>Wirft alle wartenden Ansagen weg, auf die die Bedingung zutrifft.</summary>
    public int Drop(Func<SpeechItem, bool> predicate)
    {
        lock (_lock)
        {
            return _items.RemoveAll(i => predicate(i));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    private SpeechItem? NextLocked(DateTimeOffset now)
    {
        _items.RemoveAll(i => now > i.ExpiresUtc);
        if (_items.Count == 0)
        {
            return null;
        }

        return _items
            .OrderByDescending(i => i.Priority)
            .ThenByDescending(i => i.Rank)
            .ThenBy(i => i.Seq)
            .First();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _recent.Where(r => now - r.Value > RepeatWindow).Select(r => r.Key).ToList())
        {
            _recent.Remove(key);
        }
    }
}
