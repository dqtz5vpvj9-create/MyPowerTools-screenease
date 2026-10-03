using Avalonia.Headless.XUnit;
using ScreenEase.Surface.Services;
using ScreenEase.Surface.ViewModels;
using Xunit;

namespace PersonalUx.Tests;

public sealed class ScreenEaseWorkflowTests
{
    [AvaloniaFact]
    public void Start_saves_the_edited_enabled_reminder_before_starting()
    {
        var calls = new List<string>();
        ScreenEaseReminder? saved = null;
        using var vm = new ScreenEaseViewModel(Snapshot(true),
            saveReminder: value => { saved = value; calls.Add("save"); return Task.CompletedTask; },
            startReminder: () => { calls.Add("start"); return Task.FromResult(Work()); });
        vm.FocusMinutes = "50";
        vm.ShortBreakMinutes = "10";
        vm.StartReminderCommand.Execute(null);
        Assert.Equal(new[] { "save", "start" }, calls);
        Assert.Equal(50, saved!.FocusMinutes);
        Assert.Equal(10, saved.ShortBreakMinutes);
        Assert.Equal("专注中", vm.ReminderStatusText);
    }

    [AvaloniaFact]
    public void Failed_reminder_save_keeps_disabled_state_and_never_starts()
    {
        var starts = 0;
        using var vm = new ScreenEaseViewModel(Snapshot(),
            saveReminder: _ => Task.FromException(new InvalidOperationException("test save failed")),
            startReminder: () => { starts++; return Task.FromResult(Work()); });
        vm.StartReminderCommand.Execute(null);
        Assert.Equal(0, starts);
        Assert.False(vm.ReminderEnabled);
        Assert.Equal("未开始", vm.ReminderStatusText);
        Assert.Contains("test save failed", vm.OperationMessage);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public void Reminder_start_pause_resume_reset_updates_commands_and_remaining_time()
    {
        var state = ScreenEaseReminderState.Stopped;
        using var vm = new ScreenEaseViewModel(Snapshot(), saveReminder: _ => Task.CompletedTask,
            startReminder: () => Task.FromResult(state = Work()),
            pauseReminder: () => Task.FromResult(state = state with { Phase = "paused", EndsAt = null, PausedFrom = "work", PausedRemainingSeconds = 3000 }),
            resumeReminder: () => Task.FromResult(state = Work()),
            resetReminder: () => Task.FromResult(state = ScreenEaseReminderState.Stopped));
        Assert.True(vm.StartReminderCommand.CanExecute(null));
        Assert.False(vm.PauseReminderCommand.CanExecute(null));
        vm.StartReminderCommand.Execute(null);
        Assert.True(vm.ReminderEnabled);
        Assert.Equal("50:00", vm.RemainingText);
        Assert.False(vm.StartReminderCommand.CanExecute(null));
        vm.PauseReminderCommand.Execute(null);
        Assert.Equal("已暂停", vm.ReminderStatusText);
        Assert.True(vm.ResumeReminderCommand.CanExecute(null));
        vm.ResumeReminderCommand.Execute(null);
        Assert.Equal("专注中", vm.ReminderStatusText);
        vm.ResetReminderCommand.Execute(null);
        Assert.Equal("-", vm.RemainingText);
        Assert.False(vm.ResetReminderCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Invalid_reminder_and_schedule_input_never_reaches_the_service()
    {
        var writes = 0;
        using var vm = new ScreenEaseViewModel(Snapshot(),
            saveReminder: _ => { writes++; return Task.CompletedTask; },
            saveSchedule: _ => { writes++; return Task.FromResult(Effect()); });
        vm.FocusMinutes = "241";
        vm.SaveReminderCommand.Execute(null);
        Assert.Contains("专注 1–240", vm.ReminderMessage);
        vm.Sunrise = "24:00";
        vm.SaveScheduleCommand.Execute(null);
        Assert.Contains("HH:mm", vm.OperationMessage);
        Assert.Equal(0, writes);
    }

    [AvaloniaFact]
    public void Schedule_save_uses_returned_effect_for_actual_status()
    {
        ScreenEaseSchedule? saved = null;
        using var vm = new ScreenEaseViewModel(Snapshot(), saveSchedule: value =>
        { saved = value; return Task.FromResult(Effect() with { ColorTemperatureKelvin = 3500, BrightnessPercent = 60 }); });
        vm.UseNightValues = true;
        vm.UseSchedule = true;
        vm.Sunrise = "08:30";
        vm.SaveScheduleCommand.Execute(null);
        Assert.True(saved!.UseNightValues);
        Assert.True(saved.UseSchedule);
        Assert.Equal("08:30", saved.Sunrise);
        Assert.Equal("3500 K / 60%", vm.CurrentSummary);
    }

    [AvaloniaFact]
    public void Manual_apply_and_toggle_are_logical_only_in_read_only_session()
    {
        var calls = new List<(int Kelvin, int Brightness, bool Hardware)>();
        using var vm = new ScreenEaseViewModel(Snapshot(), applyManual: (k, b, h) =>
        { calls.Add((k, b, h)); return Task.CompletedTask; }, disableEffect: () => Task.FromResult(
            new ScreenEaseDisableResult(Effect() with { Enabled = false }, false, true, "logical-only", "test")));
        vm.ColorTemperature = 4200;
        vm.Brightness = 72;
        vm.ApplyCurrentCommand.Execute(null);
        Assert.Equal((4200, 72, false), Assert.Single(calls));
        Assert.Equal("4200 K / 72%", vm.CurrentSummary);
        vm.ToggleEyeCareCommand.Execute(null);
        Assert.False(vm.EyeCareEnabled);
        vm.ToggleEyeCareCommand.Execute(null);
        Assert.True(vm.EyeCareEnabled);
        Assert.All(calls, value => Assert.False(value.Hardware));
    }

    [AvaloniaFact]
    public void New_mode_has_unique_id_and_failed_save_preserves_the_draft()
    {
        var saved = new List<ScreenEaseProfile>();
        using var vm = new ScreenEaseViewModel(Snapshot(), saveProfile: value =>
        { saved.Add(value); return Task.CompletedTask; });
        vm.ModeName = "Reading copy";
        vm.NewModeCommand.Execute(null);
        vm.NewModeCommand.Execute(null);
        Assert.Equal(new[] { "custom-1", "custom-2" }, saved.Select(value => value.Id));
        Assert.Equal(4, vm.Modes.Count);
        using var failed = new ScreenEaseViewModel(Snapshot(), saveProfile: _ => Task.FromException(new IOException("test disk full")));
        failed.Brightness = 90;
        failed.SaveModeCommand.Execute(null);
        Assert.True(failed.HasUnsavedModeChanges);
        Assert.Equal(90, failed.Brightness);
        Assert.Contains("test disk full", failed.OperationMessage);
        Assert.False(failed.IsBusy);
    }

    [AvaloniaFact]
    public void Advanced_and_overlay_validate_then_apply_normalized_runtime_response()
    {
        var advancedWrites = 0;
        var overlayWrites = 0;
        using var vm = new ScreenEaseViewModel(Snapshot(), saveAdvanced: value =>
        { advancedWrites++; return Task.FromResult(new ScreenEaseAdvancedSaveResult(2, value)); }, saveOverlay: value =>
        { overlayWrites++; return Task.FromResult(new ScreenEaseOverlayResult(value, new ScreenEaseOverlayRuntime(true, value.OpacityPercent, value.ColorHex, 2, "applied", "test"))); });
        vm.TransitionDurationMs = "120001";
        vm.SaveAdvancedCommand.Execute(null);
        Assert.Equal(0, advancedWrites);
        vm.TransitionDurationMs = "1750";
        vm.SaveAdvancedCommand.Execute(null);
        Assert.Equal(1, advancedWrites);
        vm.OverlayOpacityPercent = "96";
        vm.SaveOverlayCommand.Execute(null);
        Assert.Equal(0, overlayWrites);
        vm.OverlayOpacityPercent = "24";
        vm.OverlayColorHex = "blue";
        vm.SaveOverlayCommand.Execute(null);
        Assert.Equal(0, overlayWrites);
        vm.OverlayEnabled = true;
        vm.OverlayColorHex = "#ffcc88";
        vm.SaveOverlayCommand.Execute(null);
        Assert.Equal(1, overlayWrites);
        Assert.Equal("#FFCC88", vm.OverlayColorHex);
        Assert.Equal("遮罩正在显示", vm.OverlayStateText);
        Assert.Equal("2 个显示窗口", vm.OverlayWindowCountText);
    }

    [AvaloniaFact]
    public void Legacy_import_replaces_modes_and_clears_previous_drafts()
    {
        var imports = 0;
        var imported = Snapshot() with { ActiveProfileId = "new", Profiles = [new("new", "Imported", 55, 3300)], Effect = Effect() with { ProfileId = "new", BrightnessPercent = 55, ColorTemperatureKelvin = 3300 } };
        using var vm = new ScreenEaseViewModel(Snapshot(), importLegacy: path =>
        { Assert.Equal("test.ini", path); imports++; return Task.FromResult(imported); });
        vm.Brightness = 90;
        vm.ImportLegacyCommand.Execute(null);
        Assert.Equal(0, imports);
        vm.LegacyIniPath = " \"test.ini\" ";
        vm.ImportLegacyCommand.Execute(null);
        Assert.Equal(1, imports);
        Assert.Equal("Imported", vm.ModeName);
        Assert.False(vm.HasUnsavedModeChanges);
        Assert.Single(vm.Modes);
        Assert.Equal("3300 K / 55%", vm.CurrentSummary);
    }

    [AvaloniaFact]
    public async Task Presets_navigation_refresh_diagnostics_and_shortcuts_dispatch()
    {
        var events = new List<string>();
        ScreenEaseReminder? saved = null;
        using var vm = new ScreenEaseViewModel(Snapshot(),
            browseAllTools: () => { events.Add("browse"); return Task.CompletedTask; },
            refresh: () => { events.Add("refresh"); return Task.CompletedTask; },
            openHotkeySettings: () => { events.Add("hotkeys"); return Task.CompletedTask; },
            saveReminder: value => { saved = value; return Task.CompletedTask; },
            loadReminderState: () => Task.FromResult(ScreenEaseReminderState.Stopped));
        await vm.ApplyReminderPresetAsync(ScreenEaseReminderPreset.DeepWork);
        Assert.Equal(50, saved!.FocusMinutes);
        Assert.Equal(10, saved.ShortBreakMinutes);
        vm.BrowseAllToolsCommand.Execute(null);
        vm.RefreshCommand.Execute(null);
        vm.OpenHotkeySettingsCommand.Execute(null);
        Assert.Equal(new[] { "browse", "refresh", "hotkeys" }, events);
        vm.ToggleDiagnosticsCommand.Execute(null);
        Assert.True(vm.DiagnosticsExpanded);
        vm.ToggleDiagnosticsCommand.Execute(null);
        Assert.False(vm.DiagnosticsExpanded);
    }

    private static ScreenEaseReminderState Work() => new("work", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(50), null, "", 0, 3000);
    private static ScreenEaseDisplayEffect Effect() => new(true, "day", 6500, 100, false, DateTimeOffset.UtcNow);
    private static ScreenEaseSnapshot Snapshot(bool reminderEnabled = false) => new("day",
        [new("day", "Day", 100, 6500, 75, 4200), new("night", "Night", 80, 4500, 60, 3500)],
        [], [], new(false, false, "unavailable", "test"), new(reminderEnabled, false, 25, 5, 15, 4), ScreenEasePlan.Empty, [], 1,
        Schedule: new(true, false, "07:00", "19:00"), Effect: Effect());
}
