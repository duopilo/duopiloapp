using Kopilot.Atc;

namespace Kopilot.Voice;

/// <summary>
/// Anbindung der Sprachschleife an das ATC-Modul (<see cref="AtcService"/>): Funksprüche des Piloten weitergeben und die
/// Namen holen, an denen der Router einen Funkspruch erkennt. Die Antworten der Station spricht das ATC-Modul selbst über
/// <see cref="IAtcRadio"/> (die gemeinsame Warteschlange, Priorität ATC). Austauschbar für Tests.
/// </summary>
public interface IAtcLink
{
    /// <summary>ATC-Modul eingeschaltet (Kopilot:Atc:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>Stationsnamen und eigene Rufzeichen des Flugs.</summary>
    AtcRoutingInfo RoutingInfo();

    /// <summary>Funkspruch des Piloten (Abschrift, wie gesprochen). Startet bei Bedarf die ATC-Sitzung.</summary>
    Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct);
}

/// <summary>Das echte ATC-Modul.</summary>
public sealed class AtcServiceLink(AtcService atc) : IAtcLink
{
    public bool Enabled => atc.Enabled;

    public AtcRoutingInfo RoutingInfo() => atc.RoutingInfo();

    public Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct) => atc.TransmitAsync(text, ct);
}
