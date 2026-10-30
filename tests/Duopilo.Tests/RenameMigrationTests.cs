using Duopilo.Desktop;
using Duopilo.Hosting;
using Duopilo.Voice;
using Microsoft.Extensions.Configuration;

namespace Duopilo.Tests;

/// <summary>
/// Umbenennung Kopilot → Duopilo: Was unter dem alten Namen existiert, wird weiter gelesen oder übernommen – Datenordner,
/// Konfigurationsabschnitt, Umgebungsvariablen, Log-Kategorien, Autostart-Eintrag und Links für „Fehler melden“.
/// </summary>
public sealed class RenameMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "duopilo-rename-tests", Guid.NewGuid().ToString("N"));

    public RenameMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ Namen

    [Fact]
    public void Name_Webseite_und_Repository()
    {
        Assert.Equal("Duopilo", AppNames.Product);
        Assert.Equal("Duopilo", DuopiloOptions.Section);
        Assert.Equal("https://duopilo.app", AppNames.Website);
        Assert.Equal("https://github.com/duopilo/duopiloapp", AppNames.RepositoryUrl);
        Assert.Equal("Duopilo", new DuopiloOptions().ClientName);
        Assert.Equal("duopilo.log", FileLoggerProvider.FileName("http"));
        Assert.Equal("duopilo-stdio.log", FileLoggerProvider.FileName("stdio"));
    }

    [Fact]
    public void Build_Installer_und_Code_nennen_dasselbe_Produkt()
    {
        // <Product> (Installer, ZIP, Release) und AppNames.Product (Datenordner, Autostart, Konfiguration) müssen gleich sein
        Assert.Contains($"<Product>{AppNames.Product}</Product>", File.ReadAllText(TestData.RepoFile("Directory.Build.props")));
        Assert.Contains($"<AssemblyName>{AppNames.Product}</AssemblyName>", File.ReadAllText(TestData.RepoFile("src", "Duopilo", "Duopilo.csproj")));

        var setup = File.ReadAllText(TestData.RepoFile("installer", "setup.iss"));
        Assert.Contains("AppId={{A9BF1AF5-AD01-4EF4-9A95-94830AA2324F}", setup); // nie ändern – sonst kein Update von Kopilot
        Assert.Contains($"#define LegacyName \"{AppNames.Legacy.Product}\"", setup);
        Assert.Contains($"#define Website \"{AppNames.Website}\"", setup);
        Assert.Contains($"#define RepoUrl \"{AppNames.RepositoryUrl}\"", setup);
    }

    // ------------------------------------------------------------------ Datenordner

    [Fact]
    public void Alter_Datenordner_wird_beim_ersten_Start_uebernommen()
    {
        var legacy = Path.Combine(_root, "Kopilot");
        var target = Path.Combine(_root, "Duopilo");
        Directory.CreateDirectory(Path.Combine(legacy, "models", "piper"));
        File.WriteAllText(Path.Combine(legacy, "voice-settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "models", "piper", "stimme.onnx"), "x");

        var outcome = DataDirectoryMigration.Migrate(legacy, target);

        Assert.True(outcome.Moved);
        Assert.False(outcome.UsingLegacy);
        Assert.Equal(target, outcome.Directory);
        Assert.True(File.Exists(Path.Combine(target, "voice-settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "models", "piper", "stimme.onnx")));
        Assert.False(Directory.Exists(legacy));

        // Zweiter Start: nichts mehr zu tun
        var again = DataDirectoryMigration.Migrate(legacy, target);
        Assert.False(again.Moved);
        Assert.Equal(target, again.Directory);
    }

    [Fact]
    public void Gibt_es_den_neuen_Ordner_schon_bleibt_der_alte_liegen()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(_root, "Kopilot")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "Duopilo")).FullName;
        File.WriteAllText(Path.Combine(legacy, "speech.json"), "{}");

        var outcome = DataDirectoryMigration.Migrate(legacy, target);

        Assert.False(outcome.Moved);
        Assert.Equal(target, outcome.Directory);
        Assert.True(File.Exists(Path.Combine(legacy, "speech.json")));
    }

    [Fact]
    public void Ohne_alten_Ordner_wird_nichts_angelegt()
    {
        var target = Path.Combine(_root, "Duopilo");

        var outcome = DataDirectoryMigration.Migrate(Path.Combine(_root, "Kopilot"), target);

        Assert.False(outcome.Moved);
        Assert.Equal(target, outcome.Directory);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Haelt_ein_altes_Kopilot_Dateien_offen_wird_der_alte_Ordner_weiter_gelesen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Nur Windows sperrt das Umbenennen eines Ordners mit offenen Dateien
        }

        var legacy = Directory.CreateDirectory(Path.Combine(_root, "Kopilot", "logs")).Parent!.FullName;
        var target = Path.Combine(_root, "Duopilo");
        using (new FileStream(Path.Combine(legacy, "logs", "kopilot.log"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var outcome = DataDirectoryMigration.Migrate(legacy, target);

            Assert.True(outcome.UsingLegacy);
            Assert.Equal(legacy, outcome.Directory);
            Assert.NotNull(outcome.Error);
            Assert.False(Directory.Exists(target));
        }

        // Nächster Start, das alte Kopilot ist beendet
        Assert.True(DataDirectoryMigration.Migrate(legacy, target).Moved);
    }

    [Fact]
    public void Eingestellte_Pfade_im_alten_Ordner_zeigen_in_den_neuen()
    {
        var legacy = Path.Combine(_root, "Kopilot");
        var target = Directory.CreateDirectory(Path.Combine(_root, "Duopilo", "models")).Parent!.FullName;
        File.WriteAllText(Path.Combine(target, "models", "ggml-small.bin"), "x");
        var moved = new DataDirectoryOutcome(target, Moved: true, UsingLegacy: false, legacy);

        Assert.Equal(
            Path.Combine(target, "models", "ggml-small.bin"),
            DataDirectoryMigration.MapLegacyPath(Path.Combine(legacy, "models", "ggml-small.bin"), moved));

        // Außerhalb des alten Ordners, relativ oder leer: unverändert
        var elsewhere = Path.Combine(_root, "Modelle", "ggml-small.bin");
        Assert.Equal(elsewhere, DataDirectoryMigration.MapLegacyPath(elsewhere, moved));
        Assert.Equal("models\\x.bin", DataDirectoryMigration.MapLegacyPath("models\\x.bin", moved));
        Assert.Equal("", DataDirectoryMigration.MapLegacyPath("", moved));

        // Läuft dieser Start noch im alten Ordner, bleibt der Pfad, wie er ist
        var inUse = new DataDirectoryOutcome(legacy, Moved: false, UsingLegacy: true, legacy, "in use");
        var path = Path.Combine(legacy, "models", "ggml-small.bin");
        Assert.Equal(path, DataDirectoryMigration.MapLegacyPath(path, inUse));
    }

    // ------------------------------------------------------------------ Konfiguration

    [Fact]
    public void Alter_Abschnitt_Kopilot_wird_als_Duopilo_gelesen()
    {
        var config = Local("""{ "Kopilot": { "SimBrief": { "Username": "flo" }, "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } } }""");

        Assert.Equal("flo", config["Duopilo:SimBrief:Username"]);
        Assert.Equal("SPEED_DEVIATION", config["Duopilo:Monitoring:DisabledRules:0"]);
        var options = config.GetSection(DuopiloOptions.Section).Get<DuopiloOptions>()!;
        Assert.Equal("flo", options.SimBrief.Username);
        Assert.Equal(new[] { "SPEED_DEVIATION" }, options.Monitoring.DisabledRules);
    }

    [Fact]
    public void In_derselben_Datei_gewinnt_der_neue_Name()
    {
        var config = Local("""{ "Kopilot": { "Language": "de", "SimBrief": { "Username": "alt" } }, "Duopilo": { "SimBrief": { "Username": "neu" } } }""");

        Assert.Equal("neu", config["Duopilo:SimBrief:Username"]);
        Assert.Equal("de", config["Duopilo:Language"]);
    }

    [Fact]
    public void Alte_appsettings_Local_ueberschreibt_weiter_die_Vorgaben()
    {
        // appsettings.json (mitgeliefert, schon "Duopilo") < appsettings.Local.json (alt, "Kopilot")
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), """{ "Duopilo": { "Transport": "http", "Atc": { "Enabled": true } } }""");
        File.WriteAllText(Path.Combine(_root, DuopiloConfiguration.LocalSettingsFile), """{ "Kopilot": { "Transport": "stdio", "Atc": { "Enabled": false } } }""");
        var builder = new ConfigurationBuilder().AddJsonFile(Path.Combine(_root, "appsettings.json"), optional: false);
        DuopiloConfiguration.AddLocalSettings(builder, _root, []);

        var config = builder.Build();

        Assert.Equal("stdio", config["Duopilo:Transport"]);
        Assert.Equal("False", config["Duopilo:Atc:Enabled"], ignoreCase: true);
        Assert.Equal(DuopiloTransport.Stdio, DuopiloConfiguration.ResolveTransport([], _root));
    }

    [Fact]
    public void Kommandozeile_mit_altem_Namen_gilt_weiter()
    {
        var builder = new ConfigurationBuilder();
        DuopiloConfiguration.AddLocalSettings(builder, _root, ["--Kopilot:HttpUrl=http://127.0.0.1:5199"]);

        Assert.Equal("http://127.0.0.1:5199", builder.Build()["Duopilo:HttpUrl"]);
    }

    [Fact]
    public void Umgebungsvariablen_mit_altem_Namen_gelten_weiter()
    {
        // Schlüssel, den sonst niemand liest – Tests laufen parallel im selben Prozess
        const string name = "Kopilot__RenameTest__Value";
        Environment.SetEnvironmentVariable(name, "aus-der-umgebung");
        try
        {
            var builder = new ConfigurationBuilder();
            DuopiloConfiguration.AddLocalSettings(builder, _root, []);

            Assert.Equal("aus-der-umgebung", builder.Build()["Duopilo:RenameTest:Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Log_Kategorien_mit_altem_Namen_gelten_weiter()
    {
        var config = Local("""{ "Logging": { "LogLevel": { "Kopilot.Voice": "Debug", "Kopilot": "Warning", "Kopilotx": "Error" } } }""");

        Assert.Equal("Debug", config["Logging:LogLevel:Duopilo.Voice"]);
        Assert.Equal("Warning", config["Logging:LogLevel:Duopilo"]);
        Assert.Null(config["Logging:LogLevel:Duopilox"]);
    }

    [Fact]
    public void Links_fuer_Fehler_melden_zeigen_aufs_neue_Repository()
    {
        var defaults = new ReportOptions();
        Assert.Equal("duopilo/duopiloapp", defaults.ResolveGitHubRepository());
        Assert.Equal("duopilo-meldung.yml", defaults.ResolveIssueTemplate());

        // Alte Werte aus einer eigenen appsettings.Local.json
        var old = new ReportOptions { GitHubRepository = "Shin-Jin-Jin/Kopilot", IssueTemplate = "kopilot-meldung.yml" };
        Assert.Equal("duopilo/duopiloapp", old.ResolveGitHubRepository());
        Assert.Equal("duopilo-meldung.yml", old.ResolveIssueTemplate());

        // Eigener Fork bleibt
        var fork = new ReportOptions { GitHubRepository = "jemand/fork", IssueTemplate = "eigene.yml" };
        Assert.Equal("jemand/fork", fork.ResolveGitHubRepository());
        Assert.Equal("eigene.yml", fork.ResolveIssueTemplate());
    }

    // ------------------------------------------------------------------ appsettings.Local.json umbenennen

    [Fact]
    public void Wurzel_Eigenschaft_wird_mit_Kommentaren_umbenannt()
    {
        const string original = """
            {
              // Mein Zugang
              "Kopilot": { "Remote": { "PublicUrl": "https://duopilo.example.com" } }, // hinten
              "Logging": { "Kopilot": "nicht anfassen" }
            }
            """;

        var text = JsonTextPatcher.RenameRootProperty(original, "Kopilot", "Duopilo");

        Assert.Contains("// Mein Zugang", text);
        Assert.Contains("// hinten", text);
        Assert.Contains("\"Duopilo\": { \"Remote\"", text);
        Assert.Contains("\"Logging\": { \"Kopilot\": \"nicht anfassen\" }", text);
    }

    [Theory]
    [InlineData("""{ "Duopilo": {} }""")]
    [InlineData("""{ "Kopilot": {}, "Duopilo": {} }""")]
    [InlineData("""{ "Kopilot": """)]
    [InlineData("")]
    public void Ohne_alten_Abschnitt_oder_mit_beiden_bleibt_der_Text(string original)
    {
        Assert.Equal(original, JsonTextPatcher.RenameRootProperty(original, "Kopilot", "Duopilo"));
    }

    // ------------------------------------------------------------------ Autostart

    [Theory]
    [InlineData("\"C:\\Program Files\\Kopilot\\Kopilot.exe\" --autostart", true)]
    [InlineData("\"C:\\Tools\\Kopilot\\kopilot.exe\"", true)]
    [InlineData("C:\\Tools\\Kopilot\\Kopilot.exe --autostart", true)]
    [InlineData("\"C:\\Program Files\\Duopilo\\Duopilo.exe\" --autostart", false)]
    [InlineData("\"C:\\Kopilot\\anderes.exe\"", false)]
    [InlineData("", false)]
    public void Alter_Autostart_Eintrag_wird_erkannt(string command, bool legacy)
    {
        Assert.Equal(legacy, Autostart.IsLegacyCommand(command));
    }

    private IConfiguration Local(string json)
    {
        var file = Path.Combine(_root, $"local-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return new ConfigurationBuilder().Add(LegacySectionSource.Json(file)).Build();
    }
}

/// <summary>
/// Ansprachewort: „Kopilot“ bleibt fest, „Duopilo“ zählt zusätzlich – aber nur, wenn Whisper es eindeutig so abschreibt.
/// Ähnlich klingende Sätze im Raum dürfen nicht als Anrede gelten.
/// </summary>
public sealed class WakeWordTests
{
    private static readonly string[] Wake = ["kopilot"];

    [Theory]
    [InlineData("Kopilot, gear up", "kopilot gear up")]
    [InlineData("Co-Pilot, gear up", "kopilot gear up")]
    [InlineData("Duopilo, gear up", "kopilot gear up")]
    [InlineData("Duopilo gear up.", "kopilot gear up")]
    [InlineData("Duo Pilo, flaps one", "kopilot flaps 1")]
    [InlineData("Duo-Pilo, flaps one", "kopilot flaps 1")]
    [InlineData("Duopilot, speed check", "kopilot speed check")]
    [InlineData("Duo pilot, speed check", "kopilot speed check")]
    public void Kopilot_und_Duopilo_sind_Anrede(string raw, string expected)
    {
        Assert.Equal(expected, VoiceText.Normalize(raw));
    }

    [Theory]
    [InlineData("Do a pilot briefing")]
    [InlineData("Dual pilot operations")]
    [InlineData("Duolingo")]
    [InlineData("Duo")]
    [InlineData("Pilot")]
    [InlineData("Duopilotin")]
    [InlineData("Duos pilot")]
    public void Aehnliches_ist_keine_Anrede(string raw)
    {
        Assert.DoesNotContain("kopilot", VoiceText.Words(VoiceText.Normalize(raw)));
    }

    [Theory]
    [InlineData("Kopilot, Frankfurt Tower frequency check")]
    [InlineData("Duopilo, Frankfurt Tower frequency check")]
    public void Mit_Anrede_geht_es_immer_an_den_Kopiloten(string raw)
    {
        Assert.Equal(VoiceTarget.Kopilot, new VoiceRouter(Wake).Route(VoiceText.Normalize(raw)).Target);
    }

    [Theory]
    [InlineData("Duopilo Pause", IntentKind.Pause)]
    [InlineData("Duopilo weiter", IntentKind.Resume)]
    [InlineData("Duopilo, gear up", IntentKind.Action)]
    public void Steuerung_und_Befehle_mit_Duopilo(string raw, IntentKind kind)
    {
        Assert.Equal(kind, VoiceIntents.Recognize(VoiceText.Normalize(raw), Wake).Kind);
    }

    [Fact]
    public void Gruss_vor_Duopilo_wird_zur_Anrede()
    {
        Assert.Equal("kopilot flaps 1", VoiceDispatcher.Address(VoiceText.Normalize("Okay Duopilo, flaps one"), null, Wake));
    }

    [Fact]
    public void Whisper_kennt_beide_Ansprachewoerter_und_verwirft_das_Echo_des_Hinweises()
    {
        Assert.StartsWith("Kopilot, Duopilo.", WhisperRecognizer.DefaultPrompt);
        Assert.Equal("", WhisperRecognizer.Clean(WhisperRecognizer.DefaultPrompt));
        Assert.Equal("", WhisperRecognizer.Clean("Kopilot. Gear up, gear down, flaps one, flaps full."));
        Assert.Equal("Duopilo, gear up.", WhisperRecognizer.Clean("Duopilo, gear up."));
    }
}
