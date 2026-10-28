using System.Globalization;
using Kopilot.Aircraft;

namespace Kopilot.Sim;

/// <summary>
/// Semantische Sicht auf das Flugzeug: nimmt die Werte aus den Profil-Bindings (z. B. FBW-L:-Variablen)
/// und fällt auf die generischen SimVars zurück, wenn das Profil nichts vorgibt.
/// </summary>
public sealed class AircraftSignals(SimConnectClient sim)
{
    private const double LbsToKg = 0.45359237;

    public SimConnectClient Sim { get; } = sim;

    public AircraftProfile? Profile { get; } = sim.ActiveProfile;

    public bool Has(string key) => Profile?.Bindings.ContainsKey(key) == true;

    public ProfileBinding? Binding(string key) =>
        Profile is not null && Profile.Bindings.TryGetValue(key, out var b) ? b : null;

    // ------------------------------------------------------------------ Rohzugriff

    /// <summary>Wert eines Bindings (mit Scale). Bei ARINC-429-Wörtern nur, wenn das Wort gültig ist (Normal Operation).</summary>
    public double? Raw(string key, int? engine = null)
    {
        var b = Binding(key);
        if (b is null || RawValue(b, engine) is not { } v)
        {
            return null;
        }

        if (IsArinc(b))
        {
            var word = Arinc429Word.Decode(v);
            return word.IsNormal ? word.Value * b.Scale : null;
        }

        return v * b.Scale;
    }

    /// <summary>ARINC-429-Wort eines Bindings vom Typ "arinc429" (auch ungültige Wörter, z. B. für die SSM).</summary>
    public Arinc429Word? Arinc(string key) =>
        Binding(key) is { } b && IsArinc(b) && RawValue(b, null) is { } v ? Arinc429Word.Decode(v) : null;

    public bool? Flag(string key) => Raw(key) is { } v ? Math.Abs(v) > 0.5 : null;

    public string? Text(string key)
    {
        var b = Binding(key);
        if (b is null)
        {
            return null;
        }

        if (b.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            return Sim.Str(SimConnectClient.VarKey(b.Name, b.EffectiveUnit));
        }

        return Raw(key) is { } raw ? b.Translate(raw) ?? raw.ToString(CultureInfo.InvariantCulture) : null;
    }

    public IReadOnlyList<string>? Bits(string key) =>
        Binding(key) is { } b && Raw(key) is { } raw ? b.DecodeBits(raw) : null;

    private double? RawValue(ProfileBinding b, int? engine)
    {
        var name = engine is { } i ? b.Name.Replace("{i}", i.ToString(CultureInfo.InvariantCulture)) : b.Name;
        return Sim.Num(SimConnectClient.VarKey(name, b.EffectiveUnit));
    }

    private static bool IsArinc(ProfileBinding b) => b.Type.Equals("arinc429", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Autopilot / FMA

    public string AutopilotSource => Has(BindingKeys.Ap1) || Has(BindingKeys.Ap2) ? "profile" : "generic";

    public bool? Ap1 => Flag(BindingKeys.Ap1);

    public bool? Ap2 => Flag(BindingKeys.Ap2);

    /// <summary>Irgendein Autopilot eingeschaltet. Mit Profil über AP1/AP2, sonst AUTOPILOT MASTER.</summary>
    public bool? ApEngaged
    {
        get
        {
            if (!Has(BindingKeys.Ap1) && !Has(BindingKeys.Ap2))
            {
                return Sim.Bool("apMaster");
            }

            var ap1 = Ap1;
            var ap2 = Ap2;
            return ap1 is null && ap2 is null ? null : ap1 == true || ap2 == true;
        }
    }

    public bool? FlightDirector => Has(BindingKeys.FlightDirector) ? Flag(BindingKeys.FlightDirector) : Sim.Bool("fdActive");

    /// <summary>A/THR-Status (OFF/ARMED/ACTIVE). Ohne Profil-Binding unbekannt – die generische SimVar ist bei Airlinern unzuverlässig.</summary>
    public string? AutothrustStatus => Text(BindingKeys.AthrStatus);

    public string? AutothrustMode => Text(BindingKeys.AthrMode);

    public string? FmaLateral => Text(BindingKeys.FmaLateral);

    public IReadOnlyList<string>? FmaLateralArmed => Bits(BindingKeys.FmaLateralArmed);

    /// <summary>
    /// Vertikaler FMA-Modus. "ALT CRZ" kennt die alte FBW-Variable nicht – dann wie das FBW-PFD aus dem
    /// FMGC Discrete Word 1 ableiten (Bit 26 Strich-Anzeige, Bit 19 ALT, ohne gültige Höhenbeschränkung).
    /// </summary>
    public string? FmaVertical
    {
        get
        {
            var text = Text(BindingKeys.FmaVertical);
            if (!string.IsNullOrEmpty(text) || Arinc(BindingKeys.FmgcDiscreteWord1) is not { IsNormal: true } dw1)
            {
                return text;
            }

            var constraintValid = Arinc(BindingKeys.FmAltitudeConstraint) is { IsNormal: true };
            return dw1.Bit(26) && (!dw1.Bit(19) || !constraintValid) ? "ALT CRZ" : text;
        }
    }

    public IReadOnlyList<string>? FmaVerticalArmed => Bits(BindingKeys.FmaVerticalArmed);

    public string? Autobrake => Text(BindingKeys.Autobrake);

    public bool? MasterWarning => Flag(BindingKeys.MasterWarning);

    public bool? MasterCaution => Flag(BindingKeys.MasterCaution);

    /// <summary>FMS-Meldung "T/D REACHED" (nur mit Profil-Binding).</summary>
    public bool? TdReached => Flag(BindingKeys.TdReached);

    // ------------------------------------------------------------------ Geschwindigkeiten (immer Knoten)

    /// <summary>Ziel-Speed in kt: managed (FMS), wenn aktiv und bekannt, sonst die im FCU gewählte. Mach-Ziele werden in kt umgerechnet.</summary>
    public double? SpeedTargetKt
    {
        get
        {
            if (SpeedManaged == true)
            {
                return Raw(BindingKeys.SpeedTarget) is { } managed && managed > 30 ? managed : null;
            }

            return SelectedSpeedKt;
        }
    }

    public bool? SpeedManaged => Flag(BindingKeys.SpeedManaged);

    /// <summary>true, wenn das FCU eine Machzahl zeigt (für die Anzeige "aus Mach umgerechnet").</summary>
    public bool SelectedSpeedIsMach =>
        Has(BindingKeys.MachMode) ? Flag(BindingKeys.MachMode) == true : Sim.Bool("apMachHold") == true;

    /// <summary>Am FCU gewählte Geschwindigkeit in kt. Ein Mach-Ziel wird über den Umgebungsdruck in Knoten (CAS) umgerechnet.</summary>
    public double? SelectedSpeedKt
    {
        get
        {
            if (SelectedSpeedIsMach)
            {
                var mach = Has(BindingKeys.SelectedSpeed) ? Raw(BindingKeys.SelectedSpeed) : Sim.Num("apMachVar");

                // Manche Anzeigen liefern "78" statt "0.78"
                if (mach is > 1 and < 100)
                {
                    mach /= 100;
                }

                return AirspeedMath.MachToCasKt(mach, Sim.Num("ambientPressureHpa"));
            }

            if (Has(BindingKeys.SelectedSpeed))
            {
                return Raw(BindingKeys.SelectedSpeed) is { } v && v > 30 ? v : null;
            }

            return Sim.Num("apSpd") is { } generic && generic > 30 ? generic : null;
        }
    }

    public double? VlsKt => Raw(BindingKeys.Vls) is { } v && v > 30 ? v : null;

    public double? V1Kt => Raw(BindingKeys.V1) is { } v && v > 30 ? v : null;

    public double? VrKt => Raw(BindingKeys.Vr) is { } v && v > 30 ? v : null;

    public double? V2Kt => Raw(BindingKeys.V2) is { } v && v > 30 ? v : null;

    /// <summary>FLEX-Temperatur in °C, null wenn keine eingetragen ist.</summary>
    public double? FlexTempC => Raw(BindingKeys.FlexTemp) is { } v && Math.Abs(v) > 0.5 ? v : null;

    // ------------------------------------------------------------------ FCU

    /// <summary>Am FCU gewählte Höhe in ft.</summary>
    public double? SelectedAltitudeFt =>
        Has(BindingKeys.SelectedAltitude) ? Raw(BindingKeys.SelectedAltitude) : Sim.Num("apAlt");

    public string SelectedAltitudeSource => Has(BindingKeys.SelectedAltitude) ? "profile" : "generic";

    /// <summary>Am FCU gewählter Kurs. Null, wenn das Fenster Striche zeigt (managed NAV).</summary>
    public double? SelectedHeadingDeg
    {
        get
        {
            if (!Has(BindingKeys.SelectedHeading))
            {
                return Sim.Num("apHdg");
            }

            return Flag(BindingKeys.SelectedHeadingDashes) == true ? null : Raw(BindingKeys.SelectedHeading) is { } v && v >= 0 ? v : null;
        }
    }

    // ------------------------------------------------------------------ Konfiguration

    public bool? ParkingBrake => Has(BindingKeys.ParkingBrake) ? Flag(BindingKeys.ParkingBrake) : Sim.Bool("parkingBrake");

    public bool? SpoilersArmed => Has(BindingKeys.SpoilersArmed) ? Flag(BindingKeys.SpoilersArmed) : Sim.Bool("spoilersArmed");

    /// <summary>Klappenstellung im Klartext aus dem Profil (z. B. "1+F", "FULL"), sonst null.</summary>
    public string? FlapsLabel => Has(BindingKeys.Flaps) ? Text(BindingKeys.Flaps) : null;

    /// <summary>Klappenstellung zur Anzeige: Klartext aus dem Profil oder der generische Hebel-Index.</summary>
    public string? FlapsDisplay => FlapsLabel ?? (Sim.Num("flapsIndex") is { } i ? Math.Round(i).ToString(CultureInfo.InvariantCulture) : null);

    // ------------------------------------------------------------------ Triebwerke

    public string FuelFlowSource => Has(BindingKeys.FuelFlowKgh) ? "profile" : "generic";

    public double? FuelFlowKgh(int engine) =>
        Has(BindingKeys.FuelFlowKgh)
            ? Raw(BindingKeys.FuelFlowKgh, engine)
            : Sim.Num($"eng{engine}FuelFlowPph") * LbsToKg;

    public string EgtSource => Has(BindingKeys.EgtC) ? "profile" : "generic";

    public double? EgtC(int engine) => Has(BindingKeys.EgtC) ? Raw(BindingKeys.EgtC, engine) : Sim.Num($"eng{engine}Egt");

    // ------------------------------------------------------------------ Elektrik

    /// <summary>Elektrik-Werte aus dem Profil (Spannung, Frequenz, Busse versorgt).</summary>
    public IReadOnlyList<(string Key, string? Description, double? Value, string? Text)> Electrical()
    {
        if (Profile is null || Profile.Electrical.Count == 0)
        {
            return [];
        }

        return Profile.Electrical.Select(v =>
        {
            var unit = string.IsNullOrWhiteSpace(v.Unit) ? "number" : v.Unit;
            var value = Sim.Num(SimConnectClient.VarKey(v.Name, unit));
            return (v.Key, v.Description, value, v.Translate(value));
        }).ToList();
    }

    // ------------------------------------------------------------------ FMS

    /// <summary>Distanz (nm) und Peilung (°) zum aktiven FMS-Wegpunkt, falls das Profil sie kennt.</summary>
    public (double DistanceNm, double BearingDeg)? FmsToWaypoint =>
        Raw(BindingKeys.FmsToWptDistance) is { } d && d > 0 && Raw(BindingKeys.FmsToWptBearing) is { } b ? (d, b) : null;

    /// <summary>Eingabe für die Zuordnung des FMS-Wegpunkts zum SimBrief-Plan, null ohne FMS-Daten.</summary>
    public Kopilot.FlightPlan.FmsWaypointInput? FmsWaypoint(double lat, double lon)
    {
        if (FmsToWaypoint is not { } fms)
        {
            return null;
        }

        var variation = 0.0;
        if (Sim.Num("hdgTrue") is { } t && Sim.Num("hdgMag") is { } m)
        {
            variation = ((t - m) % 360 + 540) % 360 - 180;
        }

        return new Kopilot.FlightPlan.FmsWaypointInput(lat, lon, fms.DistanceNm, fms.BearingDeg, variation);
    }

    // ------------------------------------------------------------------ ECAM

    /// <summary>Rohcodes der EWD-Zeilen (0 = leer).</summary>
    public IReadOnlyList<long> EcamCodes(bool left)
    {
        var ecam = Profile?.Ecam;
        var pattern = left ? ecam?.LeftLines : ecam?.RightLines;
        if (ecam is null || string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var codes = new List<long>();
        for (var n = 1; n <= ecam.Lines; n++)
        {
            var name = pattern.Replace("{n}", n.ToString(CultureInfo.InvariantCulture));
            if (Sim.Num(SimConnectClient.VarKey(name, "number")) is { } v && v > 0.5)
            {
                codes.Add((long)Math.Round(v));
            }
        }

        return codes;
    }
}
