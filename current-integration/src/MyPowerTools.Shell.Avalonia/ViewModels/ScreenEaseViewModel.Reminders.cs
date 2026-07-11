using System.Globalization;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    private async Task SaveReminderAsync()
    {
        if (!TryBuildReminder(out var reminder))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _saveReminder(reminder).ConfigureAwait(true);
            LoadReminderState(await _loadReminderState().ConfigureAwait(true));
            ReminderMessage = "提醒设置已保存。";
        }).ConfigureAwait(true);
    }

    private async Task SaveScheduleAsync()
    {
        if (!TryBuildSchedule(out var schedule))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var effect = await _saveSchedule(schedule).ConfigureAwait(true);
            SetActualEffect(effect.ColorTemperatureKelvin, effect.BrightnessPercent);
            EyeCareEnabled = effect.Enabled;
            OperationMessage = "日夜值与自动切换时间已保存。";
        }).ConfigureAwait(true);
    }

    private async Task StartReminderAsync()
    {
        if (!TryBuildReminder(out var reminder))
        {
            return;
        }

        await RunTimerActionAsync(async () =>
        {
            if (!reminder.Enabled)
            {
                ReminderEnabled = true;
                reminder = reminder with { Enabled = true };
                await _saveReminder(reminder).ConfigureAwait(true);
            }
            return await _startReminder().ConfigureAwait(true);
        }, "本轮专注已开始。").ConfigureAwait(true);
    }

    private Task PauseReminderAsync()
    {
        return RunTimerActionAsync(_pauseReminder, "计时已暂停。");
    }

    private Task ResumeReminderAsync()
    {
        return RunTimerActionAsync(_resumeReminder, "计时已继续。");
    }

    private Task ResetReminderAsync()
    {
        return RunTimerActionAsync(_resetReminder, "计时已重置。");
    }

    private async Task RunTimerActionAsync(
        Func<Task<ScreenEaseReminderState>> action,
        string message)
    {
        await RunBusyAsync(async () =>
        {
            LoadReminderState(await action().ConfigureAwait(true));
            ReminderMessage = message;
        }).ConfigureAwait(true);
    }

    private async Task TickReminderAsync()
    {
        if (_timerRefreshActive)
        {
            return;
        }

        if (_reminderState.Phase == "paused")
        {
            _remainingSeconds = Math.Max(0, _reminderState.PausedRemainingSeconds ?? _remainingSeconds);
            OnPropertyChanged(nameof(RemainingText));
            return;
        }

        if (_reminderState.EndsAt is null || _reminderState.Phase == "stopped")
        {
            return;
        }

        _remainingSeconds = Math.Max(
            0,
            checked((int)Math.Ceiling((_reminderState.EndsAt.Value - DateTimeOffset.UtcNow).TotalSeconds)));
        OnPropertyChanged(nameof(RemainingText));
        if (_remainingSeconds > 0)
        {
            return;
        }

        _timerRefreshActive = true;
        try
        {
            LoadReminderState(await _loadReminderState().ConfigureAwait(true));
        }
        catch (Exception)
        {
            ReminderMessage = "计时状态暂时无法刷新。";
        }
        finally
        {
            _timerRefreshActive = false;
        }
    }

    private void LoadReminderState(ScreenEaseReminderState state)
    {
        _reminderState = state;
        _remainingSeconds = state.RemainingSeconds;
        NotifyTimerState();
    }

    private bool TryBuildSchedule(out ScreenEaseSchedule schedule)
    {
        var validSunrise = TimeOnly.TryParseExact(
            Sunrise,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var sunrise);
        var validSunset = TimeOnly.TryParseExact(
            Sunset,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var sunset);
        if (!validSunrise || !validSunset)
        {
            OperationMessage = "日出和日落时间须使用 HH:mm 格式。";
            schedule = ScreenEaseSchedule.Default;
            return false;
        }

        schedule = new ScreenEaseSchedule(
            UseNightValues,
            UseSchedule,
            sunrise.ToString("HH:mm", CultureInfo.InvariantCulture),
            sunset.ToString("HH:mm", CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryBuildReminder(out ScreenEaseReminder reminder, bool setMessage = true)
    {
        var focusValid = TryBoundedInt(FocusMinutes, 1, 240, out var focus);
        var shortBreakValid = TryBoundedInt(ShortBreakMinutes, 1, 120, out var shortBreak);
        var longBreakValid = TryBoundedInt(LongBreakMinutes, 1, 240, out var longBreak);
        var intervalValid = TryBoundedInt(LongBreakInterval, 1, 12, out var interval);
        var valid = focusValid && shortBreakValid && longBreakValid && intervalValid;
        reminder = new ScreenEaseReminder(
            ReminderEnabled,
            AutoStartNext,
            focus,
            shortBreak,
            longBreak,
            interval);
        if (!valid && setMessage)
        {
            ReminderMessage = "请检查提醒范围：专注 1–240 分钟，短休 1–120 分钟，长休 1–240 分钟，长休间隔 1–12 轮。";
        }
        return valid;
    }

    private static bool TryBoundedInt(string value, int minimum, int maximum, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
               parsed >= minimum && parsed <= maximum;
    }

    private bool CanStartReminder()
    {
        return !IsBusy && _reminderState.Phase == "stopped";
    }

    private bool CanPauseReminder()
    {
        return !IsBusy && _reminderState.Phase is "work" or "short-break" or "long-break";
    }

    private bool CanResumeReminder()
    {
        return !IsBusy && _reminderState.Phase == "paused";
    }

    private bool CanResetReminder()
    {
        return !IsBusy && _reminderState.Phase != "stopped";
    }

    private void NotifyCommandStates()
    {
        ((AsyncRelayCommand)ToggleEyeCareCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)SaveModeCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)NewModeCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ApplyCurrentCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)SaveReminderCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)SaveScheduleCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)SaveAdvancedCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)SaveOverlayCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ImportLegacyCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)OpenHotkeySettingsCommand).NotifyCanExecuteChanged();
        NotifyTimerCommands();
    }

    private void NotifyTimerState()
    {
        OnPropertyChanged(nameof(ReminderStatusText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(RoundCountText));
        NotifyTimerCommands();
    }

    private void NotifyTimerCommands()
    {
        ((AsyncRelayCommand)StartReminderCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)PauseReminderCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ResumeReminderCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ResetReminderCommand).NotifyCanExecuteChanged();
    }
}
