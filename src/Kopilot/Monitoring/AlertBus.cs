namespace Kopilot.Monitoring;

/// <summary>
/// Sammelt die Meldungen des Monitorings: Verlauf (Ringpuffer), aktive Zustände und Warten auf neue Meldungen.
/// Threadsicher – geschrieben vom MonitoringService, gelesen von den MCP-Tools.
/// </summary>
public sealed class AlertBus
{
    /// <summary>Wie lange einmalige Ereignisse in "activeAlerts" sichtbar bleiben.</summary>
    public static readonly TimeSpan RecentEventWindow = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly LinkedList<AlertEvent> _history = new();
    private readonly Dictionary<string, AlertEvent> _active = new(StringComparer.Ordinal);
    private long _lastId;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AlertBus(int capacity = 500)
    {
        _capacity = Math.Max(10, capacity);
    }

    public long LastId
    {
        get
        {
            lock (_lock)
            {
                return _lastId;
            }
        }
    }

    /// <summary>Vergibt IDs und legt die Meldungen ab. Gibt die gespeicherten Meldungen zurück.</summary>
    public IReadOnlyList<AlertEvent> Publish(IEnumerable<AlertEvent> events)
    {
        List<AlertEvent> stored = [];
        TaskCompletionSource? toRelease = null;
        lock (_lock)
        {
            foreach (var e in events)
            {
                var item = e with { Id = ++_lastId };
                stored.Add(item);
                _history.AddLast(item);
                while (_history.Count > _capacity)
                {
                    _history.RemoveFirst();
                }

                switch (item.Kind)
                {
                    case AlertKind.Raise:
                        _active[item.Rule] = item;
                        break;
                    case AlertKind.Clear:
                        _active.Remove(item.Rule);
                        break;
                }
            }

            if (stored.Count > 0)
            {
                toRelease = _signal;
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        toRelease?.TrySetResult();
        return stored;
    }

    /// <summary>Meldungen mit Id &gt; <paramref name="sinceId"/>, älteste zuerst, höchstens <paramref name="max"/> (die neuesten).</summary>
    public IReadOnlyList<AlertEvent> Since(long sinceId, int max = 50)
    {
        lock (_lock)
        {
            var list = _history.Where(e => e.Id > sinceId).ToList();
            return list.Count > max ? list.GetRange(list.Count - max, max) : list;
        }
    }

    /// <summary>Aktive Zustände plus einmalige Ereignisse der letzten Sekunden, wichtigste zuerst.</summary>
    public IReadOnlyList<AlertEvent> Active(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var recent = _history.Where(e => e.Kind == AlertKind.Event && now - e.TimeUtc <= RecentEventWindow);
            return _active.Values
                .Concat(recent)
                .OrderByDescending(e => e.Severity)
                .ThenByDescending(e => e.Id)
                .ToList();
        }
    }

    /// <summary>Wartet, bis eine Meldung mit Id &gt; <paramref name="sinceId"/> vorliegt, höchstens <paramref name="timeout"/>.</summary>
    public async Task<IReadOnlyList<AlertEvent>> WaitAsync(long sinceId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task waitFor;
            lock (_lock)
            {
                if (_lastId > sinceId)
                {
                    break;
                }

                waitFor = _signal.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return [];
            }

            try
            {
                await waitFor.WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }

        return Since(sinceId);
    }
}
