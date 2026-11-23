using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>One cover to paint: where it sits in the window (DIPs) and which screen pixels it hides.</summary>
public readonly record struct CoverTarget(Rect Dip, Int32Rect Screen);

/// <summary>
/// Transparent, click-through overlay that follows a target window's client area and paints one or more covers.
/// A cover is either an opaque colour block or a live mosaic built from a capture of what is underneath; the window
/// excludes itself from capture, so the read-back is the game rather than the cover.
/// </summary>
public partial class GameOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    private readonly System.Windows.Threading.DispatcherTimer _mosaic = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly List<FrameworkElement> _visuals = [];
    private CoverTarget[] _targets = [];
    private CoverBox[] _boxes = [];
    private string _style = CoverStyles.Solid;
    private Color _color = Colors.White;
    private bool _adjusting;

    public GameOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyClickThrough(true);
            ScreenCapture.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        };
        _mosaic.Tick += (_, _) => RefreshMosaic();
    }

    /// <summary>Raised when a box was dragged in adjust mode, with the new box in reference space.</summary>
    public event Action<int, CoverBox>? BoxChanged;

    public bool IsAdjusting => _adjusting;

    public void Apply(Rect window, IReadOnlyList<CoverTarget> targets, IReadOnlyList<CoverBox> boxes, string style, Color color)
    {
        Left = window.X;
        Top = window.Y;
        Width = window.Width;
        Height = window.Height;
        _targets = [.. targets];
        _boxes = [.. boxes];
        _style = CoverStyles.Normalize(style);
        _color = color;
        Render();
        if (_style == CoverStyles.Mosaic)
        {
            if (!_mosaic.IsEnabled) _mosaic.Start();
            RefreshMosaic();
        }
        else _mosaic.Stop();
    }

    public void SetAdjusting(bool adjusting)
    {
        _adjusting = adjusting;
        ApplyClickThrough(!adjusting);
        if (adjusting) { Show(); Activate(); }
        Render();
    }

    private void Render()
    {
        Root.Children.Clear();
        _visuals.Clear();
        for (var index = 0; index < _targets.Length; index++)
        {
            var target = _targets[index];
            FrameworkElement element;
            if (_style == CoverStyles.Mosaic)
            {
                var image = new Image { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                element = image;
            }
            else
            {
                element = new Rectangle { Fill = new SolidColorBrush(_color) };
            }
            element.Width = Math.Max(1, target.Dip.Width);
            element.Height = Math.Max(1, target.Dip.Height);
            Canvas.SetLeft(element, target.Dip.X);
            Canvas.SetTop(element, target.Dip.Y);
            if (_adjusting)
            {
                element.Cursor = Cursors.SizeAll;
                var captured = index;
                element.MouseLeftButtonDown += (_, args) => BeginDrag(captured, element, args);
            }
            Root.Children.Add(element);
            _visuals.Add(element);
        }
        RefreshMosaic();
    }

    private void RefreshMosaic()
    {
        if (_style != CoverStyles.Mosaic) return;
        for (var index = 0; index < _visuals.Count && index < _targets.Length; index++)
        {
            if (_visuals[index] is not Image image) continue;
            var screen = _targets[index].Screen;
            var block = Math.Max(3, (int)Math.Round(Math.Max(screen.Width / 22.0, screen.Height / 22.0)));
            image.Source = ScreenCapture.CaptureMosaic(screen.X, screen.Y, screen.Width, screen.Height, block);
        }
    }

    private void BeginDrag(int index, FrameworkElement element, MouseButtonEventArgs args)
    {
        if (!_adjusting) return;
        var start = args.GetPosition(Root);
        var originX = Canvas.GetLeft(element);
        var originY = Canvas.GetTop(element);
        var offsetX = start.X - originX;
        var offsetY = start.Y - originY;
        element.CaptureMouse();
        element.MouseMove += Move;
        element.MouseLeftButtonUp += Up;
        args.Handled = true;

        void Move(object? sender, MouseEventArgs moveArgs)
        {
            var current = moveArgs.GetPosition(Root);
            Canvas.SetLeft(element, Math.Clamp(current.X - offsetX, 0, Math.Max(0, ActualWidth - element.Width)));
            Canvas.SetTop(element, Math.Clamp(current.Y - offsetY, 0, Math.Max(0, ActualHeight - element.Height)));
        }

        void Up(object? sender, MouseButtonEventArgs upArgs)
        {
            element.ReleaseMouseCapture();
            element.MouseMove -= Move;
            element.MouseLeftButtonUp -= Up;
            if (index < _boxes.Length)
            {
                var current = _boxes[index];
                var scale = _targets[index].Screen.Width / Math.Max(1.0, _targets[index].Dip.Width);
                _boxes[index] = new CoverBox(
                    (int)Math.Round(Canvas.GetLeft(element) * scale),
                    (int)Math.Round(Canvas.GetTop(element) * scale),
                    (int)Math.Round(element.Width * scale),
                    (int)Math.Round(element.Height * scale));
                BoxChanged?.Invoke(index, _boxes[index]);
                _ = current;
            }
        }
    }

    private void ApplyClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64();
        style |= WsExLayered | WsExToolWindow;
        if (clickThrough) style |= WsExTransparent;
        else style &= ~(long)WsExTransparent;
        SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(style));
        IsHitTestVisible = !clickThrough;
    }
}
