using Duopilo;
using Duopilo.Desktop;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Remote;
using Microsoft.Extensions.Options;

// Duopilo (bis 0.9.x „Kopilot“) – MCP-Server, der Daten aus Microsoft Flight Simulator 2024 an Claude weitergibt.
//
//   Duopilo.exe                    → HTTP-Modus mit Tray-Symbol und Dashboard (http://127.0.0.1:5155/), MCP unter /mcp
//   Duopilo.exe --stdio            → MCP über stdin/stdout (Claude Desktop startet den Prozess selbst)
//   Duopilo.exe --console          → zusätzlich ein Konsolenfenster mit dem Log (Fehlersuche)
//   Duopilo.exe --no-tray          → ohne Tray-Symbol
//   Duopilo.exe --generate-secret  → zufälliges Geheimnis für BearerToken / ConsentPassword ausgeben
//
// Sprachschleife (Mikrofon, Whisper, Duopilo-Befehle) nur im HTTP-Modus: %LOCALAPPDATA%\Duopilo\voice-settings.json
// bzw. im Dashboard unter Ansagen → Stimme. Nach einem Absturz startet Windows die Tray-Instanz mit --restarted neu.
//
// Ohne --stdio/--http entscheidet Duopilo:Transport in appsettings(.Local).json.
//
// Alter Name: Der Datenordner %LOCALAPPDATA%\Kopilot wird beim ersten Start übernommen, der Abschnitt "Kopilot" und
// Umgebungsvariablen Kopilot__… werden weiter gelesen (Hosting/AppNames.cs).

static bool Has(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

if (Has(args, "--generate-secret"))
{
    // Duopilo ist eine Windows-Anwendung ohne eigene Konsole – an die aufrufende Konsole anhängen.
    NativeConsole.AttachToParent();
    Console.WriteLine();
    Console.WriteLine(Secrets.NewToken());
    return;
}

var transport = DuopiloConfiguration.ResolveTransport(args, AppContext.BaseDirectory);

if (transport == DuopiloTransport.Stdio)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = DuopiloConfiguration.FilterSwitches(args),
        ContentRootPath = AppContext.BaseDirectory,
    });
    DuopiloConfiguration.AddLocalSettings(builder.Configuration, AppContext.BaseDirectory, DuopiloConfiguration.FilterSwitches(args));

    // Im stdio-Modus gehört stdout exklusiv dem MCP-Protokoll – alle Logs gehen nach stderr und in die Logdatei.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    AddFileLog(builder.Logging, builder.Configuration, builder.Services, "stdio");
    CrashReporter.InstallHandlers(LogDirectory(builder.Configuration), "stdio");

    builder.Services.AddDuopiloCore(builder.Configuration);
    builder.Services.AddHostedService<LegacyNotice>();

    // Eigene ATC-Sitzungsdatei, damit sich stdio- und Tray-Instanz nicht gegenseitig überschreiben
    builder.Services.PostConfigure<DuopiloOptions>(o =>
    {
        if (o.Atc.SessionFile == AtcOptions.DefaultSessionFile)
        {
            o.Atc.SessionFile = "atc-session-stdio.json";
        }
    });
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}
else
{
    if (Has(args, "--console"))
    {
        NativeConsole.Open();
    }

    if (NativeConsole.HasConsole)
    {
        // Umlaute in der Konsole korrekt anzeigen. Ohne Konsole würde das Setzen scheitern.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }
    }

    var builder = HttpHost.CreateBuilder(args);
    var logFile = AddFileLog(builder.Logging, builder.Configuration, builder.Services, "http");
    var logDirectory = LogDirectory(builder.Configuration);
    CrashReporter.InstallHandlers(logDirectory, "http");
    HttpHost.AddServices(builder);

    var url = builder.Configuration.GetSection(DuopiloOptions.Section).Get<DuopiloOptions>()?.HttpUrl ?? new DuopiloOptions().HttpUrl;
    var dashboardUrl = url.TrimEnd('/') + "/";

    // Nur eine Instanz im HTTP-Modus: Ein zweiter Start öffnet einfach das Dashboard der laufenden Instanz.
    // Ausnahme: Neustart aus dem Dashboard (--replace) – dann auf das Ende der alten Instanz warten.
    var replacing = Has(args, AppRestart.ReplaceSwitch);
    var port = new Uri(url).Port;
    using var instance = new Mutex(initiallyOwned: true, $"Local\\{AppNames.Product}-{port}", out var firstInstance);
    if (!firstInstance && !(replacing && AppRestart.WaitForPrevious(instance, AppRestart.WaitForPreviousTimeout)))
    {
        OpenBrowser(dashboardUrl);
        return;
    }

    // Läuft noch ein altes Kopilot (bis 0.9.x) auf demselben Port, gehört der Port ihm – wie bei einer zweiten Instanz
    if (LegacyInstance.IsRunning(port))
    {
        OpenBrowser(dashboardUrl);
        return;
    }

    // Neustart aus dem Dashboard (Einstellungen) – die Argumente braucht die neue Instanz
    builder.Services.AddSingleton(sp => new AppRestart(args, sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<AppRestart>>()));

    // Sprachausgabe nur in dieser (Tray-)Instanz – die stdio-Instanz von Claude Desktop würde sonst doppelt sprechen
    builder.Services.AddSingleton<Duopilo.Speech.KokoroSpeechEngine>();

    // Piper: zweite lokale Stimme für Smalltalk in Sprachen ohne Kokoro-Stimme (Deutsch) – lädt erst bei Bedarf
    builder.Services.AddSingleton<Duopilo.Speech.PiperSpeechEngine>();
    builder.Services.AddSingleton<Duopilo.Speech.ITtsEngine>(sp => sp.GetRequiredService<Duopilo.Speech.PiperSpeechEngine>());
    builder.Services.AddSingleton(sp => new Duopilo.Speech.TtsEngineRegistry(sp.GetServices<Duopilo.Speech.ITtsEngine>()));
    builder.Services.AddSingleton<Duopilo.Speech.CalloutSpeaker>();
    builder.Services.AddSingleton<Duopilo.Speech.ISpeechOutput>(sp => sp.GetRequiredService<Duopilo.Speech.CalloutSpeaker>());
    builder.Services.AddSingleton<Duopilo.Atc.IAtcRadio>(sp => sp.GetRequiredService<Duopilo.Speech.CalloutSpeaker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Duopilo.Speech.CalloutSpeaker>());

    // Sprachschleife (Mikrofon → Whisper → Duopilo/ATC → Sprachausgabe), aus voice-settings.json – standardmäßig aus
    Duopilo.Voice.VoiceServiceCollectionExtensions.AddDuopiloVoice(builder.Services);

    if (OperatingSystem.IsWindows() && !Has(args, "--no-tray"))
    {
        // Nach einem Neustart aus dem Dashboard keine Begrüßungs-Sprechblase – das Dashboard ist ja schon offen
        builder.Services.AddSingleton(new DesktopInfo(dashboardUrl, logFile, Has(args, "--autostart") || replacing));
        builder.Services.AddHostedService<TrayService>();
    }

    builder.Services.AddHostedService<LegacyNotice>();

    var app = builder.Build();
    HttpHost.Configure(app);

    if (!DuopiloConfiguration.IsLoopbackUrl(url, out var reason))
    {
        app.Logger.LogCritical("{Reason}", reason);
        Environment.ExitCode = 1;
        return;
    }

    app.Logger.LogInformation(Loc.Log("log.program.httpStarted"), typeof(DuopiloOptions).Assembly.GetName().Version?.ToString(3), dashboardUrl, url.TrimEnd('/') + "/mcp");
    if (Has(args, "--restarted"))
    {
        app.Logger.LogWarning(Loc.Log("log.program.restartedAfterCrash"));
    }

    // Absturz-Journal (nur Tray-Instanz – es gibt nur eine) und Neustart durch Windows nach einem Absturz
    var journal = CrashReporter.StartJournal(logDirectory, "http", app.Logger);
    app.Lifetime.ApplicationStopped.Register(() => journal?.MarkClean());
    if (app.Services.GetRequiredService<IOptions<DuopiloOptions>>().Value.RestartAfterCrash)
    {
        CrashReporter.RegisterRestart(args, app.Logger);
    }

    try
    {
        await app.RunAsync(url);
    }
    finally
    {
        journal?.MarkClean();
        journal?.Dispose();
    }
}

static string LogDirectory(IConfiguration configuration) =>
    Path.Combine((configuration.GetSection(DuopiloOptions.Section).Get<DuopiloOptions>() ?? new DuopiloOptions()).ResolveDataDirectory(), "logs");

// Logdatei unter %LOCALAPPDATA%\Duopilo\logs (bzw. Duopilo:DataDirectory\logs) plus Puffer fürs Dashboard.
static string? AddFileLog(ILoggingBuilder logging, IConfiguration configuration, IServiceCollection services, string mode)
{
    var buffer = new LogBuffer();
    services.AddSingleton(buffer);
    try
    {
        var options = configuration.GetSection(DuopiloOptions.Section).Get<DuopiloOptions>() ?? new DuopiloOptions();
        var directory = Path.Combine(options.ResolveDataDirectory(), "logs");
        var provider = new FileLoggerProvider(directory, FileLoggerProvider.FileName(mode), buffer);
        logging.AddProvider(provider);
        return provider.FilePath;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(Loc.En("log.program.logFileUnavailable", mode, ex.Message));
        return null;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
    catch (System.ComponentModel.Win32Exception)
    {
    }
}
