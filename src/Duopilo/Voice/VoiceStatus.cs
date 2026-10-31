using Duopilo.Localization;

namespace Duopilo.Voice;

/// <summary>Zustand der Sprachschleife für duopilo_status, /voice, das Dashboard und das Log.</summary>
public sealed record VoiceStatus
{
    /// <summary>Gibt es in dieser Duopilo-Instanz überhaupt eine Sprachschleife (nur Tray-/HTTP-Instanz)?</summary>
    public bool Available { get; init; } = true;

    /// <summary>unavailable, off, loading, listening, muted, inactive (Sim läuft nicht, siehe <see cref="InactiveReason"/>), error.</summary>
    public string State { get; init; } = "off";

    /// <summary>Zustand des Sims: running, notConnected, noFlight, paused (null = Schleife läuft nicht).</summary>
    public string? SimState { get; init; }

    /// <summary>Warum die Sprachschleife gerade nicht zuhört („Sim nicht verbunden“, „Kein Flug geladen“, „Pausiert“), sonst null.</summary>
    public string? InactiveReason { get; init; }

    /// <summary>"enabled" aus voice-settings.json.</summary>
    public bool Enabled { get; init; }

    /// <summary>Hinweis ohne Fehler (z. B. wie man einschaltet).</summary>
    public string? Note { get; init; }

    public string? Error { get; init; }

    /// <summary>Wirksame Einstellungsdatei (%LOCALAPPDATA%\Duopilo\voice-settings.json).</summary>
    public string? SettingsFile { get; init; }

    /// <summary>Mikrofon laut Einstellungen (leer = Windows-Standardgerät).</summary>
    public string? ConfiguredMicrophone { get; init; }

    /// <summary>Wurde das eingestellte Mikrofon gefunden? (false = es läuft das Standardgerät)</summary>
    public bool? MicrophoneFound { get; init; }

    /// <summary>Gerät, von dem gerade aufgenommen wird.</summary>
    public string? Microphone { get; init; }

    /// <summary>Windows-Standardaufnahmegerät.</summary>
    public string? DefaultMicrophone { get; init; }

    /// <summary>Alle Aufnahmegeräte (Stand beim letzten Start bzw. Abruf).</summary>
    public IReadOnlyList<MicrophoneDevice>? Microphones { get; init; }

    public string? Model { get; init; }

    public string? Runtime { get; init; }

    public double? DownloadProgress { get; init; }

    public string? Hotkey { get; init; }

    public bool AtcModule { get; init; }

    public DateTimeOffset? ListeningSinceUtc { get; init; }

    /// <summary>Letzter Audioblock vom Mikrofon (null = noch nie Daten bekommen).</summary>
    public DateTimeOffset? LastAudioUtc { get; init; }

    /// <summary>Spitzenpegel der letzten Sekunden in dBFS (−120 = Stille, um −20 normale Sprache).</summary>
    public double? LevelDbfs { get; init; }

    /// <summary>Sätze, die der VAD ausgeschnitten hat (seit dem Start).</summary>
    public int Utterances { get; init; }

    /// <summary>Davon von Whisper erkannt und verarbeitet.</summary>
    public int Recognized { get; init; }

    /// <summary>Verworfen (Sim pausiert, stumm, Whisper zu langsam, nichts erkannt).</summary>
    public int Dropped { get; init; }

    public string? LastDropReason { get; init; }

    public string? LastTranscript { get; init; }

    public string? LastRoute { get; init; }

    public string? LastIntent { get; init; }

    public string? LastReply { get; init; }

    public string? LastInputResult { get; init; }

    public int? LastTranscriptionMs { get; init; }

    public DateTimeOffset? LastHeardUtc { get; init; }

    public string? PendingReadback { get; init; }

    /// <summary>Modus "ausführen + Callout" an.</summary>
    public bool? ExecuteWithCallout { get; init; }

    /// <summary>Konfidenz der letzten Erkennung (0–1).</summary>
    public double? LastConfidence { get; init; }

    /// <summary>Laufende Checkliste und aktuelle Challenge, z. B. "Before start: Beacon".</summary>
    public string? Checklist { get; init; }

    /// <summary>Meldungen, die auf "check"/"copy" warten (Callout-Texte).</summary>
    public IReadOnlyList<string>? AwaitingAcknowledgment { get; init; }

    /// <summary>Unbeantwortete Meldungen nach der Wiederholung (Warnton und Eintrag im Dashboard).</summary>
    public IReadOnlyList<string>? MissedAcknowledgment { get; init; }

    /// <summary>First Officer des laufenden Flugs (null = aus oder Sprachschleife aus).</summary>
    public FirstOfficerStatus? FirstOfficer { get; init; }

    /// <summary>Smalltalk: Anbieter ("off", "claude", "openai", "ollama").</summary>
    public string? Smalltalk { get; init; }

    /// <summary>Sprache des Smalltalks gerade ("de", "en").</summary>
    public string? SmalltalkLanguage { get; init; }

    /// <summary>Kurzer Hinweis zum Smalltalk (z. B. „keine deutsche Stimme – Smalltalk auf Englisch“), in der eingestellten Sprache.</summary>
    public string? SmalltalkNote { get; init; }

    /// <summary>Smalltalk wartet gerade auf die Antwort des Sprachmodells.</summary>
    public bool SmalltalkPending { get; init; }

    /// <summary>Letzte Antwort des Smalltalks.</summary>
    public string? LastSmalltalk { get; init; }
}

/// <summary>Der First Officer für Dashboard und Status.</summary>
/// <param name="Voice">Seine Stimme: mehrsprachig ("supertonic:M1", spricht alles) oder Kokoro ("bf_emma", spricht alles auf Englisch).</param>
/// <param name="SmalltalkVoice">Mehrsprachige Stimme, die gerade den Smalltalk spricht; null = die Kokoro-Stimme (noch nicht geladen oder Kokoro).</param>
public sealed record FirstOfficerStatus(string Name, string Gender, string Voice, string? SmalltalkVoice, bool Introduced, bool FixedName, string Flight);

/// <summary>Liefert den Zustand der Sprachschleife. In der stdio-Instanz (Claude Desktop) gibt es keine: <see cref="NoVoiceLoop"/>.</summary>
public interface IVoiceStatusSource
{
    VoiceStatus Status { get; }
}

/// <summary>Platzhalter für Instanzen ohne Sprachschleife (stdio-Modus, Tests).</summary>
public sealed class NoVoiceLoop : IVoiceStatusSource
{
    // Bei jedem Abruf neu, damit der Hinweis in der eingestellten Sprache kommt
    public VoiceStatus Status => new()
    {
        Available = false,
        State = "unavailable",
        Note = Loc.T("voice.status.trayOnly"),
    };
}

/// <summary>
/// Zähler und Pegel der Sprachschleife: Kommt überhaupt Audio an, wie laut, wie viele Sätze, was wurde verworfen.
/// Wird vom Aufnahme-Thread und vom Erkennungs-Thread beschrieben, daher mit Sperre.
/// </summary>
public sealed class VoiceDiagnostics
{
    private readonly object _lock = new();

    private DateTimeOffset? _lastAudioUtc;
    private DateTimeOffset? _firstAudioUtc;
    private double _peakSinceOpen = -120;
    private double _recentPeak = -120;
    private DateTimeOffset _recentPeakUtc;
    private int _utterances;
    private int _recognized;
    private int _dropped;
    private string? _lastDropReason;

    // Fenster seit dem letzten Lagebericht im Log
    private double _windowPeak = -120;
    private double _windowSpeechSec;
    private int _windowUtterances;
    private int _windowDropped;
    private long _windowBlocks;

    public DateTimeOffset? LastAudioUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastAudioUtc;
            }
        }
    }

    public DateTimeOffset? FirstAudioUtc
    {
        get
        {
            lock (_lock)
            {
                return _firstAudioUtc;
            }
        }
    }

    /// <summary>Höchster Pegel seit dem Öffnen des Mikrofons (−120 = bisher nur digitale Stille).</summary>
    public double PeakSinceOpen
    {
        get
        {
            lock (_lock)
            {
                return _peakSinceOpen;
            }
        }
    }

    /// <summary>Mikrofon (neu) geöffnet: "erster Block" und Stille-Erkennung beginnen von vorn.</summary>
    public void MicrophoneOpened()
    {
        lock (_lock)
        {
            _firstAudioUtc = null;
            _peakSinceOpen = -120;
        }
    }

    /// <summary>Neuer Start der Schleife: Zähler zurück.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastAudioUtc = null;
            _firstAudioUtc = null;
            _peakSinceOpen = -120;
            _recentPeak = -120;
            _utterances = _recognized = _dropped = 0;
            _lastDropReason = null;
            ResetWindow();
        }
    }

    /// <summary>Audioblock vom Mikrofon. Rückgabe: true beim allerersten Block (fürs Log).</summary>
    public bool Audio(ReadOnlySpan<float> samples, DateTimeOffset now)
    {
        var peak = MicrophoneCapture.PeakDbfs(samples);
        lock (_lock)
        {
            var first = _firstAudioUtc is null;
            _firstAudioUtc ??= now;
            _lastAudioUtc = now;
            _windowBlocks++;
            if (peak > _windowPeak)
            {
                _windowPeak = peak;
            }

            if (peak > _peakSinceOpen)
            {
                _peakSinceOpen = peak;
            }

            // Spitzenwert hält 2 s, dann zählt der aktuelle Block
            if (peak >= _recentPeak || now - _recentPeakUtc > TimeSpan.FromSeconds(2))
            {
                _recentPeak = peak;
                _recentPeakUtc = now;
            }

            return first;
        }
    }

    public void Utterance(double seconds)
    {
        lock (_lock)
        {
            _utterances++;
            _windowUtterances++;
            _windowSpeechSec += seconds;
        }
    }

    public void Recognized()
    {
        lock (_lock)
        {
            _recognized++;
        }
    }

    public void Dropped(string reason)
    {
        lock (_lock)
        {
            _dropped++;
            _windowDropped++;
            _lastDropReason = reason;
        }
    }

    /// <summary>Pegel der letzten Sekunden (null = kein Audio in den letzten 5 s).</summary>
    public double? Level(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _lastAudioUtc is { } last && now - last < TimeSpan.FromSeconds(5) ? Math.Round(_recentPeak, 1) : null;
        }
    }

    public VoiceStatus Apply(VoiceStatus status, DateTimeOffset now)
    {
        var level = Level(now);
        lock (_lock)
        {
            return status with
            {
                LastAudioUtc = _lastAudioUtc,
                LevelDbfs = level,
                Utterances = _utterances,
                Recognized = _recognized,
                Dropped = _dropped,
                LastDropReason = _lastDropReason,
            };
        }
    }

    /// <summary>Lagebericht seit dem letzten Aufruf (für das Log), danach beginnt ein neues Fenster.</summary>
    public (long Blocks, double PeakDbfs, double SpeechSec, int Utterances, int Dropped) TakeWindow()
    {
        lock (_lock)
        {
            var result = (_windowBlocks, Math.Round(_windowPeak, 1), Math.Round(_windowSpeechSec, 1), _windowUtterances, _windowDropped);
            ResetWindow();
            return result;
        }
    }

    private void ResetWindow()
    {
        _windowPeak = -120;
        _windowSpeechSec = 0;
        _windowUtterances = 0;
        _windowDropped = 0;
        _windowBlocks = 0;
    }
}
