using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SpeakerConnect.Audio;

public sealed class AudioDeviceRemovedEventArgs : EventArgs
{
    public AudioDeviceRemovedEventArgs(string deviceId)
    {
        DeviceId = deviceId;
    }

    public string DeviceId { get; }
}

/// <summary>
/// Receives Windows endpoint notifications. Playback errors are still handled by the
/// output channels; this watcher closes the gap for devices that are unplugged and
/// therefore fail asynchronously.
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly NotificationClient _client;
    private MMDeviceEnumerator? _enumerator;
    private int _registrationResult = -1;
    private volatile bool _disposed;

    public AudioDeviceWatcher()
    {
        _client = new NotificationClient(this);

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _registrationResult = _enumerator.RegisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            // Endpoint notifications are an enhancement; playback can still work
            // without them. Do not turn a watcher failure into an app startup failure.
            Debug.WriteLine($"Audio endpoint notifications are unavailable: {ex.Message}");
        }
    }

    public event EventHandler? DevicesChanged;

    public event EventHandler<AudioDeviceRemovedEventArgs>? DeviceRemoved;

    public bool IsListening => _registrationResult == 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_enumerator is not null)
        {
            if (_registrationResult == 0)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_client);
                }
                catch (COMException ex)
                {
                    Debug.WriteLine($"Failed to unregister endpoint notifications: {ex.Message}");
                }
            }

            _enumerator.Dispose();
            _enumerator = null;
        }

        GC.SuppressFinalize(this);
    }

    private void HandleDeviceAdded()
    {
        if (_disposed)
        {
            return;
        }

        RaiseSafely(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
    }

    private void HandleDeviceRemoved(string deviceId)
    {
        if (_disposed)
        {
            return;
        }

        RaiseSafely(() => DeviceRemoved?.Invoke(this, new AudioDeviceRemovedEventArgs(deviceId)));
        RaiseSafely(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
    }

    private void HandleDeviceStateChanged(string deviceId, DeviceState state)
    {
        if (_disposed)
        {
            return;
        }

        if (state != DeviceState.Active)
        {
            HandleDeviceRemoved(deviceId);
        }
        else
        {
            RaiseSafely(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void HandleDefaultDeviceChanged(DataFlow flow)
    {
        if (_disposed)
        {
            return;
        }

        if (flow == DataFlow.Render)
        {
            RaiseSafely(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private static void RaiseSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Exceptions must never escape a COM callback.
            Debug.WriteLine($"Audio device notification handler failed: {ex}");
        }
    }

    [ComVisible(true)]
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceWatcher _owner;

        public NotificationClient(AudioDeviceWatcher owner)
        {
            _owner = owner;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            => _owner.HandleDeviceStateChanged(deviceId, newState);

        public void OnDeviceAdded(string pwstrDeviceId)
            => _owner.HandleDeviceAdded();

        public void OnDeviceRemoved(string deviceId)
            => _owner.HandleDeviceRemoved(deviceId);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            => _owner.HandleDefaultDeviceChanged(flow);

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Volume and property changes fire very frequently; they do not require a refresh.
        }
    }
}
