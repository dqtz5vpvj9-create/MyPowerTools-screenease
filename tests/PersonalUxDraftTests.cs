using Avalonia.Headless.XUnit;
using ScreenEase.Surface.Services;
using ScreenEase.Surface.ViewModels;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxDraftTests
{
    [AvaloniaFact]
    public void Day_and_night_edits_survive_mode_switches_and_are_saved_together()
    {
        ScreenEaseProfile? saved = null;
        using var vm = Create(profile => { saved = profile; return Task.CompletedTask; });
        vm.ModeName = "My reading mode";
        vm.Brightness = 90;
        vm.ColorTemperature = 5500;
        vm.EditingNightValues = true;
        vm.Brightness = 60;
        vm.ColorTemperature = 3500;
        vm.Modes[1].SelectCommand.Execute(null);
        vm.Modes[0].SelectCommand.Execute(null);
        Assert.Equal(60, vm.Brightness);
        Assert.Equal("My reading mode", vm.ModeName);
        vm.EditingNightValues = false;
        Assert.Equal(90, vm.Brightness);
        Assert.Equal(5500, vm.ColorTemperature);
        Assert.True(vm.HasUnsavedModeChanges);
        vm.SaveModeCommand.Execute(null);
        Assert.NotNull(saved);
        Assert.Equal(90, saved.Brightness);
        Assert.Equal(60, saved.NightBrightness);
        Assert.Equal(3500, saved.NightColorTemperature);
        Assert.False(vm.HasUnsavedModeChanges);
    }

    [AvaloniaFact]
    public void Undo_discards_only_the_selected_modes_draft_without_writing_hardware_or_saving()
    {
        var writes = 0;
        using var vm = Create(_ => { writes++; return Task.CompletedTask; });
        vm.Brightness = 91;
        vm.Modes[1].SelectCommand.Execute(null);
        vm.Brightness = 63;
        vm.ResetModeDraftCommand.Execute(null);
        Assert.False(vm.HasUnsavedModeChanges);
        vm.Modes[0].SelectCommand.Execute(null);
        Assert.Equal(91, vm.Brightness);
        Assert.True(vm.HasUnsavedModeChanges);
        Assert.Equal(0, writes);
    }

    private static ScreenEaseViewModel Create(Func<ScreenEaseProfile, Task> save) => new(
        new ScreenEaseSnapshot("day", [new("day", "Day", 100, 6500, 75, 4200), new("night", "Night", 80, 4500, 65, 3500)],
            [], [], new(false, false, "unavailable", "test"), new(false, false, 25, 5, 15, 4), ScreenEasePlan.Empty, [], 1,
            Schedule: new(true, false, "07:00", "19:00")), saveProfile: save);
}
