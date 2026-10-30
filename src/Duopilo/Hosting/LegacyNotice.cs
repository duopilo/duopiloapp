using Duopilo.Localization;

namespace Duopilo.Hosting;

/// <summary>
/// Meldet beim Start im Log, was vom alten Namen „Kopilot“ übernommen wurde: Datenordner umbenannt, alter Ordner noch in
/// Benutzung, Einstellungen unter dem alten Abschnitt "Kopilot".
/// </summary>
internal sealed class LegacyNotice(IConfiguration configuration, ILogger<LegacyNotice> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        switch (DataDirectoryMigration.Outcome)
        {
            case { Moved: true } moved:
                log.LogInformation(Loc.Log("log.host.legacy.dataMoved"), moved.Legacy, moved.Directory);
                break;
            case { UsingLegacy: true } legacy:
                log.LogWarning(Loc.Log("log.host.legacy.dataInUse"), legacy.Legacy, legacy.Error);
                break;
        }

        if (configuration.GetSection(AppNames.Legacy.ConfigSection).Exists())
        {
            log.LogInformation(Loc.Log("log.host.legacy.configSection"), AppNames.Legacy.ConfigSection, DuopiloOptions.Section);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Läuft noch ein altes Kopilot (bis 0.9.x) im HTTP-Modus auf diesem Port? (Dessen Mutex hieß Local\Kopilot-&lt;Port&gt;.)</summary>
internal static class LegacyInstance
{
    public static bool IsRunning(int port)
    {
        try
        {
            if (Mutex.TryOpenExisting($"Local\\{AppNames.Legacy.Product}-{port}", out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Gibt es, gehört aber einem anderen Benutzer
            return true;
        }
        catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
