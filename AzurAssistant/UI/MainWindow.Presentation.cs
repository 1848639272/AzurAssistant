using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AzurAssistant.Platform;
using Forms = System.Windows.Forms;

namespace AzurAssistant.UI;

public partial class MainWindow
{
    private GameStatusWindow? _gameStatus;
    private bool _requestBorderAccessOnLoad;
    private bool _requestingBorder;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private WindowState _restoreState;

    internal void AttachGamePresentation(Func<GameWindow?> findGame, bool interactive)
    {
        _gameStatus = new GameStatusWindow(ViewModel, findGame);
        _requestBorderAccessOnLoad = interactive && !ViewModel.AutoRunDaily && WindowsGraphicsFrameSource.SupportsBorderlessCapture;
        CaptureBorderPermissionButton.IsEnabled = WindowsGraphicsFrameSource.SupportsBorderlessCapture;
        CaptureBorderPermissionText.Text = CaptureBorderPermissionButton.IsEnabled
            ? "Windows 允许无边框捕获后可隐藏系统黄框。" : "此 Windows 版本会保留系统捕获边框。";
    }

    private async void OnCaptureBorderPermission(object sender, RoutedEventArgs e) => await RequestCaptureBorderPermissionAsync();

    private async Task RequestCaptureBorderPermissionAsync()
    {
        if (_requestingBorder) return;
        _requestingBorder = true;
        CaptureBorderPermissionButton.IsEnabled = false;
        try
        {
            var granted = await WindowsGraphicsFrameSource.RequestBorderlessAccessAsync();
            CaptureBorderPermissionText.Text = granted ? "已获无边框捕获权限。" : "Windows 未授权无边框捕获，系统可能仍显示黄框。";
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            CaptureBorderPermissionText.Text = "当前 Windows 未开放无边框捕获权限，系统可能仍显示黄框。";
        }
        finally
        {
            _requestingBorder = false;
            CaptureBorderPermissionButton.IsEnabled = WindowsGraphicsFrameSource.SupportsBorderlessCapture;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarCommand);
    }

    // Only a user close command goes to the tray. Installer handoff, application shutdown and Close() retain their exit semantics.
    private nint TitleBarCommand(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF060 && !_closing && ViewModel.CloseToTray)
        {
            handled = true;
            HideInTray();
        }
        return 0;
    }

    internal bool HideInTray()
    {
        try
        {
            if (_tray is null)
            {
                using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/AzurAssistant;component/assets/branding/app.ico"))?.Stream
                    ?? throw new InvalidOperationException("未找到托盘图标。");
                using var icon = new System.Drawing.Icon(stream);
                _trayIcon = (System.Drawing.Icon)icon.Clone();
                _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "蔚蓝助手", ContextMenuStrip = new Forms.ContextMenuStrip() };
                _tray.ContextMenuStrip.Items.Add("打开蔚蓝助手", null, (_, _) => RestoreFromTray());
                _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(Close));
                _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) RestoreFromTray(); };
            }
            _tray.Visible = true;
            _restoreState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Hide();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            DisposeTray();
            ViewModel.Report($"系统托盘不可用：{ex.Message}");
            return false;
        }
    }

    internal void RestoreFromTray()
    {
        if (_closing) return;
        Show();
        WindowState = _restoreState;
        Activate();
        if (_tray is not null) _tray.Visible = false;
    }

    private void DisposeTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
