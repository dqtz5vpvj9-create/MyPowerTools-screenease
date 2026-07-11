using System.Globalization;

namespace ScreenEase.Core;

public static class LegacyIniImporter
{
    private static readonly string[] ProfileIds = ["office", "read", "edit", "movie", "game", "health", "custom"];

    public static async Task<EyeCareSettings> ImportAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A settings path is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Settings file was not found.", path);
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var ini = Parse(lines);
        var defaults = Defaults.CreateSettings();
        var screen = ini.TryGetValue("screen", out var values) ? values : new Dictionary<string, string>();

        var profiles = ProfileIds
            .Select(id => CreateProfile(id, screen, defaults))
            .Select(Validation.Normalize)
            .ToArray();

        var activeProfile = MapMode(ReadInt(screen, "mode", 0)) ?? "low-blue-evening";
        var rest = ini.TryGetValue("rest", out var restValues)
            ? new RestTimerSettings(
                Enabled: ReadBool(restValues, "enable_rest_timer", defaults.RestTimer.Enabled),
                WorkMinutes: ReadInt(restValues, "work_duration", defaults.RestTimer.WorkMinutes),
                ShortBreakMinutes: ReadInt(restValues, "short_duration", defaults.RestTimer.ShortBreakMinutes),
                LongBreakMinutes: ReadInt(restValues, "long_duration", defaults.RestTimer.LongBreakMinutes),
                LongBreakEveryWorkSessions: ReadInt(restValues, "long_pause_interval", defaults.RestTimer.LongBreakEveryWorkSessions),
                AutoStart: ReadBool(restValues, "auto_restart_timer", defaults.RestTimer.AutoStart))
            : defaults.RestTimer;

        return Validation.Normalize(defaults with
        {
            Enabled = true,
            ActiveProfileId = activeProfile,
            UseSchedule = ReadBool(screen, "enablesunset", defaults.UseSchedule),
            SmoothTransitions = ReadBool(screen, "smooth", defaults.SmoothTransitions),
            TransitionDuration = TimeSpan.FromMilliseconds(ReadInt(screen, "transition_duration", 2000) / 65.536),
            RestTimer = rest,
            Profiles = profiles
        });
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = "";
        result[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            result[current][key] = value;
        }

        return result;
    }

    private static EyeProfile CreateProfile(
        string sourceId,
        IReadOnlyDictionary<string, string> screen,
        EyeCareSettings defaults)
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
        var fallback = defaults.Profiles.First(profile => profile.Id == targetId);
        var name = fallback.Name;

        return new EyeProfile(
            targetId,
            name,
            ReadInt(screen, $"{sourceId}_colortemp", fallback.ColorTemperatureKelvin),
            ReadInt(screen, $"{sourceId}_brightness", fallback.BrightnessPercent),
            ReadInt(screen, $"{sourceId}_night_colortemp", fallback.NightColorTemperatureKelvin),
            ReadInt(screen, $"{sourceId}_night_brightness", fallback.NightBrightnessPercent));
    }

    private static string? MapMode(int mode) =>
        mode switch
        {
            1 => "long-read",
            2 => "detail-work",
            3 => "warm-video",
            4 => "bright-focus",
            9 => "low-blue-evening",
            10 => "day-office",
            _ => null
        };

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

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
            _ when bool.TryParse(raw, out var parsed) => parsed,
            _ => fallback
        };
    }
}


