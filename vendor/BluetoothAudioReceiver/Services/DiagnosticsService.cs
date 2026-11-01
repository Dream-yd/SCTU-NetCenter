using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;
using BluetoothAudioReceiver.Models;

namespace BluetoothAudioReceiver.Services;

/// <summary>
/// Collects everything needed to explain "phone says connected but the PC is
/// silent" or "audio stutters every few seconds" without guessing.
/// </summary>
public static class DiagnosticsService
{
    private static readonly Dictionary<string, string> ProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0000110A"] = "A2DP Source (device can stream audio to the PC)",
        ["0000110B"] = "A2DP Sink (PC could stream audio to this device)",
        ["0000110C"] = "AVRCP Target",
        ["0000110E"] = "AVRCP Controller",
        ["0000111E"] = "Hands-Free (HFP) - can steal the link during calls",
        ["0000111F"] = "Hands-Free Audio Gateway",
        ["00001112"] = "Headset Audio Gateway",
        ["00001108"] = "Headset (HSP)"
    };

    public static string WriteReport(ReceiverState? state, string? deviceName, AppSettings? settings)
    {
        var report = BuildReport(state, deviceName, settings);
        var path = Path.Combine(AppLog.LogDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            File.WriteAllText(path, report, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Writing the diagnostic report failed", ex);
        }
        return path;
    }

    public static string BuildReport(ReceiverState? state, string? deviceName, AppSettings? settings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Bluetooth Audio Receiver - diagnostic report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"App version: {typeof(DiagnosticsService).Assembly.GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString} ({Environment.OSVersion.Version})");
        sb.AppendLine($"Runtime: {Environment.Version}, 64-bit process: {Environment.Is64BitProcess}");
        sb.AppendLine();

        sb.AppendLine("=== APP STATE ===");
        sb.AppendLine($"Selected device: {deviceName ?? "-"}");
        sb.AppendLine($"Receiver state: {state?.ToString() ?? "-"}");
        if (settings != null)
        {
            sb.AppendLine($"AutoReconnect: {settings.AutoReconnect}");
            sb.AppendLine($"PinOutputOnConnect: {settings.PinOutputOnConnect}");
            sb.AppendLine($"RestoreOutputOnDisconnect: {settings.RestoreOutputOnDisconnect}");
            sb.AppendLine($"PreferredOutputDeviceId: {settings.PreferredOutputDeviceId ?? "(auto)"}");
            sb.AppendLine($"AutoConnect: {settings.AutoConnect}, LastDevice: {settings.LastDeviceName ?? "-"}");
        }
        sb.AppendLine();

        var endpoints = new List<AudioEndpoint>();
        try
        {
            var manager = new AudioEndpointManager();
            endpoints = manager.GetRenderEndpoints(includeFormat: true).ToList();

            sb.AppendLine("=== RENDER ENDPOINTS (active) ===");
            foreach (var endpoint in endpoints)
            {
                var flags = new List<string>();
                if (endpoint.IsDefault) flags.Add("DEFAULT");
                if (endpoint.IsDefaultCommunication) flags.Add("DEFAULT-COMM");
                if (endpoint.IsVirtual) flags.Add("VIRTUAL");
                if (endpoint.IsHdmi) flags.Add("HDMI");
                if (endpoint.IsHandsFree) flags.Add("HANDS-FREE");
                if (!endpoint.IsSafeOutput) flags.Add("NOT-AUDIBLE?");
                sb.AppendLine($"  - {endpoint.FriendlyName} | {endpoint.DeviceName} | {endpoint.Format} " +
                              $"| {string.Join(", ", flags)}");
                sb.AppendLine($"      id={endpoint.Id}");
            }

            var defaultEndpoint = endpoints.FirstOrDefault(e => e.IsDefault);
            sb.AppendLine();
            sb.AppendLine("=== ROUTING VERDICT ===");
            sb.AppendLine(defaultEndpoint == null
                ? "  No default render endpoint found."
                : $"  Default: {defaultEndpoint.FriendlyName} ({defaultEndpoint.Format})");
            var target = manager.ResolveStreamingTarget(deviceName, settings?.PreferredOutputDeviceId);
            sb.AppendLine(target == null
                ? "  App would use the current default endpoint (it looks audible)."
                : $"  App would switch the default endpoint to: {target.FriendlyName}");

            sb.AppendLine();
            sb.AppendLine("=== WARNINGS ===");
            var warnings = BuildWarnings(endpoints, target);
            if (warnings.Count == 0)
            {
                sb.AppendLine("  none");
            }
            else
            {
                foreach (var warning in warnings)
                {
                    sb.AppendLine($"  * {warning}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Endpoint inspection failed: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("=== BLUETOOTH PROFILES IN THE REGISTRY ===");
        foreach (var line in EnumerateBluetoothProfiles())
        {
            sb.AppendLine("  " + line);
        }

        sb.AppendLine();
        sb.AppendLine("=== NETWORK ADAPTERS ===");
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                sb.AppendLine($"  - {nic.Name} | {nic.NetworkInterfaceType} | {nic.Description} | " +
                              $"{nic.Speed / 1_000_000} Mbps");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  unavailable: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("=== RECENT APPLICATION LOG ===");
        foreach (var line in AppLog.GetRecent(120))
        {
            sb.AppendLine("  " + line);
        }

        sb.AppendLine();
        sb.AppendLine("=== CRASH LOG TAIL ===");
        var crashLog = Path.Combine(AppLog.LogDirectory, "crash_log.txt");
        try
        {
            if (File.Exists(crashLog))
            {
                var lines = File.ReadAllLines(crashLog);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - 40)))
                {
                    sb.AppendLine("  " + line);
                }
            }
            else
            {
                sb.AppendLine("  no crash log");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  unavailable: {ex.Message}");
        }

        return sb.ToString();
    }

    private static List<string> BuildWarnings(IReadOnlyList<AudioEndpoint> endpoints, AudioEndpoint? target)
    {
        var warnings = new List<string>();

        var currentDefault = endpoints.FirstOrDefault(e => e.IsDefault);
        if (currentDefault != null && currentDefault.IsVirtual)
        {
            warnings.Add($"The default playback device is a virtual card ({currentDefault.FriendlyName}). " +
                         "Phone audio will be routed into a virtual cable instead of the speakers.");
        }
        if (currentDefault != null && currentDefault.IsHdmi)
        {
            warnings.Add($"The default playback device is an HDMI/DisplayPort output ({currentDefault.FriendlyName}). " +
                         "If that display is off or asleep, the phone audio is simply lost.");
        }
        if (currentDefault != null && currentDefault.IsHandsFree)
        {
            warnings.Add($"The default playback device is a hands-free (HFP) endpoint ({currentDefault.FriendlyName}). " +
                         "That endpoint is narrowband and is the classic cause of muffled/choppy audio.");
        }

        var virtualCount = endpoints.Count(e => e.IsVirtual);
        if (virtualCount >= 3)
        {
            warnings.Add($"{virtualCount} virtual audio devices are installed. " +
                         "They regularly grab the default role after driver updates or reboots.");
        }

        if (target != null && currentDefault != null && !string.Equals(target.Id, currentDefault.Id, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Routing currently points to an endpoint the app considers inaudible; " +
                         "it will switch to '" + target.FriendlyName + "' on connect.");
        }

        var hfp = endpoints.Where(e => e.IsHandsFree).ToList();
        if (hfp.Count > 0)
        {
            warnings.Add("A hands-free (HFP) endpoint exists for a Bluetooth device (" +
                         string.Join(", ", hfp.Select(e => e.FriendlyName)) + "). " +
                         "Incoming calls/notifications can renegotiate the link to HFP, which silences " +
                         "A2DP for a moment and sounds like a dropout.");
        }

        return warnings;
    }

    private static IEnumerable<string> EnumerateBluetoothProfiles()
    {
        var results = new List<string>();
        const string bthEnum = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(bthEnum);
            if (root == null)
            {
                results.Add("BTHENUM key not readable");
                return results;
            }

            foreach (var serviceKeyName in root.GetSubKeyNames())
            {
                var guidStart = serviceKeyName.IndexOf('{');
                if (guidStart < 0 || serviceKeyName.Length < guidStart + 9)
                {
                    continue;
                }

                var guid = serviceKeyName.Substring(guidStart + 1, 8);
                if (!ProfileNames.TryGetValue(guid, out var profile))
                {
                    continue;
                }

                using var serviceKey = root.OpenSubKey(serviceKeyName);
                if (serviceKey == null) continue;

                foreach (var instanceName in serviceKey.GetSubKeyNames())
                {
                    using var instance = serviceKey.OpenSubKey(instanceName);
                    var friendly = CleanIndirectString(instance?.GetValue("FriendlyName") as string);
                    var address = ExtractBluetoothAddress(instanceName);
                    results.Add($"{profile}: {friendly} [{address}]");
                }
            }
        }
        catch (Exception ex)
        {
            results.Add($"unavailable: {ex.Message}");
        }

        if (results.Count == 0)
        {
            results.Add("no Bluetooth service instances found");
        }

        return results;
    }

    private static string CleanIndirectString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(no name)";
        }

        var value = raw.Replace("\r", " ").Replace("\n", " ").Trim();

        // Bluetooth friendly names are indirect strings such as
        // "@System32\drivers\btha2dp.sys,#1;%1 A2DP SNK%0" - keep the readable tail.
        if (value.StartsWith("@", StringComparison.Ordinal))
        {
            var separator = value.LastIndexOf(';');
            if (separator > 0 && separator < value.Length - 1)
            {
                value = value.Substring(separator + 1);
            }
        }

        value = value.Replace("%0", string.Empty).Replace("%1", string.Empty)
                     .Replace("%2", string.Empty).Replace("%3", string.Empty).Trim();

        return string.IsNullOrWhiteSpace(value) ? "(no name)" : value;
    }

    private static string ExtractBluetoothAddress(string instanceName)
    {
        // Instance names end with the 12 hex digit device address, e.g.
        // 8&191A8764&0&10C1975368D3_C00000000
        var parts = instanceName.Split('&');
        var last = parts.LastOrDefault()?.Split('_')[0] ?? string.Empty;
        return last.Length == 12 ? last : instanceName;
    }

    /// <summary>
    /// Opens Explorer with the report selected so the user can attach it.
    /// </summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open Explorer", ex);
        }
    }
}
