using Kopilot.Monitoring;

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

    /// <summary>Schnelle Werte: Anzahl übersprungener Sim-Frames zwischen zwei Updates (4 ≈ 10 Hz bei 50 fps).</summary>
    public int FastIntervalFrames { get; set; } = 4;

    /// <summary>Timeout für Einzelabfragen (read_simvar).</summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>Adresse des HTTP-Modus. Muss auf Loopback zeigen (127.0.0.1, localhost oder [::1]).</summary>
    public string HttpUrl { get; set; } = "http://127.0.0.1:5155";

    /// <summary>Ordner mit Flugzeugprofilen (*.json), relativ zur Kopilot.exe oder absolut.</summary>
    public string ProfilesDirectory { get; set; } = "profiles";

    /// <summary>Zugriffsschutz und Remote-Betrieb (Cloudflare Tunnel, Claude-Connector).</summary>
    public RemoteOptions Remote { get; set; } = new();

    /// <summary>SimBrief-Anbindung für Flugplan und Fortschritt.</summary>
    public SimBriefOptions SimBrief { get; set; } = new();

    /// <summary>Aktives Monitoring.</summary>
    public MonitoringOptions Monitoring { get; set; } = new();

    /// <summary>Eigene Sprachausgabe der Callouts (Kokoro, lokal). Nur im HTTP-Modus mit Tray.</summary>
    public SpeechOptions Speech { get; set; } = new();

    /// <summary>Ordner für Caches (ECAM-Tabellen). Leer = %LOCALAPPDATA%\Kopilot.</summary>
    public string DataDirectory { get; set; } = "";

    public string ResolveDataDirectory() =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kopilot")
            : DataDirectory;
}

public sealed class SimBriefOptions
{
    /// <summary>SimBrief-Benutzername (alternativ PilotId).</summary>
    public string Username { get; set; } = "";

    /// <summary>SimBrief Pilot-ID (Account Settings).</summary>
    public string PilotId { get; set; } = "";

    /// <summary>Wie lange ein geladenes OFP wiederverwendet wird, bevor neu geladen wird.</summary>
    public int CacheMinutes { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(PilotId);
}

public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Takt der Regelauswertung.</summary>
    public int IntervalMs { get; set; } = 200;

    /// <summary>Regeln, die ausgeschaltet sein sollen (IDs, z. B. "SPEED_DEVIATION").</summary>
    public List<string> DisabledRules { get; set; } = [];

    /// <summary>Wie viele vergangene Ereignisse für get_alerts aufgehoben werden.</summary>
    public int HistorySize { get; set; } = 500;
}

/// <summary>
/// Sprachausgabe mit Kokoro-82M (lokal, ONNX auf der CPU). Das Modell (~310 MB) wird beim ersten Start
/// nach <c>%LOCALAPPDATA%\Kopilot\models</c> geladen. An/aus und Stimme lassen sich im Dashboard umschalten,
/// diese Auswahl wird in <c>speech.json</c> gemerkt und hat Vorrang vor den Werten hier.
/// </summary>
public sealed class SpeechOptions
{
    /// <summary>false = Modul gar nicht starten (kein Modell, kein Download). Das Dashboard liest dann wieder im Browser vor.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Kokoro-Stimme, z. B. bm_george, am_michael, af_heart (a = amerikanisch, b = britisch, f/m = weiblich/männlich).</summary>
    public string Voice { get; set; } = "bm_george";

    /// <summary>Sprechtempo, 1.0 = normal (0.7–1.4).</summary>
    public double Speed { get; set; } = 1.1;

    /// <summary>Lautstärke 0.0–1.0.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Teil des Gerätenamens für die Ausgabe (z. B. "Headset"). Leer = Windows-Standardgerät.</summary>
    public string OutputDevice { get; set; } = "";

    /// <summary>Leiseste Stufe, die gesprochen wird: Info, Caution oder Warning.</summary>
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Info;

    /// <summary>Regeln, die nur angezeigt, aber nicht gesprochen werden (IDs, z. B. "RUNWAY_ASSIGNED").</summary>
    public List<string> MutedRules { get; set; } = [];

    /// <summary>Ältere Callouts werden verworfen statt verspätet gesprochen.</summary>
    public double MaxAgeSeconds { get; set; } = 5;

    /// <summary>CPU-Threads für die Sprachsynthese. Wenig halten, damit MSFS nicht ruckelt.</summary>
    public int Threads { get; set; } = 4;

    /// <summary>Pfad zu kokoro.onnx. Leer = %LOCALAPPDATA%\Kopilot\models\kokoro.onnx (wird automatisch geladen).</summary>
    public string ModelPath { get; set; } = "";
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
