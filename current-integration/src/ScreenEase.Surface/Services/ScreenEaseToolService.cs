using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace ScreenEase.Surface.Services;

public sealed class ScreenEaseToolService
{
    private const string ModuleId = "screenease";
    private const string ServiceUnitId = "screenease.service";
    private readonly ShellCommandExecutionService _commands;
    private readonly IServiceUnitClient _serviceUnitClient;

    public ScreenEaseToolService(IServiceUnitClient? serviceUnitClient = null)
    {
        _serviceUnitClient = serviceUnitClient ?? new NullServiceUnitClient(ModuleId);
        _commands = new ShellCommandExecutionService(_serviceUnitClient);
    }

    /// <summary>
    /// Returns the ScreenEase.Service unit snapshot, or null when no ServiceManager/unit is available.
    /// The Surface renders this compactly in the existing diagnostics/title area without a large
    /// generic status bar, per the plan's "紧凑地融入现有标题区或诊断区域".
    /// </summary>
    public async Task<ScreenEaseServiceUnitStatus?> LoadServiceUnitStatusAsync(CancellationToken cancellationToken = default)
    {
        var client = _serviceUnitClient;
        if (client is NullServiceUnitClient)
        {
            return null;
        }

        try
        {
            var units = await client.ListAsync(cancellationToken);
            var unit = units.FirstOrDefault(u => string.Equals(u.Id, ServiceUnitId, StringComparison.OrdinalIgnoreCase));
            return unit is null ? null : new ScreenEaseServiceUnitStatus(
                unit.Id,
                unit.State,
                unit.Pid,
                unit.Uptime,
                unit.RestartCount,
                unit.LastError,
                unit.Readiness);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restarts the ScreenEase.Service unit through the ServiceManager. Returns null if no unit is
    /// available (the caller can then fall back to the in-proc path or report unavailable).
    /// </summary>
    public async Task<ScreenEaseServiceUnitStatus?> RestartServiceUnitAsync(CancellationToken cancellationToken = default)
    {
        var client = _serviceUnitClient;
        if (client is NullServiceUnitClient)
        {
            return null;
        }

        try
        {
            var snapshot = await client.RestartAsync(ServiceUnitId, cancellationToken);
            return new ScreenEaseServiceUnitStatus(
                snapshot.Id,
                snapshot.State,
                snapshot.Pid,
                snapshot.Uptime,
                snapshot.RestartCount,
                snapshot.LastError,
                snapshot.Readiness);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ScreenEaseSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var statusTask = _commands.ExecuteAsync("screenease.status.summary", cancellationToken: cancellationToken);
        var planTask = _commands.ExecuteAsync("screenease.profile.plan", cancellationToken: cancellationToken);
        var settingsTask = _commands.GetSettingsAsync(cancellationToken);
        var logsTask = _commands.TailLogsAsync(cancellationToken);
        await Task.WhenAll(statusTask, planTask, settingsTask, logsTask).ConfigureAwait(false);

        EnsureSucceeded(statusTask.Result);
        var status = ParseObject(statusTask.Result.Response.Summary);
        var plan = string.Equals(planTask.Result.Response.State, "succeeded", StringComparison.OrdinalIgnoreCase)
            ? ParsePlan(ParseObject(planTask.Result.Response.Summary))
            : ScreenEasePlan.Empty;
        var nativeHost = status["nativeHost"] as JsonObject;
        var settings = settingsTask.Result.Values;
        var activity = logsTask.Result
            .OrderByDescending(entry => entry.Time)
            .Take(40)
            .Select(entry => new ScreenEaseActivity(
                entry.Time,
                entry.Level,
                entry.Message))
            .ToArray();

        return new ScreenEaseSnapshot(
            ReadString(status, "activeProfileId"),
            ParseProfiles(status["profiles"] as JsonArray),
            ParseDisplays(status["displays"] as JsonArray),
            ParseRules(status["rules"] as JsonArray),
            new ScreenEaseNativeWriter(
                ReadBool(nativeHost, "enabled"),
                ReadBool(nativeHost, "available"),
                ReadString(nativeHost, "state", "unknown"),
                ReadString(nativeHost, "message")),
            ParseReminder(status["reminder"] as JsonObject),
            plan,
            activity,
            settingsTask.Result.Revision,
            ParseSchedule(status["schedule"] as JsonObject),
            ParseReminderState(status["reminderState"] as JsonObject),
            ParseEffect(status["effect"] as JsonObject),
            ParseAdvanced(status["advanced"] as JsonObject ?? settings["advanced"] as JsonObject),
            ParseOverlayResult(status["overlay"] as JsonObject),
            ParseHotkeys(settings["hotkeys"] as JsonArray));
    }

    public async Task<ScreenEasePlan> PreviewAsync(
        string profileId,
        string displayId,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.profile.plan",
            BuildProfileTargetArgs(profileId, displayId, hardwareWrite: null),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return ParsePlan(ParseObject(result.Response.Summary));
    }

    public async Task<ScreenEaseDisplayEffect> ApplyProfileAsync(
        string profileId,
        string displayId,
        bool hardwareWrite,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.profile.apply",
            BuildApplyArgs(profileId, displayId, hardwareWrite),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return ParseEffect(ParseObject(result.Response.Summary)["effect"] as JsonObject);
    }

    public async Task SaveProfileAsync(
        ScreenEaseProfile profile,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.profile.save",
            BuildProfileArgs(profile),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
    }

    public async Task<ScreenEaseDisplayEffect> ApplyManualAsync(
        int colorTemperatureKelvin,
        int brightnessPercent,
        string displayId,
        bool hardwareWrite,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.effect.apply",
            BuildManualApplyArgs(colorTemperatureKelvin, brightnessPercent, displayId, hardwareWrite),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        var payload = ParseObject(result.Response.Summary);
        return ParseEffect(payload["effect"] as JsonObject);
    }

    public async Task<ScreenEaseDisplayEffect> ConfigureScheduleAsync(
        ScreenEaseSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.schedule.configure",
            BuildScheduleJson(schedule),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return ParseEffect(ParseObject(result.Response.Summary)["effect"] as JsonObject);
    }

    public async Task ConfigureReminderAsync(
        ScreenEaseReminder reminder,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.reminder.configure",
            BuildReminderJson(reminder),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
    }

    public async Task<ScreenEaseAdvancedSaveResult> SaveAdvancedAsync(
        ulong expectedRevision,
        ScreenEaseAdvanced advanced,
        CancellationToken cancellationToken = default)
    {
        var patch = new JsonObject
        {
            ["advanced"] = BuildAdvancedJson(advanced)
        };
        var updated = await _commands.UpdateSettingsAsync(expectedRevision, patch, cancellationToken).ConfigureAwait(false);
        var values = updated.Values;
        return new ScreenEaseAdvancedSaveResult(
            updated.Revision,
            ParseAdvanced(values["advanced"] as JsonObject));
    }

    public async Task<ScreenEaseOverlayResult> ConfigureOverlayAsync(
        ScreenEaseOverlayConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.overlay.configure",
            BuildOverlayJson(configuration),
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return ParseOverlayResult(ParseObject(result.Response.Summary));
    }

    public async Task<ScreenEaseSnapshot> ImportLegacyAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.legacy.import",
            new JsonObject { ["path"] = path },
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ScreenEaseReminderState> LoadReminderStateAsync(CancellationToken cancellationToken = default) =>
        ExecuteReminderActionAsync("status", cancellationToken);

    public Task<ScreenEaseReminderState> StartReminderAsync(CancellationToken cancellationToken = default) =>
        ExecuteReminderActionAsync("start", cancellationToken);

    public Task<ScreenEaseReminderState> PauseReminderAsync(CancellationToken cancellationToken = default) =>
        ExecuteReminderActionAsync("pause", cancellationToken);

    public Task<ScreenEaseReminderState> ResumeReminderAsync(CancellationToken cancellationToken = default) =>
        ExecuteReminderActionAsync("resume", cancellationToken);

    public Task<ScreenEaseReminderState> ResetReminderAsync(CancellationToken cancellationToken = default) =>
        ExecuteReminderActionAsync("reset", cancellationToken);

    public async Task<ScreenEaseDisableResult> DisableEffectAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "screenease.effect.disable",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        var payload = ParseObject(result.Response.Summary);
        var displayReset = payload["displayReset"] as JsonObject;
        return new ScreenEaseDisableResult(
            ParseEffect(payload),
            ReadBool(displayReset, "attempted"),
            ReadBool(displayReset, "success", true),
            ReadString(displayReset, "state", "logical-only"),
            ReadString(displayReset, "message"));
    }

    public async Task<ulong> SaveRulesAsync(
        ulong expectedRevision,
        IReadOnlyList<ScreenEaseRule> rules,
        CancellationToken cancellationToken = default)
    {
        var patch = new JsonObject
        {
            ["rules"] = BuildRuleArray(rules)
        };
        var updated = await _commands.UpdateSettingsAsync(expectedRevision, patch, cancellationToken).ConfigureAwait(false);
        return updated.Revision;
    }

    public async Task<ulong> SaveReminderAsync(
        ulong expectedRevision,
        ScreenEaseReminder reminder,
        CancellationToken cancellationToken = default)
    {
        var patch = new JsonObject
        {
            ["reminder"] = BuildReminderJson(reminder)
        };
        var updated = await _commands.UpdateSettingsAsync(expectedRevision, patch, cancellationToken).ConfigureAwait(false);
        return updated.Revision;
    }

    public static JsonObject BuildApplyArgs(string profileId, string displayId, bool hardwareWrite)
    {
        return BuildProfileTargetArgs(profileId, displayId, hardwareWrite);
    }

    public static JsonObject BuildManualApplyArgs(
        int colorTemperatureKelvin,
        int brightnessPercent,
        string displayId,
        bool hardwareWrite)
    {
        return new JsonObject
        {
            ["colorTemperatureKelvin"] = colorTemperatureKelvin,
            ["brightnessPercent"] = brightnessPercent,
            ["displayId"] = string.IsNullOrWhiteSpace(displayId) ? "all" : displayId,
            ["hardwareWrite"] = hardwareWrite
        };
    }

    public static JsonObject BuildProfileArgs(ScreenEaseProfile profile)
    {
        return new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["brightness"] = profile.Brightness,
            ["colorTemperature"] = profile.ColorTemperature,
            ["nightBrightness"] = profile.EffectiveNightBrightness,
            ["nightColorTemperature"] = profile.EffectiveNightColorTemperature
        };
    }

    public static JsonObject BuildScheduleJson(ScreenEaseSchedule schedule)
    {
        return new JsonObject
        {
            ["useNightValues"] = schedule.UseNightValues,
            ["useSchedule"] = schedule.UseSchedule,
            ["sunrise"] = schedule.Sunrise,
            ["sunset"] = schedule.Sunset
        };
    }

    public static JsonArray BuildRuleArray(IReadOnlyList<ScreenEaseRule> rules)
    {
        var array = new JsonArray();
        foreach (var rule in rules)
        {
            array.Add(new JsonObject
            {
                ["id"] = rule.Id,
                ["profileId"] = rule.ProfileId,
                ["enabled"] = rule.Enabled,
                ["condition"] = rule.Condition
            });
        }

        return array;
    }

    public static JsonObject BuildReminderJson(ScreenEaseReminder reminder)
    {
        return new JsonObject
        {
            ["enabled"] = reminder.Enabled,
            ["autoStartNext"] = reminder.AutoStartNext,
            ["focusMinutes"] = reminder.FocusMinutes,
            ["shortBreakMinutes"] = reminder.ShortBreakMinutes,
            ["longBreakMinutes"] = reminder.LongBreakMinutes,
            ["longBreakInterval"] = reminder.LongBreakInterval
        };
    }

    public static JsonObject BuildAdvancedJson(ScreenEaseAdvanced advanced)
    {
        return new JsonObject
        {
            ["smoothTransitions"] = advanced.SmoothTransitions,
            ["transitionDurationMs"] = advanced.TransitionDurationMs
        };
    }

    public static JsonObject BuildOverlayJson(ScreenEaseOverlayConfiguration configuration)
    {
        return new JsonObject
        {
            ["enabled"] = configuration.Enabled,
            ["opacityPercent"] = configuration.OpacityPercent,
            ["colorHex"] = configuration.ColorHex
        };
    }

    private static JsonObject BuildProfileTargetArgs(string profileId, string displayId, bool? hardwareWrite)
    {
        var args = new JsonObject
        {
            ["profileId"] = profileId,
            ["displayId"] = string.IsNullOrWhiteSpace(displayId) ? "all" : displayId
        };
        if (hardwareWrite is not null)
        {
            args["hardwareWrite"] = hardwareWrite.Value;
        }

        return args;
    }

    private static ScreenEasePlan ParsePlan(JsonObject root)
    {
        var expected = root["expectedChange"] as JsonObject;
        var actions = expected?["actions"] as JsonArray;
        return new ScreenEasePlan(
            ReadString(root, "activeProfileId"),
            ParseProfile(root["profile"] as JsonObject),
            ReadString(root, "targetDisplayId", "all"),
            actions?.OfType<JsonObject>()
                .Select(action => new ScreenEasePlanAction(
                    ReadString(action, "displayId"),
                    ReadString(action, "displayName"),
                    ReadInt(action, "brightness"),
                    ReadInt(action, "colorTemperature")))
                .ToArray() ?? [],
            ParseRules(root["rules"] as JsonArray));
    }

    private static IReadOnlyList<ScreenEaseProfile> ParseProfiles(JsonArray? profiles)
    {
        return profiles?.OfType<JsonObject>()
            .Select(ParseProfile)
            .Where(profile => profile.Id.Length > 0)
            .ToArray() ?? [];
    }

    private static ScreenEaseProfile ParseProfile(JsonObject? profile)
    {
        var brightness = ReadInt(profile, "brightness");
        var colorTemperature = ReadInt(profile, "colorTemperature");
        return new ScreenEaseProfile(
            ReadString(profile, "id"),
            ReadString(profile, "name"),
            brightness,
            colorTemperature,
            ReadInt(profile, "nightBrightness", brightness),
            ReadInt(profile, "nightColorTemperature", colorTemperature));
    }

    private static IReadOnlyList<ScreenEaseDisplay> ParseDisplays(JsonArray? displays)
    {
        return displays?.OfType<JsonObject>()
            .Select(display => new ScreenEaseDisplay(
                ReadString(display, "id"),
                ReadString(display, "name"),
                ReadString(display, "state", "unknown"),
                ReadInt(display, "width"),
                ReadInt(display, "height"),
                ReadInt(display, "refreshRateHz"),
                ReadString(display, "orientation", "unknown"),
                ReadBool(display, "primary"),
                ReadString(display, "detail")))
            .ToArray() ?? [];
    }

    private static IReadOnlyList<ScreenEaseRule> ParseRules(JsonArray? rules)
    {
        return rules?.OfType<JsonObject>()
            .Select(rule => new ScreenEaseRule(
                ReadString(rule, "id"),
                ReadString(rule, "profileId"),
                ReadString(rule, "profileName"),
                ReadBool(rule, "enabled"),
                ReadString(rule, "condition"),
                ReadString(rule, "state", "ready")))
            .Where(rule => rule.Id.Length > 0)
            .ToArray() ?? [];
    }

    private static ScreenEaseReminder ParseReminder(JsonObject? reminder)
    {
        return new ScreenEaseReminder(
            ReadBool(reminder, "enabled"),
            ReadBool(reminder, "autoStartNext"),
            Math.Clamp(ReadInt(reminder, "focusMinutes", 25), 1, 240),
            Math.Clamp(ReadInt(reminder, "shortBreakMinutes", 5), 1, 120),
            Math.Clamp(ReadInt(reminder, "longBreakMinutes", 15), 1, 240),
            Math.Clamp(ReadInt(reminder, "longBreakInterval", 4), 1, 12));
    }

    private static ScreenEaseSchedule ParseSchedule(JsonObject? schedule)
    {
        return new ScreenEaseSchedule(
            ReadBool(schedule, "useNightValues", true),
            ReadBool(schedule, "useSchedule"),
            ReadString(schedule, "sunrise", "07:00"),
            ReadString(schedule, "sunset", "19:00"));
    }

    private static ScreenEaseDisplayEffect ParseEffect(JsonObject? effect)
    {
        return new ScreenEaseDisplayEffect(
            ReadBool(effect, "enabled"),
            ReadString(effect, "profileId", "low-blue-evening"),
            Math.Clamp(ReadInt(effect, "colorTemperatureKelvin", 5000), 1000, 10000),
            Math.Clamp(ReadInt(effect, "brightnessPercent", 75), 1, 150),
            ReadBool(effect, "isNightValue"),
            ReadDateTimeOffset(effect, "appliedAt") ?? DateTimeOffset.MinValue);
    }

    private static ScreenEaseReminderState ParseReminderState(JsonObject? state)
    {
        return new ScreenEaseReminderState(
            ReadString(state, "phase", "stopped"),
            ReadDateTimeOffset(state, "startedAt"),
            ReadDateTimeOffset(state, "endsAt"),
            ReadNullableInt(state, "pausedRemainingSeconds"),
            ReadString(state, "pausedFrom"),
            ReadInt(state, "completedWorkSessions"),
            Math.Max(0, ReadInt(state, "remainingSeconds")));
    }

    private static ScreenEaseAdvanced ParseAdvanced(JsonObject? advanced)
    {
        return new ScreenEaseAdvanced(
            ReadBool(advanced, "smoothTransitions", true),
            Math.Clamp(ReadInt(advanced, "transitionDurationMs", 2000), 0, 120_000));
    }

    private static ScreenEaseOverlayResult ParseOverlayResult(JsonObject? root)
    {
        var settings = root?["settings"] as JsonObject;
        var runtime = root?["runtime"] as JsonObject;
        return new ScreenEaseOverlayResult(
            new ScreenEaseOverlayConfiguration(
                ReadBool(settings, "enabled"),
                Math.Clamp(ReadInt(settings, "opacityPercent", 18), 0, 95),
                ReadString(settings, "colorHex", "#FFC98A")),
            new ScreenEaseOverlayRuntime(
                ReadBool(runtime, "enabled"),
                Math.Clamp(ReadInt(runtime, "opacityPercent"), 0, 95),
                ReadString(runtime, "colorHex", "#FFC98A"),
                Math.Max(0, ReadInt(runtime, "windowCount")),
                ReadString(runtime, "state", "unknown"),
                ReadString(runtime, "message")));
    }

    private static IReadOnlyList<ScreenEaseHotkey> ParseHotkeys(JsonArray? configuredHotkeys)
    {
        var configured = (configuredHotkeys ?? [])
            .OfType<JsonObject>()
            .Where(item => ReadString(item, "id").Length > 0)
            .ToDictionary(item => ReadString(item, "id"), StringComparer.OrdinalIgnoreCase);
        return HotkeyDefaults.Select(definition =>
        {
            configured.TryGetValue(definition.Id, out var saved);
            var savedEnabled = ReadBool(saved, "enabled");
            var state = savedEnabled ? "pending" : "disabled";
            var gesture = ReadString(saved, "gesture", definition.DefaultGesture);

            return new ScreenEaseHotkey(
                definition.Id,
                definition.Title,
                definition.CommandId,
                gesture,
                definition.DefaultGesture,
                savedEnabled,
                state,
                "");
        }).ToArray();
    }

    private static readonly (string Id, string Title, string CommandId, string DefaultGesture)[] HotkeyDefaults =
    [
        ("toggle-enabled", "开关护眼", "screenease.effect.toggle", "Ctrl+Alt+F9"),
        ("brightness-up", "提高亮度", "screenease.effect.brightness.increase", "Ctrl+Alt+Up"),
        ("brightness-down", "降低亮度", "screenease.effect.brightness.decrease", "Ctrl+Alt+Down"),
        ("temperature-up", "提高色温", "screenease.effect.temperature.increase", "Ctrl+Alt+Right"),
        ("temperature-down", "降低色温", "screenease.effect.temperature.decrease", "Ctrl+Alt+Left"),
        ("long-read-profile", "应用长读柔光", "screenease.profile.apply-long-read", "Ctrl+Alt+R"),
        ("low-blue-evening-profile", "应用夜间低蓝", "screenease.profile.apply-low-blue-evening", "Ctrl+Alt+H"),
        ("toggle-overlay", "开关柔光遮罩", "screenease.overlay.toggle", "Ctrl+Alt+D")
    ];

    private async Task<ScreenEaseReminderState> ExecuteReminderActionAsync(
        string action,
        CancellationToken cancellationToken)
    {
        var result = await _commands.ExecuteAsync(
            $"screenease.reminder.{action}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return ParseReminderState(ParseObject(result.Response.Summary));
    }

    private static void EnsureSucceeded(ShellCommandExecutionResult result)
    {
        if (string.Equals(result.Response.State, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(result.Response.ErrorMessage.Length > 0
            ? result.Response.ErrorMessage
            : result.Response.Summary);
    }

    private static JsonObject ParseObject(string value)
    {
        try
        {
            return JsonNode.Parse(value) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string ReadString(JsonObject? source, string key, string fallback = "")
    {
        try
        {
            return source?[key]?.GetValue<string>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject? source, string key, int fallback = 0)
    {
        try
        {
            return source?[key]?.GetValue<int>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return int.TryParse(ReadString(source, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }
    }

    private static bool ReadBool(JsonObject? source, string key, bool fallback = false)
    {
        try
        {
            return source?[key]?.GetValue<bool>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int? ReadNullableInt(JsonObject? source, string key)
    {
        try
        {
            return source?[key] is null ? null : source[key]!.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonObject? source, string key)
    {
        try
        {
            if (source?[key] is { } node)
            {
                return node.GetValue<DateTimeOffset>();
            }
        }
        catch (InvalidOperationException)
        {
            // Some transports preserve the RFC 3339 value as a JSON string.
        }

        var value = ReadString(source, key);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}

public sealed record ScreenEaseSnapshot(
    string ActiveProfileId,
    IReadOnlyList<ScreenEaseProfile> Profiles,
    IReadOnlyList<ScreenEaseDisplay> Displays,
    IReadOnlyList<ScreenEaseRule> Rules,
    ScreenEaseNativeWriter NativeWriter,
    ScreenEaseReminder Reminder,
    ScreenEasePlan Plan,
    IReadOnlyList<ScreenEaseActivity> Activity,
    ulong SettingsRevision,
    ScreenEaseSchedule? Schedule = null,
    ScreenEaseReminderState? ReminderState = null,
    ScreenEaseDisplayEffect? Effect = null,
    ScreenEaseAdvanced? Advanced = null,
    ScreenEaseOverlayResult? Overlay = null,
    IReadOnlyList<ScreenEaseHotkey>? Hotkeys = null);

public sealed record ScreenEaseAdvanced(bool SmoothTransitions, int TransitionDurationMs)
{
    public static ScreenEaseAdvanced Default { get; } = new(true, 2000);
}

public sealed record ScreenEaseAdvancedSaveResult(ulong SettingsRevision, ScreenEaseAdvanced Advanced);

public sealed record ScreenEaseOverlayConfiguration(bool Enabled, int OpacityPercent, string ColorHex)
{
    public static ScreenEaseOverlayConfiguration Default { get; } = new(false, 18, "#FFC98A");
}

public sealed record ScreenEaseOverlayRuntime(
    bool Enabled,
    int OpacityPercent,
    string ColorHex,
    int WindowCount,
    string State,
    string Message)
{
    public static ScreenEaseOverlayRuntime Hidden { get; } = new(false, 0, "#FFC98A", 0, "hidden", "");
}

public sealed record ScreenEaseOverlayResult(
    ScreenEaseOverlayConfiguration Settings,
    ScreenEaseOverlayRuntime Runtime)
{
    public static ScreenEaseOverlayResult Default { get; } = new(
        ScreenEaseOverlayConfiguration.Default,
        ScreenEaseOverlayRuntime.Hidden);
}

public sealed record ScreenEaseHotkey(
    string Id,
    string Title,
    string CommandId,
    string Gesture,
    string DefaultGesture,
    bool Enabled,
    string State,
    string Message)
{
    public bool HasAttention => State.Trim().ToLowerInvariant() is "conflict" or "failed" or "error";
    public string StatusText => !Enabled
        ? "未启用"
        : HasAttention
            ? "需要处理"
            : State.Trim().ToLowerInvariant() is "registered" or "ok"
                ? "已注册"
                : "等待注册";
}

public sealed record ScreenEaseProfile(
    string Id,
    string Name,
    int Brightness,
    int ColorTemperature,
    int NightBrightness = -1,
    int NightColorTemperature = -1)
{
    public string BrightnessText => $"{Brightness.ToString(CultureInfo.InvariantCulture)}%";
    public string ColorTemperatureText => $"{ColorTemperature.ToString(CultureInfo.InvariantCulture)} K";
    public int EffectiveNightBrightness => NightBrightness < 0 ? Brightness : NightBrightness;
    public int EffectiveNightColorTemperature => NightColorTemperature < 0 ? ColorTemperature : NightColorTemperature;
}

public sealed record ScreenEaseDisplay(
    string Id,
    string Name,
    string State,
    int Width,
    int Height,
    int RefreshRateHz,
    string Orientation,
    bool Primary,
    string Detail)
{
    public bool IsUsable => Width > 0 &&
                            Height > 0 &&
                            State.Trim().ToLowerInvariant() is "connected" or "ready" or "available";
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Resolution unavailable";
    public string RefreshRateText => RefreshRateHz > 0 ? $"{RefreshRateHz} Hz" : "Refresh rate unavailable";
    public string PrimaryText => Primary ? "Primary display" : "Secondary display";
}

public sealed record ScreenEaseRule(
    string Id,
    string ProfileId,
    string ProfileName,
    bool Enabled,
    string Condition,
    string State);

public sealed record ScreenEaseNativeWriter(bool Enabled, bool Available, string State, string Message)
{
    public string StatusLabel => Available ? Enabled ? "Hardware control on" : "Hardware control off" : "Hardware unavailable";
}

public sealed record ScreenEaseDisplayEffect(
    bool Enabled,
    string ProfileId,
    int ColorTemperatureKelvin,
    int BrightnessPercent,
    bool IsNightValue,
    DateTimeOffset AppliedAt);

public sealed record ScreenEaseDisableResult(
    ScreenEaseDisplayEffect Effect,
    bool DisplayResetAttempted,
    bool DisplayResetSucceeded,
    string DisplayResetState,
    string DisplayResetMessage);

public sealed record ScreenEaseReminder(
    bool Enabled,
    bool AutoStartNext,
    int FocusMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes,
    int LongBreakInterval);

public sealed record ScreenEaseSchedule(
    bool UseNightValues,
    bool UseSchedule,
    string Sunrise,
    string Sunset)
{
    public static ScreenEaseSchedule Default { get; } = new(true, false, "07:00", "19:00");
}

public sealed record ScreenEaseReminderState(
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    int? PausedRemainingSeconds,
    string PausedFrom,
    int CompletedWorkSessions,
    int RemainingSeconds)
{
    public static ScreenEaseReminderState Stopped { get; } = new("stopped", null, null, null, "", 0, 0);
}

public sealed record ScreenEasePlan(
    string ActiveProfileId,
    ScreenEaseProfile Profile,
    string TargetDisplayId,
    IReadOnlyList<ScreenEasePlanAction> Actions,
    IReadOnlyList<ScreenEaseRule> Rules)
{
    public static ScreenEasePlan Empty { get; } = new("", new ScreenEaseProfile("", "", 0, 0), "all", [], []);
}

public sealed record ScreenEasePlanAction(
    string DisplayId,
    string DisplayName,
    int Brightness,
    int ColorTemperature)
{
    public string ChangeText => $"{Brightness}% · {ColorTemperature} K";
}

public sealed record ScreenEaseActivity(DateTimeOffset Time, string Level, string Message)
{
    public string TimeText => Time == DateTimeOffset.MinValue
        ? "--:--"
        : Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>
/// Live status of the ScreenEase.Service unit (when a ServiceManager is supervising it).
/// The Surface folds this into its existing diagnostics/title area rather than a separate status bar.
/// </summary>
public sealed record ScreenEaseServiceUnitStatus(
    string UnitId,
    ServiceUnitState State,
    int? Pid,
    TimeSpan? Uptime,
    int RestartCount,
    string? LastError,
    ServiceUnitReadiness? Readiness)
{
    public bool IsRunning => State == ServiceUnitState.Active || State == ServiceUnitState.Degraded;
    public string Summary => IsRunning
        ? $"service active · pid {Pid ?? 0}"
        : $"service {State.ToString().ToLowerInvariant()}";
}
