using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AzurAssistant.UI;

/// <summary>
/// Full-virtual-screen picker: shows a crosshair with the live screen coordinate and returns the point the user
/// left-clicked (null when cancelled).
/// </summary>
public partial class PointPickerWindow : Window
{
    public PointPickerWindow()
    {
        InitializeComponent();
        var look = ThemePalette.Current;
        ThemePalette.Apply(this, look.Dark, look.Ui, look.Text, look.Background, look.Accent);
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnPick;
        MouseRightButtonDown += (_, _) => DialogResult = false;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) => Activate();
    }

    /// <summary>Clicked point in physical screen pixels.</summary>
    public Point? ScreenPoint { get; private set; }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        HLine.X1 = 0; HLine.X2 = ActualWidth; HLine.Y1 = position.Y; HLine.Y2 = position.Y;
        VLine.X1 = position.X; VLine.X2 = position.X; VLine.Y1 = 0; VLine.Y2 = ActualHeight;
        var screen = PointToScreen(position);
        PositionText.Text = $"屏幕 ({screen.X:0}, {screen.Y:0})";
        if (AzurAssistant.Platform.ScreenSampler.Sample((int)screen.X, (int)screen.Y) is { } pixel)
        {
            Swatch.Fill = new SolidColorBrush(Color.FromRgb(pixel.R, pixel.G, pixel.B));
            ColorText.Text = AzurAssistant.Runtime.PixelColor.ToHex(pixel.R, pixel.G, pixel.B);
        }
        else ColorText.Text = "颜色不可读";
        var readoutX = position.X + 18;
        var readoutY = position.Y + 18;
        if (readoutX + Readout.ActualWidth > ActualWidth) readoutX = Math.Max(4, position.X - Readout.ActualWidth - 18);
        if (readoutY + Readout.ActualHeight > ActualHeight) readoutY = Math.Max(4, position.Y - Readout.ActualHeight - 18);
        Canvas.SetLeft(Readout, readoutX);
        Canvas.SetTop(Readout, readoutY);
    }

    private void OnPick(object sender, MouseButtonEventArgs e) => Pick(e);

    private void Pick(MouseButtonEventArgs e)
    {
        ScreenPoint = PointToScreen(e.GetPosition(this));
        if (ScreenPoint is { } point
            && AzurAssistant.Platform.ScreenSampler.Sample((int)point.X, (int)point.Y) is { } pixel)
            ColorHex = AzurAssistant.Runtime.PixelColor.ToHex(pixel.R, pixel.G, pixel.B);
        DialogResult = true;
    }

    /// <summary>Picked pixel colour as #RRGGBB (empty when unreadable).</summary>
    public string ColorHex { get; private set; } = "";
}
