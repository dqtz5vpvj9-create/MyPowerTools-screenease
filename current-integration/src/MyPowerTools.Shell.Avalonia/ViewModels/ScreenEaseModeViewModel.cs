using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class ScreenEaseModeViewModel : ObservableViewModel
{
    private ScreenEaseProfile _profile;
    private bool _isSelected;
    private bool _showNightValues;

    public ScreenEaseModeViewModel(
        ScreenEaseProfile profile,
        Func<ScreenEaseModeViewModel, Task> select)
    {
        _profile = profile;
        SelectCommand = new AsyncRelayCommand(() => select(this));
    }

    public ScreenEaseProfile Profile => _profile;
    public string Id => _profile.Id;
    public string Name => _profile.Name;
    public string ValuesText => _showNightValues
        ? $"{_profile.EffectiveNightColorTemperature} K · {_profile.EffectiveNightBrightness}%"
        : $"{_profile.ColorTemperature} K · {_profile.Brightness}%";
    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
    }

    public void Update(ScreenEaseProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ValuesText));
    }

    public void SetShowNightValues(bool showNightValues)
    {
        if (_showNightValues == showNightValues)
        {
            return;
        }

        _showNightValues = showNightValues;
        OnPropertyChanged(nameof(ValuesText));
    }
}

public sealed record ScreenEaseDetectedDisplayViewModel(
    string Name,
    string Detail,
    string PrimaryText)
{
    public static ScreenEaseDetectedDisplayViewModel FromDisplay(ScreenEaseDisplay display)
    {
        var boundsDetail = display.Detail.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        var refreshRateText = display.RefreshRateHz > 0 ? $"{display.RefreshRateHz} Hz" : "刷新率未知";
        var detail = boundsDetail.StartsWith("Bounds ", StringComparison.OrdinalIgnoreCase)
            ? boundsDetail.Replace("Bounds ", "位置 ", StringComparison.OrdinalIgnoreCase)
            : display.Width > 0 && display.Height > 0
                ? $"尺寸 {display.Width} × {display.Height} · {refreshRateText}"
                : boundsDetail;
        return new ScreenEaseDetectedDisplayViewModel(
            string.IsNullOrWhiteSpace(display.Name) ? display.Id : display.Name,
            detail,
            display.Primary ? "主屏" : "副屏");
    }
}
