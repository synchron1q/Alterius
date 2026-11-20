using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Alterius.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Alterius.Capture.Audio;

/// <summary>
/// Records a single WASAPI audio stream (microphone or loopback) into a temporary 48 kHz mono 16-bit WAV file
/// using NAudio resampling, pads silence across loopback idle gaps to maintain A/V sync, and encodes to Opus (.ogg).
/// Missing or disconnected devices are logged as warnings without interrupting other streams.
/// </summary>
public sealed class AudioStreamRecorder : IDisposable
{
    public const int TargetSampleRate = 48000;
    private const int SilencePadThresholdSamples = TargetSampleRate / 4; // 250 ms

    private readonly FileLogger _logger;
    private readonly string _streamName;
    private readonly bool _isLoopback;
    private readonly string? _deviceId;
    private readonly object _sync = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private IWaveProvider? _outputWaveProvider;
    private WaveFileWriter? _wavWriter;
    private WaveFormat? _captureFormat;
    private TaskCompletionSource<bool>? _stoppedTcs;
    private Stopwatch? _chunkClock;

    private string _tempWavPath = string.Empty;
    private string _outputOggPath = string.Empty;
    private byte[] _readBuffer = new byte[65536];
    private long _writtenOutputSamples;
    private bool _hasReceivedAudio;
    private bool _needsResampling;
    private bool _isActive;
    private bool _disposed;

    public AudioStreamRecorder(FileLogger logger, string streamName, bool isLoopback, string? deviceId = null)
    {
        _logger = logger;
        _streamName = streamName;
        _isLoopback = isLoopback;
        _deviceId = deviceId;
    }

    public bool IsActive => _isActive;

    public bool Start(string chunkDir)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_isActive)
            {
                return true;
            }

            _tempWavPath = Path.Combine(chunkDir, $"{_streamName}.tmp.wav");
            _outputOggPath = Path.Combine(chunkDir, $"{_streamName}.ogg");
            _writtenOutputSamples = 0;
            _hasReceivedAudio = false;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = ResolveDevice(enumerator);

                if (_device is null)
                {
                    _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] No active audio endpoint found; skipping stream for this chunk.");
                    return false;
                }

                _capture = _isLoopback
                    ? new WasapiLoopbackCapture(_device)
                    : new WasapiCapture(_device);

                _captureFormat = _capture.WaveFormat;
                _bufferedWaveProvider = new BufferedWaveProvider(_captureFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                    BufferDuration = TimeSpan.FromSeconds(5)
                };

                ISampleProvider sampleProvider = _bufferedWaveProvider.ToSampleProvider();
                if (sampleProvider.WaveFormat.Channels != 1)
                {
                    sampleProvider = new MultiChannelToMonoSampleProvider(sampleProvider);
                }

                if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
                {
                    _needsResampling = true;
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
                }
                else
                {
                    _needsResampling = false;
                }

                _outputWaveProvider = new SampleToWaveProvider16(sampleProvider);
                _wavWriter = new WaveFileWriter(_tempWavPath, _outputWaveProvider.WaveFormat);

                _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _chunkClock = Stopwatch.StartNew();
                _capture.StartRecording();
                _isActive = true;

                _logger.Info(
                    nameof(AudioStreamRecorder),
                    $"[{_streamName}] Started WASAPI {(_isLoopback ? "loopback" : "capture")} on '{_device.FriendlyName}' ({_captureFormat.SampleRate}Hz, {_captureFormat.Channels}ch -> {TargetSampleRate}Hz mono)");
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] Unavailable audio device ({ex.Message}); continuing without this stream.");
                CleanupLocked(deleteTempFile: true);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(nameof(AudioStreamRecorder), $"[{_streamName}] Unexpected failure starting audio capture.", ex);
                CleanupLocked(deleteTempFile: true);
                return false;
            }
        }
    }

    public async Task<string?> StopAndEncodeAsync(string ffmpegExePath, int bitrateKbps, CancellationToken cancellationToken = default)
    {
        WasapiCapture? capture;
        TaskCompletionSource<bool>? stoppedTcs;
        string tempWavPath;
        string outputOggPath;
        bool hasAudio;

        lock (_sync)
        {
            if (!_isActive && _capture is null)
            {
                return null;
            }

            _isActive = false;
            capture = _capture;
            stoppedTcs = _stoppedTcs;
            tempWavPath = _tempWavPath;
            outputOggPath = _outputOggPath;
        }

        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] StopRecording warning: {ex.Message}");
            }

            if (stoppedTcs is not null)
            {
                await Task.WhenAny(stoppedTcs.Task, Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false);
            }
        }

        lock (_sync)
        {
            DrainAndCloseWriterLocked();
            hasAudio = _hasReceivedAudio;
            CleanupLocked(deleteTempFile: false);
        }

        if (!hasAudio || !File.Exists(tempWavPath))
        {
            TryDeleteFile(tempWavPath);
            _logger.Info(nameof(AudioStreamRecorder), $"[{_streamName}] No audio packets captured (silence/inactive device); skipped .ogg creation.");
            return null;
        }

        if (!File.Exists(ffmpegExePath))
        {
            string fallbackWav = Path.Combine(Path.GetDirectoryName(outputOggPath) ?? string.Empty, $"{_streamName}.wav");
            TryMoveFile(tempWavPath, fallbackWav);
            _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] FFmpeg missing; preserved uncompressed WAV at {fallbackWav}");
            return fallbackWav;
        }

        bool encoded = await EncodeWavToOpusAsync(ffmpegExePath, tempWavPath, outputOggPath, bitrateKbps, cancellationToken)
            .ConfigureAwait(false);

        if (encoded && File.Exists(outputOggPath) && new FileInfo(outputOggPath).Length > 0)
        {
            TryDeleteFile(tempWavPath);
            _logger.Info(nameof(AudioStreamRecorder), $"[{_streamName}] Encoded Opus audio -> {outputOggPath}");
            return outputOggPath;
        }

        string preservedWav = Path.Combine(Path.GetDirectoryName(outputOggPath) ?? string.Empty, $"{_streamName}.wav");
        TryMoveFile(tempWavPath, preservedWav);
        _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] Opus encoding failed; preserved fallback WAV at {preservedWav}");
        return File.Exists(preservedWav) ? preservedWav : null;
    }

    private MMDevice? ResolveDevice(MMDeviceEnumerator enumerator)
    {
        try
        {
            if (!_isLoopback && !string.IsNullOrWhiteSpace(_deviceId))
            {
                try
                {
                    MMDevice specific = enumerator.GetDevice(_deviceId);
                    if (specific.State == DeviceState.Active && specific.DataFlow == DataFlow.Capture)
                    {
                        return specific;
                    }

                    specific.Dispose();
                    _logger.Warn(nameof(AudioStreamRecorder), $"Configured microphone '{_deviceId}' is not active; falling back to default capture device.");
                }
                catch (Exception ex)
                {
                    _logger.Warn(nameof(AudioStreamRecorder), $"Configured microphone '{_deviceId}' lookup failed ({ex.Message}); falling back to default.");
                }
            }

            DataFlow flow = _isLoopback ? DataFlow.Render : DataFlow.Capture;
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_isActive
                || _bufferedWaveProvider is null
                || _outputWaveProvider is null
                || _wavWriter is null
                || _captureFormat is null
                || _chunkClock is null)
            {
                return;
            }

            _hasReceivedAudio = true;

            // Pad silence if loopback paused during quiet windows so audio tracks video/events offsets.
            int incomingApproxTargetSamples = (int)Math.Round(
                (double)(e.BytesRecorded / Math.Max(1, _captureFormat.BlockAlign)) * TargetSampleRate / _captureFormat.SampleRate);
            long elapsedTargetSamples = (long)(_chunkClock.Elapsed.TotalSeconds * TargetSampleRate);
            long gapSamples = elapsedTargetSamples - (_writtenOutputSamples + incomingApproxTargetSamples);
            if (gapSamples > SilencePadThresholdSamples)
            {
                WriteSilenceSamplesLocked(gapSamples);
            }

            _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            DrainAvailableSamplesLocked(isFinalFlush: false);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] Capture stopped due to device change/error: {e.Exception.Message}");
        }

        _stoppedTcs?.TrySetResult(true);
    }

    private void DrainAndCloseWriterLocked()
    {
        if (_bufferedWaveProvider is not null && _outputWaveProvider is not null && _wavWriter is not null && _captureFormat is not null)
        {
            if (_hasReceivedAudio && _bufferedWaveProvider.BufferedBytes > 0)
            {
                // Pad a tiny tail of silence and allow ReadFully=true for final drain so resampler completes cleanly.
                _bufferedWaveProvider.ReadFully = true;
                DrainAvailableSamplesLocked(isFinalFlush: true);
            }

            if (_hasReceivedAudio && _chunkClock is not null)
            {
                long elapsedTargetSamples = (long)(_chunkClock.Elapsed.TotalSeconds * TargetSampleRate);
                long trailingGap = elapsedTargetSamples - _writtenOutputSamples;
                if (trailingGap > SilencePadThresholdSamples)
                {
                    WriteSilenceSamplesLocked(trailingGap);
                }
            }

            try
            {
                _wavWriter.Flush();
            }
            catch (Exception ex)
            {
                _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] Flush warning: {ex.Message}");
            }
        }

        _wavWriter?.Dispose();
        _wavWriter = null;
    }

    private void DrainAvailableSamplesLocked(bool isFinalFlush)
    {
        if (_bufferedWaveProvider is null || _outputWaveProvider is null || _wavWriter is null || _captureFormat is null)
        {
            return;
        }

        int blockAlign = Math.Max(1, _captureFormat.BlockAlign);
        int availableInputFrames = _bufferedWaveProvider.BufferedBytes / blockAlign;

        if (availableInputFrames <= 0)
        {
            return;
        }

        int totalOutputFrames;
        if (!_needsResampling)
        {
            totalOutputFrames = availableInputFrames;
        }
        else if (isFinalFlush)
        {
            totalOutputFrames = Math.Max(0, (int)Math.Ceiling(availableInputFrames * (double)TargetSampleRate / _captureFormat.SampleRate));
        }
        else
        {
            // Keep a 32-frame lookahead margin so WdlResamplingSampleProvider never hits short reads mid-stream.
            if (availableInputFrames < 64)
            {
                return;
            }

            totalOutputFrames = Math.Max(0, (int)Math.Floor((availableInputFrames - 32) * (double)TargetSampleRate / _captureFormat.SampleRate));
        }

        int remainingFrames = totalOutputFrames;
        while (remainingFrames > 0)
        {
            int batchFrames = Math.Min(remainingFrames, _readBuffer.Length / 2);
            int bytesRequested = batchFrames * 2;
            int bytesRead = _outputWaveProvider.Read(_readBuffer, 0, bytesRequested);
            if (bytesRead <= 0)
            {
                break;
            }

            _wavWriter.Write(_readBuffer, 0, bytesRead);
            int samplesWritten = bytesRead / 2;
            _writtenOutputSamples += samplesWritten;
            remainingFrames -= samplesWritten;
        }
    }

    private void WriteSilenceSamplesLocked(long sampleCount)
    {
        if (_wavWriter is null || sampleCount <= 0)
        {
            return;
        }

        byte[] zeros = new byte[Math.Min(_readBuffer.Length, 32768)];
        long remainingBytes = sampleCount * 2L;
        while (remainingBytes > 0)
        {
            int chunk = (int)Math.Min(remainingBytes, zeros.Length);
            _wavWriter.Write(zeros, 0, chunk);
            _writtenOutputSamples += chunk / 2;
            remainingBytes -= chunk;
        }
    }

    private async Task<bool> EncodeWavToOpusAsync(
        string ffmpegExePath,
        string tempWavPath,
        string outputOggPath,
        int bitrateKbps,
        CancellationToken cancellationToken)
    {
        try
        {
            int clampedBitrate = Math.Clamp(bitrateKbps, 16, 128);
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "-y -hide_banner -loglevel warning -i \"{0}\" -c:a libopus -b:a {1}k -vbr on -application voip \"{2}\"",
                tempWavPath,
                clampedBitrate,
                outputOggPath);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegExePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] FFmpeg Opus exit code {process.ExitCode}: {stderr.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(AudioStreamRecorder), $"[{_streamName}] FFmpeg Opus conversion error: {ex.Message}");
            return false;
        }
    }

    private void CleanupLocked(bool deleteTempFile)
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;

        _wavWriter?.Dispose();
        _wavWriter = null;

        _bufferedWaveProvider = null;
        _outputWaveProvider = null;
        _captureFormat = null;
        _chunkClock = null;
        _stoppedTcs = null;

        if (deleteTempFile && !string.IsNullOrEmpty(_tempWavPath))
        {
            TryDeleteFile(_tempWavPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryMoveFile(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }
        catch
        {
            // Best-effort fallback.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _isActive = false;
            try
            {
                _capture?.StopRecording();
            }
            catch
            {
                // Ignore during disposal.
            }

            CleanupLocked(deleteTempFile: false);
        }
    }

    /// <summary>
    /// Downmixes multi-channel IEEE float sample streams to 1-channel mono by averaging channels.
    /// </summary>
    private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = Array.Empty<float>();

        public MultiChannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = Math.Max(1, source.WaveFormat.Channels);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int requiredSourceSamples = count * _channels;
            if (_sourceBuffer.Length < requiredSourceSamples)
            {
                _sourceBuffer = new float[requiredSourceSamples];
            }

            int sourceSamplesRead = _source.Read(_sourceBuffer, 0, requiredSourceSamples);
            int framesRead = sourceSamplesRead / _channels;

            for (int frame = 0; frame < framesRead; frame++)
            {
                float sum = 0f;
                int baseIndex = frame * _channels;
                for (int ch = 0; ch < _channels; ch++)
                {
                    sum += _sourceBuffer[baseIndex + ch];
                }

                float mono = sum / _channels;
                buffer[offset + frame] = Math.Clamp(mono, -1.0f, 1.0f);
            }

            return framesRead;
        }
    }
}
