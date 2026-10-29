using System.Buffers.Binary;
using NAudio.Wave;

namespace Kopilot.Voice;

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

    /// <summary>Gerät nach Teil des Namens (ohne Groß-/Kleinschreibung). Leer oder nicht gefunden = Standardgerät (−1).</summary>
    public static (int Number, string Name, bool Found) Resolve(string? device, IReadOnlyList<string> devices)
    {
        if (!string.IsNullOrWhiteSpace(device))
        {
            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i].Contains(device.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return (i, devices[i], true);
                }
            }

            return (-1, "Standardgerät", false);
        }

        return (-1, "Standardgerät", true);
    }

    public static MicrophoneCapture Start(string? device, Action<float[]> onSamples, Action<Exception?> onStopped, out bool found)
    {
        var (number, name, ok) = Resolve(device, Devices());
        found = ok;
        var capture = new MicrophoneCapture(number, onSamples, onStopped) { DeviceName = name };
        capture._wave.StartRecording();
        return capture;
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
