using Alterius.Core;
using Alterius.Models;
using NAudio.CoreAudioApi;

namespace Alterius.Capture.Audio;

/// <summary>
/// Coordinates simultaneous capture of microphone (<c>audio_mic.ogg</c>) and
/// system loopback audio (<c>audio_sys.ogg</c>) for each chunk.
/// </summary>
public sealed class DualAudioRecorder : IDisposable
{
    private readonly FileLogger _logger;
    private AudioStreamRecorder? _micRecorder;
    private AudioStreamRecorder? _sysRecorder;
    private bool _disposed;

    public DualAudioRecorder(FileLogger logger)
    {
        _logger = logger;
    }

    public static List<AudioDeviceInfo> GetCaptureDevices(FileLogger? logger = null)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Warn(nameof(DualAudioRecorder), $"Failed to enumerate audio capture devices: {ex.Message}");
        }

        return result;
    }

    public void Start(string chunkDir, AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (config.RecordMicrophone)
        {
            _micRecorder = new AudioStreamRecorder(_logger, "audio_mic", isLoopback: false, config.MicrophoneDeviceId);
            _micRecorder.Start(chunkDir);
        }
        else
        {
            _micRecorder = null;
        }

        if (config.RecordSystemAudio)
        {
            _sysRecorder = new AudioStreamRecorder(_logger, "audio_sys", isLoopback: true);
            _sysRecorder.Start(chunkDir);
        }
        else
        {
            _sysRecorder = null;
        }
    }

    public async Task StopAndEncodeAllAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        AudioStreamRecorder? mic = _micRecorder;
        AudioStreamRecorder? sys = _sysRecorder;
        _micRecorder = null;
        _sysRecorder = null;

        string ffmpegExe = AppPaths.ResolveFfmpegPath(config.FfmpegPath);
        int bitrate = config.AudioBitrateKbps;

        var tasks = new List<Task>(2);
        if (mic is not null)
        {
            tasks.Add(StopSingleAsync(mic, ffmpegExe, bitrate, cancellationToken));
        }

        if (sys is not null)
        {
            tasks.Add(StopSingleAsync(sys, ffmpegExe, bitrate, cancellationToken));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task StopSingleAsync(
        AudioStreamRecorder recorder,
        string ffmpegExe,
        int bitrateKbps,
        CancellationToken cancellationToken)
    {
        try
        {
            await recorder.StopAndEncodeAsync(ffmpegExe, bitrateKbps, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            recorder.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _micRecorder?.Dispose();
        _sysRecorder?.Dispose();
        _micRecorder = null;
        _sysRecorder = null;
    }
}
