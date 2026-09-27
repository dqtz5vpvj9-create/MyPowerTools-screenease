using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using MyPowerTools.UI;
using ScreenEase.Surface.ViewModels;

namespace ScreenEase.Surface.Views;

public partial class ScreenEaseView : UserControl
{
    private bool _reminderPresetsAdded;
    private bool _responsiveLayoutPending;

    public ScreenEaseView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ScheduleResponsiveLayout();
        AttachedToVisualTree += (_, _) => (DataContext as ScreenEaseViewModel)?.ResumeView();
        DetachedFromVisualTree += (_, args) =>
        {
            if (OperatingSystem.IsMacOS() && args.RootVisual is Window window &&
                (!window.IsVisible || window.WindowState == WindowState.Minimized))
                (DataContext as ScreenEaseViewModel)?.SuspendView();
            else (DataContext as IDisposable)?.Dispose();
        };
        Loaded += (_, _) => { AddReminderPresetButtons(); ScheduleResponsiveLayout(); };
    }

    private void ScheduleResponsiveLayout()
    {
        if (_responsiveLayoutPending) return;
        _responsiveLayoutPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _responsiveLayoutPending = false;
            UpdateResponsiveLayout(Bounds.Width);
        }, DispatcherPriority.Background);
    }

    private void AddReminderPresetButtons()
    {
        if (_reminderPresetsAdded || ReminderCard.Child is not StackPanel reminderContent)
        {
            return;
        }

        var presetSection = new StackPanel { Spacing = 6 };
        presetSection.Children.Add(new TextBlock
        {
            Text = "快捷方案"
        });

        var buttons = new WrapPanel();
        foreach (var preset in ScreenEaseReminderPreset.BuiltIns)
        {
            var button = new Button
            {
                Content = preset.Name,
                Margin = new Thickness(0, 0, 8, 8)
            };
            button.Classes.Add("ScreenEaseSecondary");
            ToolTip.SetTip(button, "填写这一组时长并立即保存提醒设置");
            button.Click += async (_, _) =>
            {
                if (DataContext is ScreenEaseViewModel viewModel)
                {
                    await viewModel.ApplyReminderPresetAsync(preset);
                }
            };
            buttons.Children.Add(button);
        }

        presetSection.Children.Add(buttons);
        reminderContent.Children.Insert(Math.Min(1, reminderContent.Children.Count), presetSection);
        _reminderPresetsAdded = true;
    }

    private double _lastResponsiveWidth = double.NaN;

    /// <summary>
    /// Switches between the desktop split and the stacked phone layout. Only the width/height of the
    /// declared definitions and the grid positions change here: replacing Grid.ColumnDefinitions or
    /// Grid.RowDefinitions leaves the Grid's cached cell ranges pointing into the old collection, and
    /// the next measure pass throws ArgumentOutOfRangeException out of Grid.MeasureCell.
    /// </summary>
    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0 || width == _lastResponsiveWidth) return;
        _lastResponsiveWidth = width;
        var narrow = width < 760;
        var compact = width < 1080;

        if (narrow)
        {
            // Sidebar above the content, both full width.
            MainLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            MainLayout.ColumnDefinitions[1].Width = new GridLength(0);
            MainLayout.RowDefinitions[0].Height = GridLength.Auto;
            MainLayout.RowDefinitions[1].Height = GridLength.Auto;
            ModeSidebar.BorderThickness = MptThemeTokens.BottomBorderThickness;
            ModeSidebar.Margin = MptThemeTokens.CompactPageMargin;
            ModeSidebar.MaxHeight = 320;
            Grid.SetColumn(ModeSidebar, 0);
            Grid.SetRow(ModeSidebar, 0);
            Grid.SetColumn(ContentScroller, 0);
            Grid.SetRow(ContentScroller, 1);
        }
        else
        {
            var sidebarWidth = compact ? 196 : width >= 1280 ? 248 : 228;
            MainLayout.ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
            MainLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            MainLayout.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            MainLayout.RowDefinitions[1].Height = new GridLength(0);
            ModeSidebar.BorderThickness = MptThemeTokens.RightBorderThickness;
            ModeSidebar.Margin = default;
            ModeSidebar.MaxHeight = double.PositiveInfinity;
            Grid.SetColumn(ModeSidebar, 0);
            Grid.SetRow(ModeSidebar, 0);
            Grid.SetColumn(ContentScroller, 1);
            Grid.SetRow(ContentScroller, 0);
        }

        if (compact)
        {
            // One card per row: the second column collapses instead of being removed.
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(EyeCareCard, 0);
            Grid.SetRow(EyeCareCard, 0);
            Grid.SetColumn(ReminderCard, 0);
            Grid.SetRow(ReminderCard, 1);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(EyeCareCard, 0);
            Grid.SetRow(EyeCareCard, 0);
            Grid.SetColumn(ReminderCard, 1);
            Grid.SetRow(ReminderCard, 0);
        }
    }
}
