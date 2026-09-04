using ScreenEase.Surface.Services;
using MyPowerTools.AvaloniaSdk;

namespace ScreenEase.Surface.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    private readonly Dictionary<string, ScreenEaseProfile> _modeDrafts = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingModeDraft;

    public bool HasUnsavedModeChanges => SelectedMode is not null && _modeDrafts.ContainsKey(SelectedMode.Id);
    public string ModeDraftStatus => HasUnsavedModeChanges ? "当前模式有未保存的修改；切换模式或日夜编辑不会丢失。" : "当前模式已保存";

    private ScreenEaseProfile CurrentModeDraft => SelectedMode is null
        ? new ScreenEaseProfile(_modeId, ModeName, (int)Math.Round(Brightness), (int)Math.Round(ColorTemperature))
        : _modeDrafts.GetValueOrDefault(SelectedMode.Id, SelectedMode.Profile);

    private void CaptureModeDraft()
    {
        if (_loadingModeDraft || SelectedMode is null) return;
        var draft = CurrentModeDraft with { Name = ModeName };
        draft = EditingNightValues
            ? draft with { NightBrightness = (int)Math.Round(Brightness), NightColorTemperature = (int)Math.Round(ColorTemperature) }
            : draft with { Brightness = (int)Math.Round(Brightness), ColorTemperature = (int)Math.Round(ColorTemperature) };
        if (draft == SelectedMode.Profile) _modeDrafts.Remove(SelectedMode.Id);
        else _modeDrafts[SelectedMode.Id] = draft;
        NotifyModeDraftState();
    }

    private void NotifyModeDraftState()
    {
        OnPropertyChanged(nameof(HasUnsavedModeChanges));
        OnPropertyChanged(nameof(ModeDraftStatus));
        (ResetModeDraftCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private Task ResetModeDraftAsync()
    {
        if (SelectedMode is null) return Task.CompletedTask;
        _modeDrafts.Remove(SelectedMode.Id);
        LoadSelectedModeValues();
        NotifyModeDraftState();
        OperationMessage = "已恢复当前模式的已保存值；显示器效果未改变。";
        return Task.CompletedTask;
    }
}
