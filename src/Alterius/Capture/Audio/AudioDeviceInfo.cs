namespace Alterius.Capture.Audio;

/// <summary>
/// Represents an active Windows audio capture device for UI selection.
/// </summary>
public sealed record AudioDeviceInfo(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}
