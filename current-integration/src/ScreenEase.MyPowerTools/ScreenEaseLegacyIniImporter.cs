using System.Globalization;

namespace ScreenEase.MyPowerTools;

internal static class ScreenEaseLegacyIniImporter
{
    private static readonly string[] ProfileIds = ["office", "read", "edit", "movie", "game", "health", "custom"];

    public static async Task<ScreenEaseState> ImportAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A settings path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("CareUEyes settings file was not found.", fullPath);
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var ini = Parse(lines);
        var defaults = ScreenEaseState.Default();
        var screen = ini.TryGetValue("screen", out var screenValues)
            ? screenValues
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = ProfileIds
            .Select(id => CreateProfile(id, screen, defaults))
            .ToArray();
        var activeProfileId = MapMode(ReadInt(screen, "mode", 0)) ?? "low-blue-evening";
        var reminder = ini.TryGetValue("rest", out var restValues)
            ? new ScreenEaseReminderSettings(
                ReadBool(restValues, "enable_rest_timer", defaults.GetReminder().Enabled),
                ReadBool(restValues, "auto_restart_timer", defaults.GetReminder().AutoStartNext),
                Math.Clamp(ReadInt(restValues, "work_duration", defaults.GetReminder().FocusMinutes), 1, 240),
                Math.Clamp(ReadInt(restValues, "short_duration", defaults.GetReminder().ShortBreakMinutes), 1, 120),
                Math.Clamp(ReadInt(restValues, "long_duration", defaults.GetReminder().LongBreakMinutes), 1, 240),
                Math.Clamp(ReadInt(restValues, "long_pause_interval", defaults.GetReminder().LongBreakInterval), 1, 12))
            : defaults.GetReminder();
        var schedule = defaults.GetSchedule() with
        {
            UseSchedule = ReadBool(screen, "enablesunset", defaults.GetSchedule().UseSchedule)
        };
        var advanced = new ScreenEaseAdvancedSettings(
            ReadBool(screen, "smooth", defaults.GetAdvanced().SmoothTransitions),
            Math.Clamp(
                (int)Math.Round(ReadInt(screen, "transition_duration", 2000) / 65.536, MidpointRounding.AwayFromZero),
                0,
                120_000));
        var now = DateTimeOffset.Now;
        var active = profiles.First(profile => profile.Id == activeProfileId);
        var values = active.ResolveValues(schedule, now);

        return new ScreenEaseState(
            activeProfileId,
            profiles,
            defaults.Rules,
            new ScreenEaseNativeHostState(true, false, $"Imported from CareUEyes INI '{Path.GetFileName(fullPath)}'."),
            now,
            reminder,
            schedule,
            ScreenEaseReminderRuntime.Stopped(),
            new ScreenEaseDisplayEffect(true, active.Id, values.ColorTemperature, values.Brightness, values.IsNightValue, now),
            advanced,
            ScreenEaseOverlaySettings.Default(),
            ScreenEaseHotkeyBinding.Defaults(),
            true);
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new(StringComparer.OrdinalIgnoreCase)
        };
        var current = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                result.TryAdd(current, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[current][line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static ScreenEaseProfile CreateProfile(
        string sourceId,
        IReadOnlyDictionary<string, string> screen,
        ScreenEaseState defaults)
    {
        var targetId = sourceId switch
        {
            "office" => "day-office",
            "read" => "long-read",
            "edit" => "detail-work",
            "movie" => "warm-video",
            "game" => "bright-focus",
            "health" => "low-blue-evening",
            "custom" => "personal",
            _ => sourceId
        };
        var fallback = defaults.FindProfile(targetId)!;
        return new ScreenEaseProfile(
            targetId,
            fallback.Name,
            Math.Clamp(ReadInt(screen, $"{sourceId}_brightness", fallback.Brightness), 1, 150),
            Math.Clamp(ReadInt(screen, $"{sourceId}_colortemp", fallback.ColorTemperature), 1000, 10000),
            Math.Clamp(ReadInt(screen, $"{sourceId}_night_brightness", fallback.EffectiveNightBrightness), 1, 150),
            Math.Clamp(ReadInt(screen, $"{sourceId}_night_colortemp", fallback.EffectiveNightColorTemperature), 1000, 10000));
    }

    private static string? MapMode(int mode) => mode switch
    {
        1 => "long-read",
        2 => "detail-work",
        3 => "warm-video",
        4 => "bright-focus",
        9 => "low-blue-evening",
        10 => "day-office",
        _ => null
    };

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ when bool.TryParse(raw, out var value) => value,
            _ => fallback
        };
    }
}
