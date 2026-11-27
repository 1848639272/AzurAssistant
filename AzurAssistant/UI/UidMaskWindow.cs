using System.Windows;
using System.Windows.Controls;
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
    private readonly Func<PreviewSnapshot> _preview;
    private readonly DispatcherTimer _timer;
    private readonly GameOverlaySurface _surface;
    private GameWindow? _game;
    internal nint Handle => _surface.Handle;

    public UidMaskWindow(Func<PreviewSnapshot> preview, Action<string>? report = null)
    {
        _preview = preview;
        var image = new Image { Source = MaskImage, Stretch = Stretch.Fill, IsHitTestVisible = false, Focusable = false };
        _surface = new("蔚蓝助手 UID 遮罩", image, BoundsFor, report);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs args)
    {
        var snapshot = _preview();
        if (snapshot.State is PreviewState.Faulted or PreviewState.Idle) _game = null;
        else if (snapshot.Frame is { Width: 1920, Height: 1080 } frame && GameWindow.Exists(frame.WindowHandle, frame.ProcessId))
        {
            if (_game?.Handle != frame.WindowHandle || _game.ProcessId != frame.ProcessId)
                _game = new(frame.WindowHandle, frame.ProcessId);
        }
        _surface.SetTarget(_game);
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

    private static Int32Rect BoundsFor(WindowGeometry geometry)
    {
        var left = Region.X * geometry.Width / 1920;
        var top = Region.Y * geometry.Height / 1080;
        var right = ((Region.X + Region.Width) * geometry.Width + 1919) / 1920;
        var bottom = ((Region.Y + Region.Height) * geometry.Height + 1079) / 1080;
        return new Int32Rect(geometry.X + left, geometry.Y + top, right - left, bottom - top);
    }

    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; _surface.Dispose(); }
}
