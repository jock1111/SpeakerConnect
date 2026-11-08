namespace SpeakerConnect.Models;

/// <summary>
/// Immutable description of an active Windows render endpoint.
/// The NAudio COM objects are not kept alive by this type.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    bool IsDefault,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string FormatDescription)
{
    public string DisplayName => IsDefault ? $"{FriendlyName} [default]" : FriendlyName;
}
