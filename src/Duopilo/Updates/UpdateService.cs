using System.Diagnostics;
using System.Net.Http.Headers;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Voice;
using Microsoft.Extensions.Options;

namespace Duopilo.Updates;

/// <summary>Zustand der Update-Prüfung für Dashboard und Tray.</summary>
/// <param name="State">
/// "selfBuilt" (mit update.cmd gebaut – keine Prüfung), "off" (ausgeschaltet), "idle", "checking", "upToDate", "available",
/// "downloading", "installing", "failed".
/// </param>
/// <param name="Installable">Installer vorhanden und Duopilo per Installer installiert (sonst nur der Link zum Release).</param>
public sealed record UpdateStatus(
    string State,
    string Current,
    string? Latest,
    string? LatestName,
    string? PageUrl,
    bool LatestIsPrerelease,
    bool Installable,
    DateTimeOffset? LastCheckUtc,
    string? Error,
    double? Progress,
    bool CheckEnabled,
    string Prereleases)
{
    public bool Available => State is "available" or "downloading" or "installing" || (State == "failed" && Latest is not null);
}

/// <summary>
/// Sieht beim Start (nach einer Minute) und dann alle <see cref="UpdateOptions.IntervalHours"/> in den GitHub-Releases nach einer
/// neueren Version. Installieren: Installer laden, gegen SHA256SUMS.txt prüfen, still installieren lassen (mit Neustart von
/// Duopilo) und diese Instanz beenden. Nur in der Tray-Instanz, nur für Stände der Release-Pipeline.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    /// <summary>Erste Prüfung nach dem Start – nicht, während Duopilo gerade alles lädt.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly UpdateOptions _options;
    private readonly string _dataDirectory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _log;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _downloadHandler;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private ReleaseInfo? _latest;
    private string _state;
    private string? _error;
    private double? _progress;
    private DateTimeOffset? _lastCheckUtc;
    private bool _checkEnabled;
    private string _prereleases;
    private int _installing;
    private CancellationToken _stopping;

    /// <param name="http">Eigener HttpClient für die Abfrage (Tests). null = normales Netz.</param>
    /// <param name="downloadHandler">Eigener Handler für den Download des Installers (Tests).</param>
    public UpdateService(
        IOptions<DuopiloOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<UpdateService> log,
        HttpClient? http = null,
        HttpMessageHandler? downloadHandler = null,
        SemVersion? current = null,
        bool? releaseBuild = null)
    {
        _options = options.Value.Updates;
        _dataDirectory = options.Value.ResolveDataDirectory();
        _lifetime = lifetime;
        _log = log;
        _http = http ?? SharedHttp;
        _downloadHandler = downloadHandler;
        Current = current ?? UpdateLogic.CurrentVersion(typeof(UpdateService).Assembly);
        ReleaseBuild = releaseBuild ?? UpdateLogic.IsReleaseBuild(typeof(UpdateService).Assembly);
        _checkEnabled = _options.Check;
        _prereleases = UpdateLogic.NormalizePrereleases(_options.Prereleases);
        _state = !ReleaseBuild ? "selfBuilt" : _checkEnabled ? "idle" : "off";
    }

    /// <summary>Version dieser Duopilo.exe.</summary>
    public SemVersion Current { get; }

    /// <summary>Von der Release-Pipeline gebaut (sonst: update.cmd).</summary>
    public bool ReleaseBuild { get; }

    /// <summary>Per Installer installiert (Uninstaller daneben) – nur dann installiert Duopilo ein Update selbst.</summary>
    public static bool InstalledBySetup => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Neue Version gefunden (Version wechselt) – für die Sprechblase im Tray. Kommt von einem beliebigen Thread.</summary>
    public event Action<UpdateStatus>? UpdateFound;

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                var latest = _latest;
                return new UpdateStatus(
                    _state,
                    Current.ToString(),
                    latest?.Version.ToString(),
                    latest?.Name,
                    latest?.PageUrl,
                    latest?.Version.IsPrerelease == true || latest?.Prerelease == true,
                    latest?.Setup is not null && latest.Checksums is not null && InstalledBySetup && OperatingSystem.IsWindows(),
                    _lastCheckUtc,
                    _error,
                    _progress,
                    _checkEnabled,
                    _prereleases);
            }
        }
    }

    /// <summary>Einstellungen aus dem Dashboard übernehmen (gilt sofort).</summary>
    public void Configure(bool check, string prereleases)
    {
        lock (_gate)
        {
            _checkEnabled = check;
            _prereleases = UpdateLogic.NormalizePrereleases(prereleases);
            if (ReleaseBuild && _state is "off" or "idle" or "upToDate" or "available" or "failed")
            {
                _state = check ? "idle" : "off";
                _latest = check ? _latest : null;
            }
        }

        if (check)
        {
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        if (!ReleaseBuild)
        {
            _log.LogInformation(Loc.Log("log.update.selfBuilt"), Current);
            return;
        }

        try
        {
            await _wake.WaitAsync(StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_checkEnabled)
                {
                    await CheckAsync(stoppingToken);
                }

                var interval = TimeSpan.FromHours(Math.Max(1, double.IsFinite(_options.IntervalHours) ? _options.IntervalHours : 6));
                await _wake.WaitAsync(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }
    }

    /// <summary>Jetzt nachsehen (Knopf im Dashboard). Rückgabe: Zustand danach.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        if (!ReleaseBuild)
        {
            return Status;
        }

        await _checkLock.WaitAsync(ct);
        try
        {
            lock (_gate)
            {
                if (_state is "downloading" or "installing")
                {
                    return Status;
                }

                _state = "checking";
                _error = null;
            }

            var url = $"https://api.github.com/repos/{_options.Repository.Trim().Trim('/')}/releases?per_page=30";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppNames.Product, Current.ToString()));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(Loc.T("update.http.status", (int)response.StatusCode, _options.Repository));
            }

            var releases = UpdateLogic.ParseReleases(body);
            ReleaseInfo? newest;
            ReleaseInfo? previous;
            lock (_gate)
            {
                newest = UpdateLogic.Newest(releases, Current, UpdateLogic.WantsPrereleases(_prereleases, Current));
                previous = _latest;
                _latest = newest;
                _state = newest is null ? "upToDate" : "available";
                _lastCheckUtc = DateTimeOffset.UtcNow;
            }

            if (newest is null)
            {
                _log.LogInformation(Loc.Log("log.update.upToDate"), Current, releases.Count);
            }
            else if (previous?.Version.CompareTo(newest.Version) != 0)
            {
                _log.LogInformation(Loc.Log("log.update.available"), newest.Version, Current, newest.PageUrl);
                try
                {
                    UpdateFound?.Invoke(Status);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("{Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _state = _latest is null ? "idle" : "available";
            }

            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            lock (_gate)
            {
                _state = "failed";
                _error = ex is TaskCanceledException ? Loc.T("update.http.timeout") : ex.Message;
                _lastCheckUtc = DateTimeOffset.UtcNow;
            }

            _log.LogWarning(Loc.Log("log.update.checkFailed"), Loc.ToEnglish(_error) ?? _error);
        }
        finally
        {
            _checkLock.Release();
        }

        return Status;
    }

    /// <summary>
    /// Update installieren: Installer laden und prüfen, dann still installieren lassen und Duopilo beenden – der Installer startet
    /// Duopilo danach neu. Kehrt nach dem Start des Downloads zurück. Rückgabe: Fehlertext oder null.
    /// </summary>
    public string? Install()
    {
        var status = Status;
        ReleaseInfo? release;
        lock (_gate)
        {
            release = _latest;
        }

        if (release is null || !status.Available)
        {
            return Loc.T("update.install.nothing");
        }

        if (!status.Installable)
        {
            return InstalledBySetup ? Loc.T("update.install.noInstaller") : Loc.T("update.install.notInstalled");
        }

        if (Interlocked.Exchange(ref _installing, 1) == 1)
        {
            return null;
        }

        lock (_gate)
        {
            _state = "downloading";
            _progress = 0;
            _error = null;
        }

        _ = Task.Run(() => InstallAsync(release, _stopping));
        return null;
    }

    private async Task InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        try
        {
            var setup = release.Setup!;
            var sums = await DownloadTextAsync(release.Checksums!.Url, ct);
            var sha256 = UpdateLogic.ChecksumFor(sums, setup.Name) ?? throw new InvalidOperationException(Loc.T("update.install.noChecksum", setup.Name));

            var directory = Path.Combine(_dataDirectory, "updates");
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "*-Setup.exe").Where(f => !f.EndsWith(setup.Name, StringComparison.OrdinalIgnoreCase)))
            {
                TryDelete(old);
            }

            var path = Path.Combine(directory, setup.Name);
            _log.LogInformation(Loc.Log("log.update.downloading"), release.Version, setup.Url, setup.Size / (1024 * 1024));
            await ModelDownloader.EnsureAsync(
                path,
                setup.Url,
                Math.Max(1, setup.Size),
                p =>
                {
                    lock (_gate)
                    {
                        _progress = Math.Round(Math.Clamp(p, 0, 1), 2);
                    }
                },
                ct,
                sha256,
                _downloadHandler);

            lock (_gate)
            {
                _state = "installing";
                _progress = 1;
            }

            StartInstaller(path, release);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Beenden
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _installing, 0);
            lock (_gate)
            {
                _state = "failed";
                _error = ex.Message;
                _progress = null;
            }

            _log.LogWarning(Loc.Log("log.update.installFailed"), release.Version, Loc.ToEnglish(ex.Message) ?? ex.Message);
        }
    }

    private void StartInstaller(string setupPath, ReleaseInfo release)
    {
        var arguments = UpdateLogic.InstallerArguments(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? AppNames.Product + ".exe");
        var command = UpdateLogic.HelperCommand(Environment.ProcessId, setupPath, arguments, processName);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", command })
        {
            start.ArgumentList.Add(arg);
        }

        Process.Start(start)?.Dispose();
        _log.LogInformation(Loc.Log("log.update.installing"), release.Version, setupPath, arguments);

        // Kurz warten, damit die Antwort ans Dashboard noch rausgeht; der Helfer startet den Installer, sobald Duopilo beendet ist
        _ = Task.Delay(TimeSpan.FromMilliseconds(800)).ContinueWith(_ => _lifetime.StopApplication(), TaskScheduler.Default);
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppNames.Product, Current.ToString()));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nächstes Mal
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _checkLock.Dispose();
        _wake.Dispose();
    }
}
