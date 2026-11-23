using System.Windows;

namespace AzurAssistant.UI;

public enum CloseDecision
{
    Cancel,
    Exit,
    Tray,
}

/// <summary>Asks whether closing should exit the assistant or only hide it to the tray, with a remember option.</summary>
public partial class CloseConfirmWindow : Window
{
    public CloseConfirmWindow(bool trayAvailable)
    {
        InitializeComponent();
        if (trayAvailable) return;
        TrayButton.IsEnabled = false;
        TrayButton.ToolTip = "托盘图标已关闭，无法收到托盘；请先在设置里启用托盘图标。";
    }

    public CloseDecision Decision { get; private set; } = CloseDecision.Cancel;
    public bool Remember => RememberBox.IsChecked == true;

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Decision = CloseDecision.Exit;
        DialogResult = true;
    }

    private void OnTray(object sender, RoutedEventArgs e)
    {
        Decision = CloseDecision.Tray;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = CloseDecision.Cancel;
        DialogResult = false;
    }
}
