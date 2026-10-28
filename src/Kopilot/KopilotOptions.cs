namespace Kopilot;

/// <summary>Einstellungen aus appsettings.json, Abschnitt "Kopilot".</summary>
public sealed class KopilotOptions
{
    public const string Section = "Kopilot";

    /// <summary>Name, unter dem sich Kopilot bei SimConnect anmeldet.</summary>
    public string ClientName { get; set; } = "Kopilot";

    /// <summary>Wartezeit zwischen Verbindungsversuchen, solange MSFS nicht läuft.</summary>
    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>Timeout für Einzelabfragen (read_simvar).</summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>Adresse des HTTP-Modus (nur ohne --stdio).</summary>
    public string HttpUrl { get; set; } = "http://127.0.0.1:5155";

    /// <summary>Ordner mit Flugzeugprofilen (*.json), relativ zur Kopilot.exe oder absolut.</summary>
    public string ProfilesDirectory { get; set; } = "profiles";
}
