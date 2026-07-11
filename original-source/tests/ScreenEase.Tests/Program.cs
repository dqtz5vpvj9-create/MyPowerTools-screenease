using System.Text.Json;
using ScreenEase.Core;

var tests = new List<(string Name, Action Body)>
{
    ("color temperature warms by reducing blue", ColorTemperatureWarmsByReducingBlue),
    ("brightness uses gamma steps", BrightnessUsesGammaSteps),
    ("identity ramp restores full range", IdentityRampRestoresFullRange),
    ("default profile labels are independent", DefaultProfileLabelsAreIndependent),
    ("night window spans midnight", NightWindowSpansMidnight),
    ("rest timer moves from work to short break", RestTimerMovesToShortBreak),
    ("rest timer preserves paused break phase", RestTimerPreservesPausedBreakPhase),
    ("overlay settings are normalized", OverlaySettingsAreNormalized),
    ("hotkey parser handles modifier gesture", HotkeyParserHandlesModifierGesture),
    ("controller updates overlay through hotkey", ControllerUpdatesOverlayThroughHotkey),
    ("native message codec reads length-prefixed JSON", NativeMessageCodecReadsLengthPrefixedJson),
    ("native command handler applies overlay command", NativeCommandHandlerAppliesOverlayCommand),
    ("native command handler updates settings", NativeCommandHandlerUpdatesSettings),
    ("legacy INI import maps profiles", LegacyIniImportMapsProfiles),
    ("controller applies selected profile", ControllerAppliesSelectedProfile),
    ("controller persists manual adjustment profile", ControllerPersistsManualAdjustmentProfile),
    ("controller initializes when display reset fails", ControllerInitializesWhenDisplayResetFails)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine();
Console.WriteLine($"All {tests.Count} tests passed.");
return 0;

static void ColorTemperatureWarmsByReducingBlue()
{
    var daylight = ColorTemperature.ToRgbScale(6500);
    var warm = ColorTemperature.ToRgbScale(3700);

    AssertTrue(daylight.Blue > 0.95, "6500K should keep blue high.");
    AssertTrue(warm.Blue < daylight.Blue, "3700K should reduce blue.");
    AssertTrue(warm.Red >= daylight.Red * 0.99, "warm red channel should stay high.");
}

static void BrightnessUsesGammaSteps()
{
    var full = ColorTemperature.BuildGammaRamp(6500, 100);
    var half = ColorTemperature.BuildGammaRamp(6500, 50);

    AssertTrue(full.Red[255] > half.Red[255], "100 percent brightness should exceed 50 percent.");
    AssertEqual(255 * 255, full.Red[255], "100 percent red terminal");
    AssertEqual(128 * 255, half.Red[255], "50 percent red terminal");
}

static void IdentityRampRestoresFullRange()
{
    var identity = ColorTemperature.BuildIdentityRamp();

    AssertEqual(0, identity.Red[0], "identity red start");
    AssertEqual(257 * 128, identity.Red[128], "identity red midpoint");
    AssertEqual(ushort.MaxValue, identity.Red[255], "identity red terminal");
}

static void DefaultProfileLabelsAreIndependent()
{
    var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Office",
        "Reading",
        "Editing",
        "Movie",
        "Game",
        "Health",
        "Custom",
        "办公",
        "阅读",
        "编辑",
        "电影",
        "游戏",
        "健康",
        "自定义"
    };

    foreach (var profile in Defaults.CreateSettings().Profiles)
    {
        AssertFalse(blocked.Contains(profile.Name), $"profile {profile.Id} uses a blocked label");
    }

    var blockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "office",
        "reading",
        "editing",
        "movie",
        "game",
        "health",
        "custom"
    };
    foreach (var profile in Defaults.CreateSettings().Profiles)
    {
        AssertFalse(blockedIds.Contains(profile.Id), $"profile {profile.Name} uses a blocked id");
    }

    var labels = Defaults.CreateSettings().Profiles.Select(profile => profile.Name).ToArray();
    AssertEqual("日间办公", labels[0], "office label");
    AssertEqual("长读柔光", labels[1], "reading label");
    AssertEqual("细节清晰", labels[2], "editing label");
    AssertEqual("影音暖光", labels[3], "movie label");
    AssertEqual("高亮专注", labels[4], "game label");
    AssertEqual("夜间低蓝", labels[5], "health label");
    AssertEqual("我的方案", labels[6], "custom label");

    var migrated = Validation.Normalize(new EyeProfile("health", "Health", 5000, 90, 3700, 80));
    AssertEqual("low-blue-evening", migrated.Id, "migrated health id");
    AssertEqual("夜间低蓝", migrated.Name, "migrated health label");
}

static void NightWindowSpansMidnight()
{
    var sunrise = new TimeOnly(7, 0);
    var sunset = new TimeOnly(19, 0);

    AssertTrue(EyeCareController.IsNight(new TimeSpan(23, 0, 0), sunrise, sunset), "23:00 should be night.");
    AssertTrue(EyeCareController.IsNight(new TimeSpan(6, 30, 0), sunrise, sunset), "06:30 should be night.");
    AssertFalse(EyeCareController.IsNight(new TimeSpan(12, 0, 0), sunrise, sunset), "12:00 should be day.");
}

static void RestTimerMovesToShortBreak()
{
    var settings = new RestTimerSettings(true, 25, 5, 15, 4, false);
    var start = DateTimeOffset.Parse("2026-07-01T09:00:00+08:00");
    var state = RestTimerEngine.Start(settings, start);

    state = RestTimerEngine.Tick(state, settings, start.AddMinutes(25));

    AssertEqual(RestTimerPhase.ShortBreak, state.Phase, "phase");
    AssertEqual(1, state.CompletedWorkSessions, "completed sessions");
}

static void RestTimerPreservesPausedBreakPhase()
{
    var settings = new RestTimerSettings(true, 25, 5, 15, 4, false);
    var start = DateTimeOffset.Parse("2026-07-01T09:00:00+08:00");
    var state = RestTimerEngine.Start(settings, start);
    state = RestTimerEngine.Tick(state, settings, start.AddMinutes(25));
    state = RestTimerEngine.Pause(state, start.AddMinutes(26));
    state = RestTimerEngine.Resume(state, start.AddMinutes(27));

    AssertEqual(RestTimerPhase.ShortBreak, state.Phase, "resumed phase");
}

static void OverlaySettingsAreNormalized()
{
    var settings = Validation.Normalize(new OverlaySettings(true, 125, "cc8844"));

    AssertEqual(95, settings.OpacityPercent, "opacity");
    AssertEqual("#CC8844", settings.ColorHex, "color");
}

static void HotkeyParserHandlesModifierGesture()
{
    var parsed = HotkeyParser.TryParse("Ctrl+Alt+F9", out var hotkey);

    AssertTrue(parsed, "gesture should parse.");
    AssertEqual(0x0003u, hotkey.Modifiers, "modifiers");
    AssertEqual(0x78u, hotkey.VirtualKey, "virtual key");
}

static void ControllerUpdatesOverlayThroughHotkey()
{
    var display = new InMemoryDisplayDriver();
    var overlay = new InMemoryOverlayDriver();
    var hotkeys = new InMemoryHotkeyManager();
    var controller = new EyeCareController(new InMemorySettingsRepository(), display, overlay, hotkeys);

    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    var overlayState = controller.UpdateOverlayAsync(
        new UpdateOverlayCommand(true, 40, "#000000"),
        CancellationToken.None).GetAwaiter().GetResult();

    AssertTrue(overlayState.Enabled, "overlay should enable.");
    AssertEqual(1, overlayState.WindowCount, "overlay window count");

    var defaults = Defaults.CreateSettings().Hotkeys
        .Select(binding => binding.Action == HotkeyAction.ToggleOverlay
            ? binding with { Enabled = true, Gesture = "Ctrl+Alt+D" }
            : binding)
        .ToArray();
    var active = controller.UpdateHotkeysAsync(defaults, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(1, active.Count, "active hotkeys");

    hotkeys.TriggerAsync(HotkeyAction.ToggleOverlay, CancellationToken.None).GetAwaiter().GetResult();
    var state = controller.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult();
    AssertFalse(state.Overlay.Enabled, "overlay should toggle off.");
}

static void NativeMessageCodecReadsLengthPrefixedJson()
{
    using var input = new MemoryStream(NativeMessageCodec.PackForTest(new { command = "ping" }));
    using var document = NativeMessageCodec.ReadAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    AssertTrue(document is not null, "message should decode.");
    AssertEqual("ping", document!.RootElement.GetProperty("command").GetString(), "command");
}

static void NativeCommandHandlerAppliesOverlayCommand()
{
    var controller = new EyeCareController(
        new InMemorySettingsRepository(),
        new InMemoryDisplayDriver(),
        new InMemoryOverlayDriver(),
        new InMemoryHotkeyManager());
    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    using var request = JsonDocument.Parse(
        """
        {
          "command": "overlay",
          "enabled": true,
          "opacity": 44,
          "color": "#000000"
        }
        """);

    var response = new NativeCommandHandler(controller)
        .HandleAsync(request.RootElement, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertTrue(response.Ok, "native command should succeed.");
    var state = (OverlayState)response.Data!;
    AssertTrue(state.Enabled, "overlay should enable.");
    AssertEqual(44, state.OpacityPercent, "overlay opacity");
}

static void NativeCommandHandlerUpdatesSettings()
{
    var controller = new EyeCareController(
        new InMemorySettingsRepository(),
        new InMemoryDisplayDriver(),
        new InMemoryOverlayDriver(),
        new InMemoryHotkeyManager());
    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    var settings = Defaults.CreateSettings() with
    {
        Enabled = false,
        ActiveProfileId = "long-read"
    };
    var json = JsonSerializer.Serialize(
        new
        {
            command = "update_settings",
            settings
        },
        NativeMessageCodec.JsonOptions);
    using var request = JsonDocument.Parse(json);

    var response = new NativeCommandHandler(controller)
        .HandleAsync(request.RootElement, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertTrue(response.Ok, "settings command should succeed.");
    var updated = (EyeCareSettings)response.Data!;
    AssertFalse(updated.Enabled, "settings should disable filtering.");
    AssertEqual("long-read", updated.ActiveProfileId, "active profile");
}

static void LegacyIniImportMapsProfiles()
{
    var path = Path.Combine(Path.GetTempPath(), $"screenease-{Guid.NewGuid():N}.ini");
    File.WriteAllText(
        path,
        """
        [screen]
        mode=9
        health_colortemp=5000
        health_brightness=90
        health_night_colortemp=3700
        health_night_brightness=80
        read_colortemp=5500
        read_brightness=85
        read_night_colortemp=5200
        read_night_brightness=75
        enablesunset=1
        smooth=1
        transition_duration=65536
        [rest]
        enable_rest_timer=1
        work_duration=45
        short_duration=5
        long_duration=15
        auto_restart_timer=1
        """);

    try
    {
        var settings = LegacyIniImporter.ImportAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        var reading = settings.Profiles.First(profile => profile.Id == "long-read");

        AssertEqual("low-blue-evening", settings.ActiveProfileId, "active profile");
        AssertTrue(settings.UseSchedule, "schedule should import as enabled.");
        AssertEqual(5500, reading.ColorTemperatureKelvin, "reading kelvin");
        AssertEqual(45, settings.RestTimer.WorkMinutes, "work minutes");
    }
    finally
    {
        File.Delete(path);
    }
}

static void ControllerAppliesSelectedProfile()
{
    var display = new InMemoryDisplayDriver();
    var controller = new EyeCareController(new InMemorySettingsRepository(), display);

    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    var effect = controller.ApplyAsync(
        new ApplyEffectCommand("low-blue-evening", null, null, true),
        CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("low-blue-evening", effect.ProfileId, "profile id");
    AssertEqual(3700, effect.ColorTemperatureKelvin, "health kelvin");
    AssertEqual(75, effect.BrightnessPercent, "health brightness");
    AssertTrue(display.LastRequest is not null, "driver should receive request.");
}

static void ControllerPersistsManualAdjustmentProfile()
{
    var display = new InMemoryDisplayDriver();
    var controller = new EyeCareController(new InMemorySettingsRepository(), display);

    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    var effect = controller.ApplyAsync(
        new ApplyEffectCommand(null, 5700, 100, true),
        CancellationToken.None).GetAwaiter().GetResult();
    controller.TickAsync(CancellationToken.None).GetAwaiter().GetResult();
    var state = controller.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult();
    var manual = state.Settings.Profiles.First(profile => profile.Id == Defaults.ManualProfileId);

    AssertEqual(Defaults.ManualProfileId, effect.ProfileId, "manual effect profile id");
    AssertEqual(Defaults.ManualProfileId, state.Settings.ActiveProfileId, "manual active profile id");
    AssertEqual(Defaults.ManualProfileId, state.Effect.ProfileId, "manual state profile id");
    AssertEqual(Defaults.ManualProfileName, manual.Name, "manual profile name");
    AssertEqual(5700, manual.ColorTemperatureKelvin, "manual kelvin");
    AssertEqual(100, manual.BrightnessPercent, "manual brightness");
    AssertEqual(5700, state.Effect.ColorTemperatureKelvin, "manual state kelvin");
    AssertEqual(100, state.Effect.BrightnessPercent, "manual state brightness");
}

static void ControllerInitializesWhenDisplayResetFails()
{
    var controller = new EyeCareController(new InMemorySettingsRepository(), new FailingDisplayDriver());

    controller.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message) =>
    AssertTrue(!condition, message);

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

sealed class FailingDisplayDriver : IDisplayDriver
{
    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MonitorInfo>>([]);

    public Task ApplyAsync(DisplayEffectRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("display driver failed");

    public Task ResetAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("display driver failed");
}


