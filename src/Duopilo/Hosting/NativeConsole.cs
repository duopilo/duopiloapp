using System.Runtime.InteropServices;

namespace Duopilo.Hosting;

/// <summary>
/// Duopilo ist eine Windows-Anwendung ohne Konsolenfenster (es gibt das Tray-Symbol).
/// Für --generate-secret und --console wird bei Bedarf eine Konsole angehängt bzw. geöffnet.
/// </summary>
internal static class NativeConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    /// <summary>
    /// An die Konsole des aufrufenden Prozesses (cmd, PowerShell) anhängen, falls es eine gibt.
    /// Ist stdout umgeleitet (z. B. "| more" oder in eine Datei), bleibt es dabei.
    /// </summary>
    public static bool AttachToParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var stdout = GetStdHandle(StdOutputHandle);
        return (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) && AttachConsole(AttachParentProcess);
    }

    /// <summary>Eigenes Konsolenfenster öffnen (Schalter --console, zur Fehlersuche).</summary>
    public static bool Open() => OperatingSystem.IsWindows() && AllocConsole();

    /// <summary>true, wenn Ausgaben auf einer Konsole landen (sonst würde z. B. Console.OutputEncoding scheitern).</summary>
    public static bool HasConsole => !OperatingSystem.IsWindows() || GetConsoleWindow() != IntPtr.Zero;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
}
