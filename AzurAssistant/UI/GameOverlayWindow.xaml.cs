using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>
/// Transparent, click-through overlay that follows a target window's client area and paints an opaque cover
/// rectangle (UID mask). While adjusting it becomes interactive so the box can be dragged and resized.
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

    private bool _adjusting;
    private bool _dragging;
    private bool _resizing;
    private Point _start;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;

    public GameOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyClickThrough(clickThrough: true);
    }

    /// <summary>Current cover rectangle in DIPs relative to the client area (updated while dragging).</summary>
    public Rect CoverRect { get; private set; } = new(UidCoverGeometry.DefaultX, UidCoverGeometry.DefaultY,
        UidCoverGeometry.DefaultWidth, UidCoverGeometry.DefaultHeight);

    /// <summary>Raised when the user finished dragging/resizing, with the rectangle in physical pixels.</summary>
    public event Action<Rect>? CoverChanged;

    public bool IsAdjusting => _adjusting;

    /// <summary>Places the window and the cover according to the computed placement.</summary>
    public void Apply(UidCoverPlacement placement)
    {
        if (!placement.IsValid) return;
        Left = placement.WindowLeft;
        Top = placement.WindowTop;
        Width = placement.WindowWidth;
        Height = placement.WindowHeight;
        CoverRect = new Rect(placement.CoverLeft, placement.CoverTop, placement.CoverWidth, placement.CoverHeight);
        Render();
    }

    /// <summary>Turns the adjust mode on/off: it disables click-through and shows the shade, hint and grip.</summary>
    public void SetAdjusting(bool adjusting)
    {
        _adjusting = adjusting;
        ApplyClickThrough(!adjusting);
        Shade.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;
        Hint.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;
        if (adjusting)
        {
            // The window must receive mouse input while adjusting.
            Show();
            Activate();
        }
        Render();
    }

    private void Render()
    {
        Canvas.SetLeft(Cover, CoverRect.X);
        Canvas.SetTop(Cover, CoverRect.Y);
        Cover.Width = Math.Max(1, CoverRect.Width);
        Cover.Height = Math.Max(1, CoverRect.Height);
        Cover.Visibility = Visibility.Visible;

        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        Shade.Width = w;
        Shade.Height = h;
        if (_adjusting)
        {
            Hint.Text = $"拖动白框移动 · 右下角绿点缩放 · {CoverRect.Width:0}×{CoverRect.Height:0}";
            Canvas.SetLeft(Hint, 12);
            Canvas.SetTop(Hint, 12);
            Canvas.SetLeft(Grip, CoverRect.X + Math.Max(1, CoverRect.Width) - 7);
            Canvas.SetTop(Grip, CoverRect.Y + Math.Max(1, CoverRect.Height) - 7);
        }
    }

    private void OnCoverMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjusting) return;
        _dragging = true;
        _start = e.GetPosition(this);
        _startLeft = CoverRect.X;
        _startTop = CoverRect.Y;
        Cover.CaptureMouse();
        e.Handled = true;
    }

    private void OnCoverMouseMove(object sender, MouseEventArgs e)
    {
        if (!_adjusting || !_dragging) return;
        var current = e.GetPosition(this);
        var x = _startLeft + (current.X - _start.X);
        var y = _startTop + (current.Y - _start.Y);
        CoverRect = new Rect(Clamp(x, 0, Math.Max(0, ActualWidth - CoverRect.Width)),
            Clamp(y, 0, Math.Max(0, ActualHeight - CoverRect.Height)), CoverRect.Width, CoverRect.Height);
        Render();
    }

    private void OnCoverMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Cover.ReleaseMouseCapture();
        RaiseChanged();
    }

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjusting) return;
        _resizing = true;
        _start = e.GetPosition(this);
        _startWidth = CoverRect.Width;
        _startHeight = CoverRect.Height;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_adjusting || !_resizing) return;
        var current = e.GetPosition(this);
        var w = Math.Max(8, _startWidth + (current.X - _start.X));
        var h = Math.Max(8, _startHeight + (current.Y - _start.Y));
        CoverRect = new Rect(CoverRect.X, CoverRect.Y,
            Math.Min(w, Math.Max(8, ActualWidth - CoverRect.X)), Math.Min(h, Math.Max(8, ActualHeight - CoverRect.Y)));
        Render();
    }

    private void OnGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        Grip.ReleaseMouseCapture();
        RaiseChanged();
    }

    private void RaiseChanged() => CoverChanged?.Invoke(CoverRect);

    private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

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
