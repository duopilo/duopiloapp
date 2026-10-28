using Kopilot;
using Kopilot.Desktop;
using Kopilot.Hosting;
using Kopilot.Remote;
using Microsoft.Extensions.Options;

// Kopilot – MCP-Server, der Daten aus Microsoft Flight Simulator 2024 an Claude weitergibt.
//
//   Kopilot.exe                    → HTTP-Modus mit Tray-Symbol und Dashboard (http://127.0.0.1:5155/), MCP unter /mcp
//   Kopilot.exe --stdio            → MCP über stdin/stdout (Claude Desktop startet den Prozess selbst)
//   Kopilot.exe --console          → zusätzlich ein Konsolenfenster mit dem Log (Fehlersuche)
//   Kopilot.exe --no-tray          → ohne Tray-Symbol
//   Kopilot.exe --generate-secret  → zufälliges Geheimnis für BearerToken / ConsentPassword ausgeben
//
// Ohne --stdio/--http entscheidet Kopilot:Transport in appsettings(.Local).json.

static bool Has(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

if (Has(args, "--generate-secret"))
{
    // Kopilot ist eine Windows-Anwendung ohne eigene Konsole – an die aufrufende Konsole anhängen.
    NativeConsole.AttachToParent();
    Console.WriteLine();
    Console.WriteLine(Secrets.NewToken());
    return;
}

var transport = KopilotConfiguration.ResolveTransport(args, AppContext.BaseDirectory);

if (transport == KopilotTransport.Stdio)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = KopilotConfiguration.FilterSwitches(args),
        ContentRootPath = AppContext.BaseDirectory,
    });
    KopilotConfiguration.AddLocalSettings(builder.Configuration, AppContext.BaseDirectory, KopilotConfiguration.FilterSwitches(args));

    // Im stdio-Modus gehört stdout exklusiv dem MCP-Protokoll – alle Logs gehen nach stderr und in die Logdatei.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    AddFileLog(builder.Logging, builder.Configuration, builder.Services, "stdio");

    builder.Services.AddKopilotCore(builder.Configuration);
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
    HttpHost.AddServices(builder);

    var url = builder.Configuration.GetSection(KopilotOptions.Section).Get<KopilotOptions>()?.HttpUrl ?? new KopilotOptions().HttpUrl;
    var dashboardUrl = url.TrimEnd('/') + "/";

    // Nur eine Instanz im HTTP-Modus: Ein zweiter Start öffnet einfach das Dashboard der laufenden Instanz.
    using var instance = new Mutex(initiallyOwned: true, $"Local\\Kopilot-{new Uri(url).Port}", out var firstInstance);
    if (!firstInstance)
    {
        OpenBrowser(dashboardUrl);
        return;
    }

    // Sprachausgabe nur in dieser (Tray-)Instanz – die stdio-Instanz von Claude Desktop würde sonst doppelt sprechen
    builder.Services.AddSingleton<Kopilot.Speech.CalloutSpeaker>();
    builder.Services.AddSingleton<Kopilot.Speech.ISpeechOutput>(sp => sp.GetRequiredService<Kopilot.Speech.CalloutSpeaker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Kopilot.Speech.CalloutSpeaker>());

    if (OperatingSystem.IsWindows() && !Has(args, "--no-tray"))
    {
        builder.Services.AddSingleton(new DesktopInfo(dashboardUrl, logFile, Has(args, "--autostart")));
        builder.Services.AddHostedService<TrayService>();
    }

    var app = builder.Build();
    HttpHost.Configure(app);

    if (!KopilotConfiguration.IsLoopbackUrl(url, out var reason))
    {
        app.Logger.LogCritical("{Reason}", reason);
        Environment.ExitCode = 1;
        return;
    }

    app.Logger.LogInformation("Kopilot {Version} HTTP-Modus: Dashboard {Dashboard}, MCP-Endpunkt {McpUrl}", typeof(KopilotOptions).Assembly.GetName().Version?.ToString(3), dashboardUrl, url.TrimEnd('/') + "/mcp");
    await app.RunAsync(url);
}

// Logdatei unter %LOCALAPPDATA%\Kopilot\logs (bzw. Kopilot:DataDirectory\logs) plus Puffer fürs Dashboard.
static string? AddFileLog(ILoggingBuilder logging, IConfiguration configuration, IServiceCollection services, string mode)
{
    var buffer = new LogBuffer();
    services.AddSingleton(buffer);
    try
    {
        var options = configuration.GetSection(KopilotOptions.Section).Get<KopilotOptions>() ?? new KopilotOptions();
        var directory = Path.Combine(options.ResolveDataDirectory(), "logs");
        var provider = new FileLoggerProvider(directory, mode == "stdio" ? "kopilot-stdio.log" : "kopilot.log", buffer);
        logging.AddProvider(provider);
        return provider.FilePath;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Logdatei nicht verfügbar ({mode}): {ex.Message}");
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
