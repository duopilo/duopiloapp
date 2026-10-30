using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Duopilo.Atc.Airspace;

namespace Duopilo.Atc;

public enum AtcStationType
{
    Delivery,
    Ground,
    Tower,
    Departure,
    Center,
    Oceanic,
    Approach,
}

/// <summary>Eine Funkstelle im Flug, z. B. "Frankfurt Delivery" oder "Shanwick Oceanic", mit Frequenz und fester Stimme.</summary>
/// <param name="Key">Eindeutiger Schlüssel: "EDDF/delivery", "FIR/EDGG", "FIR/EGGX".</param>
/// <param name="Facility">Flughafen oder FIR (ICAO), leer bei einer unbekannten FIR.</param>
/// <param name="FrequencySource">"facility" (MSFS Facilities-API), "sectors" (VATSIM-Sektordaten), "templates" (atc-templates.json) oder "placeholder".</param>
public sealed record AtcStation(
    string Key,
    AtcStationType Type,
    string Facility,
    string Name,
    AtcValue Frequency,
    string FrequencySource,
    string Voice)
{
    public AtcValue NameValue => AtcValue.Text(Name, Name);
}

/// <summary>Frequenz aus der MSFS Facilities-API. <paramref name="Type"/> wie SimConnect: 5 Ground, 6 Tower, 7 Clearance, 8 Approach, 9 Departure …</summary>
public sealed record AirportFrequency(int Type, double Mhz, string? Name);

/// <summary>Frequenzarten der Facilities-API (FREQUENCY.TYPE).</summary>
public static class FacilityFrequencyType
{
    public const int Atis = 1;
    public const int Ground = 5;
    public const int Tower = 6;
    public const int Clearance = 7;
    public const int Approach = 8;
    public const int Departure = 9;
    public const int Center = 10;
    public const int ClearancePreTaxi = 14;
    public const int RemoteClearance = 15;
}

/// <summary>Zwischenspeicher der Flughafen-Frequenzen aus der Facilities-API (threadsicher).</summary>
public sealed class AtcFrequencyBook
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AirportFrequency>> _airports = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string icao, IReadOnlyList<AirportFrequency> frequencies) => _airports[icao] = frequencies;

    public IReadOnlyList<AirportFrequency>? Get(string icao) => _airports.TryGetValue(icao, out var f) ? f : null;

    public bool Has(string icao) => _airports.ContainsKey(icao);

    public void Clear() => _airports.Clear();

    public IReadOnlyDictionary<string, int> Counts => _airports.ToDictionary(p => p.Key, p => p.Value.Count);
}

/// <summary>
/// Baut die Stationen eines Flugs: Name aus den Vorlagen, den Sektordaten, VAT-Spy bzw. dem OFP, Frequenz aus Sektordaten,
/// Facilities-API, Vorlagen oder Platzhalter.
/// </summary>
public sealed partial class AtcStations
{
    /// <summary>Eintrag unter "firs" für Oceanic-Gebiete ohne eigenen Eintrag (Koordinaten außerhalb des Nordatlantiks).</summary>
    public const string GenericOceanicFir = "OCEANIC";

    /// <summary>Eintrag unter "firs" für Land-FIRs ohne eigenen Eintrag (z. B. KZMP): Name und Frequenz einer generischen Center-Station.</summary>
    public const string GenericCenterFir = "CENTER";

    private static readonly string[] UnitWords = ["Control", "Radar", "Center", "Centre", "Oceanic", "Information", "Radio", "Approach"];

    private readonly AtcTemplates _templates;
    private readonly Func<string, IReadOnlyList<AirportFrequency>?> _facilityFrequencies;
    private readonly AtcVoiceAssigner _voices;
    private readonly AirspaceDatabase _airspace;
    private readonly Dictionary<string, AtcStation> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="airspace">Luftraumdaten für die Namen von FIRs ohne eigenen Eintrag (VAT-Spy), null = keine.</param>
    public AtcStations(AtcTemplates templates, Func<string, IReadOnlyList<AirportFrequency>?> facilityFrequencies, AtcVoiceAssigner voices, AirspaceDatabase? airspace = null)
    {
        _templates = templates;
        _facilityFrequencies = facilityFrequencies;
        _voices = voices;
        _airspace = airspace ?? AirspaceDatabase.Empty;
    }

    public static string TypeKey(AtcStationType type) => type.ToString().ToLowerInvariant();

    /// <summary>Stimmen, die in diesem Flug schon vergeben sind (Stations-Schlüssel → Stimme).</summary>
    public IReadOnlyDictionary<string, string> AssignedVoices => _voices.Assigned;

    /// <summary>Vergebene Stimmen aus einer gesicherten Sitzung übernehmen (vor dem ersten Abruf einer Station).</summary>
    public void RestoreVoices(IReadOnlyDictionary<string, string> voices)
    {
        _cache.Clear();
        _voices.Restore(voices);
    }

    /// <summary>Station am Flughafen (Delivery, Ground, Tower, Departure, Approach).</summary>
    public AtcStation Airport(string icao, string? ofpName, AtcStationType type)
    {
        var key = $"{icao.ToUpperInvariant()}/{TypeKey(type)}";
        var cached = _cache.GetValueOrDefault(key);
        if (cached is not null && cached.FrequencySource == "facility")
        {
            return cached;
        }

        var config = _templates.Airport(icao);
        var typeConfig = _templates.StationType(type);
        var name = config?.Stations.GetValueOrDefault(TypeKey(type)) is { Length: > 0 } custom
            ? custom.Trim()
            : $"{AirportName(icao, ofpName)} {typeConfig.Name}".Trim();

        AtcValue? frequency = null;
        var source = "placeholder";
        if (ParseFrequency(config?.Frequencies.GetValueOrDefault(TypeKey(type))) is { } fromTemplates)
        {
            frequency = fromTemplates;
            source = "templates";
        }
        else if (FacilityFrequency(icao, type) is { } fromFacility)
        {
            frequency = fromFacility;
            source = "facility";
        }

        frequency ??= ParseFrequency(typeConfig.PlaceholderFrequency) ?? AtcSpeech.Frequency(122.8);
        if (cached is not null && cached.FrequencySource == source)
        {
            return cached;
        }

        var station = new AtcStation(key, type, icao.ToUpperInvariant(), name, frequency, source, cached?.Voice ?? _voices.VoiceFor(key, type));
        _cache[key] = station;
        return station;
    }

    /// <summary>Kontrollstelle einer FIR (Center oder Oceanic). Leerer Code = unbekannte FIR (generischer Name).</summary>
    public AtcStation Fir(string? fir, bool oceanic)
    {
        var code = string.IsNullOrWhiteSpace(fir) ? "" : fir.Trim().ToUpperInvariant();
        var type = oceanic ? AtcStationType.Oceanic : AtcStationType.Center;
        var key = code.Length > 0 ? $"FIR/{code}" : $"FIR/{TypeKey(type)}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // FIR ohne eigenen Eintrag: Frequenz der generischen Station aus "OCEANIC" bzw. "CENTER" (der Schlüssel bleibt je FIR,
        // damit Übergaben zwischen zwei unbekannten FIRs und eigene Stimmen erhalten bleiben)
        var config = _templates.Fir(code) ?? _templates.Fir(oceanic ? GenericOceanicFir : GenericCenterFir);
        var typeConfig = _templates.StationType(type);
        AtcValue frequency;
        string source;
        if (ParseFrequency(config?.Frequency) is { } f)
        {
            frequency = f;
            source = "templates";
        }
        else
        {
            frequency = ParseFrequency(typeConfig.PlaceholderFrequency) ?? AtcSpeech.Frequency(122.8);
            source = "placeholder";
        }

        var station = new AtcStation(key, type, code, FirName(code, oceanic, null), frequency, source, _voices.VoiceFor(key, type));
        _cache[key] = station;
        return station;
    }

    /// <summary>
    /// Station eines Routenabschnitts: mit Sektor eine eigene Station je Kontrollposition ("FIR/CZUL/MC") mit der Frequenz
    /// aus den Sektordaten, sonst die Station der FIR.
    /// </summary>
    public AtcStation Segment(AtcFirSegment? segment)
    {
        if (segment is null)
        {
            return Fir(null, oceanic: false);
        }

        if (segment.Sector is not { Length: > 0 } sector || segment.FrequencyMhz is not { } mhz || ParseFrequency(mhz.ToString(CultureInfo.InvariantCulture)) is not { } frequency)
        {
            return Fir(segment.Fir, segment.Oceanic);
        }

        var code = segment.Fir.Trim().ToUpperInvariant();
        var type = segment.Oceanic ? AtcStationType.Oceanic : AtcStationType.Center;
        var key = SectorKey(code, sector);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var station = new AtcStation(key, type, code, SegmentName(segment), frequency, "sectors", _voices.VoiceFor(key, type));
        _cache[key] = station;
        return station;
    }

    /// <summary>Schlüssel der Station einer Kontrollposition: "FIR/CZUL/MC".</summary>
    public static string SectorKey(string fir, string sector) => $"FIR/{fir.Trim().ToUpperInvariant()}/{sector}";

    /// <summary>Rufname der Station eines Routenabschnitts, ohne eine Stimme zu vergeben (für die Sprachschleife).</summary>
    public string SegmentName(AtcFirSegment segment) =>
        FirName(segment.Fir.Trim().ToUpperInvariant(), segment.Oceanic, segment.SectorName);

    /// <summary>
    /// Name einer Kontrollstelle: Vorlage ("Langen Radar") → Rufname aus den Sektordaten ("Montreal Centre" → "Montreal
    /// Center") → VAT-Spy-Name mit Zusatz nach Region ("Minneapolis" → "Minneapolis Center") → generische Station.
    /// </summary>
    public string FirName(string code, bool oceanic, string? sectorName)
    {
        if (_templates.Fir(code)?.Name is { Length: > 0 } configured)
        {
            return configured.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sectorName))
        {
            return CentreSpelling().Replace(sectorName.Trim(), "Center");
        }

        if (code.Length > 0 && _airspace.Fir(code)?.Name is { Length: > 0 } vatSpyName)
        {
            var name = vatSpyName.Trim();
            if (UnitWords.Any(u => name.EndsWith(" " + u, StringComparison.OrdinalIgnoreCase)))
            {
                return CentreSpelling().Replace(name, "Center");
            }

            var suffix = oceanic ? _templates.StationType(AtcStationType.Oceanic).Name : Suffix(code);
            return $"{name} {suffix}".Trim();
        }

        var generic = _templates.Fir(oceanic ? GenericOceanicFir : GenericCenterFir);
        var typeName = _templates.StationType(oceanic ? AtcStationType.Oceanic : AtcStationType.Center).Name;
        return generic?.Name is { Length: > 0 } n ? n.Trim() : typeName;
    }

    /// <summary>Zusatz nach Region ("K" → "Center", "ED" → "Radar"), längster Präfix gewinnt; ohne Angabe die Stationsart.</summary>
    private string Suffix(string code)
    {
        var best = _templates.FirSuffixes
            .Where(p => code.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => p.Value.Trim())
            .FirstOrDefault();
        return best ?? _templates.StationType(AtcStationType.Center).Name;
    }

    /// <summary>Rufname eines Flughafens: aus den Vorlagen, sonst aus dem OFP-Namen ohne "International", "Airport" …</summary>
    public string AirportName(string icao, string? ofpName)
    {
        if (_templates.Airport(icao)?.Name is { Length: > 0 } configured)
        {
            return configured.Trim();
        }

        if (string.IsNullOrWhiteSpace(ofpName))
        {
            return icao.ToUpperInvariant();
        }

        var name = ofpName.Split('/', StringSplitOptions.TrimEntries)[0];
        name = AirportSuffix().Replace(name, "").Trim();
        return name.Length > 0 ? name : icao.ToUpperInvariant();
    }

    public static AtcValue? ParseFrequency(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
        && mhz is >= 108.0 and < 137.0
            ? AtcSpeech.Frequency(mhz)
            : null;

    private AtcValue? FacilityFrequency(string icao, AtcStationType type)
    {
        if (_facilityFrequencies(icao) is not { Count: > 0 } list)
        {
            return null;
        }

        var preferred = type switch
        {
            AtcStationType.Delivery => new[] { FacilityFrequencyType.Clearance, FacilityFrequencyType.ClearancePreTaxi, FacilityFrequencyType.RemoteClearance },
            AtcStationType.Ground => new[] { FacilityFrequencyType.Ground },
            AtcStationType.Tower => new[] { FacilityFrequencyType.Tower },
            AtcStationType.Departure => new[] { FacilityFrequencyType.Departure, FacilityFrequencyType.Approach },
            AtcStationType.Approach => new[] { FacilityFrequencyType.Approach },
            _ => Array.Empty<int>(),
        };

        foreach (var wanted in preferred)
        {
            if (list.FirstOrDefault(f => f.Type == wanted && f.Mhz is >= 108.0 and < 137.0) is { } hit)
            {
                return AtcSpeech.Frequency(hit.Mhz);
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b(International|Intl|Airport|Airfield|Aeropuerto|Flughafen|Regional|Municipal)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex AirportSuffix();

    [GeneratedRegex(@"\bCentre\b", RegexOptions.IgnoreCase)]
    private static partial Regex CentreSpelling();
}

/// <summary>
/// Vergibt jeder Station eine Kokoro-Stimme: fest aus den Vorlagen, sonst zufällig aus dem Pool. Pro Flug fest
/// (der Zufall hängt am Flug), möglichst jede Station eine andere, und nicht die Stimme der Duopilo-Callouts.
/// </summary>
public sealed partial class AtcVoiceAssigner
{
    private const string Fallback = "bm_george";

    private readonly AtcVoiceConfig _config;
    private readonly string? _avoid;
    private readonly Func<string?>? _avoidLive;
    private readonly IReadOnlyCollection<string>? _installed;
    private readonly Dictionary<string, string> _assigned = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="flightKey">Kennung des Flugs (SimBrief-Request-ID) – gleicher Flug, gleiche Stimmen.</param>
    /// <param name="calloutVoice">Kokoro-Stimme der Callouts, die ATC nicht bekommen soll.</param>
    /// <param name="installed">Vorhandene Stimmen (null = unbekannt, dann wird nicht gefiltert).</param>
    /// <param name="kopilotVoice">
    /// Stimme des Kopiloten zur Laufzeit (First Officer, wechselt pro Flug) – wird beim Vergeben jeder neuen Station abgefragt.
    /// </param>
    public AtcVoiceAssigner(AtcVoiceConfig config, string flightKey, string? calloutVoice, IReadOnlyCollection<string>? installed, Func<string?>? kopilotVoice = null)
    {
        _config = config;
        _avoid = config.AvoidCalloutVoice ? calloutVoice : null;
        _avoidLive = config.AvoidCalloutVoice ? kopilotVoice : null;
        _installed = installed is { Count: > 0 } ? installed : null;
        _flightKey = flightKey;
    }

    private readonly string _flightKey;

    public IReadOnlyDictionary<string, string> Assigned => _assigned;

    /// <summary>Stimmen einer gesicherten Sitzung: Nach dem Neustart spricht jede Station wieder mit derselben Stimme.</summary>
    public void Restore(IReadOnlyDictionary<string, string> voices)
    {
        foreach (var (key, voice) in voices)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(voice))
            {
                _assigned[key] = voice.Trim();
            }
        }
    }

    public string VoiceFor(string stationKey, AtcStationType type)
    {
        if (_assigned.TryGetValue(stationKey, out var known))
        {
            return known;
        }

        // Feste Stimme: für die Station, für ihre FIR (Sektor "FIR/CZUL/MC" → "FIR/CZUL") oder für die Stationsart
        var parent = stationKey.Count(c => c == '/') >= 2 ? stationKey[..stationKey.LastIndexOf('/')] : null;
        var configured = _config.Stations.GetValueOrDefault(stationKey)
            ?? (parent is null ? null : _config.Stations.GetValueOrDefault(parent))
            ?? _config.Stations.GetValueOrDefault(AtcStations.TypeKey(type));
        var voice = configured is not null && Usable(configured.Trim()) ? configured.Trim() : Pick(stationKey);
        _assigned[stationKey] = voice;
        return voice;
    }

    /// <summary>
    /// Zufällig, aber aus Flug und Station abgeleitet: Nach einem Neustart von Duopilo bekommt dieselbe Station im selben Flug
    /// wieder dieselbe Stimme. Ist sie schon vergeben, die nächste freie im Pool.
    /// </summary>
    private string Pick(string stationKey)
    {
        string? live = null;
        try
        {
            live = _avoidLive?.Invoke();
        }
        catch (Exception)
        {
            // Stimme von Duopilo gerade nicht lesbar – dann nur die feste vermeiden
        }

        var pool = _config.Pool.Select(v => v.Trim()).Where(Usable).Distinct(StringComparer.Ordinal).Where(v => v != _avoid && v != live).ToList();
        if (pool.Count == 0)
        {
            pool = _config.Pool.Select(v => v.Trim()).Where(Usable).Distinct(StringComparer.Ordinal).ToList();
        }

        if (pool.Count == 0)
        {
            return Fallback;
        }

        var start = StableHash(_flightKey + "|" + stationKey.ToUpperInvariant()) % pool.Count;
        for (var i = 0; i < pool.Count; i++)
        {
            var candidate = pool[(start + i) % pool.Count];
            if (!_assigned.ContainsValue(candidate))
            {
                return candidate;
            }
        }

        return pool[start];
    }

    private bool Usable(string voice) =>
        VoiceName().IsMatch(voice) && (_installed is null || _installed.Contains(voice));

    /// <summary>FNV-1a – string.GetHashCode ist pro Prozess zufällig, die Stimmen sollen nach einem Neustart gleich bleiben.</summary>
    public static int StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash & 0x7fffffff);
        }
    }

    [GeneratedRegex("^[ab][fm]_[a-z0-9]+$")]
    private static partial Regex VoiceName();
}
