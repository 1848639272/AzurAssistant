using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>
/// Owns the click-through overlay: follows the game client area (or the assistant window when previewing),
/// positions the cover rectangle and reports a status line. It keeps working while the game is not running.
/// </summary>
public sealed class UidCoverController(Window owner, Action<string> reportStatus)
{
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private GameOverlayWindow? _window;
    private CoverRequest _request = new(false, false, UidCoverGeometry.DefaultX, UidCoverGeometry.DefaultY,
        UidCoverGeometry.DefaultWidth, UidCoverGeometry.DefaultHeight);
    private int _coverSaveGuard;
    private int _clientWidth;
    private int _clientHeight;
    private double _dpiScale = 1;

    public sealed record CoverRequest(bool Enabled, bool PreviewOnSelf, int X, int Y, int Width, int Height);

    /// <summary>Raised when the user dragged the box: x, y, width, height in physical pixels.</summary>
    public event Action<int, int, int, int>? CoverChanged;

    public bool IsAdjusting => _window?.IsAdjusting == true;

    public void Start()
    {
        _follow.Tick += (_, _) => Refresh();
        _follow.Start();
    }

    public void Stop()
    {
        _follow.Stop();
        _window?.Close();
        _window = null;
    }

    public void Update(CoverRequest request)
    {
        _request = request;
        if (!request.Enabled)
        {
            _window?.Hide();
            reportStatus("已关闭。");
            return;
        }
        Refresh();
    }

    /// <summary>Enters/leaves adjust mode; the overlay becomes interactive so the box can be dragged.</summary>
    public void SetAdjusting(bool adjusting)
    {
        if (!_request.Enabled && adjusting)
        {
            reportStatus("请先勾选「启用 UID 遮挡」，再调整位置。");
            return;
        }
        if (_window is null) Refresh();
        if (_window is null) return;
        _window.SetAdjusting(adjusting);
        reportStatus(adjusting
            ? "调整中：拖动白框移动、右下角绿点缩放；改完点「退出调整」保存。"
            : "已保存位置。");
    }

    public void Refresh()
    {
        if (!_request.Enabled)
        {
            _window?.Hide();
            return;
        }

        if (!TryTarget(out var clientX, out var clientY, out var clientWidth, out var clientHeight, out var scale,
                out var source))
        {
            _window?.Hide();
            reportStatus(source);
            return;
        }

        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _dpiScale = scale;
        var placement = UidCoverGeometry.Compute(clientX, clientY, clientWidth, clientHeight, scale,
            _request.X, _request.Y, _request.Width, _request.Height);
        if (!placement.IsValid)
        {
            _window?.Hide();
            reportStatus("无法计算遮挡位置（客户区尺寸无效）。");
            return;
        }

        if (_window is null)
        {
            _window = new GameOverlayWindow();
            _window.CoverChanged += rect =>
            {
                if (_coverSaveGuard > 0) return;
                var (x, y, w, h) = UidCoverGeometry.ToReference(rect.X, rect.Y, rect.Width, rect.Height,
                    _clientWidth, _clientHeight, _dpiScale);
                CoverChanged?.Invoke(x, y, w, h);
            };
        }
        _window.Apply(placement);
        if (!_window.IsVisible) _window.Show();
        reportStatus($"遮挡中：{_request.Width}×{_request.Height} @ ({_request.X}, {_request.Y}) · {source}"
            + (IsAdjusting ? " · 调整模式" : ""));
    }

    /// <summary>Resolves the client rect plus DPI scale of the game window, or of the assistant window for preview.</summary>
    private bool TryTarget(out int x, out int y, out int width, out int height, out double scale, out string source)
    {
        x = y = width = height = 0;
        scale = 1;
        source = "";
        if (_request.PreviewOnSelf)
        {
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle == IntPtr.Zero) { source = "预览：助手窗口尚未就绪。"; return false; }
            var origin = new NativeMethods.Point();
            if (!NativeMethods.GetClientRect(handle, out var client) || !NativeMethods.ClientToScreen(handle, ref origin))
            {
                source = "预览：无法获取助手窗口客户区。";
                return false;
            }
            x = origin.X;
            y = origin.Y;
            width = client.Right;
            height = client.Bottom;
            scale = System.Windows.Media.VisualTreeHelper.GetDpi(owner).DpiScaleX;
            source = "预览（跟随助手窗口）";
            return width > 0 && height > 0;
        }

        try
        {
            var game = GameWindow.Find("AzurPromilia");
            var geometry = game.Observe();
            x = geometry.X;
            y = geometry.Y;
            width = geometry.Width;
            height = geometry.Height;
            scale = GameDpi(game.Handle);
            source = "跟随游戏窗口";
            return width > 0 && height > 0;
        }
        catch (InvalidOperationException ex)
        {
            source = "未找到游戏窗口：" + ex.Message + "（可勾选「在本窗口预览」先摆好位置）";
            return false;
        }
    }

    private static double GameDpi(IntPtr handle)
    {
        try
        {
            var dpi = NativeMethods.GetDpiForWindow(handle);
            return dpi > 0 ? dpi / 96.0 : 1;
        }
        catch (EntryPointNotFoundException) { return 1; }
    }


}
