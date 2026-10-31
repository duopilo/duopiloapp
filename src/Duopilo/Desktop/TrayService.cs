using System.Diagnostics;
using Duopilo.Aircraft;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Sim;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Duopilo.Desktop;

/// <summary>Pfade und Adressen, die das Tray-Menü öffnet.</summary>
/// <param name="DashboardUrl">z. B. http://127.0.0.1:5155/</param>
/// <param name="LogFile">Logdatei, null wenn das Dateilog aus ist.</param>
/// <param name="StartedByAutostart">Mit Windows gestartet – dann keine Begrüßungs-Sprechblase.</param>
public sealed record DesktopInfo(string DashboardUrl, string? LogFile, bool StartedByAutostart);

/// <summary>
/// Symbol im Infobereich statt Konsolenfenster. Läuft auf einem eigenen STA-Thread mit WinForms-Nachrichtenschleife.
/// Die Farbe des Punkts zeigt den Zustand: grau = wartet auf MSFS, grün = verbunden, gelb = Caution, rot = Warning.
/// </summary>
public sealed class TrayService(
    DesktopInfo info,
    SimConnectClient sim,
    ProfileStore profiles,
    AlertBus alerts,
    MonitoringService monitoring,
    IHostApplicationLifetime lifetime,
    ILogger<TrayService> log,
    Duopilo.Speech.ISpeechOutput? speech = null,
    Duopilo.Updates.UpdateService? updates = null) : IHostedService
{
    private Thread? _thread;
    private TrayContext? _context;
    private SynchronizationContext? _ui;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Autostart von Kopilot (bis 0.9.x) übernehmen: „Mit Windows starten“ gilt jetzt für Duopilo
        try
        {
            if (Autostart.MigrateLegacy() is { } legacyCommand)
            {
                log.LogInformation(Loc.Log("log.tray.autostartMigrated"), legacyCommand);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.LogWarning(Loc.Log("log.tray.autostartMigrationFailed"), ex.Message);
        }

        // Nicht disposen: Läuft das Warten ab, ruft der Tray-Thread Set() womöglich erst danach auf.
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                WinForms.Application.EnableVisualStyles();
                WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
                var sync = new WinForms.WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(sync);
                _ui = sync;
                _context = new TrayContext(info, sim, profiles, alerts, monitoring, lifetime, speech, updates, sync);
            }
            catch (Exception ex)
            {
                log.LogError(ex, Loc.Log("log.tray.createFailed"));
                return;
            }
            finally
            {
                ready.Set();
            }

            WinForms.Application.Run(_context);
        })
        {
            IsBackground = true,
            Name = AppNames.Product + " Tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ui is not null && _context is not null)
        {
            _ui.Post(_ => _context.ExitThread(), null);
            _thread?.Join(TimeSpan.FromSeconds(3));
        }

        return Task.CompletedTask;
    }

    private sealed class TrayContext : WinForms.ApplicationContext
    {
        private readonly DesktopInfo _info;
        private readonly SimConnectClient _sim;
        private readonly ProfileStore _profiles;
        private readonly AlertBus _alerts;
        private readonly MonitoringService _monitoring;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly Duopilo.Speech.ISpeechOutput? _speech;
        private readonly Duopilo.Updates.UpdateService? _updates;
        private readonly SynchronizationContext _ui;

        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _speechItem;
        private readonly WinForms.ToolStripMenuItem _statusItem;
        private readonly WinForms.ToolStripMenuItem _autostartItem;
        private readonly WinForms.ToolStripMenuItem _dashboardItem;
        private readonly WinForms.ToolStripMenuItem _reloadItem;
        private readonly WinForms.ToolStripMenuItem _logItem;
        private readonly WinForms.ToolStripMenuItem _folderItem;
        private readonly WinForms.ToolStripMenuItem _exitItem;
        private readonly WinForms.ToolStripMenuItem _updateItem;
        private readonly WinForms.ToolStripSeparator _updateSeparator;
        private readonly WinForms.Timer _timer;
        private readonly Dictionary<string, Drawing.Icon> _icons = [];
        private string _state = "";
        private bool _closed;

        public TrayContext(DesktopInfo info, SimConnectClient sim, ProfileStore profiles, AlertBus alerts, MonitoringService monitoring, IHostApplicationLifetime lifetime, Duopilo.Speech.ISpeechOutput? speech, Duopilo.Updates.UpdateService? updates, SynchronizationContext ui)
        {
            _speech = speech;
            _updates = updates;
            _ui = ui;
            _info = info;
            _sim = sim;
            _profiles = profiles;
            _alerts = alerts;
            _monitoring = monitoring;
            _lifetime = lifetime;

            foreach (var name in new[] { "waiting", "ok", "caution", "warning" })
            {
                using var stream = typeof(TrayService).Assembly.GetManifestResourceStream($"Duopilo.Assets.tray-{name}.ico");
                if (stream is not null)
                {
                    _icons[name] = new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
                }
            }

            _statusItem = new WinForms.ToolStripMenuItem(AppNames.Product) { Enabled = false };
            _dashboardItem = new WinForms.ToolStripMenuItem("", null, (_, _) => Open(_info.DashboardUrl));
            _dashboardItem.Font = new Drawing.Font(_dashboardItem.Font, Drawing.FontStyle.Bold);
            _autostartItem = new WinForms.ToolStripMenuItem("", null, (_, _) => ToggleAutostart()) { Checked = Autostart.IsEnabled() };
            _speechItem = new WinForms.ToolStripMenuItem("", null, (_, _) => ToggleSpeech()) { Visible = speech is not null };
            _reloadItem = new WinForms.ToolStripMenuItem("", null, (_, _) => ReloadProfiles());
            _logItem = new WinForms.ToolStripMenuItem("", null, (_, _) => Open(_info.LogFile)) { Enabled = _info.LogFile is not null };
            _folderItem = new WinForms.ToolStripMenuItem("", null, (_, _) => Open(AppContext.BaseDirectory));
            _exitItem = new WinForms.ToolStripMenuItem("", null, (_, _) => _lifetime.StopApplication());
            _updateItem = new WinForms.ToolStripMenuItem("", null, (_, _) => InstallUpdate()) { Visible = false };
            _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Bold);
            _updateSeparator = new WinForms.ToolStripSeparator { Visible = false };
            SetMenuTexts();

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_updateItem);
            menu.Items.Add(_updateSeparator);
            menu.Items.Add(_dashboardItem);
            menu.Items.Add(_reloadItem);
            menu.Items.Add(_logItem);
            menu.Items.Add(_folderItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_speechItem);
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_exitItem);
            menu.Opening += (_, _) =>
            {
                Refresh();
                _autostartItem.Checked = Autostart.IsEnabled();
                RefreshSpeechItem();
                RefreshUpdateItem();
            };

            _icon = new WinForms.NotifyIcon
            {
                ContextMenuStrip = menu,
                Text = AppNames.Product,
                Icon = _icons.GetValueOrDefault("waiting"),
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => Open(_info.DashboardUrl);
            _icon.BalloonTipClicked += (_, _) => Open(_info.DashboardUrl);

            _timer = new WinForms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();

            if (!_info.StartedByAutostart)
            {
                _icon.ShowBalloonTip(
                    4000,
                    Loc.T("tray.balloon.runningTitle"),
                    Loc.T("tray.balloon.runningText"),
                    WinForms.ToolTipIcon.None);
            }

            // Language changed in the dashboard: update the menu texts (the event comes from any thread)
            Loc.LanguageChanged += OnLanguageChanged;

            // Neue Version gefunden: einmal je Version eine Sprechblase (das Ereignis kommt von einem beliebigen Thread)
            if (_updates is not null)
            {
                _updates.UpdateFound += OnUpdateFound;
            }
        }

        private void OnUpdateFound(Duopilo.Updates.UpdateStatus status)
        {
            try
            {
                _ui.Post(_ => ShowUpdate(status), null);
            }
            catch (InvalidOperationException)
            {
                // Tray-Thread beendet sich gerade
            }
        }

        private void ShowUpdate(Duopilo.Updates.UpdateStatus status)
        {
            if (_closed)
            {
                return;
            }

            RefreshUpdateItem();
            _icon.ShowBalloonTip(
                6000,
                Loc.T("tray.balloon.updateTitle", status.Latest ?? ""),
                status.Installable ? Loc.T("tray.balloon.updateInstallable") : Loc.T("tray.balloon.updateDownload"),
                WinForms.ToolTipIcon.Info);
        }

        /// <summary>Menüpunkt „Update auf … installieren“ nur, solange eins da ist.</summary>
        private void RefreshUpdateItem()
        {
            var status = _updates?.Status;
            var visible = status is { Available: true };
            _updateItem.Visible = visible;
            _updateSeparator.Visible = visible;
            if (!visible)
            {
                return;
            }

            _updateItem.Text = status!.State switch
            {
                "downloading" => Loc.T("tray.menu.updateDownloading", (int)Math.Round((status.Progress ?? 0) * 100)),
                "installing" => Loc.T("tray.menu.updateInstalling"),
                _ when status.Installable => Loc.T("tray.menu.updateInstall", status.Latest ?? ""),
                _ => Loc.T("tray.menu.updateOpen", status.Latest ?? ""),
            };
            _updateItem.Enabled = status.State is not ("downloading" or "installing");
        }

        private void InstallUpdate()
        {
            if (_updates?.Status is not { Available: true } status)
            {
                return;
            }

            if (!status.Installable)
            {
                // Nicht per Installer installiert (ZIP): die Seite des Releases öffnen
                Open(status.PageUrl);
                return;
            }

            if (_updates.Install() is { } error)
            {
                _icon.ShowBalloonTip(5000, Loc.T("tray.balloon.updateTitle", status.Latest ?? ""), error, WinForms.ToolTipIcon.Warning);
                return;
            }

            _icon.ShowBalloonTip(5000, Loc.T("tray.balloon.updateTitle", status.Latest ?? ""), Loc.T("tray.balloon.updateStarted"), WinForms.ToolTipIcon.Info);
            RefreshUpdateItem();
        }

        private void OnLanguageChanged(string language)
        {
            try
            {
                _ui.Post(_ => ApplyLanguage(), null);
            }
            catch (InvalidOperationException)
            {
                // Tray thread is shutting down (also ObjectDisposedException) – nothing left to update
            }
        }

        private void ApplyLanguage()
        {
            if (_closed)
            {
                return;
            }

            SetMenuTexts();
            RefreshSpeechItem();
            RefreshUpdateItem();
            Refresh();
        }

        /// <summary>Menu texts in the selected language (status and speech state follow in Refresh and RefreshSpeechItem when the menu opens).</summary>
        private void SetMenuTexts()
        {
            _dashboardItem.Text = Loc.T("tray.menu.openDashboard");
            _reloadItem.Text = Loc.T("tray.menu.reloadProfiles");
            _logItem.Text = Loc.T("tray.menu.openLog");
            _folderItem.Text = Loc.T("tray.menu.openFolder");
            _autostartItem.Text = Loc.T("tray.menu.autostart");
            _exitItem.Text = Loc.T("tray.menu.exit");
            _speechItem.Text = Loc.T("tray.menu.speech");
        }

        private void Refresh()
        {
            var connected = _sim.Connection.IsConnected;
            var active = _alerts.Active();
            var state = !connected ? "waiting"
                : active.Any(a => a.Severity == AlertSeverity.Warning && a.Kind == AlertKind.Raise) ? "warning"
                : active.Any(a => a.Severity == AlertSeverity.Caution && a.Kind == AlertKind.Raise) ? "caution"
                : "ok";

            if (state != _state && _icons.TryGetValue(state, out var icon))
            {
                _icon.Icon = icon;
                _state = state;
            }

            string text;
            if (!connected)
            {
                text = Loc.T("tray.tooltip.waiting");
            }
            else if (_sim.Status.Paused)
            {
                text = Loc.T("tray.tooltip.paused");
            }
            else
            {
                var profile = _sim.ActiveProfile?.Name ?? _sim.Str("title") ?? Loc.T("tray.tooltip.unknownAircraft");
                text = $"{AppNames.Product} – {profile}";
                var top = active.FirstOrDefault(a => a.Callout is not null);
                if (top is not null)
                {
                    text += $"\n{top.Callout}";
                }
            }

            // NotifyIcon.Text erlaubt höchstens 127 Zeichen
            _icon.Text = text.Length > 127 ? text[..127] : text;
            _statusItem.Text = !connected ? Loc.T("tray.status.waiting")
                : _sim.Status.Paused ? Loc.T("tray.status.paused")
                : Loc.T("tray.status.connected", _sim.ActiveProfile?.Name ?? Loc.T("tray.status.noProfile"), _monitoring.Phase);
        }

        private void ReloadProfiles()
        {
            _profiles.Reload();
            _sim.RefreshProfile();
            var errors = _profiles.Errors.Count;
            _icon.ShowBalloonTip(
                3000,
                Loc.T("tray.balloon.profilesTitle"),
                errors == 0 ? Loc.T("tray.balloon.profilesLoaded", _profiles.All.Count) : Loc.T("tray.balloon.profilesWithErrors", _profiles.All.Count, errors),
                errors == 0 ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);
        }

        private void ToggleAutostart()
        {
            try
            {
                Autostart.Set(!Autostart.IsEnabled());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _icon.ShowBalloonTip(4000, Loc.T("tray.balloon.autostartTitle"), Loc.T("tray.balloon.autostartFailed", ex.Message), WinForms.ToolTipIcon.Warning);
            }

            _autostartItem.Checked = Autostart.IsEnabled();
        }

        private void RefreshSpeechItem()
        {
            if (_speech is null)
            {
                return;
            }

            var st = _speech.Status;
            _speechItem.Checked = st.Settings.Enabled;
            _speechItem.Text = st.Kokoro?.State switch
            {
                Duopilo.Speech.KokoroState.Downloading when st.ActiveEngine is null => Loc.T("tray.menu.speechDownloading", st.Kokoro.Progress ?? 0),
                Duopilo.Speech.KokoroState.Loading when st.ActiveEngine is null => Loc.T("tray.menu.speechStarting"),
                _ when !st.Available => Loc.T("tray.menu.speechNoVoice"),
                _ => Loc.T("tray.menu.speech"),
            };
        }

        private async void ToggleSpeech()
        {
            if (_speech is null)
            {
                return;
            }

            try
            {
                var settings = _speech.Status.Settings;
                await _speech.UpdateAsync(settings with { Enabled = !settings.Enabled }, CancellationToken.None);
                RefreshSpeechItem();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _icon.ShowBalloonTip(4000, Loc.T("tray.balloon.speechTitle"), Loc.T("tray.balloon.speechFailed", ex.Message), WinForms.ToolTipIcon.Warning);
            }
        }

        private static void Open(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // z. B. keine Zuordnung für .log – dann eben nicht
            }
        }

        protected override void ExitThreadCore()
        {
            _closed = true;
            Loc.LanguageChanged -= OnLanguageChanged;
            if (_updates is not null)
            {
                _updates.UpdateFound -= OnUpdateFound;
            }

            _timer.Stop();
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            base.ExitThreadCore();
        }
    }
}

/// <summary>
/// Autostart über HKCU\Software\Microsoft\Windows\CurrentVersion\Run (nur für den angemeldeten Benutzer, keine Adminrechte).
/// Der Wert heißt wie das Produkt ("Duopilo"; muss zu AutostartName in installer/setup.iss passen). Bis 0.9.x hieß er "Kopilot".
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppNames.Product;
    private const string LegacyValueName = AppNames.Legacy.AutostartValue;

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command
            && Environment.ProcessPath is { } exe
            && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, $"\"{exe}\" --autostart");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Zeigt der alte Eintrag "Kopilot" auf eine Kopilot.exe, war „Mit Windows starten“ für die alte Version an: Er wird
    /// entfernt und gilt jetzt für diese Programmdatei. Rückgabe: der alte Befehl, wenn übernommen, sonst null.
    /// </summary>
    public static string? MigrateLegacy()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is not string command || !IsLegacyCommand(command))
        {
            return null;
        }

        if (key.GetValue(ValueName) is null)
        {
            key.SetValue(ValueName, $"\"{exe}\" --autostart");
        }

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        return command;
    }

    /// <summary>Startet der Befehl eine Kopilot.exe? (z. B. "C:\Tools\Kopilot\Kopilot.exe" --autostart)</summary>
    public static bool IsLegacyCommand(string command)
    {
        var text = command.Trim();
        var end = text.StartsWith('"') ? text.IndexOf('"', 1) : -1;
        var path = end > 0 ? text[1..end] : text.Split(' ', 2)[0];
        var name = path.Replace('\\', '/').Split('/')[^1];
        return string.Equals(name, AppNames.Legacy.Exe, StringComparison.OrdinalIgnoreCase);
    }
}
