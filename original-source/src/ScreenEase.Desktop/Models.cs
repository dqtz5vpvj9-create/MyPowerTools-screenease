namespace ScreenEase.Desktop;

public sealed class EyeProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ColorTemperatureKelvin { get; set; }
    public int BrightnessPercent { get; set; }
    public int NightColorTemperatureKelvin { get; set; }
    public int NightBrightnessPercent { get; set; }
}

public sealed class RestTimerSettings
{
    public bool Enabled { get; set; }
    public int WorkMinutes { get; set; }
    public int ShortBreakMinutes { get; set; }
    public int LongBreakMinutes { get; set; }
    public int LongBreakEveryWorkSessions { get; set; }
    public bool AutoStart { get; set; }
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public int OpacityPercent { get; set; }
    public string ColorHex { get; set; } = "#000000";
}

public sealed class OverlayState
{
    public bool Enabled { get; set; }
    public int OpacityPercent { get; set; }
    public string ColorHex { get; set; } = "#000000";
    public int WindowCount { get; set; }
}

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

public sealed class HotkeyBinding
{
    public string Id { get; set; } = string.Empty;
    public HotkeyAction Action { get; set; }
    public string Gesture { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public enum RestTimerPhase
{
    Stopped,
    Work,
    ShortBreak,
    LongBreak,
    Paused
}

public sealed class RestTimerState
{
    public RestTimerPhase Phase { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public TimeSpan? PausedRemaining { get; set; }
    public RestTimerPhase? PausedFrom { get; set; }
    public int CompletedWorkSessions { get; set; }
}

public sealed class EyeCareSettings
{
    public bool Enabled { get; set; }
    public string ActiveProfileId { get; set; } = "low-blue-evening";
    public bool UseNightValues { get; set; }
    public bool UseSchedule { get; set; }
    public TimeOnly Sunrise { get; set; }
    public TimeOnly Sunset { get; set; }
    public bool SmoothTransitions { get; set; }
    public TimeSpan TransitionDuration { get; set; }
    public OverlaySettings Overlay { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
    public RestTimerSettings RestTimer { get; set; } = new();
    public List<EyeProfile> Profiles { get; set; } = [];
}

public sealed class DisplayEffect
{
    public bool Enabled { get; set; }
    public string ProfileId { get; set; } = "low-blue-evening";
    public int ColorTemperatureKelvin { get; set; }
    public int BrightnessPercent { get; set; }
    public bool IsNightValue { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class MonitorInfo
{
    public string Id { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class EyeCareState
{
    public EyeCareSettings Settings { get; set; } = new();
    public DisplayEffect Effect { get; set; } = new();
    public OverlayState Overlay { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
    public RestTimerState RestTimer { get; set; } = new();
    public List<MonitorInfo> Monitors { get; set; } = [];
}

public sealed class ApplyEffectCommand
{
    public string? ProfileId { get; set; }
    public int? ColorTemperatureKelvin { get; set; }
    public int? BrightnessPercent { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class UpdateOverlayCommand
{
    public bool? Enabled { get; set; }
    public int? OpacityPercent { get; set; }
    public string? ColorHex { get; set; }
}

public sealed class MonitorRow
{
    public string Name { get; init; } = string.Empty;
    public string Bounds { get; init; } = string.Empty;
    public string Primary { get; init; } = string.Empty;
}
