using System.Globalization;
using System.Text.Json.Serialization;

namespace Alterius.Models;

/// <summary>
/// Persisted application configuration stored in config.json next to the executable.
/// </summary>
public sealed class AppConfig
{
    public bool AutoStartRecording { get; set; } = false;

    public int ChunkDurationMinutes { get; set; } = 15;

    public string LocalBufferDir { get; set; } = GetDefaultBufferDir();

    public double MaxLocalBufferGb { get; set; } = 5.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BufferOverflowPolicy BufferOverflowPolicy { get; set; } = BufferOverflowPolicy.DeleteOldest;

    public string FfmpegPath { get; set; } = @"bin\ffmpeg\ffmpeg.exe";

    public int VideoFps { get; set; } = 2;

    public string VideoResolution { get; set; } = "1280x720";

    public bool CaptureAllMonitors { get; set; } = false;

    public int VideoCrf { get; set; } = 30;

    public string VideoPreset { get; set; } = "ultrafast";

    public bool RecordMicrophone { get; set; } = true;

    public bool RecordSystemAudio { get; set; } = true;

    public string? MicrophoneDeviceId { get; set; } = null;

    public int AudioBitrateKbps { get; set; } = 32;

    public int MouseMoveSampleIntervalMs { get; set; } = 300;

    public int MouseMoveMinDistancePx { get; set; } = 15;

    public bool EnableTypingBurstCounter { get; set; } = true;

    public int TypingBurstWindowSeconds { get; set; } = 10;

    public string SmbSharePath { get; set; } = string.Empty;

    public string SmbUsername { get; set; } = string.Empty;

    public string SmbPasswordEncrypted { get; set; } = string.Empty;

    public int UploadPollIntervalSeconds { get; set; } = 15;

    public int LogRetentionDays { get; set; } = 14;

    public static string GetDefaultBufferDir()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Alterius", "buffer");
    }

    /// <summary>
    /// Creates a deep copy of this configuration instance.
    /// </summary>
    public AppConfig Clone()
    {
        return new AppConfig
        {
            AutoStartRecording = AutoStartRecording,
            ChunkDurationMinutes = ChunkDurationMinutes,
            LocalBufferDir = LocalBufferDir,
            MaxLocalBufferGb = MaxLocalBufferGb,
            BufferOverflowPolicy = BufferOverflowPolicy,
            FfmpegPath = FfmpegPath,
            VideoFps = VideoFps,
            VideoResolution = VideoResolution,
            CaptureAllMonitors = CaptureAllMonitors,
            VideoCrf = VideoCrf,
            VideoPreset = VideoPreset,
            RecordMicrophone = RecordMicrophone,
            RecordSystemAudio = RecordSystemAudio,
            MicrophoneDeviceId = MicrophoneDeviceId,
            AudioBitrateKbps = AudioBitrateKbps,
            MouseMoveSampleIntervalMs = MouseMoveSampleIntervalMs,
            MouseMoveMinDistancePx = MouseMoveMinDistancePx,
            EnableTypingBurstCounter = EnableTypingBurstCounter,
            TypingBurstWindowSeconds = TypingBurstWindowSeconds,
            SmbSharePath = SmbSharePath,
            SmbUsername = SmbUsername,
            SmbPasswordEncrypted = SmbPasswordEncrypted,
            UploadPollIntervalSeconds = UploadPollIntervalSeconds,
            LogRetentionDays = LogRetentionDays
        };
    }

    /// <summary>
    /// Clamps numeric settings to safe operating ranges and normalizes string fields.
    /// </summary>
    public void Normalize()
    {
        ChunkDurationMinutes = Math.Clamp(ChunkDurationMinutes, 1, 120);
        MaxLocalBufferGb = Math.Clamp(double.IsNaN(MaxLocalBufferGb) || double.IsInfinity(MaxLocalBufferGb) ? 5.0 : MaxLocalBufferGb, 0.2, 1000.0);
        VideoFps = Math.Clamp(VideoFps, 1, 5);
        VideoCrf = Math.Clamp(VideoCrf, 18, 40);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 16, 128);
        MouseMoveSampleIntervalMs = Math.Clamp(MouseMoveSampleIntervalMs, 50, 5000);
        MouseMoveMinDistancePx = Math.Clamp(MouseMoveMinDistancePx, 1, 500);
        TypingBurstWindowSeconds = Math.Clamp(TypingBurstWindowSeconds, 1, 60);
        UploadPollIntervalSeconds = Math.Clamp(UploadPollIntervalSeconds, 5, 600);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);

        LocalBufferDir = string.IsNullOrWhiteSpace(LocalBufferDir)
            ? GetDefaultBufferDir()
            : Environment.ExpandEnvironmentVariables(LocalBufferDir.Trim());

        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath)
            ? @"bin\ffmpeg\ffmpeg.exe"
            : Environment.ExpandEnvironmentVariables(FfmpegPath.Trim());

        VideoPreset = string.IsNullOrWhiteSpace(VideoPreset) ? "ultrafast" : VideoPreset.Trim();

        (int width, int height) = GetTargetResolution();
        VideoResolution = $"{width}x{height}";

        MicrophoneDeviceId = string.IsNullOrWhiteSpace(MicrophoneDeviceId) ? null : MicrophoneDeviceId.Trim();
        SmbSharePath = SmbSharePath?.Trim().TrimEnd('\\') ?? string.Empty;
        SmbUsername = SmbUsername?.Trim() ?? string.Empty;
        SmbPasswordEncrypted = SmbPasswordEncrypted?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Parses <see cref="VideoResolution"/> into even positive dimensions, defaulting to 1280x720.
    /// </summary>
    public (int Width, int Height) GetTargetResolution()
    {
        if (!string.IsNullOrWhiteSpace(VideoResolution))
        {
            string[] parts = VideoResolution.Trim().ToLowerInvariant().Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                && w >= 320 && w <= 7680
                && h >= 240 && h <= 4320)
            {
                // libx264 with yuv420p requires even width and height.
                return ((w / 2) * 2, (h / 2) * 2);
            }
        }

        return (1280, 720);
    }
}
