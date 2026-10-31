using Duopilo.Aircraft;
using Duopilo.Dashboard;
using Duopilo.Ecam;
using Duopilo.FlightPlan;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Remote;
using Duopilo.Reports;
using Duopilo.Settings;
using Duopilo.Sim;
using Duopilo.Tools;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Duopilo.Hosting;

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
            Args = DuopiloConfiguration.FilterSwitches(args),
            ContentRootPath = AppContext.BaseDirectory,
        });

        DuopiloConfiguration.AddLocalSettings(builder.Configuration, AppContext.BaseDirectory, DuopiloConfiguration.FilterSwitches(args));
        return builder;
    }

    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddDuopiloCore(builder.Configuration);
        builder.Services.TryAddSingleton(new LogBuffer());
        builder.Services.AddSingleton<DashboardData>();

        // Einstellungsseite und „Fehler melden“ im Dashboard
        builder.Services.AddSingleton(sp => LocalSettingsStore.ForApplication(sp.GetRequiredService<IOptions<DuopiloOptions>>().Value));
        builder.Services.AddSingleton<SettingsService>();

        // Smalltalk mit einem Sprachmodell (optional, standardmäßig aus) – Einstellungsseite und Sprachschleife
        builder.Services.AddSingleton<Duopilo.Smalltalk.SmalltalkService>();
        builder.Services.AddSingleton<BugReportService>();

        builder.Services.AddSingleton<OAuthStore>();
        builder.Services.AddSingleton<LoginThrottle>();

        // Erlaubte Hostnamen: lokal + die öffentliche Tunnel-Domain. Alles andere lehnt der Host-Filter ab.
        var remote = builder.Configuration.GetSection($"{DuopiloOptions.Section}:Remote").Get<RemoteOptions>() ?? new RemoteOptions();
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
        var remote = app.Services.GetRequiredService<IOptions<DuopiloOptions>>().Value.Remote;

        app.UseMiddleware<McpAuthMiddleware>();

        // Prüfseiten für den Browser – nur lokal, über den Tunnel nicht erreichbar.
        app.MapDashboard();
        app.MapSettings();
        app.MapReports();
        Duopilo.Updates.UpdateEndpoints.MapUpdates(app);
        Duopilo.Sim.SimConnectEndpoints.MapSimConnect(app);
        app.MapGet("/status", (SimConnectClient sim, ProfileStore profiles, MonitoringService monitoring, SimBriefService simBrief, EcamCatalog ecam, AlertBus alerts, Duopilo.Input.InputService input, Duopilo.Voice.IVoiceStatusSource voice) =>
            JsonText(DuopiloTools.Status(sim, profiles, monitoring, simBrief, ecam, alerts, input, voice))).LocalOnly();
        app.MapGet("/state", (SimConnectClient sim, AlertBus alerts) => JsonText(DuopiloTools.GetFlightState(sim, alerts))).LocalOnly();
        app.MapGet("/systems", (SimConnectClient sim, AlertBus alerts) => JsonText(DuopiloTools.GetSystems(sim, alerts))).LocalOnly();
        app.MapGet("/nav", (SimConnectClient sim, AlertBus alerts) => JsonText(DuopiloTools.GetNavigation(sim, alerts))).LocalOnly();
        app.MapGet("/env", (SimConnectClient sim, AlertBus alerts) => JsonText(DuopiloTools.GetEnvironment(sim, alerts))).LocalOnly();
        app.MapGet("/profile", (SimConnectClient sim, ProfileStore profiles, AlertBus alerts) => JsonText(DuopiloTools.GetAircraftProfile(sim, profiles, alerts))).LocalOnly();
        app.MapGet("/warnings", (SimConnectClient sim, EcamCatalog ecam, AlertBus alerts) => JsonText(MonitoringTools.GetWarnings(sim, ecam, alerts))).LocalOnly();
        app.MapGet("/alerts", (AlertBus alerts, long? sinceId) => JsonText(MonitoringTools.GetAlerts(alerts, sinceId ?? 0))).LocalOnly();
        app.MapGet("/monitoring", (MonitoringService monitoring, AlertBus alerts) => JsonText(MonitoringTools.GetMonitoringStatus(monitoring, alerts))).LocalOnly();
        app.MapGet("/atc", (SimConnectClient sim, SimBriefService simBrief, AlertBus alerts) => JsonText(AtcTools.GetAtc(sim, simBrief, alerts))).LocalOnly();
        app.MapGet("/atc/controller", (Duopilo.Atc.AtcService atc, AlertBus alerts) => JsonText(AtcControllerTools.GetAtcController(atc, alerts))).LocalOnly();
        app.MapGet("/plan", async (SimBriefService simBrief, AlertBus alerts, bool? refresh, CancellationToken ct) =>
            JsonText(await FlightPlanTools.GetFlightPlan(simBrief, alerts, refresh ?? false, ct))).LocalOnly();
        app.MapGet("/progress", async (SimConnectClient sim, SimBriefService simBrief, AlertBus alerts, MonitoringService monitoring, CancellationToken ct) =>
            JsonText(await FlightPlanTools.GetProgress(sim, simBrief, alerts, monitoring, ct))).LocalOnly();
        app.MapGet("/read", async (SimConnectClient sim, string name, string? unit, CancellationToken ct) =>
            JsonText(await DuopiloTools.ReadSimVar(sim, name, unit ?? "number", ct))).LocalOnly();
        app.MapGet("/voice", (Duopilo.Voice.IVoiceStatusSource voice) => JsonText(Json.Serialize(DuopiloTools.VoiceView(voice.Status)))).LocalOnly();

        // Input Events des geladenen Flugzeugs (wie list_input_events): /input-events?filter=FCU&changes=1
        app.MapGet("/input-events", async (Duopilo.Sim.InputEventExplorer explorer, string? filter, bool? values, bool? parameters, bool? changes, int? limit, CancellationToken ct) =>
            JsonText(await DuopiloTools.ListInputEvents(explorer, filter, values ?? true, parameters ?? false, changes ?? false, limit ?? Duopilo.Sim.InputEventExplorer.DefaultLimit, ct))).LocalOnly();

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
            log.LogInformation(Loc.Log("log.host.security.bearerActive"));
        }
        else if (remote.BearerToken.Length > 0)
        {
            log.LogWarning(Loc.Log("log.host.security.bearerTooShort"), RemoteOptions.MinBearerTokenLength);
        }

        if (remote.OAuthEnabled)
        {
            log.LogInformation(Loc.Log("log.host.security.oauthActive"), remote.ResourceUrl);
        }
        else if (remote.PublicUrl.Length > 0 || remote.ConsentPassword.Length > 0)
        {
            var reason = remote.PublicBaseUri is null
                ? Loc.T("host.security.publicUrlInvalid")
                : Loc.T("host.security.consentPasswordShort", RemoteOptions.MinConsentPasswordLength);
            log.LogWarning(Loc.Log("log.host.security.oauthInactive"), reason);
        }

        if (!remote.HasBearerToken && !remote.OAuthEnabled)
        {
            log.LogWarning(Loc.Log("log.host.security.noToken"));
        }
    }
}
