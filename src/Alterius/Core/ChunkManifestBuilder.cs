using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alterius.Models;

namespace Alterius.Core;

/// <summary>
/// Builds and atomically writes <c>manifest.json</c> with SHA-256 digests and top application summaries
/// after all media and telemetry streams in a chunk have closed.
/// </summary>
public static class ChunkManifestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<ChunkManifest> BuildAndWriteAsync(
        string chunkDir,
        string sessionId,
        int chunkIndex,
        DateTime startedAtUtc,
        DateTime endedAtUtc,
        string screenResolution,
        string videoTargetResolution,
        List<TopAppEntry> topApps,
        CancellationToken cancellationToken = default)
    {
        var files = new List<ChunkFileEntry>();
        string[] candidateFiles = Directory.GetFiles(chunkDir, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string filePath in candidateFiles)
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tmp.wav", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            string hash = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
            files.Add(new ChunkFileEntry
            {
                Name = fileName,
                SizeBytes = info.Length,
                Sha256 = hash
            });
        }

        double durationSec = Math.Max(0.0, Math.Round((endedAtUtc - startedAtUtc).TotalSeconds, 3));
        var manifest = new ChunkManifest
        {
            MachineName = Environment.MachineName,
            WindowsUser = Environment.UserName,
            SessionId = sessionId,
            ChunkIndex = chunkIndex,
            ChunkName = Path.GetFileName(chunkDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = endedAtUtc,
            DurationSec = durationSec,
            ScreenResolution = screenResolution,
            VideoTargetResolution = videoTargetResolution,
            Files = files,
            TopApps = topApps
        };

        string manifestPath = Path.Combine(chunkDir, "manifest.json");
        string tempPath = Path.Combine(chunkDir, "manifest.json.tmp");
        string json = JsonSerializer.Serialize(manifest, JsonOptions);

        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tempPath, manifestPath, overwrite: true);

        return manifest;
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
