using System.Windows;
using System.Windows.Threading;
using ISLA.Infrastructure;

namespace ISLA;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\ISLA.SingleInstance.Mutex";
    private const string ShowSignalName = @"Local\ISLA.SingleInstance.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Log as early as possible so startup failures leave a trace.
        AppLog.Info($"ISLA {typeof(App).Assembly.GetName().Version} starting. Log file: {AppLog.FilePath}");

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // A second copy would duplicate the tray icon, timers, and notification listener.
            AppLog.Info("Another ISLA instance is already running; asking it to show and exiting.");
            SignalRunningInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        StartShowSignalListener();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        // StartupUri is intentionally not used: the second instance must not create a window.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        AppLog.Info("Startup completed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _showSignalWait?.Unregister(null);
        _showSignal?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread any more; nothing to release.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        AppLog.Info($"ISLA exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private void StartShowSignalListener()
    {
        _showSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowSignalName);
        _showSignalWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    ShowMainWindow();
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The running copy predates the signal or is shutting down; exiting is enough.
        }
        catch (UnauthorizedAccessException exception)
        {
            AppLog.Warn("Could not signal the running ISLA instance.", exception);
        }
    }

    private static void ShowMainWindow()
    {
        var app = Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is not { } window)
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            AppLog.Info("Another instance requested the island; bringing it to the front.");
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);

        try
        {
            System.Windows.MessageBox.Show(
                $"ISLA hit an unexpected error and will keep running.\n\n{e.Exception.Message}\n\nDetails were written to:\n{AppLog.FilePath}",
                "ISLA error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // If the message box itself fails, the log entry is still there.
        }

        // Keep the island alive; most failures here are in optional panels.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Fatal: the runtime is about to terminate the process.
        AppLog.Error("Unhandled exception on a background thread.", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
