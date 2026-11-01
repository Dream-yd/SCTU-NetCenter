using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using BluetoothAudioReceiver.Models;
using BluetoothAudioReceiver.Services;

namespace BluetoothAudioReceiver;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string ActivateEventName = "BluetoothAudioReceiver_Activate_2F1B9A";
    private EventWaitHandle? _activateEvent;
    private Thread? _activationThread;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    public App()
    {
        // Global exception handling
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppLog.Info($"App constructed ({Environment.CommandLine})");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            OnStartupCore(e);
        }
        catch (Exception ex)
        {
            // Anything that throws here would otherwise leave the user with a window
            // that never appears and no trace at all.
            LogCrash(ex);
            MessageBox.Show(
                "Bluetooth Audio Receiver konnte nicht starten.\n\n" +
                ex.Message + "\n\nLog: " +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BluetoothAudioReceiver", "logs", "crash_log.txt"),
                "Startfehler", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnStartupCore(StartupEventArgs e)
    {
        // Headless diagnostic mode: writes a report and exits. Useful when the app
        // is deployed as a single exe and no debugger is available.
        if (e.Args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiagnosticsMode();
            Shutdown();
            return;
        }

        const string appName = "BluetoothAudioReceiver_SingleInstance_Mutex_Global";
        bool createdNew;

        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Already running: ask the running instance to show its window instead of
            // dropping the user with a "already running" message box.
            SignalExistingInstance();
            Shutdown();
            return;
        }

        StartActivationListener();

        base.OnStartup(e);

        // The window is created explicitly (App.xaml no longer has StartupUri) so
        // the same Application can run in headless diagnostic mode.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        AppLog.Info("Main window shown");

        // Keep the autostart entry in sync with the current location. If the app was
        // moved (or the entry was disabled by a startup manager) this restores it.
        try
        {
            var settings = AppSettings.Load();
            if (settings.AutoStart)
            {
                var ok = AutoStartService.EnsureAutoStart(settings.StartMinimized);
                AppLog.Info($"Autostart entry in sync: {ok}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Autostart check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tells the already running instance to bring its window to the foreground.
    /// </summary>
    private static void SignalExistingInstance()
    {
        // Windows' foreground lock refuses SetForegroundWindow from a process that
        // does not own the foreground. This brand new process was started by the
        // user, so it may hand the right over to the running instance.
        var granted = AllowSetForegroundWindow(AsfwAny);
        AppLog.Info($"Second instance: granting foreground ({granted})");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
                handle.Set();
                AppLog.Info("Second instance: activation requested, exiting");
                // Stay alive briefly so the grant is still valid when the other
                // instance calls SetForegroundWindow.
                Thread.Sleep(400);
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not signal the running instance: {ex.Message}");
                return;
            }
        }

        AppLog.Warn("Second instance: activation event never appeared");
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(IntPtr processId);

    private static readonly IntPtr AsfwAny = new(-1);

    /// <summary>
    /// Background listener that surfaces the main window when a second launch
    /// asks for it (taskbar / desktop double click).
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activationThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _activateEvent.WaitOne();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.BringToFront()));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Activation dispatch failed: {ex.Message}");
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceActivation"
            };
            _activationThread.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Activation listener could not start: {ex.Message}");
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true; // Prevent immediate termination if possible to show dialog

        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothAudioReceiver", "logs", "crash_log.txt");
        MessageBox.Show($"An unexpected error occurred. The application will close.\n\nPlease check the log file for details:\n{logPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// Builds a diagnostic report, mirrors it to stdout (if a console is attached)
    /// and to logs\diag-last.txt, then returns.
    /// </summary>
    private static void RunDiagnosticsMode()
    {
        try
        {
            var settings = AppSettings.Load();
            var report = DiagnosticsService.BuildReport(null, settings.LastDeviceName, settings);
            var stamped = DiagnosticsService.WriteReport(null, settings.LastDeviceName, settings);

            try
            {
                File.WriteAllText(Path.Combine(AppLog.LogDirectory, "diag-last.txt"), report, new UTF8Encoding(false));
            }
            catch
            {
                // best effort
            }

            AttachConsole(AttachParentProcess);
            Console.WriteLine(report);
            Console.WriteLine($"Report written to: {stamped}");
        }
        catch (Exception ex)
        {
            try
            {
                AttachConsole(AttachParentProcess);
                Console.WriteLine("Diagnostics failed: " + ex);
            }
            catch
            {
                // nothing else we can do
            }
        }
    }

    private void LogCrash(Exception ex)
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothAudioReceiver", "logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            string logPath = Path.Combine(logDir, "crash_log.txt");

            // Log rotation: if file > 5MB, rotate to .bak
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
            {
                string backupPath = Path.Combine(logDir, "crash_log.bak");
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(logPath, backupPath);
            }

            string errorMessage = $"[{DateTime.Now}] CRASH REPORT:\n{ex.GetType()}: {ex.Message}\nStack Trace:\n{ex.StackTrace}\n\nInner Exception:\n{ex.InnerException?.Message}\n--------------------------------------------------\n";
            File.AppendAllText(logPath, errorMessage);
        }
        catch
        {
            // Fallback if writing fails
        }
    }
}
