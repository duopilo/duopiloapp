namespace Duopilo.Sim;

/// <summary>Läuft der Sim so, dass Duopilo mitreden soll?</summary>
public enum SimActivityState
{
    /// <summary>Verbunden, Flug geladen, nicht pausiert.</summary>
    Running,

    /// <summary>SimConnect nicht verbunden (MSFS aus oder noch nicht bereit).</summary>
    NotConnected,

    /// <summary>Verbunden, aber kein Flug geladen (Hauptmenü, Weltkarte, Ladebildschirm).</summary>
    NoFlight,

    /// <summary>Flug geladen, aber pausiert.</summary>
    Paused,
}

/// <summary>Wechsel der Sprachschleife zwischen aktiv und inaktiv (<see cref="SimGate"/>).</summary>
/// <param name="Gated">true = ab jetzt inaktiv, false = wieder aktiv.</param>
/// <param name="State">Neuer Zustand des Sims.</param>
/// <param name="InactiveFor">Beim Wechsel zurück: so lange war die Schleife inaktiv.</param>
public sealed record SimGateChange(bool Gated, SimActivityState State, TimeSpan? InactiveFor = null);

/// <summary>
/// Entscheidet, ob die Sprachschleife arbeitet: nur, wenn der Sim läuft. Nicht verbunden und kein Flug geladen sperren immer,
/// Pause nur mit silentWhenPaused (voice-settings.json, Standard an). Rein rechnend, damit testbar; nicht threadsicher (die
/// Sprachschleife fragt im Sekundentakt auf ihrem Erkennungs-Thread).
/// </summary>
public sealed class SimGate
{
    /// <summary>Zustand aus den SimConnect-Werten. Kein Flug heißt: FlightRunning ist nicht ausdrücklich true.</summary>
    public static SimActivityState Read(bool connected, bool? flightRunning, bool paused) =>
        !connected ? SimActivityState.NotConnected
        : flightRunning != true ? SimActivityState.NoFlight
        : paused ? SimActivityState.Paused
        : SimActivityState.Running;

    /// <summary>Zustand eines SimConnect-Clients.</summary>
    public static SimActivityState Read(SimConnectClient sim)
    {
        var status = sim.Status;
        return Read(sim.Connection.IsConnected, status.FlightRunning, status.Paused);
    }

    /// <summary>Sperrt dieser Zustand die Sprachschleife?</summary>
    public static bool Blocks(SimActivityState state, bool silentWhenPaused) => state switch
    {
        SimActivityState.Running => false,
        SimActivityState.Paused => silentWhenPaused,
        _ => true,
    };

    /// <summary>Kennung für Status und Dashboard ("notConnected", "noFlight", "paused", "running").</summary>
    public static string Code(SimActivityState state) => state switch
    {
        SimActivityState.NotConnected => "notConnected",
        SimActivityState.NoFlight => "noFlight",
        SimActivityState.Paused => "paused",
        _ => "running",
    };

    public SimActivityState State { get; private set; } = SimActivityState.Running;

    /// <summary>Sprachschleife gerade inaktiv.</summary>
    public bool Gated { get; private set; }

    /// <summary>Seit wann inaktiv (null = aktiv).</summary>
    public DateTimeOffset? GatedSince { get; private set; }

    /// <summary>Neuen Zustand übernehmen. Rückgabe: der Wechsel aktiv ↔ inaktiv, sonst null (auch, wenn nur der Grund wechselt).</summary>
    public SimGateChange? Update(SimActivityState state, bool silentWhenPaused, DateTimeOffset now)
    {
        State = state;
        var gated = Blocks(state, silentWhenPaused);
        if (gated == Gated)
        {
            return null;
        }

        Gated = gated;
        if (gated)
        {
            GatedSince = now;
            return new SimGateChange(true, state);
        }

        var inactive = GatedSince is { } since && now > since ? now - since : TimeSpan.Zero;
        GatedSince = null;
        return new SimGateChange(false, state, inactive);
    }

    /// <summary>Neu beginnen (Sprachschleife neu gestartet): aktiv, bis der erste Zustand kommt.</summary>
    public void Reset()
    {
        State = SimActivityState.Running;
        Gated = false;
        GatedSince = null;
    }
}

/// <summary>
/// Uhr, die nur läuft, solange der Sim läuft (<see cref="Update"/> regelmäßig mit dem Zustand aufrufen). Pause, Hauptmenü und
/// getrennte Verbindung zählen nicht – so laufen Wartezeiten des ATC-Moduls in Sim-Zeit. Die Zeit liegt um die gesamte
/// Haltezeit hinter der echten Uhr. Threadsicher.
/// </summary>
public sealed class SimClock(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private TimeSpan _stopped;
    private DateTimeOffset? _stoppedSince;

    /// <summary>Läuft die Uhr gerade?</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _stoppedSince is null;
            }
        }
    }

    /// <summary>Sim-Zeit: echte Zeit minus alle Haltezeiten; steht, solange der Sim nicht läuft.</summary>
    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return (_stoppedSince ?? _time.GetUtcNow()) - _stopped;
            }
        }
    }

    /// <summary>Alle bisherigen Haltezeiten zusammen (die laufende mitgerechnet) – zum Sichern.</summary>
    public TimeSpan StoppedTotal
    {
        get
        {
            lock (_gate)
            {
                return _stopped + (_stoppedSince is { } since ? _time.GetUtcNow() - since : TimeSpan.Zero);
            }
        }
    }

    /// <summary>Uhr anhalten (false) oder weiterlaufen lassen (true).</summary>
    public void Update(bool running)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!running)
            {
                _stoppedSince ??= now;
            }
            else if (_stoppedSince is { } since)
            {
                _stopped += now - since;
                _stoppedSince = null;
            }
        }
    }

    /// <summary>Haltezeit aus einer Sicherung übernehmen (nach einem Neustart von Duopilo geht die Sim-Zeit dort weiter).</summary>
    public void Restore(TimeSpan stoppedTotal)
    {
        lock (_gate)
        {
            _stopped = stoppedTotal > TimeSpan.Zero ? stoppedTotal : TimeSpan.Zero;
            _stoppedSince = _stoppedSince is null ? null : _time.GetUtcNow();
        }
    }
}
