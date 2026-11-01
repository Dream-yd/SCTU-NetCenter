using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace BluetoothAudioReceiver.Services;

/// <summary>
/// A single audio render endpoint, enriched with the flags the app needs to
/// make a safe routing decision.
/// </summary>
public sealed class AudioEndpoint
{
    public AudioEndpoint(string id, string friendlyName, string deviceName, bool isDefault,
        bool isDefaultCommunication, bool isVirtual, bool isHdmi, bool isHandsFree, bool isActive, string format)
    {
        Id = id;
        FriendlyName = friendlyName;
        DeviceName = deviceName;
        IsDefault = isDefault;
        IsDefaultCommunication = isDefaultCommunication;
        IsVirtual = isVirtual;
        IsHdmi = isHdmi;
        IsHandsFree = isHandsFree;
        IsActive = isActive;
        Format = format;
    }

    public string Id { get; }
    public string FriendlyName { get; }
    public string DeviceName { get; }
    public bool IsDefault { get; }
    public bool IsDefaultCommunication { get; }
    public bool IsVirtual { get; }
    public bool IsHdmi { get; }
    public bool IsHandsFree { get; }
    public bool IsActive { get; }
    public string Format { get; }

    /// <summary>
    /// True when this endpoint can plausibly play audio the user will actually hear.
    /// </summary>
    public bool IsSafeOutput => IsActive && !IsVirtual && !IsHdmi && !IsHandsFree;

    public override string ToString()
    {
        var tags = new List<string>();
        if (IsDefault) tags.Add("default");
        if (IsVirtual) tags.Add("virtual");
        if (IsHdmi) tags.Add("hdmi");
        if (IsHandsFree) tags.Add("hands-free");
        var suffix = tags.Count == 0 ? string.Empty : $" [{string.Join(", ", tags)}]";
        return $"{FriendlyName}{suffix}";
    }
}

/// <summary>
/// Which Windows default role to work with.
/// </summary>
public enum DefaultRole
{
    Multimedia,
    Communications
}

/// <summary>
/// Enumerates Windows render endpoints, reads the current default device and
/// (unlike NAudio) can change the system default render device.
///
/// Why this exists: <see cref="Windows.Media.Audio.AudioPlaybackConnection"/>
/// has no API to choose a render target. Audio received from the phone is played
/// on whatever the current default playback device is. On machines with virtual
/// sound cards (Nahimic, NVIDIA Broadcast, ToDesk, Voicemeeter, ...) or a sleeping
/// HDMI/TV output, that default is frequently *not* something a human can hear,
/// which shows up as "phone says connected, PC plays nothing".
/// </summary>
public sealed class AudioEndpointManager
{
    private const string RenderEndpointsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    // PKEY_Device_DeviceDesc, e.g. "Speakers"
    private const string EndpointNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    // Adapter name, e.g. "Realtek(R) Audio"
    private const string AdapterNameValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    private static readonly string[] VirtualHints =
    {
        "virtual", "virtuell", "vb-audio", "vb cable", "cable output", "cable input",
        "voicemeeter", "nahimic", "broadcast", "todesk", "steam streaming",
        "mirroring", "easy surround", "dolby", "sonic studio", "vad", "loopback",
        "虚拟", "虚拟音频", "虚拟声卡", "网易虚拟", "内录", "环回"
    };

    private static readonly string[] HdmiHints =
    {
        "hdmi", "displayport", "digital audio", "nvidia output", "high definition audio",
        "smart tv", "television", "monitor audio"
    };

    private static readonly string[] HandsFreeHints =
    {
        "hands-free", "handsfree", "hands free", "hf audio", "免提"
    };

    /// <summary>
    /// All render endpoints. Never throws; returns an empty list on failure.
    /// </summary>
    /// <param name="activeOnly">Skip disabled/unplugged endpoints.</param>
    /// <param name="includeFormat">
    /// Also read the mix format. This activates an IAudioClient per endpoint and can
    /// take seconds on machines with virtual drivers, so it is opt-in (diagnostics only).
    /// </param>
    public IReadOnlyList<AudioEndpoint> GetRenderEndpoints(bool activeOnly = true, bool includeFormat = false)
    {
        var result = new List<AudioEndpoint>();
        try
        {
            result = EnumerateFromRegistry(activeOnly);
        }
        catch (Exception ex)
        {
            AppLog.Error("Enumerating render endpoints from the registry failed", ex);
            result = new List<AudioEndpoint>();
        }

        if (includeFormat)
        {
            FillFormats(result);
        }

        return result;
    }

    /// <summary>
    /// Reads endpoints from HKLM\...\MMDevices\Audio\Render.
    ///
    /// Reading IMMDevice property stores costs roughly 400 ms *per property* on some
    /// machines (virtual audio drivers, Bluetooth stacks), which made routing take
    /// seconds. The registry holds the same data and is read in a few milliseconds.
    /// </summary>
    private List<AudioEndpoint> EnumerateFromRegistry(bool activeOnly)
    {
        var result = new List<AudioEndpoint>();
        var defaultId = GetDefaultId(DefaultRole.Multimedia);
        var defaultCommId = GetDefaultId(DefaultRole.Communications);

        using var root = Registry.LocalMachine.OpenSubKey(RenderEndpointsKey);
        if (root == null)
        {
            return result;
        }

        foreach (var subKeyName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(subKeyName);
            if (key == null) continue;

            // DeviceState: low nibble is the state, 1 = active (higher bits are flags).
            var stateValue = key.GetValue("DeviceState");
            var state = stateValue is int value ? value : -1;
            var isActive = (state & 0xF) == 1;
            if (activeOnly && !isActive) continue;

            using var properties = key.OpenSubKey("Properties");
            if (properties == null) continue;

            var endpointName = properties.GetValue(EndpointNameValue) as string ?? string.Empty;
            var adapterName = properties.GetValue(AdapterNameValue) as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(endpointName) && string.IsNullOrWhiteSpace(adapterName)) continue;

            // Windows composes the display name as "Speakers (Realtek(R) Audio)".
            var display = string.IsNullOrWhiteSpace(adapterName)
                ? endpointName
                : string.IsNullOrWhiteSpace(endpointName) ? adapterName : $"{endpointName} ({adapterName})";

            var id = $"{{0.0.0.00000000}}.{subKeyName.ToLowerInvariant()}";
            const string format = "unknown";

            result.Add(new AudioEndpoint(
                id,
                display,
                adapterName,
                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                string.Equals(id, defaultCommId, StringComparison.OrdinalIgnoreCase),
                MatchesAny(display, adapterName, VirtualHints),
                MatchesAny(display, adapterName, HdmiHints),
                MatchesAny(display, adapterName, HandsFreeHints),
                isActive,
                format));
        }

        return result;
    }

    private void FillFormats(List<AudioEndpoint> endpoints)
    {
        if (endpoints.Count == 0) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                try
                {
                    using var device = enumerator.GetDevice(endpoint.Id);
                    var formatText = SafeFormat(device);
                    if (formatText == "unknown") continue;
                    endpoints[i] = new AudioEndpoint(endpoint.Id, endpoint.FriendlyName, endpoint.DeviceName,
                        endpoint.IsDefault, endpoint.IsDefaultCommunication, endpoint.IsVirtual, endpoint.IsHdmi,
                        endpoint.IsHandsFree, endpoint.IsActive, formatText);
                }
                catch
                {
                    // keep the placeholder
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading endpoint formats failed", ex);
        }
    }

    public string? GetDefaultId(DefaultRole role = DefaultRole.Multimedia)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return TryGetDefault(enumerator, role == DefaultRole.Multimedia ? Role.Multimedia : Role.Communications);
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading default render endpoint failed", ex);
            return null;
        }
    }

    public AudioEndpoint? GetDefaultEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device == null) return null;
            var friendly = SafeFriendlyName(device);
            var deviceName = SafeDeviceName(device);
            return new AudioEndpoint(device.ID, friendly, deviceName, true, false,
                MatchesAny(friendly, deviceName, VirtualHints),
                MatchesAny(friendly, deviceName, HdmiHints),
                MatchesAny(friendly, deviceName, HandsFreeHints),
                true, SafeFormat(device));
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading default endpoint failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Peak level of the endpoint's master meter (0..1). Used only for diagnostics.
    /// </summary>
    public float GetPeak(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return device.AudioMeterInformation?.MasterPeakValue ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>
    /// Decides which endpoint the phone audio should come out of.
    /// Returns null when the current default is already fine (no change needed).
    /// </summary>
    /// <param name="phoneName">Bluetooth device name, e.g. "Pixel 8".</param>
    /// <param name="preferredDeviceId">User-selected endpoint id, or null/empty for automatic.</param>
    public AudioEndpoint? ResolveStreamingTarget(string? phoneName, string? preferredDeviceId)
    {
        var endpoints = GetRenderEndpoints();
        if (endpoints.Count == 0) return null;

        // 1) Windows sometimes exposes a dedicated render endpoint for the phone's
        //    A2DP sink. If it exists it is always the correct target.
        if (!string.IsNullOrWhiteSpace(phoneName))
        {
            var phoneEndpoint = endpoints.FirstOrDefault(e =>
                !e.IsHandsFree &&
                (e.FriendlyName.Contains(phoneName, StringComparison.OrdinalIgnoreCase) ||
                 e.DeviceName.Contains(phoneName, StringComparison.OrdinalIgnoreCase)));
            if (phoneEndpoint != null)
            {
                return phoneEndpoint;
            }
        }

        // 2) Explicit user preference.
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var preferred = endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase) && e.IsActive);
            if (preferred != null)
            {
                return preferred;
            }
        }

        // 3) Current default is already something audible -> leave the system alone.
        var currentDefault = endpoints.FirstOrDefault(e => e.IsDefault);
        if (currentDefault != null && currentDefault.IsSafeOutput)
        {
            return null;
        }

        // 4) Current default is virtual / HDMI / hands-free -> pick the best audible device.
        var best = endpoints.FirstOrDefault(e => e.IsSafeOutput)
                   ?? endpoints.FirstOrDefault(e => e.IsActive && !e.IsHandsFree);
        return best;
    }

    /// <summary>
    /// Changes the default endpoint for one role. Returns true when the call succeeded.
    /// </summary>
    public bool SetDefault(string deviceId, DefaultRole role = DefaultRole.Multimedia)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        IPolicyConfig? policyConfig = null;
        try
        {
            policyConfig = (IPolicyConfig)new PolicyConfigClient();
            var nativeRole = role == DefaultRole.Multimedia ? ERole.Multimedia : ERole.Communications;
            var hr = policyConfig.SetDefaultEndpoint(deviceId, nativeRole);

            if (hr != 0)
            {
                AppLog.Warn($"SetDefaultEndpoint({nativeRole}) failed: HRESULT 0x{hr:X8}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Changing the default render endpoint failed", ex);
            return false;
        }
        finally
        {
            if (policyConfig != null)
            {
                try { Marshal.ReleaseComObject(policyConfig); } catch { /* ignore */ }
            }
        }
    }

    private static string? TryGetDefault(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return device?.ID;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try { return device.FriendlyName ?? string.Empty; } catch { return string.Empty; }
    }

    private static string SafeDeviceName(MMDevice device)
    {
        try { return device.DeviceFriendlyName ?? string.Empty; } catch { return string.Empty; }
    }

    private static string SafeFormat(MMDevice device)
    {
        try
        {
            var format = device.AudioClient?.MixFormat;
            if (format == null) return "unknown";
            return $"{format.SampleRate} Hz / {format.BitsPerSample} bit / {format.Channels} ch";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool MatchesAny(string friendlyName, string deviceName, string[] hints)
    {
        foreach (var hint in hints)
        {
            if (friendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                deviceName.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    #region COM interop (undocumented but stable since Windows 7)

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    #endregion
}
