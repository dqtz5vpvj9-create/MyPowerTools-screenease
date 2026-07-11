namespace ScreenEase.Core;

public sealed class EyeCareController(
    ISettingsRepository repository,
    IDisplayDriver displayDriver,
    IOverlayDriver? overlayDriver = null,
    IHotkeyManager? hotkeyManager = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly IOverlayDriver overlay = overlayDriver ?? new InMemoryOverlayDriver();
    private readonly IHotkeyManager hotkeys = hotkeyManager ?? new InMemoryHotkeyManager();
    private readonly SemaphoreSlim gate = new(1, 1);
    private EyeCareSettings settings = Defaults.CreateSettings();
    private RestTimerState restTimer = Defaults.CreateRestTimerState();
    private DisplayEffect effect = Defaults.CreateEffect(DateTimeOffset.Now);
    private OverlayState overlayState = new(false, 0, "#000000", 0);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            settings = await repository.LoadAsync(cancellationToken);
            restTimer = Defaults.CreateRestTimerState();
            effect = CreateEffect(settings, GetNow());
        }
        finally
        {
            gate.Release();
        }

        try
        {
            await ApplyCurrentAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Keep IPC alive on startup; explicit apply commands still report driver errors.
        }

        await ApplyOverlayCurrentAsync(cancellationToken);
        await ConfigureHotkeysAsync(cancellationToken);
    }

    public async Task<EyeCareState> GetStateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var monitors = await displayDriver.GetMonitorsAsync(cancellationToken);
            var activeHotkeys = await hotkeys.GetActiveBindingsAsync(cancellationToken);
            var currentOverlay = await overlay.GetStateAsync(cancellationToken);
            return new EyeCareState(settings, effect, currentOverlay, activeHotkeys, restTimer, monitors);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EyeCareSettings> UpdateSettingsAsync(EyeCareSettings next, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            settings = Validation.Normalize(next);
            effect = CreateEffect(settings, GetNow());
            await repository.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await ApplyCurrentAsync(cancellationToken);
        await ApplyOverlayCurrentAsync(cancellationToken);
        await ConfigureHotkeysAsync(cancellationToken);
        return settings;
    }

    public async Task<DisplayEffect> ApplyAsync(ApplyEffectCommand command, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = GetNow();
            var profileId = command.ProfileId is null
                ? settings.ActiveProfileId
                : Validation.NormalizeId(command.ProfileId);
            var profile = FindProfile(settings, profileId);
            var enabled = command.Enabled ?? settings.Enabled;
            var hasManualValues = command.ColorTemperatureKelvin.HasValue || command.BrightnessPercent.HasValue;
            var useManualProfile = profileId == Defaults.ManualProfileId || (command.ProfileId is null && hasManualValues);

            var kelvin = Validation.ClampKelvin(command.ColorTemperatureKelvin ?? profile.ColorTemperatureKelvin);
            var brightness = Validation.ClampBrightness(command.BrightnessPercent ?? profile.BrightnessPercent);
            if (useManualProfile)
            {
                profile = Defaults.CreateManualProfile(kelvin, brightness);
                settings = settings with
                {
                    Enabled = enabled,
                    ActiveProfileId = profile.Id,
                    Profiles = UpsertProfile(settings.Profiles, profile)
                };
                effect = new DisplayEffect(enabled, profile.Id, kelvin, brightness, false, now);
            }
            else
            {
                settings = settings with
                {
                    Enabled = enabled,
                    ActiveProfileId = profile.Id
                };
                effect = new DisplayEffect(enabled, profile.Id, kelvin, brightness, false, now);
            }

            await repository.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await ApplyCurrentAsync(cancellationToken);
        return effect;
    }

    public async Task<DisplayEffect> DisableAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            settings = settings with { Enabled = false };
            effect = effect with { Enabled = false, AppliedAt = GetNow() };
            await repository.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await displayDriver.ResetAsync(cancellationToken);
        return effect;
    }

    public async Task<OverlayState> UpdateOverlayAsync(UpdateOverlayCommand command, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = settings.Overlay;
            settings = settings with
            {
                Overlay = Validation.Normalize(new OverlaySettings(
                    Enabled: command.Enabled ?? current.Enabled,
                    OpacityPercent: command.OpacityPercent ?? current.OpacityPercent,
                    ColorHex: command.ColorHex ?? current.ColorHex))
            };
            await repository.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await ApplyOverlayCurrentAsync(cancellationToken);
        return overlayState;
    }

    public async Task<IReadOnlyList<HotkeyBinding>> UpdateHotkeysAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            settings = Validation.Normalize(settings with { Hotkeys = bindings });
            await repository.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await ConfigureHotkeysAsync(cancellationToken);
        return await hotkeys.GetActiveBindingsAsync(cancellationToken);
    }

    public async Task<RestTimerState> StartRestTimerAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            restTimer = RestTimerEngine.Start(settings.RestTimer, GetNow());
            return restTimer;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RestTimerState> PauseRestTimerAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            restTimer = RestTimerEngine.Pause(restTimer, GetNow());
            return restTimer;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RestTimerState> ResumeRestTimerAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            restTimer = RestTimerEngine.Resume(restTimer, GetNow());
            return restTimer;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RestTimerState> ResetRestTimerAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            restTimer = RestTimerEngine.Reset();
            return restTimer;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var shouldApply = false;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = GetNow();
            restTimer = RestTimerEngine.Tick(restTimer, settings.RestTimer, now);
            var nextEffect = CreateEffect(settings, now);
            if (HasEffectChanged(effect, nextEffect))
            {
                effect = nextEffect;
                shouldApply = true;
            }
        }
        finally
        {
            gate.Release();
        }

        if (shouldApply)
        {
            await ApplyCurrentAsync(cancellationToken);
        }
    }

    public async Task<EyeCareSettings> ImportLegacySettingsAsync(string path, CancellationToken cancellationToken)
    {
        var imported = await LegacyIniImporter.ImportAsync(path, cancellationToken);
        return await UpdateSettingsAsync(imported, cancellationToken);
    }

    public async Task ProcessHotkeyAsync(HotkeyAction action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case HotkeyAction.ToggleEnabled:
                if (effect.Enabled)
                {
                    await DisableAsync(cancellationToken);
                }
                else
                {
                    await ApplyAsync(new ApplyEffectCommand(effect.ProfileId, effect.ColorTemperatureKelvin, effect.BrightnessPercent, true), cancellationToken);
                }

                break;
            case HotkeyAction.IncreaseBrightness:
                await ApplyAsync(new ApplyEffectCommand(effect.ProfileId, effect.ColorTemperatureKelvin, effect.BrightnessPercent + 5, true), cancellationToken);
                break;
            case HotkeyAction.DecreaseBrightness:
                await ApplyAsync(new ApplyEffectCommand(effect.ProfileId, effect.ColorTemperatureKelvin, effect.BrightnessPercent - 5, true), cancellationToken);
                break;
            case HotkeyAction.IncreaseColorTemperature:
                await ApplyAsync(new ApplyEffectCommand(effect.ProfileId, effect.ColorTemperatureKelvin + 250, effect.BrightnessPercent, true), cancellationToken);
                break;
            case HotkeyAction.DecreaseColorTemperature:
                await ApplyAsync(new ApplyEffectCommand(effect.ProfileId, effect.ColorTemperatureKelvin - 250, effect.BrightnessPercent, true), cancellationToken);
                break;
            case HotkeyAction.ApplyLongReadProfile:
                await ApplyAsync(new ApplyEffectCommand("long-read", null, null, true), cancellationToken);
                break;
            case HotkeyAction.ApplyLowBlueEveningProfile:
                await ApplyAsync(new ApplyEffectCommand("low-blue-evening", null, null, true), cancellationToken);
                break;
            case HotkeyAction.ToggleOverlay:
                await UpdateOverlayAsync(new UpdateOverlayCommand(!settings.Overlay.Enabled, null, null), cancellationToken);
                break;
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await hotkeys.ResetAsync(cancellationToken);
        await overlay.HideAsync(cancellationToken);
        await displayDriver.ResetAsync(cancellationToken);
    }

    private async Task ApplyCurrentAsync(CancellationToken cancellationToken)
    {
        DisplayEffect current;
        EyeCareSettings currentSettings;

        await gate.WaitAsync(cancellationToken);
        try
        {
            current = effect;
            currentSettings = settings;
        }
        finally
        {
            gate.Release();
        }

        var transition = currentSettings.SmoothTransitions ? currentSettings.TransitionDuration : TimeSpan.Zero;
        var request = new DisplayEffectRequest(
            current.Enabled,
            current.ProfileId,
            current.ColorTemperatureKelvin,
            current.BrightnessPercent,
            current.IsNightValue,
            transition);
        await displayDriver.ApplyAsync(request, cancellationToken);
    }

    private async Task ApplyOverlayCurrentAsync(CancellationToken cancellationToken)
    {
        OverlaySettings current;
        await gate.WaitAsync(cancellationToken);
        try
        {
            current = settings.Overlay;
        }
        finally
        {
            gate.Release();
        }

        var monitors = await displayDriver.GetMonitorsAsync(cancellationToken);
        var nextState = current.Enabled
            ? await overlay.ApplyAsync(current, monitors, cancellationToken)
            : await overlay.HideAsync(cancellationToken);

        await gate.WaitAsync(cancellationToken);
        try
        {
            overlayState = nextState;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ConfigureHotkeysAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HotkeyBinding> bindings;
        await gate.WaitAsync(cancellationToken);
        try
        {
            bindings = settings.Hotkeys;
        }
        finally
        {
            gate.Release();
        }

        await hotkeys.ConfigureAsync(bindings, ProcessHotkeyAsync, cancellationToken);
    }

    private DisplayEffect CreateEffect(EyeCareSettings source, DateTimeOffset now)
    {
        var profile = FindProfile(source, source.ActiveProfileId);
        var useNight = source.UseNightValues && source.UseSchedule && IsNight(now.TimeOfDay, source.Sunrise, source.Sunset);
        return new DisplayEffect(
            source.Enabled,
            profile.Id,
            useNight ? profile.NightColorTemperatureKelvin : profile.ColorTemperatureKelvin,
            useNight ? profile.NightBrightnessPercent : profile.BrightnessPercent,
            useNight,
            now);
    }

    private DateTimeOffset GetNow() => time.GetUtcNow().ToLocalTime();

    private static EyeProfile FindProfile(EyeCareSettings source, string profileId) =>
        source.Profiles.FirstOrDefault(profile => profile.Id == Validation.NormalizeId(profileId))
        ?? source.Profiles.FirstOrDefault()
        ?? Defaults.CreateSettings().Profiles.First();

    private static IReadOnlyList<EyeProfile> UpsertProfile(
        IReadOnlyList<EyeProfile> profiles,
        EyeProfile profile)
    {
        var result = profiles.ToList();
        var index = result.FindIndex(item => item.Id == profile.Id);
        if (index >= 0)
        {
            result[index] = profile;
            return result;
        }

        var personalIndex = result.FindIndex(item => item.Id == "personal");
        if (personalIndex >= 0)
        {
            result.Insert(personalIndex, profile);
            return result;
        }

        result.Add(profile);
        return result;
    }

    private static bool HasEffectChanged(DisplayEffect current, DisplayEffect next) =>
        current.Enabled != next.Enabled
        || current.ProfileId != next.ProfileId
        || current.ColorTemperatureKelvin != next.ColorTemperatureKelvin
        || current.BrightnessPercent != next.BrightnessPercent
        || current.IsNightValue != next.IsNightValue;

    public static bool IsNight(TimeSpan current, TimeOnly sunrise, TimeOnly sunset)
    {
        var sunriseTime = sunrise.ToTimeSpan();
        var sunsetTime = sunset.ToTimeSpan();
        return sunsetTime < sunriseTime
            ? current >= sunsetTime && current < sunriseTime
            : current >= sunsetTime || current < sunriseTime;
    }
}


