using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Juggler.Core.Configuration;
using Juggler.Ui.Services;

namespace Juggler.Ui.Views;

public sealed partial class SettingsView : UserControl
{
    private RadioButton _residentRadio = null!;
    private RadioButton _scheduledRadio = null!;
    private ToggleSwitch _dryRunSwitch = null!;
    private TextBox _debounceBox = null!;
    private TextBox _sweepBox = null!;
    private ToggleSwitch _autostartSwitch = null!;
    private ToggleSwitch _lockSwitch = null!;
    private ToggleSwitch _batterySwitch = null!;
    private ToggleSwitch _prioritySwitch = null!;
    private ToggleSwitch _notifySwitch = null!;
    private CheckBox _notifyErrorCheck = null!;
    private CheckBox _notifyMissingCheck = null!;
    private CheckBox _notifyLowMemCheck = null!;
    private StackPanel _notifyDetails = null!;
    private TextBox _logSizeBox = null!;
    private TextBox _logRotationsBox = null!;
    private ComboBox _concurrencyBox = null!;

    /// <summary>Raised when any setting changes, so the host can persist it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Suppresses handling while controls are populated from config, so applying values does not
    /// immediately treat them as user edits and write the whole file back.
    /// </summary>
    private bool _binding;

    private bool _ready;
    private GeneralSettings _current = new();

    public SettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (_ready)
        {
            return;
        }

        _residentRadio = this.Require<RadioButton>("ResidentRadio");
        _scheduledRadio = this.Require<RadioButton>("ScheduledRadio");
        _dryRunSwitch = this.Require<ToggleSwitch>("DryRunSwitch");
        _debounceBox = this.Require<TextBox>("DebounceBox");
        _sweepBox = this.Require<TextBox>("SweepBox");
        _autostartSwitch = this.Require<ToggleSwitch>("AutostartSwitch");
        _lockSwitch = this.Require<ToggleSwitch>("LockSwitch");
        _batterySwitch = this.Require<ToggleSwitch>("BatterySwitch");
        _prioritySwitch = this.Require<ToggleSwitch>("PrioritySwitch");
        _notifySwitch = this.Require<ToggleSwitch>("NotifySwitch");
        _notifyErrorCheck = this.Require<CheckBox>("NotifyErrorCheck");
        _notifyMissingCheck = this.Require<CheckBox>("NotifyMissingCheck");
        _notifyLowMemCheck = this.Require<CheckBox>("NotifyLowMemCheck");
        _notifyDetails = this.Require<StackPanel>("NotifyDetails");
        _logSizeBox = this.Require<TextBox>("LogSizeBox");
        _logRotationsBox = this.Require<TextBox>("LogRotationsBox");
        _concurrencyBox = this.Require<ComboBox>("ConcurrencyBox");

        _ready = true;
    }

    public void Load(AppConfig config)
    {
        if (!_ready)
        {
            _current = config.General;
            return;
        }

        _binding = true;

        try
        {
            _current = config.General;

            _concurrencyBox.ItemsSource = new object[] { "1", "2", "3", "4" };
            _concurrencyBox.SelectedItem = _current.MaxConcurrent.ToString();

            _residentRadio.IsChecked = _current.Mode == RunMode.Resident;
            _scheduledRadio.IsChecked = _current.Mode == RunMode.Scheduled;

            _dryRunSwitch.IsChecked = _current.DryRun;
            _debounceBox.Text = _current.DebounceSeconds.ToString();
            _sweepBox.Text = _current.SweepIntervalMinutes.ToString();

            _autostartSwitch.IsChecked = _current.RegisterAutostart;
            _lockSwitch.IsChecked = _current.SuspendOnLock;
            _batterySwitch.IsChecked = _current.ThrottleOnBattery;
            _prioritySwitch.IsChecked = _current.Priority == PriorityMode.BelowNormal;

            _notifySwitch.IsChecked = _current.Notify;
            _notifyErrorCheck.IsChecked = _current.NotifyOnError;
            _notifyMissingCheck.IsChecked = _current.NotifyOnWatchedFolderMissing;
            _notifyLowMemCheck.IsChecked = _current.NotifyOnLowMemoryMode;
            _notifyDetails.IsVisible = _current.Notify;

            _logSizeBox.Text = _current.LogMaxMegabytes.ToString();
            _logRotationsBox.Text = _current.LogRotations.ToString();
        }
        finally
        {
            _binding = false;
        }
    }

    // ------------------------------------------------------------------ handlers
    private void OnIsCheckedChanged(object? sender, RoutedEventArgs e) => Publish();

    private void OnConcurrencyChanged(object? sender, SelectionChangedEventArgs e) => Publish();

    private void OnTextChanged(object? sender, TextChangedEventArgs e) => Publish();

    private void OnNotifyMasterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _binding)
        {
            return;
        }

        _notifyDetails.IsVisible = _notifySwitch.IsChecked == true;
        Publish();
    }

    /// <summary>
    /// Reads a numeric field, clamping rather than rejecting. A numeric box being typed into is a
    /// transient state, and refusing the value would fight the user mid-keystroke.
    /// </summary>
    private static int ReadInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text?.Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    private void Publish()
    {
        if (!_ready || _binding)
        {
            return;
        }

        _current = _current with
        {
            Mode = _scheduledRadio.IsChecked == true ? RunMode.Scheduled : RunMode.Resident,
            DryRun = _dryRunSwitch.IsChecked == true,
            DebounceSeconds = ReadInt(_debounceBox.Text, _current.DebounceSeconds, 0, 3600),
            SweepIntervalMinutes = ReadInt(_sweepBox.Text, _current.SweepIntervalMinutes, 1, 1440),
            RegisterAutostart = _autostartSwitch.IsChecked == true,
            SuspendOnLock = _lockSwitch.IsChecked == true,
            SuspendOnScreenSaver = _lockSwitch.IsChecked == true,
            ThrottleOnBattery = _batterySwitch.IsChecked == true,
            ThrottleWhenMonitorOff = _batterySwitch.IsChecked == true,
            Priority = _prioritySwitch.IsChecked == true ? PriorityMode.BelowNormal : PriorityMode.Idle,
            Notify = _notifySwitch.IsChecked == true,
            NotifyOnError = _notifyErrorCheck.IsChecked == true,
            NotifyOnWatchedFolderMissing = _notifyMissingCheck.IsChecked == true,
            NotifyOnLowMemoryMode = _notifyLowMemCheck.IsChecked == true,
            LogMaxMegabytes = ReadInt(_logSizeBox.Text, _current.LogMaxMegabytes, 1, 256),
            LogRotations = ReadInt(_logRotationsBox.Text, _current.LogRotations, 1, 20),
            MaxConcurrent = _concurrencyBox.SelectedItem is string s && int.TryParse(s, out int c) ? c : 1,
        };

        Changed?.Invoke();
    }
}
