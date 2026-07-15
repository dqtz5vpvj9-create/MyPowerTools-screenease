using System.Globalization;
using ScreenEase.Surface.Services;

namespace ScreenEase.Surface.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    private readonly Func<ScreenEaseAdvanced, Task<ScreenEaseAdvancedSaveResult>> _saveAdvanced;
    private readonly Func<ScreenEaseOverlayConfiguration, Task<ScreenEaseOverlayResult>> _saveOverlay;
    private readonly Func<string, Task<ScreenEaseSnapshot>> _importLegacy;
    private readonly Func<Task> _openHotkeySettings;
    private ulong _settingsRevision;
    private bool _smoothTransitions;
    private string _transitionDurationMs = "2000";
    private bool _overlayEnabled;
    private string _overlayOpacityPercent = "18";
    private string _overlayColorHex = "#FFC98A";
    private ScreenEaseOverlayRuntime _overlayRuntime = ScreenEaseOverlayRuntime.Hidden;
    private string _legacyIniPath = "";
    private string _advancedMessage = "";
    private string _overlayMessage = "";
    private string _legacyImportMessage = "";
    private IReadOnlyList<ScreenEaseHotkey> _hotkeys = [];

    public bool SmoothTransitions
    {
        get => _smoothTransitions;
        set => SetProperty(ref _smoothTransitions, value);
    }

    public string TransitionDurationMs
    {
        get => _transitionDurationMs;
        set => SetProperty(ref _transitionDurationMs, value);
    }

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetProperty(ref _overlayEnabled, value);
    }

    public string OverlayOpacityPercent
    {
        get => _overlayOpacityPercent;
        set => SetProperty(ref _overlayOpacityPercent, value);
    }

    public string OverlayColorHex
    {
        get => _overlayColorHex;
        set => SetProperty(ref _overlayColorHex, value);
    }

    public string LegacyIniPath
    {
        get => _legacyIniPath;
        set => SetProperty(ref _legacyIniPath, value);
    }

    public string AdvancedMessage
    {
        get => _advancedMessage;
        private set
        {
            if (SetProperty(ref _advancedMessage, value))
            {
                OnPropertyChanged(nameof(HasAdvancedMessage));
            }
        }
    }

    public string OverlayMessage
    {
        get => _overlayMessage;
        private set
        {
            if (SetProperty(ref _overlayMessage, value))
            {
                OnPropertyChanged(nameof(HasOverlayMessage));
            }
        }
    }

    public string LegacyImportMessage
    {
        get => _legacyImportMessage;
        private set
        {
            if (SetProperty(ref _legacyImportMessage, value))
            {
                OnPropertyChanged(nameof(HasLegacyImportMessage));
            }
        }
    }

    public IReadOnlyList<ScreenEaseHotkey> Hotkeys
    {
        get => _hotkeys;
        private set
        {
            if (SetProperty(ref _hotkeys, value))
            {
                OnPropertyChanged(nameof(HotkeyCountText));
                OnPropertyChanged(nameof(HotkeyStatusText));
            }
        }
    }

    public bool HasAdvancedMessage => AdvancedMessage.Length > 0;
    public bool HasOverlayMessage => OverlayMessage.Length > 0;
    public bool HasLegacyImportMessage => LegacyImportMessage.Length > 0;
    public string OverlayStateText => _overlayRuntime.State.Trim().ToLowerInvariant() switch
    {
        "applied" => "遮罩正在显示",
        "logical-only" => "设置已启用 · 当前平台仅保存",
        "unavailable" => "已启用 · 当前没有可用显示窗口",
        "failed" => "遮罩启动失败",
        "hidden" => "遮罩已关闭",
        _ => OverlayEnabled ? "等待遮罩运行状态" : "遮罩已关闭"
    };
    public string OverlayWindowCountText => $"{_overlayRuntime.WindowCount.ToString(CultureInfo.InvariantCulture)} 个显示窗口";
    public string OverlayRuntimeDetail => _overlayRuntime.Message.Length > 0
        ? _overlayRuntime.Message
        : $"当前设置：{OverlayOpacityPercent}% · {OverlayColorHex}";
    public string HotkeyCountText => $"{Hotkeys.Count.ToString(CultureInfo.InvariantCulture)} 个快捷键动作";
    public string HotkeyStatusText
    {
        get
        {
            var attention = Hotkeys.Count(item => item.HasAttention);
            var enabled = Hotkeys.Count(item => item.Enabled);
            if (attention > 0)
            {
                return $"{attention.ToString(CultureInfo.InvariantCulture)} 个快捷键需要处理";
            }

            return enabled == 0
                ? "快捷键均未启用"
                : $"{enabled.ToString(CultureInfo.InvariantCulture)} 个快捷键已启用";
        }
    }

    private void LoadExtendedSnapshot(ScreenEaseSnapshot snapshot)
    {
        var advanced = snapshot.Advanced ?? ScreenEaseAdvanced.Default;
        SmoothTransitions = advanced.SmoothTransitions;
        TransitionDurationMs = advanced.TransitionDurationMs.ToString(CultureInfo.InvariantCulture);

        var overlay = snapshot.Overlay ?? ScreenEaseOverlayResult.Default;
        ApplyOverlayResult(overlay);
        Hotkeys = snapshot.Hotkeys ?? [];
    }

    private async Task SaveAdvancedAsync()
    {
        if (!TryBoundedInt(TransitionDurationMs, 0, 120_000, out var duration))
        {
            AdvancedMessage = "过渡时长须为 0–120000 毫秒。";
            return;
        }

        await RunExtendedBusyAsync(async () =>
        {
            var result = await _saveAdvanced(new ScreenEaseAdvanced(SmoothTransitions, duration)).ConfigureAwait(true);
            _settingsRevision = result.SettingsRevision;
            SmoothTransitions = result.Advanced.SmoothTransitions;
            TransitionDurationMs = result.Advanced.TransitionDurationMs.ToString(CultureInfo.InvariantCulture);
            AdvancedMessage = SmoothTransitions
                ? $"平滑过渡已保存，时长 {TransitionDurationMs} 毫秒。"
                : "平滑过渡已关闭。";
        }, message => AdvancedMessage = message).ConfigureAwait(true);
    }

    private async Task SaveOverlayAsync()
    {
        if (!TryBoundedInt(OverlayOpacityPercent, 0, 95, out var opacity))
        {
            OverlayMessage = "遮罩不透明度须为 0–95。";
            return;
        }

        var color = OverlayColorHex.Trim().ToUpperInvariant();
        if (!IsRgbHex(color))
        {
            OverlayMessage = "遮罩颜色须使用 #RRGGBB 格式。";
            return;
        }

        await RunExtendedBusyAsync(async () =>
        {
            var result = await _saveOverlay(new ScreenEaseOverlayConfiguration(
                OverlayEnabled,
                opacity,
                color)).ConfigureAwait(true);
            ApplyOverlayResult(result);
            OverlayMessage = result.Runtime.State.Trim().ToLowerInvariant() == "failed"
                ? $"遮罩设置已保存；运行失败：{result.Runtime.Message}"
                : $"遮罩设置已应用：{OverlayStateText}。";
        }, message => OverlayMessage = message).ConfigureAwait(true);
    }

    private async Task ImportLegacyAsync()
    {
        var path = LegacyIniPath.Trim().Trim('"');
        if (path.Length == 0)
        {
            LegacyImportMessage = "请输入 CareUEyes INI 文件路径。";
            return;
        }

        await RunExtendedBusyAsync(async () =>
        {
            var imported = await _importLegacy(path).ConfigureAwait(true);
            ApplyImportedSnapshot(imported);
            LegacyIniPath = path;
            LegacyImportMessage = $"已导入 {imported.Profiles.Count.ToString(CultureInfo.InvariantCulture)} 个模式，并刷新日夜计划、提醒和过渡设置。";
        }, message => LegacyImportMessage = message).ConfigureAwait(true);
    }

    private void ApplyOverlayResult(ScreenEaseOverlayResult result)
    {
        OverlayEnabled = result.Settings.Enabled;
        OverlayOpacityPercent = result.Settings.OpacityPercent.ToString(CultureInfo.InvariantCulture);
        OverlayColorHex = result.Settings.ColorHex;
        _overlayRuntime = result.Runtime;
        OnPropertyChanged(nameof(OverlayStateText));
        OnPropertyChanged(nameof(OverlayWindowCountText));
        OnPropertyChanged(nameof(OverlayRuntimeDetail));
    }

    private void ApplyImportedSnapshot(ScreenEaseSnapshot imported)
    {
        Snapshot = imported;
        _settingsRevision = imported.SettingsRevision;
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(NativeWriter));
        OnPropertyChanged(nameof(Displays));
        OnPropertyChanged(nameof(DetectedDisplays));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(HardwareControlAvailable));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(NativeWriterStatusLabel));
        OnPropertyChanged(nameof(NativeWriterStatusDetail));
        OnPropertyChanged(nameof(NativeWriterTechnicalDetails));
        OnPropertyChanged(nameof(DisplayCountText));

        Modes.Clear();
        foreach (var profile in imported.Profiles
                     .OrderBy(profile => ModeSortOrder(profile.Id))
                     .ThenBy(profile => profile.Name, StringComparer.CurrentCulture))
        {
            Modes.Add(new ScreenEaseModeViewModel(profile, SelectModeAsync));
        }
        OnPropertyChanged(nameof(HasModes));
        OnPropertyChanged(nameof(ProfileCountText));

        var schedule = imported.Schedule ?? ScreenEaseSchedule.Default;
        UseNightValues = schedule.UseNightValues;
        EditingNightValues = false;
        UseSchedule = schedule.UseSchedule;
        Sunrise = schedule.Sunrise;
        Sunset = schedule.Sunset;

        var reminder = imported.Reminder;
        ReminderEnabled = reminder.Enabled;
        AutoStartNext = reminder.AutoStartNext;
        FocusMinutes = reminder.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        ShortBreakMinutes = reminder.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
        LongBreakMinutes = reminder.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
        LongBreakInterval = reminder.LongBreakInterval.ToString(CultureInfo.InvariantCulture);
        LoadReminderState(imported.ReminderState ?? ScreenEaseReminderState.Stopped);
        LoadExtendedSnapshot(imported);

        var selected = Modes.FirstOrDefault(mode =>
            string.Equals(mode.Id, imported.Effect?.ProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Modes.FirstOrDefault(mode =>
                string.Equals(mode.Id, imported.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Modes.FirstOrDefault();
        if (selected is not null)
        {
            SelectMode(selected);
        }

        if (imported.Effect is { } effect)
        {
            SetActualEffect(effect.ColorTemperatureKelvin, effect.BrightnessPercent);
            EyeCareEnabled = effect.Enabled;
        }
    }

    private async Task RunExtendedBusyAsync(Func<Task> action, Action<string> setFailure)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            setFailure($"操作失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static int ModeSortOrder(string id)
    {
        var index = Array.FindIndex(BuiltInOrder, value =>
            string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static bool IsRgbHex(string value)
    {
        return value.Length == 7 &&
               value[0] == '#' &&
               value.Skip(1).All(Uri.IsHexDigit);
    }
}
