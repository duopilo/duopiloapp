using System.ComponentModel;
using Kopilot.Aircraft;
using Kopilot.Sim;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>
/// MCP-Tools für Claude. Alle Tools sind lesend. Antworten sind JSON, Schlüssel englisch, Meldungen deutsch.
/// </summary>
[McpServerToolType]
public static class KopilotTools
{
    private const string NotConnectedMessage =
        "Keine Verbindung zu Microsoft Flight Simulator. Kopilot verbindet sich automatisch, sobald MSFS läuft.";

    [McpServerTool(Name = "kopilot_status", ReadOnly = true)]
    [Description(
        "Connection status of the Kopilot bridge to Microsoft Flight Simulator 2024: whether the sim is connected, " +
        "sim version, loaded aircraft title, active aircraft profile and any setup error. Call this first if other tools return no data.")]
    public static string Status(SimConnectClient sim, ProfileStore profiles)
    {
        var connection = sim.Connection;
        var profile = sim.ActiveProfile;
        return Json.Serialize(new
        {
            connected = connection.IsConnected,
            simulator = connection.Application,
            simulatorVersion = connection.Version,
            connectedSinceUtc = connection.SinceUtc,
            aircraftTitle = sim.Str("title"),
            activeProfile = profile is null ? null : new { profile.Name, file = profile.SourceFile, variables = profile.Variables.Count },
            receivingValues = sim.ReceivingCount,
            lastError = sim.LastError,
            profilesDirectory = profiles.DirectoryPath,
            profilesLoaded = profiles.All.Select(p => p.Name),
            profileErrors = profiles.Errors,
            kopilotVersion = typeof(KopilotTools).Assembly.GetName().Version?.ToString(3),
            message = connection.IsConnected ? null : NotConnectedMessage,
        });
    }

    [McpServerTool(Name = "get_flight_state", ReadOnly = true)]
    [Description(
        "Current flight state of the pilot's aircraft in MSFS 2024: altitude (indicated, flight level, AGL, radio), speeds (IAS, TAS, GS, Mach, vertical speed), " +
        "attitude and heading, position, configuration (flaps handle, gear, spoilers, parking brake), basic autopilot targets and warnings. " +
        "Use this whenever the pilot asks about the current flight situation. Values are null if the sim does not provide them. " +
        "The autopilot block uses generic SimVars that study-level airliners (e.g. iniBuilds Airbus) may not update – prefer get_aircraft_profile for FMA modes.")]
    public static string GetFlightState(SimConnectClient sim)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected();
        }

        var age = sim.AgeSeconds("altIndicated");
        return Json.Serialize(new
        {
            connected = true,
            dataAgeSec = Fmt.Round(age, 1),
            stale = age is null or > 5,
            aircraft = new { title = sim.Str("title"), atcId = sim.Str("atcId") },
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
                mach = Fmt.Round(sim.Num("mach"), 3),
                verticalSpeedFpm = Fmt.Int(sim.Num("vs")),
            },
            attitude = new
            {
                headingMagDeg = Fmt.Heading(sim.Num("hdgMag")),
                headingTrueDeg = Fmt.Heading(sim.Num("hdgTrue")),
                trackMagDeg = Fmt.Heading(sim.Num("trackMag")),
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
                flapsHandleIndex = Fmt.Int(sim.Num("flapsIndex")),
                gearHandle = sim.Bool("gearHandle") switch { true => "DOWN", false => "UP", null => null },
                gearExtendedPct = Fmt.Int(sim.Num("gearCenterPct")),
                spoilersArmed = sim.Bool("spoilersArmed"),
                speedbrakeHandlePct = Fmt.Int(sim.Num("spoilersHandlePct")),
                parkingBrakeSet = sim.Bool("parkingBrake"),
            },
            autopilot = new
            {
                note = "Generic SimVars – may not reflect the real FMA of study-level airliners.",
                master = sim.Bool("apMaster"),
                flightDirector = sim.Bool("fdActive"),
                autothrustArmed = sim.Bool("athrArmed"),
                navHold = sim.Bool("apNav"),
                approachHold = sim.Bool("apAppr"),
                selectedAltitudeFt = Fmt.Int(sim.Num("apAlt")),
                selectedHeadingDeg = Fmt.Heading(sim.Num("apHdg")),
                selectedSpeedKt = Fmt.Int(sim.Num("apSpd")),
                selectedVerticalSpeedFpm = Fmt.Int(sim.Num("apVs")),
            },
            warnings = new
            {
                stall = sim.Bool("stallWarning"),
                overspeed = sim.Bool("overspeedWarning"),
            },
        });
    }

    [McpServerTool(Name = "get_systems", ReadOnly = true)]
    [Description(
        "Aircraft systems in MSFS 2024: engines (running, N1, N2, EGT, fuel flow), APU, fuel on board and gross weight, " +
        "exterior lights, seatbelt sign, transponder code/mode and COM radio frequencies.")]
    public static string GetSystems(SimConnectClient sim)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected();
        }

        var engineCount = Math.Clamp(Fmt.Int(sim.Num("engineCount")) ?? 2, 1, SimVarCatalog.MaxEngines);
        var engines = Enumerable.Range(1, engineCount).Select(i => new
        {
            engine = i,
            running = sim.Bool($"eng{i}Running"),
            n1Pct = Fmt.Round(sim.Num($"eng{i}N1"), 1),
            n2Pct = Fmt.Round(sim.Num($"eng{i}N2"), 1),
            egtC = Fmt.Int(sim.Num($"eng{i}Egt")),
            fuelFlowKgPerHour = Fmt.Int(sim.Num($"eng{i}FuelFlowPph") * 0.45359237),
        });

        return Json.Serialize(new
        {
            connected = true,
            engines,
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
        });
    }

    [McpServerTool(Name = "get_navigation", ReadOnly = true)]
    [Description(
        "Navigation progress from the sim's GPS flight plan variables: previous/next waypoint, distance and time to the next waypoint, " +
        "waypoint index, time to destination and ETA in UTC. Note: some airliner FMS (e.g. iniBuilds) may not feed these GPS variables; " +
        "if they are empty, ask the pilot to read the MCDU F-PLN page instead.")]
    public static string GetNavigation(SimConnectClient sim)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected();
        }

        var zulu = sim.Num("zuluTimeSec");
        var destEte = sim.Num("destEteSec");
        var nextEte = sim.Num("wpEteSec");

        return Json.Serialize(new
        {
            connected = true,
            flightPlanActive = sim.Bool("gpsFlightPlanActive"),
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
        });
    }

    [McpServerTool(Name = "get_environment", ReadOnly = true)]
    [Description(
        "Weather and time at the aircraft's position in MSFS 2024: wind, outside air temperature, sea-level pressure (QNH), " +
        "the altimeter setting on the captain side, visibility, UTC and local sim time, and simulation rate.")]
    public static string GetEnvironment(SimConnectClient sim)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected();
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
        });
    }

    [McpServerTool(Name = "get_aircraft_profile", ReadOnly = true)]
    [Description(
        "Aircraft-specific values from the active Kopilot aircraft profile (usually L: variables such as FMA modes, autobrake or ECAM states), " +
        "with raw values and their text meaning where the profile defines one. Returns which profile is active for the loaded aircraft.")]
    public static string GetAircraftProfile(SimConnectClient sim, ProfileStore profiles)
    {
        if (!sim.Connection.IsConnected)
        {
            return NotConnected();
        }

        var profile = sim.ActiveProfile;
        if (profile is null)
        {
            return Json.Serialize(new
            {
                connected = true,
                aircraftTitle = sim.Str("title"),
                profile = (string?)null,
                message = $"Für dieses Flugzeug gibt es kein Profil. Profile liegen in {profiles.DirectoryPath}.",
            });
        }

        var values = profile.Variables.Select(v =>
        {
            var raw = sim.Get(SimConnectClient.ProfileKeyPrefix + v.Key)?.Value;
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
            profile = profile.Name,
            notes = profile.Notes,
            values,
        });
    }

    [McpServerTool(Name = "read_simvar", ReadOnly = true)]
    [Description(
        "Reads any single simulation variable once, for exploring values not covered by the other tools. " +
        "Accepts standard SimVars (e.g. 'AIRSPEED INDICATED', with index like 'TURB ENG N1:1') and local L: variables (e.g. 'L:SOME_VAR'). " +
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
            unit,
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
            profilesLoaded = profiles.All.Select(p => new { p.Name, file = p.SourceFile, variables = p.Variables.Count }),
            errors = profiles.Errors,
        });
    }

    private static string NotConnected() => Json.Serialize(new { connected = false, message = NotConnectedMessage });
}
