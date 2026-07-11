using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ScreenEase.Desktop;

public sealed class MainViewModel : ObservableObject
{
    private EyeCareSettings? _settings;
    private RestTimerState? _restTimer;
    private string _serviceUrl = ReadInitialEndpoint();
    private string _statusText = "Offline";
    private string _lastUpdatedText = "-";
    private string _errorText = string.Empty;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isLoadingState;
    private bool _isFilterEnabled;
    private bool _useNightValues;
    private bool _useSchedule;
    private int _colorTemperatureKelvin = 5000;
    private int _brightnessPercent = 90;
    private string _profileNameDraft = "我的方案";
    private bool _restTimerEnabled;
    private bool _restTimerAutoStart;
    private int _restTimerWorkMinutes = 25;
    private int _restTimerShortBreakMinutes = 5;
    private int _restTimerLongBreakMinutes = 15;
    private int _restTimerLongBreakEveryWorkSessions = 4;
    private string _restTimerPhaseText = "Stopped";
    private string _restTimerRemainingText = "-";
    private int _completedWorkSessions;
    private EyeProfile? _selectedProfile;

    public MainViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => IsConnected && !IsBusy);
        ToggleFilterCommand = new AsyncRelayCommand(ToggleFilterAsync, () => IsConnected && !IsBusy);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, () => IsConnected && SelectedProfile is not null && !IsBusy);
        ApplyManualCommand = new AsyncRelayCommand(ApplyManualAsync, () => IsConnected && !IsBusy);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => IsConnected && SelectedProfile is not null && !IsBusy);
        AddProfileCommand = new AsyncRelayCommand(AddProfileAsync, () => IsConnected && !IsBusy);
        DisableCommand = new AsyncRelayCommand(DisableAsync, () => IsConnected && !IsBusy);
        SaveRestTimerSettingsCommand = new AsyncRelayCommand(SaveRestTimerSettingsAsync, () => IsConnected && !IsBusy);
        StartTimerCommand = new AsyncRelayCommand(StartTimerAsync, () => IsConnected && !IsBusy);
        PauseTimerCommand = new AsyncRelayCommand(PauseTimerAsync, () => IsConnected && !IsBusy);
        ResumeTimerCommand = new AsyncRelayCommand(ResumeTimerAsync, () => IsConnected && !IsBusy);
        ResetTimerCommand = new AsyncRelayCommand(ResetTimerAsync, () => IsConnected && !IsBusy);
    }

    public ObservableCollection<EyeProfile> Profiles { get; } = [];
    public ObservableCollection<MonitorRow> Monitors { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ToggleFilterCommand { get; }
    public ICommand ApplyProfileCommand { get; }
    public ICommand ApplyManualCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand SaveRestTimerSettingsCommand { get; }
    public ICommand StartTimerCommand { get; }
    public ICommand PauseTimerCommand { get; }
    public ICommand ResumeTimerCommand { get; }
    public ICommand ResetTimerCommand { get; }

    public string ServiceUrl
    {
        get => _serviceUrl;
        set => SetProperty(ref _serviceUrl, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set
        {
            if (SetProperty(ref _lastUpdatedText, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
                RaiseAllCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseAllCanExecuteChanged();
            }
        }
    }

    public bool IsFilterEnabled
    {
        get => _isFilterEnabled;
        set
        {
            if (SetProperty(ref _isFilterEnabled, value))
            {
                OnPropertyChanged(nameof(FilterButtonText));
                OnPropertyChanged(nameof(FilterStateText));
            }
        }
    }

    public bool UseNightValues
    {
        get => _useNightValues;
        set
        {
            if (SetProperty(ref _useNightValues, value))
            {
                ApplySelectedProfileValuesToInputs();
            }
        }
    }

    public bool UseSchedule
    {
        get => _useSchedule;
        set => SetProperty(ref _useSchedule, value);
    }

    public int ColorTemperatureKelvin
    {
        get => _colorTemperatureKelvin;
        set
        {
            if (SetProperty(ref _colorTemperatureKelvin, value))
            {
                OnPropertyChanged(nameof(EffectSummary));
            }
        }
    }

    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            if (SetProperty(ref _brightnessPercent, value))
            {
                OnPropertyChanged(nameof(EffectSummary));
            }
        }
    }

    public string ProfileNameDraft
    {
        get => _profileNameDraft;
        set => SetProperty(ref _profileNameDraft, value);
    }

    public bool RestTimerEnabled
    {
        get => _restTimerEnabled;
        set => SetProperty(ref _restTimerEnabled, value);
    }

    public bool RestTimerAutoStart
    {
        get => _restTimerAutoStart;
        set => SetProperty(ref _restTimerAutoStart, value);
    }

    public int RestTimerWorkMinutes
    {
        get => _restTimerWorkMinutes;
        set => SetProperty(ref _restTimerWorkMinutes, value);
    }

    public int RestTimerShortBreakMinutes
    {
        get => _restTimerShortBreakMinutes;
        set => SetProperty(ref _restTimerShortBreakMinutes, value);
    }

    public int RestTimerLongBreakMinutes
    {
        get => _restTimerLongBreakMinutes;
        set => SetProperty(ref _restTimerLongBreakMinutes, value);
    }

    public int RestTimerLongBreakEveryWorkSessions
    {
        get => _restTimerLongBreakEveryWorkSessions;
        set => SetProperty(ref _restTimerLongBreakEveryWorkSessions, value);
    }

    public string RestTimerPhaseText
    {
        get => _restTimerPhaseText;
        private set => SetProperty(ref _restTimerPhaseText, value);
    }

    public string RestTimerRemainingText
    {
        get => _restTimerRemainingText;
        private set => SetProperty(ref _restTimerRemainingText, value);
    }

    public int CompletedWorkSessions
    {
        get => _completedWorkSessions;
        private set => SetProperty(ref _completedWorkSessions, value);
    }

    public EyeProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                ApplySelectedProfileValuesToInputs();
                ProfileNameDraft = value?.Name ?? string.Empty;
                OnPropertyChanged(nameof(CurrentProfileName));
                RaiseAllCanExecuteChanged();
                if (!_isLoadingState && IsConnected && value is not null)
                {
                    _ = ApplyProfileAsync();
                }
            }
        }
    }

    public string ConnectionSummary =>
        IsConnected
            ? $"已连接 · {LastUpdatedText}"
            : StatusText switch
            {
                "Starting" => "正在启动",
                "Error" => "连接失败",
                _ => "未连接"
            };

    public string FilterButtonText =>
        IsFilterEnabled ? "关闭护眼" : "开启护眼";

    public string FilterStateText =>
        IsFilterEnabled ? "护眼已开启" : "护眼已关闭";

    public string EffectSummary =>
        $"{ColorTemperatureKelvin} K / {BrightnessPercent}%";

    public string CurrentProfileName =>
        SelectedProfile?.Name ?? "夜间低蓝";

    public bool HasError =>
        !string.IsNullOrWhiteSpace(ErrorText);

    public async Task RefreshAsync() =>
        await RunAsync(async cancellationToken =>
        {
            await LoadFreshStateWithBootstrapAsync(cancellationToken);
        }, markDisconnectedOnError: true, timeout: TimeSpan.FromSeconds(24));

    public void TickRestTimer()
    {
        if (_restTimer is null)
        {
            return;
        }

        UpdateRestTimerText(_restTimer);
    }

    private async Task SaveSettingsAsync() =>
        await RunAsync(async cancellationToken =>
        {
            var settings = _settings ?? new EyeCareSettings();
            ApplyGeneralSettings(settings, SelectedProfile?.Id);

            using var client = CreateClient();
            _settings = await client.UpdateSettingsAsync(settings, cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task SaveProfileAsync() =>
        await SaveProfileAsync(createNew: false);

    private async Task AddProfileAsync() =>
        await SaveProfileAsync(createNew: true);

    private async Task SaveProfileAsync(bool createNew) =>
        await RunAsync(async cancellationToken =>
        {
            var settings = _settings ?? new EyeCareSettings();
            var profiles = Profiles.Select(CloneProfile).ToList();
            var name = NormalizeProfileName(ProfileNameDraft);
            EyeProfile savedProfile;

            if (createNew || SelectedProfile is null)
            {
                savedProfile = new EyeProfile
                {
                    Id = CreateCustomProfileId(profiles.Select(profile => profile.Id)),
                    Name = name,
                    ColorTemperatureKelvin = ColorTemperatureKelvin,
                    BrightnessPercent = BrightnessPercent,
                    NightColorTemperatureKelvin = ColorTemperatureKelvin,
                    NightBrightnessPercent = BrightnessPercent
                };
                profiles.Add(savedProfile);
            }
            else
            {
                var selectedId = SelectedProfile.Id;
                var index = profiles.FindIndex(profile => profile.Id == selectedId);
                savedProfile = new EyeProfile
                {
                    Id = selectedId,
                    Name = name,
                    ColorTemperatureKelvin = ColorTemperatureKelvin,
                    BrightnessPercent = BrightnessPercent,
                    NightColorTemperatureKelvin = ColorTemperatureKelvin,
                    NightBrightnessPercent = BrightnessPercent
                };

                if (index >= 0)
                {
                    profiles[index] = savedProfile;
                }
                else
                {
                    profiles.Add(savedProfile);
                }
            }

            settings.Profiles = profiles;
            ApplyGeneralSettings(settings, savedProfile.Id);

            using var client = CreateClient();
            _settings = await client.UpdateSettingsAsync(settings, cancellationToken);
            await client.ApplyAsync(
                new ApplyEffectCommand
                {
                    ProfileId = savedProfile.Id,
                    Enabled = true
                },
                cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task SaveRestTimerSettingsAsync() =>
        await RunAsync(async cancellationToken =>
        {
            var settings = _settings ?? new EyeCareSettings();
            ApplyGeneralSettings(settings, SelectedProfile?.Id);

            using var client = CreateClient();
            _settings = await client.UpdateSettingsAsync(settings, cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task ApplyProfileAsync() =>
        await RunAsync(async cancellationToken =>
        {
            using var client = CreateClient();
            await client.ApplyAsync(
                new ApplyEffectCommand
                {
                    ProfileId = SelectedProfile?.Id,
                    Enabled = true
                },
                cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task ToggleFilterAsync()
    {
        if (IsFilterEnabled)
        {
            await DisableAsync();
            return;
        }

        await ApplyManualAsync();
    }

    private async Task ApplyManualAsync() =>
        await RunAsync(async cancellationToken =>
        {
            using var client = CreateClient();
            await client.ApplyAsync(
                new ApplyEffectCommand
                {
                    ColorTemperatureKelvin = ColorTemperatureKelvin,
                    BrightnessPercent = BrightnessPercent,
                    Enabled = true
                },
                cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task DisableAsync() =>
        await RunAsync(async cancellationToken =>
        {
            using var client = CreateClient();
            await client.DisableAsync(cancellationToken);
            await LoadFreshStateAsync(client, cancellationToken);
        }, markDisconnectedOnError: false);

    private async Task StartTimerAsync() => await UpdateTimerAsync(client => client.StartRestTimerAsync);

    private async Task PauseTimerAsync() => await UpdateTimerAsync(client => client.PauseRestTimerAsync);

    private async Task ResumeTimerAsync() => await UpdateTimerAsync(client => client.ResumeRestTimerAsync);

    private async Task ResetTimerAsync() => await UpdateTimerAsync(client => client.ResetRestTimerAsync);

    private async Task UpdateTimerAsync(
        Func<ScreenEaseClient, Func<CancellationToken, Task<RestTimerState>>> actionFactory) =>
        await RunAsync(async cancellationToken =>
        {
            using var client = CreateClient();
            var action = actionFactory(client);
            LoadRestTimer(await action(cancellationToken));
            ErrorText = string.Empty;
            LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");
        }, markDisconnectedOnError: false);

    private ScreenEaseClient CreateClient() => new(ServiceUrl);

    private static string ReadInitialEndpoint()
    {
        var endpoint = Environment.GetEnvironmentVariable("ScreenEase__Endpoint");
        return string.IsNullOrWhiteSpace(endpoint) ? "pipe:screenease.core" : endpoint.Trim();
    }

    private async Task LoadFreshStateWithBootstrapAsync(CancellationToken cancellationToken)
    {
        if (CoreServiceLauncher.CanLaunch(ServiceUrl))
        {
            await CoreServiceLauncher.EnsureRunningAsync(
                ServiceUrl,
                () =>
                {
                    IsConnected = false;
                    StatusText = "Starting";
                    ErrorText = string.Empty;
                },
                cancellationToken);
        }

        using var client = CreateClient();
        await LoadFreshStateAsync(client, cancellationToken);
    }

    private async Task LoadFreshStateAsync(ScreenEaseClient client, CancellationToken cancellationToken)
    {
        var state = await client.GetStateAsync(cancellationToken);
        LoadState(state);
        IsConnected = true;
        StatusText = "Online";
        ErrorText = string.Empty;
        LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        bool markDisconnectedOnError,
        TimeSpan? timeout = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await action(cancellation.Token);
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Error";
            if (markDisconnectedOnError)
            {
                IsConnected = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadState(EyeCareState state)
    {
        _settings = state.Settings;
        IsFilterEnabled = state.Settings.Enabled;
        UseNightValues = state.Settings.UseNightValues;
        UseSchedule = state.Settings.UseSchedule;
        LoadRestTimerSettings(state.Settings.RestTimer);

        _isLoadingState = true;
        Profiles.Clear();
        foreach (var profile in state.Settings.Profiles)
        {
            Profiles.Add(new EyeProfile
            {
                Id = profile.Id,
                Name = GetProfileDisplayName(profile),
                ColorTemperatureKelvin = profile.ColorTemperatureKelvin,
                BrightnessPercent = profile.BrightnessPercent,
                NightColorTemperatureKelvin = profile.NightColorTemperatureKelvin,
                NightBrightnessPercent = profile.NightBrightnessPercent
            });
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == state.Effect.ProfileId)
                          ?? Profiles.FirstOrDefault(profile => profile.Id == state.Settings.ActiveProfileId)
                          ?? Profiles.FirstOrDefault();
        _isLoadingState = false;

        ColorTemperatureKelvin = state.Effect.ColorTemperatureKelvin;
        BrightnessPercent = state.Effect.BrightnessPercent;
        LoadRestTimer(state.RestTimer);

        Monitors.Clear();
        foreach (var monitor in state.Monitors)
        {
            Monitors.Add(new MonitorRow
            {
                Name = string.IsNullOrWhiteSpace(monitor.DeviceName) ? monitor.Id : monitor.DeviceName,
                Bounds = $"{monitor.Left}, {monitor.Top}  {monitor.Width} x {monitor.Height}",
                Primary = monitor.IsPrimary ? "是" : string.Empty
            });
        }
    }

    private void LoadRestTimer(RestTimerState restTimer)
    {
        _restTimer = restTimer;
        RestTimerPhaseText = FormatPhase(restTimer.Phase);
        CompletedWorkSessions = restTimer.CompletedWorkSessions;
        UpdateRestTimerText(restTimer);
    }

    private void LoadRestTimerSettings(RestTimerSettings settings)
    {
        RestTimerEnabled = settings.Enabled;
        RestTimerAutoStart = settings.AutoStart;
        RestTimerWorkMinutes = settings.WorkMinutes;
        RestTimerShortBreakMinutes = settings.ShortBreakMinutes;
        RestTimerLongBreakMinutes = settings.LongBreakMinutes;
        RestTimerLongBreakEveryWorkSessions = settings.LongBreakEveryWorkSessions;
    }

    private void UpdateRestTimerText(RestTimerState restTimer)
    {
        if (restTimer.Phase == RestTimerPhase.Paused && restTimer.PausedRemaining is { } paused)
        {
            RestTimerRemainingText = FormatDuration(paused);
            return;
        }

        if (restTimer.EndsAt is null || restTimer.Phase == RestTimerPhase.Stopped)
        {
            RestTimerRemainingText = "-";
            return;
        }

        var remaining = restTimer.EndsAt.Value - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        RestTimerRemainingText = FormatDuration(remaining);
    }

    private void ApplySelectedProfileValuesToInputs()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        ColorTemperatureKelvin = SelectedProfile.ColorTemperatureKelvin;
        BrightnessPercent = SelectedProfile.BrightnessPercent;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void RaiseAllCanExecuteChanged()
    {
        foreach (var command in new[]
                 {
                     RefreshCommand,
                     SaveSettingsCommand,
                     ToggleFilterCommand,
                     ApplyProfileCommand,
                     ApplyManualCommand,
                     SaveProfileCommand,
                     AddProfileCommand,
                     DisableCommand,
                     SaveRestTimerSettingsCommand,
                     StartTimerCommand,
                     PauseTimerCommand,
                     ResumeTimerCommand,
                     ResetTimerCommand
                 })
        {
            if (command is AsyncRelayCommand asyncCommand)
            {
                asyncCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void ApplyGeneralSettings(EyeCareSettings settings, string? activeProfileId)
    {
        settings.Enabled = IsFilterEnabled;
        settings.ActiveProfileId = activeProfileId ?? settings.ActiveProfileId;
        settings.UseNightValues = UseNightValues;
        settings.UseSchedule = UseSchedule;
        settings.RestTimer = new RestTimerSettings
        {
            Enabled = RestTimerEnabled,
            WorkMinutes = RestTimerWorkMinutes,
            ShortBreakMinutes = RestTimerShortBreakMinutes,
            LongBreakMinutes = RestTimerLongBreakMinutes,
            LongBreakEveryWorkSessions = RestTimerLongBreakEveryWorkSessions,
            AutoStart = RestTimerAutoStart
        };
    }

    private static EyeProfile CloneProfile(EyeProfile profile) =>
        new()
        {
            Id = profile.Id,
            Name = profile.Name,
            ColorTemperatureKelvin = profile.ColorTemperatureKelvin,
            BrightnessPercent = profile.BrightnessPercent,
            NightColorTemperatureKelvin = profile.NightColorTemperatureKelvin,
            NightBrightnessPercent = profile.NightBrightnessPercent
        };

    private static string NormalizeProfileName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "我的方案" : value.Trim();

    private static string CreateCustomProfileId(IEnumerable<string> existingIds)
    {
        var used = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var candidate = $"personal-{stamp}";
        var suffix = 2;

        while (used.Contains(candidate))
        {
            candidate = $"personal-{stamp}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string GetProfileDisplayName(EyeProfile profile) =>
        profile.Id switch
        {
            "day-office" => "日间办公",
            "long-read" => "长读柔光",
            "detail-work" => "细节清晰",
            "warm-video" => "影音暖光",
            "bright-focus" => "高亮专注",
            "low-blue-evening" => "夜间低蓝",
            "personal" => "我的方案",
            "manual-adjustment" => "自定义调节",
            _ => profile.Name
        };

    private static string FormatPhase(RestTimerPhase phase) =>
        phase switch
        {
            RestTimerPhase.Work => "专注中",
            RestTimerPhase.ShortBreak => "短休息",
            RestTimerPhase.LongBreak => "长休息",
            RestTimerPhase.Paused => "已暂停",
            _ => "未开始"
        };
}
