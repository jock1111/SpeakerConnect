using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using SpeakerConnect.Models;

namespace SpeakerConnect.Audio;

/// <summary>
/// Enumerates active Windows render (output) endpoints through MMDeviceEnumerator/WASAPI.
/// </summary>
public sealed class AudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetActiveRenderDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        using var enumerator = new MMDeviceEnumerator();
        var defaultDeviceId = TryGetDefaultDeviceId(enumerator);

        // MMDeviceCollection itself does not implement IDisposable, but each MMDevice does.
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var index = 0; index < collection.Count; index++)
        {
            using var device = collection[index];
            try
            {
                var id = device.ID;
                var name = device.FriendlyName;
                using var audioClient = device.AudioClient;
                var mixFormat = audioClient.MixFormat;

                devices.Add(new AudioDeviceInfo(
                    id,
                    name,
                    string.Equals(id, defaultDeviceId, StringComparison.OrdinalIgnoreCase),
                    mixFormat.SampleRate,
                    mixFormat.Channels,
                    mixFormat.BitsPerSample,
                    AudioFormatHelper.Describe(mixFormat)));
            }
            catch (Exception ex) when (IsExpectedDeviceFailure(ex))
            {
                // A device can disappear between enumeration and property access.
                // Skip it rather than failing the entire list.
            }
        }

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex) when (IsExpectedDeviceFailure(ex))
        {
            return null;
        }
    }

    private static bool IsExpectedDeviceFailure(Exception exception)
    {
        return exception is COMException or InvalidOperationException or UnauthorizedAccessException;
    }
}
