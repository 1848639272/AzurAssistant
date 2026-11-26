using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace AzurAssistant.UI;

public partial class VersionNotesWindow : Window
{
    internal VersionNotesWindow(Window owner, bool darkMode)
    {
        InitializeComponent();
        Owner = owner;
        ThemePalette.Apply(this, darkMode);
    }

    private void Acknowledge(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OpenProject(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "无法打开默认浏览器。项目地址为 " + e.Uri.AbsoluteUri, Title);
        }
    }
}
