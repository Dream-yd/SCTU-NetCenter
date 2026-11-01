using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BluetoothAudioReceiver.Models;
using BluetoothAudioReceiver.Services;

namespace BluetoothAudioReceiver;

/// <summary>
/// Entry in the output device combo box. Id = empty string means "automatic".
/// ToString is overridden because the custom ComboBox template in DarkTheme.xaml
/// renders SelectionBoxItem directly instead of honouring DisplayMemberPath.
/// </summary>
public sealed class OutputDeviceChoice
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    public override string ToString() => Label;
}

/// <summary>
/// Settings window code-behind.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettings _originalSettings;
    private readonly VolumeService _volumeService;
    private readonly LocalizationService _loc;
    
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        
        _settings = settings;
        _volumeService = new VolumeService();
        _loc = LocalizationService.Instance;
        
        // Apply current language
        _loc.CurrentLanguage = settings.Language;
        
        // Sync volume with current system volume
        _settings.Volume = _volumeService.GetVolume();
        
        // Store original values to detect changes
        _originalSettings = new AppSettings
        {
            Volume = settings.Volume,
            AutoStart = settings.AutoStart,
            StartMinimized = settings.StartMinimized,
            MinimizeToTray = settings.MinimizeToTray,
            AutoConnect = settings.AutoConnect,
            ShowNotifications = settings.ShowNotifications,
            Language = settings.Language,
            AutoReconnect = settings.AutoReconnect,
            PinOutputOnConnect = settings.PinOutputOnConnect,
            RestoreOutputOnDisconnect = settings.RestoreOutputOnDisconnect,
            PreferredOutputDeviceId = settings.PreferredOutputDeviceId
        };
        
        DataContext = _settings;
        
        // Setup language ComboBox
        LanguageComboBox.ItemsSource = LocalizationService.AvailableLanguages;
        LanguageComboBox.SelectedValue = _settings.Language;

        // Setup output device ComboBox
        LoadOutputDevices();
        
        // Subscribe to volume slider changes for real-time preview
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        
        // Apply localized text
        ApplyLocalization();
    }
    
    private void ApplyLocalization()
    {
        // Title bar
        Title = _loc["Settings"];
        
        // Section headers and labels (these need to be set in code since they use x:Name)
        VolumeHeader.Text = _loc["Volume"];
        AutostartHeader.Text = _loc["Autostart"];
        BehaviorHeader.Text = _loc["Behavior"];
        LanguageHeader.Text = _loc["Language"];
        
        // Accessibility names for inputs
        AutomationProperties.SetName(VolumeSlider, _loc["Volume"]);
        AutomationProperties.SetName(LanguageComboBox, _loc["Language"]);

        // Checkboxes
        AutoStartCheckBox.Content = _loc["StartWithWindows"];
        StartMinimizedCheckBox.Content = _loc["StartMinimized"];
        MinimizeToTrayCheckBox.Content = _loc["MinimizeToTray"];
        AutoConnectCheckBox.Content = _loc["AutoConnect"];
        ShowNotificationsCheckBox.Content = _loc["ShowNotifications"];
        
        // Buttons
        SaveButton.Content = _loc["Save"];
        CancelButton.Content = _loc["Cancel"];

        // Stability section
        StabilityHeader.Text = _loc["Stability"];
        AutoReconnectCheckBox.Content = _loc["AutoReconnect"];
        AutoReconnectHint.Text = _loc["AutoReconnectHint"];
        PinOutputCheckBox.Content = _loc["PinOutput"];
        PinOutputHint.Text = _loc["PinOutputHint"];
        OutputDeviceLabel.Text = _loc["OutputDevice"];
        DiagnosticsButton.Content = _loc["DiagnosticsButton"];
        AutomationProperties.SetName(OutputDeviceComboBox, _loc["OutputDevice"]);
    }

    private void LoadOutputDevices()
    {
        var choices = new List<OutputDeviceChoice>
        {
            new() { Id = string.Empty, Label = _loc["OutputDeviceAuto"] }
        };

        try
        {
            var manager = new AudioEndpointManager();
            foreach (var endpoint in manager.GetRenderEndpoints()
                         .Where(e => e.IsSafeOutput)
                         .OrderByDescending(e => e.IsDefault))
            {
                choices.Add(new OutputDeviceChoice { Id = endpoint.Id, Label = endpoint.FriendlyName });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not enumerate output devices", ex);
        }

        OutputDeviceComboBox.ItemsSource = choices;

        // Drop stale preferences (endpoint removed since last run) back to automatic.
        if (!choices.Any(c => string.Equals(c.Id, _settings.PreferredOutputDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.PreferredOutputDeviceId = string.Empty;
        }

        OutputDeviceComboBox.SelectedValue = _settings.PreferredOutputDeviceId;
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var path = DiagnosticsService.WriteReport(null, null, _settings);
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, _loc["DiagnosticsFailed"], _loc["Settings"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DiagnosticsService.RevealInExplorer(path);
        MessageBox.Show(this, string.Format(_loc["DiagnosticsSaved"], path), _loc["Settings"],
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
    
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Apply volume in real-time as user drags slider
        _volumeService.SetVolume((int)e.NewValue);
    }
    
    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedValue is string langCode)
        {
            _settings.Language = langCode;
            _loc.CurrentLanguage = langCode;
            ApplyLocalization();
        }
    }
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
    
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Update autostart registry if changed
        if (_settings.AutoStart != _originalSettings.AutoStart || 
            _settings.StartMinimized != _originalSettings.StartMinimized)
        {
            AutoStartService.SetAutoStart(_settings.AutoStart, _settings.StartMinimized);
        }
        
        // Apply final volume
        _volumeService.SetVolume(_settings.Volume);
        
        // Save settings to file
        _settings.Save();
        
        _volumeService.Dispose();
        DialogResult = true;
        Close();
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Restore original volume
        _volumeService.SetVolume(_originalSettings.Volume);
        
        // Restore original language
        _loc.CurrentLanguage = _originalSettings.Language;
        
        // Restore original values
        _settings.Volume = _originalSettings.Volume;
        _settings.AutoStart = _originalSettings.AutoStart;
        _settings.StartMinimized = _originalSettings.StartMinimized;
        _settings.MinimizeToTray = _originalSettings.MinimizeToTray;
        _settings.AutoConnect = _originalSettings.AutoConnect;
        _settings.ShowNotifications = _originalSettings.ShowNotifications;
        _settings.Language = _originalSettings.Language;
        _settings.AutoReconnect = _originalSettings.AutoReconnect;
        _settings.PinOutputOnConnect = _originalSettings.PinOutputOnConnect;
        _settings.RestoreOutputOnDisconnect = _originalSettings.RestoreOutputOnDisconnect;
        _settings.PreferredOutputDeviceId = _originalSettings.PreferredOutputDeviceId;
        
        _volumeService.Dispose();
        DialogResult = false;
        Close();
    }
}
