using System.Windows;

namespace AzurAssistant.UI;

public partial class RouteTeleportWindow : Window
{
    public RouteTeleportWindow() => InitializeComponent();
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
