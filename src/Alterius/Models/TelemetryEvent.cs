using System.Text.Json.Serialization;

namespace Alterius.Models;

/// <summary>
/// Single JSONL telemetry record stored in events.jsonl.
/// Never records keyboard keystrokes or character codes.
/// </summary>
public sealed class TelemetryEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("offset_ms")]
    public long OffsetMs { get; set; }

    [JsonPropertyName("timestamp_utc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Button { get; set; }

    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; set; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; set; }

    [JsonPropertyName("nx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Nx { get; set; }

    [JsonPropertyName("ny")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ny { get; set; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Delta { get; set; }

    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }

    [JsonPropertyName("window_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessName { get; set; }

    [JsonPropertyName("hwnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hwnd { get; set; }

    [JsonPropertyName("key_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeyCount { get; set; }

    [JsonPropertyName("window_sec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowSec { get; set; }
}
