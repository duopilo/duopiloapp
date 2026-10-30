using Duopilo.Sim;

namespace Duopilo.Ecam;

/// <summary>Aktueller Inhalt des unteren EWD (links: Warnungen/Verfahren, rechts: Memos).</summary>
/// <param name="Available">Das Profil beschreibt ECAM-Zeilen.</param>
/// <param name="TableStatus">Ladezustand der Meldungstabelle.</param>
public sealed record EcamState(bool Available, string? TableStatus, IReadOnlyList<EcamLine> Left, IReadOnlyList<EcamLine> Right)
{
    public static readonly EcamState None = new(false, null, [], []);

    /// <summary>Titelzeilen links (z. B. "AUTO FLT AP OFF"), in Anzeigereihenfolge.</summary>
    public IReadOnlyList<string> Titles => Left.Where(l => l.IsTitle).Select(l => l.Text).ToList();
}

public static class EcamReader
{
    public static EcamState Read(AircraftSignals signals, EcamCatalog catalog)
    {
        var ecam = signals.Profile?.Ecam;
        if (ecam is null)
        {
            return EcamState.None;
        }

        var table = catalog.TryGet(ecam.Format);
        return new EcamState(
            true,
            catalog.StatusFor(ecam.Format),
            Decode(signals.EcamCodes(left: true), table),
            Decode(signals.EcamCodes(left: false), table));
    }

    private static IReadOnlyList<EcamLine> Decode(IReadOnlyList<long> codes, EcamTable? table) =>
        codes.Select(c => table?.Decode(c) ?? new EcamLine(c, $"Code {c:D9}", false, false)).ToList();
}
