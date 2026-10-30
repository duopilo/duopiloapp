using System.Text.Json;
using System.Text.Json.Serialization;
using Duopilo.Localization;
using Duopilo.Speech;

namespace Duopilo.Voice;

/// <summary>
/// Einstellungen der Sprachschleife aus voice-settings.json. Die Datei liegt im Datenordner
/// (%LOCALAPPDATA%\Duopilo\voice-settings.json) und wird beim ersten Start aus der Vorlage neben der Duopilo.exe angelegt –
/// so überschreibt update.cmd sie nicht. Änderungen gelten nach einem Neustart von Duopilo.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>Sprachschleife an (Mikrofon dauerhaft offen). Standard: aus.</summary>
    public bool Enabled { get; init; }

    public MicrophoneSettings Microphone { get; init; } = new();

    public RecognitionSettings Recognition { get; init; } = new();

    public VoicesSettings Voices { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();

    public CommandSettings Commands { get; init; } = new();

    public AtcSettings Atc { get; init; } = new();

    public CalloutSettings Callouts { get; init; } = new();

    /// <summary>Bestätigungspflicht: wichtige Meldungen erwarten "check"/"copy".</summary>
    public AcknowledgeSettings Acknowledge { get; init; } = new();

    public VoiceSettings Normalized() => this with
    {
        Microphone = (Microphone ?? new()).Normalized(),
        Recognition = (Recognition ?? new()).Normalized(),
        Voices = (Voices ?? new()).Normalized(),
        Hotkeys = Hotkeys ?? new(),
        Commands = (Commands ?? new()).Normalized(),
        Atc = (Atc ?? new()).Normalized(),
        Callouts = (Callouts ?? new()).Normalized(),
        Acknowledge = (Acknowledge ?? new()).Normalized(),
    };

    internal static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    internal static List<string> CleanList(IEnumerable<string>? items) =>
        (items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Mikrofon und Spracherkennung (Voice Activity Detection mit Silero).</summary>
public sealed record MicrophoneSettings
{
    /// <summary>Mikrofon: Teil des Gerätenamens (z. B. "Headset" oder "Quest"). Leer = Windows-Standardgerät.</summary>
    public string Device { get; init; } = "";

    /// <summary>VAD-Schwelle 0,1–0,95: ab dieser Sprachwahrscheinlichkeit gilt ein 32-ms-Block als Sprache. Kleiner = empfindlicher.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>So viel Sprache muss mindestens zusammenkommen, sonst war es ein Geräusch.</summary>
    public int MinSpeechMs { get; init; } = 200;

    /// <summary>So lange Stille beendet einen Satz.</summary>
    public int SilenceMs { get; init; } = 700;

    /// <summary>So viel Audio vor dem Sprachbeginn wird mitgenommen (der erste Laut geht sonst verloren).</summary>
    public int PreRollMs { get; init; } = 300;

    /// <summary>Längere Sätze werden hier abgeschnitten.</summary>
    public double MaxUtteranceSec { get; init; } = 12;

    /// <summary>Nach einer Ansage bleibt das Mikrofon noch so lange stumm (Nachhall, Lautsprecher).</summary>
    public int EchoTailMs { get; init; } = 350;

    public MicrophoneSettings Normalized() => this with
    {
        Device = Device?.Trim() ?? "",
        Threshold = VoiceSettings.Clamp(Threshold, 0.1, 0.95, 0.5),
        MinSpeechMs = Math.Clamp(MinSpeechMs, 64, 2000),
        SilenceMs = Math.Clamp(SilenceMs, 200, 3000),
        PreRollMs = Math.Clamp(PreRollMs, 0, 1000),
        MaxUtteranceSec = VoiceSettings.Clamp(MaxUtteranceSec, 2, 30, 12),
        EchoTailMs = Math.Clamp(EchoTailMs, 0, 2000),
    };
}

/// <summary>Whisper (whisper.cpp über Whisper.net).</summary>
public sealed record RecognitionSettings
{
    /// <summary>Modell: tiny, base, small – jeweils auch quantisiert (-q5_1, -q8_0). Mehrsprachig, also ohne ".en".</summary>
    public string Model { get; init; } = "base-q5_1";

    /// <summary>Eigene Modelldatei (ggml-*.bin). Leer = %LOCALAPPDATA%\Duopilo\models, wird bei Bedarf geladen.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>"cpu" oder "gpu" (CUDA, sonst Vulkan; ohne passende GPU-Laufzeit zurück auf die CPU).</summary>
    public string Device { get; init; } = "cpu";

    /// <summary>CPU-Threads für Whisper.</summary>
    public int Threads { get; init; } = 4;

    /// <summary>"auto" (Deutsch oder Englisch je Satz), "de" oder "en".</summary>
    public string Language { get; init; } = "auto";

    /// <summary>Wortschatz-Hinweis für Whisper. Leer = eingebaute Funk- und Cockpit-Begriffe.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Audio-Kontext für Whisper (0 = voll, 30 s). Kleiner = schneller, z. B. 768 für Sätze bis 15 s.</summary>
    public int AudioContext { get; init; }

    public static readonly IReadOnlyList<string> KnownModels =
    [
        "tiny", "tiny-q5_1", "tiny-q8_0", "base", "base-q5_1", "base-q8_0", "small", "small-q5_1", "small-q8_0",
    ];

    public RecognitionSettings Normalized()
    {
        var model = (Model ?? "").Trim().ToLowerInvariant();
        var device = (Device ?? "").Trim().ToLowerInvariant();
        var language = (Language ?? "").Trim().ToLowerInvariant();
        return this with
        {
            Model = KnownModels.Contains(model) ? model : "base-q5_1",
            ModelPath = ModelPath?.Trim() ?? "",
            Device = device is "gpu" or "cuda" or "vulkan" ? "gpu" : "cpu",
            Threads = Math.Clamp(Threads, 1, 16),
            Language = language is "de" or "en" ? language : "auto",
            Prompt = Prompt?.Trim() ?? "",
            AudioContext = AudioContext <= 0 ? 0 : Math.Clamp(AudioContext, 256, 1500),
        };
    }
}

/// <summary>Stimmen und Lautstärke.</summary>
public sealed record VoicesSettings
{
    /// <summary>Feste Kokoro-Stimme des Kopiloten (Callouts und Antworten; Schlüssel "kopilot" = die Rolle, bleibt so). Leer = Stimme des First Officers, ist er aus: die aus dem Dashboard.</summary>
    public string Kopilot { get; init; } = "";

    /// <summary>Lautstärke des Kopiloten (Faktor auf die Lautstärke im Dashboard).</summary>
    public double KopilotVolume { get; init; } = 1.0;

    /// <summary>Lautstärke der ATC-Stationen (Faktor auf die Lautstärke im Dashboard). Die Stimmen vergibt das ATC-Modul.</summary>
    public double AtcVolume { get; init; } = 1.0;

    public VoicesSettings Normalized() => this with
    {
        Kopilot = KokoroSpeechEngine.IsEnglishVoice(Kopilot?.Trim()) ? Kopilot!.Trim() : "",
        KopilotVolume = VoiceSettings.Clamp(KopilotVolume, 0, 1, 1),
        AtcVolume = VoiceSettings.Clamp(AtcVolume, 0, 1, 1),
    };
}

/// <summary>Tastenkürzel (systemweit, auch wenn MSFS im Vordergrund ist).</summary>
public sealed record HotkeySettings
{
    /// <summary>Spracheingabe stumm/an, z. B. "Ctrl+Alt+K". Leer = kein Tastenkürzel.</summary>
    public string Mute { get; init; } = "Ctrl+Alt+K";
}

/// <summary>Befehle an den Kopiloten.</summary>
public sealed record CommandSettings
{
    /// <summary>
    /// Anrede für den Kopiloten, z. B. "Kopilot, Gear up" – „Kopilot“ bleibt das feste Ansprachewort. „Duopilo“ zählt zusätzlich,
    /// wenn Whisper es eindeutig so abschreibt (<see cref="VoiceText"/> macht "kopilot" daraus). Optional, außer <see cref="RequireWakeWord"/>.
    /// </summary>
    public List<string> WakeWords { get; init; } = ["kopilot", "copilot"];

    /// <summary>Befehle nur mit Anrede ("Kopilot, …") – hilft, wenn im Raum geredet wird.</summary>
    public bool RequireWakeWord { get; init; }

    /// <summary>So lange wartet ein Readback auf "Confirm" / "Correct" / "Affirm".</summary>
    public int ConfirmTimeoutSec { get; init; } = 60;

    /// <summary>Unbekannte Sätze bis zu dieser Länge bekommen "Say again"; längere (Gespräch im Raum) werden still verworfen.</summary>
    public int SayAgainMaxWords { get; init; } = 8;

    /// <summary>
    /// Modus "ausführen + Callout": sicher erkannte Befehle sofort ausführen und nach der Prüfung ansagen ("Speed 250 set").
    /// Readback mit "Confirm" nur bei unsicherer Erkennung und bei Aktionen, die eine Rückfrage brauchen. Standard: an.
    /// </summary>
    public bool ExecuteWithCallout { get; init; } = true;

    /// <summary>Konfidenz der Erkennung (0–1, Mittel der Whisper-Token-Wahrscheinlichkeiten), darunter Readback mit "Confirm".</summary>
    public double MinConfidence { get; init; } = 0.6;

    public CommandSettings Normalized() => this with
    {
        WakeWords = VoiceSettings.CleanList(WakeWords).Select(w => VoiceText.Normalize(w)).Where(w => w.Length > 0).Distinct().ToList() is { Count: > 0 } wake
            ? wake
            : ["kopilot"],
        ConfirmTimeoutSec = Math.Clamp(ConfirmTimeoutSec, 10, 300),
        SayAgainMaxWords = Math.Clamp(SayAgainMaxWords, 0, 50),
        MinConfidence = VoiceSettings.Clamp(MinConfidence, 0, 1, 0.6),
    };
}

/// <summary>
/// Bestätigungspflicht: Wichtige Meldungen erwarten "check" oder "copy" des Piloten. Ohne Antwort nach
/// <see cref="RepeatAfterSec"/> einmal wiederholen, danach Warnton und Eintrag im Dashboard – nie endlos wiederholen.
/// </summary>
public sealed record AcknowledgeSettings
{
    /// <summary>Standard: Fuel-Warnung, Sicherheits-Cautions und -Warnungen, Squawk-/Frequenzabweichung, TCAS RA.</summary>
    public static readonly IReadOnlyList<string> DefaultRules =
    [
        "FUEL_WARNING", "ATC_SQUAWK", "ATC_FREQUENCY", "TCAS_RA",
        "MASTER_WARNING", "MASTER_CAUTION", "STALL", "OVERSPEED", "GEAR_SPEED", "FLAP_SPEED", "SINK_RATE", "BANK_ANGLE",
        "GEAR_DOWN_HIGH", "SPEED_LIMIT_FL100", "SPEED_DEVIATION", "TRAFFIC_PROXIMITY", "LANDING_CHECK",
        "ATHR_OFF", "APPR_NOT_ACTIVE", "LOC_DEVIATION", "GS_DEVIATION",
    ];

    public bool Enabled { get; init; } = true;

    /// <summary>Wartezeit bis zur Wiederholung und noch einmal bis zur Eskalation (s).</summary>
    public double RepeatAfterSec { get; init; } = 10;

    /// <summary>Regeln, die eine Quittung brauchen (Meldungen ab Caution). Leer = <see cref="DefaultRules"/>.</summary>
    public List<string> Rules { get; init; } = [.. DefaultRules];

    public AcknowledgeSettings Normalized() => this with
    {
        RepeatAfterSec = VoiceSettings.Clamp(RepeatAfterSec, 5, 60, 10),
        Rules = VoiceSettings.CleanList(Rules).Select(r => r.ToUpperInvariant()).ToList() is { Count: > 0 } rules ? rules : [.. DefaultRules],
    };
}

/// <summary>
/// Zuordnung zum ATC-Modul. Rufzeichen und Stationen des Flugs kommen schon vom ATC-Modul (SimBrief-Plan, atc-templates.json) –
/// hier nur Ergänzungen.
/// </summary>
public sealed record AtcSettings
{
    /// <summary>Weitere eigene Rufzeichen in gesprochener Form, z. B. "Lufthansa 4AB" oder "DLH4AB".</summary>
    public List<string> Callsigns { get; init; } = [];

    /// <summary>Weitere Stationsnamen, die der Router als ATC erkennen soll (z. B. "Langen Information").</summary>
    public List<string> Stations { get; init; } = [];

    /// <summary>Rufzeichen auch aus MSFS lesen (ATC AIRLINE + ATC FLIGHT NUMBER, ATC ID).</summary>
    public bool UseSimCallsign { get; init; } = true;

    public AtcSettings Normalized() => this with
    {
        Callsigns = VoiceSettings.CleanList(Callsigns),
        Stations = VoiceSettings.CleanList(Stations),
    };
}

/// <summary>Proaktive Callouts.</summary>
public sealed record CalloutSettings
{
    /// <summary>Monitoring-Callouts sprechen.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gesprochene Regeln (IDs wie "PM_V1"). "*" = alle.</summary>
    public List<string> Active { get; init; } = ["*"];

    /// <summary>Nie gesprochene Regeln.</summary>
    public List<string> Muted { get; init; } = [];

    /// <summary>Sim pausiert → nichts sprechen.</summary>
    public bool SilentWhenPaused { get; init; } = true;

    public TaxiSpeedSettings TaxiSpeed { get; init; } = new();

    public CalloutSettings Normalized() => this with
    {
        Active = VoiceSettings.CleanList(Active).Select(r => r.ToUpperInvariant()).ToList() is { Count: > 0 } active ? active : ["*"],
        Muted = VoiceSettings.CleanList(Muted).Select(r => r.ToUpperInvariant()).ToList(),
        TaxiSpeed = (TaxiSpeed ?? new()).Normalized(),
    };
}

/// <summary>Taxi-Speed-Warnung (Regel TAXI_SPEED).</summary>
public sealed record TaxiSpeedSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Ab dieser Groundspeed am Boden (kt).</summary>
    public double ThresholdKt { get; init; } = 25;

    /// <summary>Frühestens nach so vielen Sekunden erneut.</summary>
    public double CooldownSec { get; init; } = 30;

    public TaxiSpeedSettings Normalized() => this with
    {
        ThresholdKt = VoiceSettings.Clamp(ThresholdKt, 5, 60, 25),
        CooldownSec = VoiceSettings.Clamp(CooldownSec, 5, 600, 30),
    };
}

/// <summary>
/// Lädt voice-settings.json aus dem Datenordner. Fehlt die Datei, wird sie aus der Vorlage neben der Duopilo.exe angelegt.
/// JSON mit Kommentaren und Komma am Ende ist erlaubt.
/// </summary>
public sealed class VoiceSettingsStore(string dataDirectory, string? templateDirectory = null)
{
    public const string FileName = "voice-settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string FilePath { get; } = Path.Combine(dataDirectory, FileName);

    public string TemplatePath { get; } = Path.Combine(templateDirectory ?? AppContext.BaseDirectory, FileName);

    /// <summary>Einstellungen und ggf. ein Fehler (dann gelten die Standardwerte, die Datei bleibt unangetastet).</summary>
    public (VoiceSettings Settings, string? Error) Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(TemplatePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(TemplatePath, FilePath, overwrite: false);
            }

            if (!File.Exists(FilePath))
            {
                return (new VoiceSettings().Normalized(), null);
            }

            return (Parse(File.ReadAllText(FilePath)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new VoiceSettings().Normalized(), $"{FileName}: {ex.Message}");
        }
    }

    public static VoiceSettings Parse(string json) =>
        (JsonSerializer.Deserialize<VoiceSettings>(json, Options) ?? new VoiceSettings()).Normalized();

    /// <summary>
    /// Schalter und Mikrofon ändern (Dashboard). Nur diese Werte werden in der Datei ersetzt – Kommentare und alle anderen
    /// Einstellungen bleiben, wie sie sind. null = unverändert lassen. Geschrieben wird erst, wenn das Ergebnis gültig ist.
    /// </summary>
    public VoiceSettings Update(bool? enabled, string? microphone)
    {
        if (!File.Exists(FilePath))
        {
            // Legt die Datei aus der Vorlage an (oder es bleibt bei den Standardwerten ohne Datei)
            Load();
        }

        var text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{\n}\n";
        if (enabled is { } on)
        {
            text = JsonTextPatcher.Set(text, ["enabled"], on ? "true" : "false");
        }

        if (microphone is not null)
        {
            text = JsonTextPatcher.Set(text, ["microphone", "device"], JsonTextPatcher.Quote(microphone.Trim()));
        }

        var parsed = Parse(text);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
        return parsed;
    }

    /// <summary>
    /// Hinweis, wenn die Vorlage neben der Duopilo.exe bearbeitet wurde, die wirksame Datei im Datenordner aber nicht:
    /// update.cmd überschreibt die Vorlage, und Duopilo liest nur die Datei im Datenordner. null = alles in Ordnung.
    /// </summary>
    public string? TemplateMismatch(VoiceSettings effective)
    {
        try
        {
            if (!File.Exists(TemplatePath) || string.Equals(Path.GetFullPath(TemplatePath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var template = Parse(File.ReadAllText(TemplatePath));
            if (template.Enabled && !effective.Enabled)
            {
                return Loc.T("voice.settings.templateEnabled", TemplatePath, FilePath);
            }

            if (template.Microphone.Device.Length > 0 && template.Microphone.Device != effective.Microphone.Device)
            {
                return Loc.T("voice.settings.templateMicrophone", template.Microphone.Device, TemplatePath, FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Vorlage kaputt oder gesperrt – kein Hinweis
        }

        return null;
    }
}
