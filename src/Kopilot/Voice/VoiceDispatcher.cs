using Kopilot.Atc;

namespace Kopilot.Voice;

/// <summary>Was mit einem Satz passiert ist – für Log, Status und Tests.</summary>
/// <param name="Normalized">Normalisierte Form.</param>
/// <param name="Route">Zuordnung (Kopilot oder ATC).</param>
/// <param name="Intent">Erkannter Kopilot-Befehl (null bei ATC).</param>
/// <param name="Reply">Was Kopilot sagt (null = nichts). ATC-Antworten spricht das ATC-Modul selbst.</param>
/// <param name="Atc">Ergebnis des ATC-Moduls (null bei Kopilot).</param>
public sealed record VoiceOutcome(string Normalized, VoiceRoute Route, VoiceIntent? Intent, VoiceReply? Reply, AtcTransmitResult? Atc);

/// <summary>
/// Ein abgeschriebener Satz → Router → ATC-Modul oder Kopilot-Dialog. "Say again" ohne Stationsnamen geht an die Seite,
/// die zuletzt gesprochen hat (ATC oder Kopilot). Ohne Sim, Mikrofon und Audio – damit testbar.
/// </summary>
public sealed class VoiceDispatcher(VoiceDialog dialog, IAtcLink atc, VoiceRouter router, IReadOnlyCollection<string> wakeWords)
{
    public const string AtcUnavailable = "ATC unavailable";

    /// <summary>Weitere eigene Rufzeichen (Einstellungen, MSFS) zusätzlich zu denen des ATC-Moduls.</summary>
    public Func<IEnumerable<string>> ExtraCallsigns { get; init; } = () => [];

    /// <summary>Weitere Stationsnamen aus den Einstellungen.</summary>
    public IReadOnlyList<string> ExtraStations { get; init; } = [];

    public async Task<VoiceOutcome> HandleAsync(string text, CancellationToken ct)
    {
        var normalized = VoiceText.Normalize(text);
        var info = atc.Enabled ? atc.RoutingInfo() : AtcRoutingInfo.Empty;
        var callsigns = info.Callsigns.Concat(ExtraCallsigns()).ToList();
        var route = router.Route(normalized, callsigns, info.Stations.Concat(ExtraStations));

        if (route.Target == VoiceTarget.Atc && !dialog.Muted)
        {
            return await ToAtcAsync(text, normalized, route, ct);
        }

        var intent = VoiceIntents.Recognize(normalized, wakeWords);
        var addressed = VoiceText.Words(normalized) is { Length: > 0 } w && wakeWords.Contains(w[0]);

        // "Say again" ohne Station: ATC wiederholt, wenn ATC zuletzt gesprochen hat
        if (intent.Kind == IntentKind.SayAgain && !dialog.Muted && !addressed && atc.Enabled
            && info.LastAtcUtc is { } lastAtc && (dialog.LastReplyUtc is not { } lastReply || lastAtc > lastReply))
        {
            return await ToAtcAsync(text, normalized, route with { Target = VoiceTarget.Atc, Reason = "say again an ATC" }, ct);
        }

        var reply = await dialog.HandleAsync(intent, normalized, addressed, ct);
        return new VoiceOutcome(normalized, route, intent, reply, null);
    }

    private async Task<VoiceOutcome> ToAtcAsync(string text, string normalized, VoiceRoute route, CancellationToken ct)
    {
        if (!atc.Enabled)
        {
            return new VoiceOutcome(normalized, route, null, VoiceReply.Of(AtcUnavailable), new AtcTransmitResult(false, "ATC ist ausgeschaltet.", []));
        }

        var result = await atc.TransmitAsync(text, ct);
        return new VoiceOutcome(normalized, route, null, result.Ok ? null : VoiceReply.Of(AtcUnavailable), result);
    }
}
