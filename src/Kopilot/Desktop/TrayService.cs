using System.Diagnostics;
using Kopilot.Aircraft;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Kopilot.Desktop;

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
    ILogger<TrayService> log) : IHostedService
{
    private Thread? _thread;
    private TrayContext? _context;
    private SynchronizationContext? _ui;

    public Task StartAsync(CancellationToken cancellationToken)
    {
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
                _context = new TrayContext(info, sim, profiles, alerts, monitoring, lifetime);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Tray-Symbol konnte nicht erstellt werden – Kopilot läuft ohne weiter.");
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
            Name = "Kopilot Tray",
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

        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _statusItem;
        private readonly WinForms.ToolStripMenuItem _autostartItem;
        private readonly WinForms.Timer _timer;
        private readonly Dictionary<string, Drawing.Icon> _icons = [];
        private string _state = "";

        public TrayContext(DesktopInfo info, SimConnectClient sim, ProfileStore profiles, AlertBus alerts, MonitoringService monitoring, IHostApplicationLifetime lifetime)
        {
            _info = info;
            _sim = sim;
            _profiles = profiles;
            _alerts = alerts;
            _monitoring = monitoring;
            _lifetime = lifetime;

            foreach (var name in new[] { "waiting", "ok", "caution", "warning" })
            {
                using var stream = typeof(TrayService).Assembly.GetManifestResourceStream($"Kopilot.Assets.tray-{name}.ico");
                if (stream is not null)
                {
                    _icons[name] = new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
                }
            }

            _statusItem = new WinForms.ToolStripMenuItem("Kopilot") { Enabled = false };
            var dashboard = new WinForms.ToolStripMenuItem("Dashboard öffnen", null, (_, _) => Open(_info.DashboardUrl));
            dashboard.Font = new Drawing.Font(dashboard.Font, Drawing.FontStyle.Bold);
            _autostartItem = new WinForms.ToolStripMenuItem("Mit Windows starten", null, (_, _) => ToggleAutostart()) { Checked = Autostart.IsEnabled() };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(dashboard);
            menu.Items.Add(new WinForms.ToolStripMenuItem("Profile neu laden", null, (_, _) => ReloadProfiles()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Logdatei öffnen", null, (_, _) => Open(_info.LogFile)) { Enabled = _info.LogFile is not null });
            menu.Items.Add(new WinForms.ToolStripMenuItem("Programmordner öffnen", null, (_, _) => Open(AppContext.BaseDirectory)));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Beenden", null, (_, _) => _lifetime.StopApplication()));
            menu.Opening += (_, _) =>
            {
                Refresh();
                _autostartItem.Checked = Autostart.IsEnabled();
            };

            _icon = new WinForms.NotifyIcon
            {
                ContextMenuStrip = menu,
                Text = "Kopilot",
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
                    "Kopilot läuft",
                    "Kopilot sitzt jetzt hier im Infobereich. Doppelklick öffnet das Dashboard.",
                    WinForms.ToolTipIcon.None);
            }
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
                text = "Kopilot – wartet auf MSFS";
            }
            else if (_sim.Status.Paused)
            {
                text = "Kopilot – Sim pausiert";
            }
            else
            {
                var profile = _sim.ActiveProfile?.Name ?? _sim.Str("title") ?? "Flugzeug unbekannt";
                text = $"Kopilot – {profile}";
                var top = active.FirstOrDefault(a => a.Callout is not null);
                if (top is not null)
                {
                    text += $"\n{top.Callout}";
                }
            }

            // NotifyIcon.Text erlaubt höchstens 127 Zeichen
            _icon.Text = text.Length > 127 ? text[..127] : text;
            _statusItem.Text = !connected ? "Wartet auf Microsoft Flight Simulator"
                : _sim.Status.Paused ? "Sim pausiert"
                : $"Verbunden: {_sim.ActiveProfile?.Name ?? "kein Profil"} (Phase {_monitoring.Phase})";
        }

        private void ReloadProfiles()
        {
            _profiles.Reload();
            _sim.RefreshProfile();
            var errors = _profiles.Errors.Count;
            _icon.ShowBalloonTip(
                3000,
                "Profile neu geladen",
                errors == 0 ? $"{_profiles.All.Count} Profile geladen." : $"{_profiles.All.Count} Profile geladen, {errors} mit Fehlern – Details im Dashboard.",
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
                _icon.ShowBalloonTip(4000, "Autostart", $"Konnte den Autostart nicht ändern: {ex.Message}", WinForms.ToolTipIcon.Warning);
            }

            _autostartItem.Checked = Autostart.IsEnabled();
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

/// <summary>Autostart über HKCU\Software\Microsoft\Windows\CurrentVersion\Run (nur für den angemeldeten Benutzer, keine Adminrechte).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kopilot";

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
}
