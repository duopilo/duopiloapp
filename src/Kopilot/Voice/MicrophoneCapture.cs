using System.Buffers.Binary;
using Kopilot.Localization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kopilot.Voice;

/// <summary>Ein Aufnahmegerät, wie WinMM es kennt (Name auf 31 Zeichen gekürzt), dazu der volle Windows-Name, wenn bekannt.</summary>
/// <param name="Number">WinMM-Gerätenummer (ändert sich beim Ein- und Ausstecken).</param>
/// <param name="Name">WinMM-Name – den in voice-settings.json unter microphone.device eintragen (ein Teil reicht).</param>
/// <param name="FullName">Voller Name aus Windows (Core Audio), sonst gleich <paramref name="Name"/>.</param>
/// <param name="IsDefault">Windows-Standardaufnahmegerät.</param>
public sealed record MicrophoneDevice(int Number, string Name, string FullName, bool IsDefault);

/// <summary>
/// Mikrofon über WinMM (NAudio): 16 kHz, Mono, 16 Bit – Windows rechnet vom Gerät herunter.
/// Liefert die Samples als float (−1…1) in Blöcken von ca. 64 ms an den Rückruf (auf dem Aufnahme-Thread).
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly WaveInEvent _wave;
    private readonly Action<float[]> _onSamples;
    private readonly Action<Exception?> _onStopped;

    private MicrophoneCapture(int deviceNumber, Action<float[]> onSamples, Action<Exception?> onStopped)
    {
        _onSamples = onSamples;
        _onStopped = onStopped;
        _wave = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SegmenterOptions.SampleRate, 16, 1),
            BufferMilliseconds = 64,
            NumberOfBuffers = 4,
        };
        _wave.DataAvailable += OnData;
        _wave.RecordingStopped += (_, e) => _onStopped(e.Exception);
    }

    /// <summary>Name des verwendeten Geräts.</summary>
    public string DeviceName { get; private init; } = "";

    /// <summary>WinMM-Nummer des Geräts (−1 = Windows-Standardgerät).</summary>
    public int DeviceNumber => _wave.DeviceNumber;

    /// <summary>Alle Aufnahmegeräte (WinMM kürzt die Namen auf 31 Zeichen).</summary>
    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }

        return list;
    }

    /// <summary>Aufnahmegeräte mit vollem Namen und Markierung des Windows-Standardgeräts (für Dashboard, Status und Log).</summary>
    public static IReadOnlyList<MicrophoneDevice> DeviceList()
    {
        var names = Devices();
        var (defaultName, fullNames) = CoreAudioNames();
        var list = new List<MicrophoneDevice>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var full = fullNames.FirstOrDefault(f => f.StartsWith(names[i], StringComparison.OrdinalIgnoreCase)) ?? names[i];
            var isDefault = defaultName is not null && defaultName.StartsWith(names[i], StringComparison.OrdinalIgnoreCase);
            list.Add(new MicrophoneDevice(i, names[i], full, isDefault));
        }

        return list;
    }

    /// <summary>Voller Name des Windows-Standardaufnahmegeräts (null = keins oder unbekannt).</summary>
    public static string? DefaultDeviceName() => CoreAudioNames().Default;

    /// <summary>Gerät nach Teil des Namens (ohne Groß-/Kleinschreibung). Leer oder nicht gefunden = Standardgerät (−1).</summary>
    public static (int Number, string Name, bool Found) Resolve(string? device, IReadOnlyList<string> devices)
    {
        if (!string.IsNullOrWhiteSpace(device))
        {
            for (var i = 0; i < devices.Count; i++)
            {
                // Teil des Namens – oder der volle Windows-Name, den WinMM auf 31 Zeichen kürzt
                if (devices[i].Contains(device.Trim(), StringComparison.OrdinalIgnoreCase)
                    || (devices[i].Length > 0 && device.Trim().StartsWith(devices[i], StringComparison.OrdinalIgnoreCase)))
                {
                    return (i, devices[i], true);
                }
            }

            return (-1, Loc.T("voice.mic.defaultDevice"), false);
        }

        return (-1, Loc.T("voice.mic.defaultDevice"), true);
    }

    public static MicrophoneCapture Start(string? device, Action<float[]> onSamples, Action<Exception?> onStopped, out bool found)
    {
        var (number, name, ok) = Resolve(device, Devices());
        found = ok;
        if (number < 0 && DefaultDeviceName() is { } defaultName)
        {
            name = Loc.T("voice.mic.defaultDeviceNamed", defaultName);
        }

        var capture = new MicrophoneCapture(number, onSamples, onStopped) { DeviceName = name };
        capture._wave.StartRecording();
        return capture;
    }

    /// <summary>Spitzenpegel eines Blocks in dBFS (−120 bei völliger Stille).</summary>
    public static double PeakDbfs(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak <= 1e-6f ? -120 : Math.Max(-120, 20 * Math.Log10(peak));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        if (count <= 0)
        {
            return;
        }

        var samples = new float[count];
        var span = e.Buffer.AsSpan(0, count * 2);
        for (var i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(i * 2)..]) / 32768f;
        }

        _onSamples(samples);
    }

    /// <summary>Namen aus Core Audio (WASAPI): Standardgerät und alle aktiven Aufnahmegeräte. Fehler = keine Namen.</summary>
    private static (string? Default, IReadOnlyList<string> All) CoreAudioNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultName = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultName = d.FriendlyName;
            }

            var all = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    all.Add(device.FriendlyName);
                }
            }

            return (defaultName, all);
        }
        catch (Exception)
        {
            // Core Audio nicht verfügbar (z. B. Testlauf ohne Audiodienst) – dann nur die WinMM-Namen
            return (null, []);
        }
    }

    public void Dispose()
    {
        try
        {
            _wave.StopRecording();
        }
        catch (Exception)
        {
            // Gerät schon weg
        }

        _wave.DataAvailable -= OnData;
        _wave.Dispose();
    }
}
