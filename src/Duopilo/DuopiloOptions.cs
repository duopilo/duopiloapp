using Duopilo.Hosting;

namespace Duopilo;

/// <summary>
/// Einstellungen aus appsettings.json / appsettings.Local.json / Umgebungsvariablen, Abschnitt "Duopilo".
/// Der alte Abschnitt "Kopilot" (bis 0.9.x) wird weiter gelesen (<see cref="LegacySectionSource"/>).
/// </summary>
public sealed class DuopiloOptions
{
    public const string Section = AppNames.Product;

    /// <summary>
    /// "stdio" (Claude Desktop startet Duopilo selbst) oder "http" (Streamable HTTP auf <see cref="HttpUrl"/>).
    /// Die Kommandozeilenschalter --stdio / --http haben Vorrang.
    /// </summary>
    public string Transport { get; set; } = "http";

    /// <summary>
    /// Language of the dashboard, tool messages and tray ("en", "de" – see src/Duopilo/i18n). English is the default.
    /// Callouts, checklists, FMA calls, warnings and radio stay English whatever this says (SOP), and so do the logs (issues,
    /// international contributors). Changeable in the dashboard settings.
    /// </summary>
    public string Language { get; set; } = Localization.Loc.DefaultLanguage;

    /// <summary>Name, unter dem sich Duopilo bei SimConnect anmeldet.</summary>
    public string ClientName { get; set; } = AppNames.Product;

    /// <summary>Wartezeit zwischen Verbindungsversuchen, solange MSFS nicht läuft.</summary>
    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>Schnelle Werte: Anzahl übersprungener Sim-Frames zwischen zwei Updates (4 ≈ 10 Hz bei 50 fps).</summary>
    public int FastIntervalFrames { get; set; } = 4;

    /// <summary>Timeout für Einzelabfragen (read_simvar).</summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>Adresse des HTTP-Modus. Muss auf Loopback zeigen (127.0.0.1, localhost oder [::1]).</summary>
    public string HttpUrl { get; set; } = "http://127.0.0.1:5155";

    /// <summary>Ordner mit Flugzeugprofilen (*.json), relativ zur Duopilo.exe oder absolut.</summary>
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

    /// <summary>
    /// Smalltalk mit einem Sprachmodell (Claude, OpenAI-kompatibel oder Ollama) – optional, standardmäßig aus. Nur für freie
    /// Gespräche und Erklärungen, nie für Aktionen. Im Dashboard unter Einstellungen → First Officer und Smalltalk.
    /// </summary>
    public SmalltalkOptions Smalltalk { get; set; } = new();

    /// <summary>Der First Officer der Sprachschleife: Name und Stimme pro Flug, Vorstellung beim Briefing.</summary>
    public FirstOfficerOptions FirstOfficer { get; set; } = new();

    /// <summary>
    /// Datenordner (Logs, Modelle, Caches, Dashboard-Einstellungen). Leer = %LOCALAPPDATA%\Duopilo – beim ersten Start
    /// übernommen aus %LOCALAPPDATA%\Kopilot (<see cref="DataDirectoryMigration"/>).
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// Nach einem Absturz startet Windows die Tray-Instanz neu (RegisterApplicationRestart, erst nach 60 s Laufzeit).
    /// Die ATC-Sitzung kommt dabei aus der Sitzungsdatei zurück.
    /// </summary>
    public bool RestartAfterCrash { get; set; } = true;

    public string ResolveDataDirectory() =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? DataDirectoryMigration.DefaultDirectory
            : DataDirectoryMigration.MapLegacyPath(DataDirectory);
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
    /// Radiohöhen (2500, 1000, 500 …), Hundred above/Minimum und Retard auch von Duopilo ansagen.
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

    /// <summary>Pfad zum Kokoro-Modell. Leer = %LOCALAPPDATA%\Duopilo\models\kokoro.onnx (wird bei Bedarf geladen).</summary>
    public string KokoroModelPath { get; set; } = "";

    /// <summary>CPU-Threads für Kokoro – wenig, damit MSFS nicht ausgebremst wird.</summary>
    public int KokoroThreads { get; set; } = 2;
}

/// <summary>
/// Zugriffsschutz für den HTTP-Modus. Geheimnisse gehören in appsettings.Local.json oder Umgebungsvariablen
/// (z. B. <c>Duopilo__Remote__BearerToken</c>; der alte Name <c>Kopilot__…</c> gilt weiter), niemals ins Repo.
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>Öffentliche Basis-URL hinter dem Tunnel, z. B. https://duopilo.example.com (ohne /mcp).</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Statischer Bearer-Token für Clients wie Claude Code oder den MCP Inspector. Mindestens 32 Zeichen.</summary>
    public string BearerToken { get; set; } = "";

    /// <summary>Passwort für die OAuth-Freigabeseite (Claude-Connector). Mindestens 12 Zeichen.</summary>
    public string ConsentPassword { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>Datei für registrierte Clients und (gehashte) Tokens. Leer = %LOCALAPPDATA%\Duopilo\oauth-state.json.</summary>
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
            ? Path.Combine(DataDirectoryMigration.DefaultDirectory, "oauth-state.json")
            : DataDirectoryMigration.MapLegacyPath(StateFile);
}

public sealed class AtcOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Vorlagendatei. Ein relativer Name wird zuerst in DataDirectory (%LOCALAPPDATA%\Duopilo) gesucht – dort überlebt eine
    /// eigene Kopie jedes Update –, dann neben der Duopilo.exe.
    /// </summary>
    public string TemplatesFile { get; set; } = "atc-templates.json";

    /// <summary>Funksprüche mit den Kokoro-Stimmen der Stationen sprechen (nur Tray-Instanz).</summary>
    public bool Speak { get; set; } = true;

    public const string DefaultSessionFile = "atc-session.json";

    /// <summary>
    /// Die laufende ATC-Sitzung wird hier gesichert (relativ = im DataDirectory) und nach einem Neustart oder Absturz von
    /// Duopilo wiederhergestellt, solange der SimBrief-Plan derselbe ist. Leer = nicht sichern.
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
    /// <summary>
    /// GitHub-Repository für die Issues ("Besitzer/Name"). Leer = nur lokal speichern. Das alte Shin-Jin-Jin/Kopilot aus
    /// einer eigenen appsettings.Local.json wird zu duopilo/duopiloapp (<see cref="ResolveGitHubRepository"/>).
    /// </summary>
    public string GitHubRepository { get; set; } = AppNames.GitHubRepository;

    /// <summary>Issue-Vorlage in .github/ISSUE_TEMPLATE des Repositorys (alt: kopilot-meldung.yml).</summary>
    public string IssueTemplate { get; set; } = AppNames.IssueTemplate;

    /// <summary>Ordner für die lokale Kopie jeder Meldung. Leer = DataDirectory\reports.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Repository für die Links – das alte Repository unter dem Namen Kopilot zählt als das neue.</summary>
    public string ResolveGitHubRepository() =>
        string.Equals((GitHubRepository ?? "").Trim(), AppNames.Legacy.GitHubRepository, StringComparison.OrdinalIgnoreCase)
            ? AppNames.GitHubRepository
            : (GitHubRepository ?? "").Trim();

    /// <summary>Vorlage für die Links – die alte Vorlage kopilot-meldung.yml heißt jetzt duopilo-meldung.yml.</summary>
    public string ResolveIssueTemplate() =>
        string.Equals((IssueTemplate ?? "").Trim(), AppNames.Legacy.IssueTemplate, StringComparison.OrdinalIgnoreCase)
            ? AppNames.IssueTemplate
            : (IssueTemplate ?? "").Trim();
}

public sealed class InputOptions
{
    /// <summary>Eingaben erlauben. Standard: aus. Der Schalter im Dashboard (input.json) hat Vorrang.</summary>
    public bool Enabled { get; set; }

    /// <summary>So lange wartet eine vorbereitete Aktion auf die Bestätigung des Piloten.</summary>
    public int ConfirmWindowSeconds { get; set; } = 60;
}

/// <summary>
/// Smalltalk über ein Sprachmodell: Sätze, die weder Befehl noch Abfrage noch Funkspruch sind, gehen an den gewählten Anbieter.
/// Das Modell bekommt nur lesenden Kontext (Flugphase, Position, Route, Sprit, Wetter) und löst nie etwas aus.
/// Der Schlüssel gehört nur in appsettings.Local.json (oder eine Umgebungsvariable) – nie in Logs oder Fehlerberichte.
/// </summary>
public sealed class SmalltalkOptions
{
    /// <summary>"off" (Standard), "claude" (eigener API-Schlüssel), "openai" (OpenAI-kompatible Schnittstelle) oder "ollama" (anderer Rechner).</summary>
    public string Provider { get; set; } = "off";

    /// <summary>Modell. Leer bei Claude = claude-haiku-5-5; bei OpenAI-kompatibel und Ollama Pflicht.</summary>
    public string Model { get; set; } = "";

    /// <summary>Adresse: OpenAI-kompatibel die Basis-URL (Standard https://api.openai.com/v1), Ollama z. B. http://192.168.1.20:11434.</summary>
    public string Url { get; set; } = "";

    /// <summary>API-Schlüssel (Claude: Pflicht, OpenAI-kompatibel: je nach Server). Nur in appsettings.Local.json.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Antworten höchstens so viele Wörter lang (10–150).</summary>
    public int MaxReplyWords { get; set; } = 40;

    /// <summary>Höchstens so viele Anfragen pro Minute (1–30) – darüber antwortet Duopilo sofort „I can't chat right now“.</summary>
    public int RequestsPerMinute { get; set; } = 6;

    /// <summary>So lange wartet Duopilo höchstens auf eine Antwort (s, 2–30).</summary>
    public int TimeoutSec { get; set; } = 10;

    /// <summary>
    /// Sätze ohne Anrede (Name des First Officers, „Kopilot“ oder „Duopilo“) gehen nur bis zu dieser Wortzahl ans Modell – längere sind
    /// meist Gespräche im Raum. 0 = nur mit Anrede.
    /// </summary>
    public int UnaddressedMaxWords { get; set; } = 12;

    /// <summary>
    /// Deutscher Smalltalk mit Supertonic 3 (Modell OpenRAIL-M, ~400 MB, lädt bei Bedarf). false = nur Piper. Piper bleibt in jedem
    /// Fall der Rückfall, wenn Supertonic nicht geladen werden kann.
    /// </summary>
    public bool Supertonic { get; set; } = true;

    /// <summary>CPU-Threads für Supertonic – wenig, damit MSFS nicht ausgebremst wird.</summary>
    public int SupertonicThreads { get; set; } = 2;

    /// <summary>Eigene piper.exe. Leer = Duopilo lädt Piper bei Bedarf nach %LOCALAPPDATA%\Duopilo\piper.</summary>
    public string PiperPath { get; set; } = "";
}

/// <summary>Der First Officer: pro Flug ein Name aus einer Liste (weiblich/männlich) und eine passende Stimme.</summary>
public sealed class FirstOfficerOptions
{
    /// <summary>Name und Stimme pro Flug. false = Duopilo spricht wie bisher mit der Stimme aus dem Dashboard.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fester Name. Leer = pro Flug zufällig.</summary>
    public string Name { get; set; } = "";

    /// <summary>Geschlecht für einen festen Namen, der nicht in der Liste steht: "auto", "female" oder "male".</summary>
    public string Gender { get; set; } = "auto";

    /// <summary>Vorstellung beim Briefing („Hi, I'm Sam, I'll be your first officer today“).</summary>
    public bool Introduce { get; set; } = true;
}
