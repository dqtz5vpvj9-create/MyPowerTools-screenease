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

    private static ScreenEaseReminderState Work() => new("work", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(50), null, "", 0, 3000);
    private static ScreenEaseDisplayEffect Effect() => new(true, "day", 6500, 100, false, DateTimeOffset.UtcNow);
    private static ScreenEaseSnapshot Snapshot(bool reminderEnabled = false) => new("day",
        [new("day", "Day", 100, 6500, 75, 4200), new("night", "Night", 80, 4500, 60, 3500)],
        [], [], new(false, false, "unavailable", "test"), new(reminderEnabled, false, 25, 5, 15, 4), ScreenEasePlan.Empty, [], 1,
        Schedule: new(true, false, "07:00", "19:00"), Effect: Effect());
}
