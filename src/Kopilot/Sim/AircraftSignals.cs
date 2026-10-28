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

    public double? Raw(string key, int? engine = null)
    {
        var b = Binding(key);
        if (b is null)
        {
            return null;
        }

        var name = engine is { } i ? b.Name.Replace("{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)) : b.Name;
        return Sim.Num(SimConnectClient.VarKey(name, b.EffectiveUnit)) is { } v ? v * b.Scale : null;
    }

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

        return Raw(key) is { } raw ? b.Translate(raw) ?? raw.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public IReadOnlyList<string>? Bits(string key) =>
        Binding(key) is { } b && Raw(key) is { } raw ? b.DecodeBits(raw) : null;

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

    public string? FmaVertical => Text(BindingKeys.FmaVertical);

    public IReadOnlyList<string>? FmaVerticalArmed => Bits(BindingKeys.FmaVerticalArmed);

    public string? Autobrake => Text(BindingKeys.Autobrake);

    public bool? MasterWarning => Flag(BindingKeys.MasterWarning);

    public bool? MasterCaution => Flag(BindingKeys.MasterCaution);

    /// <summary>Ziel-Speed in kt: managed (FMS), wenn aktiv und bekannt, sonst die im FCU gewählte. Null bei Mach-Ziel oder unbekannt.</summary>
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

    /// <summary>Am FCU gewählte Geschwindigkeit in kt. Null, wenn das FCU Mach zeigt.</summary>
    public double? SelectedSpeedKt
    {
        get
        {
            if (Flag(BindingKeys.MachMode) == true)
            {
                return null;
            }

            if (Has(BindingKeys.SelectedSpeed))
            {
                return Raw(BindingKeys.SelectedSpeed) is { } v && v > 30 ? v : null;
            }

            return Sim.Num("apSpd") is { } generic && generic > 30 ? generic : null;
        }
    }

    /// <summary>Am FCU gewählte Höhe in ft.</summary>
    public double? SelectedAltitudeFt =>
        Has(BindingKeys.SelectedAltitude) ? Raw(BindingKeys.SelectedAltitude) : Sim.Num("apAlt");

    public string SelectedAltitudeSource => Has(BindingKeys.SelectedAltitude) ? "profile" : "generic";

    public double? VlsKt => Raw(BindingKeys.Vls) is { } v && v > 30 ? v : null;

    // ------------------------------------------------------------------ Triebwerke

    public string FuelFlowSource => Has(BindingKeys.FuelFlowKgh) ? "profile" : "generic";

    public double? FuelFlowKgh(int engine) =>
        Has(BindingKeys.FuelFlowKgh)
            ? Raw(BindingKeys.FuelFlowKgh, engine)
            : Sim.Num($"eng{engine}FuelFlowPph") * LbsToKg;

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
            var name = pattern.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Sim.Num(SimConnectClient.VarKey(name, "number")) is { } v && v > 0.5)
            {
                codes.Add((long)Math.Round(v));
            }
        }

        return codes;
    }
}
