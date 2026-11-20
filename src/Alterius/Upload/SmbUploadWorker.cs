using Alterius.Core;
using Alterius.Models;

namespace Alterius.Upload;

/// <summary>
/// Background upload service polling the local chunk queue and uploading completed chunks
/// to the configured SMB share with exponential retry backoff (30s -> 60s -> 300s).
/// </summary>
public sealed class SmbUploadWorker : IDisposable
{
    private static readonly int[] RetryDelaysSeconds = { 30, 60, 300 };

    private readonly ConfigManager _configManager;
    private readonly LocalQueue _localQueue;
    private readonly SmbConnectionManager _connectionManager;
    private readonly SmbUploader _uploader;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _wakeupSignal = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private int _backoffIndex;
    private volatile bool _hasError;
    private volatile bool _isUploading;
    private volatile int _pendingCount;
    private string? _lastError;
    private string? _activeChunkDir;
    private bool _disposed;

    public SmbUploadWorker(
        ConfigManager configManager,
        LocalQueue localQueue,
        SmbConnectionManager connectionManager,
        SmbUploader uploader,
        FileLogger logger)
    {
        _configManager = configManager;
        _localQueue = localQueue;
        _connectionManager = connectionManager;
        _uploader = uploader;
        _logger = logger;

        _configManager.ConfigChanged += OnConfigChanged;
    }

    public bool HasError => _hasError;

    public bool IsUploading => _isUploading;

    public int PendingCount => _pendingCount;

    public string? LastError => _lastError;

    public event Action? StatusChanged;

    public event Action<string>? BufferLimitPauseRequested;

    public void SetActiveChunkDirectory(string? chunkDir)
    {
        Volatile.Write(ref _activeChunkDir, chunkDir);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_workerTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunLoopAsync(_cts.Token));
        _logger.Info(nameof(SmbUploadWorker), "SMB upload background worker started.");
    }

    public void TriggerUploadNow()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _wakeupSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Wakeup already pending.
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? workerTask = _workerTask;
        _cts = null;
        _workerTask = null;

        if (cts is not null)
        {
            cts.Cancel();
            TriggerUploadNow();

            if (workerTask is not null)
            {
                try
                {
                    await workerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
            }

            cts.Dispose();
        }
    }

    private void OnConfigChanged(AppConfig _)
    {
        _backoffIndex = 0;
        _connectionManager.ResetConnection();
        TriggerUploadNow();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AppConfig config = _configManager.Current;
            int waitSeconds = Math.Clamp(config.UploadPollIntervalSeconds, 5, 600);

            try
            {
                _localQueue.EnforceBufferLimit(
                    config,
                    Volatile.Read(ref _activeChunkDir),
                    out int deletedCount,
                    out bool shouldPause);

                if (deletedCount > 0)
                {
                    StatusChanged?.Invoke();
                }

                if (shouldPause)
                {
                    BufferLimitPauseRequested?.Invoke(
                        $"Локальный буфер превысил лимит {config.MaxLocalBufferGb:F1} ГБ. Запись приостановлена.");
                }

                List<QueuedChunk> pending = _localQueue.GetCompletedChunks(config.LocalBufferDir);
                UpdateStatus(pending.Count, _hasError, _lastError, _isUploading);

                if (pending.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(config.SmbSharePath))
                    {
                        SetErrorState(pending.Count, "Путь к SMB-шаре не задан в настройках; файлы накапливаются локально.");
                    }
                    else
                    {
                        bool allUploaded = await ProcessQueueAsync(pending, config, cancellationToken).ConfigureAwait(false);
                        if (!allUploaded)
                        {
                            waitSeconds = RetryDelaysSeconds[Math.Min(_backoffIndex, RetryDelaysSeconds.Length - 1)];
                            _backoffIndex = Math.Min(_backoffIndex + 1, RetryDelaysSeconds.Length - 1);
                            _logger.Warn(nameof(SmbUploadWorker), $"Retrying SMB upload in {waitSeconds}s.");
                        }
                        else
                        {
                            _backoffIndex = 0;
                        }
                    }
                }
                else
                {
                    if (_hasError)
                    {
                        ClearErrorState(0);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(nameof(SmbUploadWorker), "Unexpected upload loop error.", ex);
                waitSeconds = RetryDelaysSeconds[Math.Min(_backoffIndex, RetryDelaysSeconds.Length - 1)];
                _backoffIndex = Math.Min(_backoffIndex + 1, RetryDelaysSeconds.Length - 1);
                SetErrorState(_localQueue.GetPendingCount(config.LocalBufferDir), ex.Message);
            }

            try
            {
                await _wakeupSignal.WaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> ProcessQueueAsync(
        List<QueuedChunk> pending,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        int remaining = pending.Count;

        foreach (QueuedChunk chunk in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UpdateStatus(remaining, _hasError, _lastError, isUploading: true);
            try
            {
                await _uploader.UploadChunkAsync(chunk, config, cancellationToken).ConfigureAwait(false);
                remaining = Math.Max(0, remaining - 1);
                ClearErrorState(remaining);
                _backoffIndex = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                UpdateStatus(remaining, _hasError, _lastError, isUploading: false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(nameof(SmbUploadWorker), $"Upload failed for '{chunk.ChunkFolderName}': {ex.Message}");
                SetErrorState(remaining, ex.Message);
                return false;
            }
        }

        UpdateStatus(_localQueue.GetPendingCount(config.LocalBufferDir), hasError: false, lastError: null, isUploading: false);
        return true;
    }

    private void SetErrorState(int pendingCount, string errorMessage)
    {
        UpdateStatus(pendingCount, hasError: true, lastError: errorMessage, isUploading: false);
    }

    private void ClearErrorState(int pendingCount)
    {
        UpdateStatus(pendingCount, hasError: false, lastError: null, isUploading: false);
    }

    private void UpdateStatus(int pendingCount, bool hasError, string? lastError, bool isUploading)
    {
        bool changed = _pendingCount != pendingCount
            || _hasError != hasError
            || _isUploading != isUploading
            || !string.Equals(_lastError, lastError, StringComparison.Ordinal);

        _pendingCount = pendingCount;
        _hasError = hasError;
        _lastError = lastError;
        _isUploading = isUploading;

        if (changed)
        {
            StatusChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configManager.ConfigChanged -= OnConfigChanged;
        StopAsync().GetAwaiter().GetResult();
        _wakeupSignal.Dispose();
    }
}
