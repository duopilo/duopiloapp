using Kopilot.Monitoring;

namespace Kopilot.Speech;

/// <summary>
/// Entscheidet, welche Meldungen gesprochen werden und in welcher Reihenfolge:
/// Warnings zuerst und unterbrechen alles andere, alte Meldungen verfallen, dieselbe Ansage kommt nicht doppelt kurz hintereinander.
/// Threadsicher, ohne Abhängigkeit von Windows – damit testbar.
/// </summary>
public sealed class CalloutQueue
{
    /// <summary>Ältere Meldungen werden nicht mehr gesprochen.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(8);

    /// <summary>Dieselbe Ansage innerhalb dieser Zeit nur einmal.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(4);

    private readonly object _lock = new();
    private readonly List<AlertEvent> _items = [];
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Nimmt eine Meldung an, wenn sie gesprochen werden soll. Rückgabe: angenommen, und ob eine laufende,
    /// weniger wichtige Ansage abgebrochen werden soll.
    /// </summary>
    public (bool Accepted, bool Preempt) Offer(AlertEvent e, SpeechSettings settings, DateTimeOffset now, AlertSeverity? speaking)
    {
        if (!settings.Enabled || e.Kind == AlertKind.Clear || string.IsNullOrWhiteSpace(e.Callout)
            || e.Severity < settings.MinSeverity || settings.MutedRules.Contains(e.Rule, StringComparer.OrdinalIgnoreCase)
            || now - e.TimeUtc > MaxAge)
        {
            return (false, false);
        }

        lock (_lock)
        {
            Prune(now);
            if (_recent.TryGetValue(e.Callout, out var last) && now - last < RepeatWindow)
            {
                return (false, false);
            }

            _recent[e.Callout] = now;

            if (e.Severity == AlertSeverity.Warning)
            {
                // Eine Warning verdrängt alles Unwichtigere, was noch wartet
                _items.RemoveAll(i => i.Severity < AlertSeverity.Warning);
            }

            _items.Add(e);
            return (true, e.Severity == AlertSeverity.Warning && speaking is { } current && current < AlertSeverity.Warning);
        }
    }

    /// <summary>Nächste Ansage: wichtigste zuerst, bei gleicher Stufe die älteste. Verfallene werden verworfen.</summary>
    public AlertEvent? Take(DateTimeOffset now)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => now - i.TimeUtc > MaxAge);
            if (_items.Count == 0)
            {
                return null;
            }

            var next = _items.OrderByDescending(i => i.Severity).ThenBy(i => i.Id).First();
            _items.Remove(next);
            return next;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _recent.Where(r => now - r.Value > RepeatWindow).Select(r => r.Key).ToList())
        {
            _recent.Remove(key);
        }
    }
}
