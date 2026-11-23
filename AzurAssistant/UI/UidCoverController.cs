using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>
/// Owns the click-through overlay: follows the game client area (or the assistant window when previewing), places one
/// or more covers and reports a status line. It keeps working while the game is not running.
/// </summary>
public sealed class UidCoverController(Window owner, Action<string> reportStatus)
{
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private GameOverlayWindow? _window;
    private CoverRequest _request = CoverRequest.Default;
    private int _clientWidth;
    private int _clientHeight;
    private double _dpiScale = 1;

    /// <summary>Everything the overlay needs: which boxes, how they look and whether it is shown at all.</summary>
    public sealed record CoverRequest(bool Enabled, bool PreviewOnSelf, IReadOnlyList<CoverBox> Boxes, string Style, string Color)
    {
        public static CoverRequest Default => new(false, false, [CoverBox.Default], CoverStyles.Solid, "#FFFFFF");
    }

    /// <summary>Raised when the user dragged a box in adjust mode (index, new box).</summary>
    public event Action<int, CoverBox>? BoxChanged;

    public bool IsAdjusting => _window?.IsAdjusting == true;

    public bool Enabled => _request.Enabled;

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

    /// <summary>Turns the cover on/off without touching the saved setting (used by the hotkey and the tray menu).</summary>
    public void SetEnabled(bool enabled)
    {
        _request = _request with { Enabled = enabled };
        Update(_request);
    }

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
            ? "调整中：拖动遮挡块移动；改完点「退出调整」保存。"
            : "已保存位置。");
    }

    public void Refresh()
    {
        if (!_request.Enabled)
        {
            _window?.Hide();
            return;
        }
        if (!TryTarget(out var clientX, out var clientY, out var clientWidth, out var clientHeight, out var scale, out var source))
        {
            _window?.Hide();
            reportStatus(source);
            return;
        }

        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _dpiScale = scale;
        var window = new Rect(clientX / scale, clientY / scale, clientWidth / scale, clientHeight / scale);
        var boxes = _request.Boxes.Count > 0 ? _request.Boxes : [CoverBox.Default];
        var targets = new List<CoverTarget>();
        foreach (var box in boxes)
        {
            // Reference space -> physical pixels -> DIPs for the overlay, plus the physical screen rectangle.
            var perX = clientWidth / (double)UidCoverGeometry.ReferenceWidth;
            var perY = clientHeight / (double)UidCoverGeometry.ReferenceHeight;
            var physicalX = box.X * perX;
            var physicalY = box.Y * perY;
            var physicalWidth = Math.Max(1, box.Width * perX);
            var physicalHeight = Math.Max(1, box.Height * perY);
            var dip = new Rect(physicalX / scale, physicalY / scale, physicalWidth / scale, physicalHeight / scale);
            var screen = new Int32Rect(clientX + (int)Math.Round(physicalX), clientY + (int)Math.Round(physicalY),
                Math.Max(1, (int)Math.Round(physicalWidth)), Math.Max(1, (int)Math.Round(physicalHeight)));
            targets.Add(new CoverTarget(dip, screen));
        }
        if (targets.Count == 0)
        {
            _window?.Hide();
            reportStatus("没有可用的遮挡框。");
            return;
        }

        if (_window is null)
        {
            _window = new GameOverlayWindow();
            _window.BoxChanged += (index, box) => BoxChanged?.Invoke(index, box);
        }
        var color = PixelColor.TryParse(_request.Color, out var r, out var g, out var b)
            ? Color.FromRgb(r, g, b)
            : Colors.White;
        _window.Apply(window, targets, boxes, _request.Style, color);
        if (!_window.IsVisible) _window.Show();
        var styleLabel = CoverStyles.Normalize(_request.Style) == CoverStyles.Mosaic ? "马赛克" : "纯色 " + _request.Color;
        reportStatus($"{targets.Count} 块遮挡（{styleLabel}）· {source}" + (IsAdjusting ? " · 调整模式" : ""));
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
            if (NativeMethods.GetForegroundWindow() != handle)
            {
                source = "预览：助手窗口不在前台，已隐藏（只在本窗口前台时显示）。";
                return false;
            }
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
            if (!IsForeground(game.Handle, game.ProcessId))
            {
                source = "游戏不在前台，已隐藏（只在游戏画面上显示）。";
                return false;
            }
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

    /// <summary>True when the foreground window belongs to the target process (so the cover never floats over other apps).</summary>
    private static bool IsForeground(IntPtr target, int processId)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == target) return true;
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        return (int)pid == processId;
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
