using System.Diagnostics;
using Alterius.Capture.Audio;
using Alterius.Capture.InputTelemetry;
using Alterius.Capture.Video;
using Alterius.Models;
using Alterius.Upload;

namespace Alterius.Core;

/// <summary>
/// Orchestrates chunked screen, dual audio, and input telemetry recording, seamless chunk rotation,
/// graceful shutdown finalization, and local buffer overflow handling.
/// </summary>
public sealed class RecordingCoordinator : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly LocalQueue _localQueue;
    private readonly SmbUploadWorker _uploadWorker;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _finalizationLock = new();
    private readonly List<Task> _inFlightFinalizations = new();

    private ActiveChunkContext? _currentChunk;
    private InputTelemetryRecorder? _telemetryRecorder;
    private System.Threading.Timer? _chunkTimer;
    private Stopwatch? _sessionStopwatch;
    private string? _activeSessionId;
    private int _chunkCounter;
    private volatile RecordingState _state = RecordingState.Idle;
    private bool _disposed;

    public RecordingCoordinator(
        ConfigManager configManager,
        LocalQueue localQueue,
        SmbUploadWorker uploadWorker,
        FileLogger logger)
    {
        _configManager = configManager;
        _localQueue = localQueue;
        _uploadWorker = uploadWorker;
        _logger = logger;
    }

    public RecordingState State => _state;

    public string? ActiveSessionId => _activeSessionId;

    public TimeSpan SessionElapsed => _state == RecordingState.Recording && _sessionStopwatch is not null
        ? _sessionStopwatch.Elapsed
        : TimeSpan.Zero;

    public event Action<RecordingState>? StateChanged;

    public event Action<string, bool>? NotificationRequested;

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == RecordingState.Recording || _state == RecordingState.Stopping)
            {
                return;
            }

            AppConfig config = _configManager.Current;
            AppPaths.EnsureDirectoriesExist(config.LocalBufferDir);

            _localQueue.EnforceBufferLimit(config, activeChunkDir: null, out _, out bool shouldPause);
            if (shouldPause)
            {
                SetState(RecordingState.PausedBufferFull);
                string msg = $"Локальный буфер превысил лимит {config.MaxLocalBufferGb:F1} ГБ. Освободите место или включите выгрузку на SMB.";
                _logger.Warn(nameof(RecordingCoordinator), msg);
                NotificationRequested?.Invoke(msg, true);
                return;
            }

            string ffmpegExe = AppPaths.ResolveFfmpegPath(config.FfmpegPath);
            if (!File.Exists(ffmpegExe))
            {
                throw new FileNotFoundException(
                    $"Не найден ffmpeg.exe по пути '{ffmpegExe}'. Поместите ffmpeg.exe в bin\\ffmpeg\\ffmpeg.exe или укажите путь в настройках.",
                    ffmpegExe);
            }

            _activeSessionId = $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
            _chunkCounter = 1;

            (ActiveChunkContext chunk, InputTelemetryRecorder telemetry) = await StartFirstChunkAsync(
                _activeSessionId,
                _chunkCounter,
                config,
                cancellationToken).ConfigureAwait(false);

            _currentChunk = chunk;
            _telemetryRecorder = telemetry;
            _uploadWorker.SetActiveChunkDirectory(chunk.ChunkDir);

            _sessionStopwatch = Stopwatch.StartNew();
            ScheduleNextChunkTimer(config.ChunkDurationMinutes);

            SetState(RecordingState.Recording);
            _logger.Info(nameof(RecordingCoordinator), $"Recording started. SessionId={_activeSessionId}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopRecordingAsync(bool pauseDueToBufferFull = false)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        List<Task> pendingFinalizations;

        try
        {
            if (_state == RecordingState.Idle)
            {
                if (pauseDueToBufferFull)
                {
                    SetState(RecordingState.PausedBufferFull);
                }

                return;
            }

            if (_state == RecordingState.PausedBufferFull && !pauseDueToBufferFull)
            {
                SetState(RecordingState.Idle);
                return;
            }

            SetState(RecordingState.Stopping);
            StopChunkTimer();
            _sessionStopwatch?.Stop();

            ActiveChunkContext? closingChunk = _currentChunk;
            _currentChunk = null;

            InputTelemetryRecorder? telemetry = _telemetryRecorder;
            _telemetryRecorder = null;

            List<TopAppEntry> closingTopApps = new();
            if (telemetry is not null)
            {
                try
                {
                    closingTopApps = await telemetry.StopSessionAsync().ConfigureAwait(false);
                }
                finally
                {
                    telemetry.Dispose();
                }
            }

            if (closingChunk is not null)
            {
                await FinalizeChunkAsync(closingChunk, closingTopApps, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_finalizationLock)
            {
                pendingFinalizations = _inFlightFinalizations.ToList();
            }

            if (pendingFinalizations.Count > 0)
            {
                await Task.WhenAll(pendingFinalizations).ConfigureAwait(false);
            }

            _uploadWorker.SetActiveChunkDirectory(null);
            _activeSessionId = null;

            RecordingState nextState = pauseDueToBufferFull ? RecordingState.PausedBufferFull : RecordingState.Idle;
            SetState(nextState);
            _logger.Info(nameof(RecordingCoordinator), $"Recording stopped cleanly. FinalState={nextState}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task PauseDueToBufferLimitAsync(string reason)
    {
        if (_state != RecordingState.Recording)
        {
            return;
        }

        _logger.Warn(nameof(RecordingCoordinator), $"Pausing recording due to buffer limit: {reason}");
        await StopRecordingAsync(pauseDueToBufferFull: true).ConfigureAwait(false);
        NotificationRequested?.Invoke(reason, true);
    }

    private void OnChunkTimerElapsed(object? _)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RotateChunkAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(nameof(RecordingCoordinator), "Chunk rotation failed.", ex);
                NotificationRequested?.Invoke($"Ошибка ротации чанка: {ex.Message}", true);
            }
        });
    }

    private async Task RotateChunkAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != RecordingState.Recording || _currentChunk is null || _telemetryRecorder is null || string.IsNullOrEmpty(_activeSessionId))
            {
                return;
            }

            AppConfig config = _configManager.Current;
            _localQueue.EnforceBufferLimit(config, _currentChunk.ChunkDir, out _, out bool shouldPause);
            if (shouldPause)
            {
                _logger.Warn(nameof(RecordingCoordinator), "Buffer quota reached during chunk rotation; pausing recording.");
                _ = Task.Run(() => PauseDueToBufferLimitAsync(
                    $"Локальный буфер превысил лимит {config.MaxLocalBufferGb:F1} ГБ. Запись поставлена на паузу."));
                return;
            }

            ActiveChunkContext prevChunk = _currentChunk;
            int nextIndex = _chunkCounter + 1;
            DateTime nextStartUtc = DateTime.UtcNow;
            string nextChunkDir = BuildChunkDirectoryPath(config.LocalBufferDir, _activeSessionId, nextIndex, nextStartUtc);
            Directory.CreateDirectory(nextChunkDir);

            Rectangle nextBounds = FfmpegVideoRecorder.GetCaptureBounds(config.CaptureAllMonitors);
            string nextEventsPath = Path.Combine(nextChunkDir, "events.jsonl");

            List<TopAppEntry> prevTopApps = await _telemetryRecorder.RotateChunkAsync(
                nextEventsPath,
                nextBounds,
                nextStartUtc,
                CancellationToken.None).ConfigureAwait(false);

            var nextVideo = new FfmpegVideoRecorder(_logger);
            var nextAudio = new DualAudioRecorder(_logger);

            try
            {
                nextVideo.Start(Path.Combine(nextChunkDir, "video.mp4"), config);
                nextAudio.Start(nextChunkDir, config);
            }
            catch
            {
                nextVideo.Dispose();
                nextAudio.Dispose();
                throw;
            }

            (int targetW, int targetH) = config.GetTargetResolution();
            _chunkCounter = nextIndex;
            _currentChunk = new ActiveChunkContext(
                nextChunkDir,
                _activeSessionId,
                nextIndex,
                nextStartUtc,
                nextBounds,
                $"{targetW}x{targetH}",
                nextVideo,
                nextAudio,
                config);

            _uploadWorker.SetActiveChunkDirectory(nextChunkDir);
            ScheduleNextChunkTimer(config.ChunkDurationMinutes);

            TrackFinalizationTask(Task.Run(() => FinalizeChunkAsync(prevChunk, prevTopApps, CancellationToken.None)));
            _logger.Info(nameof(RecordingCoordinator), $"Rotated recording to chunk #{nextIndex:D4} -> {nextChunkDir}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<(ActiveChunkContext Chunk, InputTelemetryRecorder Telemetry)> StartFirstChunkAsync(
        string sessionId,
        int chunkIndex,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        DateTime startedUtc = DateTime.UtcNow;
        string chunkDir = BuildChunkDirectoryPath(config.LocalBufferDir, sessionId, chunkIndex, startedUtc);
        Directory.CreateDirectory(chunkDir);

        Rectangle bounds = FfmpegVideoRecorder.GetCaptureBounds(config.CaptureAllMonitors);
        var video = new FfmpegVideoRecorder(_logger);
        var audio = new DualAudioRecorder(_logger);
        var telemetry = new InputTelemetryRecorder(_logger);

        try
        {
            video.Start(Path.Combine(chunkDir, "video.mp4"), config);
            audio.Start(chunkDir, config);
            await telemetry.StartSessionAsync(
                Path.Combine(chunkDir, "events.jsonl"),
                bounds,
                startedUtc,
                config).ConfigureAwait(false);
        }
        catch
        {
            telemetry.Dispose();
            audio.Dispose();
            video.Dispose();
            LocalQueue.TryDeleteChunkDirectory(chunkDir);
            throw;
        }

        (int targetW, int targetH) = config.GetTargetResolution();
        var context = new ActiveChunkContext(
            chunkDir,
            sessionId,
            chunkIndex,
            startedUtc,
            bounds,
            $"{targetW}x{targetH}",
            video,
            audio,
            config);

        return (context, telemetry);
    }

    private async Task FinalizeChunkAsync(
        ActiveChunkContext chunk,
        List<TopAppEntry> topApps,
        CancellationToken cancellationToken)
    {
        DateTime endedUtc = DateTime.UtcNow;

        try
        {
            Task videoStop = chunk.VideoRecorder.StopAsync(cancellationToken);
            Task audioStop = chunk.AudioRecorder.StopAndEncodeAllAsync(chunk.ConfigSnapshot, cancellationToken);
            await Task.WhenAll(videoStop, audioStop).ConfigureAwait(false);

            string screenRes = $"{chunk.CaptureBounds.Width}x{chunk.CaptureBounds.Height}";
            await ChunkManifestBuilder.BuildAndWriteAsync(
                chunk.ChunkDir,
                chunk.SessionId,
                chunk.ChunkIndex,
                chunk.StartedAtUtc,
                endedUtc,
                screenRes,
                chunk.TargetResolution,
                topApps,
                cancellationToken).ConfigureAwait(false);

            _logger.Info(nameof(RecordingCoordinator), $"Finalized chunk #{chunk.ChunkIndex:D4} -> {chunk.ChunkDir}");
            _uploadWorker.TriggerUploadNow();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(RecordingCoordinator), $"Failed finalizing chunk '{chunk.ChunkDir}'.", ex);
        }
        finally
        {
            chunk.VideoRecorder.Dispose();
            chunk.AudioRecorder.Dispose();
        }
    }

    private void TrackFinalizationTask(Task task)
    {
        lock (_finalizationLock)
        {
            _inFlightFinalizations.Add(task);
        }

        task.ContinueWith(t =>
        {
            lock (_finalizationLock)
            {
                _inFlightFinalizations.Remove(t);
            }
        }, TaskScheduler.Default);
    }

    private void ScheduleNextChunkTimer(int chunkDurationMinutes)
    {
        StopChunkTimer();
        TimeSpan dueTime = TimeSpan.FromMinutes(Math.Clamp(chunkDurationMinutes, 1, 120));
        _chunkTimer = new System.Threading.Timer(OnChunkTimerElapsed, null, dueTime, Timeout.InfiniteTimeSpan);
    }

    private void StopChunkTimer()
    {
        System.Threading.Timer? timer = _chunkTimer;
        _chunkTimer = null;
        timer?.Dispose();
    }

    private static string BuildChunkDirectoryPath(string localBufferDir, string sessionId, int chunkIndex, DateTime startedUtc)
    {
        string chunkFolder = $"chunk_{chunkIndex:D4}_{startedUtc:yyyyMMdd_HHmmss}";
        return Path.Combine(localBufferDir, sessionId, chunkFolder);
    }

    private void SetState(RecordingState newState)
    {
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRecordingAsync().GetAwaiter().GetResult();
        _stateLock.Dispose();
    }

    private sealed record ActiveChunkContext(
        string ChunkDir,
        string SessionId,
        int ChunkIndex,
        DateTime StartedAtUtc,
        Rectangle CaptureBounds,
        string TargetResolution,
        FfmpegVideoRecorder VideoRecorder,
        DualAudioRecorder AudioRecorder,
        AppConfig ConfigSnapshot);
}
