namespace Duopilo.Hosting;

/// <summary>Ergebnis der Übernahme des alten Datenordners.</summary>
/// <param name="Directory">Datenordner, der in diesem Lauf gilt.</param>
/// <param name="Moved">Der alte Ordner wurde gerade übernommen (umbenannt).</param>
/// <param name="UsingLegacy">Übernahme ging nicht (Dateien in Benutzung) – dieser Lauf nutzt den alten Ordner weiter.</param>
/// <param name="Legacy">Pfad des alten Ordners.</param>
/// <param name="Error">Grund, warum die Übernahme nicht ging.</param>
public sealed record DataDirectoryOutcome(string Directory, bool Moved, bool UsingLegacy, string Legacy, string? Error = null);

/// <summary>
/// Datenordner %LOCALAPPDATA%\Duopilo – bis 0.9.x %LOCALAPPDATA%\Kopilot (Logs, Modelle inkl. Piper, reports, data- und
/// i18n-Overrides, ATC-Sitzungssicherung, Dashboard-Einstellungen, Claude-Anmeldungen).
/// <para>
/// Beim ersten Start wird der alte Ordner umbenannt (ein Schritt, nichts wird kopiert – die Modelle sind über 1 GB groß).
/// Geht das nicht, weil noch ein altes Kopilot läuft und Dateien offen hält, liest dieser Lauf einfach weiter aus dem alten
/// Ordner; der nächste Start versucht es erneut. Ein benannter Mutex verhindert, dass die stdio- und die Tray-Instanz
/// gleichzeitig umbenennen.
/// </para>
/// </summary>
public static class DataDirectoryMigration
{
    private static readonly Lazy<DataDirectoryOutcome> DefaultOutcome = new(
        () => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Standard-Datenordner dieses Laufs (beim ersten Zugriff wird der alte übernommen).</summary>
    public static string DefaultDirectory => DefaultOutcome.Value.Directory;

    /// <summary>Was beim ersten Zugriff passiert ist – null, solange niemand den Standardordner gebraucht hat.</summary>
    public static DataDirectoryOutcome? Outcome => DefaultOutcome.IsValueCreated ? DefaultOutcome.Value : null;

    /// <summary>Neuer und alter Standardordner unter <paramref name="localAppData"/>, ggf. mit Übernahme.</summary>
    public static DataDirectoryOutcome Resolve(string localAppData) =>
        Migrate(Path.Combine(localAppData, AppNames.Legacy.DataDirectory), Path.Combine(localAppData, AppNames.Product));

    /// <summary>
    /// Übernimmt <paramref name="legacy"/> nach <paramref name="target"/>, wenn es den neuen Ordner noch nicht gibt.
    /// Gibt es beide, gilt der neue (der alte bleibt liegen, das Deinstallieren fragt nach beiden).
    /// </summary>
    public static DataDirectoryOutcome Migrate(string legacy, string target)
    {
        if (Directory.Exists(target) || !Directory.Exists(legacy))
        {
            return new DataDirectoryOutcome(target, Moved: false, UsingLegacy: false, legacy);
        }

        using var gate = CrossProcessGate.Enter($"{AppNames.Product}-DataDirectoryMigration");

        // Nach dem Warten nochmal: Die andere Instanz hat vielleicht gerade übernommen
        if (Directory.Exists(target) || !Directory.Exists(legacy))
        {
            return new DataDirectoryOutcome(target, Moved: false, UsingLegacy: false, legacy);
        }

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(target));
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(legacy, target);
            return new DataDirectoryOutcome(target, Moved: true, UsingLegacy: false, legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Directory.Exists(target)
                ? new DataDirectoryOutcome(target, Moved: false, UsingLegacy: false, legacy)
                : new DataDirectoryOutcome(legacy, Moved: false, UsingLegacy: true, legacy, ex.Message);
        }
    }

    /// <summary>
    /// Ein eingestellter Pfad, der noch in den alten Standardordner zeigt (z. B. ein eigenes Modell unter
    /// %LOCALAPPDATA%\Kopilot\models), zeigt nach der Übernahme in den neuen. Alles andere bleibt, wie es ist.
    /// </summary>
    public static string MapLegacyPath(string path)
    {
        // Erst prüfen, ob der Pfad überhaupt im alten Ordner liegt – sonst nichts anstoßen
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return path;
        }

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppNames.Legacy.DataDirectory);
        return IsInside(path, legacy) ? MapLegacyPath(path, DefaultOutcome.Value) : path;
    }

    /// <summary>Wie <see cref="MapLegacyPath(string)"/> gegen ein bestimmtes Ergebnis (Tests).</summary>
    public static string MapLegacyPath(string path, DataDirectoryOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(path) || outcome.UsingLegacy || !Path.IsPathRooted(path) || !IsInside(path, outcome.Legacy))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            return path;
        }

        var legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outcome.Legacy));
        return Path.Join(outcome.Directory, full[legacy.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static bool IsInside(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Kurzer prozessübergreifender Abschnitt über einen benannten Mutex (pro Windows-Sitzung). Ohne Mutex geht es auch.</summary>
internal sealed class CrossProcessGate : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _owned;

    private CrossProcessGate(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public static CrossProcessGate Enter(string name, int timeoutMs = 30000)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, $"Local\\{name}");
            bool owned;
            try
            {
                owned = mutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // Die andere Instanz ist mittendrin abgestürzt – der Abschnitt gehört jetzt uns
                owned = true;
            }

            return new CrossProcessGate(mutex, owned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            mutex?.Dispose();
            return new CrossProcessGate(null, owned: false);
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
    }
}
