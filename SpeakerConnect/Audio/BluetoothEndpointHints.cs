using System.Text.RegularExpressions;

namespace SpeakerConnect.Audio;

/// <summary>
/// Best-effort detection of Bluetooth-style endpoints from their friendly names.
/// Windows does not expose a reliable "is Bluetooth" flag through the basic MMDevice
/// API, so this is a heuristic used only to preselect the loopback source faster and
/// to label the calibration hints. The user can always pick any endpoint manually.
/// </summary>
public static partial class BluetoothEndpointHints
{
    [GeneratedRegex(
        @"bluetooth|airpods?|freebuds|buds\s?\d?|galaxy\s?buds|beats|jbl|bose|sony\s?(wh|wf)|wi-?1000|headset\s?\(|\bhs-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex Pattern();

    public static bool LooksLikeBluetooth(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        try
        {
            return Pattern().IsMatch(friendlyName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
