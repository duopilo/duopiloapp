namespace Kopilot.Monitoring;

/// <summary>
/// Fahrwerk und Klappen, die das Eingabe-Modul gerade selbst nachliest und ansagt ("Gear down, three green", "Flaps two").
/// Solange ein Eintrag gilt, sagt das Monitoring den Hebelwechsel nicht zusätzlich an. Threadsicher.
/// </summary>
public sealed class ConfigCalloutClaims
{
    public const string Gear = "gear";
    public const string Flaps = "flaps";

    /// <summary>Nach dem Callout noch so lange still, damit das Monitoring den Hebelwechsel nicht hinterher ansagt.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    public void Claim(string item, DateTimeOffset until)
    {
        lock (_lock)
        {
            _until[item] = until;
        }
    }

    /// <summary>Nachlesen beendet: noch <see cref="Grace"/> lang still.</summary>
    public void Release(string item, DateTimeOffset now)
    {
        lock (_lock)
        {
            _until[item] = now + Grace;
        }
    }

    public bool IsClaimed(string item, DateTimeOffset now)
    {
        lock (_lock)
        {
            return _until.TryGetValue(item, out var until) && now <= until;
        }
    }
}
