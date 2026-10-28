using System.ComponentModel;
using Kopilot.Aircraft;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Sim;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>
/// MCP-Tools für Claude. Alle Tools sind lesend. Antworten sind JSON, Schlüssel englisch, Meldungen deutsch.
/// Jede Antwort enthält "activeAlerts" – die gerade aktiven Monitoring-Meldungen.
/// </summary>
[McpServerToolType]
public static class KopilotTools
{
    internal const string NotConnectedMessage =
        "Keine Verbindung zu Microsoft Flight Simulator. Kopilot verbindet sich automatisch, sobald MSFS läuft.";

    [McpServerTool(Name = "kopilot_status", ReadOnly = true)]
    [Description(
        "Connection status of the Kopilot bridge to Microsoft Flight Simulator 2024: whether the sim is connected and paused, " +
        "loaded aircraft (title and aircraft.cfg path), active aircraft profile, monitoring, input module, SimBrief and ECAM table status and any setup error. " +
        "Call this first if other tools return no data. Every Kopilot response includes 'activeAlerts' (current monitoring alerts with short callouts).")]
    public static string Status(
        SimConnectClient sim,
        ProfileStore profiles,
        MonitoringService monitoring,
        SimBriefService simBrief,
        EcamCatalog ecam,
        AlertBus alerts,
        Kopilot.Input.InputService input)
    {
        var connection = sim.Connection;
        var status = sim.Status;
        var profile = sim.ActiveProfile;
        return Json.Serialize(new
        {
            connected = connection.IsConnected,
            simulator = connection.Application,
            simulatorVersion = connection.Version,
            connectedSinceUtc = connection.SinceUtc,
            flightLoaded = status.FlightRunning,
            paused = status.Paused,
            pauseKind = status.PauseKind,
            aircraftTitle = sim.Str("title"),
            aircraftPath = status.AircraftPath,
            activeProfile = profile is null
                ? null
                : new
                {
                    profile.Name,
                    file = profile.SourceFile,
                    bindings = profile.Bindings.Count,
                    unverifiedBindings = profile.Bindings.Where(b => !b.Value.Verified).Select(b => b.Key),
                    variables = profile.Variables.Count,
                    ecamTable = profile.Ecam is { } e ? ecam.StatusFor(e.Format) : null,
                },
            monitoring = new
            {
                enabled = monitoring.Enabled,
                phase = monitoring.Phase,
                lastEvaluationUtc = monitoring.LastEvaluationUtc,
            },
            input = new
            {
                enabled = input.Enabled,
                actions = input.Actions.Count,
                pending = input.Pending?.Plan.Readback,
            },
            simBrief = new
            {
                configured = simBrief.IsConfigured,
                loaded = simBrief.Current is { } ofp ? $"{ofp.Origin.Icao} → {ofp.Destination.Icao}" : null,
                error = simBrief.LastError,
            },
            receivingValues = sim.ReceivingCount,
            lastError = sim.LastError,
            profilesDirectory = profiles.DirectoryPath,
            profilesLoaded = profiles.All.Select(p => p.Name),
            profileErrors = profiles.Errors,
            kopilotVersion = typeof(KopilotTools).Assembly.GetName().Version?.ToString(3),
            message = connection.IsConnected ? null : NotConnectedMessage,
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_flight_state", ReadOnly = true)]
    [Description(
        "Current flight state of the pilot's aircraft in MSFS 2024: altitude (indicated, flight level, AGL, radio), speeds in knots (IAS, TAS, GS, vertical speed), " +
        "attitude and heading, position, configuration (flaps handle, gear, spoilers, parking brake), autopilot/autothrust status with FMA modes and speed/altitude targets, " +
        "takeoff data (V1/VR/V2 in knots, FLEX temperature) and sim warnings. Speed targets are always in knots – a Mach target is converted. With an aircraft profile (FlyByWire A32NX/A380X) the autopilot block comes from the aircraft's own variables ('source': 'profile'); " +
        "otherwise from generic SimVars ('source': 'generic'), which study-level airliners may not update. 'paused' is true while the sim is paused.")]
    public static string GetFlightState(SimConnectClient sim, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected(alerts);
        }

        var signals = new AircraftSignals(sim);
        var status = sim.Status;
        var age = sim.AgeSeconds("altIndicated");
        return Json.Serialize(new
        {
            connected = true,
            paused = status.Paused,
            pauseKind = status.PauseKind,
            dataAgeSec = Fmt.Round(age, 1),
            stale = !status.Paused && age is null or > 5,
            aircraft = new { title = sim.Str("title"), atcId = sim.Str("atcId"), profile = signals.Profile?.Name },
            onGround = sim.Bool("onGround"),
            altitude = new
            {
                indicatedFt = Fmt.Int(sim.Num("altIndicated")),
                pressureAltitudeFt = Fmt.Int(sim.Num("altPressure")),
                flightLevel = Fmt.Int(sim.Num("altPressure") / 100),
                trueMslFt = Fmt.Int(sim.Num("altTrue")),
                aglFt = Fmt.Int(sim.Num("altAgl")),
                radioFt = Fmt.Int(sim.Num("altRadio")),
            },
            speed = new
            {
                iasKt = Fmt.Int(sim.Num("ias")),
                tasKt = Fmt.Int(sim.Num("tas")),
                groundSpeedKt = Fmt.Int(sim.Num("gs")),
                verticalSpeedFpm = Fmt.Int(sim.Num("vs")),
            },
            attitude = new
            {
                headingMagDeg = Fmt.Heading(sim.Num("hdgMag")),
                headingTrueDeg = Fmt.Heading(sim.Num("hdgTrue")),
                // Im Stand ist der Track bedeutungslos
                trackMagDeg = sim.Num("gs") is > 5 ? Fmt.Heading(sim.Num("trackMag")) : null,
                // MSFS liefert Pitch "Nase hoch = negativ" und Bank "links = positiv" – hier umgedreht.
                pitchUpDeg = Fmt.Round(Fmt.Negate(sim.Num("pitch")), 1),
                bankRightDeg = Fmt.Round(Fmt.Negate(sim.Num("bank")), 1),
            },
            position = new
            {
                lat = Fmt.Round(sim.Num("lat"), 5),
                lon = Fmt.Round(sim.Num("lon"), 5),
            },
            configuration = new
            {
                flaps = signals.FlapsDisplay,
                flapsHandleIndex = Fmt.Int(sim.Num("flapsIndex")),
                gearHandle = sim.Bool("gearHandle") switch { true => "DOWN", false => "UP", null => null },
                gearExtendedPct = Fmt.Int(sim.Num("gearCenterPct")),
                spoilersArmed = signals.SpoilersArmed,
                speedbrakeHandlePct = Fmt.Int(sim.Num("spoilersHandlePct")),
                parkingBrakeSet = signals.ParkingBrake,
                autobrake = signals.Autobrake,
            },
            takeoff = Takeoff(signals),
            autopilot = Autopilot(sim, signals),
            warnings = new
            {
                masterWarning = signals.MasterWarning,
                masterCaution = signals.MasterCaution,
                stall = sim.Bool("stallWarning"),
                overspeed = sim.Bool("overspeedWarning"),
            },
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_systems", ReadOnly = true)]
    [Description(
        "Aircraft systems in MSFS 2024: engines (running, N1, N2, EGT, fuel flow in kg/h), APU, fuel on board and gross weight, " +
        "exterior lights, seatbelt sign, transponder code/mode, COM radio frequencies and – with an aircraft profile – electrical values " +
        "(generator voltage/frequency, battery voltage, buses powered). Fuel flow and EGT come from the aircraft profile when available " +
        "('fuelFlowSource', 'egtSource'). 'plausibility' lists values that look implausible (e.g. extreme weather, air start).")]
    public static string GetSystems(SimConnectClient sim, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected(alerts);
        }

        var signals = new AircraftSignals(sim);
        var engineCount = Math.Clamp(Fmt.Int(sim.Num("engineCount")) ?? 2, 1, SimVarCatalog.MaxEngines);
        var notes = new List<string>();
        var engines = Enumerable.Range(1, engineCount).Select(i =>
        {
            var running = sim.Bool($"eng{i}Running");
            var n2 = sim.Num($"eng{i}N2");
            var egt = signals.EgtC(i);
            var ff = signals.FuelFlowKgh(i);
            if (running == true && egt is < -40)
            {
                notes.Add($"ENG {i}: EGT {egt:0} °C bei laufendem Triebwerk – unplausibel (extremes Wetter oder Air-Start?).");
            }

            if (running == true && n2 is > 50 && ff is not > 1)
            {
                notes.Add($"ENG {i}: Fuel Flow 0 bei laufendem Triebwerk – Variable im Profil prüfen.");
            }

            return new
            {
                engine = i,
                running,
                n1Pct = Fmt.Round(sim.Num($"eng{i}N1"), 1),
                n2Pct = Fmt.Round(n2, 1),
                egtC = Fmt.Int(egt),
                fuelFlowKgPerHour = Fmt.Int(ff),
            };
        }).ToList();

        return Json.Serialize(new
        {
            connected = true,
            engines,
            fuelFlowSource = signals.FuelFlowSource,
            egtSource = signals.EgtSource,
            totalFuelFlowKgPerHour = Fmt.Int(engines.Sum(e => (double?)e.fuelFlowKgPerHour ?? 0)),
            apu = new { rpmPct = Fmt.Round(sim.Num("apuRpmPct"), 1) },
            fuel = new
            {
                totalKg = Fmt.Int(sim.Num("fuelTotalKg")),
                grossWeightKg = Fmt.Int(sim.Num("grossWeightKg")),
            },
            lights = new
            {
                beacon = sim.Bool("lightBeacon"),
                strobe = sim.Bool("lightStrobe"),
                landing = sim.Bool("lightLanding"),
                taxi = sim.Bool("lightTaxi"),
                nav = sim.Bool("lightNav"),
                logo = sim.Bool("lightLogo"),
                wing = sim.Bool("lightWing"),
            },
            seatbeltSign = sim.Bool("seatbeltSign"),
            transponder = new
            {
                code = Fmt.Squawk(sim.Num("xpdrCode")),
                mode = Fmt.TransponderMode(sim.Num("xpdrState")),
            },
            radios = new
            {
                com1ActiveMhz = Fmt.Round(sim.Num("com1Active"), 3),
                com1StandbyMhz = Fmt.Round(sim.Num("com1Standby"), 3),
                com2ActiveMhz = Fmt.Round(sim.Num("com2Active"), 3),
            },
            electrical = signals.Electrical().Select(e => new { key = e.Key, label = e.Description, value = Fmt.Round(e.Value, 1), text = e.Text }),
            plausibility = notes,
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_navigation", ReadOnly = true)]
    [Description(
        "Navigation progress from the sim's GPS flight plan variables: previous/next waypoint, distance and time to the next waypoint, " +
        "waypoint index, time to destination and ETA in UTC. Note: airliner FMS (iniBuilds, FlyByWire) usually do not feed these GPS variables; " +
        "if they are empty, use get_progress (SimBrief plan) instead.")]
    public static string GetNavigation(SimConnectClient sim, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected(alerts);
        }

        var zulu = sim.Num("zuluTimeSec");
        var destEte = sim.Num("destEteSec");
        var nextEte = sim.Num("wpEteSec");
        var hasPlan = sim.Bool("gpsFlightPlanActive") == true || sim.Str("wpNextId") is not null;

        if (!hasPlan)
        {
            // Ohne aktiven GPS-Flugplan liefert MSFS hier nur Nullen – die wären irreführend.
            return Json.Serialize(new
            {
                connected = true,
                flightPlanActive = false,
                message = "Die GPS-Flugplanvariablen des Sims sind leer – das FMS dieses Flugzeugs speist sie nicht. Für Route und Fortschritt get_progress (SimBrief) nutzen.",
                groundSpeedKt = Fmt.Int(sim.Num("gs")),
                activeAlerts = AlertView.Active(alerts),
            });
        }

        return Json.Serialize(new
        {
            connected = true,
            flightPlanActive = true,
            previousWaypoint = sim.Str("wpPrevId"),
            nextWaypoint = sim.Str("wpNextId"),
            nextWaypointDistanceNm = Fmt.Round(sim.Num("wpDistanceNm"), 1),
            nextWaypointBearingDeg = Fmt.Heading(sim.Num("wpBearingDeg")),
            nextWaypointEte = Fmt.Duration(nextEte),
            waypointIndex = Fmt.Int(sim.Num("wpIndex")),
            waypointCount = Fmt.Int(sim.Num("wpCount")),
            destinationEte = Fmt.Duration(destEte),
            destinationEtaUtc = zulu is not null && destEte is > 0 ? Fmt.ClockTime(zulu + destEte) + "Z" : null,
            groundSpeedKt = Fmt.Int(sim.Num("gs")),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_environment", ReadOnly = true)]
    [Description(
        "Weather and time at the aircraft's position in MSFS 2024: wind, outside air temperature, sea-level pressure (QNH), " +
        "the altimeter setting on the captain side, visibility, UTC and local sim time, and simulation rate.")]
    public static string GetEnvironment(SimConnectClient sim, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected(alerts);
        }

        return Json.Serialize(new
        {
            connected = true,
            wind = new { directionDeg = Fmt.Heading(sim.Num("windDir")), speedKt = Fmt.Int(sim.Num("windKt")) },
            outsideAirTempC = Fmt.Round(sim.Num("oatC"), 1),
            seaLevelPressureHpa = Fmt.Int(sim.Num("qnhHpa")),
            altimeterSettingHpa = Fmt.Round(sim.Num("baroSettingHpa"), 1),
            visibilityM = Fmt.Int(sim.Num("visibilityM")),
            timeUtc = Fmt.ClockTime(sim.Num("zuluTimeSec")) is { } z ? z + "Z" : null,
            timeLocal = Fmt.ClockTime(sim.Num("localTimeSec")),
            simulationRate = Fmt.Round(sim.Num("simRate"), 2),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_aircraft_profile", ReadOnly = true)]
    [Description(
        "The active Kopilot aircraft profile for the loaded aircraft: semantic bindings (e.g. ap1, athrStatus, fmaVertical) with their variable, " +
        "current raw value, text meaning and whether the variable was verified in the sim, plus extra profile variables, ECAM configuration and speed limits. " +
        "Use this to check which values come from aircraft-specific L: variables.")]
    public static string GetAircraftProfile(SimConnectClient sim, ProfileStore profiles, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected(alerts);
        }

        var profile = sim.ActiveProfile;
        if (profile is null)
        {
            return Json.Serialize(new
            {
                connected = true,
                aircraftTitle = sim.Str("title"),
                aircraftPath = sim.Status.AircraftPath,
                profile = (string?)null,
                message = $"Für dieses Flugzeug gibt es kein Profil. Profile liegen in {profiles.DirectoryPath}.",
                activeAlerts = AlertView.Active(alerts),
            });
        }

        var signals = new AircraftSignals(sim);
        var bindings = profile.Bindings.Select(b =>
        {
            var raw = b.Value.Name.Contains("{i}", StringComparison.Ordinal) ? signals.Raw(b.Key, 1) : signals.Raw(b.Key);
            return new
            {
                key = b.Key,
                variable = b.Value.Name,
                type = b.Value.Type,
                verified = b.Value.Verified,
                raw = Fmt.Round(raw, 3),
                text = b.Value.Type switch
                {
                    "bitmask" => raw is { } r ? string.Join(" ", b.Value.DecodeBits(r)) : null,
                    "string" => signals.Text(b.Key),
                    _ => raw is { } r2 ? b.Value.Translate(r2) : null,
                },
                description = b.Value.Description,
            };
        });

        var values = profile.Variables.Select(v =>
        {
            var unit = string.IsNullOrWhiteSpace(v.Unit) ? "number" : v.Unit;
            var raw = sim.Get(SimConnectClient.VarKey(v.Name, unit))?.Value;
            return new
            {
                key = v.Key,
                description = v.Description,
                variable = v.Name,
                raw = raw is double d ? Math.Round(d, 3) : raw,
                text = v.Translate(raw),
            };
        });

        return Json.Serialize(new
        {
            connected = true,
            aircraftTitle = sim.Str("title"),
            aircraftPath = sim.Status.AircraftPath,
            profile = profile.Name,
            file = profile.SourceFile,
            notes = profile.Notes,
            bindings,
            missingBindings = BindingKeys.All.Where(k => !profile.Bindings.ContainsKey(k)),
            values,
            ecam = profile.Ecam is { } e ? new { e.Format, e.LeftLines, e.RightLines, e.Lines } : null,
            limits = profile.Limits,
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "read_simvar", ReadOnly = true)]
    [Description(
        "Reads any single simulation variable once, for exploring values not covered by the other tools. " +
        "Accepts standard SimVars (e.g. 'AIRSPEED INDICATED', with index like 'TURB ENG N1:1') and local L: variables (e.g. 'L:SOME_VAR'). " +
        "Text variables (e.g. 'ATC RUNWAY AIRPORT NAME', 'TITLE', 'ATC ID', any '... IDENT' or '... NAME') are read as text automatically; for other text variables pass unit 'string'. " +
        "Note: an unknown L: variable reads as 0 instead of failing, so a 0 does not prove the variable exists.")]
    public static async Task<string> ReadSimVar(
        SimConnectClient sim,
        [Description("Variable name, e.g. 'INDICATED ALTITUDE' or 'L:XYZ'. An 'A:' prefix is optional.")] string name,
        [Description("SimConnect unit, e.g. 'feet', 'knots', 'percent', 'Bool', 'number'. Use 'string' for text variables. Default: number.")] string unit = "number",
        CancellationToken cancellationToken = default)
    {
        var result = await sim.ReadOnceAsync(name, unit, cancellationToken);
        return Json.Serialize(new
        {
            name,
            unit = result.Value is string && !unit.Equals("string", StringComparison.OrdinalIgnoreCase) ? "string (automatisch)" : unit,
            ok = result.Ok,
            value = result.Value is double d ? Math.Round(d, 4) : result.Value,
            error = result.Error,
        });
    }

    [McpServerTool(Name = "reload_profiles", ReadOnly = false, Idempotent = true)]
    [Description("Reloads the aircraft profile files (*.json) from disk and re-applies the matching profile. Use after a profile file was edited.")]
    public static string ReloadProfiles(SimConnectClient sim, ProfileStore profiles)
    {
        profiles.Reload();
        sim.RefreshProfile();
        return Json.Serialize(new
        {
            profilesLoaded = profiles.All.Select(p => new { p.Name, file = p.SourceFile, bindings = p.Bindings.Count, variables = p.Variables.Count }),
            errors = profiles.Errors,
        });
    }

    private static object Autopilot(SimConnectClient sim, AircraftSignals s)
    {
        if (s.AutopilotSource == "generic")
        {
            return new
            {
                source = "generic",
                note = "Generische SimVars – bei Study-Level-Airlinern evtl. nicht aussagekräftig. A/THR-Status ist ohne Profil unbekannt.",
                engaged = s.ApEngaged,
                flightDirector = s.FlightDirector,
                navHold = sim.Bool("apNav"),
                approachHold = sim.Bool("apAppr"),
                selectedAltitudeFt = Fmt.Int(s.SelectedAltitudeFt),
                selectedHeadingDeg = Fmt.Heading(s.SelectedHeadingDeg),
                selectedSpeedKt = Fmt.Int(s.SelectedSpeedKt),
                selectedSpeedFromMach = s.SelectedSpeedIsMach ? true : (bool?)null,
                selectedVerticalSpeedFpm = Fmt.Int(sim.Num("apVs")),
            };
        }

        return new
        {
            source = "profile",
            engaged = s.ApEngaged,
            ap1 = s.Ap1,
            ap2 = s.Ap2,
            flightDirector = s.FlightDirector,
            autothrust = s.AutothrustStatus,
            fma = new
            {
                thrust = s.AutothrustMode,
                vertical = s.FmaVertical,
                verticalArmed = s.FmaVerticalArmed,
                lateral = s.FmaLateral,
                lateralArmed = s.FmaLateralArmed,
            },
            speedManaged = s.SpeedManaged,
            speedTargetKt = Fmt.Int(s.SpeedTargetKt),
            selectedSpeedKt = Fmt.Int(s.SelectedSpeedKt),
            vlsKt = Fmt.Int(s.VlsKt),
            selectedAltitudeFt = Fmt.Int(s.SelectedAltitudeFt),
            selectedAltitudeSource = s.SelectedAltitudeSource,
            selectedHeadingDeg = Fmt.Heading(s.SelectedHeadingDeg),
            selectedSpeedFromMach = s.SelectedSpeedIsMach ? true : (bool?)null,
        };
    }

    /// <summary>Startdaten aus der MCDU (V-Speeds in kt, FLEX), nur wenn das Profil sie kennt.</summary>
    private static object? Takeoff(AircraftSignals s)
    {
        if (!s.Has(BindingKeys.V1) && !s.Has(BindingKeys.FlexTemp))
        {
            return null;
        }

        return new
        {
            v1Kt = Fmt.Int(s.V1Kt),
            vrKt = Fmt.Int(s.VrKt),
            v2Kt = Fmt.Int(s.V2Kt),
            flexTempC = Fmt.Int(s.FlexTempC),
        };
    }

    internal static string NotConnected(AlertBus alerts) =>
        Json.Serialize(new { connected = false, message = NotConnectedMessage, activeAlerts = AlertView.Active(alerts) });
}
