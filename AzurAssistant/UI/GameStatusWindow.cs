using System.Windows;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

internal enum GameStatusColor { Idle, Active, Error }

/// <summary>Displays assistant state independently from the game's capture and input sessions.</summary>
internal sealed class GameStatusWindow : IDisposable
{
    private const double PanelWidth = 270;
    private const double TextLineHeight = 14;
    private readonly MainViewModel _model;
    private readonly Func<GameWindow?> _findGame;
    private readonly GameOverlaySurface _surface;
    private readonly Border _border = new() { BorderThickness = new Thickness(2), SnapsToDevicePixels = true };
    private readonly Grid _root = new() { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
    private readonly Viewbox _panel = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _content = new() { ClipToBounds = true };
    private readonly DockPanel _entries = new() { LastChildFill = false };
    private readonly TextBlock _assists = Line();
    private readonly List<TextBlock> _logs = [];
    private bool _contentChanged = true;
    private GameWindow? _game;
    private DateTimeOffset _nextSearch;
    private int _appliedScale;
    internal nint Handle => _surface.Handle;

    public GameStatusWindow(MainViewModel model, Func<GameWindow?> findGame)
    {
        _model = model;
        _findGame = findGame;
        _root.Children.Add(_border);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _assists.FontWeight = FontWeights.Medium;
        text.Children.Add(_assists);
        text.Children.Add(_entries);
        _content.Children.Add(text);
        _panel.Child = _content;
        _root.Children.Add(_panel);
        _root.SizeChanged += (_, args) => SizePanel(args.NewSize);
        _surface = new("蔚蓝助手游戏状态", _root, g => new Int32Rect(g.X, g.Y, g.Width, g.Height), model.Report);
        _model.Logs.CollectionChanged += OnLogsChanged;
    }

    private static TextBlock Line() => new()
    {
        FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = TextLineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 3 * TextLineHeight, Margin = new Thickness(0, 0, 0, 1),
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .95, BlurRadius = 3, ShadowDepth = 1 }
    };

    // Bounds are expressed in client DIPs. WPF applies the monitor's DPI exactly once when rendering.
    internal static (Rect Bounds, double TextScale) LayoutFor(Size client, int percent)
    {
        var fit = Math.Min(client.Width / 1920, client.Height / 1080);
        var scale = fit * Math.Clamp(percent, 50, 200) / 100;
        var width = Math.Min(PanelWidth * scale, client.Width * 420 / 1920);
        // Leave room below the quest list and above the chat/Enter controls, including at 200%.
        return (new Rect(client.Width * 32 / 1920, client.Height * 435 / 1080, width, client.Height * 495 / 1080), scale);
    }

    private void SizePanel(Size client)
    {
        _appliedScale = _model.GameStatusLogScalePercent;
        var (bounds, scale) = LayoutFor(client, _appliedScale);
        if (scale <= 0) return;
        _panel.Width = bounds.Width;
        _panel.Height = bounds.Height;
        _panel.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        _content.Width = bounds.Width / scale;
        _content.Height = bounds.Height / scale;
        _contentChanged = true;
        if (_model.ShowGameStatusLog) UpdateEntries();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs args) => _contentChanged = true;

    private void UpdateEntries()
    {
        if (!_contentChanged || !double.IsFinite(_content.Width) || _content.Width <= 0) return;
        _contentChanged = false;
        var available = new Size(_content.Width, double.PositiveInfinity);
        _assists.Measure(available);
        var height = _content.Height - _assists.DesiredSize.Height;
        var index = 0;
        // Dock the newest entry first at the bottom. Only admit complete rows above it.
        while (index < _model.Logs.Count && height >= TextLineHeight + 1)
        {
            if (index == _logs.Count)
            {
                var row = Line();
                DockPanel.SetDock(row, Dock.Bottom);
                _entries.Children.Add(row);
                _logs.Add(row);
            }
            var line = _logs[index];
            line.Visibility = Visibility.Visible;
            if (line.Text != _model.Logs[index]) line.Text = _model.Logs[index];
            line.Measure(available);
            if (line.DesiredSize.Height > height) break;
            height -= line.DesiredSize.Height;
            index++;
        }
        for (var i = index; i < _logs.Count; i++) _logs[i].Visibility = Visibility.Collapsed;
    }

    public void Refresh(DateTimeOffset now)
    {
        if (!_model.ShowGameStatusBorder && !_model.ShowGameStatusLog)
        {
            _surface.SetTarget(null);
            _game = null;
            _nextSearch = default;
            return;
        }
        if (_game is not null && !GameWindow.Exists(_game.Handle, _game.ProcessId)) _game = null;
        if (_model.OverlayFrame is { } frame && GameWindow.Exists(frame.WindowHandle, frame.ProcessId))
        {
            if (_game?.Handle != frame.WindowHandle || _game.ProcessId != frame.ProcessId)
                _game = new(frame.WindowHandle, frame.ProcessId);
        }
        else if (_game is null && now >= _nextSearch)
        {
            _nextSearch = now.AddSeconds(1);
            try { _game = _findGame(); }
            catch (InvalidOperationException) { _game = null; } // Wait while the game has multiple possible windows.
        }
        _surface.SetTarget(_game);
        if (_game is null) return;
        _border.Visibility = _model.ShowGameStatusBorder ? Visibility.Visible : Visibility.Collapsed;
        _border.BorderBrush = _model.OverlayColor(now) switch
        {
            GameStatusColor.Active => Brushes.DodgerBlue,
            GameStatusColor.Error => Brushes.Gold,
            _ => Brushes.MediumSeaGreen
        };
        _panel.Visibility = _model.ShowGameStatusLog ? Visibility.Visible : Visibility.Collapsed;
        if (!_model.ShowGameStatusLog) return;
        if (_appliedScale != _model.GameStatusLogScalePercent) SizePanel(_root.RenderSize);
        var names = _model.RunningRealtimeNames;
        var heading = names.Length == 0 ? "" : $"实时辅助　{names}";
        if (_assists.Text != heading || _assists.Visibility != (heading.Length == 0 ? Visibility.Collapsed : Visibility.Visible))
        {
            _assists.Text = heading;
            _assists.Visibility = heading.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _contentChanged = true;
        }
        UpdateEntries();
    }

    public void Dispose()
    {
        _model.Logs.CollectionChanged -= OnLogsChanged;
        _surface.Dispose();
    }
}
