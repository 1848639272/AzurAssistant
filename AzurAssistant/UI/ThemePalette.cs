using System.Windows;
using System.Windows.Media;

namespace AzurAssistant.UI;

/// <summary>
/// Window-scoped semantic colours shared by the production UI and independent WPF hosts.
/// Brushes are mutable on purpose: opacity sliders only rewrite their alpha in place, so dragging a slider repaints
/// instead of rebuilding the whole resource dictionary (which used to stall the UI on every tick).
/// </summary>
public sealed class ThemePalette : ResourceDictionary
{
    private static readonly string[] BackgroundKeys =
    [
        "SidebarBackground", "CardBackground", "TileBackground", "InputBackground",
        "HoverBackground", "SelectedBackground", "PreviewBackground", "MascotBackground", "MascotHoverBackground",
        "MascotPressedBackground",
    ];

    private static readonly string[] TextKeys =
    [
        "Ink", "Muted", "Subtle", "NavigationInk", "NavigationSelectedInk", "HandleInk", "InactiveInk",
        "WarningInk", "SuccessInk", "PreviewInk", "PreviewMuted",
    ];

    private static readonly string[] ChromeKeys =
    [
        "BorderBrush", "InputBorderBrush", "DividerBrush", "MascotBorderBrush", "DropIndicatorBrush",
        "Accent", "AccentForeground",
    ];

    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private FrameworkElement? _scope;
    private int _frozenSwaps;
    private int _uiOpacity = 100;
    private int _textOpacity = 100;
    private int _backgroundOpacity = 100;

    public ThemePalette() : this(false, 100, 100, 100) { }

    private ThemePalette(bool dark, int uiOpacity, int textOpacity, int backgroundOpacity)
    {
        Dark = dark;
        AddBrush("WindowBackground", dark ? "#000000" : "#F6F8FA");
        AddBrush("SidebarBackground", dark ? "#0A0A0A" : "#EFF3F5");
        AddBrush("CardBackground", dark ? "#121212" : "#FFFFFF");
        AddBrush("TileBackground", dark ? "#1C1C1C" : "#F7FAFA");
        AddBrush("InputBackground", dark ? "#0D0D0D" : "#FFFFFF");
        AddBrush("BorderBrush", dark ? "#6E6E6E" : "#E1E7EB");
        AddBrush("InputBorderBrush", dark ? "#9A9A9A" : "#B5C7CA");
        AddBrush("DividerBrush", dark ? "#4F4F4F" : "#E2ECEB");
        AddBrush("Ink", dark ? "#FFFFFF" : "#243544");
        AddBrush("Muted", dark ? "#D0D0D0" : "#768591");
        AddBrush("Subtle", dark ? "#B4B4B4" : "#86959E");
        AddBrush("Accent", dark ? "#62D2C2" : "#178C84");
        AddBrush("AccentForeground", dark ? "#102824" : "#FFFFFF");
        AddBrush("NavigationInk", dark ? "#DADADA" : "#667B86");
        AddBrush("NavigationSelectedInk", dark ? "#8BE4D7" : "#16776F");
        AddBrush("HoverBackground", dark ? "#2A2A2A" : "#E4EEEF");
        AddBrush("SelectedBackground", dark ? "#234740" : "#DCEDEB");
        AddBrush("HandleInk", dark ? "#C4C4C4" : "#82959D");
        AddBrush("WarningInk", dark ? "#F3B679" : "#B06F40");
        AddBrush("SuccessInk", dark ? "#68D6AE" : "#1C9C84");
        AddBrush("InactiveInk", dark ? "#9C9C9C" : "#A6B1B9");
        AddBrush("DropIndicatorBrush", dark ? "#77D0BD" : "#5D9B90");
        AddBrush("MascotBackground", dark ? "#254238" : "#E0F0E9");
        AddBrush("MascotHoverBackground", dark ? "#305544" : "#D3EADD");
        AddBrush("MascotPressedBackground", dark ? "#3B6751" : "#C4DFD0");
        AddBrush("MascotBorderBrush", dark ? "#4C715D" : "#C7DFD3");
        AddBrush("PreviewBackground", dark ? "#0C1216" : "#172733");
        AddBrush("PreviewInk", dark ? "#C3D3DC" : "#A6B8C1");
        AddBrush("PreviewMuted", dark ? "#93ABB8" : "#718995");
        UpdateOpacity(uiOpacity, textOpacity, backgroundOpacity);
    }

    public bool Dark { get; }

    public Color BaseColor(string key) => _colors.TryGetValue(key, out var color) ? color : Colors.Transparent;

    /// <summary>Creates a palette and pushes it into the scope's resources; returns it for later in-place updates.</summary>
    public static ThemePalette Apply(FrameworkElement scope, bool dark, int uiOpacity = 100, int textOpacity = 100, int backgroundOpacity = 100)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var palette = new ThemePalette(dark, Clamp(uiOpacity), Clamp(textOpacity), Clamp(backgroundOpacity));
        palette._scope = scope;
        foreach (var (key, brush) in palette._brushes) scope.Resources[key] = brush;
        return palette;
    }

    /// <summary>Rewrites the alpha of every grouped brush in place; consumers repaint without re-resolving resources.</summary>
    public void UpdateOpacity(int uiOpacity, int textOpacity, int backgroundOpacity)
    {
        _uiOpacity = Clamp(uiOpacity);
        _textOpacity = Clamp(textOpacity);
        _backgroundOpacity = Clamp(backgroundOpacity);
        foreach (var key in BackgroundKeys) SetAlpha(key, _backgroundOpacity);
        foreach (var key in TextKeys) SetAlpha(key, _textOpacity);
        foreach (var key in ChromeKeys) SetAlpha(key, _uiOpacity);
    }


    private static int Clamp(int value) => Math.Clamp(value, 0, 100);

    private void AddBrush(string key, string color)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color);
        _colors[key] = parsed;
        var brush = new SolidColorBrush(parsed);
        _brushes[key] = brush;
        Add(key, brush);
    }

    /// <summary>How many brushes had to be replaced because WPF froze them (reported once for diagnostics).</summary>
    public int FrozenSwapCount => _frozenSwaps;

    /// <summary>
    /// Rewrites the brush alpha in place. WPF may freeze brushes that ended up in a sealed template, so a frozen
    /// brush is replaced by a fresh instance instead of throwing (that exception used to crash the app mid-drag).
    /// </summary>
    private void SetAlpha(string key, int percent)
    {
        if (!_colors.TryGetValue(key, out var baseColor)) return;
        if (!_brushes.TryGetValue(key, out var brush)) return;
        var alpha = (byte)Math.Round(255 * Clamp(percent) / 100.0);
        var color = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        if (brush.Color == color) return;
        if (!brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }
        var replacement = new SolidColorBrush(color);
        _frozenSwaps++;
        _brushes[key] = replacement;
        this[key] = replacement;
        if (_scope is not null) _scope.Resources[key] = replacement;
    }
}
