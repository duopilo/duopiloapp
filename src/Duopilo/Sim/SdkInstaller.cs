using System.Diagnostics;
using System.IO.Compression;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Voice;
using Microsoft.Extensions.Options;

namespace Duopilo.Sim;

/// <summary>Zustand von SimConnect und der Installation des SDK für Dashboard und Tray.</summary>
/// <param name="Found">SimConnect wurde beim Start gefunden und geladen.</param>
/// <param name="Source">"app" oder "sdk" (<see cref="SimConnectFiles.Source"/>), null = nicht gefunden.</param>
/// <param name="FoundNow">Jetzt auf der Platte (z. B. nach der Installation) – dann reicht ein Neustart.</param>
/// <param name="State">"idle", "downloading", "extracting", "verifying", "installing", "installed", "cancelled", "failed".</param>
public sealed record SimConnectStatus(
    bool Found,
    string? Source,
    string? SdkRoot,
    string? LoadError,
    bool FoundNow,
    string? FoundNowRoot,
    string State,
    double? Progress,
    string? Error,
    string SdkVersion,
    string DefaultSdkRoot,
    string? ConfiguredPath,
    bool CanInstall);

/// <summary>
/// Bietet an, das MSFS-2024-SDK (Core) zu installieren, wenn SimConnect fehlt: lädt das ZIP direkt von Microsoft, entpackt es,
/// startet den Installer erst nach der Prüfung: von der eingebauten Adresse die eingetragene Prüfsumme des ZIP, von einer eigenen
/// Adresse eine gültige Signatur von Microsoft oder dem MSFS-Team,
/// und zeigt dabei Microsofts Lizenz und die Abfrage der Adminrechte. Danach sucht Duopilo SimConnect erneut; gefunden reicht ein
/// Neustart. Duopilo selbst gibt so nichts von Microsoft weiter. Nur in der Tray-Instanz.
/// </summary>
public sealed class SdkInstaller(IOptions<DuopiloOptions> options, ILogger<SdkInstaller> log, HttpMessageHandler? downloadHandler = null)
{
    /// <summary>Eingetragene SDK-Version (Core-Installer von Microsoft).</summary>
    public const string Version = "1.8.1";

    public const string DefaultUrl = "https://sdk.flightsimulator.com/msfs2024/files/installers/" + Version + "/MSFS2024_SDK_Core_Installer_" + Version + ".zip";

    /// <summary>
    /// SHA-256 des ZIP von <see cref="DefaultUrl"/> (1.8.1, 652.783.454 Bytes). Damit wird der Download geprüft; passt er, ist es
    /// genau Microsofts Paket. Die Prüfsumme deckt auch die externen CAB-Dateien neben der MSI ab, die deren Signatur nicht
    /// einschließt. Bei einer neuen SDK-Version mit <see cref="Version"/> und <see cref="DefaultUrl"/> zusammen ändern.
    /// </summary>
    public const string DefaultSha256 = "7e7cd4774f3cb47e2942960e17b19b0a553499b2bab23da4851127a2363b8750";

    /// <summary>Kleiner ist das ZIP sicher nicht (Schutz gegen abgebrochene Downloads und Fehlerseiten).</summary>
    public const long MinBytes = 50L * 1024 * 1024;

    private readonly DuopiloOptions _options = options.Value;
    private readonly object _gate = new();
    private string _state = "idle";
    private double? _progress;
    private string? _error;
    private int _running;

    /// <summary>Adresse des ZIP (eingestellt oder die eingebaute).</summary>
    public string Url => string.IsNullOrWhiteSpace(_options.SimConnectSdkInstallerUrl) ? DefaultUrl : _options.SimConnectSdkInstallerUrl.Trim();

    public SimConnectStatus Status
    {
        get
        {
            var current = SimConnectLocator.Current;
            var now = SimConnectLocator.Probe(_options.SimConnectSdkPath);
            lock (_gate)
            {
                return new SimConnectStatus(
                    current is not null,
                    current?.Source,
                    current?.SdkRoot,
                    SimConnectLocator.LoadError,
                    now is not null,
                    now?.SdkRoot,
                    _state,
                    _progress,
                    _error,
                    Version,
                    SimConnectLocator.DefaultSdkRoot,
                    string.IsNullOrWhiteSpace(_options.SimConnectSdkPath) ? null : _options.SimConnectSdkPath,
                    OperatingSystem.IsWindows() && _state is not ("downloading" or "extracting" or "verifying" or "installing"));
            }
        }
    }

    /// <summary>Installation starten (kehrt sofort zurück). Rückgabe: Fehlertext oder null.</summary>
    public string? Start(CancellationToken stopping)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Loc.T("simconnect.sdk.windowsOnly");
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return null;
        }

        Set("downloading", 0, null);
        _ = Task.Run(() => RunAsync(stopping));
        return null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var directory = Path.Combine(_options.ResolveDataDirectory(), "downloads");
            Directory.CreateDirectory(directory);
            var url = Url;
            var zip = Path.Combine(directory, Path.GetFileName(new Uri(url).LocalPath));
            log.LogInformation(Loc.Log("log.simconnect.sdkDownloading"), url, zip);
            var expectedSha256 = ExpectedSha256(url);
            await ModelDownloader.EnsureAsync(zip, url, MinBytes, p => Set("downloading", Math.Round(Math.Clamp(p, 0, 1), 2), null), ct, expectedSha256, downloadHandler);

            Set("extracting", null, null);
            var target = Path.Combine(directory, Path.GetFileNameWithoutExtension(zip));
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, target), ct);
            }
            catch (InvalidDataException)
            {
                // Kaputtes ZIP (z. B. abgebrochener Download): beim nächsten Versuch neu laden
                File.Delete(zip);
                throw;
            }
            var msi = FindInstaller(target) ?? throw new InvalidOperationException(Loc.T("simconnect.sdk.noMsi", Path.GetFileName(zip)));

            Set("verifying", null, null);
            // Eingebaute Adresse: Die Prüfsumme hat EnsureAsync schon geprüft. Eigene Adresse: nur mit gültiger Signatur von Microsoft
            // oder dem MSFS-Team
            if (expectedSha256 is null && !Authenticode.IsMicrosoftSigned(msi))
            {
                throw new InvalidOperationException(Loc.T("simconnect.sdk.untrusted", Path.GetFileName(msi), Authenticode.Signer(msi) ?? "-"));
            }

            Set("installing", null, null);
            log.LogInformation(Loc.Log("log.simconnect.sdkInstalling"), msi, Authenticode.Signer(msi) ?? "-");
            var exit = await RunMsiAsync(msi, ct);
            var found = SimConnectLocator.Probe(_options.SimConnectSdkPath);
            var outcome = MsiOutcome(exit, found is not null);
            log.LogInformation(Loc.Log("log.simconnect.sdkInstallExit"), exit, outcome, found?.SdkRoot ?? "-");
            switch (outcome)
            {
                case "installed":
                    Set("installed", 1, null);
                    break;
                case "cancelled":
                    Set("cancelled", null, Loc.T("simconnect.sdk.cancelled"));
                    break;
                default:
                    Set("failed", null, found is null && exit is 0 or 3010 ? Loc.T("simconnect.sdk.notFoundAfter", SimConnectLocator.DefaultSdkRoot) : Loc.T("simconnect.sdk.msiFailed", exit));
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Beenden
        }
        catch (Exception ex)
        {
            Set("failed", null, ex.Message);
            log.LogWarning(Loc.Log("log.simconnect.sdkFailed"), Loc.ToEnglish(ex.Message) ?? ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Der Installer im entpackten ZIP (eine .msi, bevorzugt "…Core…").</summary>
    public static string? FindInstaller(string directory) =>
        Directory.EnumerateFiles(directory, "*.msi", SearchOption.AllDirectories)
            .OrderByDescending(f => Path.GetFileName(f).Contains("Core", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .FirstOrDefault();

    /// <summary>Erwartete Prüfsumme des ZIP: die eingetragene für <see cref="DefaultUrl"/>, für eine eigene Adresse keine.</summary>
    public static string? ExpectedSha256(string url) => url == DefaultUrl ? DefaultSha256 : null;

    /// <summary>
    /// Ergebnis des Installers: 0/3010 (Neustart empfohlen) = installiert, 1602 = abgebrochen, sonst Fehler. Ist SimConnect danach
    /// nicht zu finden (anderer Ordner gewählt), zählt es nicht als installiert.
    /// </summary>
    public static string MsiOutcome(int exitCode, bool found) => exitCode switch
    {
        0 or 3010 when found => "installed",
        1602 => "cancelled",
        _ => "failed",
    };

    private static async Task<int> RunMsiAsync(string msi, CancellationToken ct)
    {
        // Mit Oberfläche: Microsofts Lizenz bestätigt der Benutzer selbst, Windows fragt nach Adminrechten
        var start = new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\"") { UseShellExecute = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException(Loc.T("simconnect.sdk.msiFailed", -1));
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private void Set(string state, double? progress, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _progress = progress;
            _error = error;
        }
    }
}
