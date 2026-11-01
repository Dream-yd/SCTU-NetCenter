using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BluetoothAudioReceiver.Models;
using Windows.Foundation;
using Windows.Media.Audio;

namespace BluetoothAudioReceiver.Services;

/// <summary>
/// High level state of the received audio pipeline.
/// </summary>
public enum ReceiverState
{
    Idle,
    Connecting,
    WaitingForDevice,
    Connected,
    Streaming,
    Reconnecting,
    Faulted
}

/// <summary>
/// Manages the Bluetooth A2DP sink connection (Windows.Media.Audio.AudioPlaybackConnection).
///
/// Compared to the original implementation this service adds:
///  * a real retry loop instead of three 500 ms attempts,
///  * a periodic watchdog that re-opens the stream when the link silently dies,
///  * explicit cleanup between attempts (a half-open connection blocks the next try),
///  * render-endpoint routing so the phone audio does not land on a virtual card
///    or a sleeping HDMI output,
///  * state reporting so the UI can show what is actually happening.
/// </summary>
public class AudioService : IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(8);
    private const int MaxRecoveryAttempts = 12;

    private readonly AudioEndpointManager _endpoints;
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private AudioPlaybackConnection? _connection;
    private string? _deviceId;
    private string? _deviceName;
    private volatile bool _wantsConnection;
    private ReceiverState _state = ReceiverState.Idle;
    private bool _isStreaming;
    private int _recoveryAttempts;
    private DateTime _streamingSince = DateTime.MinValue;
    private DateTime _lastRecoveryUtc = DateTime.MinValue;
    private string? _savedDefaultEndpointId;
    private string? _savedCommEndpointId;
    private bool _commRoleMoved;
    private string? _pinnedEndpointId;
    private string? _activeTargetId;
    private DateTime _lastRouteCheckUtc = DateTime.MinValue;

    public AudioService(AudioEndpointManager endpoints, Func<AppSettings> settings)
    {
        _endpoints = endpoints;
        _settings = settings;
        _ = WatchdogLoopAsync(_lifetime.Token);
    }

    public event EventHandler<ReceiverState>? StateChanged;
    public event EventHandler<bool>? StreamingStateChanged;
    public event EventHandler<string>? ErrorOccurred;

    public ReceiverState State => _state;
    public bool IsStreaming => _isStreaming;
    public bool IsConnected => _wantsConnection && _connection != null;
    public string? CurrentDeviceId => _deviceId;
    public string? CurrentDeviceName => _deviceName;

    /// <summary>
    /// Opens the A2DP connection to the given device. Retries with backoff so a
    /// phone that is still waking up does not have to be clicked twice.
    /// </summary>
    public async Task<bool> OpenConnectionAsync(string deviceId, string? deviceName)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _wantsConnection = true;
        Interlocked.Exchange(ref _recoveryAttempts, 0);
        AppLog.Info($"Connect requested: {deviceName ?? "unknown"} ({deviceId})");

        if (!await _operationGate.WaitAsync(0))
        {
            AppLog.Info("Connect request ignored: another operation is already running");
            return false;
        }

        try
        {
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (!_wantsConnection) return false;

                SetState(attempt == 1 ? ReceiverState.Connecting : ReceiverState.WaitingForDevice);

                if (await TryOpenOnceAsync()) return true;

                if (attempt == maxAttempts) break;

                var delay = TimeSpan.FromMilliseconds(Math.Min(5000, 800 * Math.Pow(1.7, attempt - 1)));
                AppLog.Info($"Open attempt {attempt}/{maxAttempts} failed, retrying in {delay.TotalMilliseconds:0} ms");
                await Task.Delay(delay);
            }

            if (_wantsConnection)
            {
                // Keep the PC announced as an audio sink; the watchdog retries as
                // soon as the phone actually shows up.
                SetState(ReceiverState.WaitingForDevice);
            }
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Tears the stream down and builds it up again. Used by the "Repair" button
    /// and whenever the watchdog detects a dead stream.
    /// </summary>
    public async Task<bool> ReconnectAsync()
    {
        if (_deviceId == null)
        {
            RaiseError("No device selected.");
            return false;
        }

        _wantsConnection = true;
        Interlocked.Exchange(ref _recoveryAttempts, 0);
        AppLog.Info("Manual repair requested");
        return await RecoverAsync("manual repair");
    }

    /// <summary>
    /// User pressed Disconnect: stop reconnecting and release the sink.
    /// </summary>
    public async Task CloseConnectionAsync()
    {
        _wantsConnection = false;
        AppLog.Info("Disconnect requested");

        if (await _operationGate.WaitAsync(500))
        {
            try
            {
                CleanupConnection();
            }
            finally
            {
                _operationGate.Release();
            }
        }
        else
        {
            CleanupConnection();
        }

        RestoreOutputRouting(force: false);
        UpdateStreaming(false);
        SetState(ReceiverState.Idle);
    }

    /// <summary>
    /// Called when the Bluetooth device watcher reports the phone as connected.
    /// Gives the app a push instead of waiting for the next watchdog tick, which
    /// is what makes "phone connects -> audio starts by itself" work.
    /// </summary>
    public void NotifyDeviceLinkUp()
    {
        if (!_wantsConnection) return;
        if (_connection != null && SafeState(_connection) == AudioPlaybackConnectionState.Opened) return;
        if ((DateTime.UtcNow - _lastRecoveryUtc).TotalSeconds < 3) return;

        AppLog.Info("Phone reported connected, trying to open the audio stream");
        _ = RecoverAsync("phone link up");
    }

    private async Task<bool> TryOpenOnceAsync()
    {
        CleanupConnection();

        if (_deviceId == null)
        {
            RaiseError("No device selected.");
            return false;
        }

        try
        {
            var connection = AudioPlaybackConnection.TryCreateFromId(_deviceId);
            if (connection == null)
            {
                RaiseError("Could not create an audio connection. Is the device still paired and A2DP capable?");
                AppLog.Warn("TryCreateFromId returned null");
                return false;
            }

            connection.StateChanged += OnConnectionStateChanged;
            _connection = connection;

            await connection.StartAsync();
            AppLog.Info("A2DP sink announced (StartAsync completed)");

            var openTask = connection.OpenAsync().AsTask();
            var completed = await Task.WhenAny(openTask, Task.Delay(OpenTimeout, _lifetime.Token));
            if (completed != openTask)
            {
                AppLog.Warn($"OpenAsync timed out after {OpenTimeout.TotalSeconds:0}s");
                CleanupConnection();
                return false;
            }

            var result = await openTask;
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                AppLog.Warn($"OpenAsync failed: {result.Status} ({result.ExtendedError?.Message ?? "no extended error"})");
                CleanupConnection();
                return false;
            }

            await WaitForOpenedAsync(connection, 1500);

            var opened = SafeState(connection) == AudioPlaybackConnectionState.Opened;
            AppLog.Info($"A2DP connection opened (state={(opened ? "Opened" : "Closed")})");

            UpdateStreaming(opened);

            SetState(opened ? ReceiverState.Streaming : ReceiverState.Connected);

            // AudioPlaybackConnection renders to the *default* endpoint, so make sure
            // that endpoint is one the user can hear. This touches COM, keep it off
            // the UI thread so the window never freezes.
            await Task.Run(PinOutputRouting);

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Opening the audio connection failed", ex);
            CleanupConnection();
            return false;
        }
    }

    private async Task<bool> RecoverAsync(string reason)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            AppLog.Warn($"Recovering A2DP connection ({reason})");
            SetState(ReceiverState.Reconnecting);

            RestoreOutputRouting(force: false);
            CleanupConnection();

            // Give the audio engine a moment to release the old endpoint.
            await Task.Delay(250);

            if (!_wantsConnection) return false;

            if (await TryOpenOnceAsync()) return true;

            SetState(ReceiverState.WaitingForDevice);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(WatchdogInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await EvaluateHealthAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Watchdog tick failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task EvaluateHealthAsync()
    {
        if (!_wantsConnection) return;

        var settings = _settings();
        var connection = _connection;

        if (connection != null && SafeState(connection) == AudioPlaybackConnectionState.Opened)
        {
            UpdateStreaming(true);
            if (_streamingSince == DateTime.MinValue)
            {
                _streamingSince = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - _streamingSince).TotalSeconds > 30)
            {
                // Stable for a while: forget older failures.
                Interlocked.Exchange(ref _recoveryAttempts, 0);
            }

            MaintainRouting();
            return;
        }

        if (!settings.AutoReconnect) return;
        if (_operationGate.CurrentCount == 0) return; // an operation is in flight
        if ((DateTime.UtcNow - _lastRecoveryUtc) < RecoveryCooldown) return;

        if (Interlocked.Increment(ref _recoveryAttempts) > MaxRecoveryAttempts)
        {
            SetState(ReceiverState.Faulted);
            RaiseError("Automatic reconnect gave up. Use Repair or reconnect manually.");
            return;
        }

        _lastRecoveryUtc = DateTime.UtcNow;
        await RecoverAsync(connection == null ? "connection object missing" : "stream closed");

        // RecoverAsync has its own cooldown bookkeeping through _lastRecoveryUtc.
        _lastRecoveryUtc = DateTime.UtcNow;
    }

    private async Task WaitForOpenedAsync(AudioPlaybackConnection connection, int timeoutMs)
    {
        if (SafeState(connection) == AudioPlaybackConnectionState.Opened) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<AudioPlaybackConnection, object>? handler = null;
        handler = (sender, _) =>
        {
            if (SafeState(sender) == AudioPlaybackConnectionState.Opened)
            {
                tcs.TrySetResult(true);
            }
        };

        try
        {
            connection.StateChanged += handler;
            if (SafeState(connection) == AudioPlaybackConnectionState.Opened)
            {
                return;
            }
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }
        finally
        {
            try { connection.StateChanged -= handler; } catch { /* already disposed */ }
        }
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        try
        {
            var opened = SafeState(sender) == AudioPlaybackConnectionState.Opened;
            AppLog.Info($"A2DP state changed -> {(opened ? "Opened" : "Closed")}");
            UpdateStreaming(opened);

            if (opened)
            {
                Interlocked.Exchange(ref _recoveryAttempts, 0);
                if (_state is ReceiverState.Connected or ReceiverState.Reconnecting or ReceiverState.WaitingForDevice)
                {
                    SetState(ReceiverState.Streaming);
                }
            }
            else if (_state is ReceiverState.Streaming)
            {
                // Do not close here: the watchdog decides whether this is a real drop.
                SetState(ReceiverState.Connected);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("State change handler failed", ex);
        }
    }

    #region Output routing

    /// <summary>
    /// Re-evaluates the routing preference while a stream is already running.
    /// Called after the user changes the output device in the settings window.
    /// </summary>
    public void ReapplyOutputRouting()
    {
        RestoreOutputRouting(force: false);
        PinOutputRouting();
    }

    private void PinOutputRouting()
    {
        var settings = _settings();
        if (!settings.PinOutputOnConnect) return;

        try
        {
            var currentId = _endpoints.GetDefaultId();
            var target = _endpoints.ResolveStreamingTarget(_deviceName, settings.PreferredOutputDeviceId);

            if (target == null)
            {
                _activeTargetId = currentId;
                AppLog.Info("Output routing: current default endpoint is fine");
                return;
            }

            if (string.Equals(target.Id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                _activeTargetId = target.Id;
                AppLog.Info($"Output routing: {target.FriendlyName} is already the default endpoint");
                return;
            }

            _savedDefaultEndpointId ??= currentId;
            var currentCommId = _endpoints.GetDefaultId(DefaultRole.Communications);
            _savedCommEndpointId ??= currentCommId;
            // Only move the communications role if it was pointing at the same
            // endpoint; otherwise we would break a dedicated headset/mic setup.
            var communicationsFollowsMultimedia =
                string.Equals(currentCommId, currentId, StringComparison.OrdinalIgnoreCase);

            if (_endpoints.SetDefault(target.Id, DefaultRole.Multimedia))
            {
                _pinnedEndpointId = target.Id;
                _activeTargetId = target.Id;
                _commRoleMoved = false;

                if (communicationsFollowsMultimedia &&
                    _endpoints.SetDefault(target.Id, DefaultRole.Communications))
                {
                    _commRoleMoved = true;
                }

                AppLog.Info($"Output routing: default endpoint switched to {target} " +
                            $"(previous: {_savedDefaultEndpointId ?? "unknown"})");
            }
            else
            {
                _activeTargetId = currentId;
                AppLog.Warn($"Output routing: could not switch the default endpoint to {target}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Output routing failed", ex);
        }
    }

    /// <summary>
    /// Keeps the phone audio on an audible endpoint. Windows happily re-points the
    /// default to the narrowband Hands-Free (HFP) endpoint when the phone registers
    /// for calls, or to a virtual card that a driver just installed - in both cases
    /// the stream keeps running but nothing is heard.
    /// </summary>
    private void MaintainRouting()
    {
        var settings = _settings();
        if (!settings.PinOutputOnConnect) return;
        if (string.IsNullOrEmpty(_activeTargetId)) return;
        if ((DateTime.UtcNow - _lastRouteCheckUtc).TotalSeconds < 5) return;

        _lastRouteCheckUtc = DateTime.UtcNow;

        try
        {
            var currentId = _endpoints.GetDefaultId();
            if (string.IsNullOrEmpty(currentId)) return;
            if (string.Equals(currentId, _activeTargetId, StringComparison.OrdinalIgnoreCase)) return;

            var current = _endpoints.GetRenderEndpoints()
                .FirstOrDefault(e => string.Equals(e.Id, currentId, StringComparison.OrdinalIgnoreCase));

            if (current == null) return;

            if (current.IsSafeOutput)
            {
                // A real, audible device was chosen (probably by the user): follow it
                // and stop treating the old device as something to restore later.
                AppLog.Info($"Output routing: default endpoint changed to {current.FriendlyName}, following it");
                _activeTargetId = currentId;
                _pinnedEndpointId = null;
                _savedDefaultEndpointId = null;
                _savedCommEndpointId = null;
                _commRoleMoved = false;
                return;
            }

            AppLog.Warn($"Output routing: default endpoint was taken over by '{current.FriendlyName}' " +
                        "(virtual / HDMI / hands-free), switching back");
            if (!_endpoints.SetDefault(_activeTargetId))
            {
                _activeTargetId = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Routing guard failed", ex);
        }
    }

    private void RestoreOutputRouting(bool force)
    {
        var settings = _settings();
        if (!force && !settings.RestoreOutputOnDisconnect) return;
        if (_pinnedEndpointId == null || _savedDefaultEndpointId == null) return;

        try
        {
            var currentId = _endpoints.GetDefaultId();
            if (!force && !string.Equals(currentId, _pinnedEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                // The user changed the default device while streaming: respect that.
                AppLog.Info("Output routing: default endpoint was changed by the user, not restoring");
                _pinnedEndpointId = null;
                _savedDefaultEndpointId = null;
                return;
            }

            if (_endpoints.SetDefault(_savedDefaultEndpointId, DefaultRole.Multimedia))
            {
                AppLog.Info($"Output routing: restored default endpoint to {_savedDefaultEndpointId}");
            }

            if (_commRoleMoved && _savedCommEndpointId != null)
            {
                _endpoints.SetDefault(_savedCommEndpointId, DefaultRole.Communications);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Restoring the default endpoint failed", ex);
        }
        finally
        {
            _pinnedEndpointId = null;
            _savedDefaultEndpointId = null;
            _savedCommEndpointId = null;
            _commRoleMoved = false;
            _activeTargetId = null;
        }
    }

    #endregion

    private void CleanupConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection == null) return;

        try { connection.StateChanged -= OnConnectionStateChanged; } catch { /* ignore */ }
        try { connection.Dispose(); } catch { /* ignore */ }

        UpdateStreaming(false);
    }

    private static AudioPlaybackConnectionState? SafeState(AudioPlaybackConnection connection)
    {
        try { return connection.State; } catch { return null; }
    }

    private void SetState(ReceiverState state)
    {
        if (_state == state) return;
        var previous = _state;
        _state = state;
        AppLog.Info($"Receiver state: {previous} -> {state}");
        try { StateChanged?.Invoke(this, state); } catch (Exception ex) { AppLog.Error("StateChanged handler failed", ex); }
    }

    private void UpdateStreaming(bool streaming)
    {
        if (_isStreaming == streaming) return;
        _isStreaming = streaming;
        _streamingSince = streaming ? DateTime.UtcNow : DateTime.MinValue;
        AppLog.Info($"Streaming = {streaming}");
        try { StreamingStateChanged?.Invoke(this, streaming); } catch (Exception ex) { AppLog.Error("StreamingStateChanged handler failed", ex); }
    }

    private void RaiseError(string message)
    {
        AppLog.Warn($"Error surfaced: {message}");
        try { ErrorOccurred?.Invoke(this, message); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { /* ignore */ }

        CleanupConnection();
        RestoreOutputRouting(force: true);

        _lifetime.Dispose();
        _operationGate.Dispose();
    }
}
