using MyPowerTools.AvaloniaSdk;
using ScreenEase.Surface.ViewModels;

namespace ScreenEase.Surface.Views;

public partial class ScreenEaseView : IMptShortcutCommandSource
{
    public string ShortcutToolId => "screenease";
    public string ShortcutContext => DataContext is ScreenEaseViewModel vm ? "overview" : "";

    public IReadOnlyList<MptShortcutCommand> GetShortcutCommands()
    {
        if (DataContext is not ScreenEaseViewModel vm) return [];
        return
        [
            MptShortcutCommand.FromCommand("screenease.ui.refresh", vm.RefreshCommand),
            MptShortcutCommand.FromCommand("screenease.ui.toggle-eye-care", vm.ToggleEyeCareCommand),
            MptShortcutCommand.FromCommand("screenease.ui.save-mode", vm.SaveModeCommand),
            MptShortcutCommand.FromCommand("screenease.ui.reset-mode-draft", vm.ResetModeDraftCommand),
            MptShortcutCommand.FromCommand("screenease.ui.new-mode", vm.NewModeCommand),
            MptShortcutCommand.FromCommand("screenease.ui.apply-current", vm.ApplyCurrentCommand),
            MptShortcutCommand.FromCommand("screenease.ui.save-reminder", vm.SaveReminderCommand),
            MptShortcutCommand.FromCommand("screenease.ui.save-schedule", vm.SaveScheduleCommand),
            MptShortcutCommand.FromCommand("screenease.ui.start-reminder", vm.StartReminderCommand),
            MptShortcutCommand.FromCommand("screenease.ui.pause-reminder", vm.PauseReminderCommand),
            MptShortcutCommand.FromCommand("screenease.ui.resume-reminder", vm.ResumeReminderCommand),
            MptShortcutCommand.FromCommand("screenease.ui.reset-reminder", vm.ResetReminderCommand),
            MptShortcutCommand.FromCommand("screenease.ui.toggle-diagnostics", vm.ToggleDiagnosticsCommand),
            MptShortcutCommand.FromCommand("screenease.ui.save-advanced", vm.SaveAdvancedCommand),
            MptShortcutCommand.FromCommand("screenease.ui.save-overlay", vm.SaveOverlayCommand),
            MptShortcutCommand.FromCommand("screenease.ui.import-legacy", vm.ImportLegacyCommand),
            MptShortcutCommand.FromCommand("screenease.ui.open-hotkey-settings", vm.OpenHotkeySettingsCommand),
        ];
    }
}
