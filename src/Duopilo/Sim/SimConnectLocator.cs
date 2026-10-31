using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Duopilo.Sim;

/// <summary>Gefundene SimConnect-Bibliotheken.</summary>
/// <param name="Managed">Microsoft.FlightSimulator.SimConnect.dll</param>
/// <param name="Native">SimConnect.dll (x64)</param>
/// <param name="Source">"app" (neben Duopilo.exe – selbst gebaut oder von Hand hineinkopiert) oder "sdk" (aus dem installierten MSFS-SDK).</param>
/// <param name="SdkRoot">Ordner des SDK (bei "sdk").</param>
public sealed record SimConnectFiles(string Managed, string Native, string Source, string? SdkRoot);

/// <summary>
/// Findet SimConnect und lädt es von dort. Die Release-Pakete enthalten die Bibliotheken nicht (sie gehören zum MSFS-SDK, ihre
/// Weitergabe ist nicht geklärt): Sie kommen aus dem installierten SDK. Reihenfolge: neben Duopilo.exe, der eingestellte
/// SDK-Ordner (Duopilo:SimConnectSdkPath), die Umgebungsvariable MSFS2024_SDK (Prozess, Benutzer, System), C:\MSFS 2024 SDK.
/// <para>
/// Laden aus dem SDK: die native SimConnect.dll einmal mit vollem Pfad (danach findet Windows sie für alle, die sie per Namen
/// laden – auch die verwaltete Bibliothek und <see cref="NativeSimConnect"/>), die verwaltete über
/// <see cref="AssemblyLoadContext.Resolving"/>, sobald .NET sie braucht. <see cref="Prepare"/> läuft in Program.cs gleich nach dem
/// Lesen der Einstellungen. Der Resolver hängt schon ab dem Laden von Duopilo.dll (Modul-Initialisierer): Braucht .NET SimConnect
/// doch früher, sucht er ohne den Ordner aus der Datei (nur Umgebungsvariablen).
/// </para>
/// </summary>
public static class SimConnectLocator
{
    public const string ManagedName = "Microsoft.FlightSimulator.SimConnect";
    public const string ManagedFile = ManagedName + ".dll";
    public const string NativeFile = "SimConnect.dll";
    public const string SdkVariable = "MSFS2024_SDK";
    public const string DefaultSdkRoot = @"C:\MSFS 2024 SDK";

    private static readonly object Gate = new();
    private static nint _native;
    private static bool _prepared;

    /// <summary>Was beim Start gefunden und geladen wurde (null = nichts gefunden).</summary>
    public static SimConnectFiles? Current { get; private set; }

    /// <summary>Fehler beim Laden der gefundenen Bibliotheken (null = keiner).</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Verwaltete Bibliothek im SDK-Ordner.</summary>
    public static string ManagedIn(string sdkRoot) => Path.Combine(sdkRoot, "SimConnect SDK", "lib", "managed", ManagedFile);

    /// <summary>Native Bibliothek im SDK-Ordner.</summary>
    public static string NativeIn(string sdkRoot) => Path.Combine(sdkRoot, "SimConnect SDK", "lib", NativeFile);

    /// <summary>Mögliche SDK-Ordner in Reihenfolge, ohne Doppelte.</summary>
    public static IReadOnlyList<string> SdkRoots(string? configured, Func<string, string?> environment)
    {
        var roots = new List<string>();
        foreach (var candidate in new[] { configured, environment(SdkVariable), DefaultSdkRoot })
        {
            var root = candidate?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    /// <summary>SimConnect suchen (rein rechnend, damit testbar).</summary>
    public static SimConnectFiles? Find(string appDirectory, string? configured, Func<string, string?> environment, Func<string, bool> exists)
    {
        var appManaged = Path.Combine(appDirectory, ManagedFile);
        var appNative = Path.Combine(appDirectory, NativeFile);
        if (exists(appManaged) && exists(appNative))
        {
            return new SimConnectFiles(appManaged, appNative, "app", null);
        }

        foreach (var root in SdkRoots(configured, environment))
        {
            var managed = ManagedIn(root);
            var native = NativeIn(root);
            if (exists(managed) && exists(native))
            {
                return new SimConnectFiles(managed, native, "sdk", root);
            }
        }

        return null;
    }

    /// <summary>Ist in <paramref name="sdkRoot"/> ein SDK mit SimConnect?</summary>
    public static bool IsSdkRoot(string? sdkRoot) =>
        !string.IsNullOrWhiteSpace(sdkRoot) && File.Exists(ManagedIn(sdkRoot.Trim().Trim('"'))) && File.Exists(NativeIn(sdkRoot.Trim().Trim('"')));

    /// <summary>Umgebungsvariable aus Prozess, Benutzer oder System (ein frisch installiertes SDK steht erst in der Registry).</summary>
    public static string? Environment(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
        {
            return value;
        }

        return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
    }

    /// <summary>Jetzt suchen (Dashboard, nach der Installation des SDK) – lädt nichts.</summary>
    public static SimConnectFiles? Probe(string? configured) => Find(AppContext.BaseDirectory, configured, Environment, File.Exists);

    /// <summary>Resolver für die verwaltete Bibliothek, sobald Duopilo.dll geladen ist – greift nur, wenn .NET sie sonst nicht findet.</summary>
#pragma warning disable CA2255 // Absicht: Duopilo ist die Anwendung, der Resolver muss vor jedem Zugriff auf SimConnect hängen
    [ModuleInitializer]
    internal static void RegisterResolver() => AssemblyLoadContext.Default.Resolving += ResolveManaged;
#pragma warning restore CA2255

    /// <summary>Beim Start einmal: SimConnect suchen und, wenn es aus dem SDK kommt, von dort laden.</summary>
    public static SimConnectFiles? Prepare(string? configured)
    {
        lock (Gate)
        {
            if (_prepared)
            {
                return Current;
            }

            _prepared = true;
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var files = Probe(configured);
            Current = files;
            if (files is null || files.Source == "app")
            {
                return files;
            }

            try
            {
                _native = NativeLibrary.Load(files.Native);
                NativeLibrary.SetDllImportResolver(typeof(SimConnectLocator).Assembly, ResolveNative);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                LoadError = ex.Message;
                Current = null;
            }

            return Current;
        }
    }

    private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName name)
    {
        if (!string.Equals(name.Name, ManagedName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Früher gebraucht als Program.cs Prepare aufruft: ohne Einstellungsdatei suchen. Auch "app": Nach einem Update liegen die
        // DLLs einer älteren Version noch im Programmordner, stehen aber nicht mehr in Duopilo.deps.json – .NET lädt sie dann nicht selbst.
        var files = _prepared ? Current : Prepare(System.Environment.GetEnvironmentVariable("Duopilo__" + nameof(DuopiloOptions.SimConnectSdkPath)));
        if (files is null)
        {
            return null;
        }

        try
        {
            return context.LoadFromAssemblyPath(files.Managed);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            LoadError = ex.Message;
            return null;
        }
    }

    private static nint ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        _native != 0 && (string.Equals(libraryName, NativeFile, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(libraryName, "SimConnect", StringComparison.OrdinalIgnoreCase))
            ? _native
            : 0;
}
