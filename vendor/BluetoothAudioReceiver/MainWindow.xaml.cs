using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using BluetoothAudioReceiver.Models;
using BluetoothAudioReceiver.Services;
using BluetoothAudioReceiver.ViewModels;

namespace BluetoothAudioReceiver;

/// <summary>
/// Main window code-behind with custom title bar and system tray integration.
/// </summary>
public partial class MainWindow : Window
{
    private TrayIconService? _trayService;
    private AppSettings _settings;
    private MainViewModel? _viewModel;
    private bool _isExiting;
    
    public MainWindow()
    {
        InitializeComponent();
        
        // Load settings
        _settings = AppSettings.Load();
        
        // Initialize ViewModel
        _viewModel = new MainViewModel(_settings);
        DataContext = _viewModel;
        
        // Initialize tray icon
        _trayService = new TrayIconService(this, _settings);
        _trayService.ShowWindowRequested += OnShowWindowRequested;
        _trayService.ExitRequested += OnExitRequested;
        _trayService.SettingsRequested += OnSettingsRequested;
        _trayService.RepairRequested += OnRepairRequested;
        _trayService.DiagnosticsRequested += OnDiagnosticsRequested;
        
        // Subscribe to connection state changes for tray notifications
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        
        // Handle minimize to tray
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        
        // Only the autostart entry (which passes --minimized) starts hidden.
        // A manual double click must always show the window, otherwise the app looks
        // like it "does not open" when StartMinimized happens to be enabled.
        var args = Environment.GetCommandLineArgs();
        var launchedByAutostart = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (launchedByAutostart)
        {
            // Hiding inside the constructor is undone by Application.Show(), so wait
            // until the window is actually loaded.
            Loaded += (_, _) =>
            {
                WindowState = WindowState.Minimized;
                if (_settings.MinimizeToTray)
                {
                    Hide();
                }
            };
        }
    }

    /// <summary>
    /// Restores the window from the tray / minimised state and gives it focus.
    /// Called when the user starts the app a second time.
    /// </summary>
    public void BringToFront()
    {
        AppLog.Info("BringToFront requested");

        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Activate() is not enough when another application owns the foreground
        // (Windows' foreground lock). Nudge it with the native calls as well.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwRestore);
            SwitchToThisWindow(handle, true);
            SetForegroundWindow(handle);
        }

        Topmost = true;
        Topmost = false;
        Focus();
    }

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
    
    #region Custom Title Bar
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
    
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimizeToTray)
        {
            WindowState = WindowState.Minimized;
            Hide();
            _trayService?.ShowNotification("Minimiert", "Die App läuft weiter im System Tray.");
        }
        else
        {
            _isExiting = true;
            Close();
        }
    }
    
    #endregion
    
    #region ViewModel Events
    
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null || _trayService == null) return;
        
        if (e.PropertyName == nameof(MainViewModel.Status))
        {
            _trayService.UpdateStatus(_viewModel.Status);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            if (_viewModel.IsConnected)
            {
                _trayService.ShowNotification("Verbunden", 
                    $"Audio-Verbindung zu {_viewModel.SelectedDevice?.Name} hergestellt.");
                
                // Save last device for auto-connect
                if (_viewModel.SelectedDevice != null)
                {
                    _settings.LastDeviceId = _viewModel.SelectedDevice.Id;
                    _settings.LastDeviceName = _viewModel.SelectedDevice.Name;
                    _settings.Save();
                }
            }
        }
    }
    
    #endregion
    
    #region Window State
    
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
        }
    }
    
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
            _trayService?.ShowNotification("Minimiert", 
                "Die App läuft weiter im System Tray.");
        }
        else
        {
            // Actually closing - clean up
            _viewModel?.Dispose();
            _trayService?.Dispose();
        }
    }
    
    #endregion
    
    #region Tray Events
    
    private void OnShowWindowRequested(object? sender, EventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    
    private void OnExitRequested(object? sender, EventArgs e)
    {
        _isExiting = true;
        _viewModel?.Dispose();
        _trayService?.Dispose();
        Application.Current.Shutdown();
    }
    
    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowSettingsWindow();
    }

    private void OnRepairRequested(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.RepairConnectionCommand.Execute(null);
        }
        _trayService?.ShowNotification("Repair", "Rebuilding the audio pipeline...");
    }

    private void OnDiagnosticsRequested(object? sender, EventArgs e)
    {
        ShowDiagnosticReport();
    }

    /// <summary>
    /// Writes a diagnostic report and reveals it in Explorer.
    /// </summary>
    private void ShowDiagnosticReport()
    {
        var path = DiagnosticsService.WriteReport(
            _viewModel?.CurrentReceiverState,
            _viewModel?.SelectedDevice?.Name,
            _settings);

        if (!string.IsNullOrEmpty(path))
        {
            DiagnosticsService.RevealInExplorer(path);
        }
        else
        {
            MessageBox.Show("Could not write the diagnostic report.", "Bluetooth Audio Receiver",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    
    #endregion
    
    #region Button Handlers
    
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }
    
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new HelpWindow { Owner = this };
        helpWindow.ShowDialog();
    }
    
    private void ShowSettingsWindow()
    {
        // Show window if hidden
        if (!IsVisible)
        {
            Show();
            WindowState = WindowState.Normal;
        }
        
        var settingsWindow = new SettingsWindow(_settings) { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            // SettingsWindow mutates the shared AppSettings instance in place, so the
            // ViewModel and the audio service immediately see the new values.
            _viewModel?.OnSettingsChanged();
        }
    }
    
    #endregion
}

/// <summary>
/// Converter to determine if the Open Connection button should be enabled.
/// </summary>
public class CanConnectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return false;
        
        var hasSelectedDevice = values[0] != null;
        var isConnected = values[1] is bool connected && connected;
        var isConnecting = values[2] is bool connecting && connecting;
        
        return hasSelectedDevice && !isConnected && !isConnecting;
    }
    
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
