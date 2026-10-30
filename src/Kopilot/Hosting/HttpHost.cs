using Kopilot.Aircraft;
using Kopilot.Dashboard;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Remote;
using Kopilot.Sim;
using Kopilot.Tools;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.TryAddSingleton(new LogBuffer());
        builder.Services.AddSingleton<DashboardData>();
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
        app.MapDashboard();
        app.MapGet("/status", (SimConnectClient sim, ProfileStore profiles, MonitoringService monitoring, SimBriefService simBrief, EcamCatalog ecam, AlertBus alerts, Kopilot.Input.InputService input, Kopilot.Voice.IVoiceStatusSource voice) =>
            JsonText(KopilotTools.Status(sim, profiles, monitoring, simBrief, ecam, alerts, input, voice))).LocalOnly();
        app.MapGet("/state", (SimConnectClient sim, AlertBus alerts) => JsonText(KopilotTools.GetFlightState(sim, alerts))).LocalOnly();
        app.MapGet("/systems", (SimConnectClient sim, AlertBus alerts) => JsonText(KopilotTools.GetSystems(sim, alerts))).LocalOnly();
        app.MapGet("/nav", (SimConnectClient sim, AlertBus alerts) => JsonText(KopilotTools.GetNavigation(sim, alerts))).LocalOnly();
        app.MapGet("/env", (SimConnectClient sim, AlertBus alerts) => JsonText(KopilotTools.GetEnvironment(sim, alerts))).LocalOnly();
        app.MapGet("/profile", (SimConnectClient sim, ProfileStore profiles, AlertBus alerts) => JsonText(KopilotTools.GetAircraftProfile(sim, profiles, alerts))).LocalOnly();
        app.MapGet("/warnings", (SimConnectClient sim, EcamCatalog ecam, AlertBus alerts) => JsonText(MonitoringTools.GetWarnings(sim, ecam, alerts))).LocalOnly();
        app.MapGet("/alerts", (AlertBus alerts, long? sinceId) => JsonText(MonitoringTools.GetAlerts(alerts, sinceId ?? 0))).LocalOnly();
        app.MapGet("/monitoring", (MonitoringService monitoring, AlertBus alerts) => JsonText(MonitoringTools.GetMonitoringStatus(monitoring, alerts))).LocalOnly();
        app.MapGet("/atc", (SimConnectClient sim, SimBriefService simBrief, AlertBus alerts) => JsonText(AtcTools.GetAtc(sim, simBrief, alerts))).LocalOnly();
        app.MapGet("/atc/controller", (Kopilot.Atc.AtcService atc, AlertBus alerts) => JsonText(AtcControllerTools.GetAtcController(atc, alerts))).LocalOnly();
        app.MapGet("/plan", async (SimBriefService simBrief, AlertBus alerts, bool? refresh, CancellationToken ct) =>
            JsonText(await FlightPlanTools.GetFlightPlan(simBrief, alerts, refresh ?? false, ct))).LocalOnly();
        app.MapGet("/progress", async (SimConnectClient sim, SimBriefService simBrief, AlertBus alerts, MonitoringService monitoring, CancellationToken ct) =>
            JsonText(await FlightPlanTools.GetProgress(sim, simBrief, alerts, monitoring, ct))).LocalOnly();
        app.MapGet("/read", async (SimConnectClient sim, string name, string? unit, CancellationToken ct) =>
            JsonText(await KopilotTools.ReadSimVar(sim, name, unit ?? "number", ct))).LocalOnly();
        app.MapGet("/voice", (Kopilot.Voice.IVoiceStatusSource voice) => JsonText(Json.Serialize(KopilotTools.VoiceView(voice.Status)))).LocalOnly();

        if (remote.OAuthEnabled)
        {
            app.MapOAuth();
        }

        app.MapMcp("/mcp");

        LogSecuritySummary(app.Logger, remote);
    }

    private static IResult JsonText(string json) => Results.Content(json, "application/json");

    internal static RouteHandlerBuilder LocalOnly(this RouteHandlerBuilder builder) =>
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
