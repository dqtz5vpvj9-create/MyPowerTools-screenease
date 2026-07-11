using System.Globalization;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    public bool HasModes => Modes.Count > 0;
    public bool HasOperationMessage => OperationMessage.Length > 0;
    public bool HasReminderMessage => ReminderMessage.Length > 0;
    public bool IsConnected => Displays.Count > 0;
    public bool HardwareControlAvailable => NativeWriter.Available;
    public string ConnectionText => IsConnected
        ? HardwareControlAvailable
            ? "已连接"
            : "已连接 · 当前会话只读"
        : "未检测到显示器";
    public string CurrentSummary => $"{ActualColorTemperatureText} / {ActualBrightnessText}";
    public string ActualColorTemperatureText => $"{_actualColorTemperatureKelvin} K";
    public string ActualBrightnessText => $"{_actualBrightnessPercent}%";
    public string ColorTemperatureText => $"{(int)Math.Round(ColorTemperature)} K";
    public string BrightnessText => $"{(int)Math.Round(Brightness)}%";
    public string EyeCareStatusText => HardwareControlAvailable
        ? EyeCareEnabled ? "护眼已开启" : "护眼已关闭"
        : EyeCareEnabled ? "护眼逻辑已开启 · 硬件当前只读" : "护眼已关闭 · 硬件当前只读";
    public string EyeCareActionLabel => EyeCareEnabled ? "关闭护眼" : "开启护眼";
    public string ApplyCurrentLabel => "应用当前调节";
    public string DiagnosticsTitle => DiagnosticsExpanded ? "收起诊断" : "诊断";
    public string DisplayCountText => Snapshot.Displays.Count == 1 ? "1 台显示器" : $"{Snapshot.Displays.Count} 台显示器";
    public string ProfileCountText => Modes.Count == 1 ? "1 个模式" : $"{Modes.Count} 个模式";
    public string NativeWriterStatusLabel => HardwareControlAvailable
        ? "硬件调节可用"
        : IsConnected ? "当前会话无法访问显示器硬件" : "等待显示器连接";
    public string NativeWriterStatusDetail => HardwareControlAvailable
        ? "色温和亮度调节会写入当前显示器。"
        : IsConnected
            ? "你仍可编辑和保存模式。返回本地桌面后，再尝试应用到显示器。"
            : "连接显示器后，ScreenEase 会重新检测可用的调节能力。";
    public string NativeWriterTechnicalDetails => $"状态代码：{NormalizeWriterState(NativeWriter.State)}";
    public string ReminderStatusText => _reminderState.Phase switch
    {
        "work" => "专注中",
        "short-break" => "短休中",
        "long-break" => "长休中",
        "paused" => "已暂停",
        _ => "未开始"
    };
    public string RemainingText => _reminderState.Phase == "stopped"
        ? "-"
        : $"{Math.Max(0, _remainingSeconds) / 60:00}:{Math.Max(0, _remainingSeconds) % 60:00}";
    public string RoundCountText => _reminderState.CompletedWorkSessions.ToString(CultureInfo.InvariantCulture);
    public string SchedulePeriodText => EditingNightValues ? "正在编辑夜间值" : "正在编辑日间值";
    public string ScheduleStatusText => UseSchedule
        ? UseNightValues
            ? $"自动切换：{Sunset}–{Sunrise} 使用夜间值"
            : "自动切换已启用；夜间值当前关闭"
        : "自动切换已关闭";

    public ScreenEaseModeViewModel? SelectedMode
    {
        get => _selectedMode;
        private set => SetProperty(ref _selectedMode, value);
    }

    public string ModeName
    {
        get => _modeName;
        set => SetProperty(ref _modeName, value);
    }

    public double ColorTemperature
    {
        get => _colorTemperature;
        set
        {
            var normalized = Math.Clamp(Math.Round(value / 100d) * 100d, 2500d, 6500d);
            if (SetProperty(ref _colorTemperature, normalized))
            {
                OnPropertyChanged(nameof(ColorTemperatureText));
            }
        }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            var normalized = Math.Clamp(Math.Round(value), 10d, 100d);
            if (SetProperty(ref _brightness, normalized))
            {
                OnPropertyChanged(nameof(BrightnessText));
            }
        }
    }

    public bool EyeCareEnabled
    {
        get => _eyeCareEnabled;
        private set
        {
            if (SetProperty(ref _eyeCareEnabled, value))
            {
                OnPropertyChanged(nameof(EyeCareStatusText));
                OnPropertyChanged(nameof(EyeCareActionLabel));
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
                NotifyCommandStates();
            }
        }
    }

    public string OperationMessage
    {
        get => _operationMessage;
        private set
        {
            if (SetProperty(ref _operationMessage, value))
            {
                OnPropertyChanged(nameof(HasOperationMessage));
            }
        }
    }

    public bool DiagnosticsExpanded
    {
        get => _diagnosticsExpanded;
        private set
        {
            if (SetProperty(ref _diagnosticsExpanded, value))
            {
                OnPropertyChanged(nameof(DiagnosticsTitle));
            }
        }
    }

    public bool ReminderEnabled
    {
        get => _reminderEnabled;
        set => SetProperty(ref _reminderEnabled, value);
    }

    public bool AutoStartNext
    {
        get => _autoStartNext;
        set => SetProperty(ref _autoStartNext, value);
    }

    public bool UseNightValues
    {
        get => _useNightValues;
        set
        {
            if (!SetProperty(ref _useNightValues, value))
            {
                return;
            }

            if (!value)
            {
                EditingNightValues = false;
            }
            OnPropertyChanged(nameof(ScheduleStatusText));
        }
    }

    public bool EditingNightValues
    {
        get => _editingNightValues;
        set
        {
            var normalized = UseNightValues && value;
            if (!SetProperty(ref _editingNightValues, normalized))
            {
                return;
            }

            foreach (var mode in Modes)
            {
                mode.SetShowNightValues(normalized);
            }
            LoadSelectedModeValues();
            OnPropertyChanged(nameof(SchedulePeriodText));
        }
    }

    public bool UseSchedule
    {
        get => _useSchedule;
        set
        {
            if (SetProperty(ref _useSchedule, value))
            {
                OnPropertyChanged(nameof(ScheduleStatusText));
            }
        }
    }

    public string Sunrise
    {
        get => _sunrise;
        set
        {
            if (SetProperty(ref _sunrise, value))
            {
                OnPropertyChanged(nameof(ScheduleStatusText));
            }
        }
    }

    public string Sunset
    {
        get => _sunset;
        set
        {
            if (SetProperty(ref _sunset, value))
            {
                OnPropertyChanged(nameof(ScheduleStatusText));
            }
        }
    }

    public string FocusMinutes
    {
        get => _focusMinutes;
        set => SetProperty(ref _focusMinutes, value);
    }

    public string ShortBreakMinutes
    {
        get => _shortBreakMinutes;
        set => SetProperty(ref _shortBreakMinutes, value);
    }

    public string LongBreakMinutes
    {
        get => _longBreakMinutes;
        set => SetProperty(ref _longBreakMinutes, value);
    }

    public string LongBreakInterval
    {
        get => _longBreakInterval;
        set => SetProperty(ref _longBreakInterval, value);
    }

    public string ReminderMessage
    {
        get => _reminderMessage;
        private set
        {
            if (SetProperty(ref _reminderMessage, value))
            {
                OnPropertyChanged(nameof(HasReminderMessage));
            }
        }
    }

    private void SetActualEffect(int kelvin, int brightness)
    {
        var normalizedKelvin = Math.Clamp(kelvin, 1000, 10000);
        var normalizedBrightness = Math.Clamp(brightness, 1, 150);
        if (_actualColorTemperatureKelvin == normalizedKelvin &&
            _actualBrightnessPercent == normalizedBrightness)
        {
            return;
        }

        _actualColorTemperatureKelvin = normalizedKelvin;
        _actualBrightnessPercent = normalizedBrightness;
        OnPropertyChanged(nameof(ActualColorTemperatureText));
        OnPropertyChanged(nameof(ActualBrightnessText));
        OnPropertyChanged(nameof(CurrentSummary));
    }
}
