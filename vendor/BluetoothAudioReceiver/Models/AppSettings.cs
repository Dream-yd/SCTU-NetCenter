using System;
using System.IO;
using System.Text.Json;

namespace BluetoothAudioReceiver.Models;

/// <summary>
/// Application settings that persist between sessions.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BluetoothAudioReceiver",
        "settings.json");
    
    /// <summary>
    /// Volume level (0-100).
    /// </summary>
    public int Volume { get; set; } = 100;
    
    /// <summary>
    /// Start application with Windows.
    /// </summary>
    public bool AutoStart { get; set; } = false;
    
    /// <summary>
    /// Start minimized to system tray.
    /// </summary>
    public bool StartMinimized { get; set; } = false;
    
    /// <summary>
    /// Minimize to tray instead of taskbar when closing.
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;
    
    /// <summary>
    /// Auto-connect to last used device on startup.
    /// </summary>
    public bool AutoConnect { get; set; } = false;
    
    /// <summary>
    /// Last connected device ID for auto-connect.
    /// </summary>
    public string? LastDeviceId { get; set; }
    
    /// <summary>
    /// Last connected device name for display.
    /// </summary>
    public string? LastDeviceName { get; set; }
    
    /// <summary>
    /// Show notification when connection state changes.
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Re-open the A2DP connection automatically when the link or the audio
    /// stream drops unexpectedly. This is the main fix for "it disconnects
    /// after a while and I have to press Connect again".
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// While streaming, make sure the Windows default playback device is one the
    /// user can actually hear (not a virtual card or a sleeping HDMI output).
    /// AudioPlaybackConnection always renders to the default device, so a wrong
    /// default means "phone connected, PC silent".
    /// </summary>
    public bool PinOutputOnConnect { get; set; } = true;

    /// <summary>
    /// Restore the previous default playback device when the phone disconnects.
    /// </summary>
    public bool RestoreOutputOnDisconnect { get; set; } = true;

    /// <summary>
    /// Preferred render endpoint id for phone audio (null = choose automatically).
    /// </summary>
    public string? PreferredOutputDeviceId { get; set; }

    /// <summary>
    /// Preferred render endpoint name, kept for display in the settings window.
    /// </summary>
    public string? PreferredOutputDeviceName { get; set; }
    
    /// <summary>
    /// UI language code (e.g., "en", "de", "es").
    /// </summary>
    public string Language { get; set; } = "en";
    
    /// <summary>
    /// Loads settings from disk, or returns defaults if not found.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore errors, return defaults
        }
        return new AppSettings();
    }
    
    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}
