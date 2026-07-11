using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class ScreenEaseViewModel : ToolProductPageViewModel, IDisposable
{
    private static readonly string[] BuiltInOrder =
    [
        "day-office",
        "day",
        "long-read",
        "reading",
        "detail-work",
        "clarity",
        "warm-video",
        "media",
        "bright-focus",
        "focus",
        "low-blue-evening",
        "night",
        "personal",
        "manual-adjustment",
        "custom"
    ];

    private readonly Func<ScreenEaseProfile, Task>? _saveProfile;
    private readonly Func<string, string, bool, Task>? _apply;
    private readonly Func<int, int, bool, Task>? _applyManual;
    private readonly Func<Task<ScreenEaseDisableResult>>? _disableEffect;
    private readonly Func<ScreenEaseReminder, Task> _saveReminder;
    private readonly Func<ScreenEaseSchedule, Task<ScreenEaseDisplayEffect>> _saveSchedule;
    private readonly Func<Task<ScreenEaseReminderState>> _loadReminderState;
    private readonly Func<Task<ScreenEaseReminderState>> _startReminder;
    private readonly Func<Task<ScreenEaseReminderState>> _pauseReminder;
    private readonly Func<Task<ScreenEaseReminderState>> _resumeReminder;
    private readonly Func<Task<ScreenEaseReminderState>> _resetReminder;
    private readonly DispatcherTimer _timer;
    private readonly EventHandler _timerTickHandler;
    private int _disposed;
    private ScreenEaseModeViewModel? _selectedMode;
    private string _modeId = "";
    private string _modeName = "";
    private double _colorTemperature;
    private double _brightness;
    private int _actualColorTemperatureKelvin;
    private int _actualBrightnessPercent;
    private bool _eyeCareEnabled;
    private bool _isBusy;
    private string _operationMessage = "";
    private bool _diagnosticsExpanded;
    private bool _reminderEnabled;
    private bool _autoStartNext;
    private string _focusMinutes = "";
    private string _shortBreakMinutes = "";
    private string _longBreakMinutes = "";
    private string _longBreakInterval = "";
    private string _reminderMessage = "";
    private bool _useNightValues;
    private bool _editingNightValues;
    private bool _useSchedule;
    private string _sunrise = "";
    private string _sunset = "";
    private ScreenEaseReminderState _reminderState;
    private int _remainingSeconds;
    private bool _timerRefreshActive;

    public ScreenEaseViewModel(
        ScreenEaseSnapshot snapshot,
        string initialRouteId = "profiles",
        Func<Task>? browseAllTools = null,
        Func<Task>? refresh = null,
        Func<ScreenEaseProfile, Task>? saveProfile = null,
        Func<string, string, bool, Task>? apply = null,
        Func<int, int, bool, Task>? applyManual = null,
        Func<Task<ScreenEaseDisableResult>>? disableEffect = null,
        Func<ScreenEaseReminder, Task>? saveReminder = null,
        Func<ScreenEaseSchedule, Task<ScreenEaseDisplayEffect>>? saveSchedule = null,
        Func<Task<ScreenEaseReminderState>>? loadReminderState = null,
        Func<Task<ScreenEaseReminderState>>? startReminder = null,
        Func<Task<ScreenEaseReminderState>>? pauseReminder = null,
        Func<Task<ScreenEaseReminderState>>? resumeReminder = null,
        Func<Task<ScreenEaseReminderState>>? resetReminder = null,
        Func<ScreenEaseAdvanced, Task<ScreenEaseAdvancedSaveResult>>? saveAdvanced = null,
        Func<ScreenEaseOverlayConfiguration, Task<ScreenEaseOverlayResult>>? saveOverlay = null,
        Func<string, Task<ScreenEaseSnapshot>>? importLegacy = null,
        Func<Task>? openHotkeySettings = null)
        : base("ScreenEase", "护眼模式与休息提醒", ToolProductState.Ready)
    {
        Snapshot = snapshot;
        _saveProfile = saveProfile;
        _apply = apply;
        _applyManual = applyManual;
        _disableEffect = disableEffect;
        var toolService = new ScreenEaseToolService();
        _saveReminder = saveReminder ?? (value => toolService.ConfigureReminderAsync(value));
        _saveSchedule = saveSchedule ?? (value => toolService.ConfigureScheduleAsync(value));
        _loadReminderState = loadReminderState ?? (() => toolService.LoadReminderStateAsync());
        _startReminder = startReminder ?? (() => toolService.StartReminderAsync());
        _pauseReminder = pauseReminder ?? (() => toolService.PauseReminderAsync());
        _resumeReminder = resumeReminder ?? (() => toolService.ResumeReminderAsync());
        _resetReminder = resetReminder ?? (() => toolService.ResetReminderAsync());
        _saveAdvanced = saveAdvanced ?? (value => toolService.SaveAdvancedAsync(_settingsRevision, value));
        _saveOverlay = saveOverlay ?? (value => toolService.ConfigureOverlayAsync(value));
        _importLegacy = importLegacy ?? (path => toolService.ImportLegacyAsync(path));
        _openHotkeySettings = openHotkeySettings ?? (() => Task.CompletedTask);
        _eyeCareEnabled = snapshot.Effect?.Enabled ?? false;
        _settingsRevision = snapshot.SettingsRevision;

        var order = BuiltInOrder
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        Modes = new ObservableCollection<ScreenEaseModeViewModel>(
            snapshot.Profiles
                .OrderBy(profile => order.GetValueOrDefault(profile.Id, int.MaxValue))
                .ThenBy(profile => profile.Name, StringComparer.CurrentCulture)
                .Select(profile => new ScreenEaseModeViewModel(profile, SelectModeAsync)));

        var reminder = snapshot.Reminder;
        _reminderEnabled = reminder.Enabled;
        _autoStartNext = reminder.AutoStartNext;
        _focusMinutes = reminder.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        _shortBreakMinutes = reminder.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
        _longBreakMinutes = reminder.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
        _longBreakInterval = reminder.LongBreakInterval.ToString(CultureInfo.InvariantCulture);
        var schedule = snapshot.Schedule ?? ScreenEaseSchedule.Default;
        _useNightValues = schedule.UseNightValues;
        _editingNightValues = false;
        _useSchedule = schedule.UseSchedule;
        _sunrise = schedule.Sunrise;
        _sunset = schedule.Sunset;
        _reminderState = snapshot.ReminderState ?? ScreenEaseReminderState.Stopped;
        _remainingSeconds = _reminderState.RemainingSeconds;
        LoadExtendedSnapshot(snapshot);

        BrowseAllToolsCommand = new AsyncRelayCommand(() => browseAllTools?.Invoke() ?? Task.CompletedTask);
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        ToggleEyeCareCommand = new AsyncRelayCommand(ToggleEyeCareAsync, () => !IsBusy);
        SaveModeCommand = new AsyncRelayCommand(SaveModeAsync, () => !IsBusy);
        NewModeCommand = new AsyncRelayCommand(NewModeAsync, () => !IsBusy);
        ApplyCurrentCommand = new AsyncRelayCommand(ApplyCurrentAsync, () => !IsBusy);
        SaveReminderCommand = new AsyncRelayCommand(SaveReminderAsync, () => !IsBusy);
        SaveScheduleCommand = new AsyncRelayCommand(SaveScheduleAsync, () => !IsBusy);
        StartReminderCommand = new AsyncRelayCommand(StartReminderAsync, CanStartReminder);
        PauseReminderCommand = new AsyncRelayCommand(PauseReminderAsync, CanPauseReminder);
        ResumeReminderCommand = new AsyncRelayCommand(ResumeReminderAsync, CanResumeReminder);
        ResetReminderCommand = new AsyncRelayCommand(ResetReminderAsync, CanResetReminder);
        ToggleDiagnosticsCommand = new AsyncRelayCommand(() =>
        {
            DiagnosticsExpanded = !DiagnosticsExpanded;
            return Task.CompletedTask;
        });
        SaveAdvancedCommand = new AsyncRelayCommand(SaveAdvancedAsync, () => !IsBusy);
        SaveOverlayCommand = new AsyncRelayCommand(SaveOverlayAsync, () => !IsBusy);
        ImportLegacyCommand = new AsyncRelayCommand(ImportLegacyAsync, () => !IsBusy);
        OpenHotkeySettingsCommand = new AsyncRelayCommand(_openHotkeySettings, () => !IsBusy);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timerTickHandler = OnTimerTick;
        _timer.Tick += _timerTickHandler;
        _timer.Start();

        var selected = Modes.FirstOrDefault(mode =>
            string.Equals(mode.Id, snapshot.Effect?.ProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Modes.FirstOrDefault(mode =>
                string.Equals(mode.Id, snapshot.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Modes.FirstOrDefault();
        if (selected is not null)
        {
            SelectMode(selected);
        }

        if (snapshot.Effect is { } effect)
        {
            SetActualEffect(effect.ColorTemperatureKelvin, effect.BrightnessPercent);
        }
        else if (selected is not null)
        {
            var (kelvin, brightness) = ResolveProfileEffect(selected.Profile);
            SetActualEffect(kelvin, brightness);
        }

        NotifyTimerState();
        _ = initialRouteId;
    }

    public ScreenEaseSnapshot Snapshot { get; private set; }
    public ObservableCollection<ScreenEaseModeViewModel> Modes { get; }
    public ScreenEaseNativeWriter NativeWriter => Snapshot.NativeWriter;
    public IReadOnlyList<ScreenEaseDisplay> Displays => Snapshot.Displays.Where(display => display.IsUsable).ToArray();
    public IReadOnlyList<ScreenEaseDetectedDisplayViewModel> DetectedDisplays =>
        Snapshot.Displays.Select(ScreenEaseDetectedDisplayViewModel.FromDisplay).ToArray();

    public ICommand BrowseAllToolsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleEyeCareCommand { get; }
    public ICommand SaveModeCommand { get; }
    public ICommand NewModeCommand { get; }
    public ICommand ApplyCurrentCommand { get; }
    public ICommand SaveReminderCommand { get; }
    public ICommand SaveScheduleCommand { get; }
    public ICommand StartReminderCommand { get; }
    public ICommand PauseReminderCommand { get; }
    public ICommand ResumeReminderCommand { get; }
    public ICommand ResetReminderCommand { get; }
    public ICommand ToggleDiagnosticsCommand { get; }
    public ICommand SaveAdvancedCommand { get; }
    public ICommand SaveOverlayCommand { get; }
    public ICommand ImportLegacyCommand { get; }
    public ICommand OpenHotkeySettingsCommand { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Stop();
            _timer.Tick -= _timerTickHandler;
        }
    }

    private void OnTimerTick(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ShellCommandFaultBoundary.Run(
                this,
                "Refresh ScreenEase reminder timer",
                TickReminderAsync);
        }
    }
}
