namespace Duopilo.Traffic;

/// <summary>
/// Flughäfen, die für den eigenen KI-Verkehr gesperrt sind. Bei Mayday gehört der Platz der Feuerwehr: Ihre Fahrzeuge kreuzen mit
/// Blaulicht jede Bahn ohne Halt und ohne Verkehrsabfrage (Nachtest EDDK 05.11., Florian: „beim Mayday ist der Flughafen eh zu“).
/// Die Feuerwehr setzt die Sperre (<see cref="Duopilo.Fire.FireService"/>), der Verkehrstest fragt sie ab: Kein Flugzeug landet dort
/// (Durchstarten), keins kreuzt eine Bahn oder reiht sich zum Start auf – außer dem Flugzeug des Notfalls selbst. Den Live-Verkehr des
/// Sims steuert Duopilo nicht.
/// </summary>
public sealed class AirportClosures
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Reason, string? ExceptTail)> _closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flughafen sperren (oder Grund und Ausnahme ändern). <paramref name="exceptTail"/>: das Flugzeug des Notfalls (Verkehrstest).</summary>
    /// <returns>True, wenn er vorher nicht gesperrt war.</returns>
    public bool Close(string icao, string reason, string? exceptTail)
    {
        lock (_lock)
        {
            var added = !_closed.ContainsKey(icao);
            _closed[icao] = (reason, exceptTail);
            return added;
        }
    }

    /// <summary>Sperre aufheben.</summary>
    /// <returns>True, wenn er gesperrt war.</returns>
    public bool Open(string icao)
    {
        lock (_lock)
        {
            return _closed.Remove(icao);
        }
    }

    /// <summary>Die gesperrten Flughäfen.</summary>
    public IReadOnlyList<string> Closed
    {
        get
        {
            lock (_lock)
            {
                return [.. _closed.Keys];
            }
        }
    }

    /// <summary>Grund der Sperre für ein Flugzeug (Kennzeichen des Verkehrstests) an diesem Flughafen, sonst null.</summary>
    public string? ClosedFor(string? icao, string? tail)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        lock (_lock)
        {
            return _closed.TryGetValue(icao.Trim(), out var closure)
                && !(tail is not null && string.Equals(closure.ExceptTail, tail, StringComparison.OrdinalIgnoreCase))
                ? closure.Reason
                : null;
        }
    }
}
