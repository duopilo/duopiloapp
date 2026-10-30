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

    /// <summary>
    /// Language of the dashboard, tool messages and tray ("en", "de" – see src/Kopilot/i18n). English is the default.
    /// Callouts, checklists, FMA calls, warnings and radio stay English whatever this says (SOP), and so do the logs (issues,
    /// international contributors). Changeable in the dashboard settings.
    /// </summary>
    public string Language { get; set; } = Localization.Loc.DefaultLanguage;

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

    /// <summary>Sprachausgabe der Callouts über Windows.</summary>
    public SpeechOptions Speech { get; set; } = new();

    /// <summary>Eingabe-Modul: Schalter und Knöpfe im Cockpit nach Bestätigung durch den Piloten.</summary>
    public InputOptions Input { get; set; } = new();

    /// <summary>ATC-Modul: Zustandsmaschine mit festen Textvorlagen (atc-templates.json).</summary>
    public AtcOptions Atc { get; set; } = new();

    /// <summary>Knopf „Fehler melden“ im Dashboard: GitHub-Issue mit Kontext, Kopie im Datenordner.</summary>
    public ReportOptions Reports { get; set; } = new();

    /// <summary>Ordner für Caches (ECAM-Tabellen). Leer = %LOCALAPPDATA%\Kopilot.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// Nach einem Absturz startet Windows die Tray-Instanz neu (RegisterApplicationRestart, erst nach 60 s Laufzeit).
    /// Die ATC-Sitzung kommt dabei aus der Sitzungsdatei zurück.
    /// </summary>
    public bool RestartAfterCrash { get; set; } = true;

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

    /// <summary>
    /// Radiohöhen (2500, 1000, 500 …), Hundred above/Minimum und Retard auch von Kopilot ansagen.
    /// Standard aus – der FBW A32NX und andere Airliner sprechen sie selbst. Im Dashboard umschaltbar.
    /// </summary>
    public bool AircraftAutoCallouts { get; set; }

    /// <summary>Taxi-Speed-Warnung (TAXI_SPEED) ab dieser Groundspeed am Boden. voice-settings.json hat Vorrang.</summary>
    public double TaxiSpeedKt { get; set; } = 25;

    /// <summary>Taxi-Speed-Warnung frühestens nach so vielen Sekunden erneut.</summary>
    public double TaxiSpeedCooldownSec { get; set; } = 30;

    /// <summary>Regel TRAFFIC_PROXIMITY (abschalten über <see cref="DisabledRules"/>).</summary>
    public TrafficMonitoringOptions Traffic { get; set; } = new();

    /// <summary>Fuel Check im Reiseflug: Regeln FUEL_CHECK und FUEL_WARNING (abschalten über <see cref="DisabledRules"/>).</summary>
    public FuelCheckOptions FuelCheck { get; set; } = new();
}

/// <summary>
/// Fuel Check: an jedem OFP-Wegpunkt im Reiseflug Ist gegen Plan ansagen, auf langen Strecken ohne Wegpunkt im Intervall.
/// Warnung bei wachsender Abweichung (Leckverdacht) oder wenn die prognostizierte Landemenge unter Minimum + Puffer fällt.
/// </summary>
public sealed class FuelCheckOptions
{
    /// <summary>Intervall-Check nach so vielen Minuten (Sim-Zeit) im Reiseflug ohne Check. 0 = aus.</summary>
    public double IntervalMinutes { get; set; } = 30;

    /// <summary>Warnung, wenn die Abweichung gegen den Plan seit dem letzten Check um mehr als so viele kg schlechter wird. 0 = aus.</summary>
    public double DeviationIncreaseKg { get; set; } = 300;

    /// <summary>Puffer über der Mindest-Landemenge (höchstens die Hälfte des geplanten Überschusses über dem Minimum).</summary>
    public double LandingBufferKg { get; set; } = 500;

    /// <summary>Wegpunkt-Checks frühestens so viele Minuten nach dem letzten Check. 0 = an jedem Wegpunkt.</summary>
    public double MinSpacingMinutes { get; set; }
}

/// <summary>Verkehrswarnung des Monitorings: Ziel näher als <see cref="ProximityRangeNm"/> und innerhalb ±<see cref="ProximityAltitudeFt"/>.</summary>
public sealed class TrafficMonitoringOptions
{
    public double ProximityRangeNm { get; set; } = 3;

    public double ProximityAltitudeFt { get; set; } = 1000;

    /// <summary>Nach dem Ende einer Warnung so lange keine neue.</summary>
    public int ProximityCooldownSec { get; set; } = 60;

    /// <summary>Abstand der Verkehrsabfragen im Hintergrund (nur in der Luft und wenn die Regel an ist).</summary>
    public int ScanIntervalSec { get; set; } = 3;

    /// <summary>
    /// Womit die Höhe der Ziele (PLANE ALTITUDE) verglichen wird – gilt für get_traffic und die Verkehrswarnung.
    /// Pressure (Standard): eigene Druckhöhe. Passt zum Live-Traffic von MSFS, der seine barometrische Höhe aus ADS-B liefert.
    /// True: eigene wahre Höhe (Verhalten bis 0.9) – für Ziele, deren PLANE ALTITUDE eine wahre Höhe ist
    /// (bei KI-Verkehr und Multiplayer noch nicht im Sim geprüft).
    /// </summary>
    public TrafficAltitudeReference AltitudeReference { get; set; } = TrafficAltitudeReference.Pressure;

    /// <summary>
    /// Jede Antwort der Verkehrsabfrage (Größe, Objekt, Anzahl) nach logs\traffic-messages.csv schreiben – zum Prüfen der
    /// Absturz-Hypothesen aus dem Testlog 0.9. Rund 250 KB pro Flugstunde, bei 10 MB wird rotiert.
    /// </summary>
    public bool MessageLog { get; set; } = true;
}

/// <summary>Eigene Bezugshöhe für die Höhendifferenz zum Verkehr.</summary>
public enum TrafficAltitudeReference
{
    /// <summary>PRESSURE ALTITUDE (Bezug 1013,25 hPa), wie TCAS und Flugflächen.</summary>
    Pressure,

    /// <summary>PLANE ALTITUDE (wahre Höhe über MSL).</summary>
    True,
}

/// <summary>Sprachausgabe über Windows (nur HTTP-Modus mit Tray). Die Einstellungen aus dem Dashboard liegen in DataDirectory\speech.json.</summary>
public sealed class SpeechOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Name der Stimme (z. B. "Microsoft George"). Leer = erste englische Stimme (en-GB bevorzugt).</summary>
    public string Voice { get; set; } = "";

    /// <summary>Ausgabegerät (Windows-Geräte-ID). Leer = Standardgerät.</summary>
    public string Device { get; set; } = "";

    /// <summary>Lautstärke 0–1.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Sprechtempo 0,5–2.</summary>
    public double Rate { get; set; } = 1.1;

    /// <summary>Ab welcher Stufe gesprochen wird: Info, Caution oder Warning.</summary>
    public string MinSeverity { get; set; } = "Info";

    /// <summary>
    /// "auto" (englische Windows-Stimme, wenn installiert, sonst Kokoro), "windows" oder "kokoro".
    /// Kokoro ist eine neuronale englische Stimme, die lokal auf der CPU rechnet (Modell ~310 MB, Download beim ersten Bedarf).
    /// </summary>
    public string Engine { get; set; } = "auto";

    /// <summary>Kokoro-Stimme, z. B. bm_george, bf_emma, am_michael, af_heart (a/b = US/GB, f/m = weiblich/männlich).</summary>
    public string KokoroVoice { get; set; } = "bm_george";

    /// <summary>Pfad zum Kokoro-Modell. Leer = %LOCALAPPDATA%\Kopilot\models\kokoro.onnx (wird bei Bedarf geladen).</summary>
    public string KokoroModelPath { get; set; } = "";

    /// <summary>CPU-Threads für Kokoro – wenig, damit MSFS nicht ausgebremst wird.</summary>
    public int KokoroThreads { get; set; } = 2;
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

public sealed class AtcOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Vorlagendatei. Ein relativer Name wird zuerst in DataDirectory (%LOCALAPPDATA%\Kopilot) gesucht – dort überlebt eine
    /// eigene Kopie jedes Update –, dann neben der Kopilot.exe.
    /// </summary>
    public string TemplatesFile { get; set; } = "atc-templates.json";

    /// <summary>Funksprüche mit den Kokoro-Stimmen der Stationen sprechen (nur Tray-Instanz).</summary>
    public bool Speak { get; set; } = true;

    public const string DefaultSessionFile = "atc-session.json";

    /// <summary>
    /// Die laufende ATC-Sitzung wird hier gesichert (relativ = im DataDirectory) und nach einem Neustart oder Absturz von
    /// Kopilot wiederhergestellt, solange der SimBrief-Plan derselbe ist. Leer = nicht sichern.
    /// Die stdio-Instanz nimmt automatisch eine eigene Datei (atc-session-stdio.json).
    /// </summary>
    public string SessionFile { get; set; } = DefaultSessionFile;

    /// <summary>Ältere Sicherungen werden nicht wiederhergestellt (Stunden).</summary>
    public double SessionMaxAgeHours { get; set; } = 24;

    /// <summary>
    /// Squawk vom MSFS-ATC übernehmen: Ändert der Sim den Transpondercode im Flug selbst (auch bei stumm geschaltetem
    /// Sim-ATC), gilt der neue Code als zugewiesen – statt "check squawk, should be …". Nur vergebene Codes, nie 2000/1200/7000
    /// oder Notfallcodes.
    /// </summary>
    public bool SquawkFromSim { get; set; }
}

/// <summary>Fehlermeldungen aus dem Dashboard (Knopf „Fehler melden“).</summary>
public sealed class ReportOptions
{
    /// <summary>GitHub-Repository für die Issues ("Besitzer/Name"). Leer = nur lokal speichern.</summary>
    public string GitHubRepository { get; set; } = "Shin-Jin-Jin/Kopilot";

    /// <summary>Issue-Vorlage in .github/ISSUE_TEMPLATE des Repositorys.</summary>
    public string IssueTemplate { get; set; } = "kopilot-meldung.yml";

    /// <summary>Ordner für die lokale Kopie jeder Meldung. Leer = DataDirectory\reports.</summary>
    public string Directory { get; set; } = "";
}

public sealed class InputOptions
{
    /// <summary>Eingaben erlauben. Standard: aus. Der Schalter im Dashboard (input.json) hat Vorrang.</summary>
    public bool Enabled { get; set; }

    /// <summary>So lange wartet eine vorbereitete Aktion auf die Bestätigung des Piloten.</summary>
    public int ConfirmWindowSeconds { get; set; } = 60;
}
