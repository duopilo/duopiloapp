using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kopilot.Speech;

/// <summary>Wiedergabe über WinMM (NAudio WaveOutEvent). Läuft neben MSFS, ohne den Sim-Sound zu stören.</summary>
public sealed class WaveOutAudio(IOptions<KopilotOptions> options, ILogger<WaveOutAudio> log) : IAudioOutput
{
    private readonly SpeechOptions _options = options.Value.Speech;
    private readonly object _lock = new();
    private int? _device;
    private string _deviceName = "Standard";

    public IReadOnlyList<string> Devices
    {
        get
        {
            try
            {
                return Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();
            }
            catch (Exception ex) when (ex is NAudio.MmException or DllNotFoundException or EntryPointNotFoundException)
            {
                return [];
            }
        }
    }

    public string DeviceName
    {
        get
        {
            ResolveDevice();
            return _deviceName;
        }
    }

    public async Task PlayAsync(SpeechClip clip, float volume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (clip.Samples.Length == 0)
        {
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new WaveOutEvent { DeviceNumber = ResolveDevice(), DesiredLatency = 120 };
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                done.TrySetException(e.Exception);
            }
            else
            {
                done.TrySetResult();
            }
        };

        var source = new VolumeSampleProvider(new ArraySampleProvider(clip.Samples, clip.SampleRate)) { Volume = Math.Clamp(volume, 0f, 1f) };
        output.Init(source, convertTo16Bit: true);
        output.Play();

        await using (ct.Register(() => output.Stop()))
        {
            await done.Task;
        }

        ct.ThrowIfCancellationRequested();
    }

    private int ResolveDevice()
    {
        lock (_lock)
        {
            if (_device is { } known)
            {
                return known;
            }

            var wanted = _options.OutputDevice.Trim();
            _device = -1;
            _deviceName = "Standard";
            if (wanted.Length == 0)
            {
                return -1;
            }

            var devices = Devices;
            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i].Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    _device = i;
                    _deviceName = devices[i];
                    return i;
                }
            }

            log.LogWarning("Ausgabegerät \"{Wanted}\" nicht gefunden – nutze das Windows-Standardgerät. Vorhanden: {Devices}", wanted, string.Join(", ", devices));
            return -1;
        }
    }

    /// <summary>Spielt ein fertiges Sample-Array einmal ab.</summary>
    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            if (n <= 0)
            {
                return 0;
            }

            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
