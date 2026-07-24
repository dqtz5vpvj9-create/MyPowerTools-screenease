using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Abstractions;
using ScreenEase.Surface.Services;
using ScreenEase.Surface.ViewModels;
using ScreenEase.Surface.Views;

namespace ScreenEase.Surface;

/// <summary>
/// Dotnet-surface factory for the ScreenEase tool. Loaded by the Shell's DotnetSurfaceLoader
/// from this assembly via the route's <c>assembly</c>+<c>type</c> manifest fields. Builds the
/// ScreenEaseViewModel with callbacks wired through <see cref="MptAvaloniaSurfaceContext"/> so the
/// tool operates independently of the Shell controller.
/// </summary>
public sealed class ScreenEaseSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var host = new ContentControl
        {
            Content = CreateLoadingView()
        };

        _ = PopulateAsync(host, context);
        return host;
    }

    private static async Task PopulateAsync(ContentControl host, MptAvaloniaSurfaceContext context)
    {
        try
        {
            host.Content = await CreateLoadedSurfaceAsync(context);
        }
        catch (Exception ex)
        {
            Info(context, $"ScreenEase failed to load: {ex.Message}");
            host.Content = CreateFailureView(host, context, ex.Message);
        }
    }

    private static async Task<UserControl> CreateLoadedSurfaceAsync(MptAvaloniaSurfaceContext context)
    {
        var tools = new ScreenEaseToolService(context.ServiceUnits);

        var snapshot = await tools.LoadAsync();
        var settingsRevision = snapshot.SettingsRevision;

        ScreenEaseViewModel viewModel = null!;
        viewModel = new ScreenEaseViewModel(
            snapshot,
            context.RouteId,
            browseAllTools: () => context.NavigateAsync("", "", null),
            refresh: () => context.NavigateAsync(context.ToolId, context.RouteId, null),
            saveProfile: async profile =>
            {
                await tools.SaveProfileAsync(profile);
                Info(context, $"Saved ScreenEase profile '{profile.Name}'.");
            },
            apply: async (profileId, displayId, hardwareWrite) =>
            {
                await tools.ApplyProfileAsync(profileId, displayId, hardwareWrite);
                Info(context, $"Applied ScreenEase profile '{profileId}'.");
            },
            applyManual: async (colorTemperatureKelvin, brightnessPercent, hardwareWrite) =>
            {
                await tools.ApplyManualAsync(colorTemperatureKelvin, brightnessPercent, "all", hardwareWrite);
                Info(context, $"Applied ScreenEase manual values: {colorTemperatureKelvin} K / {brightnessPercent}%.");
            },
            disableEffect: async () =>
            {
                var disabled = await tools.DisableEffectAsync();
                Info(context, disabled.DisplayResetSucceeded
                    ? "ScreenEase eye care disabled."
                    : $"ScreenEase eye care disabled; display reset warning: {disabled.DisplayResetMessage}");
                return disabled;
            },
            saveReminder: async reminder =>
            {
                settingsRevision = await tools.SaveReminderAsync(settingsRevision, reminder);
                Info(context, "ScreenEase reminder settings saved.");
            },
            saveSchedule: async schedule =>
            {
                var effect = await tools.ConfigureScheduleAsync(schedule);
                Info(context, "ScreenEase day and night schedule saved.");
                return effect;
            },
            loadReminderState: () => tools.LoadReminderStateAsync(),
            startReminder: () => tools.StartReminderAsync(),
            pauseReminder: () => tools.PauseReminderAsync(),
            resumeReminder: () => tools.ResumeReminderAsync(),
            resetReminder: () => tools.ResetReminderAsync(),
            saveAdvanced: async advanced =>
            {
                var saved = await tools.SaveAdvancedAsync(settingsRevision, advanced);
                settingsRevision = saved.SettingsRevision;
                Info(context, "ScreenEase transition settings saved.");
                return saved;
            },
            saveOverlay: async overlay =>
            {
                var result = await tools.ConfigureOverlayAsync(overlay);
                Info(context, result.Runtime.Message);
                return result;
            },
            importLegacy: async path =>
            {
                var imported = await tools.ImportLegacyAsync(path);
                settingsRevision = imported.SettingsRevision;
                Info(context, $"Imported ScreenEase settings from '{Path.GetFileName(path)}'.");
                return imported;
            },
            openHotkeySettings: () => context.NavigateAsync("", "", null));

        // Fold the ScreenEase.Service unit status (if a ServiceManager is supervising it) into the
        // existing diagnostics/title area. Falls back gracefully to null when no ServiceManager runs.
        viewModel.ServiceUnitStatus = await tools.LoadServiceUnitStatusAsync();

        Info(context, $"ScreenEase loaded: {viewModel.DisplayCountText}, {viewModel.ProfileCountText}.");
        return new ScreenEaseView { DataContext = viewModel };
    }

    private static Control CreateLoadingView() =>
        new Border
        {
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "ScreenEase", FontSize = 30, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "正在连接 ScreenEase Service…" },
                    new ProgressBar { IsIndeterminate = true, Width = 240, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }
                }
            }
        };

    private static Control CreateFailureView(
        ContentControl host,
        MptAvaloniaSurfaceContext context,
        string message)
    {
        var retry = new Button { Content = "重试", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        retry.Click += (_, _) =>
        {
            host.Content = CreateLoadingView();
            _ = PopulateAsync(host, context);
        };

        return new Border
        {
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "ScreenEase 暂时无法连接", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    retry
                }
            }
        };
    }

    private static void Info(MptAvaloniaSurfaceContext context, string message)
    {
        context.Log(new MptSurfaceLogEntry("info", message, DateTimeOffset.Now));
    }

}
