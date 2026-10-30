using Kopilot.Atc;
using Kopilot.Localization;

namespace Kopilot.Voice;

/// <summary>Was mit einem Satz passiert ist – für Log, Status und Tests.</summary>
/// <param name="Normalized">Normalisierte Form.</param>
/// <param name="Route">Zuordnung (Kopilot oder ATC).</param>
/// <param name="Intent">Erkannter Kopilot-Befehl (null bei ATC).</param>
/// <param name="Reply">Was Kopilot sagt (null = nichts). ATC-Antworten spricht das ATC-Modul selbst.</param>
/// <param name="Atc">Ergebnis des ATC-Moduls (null bei Kopilot).</param>
/// <param name="Chat">
/// Smalltalk: weder Befehl noch Abfrage noch Funkspruch – der Satz geht ans Sprachmodell (die Antwort kommt später und wird nur
/// gesprochen). <paramref name="Reply"/> ist dann null.
/// </param>
public sealed record VoiceOutcome(string Normalized, VoiceRoute Route, VoiceIntent? Intent, VoiceReply? Reply, AtcTransmitResult? Atc, bool Chat = false);

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

    /// <summary>Name des First Officers als Anrede (normalisiert, z. B. "sam") – zusätzlich zu "Kopilot". null = keiner.</summary>
    public Func<string?> PersonaName { get; init; } = () => null;

    /// <summary>
    /// Smalltalk an: Höchstzahl der Wörter für Sätze ohne Anrede (0 = nur mit Anrede). null = Smalltalk aus – dann bleibt es bei
    /// "Say again".
    /// </summary>
    public Func<int?> ChatWords { get; init; } = () => null;

    // Vor der Anrede erlaubt: "Hey Sam, …", "Okay Kopilot, …"
    private static readonly HashSet<string> Greetings = new(StringComparer.Ordinal) { "hey", "hi", "hello", "hallo", "ok", "okay" };

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
        var normalized = Address(VoiceText.Normalize(text), PersonaName(), wakeWords);
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

        // Weder Befehl noch Abfrage noch Funkspruch: Smalltalk (Sprachmodell), statt "Say again" – nur wenn nichts Wichtigeres offen ist
        if (ChatWords() is { } chatWords && dialog.CanChat(intent, normalized, addressed, chatWords))
        {
            return new VoiceOutcome(normalized, route with { Reason = Loc.T("voice.route.smalltalk") }, intent, null, null, Chat: true);
        }

        var reply = await dialog.HandleAsync(intent, normalized, addressed, startUtc, confidence, ct);
        return new VoiceOutcome(normalized, route, intent, reply, null);
    }

    /// <summary>
    /// Anrede vereinheitlichen: Name des First Officers am Satzanfang ("Sam, gear up") oder mit Gruß davor ("Hey Sam", "Okay Kopilot")
    /// wird zur ersten Anrede ("kopilot gear up") – Router, Befehle und Checklisten kennen nur diese. Sonst unverändert.
    /// </summary>
    public static string Address(string normalized, string? personaName, IReadOnlyCollection<string> wakeWords)
    {
        var words = VoiceText.Words(normalized);
        if (words.Length == 0)
        {
            return normalized;
        }

        var wake = wakeWords.FirstOrDefault() ?? "kopilot";
        var i = words.Length > 1 && Greetings.Contains(words[0]) ? 1 : 0;
        var isName = !string.IsNullOrEmpty(personaName) && words[i] == personaName;
        if (!isName && !(i == 1 && wakeWords.Contains(words[1])))
        {
            return normalized;
        }

        return string.Join(' ', words.Skip(i + 1).Prepend(wake));
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
