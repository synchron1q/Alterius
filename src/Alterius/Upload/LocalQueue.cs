using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alterius.Core;
using Alterius.Models;

namespace Alterius.Upload;

public sealed record QueuedChunk(
    string ChunkDir,
    string SessionId,
    string ChunkFolderName,
    string ManifestPath,
    DateTime StartedAtUtc);

/// <summary>
/// Manages completed chunks in the local buffer directory, enforces disk quotas,
/// and recovers interrupted chunks after an unexpected crash or reboot.
/// </summary>
public sealed class LocalQueue
{
    private static readonly Regex ChunkFolderRegex = new(
        @"^chunk_(\d+)_(\d{8}_\d{6})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FileLogger _logger;

    public LocalQueue(FileLogger logger)
    {
        _logger = logger;
    }

    public List<QueuedChunk> GetCompletedChunks(string localBufferDir)
    {
        var result = new List<QueuedChunk>();
        if (string.IsNullOrWhiteSpace(localBufferDir) || !Directory.Exists(localBufferDir))
        {
            return result;
        }

        try
        {
            foreach (string sessionDir in Directory.EnumerateDirectories(localBufferDir))
            {
                string sessionId = Path.GetFileName(sessionDir);
                foreach (string chunkDir in Directory.EnumerateDirectories(sessionDir, "chunk_*"))
                {
                    string manifestPath = Path.Combine(chunkDir, "manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    string chunkFolderName = Path.GetFileName(chunkDir);
                    DateTime startedAtUtc = ReadChunkStartUtc(manifestPath, chunkFolderName, chunkDir);
                    result.Add(new QueuedChunk(chunkDir, sessionId, chunkFolderName, manifestPath, startedAtUtc));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(LocalQueue), $"Error scanning local buffer '{localBufferDir}': {ex.Message}");
        }

        result.Sort((a, b) => a.StartedAtUtc.CompareTo(b.StartedAtUtc));
        return result;
    }

    public int GetPendingCount(string localBufferDir) => GetCompletedChunks(localBufferDir).Count;

    public long GetTotalBufferBytes(string localBufferDir)
    {
        if (string.IsNullOrWhiteSpace(localBufferDir) || !Directory.Exists(localBufferDir))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(localBufferDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Ignore transiently deleted files.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(LocalQueue), $"Error calculating buffer size: {ex.Message}");
        }

        return total;
    }

    /// <summary>
    /// Enforces <see cref="AppConfig.MaxLocalBufferGb"/> by deleting oldest completed chunks
    /// or requesting recording pause, depending on <see cref="AppConfig.BufferOverflowPolicy"/>.
    /// </summary>
    public void EnforceBufferLimit(
        AppConfig config,
        string? activeChunkDir,
        out int deletedCount,
        out bool shouldPause)
    {
        deletedCount = 0;
        shouldPause = false;

        long maxBytes = (long)(Math.Max(0.2, config.MaxLocalBufferGb) * 1024.0 * 1024.0 * 1024.0);
        long currentBytes = GetTotalBufferBytes(config.LocalBufferDir);

        if (currentBytes <= maxBytes)
        {
            return;
        }

        if (config.BufferOverflowPolicy == BufferOverflowPolicy.PauseRecording)
        {
            shouldPause = true;
            return;
        }

        // DeleteOldest: trim completed chunks until buffer drops below 90% of limit.
        long targetBytes = (long)(maxBytes * 0.90);
        List<QueuedChunk> chunks = GetCompletedChunks(config.LocalBufferDir);
        string? normalizedActive = string.IsNullOrWhiteSpace(activeChunkDir)
            ? null
            : Path.GetFullPath(activeChunkDir).TrimEnd(Path.DirectorySeparatorChar);

        foreach (QueuedChunk chunk in chunks)
        {
            if (currentBytes <= targetBytes)
            {
                break;
            }

            string normalizedChunk = Path.GetFullPath(chunk.ChunkDir).TrimEnd(Path.DirectorySeparatorChar);
            if (normalizedActive is not null && string.Equals(normalizedChunk, normalizedActive, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long chunkBytes = GetDirectorySizeBytes(chunk.ChunkDir);
            if (TryDeleteChunkDirectory(chunk.ChunkDir))
            {
                currentBytes = Math.Max(0, currentBytes - chunkBytes);
                deletedCount++;
                _logger.Warn(
                    nameof(LocalQueue),
                    $"Local buffer exceeded {config.MaxLocalBufferGb:F1} GB; deleted oldest chunk '{chunk.ChunkFolderName}' ({chunkBytes / (1024.0 * 1024.0):F1} MB).");
            }
        }
    }

    /// <summary>
    /// Finalizes any leftover chunk folders from prior sessions that lack <c>manifest.json</c>
    /// (for example after an unexpected power loss or crash).
    /// </summary>
    public async Task<int> RecoverOrphanedChunksAsync(
        string localBufferDir,
        string ffmpegExePath,
        int audioBitrateKbps,
        string videoTargetResolution,
        string? activeSessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localBufferDir) || !Directory.Exists(localBufferDir))
        {
            return 0;
        }

        int recoveredCount = 0;

        foreach (string sessionDir in Directory.EnumerateDirectories(localBufferDir))
        {
            string sessionId = Path.GetFileName(sessionDir);
            if (!string.IsNullOrEmpty(activeSessionId) && string.Equals(sessionId, activeSessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string chunkDir in Directory.EnumerateDirectories(sessionDir, "chunk_*"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string manifestPath = Path.Combine(chunkDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    continue;
                }

                string chunkFolderName = Path.GetFileName(chunkDir);
                try
                {
                    await RecoverTempAudioFilesAsync(chunkDir, ffmpegExePath, audioBitrateKbps, cancellationToken)
                        .ConfigureAwait(false);

                    // Clean up leftover *.tmp files (such as interrupted manifest.json.tmp).
                    foreach (string tmp in Directory.EnumerateFiles(chunkDir, "*.tmp"))
                    {
                        TryDeleteFile(tmp);
                    }

                    string[] remainingFiles = Directory.GetFiles(chunkDir, "*", SearchOption.TopDirectoryOnly);
                    if (remainingFiles.Length == 0)
                    {
                        TryDeleteChunkDirectory(chunkDir);
                        continue;
                    }

                    (int chunkIndex, DateTime startedUtc) = ParseChunkFolderMetadata(chunkFolderName, chunkDir);
                    DateTime endedUtc = remainingFiles.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(startedUtc).Max();
                    if (endedUtc < startedUtc)
                    {
                        endedUtc = startedUtc;
                    }

                    Rectangle primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                    string screenResolution = $"{primary.Width}x{primary.Height}";

                    await ChunkManifestBuilder.BuildAndWriteAsync(
                        chunkDir,
                        sessionId,
                        chunkIndex,
                        startedUtc,
                        endedUtc,
                        screenResolution,
                        videoTargetResolution,
                        new List<TopAppEntry>(),
                        cancellationToken).ConfigureAwait(false);

                    recoveredCount++;
                    _logger.Info(nameof(LocalQueue), $"Recovered interrupted chunk '{sessionId}/{chunkFolderName}' into upload queue.");
                }
                catch (Exception ex)
                {
                    _logger.Warn(nameof(LocalQueue), $"Failed to recover orphaned chunk '{chunkDir}': {ex.Message}");
                }
            }

            TryDeleteEmptySessionDirectory(sessionDir);
        }

        return recoveredCount;
    }

    public static bool TryDeleteChunkDirectory(string chunkDir)
    {
        try
        {
            if (Directory.Exists(chunkDir))
            {
                Directory.Delete(chunkDir, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void TryDeleteEmptySessionDirectory(string sessionDir)
    {
        try
        {
            if (Directory.Exists(sessionDir) && !Directory.EnumerateFileSystemEntries(sessionDir).Any())
            {
                Directory.Delete(sessionDir, recursive: false);
            }
        }
        catch
        {
            // Ignore when directory is non-empty or transiently locked.
        }
    }

    private async Task RecoverTempAudioFilesAsync(
        string chunkDir,
        string ffmpegExePath,
        int audioBitrateKbps,
        CancellationToken cancellationToken)
    {
        foreach (string tempWav in Directory.GetFiles(chunkDir, "*.tmp.wav"))
        {
            var info = new FileInfo(tempWav);
            if (info.Length <= 44)
            {
                TryDeleteFile(tempWav);
                continue;
            }

            string baseName = Path.GetFileName(tempWav);
            string streamName = baseName[..^".tmp.wav".Length];
            string oggPath = Path.Combine(chunkDir, $"{streamName}.ogg");
            string fallbackWavPath = Path.Combine(chunkDir, $"{streamName}.wav");

            if (File.Exists(ffmpegExePath))
            {
                try
                {
                    int clampedBitrate = Math.Clamp(audioBitrateKbps, 16, 128);
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffmpegExePath,
                            Arguments = string.Format(
                                CultureInfo.InvariantCulture,
                                "-y -hide_banner -loglevel warning -i \"{0}\" -c:a libopus -b:a {1}k -vbr on -application voip \"{2}\"",
                                tempWav,
                                clampedBitrate,
                                oggPath),
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    if (process.Start())
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
                        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                        if (process.ExitCode == 0 && File.Exists(oggPath) && new FileInfo(oggPath).Length > 0)
                        {
                            TryDeleteFile(tempWav);
                            continue;
                        }
                    }
                }
                catch
                {
                    // Fall back to preserving raw WAV below.
                }
            }

            try
            {
                File.Move(tempWav, fallbackWavPath, overwrite: true);
            }
            catch
            {
                // Keep original temp file if rename fails.
            }
        }
    }

    private static DateTime ReadChunkStartUtc(string manifestPath, string chunkFolderName, string chunkDir)
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            ChunkManifest? manifest = JsonSerializer.Deserialize<ChunkManifest>(json, JsonOptions);
            if (manifest is not null && manifest.StartedAtUtc != default)
            {
                return manifest.StartedAtUtc;
            }
        }
        catch
        {
            // Fall back to folder name or file timestamp.
        }

        (_, DateTime parsed) = ParseChunkFolderMetadata(chunkFolderName, chunkDir);
        return parsed;
    }

    private static (int ChunkIndex, DateTime StartedAtUtc) ParseChunkFolderMetadata(string chunkFolderName, string chunkDir)
    {
        Match match = ChunkFolderRegex.Match(chunkFolderName);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
            && DateTime.TryParseExact(
                match.Groups[2].Value,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsedUtc))
        {
            return (idx, parsedUtc);
        }

        return (1, Directory.GetCreationTimeUtc(chunkDir));
    }

    private static long GetDirectorySizeBytes(string dir)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }
        }
        catch
        {
            // Best-effort size calculation.
        }

        return total;
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
            // Ignore cleanup failure.
        }
    }
}
