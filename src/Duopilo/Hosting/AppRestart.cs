using System.Diagnostics;
using Kopilot.Localization;

namespace Kopilot.Hosting;

/// <summary>
/// Neustart der Tray-Instanz aus dem Dashboard (z. B. nach geänderten Einstellungen für Claude, Monitoring oder ATC).
/// Startet eine neue Kopilot.exe mit <see cref="ReplaceSwitch"/> und beendet die laufende sauber. Die neue Instanz wartet,
/// bis die alte ihren Instanz-Mutex freigibt – sonst würde sie nur das Dashboard öffnen und sich beenden.
/// Die ATC-Sitzung kommt wie nach einem Absturz aus der Sitzungsdatei zurück.
/// </summary>
public sealed class AppRestart(string[] args, IHostApplicationLifetime lifetime, ILogger<AppRestart> log)
{
    /// <summary>Schalter der neuen Instanz: auf die laufende warten statt nur das Dashboard zu öffnen.</summary>
    public const string ReplaceSwitch = "--replace";

    /// <summary>So lange wartet die neue Instanz höchstens auf das Ende der alten (Herunterfahren dauert bis zu 30 s).</summary>
    public static readonly TimeSpan WaitForPreviousTimeout = TimeSpan.FromSeconds(120);

    private int _started;

    public bool Available => OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.ProcessPath);

    /// <summary>Neue Instanz starten und diese beenden. Rückgabe: Fehlertext oder null.</summary>
    public string? Restart(string reason)
    {
        if (!Available)
        {
            return Loc.T("host.restart.unavailable");
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return null;
        }

        var arguments = args
            .Where(a => !a.Equals(ReplaceSwitch, StringComparison.OrdinalIgnoreCase) && !a.Equals("--restarted", StringComparison.OrdinalIgnoreCase))
            .Append(ReplaceSwitch)
            .ToList();
        try
        {
            // ShellExecute: die neue Instanz erbt keine Handles (Mutex, Sockets) von dieser
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = string.Join(' ', arguments.Select(a => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a)),
            };
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Interlocked.Exchange(ref _started, 0);
            log.LogWarning(Loc.Log("log.host.restart.failed"), ex.Message);
            return Loc.T("host.restart.failed", ex.Message);
        }

        log.LogInformation(Loc.Log("log.host.restart.restarting"), reason);

        // Kurz warten, damit die Antwort ans Dashboard noch rausgeht
        _ = Task.Delay(TimeSpan.FromMilliseconds(400)).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
        return null;
    }

    /// <summary>Neue Instanz: auf die alte warten. true = Mutex übernommen.</summary>
    public static bool WaitForPrevious(Mutex instance, TimeSpan timeout)
    {
        try
        {
            return instance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // Die alte Instanz ist beendet, ohne den Mutex freizugeben – er gehört jetzt dieser
            return true;
        }
    }
}
