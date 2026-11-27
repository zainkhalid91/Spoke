using Microsoft.UI.Xaml;

namespace Spoke.Settings;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _single;

    public App()
    {
        InitializeComponent();
        // Log every unhandled error; a XAML event handler that throws must not take the whole window down.
        UnhandledException += (_, e) =>
        {
            Log.Write("Unhandled: " + e.Exception);
            e.Handled = true;
            (_window as MainWindow)?.ShowSaveMessage("Something went wrong: " + e.Message + " (details in settings.log)");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Task: " + e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One Settings window: a second launch brings the first one forward and exits.
        _single = new Mutex(true, "Local\\Spoke.Settings.SingleInstance", out bool first);
        if (!first)
        {
            nint existing = Native.FindWindow(null, "Spoke Settings");
            if (existing != 0)
            {
                if (Native.IsIconic(existing)) Native.ShowWindow(existing, 9 /* SW_RESTORE */);
                Native.SetForegroundWindow(existing);
            }
            Exit();
            return;
        }
        Log.Write($"Settings started (pipe {Spoke.Core.Ipc.PipeName})");
        _window = new MainWindow();
        _window.Activate();
    }
}
