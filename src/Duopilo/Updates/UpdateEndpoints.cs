using Duopilo.Dashboard;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Settings;
using Duopilo.Tools;

namespace Duopilo.Updates;

/// <summary>
/// Update-Prüfung im Dashboard: Zustand, jetzt prüfen, installieren, Einstellungen. Nur lokal; schreibende Aufrufe mit dem
/// Header "X-Duopilo: 1" wie die übrigen Dashboard-Aktionen.
/// </summary>
public static class UpdateEndpoints
{
    /// <summary>Einstellungen der Update-Prüfung (null = unverändert).</summary>
    public sealed record UpdateSettingsBody(bool? Check, string? Prereleases);

    public static void MapUpdates(this WebApplication app)
    {
        app.MapGet("/api/update", (HttpContext context) =>
        {
            NoCache(context);
            return Answer(context, null, null);
        }).LocalOnly();

        app.MapPost("/api/update/check", async (HttpContext context, CancellationToken ct) =>
        {
            if (!DashboardEndpoints.IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            if (context.RequestServices.GetService<UpdateService>() is { } updates)
            {
                await updates.CheckAsync(ct);
            }

            return Answer(context, null, null);
        }).LocalOnly();

        app.MapPost("/api/update/install", (HttpContext context) =>
        {
            if (!DashboardEndpoints.IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var error = context.RequestServices.GetService<UpdateService>() is { } updates ? updates.Install() : Loc.T("update.trayOnly");
            return Answer(context, error is null ? Loc.T("update.install.started") : null, error);
        }).LocalOnly();

        app.MapPost("/api/update/settings", (HttpContext context, SettingsService settings, UpdateSettingsBody body) =>
        {
            if (!DashboardEndpoints.IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var result = settings.SaveUpdates(body.Check, body.Prereleases);
            return Answer(context, result.Ok ? result.Message : null, result.Ok ? null : result.Error);
        }).LocalOnly();
    }

    /// <summary>Zustand fürs Dashboard (ohne Tray-Instanz: available = false).</summary>
    public static object View(UpdateService? updates) => updates?.Status is { } s
        ? (object)new
        {
            available = true,
            state = s.State,
            current = s.Current,
            latest = s.Latest,
            latestName = s.LatestName,
            pageUrl = s.PageUrl,
            latestIsPrerelease = s.LatestIsPrerelease,
            installable = s.Installable,
            updateAvailable = s.Available,
            lastCheckUtc = s.LastCheckUtc,
            error = s.Error,
            progress = s.Progress,
            check = s.CheckEnabled,
            prereleases = s.Prereleases,
            prereleaseOptions = UpdateLogic.PrereleaseOptions,
            installedBySetup = UpdateService.InstalledBySetup,
        }
        : new { available = false, current = UpdateLogic.CurrentVersion(typeof(UpdateService).Assembly).ToString() };

    private static IResult Answer(HttpContext context, string? message, string? error) =>
        Results.Content(
            Json.Serialize(new { ok = error is null, message, error, update = View(context.RequestServices.GetService<UpdateService>()) }),
            "application/json");

    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-store";
}
