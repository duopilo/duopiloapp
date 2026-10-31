using Duopilo.Dashboard;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Settings;
using Duopilo.Tools;

namespace Duopilo.Sim;

/// <summary>
/// SimConnect im Dashboard: Zustand, SDK von Microsoft installieren, SDK-Ordner einstellen. Nur lokal; schreibende Aufrufe mit
/// dem Header "X-Duopilo: 1". Neu starten geht über /api/restart.
/// </summary>
public static class SimConnectEndpoints
{
    public sealed record SdkPathBody(string? Path);

    public static void MapSimConnect(this WebApplication app)
    {
        app.MapGet("/api/simconnect", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Answer(context, null, null);
        }).LocalOnly();

        app.MapPost("/api/simconnect/install", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!DashboardEndpoints.IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var error = context.RequestServices.GetService<SdkInstaller>() is { } installer
                ? installer.Start(lifetime.ApplicationStopping)
                : Loc.T("simconnect.sdk.trayOnly");
            return Answer(context, error is null ? Loc.T("simconnect.sdk.started") : null, error);
        }).LocalOnly();

        app.MapPost("/api/simconnect/path", (HttpContext context, SettingsService settings, SdkPathBody body) =>
        {
            if (!DashboardEndpoints.IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var result = settings.SaveSimConnectSdkPath(body.Path);
            return Answer(context, result.Ok ? result.Message : null, result.Ok ? null : result.Error);
        }).LocalOnly();
    }

    /// <summary>Zustand fürs Dashboard; ohne Tray-Instanz nur, was beim Start gefunden wurde.</summary>
    public static object View(SdkInstaller? installer)
    {
        if (installer?.Status is { } s)
        {
            return new
            {
                found = s.Found,
                source = s.Source,
                sdkRoot = s.SdkRoot,
                loadError = s.LoadError,
                foundNow = s.FoundNow,
                foundNowRoot = s.FoundNowRoot,
                state = s.State,
                progress = s.Progress,
                error = s.Error,
                sdkVersion = s.SdkVersion,
                defaultSdkRoot = s.DefaultSdkRoot,
                configuredPath = s.ConfiguredPath,
                canInstall = s.CanInstall,
            };
        }

        var current = SimConnectLocator.Current;
        return new
        {
            found = current is not null,
            source = current?.Source,
            sdkRoot = current?.SdkRoot,
            loadError = SimConnectLocator.LoadError,
            foundNow = current is not null,
            foundNowRoot = current?.SdkRoot,
            state = "idle",
            progress = (double?)null,
            error = (string?)null,
            sdkVersion = SdkInstaller.Version,
            defaultSdkRoot = SimConnectLocator.DefaultSdkRoot,
            configuredPath = (string?)null,
            canInstall = false,
        };
    }

    private static IResult Answer(HttpContext context, string? message, string? error) =>
        Results.Content(
            Json.Serialize(new { ok = error is null, message, error, simconnect = View(context.RequestServices.GetService<SdkInstaller>()) }),
            "application/json");
}
