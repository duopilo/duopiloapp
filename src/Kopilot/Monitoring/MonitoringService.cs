using Kopilot.FlightPlan;
using Microsoft.Extensions.Options;

namespace Kopilot.Monitoring;

/// <summary>Wertet die Regeln im Takt aus (Standard 200 ms) und legt Meldungen im <see cref="AlertBus"/> ab.</summary>
public sealed class MonitoringService : BackgroundService
{
    private readonly MonitoringOptions _options;
    private readonly SnapshotFactory _factory;
    private readonly AlertBus _bus;
    private readonly SimBriefService _simBrief;
    private readonly FmaLog _fmaLog;
    private readonly ILogger<MonitoringService> _log;
    private readonly MonitorEngine _engine;

    private DateTimeOffset _lastErrorLogUtc;
    private DateTimeOffset _lastSimBriefAttemptUtc;

    public MonitoringService(IOptions<KopilotOptions> options, SnapshotFactory factory, AlertBus bus, SimBriefService simBrief, FmaLog fmaLog, ILogger<MonitoringService> log)
    {
        _fmaLog = fmaLog;
        _options = options.Value.Monitoring;
        _factory = factory;
        _bus = bus;
        _simBrief = simBrief;
        _log = log;
        _engine = new MonitorEngine(new MonitorSettings
        {
            DisabledRules = new HashSet<string>(_options.DisabledRules, StringComparer.OrdinalIgnoreCase),
        });
    }

    public bool Enabled => _options.Enabled;

    public int IntervalMs => Math.Clamp(_options.IntervalMs, 50, 2000);

    public string Phase => _engine.Phase;

    public DateTimeOffset SuppressedUntilUtc => _engine.SuppressedUntilUtc;

    public IReadOnlyList<string> EnabledRules => _engine.EnabledRules.ToList();

    public IReadOnlyList<string> DisabledRules => Rules.All.Where(r => !_engine.IsEnabled(r)).ToList();

    public DateTimeOffset? LastEvaluationUtc { get; private set; }

    public FlightSnapshot? LastSnapshot { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Monitoring ist ausgeschaltet (Kopilot:Monitoring:Enabled).");
            return;
        }

        _log.LogInformation("Monitoring aktiv ({Interval} ms, {Rules} Regeln).", IntervalMs, EnabledRules.Count);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Tick(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Beenden
        }
    }

    private void Tick(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var snapshot = _factory.Build(now);
            var events = _engine.Evaluate(snapshot);
            if (events.Count > 0)
            {
                foreach (var e in _bus.Publish(events))
                {
                    if (e.Callout is not null)
                    {
                        _log.LogInformation("Callout #{Id} {Rule}: {Callout} – {Message}", e.Id, e.Rule, e.Callout, e.Message);
                    }
                    else
                    {
                        _log.LogDebug("Monitoring #{Id} {Rule} {Kind}: {Message}", e.Id, e.Rule, e.Kind, e.Message);
                    }
                }
            }

            if (_fmaLog.Update(snapshot) is { } fma)
            {
                _log.LogInformation("FMA #{Id}: {Changes} (IAS {Ias:0} kt, RA {Ra:0} ft)", fma.Id, string.Join("; ", fma.Changes), fma.IasKt, fma.RadioAltFt);
            }

            LastSnapshot = snapshot;
            LastEvaluationUtc = now;

            // Für L/C/R der zugewiesenen Bahn: SimBrief-Plan im Hintergrund laden, sobald ein Flug läuft
            if (snapshot.Connected && snapshot.FlightRunning != false && _simBrief.IsConfigured && _simBrief.Current is null
                && now - _lastSimBriefAttemptUtc > TimeSpan.FromMinutes(5))
            {
                _lastSimBriefAttemptUtc = now;
                _ = _simBrief.GetAsync(refresh: false, ct);
            }
        }
        catch (Exception ex)
        {
            if (now - _lastErrorLogUtc > TimeSpan.FromMinutes(1))
            {
                _lastErrorLogUtc = now;
                _log.LogError(ex, "Fehler im Monitoring");
            }
        }
    }
}
