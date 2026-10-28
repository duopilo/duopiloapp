namespace Kopilot.Sim;

/// <summary>
/// Standard-SimVars, die Kopilot dauerhaft (1x pro Sekunde) abonniert.
/// Jede Variable bekommt eine eigene Data Definition – ist ein Name in MSFS unbekannt,
/// bleibt nur dieser eine Wert leer, alle anderen funktionieren weiter.
/// </summary>
public static class SimVarCatalog
{
    public const int MaxEngines = 4;

    public static IReadOnlyList<SimVarSpec> All { get; } = Build();

    private static List<SimVarSpec> Build()
    {
        var list = new List<SimVarSpec>
        {
            // Flugzeug
            SimVarSpec.Str("title", "TITLE"),
            SimVarSpec.Str("atcId", "ATC ID"),
            SimVarSpec.Num("engineCount", "NUMBER OF ENGINES", "number"),

            // Position & Höhe
            SimVarSpec.Num("lat", "PLANE LATITUDE", "degrees"),
            SimVarSpec.Num("lon", "PLANE LONGITUDE", "degrees"),
            SimVarSpec.Num("altTrue", "PLANE ALTITUDE", "feet"),
            SimVarSpec.Num("altIndicated", "INDICATED ALTITUDE", "feet").AsFast(),
            SimVarSpec.Num("altPressure", "PRESSURE ALTITUDE", "feet").AsFast(),
            SimVarSpec.Num("altAgl", "PLANE ALT ABOVE GROUND", "feet").AsFast(),
            SimVarSpec.Num("altRadio", "RADIO HEIGHT", "feet").AsFast(),
            SimVarSpec.Bool("onGround", "SIM ON GROUND").AsFast(),

            // Geschwindigkeiten
            SimVarSpec.Num("ias", "AIRSPEED INDICATED", "knots").AsFast(),
            SimVarSpec.Num("tas", "AIRSPEED TRUE", "knots"),
            SimVarSpec.Num("mach", "AIRSPEED MACH", "mach"),
            SimVarSpec.Num("gs", "GROUND VELOCITY", "knots").AsFast(),
            SimVarSpec.Num("vs", "VERTICAL SPEED", "feet per minute").AsFast(),

            // Lage & Kurs
            SimVarSpec.Num("hdgMag", "PLANE HEADING DEGREES MAGNETIC", "degrees").AsFast(),
            SimVarSpec.Num("hdgTrue", "PLANE HEADING DEGREES TRUE", "degrees").AsFast(),
            SimVarSpec.Num("trackMag", "GPS GROUND MAGNETIC TRACK", "degrees"),
            SimVarSpec.Num("pitch", "PLANE PITCH DEGREES", "degrees").AsFast(),
            SimVarSpec.Num("bank", "PLANE BANK DEGREES", "degrees").AsFast(),

            // Konfiguration
            SimVarSpec.Num("flapsIndex", "FLAPS HANDLE INDEX", "number").AsFast(),
            SimVarSpec.Bool("gearHandle", "GEAR HANDLE POSITION").AsFast(),
            SimVarSpec.Num("gearCenterPct", "GEAR CENTER POSITION", "percent").AsFast(),
            SimVarSpec.Bool("spoilersArmed", "SPOILERS ARMED"),
            SimVarSpec.Num("spoilersHandlePct", "SPOILERS HANDLE POSITION", "percent"),
            SimVarSpec.Bool("parkingBrake", "BRAKE PARKING POSITION"),

            // Autopilot (Standard-SimVars – bei Study-Level-Airlinern evtl. nicht aussagekräftig)
            SimVarSpec.Bool("apMaster", "AUTOPILOT MASTER").AsFast(),
            SimVarSpec.Bool("fdActive", "AUTOPILOT FLIGHT DIRECTOR ACTIVE"),
            SimVarSpec.Bool("athrArmed", "AUTOPILOT THROTTLE ARM"),
            SimVarSpec.Num("apAlt", "AUTOPILOT ALTITUDE LOCK VAR", "feet").AsFast(),
            SimVarSpec.Num("apHdg", "AUTOPILOT HEADING LOCK DIR", "degrees"),
            SimVarSpec.Num("apSpd", "AUTOPILOT AIRSPEED HOLD VAR", "knots").AsFast(),
            SimVarSpec.Bool("apMachHold", "AUTOPILOT MACH HOLD"),
            SimVarSpec.Num("apMachVar", "AUTOPILOT MACH HOLD VAR", "number"),
            SimVarSpec.Num("apVs", "AUTOPILOT VERTICAL HOLD VAR", "feet per minute"),
            SimVarSpec.Bool("apNav", "AUTOPILOT NAV1 LOCK"),
            SimVarSpec.Bool("apAppr", "AUTOPILOT APPROACH HOLD"),

            // Warnungen
            SimVarSpec.Bool("stallWarning", "STALL WARNING").AsFast(),
            SimVarSpec.Bool("overspeedWarning", "OVERSPEED WARNING").AsFast(),

            // APU, Treibstoff, Gewicht
            SimVarSpec.Num("apuRpmPct", "APU PCT RPM", "percent"),
            SimVarSpec.Num("fuelTotalKg", "FUEL TOTAL QUANTITY WEIGHT", "kilograms"),
            SimVarSpec.Num("grossWeightKg", "TOTAL WEIGHT", "kilograms"),

            // Lichter & Kabine
            SimVarSpec.Bool("lightBeacon", "LIGHT BEACON"),
            SimVarSpec.Bool("lightStrobe", "LIGHT STROBE"),
            SimVarSpec.Bool("lightLanding", "LIGHT LANDING"),
            SimVarSpec.Bool("lightTaxi", "LIGHT TAXI"),
            SimVarSpec.Bool("lightNav", "LIGHT NAV"),
            SimVarSpec.Bool("lightLogo", "LIGHT LOGO"),
            SimVarSpec.Bool("lightWing", "LIGHT WING"),
            SimVarSpec.Bool("seatbeltSign", "CABIN SEATBELTS ALERT SWITCH"),

            // Funk & Transponder
            SimVarSpec.Num("xpdrCode", "TRANSPONDER CODE:1", "Bco16"),
            SimVarSpec.Num("xpdrState", "TRANSPONDER STATE:1", "number"),
            SimVarSpec.Num("com1Active", "COM ACTIVE FREQUENCY:1", "MHz"),
            SimVarSpec.Num("com1Standby", "COM STANDBY FREQUENCY:1", "MHz"),
            SimVarSpec.Num("com2Active", "COM ACTIVE FREQUENCY:2", "MHz"),

            // Ingame-ATC
            SimVarSpec.Str("atcAirline", "ATC AIRLINE"),
            SimVarSpec.Str("atcFlightNumber", "ATC FLIGHT NUMBER"),
            SimVarSpec.Bool("atcClearedIfr", "ATC CLEARED IFR"),
            SimVarSpec.Bool("atcClearedTaxi", "ATC CLEARED TAXI"),
            SimVarSpec.Bool("atcClearedTakeoff", "ATC CLEARED TAKEOFF"),
            SimVarSpec.Bool("atcClearedLanding", "ATC CLEARED LANDING"),
            SimVarSpec.Bool("atcRunwaySelected", "ATC RUNWAY SELECTED"),
            SimVarSpec.Str("atcRunwayAirport", "ATC RUNWAY AIRPORT NAME"),
            SimVarSpec.Num("atcRunwayHeadingTrue", "ATC RUNWAY HEADING DEGREES TRUE", "degrees"),
            SimVarSpec.Num("atcRunwayLengthFt", "ATC RUNWAY LENGTH", "feet"),
            SimVarSpec.Num("atcRunwayStartDistanceFt", "ATC RUNWAY START DISTANCE", "feet"),
            // Kandidaten für Höhenvorgaben des Ingame-ATC – Bedeutung im Testflug prüfen
            SimVarSpec.Num("atcCurrentWpAltFt", "ATC CURRENT WAYPOINT ALTITUDE", "feet"),
            SimVarSpec.Num("atcPreviousWpAltFt", "ATC PREVIOUS WAYPOINT ALTITUDE", "feet"),
            SimVarSpec.Num("atcFpDiffAltFt", "ATC FLIGHTPLAN DIFF ALT", "feet"),
            SimVarSpec.Num("atcFpDiffHdgDeg", "ATC FLIGHTPLAN DIFF HEADING", "degrees"),
            SimVarSpec.Num("atcFpDiffDistM", "ATC FLIGHTPLAN DIFF DISTANCE", "meters"),
            SimVarSpec.Bool("atcAirportTowered", "ATC AIRPORT IS TOWERED"),
            SimVarSpec.Bool("atcOnParkingSpot", "ATC ON PARKING SPOT"),

            // Navigation (GPS-Variablen – werden nicht von jedem FMS befüllt)
            SimVarSpec.Bool("gpsFlightPlanActive", "GPS IS ACTIVE FLIGHT PLAN"),
            SimVarSpec.Str("wpNextId", "GPS WP NEXT ID"),
            SimVarSpec.Str("wpPrevId", "GPS WP PREV ID"),
            SimVarSpec.Num("wpDistanceNm", "GPS WP DISTANCE", "nautical miles"),
            SimVarSpec.Num("wpEteSec", "GPS WP ETE", "seconds"),
            SimVarSpec.Num("wpBearingDeg", "GPS WP BEARING", "degrees"),
            SimVarSpec.Num("wpIndex", "GPS FLIGHT PLAN WP INDEX", "number"),
            SimVarSpec.Num("wpCount", "GPS FLIGHT PLAN WP COUNT", "number"),
            SimVarSpec.Num("destEteSec", "GPS ETE", "seconds"),

            // Umgebung & Zeit
            SimVarSpec.Num("oatC", "AMBIENT TEMPERATURE", "celsius"),
            SimVarSpec.Num("ambientPressureHpa", "AMBIENT PRESSURE", "millibars"),
            SimVarSpec.Num("windDir", "AMBIENT WIND DIRECTION", "degrees"),
            SimVarSpec.Num("windKt", "AMBIENT WIND VELOCITY", "knots"),
            SimVarSpec.Num("visibilityM", "AMBIENT VISIBILITY", "meters"),
            SimVarSpec.Num("qnhHpa", "SEA LEVEL PRESSURE", "millibars"),
            SimVarSpec.Num("baroSettingHpa", "KOHLSMAN SETTING MB:1", "millibars"),
            SimVarSpec.Num("zuluTimeSec", "ZULU TIME", "seconds"),
            SimVarSpec.Num("localTimeSec", "LOCAL TIME", "seconds"),
            SimVarSpec.Num("simRate", "SIMULATION RATE", "number"),
        };

        // Triebwerke 1–4 (ausgegeben werden nur so viele, wie NUMBER OF ENGINES meldet)
        for (var i = 1; i <= MaxEngines; i++)
        {
            list.Add(SimVarSpec.Bool($"eng{i}Running", $"GENERAL ENG COMBUSTION:{i}"));
            list.Add(SimVarSpec.Num($"eng{i}N1", $"TURB ENG N1:{i}", "percent"));
            list.Add(SimVarSpec.Num($"eng{i}N2", $"TURB ENG N2:{i}", "percent"));
            list.Add(SimVarSpec.Num($"eng{i}Egt", $"ENG EXHAUST GAS TEMPERATURE:{i}", "celsius"));
            list.Add(SimVarSpec.Num($"eng{i}FuelFlowPph", $"ENG FUEL FLOW PPH:{i}", "pounds per hour"));
        }

        return list;
    }
}
