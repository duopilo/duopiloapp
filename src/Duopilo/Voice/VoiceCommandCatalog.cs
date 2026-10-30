namespace Duopilo.Voice;

/// <summary>Ein Beispielsatz für einen Sprachbefehl: Aktion und Wert (wie <see cref="VoiceIntents.Recognize"/> sie liefert), englisch und deutsch.</summary>
/// <param name="ActionId">Aktion im Flugzeugprofil, z. B. "flaps".</param>
/// <param name="Value">Erkannter Wert (Auswahl wie im Profil, Zahl als Text, "FL120"), null bei Aktionen ohne Wert.</param>
/// <param name="English">Beispiel auf Englisch.</param>
/// <param name="German">Beispiel auf Deutsch.</param>
public sealed record VoiceCommandExample(string ActionId, string? Value, string English, string German);

/// <summary>
/// Für jede Aktion jedes Profils mindestens ein Sprachbefehl – englisch und deutsch, für jede Auswahl eines Schalters.
/// Grundlage für den Abdeckungstest (alle Profile) und die Befehlsliste im README. Eine neue Aktion im Profil braucht einen
/// Eintrag hier und ein Muster in <see cref="VoiceIntents"/>, sonst schlägt der Test fehl.
/// </summary>
public static class VoiceCommandCatalog
{
    public static readonly IReadOnlyList<VoiceCommandExample> Examples =
    [
        // ---------------- FCU
        new("fcu.speed", "250", "Speed two five zero", "Geschwindigkeit zweihundertfünfzig"),
        new("fcu.speedManaged", null, "Managed speed", "Geschwindigkeit managed"),
        new("fcu.heading", "090", "Heading zero niner zero", "Kurs null neun null"),
        new("fcu.headingManaged", null, "Managed heading", "Kurs managed"),
        new("fcu.altitude", "FL120", "Flight level one two zero", "Flugfläche eins zwei null"),
        new("fcu.altitude", "5000", "Altitude five thousand", "Höhe fünftausend"),
        new("fcu.altitudePull", null, "Pull altitude", "Höhe ziehen"),
        new("fcu.altitudePush", null, "Managed descent", "Höhe drücken"),
        new("fcu.altIncrement", "100", "Altitude increment one hundred", "Höhenschritt hundert"),
        new("fcu.altIncrement", "1000", "Altitude increment one thousand", "Höhenschritt tausend"),
        new("fcu.vs", "-1000", "Vertical speed minus one thousand", "Sinkrate minus tausend"),
        new("fcu.loc", "on", "LOC on", "LOC an"),
        new("fcu.loc", "off", "Localizer off", "LOC aus"),
        new("fcu.appr", "on", "Approach on", "Anflug an"),
        new("fcu.appr", "off", "Approach off", "Anflug aus"),
        new("fcu.exped", "on", "Expedite on", "Expedite an"),
        new("fcu.exped", "off", "Expedite off", "Expedite aus"),
        new("fcu.ap1", "on", "Autopilot one on", "Autopilot eins an"),
        new("fcu.ap1", "off", "Autopilot one off", "Autopilot eins aus"),
        new("fcu.ap2", "on", "Autopilot two on", "Autopilot zwei an"),
        new("fcu.ap2", "off", "Autopilot two off", "Autopilot zwei aus"),
        new("fcu.athr", "on", "Autothrust on", "Schubautomatik an"),
        new("fcu.athr", "off", "Autothrust off", "Schubautomatik aus"),

        // ---------------- EFIS
        new("efis.baro", "1013", "QNH one zero one three", "QNH eins null eins drei"),
        new("efis.baroMode", "STD", "Baro standard", "Höhenmesser Standard"),
        new("efis.baroMode", "QNH", "Baro QNH", "QNH Modus"),
        new("efis.ls", "on", "LS on", "LS an"),
        new("efis.ls", "off", "ILS off", "LS aus"),
        new("efis.fd", "on", "Flight directors on", "Flight Director an"),
        new("efis.fd", "off", "Flight directors off", "Flight Director aus"),
        new("efis.ndMode", "ls", "ND mode ILS", "ND Modus ILS"),
        new("efis.ndMode", "vor", "ND mode VOR", "ND Modus VOR"),
        new("efis.ndMode", "nav", "ND mode NAV", "ND Modus NAV"),
        new("efis.ndMode", "arc", "ND mode ARC", "ND Modus Bogen"),
        new("efis.ndMode", "plan", "ND mode plan", "ND Modus Plan"),
        new("efis.ndRange", "10", "ND range ten", "ND Reichweite zehn"),
        new("efis.ndRange", "20", "ND range twenty", "ND Reichweite zwanzig"),
        new("efis.ndRange", "40", "ND range forty", "ND Reichweite vierzig"),
        new("efis.ndRange", "80", "ND range eighty", "ND Reichweite achtzig"),
        new("efis.ndRange", "160", "ND range one hundred sixty", "ND Reichweite hundertsechzig"),
        new("efis.ndRange", "320", "ND range three hundred twenty", "ND Reichweite dreihundertzwanzig"),

        // ---------------- Konfiguration
        new("gear.down", null, "Gear down", "Fahrwerk ausfahren"),
        new("gear.up", null, "Gear up", "Fahrwerk einfahren"),
        new("flaps", "0", "Flaps zero", "Klappen null"),
        new("flaps", "1", "Flaps one", "Klappen eins"),
        new("flaps", "2", "Flaps two", "Klappen zwei"),
        new("flaps", "3", "Flaps three", "Klappen drei"),
        new("flaps", "FULL", "Flaps full", "Klappen voll"),
        new("speedbrake", "retract", "Speedbrake retract", "Bremsklappen einfahren"),
        new("speedbrake", "half", "Speedbrake half", "Bremsklappen halb"),
        new("speedbrake", "full", "Speedbrake full", "Bremsklappen voll"),
        new("spoilers.arm", "arm", "Arm spoilers", "Spoiler scharf"),
        new("spoilers.arm", "disarm", "Disarm spoilers", "Spoiler entschärfen"),
        new("brakes.autobrake", "off", "Autobrake off", "Autobremse aus"),
        new("brakes.autobrake", "lo", "Autobrake low", "Autobremse niedrig"),
        new("brakes.autobrake", "med", "Autobrake medium", "Autobremse mittel"),
        new("brakes.autobrake", "max", "Autobrake max", "Autobremse maximum"),

        // ---------------- Licht
        new("lights.beacon", "on", "Beacon on", "Antikollisionslicht an"),
        new("lights.beacon", "off", "Beacon off", "Antikollisionslicht aus"),
        new("lights.wing", "on", "Wing lights on", "Flügelbeleuchtung an"),
        new("lights.wing", "off", "Wing lights off", "Flügelbeleuchtung aus"),
        new("lights.runwayTurnoff", "on", "Runway turnoff lights on", "Abrolllichter an"),
        new("lights.runwayTurnoff", "off", "Runway turnoff lights off", "Abrolllichter aus"),
        new("lights.landing", "on", "Landing lights on", "Landelichter an"),
        new("lights.landing", "off", "Landing lights off", "Landelichter aus"),
        new("lights.landing", "retract", "Landing lights retract", "Landelichter einfahren"),
        new("lights.nose", "to", "Takeoff lights on", "Startlicht an"),
        new("lights.nose", "taxi", "Taxi light on", "Rollscheinwerfer an"),
        new("lights.nose", "off", "Nose light off", "Rollscheinwerfer aus"),
        new("lights.strobe", "on", "Strobe on", "Blitzlichter an"),
        new("lights.strobe", "auto", "Strobe auto", "Blitzlichter auto"),
        new("lights.strobe", "off", "Strobe off", "Blitzlichter aus"),
        new("lights.nav", "on", "Nav lights on", "Positionslichter an"),
        new("lights.nav", "off", "Nav lights off", "Positionslichter aus"),
        new("lights.logo", "on", "Logo lights on", "Logolicht an"),
        new("lights.logo", "off", "Logo lights off", "Logolicht aus"),

        // ---------------- Schilder
        new("signs.seatbelts", "on", "Seat belts on", "Anschnallzeichen an"),
        new("signs.seatbelts", "off", "Seat belts off", "Anschnallzeichen aus"),
        new("signs.seatbelts", "auto", "Seat belts auto", "Anschnallzeichen auto"),
        new("signs.noSmoking", "on", "No smoking on", "Rauchverbot an"),
        new("signs.noSmoking", "auto", "No smoking auto", "Rauchverbot auto"),
        new("signs.noSmoking", "off", "No smoking off", "Rauchverbot aus"),

        // ---------------- Anti-Ice
        new("antiice.engines", "on", "Engine anti ice on", "Triebwerksenteisung an"),
        new("antiice.engines", "off", "Engine anti ice off", "Triebwerksenteisung aus"),
        new("antiice.eng1", "on", "Engine one anti ice on", "Triebwerk eins Enteisung an"),
        new("antiice.eng1", "off", "Engine one anti ice off", "Triebwerk eins Enteisung aus"),
        new("antiice.eng2", "on", "Engine two anti ice on", "Triebwerk zwei Enteisung an"),
        new("antiice.eng2", "off", "Engine two anti ice off", "Triebwerk zwei Enteisung aus"),
        new("antiice.wing", "on", "Wing anti ice on", "Flügelenteisung an"),
        new("antiice.wing", "off", "Wing anti ice off", "Flügelenteisung aus"),
        new("antiice.probeHeat", "on", "Probe heat on", "Sondenheizung an"),
        new("antiice.probeHeat", "auto", "Probe heat auto", "Sondenheizung auto"),

        // ---------------- Packs, APU, Elektrik
        new("air.pack1", "on", "Pack one on", "Pack eins an"),
        new("air.pack1", "off", "Pack one off", "Pack eins aus"),
        new("air.pack2", "on", "Pack two on", "Pack zwei an"),
        new("air.pack2", "off", "Pack two off", "Pack zwei aus"),
        new("apu.master", "on", "APU master on", "APU Master an"),
        new("apu.master", "off", "APU master off", "APU Master aus"),
        new("apu.start", null, "APU start", "APU starten"),
        new("apu.bleed", "on", "APU bleed on", "APU Bleed an"),
        new("apu.bleed", "off", "APU bleed off", "APU Bleed aus"),
        new("elec.battery", "on", "Batteries on", "Batterien an"),
        new("elec.battery", "off", "Batteries off", "Batterien aus"),
        new("elec.extPwr", "on", "External power on", "Bodenstrom an"),
        new("elec.extPwr", "off", "External power off", "Bodenstrom aus"),

        // ---------------- Transponder, TCAS, Funk
        new("xpdr.code", "7000", "Squawk seven thousand", "Squawk sieben null null null"),
        new("xpdr.mode", "stby", "Transponder standby", "Transponder Standby"),
        new("xpdr.mode", "auto", "Transponder auto", "Transponder auto"),
        new("xpdr.mode", "on", "Transponder on", "Transponder an"),
        new("xpdr.altReporting", "on", "Altitude reporting on", "Höhenübermittlung an"),
        new("xpdr.altReporting", "off", "Altitude reporting off", "Höhenübermittlung aus"),
        new("tcas.mode", "stby", "TCAS standby", "TCAS Standby"),
        new("tcas.mode", "ta", "TCAS TA only", "TCAS auf TA"),
        new("tcas.mode", "tara", "TCAS TA RA", "TCAS auf TA/RA"),
        new("tcas.traffic", "thrt", "Traffic threat", "Verkehr Bedrohung"),
        new("tcas.traffic", "all", "Traffic all", "Verkehr alle"),
        new("tcas.traffic", "abv", "Traffic above", "Verkehr oben"),
        new("tcas.traffic", "blw", "Traffic below", "Verkehr unten"),
        new("com1.standby", "121.8", "COM one standby one two one decimal eight", "COM eins Standby eins zwei eins Komma acht"),
        new("com1.swap", null, "COM one swap", "Frequenzen tauschen"),
    ];

    /// <summary>Beispiele für eine Aktion (Groß-/Kleinschreibung egal).</summary>
    public static IEnumerable<VoiceCommandExample> For(string actionId) =>
        Examples.Where(e => e.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase));
}
