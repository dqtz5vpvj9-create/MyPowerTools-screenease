namespace ScreenEase.Core;

public static class Validation
{
    public const int MinimumKelvin = 1000;
    public const int MaximumKelvin = 10000;
    public const int MinimumBrightness = 1;
    public const int MaximumBrightness = 150;
    public const int MinimumOverlayOpacity = 0;
    public const int MaximumOverlayOpacity = 95;

    public static int ClampKelvin(int kelvin) =>
        Math.Clamp(kelvin, MinimumKelvin, MaximumKelvin);

    public static int ClampBrightness(int brightnessPercent) =>
        Math.Clamp(brightnessPercent, MinimumBrightness, MaximumBrightness);

    public static EyeProfile Normalize(EyeProfile profile) =>
        profile with
        {
            Id = NormalizeId(profile.Id),
            Name = NormalizeProfileName(profile.Id, profile.Name),
            ColorTemperatureKelvin = ClampKelvin(profile.ColorTemperatureKelvin),
            BrightnessPercent = ClampBrightness(profile.BrightnessPercent),
            NightColorTemperatureKelvin = ClampKelvin(profile.NightColorTemperatureKelvin),
            NightBrightnessPercent = ClampBrightness(profile.NightBrightnessPercent)
        };

    public static EyeCareSettings Normalize(EyeCareSettings settings)
    {
        var profiles = settings.Profiles.Count == 0
            ? Defaults.CreateSettings().Profiles
            : settings.Profiles.Select(Normalize).ToArray();

        var activeProfileId = NormalizeId(settings.ActiveProfileId);
        if (profiles.All(profile => profile.Id != activeProfileId))
        {
            activeProfileId = profiles[0].Id;
        }

        return settings with
        {
            ActiveProfileId = activeProfileId,
            TransitionDuration = ClampTransition(settings.TransitionDuration),
            Overlay = Normalize(settings.Overlay ?? Defaults.CreateSettings().Overlay),
            Hotkeys = NormalizeHotkeys(settings.Hotkeys),
            RestTimer = Normalize(settings.RestTimer),
            Profiles = profiles
        };
    }

    public static OverlaySettings Normalize(OverlaySettings settings) =>
        settings with
        {
            OpacityPercent = Math.Clamp(settings.OpacityPercent, MinimumOverlayOpacity, MaximumOverlayOpacity),
            ColorHex = NormalizeColorHex(settings.ColorHex)
        };

    public static RestTimerSettings Normalize(RestTimerSettings settings) =>
        settings with
        {
            WorkMinutes = Math.Clamp(settings.WorkMinutes, 1, 240),
            ShortBreakMinutes = Math.Clamp(settings.ShortBreakMinutes, 1, 120),
            LongBreakMinutes = Math.Clamp(settings.LongBreakMinutes, 1, 240),
            LongBreakEveryWorkSessions = Math.Clamp(settings.LongBreakEveryWorkSessions, 1, 12)
        };

    public static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "personal";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "office" => "day-office",
            "reading" or "read" => "long-read",
            "editing" or "edit" => "detail-work",
            "movie" => "warm-video",
            "game" => "bright-focus",
            "health" => "low-blue-evening",
            "custom" => "personal",
            "manual" => Defaults.ManualProfileId,
            _ => normalized
        };
    }

    private static string NormalizeProfileName(string id, string name) =>
        NormalizeId(id) switch
        {
            "day-office" => "日间办公",
            "long-read" => "长读柔光",
            "detail-work" => "细节清晰",
            "warm-video" => "影音暖光",
            "bright-focus" => "高亮专注",
            "low-blue-evening" => "夜间低蓝",
            "personal" => "我的方案",
            Defaults.ManualProfileId => Defaults.ManualProfileName,
            _ => string.IsNullOrWhiteSpace(name) ? "我的方案" : name.Trim()
        };

    public static string NormalizeColorHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "#000000";
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = "#" + trimmed;
        }

        if (trimmed.Length != 7)
        {
            return "#000000";
        }

        for (var index = 1; index < trimmed.Length; index++)
        {
            if (!Uri.IsHexDigit(trimmed[index]))
            {
                return "#000000";
            }
        }

        return trimmed.ToUpperInvariant();
    }

    private static IReadOnlyList<HotkeyBinding> NormalizeHotkeys(IReadOnlyList<HotkeyBinding>? hotkeys)
    {
        var defaults = Defaults.CreateSettings().Hotkeys;
        if (hotkeys is null || hotkeys.Count == 0)
        {
            return defaults;
        }

        var byAction = hotkeys
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Id))
            .GroupBy(binding => binding.Action)
            .ToDictionary(group => group.Key, group => group.Last());

        return defaults
            .Select(defaultBinding =>
            {
                var binding = byAction.TryGetValue(defaultBinding.Action, out var found) ? found : defaultBinding;
                return binding with
                {
                    Id = NormalizeId(binding.Id),
                    Gesture = NormalizeGesture(binding.Gesture),
                    Enabled = binding.Enabled && !string.IsNullOrWhiteSpace(binding.Gesture)
                };
            })
            .ToArray();
    }

    private static string NormalizeGesture(string value) =>
        string.Join(
            '+',
            (value ?? string.Empty)
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static TimeSpan ClampTransition(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : value;
    }
}


