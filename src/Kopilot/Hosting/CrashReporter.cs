using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Kopilot.Hosting;

/// <summary>
/// Absturzprotokoll:
/// <list type="bullet">
/// <item>Unbehandelte Ausnahmen landen sofort (synchron) in logs\crash.log und im Kopilot-Log.</item>
/// <item>Harte Abstürze (AccessViolation in SimConnect, FailFast) lassen sich in .NET nicht abfangen. Dafür führt Kopilot
/// ein <see cref="CrashJournal"/>; beim nächsten Start meldet es den nicht sauber beendeten Lauf, die letzten
/// Einträge und – aus der Windows-Ereignisanzeige – die Ausnahme mit Stacktrace.</item>
/// <item>Windows startet Kopilot nach einem Absturz neu (RegisterApplicationRestart, erst nach 60 s Laufzeit – keine Schleife).</item>
/// </list>
/// </summary>
public static class CrashReporter
{
    public const string CrashLogFile = "crash.log";

    private const string EventLogUnreadable = "Ereignisanzeige nicht lesbar";

    // RegisterApplicationRestart: nicht nach Updates/Neustarts des Systems, nur nach Absturz oder Hänger
    private const int RestartNoPatch = 4;
    private const int RestartNoReboot = 8;

    private static string? _logDirectory;
    private static string _mode = "";

    /// <summary>Globale Handler für unbehandelte Ausnahmen (beide Modi).</summary>
    public static void InstallHandlers(string logDirectory, string mode)
    {
        _logDirectory = logDirectory;
        _mode = mode;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(e.ExceptionObject as Exception, $"Unbehandelte Ausnahme ({(e.IsTerminating ? "Kopilot wird beendet" : "Kopilot läuft weiter")})");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception, "Nicht beobachtete Task-Ausnahme (Kopilot läuft weiter)");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Tray-Instanz: Journal öffnen und einen vorigen Absturz melden. Rückgabe: das Journal (beim Beenden <see cref="CrashJournal.MarkClean"/>).
    /// </summary>
    public static CrashJournal? StartJournal(string logDirectory, string mode, ILogger log)
    {
        _logDirectory ??= logDirectory;
        var path = Path.Combine(logDirectory, $"journal-{mode}.bin");
        CrashJournal journal;
        CrashJournalRun? previous;
        try
        {
            (journal, previous) = CrashJournal.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Absturz-Journal nicht verfügbar ({Path}): {Message}", path, ex.Message);
            return null;
        }

        CrashJournal.Note($"Start Kopilot {Version} ({mode}), PID {Environment.ProcessId}");
        if (previous is not null)
        {
            ReportPrevious(previous, log);
        }

        return journal;
    }

    /// <summary>Nach einem Absturz startet Windows Kopilot neu (mit denselben Schaltern und --restarted).</summary>
    public static void RegisterRestart(string[] args, ILogger log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var commandLine = string.Join(' ', args.Where(a => !a.Equals("--restarted", StringComparison.OrdinalIgnoreCase)).Append("--restarted").Select(Quote));
        try
        {
            var hr = RegisterApplicationRestart(commandLine, RestartNoPatch | RestartNoReboot);
            if (hr != 0)
            {
                log.LogWarning("Neustart nach Absturz nicht registriert (HRESULT 0x{Hr:X8}).", hr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogWarning("Neustart nach Absturz nicht verfügbar: {Message}", ex.Message);
        }
    }

    /// <summary>Ausnahme sofort und ohne Warteschlange festhalten (der Prozess ist danach vielleicht weg).</summary>
    public static void WriteCrash(Exception? exception, string title)
    {
        var text = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(" CRT Kopilot ").Append(Version).Append(" (").Append(_mode).Append(", PID ").Append(Environment.ProcessId).Append("): ")
            .AppendLine(title)
            .AppendLine(exception?.ToString() ?? "(keine Ausnahme übergeben)")
            .AppendLine()
            .ToString();

        CrashJournal.Note($"{title}: {exception?.GetType().Name}: {exception?.Message}");
        if (_logDirectory is null)
        {
            return;
        }

        foreach (var file in new[] { CrashLogFile, _mode == "stdio" ? "kopilot-stdio.log" : "kopilot.log" })
        {
            try
            {
                File.AppendAllText(Path.Combine(_logDirectory, file), text, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Datei gesperrt (Log-Thread schreibt gerade) – crash.log reicht
            }
        }
    }

    private static void ReportPrevious(CrashJournalRun run, ILogger log)
    {
        var end = run.LastWriteUtc ?? run.StartUtc;
        log.LogWarning(
            "Kopilot wurde zuletzt NICHT sauber beendet: Lauf mit PID {Pid}, gestartet {Start:HH:mm:ss}Z, letztes Lebenszeichen {End:HH:mm:ss}Z{State}. " +
            "Vermutlich ein Absturz – Details folgen und stehen in {CrashLog}.",
            run.Pid,
            run.StartUtc,
            end,
            run.State is null ? "" : $", zuletzt in \"{run.State}\" (seit {run.StateSinceUtc:HH:mm:ss.fff}Z)",
            CrashLogFile);

        var report = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(" Bericht nach Neustart: Lauf PID ").Append(run.Pid)
            .Append(", Start ").Append(run.StartUtc.ToString("u", CultureInfo.InvariantCulture))
            .Append(", letztes Lebenszeichen ").Append(end.ToString("u", CultureInfo.InvariantCulture))
            .AppendLine(run.State is null ? "" : $", zuletzt in \"{run.State}\"");

        var last = run.Entries.TakeLast(15).ToList();
        if (last.Count > 0)
        {
            report.AppendLine("Letzte Einträge im Journal:");
            foreach (var (time, text) in last)
            {
                report.Append("  ").Append(time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z ").AppendLine(text);
            }

            log.LogWarning(
                "Letzte Einträge vor dem Absturz: {Entries}",
                string.Join(" | ", run.Entries.TakeLast(6).Select(e => $"{e.TimeUtc:HH:mm:ss}Z {e.Text}")));
        }

        // Die Ereignisanzeige kennt die Ausnahme (".NET Runtime", Ereignis 1026) und das abgestürzte Modul ("Application Error", 1000)
        var events = WindowsCrashEvents(run.StartUtc.AddMinutes(-1), DateTimeOffset.UtcNow);
        foreach (var e in events)
        {
            report.AppendLine().Append("Ereignisanzeige ").AppendLine(e);
            if (e.StartsWith(EventLogUnreadable, StringComparison.Ordinal))
            {
                log.LogWarning("{Message}", e);
                continue;
            }

            var lines = e.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var exception = lines.FirstOrDefault(l => l.StartsWith("Exception Info", StringComparison.OrdinalIgnoreCase)
                                                     || l.Contains("Exception", StringComparison.Ordinal));
            var frames = lines.Where(l => l.StartsWith("at ", StringComparison.Ordinal)).Take(4);
            log.LogError("Absturzursache laut Ereignisanzeige: {Exception} {Frames}", exception ?? lines.FirstOrDefault(), string.Join(" ← ", frames));
        }

        if (events.Count == 0)
        {
            report.AppendLine("Ereignisanzeige: kein passender Eintrag für Kopilot gefunden.");
        }

        report.AppendLine();
        try
        {
            File.AppendAllText(Path.Combine(_logDirectory ?? ".", CrashLogFile), report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("{File} nicht geschrieben: {Message}", CrashLogFile, ex.Message);
        }
    }

    /// <summary>Fehler-Einträge von ".NET Runtime" und "Application Error" zu Kopilot im Anwendungsprotokoll.</summary>
    public static IReadOnlyList<string> WindowsCrashEvents(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        try
        {
            var since = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var until = to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var xpath = "*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error']) and (Level=1 or Level=2) " +
                        $"and TimeCreated[@SystemTime>='{since}' and @SystemTime<='{until}']]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath) { ReverseDirection = true });
            for (var record = reader.ReadEvent(); record is not null && result.Count < 4; record = reader.ReadEvent())
            {
                using (record)
                {
                    string? message;
                    try
                    {
                        message = record.FormatDescription();
                    }
                    catch (EventLogException)
                    {
                        message = null;
                    }

                    message ??= string.Join(" | ", record.Properties.Select(p => p.Value?.ToString()));
                    if (message.Contains("Kopilot", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add($"{record.TimeCreated?.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z {record.ProviderName} (Ereignis {record.Id}):\n{message.Trim()}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            result.Add($"{EventLogUnreadable}: {ex.Message}");
        }

        result.Reverse();
        return result;
    }

    private static string Version => typeof(CrashReporter).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);
}
