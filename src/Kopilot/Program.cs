using Kopilot;
using Kopilot.Aircraft;
using Kopilot.Sim;
using Kopilot.Tools;

// Kopilot – MCP-Server, der Daten aus Microsoft Flight Simulator 2024 an Claude weitergibt.
//
//   Kopilot.exe --stdio   → MCP über stdin/stdout (für Claude Desktop, das den Prozess selbst startet)
//   Kopilot.exe           → MCP über HTTP (http://127.0.0.1:5155/mcp) + Status-Seiten im Browser

var useStdio = args.Any(a => string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase));

if (useStdio)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

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

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.AddKopilotCore(builder.Configuration);
    builder.Services
        .AddMcpServer()
        // Lokaler Einzelprozess: Sessions sind unproblematisch und funktionieren auch mit älteren MCP-Clients.
        .WithHttpTransport(o => o.Stateless = false)
        .WithToolsFromAssembly();

    var app = builder.Build();

    // Einfache Prüfseiten für den Browser (kein MCP nötig)
    app.MapGet("/", (SimConnectClient sim, ProfileStore profiles) => Results.Content(KopilotTools.Status(sim, profiles), "application/json"));
    app.MapGet("/state", (SimConnectClient sim) => Results.Content(KopilotTools.GetFlightState(sim), "application/json"));
    app.MapGet("/systems", (SimConnectClient sim) => Results.Content(KopilotTools.GetSystems(sim), "application/json"));
    app.MapGet("/nav", (SimConnectClient sim) => Results.Content(KopilotTools.GetNavigation(sim), "application/json"));
    app.MapGet("/env", (SimConnectClient sim) => Results.Content(KopilotTools.GetEnvironment(sim), "application/json"));
    app.MapGet("/profile", (SimConnectClient sim, ProfileStore profiles) => Results.Content(KopilotTools.GetAircraftProfile(sim, profiles), "application/json"));
    app.MapGet("/read", async (SimConnectClient sim, string name, string? unit, CancellationToken ct) =>
        Results.Content(await KopilotTools.ReadSimVar(sim, name, unit ?? "number", ct), "application/json"));

    app.MapMcp("/mcp");

    var url = app.Configuration[$"{KopilotOptions.Section}:{nameof(KopilotOptions.HttpUrl)}"] ?? "http://127.0.0.1:5155";
    app.Logger.LogInformation("Kopilot HTTP-Modus: {Url} (MCP-Endpunkt: {McpUrl})", url, url.TrimEnd('/') + "/mcp");
    await app.RunAsync(url);
}

internal static class KopilotServiceCollectionExtensions
{
    public static IServiceCollection AddKopilotCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KopilotOptions>(configuration.GetSection(KopilotOptions.Section));
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<SimConnectClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SimConnectClient>());
        return services;
    }
}
