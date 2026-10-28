namespace Kopilot.Hosting;

public enum KopilotTransport
{
    Stdio,
    Http,
}

/// <summary>Konfiguration laden, Transport bestimmen, Loopback-Bindung prüfen.</summary>
public static class KopilotConfiguration
{
    public const string LocalSettingsFile = "appsettings.Local.json";

    /// <summary>
    /// Ergänzt appsettings.Local.json (Geheimnisse, nicht im Repo). Umgebungsvariablen und Kommandozeile werden
    /// danach erneut angehängt, damit sie weiterhin Vorrang vor Dateien haben.
    /// </summary>
    public static void AddLocalSettings(IConfigurationBuilder configuration, string baseDirectory, string[] args)
    {
        configuration.AddJsonFile(Path.Combine(baseDirectory, LocalSettingsFile), optional: true, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);
    }

    /// <summary>--stdio / --http haben Vorrang, sonst Kopilot:Transport aus der Konfiguration (Standard: http).</summary>
    public static KopilotTransport ResolveTransport(string[] args, string baseDirectory)
    {
        if (args.Any(a => string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase)))
        {
            return KopilotTransport.Stdio;
        }

        if (args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase)))
        {
            return KopilotTransport.Http;
        }

        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(baseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        AddLocalSettings(builder, baseDirectory, FilterSwitches(args));
        var value = builder.Build()[$"{KopilotOptions.Section}:{nameof(KopilotOptions.Transport)}"];

        return string.Equals(value?.Trim(), "stdio", StringComparison.OrdinalIgnoreCase)
            ? KopilotTransport.Stdio
            : KopilotTransport.Http;
    }

    /// <summary>Prüft, dass der HTTP-Modus nur an eine Loopback-Adresse bindet.</summary>
    public static bool IsLoopbackUrl(string url, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = $"Ungültige HttpUrl: '{url}'.";
            return false;
        }

        if (!Remote.RequestOrigin.IsLoopbackHost(uri.Host))
        {
            reason = $"HttpUrl '{url}' bindet nicht an localhost. Erlaubt sind nur 127.0.0.1, localhost und [::1] – " +
                     "der Zugang von außen läuft ausschließlich über den Cloudflare Tunnel.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Entfernt Kopilot-eigene Schalter, die der Kommandozeilen-Provider nicht versteht.</summary>
    public static string[] FilterSwitches(string[] args) =>
        args.Where(a => !string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(a, "--generate-secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
