namespace Alterius.Models;

/// <summary>
/// Lifecycle state of the recording pipeline.
/// </summary>
public enum RecordingState
{
    Idle,
    Recording,
    Stopping,
    PausedBufferFull
}

/// <summary>
/// Policy applied when local chunk buffer exceeds <see cref="AppConfig.MaxLocalBufferGb"/>.
/// </summary>
public enum BufferOverflowPolicy
{
    DeleteOldest,
    PauseRecording
}
