using System.Globalization;
using System.Text.Json;
using Alterius.Core;
using Alterius.Models;

namespace Alterius.Upload;

/// <summary>
/// Atomically uploads completed local chunks to an SMB share using a temporary <c>.uploading</c>
/// staging folder, SHA-256 verification, and atomic rename to the final chunk directory.
/// </summary>
public sealed class SmbUploader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FileLogger _logger;
    private readonly SmbConnectionManager _connectionManager;

    public SmbUploader(FileLogger logger, SmbConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    public async Task UploadChunkAsync(QueuedChunk chunk, AppConfig config, CancellationToken cancellationToken)
    {
        if (!File.Exists(chunk.ManifestPath))
        {
            throw new FileNotFoundException("Chunk manifest not found for upload.", chunk.ManifestPath);
        }

        string plainPassword = DpapiHelper.Decrypt(config.SmbPasswordEncrypted);
        if (!_connectionManager.EnsureConnected(config.SmbSharePath, config.SmbUsername, plainPassword, out string? connError))
        {
            throw new IOException(connError ?? "Unable to connect to SMB share.");
        }

        string manifestJson = await File.ReadAllTextAsync(chunk.ManifestPath, cancellationToken).ConfigureAwait(false);
        ChunkManifest manifest = JsonSerializer.Deserialize<ChunkManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidDataException($"Invalid manifest.json at '{chunk.ManifestPath}'.");

        string machineUserSegment = $"{SanitizePathSegment(manifest.MachineName)}_{SanitizePathSegment(manifest.WindowsUser)}";
        string dateSegment = manifest.StartedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string sessionSegment = SanitizePathSegment(chunk.SessionId);
        string chunkSegment = SanitizePathSegment(chunk.ChunkFolderName);

        string targetParentDir = Path.Combine(config.SmbSharePath, machineUserSegment, dateSegment, sessionSegment);
        string finalDir = Path.Combine(targetParentDir, chunkSegment);
        string stagingDir = finalDir + ".uploading";

        // Idempotency guard: if a prior upload already succeeded and renamed before local cleanup, verify and clean local.
        if (Directory.Exists(finalDir) && File.Exists(Path.Combine(finalDir, "manifest.json")))
        {
            if (await VerifyRemoteDirectoryMatchesAsync(finalDir, manifest, chunk.ManifestPath, cancellationToken).ConfigureAwait(false))
            {
                _logger.Info(nameof(SmbUploader), $"Chunk '{chunk.ChunkFolderName}' already verified on SMB share; cleaning local copy.");
                DeleteLocalChunkAndEmptySession(chunk);
                return;
            }
        }

        Directory.CreateDirectory(targetParentDir);

        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        Directory.CreateDirectory(stagingDir);

        try
        {
            foreach (ChunkFileEntry entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string localFile = Path.Combine(chunk.ChunkDir, entry.Name);
                if (!File.Exists(localFile))
                {
                    throw new FileNotFoundException($"Missing chunk file '{entry.Name}' listed in manifest.", localFile);
                }

                string remoteFile = Path.Combine(stagingDir, entry.Name);
                await CopyFileAsync(localFile, remoteFile, cancellationToken).ConfigureAwait(false);

                var remoteInfo = new FileInfo(remoteFile);
                if (remoteInfo.Length != entry.SizeBytes)
                {
                    throw new IOException(
                        $"Size mismatch for '{entry.Name}' on SMB share (expected {entry.SizeBytes}, got {remoteInfo.Length}).");
                }

                string remoteHash = await ChunkManifestBuilder.ComputeSha256Async(remoteFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(remoteHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"SHA-256 mismatch for '{entry.Name}' on SMB share (expected {entry.Sha256}, got {remoteHash}).");
                }
            }

            // Copy manifest.json last and verify size before committing rename.
            string remoteManifestPath = Path.Combine(stagingDir, "manifest.json");
            await CopyFileAsync(chunk.ManifestPath, remoteManifestPath, cancellationToken).ConfigureAwait(false);

            long localManifestLen = new FileInfo(chunk.ManifestPath).Length;
            long remoteManifestLen = new FileInfo(remoteManifestPath).Length;
            if (localManifestLen != remoteManifestLen)
            {
                throw new IOException(
                    $"Size mismatch for manifest.json on SMB share (expected {localManifestLen}, got {remoteManifestLen}).");
            }

            if (Directory.Exists(finalDir))
            {
                Directory.Delete(finalDir, recursive: true);
            }

            Directory.Move(stagingDir, finalDir);
        }
        catch
        {
            TryDeleteRemoteDirectory(stagingDir);
            throw;
        }

        _logger.Info(nameof(SmbUploader), $"Uploaded chunk '{chunk.SessionId}/{chunk.ChunkFolderName}' -> '{finalDir}'.");
        DeleteLocalChunkAndEmptySession(chunk);
    }

    private static async Task<bool> VerifyRemoteDirectoryMatchesAsync(
        string remoteDir,
        ChunkManifest manifest,
        string localManifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            string remoteManifest = Path.Combine(remoteDir, "manifest.json");
            if (!File.Exists(remoteManifest) || new FileInfo(remoteManifest).Length != new FileInfo(localManifestPath).Length)
            {
                return false;
            }

            foreach (ChunkFileEntry entry in manifest.Files)
            {
                string remoteFile = Path.Combine(remoteDir, entry.Name);
                if (!File.Exists(remoteFile))
                {
                    return false;
                }

                if (new FileInfo(remoteFile).Length != entry.SizeBytes)
                {
                    return false;
                }

                string hash = await ChunkManifestBuilder.ComputeSha256Async(remoteFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        await using var destination = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DeleteLocalChunkAndEmptySession(QueuedChunk chunk)
    {
        if (!LocalQueue.TryDeleteChunkDirectory(chunk.ChunkDir))
        {
            _logger.Warn(nameof(SmbUploader), $"Uploaded '{chunk.ChunkFolderName}', but failed to delete local directory '{chunk.ChunkDir}'.");
            return;
        }

        string? sessionDir = Path.GetDirectoryName(chunk.ChunkDir);
        if (!string.IsNullOrEmpty(sessionDir))
        {
            LocalQueue.TryDeleteEmptySessionDirectory(sessionDir);
        }
    }

    private static void TryDeleteRemoteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Ignore staging cleanup failures; next attempt cleans up prior .uploading directory.
        }
    }

    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "unknown";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = input.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
