using System.Globalization;
using Kopilot.Aircraft;
using Kopilot.Monitoring;

namespace Kopilot.Input;

/// <summary>Was nach Fahrwerk oder Klappen nachgelesen wird.</summary>
/// <param name="Item"><see cref="ConfigCalloutClaims.Gear"/> oder <see cref="ConfigCalloutClaims.Flaps"/>.</param>
/// <param name="Target">"down", "up" bzw. die Klappenstellung ("0", "2", "FULL").</param>
/// <param name="SuccessCallout">z. B. "Gear down, three green" oder "Flaps two".</param>
/// <param name="FailCallout">z. B. "Gear disagree".</param>
public sealed record VerificationJob(
    string Item,
    string Target,
    IReadOnlyList<VerificationVariable> Variables,
    TimeSpan Timeout,
    string SuccessCallout,
    string FailCallout,
    string Rule)
{
    public GearVerification? Gear { get; init; }

    public FlapsVerification? Flaps { get; init; }

    /// <summary>Kurzbeschreibung für die Antwort an Claude.</summary>
    public string Describe() =>
        $"{(Item == ConfigCalloutClaims.Gear ? "Fahrwerk" : "Klappen")} wird nachgelesen – Callout \"{SuccessCallout}\" bei erreichter Stellung, " +
        $"sonst nach {Timeout.TotalSeconds:0} s \"{FailCallout}\".";
}

/// <summary>Ergebnis des Nachlesens. <see cref="Readable"/> false = Variablen nicht lesbar, nichts angesagt.</summary>
public sealed record VerificationOutcome(bool Ok, bool Readable, string? Callout, string Message, bool Superseded = false);

/// <summary>
/// Nach gear.down/gear.up und flaps: wartet, bis die Stellung wirklich erreicht ist, und sagt sie an
/// ("Gear down, three green", "Gear up", "Flaps two"). Klappt das nicht in der Zeit (Fahrwerk 20 s), kommt "Gear disagree"
/// bzw. "Flaps disagree". Pausen im Sim verlängern die Zeit. Ein neuer Befehl für dasselbe Teil ersetzt das laufende Nachlesen.
/// Die Callouts gehen über den <see cref="AlertBus"/> (Regeln PM_GEAR/PM_FLAPS) an die Sprachausgabe und get_alerts.
/// </summary>
public sealed class ConfigVerifier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IInputBackend _backend;
    private readonly AlertBus? _bus;
    private readonly ConfigCalloutClaims? _claims;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigVerifier> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    public ConfigVerifier(IInputBackend backend, ILogger<ConfigVerifier> log, AlertBus? bus = null, ConfigCalloutClaims? claims = null, TimeProvider? time = null)
    {
        _backend = backend;
        _log = log;
        _bus = bus;
        _claims = claims;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Nachlesen für diese Aktion, oder null (keine Fahrwerks- oder Klappenaktion).</summary>
    public static VerificationJob? JobFor(ActionPlan plan, AircraftProfile? profile)
    {
        static string Name(ResolvedStep s)
        {
            var n = s.Event?.Trim() ?? "";
            return n.StartsWith("K:", StringComparison.OrdinalIgnoreCase) ? n[2..].Trim() : n;
        }

        var events = plan.Steps.Where(s => s.Event is not null).Select(Name).ToList();
        if (events.Any(e => e.Equals("GEAR_DOWN", StringComparison.OrdinalIgnoreCase) || e.Equals("GEAR_UP", StringComparison.OrdinalIgnoreCase)))
        {
            var down = events.Any(e => e.Equals("GEAR_DOWN", StringComparison.OrdinalIgnoreCase));
            var gear = profile?.Verification?.Gear is { Legs.Count: > 0 } g ? g : GearVerification.Default();
            var legs = gear.Legs.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name)).ToList();
            return new VerificationJob(
                ConfigCalloutClaims.Gear,
                down ? "down" : "up",
                legs,
                TimeSpan.FromSeconds(Math.Clamp(gear.TimeoutSec, 5, 120)),
                down ? $"Gear down, {CountWord(legs.Count)} green" : "Gear up",
                "Gear disagree",
                Rules.PmGear) { Gear = gear };
        }

        if (events.Any(e => e.StartsWith("FLAPS_", StringComparison.OrdinalIgnoreCase)) && plan.Option is { } option)
        {
            var flaps = profile?.Verification?.Flaps is { Surfaces.Count: > 0 } f ? f : FlapsVerification.Default();
            var spoken = plan.Action.Parameter?.Spoken?.FirstOrDefault(kv => kv.Key.Equals(option, StringComparison.OrdinalIgnoreCase)).Value
                         ?? option.ToLowerInvariant();
            return new VerificationJob(
                ConfigCalloutClaims.Flaps,
                option,
                flaps.Surfaces.Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Name)).ToList(),
                TimeSpan.FromSeconds(Math.Clamp(flaps.TimeoutSec, 5, 180)),
                $"Flaps {spoken}",
                "Flaps disagree",
                Rules.PmFlaps) { Flaps = flaps };
        }

        return null;
    }

    /// <summary>Vor dem Senden: Monitoring sagt den Hebelwechsel nicht zusätzlich an.</summary>
    public void Claim(VerificationJob job) => _claims?.Claim(job.Item, _time.GetUtcNow() + job.Timeout + TimeSpan.FromSeconds(30));

    /// <summary>Ohne Nachlesen (Aktion nicht ausgeführt): Monitoring wieder normal.</summary>
    public void Unclaim(VerificationJob job) => _claims?.Claim(job.Item, DateTimeOffset.MinValue);

    /// <summary>Nachlesen starten; ein laufendes für dasselbe Teil wird ersetzt (ohne Callout).</summary>
    public async Task<VerificationOutcome> RunAsync(VerificationJob job, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
        {
            if (_running.TryGetValue(job.Item, out var previous))
            {
                previous.Cancel();
            }

            _running[job.Item] = cts;
        }

        try
        {
            var outcome = job.Gear is not null ? await GearAsync(job, cts.Token) : await FlapsAsync(job, cts.Token);
            Publish(job, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new VerificationOutcome(false, true, null, "Durch einen neuen Befehl ersetzt.", Superseded: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nachlesen {Item} fehlgeschlagen", job.Item);
            var outcome = new VerificationOutcome(false, false, null, $"Nachlesen fehlgeschlagen: {ex.Message}");
            Publish(job, outcome);
            return outcome;
        }
        finally
        {
            lock (_lock)
            {
                if (_running.TryGetValue(job.Item, out var current) && ReferenceEquals(current, cts))
                {
                    _running.Remove(job.Item);
                }
            }
        }
    }

    private async Task<VerificationOutcome> GearAsync(VerificationJob job, CancellationToken ct)
    {
        var gear = job.Gear!;
        var down = job.Target == "down";
        var deadline = _time.GetUtcNow() + job.Timeout;
        var last = _time.GetUtcNow();
        double?[] values = new double?[job.Variables.Count];
        var everRead = false;
        while (true)
        {
            for (var i = 0; i < job.Variables.Count; i++)
            {
                values[i] = await _backend.ReadAsync(job.Variables[i].Name.Trim(), job.Variables[i].Unit, ct);
                everRead |= values[i] is not null;
            }

            var now = _time.GetUtcNow();
            deadline = Paused(deadline, ref last, now);
            if (values.Length > 0 && values.All(v => v is { } x && (down ? x >= gear.DownAt : x <= gear.UpAt)))
            {
                var callout = job.SuccessCallout;
                return new VerificationOutcome(true, true, callout, down
                    ? $"Fahrwerk unten und verriegelt ({values.Length}/{values.Length})."
                    : "Fahrwerk eingefahren.");
            }

            if (now >= deadline)
            {
                if (!everRead)
                {
                    return new VerificationOutcome(false, false, null, "Fahrwerksstellung nicht lesbar – keine Ansage. Profil: verification.gear prüfen.");
                }

                return new VerificationOutcome(false, true, job.FailCallout,
                    $"Fahrwerk nach {job.Timeout.TotalSeconds:0} s nicht {(down ? "unten verriegelt" : "eingefahren")}: {Values(job, values)}.");
            }

            await Task.Delay(PollInterval, _time, ct);
        }
    }

    private async Task<VerificationOutcome> FlapsAsync(VerificationJob job, CancellationToken ct)
    {
        var flaps = job.Flaps!;
        var retract = job.Target == "0";
        var settle = TimeSpan.FromSeconds(Math.Clamp(flaps.SettleSec, 0.5, 10));
        var tolerance = Math.Max(0.01, flaps.Tolerance);
        var deadline = _time.GetUtcNow() + job.Timeout;
        var last = _time.GetUtcNow();
        var count = job.Variables.Count;
        var start = new double?[count];
        var prev = new double?[count];
        var current = new double?[count];
        var lastChange = _time.GetUtcNow();
        var moved = false;
        var everRead = false;

        while (true)
        {
            for (var i = 0; i < count; i++)
            {
                current[i] = await _backend.ReadAsync(job.Variables[i].Name.Trim(), job.Variables[i].Unit, ct);
            }

            var now = _time.GetUtcNow();
            deadline = Paused(deadline, ref last, now);
            for (var i = 0; i < count; i++)
            {
                if (current[i] is not { } v)
                {
                    continue;
                }

                everRead = true;
                start[i] ??= v;
                if (prev[i] is { } p && Math.Abs(v - p) > tolerance)
                {
                    lastChange = now;
                }

                if (Math.Abs(v - start[i]!.Value) > 1)
                {
                    moved = true;
                }

                prev[i] = v;
            }

            var readable = current.Where(v => v is not null).Select(v => v!.Value).ToList();
            var allRetracted = retract && readable.Count > 0 && readable.All(v => v <= 1);
            if (readable.Count > 0 && (moved || allRetracted) && now - lastChange >= settle)
            {
                return new VerificationOutcome(true, true, job.SuccessCallout, $"Klappen {job.Target} erreicht ({Values(job, current)}).");
            }

            if (now >= deadline)
            {
                if (!everRead)
                {
                    return new VerificationOutcome(false, false, null, "Klappenstellung nicht lesbar – keine Ansage. Profil: verification.flaps prüfen.");
                }

                return new VerificationOutcome(false, true, job.FailCallout,
                    $"Klappen {job.Target} nach {job.Timeout.TotalSeconds:0} s nicht erreicht ({(moved ? "noch in Bewegung" : "keine Bewegung")}: {Values(job, current)}).");
            }

            await Task.Delay(PollInterval, _time, ct);
        }
    }

    /// <summary>Sim pausiert: die Wartezeit läuft nicht weiter.</summary>
    private DateTimeOffset Paused(DateTimeOffset deadline, ref DateTimeOffset last, DateTimeOffset now)
    {
        var elapsed = now - last;
        last = now;
        try
        {
            return _backend.Snapshot().Paused && elapsed > TimeSpan.Zero ? deadline + elapsed : deadline;
        }
        catch (Exception)
        {
            return deadline;
        }
    }

    private void Publish(VerificationJob job, VerificationOutcome outcome)
    {
        var now = _time.GetUtcNow();
        _claims?.Release(job.Item, now);
        _log.LogInformation("Nachlesen {Item} {Target}: {Callout} – {Message}", job.Item, job.Target, outcome.Callout ?? "(keine Ansage)", outcome.Message);
        _bus?.Publish([new AlertEvent(0, now, job.Rule, outcome.Ok || !outcome.Readable ? AlertSeverity.Info : AlertSeverity.Caution, AlertKind.Event, outcome.Callout, outcome.Message)]);
    }

    private static string Values(VerificationJob job, IReadOnlyList<double?> values) =>
        string.Join(", ", job.Variables.Select((v, i) =>
            $"{v.Label ?? v.Name} {(values[i] is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) : "?")}"));

    private static string CountWord(int n) => n switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
