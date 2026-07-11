namespace ScreenEase.Core;

public sealed record EyeProfile(
    string Id,
    string Name,
    int ColorTemperatureKelvin,
    int BrightnessPercent,
    int NightColorTemperatureKelvin,
    int NightBrightnessPercent);

public sealed record RestTimerSettings(
    bool Enabled,
    int WorkMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes,
    int LongBreakEveryWorkSessions,
    bool AutoStart);

public sealed record OverlaySettings(
    bool Enabled,
    int OpacityPercent,
    string ColorHex);

public sealed record OverlayState(
    bool Enabled,
    int OpacityPercent,
    string ColorHex,
    int WindowCount);

public enum HotkeyAction
{
    ToggleEnabled,
    IncreaseBrightness,
    DecreaseBrightness,
    IncreaseColorTemperature,
    DecreaseColorTemperature,
    ApplyLongReadProfile,
    ApplyLowBlueEveningProfile,
    ToggleOverlay
}

public sealed record HotkeyBinding(
    string Id,
    HotkeyAction Action,
    string Gesture,
    bool Enabled);

public enum RestTimerPhase
{
    Stopped,
    Work,
    ShortBreak,
    LongBreak,
    Paused
}

public sealed record RestTimerState(
    RestTimerPhase Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    TimeSpan? PausedRemaining,
    RestTimerPhase? PausedFrom,
    int CompletedWorkSessions);

public sealed record EyeCareSettings(
    bool Enabled,
    string ActiveProfileId,
    bool UseNightValues,
    bool UseSchedule,
    TimeOnly Sunrise,
    TimeOnly Sunset,
    bool SmoothTransitions,
    TimeSpan TransitionDuration,
    OverlaySettings Overlay,
    IReadOnlyList<HotkeyBinding> Hotkeys,
    RestTimerSettings RestTimer,
    IReadOnlyList<EyeProfile> Profiles);

public sealed record DisplayEffect(
    bool Enabled,
    string ProfileId,
    int ColorTemperatureKelvin,
    int BrightnessPercent,
    bool IsNightValue,
    DateTimeOffset AppliedAt);

public sealed record DisplayEffectRequest(
    bool Enabled,
    string ProfileId,
    int ColorTemperatureKelvin,
    int BrightnessPercent,
    bool IsNightValue,
    TimeSpan TransitionDuration);

public sealed record MonitorInfo(
    string Id,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

public sealed record EyeCareState(
    EyeCareSettings Settings,
    DisplayEffect Effect,
    OverlayState Overlay,
    IReadOnlyList<HotkeyBinding> Hotkeys,
    RestTimerState RestTimer,
    IReadOnlyList<MonitorInfo> Monitors);

public sealed record ApplyEffectCommand(
    string? ProfileId,
    int? ColorTemperatureKelvin,
    int? BrightnessPercent,
    bool? Enabled);

public sealed record UpdateOverlayCommand(
    bool? Enabled,
    int? OpacityPercent,
    string? ColorHex);

public sealed record LegacyImportRequest(string Path);


