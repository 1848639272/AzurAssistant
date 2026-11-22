using System.Windows;
using System.Windows.Input;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

/// <summary>
/// Small always-on-top panel with the current task and pause/stop buttons.
/// Size and position are computed in DIPs of the target monitor's work area, so it fits every resolution and DPI.
/// </summary>
public partial class HudWindow : Window
{
    private const double PreferredWidth = 360;
    private const double MinimumWidth = 220;
    private const double Edge = 16;

    public HudWindow()
    {
        InitializeComponent();
    }

    /// <summary>Fits the panel into the monitor work area and pins it to the top-right corner.</summary>
    public void PlaceOn(DisplayMonitor? monitor)
    {
        if (monitor is null) return;
        if (!IsVisible) Show();
        UpdateLayout();

        var workWidth = monitor.DipWorkWidth;
        var workHeight = monitor.DipWorkHeight;
        // Never wider than the screen minus margins, and never below a usable minimum.
        var width = Math.Clamp(workWidth - Edge * 2, MinimumWidth, PreferredWidth);
        if (Math.Abs(Width - width) > 0.5) Width = width;
        MaxHeight = Math.Max(96, workHeight - Edge * 2);
        UpdateLayout();

        var actualWidth = ActualWidth > 0 ? ActualWidth : width;
        var actualHeight = ActualHeight > 0 ? ActualHeight : 64;
        Left = monitor.DipWorkX + workWidth - actualWidth - Edge;
        Top = monitor.DipWorkY + Edge;
        // Final clamp for very small or oddly placed screens.
        if (Left < monitor.DipWorkX) Left = monitor.DipWorkX;
        if (Top + actualHeight > monitor.DipWorkY + workHeight)
            Top = Math.Max(monitor.DipWorkY, monitor.DipWorkY + workHeight - actualHeight);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await viewModel.TogglePauseAsync();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await viewModel.StopTaskAsync();
    }
}
