namespace Duopilo.Sim;

/// <summary>Zustand (Sign/Status Matrix) eines ARINC-429-Datenworts.</summary>
public enum Arinc429Ssm
{
    FailureWarning = 0,
    NoComputedData = 1,
    FunctionalTest = 2,
    NormalOperation = 3,
}

/// <summary>
/// FlyByWire legt ARINC-429-Wörter so in einer L:-Variable ab: SSM · 2^32 + die 32 Bit eines float32-Werts.
/// (Gleiche Kodierung wie Arinc429Word.fromSimVarValue im FBW-Code.)
/// </summary>
public readonly record struct Arinc429Word(Arinc429Ssm Ssm, float Value)
{
    public bool IsNormal => Ssm == Arinc429Ssm.NormalOperation;

    public static Arinc429Word Decode(double raw)
    {
        if (!double.IsFinite(raw) || raw < 0)
        {
            return new Arinc429Word(Arinc429Ssm.FailureWarning, 0);
        }

        var word = (ulong)raw;
        var ssm = (Arinc429Ssm)((word >> 32) & 0b11);
        var value = BitConverter.Int32BitsToSingle(unchecked((int)(word & 0xFFFF_FFFF)));
        return new Arinc429Word(ssm, value);
    }

    public static double Encode(Arinc429Ssm ssm, float value) =>
        (double)(((ulong)ssm << 32) | unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    /// <summary>Bit eines diskreten Worts, Zählung wie in der FBW-Doku (Bit 11 = 1 &lt;&lt; 10).</summary>
    public bool Bit(int bit) => bit is >= 1 and <= 31 && (((long)Value >> (bit - 1)) & 1) != 0;
}

/// <summary>Umrechnungen rund um Fahrt und Luftdruck.</summary>
public static class AirspeedMath
{
    private const double SeaLevelPressureHpa = 1013.25;
    private const double SeaLevelSpeedOfSoundKt = 661.4786;

    /// <summary>
    /// Calibrated Airspeed in Knoten für eine Machzahl beim gegebenen statischen Druck (Unterschall).
    /// So lässt sich ein Mach-Ziel des FCU als Knoten-Ziel ausgeben.
    /// </summary>
    public static double? MachToCasKt(double? mach, double? staticPressureHpa)
    {
        if (mach is not { } m || staticPressureHpa is not { } p || m <= 0 || m >= 1 || p <= 0)
        {
            return null;
        }

        var impactPressure = p * (Math.Pow(1 + 0.2 * m * m, 3.5) - 1);
        var cas = SeaLevelSpeedOfSoundKt * Math.Sqrt(5 * (Math.Pow(impactPressure / SeaLevelPressureHpa + 1, 2.0 / 7.0) - 1));
        return double.IsFinite(cas) ? cas : null;
    }
}
