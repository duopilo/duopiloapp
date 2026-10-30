using System.Globalization;
using System.Text.Json.Serialization;

namespace Kopilot.Aircraft;

/// <summary>
/// Flugzeugprofil: sagt Kopilot, woher flugzeugspezifische Werte kommen (meist L:-Variablen),
/// wie das ECAM gelesen wird und welche Grenzwerte gelten.
/// </summary>
public sealed class AircraftProfile
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Erkennung: Eine Regel passt, wenn ALLE ihre Texte im Pfad bzw. Titel vorkommen. Das Profil passt, wenn EINE Regel passt.
    /// Pfad-Treffer (aircraft.cfg-Pfad aus SimConnect) gehen vor Titel-Treffern.
    /// </summary>
    public List<ProfileMatch> Match { get; set; } = [];

    /// <summary>Altform: passt, wenn der TITLE einen dieser Texte enthält.</summary>
    public List<string> TitleContains { get; set; } = [];

    public string? Notes { get; set; }

    /// <summary>Semantische Werte (z. B. "ap1", "athrStatus", "fmaVertical") → Quelle. Siehe <see cref="BindingKeys"/>.</summary>
    public Dictionary<string, ProfileBinding> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public EcamConfig? Ecam { get; set; }

    public AircraftLimits Limits { get; set; } = new();

    /// <summary>Freie Zusatzvariablen, die get_aircraft_profile roh ausgibt.</summary>
    public List<ProfileVariable> Variables { get; set; } = [];

    /// <summary>Elektrik-Werte für get_systems (z. B. Generatorspannung, Busse versorgt). Leer = kein Elektrik-Block.</summary>
    public List<ProfileVariable> Electrical { get; set; } = [];

    /// <summary>Treibstoffsystem für get_systems (z. B. Pumpenschalter, Pumpe läuft, Transferventile). Leer = keine Pumpenliste.</summary>
    public List<ProfileVariable> Fuel { get; set; } = [];

    /// <summary>Erlaubte Eingaben (Eingabe-Modul). Nur was hier steht, kann Kopilot im Cockpit bedienen.</summary>
    public List<Kopilot.Input.ProfileAction> Actions { get; set; } = [];

    /// <summary>Wie Kopilot nach Fahrwerk und Klappen nachliest, ob die Stellung erreicht ist. Fehlt = Standard-SimVars.</summary>
    public VerificationConfig? Verification { get; set; }

    [JsonIgnore]
    public string? SourceFile { get; set; }

    /// <summary>2 = Pfad-Treffer, 1 = Titel-Treffer, 0 = passt nicht.</summary>
    public int MatchScore(string? title, string? path)
    {
        var best = 0;
        foreach (var rule in Match)
        {
            var hasPath = rule.Path.Count > 0;
            var hasTitle = rule.Title.Count > 0;
            if (!hasPath && !hasTitle)
            {
                continue;
            }

            var pathOk = !hasPath || (path is not null && rule.Path.All(t => path.Contains(t, StringComparison.OrdinalIgnoreCase)));
            var titleOk = !hasTitle || (title is not null && rule.Title.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (pathOk && titleOk)
            {
                best = Math.Max(best, hasPath ? 2 : 1);
            }
        }

        if (best == 0 && !string.IsNullOrWhiteSpace(title)
            && TitleContains.Any(t => !string.IsNullOrWhiteSpace(t) && title.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            best = 1;
        }

        return best;
    }
}

public sealed class ProfileMatch
{
    public List<string> Path { get; set; } = [];

    public List<string> Title { get; set; } = [];
}

/// <summary>Quelle für einen semantischen Wert.</summary>
public sealed class ProfileBinding
{
    /// <summary>SimVar oder L:-Variable, z. B. "L:A32NX_AUTOPILOT_1_ACTIVE". "{i}" wird bei Triebwerkswerten ersetzt.</summary>
    public string Name { get; set; } = "";

    public string Unit { get; set; } = "number";

    /// <summary>number | bool | enum | bitmask | string | arinc429 (FBW-Datenwort: Wert nur bei Normal Operation)</summary>
    public string Type { get; set; } = "number";

    /// <summary>Umrechnungsfaktor auf die Kopilot-Einheit (z. B. lbs/h → kg/h = 0.45359237).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>enum: Rohwert → Text. bitmask: Bitnummer → Text.</summary>
    public Dictionary<string, string>? Values { get; set; }

    /// <summary>Schnell abonnieren (für Monitoring). Standard je nach Schlüssel.</summary>
    public bool? Fast { get; set; }

    /// <summary>false = Name stammt aus Doku/Annahme und ist im Sim noch nicht geprüft.</summary>
    public bool Verified { get; set; } = true;

    public string? Description { get; set; }

    public string EffectiveUnit => Type.Equals("string", StringComparison.OrdinalIgnoreCase) ? "string" : Unit;

    public string? Translate(double raw)
    {
        if (Values is null)
        {
            return null;
        }

        var key = Math.Round(raw).ToString(CultureInfo.InvariantCulture);
        return Values.TryGetValue(key, out var text) ? text : null;
    }

    public IReadOnlyList<string> DecodeBits(double raw)
    {
        var bits = (long)Math.Round(raw);
        var result = new List<string>();
        if (Values is null)
        {
            return result;
        }

        foreach (var (bit, text) in Values.OrderBy(v => int.TryParse(v.Key, out var b) ? b : 99))
        {
            if (int.TryParse(bit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b is >= 0 and < 63 && (bits & (1L << b)) != 0)
            {
                result.Add(text);
            }
        }

        return result;
    }
}

/// <summary>Wie das ECAM gelesen wird.</summary>
public sealed class EcamConfig
{
    /// <summary>Codeformat/Tabelle: "fbw-a32nx" (FlyByWire) oder "hdw-a339x" (Headwind A330-900neo).</summary>
    public string Format { get; set; } = "fbw-a32nx";

    /// <summary>Linker unterer EWD-Bereich (Warnungen/Verfahren), "{n}" = Zeilennummer.</summary>
    public string LeftLines { get; set; } = "";

    /// <summary>Rechter unterer EWD-Bereich (Memos), "{n}" = Zeilennummer.</summary>
    public string RightLines { get; set; } = "";

    public int Lines { get; set; } = 7;
}

/// <summary>Grenzwerte für das Monitoring. Fehlende Werte = Regel nutzt Standard bzw. ist aus.</summary>
public sealed class AircraftLimits
{
    public double? VmoKt { get; set; }

    /// <summary>Höchstgeschwindigkeit mit ausgefahrenem Fahrwerk (VLE).</summary>
    public double? VleKt { get; set; }

    /// <summary>Höchstgeschwindigkeit zum Ausfahren des Fahrwerks (VLO). Fehlt = VLE.</summary>
    public double? VloKt { get; set; }

    /// <summary>VFE je Klappenstellung: Schlüssel ist der Klartext der Stellung (z. B. "1+F", "FULL") oder der Hebel-Index ("1", "2", …).</summary>
    public Dictionary<string, double> VfeKt { get; set; } = [];

    /// <summary>Klappenstellungen, die als Landekonfiguration gelten (z. B. "3", "FULL"). Leer = Klappen nicht prüfen.</summary>
    public List<string> LandingFlaps { get; set; } = [];

    /// <summary>Speed-Limit unterhalb FL100.</summary>
    public double SpeedLimitBelowFl100Kt { get; set; } = 250;

    /// <summary>
    /// Höchste Höhe (Druckhöhe, ft), bis zu der das Fahrwerk ausgefahren werden darf (A320/A330: 25 000 ft).
    /// Fehlt = gear.down in der Luft nur mit Rückfrage.
    /// </summary>
    public double? MaxGearAltitudeFt { get; set; }

    /// <summary>
    /// Klappenstellungen (Klartext wie in der Klappen-Bindung, z. B. "FULL"), in denen die Speedbrake nicht ausgefahren werden darf.
    /// Fehlt = Speedbrake bei ausgefahrenen Klappen nur mit Rückfrage. Leere Liste = keine Einschränkung.
    /// </summary>
    public List<string>? SpeedbrakeInhibitFlaps { get; set; }

    /// <summary>
    /// Woher die MSA für die Prüfung der FCU-Höhe kommt: "ofp" = MORA aus dem SimBrief-Navlog (aktueller Abschnitt der Route).
    /// Fehlt = FCU-Höhe nur mit Rückfrage.
    /// </summary>
    public string? MsaSource { get; set; }

    /// <summary>Bekannte MSA-Quellen.</summary>
    public static readonly IReadOnlyList<string> MsaSources = ["ofp"];

    /// <summary>VFE für die aktuelle Stellung: zuerst nach Klartext (aus dem Profil), sonst nach Hebel-Index. Eingefahren = null.</summary>
    public double? VfeFor(double? flapsIndex, string? flapsLabel = null)
    {
        if (flapsLabel is not null)
        {
            return flapsLabel is "0" or "" ? null : Lookup(flapsLabel);
        }

        if (flapsIndex is not { } f || f < 0.5)
        {
            return null;
        }

        return Lookup(Math.Round(f).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>VFE nach Stellung, Groß-/Kleinschreibung egal ("Full" = "FULL").</summary>
    private double? Lookup(string key)
    {
        if (VfeKt is null)
        {
            return null;
        }

        foreach (var (k, v) in VfeKt)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    public bool IsLandingFlaps(double? flapsIndex, string? flapsLabel)
    {
        var key = flapsLabel ?? (flapsIndex is { } f ? Math.Round(f).ToString(CultureInfo.InvariantCulture) : null);
        return key is not null && LandingFlaps.Contains(key, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Nachlesen nach Fahrwerk/Klappen (Eingabe-Modul): Erst wenn die Stellung erreicht ist, sagt Kopilot "Gear down, three green"
/// bzw. "Flaps two" – sonst nach der Zeitgrenze "Gear disagree" bzw. "Flaps disagree".
/// </summary>
public sealed class VerificationConfig
{
    public GearVerification? Gear { get; set; }

    public FlapsVerification? Flaps { get; set; }
}

/// <summary>Eine Variable, die beim Nachlesen gelesen wird.</summary>
public sealed class VerificationVariable
{
    public string Name { get; set; } = "";

    public string Unit { get; set; } = "number";

    /// <summary>Name für Log und Meldung, z. B. "nose".</summary>
    public string? Label { get; set; }
}

/// <summary>Fahrwerk: jedes Bein einzeln. Unten verriegelt ab <see cref="DownAt"/>, oben ab <see cref="UpAt"/>.</summary>
public sealed class GearVerification
{
    public List<VerificationVariable> Legs { get; set; } = [];

    public double DownAt { get; set; } = 99;

    public double UpAt { get; set; } = 1;

    public int TimeoutSec { get; set; } = 20;

    /// <summary>MSFS-Standard: Bug- und Hauptfahrwerk in Prozent.</summary>
    public static GearVerification Default() => new()
    {
        Legs =
        [
            new VerificationVariable { Name = "GEAR CENTER POSITION", Unit = "percent", Label = "nose" },
            new VerificationVariable { Name = "GEAR LEFT POSITION", Unit = "percent", Label = "left" },
            new VerificationVariable { Name = "GEAR RIGHT POSITION", Unit = "percent", Label = "right" },
        ],
    };
}

/// <summary>
/// Klappen: Die Stellung gilt als erreicht, wenn sich die Flächen bewegt haben und dann <see cref="SettleSec"/> lang stillstehen
/// (Änderung höchstens <see cref="Tolerance"/>). Beim Einfahren auf 0 reicht, dass alle Flächen eingefahren sind.
/// </summary>
public sealed class FlapsVerification
{
    public List<VerificationVariable> Surfaces { get; set; } = [];

    public double SettleSec { get; set; } = 2;

    public double Tolerance { get; set; } = 0.5;

    public int TimeoutSec { get; set; } = 40;

    /// <summary>MSFS-Standard: Hinterkantenklappen und Vorflügel links in Prozent.</summary>
    public static FlapsVerification Default() => new()
    {
        Surfaces =
        [
            new VerificationVariable { Name = "TRAILING EDGE FLAPS LEFT PERCENT", Unit = "percent", Label = "flaps" },
            new VerificationVariable { Name = "LEADING EDGE FLAPS LEFT PERCENT", Unit = "percent", Label = "slats" },
        ],
    };
}

public sealed class ProfileVariable
{
    /// <summary>Eigener Schlüssel, z. B. "fmaLateral".</summary>
    public string Key { get; set; } = "";

    /// <summary>SimConnect-Name, z. B. "L:INI_FMA_LATERAL" (Beispiel).</summary>
    public string Name { get; set; } = "";

    public string Unit { get; set; } = "number";

    public string? Description { get; set; }

    /// <summary>Optionale Übersetzung Rohwert → Text, z. B. { "1": "HDG", "2": "NAV" }.</summary>
    public Dictionary<string, string>? Values { get; set; }

    public string? Translate(object? raw)
    {
        if (Values is null || raw is not double d)
        {
            return null;
        }

        var key = Math.Round(d).ToString(CultureInfo.InvariantCulture);
        return Values.TryGetValue(key, out var text) ? text : null;
    }
}

/// <summary>Bekannte semantische Schlüssel, die Tools und Monitoring auswerten.</summary>
public static class BindingKeys
{
    public const string Ap1 = "ap1";
    public const string Ap2 = "ap2";
    public const string FlightDirector = "flightDirector";
    public const string AthrStatus = "athrStatus";
    public const string AthrMode = "athrMode";
    public const string FmaLateral = "fmaLateral";
    public const string FmaLateralArmed = "fmaLateralArmed";
    public const string FmaVertical = "fmaVertical";
    public const string FmaVerticalArmed = "fmaVerticalArmed";
    public const string Autobrake = "autobrake";
    public const string MasterWarning = "masterWarning";
    public const string MasterCaution = "masterCaution";
    public const string SpeedTarget = "speedTarget";
    public const string SpeedManaged = "speedManaged";
    public const string SelectedSpeed = "selectedSpeed";
    public const string MachMode = "machMode";
    public const string SelectedAltitude = "selectedAltitude";
    public const string Vls = "vls";
    public const string FuelFlowKgh = "fuelFlowKgh";
    public const string EgtC = "egtC";
    public const string ParkingBrake = "parkingBrake";
    public const string SpoilersArmed = "spoilersArmed";
    public const string SelectedHeading = "selectedHeading";
    public const string SelectedHeadingDashes = "selectedHeadingDashes";
    public const string Flaps = "flaps";
    public const string V1 = "v1";
    public const string Vr = "vr";
    public const string V2 = "v2";
    public const string FlexTemp = "flexTemp";
    public const string TdReached = "tdReached";
    public const string FmgcDiscreteWord1 = "fmgcDiscreteWord1";
    public const string FmAltitudeConstraint = "fmAltitudeConstraint";
    public const string FmsToWptDistance = "fmsToWptDistance";
    public const string FmsToWptBearing = "fmsToWptBearing";
    public const string GroundSpoilersOut = "groundSpoilersOut";
    public const string DecelLight = "decelLight";
    public const string TransitionAltitude = "transAlt";
    public const string TransitionLevel = "transLevel";
    public const string DecisionHeight = "decisionHeight";
    public const string MinimumDescentAltitude = "minimumDescentAltitude";
    public const string ApuMaster = "apuMaster";
    public const string ApuStart = "apuStart";
    public const string ApuAvailable = "apuAvailable";
    public const string ApuN = "apuN";
    public const string ApuEgt = "apuEgt";
    public const string ApuBleed = "apuBleed";
    public const string ApuFlapOpen = "apuFlapOpen";

    /// <summary>Alle bekannten Schlüssel (für get_aircraft_profile).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Ap1, Ap2, FlightDirector, AthrStatus, AthrMode, FmaLateral, FmaLateralArmed, FmaVertical, FmaVerticalArmed, Autobrake,
        MasterWarning, MasterCaution, SpeedTarget, SpeedManaged, SelectedSpeed, MachMode, SelectedAltitude, Vls, FuelFlowKgh,
        EgtC, ParkingBrake, SpoilersArmed, SelectedHeading, SelectedHeadingDashes, Flaps, V1, Vr, V2, FlexTemp, TdReached,
        FmgcDiscreteWord1, FmAltitudeConstraint, FmsToWptDistance, FmsToWptBearing, GroundSpoilersOut, DecelLight,
        TransitionAltitude, TransitionLevel, DecisionHeight, MinimumDescentAltitude,
        ApuMaster, ApuStart, ApuAvailable, ApuN, ApuEgt, ApuBleed, ApuFlapOpen,
    ];

    /// <summary>Schlüssel, die standardmäßig schnell abonniert werden (Monitoring).</summary>
    public static readonly HashSet<string> FastByDefault = new(StringComparer.OrdinalIgnoreCase)
    {
        Ap1, Ap2, AthrStatus, FmaLateral, FmaVertical, MasterWarning, MasterCaution, SpeedTarget, SpeedManaged,
        SelectedSpeed, MachMode, SelectedAltitude, Vls, FmaLateralArmed, FmaVerticalArmed, AthrMode, Flaps, SpoilersArmed,
        FmgcDiscreteWord1, TdReached, GroundSpoilersOut, DecelLight, V1, Vr,
    };
}
