using Duopilo.Sim;

namespace Duopilo.Tests;

/// <summary>
/// A/THR-Modus aus dem FCU ATS FMA Discrete Word (Headwind A330: L:A32NX_AUTOTHRUST_MODE meldet nur A.FLOOR). Bits und Reihenfolge
/// wie im FBW-PFD (FMA.tsx, getA1A2CellText).
/// </summary>
public sealed class AthrFmaWordTests
{
    private static Arinc429Word Word(params int[] bits) =>
        new(Arinc429Ssm.NormalOperation, bits.Aggregate(0f, (v, b) => v + (1 << (b - 1))));

    [Theory]
    [InlineData(19, "SPEED")]
    [InlineData(20, "MACH")]
    [InlineData(14, "THR CLB")]
    [InlineData(16, "THR IDLE")]
    [InlineData(11, "MAN TOGA")]
    [InlineData(13, "MAN FLX")]
    [InlineData(29, "MAN DTO")]
    [InlineData(17, "A.FLOOR")]
    [InlineData(18, "TOGA LK")]
    public void Modus_aus_einem_Bit(int bit, string mode) =>
        Assert.Equal(mode, AircraftSignals.AutothrustModeFromFcu(Word(bit), engaged: true, active: true));

    [Fact]
    public void Kein_Bit_kein_Modus() =>
        Assert.Equal("", AircraftSignals.AutothrustModeFromFcu(Word(), engaged: false, active: false));

    /// <summary>MCT und THR: nur engaged (Schubhebel nicht in CL) = MAN …, engaged und active = THR …</summary>
    [Fact]
    public void MCT_und_LVR_haengen_von_engaged_und_active_ab()
    {
        Assert.Equal("MAN MCT", AircraftSignals.AutothrustModeFromFcu(Word(12), engaged: true, active: false));
        Assert.Equal("THR MCT", AircraftSignals.AutothrustModeFromFcu(Word(12), engaged: true, active: true));
        Assert.Equal("MAN THR", AircraftSignals.AutothrustModeFromFcu(Word(15), engaged: true, active: false));
        Assert.Equal("THR LVR", AircraftSignals.AutothrustModeFromFcu(Word(15), engaged: true, active: true));

        // Unbekannt (kein ATS-Wort, kein Status): wie active
        Assert.Equal("THR MCT", AircraftSignals.AutothrustModeFromFcu(Word(12), engaged: null, active: null));
    }

    /// <summary>Wie im PFD gewinnt der erste passende Modus (z. B. MAN FLX vor SPEED).</summary>
    [Fact]
    public void Reihenfolge_wie_im_PFD()
    {
        Assert.Equal("MAN TOGA", AircraftSignals.AutothrustModeFromFcu(Word(11, 13, 19), engaged: true, active: true));
        Assert.Equal("A.FLOOR", AircraftSignals.AutothrustModeFromFcu(Word(17, 19), engaged: true, active: true));
        Assert.Equal("SPEED", AircraftSignals.AutothrustModeFromFcu(Word(19, 14), engaged: true, active: true));
    }
}
