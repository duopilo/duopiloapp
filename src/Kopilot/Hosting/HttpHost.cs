using Kopilot.Aircraft;
using Kopilot.Remote;
using Kopilot.Sim;
using Kopilot.Tools;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

namespace Kopilot.Hosting;

/// <summary>
/// HTTP-Modus: Streamable HTTP unter /mcp, geschützt per Bearer-Token und/oder OAuth.
/// Bindet nur an localhost; von außen erreichbar ausschließlich über den Cloudflare Tunnel.
/// </summary>
public static class HttpHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = KopilotConfiguration.FilterSwitches(args),
            ContentRootPath = AppContext.BaseDirectory,
        });

        KopilotConfiguration.AddLocalSettings(builder.Configuration, AppContext.BaseDirectory, KopilotConfiguration.FilterSwitches(args));
        return builder;
    }

    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddKopilotCore(builder.Configuration);
        builder.Services.AddSingleton<OAuthStore>();
        builder.Services.AddSingleton<LoginThrottle>();

        // Erlaubte Hostnamen: lokal + die öffentliche Tunnel-Domain. Alles andere lehnt der Host-Filter ab.
        var remote = builder.Configuration.GetSection($"{KopilotOptions.Section}:Remote").Get<RemoteOptions>() ?? new RemoteOptions();
        var allowedHosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };
        if (remote.PublicBaseUri is { } publicUri)
        {
            allowedHosts.Add(publicUri.Host);
        }

        builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = allowedHosts);

        builder.Services
            .AddMcpServer()
            // Lokaler Einzelprozess: Sessions sind unproblematisch und funktionieren auch mit älteren MCP-Clients.
            .WithHttpTransport(o => o.Stateless = false)
            .WithToolsFromAssembly();
    }

    public static void Configure(WebApplication app)
    {
        var remote = app.Services.GetRequiredService<IOptions<KopilotOptions>>().Value.Remote;

        app.UseMiddleware<McpAuthMiddleware>();

        // Prüfseiten für den Browser – nur lokal, über den Tunnel nicht erreichbar.
        app.MapGet("/", (SimConnectClient sim, ProfileStore profiles) => Results.Content(KopilotTools.Status(sim, profiles), "application/json")).LocalOnly();
        app.MapGet("/state", (SimConnectClient sim) => Results.Content(KopilotTools.GetFlightState(sim), "application/json")).LocalOnly();
        app.MapGet("/systems", (SimConnectClient sim) => Results.Content(KopilotTools.GetSystems(sim), "application/json")).LocalOnly();
        app.MapGet("/nav", (SimConnectClient sim) => Results.Content(KopilotTools.GetNavigation(sim), "application/json")).LocalOnly();
        app.MapGet("/env", (SimConnectClient sim) => Results.Content(KopilotTools.GetEnvironment(sim), "application/json")).LocalOnly();
        app.MapGet("/profile", (SimConnectClient sim, ProfileStore profiles) => Results.Content(KopilotTools.GetAircraftProfile(sim, profiles), "application/json")).LocalOnly();
        app.MapGet("/read", async (SimConnectClient sim, string name, string? unit, CancellationToken ct) =>
            Results.Content(await KopilotTools.ReadSimVar(sim, name, unit ?? "number", ct), "application/json")).LocalOnly();

        if (remote.OAuthEnabled)
        {
            app.MapOAuth();
        }

        app.MapMcp("/mcp");

        LogSecuritySummary(app.Logger, remote);
    }

    private static RouteHandlerBuilder LocalOnly(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
            RequestOrigin.IsLocal(context.HttpContext) ? await next(context) : Results.NotFound());

    private static void LogSecuritySummary(ILogger log, RemoteOptions remote)
    {
        if (remote.HasBearerToken)
        {
            log.LogInformation("Zugriffsschutz: Bearer-Token aktiv.");
        }
        else if (remote.BearerToken.Length > 0)
        {
            log.LogWarning("Bearer-Token ist kürzer als {Min} Zeichen und wird ignoriert. Neuen erzeugen mit: Kopilot.exe --generate-secret", RemoteOptions.MinBearerTokenLength);
        }

        if (remote.OAuthEnabled)
        {
            log.LogInformation("Zugriffsschutz: OAuth aktiv. Connector-URL für Claude: {Url}", remote.ResourceUrl);
        }
        else if (remote.PublicUrl.Length > 0 || remote.ConsentPassword.Length > 0)
        {
            var reason = remote.PublicBaseUri is null
                ? "PublicUrl muss eine https-Adresse ohne Pfad sein"
                : $"ConsentPassword muss mindestens {RemoteOptions.MinConsentPasswordLength} Zeichen haben";
            log.LogWarning("OAuth ist NICHT aktiv: {Reason}.", reason);
        }

        if (!remote.HasBearerToken && !remote.OAuthEnabled)
        {
            log.LogWarning("Kein Token konfiguriert – /mcp ist gesperrt. Die Browser-Prüfseiten funktionieren lokal weiterhin. Siehe README, 'Remote-Betrieb'.");
        }
    }
}
