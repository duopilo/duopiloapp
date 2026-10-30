namespace Duopilo.Atc.Airspace;

/// <summary>FIR aus VAT-Spy: ICAO-Kennung, Name ("Gander", "Montreal") und Grenzen (leer, wenn VAT-Spy nur den Namen kennt).</summary>
public sealed class AirspaceFir(string id, string? name, IReadOnlyList<GeoPolygon> polygons)
{
    public string Id { get; } = id;

    /// <summary>Name laut VATSpy.dat, meist nur die Stadt ("Minneapolis").</summary>
    public string? Name { get; } = name;

    public IReadOnlyList<GeoPolygon> Polygons { get; } = polygons;

    public double Area { get; } = polygons.Sum(p => p.Area);

    public bool HasBoundary => Polygons.Count > 0;

    public bool Contains(double lat, double lon)
    {
        foreach (var polygon in Polygons)
        {
            if (polygon.Contains(lat, lon))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Streckensektor (VATSIM-Sektordaten): Luftraum einer Kontrollposition mit Höhenband und Frequenz. <see cref="Fir"/> ist
/// die FIR bzw. Kontrollstelle, zu der die Position gehört ("CZUL", "EDYY" für Maastricht), <see cref="Position"/> die
/// Kennung der Position im Datensatz ("MC").
/// </summary>
public sealed class AirspaceSector(string fir, string id, string position, string name, double frequencyMhz, int minFl, int maxFl, GeoRing ring)
{
    public string Fir { get; } = fir;

    public string Id { get; } = id;

    public string Position { get; } = position;

    /// <summary>Rufname der Station laut Datensatz ("Montreal Centre", "Maastricht Radar").</summary>
    public string Name { get; } = name;

    public double FrequencyMhz { get; } = frequencyMhz;

    public int MinFl { get; } = minFl;

    public int MaxFl { get; } = maxFl;

    public GeoRing Ring { get; } = ring;

    /// <summary>Kennung der Kontrollposition: Sektoren mit gleicher Kennung gehören zur selben Station (keine Übergabe).</summary>
    public string StationKey => $"{Fir}/{Position}";

    public bool Contains(double lat, double lon, double flightLevel) =>
        flightLevel >= MinFl && flightLevel <= MaxFl && Ring.Contains(lat, lon);
}

/// <summary>
/// Luftraumdaten für das Duopilo-ATC: FIR-Grenzen aus VAT-Spy und Streckensektoren mit Frequenzen. Unveränderlich und
/// threadsicher. <see cref="Empty"/> ohne Daten – dann teilt Duopilo die Route wie früher nur nach den FIR-Angaben des OFP.
/// </summary>
public sealed class AirspaceDatabase
{
    public static readonly AirspaceDatabase Empty = new([], []);

    private readonly Dictionary<string, AirspaceFir> _firs;
    private readonly AirspaceFir[] _withBoundary;
    private readonly HashSet<string> _sectorFirs;

    public AirspaceDatabase(IEnumerable<AirspaceFir> firs, IEnumerable<AirspaceSector> sectors, IReadOnlyList<string>? sources = null)
    {
        _firs = new Dictionary<string, AirspaceFir>(StringComparer.OrdinalIgnoreCase);
        foreach (var fir in firs)
        {
            _firs[fir.Id] = fir;
        }

        _withBoundary = _firs.Values.Where(f => f.HasBoundary).ToArray();
        Sectors = sectors.ToArray();
        _sectorFirs = new HashSet<string>(Sectors.Select(s => s.Fir), StringComparer.OrdinalIgnoreCase);
        Sources = sources ?? [];
    }

    public IReadOnlyCollection<AirspaceFir> Firs => _firs.Values;

    public IReadOnlyList<AirspaceSector> Sectors { get; }

    /// <summary>Herkunft der Daten (für Status und Log).</summary>
    public IReadOnlyList<string> Sources { get; }

    public bool IsEmpty => _withBoundary.Length == 0 && Sectors.Count == 0;

    public AirspaceFir? Fir(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _firs.TryGetValue(id.Trim(), out var fir) ? fir : null;

    public bool HasBoundary(string? id) => Fir(id) is { HasBoundary: true };

    public bool HasSectors(string? id) => !string.IsNullOrWhiteSpace(id) && _sectorFirs.Contains(id.Trim());

    public bool InFir(string? id, double lat, double lon) => Fir(id) is { } fir && fir.Contains(lat, lon);

    /// <summary>FIRs mit Grenzen, die den Punkt enthalten (VAT-Spy hat Überlappungen, z. B. Gander/Moncton, NAT).</summary>
    public IEnumerable<AirspaceFir> FirsAt(double lat, double lon)
    {
        foreach (var fir in _withBoundary)
        {
            if (fir.Contains(lat, lon))
            {
                yield return fir;
            }
        }
    }

    /// <summary>Kleinste FIR am Punkt, die <paramref name="filter"/> erfüllt – große Überlagerungen (NAT, EDXX) verlieren.</summary>
    public AirspaceFir? SmallestFirAt(double lat, double lon, Func<AirspaceFir, bool>? filter = null) =>
        FirsAt(lat, lon).Where(f => filter is null || filter(f)).OrderBy(f => f.Area).FirstOrDefault();

    /// <summary>Sektoren am Punkt in der Flughöhe (FL).</summary>
    public IEnumerable<AirspaceSector> SectorsAt(double lat, double lon, double flightLevel)
    {
        foreach (var sector in Sectors)
        {
            if (sector.Contains(lat, lon, flightLevel))
            {
                yield return sector;
            }
        }
    }
}
