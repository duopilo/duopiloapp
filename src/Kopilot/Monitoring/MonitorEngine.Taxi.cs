using Kopilot.Localization;

namespace Kopilot.Monitoring;

/// <summary>
/// Taxi-Speed-Warnung: am Boden schneller als erlaubt gerollt (Standard 25 kt Groundspeed).
/// Startlauf und Landerollen zählen nicht – die Warnung ist erst "scharf", wenn wirklich langsam gerollt wird,
/// und schweigt, solange kräftig beschleunigt wird (Startschub).
/// </summary>
public sealed partial class MonitorEngine
{
    /// <summary>Darüber ist es sicher ein Start- oder Landerollen.</summary>
    private const double RollSpeedKt = 45;

    /// <summary>Stärkere Beschleunigung (kt/s, geglättet) gilt als Startschub.</summary>
    private const double TakeoffAccelerationKtPerSec = 1.5;

    /// <summary>So lange muss die Groundspeed über der Grenze liegen.</summary>
    private static readonly TimeSpan TaxiSpeedOnDelay = TimeSpan.FromSeconds(2);

    private bool _taxiArmed;
    private DateTimeOffset? _taxiFastSince;
    private DateTimeOffset? _taxiLastWarningUtc;
    private double? _taxiAccelKtPerSec;
    private double? _taxiPrevGs;
    private DateTimeOffset _taxiPrevTime;

    private void CheckTaxiSpeed(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var limit = _settings.TaxiSpeedKt;
        if (s.OnGround != true || s.GroundSpeedKt is not { } gs || !(limit > 0))
        {
            _taxiArmed = false;
            _taxiFastSince = null;
            _taxiAccelKtPerSec = null;
            _taxiPrevGs = null;
            return;
        }

        if (_taxiPrevGs is { } previous)
        {
            var dt = (now - _taxiPrevTime).TotalSeconds;
            if (dt is > 0.05 and < 2)
            {
                var rate = (gs - previous) / dt;
                _taxiAccelKtPerSec = _taxiAccelKtPerSec is { } a ? (a * 0.7) + (rate * 0.3) : rate;
            }
        }

        _taxiPrevGs = gs;
        _taxiPrevTime = now;

        // Start- oder Landerollen: aus, bis wieder langsam gerollt wird
        if (gs > RollSpeedKt || Phase is "takeoff_roll" or "rollout" || _taxiAccelKtPerSec > TakeoffAccelerationKtPerSec)
        {
            _taxiArmed = false;
            _taxiFastSince = null;
            return;
        }

        if (gs < limit - 5)
        {
            _taxiArmed = true;
            _taxiFastSince = null;
            return;
        }

        if (!_taxiArmed || gs <= limit)
        {
            _taxiFastSince = null;
            return;
        }

        _taxiFastSince ??= now;
        if (now - _taxiFastSince < TaxiSpeedOnDelay)
        {
            return;
        }

        if (_taxiLastWarningUtc is { } last && (now - last).TotalSeconds < Math.Max(5, _settings.TaxiSpeedCooldownSec))
        {
            return;
        }

        _taxiLastWarningUtc = now;
        if (!suppressed && IsEnabled(Rules.TaxiSpeed))
        {
            events.Add(Event(now, Rules.TaxiSpeed, AlertSeverity.Caution, AlertKind.Event, "Taxi speed", Loc.T("monitor.alert.taxiSpeed", Kt(gs), Kt(limit))));
        }
    }
}
