using Kopilot.Atc;
using Kopilot.Localization;

namespace Kopilot.Voice;

/// <summary>Was mit einem Satz passiert ist – für Log, Status und Tests.</summary>
/// <param name="Normalized">Normalisierte Form.</param>
/// <param name="Route">Zuordnung (Kopilot oder ATC).</param>
/// <param name="Intent">Erkannter Kopilot-Befehl (null bei ATC).</param>
/// <param name="Reply">Was Kopilot sagt (null = nichts). ATC-Antworten spricht das ATC-Modul selbst.</param>
/// <param name="Atc">Ergebnis des ATC-Moduls (null bei Kopilot).</param>
public sealed record VoiceOutcome(string Normalized, VoiceRoute Route, VoiceIntent? Intent, VoiceReply? Reply, AtcTransmitResult? Atc);

/// <summary>
/// Ein abgeschriebener Satz → Router → ATC-Modul oder Kopilot-Dialog. Kurze Antworten ohne Station und ohne Anrede
/// ("Say again", "Affirm", "Negative") gehen an die Seite, die zuletzt gesprochen hat (ATC oder Kopilot).
/// Spricht ATC nach einem Readback von Kopilot, verfällt die offene Aktion – ein "Affirm" danach gilt ATC.
/// Ohne Sim, Mikrofon und Audio – damit testbar.
/// </summary>
public sealed class VoiceDispatcher(VoiceDialog dialog, IAtcLink atc, VoiceRouter router, IReadOnlyCollection<string> wakeWords)
{
    public const string AtcUnavailable = "ATC unavailable";

    /// <summary>Weitere eigene Rufzeichen (Einstellungen, MSFS) zusätzlich zu denen des ATC-Moduls.</summary>
    public Func<IEnumerable<string>> ExtraCallsigns { get; init; } = () => [];

    /// <summary>Weitere Stationsnamen aus den Einstellungen.</summary>
    public IReadOnlyList<string> ExtraStations { get; init; } = [];

    /// <summary>Ende des zuletzt gesprochenen ATC-Funkspruchs (Sprachausgabe) – genauer als die Zeit im Protokoll des ATC-Moduls.</summary>
    public Func<DateTimeOffset?> AtcHeardUtc { get; init; } = () => null;

    public Task<VoiceOutcome> HandleAsync(string text, CancellationToken ct) => HandleAsync(text, null, ct);

    /// <param name="text">Abschrift.</param>
    /// <param name="startUtc">Wann der Pilot zu sprechen begann (null = jetzt).</param>
    /// <param name="ct">Abbruch.</param>
    public Task<VoiceOutcome> HandleAsync(string text, DateTimeOffset? startUtc, CancellationToken ct) => HandleAsync(text, startUtc, null, ct);

    /// <param name="text">Abschrift.</param>
    /// <param name="startUtc">Wann der Pilot zu sprechen begann (null = jetzt).</param>
    /// <param name="confidence">Konfidenz der Erkennung (0–1, null = unbekannt) – darunter Readback statt sofort ausführen.</param>
    /// <param name="ct">Abbruch.</param>
    public async Task<VoiceOutcome> HandleAsync(string text, DateTimeOffset? startUtc, double? confidence, CancellationToken ct)
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

        if (atc.Enabled && !dialog.Muted && Latest(info.LastAtcUtc, AtcHeardUtc()) is { } lastAtc)
        {
            // ATC hat nach dem Readback gesprochen: Ein "Confirm" ist jetzt nicht mehr eindeutig – Aktion verwerfen
            if (dialog.Pending is { HeardUtc: { } heard } && lastAtc > heard)
            {
                dialog.CancelPending(Loc.T("voice.cancel.atcSpoke"));
            }

            // "Say again", "Affirm", "Negative" ohne Station und Anrede: an ATC, wenn ATC zuletzt gesprochen hat
            if (!addressed && intent.Kind is (IntentKind.SayAgain or IntentKind.Confirm or IntentKind.Negative)
                && (dialog.LastSpokeUtc is not { } lastKopilot || lastAtc > lastKopilot))
            {
                return await ToAtcAsync(text, normalized, route with { Target = VoiceTarget.Atc, Reason = Loc.T("voice.route.toAtc", intent) }, ct);
            }
        }

        var reply = await dialog.HandleAsync(intent, normalized, addressed, startUtc, confidence, ct);
        return new VoiceOutcome(normalized, route, intent, reply, null);
    }

    private async Task<VoiceOutcome> ToAtcAsync(string text, string normalized, VoiceRoute route, CancellationToken ct)
    {
        if (!atc.Enabled)
        {
            return new VoiceOutcome(normalized, route, null, VoiceReply.Of(AtcUnavailable), new AtcTransmitResult(false, Loc.T("voice.atc.disabled"), []));
        }

        var result = await atc.TransmitAsync(text, ct);
        return new VoiceOutcome(normalized, route, null, result.Ok ? null : VoiceReply.Of(AtcUnavailable), result);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
