namespace Duopilo.Hosting;

/// <summary>
/// Produktname, Adressen und der alte Name. Bis 0.9.x hieß Duopilo „Kopilot“ – alles, was unter dem alten Namen
/// existiert, wird weiter gelesen: Konfigurationsabschnitt und Umgebungsvariablen, Datenordner, Dashboard-Header,
/// Autostart-Eintrag und Repository-Links. Programmdatei und Installer-Name kommen aus dem Projekt
/// (&lt;AssemblyName&gt;, &lt;Product&gt; in Directory.Build.props).
/// </summary>
public static class AppNames
{
    /// <summary>Produktname: Konfigurationsabschnitt, Datenordner, Autostart-Eintrag, MCP-Servername.</summary>
    public const string Product = "Duopilo";

    /// <summary>Webseite (Hauptadresse, festgelegt am 30.10.2026).</summary>
    public const string Website = "https://duopilo.app";

    /// <summary>GitHub-Repository ("Besitzer/Name").</summary>
    public const string GitHubRepository = "duopilo/duopiloapp";

    public const string RepositoryUrl = "https://github.com/" + GitHubRepository;

    /// <summary>Kennung für HTTP-Anfragen nach außen (SimBrief, Modelle, Datenquellen).</summary>
    public const string UserAgent = Product + "/1.0 (+" + Website + ")";

    /// <summary>Issue-Vorlage für „Fehler melden“ (.github/ISSUE_TEMPLATE).</summary>
    public const string IssueTemplate = "duopilo-meldung.yml";

    /// <summary>Der alte Name „Kopilot“ – nur noch zum Lesen und Übernehmen alter Einstellungen und Daten.</summary>
    public static class Legacy
    {
        public const string Product = "Kopilot";

        /// <summary>Alter Konfigurationsabschnitt ("Kopilot": { … }, Umgebungsvariablen Kopilot__…).</summary>
        public const string ConfigSection = Product;

        /// <summary>Alter Datenordner %LOCALAPPDATA%\Kopilot.</summary>
        public const string DataDirectory = Product;

        /// <summary>Alter Wert unter HKCU\…\Run.</summary>
        public const string AutostartValue = Product;

        /// <summary>Alte Programmdatei (Autostart-Einträge, laufende Instanzen).</summary>
        public const string Exe = Product + ".exe";

        /// <summary>Alter Header für schreibende Dashboard-Aufrufe.</summary>
        public const string ActionHeader = "X-" + Product;

        public const string GitHubRepository = "Shin-Jin-Jin/Kopilot";

        public const string IssueTemplate = "kopilot-meldung.yml";
    }
}
