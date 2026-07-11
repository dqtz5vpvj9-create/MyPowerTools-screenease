namespace ScreenEase.Core;

public static class Defaults
{
    public const string ManualProfileId = "manual-adjustment";
    public const string ManualProfileName = "自定义调节";

    public static EyeCareSettings CreateSettings() =>
        new(
            Enabled: false,
            ActiveProfileId: "low-blue-evening",
            UseNightValues: true,
            UseSchedule: false,
            Sunrise: new TimeOnly(7, 0),
            Sunset: new TimeOnly(19, 0),
            SmoothTransitions: true,
            TransitionDuration: TimeSpan.FromSeconds(2),
            Overlay: new OverlaySettings(
                Enabled: false,
                OpacityPercent: 18,
                ColorHex: "#FFC98A"),
            Hotkeys:
            [
                new HotkeyBinding("toggle-enabled", HotkeyAction.ToggleEnabled, "Ctrl+Alt+F9", false),
                new HotkeyBinding("brightness-up", HotkeyAction.IncreaseBrightness, "Ctrl+Alt+Up", false),
                new HotkeyBinding("brightness-down", HotkeyAction.DecreaseBrightness, "Ctrl+Alt+Down", false),
                new HotkeyBinding("temperature-up", HotkeyAction.IncreaseColorTemperature, "Ctrl+Alt+Right", false),
                new HotkeyBinding("temperature-down", HotkeyAction.DecreaseColorTemperature, "Ctrl+Alt+Left", false),
                new HotkeyBinding("long-read-profile", HotkeyAction.ApplyLongReadProfile, "Ctrl+Alt+R", false),
                new HotkeyBinding("low-blue-evening-profile", HotkeyAction.ApplyLowBlueEveningProfile, "Ctrl+Alt+H", false),
                new HotkeyBinding("toggle-overlay", HotkeyAction.ToggleOverlay, "Ctrl+Alt+D", false)
            ],
            RestTimer: new RestTimerSettings(
                Enabled: false,
                WorkMinutes: 25,
                ShortBreakMinutes: 5,
                LongBreakMinutes: 15,
                LongBreakEveryWorkSessions: 4,
                AutoStart: false),
            Profiles:
            [
                new EyeProfile("day-office", "日间办公", 6500, 100, 5000, 90),
                new EyeProfile("long-read", "长读柔光", 5000, 85, 4200, 75),
                new EyeProfile("detail-work", "细节清晰", 6500, 90, 5000, 85),
                new EyeProfile("warm-video", "影音暖光", 4500, 85, 3700, 75),
                new EyeProfile("bright-focus", "高亮专注", 6500, 95, 5000, 85),
                new EyeProfile("low-blue-evening", "夜间低蓝", 3700, 75, 3200, 65),
                new EyeProfile("personal", "我的方案", 5000, 85, 4200, 75)
            ]);

    public static RestTimerState CreateRestTimerState() =>
        new(
            Phase: RestTimerPhase.Stopped,
            StartedAt: null,
            EndsAt: null,
            PausedRemaining: null,
            PausedFrom: null,
            CompletedWorkSessions: 0);

    public static DisplayEffect CreateEffect(DateTimeOffset now) =>
        new(
            Enabled: false,
            ProfileId: "low-blue-evening",
            ColorTemperatureKelvin: 3700,
            BrightnessPercent: 75,
            IsNightValue: false,
            AppliedAt: now);

    public static EyeProfile CreateManualProfile(int kelvin, int brightnessPercent) =>
        new(
            ManualProfileId,
            ManualProfileName,
            Validation.ClampKelvin(kelvin),
            Validation.ClampBrightness(brightnessPercent),
            Validation.ClampKelvin(kelvin),
            Validation.ClampBrightness(brightnessPercent));
}


