using Duopilo.Localization;

namespace Duopilo.Hosting;

public enum DuopiloTransport
{
    Stdio,
    Http,
}

/// <summary>Konfiguration laden, Transport bestimmen, Loopback-Bindung prüfen.</summary>
public static class DuopiloConfiguration
{
    public const string LocalSettingsFile = "appsettings.Local.json";

    /// <summary>
    /// Ergänzt appsettings.Local.json (Geheimnisse, nicht im Repo). Umgebungsvariablen und Kommandozeile werden
    /// danach erneut angehängt, damit sie weiterhin Vorrang vor Dateien haben. Alle drei lesen den alten Abschnitt
    /// "Kopilot" (bzw. Kopilot__…) mit (<see cref="LegacySectionSource"/>).
    /// </summary>
    public static void AddLocalSettings(IConfigurationBuilder configuration, string baseDirectory, string[] args)
    {
        configuration.Add(LegacySectionSource.Json(Path.Combine(baseDirectory, LocalSettingsFile)));
        configuration.Add(LegacySectionSource.EnvironmentVariables());
        configuration.Add(LegacySectionSource.CommandLine(args));
    }

    /// <summary>--stdio / --http haben Vorrang, sonst Duopilo:Transport aus der Konfiguration (Standard: http).</summary>
    public static DuopiloTransport ResolveTransport(string[] args, string baseDirectory)
    {
        if (args.Any(a => string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase)))
        {
            return DuopiloTransport.Stdio;
        }

        if (args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase)))
        {
            return DuopiloTransport.Http;
        }

        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(baseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        AddLocalSettings(builder, baseDirectory, FilterSwitches(args));
        var value = builder.Build()[$"{DuopiloOptions.Section}:{nameof(DuopiloOptions.Transport)}"];

        return string.Equals(value?.Trim(), "stdio", StringComparison.OrdinalIgnoreCase)
            ? DuopiloTransport.Stdio
            : DuopiloTransport.Http;
    }

    /// <summary>Prüft, dass der HTTP-Modus nur an eine Loopback-Adresse bindet.</summary>
    public static bool IsLoopbackUrl(string url, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = Loc.En("log.host.config.httpUrlInvalid", url);
            return false;
        }

        if (!Remote.RequestOrigin.IsLoopbackHost(uri.Host))
        {
            reason = Loc.En("log.host.config.httpUrlNotLoopback", url);
            return false;
        }

        reason = "";
        return true;
    }

    private static readonly string[] Switches = ["--stdio", "--http", "--generate-secret", "--console", "--no-tray", "--autostart", "--restarted", AppRestart.ReplaceSwitch];

    /// <summary>Entfernt Duopilo-eigene Schalter, die der Kommandozeilen-Provider nicht versteht.</summary>
    public static string[] FilterSwitches(string[] args) =>
        args.Where(a => !Switches.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
}
