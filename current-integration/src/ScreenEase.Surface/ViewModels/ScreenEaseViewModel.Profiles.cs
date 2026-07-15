using System.Globalization;
using ScreenEase.Surface.Services;

namespace ScreenEase.Surface.ViewModels;

public sealed partial class ScreenEaseViewModel
{
    private async Task SelectModeAsync(ScreenEaseModeViewModel mode)
    {
        if (IsBusy)
        {
            return;
        }

        SelectMode(mode);
        await RunBusyAsync(async () =>
        {
            if (_apply is not null)
            {
                await _apply(mode.Id, "all", HardwareControlAvailable).ConfigureAwait(true);
            }
            var (kelvin, brightness) = ResolveProfileEffect(mode.Profile);
            SetActualEffect(kelvin, brightness);
            EyeCareEnabled = true;
            OperationMessage = HardwareControlAvailable
                ? $"已应用“{mode.Name}”：{CurrentSummary}。"
                : $"已应用“{mode.Name}”的逻辑状态；当前会话未写入显示器硬件。";
        }).ConfigureAwait(true);
    }

    private void SelectMode(ScreenEaseModeViewModel mode)
    {
        foreach (var item in Modes)
        {
            item.SetSelected(ReferenceEquals(item, mode));
        }

        SelectedMode = mode;
        _modeId = mode.Id;
        ModeName = mode.Name;
        mode.SetShowNightValues(EditingNightValues);
        LoadSelectedModeValues();
        OperationMessage = "";
    }

    private void LoadSelectedModeValues()
    {
        if (SelectedMode is null)
        {
            return;
        }

        ColorTemperature = EditingNightValues
            ? SelectedMode.Profile.EffectiveNightColorTemperature
            : SelectedMode.Profile.ColorTemperature;
        Brightness = EditingNightValues
            ? SelectedMode.Profile.EffectiveNightBrightness
            : SelectedMode.Profile.Brightness;
    }

    private async Task ToggleEyeCareAsync()
    {
        var enabled = !EyeCareEnabled;
        await RunBusyAsync(async () =>
        {
            if (!enabled)
            {
                ScreenEaseDisableResult? disableResult = null;
                if (_disableEffect is not null)
                {
                    disableResult = await _disableEffect().ConfigureAwait(true);
                }
                if (disableResult is not null)
                {
                    SetActualEffect(
                        disableResult.Effect.ColorTemperatureKelvin,
                        disableResult.Effect.BrightnessPercent);
                }
                EyeCareEnabled = false;
                OperationMessage = disableResult is { DisplayResetAttempted: true, DisplayResetSucceeded: false }
                    ? $"护眼逻辑已关闭；显示器硬件复位失败：{disableResult.DisplayResetMessage}"
                    : disableResult is { DisplayResetAttempted: true, DisplayResetSucceeded: true }
                        ? "护眼模式已关闭，显示器硬件已复位。"
                        : "护眼模式已关闭。";
                return;
            }

            if (_applyManual is not null)
            {
                await _applyManual(
                    (int)Math.Round(ColorTemperature),
                    (int)Math.Round(Brightness),
                    HardwareControlAvailable).ConfigureAwait(true);
                SetActualEffect((int)Math.Round(ColorTemperature), (int)Math.Round(Brightness));
            }
            else if (_apply is not null && SelectedMode is not null)
            {
                await _apply(SelectedMode.Id, "all", HardwareControlAvailable).ConfigureAwait(true);
                var (kelvin, brightness) = ResolveProfileEffect(SelectedMode.Profile);
                SetActualEffect(kelvin, brightness);
            }
            EyeCareEnabled = true;
            OperationMessage = HardwareControlAvailable
                ? "护眼模式已开启。"
                : "护眼模式已开启；当前会话仅更新逻辑状态。";
        }).ConfigureAwait(true);
    }

    private async Task SaveModeAsync()
    {
        if (!TryBuildCurrentProfile(out var profile))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (_saveProfile is not null)
            {
                await _saveProfile(profile).ConfigureAwait(true);
            }
            UpsertMode(profile);
            if (_apply is not null)
            {
                await _apply(profile.Id, "all", HardwareControlAvailable).ConfigureAwait(true);
            }
            var (kelvin, brightness) = ResolveProfileEffect(profile);
            SetActualEffect(kelvin, brightness);
            EyeCareEnabled = true;
            OperationMessage = $"“{profile.Name}”已保存并应用。";
        }).ConfigureAwait(true);
    }

    private async Task NewModeAsync()
    {
        if (!TryBuildCurrentProfile(out var draft))
        {
            return;
        }

        var profile = draft with { Id = CreateCustomProfileId() };
        await RunBusyAsync(async () =>
        {
            if (_saveProfile is not null)
            {
                await _saveProfile(profile).ConfigureAwait(true);
            }
            UpsertMode(profile);
            if (_apply is not null)
            {
                await _apply(profile.Id, "all", HardwareControlAvailable).ConfigureAwait(true);
            }
            var (kelvin, brightness) = ResolveProfileEffect(profile);
            SetActualEffect(kelvin, brightness);
            EyeCareEnabled = true;
            OperationMessage = $"“{profile.Name}”已新增并应用。";
        }).ConfigureAwait(true);
    }

    private async Task ApplyCurrentAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (_applyManual is not null)
            {
                await _applyManual(
                    (int)Math.Round(ColorTemperature),
                    (int)Math.Round(Brightness),
                    HardwareControlAvailable).ConfigureAwait(true);
                SetActualEffect((int)Math.Round(ColorTemperature), (int)Math.Round(Brightness));
            }
            else if (_apply is not null && SelectedMode is not null)
            {
                await _apply(SelectedMode.Id, "all", HardwareControlAvailable).ConfigureAwait(true);
                var (kelvin, brightness) = ResolveProfileEffect(SelectedMode.Profile);
                SetActualEffect(kelvin, brightness);
            }
            EyeCareEnabled = true;
            OperationMessage = HardwareControlAvailable
                ? $"已应用当前调节：{CurrentSummary}。"
                : "已应用当前调节的逻辑状态；当前会话未写入显示器硬件。";
        }).ConfigureAwait(true);
    }

    private string CreateCustomProfileId()
    {
        var ids = Modes.Select(mode => mode.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = $"custom-{index}";
            if (!ids.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"custom-{Guid.NewGuid():N}";
    }

    private bool TryBuildCurrentProfile(out ScreenEaseProfile profile)
    {
        if (string.IsNullOrWhiteSpace(ModeName))
        {
            OperationMessage = "模式名称不能为空。";
            profile = new ScreenEaseProfile("", "", 0, 0);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_modeId))
        {
            _modeId = $"custom-{Guid.NewGuid():N}";
        }

        profile = new ScreenEaseProfile(
            _modeId,
            ModeName.Trim(),
            EditingNightValues && SelectedMode is not null ? SelectedMode.Profile.Brightness : (int)Math.Round(Brightness),
            EditingNightValues && SelectedMode is not null ? SelectedMode.Profile.ColorTemperature : (int)Math.Round(ColorTemperature),
            EditingNightValues ? (int)Math.Round(Brightness) : SelectedMode?.Profile.EffectiveNightBrightness ?? (int)Math.Round(Brightness),
            EditingNightValues ? (int)Math.Round(ColorTemperature) : SelectedMode?.Profile.EffectiveNightColorTemperature ?? (int)Math.Round(ColorTemperature));
        return true;
    }

    private void UpsertMode(ScreenEaseProfile profile)
    {
        var existing = Modes.FirstOrDefault(mode =>
            string.Equals(mode.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ScreenEaseModeViewModel(profile, SelectModeAsync);
            Modes.Add(existing);
            OnPropertyChanged(nameof(HasModes));
        }
        else
        {
            existing.Update(profile);
        }

        SelectMode(existing);
    }

    private (int Kelvin, int Brightness) ResolveProfileEffect(ScreenEaseProfile profile)
    {
        var useNight = UseNightValues && UseSchedule && IsScheduledNight();
        return useNight
            ? (profile.EffectiveNightColorTemperature, profile.EffectiveNightBrightness)
            : (profile.ColorTemperature, profile.Brightness);
    }

    private bool IsScheduledNight()
    {
        if (!TimeOnly.TryParseExact(Sunrise, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sunrise) ||
            !TimeOnly.TryParseExact(Sunset, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sunset))
        {
            return false;
        }

        return IsNightAt(TimeOnly.FromDateTime(DateTime.Now), sunrise, sunset);
    }

    public static bool IsNightAt(TimeOnly now, TimeOnly sunrise, TimeOnly sunset)
    {
        return sunset >= sunrise
            ? now >= sunset || now < sunrise
            : now >= sunset && now < sunrise;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception)
        {
            OperationMessage = "操作失败。请稍后重试，或在“系统”中查看运行日志。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string NormalizeWriterState(string state)
    {
        return state.Trim().ToLowerInvariant() switch
        {
            "ready" => "ready",
            "enabled" => "enabled",
            "disabled" => "disabled",
            "unsupported" => "unsupported",
            "unavailable" => "unavailable",
            _ => "unknown"
        };
    }
}
