using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

/// <summary>Bottom-right notification that fades in, waits, and closes itself.</summary>
public partial class ToastWindow : Window
{
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

    /// <summary>Bottom-right corner of the monitor, above the taskbar area.</summary>
    public void PlaceOn(DisplayMonitor? monitor)
    {
        if (monitor is null) return;
        Left = monitor.X + monitor.Width - ActualWidth - 24;
        Top = monitor.Y + monitor.Height - ActualHeight - 72;
        if (Left < monitor.X) Left = monitor.X + 8;
        if (Top < monitor.Y) Top = monitor.Y + 8;
    }

    private void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
