using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>Owned, nonactivating presentation surface. A static feathered rectangle covers the UID without sampling the game pixels.</summary>
internal sealed class UidMaskWindow : IDisposable
{
    internal static readonly Int32Rect Region = new(112, 1047, 208, 33);
    internal static readonly BitmapSource MaskImage = CreateMask();
    private const uint NoActivate = 0x10, NoZOrder = 0x4, NoOwnerZOrder = 0x200, ShowWindow = 0x40;
    private readonly Func<PreviewSnapshot> _preview;
    private readonly DispatcherTimer _timer;
    private readonly LocationCallback _onLocation;
    private HwndSource? _surface;
    private GameWindow? _game;
    private readonly Image _image = new() { Source = MaskImage, Stretch = Stretch.Fill, IsHitTestVisible = false, Focusable = false };
    private nint _hook;
    private Int32Rect? _bounds;
    internal nint Handle => _surface?.Handle ?? 0;

    public UidMaskWindow(Func<PreviewSnapshot> preview)
    {
        _preview = preview;
        _onLocation = (_, _, window, obj, child, _, _) =>
        {
            if (_game?.Handle == window && obj == 0 && child == 0) Place();
        };
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs args)
    {
        var snapshot = _preview();
        var frame = snapshot.Frame;
        if (snapshot.State == PreviewState.Faulted || snapshot.State == PreviewState.Idle) { RemoveSurface(); return; }
        if (frame is not null && frame.Width == 1920 && frame.Height == 1080 &&
            GameWindow.Exists(frame.WindowHandle, frame.ProcessId))
        {
            if (_game?.Handle != frame.WindowHandle || _game.ProcessId != frame.ProcessId)
            {
                RemoveSurface();
                _game = new(frame.WindowHandle, frame.ProcessId);
                var parameters = new HwndSourceParameters("蔚蓝助手 UID 遮罩")
                {
                    ParentWindow = frame.WindowHandle, WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                    ExtendedWindowStyle = 0x08000000 | 0x80 | 0x20, // NOACTIVATE | TOOLWINDOW | TRANSPARENT
                    UsesPerPixelOpacity = true, Width = 1, Height = 1, PositionX = -32000, PositionY = -32000
                };
                _surface = new(parameters) { SizeToContent = SizeToContent.Manual, RootVisual = _image };
                _hook = SetWinEventHook(0x800B, 0x800B, 0, _onLocation, (uint)frame.ProcessId, 0, 0);
            }
        }
        Place();
    }

    private static BitmapSource CreateMask()
    {
        const double cornerRadius = 13, featherWidth = 6;
        var width = Region.Width;
        var height = Region.Height;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Signed distance gives the straight sides and the circular corners the same feather profile.
            var dx = Math.Abs(x + 0.5 - width / 2d) - (width / 2d - cornerRadius);
            var dy = Math.Abs(y + 0.5 - height / 2d) - (height / 2d - cornerRadius);
            var distance = Math.Min(Math.Max(dx, dy), 0)
                + Math.Sqrt(Math.Pow(Math.Max(dx, 0), 2) + Math.Pow(Math.Max(dy, 0), 2)) - cornerRadius;
            var t = Math.Clamp(-distance / featherWidth, 0, 1);
            var i = (y * width + x) * 4;
            pixels[i] = 247;
            pixels[i + 1] = 244;
            pixels[i + 2] = 239;
            pixels[i + 3] = (byte)Math.Round(255 * t * t * (3 - 2 * t));
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private void Place()
    {
        if (_game is null || _surface is null) return;
        WindowGeometry geometry;
        try { geometry = _game.Observe(); }
        catch (InvalidOperationException)
        {
            if (_bounds is not null) ShowWindowNative(Handle, 0);
            _bounds = null;
            return;
        }
        var left = Region.X * geometry.Width / 1920;
        var top = Region.Y * geometry.Height / 1080;
        var right = ((Region.X + Region.Width) * geometry.Width + 1919) / 1920;
        var bottom = ((Region.Y + Region.Height) * geometry.Height + 1079) / 1080;
        var bounds = new Int32Rect(geometry.X + left, geometry.Y + top, right - left, bottom - top);
        if (_bounds == bounds) return;
        if (SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | NoZOrder | NoOwnerZOrder | ShowWindow))
            _bounds = bounds;
    }

    private void RemoveSurface()
    {
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
        _game = null;
        if (_surface is not null) { _surface.RootVisual = null; _surface.Dispose(); _surface = null; }
        _bounds = null;
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; RemoveSurface(); }

    private delegate void LocationCallback(nint hook, uint type, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module,
        LocationCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
}
