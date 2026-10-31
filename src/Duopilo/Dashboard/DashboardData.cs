using Duopilo.Aircraft;
using Duopilo.Atc;
using Duopilo.Ecam;
using Duopilo.FlightPlan;
using Duopilo.Hosting;
using Duopilo.Input;
using Duopilo.Monitoring;
using Duopilo.Sim;
using Duopilo.Speech;
using Duopilo.Tools;

namespace Duopilo.Dashboard;

/// <summary>Großanzeige im Dashboard: eine Meldung und ob ihr Zustand noch besteht.</summary>
public sealed record DashboardHeadline(AlertEvent Alert, bool Active);

/// <summary>Alle Werte für das Browser-Dashboard in einer Antwort (wird alle 500 ms abgefragt).</summary>
public sealed class DashboardData(
    SimConnectClient sim,
    MonitoringService monitoring,
    AlertBus alerts,
    SimBriefService simBrief,
    EcamCatalog ecam,
    FmaLog fmaLog,
    LogBuffer logs,
    InputService? input = null,
    ISpeechOutput? speech = null,
    Duopilo.Voice.IVoiceStatusSource? voice = null,
    Duopilo.Updates.UpdateService? updates = null)
{
    /// <summary>Volle Version ("0.10.0-beta.4") für die Kopfzeile.</summary>
    private static readonly string AppVersion = Duopilo.Updates.UpdateLogic.CurrentVersion(typeof(DashboardData).Assembly).ToString();

    /// <summary>So lange bleibt eine einmalige Ansage groß stehen.</summary>
    public static readonly TimeSpan HeadlineWindow = TimeSpan.FromSeconds(60);

    /// <summary>Name des First Officers für die Kopfzeile (nur mit laufender Sprachschleife).</summary>
    private object? FirstOfficerView()
    {
        var status = voice?.Status;
        return status?.FirstOfficer is { } fo && status.State is "listening" or "muted" or "loading" or "inactive"
            ? new { name = fo.Name, gender = fo.Gender, voice = fo.Voice, smalltalk = status.Smalltalk, note = status.SmalltalkNote, pending = status.SmalltalkPending }
            : null;
    }

    public object Build(long sinceId, bool includeLog)
    {
        var connection = sim.Connection;
        var status = sim.Status;
        var now = DateTimeOffset.UtcNow;

        return new
        {
            version = AppVersion,
            update = updates?.Status is { Available: true } u
                ? new { version = u.Latest, state = u.State, installable = u.Installable, progress = u.Progress, pageUrl = u.PageUrl }
                : null,
            connected = connection.IsConnected,
            simulator = connection.Application,
            simVersion = connection.Version,
            flightLoaded = status.FlightRunning,
            paused = status.Paused,
            pauseKind = status.PauseKind,
            lastError = sim.LastError,
            speech = speech?.Status is { } st
                ? new
                {
                    available = st.Available,
                    enabled = st.Settings.Enabled,
                    speaking = st.Speaking,
                    error = st.Error,
                    engine = st.ActiveEngine,
                }
                : null,
            input = input is null ? null : InputView(input, now),
            firstOfficer = FirstOfficerView(),
            monitoring = new
            {
                enabled = monitoring.Enabled,
                phase = monitoring.Phase,
                quietForSec = monitoring.SuppressedUntilUtc > now ? Math.Round((monitoring.SuppressedUntilUtc - now).TotalSeconds) : 0,
            },
            alerts = new
            {
                lastId = alerts.LastId,
                active = alerts.Active(now).Select(AlertView.Full),
                headline = Headline(alerts, now) is { } headline ? new { alert = AlertView.Full(headline.Alert), active = headline.Active } : null,
                events = alerts.Since(sinceId < 0 ? Math.Max(0, alerts.LastId - 30) : sinceId, 30).Select(AlertView.Full),
            },
            sim = connection.IsConnected ? SimPart(status) : null,
            fmaLog = fmaLog.Last(12).Reverse().Select(e => new
            {
                time = e.TimeUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                changes = e.Changes,
                ias = Fmt.Int(e.IasKt),
                ra = e.RadioAltFt is < 2500 ? Fmt.Int(e.RadioAltFt) : null,
                alt = Fmt.Int(e.AltitudeFt),
            }),
            plan = Plan(),
            log = includeLog
                ? logs.Tail(150).Select(l => new
                {
                    time = l.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    level = FileLoggerProvider.Level(l.Level),
                    category = l.Category,
                    message = l.Message,
                })
                : null,
        };
    }

    /// <summary>
    /// Was groß angezeigt wird: der wichtigste aktive Zustand, sonst die letzte einmalige Ansage der letzten 60 s.
    /// Beendete Zustände zählen nicht mehr – sonst bliebe z. B. "Sim paused" nach dem Ende der Pause stehen.
    /// </summary>
    public static DashboardHeadline? Headline(AlertBus alerts, DateTimeOffset now)
    {
        if (alerts.Active(now).FirstOrDefault(e => e.Callout is not null) is { } top)
        {
            return new DashboardHeadline(top, true);
        }

        var recent = alerts.Since(0, 40)
            .Where(e => e.Kind == AlertKind.Event && e.Callout is not null && now - e.TimeUtc <= HeadlineWindow)
            .MaxBy(e => e.Id);
        return recent is null ? null : new DashboardHeadline(recent, false);
    }

    private object SimPart(SimStatus status)
    {
        var s = new AircraftSignals(sim);
        var profile = s.Profile;
        var ecamState = EcamReader.Read(s, ecam);
        var engineCount = Math.Clamp(Fmt.Int(sim.Num("engineCount")) ?? 2, 1, SimVarCatalog.MaxEngines);
        var runway = AtcReader.AssignedRunway(sim, simBrief.Current);

        return new
        {
            aircraft = new
            {
                title = sim.Str("title"),
                path = status.AircraftPath,
                profile = profile?.Name,
                profileFile = profile?.SourceFile,
                profileNotes = profile?.Notes,
            },
            dataAgeSec = Fmt.Round(sim.AgeSeconds("altIndicated"), 1),
            flight = new
            {
                onGround = sim.Bool("onGround"),
                ias = Fmt.Int(sim.Num("ias")),
                iasTarget = Fmt.Int(s.SpeedTargetKt),
                speedManaged = s.SpeedManaged,
                targetFromMach = s.SpeedManaged != true && s.SelectedSpeedIsMach,
                vls = Fmt.Int(s.VlsKt),
                tas = Fmt.Int(sim.Num("tas")),
                gs = Fmt.Int(sim.Num("gs")),
                alt = Fmt.Int(sim.Num("altIndicated")),
                altSel = Fmt.Int(s.SelectedAltitudeFt),
                fl = Fmt.Int(sim.Num("altPressure") / 100),
                vs = Fmt.Int(sim.Num("vs")),
                hdg = Fmt.Heading(sim.Num("hdgMag")),
                hdgSel = Fmt.Heading(s.SelectedHeadingDeg),
                trk = sim.Num("gs") is > 5 ? Fmt.Heading(sim.Num("trackMag")) : null,
                ra = Fmt.Int(sim.Num("altRadio")),
                agl = Fmt.Int(sim.Num("altAgl")),
                pitch = Fmt.Round(Fmt.Negate(sim.Num("pitch")), 1),
                bank = Fmt.Round(Fmt.Negate(sim.Num("bank")), 1),
            },
            fma = new
            {
                source = s.AutopilotSource,
                ap = s.ApEngaged,
                ap1 = s.Ap1,
                ap2 = s.Ap2,
                fd = s.FlightDirector,
                athr = s.AutothrustStatus,
                thrust = s.AutothrustMode,
                vertical = s.FmaVertical,
                verticalArmed = s.FmaVerticalArmed,
                lateral = s.FmaLateral,
                lateralArmed = s.FmaLateralArmed,
            },
            config = new
            {
                flaps = s.FlapsDisplay,
                gearPct = Fmt.Int(sim.Num("gearCenterPct")),
                gearHandleDown = sim.Bool("gearHandle"),
                spoilersArmed = s.SpoilersArmed,
                speedbrakePct = Fmt.Int(sim.Num("spoilersHandlePct")),
                parkingBrake = s.ParkingBrake,
                autobrake = s.Autobrake,
            },
            takeoff = s.Has(BindingKeys.V1) || s.Has(BindingKeys.FlexTemp)
                ? new { v1 = Fmt.Int(s.V1Kt), vr = Fmt.Int(s.VrKt), v2 = Fmt.Int(s.V2Kt), flex = Fmt.Int(s.FlexTempC) }
                : null,
            electrical = s.Electrical().Select(e => new { label = e.Description ?? e.Key, value = Fmt.Round(e.Value, 0), text = e.Text }),
            warnings = new
            {
                master = s.MasterWarning,
                caution = s.MasterCaution,
                stall = sim.Bool("stallWarning"),
                overspeed = sim.Bool("overspeedWarning"),
                ecam = new
                {
                    available = ecamState.Available,
                    table = ecamState.TableStatus,
                    left = ecamState.Left.Select(l => new { text = l.Text, title = l.IsTitle, known = l.Known }),
                    right = ecamState.Right.Select(l => new { text = l.Text, known = l.Known }),
                },
            },
            engines = Enumerable.Range(1, engineCount).Select(i => new
            {
                n = i,
                running = sim.Bool($"eng{i}Running"),
                n1 = Fmt.Round(sim.Num($"eng{i}N1"), 1),
                n2 = Fmt.Round(sim.Num($"eng{i}N2"), 1),
                egt = Fmt.Int(s.EgtC(i)),
                ff = Fmt.Int(s.FuelFlowKgh(i)),
            }),
            fuelFlowSource = s.FuelFlowSource,
            fuelKg = Fmt.Int(sim.Num("fuelTotalKg")),
            grossWeightKg = Fmt.Int(sim.Num("grossWeightKg")),
            environment = new
            {
                windDir = Fmt.Heading(sim.Num("windDir")),
                windKt = Fmt.Int(sim.Num("windKt")),
                oat = Fmt.Round(sim.Num("oatC"), 0),
                qnh = Fmt.Int(sim.Num("qnhHpa")),
                baro = Fmt.Round(sim.Num("baroSettingHpa"), 0),
                utc = Fmt.ClockTime(sim.Num("zuluTimeSec")),
            },
            atc = new
            {
                callsign = sim.Str("atcId"),
                runway = runway is null
                    ? null
                    : new { ident = runway.Ident, spoken = "runway " + runway.Spoken, airport = runway.Airport, designatorSource = runway.DesignatorSource },
                clearances = new
                {
                    ifr = sim.Bool("atcClearedIfr"),
                    taxi = sim.Bool("atcClearedTaxi"),
                    takeoff = sim.Bool("atcClearedTakeoff"),
                    landing = sim.Bool("atcClearedLanding"),
                },
            },
            bindings = profile is null ? null : Bindings(profile, s),
        };
    }

    private static IEnumerable<object> Bindings(AircraftProfile profile, AircraftSignals s) =>
        profile.Bindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).Select(b =>
        {
            var raw = b.Value.Name.Contains("{i}", StringComparison.Ordinal) ? s.Raw(b.Key, 1) : s.Raw(b.Key);
            string? text = b.Value.Type switch
            {
                "bitmask" => raw is { } r ? string.Join(" ", b.Value.DecodeBits(r)) : null,
                "string" => s.Text(b.Key),
                _ => raw is { } r2 ? b.Value.Translate(r2) : null,
            };

            return (object)new { key = b.Key, variable = b.Value.Name, verified = b.Value.Verified, raw = Fmt.Round(raw, 2), text };
        });

    private object Plan()
    {
        var ofp = simBrief.Current;
        if (ofp is null)
        {
            return new { configured = simBrief.IsConfigured, loaded = false, error = simBrief.LastError };
        }

        object? progress = null;
        var points = ofp.RoutePoints();
        if (sim.Connection.IsConnected && sim.Num("lat") is { } lat && sim.Num("lon") is { } lon)
        {
            if (simBrief.Locate(ofp, points, lat, lon) is { } route)
            {
                var gs = sim.Num("gs");
                var zulu = sim.Num("zuluTimeSec");
                var passage = simBrief.ObservePassage(ofp, points, route, zulu, gs);
                var fp = ProgressCalculator.Compute(ofp, points, route, sim.Num("fuelTotalKg"), new AircraftSignals(sim).FmsWaypoint(lat, lon));
                var times = EtaCalculator.Predict(ofp, points, fp, gs, passage);
                progress = new
                {
                    flownNm = Math.Round(route.DistanceFlownNm),
                    totalNm = Math.Round(fp.TotalDistanceNm),
                    toGoNm = Math.Round(fp.DistanceToGoNm),
                    eta = FlightPlanTools.Eta(zulu, times.DestinationSec),
                    etaSource = times.Source,
                    versusSchedule = FlightPlanTools.VersusSchedule(ofp, zulu, times.DestinationSec),
                    next = new
                    {
                        ident = fp.Next.Ident,
                        source = fp.NextSource,
                        notInPlan = fp.FmsWaypointNotInPlan,
                        airway = fp.NextFix?.Airway,
                        distNm = Math.Round(fp.DistanceToNextNm, 1),
                        eta = FlightPlanTools.Eta(zulu, times.NextSec),
                        altFt = fp.NextFix?.AltitudeFt,
                    },
                    following = fp.Following,
                    toc = fp.TopOfClimb is { } toc ? new { distNm = Math.Round(toc.DistanceNm), eta = FlightPlanTools.Eta(zulu, times.TopOfClimbSec) } : null,
                    tod = fp.TopOfDescent is { } tod ? new { distNm = Math.Round(tod.DistanceNm), eta = FlightPlanTools.Eta(zulu, times.TopOfDescentSec) } : null,
                    xtk = Math.Round(route.CrossTrackNm, 1),
                    onRoute = fp.OnRoute,
                    terminal = fp.InTerminalArea,
                    fuel = new
                    {
                        actual = Fmt.Int(fp.ActualFuelKg),
                        planned = Fmt.Int(fp.PlannedFuelHereKg),
                        diff = Fmt.Int(fp.FuelDifferenceKg),
                        predictedLanding = Fmt.Int(fp.PredictedLandingFuelKg),
                        plannedLanding = Fmt.Int(ofp.Fuel.PlanLandingKg),
                    },
                };
            }
        }

        return new
        {
            configured = true,
            loaded = true,
            error = simBrief.LastError,
            callsign = ofp.Callsign,
            origin = new { icao = ofp.Origin.Icao, runway = ofp.Origin.PlanRunway },
            destination = new { icao = ofp.Destination.Icao, runway = ofp.Destination.PlanRunway },
            alternate = ofp.Alternate?.Icao,
            initialAltitudeFt = ofp.InitialAltitudeFt,
            costIndex = ofp.CostIndex,
            progress,
            fuelChecks = FuelTrend(ofp, points),
        };
    }

    /// <summary>Verlauf der Fuel Checks für den Trend unter der Route (x = Strecke ab Start, wie die Routenleiste).</summary>
    private object? FuelTrend(Ofp ofp, IReadOnlyList<RoutePoint> points)
    {
        var log = monitoring.FuelChecks;
        if (log.PlanId != FuelPlanSample.PlanIdOf(ofp))
        {
            return null;
        }

        var entries = log.Last(200);
        if (entries.Count == 0)
        {
            return null;
        }

        var totalNm = 0.0;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            totalNm += RouteMath.DistanceNm(points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon);
        }

        return new
        {
            totalNm = Math.Round(totalNm),
            intervalMinutes = monitoring.FuelCheckSettings.IntervalMinutes,
            items = entries.Select(e => new
            {
                id = e.Id,
                kind = e.Kind,
                time = e.ZuluSec is { } z ? Fmt.ClockTime(z) + "Z" : e.TimeUtc.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z",
                nm = Fmt.Round(e.DistanceFlownNm, 0),
                fix = e.Ident,
                actual = Fmt.Int(e.ActualKg),
                diff = Fmt.Int(e.DiffKg),
                change = Fmt.Int(e.ChangeKg),
                landing = Fmt.Int(e.PredictedLandingKg),
                minimum = Fmt.Int(e.MinimumLandingKg),
                from = Fmt.Int(e.FromKg),
                warnings = e.Warnings,
                callout = e.Callout,
            }),
        };
    }

    internal static object InputView(InputService input, DateTimeOffset now) => new
    {
        enabled = input.Enabled,
        actions = input.Actions.Count,
        pending = input.Pending is { } p
            ? new
            {
                action = p.Plan.Action.Id,
                value = p.Plan.ValueText,
                readback = p.Prompt,
                expiresInSec = Math.Max(0, (int)Math.Ceiling((p.ExpiresUtc - now).TotalSeconds)),
                other = false,
            }
            : input.ForeignPending is { } f
                ? new
                {
                    action = f.Action,
                    value = f.Value,
                    readback = f.Readback,
                    expiresInSec = Math.Max(0, (int)Math.Ceiling((f.ExpiresUtc - now).TotalSeconds)),
                    other = true,
                }
                : null,
        log = input.History.Take(10).Select(e => new
        {
            time = e.TimeUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            action = e.Action,
            value = e.Value,
            outcome = e.Outcome,
            // Fester Code (InputOutcome) für die Farbe im Dashboard – outcome ist der Text in der eingestellten Sprache
            code = e.Code,
            detail = e.Detail,
            verified = e.Verified,
        }),
    };
}
