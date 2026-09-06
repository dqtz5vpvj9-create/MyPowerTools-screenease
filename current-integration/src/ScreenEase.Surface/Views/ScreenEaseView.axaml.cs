using Avalonia;
using Avalonia.Controls;
using MyPowerTools.UI;
using ScreenEase.Surface.ViewModels;

namespace ScreenEase.Surface.Views;

public partial class ScreenEaseView : UserControl
{
    private bool _reminderPresetsAdded;

    public ScreenEaseView()
    {
        InitializeComponent();
        SizeChanged += (_, eventArgs) => UpdateResponsiveLayout(eventArgs.NewSize.Width);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += (_, _) => { AddReminderPresetButtons(); UpdateResponsiveLayout(Bounds.Width); };
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

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var narrow = width < 760;
        var compact = width < 1080;
        MainLayout.ColumnDefinitions.Clear();
        MainLayout.RowDefinitions.Clear();
        if (narrow)
        {
            MainLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            MainLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            MainLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ModeSidebar.Height = 320;
            ModeSidebar.BorderThickness = MptThemeTokens.BottomBorderThickness;
            ContentPanel.Margin = MptThemeTokens.CompactPageMargin;
            Grid.SetColumn(ModeSidebar, 0);
            Grid.SetRow(ModeSidebar, 0);
            Grid.SetColumn(ContentPanel, 0);
            Grid.SetRow(ContentPanel, 1);
        }
        else
        {
            MainLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(compact ? 196 : width >= 1280 ? 248 : 228)));
            MainLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            MainLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            ModeSidebar.Height = double.NaN;
            ModeSidebar.BorderThickness = MptThemeTokens.RightBorderThickness;
            ContentPanel.Margin = MptThemeTokens.PageMargin;
            Grid.SetColumn(ModeSidebar, 0);
            Grid.SetRow(ModeSidebar, 0);
            Grid.SetColumn(ContentPanel, 1);
            Grid.SetRow(ContentPanel, 0);
        }

        WorkspaceGrid.ColumnDefinitions.Clear();
        WorkspaceGrid.RowDefinitions.Clear();
        if (compact)
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(EyeCareCard, 0);
            Grid.SetRow(EyeCareCard, 0);
            Grid.SetColumn(ReminderCard, 0);
            Grid.SetRow(ReminderCard, 1);
            return;
        }

        WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(EyeCareCard, 0);
        Grid.SetRow(EyeCareCard, 0);
        Grid.SetColumn(ReminderCard, 1);
        Grid.SetRow(ReminderCard, 0);
    }
}
