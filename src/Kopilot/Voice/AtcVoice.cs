namespace Kopilot.Voice;

/// <summary>Funkspruch des Piloten an eine ATC-Station, so wie ihn der Router erkannt hat.</summary>
/// <param name="Text">Abschrift von Whisper (Rohtext).</param>
/// <param name="Normalized">Normalisierte Form (<see cref="VoiceText.Normalize"/>).</param>
/// <param name="Station">Erkannte Station, z. B. "frankfurt tower" (null, wenn nur das eigene Rufzeichen erkannt wurde).</param>
/// <param name="Callsign">Erkanntes eigenes Rufzeichen am Anfang (null, wenn nur die Station erkannt wurde).</param>
/// <param name="TimeUtc">Ende des Funkspruchs.</param>
public sealed record PilotTransmission(string Text, string Normalized, string? Station, string? Callsign, DateTimeOffset TimeUtc);

/// <summary>Funkspruch einer ATC-Station.</summary>
/// <param name="Text">Was die Station sagt (englisch, Funk-Phraseologie). Zahlen und Buchstaben am besten schon ausgeschrieben.</param>
/// <param name="Station">Station, z. B. "Frankfurt Tower" – bestimmt die Stimme, wenn <paramref name="Voice"/> leer ist.</param>
/// <param name="Voice">Kokoro-Stimme (z. B. "am_michael"). Leer = feste Stimme je Station aus voice-settings.json.</param>
public sealed record AtcTransmission(string Text, string? Station = null, string? Voice = null)
{
    /// <summary>Text für die Sprachsynthese, falls er vom Anzeigetext abweicht.</summary>
    public string? Spoken { get; init; }
}

/// <summary>Funk für das ATC-Modul: unaufgeforderte Meldungen (Freigaben, Frequenzwechsel) in die Sprachausgabe.</summary>
public interface IAtcRadio
{
    /// <summary>Reiht den Funkspruch mit Priorität ATC ein. false = nicht angenommen (keine Stimme, Sim pausiert).</summary>
    bool Transmit(AtcTransmission transmission);
}

/// <summary>
/// Schnittstelle zum ATC-Modul (Branch feature/atc-templates). Die Sprachschleife erkennt Funksprüche an ATC
/// (Stationsrufzeichen oder eigenes Rufzeichen am Anfang) und gibt sie hierher; Antworten spricht sie mit den ATC-Stimmen
/// und der Priorität ATC (nach Warnungen, Callouts und Antworten von Kopilot).
/// <para>
/// Registrierung im ATC-Modul: <c>services.AddSingleton&lt;IAtcVoiceModule, MeinAtcModul&gt;()</c> vor
/// <c>AddKopilotVoice()</c> – sonst gilt <see cref="NullAtcVoiceModule"/>.
/// </para>
/// </summary>
public interface IAtcVoiceModule
{
    /// <summary>Ist ein ATC-Modul aktiv? Ohne Modul werden Funksprüche nur protokolliert.</summary>
    bool IsAvailable { get; }

    /// <summary>Weitere Stationsnamen, die der Router als ATC erkennen soll (z. B. "Langen Information").</summary>
    IReadOnlyList<string> KnownStations { get; }

    /// <summary>Eigene Rufzeichen in gesprochener Form (z. B. "Lufthansa 4AB"), zusätzlich zu Einstellungen und MSFS.</summary>
    IReadOnlyList<string> OwnCallsigns { get; }

    /// <summary>Wird beim Start der Sprachschleife einmal aufgerufen – darüber meldet sich ATC von selbst.</summary>
    void Attach(IAtcRadio radio);

    /// <summary>Funkspruch des Piloten. Rückgabe: Antworten der Station(en), in dieser Reihenfolge gesprochen.</summary>
    Task<IReadOnlyList<AtcTransmission>> HandleAsync(PilotTransmission transmission, CancellationToken ct);
}

/// <summary>Kein ATC-Modul installiert: Funksprüche an ATC bleiben unbeantwortet (wie im echten Funk ohne Station).</summary>
public sealed class NullAtcVoiceModule : IAtcVoiceModule
{
    public bool IsAvailable => false;

    public IReadOnlyList<string> KnownStations => [];

    public IReadOnlyList<string> OwnCallsigns => [];

    public void Attach(IAtcRadio radio)
    {
    }

    public Task<IReadOnlyList<AtcTransmission>> HandleAsync(PilotTransmission transmission, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AtcTransmission>>([]);
}
