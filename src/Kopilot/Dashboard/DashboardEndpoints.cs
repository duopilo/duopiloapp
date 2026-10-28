using Kopilot.Aircraft;
using Kopilot.FlightPlan;
using Kopilot.Hosting;
using Kopilot.Sim;
using Kopilot.Tools;

namespace Kopilot.Dashboard;

/// <summary>
/// Das Dashboard im Browser (nur lokal): Seite, Schriften, Daten-API und zwei Knöpfe (Profile neu laden, SimBrief laden).
/// Schreibende Aufrufe verlangen den Header "X-Kopilot: 1" – fremde Webseiten im selben Browser können ihn
/// nicht ohne CORS-Freigabe setzen.
/// </summary>
public static class DashboardEndpoints
{
    public const string ActionHeader = "X-Kopilot";

    private const string ResourcePrefix = "Kopilot.Dashboard.";

    private static readonly Dictionary<string, string> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B612-Regular.woff"] = "fonts.B612-Regular.woff",
        ["B612-Bold.woff"] = "fonts.B612-Bold.woff",
        ["B612Mono-Regular.woff"] = "fonts.B612Mono-Regular.woff",
        ["B612Mono-Bold.woff"] = "fonts.B612Mono-Bold.woff",
    };

    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context) =>
        {
            NoCache(context);
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Resource("dashboard.html", "text/html; charset=utf-8");
        }).LocalOnly();

        app.MapGet("/dashboard/fonts/{file}", (string file, HttpContext context) =>
        {
            if (!Fonts.TryGetValue(file, out var resource))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Resource(resource, "font/woff");
        }).LocalOnly();

        app.MapGet("/favicon.ico", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Resource("favicon.ico", "image/x-icon");
        }).LocalOnly();

        app.MapGet("/api/dashboard", (DashboardData data, HttpContext context, long? since, bool? log) =>
        {
            NoCache(context);
            return Results.Content(Json.Serialize(data.Build(since ?? -1, log == true)), "application/json");
        }).LocalOnly();

        app.MapPost("/api/reload-profiles", (HttpContext context, SimConnectClient sim, ProfileStore profiles) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            profiles.Reload();
            sim.RefreshProfile();
            return Results.Content(Json.Serialize(new { profiles = profiles.All.Count, errors = profiles.Errors }), "application/json");
        }).LocalOnly();

        app.MapPost("/api/simbrief/refresh", async (HttpContext context, SimBriefService simBrief, CancellationToken ct) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var ofp = await simBrief.GetAsync(refresh: true, ct);
            return Results.Content(Json.Serialize(new { loaded = ofp is not null, error = simBrief.LastError }), "application/json");
        }).LocalOnly();
    }

    private static bool IsDashboardAction(HttpContext context) =>
        context.Request.Headers.TryGetValue(ActionHeader, out var value) && value == "1";

    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static IResult Resource(string name, string contentType)
    {
        var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream(ResourcePrefix + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }
}
