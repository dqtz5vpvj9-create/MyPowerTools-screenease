using Avalonia;
using Avalonia.Controls;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia.Views;

public partial class ScreenEaseView : UserControl
{
    public ScreenEaseView()
    {
        InitializeComponent();
        SizeChanged += (_, eventArgs) => UpdateResponsiveLayout(eventArgs.NewSize.Width);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += (_, _) => UpdateResponsiveLayout(Bounds.Width);
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
