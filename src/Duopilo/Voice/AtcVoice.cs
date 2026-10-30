using Duopilo.Atc;

namespace Duopilo.Voice;

/// <summary>
/// Anbindung der Sprachschleife an das ATC-Modul (<see cref="AtcService"/>): Funksprüche des Piloten weitergeben und die
/// Namen holen, an denen der Router einen Funkspruch erkennt. Die Antworten der Station spricht das ATC-Modul selbst über
/// <see cref="IAtcRadio"/> (die gemeinsame Warteschlange, Priorität ATC). Austauschbar für Tests.
/// </summary>
public interface IAtcLink
{
    /// <summary>ATC-Modul eingeschaltet (Duopilo:Atc:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>Stationsnamen und eigene Rufzeichen des Flugs.</summary>
    AtcRoutingInfo RoutingInfo();

    /// <summary>Funkspruch des Piloten (Abschrift, wie gesprochen). Startet bei Bedarf die ATC-Sitzung.</summary>
    Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct);

    /// <summary>Aktive Station der laufenden Sitzung mit Frequenz (für die Abfrage "Station"), null ohne Sitzung.</summary>
    StationReading? ActiveStation() => null;

    /// <summary>Kokoro-Stimmen, die die Stationen der laufenden Sitzung schon haben (die Stimme des First Officers meidet sie).</summary>
    IReadOnlyCollection<string> AssignedVoices() => [];
}

/// <summary>Das echte ATC-Modul.</summary>
public sealed class AtcServiceLink(AtcService atc) : IAtcLink
{
    public bool Enabled => atc.Enabled;

    public AtcRoutingInfo RoutingInfo() => atc.RoutingInfo();

    public Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct) => atc.TransmitAsync(text, ct);

    public StationReading? ActiveStation() => atc.WithSession((session, _) =>
        session is null || session.Phase is AtcPhase.Inactive or AtcPhase.Done
            ? null
            : new StationReading(session.Station.Name, session.Station.Frequency.Number));

    public IReadOnlyCollection<string> AssignedVoices() => atc.WithSession<IReadOnlyCollection<string>>((session, _) =>
        session is null ? Array.Empty<string>() : session.Voices.Values.Append(session.Station.Voice).Distinct(StringComparer.Ordinal).ToList());
}
