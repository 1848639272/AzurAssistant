using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Runtime;
using AzurAssistant.Vision;

namespace AzurAssistant.UI;

/// <summary>Lets the user drag a box over a captured frame and save it as a template of an asset pack.</summary>
public partial class TemplateAnnotatorWindow : Window
{
    private readonly string _framePath;
    private readonly List<string> _packs;
    private readonly Dictionary<string, string> _packLabels = new(StringComparer.Ordinal);
    private Point _start;
    private PixelRegion? _selection;
    private bool _selecting;

    public TemplateAnnotatorWindow(string framePath, IEnumerable<string> packDirectories)
    {
        InitializeComponent();
        _framePath = framePath;
        _packs = packDirectories.ToList();
        foreach (var pack in _packs)
        {
            var label = Path.GetFileName(pack);
            _packLabels[label] = pack;
            PackBox.Items.Add(label);
        }
        if (PackBox.Items.Count > 0) PackBox.SelectedIndex = 0;
        Loaded += OnLoadedWindow;
    }

    public string ResultMessage { get; private set; } = "";

    private void OnLoadedWindow(object sender, RoutedEventArgs e)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(_framePath);
        bitmap.EndInit();
        bitmap.Freeze();
        FrameImage.Source = bitmap;
        Overlay.Width = bitmap.PixelWidth;
        Overlay.Height = bitmap.PixelHeight;
        Overlay.Background = Brushes.Transparent;
        StatusText.Text = $"画面 {bitmap.PixelWidth}×{bitmap.PixelHeight}：{Path.GetFileName(_framePath)}";
    }

    private void OnSelectStart(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Overlay);
        _selecting = true;
        Overlay.CaptureMouse();
    }

    private void OnSelectMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        DrawSelection(_start, e.GetPosition(Overlay));
    }

    private void OnSelectEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        Overlay.ReleaseMouseCapture();
        var end = e.GetPosition(Overlay);
        var region = ToRegion(_start, end);
        if (region.Width < 6 || region.Height < 6)
        {
            _selection = null;
            SelectionText.Text = "选区太小，请重新拖拽";
            return;
        }
        _selection = region;
        SelectionText.Text = $"x={region.X} y={region.Y} 宽={region.Width} 高={region.Height}";
        DrawSelection(_start, end);
    }

    private void DrawSelection(Point from, Point to)
    {
        Overlay.Children.Clear();
        var region = ToRegion(from, to);
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = region.Width,
            Height = region.Height,
            Stroke = new SolidColorBrush(Color.FromRgb(0x62, 0xD2, 0xC2)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0x62, 0xD2, 0xC2)),
        };
        Canvas.SetLeft(rect, region.X);
        Canvas.SetTop(rect, region.Y);
        Overlay.Children.Add(rect);
    }

    private static PixelRegion ToRegion(Point from, Point to) => new(
        (int)Math.Round(Math.Min(from.X, to.X)),
        (int)Math.Round(Math.Min(from.Y, to.Y)),
        (int)Math.Round(Math.Abs(to.X - from.X)),
        (int)Math.Round(Math.Abs(to.Y - from.Y)));

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection)
        {
            StatusText.Text = "请先在画面上拖出一个框。";
            return;
        }
        if (PackBox.SelectedItem is not string label || !_packLabels.TryGetValue(label, out var packDirectory))
        {
            StatusText.Text = "请选择一个资产包。";
            return;
        }
        var id = IdBox.Text.Trim();
        if (id.Length == 0)
        {
            StatusText.Text = "请填写模板名。";
            return;
        }
        if (!double.TryParse(ThresholdBox.Text, out var threshold) || threshold is < 0.5 or > 0.99)
        {
            StatusText.Text = "阈值需在 0.5 到 0.99 之间。";
            return;
        }
        var result = TemplateAnnotator.Create(packDirectory, _framePath, id, selection, threshold);
        StatusText.Text = result.Message;
        if (result.Ok)
        {
            ResultMessage = result.Message;
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
