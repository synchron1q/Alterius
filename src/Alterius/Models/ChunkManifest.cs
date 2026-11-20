using System.Text.Json.Serialization;

namespace Alterius.Models;

/// <summary>
/// Metadata manifest created atomically when a recording chunk finishes.
/// Presence of manifest.json signals that the chunk is ready for SMB upload.
/// </summary>
public sealed class ChunkManifest
{
    [JsonPropertyName("machine_name")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("windows_user")]
    public string WindowsUser { get; set; } = Environment.UserName;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("chunk_name")]
    public string ChunkName { get; set; } = string.Empty;

    [JsonPropertyName("started_at_utc")]
    public DateTime StartedAtUtc { get; set; }

    [JsonPropertyName("ended_at_utc")]
    public DateTime EndedAtUtc { get; set; }

    [JsonPropertyName("duration_sec")]
    public double DurationSec { get; set; }

    [JsonPropertyName("screen_resolution")]
    public string ScreenResolution { get; set; } = string.Empty;

    [JsonPropertyName("video_target_resolution")]
    public string VideoTargetResolution { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ChunkFileEntry> Files { get; set; } = new();

    [JsonPropertyName("top_apps")]
    public List<TopAppEntry> TopApps { get; set; } = new();
}

/// <summary>
/// File descriptor inside a finalized chunk including SHA-256 integrity hash.
/// </summary>
public sealed class ChunkFileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Aggregated foreground application usage summary for a chunk.
/// </summary>
public sealed class TopAppEntry
{
    [JsonPropertyName("process_name")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("active_seconds")]
    public double ActiveSeconds { get; set; }

    [JsonPropertyName("focus_count")]
    public int FocusCount { get; set; }

    [JsonPropertyName("sample_titles")]
    public List<string> SampleTitles { get; set; } = new();
}
