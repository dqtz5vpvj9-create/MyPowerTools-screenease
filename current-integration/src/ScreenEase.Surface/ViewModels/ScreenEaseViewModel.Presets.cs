using System.Globalization;

namespace ScreenEase.Surface.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    public async Task ApplyReminderPresetAsync(ScreenEaseReminderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (IsBusy)
        {
            return;
        }

        FocusMinutes = preset.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        ShortBreakMinutes = preset.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
        LongBreakMinutes = preset.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
        LongBreakInterval = preset.LongBreakInterval.ToString(CultureInfo.InvariantCulture);
        ReminderMessage = $"已应用“{preset.Name}”，正在保存提醒设置。";
        try
        {
            await SaveReminderAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ReminderMessage = $"提醒设置保存失败：{exception.Message}";
        }
    }
}

public sealed record ScreenEaseReminderPreset(
    string Id,
    string Name,
    int FocusMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes,
    int LongBreakInterval)
{
    public static ScreenEaseReminderPreset Pomodoro { get; } =
        new("pomodoro", "番茄钟 25 / 5", 25, 5, 15, 4);

    public static ScreenEaseReminderPreset DeepWork { get; } =
        new("deep-work", "深度工作 50 / 10", 50, 10, 20, 2);

    public static IReadOnlyList<ScreenEaseReminderPreset> BuiltIns { get; } =
        [Pomodoro, DeepWork];
}
