using Kopilot;
using Kopilot.Hosting;
using Kopilot.Remote;
using Microsoft.Extensions.Options;

// Kopilot – MCP-Server, der Daten aus Microsoft Flight Simulator 2024 an Claude weitergibt.
//
//   Kopilot.exe --stdio            → MCP über stdin/stdout (Claude Desktop startet den Prozess selbst)
//   Kopilot.exe [--http]           → MCP über Streamable HTTP (http://127.0.0.1:5155/mcp), geschützt per Token/OAuth
//   Kopilot.exe --generate-secret  → zufälliges Geheimnis für BearerToken / ConsentPassword ausgeben
//
// Ohne Schalter entscheidet Kopilot:Transport in appsettings(.Local).json.

if (args.Any(a => string.Equals(a, "--generate-secret", StringComparison.OrdinalIgnoreCase)))
{
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

    // Im stdio-Modus gehört stdout exklusiv dem MCP-Protokoll – alle Logs gehen nach stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddKopilotCore(builder.Configuration);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}
else
{
    // Umlaute und Sonderzeichen in der Konsole korrekt anzeigen (nur HTTP-Modus, stdio bleibt unangetastet)
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    var builder = HttpHost.CreateBuilder(args);
    HttpHost.AddServices(builder);
    var app = builder.Build();
    HttpHost.Configure(app);

    var url = app.Services.GetRequiredService<IOptions<KopilotOptions>>().Value.HttpUrl;
    if (!KopilotConfiguration.IsLoopbackUrl(url, out var reason))
    {
        app.Logger.LogCritical("{Reason}", reason);
        Environment.ExitCode = 1;
        return;
    }

    app.Logger.LogInformation("Kopilot HTTP-Modus: {Url} (MCP-Endpunkt: {McpUrl})", url, url.TrimEnd('/') + "/mcp");
    await app.RunAsync(url);
}
