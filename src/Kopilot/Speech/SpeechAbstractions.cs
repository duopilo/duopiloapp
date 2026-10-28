namespace Kopilot.Speech;

/// <summary>Fertig synthetisierte Ansage: Mono-Samples (−1…1).</summary>
public sealed record SpeechClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(Samples.Length / (double)SampleRate) : TimeSpan.Zero;
}

/// <summary>Text → Audio. Die echte Umsetzung ist <see cref="KokoroSpeechEngine"/>, die Tests nutzen eine Attrappe.</summary>
public interface ISpeechEngine : IDisposable
{
    /// <summary>Modell geladen, <see cref="Synthesize"/> kann benutzt werden.</summary>
    bool IsLoaded { get; }

    /// <summary>Verfügbare englische Stimmen (Dateinamen ohne .npy), auch vor dem Laden bekannt.</summary>
    IReadOnlyList<string> Voices { get; }

    /// <summary>Lädt das Modell, bei Bedarf vorher herunter. <paramref name="downloadProgress"/> bekommt 0…1.</summary>
    Task LoadAsync(Action<double>? downloadProgress, CancellationToken ct);

    /// <summary>Synthetisiert synchron (CPU). Aufrufer legen das auf einen Hintergrund-Thread.</summary>
    SpeechClip Synthesize(string text, string voice, double speed);
}

/// <summary>Gibt Audio aus. Abbrechen über das Token stoppt die Wiedergabe sofort.</summary>
public interface IAudioOutput
{
    /// <summary>Namen der Ausgabegeräte (für Konfiguration und Diagnose).</summary>
    IReadOnlyList<string> Devices { get; }

    /// <summary>Gewähltes Gerät, "Standard" wenn keins konfiguriert oder nicht gefunden.</summary>
    string DeviceName { get; }

    /// <summary>Spielt ab und kehrt nach dem Ende zurück. Wirft <see cref="OperationCanceledException"/>, wenn abgebrochen.</summary>
    Task PlayAsync(SpeechClip clip, float volume, CancellationToken ct);
}
