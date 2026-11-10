using System.Threading;
using System.Windows;

namespace SpeakerConnect;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A second process could capture an endpoint that the first process is
        // rendering to, creating an application-level feedback loop. Keep the
        // router single-instance per Windows session.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\SpeakerConnect.SingleInstance",
            createdNew: out _ownsMutex);

        if (!_ownsMutex)
        {
            MessageBox.Show(
                "Speaker Connect is already running. Close the existing window before starting another instance.",
                "Speaker Connect",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Apply the saved theme before any window or resource is created.
        var preferences = new Infrastructure.UiPreferencesStore();
        Infrastructure.ThemeManager.Apply(preferences.Theme);
        preferences.Dispose();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The mutex may already have been abandoned during shutdown.
                }
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ownsMutex = false;
        }

        base.OnExit(e);
    }
}
