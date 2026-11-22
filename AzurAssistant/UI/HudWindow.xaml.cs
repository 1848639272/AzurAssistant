using System.Windows;
using System.Windows.Input;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

/// <summary>Small always-on-top panel showing the current task with pause/stop buttons.</summary>
public partial class HudWindow : Window
{
    public HudWindow()
    {
        InitializeComponent();
    }

    /// <summary>Places the HUD at the top-right corner of the given monitor, keeping it on screen.</summary>
    public void PlaceOn(DisplayMonitor? monitor)
    {
        if (monitor is null) return;
        if (!IsVisible) Show();
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var right = monitor.X + monitor.Width;
        Left = right - width - 24;
        Top = monitor.Y + 24;
        if (Left < monitor.X) Left = monitor.X + 8;
        if (Top < monitor.Y) Top = monitor.Y + 8;
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
