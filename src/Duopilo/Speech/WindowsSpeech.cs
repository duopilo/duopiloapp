using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Kopilot.Speech;

/// <summary>
/// Sprachausgabe über die Windows-Sprachsynthese (OneCore-Stimmen) und MediaPlayer – damit lässt sich das Ausgabegerät wählen
/// (z. B. das Headset statt der Lautsprecher). Nicht threadsicher: nur vom CalloutSpeaker benutzt.
/// </summary>
internal sealed class WindowsSpeech : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private MediaPlayer _player = CreatePlayer();
    private string? _deviceId;

    public string? ActiveVoice => _synth.Voice?.DisplayName;

    public static IReadOnlyList<VoiceInfo> Voices() =>
        SpeechSynthesizer.AllVoices
            .Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language, v.Gender.ToString()))
            .OrderByDescending(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static async Task<IReadOnlyList<AudioDeviceInfo>> DevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        return devices.Where(d => d.IsEnabled).Select(d => new AudioDeviceInfo(d.Id, d.Name)).ToList();
    }

    /// <summary>Stimme, Tempo, Lautstärke und Ausgabegerät übernehmen. Ohne passende Stimme: erste englische (en-GB bevorzugt).</summary>
    public async Task ApplyAsync(SpeechSettings settings)
    {
        var all = SpeechSynthesizer.AllVoices;
        var voice = all.FirstOrDefault(v => string.Equals(v.Id, settings.Voice, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(v.DisplayName, settings.Voice, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(v => v.Language.StartsWith("en-GB", StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (voice is not null)
        {
            _synth.Voice = voice;
        }

        _synth.Options.SpeakingRate = settings.Rate;
        _synth.Options.AudioVolume = settings.Volume;

        if (!string.Equals(_deviceId, settings.Device, StringComparison.Ordinal))
        {
            // Für das Standardgerät einen neuen Player anlegen – AudioDevice lässt sich nicht auf "Standard" zurücksetzen
            _player.Dispose();
            _player = CreatePlayer();
            if (settings.Device is { } id)
            {
                _player.AudioDevice = await DeviceInformation.CreateFromIdAsync(id);
            }

            _deviceId = settings.Device;
        }
    }

    public bool HasEnglishVoice => SpeechSynthesizer.AllVoices.Any(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));

    /// <summary>Spricht den Text mit der Windows-Stimme und wartet, bis er zu Ende ist (oder abgebrochen wird).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        using var stream = await _synth.SynthesizeTextToStreamAsync(text);
        await PlayAsync(stream, stream.ContentType, ct);
    }

    /// <summary>Spielt fertiges Audio (WAV, z. B. von Kokoro) über dasselbe Ausgabegerät.</summary>
    public async Task PlayWavAsync(byte[] wav, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(wav);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        await PlayAsync(stream, "audio/wav", ct);
    }

    private async Task PlayAsync(IRandomAccessStream stream, string contentType, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) => done.TrySetResult();
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, e) => done.TrySetException(new InvalidOperationException(e.ErrorMessage));

        var player = _player;
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        try
        {
            player.Source = MediaSource.CreateFromStream(stream, contentType);
            player.Play();
            using (ct.Register(() =>
                   {
                       player.Pause();
                       done.TrySetCanceled();
                   }))
            {
                await done.Task.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            }
        }
        finally
        {
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
            player.Source = null;
        }
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
    }

    private static MediaPlayer CreatePlayer() => new()
    {
        AudioCategory = MediaPlayerAudioCategory.Speech,
        AutoPlay = false,
    };
}
