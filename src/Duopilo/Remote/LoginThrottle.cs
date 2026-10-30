namespace Duopilo.Remote;

/// <summary>
/// Schutz gegen Passwort-Raten auf der Freigabeseite: nach 5 Fehlversuchen innerhalb von 15 Minuten
/// ist die Freigabe 15 Minuten lang gesperrt (global – es gibt nur einen Nutzer).
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public LoginThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsLocked(out DateTimeOffset until)
    {
        lock (_lock)
        {
            until = _lockedUntil;
            return _lockedUntil > _time.GetUtcNow();
        }
    }

    public void RegisterFailure()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _failures.Enqueue(now);
            while (_failures.Count > 0 && _failures.Peek() < now - Window)
            {
                _failures.Dequeue();
            }

            if (_failures.Count >= MaxFailures)
            {
                _lockedUntil = now + LockDuration;
                _failures.Clear();
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _failures.Clear();
        }
    }
}
