namespace Kopilot.Monitoring;

/// <summary>Ein FMA-Wechsel mit dem, was sich geändert hat, und den Flugdaten in dem Moment.</summary>
/// <param name="Changes">z. B. "vertical: G/S → LAND", "lateral: LAND → FLARE".</param>
public sealed record FmaLogEntry(
    long Id,
    DateTimeOffset TimeUtc,
    IReadOnlyList<string> Changes,
    FmaState State,
    double? IasKt,
    double? AltitudeFt,
    double? RadioAltFt,
    bool? OnGround);

/// <summary>
/// Protokolliert jeden Wechsel der FMA (Schub, vertikal, lateral, armed, AP, FD, A/THR), z. B. um nach einer Autolandung
/// zu prüfen, ob ROLL OUT aktiv war. Threadsicher: geschrieben vom MonitoringService, gelesen von Tools und Dashboard.
/// </summary>
public sealed class FmaLog(int capacity = 400)
{
    private readonly object _lock = new();
    private readonly LinkedList<FmaLogEntry> _entries = new();
    private FmaState? _last;
    private long _lastId;

    /// <summary>Übernimmt den Zustand aus dem Snapshot. Gibt den neuen Eintrag zurück, wenn sich etwas geändert hat.</summary>
    public FmaLogEntry? Update(FlightSnapshot s)
    {
        if (!s.Connected || s.Fma is not { } state)
        {
            return null;
        }

        lock (_lock)
        {
            if (_last is null)
            {
                _last = state;
                if (IsEmpty(state))
                {
                    return null;
                }

                return Add(s, state, ["Start: " + Describe(state)]);
            }

            if (state == _last)
            {
                return null;
            }

            var changes = new List<string>();
            Compare(changes, "Schub", _last.Thrust, state.Thrust);
            Compare(changes, "vertikal", _last.Vertical, state.Vertical);
            Compare(changes, "vertikal armed", _last.VerticalArmed, state.VerticalArmed);
            Compare(changes, "lateral", _last.Lateral, state.Lateral);
            Compare(changes, "lateral armed", _last.LateralArmed, state.LateralArmed);
            Compare(changes, "AP", _last.Autopilot, state.Autopilot);
            Compare(changes, "FD", OnOff(_last.FlightDirector), OnOff(state.FlightDirector));
            Compare(changes, "A/THR", _last.Autothrust, state.Autothrust);
            _last = state;
            return changes.Count == 0 ? null : Add(s, state, changes);
        }
    }

    public IReadOnlyList<FmaLogEntry> Last(int count)
    {
        lock (_lock)
        {
            return _entries.TakeLast(Math.Max(0, count)).ToList();
        }
    }

    public IReadOnlyList<FmaLogEntry> Since(long sinceId)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Id > sinceId).ToList();
        }
    }

    /// <summary>Kam dieser Modus (lateral oder vertikal) seit dem Zeitpunkt vor?</summary>
    public bool Seen(string mode, DateTimeOffset sinceUtc)
    {
        lock (_lock)
        {
            return _entries.Any(e => e.TimeUtc >= sinceUtc
                && (string.Equals(e.State.Lateral, mode, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.State.Vertical, mode, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _last = null;
        }
    }

    private FmaLogEntry Add(FlightSnapshot s, FmaState state, IReadOnlyList<string> changes)
    {
        var entry = new FmaLogEntry(++_lastId, s.TimeUtc, changes, state, s.IasKt, s.AltitudeFt, s.RadioAltFt, s.OnGround);
        _entries.AddLast(entry);
        while (_entries.Count > capacity)
        {
            _entries.RemoveFirst();
        }

        return entry;
    }

    private static void Compare(List<string> changes, string label, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add($"{label}: {before ?? "–"} → {after ?? "–"}");
        }
    }

    private static string? OnOff(bool? value) => value switch { true => "ON", false => "OFF", null => null };

    private static bool IsEmpty(FmaState s) =>
        s.Thrust is null && s.Vertical is null && s.Lateral is null && s.Autopilot is null && s.Autothrust is null;

    public static string Describe(FmaState s) =>
        string.Join(" | ", new[]
        {
            s.Thrust,
            s.Vertical + (s.VerticalArmed is null ? "" : $" ({s.VerticalArmed})"),
            s.Lateral + (s.LateralArmed is null ? "" : $" ({s.LateralArmed})"),
            s.Autopilot,
            s.Autothrust is null ? null : "A/THR " + s.Autothrust,
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
