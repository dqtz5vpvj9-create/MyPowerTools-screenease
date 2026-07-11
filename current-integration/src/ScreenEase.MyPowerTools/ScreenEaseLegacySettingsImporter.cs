using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace ScreenEase.MyPowerTools;

internal static class ScreenEaseLegacySettingsImporter
{
    public static bool TryImportFile(string? path, out ScreenEaseState state)
    {
        state = ScreenEaseState.Default();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
            {
                return false;
            }

            state = Import(root, File.GetLastWriteTimeUtc(path));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static ScreenEaseState Import(JsonObject root, DateTimeOffset? importedAt = null)
    {
        var defaults = ScreenEaseState.Default();
        var profiles = root["profiles"] is JsonArray profileArray
            ? profileArray
                .OfType<JsonObject>()
                .Select(ParseProfile)
                .Where(profile => profile.Validate().Count == 0)
                .ToArray()
            : [];
        if (profiles.Length == 0)
        {
            profiles = defaults.Profiles.ToArray();
        }

        var activeProfileId = ScreenEaseProfileIds.Normalize(SettingsJson.ReadString(root, "activeProfileId") ?? profiles[0].Id);
        if (!profiles.Any(profile => string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            activeProfileId = profiles[0].Id;
        }

        var schedule = new ScreenEaseScheduleSettings(
            SettingsJson.ReadBool(root, "useNightValues") ?? true,
            SettingsJson.ReadBool(root, "useSchedule") ?? false,
            NormalizeTime(SettingsJson.ReadString(root, "sunrise"), "07:00"),
            NormalizeTime(SettingsJson.ReadString(root, "sunset"), "19:00"));
        var reminderRoot = root["restTimer"] as JsonObject;
        var reminder = new ScreenEaseReminderSettings(
            SettingsJson.ReadBool(reminderRoot ?? new JsonObject(), "enabled") ?? false,
            SettingsJson.ReadBool(reminderRoot ?? new JsonObject(), "autoStart") ?? false,
            Math.Clamp(SettingsJson.ReadInt(reminderRoot ?? new JsonObject(), "workMinutes") ?? 25, 1, 240),
            Math.Clamp(SettingsJson.ReadInt(reminderRoot ?? new JsonObject(), "shortBreakMinutes") ?? 5, 1, 120),
            Math.Clamp(SettingsJson.ReadInt(reminderRoot ?? new JsonObject(), "longBreakMinutes") ?? 15, 1, 240),
            Math.Clamp(SettingsJson.ReadInt(reminderRoot ?? new JsonObject(), "longBreakEveryWorkSessions") ?? 4, 1, 12));
        var now = importedAt ?? DateTimeOffset.Now;
        var profile = profiles.First(item => string.Equals(item.Id, activeProfileId, StringComparison.OrdinalIgnoreCase));
        var values = profile.ResolveValues(schedule, now);
        var enabled = SettingsJson.ReadBool(root, "enabled") ?? false;
        var advanced = new ScreenEaseAdvancedSettings(
            SettingsJson.ReadBool(root, "smoothTransitions") ?? true,
            ReadTransitionDurationMs(root));
        var overlay = root["overlay"] is JsonObject overlayRoot
            ? ScreenEaseOverlaySettings.FromJson(overlayRoot)
            : ScreenEaseOverlaySettings.Default();
        var hotkeys = root["hotkeys"] is JsonArray hotkeyArray
            ? ScreenEaseHotkeyBinding.Normalize(hotkeyArray.OfType<JsonObject>().Select(ParseHotkey))
            : ScreenEaseHotkeyBinding.Defaults();

        return new ScreenEaseState(
            activeProfileId,
            profiles,
            [
                new ScreenEaseRule("evening", "low-blue-evening", true, "local-time >= sunset"),
                new ScreenEaseRule("morning", "day-office", true, "local-time >= sunrise")
            ],
            new ScreenEaseNativeHostState(
                true,
                false,
                "Imported from the original ScreenEase settings; gamma-ramp writes are enabled when the local display session supports them."),
            now,
            reminder,
            schedule,
            ScreenEaseReminderRuntime.Stopped(),
            new ScreenEaseDisplayEffect(
                enabled,
                profile.Id,
                values.ColorTemperature,
                values.Brightness,
                values.IsNightValue,
                now),
            advanced,
            overlay,
            hotkeys,
            true);
    }

    private static ScreenEaseProfile ParseProfile(JsonObject profile)
    {
        var brightness = Math.Clamp(SettingsJson.ReadInt(profile, "brightnessPercent") ?? 85, 1, 150);
        var temperature = Math.Clamp(SettingsJson.ReadInt(profile, "colorTemperatureKelvin") ?? 5000, 1000, 10000);
        return new ScreenEaseProfile(
            ScreenEaseProfileIds.Normalize(SettingsJson.ReadString(profile, "id") ?? "personal"),
            SettingsJson.ReadString(profile, "name") ?? "我的方案",
            brightness,
            temperature,
            Math.Clamp(SettingsJson.ReadInt(profile, "nightBrightnessPercent") ?? brightness, 1, 150),
            Math.Clamp(SettingsJson.ReadInt(profile, "nightColorTemperatureKelvin") ?? temperature, 1000, 10000));
    }

    private static string NormalizeTime(string? value, string fallback)
    {
        return TimeOnly.TryParse(value, out var parsed) ? parsed.ToString("HH:mm") : fallback;
    }

    private static int ReadTransitionDurationMs(JsonObject root)
    {
        var explicitMilliseconds = SettingsJson.ReadInt(root, "transitionDurationMs");
        if (explicitMilliseconds is not null)
        {
            return Math.Clamp(explicitMilliseconds.Value, 0, 120_000);
        }

        var serialized = SettingsJson.ReadString(root, "transitionDuration");
        return TimeSpan.TryParse(serialized, out var duration)
            ? Math.Clamp((int)Math.Round(duration.TotalMilliseconds), 0, 120_000)
            : 2000;
    }

    private static ScreenEaseHotkeyBinding ParseHotkey(JsonObject node) => new(
        ResolveHotkeyId(node["action"]) ?? SettingsJson.ReadString(node, "id") ?? "",
        SettingsJson.ReadString(node, "gesture") ?? "",
        SettingsJson.ReadBool(node, "enabled") ?? false);

    private static string? ResolveHotkeyId(JsonNode? action)
    {
        if (action is null)
        {
            return null;
        }

        try
        {
            return ResolveHotkeyId(action.GetValue<int>());
        }
        catch (InvalidOperationException)
        {
            // Original settings serialize HotkeyAction as a string; numeric payloads remain supported.
        }

        try
        {
            var value = action.GetValue<string>().Trim();
            if (int.TryParse(value, out var numeric))
            {
                return ResolveHotkeyId(numeric);
            }

            return value.ToLowerInvariant() switch
            {
                "toggleenabled" => "toggle-enabled",
                "increasebrightness" => "brightness-up",
                "decreasebrightness" => "brightness-down",
                "increasecolortemperature" => "temperature-up",
                "decreasecolortemperature" => "temperature-down",
                "applylongreadprofile" => "long-read-profile",
                "applylowblueeveningprofile" => "low-blue-evening-profile",
                "toggleoverlay" => "toggle-overlay",
                _ => null
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ResolveHotkeyId(int action) => action switch
    {
        0 => "toggle-enabled",
        1 => "brightness-up",
        2 => "brightness-down",
        3 => "temperature-up",
        4 => "temperature-down",
        5 => "long-read-profile",
        6 => "low-blue-evening-profile",
        7 => "toggle-overlay",
        _ => null
    };
}
