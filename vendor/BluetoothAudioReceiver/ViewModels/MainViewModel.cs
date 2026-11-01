using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BluetoothAudioReceiver.Models;
using BluetoothAudioReceiver.Services;

namespace BluetoothAudioReceiver.ViewModels;

/// <summary>
/// Main ViewModel for the Bluetooth Audio Receiver application.
/// Uses MVVM pattern with CommunityToolkit.Mvvm for clean separation.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly BluetoothService _bluetoothService;
    private readonly AudioService _audioService;
    private readonly AudioEndpointManager _endpointManager;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly object _devicesLock = new();
    private bool _isEnumerating = true;
    private bool _autoConnectPending;

    [ObservableProperty]
    private ObservableCollection<BluetoothDevice> _devices = new();

    [ObservableProperty]
    private BluetoothDevice? _selectedDevice;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _loc.CurrentLanguage = settings.Language;
        _bluetoothService = new BluetoothService();
        _endpointManager = new AudioEndpointManager();
        _audioService = new AudioService(_endpointManager, () => _settings);

        Status = _loc.Get("Idle");
        StatusDetail = _loc.Get("SelectDevice");

        // Enable multi-threaded access to the collection
        BindingOperations.EnableCollectionSynchronization(Devices, _devicesLock);

        // Wire up events (event-driven, no polling!)
        _bluetoothService.DeviceAdded += OnDeviceAdded;
        _bluetoothService.DeviceRemoved += OnDeviceRemoved;
        _bluetoothService.DeviceUpdated += OnDeviceUpdated;
        _bluetoothService.EnumerationCompleted += OnEnumerationCompleted;

        _audioService.StateChanged += OnReceiverStateChanged;
        _audioService.StreamingStateChanged += OnStreamingStateChanged;
        _audioService.ErrorOccurred += OnAudioError;

        _autoConnectPending = settings.AutoConnect && !string.IsNullOrEmpty(settings.LastDeviceId);
        if (_autoConnectPending)
        {
            AppLog.Info($"Auto-connect armed for {settings.LastDeviceName ?? settings.LastDeviceId}");
        }

        AppLog.Info("MainViewModel initialised");

        // Start discovering devices
        _bluetoothService.StartWatching();
    }

    private void OnDeviceAdded(object? sender, BluetoothDevice device)
    {
        lock (_devicesLock)
        {
            Devices.Add(device);
            if (!_isEnumerating)
            {
                StatusDetail = string.Format(_loc.Get("DevicesFound"), Devices.Count);
            }
        }

        TryAutoConnect(device);
    }

    private void OnEnumerationCompleted(object? sender, EventArgs e)
    {
        lock (_devicesLock)
        {
            _isEnumerating = false;
            StatusDetail = string.Format(_loc.Get("DevicesFound"), Devices.Count);
        }

        if (_autoConnectPending)
        {
            _autoConnectPending = false;
            AppLog.Warn("Auto-connect skipped: the last used device was not found");
        }
    }

    private void OnDeviceRemoved(object? sender, string deviceId)
    {
        lock (_devicesLock)
        {
            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                if (Devices[i].Id == deviceId)
                {
                    Devices.RemoveAt(i);
                    break;
                }
            }
            StatusDetail = string.Format(_loc.Get("DevicesFound"), Devices.Count);
        }
    }

    private void OnDeviceUpdated(object? sender, BluetoothDevice device)
    {
        // Push the "phone is back" event into the audio service so it can reopen
        // the stream without waiting for the next watchdog tick.
        if (!device.IsConnected) return;
        if (!string.Equals(device.Id, _audioService.CurrentDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        _audioService.NotifyDeviceLinkUp();
    }

    private void TryAutoConnect(BluetoothDevice device)
    {
        if (!_autoConnectPending) return;
        if (!string.Equals(device.Id, _settings.LastDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        _autoConnectPending = false;
        AppLog.Info($"Auto-connecting to {device.Name}");
        SelectedDevice = device;
        _ = ConnectAsync(device);
    }

    private void OnReceiverStateChanged(object? sender, ReceiverState state)
    {
        RunOnUi(() =>
        {
            IsConnected = state is ReceiverState.Connected or ReceiverState.Streaming;
            IsConnecting = state is ReceiverState.Connecting or ReceiverState.WaitingForDevice or ReceiverState.Reconnecting;

            var deviceName = SelectedDevice?.Name ?? _audioService.CurrentDeviceName;

            switch (state)
            {
                case ReceiverState.Idle:
                    Status = _loc.Get("Idle");
                    StatusDetail = _loc.Get("SelectDevice");
                    break;
                case ReceiverState.Connecting:
                    Status = _loc.Get("Connecting");
                    StatusDetail = string.Format(_loc.Get("OpeningConnectionTo"), deviceName);
                    break;
                case ReceiverState.WaitingForDevice:
                    Status = _loc.Get("WaitingForDevice");
                    StatusDetail = _loc.Get("WaitingForDeviceDetail");
                    break;
                case ReceiverState.Connected:
                    Status = _loc.Get("Connected");
                    StatusDetail = string.Format(_loc.Get("AudioReadyFrom"), deviceName);
                    ErrorMessage = null;
                    break;
                case ReceiverState.Streaming:
                    Status = _loc.Get("Streaming");
                    StatusDetail = string.Format(_loc.Get("ReceivingAudioFrom"), deviceName);
                    ErrorMessage = null;
                    break;
                case ReceiverState.Reconnecting:
                    Status = _loc.Get("Reconnecting");
                    StatusDetail = _loc.Get("ReconnectingDetail");
                    break;
                case ReceiverState.Faulted:
                    Status = _loc.Get("ConnectionLost");
                    StatusDetail = _loc.Get("ConnectionLostDetail");
                    break;
            }
        });
    }

    private void OnStreamingStateChanged(object? sender, bool streaming)
    {
        RunOnUi(() =>
        {
            if (streaming)
            {
                Status = _loc.Get("Streaming");
                StatusDetail = string.Format(_loc.Get("ReceivingAudioFrom"),
                    SelectedDevice?.Name ?? _audioService.CurrentDeviceName);
            }
        });
    }

    private void OnAudioError(object? sender, string error)
    {
        RunOnUi(() =>
        {
            ErrorMessage = error;
            IsConnecting = false;
        });
    }

    [RelayCommand]
    private async Task OpenConnectionAsync()
    {
        if (SelectedDevice == null) return;
        await ConnectAsync(SelectedDevice);
    }

    private async Task ConnectAsync(BluetoothDevice device)
    {
        IsConnecting = true;
        ErrorMessage = null;
        Status = _loc.Get("Connecting");
        StatusDetail = string.Format(_loc.Get("OpeningConnectionTo"), device.Name);

        await _audioService.OpenConnectionAsync(device.Id, device.Name);

        if (_audioService.State is ReceiverState.WaitingForDevice or ReceiverState.Faulted)
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task CloseConnectionAsync()
    {
        await _audioService.CloseConnectionAsync();
    }

    /// <summary>
    /// Tears the audio pipeline down and rebuilds it. One click instead of
    /// "disconnect, wait, connect" when the stream went silent.
    /// </summary>
    [RelayCommand]
    private async Task RepairConnectionAsync()
    {
        if (_audioService.CurrentDeviceId == null && SelectedDevice != null)
        {
            await ConnectAsync(SelectedDevice);
            return;
        }

        IsConnecting = true;
        ErrorMessage = null;
        await _audioService.ReconnectAsync();
        IsConnecting = false;
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        lock (_devicesLock)
        {
            Devices.Clear();
        }
        _bluetoothService.StopWatching();

        _isEnumerating = true;
        StatusDetail = _loc.Get("Scanning");
        _bluetoothService.StartWatching();
    }

    /// <summary>
    /// Current state of the audio pipeline, used by the diagnostic report.
    /// </summary>
    public ReceiverState CurrentReceiverState => _audioService.State;

    /// <summary>
    /// Called after the settings dialog closes so the UI reflects changes that do
    /// not have a bound property (for example language or routing preferences).
    /// </summary>
    public void OnSettingsChanged()
    {
        _loc.CurrentLanguage = _settings.Language;
        if (_audioService.State == ReceiverState.Streaming && _settings.PinOutputOnConnect)
        {
            _audioService.ReapplyOutputRouting();
        }
    }

    private static void RunOnUi(Action action)    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        _bluetoothService.DeviceAdded -= OnDeviceAdded;
        _bluetoothService.DeviceRemoved -= OnDeviceRemoved;
        _bluetoothService.DeviceUpdated -= OnDeviceUpdated;
        _bluetoothService.EnumerationCompleted -= OnEnumerationCompleted;

        _audioService.StateChanged -= OnReceiverStateChanged;
        _audioService.StreamingStateChanged -= OnStreamingStateChanged;
        _audioService.ErrorOccurred -= OnAudioError;

        _bluetoothService.Dispose();
        _audioService.Dispose();
    }
}
