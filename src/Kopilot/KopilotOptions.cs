namespace Kopilot;

/// <summary>Einstellungen aus appsettings.json / appsettings.Local.json / Umgebungsvariablen, Abschnitt "Kopilot".</summary>
public sealed class KopilotOptions
{
    public const string Section = "Kopilot";

    /// <summary>
    /// "stdio" (Claude Desktop startet Kopilot selbst) oder "http" (Streamable HTTP auf <see cref="HttpUrl"/>).
    /// Die Kommandozeilenschalter --stdio / --http haben Vorrang.
    /// </summary>
    public string Transport { get; set; } = "http";

    /// <summary>Name, unter dem sich Kopilot bei SimConnect anmeldet.</summary>
    public string ClientName { get; set; } = "Kopilot";

    /// <summary>Wartezeit zwischen Verbindungsversuchen, solange MSFS nicht läuft.</summary>
    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>Timeout für Einzelabfragen (read_simvar).</summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>Adresse des HTTP-Modus. Muss auf Loopback zeigen (127.0.0.1, localhost oder [::1]).</summary>
    public string HttpUrl { get; set; } = "http://127.0.0.1:5155";

    /// <summary>Ordner mit Flugzeugprofilen (*.json), relativ zur Kopilot.exe oder absolut.</summary>
    public string ProfilesDirectory { get; set; } = "profiles";

    /// <summary>Zugriffsschutz und Remote-Betrieb (Cloudflare Tunnel, Claude-Connector).</summary>
    public RemoteOptions Remote { get; set; } = new();
}

/// <summary>
/// Zugriffsschutz für den HTTP-Modus. Geheimnisse gehören in appsettings.Local.json oder Umgebungsvariablen
/// (z. B. <c>Kopilot__Remote__BearerToken</c>), niemals ins Repo.
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>Öffentliche Basis-URL hinter dem Tunnel, z. B. https://kopilot.flo210500.com (ohne /mcp).</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Statischer Bearer-Token für Clients wie Claude Code oder den MCP Inspector. Mindestens 32 Zeichen.</summary>
    public string BearerToken { get; set; } = "";

    /// <summary>Passwort für die OAuth-Freigabeseite (Claude-Connector). Mindestens 12 Zeichen.</summary>
    public string ConsentPassword { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>Datei für registrierte Clients und (gehashte) Tokens. Leer = %LOCALAPPDATA%\Kopilot\oauth-state.json.</summary>
    public string StateFile { get; set; } = "";

    public const int MinBearerTokenLength = 32;

    public const int MinConsentPasswordLength = 12;

    public bool HasBearerToken => BearerToken.Trim().Length >= MinBearerTokenLength;

    /// <summary>OAuth ist aktiv, sobald eine gültige öffentliche HTTPS-URL und ein ausreichend langes Freigabe-Passwort gesetzt sind.</summary>
    public bool OAuthEnabled => PublicBaseUri is not null && ConsentPassword.Length >= MinConsentPasswordLength;

    /// <summary>
    /// Öffentliche Basis-URL, oder null wenn nicht (gültig) gesetzt.
    /// Muss HTTPS sein und darf keinen Pfad haben – die Metadaten liegen unter /.well-known/ auf der Wurzel.
    /// </summary>
    public Uri? PublicBaseUri =>
        Uri.TryCreate(PublicUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
            ? uri
            : null;

    /// <summary>Öffentliche Basis-URL als String ohne abschließenden Schrägstrich.</summary>
    public string Issuer => PublicBaseUri is { } u ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : "";

    /// <summary>Die MCP-URL, die im Claude-Connector eingetragen wird (= OAuth "resource").</summary>
    public string ResourceUrl => Issuer.Length > 0 ? Issuer + "/mcp" : "";

    public string ResolveStateFile() =>
        string.IsNullOrWhiteSpace(StateFile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kopilot", "oauth-state.json")
            : StateFile;
}
