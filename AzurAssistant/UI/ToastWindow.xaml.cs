using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

/// <summary>Bottom-right notification that fades in, waits, and closes itself. Fits any resolution and DPI.</summary>
public partial class ToastWindow : Window
{
    private const double PreferredWidth = 360;
    private const double MinimumWidth = 200;
    private const double Edge = 16;
    private readonly DispatcherTimer _close;

    public ToastWindow()
    {
        InitializeComponent();
        _close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _close.Tick += (_, _) => { _close.Stop(); FadeOutAndClose(); };
    }

    public void Present(string title, string message, DisplayMonitor? monitor, TimeSpan? duration = null)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        if (duration is { } custom) _close.Interval = custom;
        Show();
        UpdateLayout();
        PlaceOn(monitor);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _close.Stop();
        _close.Start();
    }

    /// <summary>Bottom-right corner of the monitor work area, sized so it never exceeds the screen.</summary>
    public void PlaceOn(DisplayMonitor? monitor)
    {
        if (monitor is null) return;
        var workWidth = monitor.DipWorkWidth;
        var workHeight = monitor.DipWorkHeight;
        var width = Math.Clamp(workWidth - Edge * 2, MinimumWidth, PreferredWidth);
        if (Math.Abs(Width - width) > 0.5) Width = width;
        MaxHeight = Math.Max(120, workHeight - Edge * 3);
        UpdateLayout();

        var actualWidth = ActualWidth > 0 ? ActualWidth : width;
        var actualHeight = ActualHeight > 0 ? ActualHeight : 90;
        Left = monitor.DipWorkX + workWidth - actualWidth - Edge;
        Top = monitor.DipWorkY + workHeight - actualHeight - Edge;
        if (Left < monitor.DipWorkX) Left = monitor.DipWorkX;
        if (Top < monitor.DipWorkY) Top = monitor.DipWorkY;
    }

    private void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
